import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("helper", Path(__file__).parents[1] / "helper.py")
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class HelperTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.environment = patch.dict(os.environ, {"MICAMERA_STATE_DIR": self.directory.name})
        self.environment.start()
        helper.initialize("192.168.1.100")

    def tearDown(self):
        self.environment.stop()
        self.directory.cleanup()

    def configure_complete_state(self):
        helper.write_secret("miloco_password_md5", hashlib.md5(b"123456").hexdigest())
        helper.stage_stream("device1", "camera-1", "0", "H265")
        helper.commit_stream()
        helper.main(["ack-license"])

    def test_repeat_init_preserves_generated_secrets(self):
        first = {name: helper.read_secret(name, allow_empty=name == "miloco_password_md5") for name in helper.SECRET_PATTERNS}
        helper.initialize("192.168.1.100")
        second = {name: helper.read_secret(name, allow_empty=name == "miloco_password_md5") for name in helper.SECRET_PATTERNS}
        self.assertEqual(first, second)

    def test_invalid_persisted_secret_is_not_silently_replaced(self):
        helper.secret_path("miloco_jwt_secret").write_text("", encoding="ascii")
        with self.assertRaises(helper.DeploymentError):
            helper.initialize("192.168.1.100")
        self.assertEqual("", helper.secret_path("miloco_jwt_secret").read_text(encoding="ascii"))

    def test_source_password_is_not_used_and_durations_are_seconds(self):
        document = json.loads((helper.state_dir() / "MiCameraConfig.json").read_text())
        self.assertEqual("", document["Miloco"]["Password"])
        self.assertEqual(15, document["Miloco"]["RequestTimeout"])
        self.assertEqual(60, document["Streaming"]["FirstKeyFrameTimeout"])

    def test_invalid_ip_or_changed_ip_is_rejected(self):
        for address in ("127.0.0.1", "8.8.8.8", "192.168.1.101"):
            with self.assertRaises(helper.DeploymentError):
                helper.initialize(address)

    def test_actual_account_status_and_unsupported_status(self):
        self.assertTrue(helper.login_status({"code": 0, "data": {"is_logged_in": True}}))
        self.assertFalse(helper.login_status({"code": 0, "data": {"is_logged_in": False}}))
        for body in ({"code": 0, "data": True}, {"code": 0, "data": {"is_logged_in": "true"}}, {"code": True, "data": {}}):
            with self.assertRaises(helper.DeploymentError):
                helper.login_status(body)

    def test_pending_commit_and_rollback_preserve_original(self):
        original = (helper.state_dir() / "MiCameraConfig.json").read_bytes()
        helper.stage_stream("device1", "camera-1", "0", "H265")
        self.assertEqual(original, (helper.state_dir() / "MiCameraConfig.json").read_bytes())
        helper.commit_stream()
        document = json.loads((helper.state_dir() / "MiCameraConfig.json").read_text())
        self.assertEqual("device1", document["MediaServer"]["Rtsp"]["Streams"][0]["CameraDeviceId"])
        self.assertNotIn("CameraId", document["MediaServer"]["Rtsp"]["Streams"][0])
        helper.main(["rollback-stream"])
        self.assertEqual(original, (helper.state_dir() / "MiCameraConfig.json").read_bytes())

    def test_duplicate_stream_or_device_channel_rejected(self):
        helper.stage_stream("device1", "camera-1", "0", "H264")
        helper.commit_stream()
        for args in (("device2", "CAMERA-1", "0", "H265"), ("device1", "camera-2", "0", "H265")):
            with self.assertRaises(helper.DeploymentError):
                helper.stage_stream(*args)
        helper.stage_stream("device1", "camera-2", "1", "H265")

    def test_password_is_hashed_to_secret_file(self):
        value = os.urandom(20).hex()
        with patch.object(helper.sys, "stdin", io.StringIO(value)):
            helper.main(["set-password"])
        result = helper.read_secret("miloco_password_md5")
        self.assertNotEqual(value, result)
        self.assertRegex(result, r"^[0-9a-f]{32}$")

    def test_secret_file_accepts_one_terminal_newline_only(self):
        value = "a" * 64
        helper.secret_path("rtsp_api_token").write_text(value + "\n", encoding="ascii")
        self.assertEqual(value, helper.read_secret("rtsp_api_token"))
        helper.secret_path("rtsp_api_token").write_text(value + "\nextra", encoding="ascii")
        with self.assertRaises(helper.DeploymentError):
            helper.read_secret("rtsp_api_token")

    def test_runtime_secret_rejects_direct_and_file_values_together(self):
        with patch.dict(os.environ, {"RTSP_API_TOKEN": "a" * 64, "RTSP_API_TOKEN_FILE": "/tmp/token"}, clear=False):
            with self.assertRaises(helper.DeploymentError):
                helper.runtime_secret("RTSP_API_TOKEN", "rtsp_api_token")

    def test_noninteractive_state_requires_complete_secrets_stream_and_acknowledgement(self):
        with self.assertRaises(helper.DeploymentError):
            helper.validate_state("192.168.1.100")
        self.configure_complete_state()
        with self.assertRaises(helper.DeploymentError):
            helper.validate_state("192.168.1.100")
        certificate = helper.state_dir() / "miloco" / "cert" / "cert.pem"
        certificate.parent.mkdir(mode=0o700)
        certificate.write_text("test certificate\n", encoding="ascii")
        helper.validate_state("192.168.1.100")

    def test_legacy_env_layout_is_rejected_without_migration(self):
        legacy = Path(self.directory.name) / "bridge.env"
        legacy.write_text("MILOCO_PASSWORD=deadbeef\n", encoding="ascii")
        with self.assertRaises(helper.DeploymentError):
            helper.initialize("192.168.1.100")
        self.assertTrue(legacy.exists())


if __name__ == "__main__":
    unittest.main()
