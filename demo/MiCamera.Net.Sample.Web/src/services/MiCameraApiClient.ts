import type { CameraStreamInfo, IceCandidate, SessionDescription, WebRtcSessionOffer } from "../types/api";

export interface SetupStatus {
    configured: boolean;
    listening: boolean;
    username: string | null;
    version: number;
    applyPending: boolean;
}

export interface CameraDevice {
    did: string;
    name: string;
    roomName: string | null;
    online: boolean;
    channelCount: number | null;
}

export interface StreamSettings {
    streamId: string;
    cameraDeviceId: string;
    channel: number;
    codec: "H264" | "H265";
    nominalFrameRate: number;
}

export interface SettingsView {
    version: number;
    milocoBaseUrl: string;
    hasMilocoPin: boolean;
    rtspUsername: string;
    hasRtspPassword: boolean;
    streams: StreamSettings[];
}

export interface SaveSettings {
    version: number;
    baseUrl: string;
    pin?: string;
    rtspUsername: string;
    rtspPassword?: string;
    streams: StreamSettings[];
}

export class MiCameraApiError extends Error {
    public readonly status: number;

    public constructor(status: number, message: string) {
        super(message);
        this.name = "MiCameraApiError";
        this.status = status;
    }
}

export class MiCameraApiClient {
    private readonly _baseUrl: string;
    private readonly _bearerToken: string;

    public constructor(baseUrl: string, bearerToken: string) {
        let parsedUrl: URL;

        try {
            parsedUrl = new URL(baseUrl);
        } catch {
            throw new Error("API 地址必须是完整的 HTTP 或 HTTPS URL。");
        }

        if (parsedUrl.protocol !== "http:" && parsedUrl.protocol !== "https:") {
            throw new Error("API 地址必须使用 HTTP 或 HTTPS 协议。");
        }

        this._baseUrl = parsedUrl.toString().replace(/\/+$/, "");
        this._bearerToken = bearerToken.trim();
    }

    public async getCameras(signal?: AbortSignal): Promise<CameraStreamInfo[]> {
        return this.requestJson<CameraStreamInfo[]>("/api/cameras", { signal });
    }

    public async getSetup(signal?: AbortSignal): Promise<SetupStatus> {
        return this.requestJson<SetupStatus>("/api/setup", { signal, cache: "no-store" });
    }

    public async getSettings(signal?: AbortSignal): Promise<SettingsView> {
        return this.requestJson<SettingsView>("/api/settings", { signal, cache: "no-store" });
    }

    public async activateSetup(signal?: AbortSignal): Promise<SetupStatus> {
        return this.requestJson<SetupStatus>("/api/setup/activate", { method: "POST", signal });
    }

    public async discover(baseUrl: string, pin?: string, signal?: AbortSignal): Promise<CameraDevice[]> {
        return this.requestJson<CameraDevice[]>("/api/setup/discover", {
            method: "POST", signal, body: JSON.stringify({ baseUrl, pin })
        });
    }

    public async saveSettings(settings: SaveSettings, signal?: AbortSignal): Promise<SetupStatus> {
        return this.requestJson<SetupStatus>("/api/settings", {
            method: "PUT", signal, body: JSON.stringify(settings)
        });
    }

    public async getSnapshot(streamId: string, signal?: AbortSignal): Promise<Blob> {
        return this.requestBlob(`/api/cameras/${encodeURIComponent(streamId)}/snapshot`, { signal });
    }

    public async createSession(streamId: string, signal?: AbortSignal): Promise<WebRtcSessionOffer> {
        return this.requestJson<WebRtcSessionOffer>("/api/webrtc/sessions", {
            method: "POST",
            signal,
            body: JSON.stringify({ streamId })
        });
    }

    public async setAnswer(sessionId: string, answer: SessionDescription, signal?: AbortSignal): Promise<void> {
        await this.request(`/api/webrtc/sessions/${encodeURIComponent(sessionId)}/answer`, {
            method: "POST",
            signal,
            body: JSON.stringify(answer)
        });
    }

    public async addIceCandidate(sessionId: string, candidate: IceCandidate, signal?: AbortSignal): Promise<void> {
        await this.request(`/api/webrtc/sessions/${encodeURIComponent(sessionId)}/ice-candidates`, {
            method: "POST",
            signal,
            body: JSON.stringify(candidate)
        });
    }

    public async deleteSession(sessionId: string, keepalive = false): Promise<void> {
        const cancellation = new AbortController();
        const timeout = setTimeout(() => cancellation.abort(), 5_000);
        try {
            await this.request(`/api/webrtc/sessions/${encodeURIComponent(sessionId)}`, {
                method: "DELETE",
                keepalive,
                signal: cancellation.signal
            });
        } catch (error) {
            if (error instanceof MiCameraApiError && error.status === 404) {
                return;
            }

            throw error;
        } finally {
            clearTimeout(timeout);
        }
    }

    private async requestJson<T>(path: string, options: RequestInit): Promise<T> {
        const response = await this.request(path, options);
        return response.json() as Promise<T>;
    }

    private async requestBlob(path: string, options: RequestInit): Promise<Blob> {
        const response = await this.request(path, { ...options, cache: "no-store" });
        return response.blob();
    }

    private async request(path: string, options: RequestInit): Promise<Response> {
        const headers = new Headers(options.headers);

        if (options.body !== undefined) {
            headers.set("Content-Type", "application/json");
        }

        if (this._bearerToken.length > 0) {
            headers.set("Authorization", `Bearer ${this._bearerToken}`);
        }

        const url = `${this._baseUrl}${path}`;
        let response: Response;
        try {
            response = await fetch(url, { ...options, headers });
        } catch (error) {
            if (error instanceof TypeError && !options.signal?.aborted) {
                throw new Error(
                    `无法访问摄像头 API（${options.method ?? "GET"} ${url}）。\n` +
                    `浏览器未收到可读取的响应，请检查服务和 API 地址，确认服务端 AllowedOrigins 包含当前页面源 ${window.location.origin}，` +
                    "并检查浏览器的本地网络访问权限及 HTTPS/HTTP 混合内容限制。\n" +
                    `原始错误：${error.message}`, { cause: error });
            }
            throw error;
        }

        if (!response.ok) {
            throw await MiCameraApiClient.createError(response);
        }

        return response;
    }

    private static async createError(response: Response): Promise<MiCameraApiError> {
        let message = response.status === 401 ? "API 鉴权失败，请检查 Bearer Token 或部署代理设置。" : `API 请求失败（HTTP ${response.status}）。`;
        const contentType = response.headers.get("content-type") ?? "";

        try {
            if (contentType.includes("json")) {
                const body = await response.json() as { error?: unknown; errors?: Record<string, string[]> };
                if (typeof body.error === "string" && body.error.length > 0) {
                    message = body.error;
                }
                else if (body.errors !== undefined) message = Object.values(body.errors).flat().join("；");
            } else {
                const body = await response.text();
                if (body.trim().length > 0) {
                    message = body;
                }
            }
        } catch {
            // Keep the status-based fallback if the server returned an unreadable error body.
        }

        return new MiCameraApiError(response.status, message);
    }
}
