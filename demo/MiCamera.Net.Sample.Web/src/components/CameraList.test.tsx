import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { CameraList } from "./CameraList";

describe("CameraList", () => {
    it("selects a stream when its card is clicked", () => {
        const onSelect = vi.fn();

        render(
            <CameraList
                cameras={[
                    {
                        streamId: "living-room",
                        cameraId: "camera-id",
                        channel: 0,
                        sourceCodec: "H265",
                        state: "Streaming",
                        lastReceivedAt: null,
                        rtspUrl: "rtsp://127.0.0.1:8554/live/living-room",
                        snapshotAvailable: true,
                        webRtcAvailable: true
                    }
                ]}
                selectedStreamId={null}
                onSelect={onSelect}
            />
        );

        fireEvent.click(screen.getByRole("button", { name: /living-room/i }));

        expect(onSelect).toHaveBeenCalledWith("living-room");
    });
});
