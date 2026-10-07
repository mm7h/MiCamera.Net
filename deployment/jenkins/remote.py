#!/usr/bin/env python3
"""104-only deployment/reset transactions; never prints application secrets."""
import argparse
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


ROOT = Path("/home/hang/micamera-net")
STATE = ROOT / ".deploy"
CI = STATE / "ci"
SERVICES = ("web", "bridge", "miloco")
RESET_NAMES = ("micamera-state", "miloco", "secrets", "cameras.json", "rtsp-state", "bridge.env", "miloco.env")


def run(*arguments, capture=False, timeout=120):
    return subprocess.run(arguments, check=True, text=True, timeout=timeout,
                          stdout=subprocess.PIPE if capture else None).stdout


def write_json(path, value):
    pending = path.with_name(path.name + ".pending")
    with pending.open("w", encoding="utf-8") as output:
        json.dump(value, output, indent=2)
        output.write("\n")
        output.flush()
        os.fsync(output.fileno())
    pending.replace(path)


def validate_paths(release):
    for path in (ROOT, STATE, CI, CI / "incoming", ROOT / "docker-compose.yml"):
        if path.is_symlink() or not path.exists():
            raise ValueError("The existing deployment paths must exist and must not be symlinks.")
    release = Path(release)
    if release.parent != CI / "incoming" or release.is_symlink() or not release.is_dir():
        raise ValueError("Release must be a direct directory under .deploy/ci/incoming.")
    if release.resolve() != release:
        raise ValueError("Release path must be absolute and canonical.")
    for name in ("deployment.env", "settings.json", "license-ack", ".lock"):
        if (STATE / name).is_symlink() or not (STATE / name).is_file():
            raise ValueError("The deployment is not initialized or contains unsafe paths.")
    addresses = [line.partition("=")[2].strip() for line in
                 (STATE / "deployment.env").read_text(encoding="utf-8-sig").splitlines()
                 if line.partition("=")[0].strip() == "LAN_IP"]
    if addresses != ["192.168.3.104"]:
        raise ValueError("The existing deployment must use LAN_IP=192.168.3.104.")
    return release


def verify_release(release, service):
    names = {"manifest.json", "remote.py"}
    names |= {"helper.py"} if service == "reset" else {"image.tar.gz"}
    if service == "miloco":
        names.add("miloco-entrypoint.py")
    checks = {}
    for line in (release / "SHA256SUMS").read_text(encoding="ascii").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9.-]+)", line)
        if not match or match[2] in checks:
            raise ValueError("Invalid or duplicate checksum entry.")
        checks[match[2]] = match[1]
    if set(checks) != names:
        raise ValueError("The release has missing or unexpected files.")
    for name, expected in checks.items():
        path = release / name
        if path.is_symlink() or not path.is_file():
            raise ValueError("Release artifacts must be regular files.")
        digest = hashlib.sha256()
        with path.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
        if digest.hexdigest() != expected:
            raise ValueError("Release checksum failed; running containers were not changed.")
    manifest = json.loads((release / "manifest.json").read_text(encoding="utf-8"))
    if manifest["service"] != service or manifest["platform"] != "linux/amd64":
        raise ValueError("Release service/platform mismatch.")
    if not re.fullmatch(r"[0-9a-f]{40}", manifest["commit"]) or not re.fullmatch(
            r"[0-9a-f]{12}-[0-9]+-amd64", manifest["version"]):
        raise ValueError("Invalid release identity.")
    if release.name != f"{service}-{manifest['version']}":
        raise ValueError("Release directory does not match its identity.")
    if service != "reset":
        if manifest["image"] != f"micamera-net-{service}:{manifest['version']}" or not re.fullmatch(
                r"sha256:[0-9a-f]{64}", manifest["image_id"]):
            raise ValueError("Invalid image identity.")
    return manifest


def compose(override, *arguments):
    return run("docker", "compose", "--project-name", "micamera-net", "--project-directory", str(ROOT),
               "--env-file", str(STATE / "deployment.env"), "-f", str(ROOT / "docker-compose.yml"),
               "-f", str(override), *arguments, timeout=180)


def inspect(service):
    info = json.loads(run("docker", "inspect", f"micamera-net-{service}-1", capture=True))[0]
    labels = info["Config"]["Labels"]
    if labels.get("com.docker.compose.project") != "micamera-net" or labels.get(
            "com.docker.compose.project.working_dir") != str(ROOT):
        raise ValueError("Container does not belong to the expected deployment.")
    return info


def snapshot():
    services = {}
    for service in SERVICES:
        info = inspect(service)
        image = info["Image"]
        # A stable baseline tag also keeps the previous image available for rollback.
        tag = f"micamera-net-{service}:rollback-{image.split(':')[1][:12]}"
        run("docker", "tag", image, tag)
        services[service] = {"image": tag}
        if service == "miloco":
            mount = next(m for m in info["Mounts"] if m["Destination"] == "/opt/micamera/miloco-entrypoint.py")
            services[service]["volumes"] = [{"type": "bind", "source": mount["Source"],
                "target": mount["Destination"], "read_only": True, "bind": {"create_host_path": False}}]
    return {"services": services}


def wait_for(check, seconds=300):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            if check():
                return
        except (subprocess.SubprocessError, OSError, ValueError, KeyError, urllib.error.URLError):
            pass
        time.sleep(3)
    raise RuntimeError("Health verification timed out; response bodies are not logged.")


def healthy(service):
    state = inspect(service)["State"]
    return state.get("Running") is True and state.get("Health", {}).get("Status") == "healthy"


def api(path):
    token = (STATE / "secrets/rtsp_api_token").read_text(encoding="ascii").strip()
    request = urllib.request.Request("http://192.168.3.104:5080" + path,
                                     headers={"Authorization": "Bearer " + token})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=10) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        if error.code != 503:
            raise RuntimeError(f"API probe failed with HTTP {error.code}.") from None
        return error.code, json.load(error)


def deployment_health(service, configured):
    wait_for(lambda: healthy(service))
    if service == "bridge":
        wait_for(lambda: api("/api/health/live")[1].get("live") is True)
        if configured:
            wait_for(lambda: api("/api/health/ready")[1].get("ready") is True)


def deploy(release, manifest):
    service = manifest["service"]
    configured = api("/api/setup")[1].get("configured") is True
    previous = snapshot()
    rollback = release / "rollback.json"
    candidate = release / "compose.json"
    write_json(rollback, previous)
    run("docker", "load", "-i", str(release / "image.tar.gz"), timeout=900)
    info = json.loads(run("docker", "image", "inspect", manifest["image"], capture=True))[0]
    if (info["Id"], info["Os"], info["Architecture"]) != (manifest["image_id"], "linux", "amd64"):
        raise ValueError("Loaded image ID/platform does not match the manifest.")
    updated = copy.deepcopy(previous)
    updated["services"][service]["image"] = manifest["image"]
    if service == "miloco":
        updated["services"][service]["volumes"][0]["source"] = str(release / "miloco-entrypoint.py")
    write_json(candidate, updated)
    try:
        compose(candidate, "up", "-d", "--no-deps", "--no-build", "--pull", "never", service)
        deployment_health(service, configured)
        write_json(CI / "current.json", updated)
        write_json(release / "result.json", {"status": "success", **manifest})
        print(f"Deployment verified: {service}, {manifest['commit']}, {info['Id']}", flush=True)
    except BaseException:
        print("Deployment failed; restoring the previous service.", flush=True)
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        compose(rollback, "up", "-d", "--no-deps", "--no-build", "--pull", "never", service)
        deployment_health(service, configured)
        raise


def reset_targets(directory):
    targets = set(RESET_NAMES)
    targets.update(p.name for p in directory.glob("MiCameraConfig*"))
    targets = sorted(name for name in targets if (directory / name).exists() or (directory / name).is_symlink())
    for name in targets:
        if (directory / name).is_symlink():
            raise ValueError("Reset refuses symlinked application state.")
    return targets


# Run file moves as root inside the existing bridge image: Miloco/SQLite files may
# be owned by container UIDs. Only the fixed application-state mount is writable.
MOVE_STATE = '''
import json,os,pathlib,sys
root=pathlib.Path('/state'); backup=root/'ci'/'backups'/sys.argv[1]
names=json.loads(sys.argv[2]); restore=sys.argv[3]=='restore'
if backup.is_symlink(): raise ValueError('Unsafe backup path')
if not restore:
    backup.mkdir(mode=0o700)
    (backup/'files.json').write_text(json.dumps(names))
for name in names:
    if '/' in name or name in ('.','..'): raise ValueError('Unsafe reset name')
    source=(backup if restore else root)/name
    target=(root if restore else backup)/name
    if source.is_symlink() or target.is_symlink(): raise ValueError('Unsafe state path')
    if source.exists():
        if restore and target.exists():
            failed=backup/'failed-new-state'; failed.mkdir(mode=0o700,exist_ok=True)
            target.rename(failed/name)
        source.rename(target)
'''


def state_container(image, script, *arguments):
    run("docker", "run", "--rm", "--network", "none", "--user", "0:0", "--entrypoint", "python3",
        "--mount", f"type=bind,source={STATE},target=/state", image, "-c", script, *arguments)


def reset(release, manifest, confirmation):
    if confirmation != "MiCamera.Net":
        raise ValueError("Reset requires explicit CONFIRM_RESET=MiCamera.Net.")
    previous = snapshot()
    rollback = release / "rollback.json"
    write_json(rollback, previous)
    image = previous["services"]["bridge"]["image"]
    targets = reset_targets(STATE)
    backup_name = "reset-" + manifest["version"]
    backups = CI / "backups"
    if backups.is_symlink():
        raise ValueError("Backup directory must not be a symlink.")
    backups.mkdir(mode=0o700, exist_ok=True)
    if (backups / backup_name).exists():
        raise ValueError("A reset backup already exists for this build; it will not be overwritten.")
    backup_started = False
    configured = api("/api/setup")[1].get("configured") is True
    try:
        print("[Reset 1/5] Stopping services and backing up application state on 104.", flush=True)
        compose(rollback, "stop", *SERVICES)
        backup_started = True
        state_container(image, MOVE_STATE, backup_name, json.dumps(targets), "backup")
        print("[Reset 2/5] Generating default configuration and new secrets.", flush=True)
        specification = importlib.util.spec_from_file_location("reset_helper", release / "helper.py")
        helper = importlib.util.module_from_spec(specification)
        specification.loader.exec_module(helper)
        # Initialize only fresh application state. The existing deployment.env
        # may also pin image versions and must remain byte-for-byte unchanged.
        previous_state_dir = os.environ.get("MICAMERA_STATE_DIR")
        try:
            with tempfile.TemporaryDirectory(prefix="reset-init-", dir=CI) as temporary:
                staged = Path(temporary)
                os.environ["MICAMERA_STATE_DIR"] = str(staged)
                helper.initialize("192.168.3.104")
                for name in ("MiCameraConfig.json", "miloco", "secrets"):
                    (staged / name).rename(STATE / name)
        finally:
            if previous_state_dir is None:
                os.environ.pop("MICAMERA_STATE_DIR", None)
            else:
                os.environ["MICAMERA_STATE_DIR"] = previous_state_dir
        state_container(image, "import os; os.mkdir('/state/micamera-state',0o700); os.chown('/state/micamera-state',10001,10001)")
        print("[Reset 3/5] Starting Miloco/frontend and waiting for the new certificate.", flush=True)
        compose(rollback, "up", "-d", "--no-deps", "--no-build", "--pull", "never", "--force-recreate", "miloco", "web")
        wait_for(lambda: healthy("miloco") and (STATE / "miloco/cert/cert.pem").is_file())
        print("[Reset 4/5] Starting the backend with the new credentials.", flush=True)
        compose(rollback, "up", "-d", "--no-deps", "--no-build", "--pull", "never", "--force-recreate", "bridge")
        for service in SERVICES:
            deployment_health(service, False)
        print("[Reset 5/5] Verifying the unconfigured first-deployment state.", flush=True)
        setup = api("/api/setup")[1]
        settings = api("/api/settings")[1]
        status, ready = api("/api/health/ready")
        if setup.get("configured") is not False or setup.get("listening") is not False or status != 503 or ready.get("ready") is not False:
            raise RuntimeError("Reset did not produce the expected first-deployment state.")
        if settings.get("hasMilocoPin") is not False or settings.get("hasRtspPassword") is not False or settings.get("streams") != []:
            raise RuntimeError("Reset left application credentials or camera configuration active.")
        write_json(CI / "current.json", previous)
        write_json(release / "result.json", {"status": "success", "backup": backup_name, **manifest})
        print("Reset verified. Complete Miloco authorization and frontend setup again.", flush=True)
    except BaseException:
        print("Reset failed; restoring the previous application state.", flush=True)
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        compose(rollback, "stop", *SERVICES)
        if backup_started:
            state_container(image, MOVE_STATE, backup_name, json.dumps(targets), "restore")
        compose(rollback, "up", "-d", "--no-deps", "--no-build", "--pull", "never", "--force-recreate", "miloco", "web")
        wait_for(lambda: healthy("miloco"))
        compose(rollback, "up", "-d", "--no-deps", "--no-build", "--pull", "never", "--force-recreate", "bridge")
        for service in SERVICES:
            deployment_health(service, configured)
        raise


def main():
    import fcntl
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("service", choices=(*SERVICES, "reset"))
    parser.add_argument("--release", required=True)
    parser.add_argument("--confirm", default="")
    arguments = parser.parse_args()
    if arguments.service == "reset" and arguments.confirm != "MiCamera.Net":
        raise ValueError("Reset confirmation is missing.")
    if run("uname", "-m", capture=True).strip() != "x86_64":
        raise ValueError("This deployment requires Linux amd64.")
    os.umask(0o077)
    release = validate_paths(arguments.release)
    manifest = verify_release(release, arguments.service)
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(InterruptedError("Deployment interrupted.")))
    with (STATE / ".lock").open("r+") as lock:
        def acquire():
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return True
            except BlockingIOError:
                return False
        wait_for(acquire, 600)
        if arguments.service == "reset":
            reset(release, manifest, arguments.confirm)
        else:
            deploy(release, manifest)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # External tools/HTTP exceptions may include private data; don't echo them.
        detail = str(error) if isinstance(error, (ValueError, RuntimeError)) else type(error).__name__
        print("Operation failed: " + detail + ". Application response bodies and secrets were not logged.", file=sys.stderr)
        sys.exit(1)
