"""Validate the real Compose definition using synthetic state, without requiring an Engine."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from unittest.mock import patch

repository = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("helper", repository / "deployment/helper.py")
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
with tempfile.TemporaryDirectory(prefix="micamera-compose-check-") as temporary:
    project = Path(temporary)
    state = project / ".deploy"
    (project / "docker-compose.yml").write_text((repository / "docker-compose.yml").read_text(encoding="utf-8"), encoding="utf-8")
    with patch.dict(os.environ, {"MICAMERA_STATE_DIR": str(state)}):
        helper.initialize("192.168.1.100")
        helper.stage_stream("device-test", "camera-test", "0", "H265")
        helper.commit_stream()
    command = [sys.argv[1], "--project-directory", str(project), "--env-file", str(state / "deployment.env"),
               "-f", str(project / "docker-compose.yml"), "config"]
    subprocess.run(command + ["--quiet"], check=True)
    result = subprocess.run(command + ["--format", "json"], capture_output=True, text=True, check=True)
    document = json.loads(result.stdout)
    services = document["services"]
    assert set(services) == {"miloco", "bridge", "web"}
    assert services["miloco"]["network_mode"] == "host"
    assert "@sha256:" in services["miloco"]["image"]
    assert services["bridge"]["network_mode"] == "host"
    assert services["web"]["ports"][0]["host_ip"] == "192.168.1.100"
    assert services["bridge"]["environment"] == {"TZ": "Asia/Shanghai"}
    assert services["miloco"]["environment"]["SECRET_KEY_FILE"] == "/run/secrets/miloco_jwt_secret"
    assert {item["source"] for item in services["bridge"]["secrets"]} == {
        "miloco_password_md5", "rtsp_api_token", "rtsp_password"}
    assert {item["source"] for item in services["miloco"]["secrets"]} == {"miloco_jwt_secret"}
    assert services["bridge"]["configs"][0]["target"] == "/run/configs/MiCameraConfig.json"
    assert services["bridge"]["volumes"][0]["target"] == "/run/configs/miloco-server-cert.pem"
    assert services["bridge"]["volumes"][0]["read_only"] is True
    assert set(document["secrets"]) == {"miloco_jwt_secret", "miloco_password_md5", "rtsp_api_token", "rtsp_password"}
    assert set(document["configs"]) == {"micamera_config"}
    for service in services.values():
        assert service["restart"] == "unless-stopped"
        assert service["logging"]["options"]["max-size"] == "10m"
        assert "healthcheck" in service
    print("Compose schema and three-service network/security configuration passed (no Engine startup performed).")
