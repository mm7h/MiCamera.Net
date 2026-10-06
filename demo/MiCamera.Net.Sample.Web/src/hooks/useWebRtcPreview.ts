import { useCallback, useEffect, useRef, useState } from "react";
import { MiCameraApiClient } from "../services/MiCameraApiClient";
import {
    WebRtcPreviewController,
    type WebRtcPreviewState
} from "../services/WebRtcPreviewController";

const IdleState: WebRtcPreviewState = { phase: "idle", message: null };

export interface WebRtcPreview {
    readonly state: WebRtcPreviewState;
    readonly videoRef: React.RefObject<HTMLVideoElement | null>;
    start(streamId: string): Promise<void>;
    stop(keepalive?: boolean): Promise<void>;
}

export function useWebRtcPreview(apiClient: MiCameraApiClient | null, onError: (message: string) => void): WebRtcPreview {
    const controllerRef = useRef<WebRtcPreviewController | null>(null);
    const videoRef = useRef<HTMLVideoElement | null>(null);
    const [state, setState] = useState<WebRtcPreviewState>(IdleState);
    const receiveState = useCallback((nextState: WebRtcPreviewState): void => {
        setState(nextState);
        if (nextState.phase === "error" && nextState.message !== null) {
            onError(nextState.message);
        }
    }, [onError]);

    useEffect(() => {
        if (apiClient === null) {
            controllerRef.current = null;
            setState(IdleState);
            return undefined;
        }

        const controller = new WebRtcPreviewController(apiClient, receiveState);
        controllerRef.current = controller;

        return () => {
            if (controllerRef.current === controller) {
                controllerRef.current = null;
            }

            void controller.stop();
        };
    }, [apiClient, receiveState]);

    const start = useCallback(async (streamId: string): Promise<void> => {
        const controller = controllerRef.current;
        const videoElement = videoRef.current;

        if (controller === null || videoElement === null) {
            receiveState({ phase: "error", message: "请先保存有效的 API 地址。" });
            return;
        }

        await controller.start(streamId, videoElement);
    }, [receiveState]);

    const stop = useCallback(async (keepalive = false): Promise<void> => {
        await controllerRef.current?.stop(keepalive);
    }, []);

    return { state, videoRef, start, stop };
}
