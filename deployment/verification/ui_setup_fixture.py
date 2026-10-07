"""Disposable API fixture for browser checks: run alongside npm run dev.
Two isolated backends on 5080/5082; restart to discard all simulated settings.
No Miloco, SQLite, or real credentials are accessed.
"""
import json
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def reply(self, value, code=200):
        body = json.dumps(value).encode()
        self.send_response(code)
        self.send_header("Access-Control-Allow-Origin", "http://127.0.0.1:5081")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, Authorization")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS")
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_OPTIONS(self):
        self.reply({})

    def do_GET(self):
        state = self.server.state
        if self.path == "/api/setup":
            self.reply(dict(configured=state["version"] > 0, listening=state["version"] > 0,
                            applyPending=False, version=state["version"], username=state["rtspUsername"]))
        elif self.path == "/api/settings":
            self.reply(state)
        elif self.path == "/api/cameras":
            self.reply([dict(streamId=s["streamId"], cameraId=s["cameraDeviceId"], channel=s["channel"],
                             sourceCodec=s["codec"], state="Streaming", lastReceivedAt=None,
                             rtspUrl="rtsp://127.0.0.1:8554/" + s["streamId"],
                             snapshotAvailable=False, webRtcAvailable=True) for s in state["streams"]])
        else:
            self.reply({}, 404)

    def do_POST(self):
        values = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        if self.path == "/api/setup/discover":
            if values.get("pin") != "123456":
                return self.reply(dict(message="模拟 PIN 无效"), 400)
            time.sleep(1)
            self.reply([dict(did=did, name=name, roomName="测试房间", online=True, channelCount=count)
                        for did, name, count in [("single", "单摄测试", 1), ("dual", "双摄测试", 2), ("unknown", "未知通道测试", None)]])
        elif self.path == "/api/webrtc/sessions":
            time.sleep(8)
            self.reply(dict(message="模拟连接失败，验证弹窗关闭"), 503)
        else:
            self.reply({}, 404)

    def do_PUT(self):
        values = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        streams = values["streams"]
        assert values["version"] == self.server.state["version"], "后端配置版本不匹配。"
        assert streams and all(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}", s["streamId"]) for s in streams)
        assert len({s["streamId"].lower() for s in streams}) == len(streams)
        assert len({(s["cameraDeviceId"], s["channel"]) for s in streams}) == len(streams)
        self.server.state.update(version=values["version"] + 1, milocoBaseUrl=values["baseUrl"],
                                 rtspUsername=values["rtspUsername"], hasMilocoPin=True,
                                 hasRtspPassword=True, streams=streams)
        print("已验证配置向导保存，端口：", self.server.server_port, flush=True)
        self.reply(dict(configured=True, listening=True, applyPending=False,
                        version=self.server.state["version"], username=values["rtspUsername"]))


if __name__ == "__main__":
    for port in (5080, 5082):
        server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
        server.state = dict(version=0, milocoBaseUrl="", hasMilocoPin=False,
                            rtspUsername="", hasRtspPassword=False, streams=[])
        threading.Thread(target=server.serve_forever, daemon=True).start()
    print("临时界面验证服务正在监听 5080/5082。", flush=True)
    threading.Event().wait()
