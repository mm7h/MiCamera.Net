import { useEffect, useState } from "react";
import type { SnapshotHistoryItem } from "../types/snapshot";

interface SnapshotHistoryProps {
    items: readonly SnapshotHistoryItem[];
}

export function SnapshotHistory({ items }: SnapshotHistoryProps): React.JSX.Element {
    const [previewItem, setPreviewItem] = useState<SnapshotHistoryItem | null>(null);

    useEffect(() => {
        if (previewItem !== null && !items.some((item) => item.id === previewItem.id)) {
            setPreviewItem(null);
        }
    }, [items, previewItem]);

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
                <p className="empty-state">刷新截图后会在此保留预览和下载记录。</p>
            ) : (
                <div className="history-list">
                    {items.map((item) => (
                        <article key={item.id} className="history-item">
                            <button
                                className="history-thumbnail"
                                type="button"
                                onClick={() => setPreviewItem(item)}
                                aria-label={`预览 ${formatCapturedAt(item.capturedAt)} 的截图`}>
                                <img src={item.url} alt={`${item.streamId} 的历史截图`} />
                            </button>
                            <div className="history-item-footer">
                                <time dateTime={item.capturedAt.toISOString()}>{formatCapturedAt(item.capturedAt)}</time>
                                <a href={item.url} download={getDownloadName(item)}>下载</a>
                            </div>
                        </article>
                    ))}
                </div>
            )}

            {previewItem !== null && (
                <div className="image-dialog-backdrop" onMouseDown={(event) => {
                    if (event.target === event.currentTarget) {
                        setPreviewItem(null);
                    }
                }}>
                    <section className="image-dialog" role="dialog" aria-modal="true" aria-label="历史截图预览">
                        <div className="section-heading">
                            <div>
                                <p className="eyebrow">截图预览</p>
                                <h2>{formatCapturedAt(previewItem.capturedAt)}</h2>
                            </div>
                            <button className="secondary" type="button" onClick={() => setPreviewItem(null)}>关闭</button>
                        </div>
                        <img src={previewItem.url} alt={`${previewItem.streamId} 的历史截图预览`} />
                        <a className="primary download-link" href={previewItem.url} download={getDownloadName(previewItem)}>下载原图</a>
                    </section>
                </div>
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

function getDownloadName(item: SnapshotHistoryItem): string {
    const timestamp = item.capturedAt.toISOString().replaceAll(":", "-").replaceAll(".", "-");
    return `micamera-${item.streamId}-${timestamp}.jpg`;
}
