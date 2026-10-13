"""Offline regression checks for archive integrity and release write safeguards."""
from contextlib import ExitStack
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

import release_packages as release


VERSION = "20261013-abcdef0"
SHA = "abcdef0" + "1" * 33


def fixture(directory, arch="amd64", change=None, duplicate=False):
    root = f"micamera-net-{VERSION}-linux-{arch}"
    files = {name: b"fixture\n" for name in release.PACKAGE_FILES}
    files["deploy.sh"] = b"#!/bin/bash\nexit 0\n"
    files["docker-compose.yml"] = b"services:\n  bridge:\n    image: fixture\n"
    files["deployment/package.env"] = (
        f"PACKAGE_VERSION={VERSION}\nPACKAGE_ARCH={arch}\n"
        f"MICAMERA_BRIDGE_IMAGE=micamera-net-bridge:{VERSION}-{arch}\n"
        f"MICAMERA_WEB_IMAGE=micamera-net-web:{VERSION}-{arch}\n"
        f"BRIDGE_ID=sha256:{'a' * 64}\nWEB_ID=sha256:{'b' * 64}\n"
    ).encode()
    if change:
        change(files)
    files["SHA256SUMS"] = "".join(
        f"{hashlib.sha256(data).hexdigest()}  {name}\n"
        for name, data in sorted(files.items()) if name != "SHA256SUMS"
    ).encode()
    archive = directory / (root + ".tar.gz")
    with tarfile.open(archive, "w:gz") as output:
        for name, data in files.items():
            member = tarfile.TarInfo(root + "/" + name)
            member.mode = 0o755 if name == "deploy.sh" else 0o644
            member.size = len(data)
            output.addfile(member, io.BytesIO(data))
        if duplicate:
            member = tarfile.TarInfo(root + "/LICENSE")
            output.addfile(member, io.BytesIO())
    checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".gz.sha256").write_text(f"{checksum}  {archive.name}\n", encoding="ascii")
    return archive


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.print_patch = patch.object(release, "print", create=True)
        self.print_patch.start()
        self.addCleanup(self.print_patch.stop)

    def test_both_architectures_and_exact_file_set(self):
        for arch in ("amd64", "arm64"):
            fixture(self.directory, arch)
        self.assertEqual(len(release.verify(self.directory, VERSION, ("amd64", "arm64"))), 4)
        (self.directory / "extra.txt").write_text("unexpected")
        with self.assertRaisesRegex(RuntimeError, "exactly"):
            release.verify(self.directory, VERSION, ("amd64", "arm64"))

    def test_corrupt_archive_and_wrong_checksum_filename(self):
        path = fixture(self.directory)
        checksum = path.with_suffix(".gz.sha256")
        checksum.write_text(f"{'a' * 64}  {path.name}\n")
        with self.assertRaisesRegex(RuntimeError, "SHA256 mismatch"):
            release.verify_archive(path, VERSION, "amd64")
        checksum.write_text(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  wrong.tar.gz\n")
        with self.assertRaisesRegex(RuntimeError, "SHA256 mismatch"):
            release.verify_archive(path, VERSION, "amd64")

    def test_disallowed_payloads_even_with_valid_checksums(self):
        changes = {
            "user state": lambda files: files.update({".deploy/settings.db": b"state"}),
            "path traversal": lambda files: files.update({"../escape": b"outside"}),
            "missing notices": lambda files: files.pop("deployment/THIRD-PARTY-NOTICES.md"),
            "source build": lambda files: files.update({"docker-compose.yml": b"services:\n  bridge:\n    build: .\n"}),
            "CRLF script": lambda files: files.update({"deploy.sh": b"#!/bin/bash\r\n"}),
            "wrong metadata": lambda files: files.update({"deployment/package.env": files["deployment/package.env"].replace(b"PACKAGE_ARCH=amd64", b"PACKAGE_ARCH=arm64")}),
        }
        for name, change in changes.items():
            with self.subTest(name=name):
                path = fixture(self.directory, change=change)
                with self.assertRaises(RuntimeError):
                    release.verify_archive(path, VERSION, "amd64")

    def test_duplicate_archive_member(self):
        path = fixture(self.directory, duplicate=True)
        with self.assertRaisesRegex(RuntimeError, "Duplicate archive"):
            release.verify_archive(path, VERSION, "amd64")

    def test_inner_checksum_and_script_mode(self):
        path = fixture(self.directory)
        with tarfile.open(path) as source:
            members = [(member, source.extractfile(member).read()) for member in source]
        for mode_failure in (False, True):
            with tarfile.open(path, "w:gz") as output:
                for member, data in members:
                    if mode_failure and member.name.endswith("/deploy.sh"):
                        member.mode = 0o644
                    elif not mode_failure and member.name.endswith("/LICENSE"):
                        data = b"tampered\n"
                    member.size = len(data)
                    output.addfile(member, io.BytesIO(data))
            path.with_suffix(".gz.sha256").write_text(
                f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n")
            with self.assertRaisesRegex(RuntimeError, "executable" if mode_failure else "Internal SHA256"):
                release.verify_archive(path, VERSION, "amd64")

    def test_reject_symlinks(self):
        path = fixture(self.directory)
        with tarfile.open(path) as source:
            members = [(member, source.extractfile(member).read()) for member in source]
        with tarfile.open(path, "w:gz") as output:
            for member, data in members:
                if member.name.endswith("/LICENSE"):
                    member.type = tarfile.SYMTYPE
                    member.linkname = "../../outside"
                    member.size = 0
                output.addfile(member, io.BytesIO(data))
        path.with_suffix(".gz.sha256").write_text(
            f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n")
        with self.assertRaisesRegex(RuntimeError, "member or link"):
            release.verify_archive(path, VERSION, "amd64")

    def test_image_architecture_and_id_mismatch_stop_container_checks(self):
        path = fixture(self.directory)
        env, _ = release.verify_archive(path, VERSION, "amd64")
        for info in ({"Os": "linux", "Architecture": "arm64", "Id": env["BRIDGE_ID"]},
                     {"Os": "linux", "Architecture": "amd64", "Id": "sha256:" + "c" * 64}):
            with patch.object(release, "capture", return_value=json.dumps([info])), \
                    patch.object(release.subprocess, "run") as run:
                with self.assertRaisesRegex(RuntimeError, "Image metadata mismatch"):
                    release.verify_images(env, "amd64")
                run.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "Real packager executable modes require a Linux filesystem")
    def test_existing_packager_output_is_accepted(self):
        root = Path(__file__).resolve().parents[2]
        spec = importlib.util.spec_from_file_location("build_package", root / "deployment/build-package.py")
        builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(builder)

        def inspect(image):
            return {"Architecture": "amd64", "Id": "sha256:" + ("a" if "bridge" in image else "b") * 64}

        def save(*arguments):
            self.assertEqual(arguments[:3], ("docker", "save", "-o"))
            Path(arguments[3]).write_bytes(b"offline docker save fixture")

        with patch.object(builder, "OUTPUT", self.directory), patch.object(builder, "inspect", inspect), \
                patch.object(builder, "run", save), patch.object(builder, "print", create=True):
            builder.package(VERSION, "amd64", reuse=True, build_args=[])
        release.verify(self.directory, VERSION, ("amd64",))


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.directory = self.root / "assets"
        self.directory.mkdir()
        for arch in ("amd64", "arm64"):
            fixture(self.directory, arch)
        self.document = {"id": 123, "draft": True, "name": "Custom title", "body": "Custom notes",
                         "html_url": "https://github.com/example/repo/releases/tag/0.2.0"}
        self.assets = [{"name": path.name, "size": path.stat().st_size, "state": "uploaded"}
                       for path in self.directory.iterdir()]
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        self.stack.enter_context(patch.object(release, "print", create=True))
        self.stack.enter_context(patch.dict(os.environ, {
            "GITHUB_REPOSITORY": "example/repo", "GITHUB_STEP_SUMMARY": str(self.root / "summary.md"),
            "GH_TOKEN": "offline-fixture-token",
        }))
        self.resolve = self.stack.enter_context(patch.object(release, "resolve_tag", return_value=SHA))
        self.stack.enter_context(patch.object(release, "version_for", return_value=VERSION))
        self.lookup = self.stack.enter_context(patch.object(release, "release", return_value=self.document))
        self.list_assets = self.stack.enter_context(patch.object(release, "release_assets", return_value=self.assets))
        self.run = self.stack.enter_context(patch.object(release.subprocess, "run"))

    def publish(self, prerelease=False):
        release.publish(self.directory, "0.2.0", SHA, VERSION, prerelease)

    def test_create_draft_and_never_publish(self):
        self.lookup.side_effect = [None, self.document, self.document]
        self.publish(prerelease=True)
        create, upload = [call.args[0] for call in self.run.call_args_list]
        self.assertIn("--draft", create)
        self.assertIn("--verify-tag", create)
        self.assertIn("--generate-notes", create)
        self.assertIn("--prerelease", create)
        self.assertEqual(upload[:3], ["gh", "release", "upload"])
        self.assertIn("--clobber", upload)
        self.assertTrue((self.root / "summary.md").is_file())

    def test_rerun_only_uploads_and_preserves_release_metadata(self):
        original = dict(self.document)
        self.publish()
        self.assertEqual(self.run.call_count, 1)
        self.assertEqual(self.document, original)

    def test_reject_published_release_without_write(self):
        self.document["draft"] = False
        with self.assertRaisesRegex(RuntimeError, "already published"):
            self.publish()
        self.run.assert_not_called()

    def test_release_published_between_checks(self):
        self.lookup.side_effect = [self.document, {**self.document, "draft": False}]
        with self.assertRaisesRegex(RuntimeError, "already published"):
            self.publish()
        self.run.assert_not_called()

    def test_moved_tag_before_upload(self):
        for results in (["f" * 40], [SHA, "f" * 40]):
            with self.subTest(results=results):
                self.resolve.side_effect = results
                with self.assertRaisesRegex(RuntimeError, "tag moved"):
                    self.publish()
                self.run.assert_not_called()

    def test_unrelated_draft_assets_are_not_deleted(self):
        self.list_assets.return_value = [{"name": "unrelated.zip"}]
        with self.assertRaisesRegex(RuntimeError, "unrelated assets"):
            self.publish()
        self.run.assert_not_called()

    def test_partial_upload_failure_leaves_draft_for_retry(self):
        self.lookup.side_effect = [None, self.document]
        self.run.side_effect = [None, subprocess.CalledProcessError(1, ["gh", "release", "upload"])]
        with self.assertRaises(subprocess.CalledProcessError):
            self.publish()
        self.assertTrue(self.document["draft"])
        self.assertFalse((self.root / "summary.md").exists())
        self.assertEqual(self.run.call_count, 2)

    def test_partial_assets_wrong_size_and_incomplete_upload_fail(self):
        for assets in (self.assets[:-1],
                       [{**asset, "size": 0} for asset in self.assets],
                       [{**asset, "state": "starter"} for asset in self.assets]):
            with self.subTest(assets=assets):
                self.list_assets.side_effect = [self.assets, assets]
                with self.assertRaisesRegex(RuntimeError, "asset verification failed"):
                    self.publish()
                self.assertFalse((self.root / "summary.md").exists())

    def test_missing_architecture_prevents_any_write(self):
        (self.directory / f"micamera-net-{VERSION}-linux-arm64.tar.gz").unlink()
        with self.assertRaisesRegex(RuntimeError, "exactly"):
            self.publish()
        self.run.assert_not_called()

    def test_prepare_rejects_published_release_and_missing_tag(self):
        with patch.dict(os.environ, {"GITHUB_OUTPUT": str(self.root / "output")}):
            self.document["draft"] = False
            with self.assertRaisesRegex(RuntimeError, "already published"):
                release.prepare("0.2.0")
            self.resolve.side_effect = subprocess.CalledProcessError(128, ["git", "fetch"])
            with self.assertRaises(subprocess.CalledProcessError):
                release.prepare("missing-tag")
            self.assertFalse((self.root / "output").exists())


class GitAndApiTests(unittest.TestCase):
    def test_shanghai_date_does_not_depend_on_runner_timezone(self):
        # 2026-10-12 16:30 UTC is already October 13 in Shanghai.
        with patch.object(release, "capture", return_value="1791822600"):
            self.assertEqual(release.version_for(SHA), VERSION)

    def test_lightweight_annotated_and_missing_remote_tags(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "--quiet", str(root)], check=True)
            git = lambda *args: subprocess.run(["git", "-C", str(root), *args], check=True,
                                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                "commit", "--allow-empty", "-m", "fixture")
            git("tag", "lightweight")
            git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                "tag", "-a", "annotated", "-m", "fixture")
            git("remote", "add", "origin", str(root))
            before = Path.cwd()
            try:
                os.chdir(root)
                expected = release.capture("git", "rev-parse", "HEAD")
                self.assertEqual(release.resolve_tag("lightweight"), expected)
                self.assertEqual(release.resolve_tag("annotated"), expected)
                with self.assertRaises(subprocess.CalledProcessError):
                    release.resolve_tag("missing")
                with self.assertRaises(subprocess.CalledProcessError):
                    release.resolve_tag("bad\ntag")
            finally:
                os.chdir(before)

    def test_api_only_treats_404_as_absent(self):
        with patch.dict(os.environ, {"GH_TOKEN": "offline-fixture-token"}), \
                patch.object(release, "urlopen") as open_url:
            for status in (404, 403, 429, 500):
                open_url.side_effect = HTTPError("https://example.test", status, "fixture", {}, None)
                if status == 404:
                    self.assertIsNone(release.api("/fixture", missing_ok=True))
                else:
                    with self.assertRaisesRegex(RuntimeError, f"HTTP {status}"):
                        release.api("/fixture", missing_ok=True)


if __name__ == "__main__":
    unittest.main()
