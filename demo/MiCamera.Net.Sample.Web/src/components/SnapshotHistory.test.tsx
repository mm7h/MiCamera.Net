import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SnapshotHistory } from "./SnapshotHistory";

describe("SnapshotHistory", () => {
    it("opens a preview and provides a download link for a cached image", () => {
        render(
            <SnapshotHistory
                items={[
                    {
                        id: 1,
                        streamId: "living-room",
                        capturedAt: new Date("2026-09-29T08:00:00Z"),
                        url: "blob:history-image"
                    }
                ]}
            />
        );

        fireEvent.click(screen.getByRole("button", { name: /预览/i }));

        expect(screen.getByRole("dialog", { name: "历史截图预览" })).toBeInTheDocument();
        expect(screen.getByRole("link", { name: "下载原图" })).toHaveAttribute("download");
        expect(screen.getByRole("link", { name: "下载原图" })).toHaveAttribute("href", "blob:history-image");
    });
});
