import type { RefObject } from "react";
import type { CameraStreamInfo } from "../types/api";
import type { WebRtcPreviewState } from "../services/WebRtcPreviewController";

interface VideoPreviewProps {
    camera: CameraStreamInfo | null;
    state: WebRtcPreviewState;
    videoRef: RefObject<HTMLVideoElement | null>;
    onStart(): void;
    onStop(): void;
}

export function VideoPreview({ camera, state, videoRef, onStart, onStop }: VideoPreviewProps): React.JSX.Element {
    const canStart = camera !== null && camera.webRtcAvailable && state.phase !== "connecting";

    return (
        <section className="preview-panel card">
            <div className="section-heading">
                <div>
                    <p className="eyebrow">WebRTC 实时画面</p>
                    <h2>{camera?.streamId ?? "选择一台摄像头"}</h2>
                </div>
                <span className={`connection-state ${state.phase}`}>{getStateLabel(state.phase)}</span>
            </div>
            <div className="video-frame">
                <video ref={videoRef} autoPlay muted playsInline controls />
            </div>
            {state.message !== null && <p className={`connection-message ${state.phase}`}>{state.message}</p>}
            <div className="button-row">
                <button className="primary" type="button" onClick={onStart} disabled={!canStart}>
                    开始预览
                </button>
                <button className="secondary" type="button" onClick={onStop} disabled={state.phase === "idle"}>
                    停止预览
                </button>
            </div>
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
