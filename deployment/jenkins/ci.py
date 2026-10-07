#!/usr/bin/env python3
"""Jenkins build/transfer steps. Credentials are supplied through the environment."""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import tarfile


ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "deployment" / "build_output" / "jenkins"
HOST = "192.168.3.104"
REMOTE_ROOT = "/home/hang/micamera-net"
SERVICES = ("web", "bridge", "miloco", "reset")


def run(*arguments, capture=False):
    return subprocess.run(arguments, cwd=ROOT, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def build_identity(service):
    commit = run("git", "rev-parse", "HEAD", capture=True).strip()
    number = os.environ.get("BUILD_NUMBER", "")
    if not re.fullmatch(r"[0-9a-f]{40}", commit) or not re.fullmatch(r"[0-9]+", number):
        raise ValueError("A Git checkout and numeric Jenkins BUILD_NUMBER are required.")
    version = f"{commit[:12]}-{number}-amd64"
    return commit, version, f"micamera-net-{service}:{version}"


def upstream_image():
    compose = (ROOT / "docker-compose.yml").read_text(encoding="utf-8")
    match = re.search(r"^  miloco:\s*\n    image: (ghcr\.io/miiot/miloco:[^\s]+@sha256:[0-9a-f]{64})$",
                      compose, re.MULTILINE)
    if not match:
        raise ValueError("The Miloco image must specify its version and SHA256 digest.")
    return match[1]


def image_info(image):
    info = json.loads(run("docker", "image", "inspect", image, capture=True))[0]
    if info["Architecture"] != "amd64" or info["Os"] != "linux":
        raise ValueError("Only Linux amd64 images can be deployed to 104.")
    return info


def build(service, stage):
    if service == "miloco":
        if stage != "image":
            print("Skipped: Miloco uses the pinned upstream image.")
            return
        upstream = upstream_image()
        # Warm the exact pinned blobs through the faster LAN-accessible mirror.
        mirror = upstream.replace("ghcr.io/", "ghcr.nju.edu.cn/", 1)
        try:
            run("docker", "pull", "--platform", "linux/amd64", mirror)
        except subprocess.CalledProcessError:
            print("Miloco mirror unavailable; falling back to the pinned GHCR image.")
        run("docker", "pull", "--platform", "linux/amd64", upstream)
        image_info(upstream)
        run("docker", "tag", upstream, build_identity(service)[2])
        return
    arguments = ["docker", "buildx", "build", "--platform", "linux/amd64",
                 "-f", f"deployment/Dockerfile.{service}"]
    if stage == "image":
        arguments += ["--load", "-t", build_identity(service)[2]]
    else:
        arguments += ["--target", stage, "--output", "type=cacheonly"]
    run(*arguments, ".")


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def archive_image_id(path, image):
    # Docker 29/containerd reports the manifest/index ID; Docker 28/classic reports
    # the config ID. docker save includes the canonical config used by the target.
    with tarfile.open(path, "r:gz") as archive:
        entries = json.load(archive.extractfile("manifest.json"))
        selected = [entry for entry in entries if image in (entry.get("RepoTags") or [])]
        if len(selected) != 1:
            raise ValueError("The image archive must contain exactly one tagged target image.")
        config = archive.extractfile(selected[0]["Config"]).read()
        platform = json.loads(config)
        if platform.get("architecture") != "amd64" or platform.get("os") != "linux":
            raise ValueError("The archived image must be Linux amd64.")
        return "sha256:" + hashlib.sha256(config).hexdigest()


def export(service):
    commit, version, image = build_identity(service)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    files = ["remote.py"]
    shutil.copyfile(ROOT / "deployment/jenkins/remote.py", OUTPUT / "remote.py")
    manifest = {"service": service, "commit": commit, "version": version, "platform": "linux/amd64"}
    if service == "reset":
        files.append("helper.py")
        shutil.copyfile(ROOT / "deployment/helper.py", OUTPUT / "helper.py")
    else:
        manifest.update(image=image, source_image_id=image_info(image)["Id"])
        if service == "miloco":
            manifest["upstream"] = upstream_image()
            files.append("miloco-entrypoint.py")
            shutil.copyfile(ROOT / "deployment/miloco-entrypoint.py", OUTPUT / "miloco-entrypoint.py")
        archive = OUTPUT / "image.tar.gz"
        # Stream docker save to gzip without retaining a second, uncompressed archive.
        with (OUTPUT / "image.tar.gz.pending").open("wb") as destination:
            process = subprocess.Popen(["docker", "save", image], stdout=subprocess.PIPE)
            try:
                with gzip.GzipFile(fileobj=destination, mode="wb", compresslevel=3, mtime=0) as zipped:
                    shutil.copyfileobj(process.stdout, zipped)
                if process.wait() != 0:
                    raise RuntimeError("docker save failed.")
            finally:
                process.stdout.close()
                if process.poll() is None:
                    process.kill()
                    process.wait()
        (OUTPUT / "image.tar.gz.pending").replace(archive)
        manifest["image_id"] = archive_image_id(archive, image)
        files.append("image.tar.gz")
    (OUTPUT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    files.append("manifest.json")
    (OUTPUT / "SHA256SUMS").write_text(
        "".join(f"{sha256(OUTPUT / name)}  {name}\n" for name in files), encoding="ascii")


def ssh_options():
    key = os.environ["DEPLOY_KEY"]
    known_hosts = os.environ["DEPLOY_KNOWN_HOSTS"]
    if os.environ["DEPLOY_USER"] != "hang":
        raise ValueError("The deployment credential must use the hang account.")
    return ["-i", key, "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=yes", "-o", f"UserKnownHostsFile={known_hosts}",
            "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3"]


def transfer(service, execute=False):
    _, version, _ = build_identity(service)
    release = f"{REMOTE_ROOT}/.deploy/ci/incoming/{service}-{version}"
    destination = f"hang@{HOST}"
    options = ssh_options()
    if execute:
        # The remote process has its own lifetime limit even if Jenkins disconnects.
        command = ["timeout", "--signal=TERM", "--kill-after=600s", "40m", "python3",
                   release + "/remote.py", service, "--release", release]
        if service == "reset":
            command += ["--confirm", os.environ.get("CONFIRM_RESET", "")]
        run("ssh", *options, destination, shlex.join(command))
        return
    prepare = (f"set -eu; test ! -L {REMOTE_ROOT}; test ! -L {REMOTE_ROOT}/.deploy; "
               f"test -d {REMOTE_ROOT}/.deploy; "
               f"test ! -L {REMOTE_ROOT}/.deploy/ci; umask 077; "
               f"mkdir -p {REMOTE_ROOT}/.deploy/ci/incoming; "
               f"test ! -L {REMOTE_ROOT}/.deploy/ci/incoming; mkdir {shlex.quote(release)}")
    run("ssh", *options, destination, prepare)
    manifest = json.loads((OUTPUT / "manifest.json").read_text(encoding="utf-8"))
    if manifest["service"] != service or manifest["version"] != version:
        raise ValueError("The deployment package does not match this checkout/build.")
    names = [line.split("  ", 1)[1] for line in (OUTPUT / "SHA256SUMS").read_text().splitlines()]
    run("scp", *options, *(str(OUTPUT / name) for name in names), str(OUTPUT / "SHA256SUMS"),
        destination + ":" + release + "/")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("service", choices=SERVICES)
    parser.add_argument("step", choices=("dependencies", "build", "image", "export", "upload", "run"))
    arguments = parser.parse_args()
    if arguments.service == "reset" and os.environ.get("CONFIRM_RESET") != "MiCamera.Net":
        raise ValueError("Reset requires CONFIRM_RESET=MiCamera.Net.")
    if arguments.step == "export":
        export(arguments.service)
    elif arguments.step in ("upload", "run"):
        transfer(arguments.service, arguments.step == "run")
    elif arguments.service == "reset":
        raise ValueError("Reset does not build images.")
    else:
        build(arguments.service, arguments.step)


if __name__ == "__main__":
    main()
