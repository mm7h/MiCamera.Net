# MiCamera.Net

`MiCamera.Net` 连接小米 Miloco 服务，接收 H.264/H.265 Annex-B 视频流，并将已配置的摄像头通道提供为 RTSP、JPEG 截图和 WebRTC 信令接口。

## 功能概览

- 通过 Miloco WebSocket 获取摄像头视频，并为每个通道独立维护认证、重连和关键帧同步。
- 为多个摄像头通道提供 RTSP 直播、JPEG 截图和浏览器 WebRTC 预览。
- 支持 H.264 直通，以及在 FFmpeg 可用时将 H.265 转换为 WebRTC 可用的 H.264。
- 附带控制台服务示例和 React/Vite 浏览器预览示例。

## 项目结构

| 项目 | 用途 |
| --- | --- |
| `MiCamera.Net.Abstractions` | 公共配置、流描述、视频块和服务构建接口。 |
| `MiCamera.Net` | Miloco 认证、WebSocket 拉流、流分发、关键帧同步和托管服务生命周期。 |
| `MiCamera.Net.RTSP.Abstractions` | RTSP、媒体处理和 WebRTC 的公共配置与契约。 |
| `MiCamera.Net.Media` | 基于 FFmpeg 的解码、JPEG 截图和 H.265 到 H.264 的按需转码。 |
| `MiCamera.Net.RTSP` | RTSP 服务、HTTP 控制器和基于 SIPSorcery 的 WebRTC 会话管理。 |
| `MiCamera.Net.Sample.Server` | 从 JSON 和环境变量加载配置的控制台宿主。 |
| `MiCamera.Net.Sample.Web` | 独立的 React/Vite 浏览器预览示例，未纳入 Visual Studio 解决方案。 |

## 快速开始

需要 .NET 8 SDK。若要运行浏览器示例，还需要与 `demo/MiCamera.Net.Sample.Web/package.json` 中 `engines` 约束相符的 Node.js。

1. 编辑 `demo/MiCamera.Net.Sample.Server/Configs/MiCameraConfig.json`，填写真实的摄像头 DID。
2. 通过环境变量提供密码。JSON 中的 `Password` 可以保持为空。
3. 启动控制台服务：

   ```powershell
   $env:MILOCO_PASSWORD = "你的 Miloco 密码 MD5 值"
   dotnet run --project demo/MiCamera.Net.Sample.Server
   ```

Miloco 密码应为其本地接口要求的小写 MD5 值。程序不会将密码写入日志。

## 服务配置

示例服务从 `demo/MiCamera.Net.Sample.Server/Configs/MiCameraConfig.json` 读取核心配置；环境变量会覆盖相应的 JSON 值。

| 配置段 | 关键字段 | 说明 |
| --- | --- | --- |
| `Miloco` | `BaseUrl`、`Username`、`Password`、`RequestTimeout` | Miloco 地址、认证信息和请求超时。 |
| `Streaming` | `ConnectTimeout`、`FirstKeyFrameTimeout`、`IdleTimeout` | 流连接、首个关键帧和空闲检测限制。 |
| `Reconnect` | `InitialDelay`、`MaximumDelay`、`BackoffMultiplier`、`JitterRatio` | 拉流失败后的退避重连策略。 |
| `Streams[]` | `StreamId`、`CameraDeviceId`、`Channel`、`Codec`、`NominalFrameRate` | 要公开的摄像头通道。 |

`MiCameraConfig.json` 中所有时长值均为数值秒数，例如 `"RequestTimeout": 15`。不再支持 `"00:00:15"` 这类 `TimeSpan` 字符串格式。

每个 `StreamId` 必须唯一；每组 `CameraDeviceId + Channel` 也必须唯一。`H265` 是默认编解码器，Miloco 输出 H.264 时应显式配置为 `H264`。当视频流没有有效时间信息时，`NominalFrameRate` 用作回退帧率。

| 环境变量 | 用途 |
| --- | --- |
| `MILOCO_BASE_URL`、`MILOCO_USERNAME`、`MILOCO_PASSWORD` | 覆盖 Miloco 地址、用户名和密码。 |
| `CAMERA_ID`、`STREAM_CHANNEL`、`VIDEO_CODEC` | 仅配置一个流时，覆盖其设备 ID、通道和编解码器；编解码器可为 `h264`、`h265` 或 `hevc`。 |
| `RTSP_LISTEN_ADDRESS`、`RTSP_PORT`、`RTSP_USERNAME`、`RTSP_PASSWORD` | 覆盖 RTSP 监听与 Digest 认证配置。 |
| `RTSP_HTTP_LISTEN_URL`、`RTSP_API_TOKEN`、`RTSP_ALLOWED_ORIGINS` | 覆盖 HTTP 监听地址、Bearer Token 和允许的 CORS 源。多个源以分号分隔，且只能是没有路径的 HTTP/HTTPS 源。 |
| `FFMPEG_ROOT_PATH` | 指定 FFmpeg 7.1 兼容原生库的位置。 |

示例配置为方便本地部署将 `AllowInvalidServerCertificate` 设为 `true`。生产环境应设为 `false`，并正确信任 Miloco 证书。

## 默认接口

`WithRtsp` 默认只监听本机回环地址，并提供以下接口：

| 接口 | 地址或方法 | 说明 |
| --- | --- | --- |
| RTSP | `rtsp://127.0.0.1:8554/live/{streamId}` | 当前仅支持 RTP/RTCP over TCP。 |
| 摄像头列表 | `GET http://127.0.0.1:5080/api/cameras` | 返回已配置流及其状态。 |
| JPEG 截图 | `GET /api/cameras/{streamId}/snapshot` | 返回最近的已解码关键帧截图。 |
| 创建 WebRTC 会话 | `POST /api/webrtc/sessions` | 请求体为 `{ "streamId": "living-room" }`，返回会话 ID 和 SDP offer。 |
| WebRTC 应答和 ICE | `POST /api/webrtc/sessions/{id}/answer`、`POST /api/webrtc/sessions/{id}/ice-candidates` | 浏览器提交 SDP answer 和候选项。 |
| 关闭 WebRTC 会话 | `DELETE /api/webrtc/sessions/{id}` | 释放会话资源。 |

非回环 HTTP 监听必须配置 `RTSP_API_TOKEN`；非回环 RTSP 监听必须配置用户名和密码。`0.0.0.0` 仅表示监听所有网卡，不能作为浏览器访问地址。

## 浏览器预览示例

先启动控制台服务，再启动前端：

```powershell
dotnet run --project demo/MiCamera.Net.Sample.Server
Set-Location demo/MiCamera.Net.Sample.Web
npm.cmd install
npm.cmd run dev
```

打开 `http://127.0.0.1:5081`。前端默认访问 `http://127.0.0.1:5080`，也可通过 `VITE_MICAMERA_API_URL` 指定 API 地址。可选 Bearer Token 和截图历史只保存在浏览器内存中，刷新页面或更改 API 地址后会清除。

在局域网中预览时，使用 `npm.cmd run dev:lan`，并为服务设置实际可访问的 HTTP 地址、Bearer Token 与前端源：

```powershell
$env:RTSP_HTTP_LISTEN_URL = "http://192.168.1.20:5080"
$env:RTSP_API_TOKEN = "替换为随机令牌"
$env:RTSP_ALLOWED_ORIGINS = "http://192.168.1.20:5081"
dotnet run --project demo/MiCamera.Net.Sample.Server

Set-Location demo/MiCamera.Net.Sample.Web
npm.cmd run dev:lan
```

该示例面向受信任的本地网络。若要对外开放，请在前端或反向代理层启用 HTTPS，并妥善保护访问令牌。

## 媒体能力与依赖

未提供 FFmpeg 原生库时，RTSP 直通仍可使用，但 JPEG 截图和 H.265 WebRTC 转码不可用。项目使用 `Flurl.Http`、`Websocket.Client`、`SharpRTSP`、`SIPSorcery` 和 `FFmpeg.AutoGen`；发布前请审阅这些依赖的许可证，尤其是 SIPSorcery 的附加使用限制。

## 开发验证

```powershell
dotnet build MiCamera.Net.sln

Set-Location demo/MiCamera.Net.Sample.Web
npm.cmd install
npm.cmd run lint
npm.cmd test
npm.cmd run build
```
