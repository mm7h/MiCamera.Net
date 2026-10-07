"""Offline regression checks for release integrity and deployment/reset rollback."""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import tarfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch


def load(name, path):
    specification = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module


HERE = Path(__file__).resolve().parent
remote = load("remote", HERE / "remote.py")
ci = load("ci", HERE / "ci.py")
configure = load("configure", HERE / "configure.py")
COMMIT = "a" * 40
VERSION = "aaaaaaaaaaaa-7-amd64"
IMAGE_ID = "sha256:" + "b" * 64


class PipelineTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.state = self.root / ".deploy"
        self.ci = self.state / "ci"
        self.incoming = self.ci / "incoming"
        self.incoming.mkdir(parents=True)
        for name, content in (("deployment.env", "LAN_IP=192.168.3.104\n"), ("settings.json", '{"lanIp":"192.168.3.104"}'),
                              ("license-ack", "ack"), (".lock", "")):
            (self.state / name).write_text(content)
        (self.root / "docker-compose.yml").write_text("name: micamera-net\n")
        self.patches = [patch.object(remote, "ROOT", self.root), patch.object(remote, "STATE", self.state),
                        patch.object(remote, "CI", self.ci)]
        for item in self.patches:
            item.start()
        self.addCleanup(lambda: [item.stop() for item in reversed(self.patches)])
        self.addCleanup(self.temporary.cleanup)

    def release(self, service):
        directory = self.incoming / f"{service}-{VERSION}"
        directory.mkdir()
        manifest = {"service": service, "commit": COMMIT, "version": VERSION, "platform": "linux/amd64"}
        files = {"remote.py": b"# test", "manifest.json": b""}
        if service == "reset":
            files["helper.py"] = (HERE.parent / "helper.py").read_bytes()
        else:
            manifest.update(image=f"micamera-net-{service}:{VERSION}", image_id=IMAGE_ID)
            files["image.tar.gz"] = b"test image"
        if service == "miloco":
            files["miloco-entrypoint.py"] = b"# adapter"
        files["manifest.json"] = json.dumps(manifest).encode()
        for name, data in files.items():
            (directory / name).write_bytes(data)
        (directory / "SHA256SUMS").write_text(
            "".join(f"{hashlib.sha256(data).hexdigest()}  {name}\n" for name, data in files.items()))
        return directory, manifest

    def baseline(self):
        return {"services": {service: {"image": f"micamera-net-{service}:previous"} for service in remote.SERVICES}}

    def test_release_verifies_and_rejects_corrupt_image(self):
        directory, manifest = self.release("bridge")
        self.assertEqual(remote.verify_release(directory, "bridge"), manifest)
        (directory / "image.tar.gz").write_bytes(b"corrupt")
        with self.assertRaisesRegex(ValueError, "checksum"):
            remote.verify_release(directory, "bridge")

    def test_release_rejects_service_substitution(self):
        directory, _ = self.release("bridge")
        with self.assertRaises(ValueError):
            remote.verify_release(directory, "web")

    def test_release_rejects_checksum_traversal(self):
        directory, _ = self.release("web")
        (directory / "SHA256SUMS").write_text("a" * 64 + "  ../secrets\n")
        with self.assertRaises(ValueError):
            remote.verify_release(directory, "web")

    def test_release_rejects_paths_outside_incoming(self):
        with self.assertRaises(ValueError):
            remote.validate_paths(self.root)

    def test_deployment_env_can_preserve_existing_image_overrides(self):
        directory, _ = self.release("web")
        (self.state / "deployment.env").write_text("LAN_IP=192.168.3.104\nMICAMERA_WEB_IMAGE=micamera-net-web:previous\n")
        self.assertEqual(remote.validate_paths(directory), directory)

    def test_deployment_changes_only_requested_service(self):
        directory, manifest = self.release("web")
        before = self.baseline()
        with patch.object(remote, "snapshot", return_value=before), patch.object(remote, "api", return_value=(200, {"configured": True})), \
                patch.object(remote, "run", return_value=json.dumps([{"Id": IMAGE_ID, "Os": "linux", "Architecture": "amd64"}])), \
                patch.object(remote, "compose") as compose, patch.object(remote, "deployment_health"):
            remote.deploy(directory, manifest)
        saved = json.loads((self.ci / "current.json").read_text())
        self.assertEqual(saved["services"]["bridge"], before["services"]["bridge"])
        self.assertEqual(saved["services"]["miloco"], before["services"]["miloco"])
        self.assertEqual(saved["services"]["web"]["image"], manifest["image"])
        self.assertEqual(compose.call_count, 1)
        self.assertEqual(compose.call_args.args[-1], "web")

    def test_health_failure_rolls_back_without_publishing_candidate(self):
        directory, manifest = self.release("bridge")
        with patch.object(remote, "snapshot", return_value=self.baseline()), patch.object(remote, "api", return_value=(200, {"configured": True})), \
                patch.object(remote, "run", return_value=json.dumps([{"Id": IMAGE_ID, "Os": "linux", "Architecture": "amd64"}])), \
                patch.object(remote, "compose") as compose, patch.object(remote, "signal"), \
                patch.object(remote, "deployment_health", side_effect=[RuntimeError("unhealthy"), None]):
            with self.assertRaisesRegex(RuntimeError, "unhealthy"):
                remote.deploy(directory, manifest)
        self.assertEqual(compose.call_count, 2)
        self.assertEqual(compose.call_args.args[0], directory / "rollback.json")
        self.assertFalse((self.ci / "current.json").exists())

    def test_image_mismatch_never_replaces_container(self):
        directory, manifest = self.release("web")
        with patch.object(remote, "snapshot", return_value=self.baseline()), patch.object(remote, "api", return_value=(200, {})), \
                patch.object(remote, "run", return_value=json.dumps([{"Id": "wrong", "Os": "linux", "Architecture": "amd64"}])), \
                patch.object(remote, "compose") as compose:
            with self.assertRaises(ValueError):
                remote.deploy(directory, manifest)
        compose.assert_not_called()

    def test_reset_requires_confirmation_before_stopping(self):
        directory, manifest = self.release("reset")
        with patch.object(remote, "compose") as compose:
            with self.assertRaises(ValueError):
                remote.reset(directory, manifest, "")
        compose.assert_not_called()

    def test_reset_targets_preserve_infrastructure_and_include_legacy_credentials(self):
        for name in ("MiCameraConfig.json", "MiCameraConfig.before-web-setup.json", "cameras.json"):
            (self.state / name).write_text("old")
        targets = remote.reset_targets(self.state)
        self.assertIn("MiCameraConfig.before-web-setup.json", targets)
        self.assertIn("cameras.json", targets)
        self.assertNotIn("deployment.env", targets)
        self.assertNotIn("ci", targets)
        self.assertNotIn(".lock", targets)

    def application_state(self):
        for name in ("secrets", "miloco", "micamera-state"):
            (self.state / name).mkdir()
            (self.state / name / "old-data").write_text("original-" + name)
        (self.state / "MiCameraConfig.json").write_text('{"original":true}')

    def move_state(self, image, script, *arguments):
        if script == remote.MOVE_STATE:
            script = script.replace("pathlib.Path('/state')", "pathlib.Path(sys.argv[4])")
            subprocess.run([sys.executable, "-c", script, *arguments, str(self.state)], check=True)
        else:
            (self.state / "micamera-state").mkdir(mode=0o700)

    def test_reset_health_failure_restores_original_credentials_and_configuration(self):
        self.application_state()
        directory, manifest = self.release("reset")
        original_environment = (self.state / "deployment.env").read_bytes()
        with patch.object(remote, "snapshot", return_value=self.baseline()), patch.object(remote, "compose"), \
                patch.object(remote, "state_container", side_effect=self.move_state), patch.object(remote, "signal"), \
                patch.object(remote, "api", return_value=(200, {"configured": True})), \
                patch.object(remote, "wait_for", side_effect=[RuntimeError("failed startup"), None]), \
                patch.object(remote, "deployment_health"), patch.dict(os.environ, {}, clear=False):
            with self.assertRaisesRegex(RuntimeError, "failed startup"):
                remote.reset(directory, manifest, "MiCamera.Net")
        self.assertEqual((self.state / "secrets/old-data").read_text(), "original-secrets")
        self.assertEqual((self.state / "miloco/old-data").read_text(), "original-miloco")
        self.assertEqual((self.state / "micamera-state/old-data").read_text(), "original-micamera-state")
        self.assertEqual(json.loads((self.state / "MiCameraConfig.json").read_text()), {"original": True})
        self.assertEqual((self.state / "deployment.env").read_bytes(), original_environment)

    def test_successful_reset_generates_new_secrets_and_accepts_unconfigured_readiness(self):
        self.application_state()
        environment = b"LAN_IP=192.168.3.104\nMICAMERA_WEB_IMAGE=micamera-net-web:previous\n"
        (self.state / "deployment.env").write_bytes(environment)
        directory, manifest = self.release("reset")
        responses = [(200, {"configured": True}), (200, {"configured": False, "listening": False}),
                     (200, {"hasMilocoPin": False, "hasRtspPassword": False, "streams": []}), (503, {"ready": False})]
        with patch.object(remote, "snapshot", return_value=self.baseline()), patch.object(remote, "compose"), \
                patch.object(remote, "state_container", side_effect=self.move_state), patch.object(remote, "wait_for"), \
                patch.object(remote, "api", side_effect=responses), patch.object(remote, "deployment_health"), \
                patch.dict(os.environ, {}, clear=False):
            remote.reset(directory, manifest, "MiCamera.Net")
        token = (self.state / "secrets/rtsp_api_token").read_text().strip()
        self.assertRegex(token, r"^[0-9a-f]{64}$")
        self.assertFalse((self.state / "micamera-state/old-data").exists())
        self.assertTrue((self.ci / f"backups/reset-{VERSION}/secrets/old-data").exists())
        self.assertEqual(json.loads((directory / "result.json").read_text())["status"], "success")
        self.assertEqual((self.state / "deployment.env").read_bytes(), environment)

    def test_miloco_uses_digest_and_skips_source_build(self):
        self.assertRegex(ci.upstream_image(), r"@sha256:[0-9a-f]{64}$")
        with patch.object(ci, "run") as run:
            ci.build("miloco", "dependencies")
            ci.build("miloco", "build")
        run.assert_not_called()

    def test_archive_config_identity_is_independent_of_docker_manifest_id(self):
        image = "micamera-net-web:test-amd64"
        config = b'{"architecture":"amd64","os":"linux"}'
        config_id = hashlib.sha256(config).hexdigest()
        archive_path = self.root / "image.tar.gz"
        for config_name in (config_id + ".json", "blobs/sha256/" + config_id):
            with tarfile.open(archive_path, "w:gz") as archive:
                entries = json.dumps([{"Config": config_name, "RepoTags": [image], "Layers": []}]).encode()
                for name, data in (("manifest.json", entries), (config_name, config)):
                    member = tarfile.TarInfo(name)
                    member.size = len(data)
                    archive.addfile(member, io.BytesIO(data))
            self.assertEqual(ci.archive_image_id(archive_path, image), "sha256:" + config_id)
            with self.assertRaises(ValueError):
                ci.archive_image_id(archive_path, "micamera-net-web:different")

    def test_miloco_mirror_preserves_pin_and_confirms_upstream_before_tagging(self):
        upstream = ci.upstream_image()
        mirror = upstream.replace("ghcr.io/", "ghcr.nju.edu.cn/", 1)
        image = "micamera-net-miloco:" + VERSION
        for mirror_fails in (False, True):
            with self.subTest(mirror_fails=mirror_fails):
                results = [subprocess.CalledProcessError(1, "docker") if mirror_fails else None, None, None]
                with patch.object(ci, "run", side_effect=results) as run, patch.object(ci, "image_info") as info, \
                        patch.object(ci, "build_identity", return_value=(COMMIT, VERSION, image)):
                    ci.build("miloco", "image")
                self.assertEqual([item.args for item in run.call_args_list], [
                    ("docker", "pull", "--platform", "linux/amd64", mirror),
                    ("docker", "pull", "--platform", "linux/amd64", upstream),
                    ("docker", "tag", upstream, image)])
                info.assert_called_once_with(upstream)

    def test_ssh_requires_strict_host_verification(self):
        with patch.dict(os.environ, {"DEPLOY_KEY": "private-key", "DEPLOY_USER": "hang", "DEPLOY_KNOWN_HOSTS": "known-hosts"}):
            options = ci.ssh_options()
        self.assertIn("StrictHostKeyChecking=yes", options)
        self.assertIn("BatchMode=yes", options)

    def test_all_four_jobs_use_private_develop_scm_without_triggers(self):
        self.assertEqual(set(configure.JOBS), {"Frontend", "Backend", "Miloco", "Reset"})
        for service in configure.JOBS.values():
            definition = ET.fromstring(configure.job_xml(service))
            self.assertEqual(definition.findtext("definition/scm/branches/hudson.plugins.git.BranchSpec/name"), "*/develop")
            self.assertEqual(definition.findtext("definition/scm/userRemoteConfigs/hudson.plugins.git.UserRemoteConfig/credentialsId"), "micamera-github-readonly")
            self.assertEqual(definition.findtext("definition/scriptPath"), f"deployment/jenkins/Jenkinsfile.{service}")
            self.assertEqual(len(definition.find("triggers")), 0)
        reset = ET.fromstring(configure.job_xml("reset"))
        self.assertEqual(reset.findtext("properties/hudson.model.ParametersDefinitionProperty/parameterDefinitions/hudson.model.StringParameterDefinition/name"), "CONFIRM_RESET")

    @unittest.skipIf(os.name == "nt", "fcntl locks require Linux")
    def test_remote_uses_the_existing_lock_and_waits_for_exclusive_access(self):
        import fcntl
        directory, manifest = self.release("web")
        statuses = []
        with (self.state / ".lock").open("r+") as other:
            fcntl.flock(other, fcntl.LOCK_EX | fcntl.LOCK_NB)
            def wait(check, seconds):
                self.assertEqual(seconds, 600)
                statuses.append(check())
                fcntl.flock(other, fcntl.LOCK_UN)
                statuses.append(check())
            with patch.object(sys, "argv", ["remote.py", "web", "--release", str(directory)]), \
                    patch.object(remote, "run", return_value="x86_64"), patch.object(remote, "wait_for", side_effect=wait), \
                    patch.object(remote, "signal"), patch.object(remote, "deploy") as deploy:
                remote.main()
            deploy.assert_called_once_with(directory, manifest)
        self.assertEqual(statuses, [False, True])


if __name__ == "__main__":
    unittest.main()
