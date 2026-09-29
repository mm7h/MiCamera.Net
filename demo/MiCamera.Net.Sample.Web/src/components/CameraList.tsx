import type { CameraStreamInfo } from "../types/api";

interface CameraListProps {
    cameras: readonly CameraStreamInfo[];
    selectedStreamId: string | null;
    onSelect(streamId: string): void;
}

export function CameraList({ cameras, selectedStreamId, onSelect }: CameraListProps): React.JSX.Element {
    if (cameras.length === 0) {
        return <p className="empty-state">没有可用摄像头。请检查 API 地址、Token 和服务端流配置。</p>;
    }

    return (
        <div className="camera-list">
            {cameras.map((camera) => (
                <button
                    key={camera.streamId}
                    type="button"
                    className={`camera-card ${camera.streamId === selectedStreamId ? "selected" : ""}`}
                    aria-pressed={camera.streamId === selectedStreamId}
                    onClick={() => onSelect(camera.streamId)}>
                    <span className="camera-name">{camera.streamId}</span>
                    <span className="camera-meta">{camera.sourceCodec} · 通道 {camera.channel}</span>
                    <span className={`status-badge ${camera.state.toLowerCase()}`}>{camera.state}</span>
                    {!camera.webRtcAvailable && <span className="warning">WebRTC 暂不可用</span>}
                </button>
            ))}
        </div>
    );
}
