import { afterEach, describe, expect, it, vi } from "vitest";
import { MiCameraApiClient } from "./MiCameraApiClient";
import { WebRtcPreviewController } from "./WebRtcPreviewController";

class FakePeerConnection {
    public static latest: FakePeerConnection | null = null;
    public connectionState: RTCPeerConnectionState = "new";
    public localDescription: RTCSessionDescription | null = null;
    public onconnectionstatechange: ((this: RTCPeerConnection, event: Event) => unknown) | null = null;
    public onicecandidate: ((this: RTCPeerConnection, event: RTCPeerConnectionIceEvent) => unknown) | null = null;
    public ontrack: ((this: RTCPeerConnection, event: RTCTrackEvent) => unknown) | null = null;
    public readonly close = vi.fn();

    public constructor() {
        FakePeerConnection.latest = this;
    }

    public async setRemoteDescription(): Promise<void> {
    }

    public async createAnswer(): Promise<RTCSessionDescriptionInit> {
        return { type: "answer", sdp: "browser-answer" };
    }

    public async setLocalDescription(description: RTCSessionDescriptionInit): Promise<void> {
        this.localDescription = description as RTCSessionDescription;
    }

    public emitCandidate(): void {
        this.onicecandidate?.call(this as unknown as RTCPeerConnection, {
            candidate: {
                candidate: "candidate:1 1 udp 1 127.0.0.1 9 typ host",
                sdpMid: "0",
                sdpMLineIndex: 0,
                usernameFragment: null
            }
        } as unknown as RTCPeerConnectionIceEvent);
    }
}

describe("WebRtcPreviewController", () => {
    afterEach(() => {
        vi.unstubAllGlobals();
        FakePeerConnection.latest = null;
    });

    it("reports a browser WebRTC initialization failure", async () => {
        class ThrowingPeerConnection {
            public constructor() {
                throw new Error("WebRTC is unavailable.");
            }
        }

        vi.stubGlobal("RTCPeerConnection", ThrowingPeerConnection as unknown as typeof RTCPeerConnection);
        const states: string[] = [];
        const api = {
            createSession: vi.fn(),
            setAnswer: vi.fn(),
            addIceCandidate: vi.fn(),
            deleteSession: vi.fn()
        };
        const controller = new WebRtcPreviewController(api as unknown as MiCameraApiClient, (state) => {
            states.push(`${state.phase}:${state.message}`);
        });

        await controller.start("living-room", document.createElement("video"));

        expect(api.createSession).not.toHaveBeenCalled();
        expect(states).toContain("error:WebRTC is unavailable.");
    });

    it("queues browser ICE candidates until the answer is accepted", async () => {
        vi.stubGlobal("RTCPeerConnection", FakePeerConnection as unknown as typeof RTCPeerConnection);
        let acceptAnswer: (() => void) | undefined;
        const api = {
            createSession: vi.fn().mockResolvedValue({
                sessionId: "session-id",
                offer: { type: "offer", sdp: "server-offer" },
                expiresAt: "2026-01-01T00:00:00Z"
            }),
            setAnswer: vi.fn().mockImplementation(() => new Promise<void>((resolve) => {
                acceptAnswer = resolve;
            })),
            addIceCandidate: vi.fn().mockResolvedValue(undefined),
            deleteSession: vi.fn().mockResolvedValue(undefined)
        };
        const controller = new WebRtcPreviewController(api as unknown as MiCameraApiClient, vi.fn());
        const video = document.createElement("video");
        Object.defineProperty(video, "srcObject", { configurable: true, value: null, writable: true });

        const start = controller.start("living-room", video);
        await vi.waitFor(() => expect(api.setAnswer).toHaveBeenCalledOnce());
        FakePeerConnection.latest?.emitCandidate();
        expect(api.addIceCandidate).not.toHaveBeenCalled();

        acceptAnswer?.();
        await start;

        expect(api.addIceCandidate).toHaveBeenCalledWith("session-id", expect.objectContaining({
            candidate: "candidate:1 1 udp 1 127.0.0.1 9 typ host"
        }));
    });

    it("deletes the active server session when stopped", async () => {
        vi.stubGlobal("RTCPeerConnection", FakePeerConnection as unknown as typeof RTCPeerConnection);
        const api = {
            createSession: vi.fn().mockResolvedValue({
                sessionId: "session-id",
                offer: { type: "offer", sdp: "server-offer" },
                expiresAt: "2026-01-01T00:00:00Z"
            }),
            setAnswer: vi.fn().mockResolvedValue(undefined),
            addIceCandidate: vi.fn().mockResolvedValue(undefined),
            deleteSession: vi.fn().mockResolvedValue(undefined)
        };
        const controller = new WebRtcPreviewController(api as unknown as MiCameraApiClient, vi.fn());
        const video = document.createElement("video");
        Object.defineProperty(video, "srcObject", { configurable: true, value: null, writable: true });

        await controller.start("living-room", video);
        await controller.stop();

        expect(api.deleteSession).toHaveBeenCalledWith("session-id", false);
        expect(FakePeerConnection.latest?.close).toHaveBeenCalledOnce();
    });
});
