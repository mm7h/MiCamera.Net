<div align="center">
<img src="./docs/assets/logo.png" alt="MiCamera.Net 摄像头视频桥接图标" width="100" height="100" />

# Mi Camera

<p>

[![.NET](https://img.shields.io/badge/.NET-8.0-7355dd?logo=dotnet)](https://dotnet.microsoft.com/)
[![Issues](https://img.shields.io/github/issues/mm7h/MiCamera.Net)](https://github.com/mm7h/MiCamera.Net/issues)
[![License](https://img.shields.io/github/license/mm7h/MiCamera.Net)](./LICENSE)

</p>

</div>

**MiCamera.Net** 是使用 **.NET 8** 开发的小米摄像头视频桥接 SDK 与服务。它连接 **Miloco** 获取 H.264/H.265 视频，为多个摄像头通道提供 **RTSP 直播、WebRTC 浏览器预览和 JPEG 截图**，附带控制台宿主与 React 前端示例。

## 文档导航 📚

| 我想要了解… | 从这里开始 |
| --- | --- |
| 配置文件结构、字段与迁移 | [⚙️ 配置文件参考](docs/00-配置文件参考.md) |
| 部署服务、授权、升级与备份 | [🚀 桥接服务部署](docs/01-桥接服务部署.md) |
| 项目分层、拉流与媒体处理原理 | [🧩 架构与实现](docs/02-架构与实现.md) |
| HTTP/RTSP 接口与 .NET SDK 接入 | [🔌 接口与 SDK 接入](docs/03-接口与SDK接入.md) |
| 从源码开发、构建镜像和生成完整包 | [🛠️ 源码开发与构建包](docs/04-源码开发与构建包.md) |
| 登录、黑屏、连接失败与卡顿 | [🔍 故障排查](docs/05-故障排查.md) |
| 已验证的部署能力及剩余验证范围 | [✅ 部署验收记录](docs/DEPLOYMENT_VERIFICATION.md) |
| 特定服务器的性能测量与历史分析 | [📊 服务器性能记录](docs/SERVER_PERFORMANCE.md) |

## 快速开始 👋

推荐从源码自行生成完整部署包，再部署到 Linux 服务器；已有 Release 包时可直接下载使用。运行服务器无需安装 Python、Node.js 或 .NET，**Miloco 首次拉取与小米账号授权仍需联网**。

### 一、项目配置

准备与摄像头互通的 **Linux amd64/arm64** 服务器，安装 Docker Engine、Compose v2.20+、Bash 4+、iproute2、awk、coreutils 和 util-linux。建议固定服务器局域网 IP，使用可信家庭网络。

配置采用 `Miloco`、`Streaming`、`Reconnect`、`MediaServer` 四个顶层段，摄像头列表位于 `MediaServer.Rtsp.Streams`。部署向导会生成 `.deploy/MiCameraConfig.json`、独立 secrets 和持久化目录，并引导授权、选流；首次部署无需手工填写带密码的 JSON。

手动配置、SDK 接入或迁移旧部署前，请阅读 [配置文件参考](docs/00-配置文件参考.md)。

### 二、下载镜像

前往 **[GitHub Releases 下载构建包](https://github.com/mm7h/MiCamera.Net/releases)**，选择目标版本的 `micamera-net-<版本>-linux-amd64.tar.gz` 或 `linux-arm64.tar.gz`，同时下载对应 `.sha256`。构建包包含桥接和前端镜像，不包含 **Miloco** 镜像。

**推荐自行构建**：在具备 Python 3、Docker 和 buildx 的构建机器上获取源码，在仓库根目录执行，例如生成 amd64 包：

```bash
python3 deployment/build-package.py local-build --arch amd64
```

产物位于 `deployment/build_output/`。ARM64、双架构构建、Windows 构建及依赖要求见 [完整构建包指南](docs/04-源码开发与构建包.md)。Release 尚无对应资产时使用此方式。

### 三、服务器部署并运行

将归档和校验文件复制到 Linux 服务器。以下以自行构建的 amd64 包为例，版本和架构按实际文件替换：

```bash
sha256sum -c micamera-net-local-build-linux-amd64.tar.gz.sha256
tar -xzf micamera-net-local-build-linux-amd64.tar.gz
cd micamera-net-local-build-linux-amd64
bash deploy.sh
```

跟随中文向导选择 IP，在 `https://服务器IP:8000` 设置 Miloco 本地密码并绑定小米账号；回到终端隐藏输入 **Miloco 本地密码**，选择摄像头后启动服务。

首次打开 **`http://服务器IP:5081`**，填写 RTSP 用户名、密码和确认密码，再启动浏览器预览。设置前 RTSP 端口关闭，保存后立即启用。播放器使用 `rtsp://服务器IP:8554/live/流名称`，选择 **RTSP over TCP**，在认证界面输入刚设置的凭据。前端代理自动附加 API Token，浏览器无需输入。

完整环境、端口、非交互部署、升级与恢复步骤见 [桥接服务部署](docs/01-桥接服务部署.md)。在服务器源码目录直接执行 `bash deploy.sh` 也可本地构建部署，但不会生成完整归档。

## 功能清单 ✨

### 已实现 ✅

| 功能名称 | 说明 |
| :---: | --- |
| 📷 多通道拉流 | 按配置连接 Miloco；各通道独立连接、超时与退避重连，共享上游认证会话 |
| 🎞️ RTSP 直播 | H.264/H.265 原编码直通，支持 Digest 认证，使用 RTP/RTCP over TCP |
| 📺 WebRTC 预览 | 浏览器接收 H.264；H.264 源直通，H.265 源通过 FFmpeg 按需转码 |
| 🖼️ JPEG 截图 | 关键帧解码与缓存截图，HTTP 获取；支持质量和生成间隔配置 |
| 🔐 访问认证 | HTTP Bearer、RTSP Digest、Miloco PEM 证书固定；Docker secrets 与独立 RTSP 凭据持久化 |
| 🔄 会话恢复 | 上游关键帧同步与重连，WebRTC 超时回收，示例前端解码停滞后重建会话 |
| 🌐 浏览器示例 | React/Vite 前端，摄像头状态、预览、截图历史与 RTSP 首次设置 |
| 📦 部署工具 | 三服务 Docker 向导、amd64/arm64 完整包生成、状态检查与配置备份 |

## 注意事项 ⚠️

- **网络**：向导面向可信局域网与原生 Linux rootful Docker。Docker Desktop 可构建包，不能直接套用 LAN 部署。首个网页访问者可初始化 RTSP；不要把默认 HTTP 服务或端口转发到公网。
- **编码**：`Codec` 必须与 Miloco 实际输出一致。RTSP 只支持 TCP；`NominalFrameRate` 是时间信息缺失时的回退值，不会提升摄像头帧率。
- **媒体依赖**：缺少 FFmpeg 原生库时，RTSP 可直通，但 JPEG 与 H.265 WebRTC 转码不可用。H.265 转码使用 CPU，并发与流畅度需按实际硬件验收。
- **个人隐私**：不要提交 `.deploy`、密码、Token、Cookie 或包含秘密的备份。旧静态 RTSP 部署升级后需网页初始化一次，之后重启和更新保留凭据。
- **验证范围**：健康检查不代表真实画面或长期流畅；ARM64 QEMU 检查不等于原生 ARM 验证。具体证据与限制见验收和性能记录。
- **功能范围**：当前提供视频、截图与预览，没有音频、录像、对讲、云台和开箱即用的异地访问。

## 贡献 🙌

欢迎通过 [Issues](https://github.com/mm7h/MiCamera.Net/issues) 反馈问题或提交改进。

开始前阅读 [源码开发与构建包](docs/04-源码开发与构建包.md)；代码变更提交前运行 .NET Release 构建、前端 lint 与生产构建，部署文件变更另做 Bash/Compose 检查。

问题反馈请注明版本或提交、服务器架构、部署方式、摄像头型号/编码、复现步骤，以及脱敏状态和日志。媒体问题说明实际播放时长、帧率、丢包与停顿；不要公开设备 DID、账号数据或访问秘密。排查清单见 [故障排查](docs/05-故障排查.md)。

## 特别鸣谢 ❤️

| 项目 / 组件 | 用途 |
| :---: | --- |
| [micam](https://github.com/miiot/micam) | 小米摄像头接入的参考项目，以及部署使用的 miiot/Miloco 社区镜像相关实现 |
| [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) | WebRTC peer、ICE、DTLS、SRTP 与 RTP/RTCP 基础能力 |
| [SharpRTSP](https://github.com/ngraziano/SharpRTSP) | 项目使用的 RTSP/RTP 相关依赖 |


## 许可证 📝

[MIT License](./LICENSE)
