import { afterEach, describe, expect, it, vi } from "vitest";
import { MiCameraApiClient, MiCameraApiError } from "./MiCameraApiClient";

describe("MiCameraApiClient", () => {
    afterEach(() => {
        vi.unstubAllGlobals();
    });

    it("gets cameras with the configured bearer token", async () => {
        const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify([
            {
                streamId: "living-room",
                cameraId: "camera-id",
                channel: 0,
                sourceCodec: "H265",
                state: "Streaming",
                lastReceivedAt: null,
                rtspUrl: "rtsp://127.0.0.1:8554/live/living-room",
                snapshotAvailable: true,
                webRtcAvailable: true
            }
        ]), {
            headers: { "content-type": "application/json" }
        }));
        vi.stubGlobal("fetch", fetchMock);
        const client = new MiCameraApiClient("http://127.0.0.1:5080/", "demo-token");

        const cameras = await client.getCameras();

        expect(cameras).toHaveLength(1);
        expect(cameras[0]?.streamId).toBe("living-room");
        const [url, request] = fetchMock.mock.calls[0] as [string, RequestInit];
        expect(url).toBe("http://127.0.0.1:5080/api/cameras");
        expect(new Headers(request.headers).get("Authorization")).toBe("Bearer demo-token");
    });

    it("returns the server's JSON error message", async () => {
        vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({
            error: "A decoded key-frame snapshot is not available yet."
        }), {
            status: 503,
            headers: { "content-type": "application/json" }
        })));
        const client = new MiCameraApiClient("http://127.0.0.1:5080", "");

        await expect(client.getSnapshot("living-room")).rejects.toEqual(
            new MiCameraApiError(503, "A decoded key-frame snapshot is not available yet."));
    });

    it("does not send an end-of-candidates marker to the server", async () => {
        const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
        vi.stubGlobal("fetch", fetchMock);
        const client = new MiCameraApiClient("http://127.0.0.1:5080", "");

        await client.addIceCandidate("session", {
            candidate: "candidate:1 1 udp 1 127.0.0.1 9 typ host",
            sdpMid: "0",
            sdpMLineIndex: 0,
            usernameFragment: null
        });

        const [, request] = fetchMock.mock.calls[0] as [string, RequestInit];
        expect(request.body).toBe(JSON.stringify({
            candidate: "candidate:1 1 udp 1 127.0.0.1 9 typ host",
            sdpMid: "0",
            sdpMLineIndex: 0,
            usernameFragment: null
        }));
    });
});
