import { Button, Empty, Modal } from "antd";
import { useState } from "react";
import type { SnapshotHistoryItem } from "../types/snapshot";

interface SnapshotHistoryProps {
    items: readonly SnapshotHistoryItem[];
    onDelete(itemId: number): void;
}

export function SnapshotHistory({ items, onDelete }: SnapshotHistoryProps): React.JSX.Element {
    const [previewId, setPreviewId] = useState<number | null>(null);
    const previewItem = items.find((item) => item.id === previewId) ?? null;

    return (
        <section className="snapshot-history">
            <div className="section-heading">
                <div>
                    <p className="eyebrow">浏览器缓存</p>
                    <h2>历史截图</h2>
                </div>
                {items.length > 0 && <span className="camera-count">{items.length}</span>}
            </div>
            {items.length === 0 ? (
                <Empty description="刷新截图后会在此保留预览和下载记录。" />
            ) : (
                <div className="history-list">
                    {items.map((item) => (
                        <article key={item.id} className="history-item">
                            <button
                                className="history-thumbnail"
                                type="button"
                                onClick={() => setPreviewId(item.id)}
                                aria-label={`预览 ${formatCapturedAt(item.capturedAt)} 的截图`}>
                                <img src={item.url} alt={`${item.streamId} 的历史截图`} />
                            </button>
                            <div className="history-item-footer">
                                <time dateTime={item.capturedAt.toISOString()} title={formatCapturedAt(item.capturedAt)}>
                                    {formatCapturedClock(item.capturedAt)}
                                </time>
                                <div className="history-actions">
                                    <Button type="text" href={item.url} download={getDownloadName(item)} icon={<DownloadIcon />} aria-label={`下载 ${formatCapturedAt(item.capturedAt)} 的截图`} />
                                    <Button type="text" danger onClick={() => onDelete(item.id)} icon={<TrashIcon />} aria-label={`删除 ${formatCapturedAt(item.capturedAt)} 的截图`} />
                                </div>
                            </div>
                        </article>
                    ))}
                </div>
            )}

            {previewItem !== null && (
                <Modal open title={formatCapturedAt(previewItem.capturedAt)} width={1000} onCancel={() => setPreviewId(null)}
                    footer={<Button type="primary" href={previewItem.url} download={getDownloadName(previewItem)}>下载原图</Button>}>
                    <img className="snapshot-full" src={previewItem.url} alt={`${previewItem.streamId} 的历史截图预览`} />
                </Modal>
            )}
        </section>
    );
}

function formatCapturedAt(capturedAt: Date): string {
    return new Intl.DateTimeFormat("zh-CN", {
        dateStyle: "short",
        timeStyle: "medium"
    }).format(capturedAt);
}

function formatCapturedClock(capturedAt: Date): string {
    return new Intl.DateTimeFormat("zh-CN", {
        hour: "2-digit",
        minute: "2-digit",
        hour12: false
    }).format(capturedAt);
}

function getDownloadName(item: SnapshotHistoryItem): string {
    const timestamp = item.capturedAt.toISOString().replaceAll(":", "-").replaceAll(".", "-");
    return `micamera-${item.streamId}-${timestamp}.jpg`;
}

function DownloadIcon(): React.JSX.Element {
    return (
        <svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" width="16" height="16">
            <g fill="none" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2">
                <path d="M12 3v11" />
                <path d="M8 10.5 12 14.5l4-4" />
                <path d="M5 17v2a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-2" />
            </g>
        </svg>
    );
}

function TrashIcon(): React.JSX.Element {
    return (
        <svg aria-hidden="true" focusable="false" viewBox="0 0 24 24" width="16" height="16">
            <g fill="none" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2">
                <path d="M4 7h16" />
                <path d="M10 4h4" />
                <path d="M6.5 7l1 12.5h9L17 7" />
                <path d="M10.5 11v5.5M13.5 11v5.5" />
            </g>
        </svg>
    );
}
