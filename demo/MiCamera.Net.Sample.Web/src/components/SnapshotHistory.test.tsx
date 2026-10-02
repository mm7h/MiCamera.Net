import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { SnapshotHistory } from "./SnapshotHistory";

const Item = {
    id: 1,
    streamId: "living-room",
    capturedAt: new Date("2026-09-29T08:00:00Z"),
    url: "blob:history-image"
};

describe("SnapshotHistory", () => {
    it("opens a preview and provides a download link for a cached image", () => {
        render(<SnapshotHistory items={[Item]} onDelete={vi.fn()} />);

        fireEvent.click(screen.getByRole("button", { name: /预览/i }));

        expect(screen.getByRole("dialog", { name: "历史截图预览" })).toBeInTheDocument();
        expect(screen.getByRole("link", { name: "下载原图" })).toHaveAttribute("download");
        expect(screen.getByRole("link", { name: "下载原图" })).toHaveAttribute("href", "blob:history-image");
    });

    it("offers icon actions that download and delete a cached image", () => {
        const onDelete = vi.fn();
        render(<SnapshotHistory items={[Item]} onDelete={onDelete} />);

        const download = screen.getByRole("link", { name: /^下载 / });
        expect(download).toHaveAttribute("href", "blob:history-image");
        expect(download.getAttribute("download")).toContain("micamera-living-room-");

        fireEvent.click(screen.getByRole("button", { name: /^删除 / }));

        expect(onDelete).toHaveBeenCalledWith(1);
    });

    it("shows the capture time as HH:mm and keeps the full timestamp in the title", () => {
        render(<SnapshotHistory items={[Item]} onDelete={vi.fn()} />);

        const clock = screen.getByText(
            new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false }).format(Item.capturedAt));

        expect(clock.tagName).toBe("TIME");
        expect(clock.getAttribute("title")).toContain("2026");
        expect(clock.getAttribute("datetime")).toBe(Item.capturedAt.toISOString());
    });

    it("closes an open preview when its image is deleted", () => {
        const { rerender } = render(<SnapshotHistory items={[Item]} onDelete={vi.fn()} />);

        fireEvent.click(screen.getByRole("button", { name: /预览/i }));
        expect(screen.getByRole("dialog", { name: "历史截图预览" })).toBeInTheDocument();

        rerender(<SnapshotHistory items={[]} onDelete={vi.fn()} />);

        expect(screen.queryByRole("dialog", { name: "历史截图预览" })).not.toBeInTheDocument();
        expect(screen.getByText("刷新截图后会在此保留预览和下载记录。")).toBeInTheDocument();
    });
});
