import type { CameraStreamInfo, IceCandidate, SessionDescription, WebRtcSessionOffer } from "../types/api";

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

    public async setAnswer(sessionId: string, answer: SessionDescription): Promise<void> {
        await this.request(`/api/webrtc/sessions/${encodeURIComponent(sessionId)}/answer`, {
            method: "POST",
            body: JSON.stringify(answer)
        });
    }

    public async addIceCandidate(sessionId: string, candidate: IceCandidate): Promise<void> {
        await this.request(`/api/webrtc/sessions/${encodeURIComponent(sessionId)}/ice-candidates`, {
            method: "POST",
            body: JSON.stringify(candidate)
        });
    }

    public async deleteSession(sessionId: string, keepalive = false): Promise<void> {
        try {
            await this.request(`/api/webrtc/sessions/${encodeURIComponent(sessionId)}`, {
                method: "DELETE",
                keepalive
            });
        } catch (error) {
            if (error instanceof MiCameraApiError && error.status === 404) {
                return;
            }

            throw error;
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

        const response = await fetch(`${this._baseUrl}${path}`, {
            ...options,
            headers
        });

        if (!response.ok) {
            throw await MiCameraApiClient.createError(response);
        }

        return response;
    }

    private static async createError(response: Response): Promise<MiCameraApiError> {
        let message = `API 请求失败（HTTP ${response.status}）。`;
        const contentType = response.headers.get("content-type") ?? "";

        try {
            if (contentType.includes("application/json")) {
                const body = await response.json() as { error?: unknown };
                if (typeof body.error === "string" && body.error.length > 0) {
                    message = body.error;
                }
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
