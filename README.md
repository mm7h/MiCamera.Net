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
| `MiCamera.Net.Sample.Server` | 从启动参数指定的 JSON 文件加载配置的控制台宿主。 |
| `MiCamera.Net.Sample.Web` | 独立的 React/Vite 浏览器预览示例，未纳入 Visual Studio 解决方案。 |

## 快速开始

### Linux Docker 一键部署

将当前源码放到与摄像头同一家庭局域网的 Linux amd64/arm64 服务器上，安装 Docker Engine、Compose v2.20+、Bash 4+、iproute2、awk、coreutils 和 util-linux，然后运行：

```bash
bash deploy.sh
```

中文向导会引导选择局域网 IP、确认依赖许可、启动 Miloco、在网页绑定小米账号、隐藏输入 **Miloco 本地密码**、选择摄像头并启动前后端。无需安装主机 .NET/Node/Python，也不需要 OpenClaw、Python micam、go2rtc 或 AI/GPU 服务。小米账号密码仅在授权网页输入，不由脚本采集。

正式镜像不包含可运行的示例配置。向导将无密码的流配置写入 `.deploy/MiCameraConfig.json` 并只读挂载；容器入口将 Compose secret 文件合成为仅容器内可读的临时 JSON，再把该 JSON 路径作为应用的唯一参数传入。不要在 `docker run -e`、Compose YAML 或源码 JSON 中写入这些值。

本地构建镜像包含 FFmpeg 原生解码、JPEG 和 H.265→H.264 所需组件；部署完成后还必须在浏览器确认实际画面。前端容器通过 `http://服务器IP:5081/api` 同源代理访问桥接服务并自动附加 Bearer Token，浏览器无需输入 Token。Docker Desktop 上已通过 amd64/arm64 镜像构建和隔离的合成视频测试（ARM64 使用模拟执行），但 Desktop 网络不能替代本向导要求的原生 Linux LAN host 网络。完整说明、恢复步骤和真机验收清单见 [Docker 部署说明](docs/DOCKER_DEPLOYMENT.md)，已执行检查见 [部署验收记录](docs/DEPLOYMENT_VERIFICATION.md)。原生 Linux 双架构和真机端到端验收仍未完成。

### 从源码直接运行

需要 .NET 8 SDK。若要运行浏览器示例，还需要与 `demo/MiCamera.Net.Sample.Web/package.json` 中 `engines` 约束相符的 Node.js。

1. 将 `demo/MiCamera.Net.Sample.Server/Configs/MiCameraConfig.json` 复制到一个未纳入版本控制的位置。
2. 编辑该副本，填写摄像头 DID、Miloco 密码 MD5，以及需要的 RTSP/HTTP 认证信息。
3. 将配置文件路径作为唯一参数启动控制台服务：

   ```powershell
   dotnet run --project demo/MiCamera.Net.Sample.Server -- C:\secure-config\MiCameraConfig.json
   ```

Miloco 密码应为其本地接口要求的小写 MD5 值。不要把包含密码、Bearer Token 或 RTSP 密码的文件提交到仓库；程序不会将这些值写入日志。

## 服务配置

示例服务只接受一个启动参数：`MiCameraConfig.json` 的路径。程序不提供默认配置路径，也不读取环境变量、secret 文件或其他覆盖来源；所有运行时选项均从这个 JSON 文件解析。

| 配置段 | 关键字段 | 说明 |
| --- | --- | --- |
| `Miloco` | `BaseUrl`、`Username`、`Password`、`RequestTimeout` | Miloco 地址、认证信息和请求超时。 |
| `Streaming` | `ConnectTimeout`、`FirstKeyFrameTimeout`、`IdleTimeout` | 流连接、首个关键帧和空闲检测限制。 |
| `Reconnect` | `InitialDelay`、`MaximumDelay`、`BackoffMultiplier`、`JitterRatio` | 拉流失败后的退避重连策略。 |
| `MediaServer` | `ListenAddress`、`BearerToken`、`AllowedOrigins` | HTTP API 的监听 URL、认证和 CORS。 |
| `MediaServer.Rtsp` | `ListenAddress`、`Port`、`Username`、`Password` | RTSP 的端点与 Digest 认证配置。 |
| `MediaServer.WebRtc`、`MediaServer.Snapshot` | WebRTC UDP 绑定/端口范围、截图开关 | 可选媒体服务的运行参数。 |
| `MediaServer.FFmpeg` | `Path`、H.264 编码参数、`H264MaxWidth`/`H264MaxHeight` | FFmpeg 7.1 兼容原生库所在目录及转码设置；路径留空时使用系统默认搜索路径。`H264MaxWidth`/`H264MaxHeight` 限制浏览器收到的 H.264 分辨率，两者同时为 0 时保持摄像头分辨率。 |
| `MediaServer.Rtsp.Streams[]` | `StreamId`、`CameraDeviceId`、`Channel`、`Codec`、`NominalFrameRate` | 要公开的摄像头通道，同时供 RTSP、WebRTC 和截图使用。 |

`MiCameraConfig.json` 中所有时长值均为数值秒数，例如 `"RequestTimeout": 15`。不再支持 `"00:00:15"` 这类 `TimeSpan` 字符串格式。

媒体服务配置统一放在 `MediaServer` 下，该对象必须存在。旧的顶层 `Rtsp`、`Http`、`Media`、`WebRtc`、`Snapshot` 配置会被拒绝：将 `Rtsp`、`WebRtc`、`Snapshot` 移入 `MediaServer`，将 `Http.ListenUrl` 改为 `MediaServer.ListenAddress`，将 HTTP 的认证和 CORS 字段移入 `MediaServer`，将 `Media` 改为 `MediaServer.FFmpeg`，其中 `NativeLibraryPath` 改名为 `Path`。通过 C# `WithRtsp` 配置的公共运行时选项名称保持不变。

流列表位于 `MediaServer.Rtsp.Streams`；旧的顶层 `Streams` 会被拒绝，迁移时请将整个数组移动到该位置。示例宿主将其映射到核心运行时的 `MiCameraServerOptions.Streams`，因此 WebRTC 和截图仍使用同一批流，C# 公共配置接口保持不变。

每个 `StreamId` 必须唯一；每组 `CameraDeviceId + Channel` 也必须唯一。`H265` 是默认编解码器，Miloco 输出 H.264 时应显式配置为 `H264`。当视频流没有有效时间信息时，`NominalFrameRate` 用作回退帧率。

在 Windows 上可将 `MediaServer.FFmpeg.Path` 设为例如 `C:\\ffmpeg\\bin`；它应指向包含 FFmpeg 原生动态库的目录，而不是 `ffmpeg.exe` 文件本身。相对路径按应用输出目录解析，因此示例中的 `ffmpeg` 对应 `demo/MiCamera.Net.Sample.Server/bin/Debug/net8.0/ffmpeg`。

示例配置为方便本地部署将 `AllowInvalidServerCertificate` 设为 `true`。生产环境应设为 `false`，并在 `Miloco.TrustedServerCertificatePath` 中固定配置受信任的 PEM 证书；HTTP 和 WebSocket 都只接受该证书。非回环 RTSP 监听必须在 `MediaServer.Rtsp` 中同时配置用户名与密码，非回环 HTTP 监听必须在 `MediaServer.BearerToken` 中配置 Token。

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
| 服务存活 | `GET /api/health/live` | 服务可响应时返回 200；不依赖摄像头在线情况，沿用 Bearer 认证。 |
| 媒体就绪 | `GET /api/health/ready` | 返回 `ready`、`mediaAvailable` 和逐流状态；要求近期收帧、媒体能力及已启用截图/WebRTC 功能可用，未就绪返回 503。 |

非回环 HTTP 监听必须配置 `MediaServer.BearerToken`；非回环 RTSP 监听必须配置 `MediaServer.Rtsp.Username` 和 `MediaServer.Rtsp.Password`。`0.0.0.0` 仅表示监听所有网卡，不能作为浏览器访问地址。

## 浏览器预览示例

先启动控制台服务，再启动前端：

```powershell
dotnet run --project demo/MiCamera.Net.Sample.Server -- C:\secure-config\MiCameraConfig.json
Set-Location demo/MiCamera.Net.Sample.Web
npm.cmd install
npm.cmd run dev
```

打开 `http://127.0.0.1:5081`。前端默认访问 `http://127.0.0.1:5080`，也可通过 `VITE_MICAMERA_API_URL` 指定 API 地址。可选 Bearer Token 和截图历史只保存在浏览器内存中，刷新页面或更改 API 地址后会清除。容器镜像在构建时设置 `VITE_MICAMERA_SAME_ORIGIN_API=true`，使前端默认改用同源地址并经由前述 Nginx 代理访问 API，因此部署后无需输入 Token。

在局域网中预览时，使用 `npm.cmd run dev:lan`，并在 JSON 的 `MediaServer.ListenAddress`、`MediaServer.BearerToken` 和 `MediaServer.AllowedOrigins` 中设置实际可访问的 HTTP 地址、Bearer Token 与前端源：

```powershell
dotnet run --project demo/MiCamera.Net.Sample.Server -- C:\secure-config\MiCameraConfig.lan.json

Set-Location demo/MiCamera.Net.Sample.Web
npm.cmd run dev:lan
```

该示例面向受信任的本地网络。若要对外开放，请在前端或反向代理层启用 HTTPS，并妥善保护访问令牌。

## 媒体能力与依赖

未提供 FFmpeg 原生库时，RTSP 直通仍可使用，但 JPEG 截图和 H.265 WebRTC 转码不可用。H.265 摄像头只有在没有 WebRTC 观看者时才按关键帧解码（只维护截图），观看时按需转码；低功耗主机应把 `H264MaxWidth`/`H264MaxHeight` 限制在 1920×1080，否则 4K H.265 到 4K H.264 的 CPU 转码会跟不上实时速度并逐步累积延迟。项目使用 `Flurl.Http`、`Websocket.Client`、`SharpRTSP`、`SIPSorcery` 和 `FFmpeg.AutoGen`；发布前请审阅这些依赖的许可证，尤其是 SIPSorcery 的附加使用限制。

Miloco 本地登录与小米账号授权状态分别校验。HTTP 200 并不代表小米授权有效；不兼容的登录状态格式会明确失败。WebRTC offer 使用源 H.264 SPS 或实际转码 SPS 的 profile；尚无有效参数时不会发布猜测的 profile。收到 answer 但未连接的会话仍会超时，浏览器预览也有连接超时提示。

## 开发验证

```powershell
dotnet build MiCamera.Net.sln
dotnet test tests/MiCamera.Net.Tests/MiCamera.Net.Tests.csproj

Set-Location demo/MiCamera.Net.Sample.Web
npm.cmd install
npm.cmd run lint
npm.cmd test
npm.cmd run build
```
