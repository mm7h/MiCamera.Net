#!/usr/bin/env python3
"""Materialize the bridge's private runtime configuration before starting it."""
import json
import os
from pathlib import Path
import sys
import tempfile


CONFIG_TEMPLATE = Path("/run/configs/MiCameraConfig.json")
RUNTIME_CONFIG_DIRECTORY = Path("/tmp")
APPLICATION = "/app/MiCamera.Net.Sample.Server"
SECRETS = {
    ("MediaServer", "BearerToken"): Path("/run/secrets/rtsp_api_token"),
}


def read_secret(path):
    value = path.read_text(encoding="ascii").rstrip("\r\n")
    if not value or "\r" in value or "\n" in value:
        raise RuntimeError(f"密钥文件 {path} 必须且只能包含一个非空值。")
    return value


def materialize_configuration():
    document = json.loads(CONFIG_TEMPLATE.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise RuntimeError("MiCameraConfig.json 必须包含 JSON 对象。")

    for property_path, secret_path in SECRETS.items():
        values = document
        for index, section in enumerate(property_path[:-1]):
            values = values.get(section)
            if not isinstance(values, dict):
                section_path = ".".join(property_path[:index + 1])
                raise RuntimeError(f"MiCameraConfig.json 缺少 {section_path} 对象。")
        values[property_path[-1]] = read_secret(secret_path)

    descriptor, temporary_path = tempfile.mkstemp(
        prefix="micamera-config-", suffix=".json", dir=RUNTIME_CONFIG_DIRECTORY, text=True)
    try:
        os.fchmod(descriptor, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as runtime_file:
            json.dump(document, runtime_file, separators=(",", ":"))
            runtime_file.write("\n")
    except BaseException:
        try:
            os.close(descriptor)
        except OSError:
            pass
        Path(temporary_path).unlink(missing_ok=True)
        raise

    return temporary_path


def main():
    validate_only = sys.argv[1:] == ["--validate-config"]
    if len(sys.argv) > 1 and not validate_only:
        raise RuntimeError("桥接服务入口仅接受 --validate-config 参数。")

    runtime_config = materialize_configuration()
    if validate_only:
        return

    os.execv(APPLICATION, [APPLICATION, runtime_config])


if __name__ == "__main__":
    main()
