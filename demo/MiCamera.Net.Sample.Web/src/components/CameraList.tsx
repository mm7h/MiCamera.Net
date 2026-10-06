import { Button, Empty, Tag } from "antd";
import { VideoCameraOutlined } from "@ant-design/icons";
import type { CameraStreamInfo } from "../types/api";

interface CameraListProps {
    cameras: readonly CameraStreamInfo[]; selectedStreamId: string | null; onSelect(streamId: string): void;
}

export function CameraList({ cameras, selectedStreamId, onSelect }: CameraListProps): React.JSX.Element {
    if (cameras.length === 0) return <Empty description="没有可用摄像头，请检查连接与已保存配置。" />;
    return <div className="camera-list">{cameras.map((camera) => <Button key={camera.streamId}
        className="camera-card" type={camera.streamId === selectedStreamId ? "primary" : "default"}
        aria-pressed={camera.streamId === selectedStreamId} onClick={() => onSelect(camera.streamId)}>
        <span className="camera-name"><VideoCameraOutlined /> {camera.streamId}</span>
        <span>{camera.sourceCodec} · 通道 {camera.channel}</span>
        <Tag color={camera.state === "Streaming" ? "green" : "orange"}>{camera.state}</Tag>
        {!camera.webRtcAvailable && <span>WebRTC 暂不可用</span>}
    </Button>)}</div>;
}
