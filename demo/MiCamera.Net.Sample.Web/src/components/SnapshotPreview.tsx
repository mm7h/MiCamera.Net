import { Button } from "antd";
import { CameraOutlined } from "@ant-design/icons";
import { SnapshotHistory } from "./SnapshotHistory";
import type { SnapshotHistoryItem } from "../types/snapshot";

interface SnapshotPreviewProps {
    available: boolean;
    historyItems: readonly SnapshotHistoryItem[];
    snapshotUrl: string | null;
    loading: boolean;
    onRefresh(): void;
    onDeleteSnapshot(itemId: number): void;
}

export function SnapshotPreview({
    available,
    historyItems,
    snapshotUrl,
    loading,
    onRefresh,
    onDeleteSnapshot
}: SnapshotPreviewProps): React.JSX.Element {
    return (
        <section className="snapshot-panel card">
            <div className="section-heading">
                <div>
                    <p className="eyebrow">最新关键帧</p>
                    <h2>JPEG 截图</h2>
                </div>
                <Button icon={<CameraOutlined />} onClick={onRefresh} loading={loading} disabled={!available}>
                    {loading ? "正在获取…" : "刷新截图"}
                </Button>
            </div>
            <div className="snapshot-frame">
                {snapshotUrl !== null ? (
                    <img src={snapshotUrl} alt="摄像头最新截图" />
                ) : (
                    <p>{available ? "尚未加载截图。" : "服务端尚未生成可用截图。"}</p>
                )}
            </div>
            <SnapshotHistory items={historyItems} onDelete={onDeleteSnapshot} />
        </section>
    );
}
