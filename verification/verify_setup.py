"""End-to-end configuration checks using a local fake Miloco and a temporary SQLite database.

Run after `dotnet build MiCamera.Net.sln -c Release`. No third-party Python packages required.
"""
import concurrent.futures
import base64
import struct
from contextlib import closing
import hashlib
import http.server
import importlib.util
import json
import os
from pathlib import Path
import re
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
PIN = "001234"
SECRET = "setup-test-password"
TOKEN = "setup-test-api-token"


class FakeMiloco(http.server.BaseHTTPRequestHandler):
    pin = PIN
    session_cookie = "test_session=authenticated"
    websocket_requests = []
    login_requests = []
    authorized = True
    malformed = False
    empty = False
    camera_requests = 0
    codec = "H265"
    frames = {}

    def log_message(self, *args):
        pass

    def reply(self, data, status=200, cookie=False):
        payload = json.dumps({"code": 0, "data": data}).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        if cookie:
            self.send_header("Set-Cookie", self.session_cookie + "; Path=/; HttpOnly")
        self.end_headers()
        self.wfile.write(payload)

    def do_POST(self):
        payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        type(self).login_requests.append(payload)
        valid = payload == {"username": "admin", "password": hashlib.md5(self.pin.encode()).hexdigest()}
        self.reply({}, 200 if valid else 401, valid)

    def do_GET(self):
        if self.headers.get("Cookie") != self.session_cookie:
            self.reply({}, 401)
        elif self.path == "/api/miot/login_status":
            self.reply({"is_logged_in": self.authorized})
        elif self.path == "/api/miot/camera_list":
            type(self).camera_requests += 1
            self.reply({} if self.malformed else [] if self.empty else [
                {"did": "123456", "name": "Living room", "room_name": "Living room", "online": True, "channel_count": 2},
                {"did": "654321", "name": "Door", "online": False, "channel_count": None}])
        elif self.path.startswith("/api/miot/ws/video_stream") and self.frames:
            type(self).websocket_requests.append(self.path)
            accept = base64.b64encode(hashlib.sha1((self.headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
            self.send_response(101)
            self.send_header("Upgrade", "websocket")
            self.send_header("Connection", "Upgrade")
            self.send_header("Sec-WebSocket-Accept", accept)
            self.end_headers()
            try:
                frames = self.frames[self.codec]
                while True:
                    for frame in frames:
                        length = len(frame)
                        header = b"\x82" + (bytes([length]) if length < 126 else b"\x7e" + struct.pack("!H", length))
                        self.connection.sendall(header + frame)
                        time.sleep(.2)
            except OSError:
                pass
        else:
            self.reply({}, 404)


def available_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


class Fixture:
    def __init__(self, web=False, media=False, debug=False):
        self.temp = tempfile.TemporaryDirectory(prefix="micamera-setup-")
        self.directory = Path(self.temp.name)
        self.media = media
        native = Path(os.environ.get("FFMPEG_NATIVE_DIR", str(ROOT / "demo/MiCamera.Net.Sample.Server/bin/Debug/net8.0/ffmpeg")))
        if media:
            assert native.is_dir(), "Set FFMPEG_NATIVE_DIR to the FFmpeg 7.1 native library directory"
            for codec, encoder, format_name, extra in (("H265", "libx265", "hevc", ["-x265-params", "aud=1:keyint=10:repeat-headers=1:log-level=error"]),
                    ("H264", "libx264", "h264", ["-x264-params", "aud=1:keyint=10:repeat-headers=1"])):
                result = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=128x96:rate=5",
                    "-t", "2", "-c:v", encoder, "-preset", "ultrafast", *extra, "-f", format_name, "pipe:1"], capture_output=True, timeout=20, check=True)
                data = result.stdout
                nals = list(re.finditer(b"\x00\x00(?:\x00)?\x01", data))
                starts = [match.start() for match in nals if ((data[match.end()] >> 1) & 63 if codec == "H265" else data[match.end()] & 31) == (35 if codec == "H265" else 9)]
                assert starts
                starts.append(len(data))
                FakeMiloco.frames[codec] = [data[left:right] for left, right in zip(starts, starts[1:])]
        self.upstream = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FakeMiloco)
        threading.Thread(target=self.upstream.serve_forever, daemon=True).start()
        self.api_port = 5080 if web else available_port()
        self.rtsp_port = available_port()
        self.base = f"http://127.0.0.1:{self.api_port}"
        self.miloco = f"http://127.0.0.1:{self.upstream.server_port}"
        self.config = self.directory / "config.json"
        self.config.write_text(json.dumps({
            "Miloco": {"AllowInvalidServerCertificate": False, "RequestTimeout": 2},
            "MediaServer": {"ListenAddress": self.base, "BearerToken": TOKEN,
                "AllowedOrigins": ["http://127.0.0.1:5081", "http://localhost:5081"],
                "Rtsp": {"ListenAddress": "127.0.0.1", "Port": self.rtsp_port},
                "WebRtc": {"Enabled": media}, "Snapshot": {"Enabled": media},
                "FFmpeg": {"Path": str(native) if media else ""}}}), encoding="utf-8")
        configuration = "Debug" if debug else "Release"
        self.assembly = ROOT / f"demo/MiCamera.Net.Sample.Server/bin/{configuration}/net8.0/MiCamera.Net.Sample.Server.dll"
        self.debug_config = self.assembly.parent / "Configs/MiCameraConfig.json" if debug else None
        self.debug_backup = self.debug_config.read_bytes() if self.debug_config and self.debug_config.exists() else None
        if self.debug_config:
            self.debug_config.parent.mkdir(exist_ok=True)
            self.debug_config.write_bytes(self.config.read_bytes())
        self.process = None
        self.log = (self.directory / "server.log").open("wb")
        self.start()

    def start(self):
        self.process = subprocess.Popen(["dotnet", str(self.assembly), str(self.config)], cwd=ROOT,
            env={**os.environ, "MICAMERA_DATA_DIRECTORY": str(self.directory / "data")}, stdout=self.log, stderr=self.log)
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise AssertionError("Backend exited: " + (self.directory / "server.log").read_text(encoding="utf-8"))
            try:
                if self.call("/api/health/live")[0] == 200:
                    return
            except OSError:
                pass
            time.sleep(.1)
        raise AssertionError("Backend did not start")

    def stop(self):
        if self.process and self.process.poll() is None:
            self.process.terminate()
            self.process.wait(timeout=15)

    def close(self):
        self.stop()
        self.upstream.shutdown()
        self.upstream.server_close()
        self.log.close()
        if self.debug_config:
            if self.debug_backup is not None:
                self.debug_config.write_bytes(self.debug_backup)
            else:
                self.debug_config.unlink(missing_ok=True)
        self.temp.cleanup()

    def call(self, path, payload=None, method=None, token=TOKEN):
        headers = {"Content-Type": "application/json"}
        if token:
            headers["Authorization"] = "Bearer " + token
        request = urllib.request.Request(self.base + path,
            data=json.dumps(payload).encode() if payload is not None else None, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as error:
            body = error.read()
            return error.code, json.loads(body) if body else None


def digest_check(port, username="viewer", password=SECRET):
    def md5(value):
        return hashlib.md5(value.encode()).hexdigest()
    with socket.create_connection(("127.0.0.1", port), timeout=3) as client:
        client.sendall(b"OPTIONS rtsp://127.0.0.1/live/front RTSP/1.0\r\nCSeq: 1\r\n\r\n")
        challenge = client.recv(4096).decode()
        assert "401 Unauthorized" in challenge
        nonce = re.search(r'nonce="([^"]+)"', challenge)[1]
        uri = "rtsp://127.0.0.1/live/front"
        response = md5(f'{md5(f"{username}:MiCamera.Net:{password}")}:{nonce}:00000001:sample:auth:{md5(f"OPTIONS:{uri}")}')
        authorization = f'Digest username="{username}", realm="MiCamera.Net", nonce="{nonce}", uri="{uri}", response="{response}", qop=auth, nc=00000001, cnonce="sample"'
        client.sendall(f"OPTIONS {uri} RTSP/1.0\r\nCSeq: 2\r\nAuthorization: {authorization}\r\n\r\n".encode())
        return client.recv(4096).decode()


def verify(fixture):
    assert fixture.call("/api/setup", token=None)[0] == 401
    assert fixture.call("/api/setup", token="wrong")[0] == 401
    assert fixture.call("/api/setup")[1] == {"configured": False, "listening": False, "username": None, "version": 0, "applyPending": False}
    assert fixture.call("/api/cameras")[1] == []
    assert fixture.call("/api/setup/activate", method="POST")[0] == 400
    assert fixture.call("/api/health/ready")[0] == 503
    swagger = fixture.call("/swagger/v1/swagger.json", token=None)[1]
    assert "/api/setup/discover" in swagger["paths"] and "/api/settings" in swagger["paths"]
    assert swagger["components"]["securitySchemes"]["Bearer"]["scheme"] == "bearer"
    with urllib.request.urlopen(fixture.base + "/swagger/index.html") as response:
        assert b"Swagger UI" in response.read()
    connection = {"baseUrl": fixture.miloco, "pin": PIN}
    assert fixture.call("/api/setup/discover", {**connection, "pin": "12345"})[0] == 400
    assert fixture.call("/api/setup/discover", {**connection, "pin": "999999"})[0] == 502
    FakeMiloco.authorized = False
    assert fixture.call("/api/setup/discover", connection)[0] == 502
    FakeMiloco.authorized = True
    FakeMiloco.malformed = True
    assert fixture.call("/api/setup/discover", connection)[0] == 502
    FakeMiloco.malformed = False
    FakeMiloco.empty = True
    assert fixture.call("/api/setup/discover", connection)[1] == []
    FakeMiloco.empty = False
    devices = fixture.call("/api/setup/discover", connection)[1]
    assert devices[0]["channelCount"] == 2 and devices[1]["channelCount"] is None
    assert fixture.call("/api/setup")[1]["configured"] is False  # Discovery never commits settings.
    stream = {"streamId": "front", "cameraDeviceId": "123456", "channel": 0, "codec": "H265", "nominalFrameRate": 25}
    payload = {**connection, "version": 0, "rtspUsername": "viewer", "rtspPassword": SECRET,
        "streams": [stream, {**stream, "streamId": "rear", "channel": 1}]}
    for streams in ([], [stream, {**stream, "streamId": "FRONT", "channel": 1}],
            [stream, {**stream, "streamId": "other"}], [{**stream, "channel": 2}],
            [{**stream, "streamId": "bad/name"}], [{**stream, "nominalFrameRate": 121}],
            [{**stream, "cameraDeviceId": "unknown"}]):
        assert fixture.call("/api/settings", {**payload, "streams": streams}, "PUT")[0] == 400
    with socket.socket() as occupied:
        occupied.bind(("127.0.0.1", fixture.rtsp_port)); occupied.listen()
        assert fixture.call("/api/settings", payload, "PUT")[0] == 503
        assert fixture.call("/api/setup")[1]["version"] == 0
    status, result = fixture.call("/api/settings", payload, "PUT")
    assert status == 200 and result["configured"] and result["listening"] and not result["applyPending"]
    assert "200 OK" in digest_check(fixture.rtsp_port)
    assert fixture.call("/api/setup/activate", method="POST")[1]["listening"] is True
    assert "401 Unauthorized" in digest_check(fixture.rtsp_port, password="wrong")
    assert len(fixture.call("/api/cameras")[1]) == 2
    assert fixture.call("/api/settings", payload, "PUT")[0] == 409
    view = fixture.call("/api/settings")[1]
    assert "rtspPassword" not in view and "pin" not in view and "milocoPasswordMd5" not in view
    with closing(sqlite3.connect(fixture.directory / "data/settings.db")) as database:
        saved = database.execute("SELECT * FROM Settings").fetchone()
        assert SECRET not in str(saved) and PIN not in str(saved)
        assert saved[3] == hashlib.md5(PIN.encode()).hexdigest()
        assert database.execute("SELECT COUNT(*) FROM Streams").fetchone()[0] == 2
    changed = {**payload, "version": 1, "pin": "", "rtspPassword": "", "streams": [{**stream, "streamId": "changed", "codec": "H264"}]}
    changed["streams"][0].update(channel=1, nominalFrameRate=30)
    old_session = verify_media(fixture, "front") if fixture.media else None
    process_id = fixture.process.pid
    old_rtsp = socket.create_connection(("127.0.0.1", fixture.rtsp_port), timeout=3)
    old_rtsp.sendall(b"OPTIONS rtsp://127.0.0.1/live/front RTSP/1.0\r\nCSeq: 1\r\n\r\n")
    assert b"401 Unauthorized" in old_rtsp.recv(4096)
    assert fixture.call("/api/settings", {**changed, "rtspUsername": "new-user"}, "PUT")[0] == 400
    FakeMiloco.codec = "H264"
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
        statuses = list(executor.map(lambda _: fixture.call("/api/settings", changed, "PUT")[0], range(2)))
    assert sorted(statuses) == [200, 409]
    assert fixture.call("/api/setup")[1]["applyPending"] is False
    cameras = fixture.call("/api/cameras")[1]
    assert [item["streamId"] for item in cameras] == ["changed"]
    assert cameras[0]["channel"] == 1 and cameras[0]["sourceCodec"] == "H264"
    assert fixture.process.pid == process_id and fixture.process.poll() is None
    try:
        assert old_rtsp.recv(4096) == b""
    except ConnectionResetError:
        pass
    finally:
        old_rtsp.close()
    assert fixture.call("/api/cameras/front/snapshot")[0] == 404
    if old_session:
        assert fixture.call(f"/api/webrtc/sessions/{old_session}", method="DELETE")[0] == 404
    if fixture.media:
        verify_media(fixture, "changed")

    class ReplacementMiloco(FakeMiloco):
        pin = "009876"
        codec = "H265"
        session_cookie = "replacement_session=authenticated"
        websocket_requests = []
        login_requests = []

    replacement = http.server.ThreadingHTTPServer(("127.0.0.1", 0), ReplacementMiloco)
    threading.Thread(target=replacement.serve_forever, daemon=True).start()
    try:
        updated = {**changed, "version": 2, "baseUrl": f"http://127.0.0.1:{replacement.server_port}",
            "pin": ReplacementMiloco.pin, "rtspUsername": "new-user", "rtspPassword": "new-password",
            "streams": [{**changed["streams"][0], "cameraDeviceId": "654321", "channel": 0, "codec": "H265"}]}
        status, applied = fixture.call("/api/settings", updated, "PUT")
        assert status == 200 and applied["version"] == 3 and applied["username"] == "new-user"
        assert applied["listening"] and not applied["applyPending"]
        assert fixture.process.pid == process_id and fixture.process.poll() is None
        assert "401 Unauthorized" in digest_check(fixture.rtsp_port)
        assert "200 OK" in digest_check(fixture.rtsp_port, "new-user", "new-password")
        camera = fixture.call("/api/cameras")[1][0]
        assert camera["cameraId"] == "654321" and camera["sourceCodec"] == "H265" and camera["channel"] == 0
        if fixture.media:
            verify_media(fixture, "changed")
            assert any("camera_id=654321&channel=0" in path for path in ReplacementMiloco.websocket_requests)
        # Discovery and the newly created stream generation both log in with the new PIN.
        deadline = time.monotonic() + 5
        while len(ReplacementMiloco.login_requests) < 2 and time.monotonic() < deadline:
            time.sleep(.1)
        assert len(ReplacementMiloco.login_requests) >= 2
        assert all(item["password"] == hashlib.md5(ReplacementMiloco.pin.encode()).hexdigest()
            for item in ReplacementMiloco.login_requests)
        with closing(sqlite3.connect(fixture.directory / "data/settings.db")) as database:
            assert database.execute("SELECT NominalFrameRate FROM Streams").fetchone()[0] == 30
        fixture.stop()
        fixture.start()
        assert fixture.call("/api/setup")[1]["applyPending"] is False
        assert fixture.call("/api/setup")[1]["version"] == 3
        assert [item["streamId"] for item in fixture.call("/api/cameras")[1]] == ["changed"]
        assert "200 OK" in digest_check(fixture.rtsp_port, "new-user", "new-password")
        if fixture.media:
            verify_media(fixture, "changed")
    finally:
        replacement.shutdown()
        replacement.server_close()
    fixture.stop()
    with closing(sqlite3.connect(fixture.directory / "data/settings.db")) as database:
        database.execute("PRAGMA user_version=99")
    broken = subprocess.run(["dotnet", str(fixture.assembly), str(fixture.config)], cwd=ROOT,
        env={**os.environ, "MICAMERA_DATA_DIRECTORY": str(fixture.directory / "data")}, capture_output=True, timeout=15)
    assert broken.returncode != 0
    with closing(sqlite3.connect(fixture.directory / "data/settings.db")) as database:
        assert database.execute("SELECT COUNT(*) FROM Streams").fetchone()[0] == 1


def verify_deployment():
    spec = importlib.util.spec_from_file_location("deployment_helper", ROOT / "deployment/helper.py")
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    with tempfile.TemporaryDirectory(prefix="micamera-deploy-") as directory:
        old = os.environ.get("MICAMERA_STATE_DIR")
        os.environ["MICAMERA_STATE_DIR"] = directory
        try:
            helper.initialize("192.168.3.10")
            config = helper.validate_deployment_config("192.168.3.10")
            assert "BaseUrl" not in config["Miloco"] and "Streams" not in config["MediaServer"]["Rtsp"]
            assert not (Path(directory) / "secrets/miloco_password_md5").exists()
            config["Miloco"].update(BaseUrl="https://old:8000", Username="admin", Password="old-test-md5")
            config["MediaServer"]["Rtsp"].update(Username="old-user", Password="old-test-password", Streams=[{}], CredentialsFilePath="/old/credentials.json")
            (Path(directory) / "MiCameraConfig.json").write_text(json.dumps(config), encoding="utf-8")
            helper.remove_file_managed_settings()
            cleaned = helper.validate_deployment_config("192.168.3.10")
            assert "Password" not in cleaned["Miloco"] and "Password" not in cleaned["MediaServer"]["Rtsp"]
            assert (Path(directory) / "MiCameraConfig.before-web-setup.json").exists()
            assert not (Path(directory) / "micamera-state/settings.db").exists()
        finally:
            if old is None:
                os.environ.pop("MICAMERA_STATE_DIR", None)
            else:
                os.environ["MICAMERA_STATE_DIR"] = old


def verify_media(fixture, stream_id):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        cameras = fixture.call("/api/cameras")[1]
        camera = next(camera for camera in cameras if camera["streamId"] == stream_id)
        if camera["state"] == "Streaming" and camera["snapshotAvailable"] and camera["webRtcAvailable"]:
            break
        time.sleep(.2)
    else:
        raise AssertionError("Media did not become available: " + str(cameras))
    request = urllib.request.Request(fixture.base + f"/api/cameras/{stream_id}/snapshot", headers={"Authorization": "Bearer " + TOKEN})
    with urllib.request.urlopen(request) as response:
        assert response.headers["Content-Type"].startswith("image/jpeg") and response.read().startswith(b"\xff\xd8")
    status, session = fixture.call("/api/webrtc/sessions", {"streamId": stream_id})
    assert status in (200, 201), session
    assert "v=0" in str(session)
    assert fixture.call("/api/health/ready")[0] == 200
    return session["sessionId"]


if __name__ == "__main__":
    fixture = Fixture(web="--serve" in sys.argv, media="--media" in sys.argv, debug="--debug" in sys.argv)
    try:
        if "--serve" in sys.argv:
            print(f"UI fixture: API={fixture.base} Miloco={fixture.miloco} PIN={PIN} Token={TOKEN}", flush=True)
            while True:
                time.sleep(1)
        else:
            verify(fixture)
            verify_deployment()
            print("PASS: setup, discovery, Swagger, hot reload without process restart, session cleanup, Digest, SQLite atomicity, concurrency, persistence and deployment configuration" + (" (Debug)" if "--debug" in sys.argv else " (Release)"))
    except Exception:
        print((fixture.directory / "server.log").read_text(encoding="utf-8")[-12000:], file=sys.stderr)
        raise
    finally:
        fixture.close()
