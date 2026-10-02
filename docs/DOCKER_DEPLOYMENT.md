# Linux Docker 部署与验收

## 边界与前提

三项常驻服务：社区基础版 Miloco、MiCamera.Net 桥接服务、Nginx 前端。Miloco 保留上游基础后台和授权 UI，不另行裁剪源码；不启动 AI 模型、OpenClaw、Python micam 或 go2rtc。首版仅视频、截图和浏览器预览，没有音频、录像、对讲、云台或异地访问。

服务器、摄像头和浏览器应在同一可互通家庭局域网；访客 Wi-Fi、AP 客户端隔离及 VLAN 防火墙可能导致失败。需要联网下载 Docker、NuGet、npm、Debian 依赖，并访问小米授权服务；不是离线安装包。H.265 浏览器预览使用 CPU 转码，不承诺任意服务器的并发路数。

支持目标为 Linux amd64/arm64、本机 rootful Docker Unix socket；不支持远程 Docker context、rootless Docker、Windows Docker 或未配置媒体网络的云服务器。安装 Docker Engine 和 Compose v2.20+；脚本使用 Bash 4+、iproute2（ip/ss）、awk、coreutils、util-linux（flock）。缺少工具时脚本提示，不自动安装系统软件或修改防火墙。

Docker Desktop 的 Linux 容器可以用于构建和隔离的镜像测试，但不能直接套用本向导的 LAN IP 绑定。即使在 WSL 的 Linux Shell 中运行，也不等于原生 Linux host 网络；向导会识别 Desktop 并明确退出。[Docker 官方 host 网络说明](https://docs.docker.com/engine/network/drivers/host/)指出 Desktop 容器不能直接绑定宿主机网卡 IP。本项目不自动转换 WSL 版本或修改 Desktop 网络设置。

使用前审阅 [依赖许可说明](../deployment/THIRD-PARTY-NOTICES.md)，特别是 Miloco 的用途限制和 SIPSorcery 的附加限制。向导的确认不是授权证明；个人非商业部署也不自动代表许可允许此集成。许可不明确时先取得澄清。

## 启动向导

```bash
cd /实际的源码目录/MiCamera.Net
bash deploy.sh
```

也可在 Linux 上执行 `chmod +x deploy.sh` 后使用 `./deploy.sh`；源码由 Windows 上传时保持 Shell 文件为 LF，默认的 `bash deploy.sh` 不依赖执行位。

1. 选择本机 RFC1918 IPv4 地址。建议在路由器中固定 DHCP 租约。
2. 核对许可后输入 `YES`。构建前端与 .NET 8 自包含镜像；桥接镜像内包含 FFmpeg 7.1 兼容原生库。
3. 在自己的电脑浏览器打开向导给出的 `https://服务器IP:8000`，确认这是本机 Miloco 后处理自签名证书提示，设置本地密码并绑定小米账号。脚本不会尝试打开 SSH 服务器的浏览器。
4. 隐藏输入刚设置的 **Miloco 本地密码，不是小米账号密码**。脚本生成小写 MD5，分别检查本地登录和 `data.is_logged_in`。失败可重新检查授权、重新输入密码或退出后续跑。
5. 从 Miloco 返回的摄像头列表选择设备，或手工输入 DID。默认通道 0、H.265、名义帧率 30；按实际摄像头输出选择编码。设备离线不等于不能配置，但就绪检查不会通过。
6. 校验配置并启动桥接服务，检查近期视频和截图状态。打开 `http://服务器IP:5081`，在终端执行 `bash deploy.sh credentials`，将显示的 API Token 输入前端后验证播放。

服务输出不含自动打印的凭据；`credentials` 只允许交互终端显式显示 Token 和 RTSP 凭据，不显示 Miloco 密码。前端 Token 只存在浏览器内存中，刷新后重新输入。桥接镜像不包含示例 JSON，运行时必须获得向导创建的外部只读配置和 secrets；入口仅在容器内将它们合成为权限 0600 的临时 JSON，并以该路径启动应用。

初始化未完成或视频检查失败，脚本退出非零，但保留已有服务、授权和配置；再次执行可以继续。不要把脚本用 `sudo`/普通用户交替运行，以免配置目录的文件所有权不一致。

## 非交互部署

自动化系统使用以下命令，而不是在命令行或 Compose 环境变量中传递秘密：

```bash
bash deploy.sh up --non-interactive
```

运行前必须由受控的配置管理系统预置 `.deploy`：`deployment.env`（只包含 `LAN_IP`）、`settings.json`、`license-ack`、`MiCameraConfig.json`、`miloco/cert/cert.pem` 和 `secrets/` 下的全部文件。`MiCameraConfig.json` 至少有一条流，`Miloco.Password` 必须为空，用户名必须为 `admin`，`AllowInvalidServerCertificate` 必须为 `false`，并指定 `/run/configs/miloco-server-cert.pem`。`secrets/` 需要 `miloco_password_md5`（32 位小写 MD5）、`miloco_jwt_secret`（64 位小写十六进制）、`rtsp_api_token`（64 位）和 `rtsp_password`（48 位）。

该模式不能自动登录小米账号：若持久化的 Miloco 数据尚未授权，脚本在网页授权检查处退出非零；管理员完成网页操作后重新执行同一命令。旧版 `bridge.env`、`miloco.env` 凭据布局不兼容，脚本会拒绝启动且不会迁移或删除它们。先完成受控备份，移除旧文件，然后重新初始化或预置新布局。

## 地址与网络

| 服务 | 默认地址 | 认证/用途 |
| --- | --- | --- |
| Miloco | `https://服务器IP:8000` | Miloco 本地登录及小米账号绑定 |
| HTTP API | `http://服务器IP:5080` | Bearer Token |
| 前端 | `http://服务器IP:5081` | 静态页面；不内置 Token |
| RTSP | `rtsp://服务器IP:8554/live/流名称` | Digest；仅 RTP over TCP |
| WebRTC 媒体 | UDP 50000–50100 | 与浏览器进行 ICE/DTLS/SRTP |

仅向可信局域网允许上述 TCP/UDP 端口，禁止路由器公网转发。脚本不修改防火墙；Miloco 与桥接服务采用 host 网络，因此 host 服务没有 Compose `ports` 映射。前端映射到选定的 LAN IP。

RTSP 客户端必须启用 TCP，并通过播放器的认证界面提供用户名/密码。Docker 管理员和能读取部署目录的管理员仍可读取凭据；HTTP Token 在本首版的可信 LAN 明文 HTTP 上传输，不适合不可信网络或公网。Miloco 的自签名证书由其持久化目录生成；桥接容器以只读方式挂载并固定信任这一张 PEM 证书，HTTP 和 WebSocket 都不会接受其他证书。浏览器访问 Miloco 页面仍需为该局域网地址处理自签名证书提示；不得将此例外扩展到任意远端。

## 日常操作与恢复

```bash
bash deploy.sh              # 重用配置，重建镜像并启动
bash deploy.sh add-camera   # 添加通道；成功校验后重建桥接容器
bash deploy.sh status       # 容器状态 + 媒体就绪状态
bash deploy.sh logs         # 最近 100 行日志，不持续阻塞终端
bash deploy.sh stop         # 仅停止，保留授权和配置
bash deploy.sh build        # 只构建本地镜像，不启动服务
bash deploy.sh credentials  # 仅在交互终端显式显示访问凭据
bash deploy.sh up --non-interactive # 使用预置的 .deploy 状态启动
```

添加摄像头时 `StreamId`（忽略大小写）和 `CameraDeviceId + Channel` 必须唯一。新配置先写暂存文件，经容器运行时配置合成校验后原子替换；保留上一份配置。桥接重建会中断当前播放，新增流未在 120 秒内就绪时恢复原配置并重建桥接服务，不重启 Miloco。首次视频失败保留新配置供排查，不回退为空流。

`.deploy` 被 Git 和 Docker 构建上下文排除，权限 0700。其主要内容：

- `deployment.env`、`settings.json`：部署地址。
- `secrets/`：`miloco_password_md5`、`miloco_jwt_secret`、`rtsp_api_token`、`rtsp_password`，每个文件权限 0600；Compose 仅将所需文件挂载给对应服务。
- `MiCameraConfig.json`：不含密码的完整运行配置，时长为数值秒数；作为只读 Compose config 挂载。入口将其与三个 bridge secret 合成为临时 JSON 后，以该文件路径启动应用。它固定引用 `/run/configs/miloco-server-cert.pem`，不能恢复 `AllowInvalidServerCertificate: true`。

  媒体服务配置统一位于 `MediaServer`：HTTP 使用 `ListenAddress`、`BearerToken` 和 `AllowedOrigins`，子对象为 `Rtsp`、`WebRtc`、`Snapshot` 和 `FFmpeg`（原生库目录为 `Path`）。摄像头流列表位于 `MediaServer.Rtsp.Streams`，同时供 RTSP、WebRTC 和截图使用。已有部署须按 README 的迁移说明调整 `.deploy/MiCameraConfig.json`；向导不会覆盖已有配置，旧顶层媒体配置和顶层 `Streams` 会被拒绝。
- `miloco/`：Miloco 数据库、`cert/cert.pem` 和小米账号授权资料；桥接仅以只读方式挂载该证书，部分文件由 Miloco 容器用户创建。
- `MiCameraConfig.previous.json`：最近一次配置替换前的副本。

变更 Miloco 本地密码后重新运行向导并输入新密码。小米授权失效后在 Miloco 页面重新绑定；桥接会重试，脚本也会报告未授权。若网页全局退出登录导致已有 Cookie 失效，桥接应重新认证。

备份前停止服务，以有权读取 `miloco/` 的管理员身份备份整个 `.deploy`，确保备份文件仅管理员可读。备份包含访问秘密，不上传公开仓库或工单。恢复时同时恢复授权数据、JWT 密钥和访问凭据，保持原文件权限。

服务器 IP 变化时先停止服务并备份，然后核对本机新 IP，更新 `.deploy/deployment.env` 的 `LAN_IP`、`settings.json` 的 `lanIp`，以及 `MiCameraConfig.json` 中的 `Miloco.BaseUrl`、`MediaServer.Rtsp.ListenAddress`、`MediaServer.ListenAddress`、`MediaServer.AllowedOrigins` 和 `MediaServer.WebRtc.BindAddress`，再运行向导。保留 `TrustedServerCertificatePath` 不变；前端访问地址及 CORS 随新 IP 更新；已打开的浏览器需重新连接。脚本检测旧 IP 不存在会拒绝继续，不静默选择另一网卡。

默认端口冲突时先处理冲突，不静默换端口。自定义端口需要同时调整 Compose、前端 API 地址、CORS 和防火墙；本首版向导不提供端口迁移菜单。停用不删除数据；不得为了修复启动故障直接清空授权目录。

## 镜像、日志与升级

Miloco 固定为社区镜像 `ghcr.io/miiot/miloco:260618@sha256:1627132b4364feea0a60d2c880bacdbea614904a04ec2d4d457143d2fb5f7b68`，不是官方 Xiaomi 发布镜像。固定版本必须继续匹配旧版本地认证、`NormalResponse` 和二进制视频 WebSocket 接口，不能直接换成新的 OpenClaw 插件接口。首次构建失败时检查到 GHCR、MCR、npm、NuGet、Debian 仓库的网络访问，不自动切换未经核对的镜像。

三个服务的 Docker 日志限制为每份 10MB、3 份。Miloco 启动包装只关闭上游无限增长的文件日志，保留控制台输出；如果固定镜像的旧配置模块不再匹配，启动应失败并排查，而非悄悄使用其他协议。

更新源码后向导重建本地前后端镜像；此前端 Node 构建阶段运行 lint、test、build。升级 Miloco 必须另行核对新版本协议和许可证，并先备份 `.deploy`。回退时恢复已验证的镜像摘要和升级前数据，不假定数据库可以降级。

## 检查层级与验收

`/api/health/live` 沿用 Bearer 认证，只检查后端响应，摄像头离线不会导致健康检查反复重启服务。进程退出由 Docker 重启策略处理，Miloco断连由核心重连处理。`/api/health/ready` 未就绪返回 503，输出逐流收帧、截图和 WebRTC 能力；不是浏览器端到端检查。

构建镜像时生成不含用户数据的 64×64 H.264/H.265 样本，并通过真正的 .NET FFmpeg 原生处理验证解码、JPEG 和 H.265→H.264 输出。没有 FFmpeg 或原生能力不足时仍保留 RTSP 直通，截图/转码明确降级；完整部署就绪检查不会误报成功。

提交前检查：

```bash
dotnet build MiCamera.Net.sln
dotnet test tests/MiCamera.Net.Tests/MiCamera.Net.Tests.csproj
python3 -m unittest discover -s deployment/tests -v
bash -n deploy.sh
bash deployment/tests/test_deploy.sh
shellcheck deploy.sh deployment/tests/test_deploy.sh
docker compose --env-file .deploy/deployment.env config --quiet
```

已构建镜像可另行运行隔离的容器冒烟测试（需要测试机安装 Python 3，实际部署仍不需要主机 Python）：

```bash
python3 deployment/tests/docker_smoke.py
python3 deployment/tests/docker_smoke.py --without-media
```

测试使用镜像内的合成黑色视频和模拟 Miloco 协议，检查鉴权、HTTP 健康状态、JPEG 解码、RTSP Digest/TCP 帧解码、H.264 WebRTC offer/清理，以及缺少原生库时的降级；只验证前端 HTML/健康页，不验证浏览器播放。它不绑定小米账号，不发布主机端口，不拉取/运行真正的 Miloco，结束时清理本次临时容器和网络。可通过 `--bridge-image`、`--web-image` 指定已构建的验证镜像，跨架构运行显式传入 `--platform linux/arm64` 或 `--platform linux/amd64`。不能据此宣称真实 Miloco 或摄像头已兼容。

Linux amd64/arm64 都必须另行实际构建并启动镜像。真机验收至少包括 H.264 和 H.265 各一台、RTSP TCP 播放、有效 JPEG、浏览器实际画面、两个客户端、网络断连重连、服务重启和主机重启后恢复。记录分辨率、帧率、CPU/内存、连续运行结果和失败型号；不能用一个型号支持声明覆盖全部小米摄像头。

Docker Desktop 上已补充真实容器测试，原生 Linux LAN 和真机端到端验收仍未完成。依赖许可及社区镜像实际内容仍需在部署环境确认。实际已执行的检查及其证明范围见 [部署验收记录](DEPLOYMENT_VERIFICATION.md)。
