#!/usr/bin/env python3
"""Standard-library-only deployment helper. Never prints secrets, response bodies, or cookies."""
import hashlib
import http.cookiejar
import ipaddress
import json
import os
import re
import ssl
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


class DeploymentError(Exception):
    pass


SECRET_PATTERNS = {
    "miloco_password_md5": re.compile(r"[0-9a-f]{32}"),
    "miloco_jwt_secret": re.compile(r"[0-9a-f]{64}"),
    "rtsp_api_token": re.compile(r"[0-9a-f]{64}"),
    "rtsp_password": re.compile(r"[0-9a-f]{48}"),
}
SECRET_LIMIT = 4097
LICENSE_ACKNOWLEDGEMENT = "User acknowledged the deployment license notice; this is not a license grant.\n"


def state_dir():
    return Path(os.environ.get("MICAMERA_STATE_DIR", "/state"))


def secrets_dir():
    return state_dir() / "secrets"


def atomic_write(path, content, mode=0o600):
    path = Path(path)
    descriptor, temporary = tempfile.mkstemp(prefix=".pending-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def reject_symlink(path, label):
    if Path(path).is_symlink():
        raise DeploymentError(f"部署文件不能是符号链接：{label}")


def validate_lan_ip(lan_ip):
    try:
        address = ipaddress.IPv4Address(lan_ip)
    except ipaddress.AddressValueError as error:
        raise DeploymentError("首版仅支持家庭局域网的 RFC1918 IPv4 地址。") from error
    if not (address in ipaddress.ip_network("10.0.0.0/8") or address in ipaddress.ip_network("172.16.0.0/12") or
            address in ipaddress.ip_network("192.168.0.0/16")):
        raise DeploymentError("首版仅支持家庭局域网的 RFC1918 IPv4 地址。")


def reject_legacy_layout(directory):
    for name in ("bridge.env", "miloco.env"):
        path = directory / name
        reject_symlink(path, name)
        if path.exists():
            raise DeploymentError("检测到旧版 .deploy/*.env 凭据布局。此版本不迁移旧凭据；请先完成受控备份、移除旧文件并重新初始化。")


def prepare_state_directory():
    directory = state_dir()
    reject_symlink(directory, ".deploy")
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    if not directory.is_dir():
        raise DeploymentError("部署状态路径不是目录。")
    os.chmod(directory, 0o700)
    reject_legacy_layout(directory)
    for name in ("deployment.env", "settings.json", "license-ack", "MiCameraConfig.json", "MiCameraConfig.pending.json",
                 "MiCameraConfig.previous.json", "miloco", "secrets"):
        reject_symlink(directory / name, name)
    secret_directory = secrets_dir()
    secret_directory.mkdir(mode=0o700, exist_ok=True)
    if not secret_directory.is_dir():
        raise DeploymentError("部署 secrets 路径不是目录。")
    os.chmod(secret_directory, 0o700)


def secret_path(name):
    if name not in SECRET_PATTERNS:
        raise DeploymentError("未知部署 secret。")
    directory = secrets_dir()
    reject_symlink(directory, "secrets")
    if not directory.is_dir():
        raise DeploymentError("部署 secrets 路径不存在。")
    path = directory / name
    reject_symlink(path, f"secrets/{name}")
    return path


def normalize_secret_content(content, name, allow_empty=False):
    if len(content) > SECRET_LIMIT:
        raise DeploymentError(f"secret 文件无效：{name}。")
    if content.endswith(b"\r\n"):
        content = content[:-2]
    elif content.endswith(b"\n"):
        content = content[:-1]
    if b"\r" in content or b"\n" in content:
        raise DeploymentError(f"secret 文件必须只包含一个值：{name}。")
    try:
        value = content.decode("ascii")
    except UnicodeDecodeError as error:
        raise DeploymentError(f"secret 文件必须是 ASCII：{name}。") from error
    if not value and allow_empty:
        return value
    if not SECRET_PATTERNS[name].fullmatch(value):
        raise DeploymentError(f"secret 文件格式无效：{name}。")
    return value


def read_secret(name, allow_empty=False):
    path = secret_path(name)
    if not path.is_file():
        raise DeploymentError(f"缺少部署 secret：{name}。")
    try:
        content = path.read_bytes()
    except OSError as error:
        raise DeploymentError(f"无法读取部署 secret：{name}。") from error
    return normalize_secret_content(content, name, allow_empty)


def write_secret(name, value):
    if value and not SECRET_PATTERNS[name].fullmatch(value):
        raise DeploymentError(f"secret 值格式无效：{name}。")
    if not value and name != "miloco_password_md5":
        raise DeploymentError(f"secret 值不能为空：{name}。")
    atomic_write(secret_path(name), value, 0o600)


def ensure_secret(name, value):
    path = secret_path(name)
    if path.exists():
        read_secret(name, allow_empty=name == "miloco_password_md5")
    else:
        write_secret(name, value)


def deployment_lan_ip(directory):
    path = directory / "deployment.env"
    reject_symlink(path, "deployment.env")
    if not path.is_file():
        raise DeploymentError("缺少 deployment.env。")
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeError) as error:
        raise DeploymentError("deployment.env 无法读取。") from error
    if len(lines) != 1 or not lines[0].startswith("LAN_IP="):
        raise DeploymentError("deployment.env 只能包含 LAN_IP。")
    value = lines[0].partition("=")[2]
    validate_lan_ip(value)
    return value


def validate_deployment_config(lan_ip, require_streams):
    path = state_dir() / "MiCameraConfig.json"
    reject_symlink(path, "MiCameraConfig.json")
    if not path.is_file():
        raise DeploymentError("缺少 MiCameraConfig.json。")
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise DeploymentError("MiCameraConfig.json 不是有效 JSON。") from error
    miloco = document.get("Miloco") if isinstance(document, dict) else None
    if not isinstance(miloco, dict) or miloco.get("Password", "") != "":
        raise DeploymentError("正式部署的 MiCameraConfig.json 不能包含 Miloco.Password。")
    if miloco.get("Username") != "admin":
        raise DeploymentError("正式部署的 Miloco.Username 必须为 admin。")
    if miloco.get("BaseUrl") != f"https://{lan_ip}:8000":
        raise DeploymentError("MiCameraConfig.json 的 Miloco.BaseUrl 必须匹配当前 LAN_IP。")
    if miloco.get("AllowInvalidServerCertificate") is not False:
        raise DeploymentError("正式部署必须启用 Miloco 证书固定，不能跳过 TLS 证书校验。")
    if miloco.get("TrustedServerCertificatePath") != "/run/configs/miloco-server-cert.pem":
        raise DeploymentError("正式部署的 Miloco 证书路径无效。")
    if any(name.lower() in {"rtsp", "http", "media", "webrtc", "snapshot"} for name in document):
        raise DeploymentError("旧的顶层媒体配置不再支持，请迁移至 MediaServer。")
    if any(name.lower() == "streams" for name in document):
        raise DeploymentError("旧的顶层 Streams 不再支持，请迁移至 MediaServer.Rtsp.Streams。")
    media_server = document.get("MediaServer")
    if not isinstance(media_server, dict):
        raise DeploymentError("正式部署缺少 MediaServer 配置。")
    rtsp = media_server.get("Rtsp")
    if not isinstance(rtsp, dict) or rtsp.get("ListenAddress") != lan_ip or rtsp.get("Port") != 8554 or \
            rtsp.get("Username") != "micamera" or rtsp.get("Password", "") != "":
        raise DeploymentError("正式部署的 MediaServer.Rtsp 配置无效或包含密码。")
    if media_server.get("ListenAddress") != f"http://{lan_ip}:5080" or \
            media_server.get("BearerToken", "") != "" or media_server.get("AllowedOrigins") != [f"http://{lan_ip}:5081"]:
        raise DeploymentError("正式部署的 MediaServer HTTP 配置无效或包含 Bearer Token。")
    web_rtc = media_server.get("WebRtc")
    if not isinstance(web_rtc, dict) or web_rtc.get("BindAddress") != lan_ip or \
            web_rtc.get("PortRangeStart") != 50000 or web_rtc.get("PortRangeEnd") != 50100:
        raise DeploymentError("正式部署的 MediaServer.WebRtc 配置无效。")
    ffmpeg = media_server.get("FFmpeg")
    if not isinstance(ffmpeg, dict) or ffmpeg.get("Path") != "/app/native":
        raise DeploymentError("正式部署的 MediaServer.FFmpeg 配置无效。")
    streams = rtsp.get("Streams")
    if require_streams and (not isinstance(streams, list) or not streams):
        raise DeploymentError("非交互部署至少需要配置一个摄像头流。")
    return document


def initialize(lan_ip):
    validate_lan_ip(lan_ip)
    prepare_state_directory()
    directory = state_dir()
    settings_path = directory / "settings.json"
    if settings_path.exists():
        try:
            saved_ip = json.loads(settings_path.read_text(encoding="utf-8"))["lanIp"]
        except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError) as error:
            raise DeploymentError("已保存的部署地址配置无效。") from error
        if saved_ip != lan_ip:
            raise DeploymentError("部署 IP 已变化。请先停止服务，备份配置并按文档更新访问地址。")
    else:
        atomic_write(settings_path, json.dumps({"lanIp": lan_ip}, ensure_ascii=False) + "\n")
    env_path = directory / "deployment.env"
    if env_path.exists() and deployment_lan_ip(directory) != lan_ip:
        raise DeploymentError("部署 IP 已变化。请先停止服务，备份配置并按文档更新访问地址。")
    if not env_path.exists():
        atomic_write(env_path, f"LAN_IP={lan_ip}\n")
    ensure_secret("miloco_jwt_secret", os.urandom(32).hex())
    ensure_secret("rtsp_api_token", os.urandom(32).hex())
    ensure_secret("rtsp_password", os.urandom(24).hex())
    ensure_secret("miloco_password_md5", "")
    miloco_directory = directory / "miloco"
    miloco_directory.mkdir(mode=0o700, exist_ok=True)
    os.chmod(miloco_directory, 0o700)
    config_path = directory / "MiCameraConfig.json"
    if not config_path.exists():
        atomic_write(config_path, json.dumps({
            "Miloco": {"BaseUrl": f"https://{lan_ip}:8000", "Username": "admin", "Password": "",
                       "AllowInvalidServerCertificate": False,
                       "TrustedServerCertificatePath": "/run/configs/miloco-server-cert.pem", "RequestTimeout": 15},
            "Streaming": {"ConnectTimeout": 10, "FirstKeyFrameTimeout": 60, "IdleTimeout": 60,
                          "MaxMessageBytes": 4194304, "SubscriberBufferCapacity": 32},
            "Reconnect": {"InitialDelay": 1, "MaximumDelay": 30, "BackoffMultiplier": 2, "JitterRatio": 0.2},
            "MediaServer": {
                "ListenAddress": f"http://{lan_ip}:5080", "BearerToken": "",
                "AllowedOrigins": [f"http://{lan_ip}:5081"],
                "Rtsp": {"ListenAddress": lan_ip, "Port": 8554, "PathPrefix": "/live", "Username": "micamera",
                         "Password": "", "RtpMtu": 1200, "SessionTimeout": 60, "Streams": []},
                "WebRtc": {"BindAddress": lan_ip, "PortRangeStart": 50000, "PortRangeEnd": 50100, "Enabled": True,
                           "IceServers": [], "IceGatheringTimeout": 5, "PendingSessionTimeout": 30,
                           "DisconnectedGracePeriod": 15, "TranscoderIdleTimeout": 10, "MaxPeersPerStream": 4},
                "FFmpeg": {"Path": "/app/native", "H264EncoderName": "libx264", "H264Bitrate": 2500000,
                           "H264Preset": "veryfast", "KeyFrameInterval": 2},
                "Snapshot": {"Enabled": True, "JpegQuality": 85}}}, indent=2) + "\n", 0o644)


def validate_state(lan_ip):
    validate_lan_ip(lan_ip)
    directory = state_dir()
    reject_symlink(directory, ".deploy")
    if not directory.is_dir():
        raise DeploymentError("缺少预置部署状态目录。")
    reject_legacy_layout(directory)
    if deployment_lan_ip(directory) != lan_ip:
        raise DeploymentError("deployment.env 的 LAN_IP 与当前部署地址不一致。")
    settings_path = directory / "settings.json"
    reject_symlink(settings_path, "settings.json")
    try:
        if json.loads(settings_path.read_text(encoding="utf-8"))["lanIp"] != lan_ip:
            raise DeploymentError("settings.json 的 LAN IP 与当前部署地址不一致。")
    except DeploymentError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError) as error:
        raise DeploymentError("settings.json 无效。") from error
    acknowledgement = directory / "license-ack"
    reject_symlink(acknowledgement, "license-ack")
    if not acknowledgement.is_file() or acknowledgement.read_text(encoding="utf-8") != LICENSE_ACKNOWLEDGEMENT:
        raise DeploymentError("非交互部署需要预置有效的 license-ack。")
    miloco_directory = directory / "miloco"
    reject_symlink(miloco_directory, "miloco")
    if not miloco_directory.is_dir():
        raise DeploymentError("缺少 Miloco 持久化数据目录。")
    certificate_directory = miloco_directory / "cert"
    reject_symlink(certificate_directory, "Miloco 证书目录")
    certificate_path = certificate_directory / "cert.pem"
    reject_symlink(certificate_path, "Miloco 证书")
    if not certificate_path.is_file():
        raise DeploymentError("非交互部署需要预置 Miloco 证书（.deploy/miloco/cert/cert.pem）。")
    for name in SECRET_PATTERNS:
        read_secret(name)
    validate_deployment_config(lan_ip, require_streams=True)


def runtime_secret(variable, name):
    direct = os.environ.get(variable)
    file_path = os.environ.get(variable + "_FILE")
    has_direct = bool(direct and direct.strip())
    has_file = bool(file_path and file_path.strip())
    if has_direct and has_file:
        raise DeploymentError(f"只能设置 {variable} 或 {variable}_FILE 之一。")
    if has_file:
        path = Path(file_path)
        reject_symlink(path, variable + "_FILE")
        if not path.is_file():
            raise DeploymentError(f"缺少运行时 secret：{variable}。")
        try:
            return normalize_secret_content(path.read_bytes(), name)
        except OSError as error:
            raise DeploymentError(f"无法读取运行时 secret：{variable}。") from error
    if has_direct:
        try:
            content = direct.encode("ascii")
        except UnicodeEncodeError as error:
            raise DeploymentError(f"运行时 secret 格式无效：{variable}。") from error
        return normalize_secret_content(content, name)
    return read_secret(name)


def normal_data(payload):
    if not isinstance(payload, dict) or type(payload.get("code")) is not int or payload["code"] != 0 or "data" not in payload:
        raise DeploymentError("Miloco 返回了业务错误或不支持的协议响应；响应内容已隐藏。")
    return payload["data"]


def login_status(payload):
    data = normal_data(payload)
    if not isinstance(data, dict) or type(data.get("is_logged_in")) is not bool:
        raise DeploymentError("Miloco 登录状态协议不兼容，不能将 HTTP 200 当作授权成功。")
    return data["is_logged_in"]


def miloco_opener():
    base_url = os.environ["MILOCO_BASE_URL"]
    parsed = urllib.parse.urlparse(base_url)
    if parsed.scheme != "https" or parsed.hostname != os.environ["LAN_IP"] or parsed.port != 8000:
        raise DeploymentError("部署探测只允许访问本次配置的局域网 Miloco HTTPS 接口。")
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=ssl._create_unverified_context()),
                                       urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def request_json(opener, url, data=None, headers=None, allowed_statuses=()):
    request_headers = dict(headers or {})
    if data is not None:
        request_headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=None if data is None else json.dumps(data).encode("utf-8"), headers=request_headers)
    try:
        with opener.open(request, timeout=10) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        if error.code in allowed_statuses:
            try:
                return json.load(error)
            except (ValueError, UnicodeError):
                raise DeploymentError("健康接口返回无效 JSON。") from None
        if error.code in (401, 403):
            raise DeploymentError("认证失败：请检查 Miloco 本地密码或 API Token；响应内容已隐藏。") from None
        raise DeploymentError(f"服务请求失败（HTTP {error.code}），请查看脱敏日志。") from None
    except (urllib.error.URLError, TimeoutError, OSError):
        raise DeploymentError("服务暂不可达：请检查启动状态、IP、防火墙和 TLS 配置。") from None
    except (json.JSONDecodeError, UnicodeError):
        raise DeploymentError("服务未返回有效 JSON，可能存在协议不兼容。") from None


def authenticated_miloco():
    opener = miloco_opener()
    password = runtime_secret("MILOCO_PASSWORD", "miloco_password_md5")
    normal_data(request_json(opener, os.environ["MILOCO_BASE_URL"] + "/api/auth/login",
                             {"username": "admin", "password": password}))
    if not login_status(request_json(opener, os.environ["MILOCO_BASE_URL"] + "/api/miot/login_status")):
        raise DeploymentError("小米账号尚未授权或授权已失效，请在 Miloco 页面完成绑定后重新检查。")
    return opener


def cameras():
    opener = authenticated_miloco()
    data = normal_data(request_json(opener, os.environ["MILOCO_BASE_URL"] + "/api/miot/camera_list"))
    if not isinstance(data, list) or any(not isinstance(item, dict) or not isinstance(item.get("did"), str) or
                                        not re.fullmatch(r"[A-Za-z0-9._:-]{1,128}", item["did"]) for item in data):
        raise DeploymentError("摄像头列表协议不兼容，请改为手动填写 DID。")
    atomic_write(state_dir() / "cameras.json", json.dumps(data, ensure_ascii=False) + "\n")
    for index, item in enumerate(data, 1):
        label = str(item.get("name", "未命名摄像头"))
        label = "".join(char for char in label if char.isprintable())[:80]
        print(f"  {index}. {label}（DID: {item['did']}，{'在线' if item.get('online') else '离线/未知'}）")
    print("  0. 手动输入 DID")


def stage_stream(did, stream_id, channel, codec):
    if not re.fullmatch(r"[A-Za-z0-9._:-]{1,128}", did):
        raise DeploymentError("DID 格式不正确。")
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}", stream_id):
        raise DeploymentError("流名称必须为 1–64 位字母、数字、连字符或下划线。")
    if not channel.isdecimal() or int(channel) > 2147483647 or codec not in ("H264", "H265"):
        raise DeploymentError("通道必须为非负整数，编码必须为 H264 或 H265。")
    document = json.loads((state_dir() / "MiCameraConfig.json").read_text(encoding="utf-8"))
    streams = document["MediaServer"]["Rtsp"]["Streams"]
    if any(item["StreamId"].lower() == stream_id.lower() for item in streams):
        raise DeploymentError("StreamId 已存在，请选择其他流名称。")
    if any(item["CameraDeviceId"] == did and item["Channel"] == int(channel) for item in streams):
        raise DeploymentError("该摄像头通道已配置。")
    streams.append({"StreamId": stream_id, "CameraDeviceId": did, "Channel": int(channel),
                    "Codec": codec, "NominalFrameRate": 30})
    atomic_write(state_dir() / "MiCameraConfig.pending.json", json.dumps(document, indent=2) + "\n", 0o644)


def commit_stream():
    directory = state_dir()
    original = directory / "MiCameraConfig.json"
    atomic_write(directory / "MiCameraConfig.previous.json", original.read_text(encoding="utf-8"), 0o644)
    os.replace(directory / "MiCameraConfig.pending.json", original)


def api_health(path, verbose=False):
    url = os.environ["RTSP_HTTP_LISTEN_URL"] + path
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    token = runtime_secret("RTSP_API_TOKEN", "rtsp_api_token")
    response = request_json(opener, url, headers={"Authorization": "Bearer " + token}, allowed_statuses=(503,))
    if verbose:
        print(json.dumps(response, ensure_ascii=False, indent=2))
    return response


def main(arguments):
    command = arguments[0]
    if command == "init":
        initialize(arguments[1])
    elif command == "validate-state":
        validate_state(arguments[1])
    elif command == "set-password":
        password = sys.stdin.read(4097)
        if not password or len(password) > 4096:
            raise DeploymentError("Miloco 本地密码不能为空或超过 4096 字符。")
        write_secret("miloco_password_md5", hashlib.md5(password.encode("utf-8")).hexdigest())
    elif command == "miloco-live":
        normal_data(request_json(miloco_opener(), os.environ["MILOCO_BASE_URL"] + "/api/auth/register-status"))
    elif command == "authorize":
        authenticated_miloco()
        print("✓ Miloco 本地登录和小米账号授权有效。")
    elif command == "cameras":
        cameras()
    elif command == "select-camera":
        data = json.loads((state_dir() / "cameras.json").read_text(encoding="utf-8"))
        index = int(arguments[1])
        if index < 1 or index > len(data):
            raise DeploymentError("摄像头序号不正确。")
        did = data[index - 1]["did"]
        if not re.fullmatch(r"[A-Za-z0-9._:-]{1,128}", did):
            raise DeploymentError("摄像头 DID 不符合部署配置格式，请手工核对。")
        print(did)
    elif command == "stage-stream":
        stage_stream(*arguments[1:])
    elif command == "commit-stream":
        commit_stream()
    elif command == "rollback-stream":
        directory = state_dir()
        atomic_write(directory / "MiCameraConfig.json", (directory / "MiCameraConfig.previous.json").read_text(encoding="utf-8"), 0o644)
    elif command == "stream-count":
        print(len(json.loads((state_dir() / "MiCameraConfig.json").read_text(encoding="utf-8"))["MediaServer"]["Rtsp"]["Streams"]))
    elif command == "live":
        if api_health("/api/health/live").get("live") is not True:
            raise DeploymentError("后端存活检查失败。")
    elif command == "ready":
        if api_health("/api/health/ready", "--verbose" in arguments).get("ready") is not True:
            raise DeploymentError("后端媒体能力或摄像头视频尚未就绪。")
    elif command == "status":
        api_health("/api/health/ready", True)
    elif command == "ack-license":
        atomic_write(state_dir() / "license-ack", LICENSE_ACKNOWLEDGEMENT)
    elif command == "credentials":
        print(f"RTSP_API_TOKEN={read_secret('rtsp_api_token')}")
        print("RTSP_USERNAME=micamera")
        print(f"RTSP_PASSWORD={read_secret('rtsp_password')}")
    else:
        raise DeploymentError("未知部署辅助命令。")


if __name__ == "__main__":
    try:
        main(sys.argv[1:])
    except (DeploymentError, ValueError, KeyError, IndexError, OSError) as error:
        print(str(error) if isinstance(error, DeploymentError) else "部署辅助操作失败，请检查参数、文件权限和配置格式。", file=sys.stderr)
        sys.exit(1)
