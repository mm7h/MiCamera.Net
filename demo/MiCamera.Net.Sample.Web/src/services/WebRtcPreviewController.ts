import type { IceCandidate } from "../types/api";
import { MiCameraApiClient } from "./MiCameraApiClient";

export type WebRtcPreviewPhase = "idle" | "connecting" | "connected" | "error";

export interface WebRtcPreviewState {
    phase: WebRtcPreviewPhase;
    message: string | null;
}

type StateListener = (state: WebRtcPreviewState) => void;

/**
 * Owns one browser WebRTC peer and its matching MiCamera server session.
 */
export class WebRtcPreviewController {
    private readonly _apiClient: MiCameraApiClient;
    private readonly _onStateChanged: StateListener;
    private _answerAccepted = false;
    private _candidateQueue: IceCandidate[] = [];
    private _candidateSendChain: Promise<void> = Promise.resolve();
    private _generation = 0;
    private _mediaStream: MediaStream | null = null;
    private _peer: RTCPeerConnection | null = null;
    private _sessionId: string | null = null;
    private _videoElement: HTMLVideoElement | null = null;

    public constructor(apiClient: MiCameraApiClient, onStateChanged: StateListener) {
        this._apiClient = apiClient;
        this._onStateChanged = onStateChanged;
    }

    public async start(streamId: string, videoElement: HTMLVideoElement): Promise<void> {
        const generation = ++this._generation;
        await this.release(true);

        if (!this.isGenerationCurrent(generation)) {
            return;
        }

        this._videoElement = videoElement;
        this.report({ phase: "connecting", message: "正在创建 WebRTC 会话…" });

        let peer: RTCPeerConnection | null = null;

        try {
            const activePeer = new RTCPeerConnection();
            peer = activePeer;
            this._peer = activePeer;
            activePeer.onicecandidate = (event) => this.handleIceCandidate(activePeer, generation, event);
            activePeer.ontrack = (event) => this.handleTrack(activePeer, generation, event);
            activePeer.onconnectionstatechange = () => this.handleConnectionState(activePeer, generation);

            const session = await this._apiClient.createSession(streamId);
            if (!this.isCurrent(peer, generation)) {
                await this.deleteAbandonedSession(session.sessionId);
                return;
            }

            if (session.offer.type !== "offer") {
                throw new Error("服务端返回的 WebRTC 会话不是 offer。");
            }

            this._sessionId = session.sessionId;
            await peer.setRemoteDescription(session.offer);
            const answer = await peer.createAnswer();
            await peer.setLocalDescription(answer);

            if (!this.isCurrent(peer, generation) || peer.localDescription === null) {
                return;
            }

            if (peer.localDescription.type !== "answer") {
                throw new Error("浏览器未能生成有效的 WebRTC answer。");
            }

            await this._apiClient.setAnswer(session.sessionId, {
                type: peer.localDescription.type,
                sdp: peer.localDescription.sdp
            });

            if (!this.isCurrent(peer, generation)) {
                return;
            }

            this._answerAccepted = true;
            const pendingCandidates = this._candidateQueue;
            this._candidateQueue = [];

            for (const candidate of pendingCandidates) {
                await this.queueCandidateSend(candidate, peer, generation);
            }
        } catch (error) {
            if (this.isGenerationCurrent(generation) && (peer === null || this._peer === peer)) {
                await this.release(true);
                if (this.isGenerationCurrent(generation)) {
                    this.report({ phase: "error", message: WebRtcPreviewController.getErrorMessage(error) });
                }
            }
        }
    }

    public async stop(keepalive = false): Promise<void> {
        ++this._generation;
        await this.release(true, keepalive);
        this.report({ phase: "idle", message: null });
    }

    private async release(notifyServer: boolean, keepalive = false): Promise<void> {
        const peer = this._peer;
        const sessionId = this._sessionId;
        const videoElement = this._videoElement;

        this._answerAccepted = false;
        this._candidateQueue = [];
        this._candidateSendChain = Promise.resolve();
        this._mediaStream = null;
        this._peer = null;
        this._sessionId = null;
        this._videoElement = null;

        if (peer !== null) {
            peer.onconnectionstatechange = null;
            peer.onicecandidate = null;
            peer.ontrack = null;
            peer.close();
        }

        if (videoElement !== null) {
            videoElement.srcObject = null;
        }

        if (notifyServer && sessionId !== null) {
            try {
                await this._apiClient.deleteSession(sessionId, keepalive);
            } catch {
                // The server's session timeout remains a safe cleanup fallback.
            }
        }
    }

    private handleIceCandidate(
        peer: RTCPeerConnection,
        generation: number,
        event: RTCPeerConnectionIceEvent): void {
        if (!this.isCurrent(peer, generation) || event.candidate === null || event.candidate.candidate.length === 0) {
            return;
        }

        const candidate: IceCandidate = {
            candidate: event.candidate.candidate,
            sdpMid: event.candidate.sdpMid,
            sdpMLineIndex: event.candidate.sdpMLineIndex,
            usernameFragment: event.candidate.usernameFragment
        };

        if (!this._answerAccepted) {
            this._candidateQueue.push(candidate);
            return;
        }

        void this.queueCandidateSend(candidate, peer, generation);
    }

    private handleTrack(peer: RTCPeerConnection, generation: number, event: RTCTrackEvent): void {
        if (!this.isCurrent(peer, generation) || this._videoElement === null) {
            return;
        }

        const stream = event.streams[0] ?? this._mediaStream ?? new MediaStream();
        if (event.streams[0] === undefined && !stream.getTracks().some((track) => track.id === event.track.id)) {
            stream.addTrack(event.track);
        }

        this._mediaStream = stream;
        this._videoElement.srcObject = stream;
        void this._videoElement.play().catch(() => {
            this.report({ phase: "connecting", message: "浏览器阻止了自动播放，请使用视频控件开始播放。" });
        });
    }

    private handleConnectionState(peer: RTCPeerConnection, generation: number): void {
        if (!this.isCurrent(peer, generation)) {
            return;
        }

        if (peer.connectionState === "connected") {
            this.report({ phase: "connected", message: "实时视频已连接。" });
            return;
        }

        if (peer.connectionState === "failed" || peer.connectionState === "closed") {
            void this.fail(peer, generation, "WebRTC 连接已中断。");
        }
    }

    private async queueCandidateSend(
        candidate: IceCandidate,
        peer: RTCPeerConnection,
        generation: number): Promise<void> {
        const sessionId = this._sessionId;
        if (sessionId === null || !this.isCurrent(peer, generation)) {
            return;
        }

        const send = this._candidateSendChain.then(async () => {
            if (!this.isCurrent(peer, generation) || this._sessionId !== sessionId) {
                return;
            }

            await this._apiClient.addIceCandidate(sessionId, candidate);
        });

        this._candidateSendChain = send.catch((error: unknown) => {
            if (this.isCurrent(peer, generation)) {
                void this.fail(peer, generation, WebRtcPreviewController.getErrorMessage(error));
            }
        });

        await send;
    }

    private async fail(peer: RTCPeerConnection, generation: number, message: string): Promise<void> {
        if (!this.isCurrent(peer, generation)) {
            return;
        }

        ++this._generation;
        await this.release(true);
        this.report({ phase: "error", message });
    }

    private isCurrent(peer: RTCPeerConnection, generation: number): boolean {
        return this._peer === peer && this.isGenerationCurrent(generation);
    }

    private isGenerationCurrent(generation: number): boolean {
        return this._generation === generation;
    }

    private async deleteAbandonedSession(sessionId: string): Promise<void> {
        try {
            await this._apiClient.deleteSession(sessionId);
        } catch {
            // A session that never became active will expire server-side if deletion races with shutdown.
        }
    }

    private report(state: WebRtcPreviewState): void {
        this._onStateChanged(state);
    }

    private static getErrorMessage(error: unknown): string {
        return error instanceof Error ? error.message : "无法建立 WebRTC 视频连接。";
    }
}
