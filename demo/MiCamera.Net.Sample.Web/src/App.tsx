import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { CameraList } from "./components/CameraList";
import { ConnectionSettings } from "./components/ConnectionSettings";
import { SnapshotPreview } from "./components/SnapshotPreview";
import { VideoPreview } from "./components/VideoPreview";
import { useColorTheme } from "./hooks/useColorTheme";
import { useWebRtcPreview } from "./hooks/useWebRtcPreview";
import { MiCameraApiClient } from "./services/MiCameraApiClient";
import type { CameraStreamInfo } from "./types/api";
import type { SnapshotHistoryItem } from "./types/snapshot";

interface ConnectionConfiguration {
    apiUrl: string;
    bearerToken: string;
}

interface ClientConfiguration {
    client: MiCameraApiClient | null;
    error: string | null;
}

const MaximumScreenshotHistoryItems = 40;

export default function App(): React.JSX.Element {
    const { theme, toggleTheme } = useColorTheme();
    const defaultApiUrl = useMemo(getDefaultApiUrl, []);
    const [draftApiUrl, setDraftApiUrl] = useState(defaultApiUrl);
    const [draftBearerToken, setDraftBearerToken] = useState("");
    const [connection, setConnection] = useState<ConnectionConfiguration>({
        apiUrl: defaultApiUrl,
        bearerToken: ""
    });
    const [cameras, setCameras] = useState<CameraStreamInfo[]>([]);
    const [cameraError, setCameraError] = useState<string | null>(null);
    const [camerasLoading, setCamerasLoading] = useState(false);
    const [selectedStreamId, setSelectedStreamId] = useState<string | null>(null);
    const [snapshotHistory, setSnapshotHistory] = useState<SnapshotHistoryItem[]>([]);
    const [snapshotError, setSnapshotError] = useState<string | null>(null);
    const [snapshotLoading, setSnapshotLoading] = useState(false);
    const [copyMessage, setCopyMessage] = useState<string | null>(null);
    const cameraAbortRef = useRef<AbortController | null>(null);
    const cameraRequestIdRef = useRef(0);
    const hasLoadedInitialCamerasRef = useRef(false);
    const pendingCameraRefreshRef = useRef(false);
    const snapshotAbortRef = useRef<AbortController | null>(null);
    const snapshotHistoryRef = useRef<SnapshotHistoryItem[]>([]);
    const nextSnapshotIdRef = useRef(0);

    const clientConfiguration = useMemo<ClientConfiguration>(() => {
        try {
            return {
                client: new MiCameraApiClient(connection.apiUrl, connection.bearerToken),
                error: null
            };
        } catch (error) {
            return {
                client: null,
                error: getErrorMessage(error)
            };
        }
    }, [connection]);

    const apiClient = clientConfiguration.client;
    const selectedCamera = cameras.find((camera) => camera.streamId === selectedStreamId) ?? null;
    const { state: previewState, videoRef, start: startPreview, stop: stopPreview } = useWebRtcPreview(apiClient);

    const clearSnapshotHistory = useCallback((): void => {
        for (const item of snapshotHistoryRef.current) {
            URL.revokeObjectURL(item.url);
        }

        snapshotHistoryRef.current = [];
        setSnapshotHistory([]);
    }, []);

    const removeSnapshotFromHistory = useCallback((itemId: number): void => {
        const item = snapshotHistoryRef.current.find((entry) => entry.id === itemId);
        if (item === undefined) {
            return;
        }

        URL.revokeObjectURL(item.url);
        snapshotHistoryRef.current = snapshotHistoryRef.current.filter((entry) => entry.id !== itemId);
        setSnapshotHistory(snapshotHistoryRef.current);
    }, []);

    const addSnapshotToHistory = useCallback((streamId: string, blob: Blob): void => {
        const item: SnapshotHistoryItem = {
            id: ++nextSnapshotIdRef.current,
            streamId,
            capturedAt: new Date(),
            url: URL.createObjectURL(blob)
        };
        const nextHistory = [item, ...snapshotHistoryRef.current];
        const expiredItems = nextHistory.slice(MaximumScreenshotHistoryItems);

        for (const expiredItem of expiredItems) {
            URL.revokeObjectURL(expiredItem.url);
        }

        snapshotHistoryRef.current = nextHistory.slice(0, MaximumScreenshotHistoryItems);
        setSnapshotHistory(snapshotHistoryRef.current);
    }, []);

    const refreshCameras = useCallback(async (): Promise<void> => {
        if (apiClient === null) {
            setCameras([]);
            setSelectedStreamId(null);
            setCameraError(clientConfiguration.error);
            return;
        }

        cameraAbortRef.current?.abort();
        const controller = new AbortController();
        const requestId = ++cameraRequestIdRef.current;
        cameraAbortRef.current = controller;
        setCamerasLoading(true);
        setCameraError(null);

        try {
            const streams = await apiClient.getCameras(controller.signal);
            if (requestId !== cameraRequestIdRef.current) {
                return;
            }

            setCameras(streams);
            setSelectedStreamId((currentStreamId) => {
                if (currentStreamId !== null && streams.some((stream) => stream.streamId === currentStreamId)) {
                    return currentStreamId;
                }

                return streams[0]?.streamId ?? null;
            });
        } catch (error) {
            if (controller.signal.aborted || requestId !== cameraRequestIdRef.current) {
                return;
            }

            setCameras([]);
            setSelectedStreamId(null);
            setCameraError(getErrorMessage(error));
        } finally {
            if (requestId === cameraRequestIdRef.current) {
                setCamerasLoading(false);
            }
        }
    }, [apiClient, clientConfiguration.error]);

    const refreshSnapshot = useCallback(async (): Promise<void> => {
        if (apiClient === null || selectedStreamId === null || selectedCamera?.snapshotAvailable !== true) {
            snapshotAbortRef.current?.abort();
            setSnapshotLoading(false);
            setSnapshotError(null);
            return;
        }

        snapshotAbortRef.current?.abort();
        const controller = new AbortController();
        snapshotAbortRef.current = controller;
        setSnapshotLoading(true);
        setSnapshotError(null);

        try {
            const blob = await apiClient.getSnapshot(selectedStreamId, controller.signal);
            if (controller.signal.aborted) {
                return;
            }

            addSnapshotToHistory(selectedStreamId, blob);
        } catch (error) {
            if (!controller.signal.aborted) {
                setSnapshotError(getErrorMessage(error));
            }
        } finally {
            if (!controller.signal.aborted) {
                setSnapshotLoading(false);
            }
        }
    }, [addSnapshotToHistory, apiClient, selectedCamera?.snapshotAvailable, selectedStreamId]);

    useEffect(() => {
        if (hasLoadedInitialCamerasRef.current && !pendingCameraRefreshRef.current) {
            return;
        }

        hasLoadedInitialCamerasRef.current = true;
        pendingCameraRefreshRef.current = false;
        void refreshCameras();
    }, [refreshCameras]);

    useEffect(() => {
        void refreshSnapshot();

        return () => {
            snapshotAbortRef.current?.abort();
        };
    }, [refreshSnapshot]);

    useEffect(() => () => {
        cameraAbortRef.current?.abort();
        clearSnapshotHistory();
    }, [clearSnapshotHistory]);

    useEffect(() => {
        if (selectedStreamId !== null && !cameras.some((camera) => camera.streamId === selectedStreamId)) {
            void stopPreview();
        }
    }, [cameras, selectedStreamId, stopPreview]);

    const applyConnection = (): void => {
        void stopPreview();
        clearSnapshotHistory();
        setCameras([]);
        setSelectedStreamId(null);
        pendingCameraRefreshRef.current = true;
        setConnection({
            apiUrl: draftApiUrl.trim(),
            bearerToken: draftBearerToken
        });
    };

    const selectCamera = async (streamId: string): Promise<void> => {
        if (streamId === selectedStreamId) {
            return;
        }

        await stopPreview();
        setSelectedStreamId(streamId);
        setCopyMessage(null);
    };

    const copyRtspUrl = async (): Promise<void> => {
        if (selectedCamera === null) {
            return;
        }

        try {
            await navigator.clipboard.writeText(selectedCamera.rtspUrl);
            setCopyMessage("RTSP 地址已复制。");
        } catch {
            setCopyMessage("无法访问剪贴板，请手动复制 RTSP 地址。");
        }
    };

    return (
        <main className="app-shell">
            <header className="app-header">
                <div>
                    <p className="eyebrow">MiCamera.Net · Preview</p>
                    <h1>视频流预览台</h1>
                    <p className="subtitle">通过 WebRTC 在浏览器中预览米家摄像头画面。</p>
                </div>
                <div className="header-actions">
                    <button className="secondary" type="button" onClick={toggleTheme}>
                        {theme === "light" ? "切换深色" : "切换浅色"}
                    </button>
                    <button className="secondary" type="button" onClick={() => void refreshCameras()} disabled={camerasLoading}>
                        {camerasLoading ? "正在刷新…" : "刷新摄像头"}
                    </button>
                </div>
            </header>

            <ConnectionSettings
                apiUrl={draftApiUrl}
                bearerToken={draftBearerToken}
                busy={camerasLoading}
                onApiUrlChange={setDraftApiUrl}
                onBearerTokenChange={setDraftBearerToken}
                onSubmit={applyConnection}
            />

            {cameraError !== null && <p className="error-banner">{cameraError}</p>}

            <div className="workspace-grid">
                <section className="camera-browser card">
                    <div className="section-heading">
                        <div>
                            <p className="eyebrow">可用视频流</p>
                            <h2>摄像头</h2>
                        </div>
                        {cameras.length > 0 && <span className="camera-count">{cameras.length}</span>}
                    </div>
                    <CameraList
                        cameras={cameras}
                        selectedStreamId={selectedStreamId}
                        onSelect={(streamId) => void selectCamera(streamId)}
                    />
                </section>

                <VideoPreview
                    camera={selectedCamera}
                    state={previewState}
                    videoRef={videoRef}
                    onStart={() => {
                        if (selectedStreamId !== null) {
                            void startPreview(selectedStreamId);
                        }
                    }}
                    onStop={() => void stopPreview()}
                />

                <SnapshotPreview
                    available={selectedCamera?.snapshotAvailable ?? false}
                    snapshotUrl={getLatestSnapshotUrl(snapshotHistory, selectedStreamId)}
                    historyItems={getSnapshotsForStream(snapshotHistory, selectedStreamId)}
                    loading={snapshotLoading}
                    message={snapshotError}
                    onRefresh={() => void refreshSnapshot()}
                    onDeleteSnapshot={removeSnapshotFromHistory}
                />
            </div>

            {selectedCamera !== null && (
                <section className="stream-details card">
                    <div>
                        <p className="eyebrow">辅助信息</p>
                        <h2>{selectedCamera.streamId}</h2>
                        <dl>
                            <div>
                                <dt>源编码</dt>
                                <dd>{selectedCamera.sourceCodec}</dd>
                            </div>
                            <div>
                                <dt>最后收帧</dt>
                                <dd>{formatDateTime(selectedCamera.lastReceivedAt)}</dd>
                            </div>
                            <div>
                                <dt>RTSP 地址</dt>
                                <dd className="rtsp-url">{selectedCamera.rtspUrl}</dd>
                            </div>
                        </dl>
                    </div>
                    <div className="details-actions">
                        <button className="secondary" type="button" onClick={() => void copyRtspUrl()}>
                            复制 RTSP 地址
                        </button>
                        {copyMessage !== null && <p className="help-text">{copyMessage}</p>}
                    </div>
                </section>
            )}
        </main>
    );
}

function getLatestSnapshotUrl(items: readonly SnapshotHistoryItem[], streamId: string | null): string | null {
    return getSnapshotsForStream(items, streamId)[0]?.url ?? null;
}

function getSnapshotsForStream(
    items: readonly SnapshotHistoryItem[],
    streamId: string | null): SnapshotHistoryItem[] {
    if (streamId === null) {
        return [];
    }

    return items.filter((item) => item.streamId === streamId);
}

function getDefaultApiUrl(): string {
    const configuredUrl = import.meta.env.VITE_MICAMERA_API_URL?.trim();
    if (configuredUrl !== undefined && configuredUrl.length > 0) {
        return configuredUrl;
    }

    // The deployment image serves this app and proxies /api on the same origin.
    if (import.meta.env.VITE_MICAMERA_SAME_ORIGIN_API === "true") {
        return window.location.origin;
    }

    const protocol = window.location.protocol === "https:" ? "https:" : "http:";
    return `${protocol}//${window.location.hostname}:5080`;
}

function formatDateTime(value: string | null): string {
    if (value === null) {
        return "尚未收到视频数据";
    }

    const timestamp = new Date(value);
    if (Number.isNaN(timestamp.getTime())) {
        return value;
    }

    return new Intl.DateTimeFormat("zh-CN", {
        dateStyle: "medium",
        timeStyle: "medium"
    }).format(timestamp);
}

function getErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : "请求摄像头服务时发生未知错误。";
}
