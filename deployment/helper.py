#!/usr/bin/env python3
"""Standard-library-only deployment helper. Never prints secrets, response bodies, or cookies."""
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
    "miloco_jwt_secret": re.compile(r"[0-9a-f]{64}"),
    "rtsp_api_token": re.compile(r"[0-9a-f]{64}"),
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
        raise DeploymentError("部署密钥目录不是目录。")
    os.chmod(secret_directory, 0o700)


def secret_path(name):
    if name not in SECRET_PATTERNS:
        raise DeploymentError("未知部署密钥。")
    directory = secrets_dir()
    reject_symlink(directory, "secrets")
    if not directory.is_dir():
        raise DeploymentError("部署密钥目录不存在。")
    path = directory / name
    reject_symlink(path, f"secrets/{name}")
    return path


def normalize_secret_content(content, name, allow_empty=False):
    if len(content) > SECRET_LIMIT:
        raise DeploymentError(f"密钥文件无效：{name}。")
    if content.endswith(b"\r\n"):
        content = content[:-2]
    elif content.endswith(b"\n"):
        content = content[:-1]
    if b"\r" in content or b"\n" in content:
        raise DeploymentError(f"密钥文件必须只包含一个值：{name}。")
    try:
        value = content.decode("ascii")
    except UnicodeDecodeError as error:
        raise DeploymentError(f"密钥文件必须是 ASCII：{name}。") from error
    if not value and allow_empty:
        return value
    if not SECRET_PATTERNS[name].fullmatch(value):
        raise DeploymentError(f"密钥文件格式无效：{name}。")
    return value


def read_secret(name, allow_empty=False):
    path = secret_path(name)
    if not path.is_file():
        raise DeploymentError(f"缺少部署密钥：{name}。")
    try:
        content = path.read_bytes()
    except OSError as error:
        raise DeploymentError(f"无法读取部署密钥：{name}。") from error
    return normalize_secret_content(content, name, allow_empty)


def write_secret(name, value):
    if value and not SECRET_PATTERNS[name].fullmatch(value):
        raise DeploymentError(f"密钥值格式无效：{name}。")
    if not value:
        raise DeploymentError(f"密钥值不能为空：{name}。")
    # Docker Compose mounts file-backed secrets with their host file mode.  The
    # bridge runs as an unprivileged UID, while the enclosing secrets directory
    # remains 0700, so the file itself must be readable inside that mount.
    atomic_write(secret_path(name), value, 0o644)


def ensure_secret(name, value):
    path = secret_path(name)
    if path.exists():
        read_secret(name)
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


def validate_deployment_config(lan_ip):
    path = state_dir() / "MiCameraConfig.json"
    reject_symlink(path, "MiCameraConfig.json")
    if not path.is_file():
        raise DeploymentError("缺少 MiCameraConfig.json。")
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise DeploymentError("MiCameraConfig.json 不是有效 JSON。") from error
    miloco = document.get("Miloco") if isinstance(document, dict) else None
    if not isinstance(miloco, dict) or any(key in miloco for key in ("BaseUrl", "Username", "Password")):
        raise DeploymentError("Miloco 地址及凭据应通过网页配置，不能出现在部署配置中。")
    if miloco.get("AllowInvalidServerCertificate") is not False:
        raise DeploymentError("正式部署必须启用 Miloco 证书固定，不能跳过 TLS 证书校验。")
    if miloco.get("TrustedServerCertificatePath") != "/run/configs/miloco-server-cert.pem":
        raise DeploymentError("正式部署的 Miloco 证书路径无效。")
    if any(name.lower() in {"rtsp", "http", "media", "webrtc", "snapshot"} for name in document):
        raise DeploymentError("旧的顶层媒体配置不再支持，请迁移至 MediaServer。")
    if any(name.lower() == "streams" for name in document):
        raise DeploymentError("旧的顶层 Streams 不再支持，请通过网页选择摄像头。")
    media_server = document.get("MediaServer")
    if not isinstance(media_server, dict):
        raise DeploymentError("正式部署缺少 MediaServer 配置。")
    rtsp = media_server.get("Rtsp")
    if not isinstance(rtsp, dict) or rtsp.get("ListenAddress") != lan_ip or rtsp.get("Port") != 8554 or \
            any(key in rtsp for key in ("Username", "Password", "CredentialsFilePath", "Streams")):
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
    miloco_directory = directory / "miloco"
    miloco_directory.mkdir(mode=0o700, exist_ok=True)
    os.chmod(miloco_directory, 0o700)
    config_path = directory / "MiCameraConfig.json"
    if not config_path.exists():
        atomic_write(config_path, json.dumps({
            "Miloco": {"AllowInvalidServerCertificate": False,
                       "TrustedServerCertificatePath": "/run/configs/miloco-server-cert.pem", "RequestTimeout": 15},
            "Streaming": {"ConnectTimeout": 10, "FirstKeyFrameTimeout": 60, "IdleTimeout": 60,
                          "MaxMessageBytes": 4194304, "SubscriberBufferCapacity": 32},
            "Reconnect": {"InitialDelay": 1, "MaximumDelay": 30, "BackoffMultiplier": 2, "JitterRatio": 0.2},
            "MediaServer": {
                "ListenAddress": f"http://{lan_ip}:5080",
                "AllowedOrigins": [f"http://{lan_ip}:5081"],
                "Rtsp": {"ListenAddress": lan_ip, "Port": 8554, "PathPrefix": "/live",
                         "RtpMtu": 1200, "SessionTimeout": 60},
                "WebRtc": {"BindAddress": lan_ip, "PortRangeStart": 50000, "PortRangeEnd": 50100, "Enabled": True,
                           "IceGatheringTimeout": 5, "PendingSessionTimeout": 30,
                           "DisconnectedGracePeriod": 15, "PlayoutDelay": 0.5, "TranscoderIdleTimeout": 10,
                           "MaxPeersPerStream": 4},
                "FFmpeg": {"Path": "/app/native", "H264EncoderName": "libx264", "H264Bitrate": 2500000,
                           "H264Preset": "veryfast", "H264MaxWidth": 1920, "H264MaxHeight": 1080,
                           "KeyFrameInterval": 2},
                "Snapshot": {"Enabled": True, "JpegQuality": 85}}}, indent=2) + "\n", 0o644)
    remove_file_managed_settings()


def remove_file_managed_settings():
    path = state_dir() / "MiCameraConfig.json"
    document = json.loads(path.read_text(encoding="utf-8"))
    rtsp = document["MediaServer"]["Rtsp"]
    miloco = document["Miloco"]
    removed = {"Miloco": ("BaseUrl", "Username", "Password"),
               "Rtsp": ("Username", "Password", "CredentialsFilePath", "Streams")}
    if not any(key in miloco for key in removed["Miloco"]) and not any(key in rtsp for key in removed["Rtsp"]):
        return
    # Preserve old files for backup; do not import their credentials into SQLite.
    backup = state_dir() / "MiCameraConfig.before-web-setup.json"
    reject_symlink(backup, backup.name)
    if not backup.exists():
        atomic_write(backup, path.read_text(encoding="utf-8"), 0o600)
    for key in removed["Miloco"]:
        miloco.pop(key, None)
    for key in removed["Rtsp"]:
        rtsp.pop(key, None)
    atomic_write(path, json.dumps(document, indent=2) + "\n", 0o644)


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
            raise DeploymentError("settings.json 的局域网 IP 与当前部署地址不一致。")
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
    validate_deployment_config(lan_ip)


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
            raise DeploymentError(f"缺少运行时密钥：{variable}。")
        try:
            return normalize_secret_content(path.read_bytes(), name)
        except OSError as error:
            raise DeploymentError(f"无法读取运行时密钥：{variable}。") from error
    if has_direct:
        try:
            content = direct.encode("ascii")
        except UnicodeEncodeError as error:
            raise DeploymentError(f"运行时密钥 格式无效：{variable}。") from error
        return normalize_secret_content(content, name)
    return read_secret(name)


def normal_data(payload):
    if not isinstance(payload, dict) or type(payload.get("code")) is not int or payload["code"] != 0 or "data" not in payload:
        raise DeploymentError("Miloco 返回了业务错误或不支持的协议响应；响应内容已隐藏。")
    return payload["data"]


def miloco_opener():
    base_url = os.environ["MILOCO_BASE_URL"]
    parsed = urllib.parse.urlparse(base_url)
    if parsed.scheme != "https" or parsed.hostname != os.environ["LAN_IP"] or parsed.port != 8000:
        raise DeploymentError("部署探测只允许访问本次配置的局域网 Miloco HTTPS 接口。")
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))


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
            raise DeploymentError("认证失败：请检查 Miloco 本地密码或 API 令牌；响应内容已隐藏。") from None
        raise DeploymentError(f"服务请求失败（HTTP {error.code}），请查看脱敏日志。") from None
    except (urllib.error.URLError, TimeoutError, OSError):
        raise DeploymentError("服务暂不可达：请检查启动状态、IP、防火墙和 TLS 配置。") from None
    except (json.JSONDecodeError, UnicodeError):
        raise DeploymentError("服务未返回有效 JSON，可能存在协议不兼容。") from None


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
    elif command == "prepare-web-config":
        remove_file_managed_settings()
    elif command == "miloco-live":
        normal_data(request_json(miloco_opener(), os.environ["MILOCO_BASE_URL"] + "/api/auth/register-status"))
    elif command == "live":
        if api_health("/api/health/live").get("live") is not True:
            raise DeploymentError("后端存活检查失败。")
    elif command == "ready":
        if api_health("/api/health/ready", "--verbose" in arguments).get("ready") is not True:
            raise DeploymentError("后端媒体能力或摄像头视频尚未就绪。")
    elif command == "deployment-ready":
        if api_health("/api/health/live").get("live") is not True:
            raise DeploymentError("后端存活检查失败。")
    elif command == "status":
        api_health("/api/health/ready", True)
    elif command == "ack-license":
        atomic_write(state_dir() / "license-ack", LICENSE_ACKNOWLEDGEMENT)
    elif command in ("credentials", "add-camera"):
        print("请进入前端页面的重新配置入口修改摄像头或 RTSP 凭据；密码不会回显，保存后立即生效，无需重启服务。")
    else:
        raise DeploymentError("未知部署辅助命令。")


if __name__ == "__main__":
    try:
        main(sys.argv[1:])
    except (DeploymentError, ValueError, KeyError, IndexError, OSError) as error:
        print(str(error) if isinstance(error, DeploymentError) else "部署辅助操作失败，请检查参数、文件权限和配置格式。", file=sys.stderr)
        sys.exit(1)
