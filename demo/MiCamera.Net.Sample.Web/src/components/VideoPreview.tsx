import { Button, Tag, Modal, Typography, Space } from "antd";
import { PlayCircleOutlined, StopOutlined, InfoCircleOutlined } from "@ant-design/icons";
import { useState, type RefObject } from "react";
import type { CameraStreamInfo } from "../types/api";
import type { WebRtcPreviewState } from "../services/WebRtcPreviewController";

interface VideoPreviewProps {
    camera: CameraStreamInfo | null;
    state: WebRtcPreviewState;
    videoRef: RefObject<HTMLVideoElement | null>;
    rtspUsername: string | null;
    onStart(): void;
    onStop(): void;
}

export function VideoPreview({ camera, state, videoRef, rtspUsername, onStart, onStop }: VideoPreviewProps): React.JSX.Element {
    const [infoStreamId, setInfoStreamId] = useState<string | null>(null);
    const sessionActive = state.phase === "connecting" || state.phase === "connected";
    const infoOpen = sessionActive && camera !== null && infoStreamId === camera.streamId;
    // Reset during render so a stopped or changed session cannot reopen stale information.
    if (infoStreamId !== null && (!sessionActive || camera?.streamId !== infoStreamId)) setInfoStreamId(null);
    const canStart = camera !== null && camera.webRtcAvailable && !sessionActive;

    return (
        <section className="preview-panel card">
            <div className="section-heading">
                <div>
                    <p className="eyebrow">WebRTC 实时画面</p>
                    <h2>{camera?.streamId ?? "选择一台摄像头"}</h2>
                </div>
                <Space wrap><Tag color={state.phase === "connected" ? "green" : state.phase === "error" ? "red" : "blue"}>{getStateLabel(state.phase)}</Tag>
                    <Button icon={<InfoCircleOutlined />} disabled={!sessionActive || camera === null} onClick={() => setInfoStreamId(camera?.streamId ?? null)}>详细信息</Button>
                </Space>
            </div>
            <div className="video-frame">
                <video ref={videoRef} autoPlay muted playsInline controls />
            </div>
            {state.phase !== "error" && state.message !== null && <p className={`connection-message ${state.phase}`}>{state.message}</p>}
            <div className="button-row">
                <Button type="primary" icon={<PlayCircleOutlined />} onClick={onStart} disabled={!canStart} loading={state.phase === "connecting"}>
                    {state.phase === "connecting" ? "正在连接…" : "开始预览"}
                </Button>
                <Button icon={<StopOutlined />} onClick={onStop} disabled={!sessionActive}>
                    停止预览
                </Button>
            </div>
            <Modal open={infoOpen} title="详细信息" onCancel={() => setInfoStreamId(null)} footer={null}>
                {camera !== null && <dl>
                    <div><dt>源编码</dt><dd>{camera.sourceCodec}</dd></div>
                    <div><dt>RTSP 地址</dt><dd><Typography.Text className="rtsp-url" copyable={{ text: camera.rtspUrl }}>{camera.rtspUrl}</Typography.Text></dd></div>
                    <div><dt>RTSP 用户名</dt><dd>{rtspUsername || "未启用认证"}</dd></div>
                </dl>}
            </Modal>
        </section>
    );
}

function getStateLabel(phase: WebRtcPreviewState["phase"]): string {
    switch (phase) {
        case "connecting":
            return "连接中";
        case "connected":
            return "已连接";
        case "error":
            return "连接失败";
        default:
            return "未连接";
    }
}
