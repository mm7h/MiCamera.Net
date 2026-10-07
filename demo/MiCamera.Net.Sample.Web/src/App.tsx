import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { CameraList } from "./components/CameraList";
import { SetupGate } from "./components/SetupGate";
import { SetupWizard } from "./components/SetupWizard";
import { Alert, Button, ConfigProvider, theme as antTheme } from "antd";
import zhCN from "antd/locale/zh_CN";
import { SettingOutlined, ReloadOutlined, BulbOutlined } from "@ant-design/icons";
import { ErrorDrawer, type ErrorNotification } from "./components/ErrorDrawer";
import { SnapshotPreview } from "./components/SnapshotPreview";
import { VideoPreview } from "./components/VideoPreview";
import { useColorTheme } from "./hooks/useColorTheme";
import { useWebRtcPreview } from "./hooks/useWebRtcPreview";
import { MiCameraApiClient, type SetupStatus } from "./services/MiCameraApiClient";
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
    const [connection, setConnection] = useState<ConnectionConfiguration>({
        apiUrl: defaultApiUrl,
        bearerToken: ""
    });
    const [cameras, setCameras] = useState<CameraStreamInfo[]>([]);
    const [settingsOpen, setSettingsOpen] = useState(false);
    const pendingSetupRef = useRef<SetupStatus | null>(null);
    const [rtspSetup, setRtspSetup] = useState<SetupStatus | null>(null);
    const [notification, setNotification] = useState<ErrorNotification | null>(null);
    const nextNotificationIdRef = useRef(0);
    const [camerasLoading, setCamerasLoading] = useState(false);
    const [selectedStreamId, setSelectedStreamId] = useState<string | null>(null);
    const [snapshotHistory, setSnapshotHistory] = useState<SnapshotHistoryItem[]>([]);
    const [snapshotLoading, setSnapshotLoading] = useState(false);
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
    const notifyError = useCallback((title: string, message: string): void => {
        setNotification({ id: ++nextNotificationIdRef.current, title, message });
    }, []);
    const closeNotification = useCallback(() => setNotification(null), []);
    const notifyPreviewError = useCallback((message: string): void => {
        notifyError("视频连接失败", message);
    }, [notifyError]);
    const { state: previewState, videoRef, start: startPreview, stop: stopPreview } = useWebRtcPreview(apiClient, notifyPreviewError);

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
        if (rtspSetup?.configured !== true) return;
        if (apiClient === null) {
            setCameras([]);
            setSelectedStreamId(null);
            notifyError("连接配置无效", clientConfiguration.error ?? "请先保存有效的 API 地址。");
            return;
        }

        cameraAbortRef.current?.abort();
        const controller = new AbortController();
        const requestId = ++cameraRequestIdRef.current;
        cameraAbortRef.current = controller;
        setCamerasLoading(true);

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
            notifyError("摄像头列表加载失败", getErrorMessage(error));
        } finally {
            if (requestId === cameraRequestIdRef.current) {
                setCamerasLoading(false);
            }
        }
    }, [apiClient, clientConfiguration.error, notifyError, rtspSetup]);

    const refreshSnapshot = useCallback(async (): Promise<void> => {
        if (apiClient === null || selectedStreamId === null || selectedCamera?.snapshotAvailable !== true) {
            snapshotAbortRef.current?.abort();
            setSnapshotLoading(false);
            return;
        }

        snapshotAbortRef.current?.abort();
        const controller = new AbortController();
        snapshotAbortRef.current = controller;
        setSnapshotLoading(true);

        try {
            const blob = await apiClient.getSnapshot(selectedStreamId, controller.signal);
            if (controller.signal.aborted) {
                return;
            }

            addSnapshotToHistory(selectedStreamId, blob);
        } catch (error) {
            if (!controller.signal.aborted) {
                notifyError("截图获取失败", getErrorMessage(error));
            }
        } finally {
            if (!controller.signal.aborted) {
                setSnapshotLoading(false);
            }
        }
    }, [addSnapshotToHistory, apiClient, notifyError, selectedCamera?.snapshotAvailable, selectedStreamId]);

    useEffect(() => {
        if (rtspSetup?.configured !== true) return;
        if (hasLoadedInitialCamerasRef.current && !pendingCameraRefreshRef.current) {
            return;
        }

        hasLoadedInitialCamerasRef.current = true;
        pendingCameraRefreshRef.current = false;
        void refreshCameras();
    }, [refreshCameras, rtspSetup]);

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

    const applyConnection = (next: ConnectionConfiguration, status: SetupStatus): void => {
        pendingSetupRef.current = status;
        if (next.apiUrl === connection.apiUrl && next.bearerToken === connection.bearerToken) return;
        cameraAbortRef.current?.abort();
        cameraRequestIdRef.current += 1;
        void stopPreview();
        clearSnapshotHistory();
        setCameras([]);
        setSelectedStreamId(null);
        pendingCameraRefreshRef.current = false;
        setConnection(next);
    };

    const selectCamera = async (streamId: string): Promise<void> => {
        if (streamId === selectedStreamId) {
            return;
        }

        await stopPreview();
        setSelectedStreamId(streamId);
    };

    if (rtspSetup?.configured !== true) {
        return <ConfigProvider locale={zhCN} theme={{ algorithm: theme === "dark" ? antTheme.darkAlgorithm : antTheme.defaultAlgorithm, token: { colorPrimary: "#2878d4", borderRadius: 10 } }}>
            <SetupGate client={apiClient} connection={connection} onConnected={applyConnection} onReady={setRtspSetup} /></ConfigProvider>;
    }

    return (
        <ConfigProvider locale={zhCN} theme={{ algorithm: theme === "dark" ? antTheme.darkAlgorithm : antTheme.defaultAlgorithm, token: { colorPrimary: "#2878d4", borderRadius: 10 } }}>
        <main className="app-shell">
            <header className="app-header">
                <div>
                    <p className="eyebrow">MiCamera.Net · Preview</p>
                    <h1>视频流预览台</h1>
                    <p className="subtitle">通过 WebRTC 在浏览器中预览米家摄像头画面。</p>
                </div>
                <div className="header-actions">
                    <Button icon={<BulbOutlined />} onClick={toggleTheme}>
                        {theme === "light" ? "切换深色" : "切换浅色"}
                    </Button>
                    <Button icon={<SettingOutlined />} onClick={() => setSettingsOpen(true)}>重新配置</Button>
                    <Button icon={<ReloadOutlined />} onClick={() => void refreshCameras()} loading={camerasLoading}>
                        刷新摄像头
                    </Button>
                </div>
            </header>

            {rtspSetup.applyPending && <Alert className="setup-notice" type="info" showIcon title="配置已保存但尚未生效，请重新打开配置并重试应用。" />}
            {settingsOpen && apiClient !== null && <SetupWizard client={apiClient} connection={connection} onConnected={applyConnection} editing onCancel={() => {
                if (pendingSetupRef.current !== null) {
                    pendingCameraRefreshRef.current = true;
                    setRtspSetup(pendingSetupRef.current);
                    pendingSetupRef.current = null;
                }
                setSettingsOpen(false);
            }}
                onComplete={(status) => {
                    void stopPreview();
                    snapshotAbortRef.current?.abort();
                    clearSnapshotHistory();
                    pendingCameraRefreshRef.current = true;
                    pendingSetupRef.current = null;
                    setRtspSetup(status); setSettingsOpen(false);
                }} />}
            <ErrorDrawer notification={notification} onClose={closeNotification} />

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
                    rtspUsername={rtspSetup.username}
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
                    onRefresh={() => void refreshSnapshot()}
                    onDeleteSnapshot={removeSnapshotFromHistory}
                />
            </div>

        </main></ConfigProvider>
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

function getErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : "请求摄像头服务时发生未知错误。";
}
