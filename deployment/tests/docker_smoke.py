"""Isolated image smoke test. Synthetic upstream/video, never Xiaomi credentials.

Run after building the two images:
  python3 deployment/tests/docker_smoke.py --bridge-image IMAGE --web-image IMAGE
No host ports are published. Only this run's containers/network are removed.
"""
import argparse
import base64
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from unittest.mock import patch


class SmokeFailure(Exception):
    pass


def require(condition, message):
    if not condition:
        raise SmokeFailure(message)


def serve():
    cookie = os.urandom(24).hex()
    password = Path(os.environ["MILOCO_PASSWORD_FILE"]).read_text(encoding="ascii").strip()
    fixtures = {"h264": Path("/app/fixtures/sample.h264").read_bytes(),
                "h265": Path("/app/fixtures/sample.h265").read_bytes()}

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *_):
            pass

        def response(self, data, status=200, login=False):
            body = json.dumps({"code": 0, "data": data}).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            if login:
                self.send_header("Set-Cookie", f"access_token={cookie}; Path=/; HttpOnly")
            self.end_headers()
            self.wfile.write(body)

        def authorized(self):
            return f"access_token={cookie}" in self.headers.get("Cookie", "").split("; ")

        def do_POST(self):
            if self.path != "/api/auth/login":
                self.response(None, 404)
                return
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            if body.get("username") != "admin" or body.get("password") != password:
                self.response(None, 401)
                return
            self.response({"username": "admin"}, login=True)

        def do_GET(self):
            parsed = urllib.parse.urlparse(self.path)
            if not self.authorized():
                self.response(None, 401)
            elif parsed.path == "/api/miot/login_status":
                self.response({"is_logged_in": True})
            elif parsed.path == "/api/miot/ws/video_stream":
                did = urllib.parse.parse_qs(parsed.query).get("camera_id", [""])[0]
                if did not in fixtures or self.headers.get("Upgrade", "").lower() != "websocket":
                    self.response(None, 400)
                    return
                accept = base64.b64encode(hashlib.sha1((self.headers["Sec-WebSocket-Key"] +
                    "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
                self.send_response(101)
                self.send_header("Upgrade", "websocket")
                self.send_header("Connection", "Upgrade")
                self.send_header("Sec-WebSocket-Accept", accept)
                self.end_headers()
                payload = fixtures[did]
                header = bytes((0x82, len(payload))) if len(payload) < 126 else bytes((0x82, 126)) + struct.pack("!H", len(payload))
                try:
                    while True:
                        self.connection.sendall(header + payload)
                        time.sleep(0.1)
                except OSError:
                    pass
                self.close_connection = True
            else:
                self.response(None, 404)

    ThreadingHTTPServer(("0.0.0.0", 8000), Handler).serve_forever()


def probe():
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    token = Path(os.environ["RTSP_API_TOKEN_FILE"]).read_text(encoding="ascii").strip()
    base = "http://127.0.0.1:5080"
    without_media = os.environ.get("MICAMERA_SMOKE_NO_MEDIA") == "1"

    def request(path, data=None, method=None, authorized=True):
        headers = {"Authorization": "Bearer " + token} if authorized else {}
        if data is not None:
            headers["Content-Type"] = "application/json"
        message = urllib.request.Request(base + path, headers=headers, method=method,
            data=None if data is None else json.dumps(data).encode())
        try:
            with opener.open(message, timeout=40) as response:
                return response.status, response.read(), response.headers
        except urllib.error.HTTPError as response:
            return response.code, response.read(), response.headers

    deadline = time.monotonic() + 60
    while True:
        try:
            status, body, _ = request("/api/health/ready")
            health = json.loads(body)
            if without_media and status == 503 and len(health.get("streams", [])) == 2 and all(item["receiving"] for item in health["streams"]):
                break
            if not without_media and status == 200 and health.get("ready") is True:
                break
        except (OSError, urllib.error.URLError):
            pass
        require(time.monotonic() < deadline, "Synthetic camera streams did not become ready within 60 seconds.")
        time.sleep(1)

    require(request("/api/health/live")[0] == 200, "Authenticated backend liveness failed.")
    require(request("/api/health/live", authorized=False)[0] == 401, "Anonymous HTTP access was not rejected.")
    health = json.loads(body)
    require(len(health["streams"]) == 2 and health["mediaAvailable"] is not without_media, "Native media availability did not match the test mode.")
    print("PASS: authenticated HTTP health, anonymous rejection, H.264/H.265 ingest.", flush=True)

    with socket.create_connection(("127.0.0.1", 8554), timeout=5) as client:
        client.sendall(b"DESCRIBE rtsp://127.0.0.1:8554/live/h264 RTSP/1.0\r\nCSeq: 1\r\nAccept: application/sdp\r\n\r\n")
        challenge = b""
        while b"\r\n\r\n" not in challenge:
            chunk = client.recv(4096)
            require(bool(chunk) and len(challenge) < 16384, "RTSP authentication challenge was incomplete.")
            challenge += chunk
        require(challenge.startswith(b"RTSP/1.0 401 ") and b"www-authenticate: digest" in challenge.lower(),
                "Anonymous RTSP access did not require Digest authentication.")
    print("PASS: anonymous RTSP rejected with a Digest challenge.", flush=True)

    for stream in ("h264", "h265"):
        status, jpeg, headers = request(f"/api/cameras/{stream}/snapshot")
        if without_media:
            require(status == 503, f"{stream}: unavailable native JPEG did not return 503.")
        else:
            require(status == 200 and headers.get_content_type() == "image/jpeg" and jpeg[:2] == b"\xff\xd8",
                    f"{stream}: JPEG snapshot request failed.")
            with tempfile.TemporaryDirectory(prefix="micamera-jpeg-") as folder:
                path = Path(folder) / "snapshot.jpg"
                path.write_bytes(jpeg)
                decoded = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(path), "-f", "null", "-"],
                                         capture_output=True, timeout=15)
                require(decoded.returncode == 0, f"{stream}: returned JPEG was not decodable.")
        username = urllib.parse.quote(os.environ["RTSP_USERNAME"], safe="")
        password = urllib.parse.quote(Path(os.environ["RTSP_PASSWORD_FILE"]).read_text(encoding="ascii").strip(), safe="")
        uri = f"rtsp://{username}:{password}@127.0.0.1:8554/live/{stream}"
        played = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp", "-i", uri,
                                 "-frames:v", "1", "-f", "null", "-"], capture_output=True, timeout=25)
        require(played.returncode == 0, f"{stream}: RTSP Digest/TCP frame decode failed.")
        status, offer_body, _ = request("/api/webrtc/sessions", {"streamId": stream})
        if without_media and stream == "h265":
            require(status == 503, "Unavailable H.265 WebRTC transcode did not return 503.")
            print("PASS: no native media: JPEG/H.265 WebRTC return 503, RTSP still decodes both codecs.", flush=True)
            continue
        require(status == 201, f"{stream}: WebRTC offer request failed.")
        offer = json.loads(offer_body)
        session_id = offer["sessionId"]
        try:
            require(offer["offer"]["type"] == "offer" and "H264/90000" in offer["offer"]["sdp"] and
                    "a=candidate:" in offer["offer"]["sdp"], f"{stream}: offer codec/ICE candidates were missing.")
        finally:
            require(request(f"/api/webrtc/sessions/{session_id}", method="DELETE")[0] in (200, 204), "WebRTC cleanup failed.")
        print(f"PASS: {stream} {'JPEG unavailable (503)' if without_media else 'JPEG decode'}, RTSP Digest/TCP frame, H.264 WebRTC offer/cleanup (not browser media).", flush=True)


def run_images(bridge_image, web_image, without_media=False, platform=None):
    def docker(*arguments, check=True, timeout=120):
        result = subprocess.run(["docker", *arguments], capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=timeout)
        if check:
            require(result.returncode == 0, f"Docker {arguments[0]} failed; check image availability and test mount permissions.")
        return result

    for image in (bridge_image, web_image):
        docker("image", "inspect", image)
    repository = Path(__file__).resolve().parents[2]
    spec = importlib.util.spec_from_file_location("deployment_helper", repository / "deployment/helper.py")
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    prefix = "micamera-smoke-" + uuid.uuid4().hex[:12]
    containers = []
    network_created = False
    with tempfile.TemporaryDirectory(prefix="micamera-smoke-") as folder:
        state = Path(folder)
        with patch.dict(os.environ, {"MICAMERA_STATE_DIR": str(state)}):
            helper.initialize("192.168.1.100")
            config_path = state / "MiCameraConfig.json"
            config = json.loads(config_path.read_text(encoding="utf-8"))
            # The synthetic upstream is HTTP, so no TLS certificate exists to pin.
            config["Miloco"].update({"BaseUrl": "http://upstream:8000", "TrustedServerCertificatePath": ""})
            config["MediaServer"]["Rtsp"]["ListenAddress"] = "0.0.0.0"
            config["MediaServer"]["ListenAddress"] = "http://0.0.0.0:5080"
            config["MediaServer"]["WebRtc"]["BindAddress"] = "0.0.0.0"
            if without_media:
                config["MediaServer"]["FFmpeg"]["Path"] = "/micamera-missing-native"
            helper.atomic_write(config_path, json.dumps(config, indent=2) + "\n", 0o644)
            helper.stage_stream("h264", "h264", "0", "H264")
            helper.commit_stream()
            helper.stage_stream("h265", "h265", "0", "H265")
            helper.commit_stream()
            helper.write_secret("miloco_password_md5", hashlib.md5(os.urandom(32)).hexdigest())
        test_mount = f"type=bind,source={Path(__file__).resolve()},target=/smoke.py,readonly"
        password_mount = f"type=bind,source={state / 'secrets' / 'miloco_password_md5'},target=/run/secrets/miloco_password_md5,readonly"
        token_mount = f"type=bind,source={state / 'secrets' / 'rtsp_api_token'},target=/run/secrets/rtsp_api_token,readonly"
        rtsp_password_mount = f"type=bind,source={state / 'secrets' / 'rtsp_password'},target=/run/secrets/rtsp_password,readonly"
        try:
            docker("network", "create", prefix)
            network_created = True
            for service, image in (("upstream", bridge_image), ("bridge", bridge_image), ("web", web_image)):
                name = prefix + "-" + service
                args = ["run", "-d", "--name", name, "--network", prefix, "--network-alias", service]
                if platform:
                    args += ["--platform", platform]
                if service == "upstream":
                    args += ["--mount", test_mount, "--mount", password_mount,
                             "-e", "MILOCO_PASSWORD_FILE=/run/secrets/miloco_password_md5"]
                    args += ["--entrypoint", "python3", image, "/smoke.py", "serve"]
                elif service == "bridge":
                    args += ["--mount", test_mount, "--mount", password_mount, "--mount", token_mount, "--mount", rtsp_password_mount]
                    if without_media:
                        args += ["-e", "MICAMERA_SMOKE_NO_MEDIA=1"]
                    args += ["--mount", f"type=bind,source={state / 'MiCameraConfig.json'},target=/run/configs/MiCameraConfig.json,readonly", image]
                else:
                    args += [image]
                docker(*args)
                containers.append(name)
            report = docker("exec", prefix + "-bridge", "python3", "/smoke.py", "probe", check=False, timeout=180)
            print(report.stdout, end="", flush=True)
            require(report.returncode == 0, "Bridge runtime smoke test failed: " + report.stderr.strip())
            front = docker("exec", prefix + "-web", "wget", "-q", "-O", "-", "http://127.0.0.1:8080/healthz")
            require(front.stdout.strip() == "ok", "Frontend health response was not valid.")
            page = docker("exec", prefix + "-web", "wget", "-q", "-O", "-", "http://127.0.0.1:8080/")
            require('id="root"' in page.stdout, "Frontend HTML was not served.")
            print("PASS: Nginx health and frontend HTML. No Xiaomi account/real camera/browser media tested.", flush=True)
        finally:
            for name in reversed(containers):
                docker("rm", "-f", name, check=False)
            if network_created:
                docker("network", "rm", prefix, check=False)


if __name__ == "__main__":
    try:
        if len(sys.argv) == 2 and sys.argv[1] == "serve":
            serve()
        elif len(sys.argv) == 2 and sys.argv[1] == "probe":
            probe()
        else:
            parser = argparse.ArgumentParser(description=__doc__)
            parser.add_argument("--bridge-image", default="micamera-net-bridge:local")
            parser.add_argument("--web-image", default="micamera-net-web:local")
            parser.add_argument("--without-media", action="store_true", help="Force a missing native-library path; verify RTSP survives and JPEG/H.265 WebRTC return 503.")
            parser.add_argument("--platform", choices=("linux/amd64", "linux/arm64"), help="Explicit runtime platform for emulated image tests.")
            args = parser.parse_args()
            run_images(args.bridge_image, args.web_image, args.without_media, args.platform)
    except Exception as error:
        # Never print arbitrary HTTP bodies, URLs with credentials, or subprocess arguments.
        print(str(error) if isinstance(error, SmokeFailure) else f"Smoke test failed ({type(error).__name__}); secret details suppressed.", file=sys.stderr)
        sys.exit(1)
