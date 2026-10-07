"""Run the pinned Miloco image as the raw-video upstream for MiCamera.Net.

MiCamera.Net produces its own JPEG snapshots. Miloco's vision JPEG subscription
otherwise decodes every 4K frame again, even without an AI model or UI viewer.
"""
import os
from pathlib import Path
import runpy
import sys


async def skip_vision_jpeg(self, callback, channel=0, multi_reg=False):
    # Match MIoTCameraInstance.register_decode_jpg_async's default registration ID.
    return 0


def main():
    os.environ["SECRET_KEY"] = Path(os.environ["SECRET_KEY_FILE"]).read_text().strip()
    sys.path.insert(0, "/app")
    from miot.camera import MIoTCameraInstance
    from miloco_server.config.normal_config import SERVER_CONFIG

    SERVER_CONFIG["enable_file_logging"] = False
    MIoTCameraInstance.register_decode_jpg_async = skip_vision_jpeg
    print("MiCamera.Net 上游服务：已启用原始视频，已禁用视觉功能的重复 JPEG 解码。", flush=True)
    runpy.run_path("/app/start_server.py", run_name="__main__")


if __name__ == "__main__":
    main()
