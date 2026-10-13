"""Release workflow safeguards. Requires only Python's standard library, Git and gh."""
import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen


PACKAGE_FILES = {
    "README.md", "LICENSE", "deploy.sh", "docker-compose.yml", "images.tar",
    "SHA256SUMS", "deployment/package.env", "deployment/miloco-entrypoint.py",
    "deployment/THIRD-PARTY-NOTICES.md",
}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def capture(*arguments):
    return subprocess.check_output(arguments, text=True).strip()


def api(path, missing_ok=False):
    url = os.environ.get("GITHUB_API_URL", "https://api.github.com") + path
    request = Request(url, headers={
        "Authorization": "Bearer " + os.environ["GH_TOKEN"],
        "Accept": "application/vnd.github+json", "User-Agent": "MiCamera.Net-release",
        "X-GitHub-Api-Version": "2022-11-28",
    })
    try:
        with urlopen(request, timeout=60) as response:
            return json.load(response)
    except HTTPError as error:
        error.close()
        if missing_ok and error.code == 404:
            return None
        raise RuntimeError(f"GitHub API returned HTTP {error.code} for {path}") from error


def release(tag):
    repo = os.environ["GITHUB_REPOSITORY"]
    document = api(f"/repos/{repo}/releases/tags/{quote(tag, safe='')}", missing_ok=True)
    if document is not None:
        return document
    # The by-tag endpoint only finds published releases. Authenticated release
    # listings also include drafts, whose pending tag must be matched explicitly.
    page = 1
    while True:
        batch = api(f"/repos/{repo}/releases?per_page=100&page={page}")
        for document in batch:
            if document["tag_name"] == tag:
                return document
        if len(batch) < 100:
            return None
        page += 1


def require_draft(document):
    require(document is None or document["draft"],
            "Release is already published. Use a new tag; published assets will not be modified.")


def resolve_tag(tag):
    ref = "refs/tags/" + tag
    subprocess.run(["git", "check-ref-format", ref], check=True)
    # An explicit tag ref cannot accidentally resolve to a branch of the same name.
    subprocess.run(["git", "fetch", "--no-tags", "--force", "origin", ref], check=True)
    sha = capture("git", "rev-parse", "FETCH_HEAD^{commit}")
    require(re.fullmatch(r"[0-9a-f]{40}", sha), "Tag did not resolve to a commit SHA.")
    return sha


def version_for(sha):
    timestamp = int(capture("git", "show", "-s", "--format=%ct", sha))
    date = datetime.fromtimestamp(timestamp, timezone(timedelta(hours=8))).strftime("%Y%m%d")
    return f"{date}-{sha[:7]}"


def prepare(tag):
    sha = resolve_tag(tag)
    require_draft(release(tag))
    version = version_for(sha)
    with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
        output.write(f"sha={sha}\nversion={version}\n")
    print(f"Resolved {tag!r} to {sha}; package version {version}")


def digest(stream):
    checksum = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        checksum.update(block)
    return checksum.hexdigest()


def checksums(text):
    result = {}
    for line in text.splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
        require(match is not None, "Malformed SHA256 entry.")
        checksum, name = match.groups()
        require(name not in result, f"Duplicate checksum entry: {name}")
        result[name] = checksum
    return result


def filenames(version, arches):
    require(re.fullmatch(r"[0-9]{8}-[0-9a-f]{7}", version), "Invalid package version.")
    return [f"micamera-net-{version}-linux-{arch}.tar.gz{suffix}"
            for arch in arches for suffix in ("", ".sha256")]


def verify_archive(path, version, arch):
    with path.open("rb") as source:
        checksum = digest(source)
    outer = checksums(path.with_suffix(path.suffix + ".sha256").read_text(encoding="ascii"))
    require(outer == {path.name: checksum}, f"Archive SHA256 mismatch: {path.name}")
    root = path.name.removesuffix(".tar.gz")
    contents = {}
    hashes = {}
    seen = set()
    with tarfile.open(path, "r:gz") as archive:
        for member in archive:
            require(member.name not in seen, f"Duplicate archive member: {member.name}")
            seen.add(member.name)
            if member.isdir():
                require(member.name in (root, root + "/deployment"), "Unexpected archive directory.")
                continue
            require(member.name.startswith(root + "/") and member.isfile(),
                    "Invalid archive member or link.")
            relative = member.name[len(root) + 1:]
            require(relative in PACKAGE_FILES, f"Unexpected package file: {relative}")
            if relative == "deploy.sh":
                require(member.mode & 0o111 == 0o111, "deploy.sh must be executable.")
            with archive.extractfile(member) as source:
                hashes[relative] = digest(source)
            if relative in ("SHA256SUMS", "deployment/package.env", "docker-compose.yml", "deploy.sh"):
                with archive.extractfile(member) as source:
                    contents[relative] = source.read().decode("utf-8")
    require(set(hashes) == PACKAGE_FILES, "Package is missing required files.")
    inner = checksums(contents["SHA256SUMS"])
    require(inner == {name: value for name, value in hashes.items() if name != "SHA256SUMS"},
            "Internal SHA256SUMS mismatch or incomplete coverage.")
    require("\r" not in contents["deploy.sh"], "deploy.sh must use LF line endings.")
    require(not re.search(r"^\s*build:", contents["docker-compose.yml"], re.MULTILINE),
            "Packaged Compose must not contain source build definitions.")
    env = {}
    for line in contents["deployment/package.env"].splitlines():
        key, separator, value = line.partition("=")
        require(separator and key not in env, "Invalid or duplicate package.env entry.")
        env[key] = value
    expected = {
        "PACKAGE_VERSION": version, "PACKAGE_ARCH": arch,
        "MICAMERA_BRIDGE_IMAGE": f"micamera-net-bridge:{version}-{arch}",
        "MICAMERA_WEB_IMAGE": f"micamera-net-web:{version}-{arch}",
    }
    require(set(env) == set(expected) | {"BRIDGE_ID", "WEB_ID"}, "Unexpected package metadata.")
    require(all(env[key] == value for key, value in expected.items()), "Package version/architecture mismatch.")
    require(all(re.fullmatch(r"sha256:[0-9a-f]{64}", env[key]) for key in ("BRIDGE_ID", "WEB_ID")),
            "Invalid image ID in package.env.")
    return env, checksum


def verify_images(env, arch):
    for name in ("BRIDGE", "WEB"):
        image = env[f"MICAMERA_{name}_IMAGE"]
        info = json.loads(capture("docker", "image", "inspect", image))[0]
        require(info["Os"] == "linux" and info["Architecture"] == arch
                and info["Id"] == env[f"{name}_ID"], f"Image metadata mismatch: {image}")
        if name == "BRIDGE":
            check = """
test -x /app/MiCamera.Net.Sample.Server
test -s /app/MiCamera.Net.Sample.Server.dll
test -s /app/licenses/MiCamera.Net-LICENSE
test -s /app/licenses/SIPSorcery-LICENSE.md
test -s /app/licenses/THIRD-PARTY-NOTICES.md
test ! -e /app/Configs/MiCameraConfig.json
for library in libavcodec.so.61 libavformat.so.61 libavutil.so.59 libswscale.so.8; do
    test -r "/app/native/$library"
done
test -n "$(find /app -name libe_sqlite3.so -print -quit)"
ffmpeg -hide_banner -encoders | grep -q libx264
ffmpeg -hide_banner -encoders | grep -q libx265
"""
        else:
            check = """
test -s /usr/share/nginx/html/index.html
test -d /usr/share/nginx/html/assets
test -s /etc/nginx/templates/default.conf.template
test -x /docker-entrypoint.d/16-micamera-api-proxy.envsh
nginx -v
"""
        subprocess.run(["docker", "run", "--rm", "--network", "none", "--entrypoint", "sh",
                        image, "-ec", check], check=True)


def verify(directory, version, arches, images=False):
    names = filenames(version, arches)
    require({path.name for path in directory.iterdir()} == set(names),
            "Artifact directory must contain exactly the expected archives and SHA256 files.")
    for arch in arches:
        path = directory / f"micamera-net-{version}-linux-{arch}.tar.gz"
        env, checksum = verify_archive(path, version, arch)
        if images:
            verify_images(env, arch)
        print(f"Verified {path.name}: sha256:{checksum}")
    return [directory / name for name in names]


def release_assets(document):
    repo = os.environ["GITHUB_REPOSITORY"]
    assets = []
    page = 1
    while True:
        batch = api(f"/repos/{repo}/releases/{document['id']}/assets?per_page=100&page={page}")
        assets.extend(batch)
        if len(batch) < 100:
            return assets
        page += 1


def publish(directory, tag, sha, version, prerelease):
    files = verify(directory, version, ("amd64", "arm64"))
    require(resolve_tag(tag) == sha and version_for(sha) == version,
            "Release tag moved or package version changed. Start a new workflow run.")
    document = release(tag)
    require_draft(document)
    expected = {path.name: path.stat().st_size for path in files}
    if document is not None:
        existing = release_assets(document)
        require(all(asset["name"] in expected for asset in existing),
                "Draft contains unrelated assets. Remove them manually before rerunning.")
    else:
        command = ["gh", "release", "create", "--repo", os.environ["GITHUB_REPOSITORY"],
                   "--draft", "--verify-tag", "--generate-notes", "--title", tag]
        if prerelease:
            command.append("--prerelease")
        subprocess.run([*command, "--", tag], check=True)
    # Recheck immediately before overwriting any draft asset.
    require(resolve_tag(tag) == sha, "Release tag moved before upload.")
    document = release(tag)
    require(document is not None, "Release draft disappeared before upload.")
    require_draft(document)
    subprocess.run(["gh", "release", "upload", "--repo", os.environ["GITHUB_REPOSITORY"],
                    "--clobber", "--", tag, *(str(path) for path in files)], check=True)
    document = release(tag)
    require(document is not None, "Release draft disappeared after upload.")
    require_draft(document)
    assets = release_assets(document)
    require(len(assets) == len(expected)
            and {asset["name"]: asset["size"] for asset in assets} == expected
            and all(asset["state"] == "uploaded" for asset in assets),
            "Release asset verification failed. Keep the draft unpublished and rerun.")
    require(resolve_tag(tag) == sha, "Release tag moved during upload; do not publish this draft.")
    with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a", encoding="utf-8") as summary:
        summary.write(f"## Release packages ready\n\nTag: `{tag}`\n\nCommit: `{sha}`\n\n"
                      f"Package version: `{version}`\n\n[Review draft]({document['html_url']})\n\n"
                      "| Asset | Bytes | SHA256 |\n| --- | ---: | --- |\n")
        for path in files:
            with path.open("rb") as source:
                checksum = digest(source)
            summary.write(f"| `{path.name}` | {path.stat().st_size} | `{checksum}` |\n")
        summary.write("\nReview the release notes and all four assets, then publish the draft manually.\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("prepare", "verify", "publish"))
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--arch", choices=("amd64", "arm64"))
    parser.add_argument("--images", action="store_true")
    arguments = parser.parse_args()
    if arguments.command == "prepare":
        prepare(os.environ["RELEASE_TAG"])
    else:
        require(arguments.directory is not None, "--directory is required.")
        version = os.environ["PACKAGE_VERSION"]
        if arguments.command == "verify":
            require(arguments.arch is not None, "--arch is required.")
            verify(arguments.directory, version, (arguments.arch,), arguments.images)
        else:
            publish(arguments.directory, os.environ["RELEASE_TAG"], os.environ["RELEASE_SHA"],
                    version, os.environ.get("RELEASE_PRERELEASE") == "true")


if __name__ == "__main__":
    main()
