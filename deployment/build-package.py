#!/usr/bin/env python3
"""Build Linux experience packages. Builder requires Python 3 and Docker buildx."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile


ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "deployment" / "build_output"
FILES = ("deploy.sh", "deployment/miloco-entrypoint.py", "deployment/THIRD-PARTY-NOTICES.md", "LICENSE")


def run(*arguments):
    subprocess.run(arguments, cwd=ROOT, check=True)


def inspect(image):
    return json.loads(subprocess.check_output(["docker", "image", "inspect", image], text=True))[0]


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def package(version, arch, reuse, build_args):
    bridge = f"micamera-net-bridge:{version}-{arch}"
    web = f"micamera-net-web:{version}-{arch}"
    for name, image in (("bridge", bridge), ("web", web)):
        if not reuse:
            extra = [item for value in build_args for item in ("--build-arg", value)]
            run("docker", "buildx", "build", "--platform", f"linux/{arch}", "--load", "-t", image,
                "-f", f"deployment/Dockerfile.{name}", *extra, ".")
        if inspect(image)["Architecture"] != arch:
            raise RuntimeError(f"Wrong architecture: {image}")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    name = f"micamera-net-{version}-linux-{arch}"
    with tempfile.TemporaryDirectory(prefix=".package-", dir=OUTPUT) as temporary:
        directory = Path(temporary) / name
        directory.mkdir()
        for relative in FILES:
            target = directory / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, target)
        (directory / "deploy.sh").chmod(0o755)
        compose = (ROOT / "docker-compose.yml").read_text(encoding="utf-8")
        compose, count = re.subn(r"    build:\n      context: \.\n      dockerfile: deployment/Dockerfile\.(?:bridge|web)\n", "", compose)
        if count != 2:
            raise RuntimeError("Expected exactly two source build definitions.")
        (directory / "docker-compose.yml").write_text(compose, encoding="utf-8", newline="\n")
        (directory / "deployment/package.env").write_text(
            f"PACKAGE_VERSION={version}\nPACKAGE_ARCH={arch}\nMICAMERA_BRIDGE_IMAGE={bridge}\n"
            f"MICAMERA_WEB_IMAGE={web}\nBRIDGE_ID={inspect(bridge)['Id']}\nWEB_ID={inspect(web)['Id']}\n",
            encoding="ascii", newline="\n")
        (directory / "README.md").write_text(
            f"# MiCamera.Net {version} Linux {arch} 体验包\n\n"
            "需要与摄像头互通的原生 Linux、Docker Engine、Compose 2.20+、Bash 4+、iproute2、awk、coreutils 和 util-linux。\n"
            "无需在主机安装 Python、Node.js 或 .NET；不支持 Docker Desktop、rootless 或远程 Docker。\n\n"
            "1. 阅读 deployment/THIRD-PARTY-NOTICES.md。\n"
            "2. 在解压目录运行 `bash deploy.sh`，校验并导入包内前后端镜像。\n"
            "3. 按提示打开 Miloco 页面，绑定小米账号并选择摄像头。Miloco 镜像首次在线拉取，授权也需要联网。\n"
            "4. 首次访问 `http://服务器IP:5081`，填写 RTSP 用户名、密码和确认密码。设置前 RTSP 端口关闭。\n"
            "5. 使用 RTSP over TCP，在播放器认证界面输入自行设置的凭据。\n\n"
            "升级旧部署也须首次网页重设；之后更新和重启保留凭据。首个访问者可以初始化，适用于可信局域网。\n"
            "用 `bash deploy.sh status` 查看媒体和初始化状态；`logs` 查看日志；`stop` 停止且保留数据；"
            "`credentials` 仅在交互终端显示凭据。\n"
            "运行状态保存在 `.deploy/`，备份时包含该目录。前端 HTTP 5081、API 5080、Miloco HTTPS 8000、"
            "RTSP TCP 8554、WebRTC UDP 50000–50100。仅向可信局域网开放。\n"
            "完成部署后会清理本项目不再被容器引用的旧镜像，保留其他项目镜像和授权数据。\n",
            encoding="utf-8", newline="\n")
        run("docker", "save", "-o", str(directory / "images.tar"), bridge, web)
        checks = "".join(f"{digest(path)}  {path.relative_to(directory).as_posix()}\n"
                         for path in sorted(directory.rglob("*")) if path.is_file())
        (directory / "SHA256SUMS").write_text(checks, encoding="ascii", newline="\n")
        artifact = OUTPUT / f"{name}.tar.gz"
        pending = OUTPUT / f".{name}.tar.gz.pending"
        try:
            with tarfile.open(pending, "w:gz", compresslevel=3) as archive:
                archive.add(directory, arcname=name)
            pending.replace(artifact)
        finally:
            pending.unlink(missing_ok=True)
        (OUTPUT / f"{name}.tar.gz.sha256").write_text(f"{digest(artifact)}  {artifact.name}\n", encoding="ascii")
        print(artifact, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("version")
    parser.add_argument("--arch", choices=("amd64", "arm64", "all"), default="all")
    parser.add_argument("--reuse-images", action="store_true", help="Package already built versioned images.")
    parser.add_argument("--build-arg", action="append", default=[], help="Explicit Docker build argument, NAME=VALUE.")
    arguments = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,80}", arguments.version):
        parser.error("Invalid version identifier.")
    for arch in (("amd64", "arm64") if arguments.arch == "all" else (arguments.arch,)):
        package(arguments.version, arch, arguments.reuse_images, arguments.build_arg)


if __name__ == "__main__":
    main()
