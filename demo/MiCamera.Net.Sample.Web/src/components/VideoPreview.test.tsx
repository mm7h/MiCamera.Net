import { createRef } from "react";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { VideoPreview } from "./VideoPreview";
import type { CameraStreamInfo } from "../types/api";
import type { WebRtcPreviewState } from "../services/WebRtcPreviewController";

const Camera: CameraStreamInfo = {
    streamId: "living-room",
    cameraId: "camera-id",
    channel: 0,
    sourceCodec: "H265",
    state: "Streaming",
    lastReceivedAt: null,
    rtspUrl: "rtsp://127.0.0.1:8554/live/living-room",
    snapshotAvailable: true,
    webRtcAvailable: true
};

function renderPreview(phase: WebRtcPreviewState["phase"]): { onStart: () => void; onStop: () => void } {
    const onStart = vi.fn();
    const onStop = vi.fn();

    render(
        <VideoPreview
            camera={Camera}
            state={{ phase, message: null }}
            videoRef={createRef<HTMLVideoElement>()}
            onStart={onStart}
            onStop={onStop}
        />
    );

    return { onStart, onStop };
}

describe("VideoPreview", () => {
    it("offers to start a preview and cannot stop while idle", () => {
        const { onStart } = renderPreview("idle");

        const start = screen.getByRole("button", { name: "开始预览" });
        expect(start).toBeEnabled();
        expect(screen.getByRole("button", { name: "停止预览" })).toBeDisabled();

        fireEvent.click(start);

        expect(onStart).toHaveBeenCalledOnce();
    });

    it("disables both controls while the session is connecting", () => {
        renderPreview("connecting");

        expect(screen.getByRole("button", { name: "正在连接…" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "停止预览" })).toBeEnabled();
    });

    it("only offers to stop once the session is connected", () => {
        const { onStop } = renderPreview("connected");

        expect(screen.getByRole("button", { name: "开始预览" })).toBeDisabled();
        const stop = screen.getByRole("button", { name: "停止预览" });
        expect(stop).toBeEnabled();

        fireEvent.click(stop);

        expect(onStop).toHaveBeenCalledOnce();
    });

    it("allows retrying after a failure without offering a stop", () => {
        renderPreview("error");

        expect(screen.getByRole("button", { name: "开始预览" })).toBeEnabled();
        expect(screen.getByRole("button", { name: "停止预览" })).toBeDisabled();
    });
});
