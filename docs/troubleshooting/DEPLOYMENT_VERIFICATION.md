# 一键部署验收记录

[← 返回首页](../README.md) · [当前部署指南](01-桥接服务部署.md) · [故障排查](05-故障排查.md)

> 📚 本页保留截至 **2026-10-05** 的历史验收证据与失败结果。镜像 ID、测试数量、服务器状态和路径描述仅适用于对应日期，不表示最新源码或 Release 已完成同等验证；历史测试入口已移除。操作步骤请使用当前专题指南，本次文档重整没有重新执行生产部署、真机播放或原生 ARM 验收。

> 2026-10-05 更新：按用户明确要求，已移除全部测试源码（含前端回归、deployment/tests 和测试专用程序集访问/媒体自检入口），并取消测试依赖及构建中的测试命令。下文旧测试路径、命令和镜像标识均为历史记录，不是当前运行说明。当前部署与本轮证据以 [Docker 部署说明](DOCKER_DEPLOYMENT.md) 和部署验收记录中的“RTSP 网页初始化与预构建包”一节为准。

## RTSP 网页初始化与预构建包（2026-10-05）

首次设置界面已更新为原生遮罩弹窗，禁止跳过，包含 RTSP 播放器认证用途和凭据保存说明；表单与三个字段均禁用自动填充，并添加 LastPass/1Password 忽略标记。lint、TypeScript、生产构建通过，服务器静态资源和两个体验包已同步。两个包的内外 SHA-256 及部署文件白名单复核通过；Docker Engine 实际加载了包使用的两镜像归档格式。

版本：`20261005-rtsp-setup`。服务器 `192.168.3.104` 从 amd64 体验包部署，实际完成校验、`docker load` 和 `--no-build` 启动；包内没有源码或开发工具。Miloco 沿用原有固定镜像和有效的小米授权。

| 检查 | 结果与范围 |
| --- | --- |
| 首次设置 | 设置前 RTSP 不监听；拒绝空白、非法用户名、重复设置；并发提交只有一次成功；端口冲突不提交文件；损坏的持久化凭据使启动失败 |
| 重启持久化 | 隔离候选使用自行指定的临时凭据保存，重启后仍能实际 RTSP Digest/TCP 解码；不初始化生产凭据 |
| Linux amd64 | 最终桥接、前端镜像启动；H.264/H.265 合成上游、JPEG 解码、RTSP 实际帧解码、WebRTC offer/释放通过；媒体缺失降级也已检查 |
| 真实摄像头 | 独立候选挂载已有授权，验证两路 JPEG 解码、使用选定凭据的 RTSP 实际帧解码、WebRTC offer/释放及重启；未改变生产设置 |
| Linux arm64 | 自包含发布和目标架构镜像构建通过；最终镜像在 QEMU 中通过双编码收帧、JPEG、RTSP 解码、WebRTC 协商和前端 HTML 检查；不是原生 ARM 机器证明 |
| 移除测试后构建 | .NET Release 编译 0 警告、0 错误；前端 lint、TypeScript 和生产构建通过；锁文件已移除 Vitest、jsdom、Testing Library；镜像构建取消 `npm test` |
| 生产升级 | 三容器均健康；两路媒体就绪；空设置请求返回 400；状态为 `configured=false/listening=false`；8554 关闭；等待用户首次网页填写 |
| 权限及备份 | `.deploy` 为 0700；`rtsp-state` 为 10001 所有、0700，创建凭据文件为 0600。升级前完整状态备份为服务器 `/home/hang/micamera-net-backup-20261005/deploy-state.tar.gz`（0600），另有配置迁移备份 |
| 镜像清理 | 已移除旧桥接/前端标签与本轮旧候选镜像；保留当前 amd64 生产镜像、Miloco 和其他项目镜像；临时 ARM 模拟注册及工具镜像已移除 |

当前运行桥接镜像为 `sha256:91d9e26d6bae04199faaa48b8109709005a421ca2320812436c60318442881d9`，前端为 `sha256:61823f5fe90e0e0f7146024151ff581c6e81d14550f6d6d3b30f03003f0a057b`。ARM64 包内镜像分别为 `sha256:119cb7846d344808e5580805ee5d8c03a19f7761de5ab70161c450e032321a69` 和 `sha256:7d980d0c37277f301e4367ba5f80c8a0aba30791c2b9c7f6d1e0b930b0a8337a`。

两个最终归档位于 `deployment/build_output/`：amd64 303.8 MiB、arm64 292.6 MiB，各有独立 `.sha256`。下载后已校验外层归档及包内全部文件的 SHA-256、架构和文件白名单；Compose 不含源码构建定义。归档不含源码、测试、开发依赖、`.deploy` 或用户凭据。服务器本轮构建/验收临时目录已清理，生产目录内的旧测试源码也已移除。

本轮浏览器自动化多次超时，未取得网页渲染或浏览器实播证据；确认了服务器提供最终初始化页面资源及前端回归检查通过。没有重新进行长时间浏览器播放或主机重启检查。RTSP 新用户名和密码留给用户填写，升级前凭据不再生效。以下内容为旧版历史验证。


更新日期：2026-09-30。此记录对应当前工作区实现，不代表已发布版本，也不是原生 Linux、真实 Miloco 或真机兼容性证明。早期结果保留为历史基线；外置配置、文件型 secrets 与 Miloco PEM 证书固定变更后的复验见“Docker Desktop 重试”末尾。

## 已执行的本地验证

初次验证环境为 Windows、.NET SDK 9（项目目标仍为 .NET 8）、Node.js 24.19.0、Python 3（原记录未注明具体版本）、Git Bash、ShellCheck 0.11.0 和独立 Compose 2.39.4。使用兼容 Node 运行 npm CLI，避免本机默认 Node 低于锁定依赖的要求；当时没有 Docker Engine。用户随后配置 Docker Desktop，新增验证见下一节；测试没有改变系统防火墙。

当前本机使用 **Python 3.13.2**（2026-10-06 通过 `python --version` 确认）；当前打包环境见 [源码开发与构建包](../04-源码开发与构建包.md)。

| 验证 | 结果 | 证明范围 |
| --- | --- | --- |
| `dotnet build MiCamera.Net.sln` | 通过，0 警告、0 错误 | 当前后端及示例能编译 |
| `dotnet test tests/MiCamera.Net.Tests/MiCamera.Net.Tests.csproj --no-restore` | 26 项通过 | 包括认证业务状态、Cookie 复用、错误脱敏、监听安全、会话释放/过期、健康接口、缺少媒体能力响应，以及 HTTP/WebSocket 对同一 Miloco PEM 的精确证书固定 |
| npm `ci`、`run lint`、`test`、`run build` | 通过，15 项测试 | 前端能构建；会话超时、停止、候选项顺序和旧请求不覆盖新预览等行为 |
| Python `-m unittest discover -s deployment/tests -v` | 12 项通过 | 凭据持久化、密码输入哈希、数字秒数、授权状态解析、配置暂存/回退、唯一性、文件型 secret 与非交互部署状态预检 |
| `bash -n deploy.sh`、`bash deployment/tests/test_deploy.sh` | 通过 | Shell 语法、非交互输入失败、LAN 地址过滤、端口冲突、有限重试、停用保留数据和新增配置失败回退；Docker 调用为模拟 |
| `shellcheck deploy.sh deployment/tests/test_deploy.sh` | 通过（初次环境） | Shell 静态检查；本次 Windows 环境未安装该命令，未将其重复执行结果标记为通过 |
| `deployment/tests/validate_compose.py` 配合独立 Compose 可执行文件 | 通过 | 实际 YAML 可解析；三服务、镜像摘要、网络、挂载、健康检查和日志配置符合断言；未连接 Engine |
| `.NET` 自包含 `linux-x64`、`linux-arm64` 发布 | 均通过 | 两个 Linux RID 能生成发布产物；没有运行产物或构建镜像 |
| `git diff --check` | 通过 | 已跟踪修改没有空白错误 |

前端新增四项测试曾分别复现旧失败清理覆盖新预览、旧停止清理覆盖新预览、无效 offer 未主动删除会话，以及旧自动播放拒绝覆盖新状态；修复后均通过。上述测试不验证真实浏览器的媒体网络。

## Docker Desktop 重试

已确认 Docker Desktop 4.93.0、Engine 29.8.1、Compose 5.5.1，容器为 Linux/amd64。本次使用独立 `verify-amd64`/`verify-arm64` 镜像标签，没有修改生产 `.deploy` 状态。

- amd64 前端与桥接 Dockerfile 均实际构建成功；前端在镜像内运行完整 lint、15 项测试及生产构建。
- 桥接镜像构建时，以及默认非 root 用户 10001 运行时，真实 FFmpeg 解码/JPEG/H.265→H.264 自检通过。
- `docker_smoke.py --bridge-image micamera-net-bridge:verify-amd64 --web-image micamera-net-web:verify-amd64` 通过：模拟上游登录/Cookie、H.264/H.265 二进制 WebSocket 收帧、匿名 HTTP 拒绝、匿名 RTSP Digest 挑战、两类视频 JPEG 解码、RTSP TCP 实际帧解码、H.264 WebRTC offer/ICE 候选/删除，以及 Nginx 健康页和前端 HTML。
- 同一命令加 `--without-media` 通过：强制不存在的 .NET FFmpeg 原生库路径，确认后端存活、两类 RTSP 仍能解码、JPEG/H.265 WebRTC 返回 503、H.264 offer 仍可创建；CLI FFmpeg 只作为测试客户端。
- ARM64 前端首次在 QEMU 中运行测试时 worker 启动超时，不能作为通过。构建阶段改为 `BUILDPLATFORM` 原生 Node，仍执行完整 lint/test/build，最终 Nginx 保持目标 ARM64；重新构建成功。
- ARM64 桥接镜像实际构建成功；默认非 root 用户下运行原生媒体自检通过。显式指定 `--platform linux/arm64` 的完整合成视频冒烟测试也通过，包括双编码收帧、JPEG 解码、RTSP Digest/TCP、H.264 WebRTC offer/清理和前端 HTML/健康页。这些 ARM64 运行使用 Docker Desktop QEMU 模拟，不等于原生 ARM 服务器验收。
- 测试容器和网络已清理，验证镜像和 Docker 构建缓存保留。未获取或存储任何小米账号密码，没有拉取/运行真实 Miloco。

在上述基线之后，重新构建了 amd64 `micamera-net-bridge:verify-amd64` 和 `micamera-net-web:verify-amd64`，并实际复验：

- 桥接镜像中不存在 `/app/Configs/MiCameraConfig.json`；运行必须挂载外置配置。
- `dotnet build MiCamera.Net.sln --no-restore` 为 0 警告、0 错误；26 项 .NET 测试、12 项 Python 测试、Bash 控制流测试和 Compose 静态验证全部通过。
- 两次 `docker_smoke.py`（正常媒体与 `--without-media`）均通过。测试秘密仅作为只读 `*_FILE` 挂载给合成上游与桥接服务；证明合成协议、RTSP、WebRTC 和媒体降级，不证明真实 Miloco、证书链或摄像头。

Windows 测试工具还修复了 Docker UTF-8 输出被系统默认 GBK 解码的问题。部署向导新增 Desktop 引擎识别：Linux/WSL Shell 不能证明容器能绑定 LAN IP，参见 [Docker 官方限制](https://docs.docker.com/engine/network/drivers/host/)。本机 Ubuntu 的 Docker CLI 也明确报告 WSL 1 不支持当前 Desktop 集成；未自动转换其版本或改变网络设置。

## 需求与剩余验收

| 需求 | 当前证据 | 仍需验证 |
| --- | --- | --- |
| 一个命令部署三个常驻服务 | `deploy.sh`、`docker-compose.yml`、控制流程及 Compose 检查；Desktop amd64/arm64 本地镜像构建/隔离运行通过 | 原生 Linux 上三服务向导、真实 Miloco 和双架构运行 |
| 无 OpenClaw、micam/go2rtc 常驻服务、AI 或 GPU 要求 | Compose 仅定义 Miloco、桥接、前端；使用固定基础版 Miloco | 固定社区镜像实际启动行为及其内容 |
| 中文交互、隐藏输入、账号关联 | 向导源码；Miloco 登录/授权检查及配置测试 | 浏览器首次设置本地密码、绑定本人小米账号、授权失效后恢复 |
| JPEG、RTSP 和 WebRTC 播放 | amd64/arm64 媒体自检及合成视频 JPEG/RTSP/offer 通过；amd64 缺少原生库降级测试通过 | 真实摄像头的三种输出及浏览器实际画面 |
| H.264 与 H.265 | 双编码处理代码、媒体能力诊断、H.265 转码自检定义 | 两类摄像头各至少一台；实际分辨率、帧率及 CPU/内存记录 |
| 多客户端、重连和重启恢复 | 会话释放/过期测试；持久化配置及重启策略 | 两个真实客户端、网络中断恢复、容器和主机重启后恢复 |
| 密钥、账号资料和访问安全 | 凭据生成/复用测试、外置只读配置、Compose 文件型 secrets、Bearer/Digest 校验，以及 HTTP/WebSocket Miloco PEM 固定测试 | Linux 文件权限、真实 Miloco 证书轮换、真实未授权请求拒绝、局域网防火墙范围及日志脱敏 |
| 第三方许可 | `deployment/THIRD-PARTY-NOTICES.md` 和向导许可提示 | 部署者确认所需使用授权；输入 `YES` 不构成许可授予 |

不把“Compose 配置有效”“容器进程存活”“后端媒体就绪”“浏览器收到画面”当作同一个结果；后面的每一层都需要单独证据。

## Linux 验收时的反馈

按 [部署说明](DOCKER_DEPLOYMENT.md) 运行 `bash deploy.sh`。失败后可运行 `bash deploy.sh status`、`bash deploy.sh logs`，先脱敏再提供输出；不要提供 `credentials` 输出、`.deploy` 文件、小米密码、授权 Cookie、Token、JWT 密钥或 SSH 私钥。

记录以下信息即可：

- Linux 发行版、CPU 架构、Docker/Compose 版本和首次构建是否成功。
- 失败阶段、退出码及脱敏错误；健康状态是否通过。
- 摄像头型号与 H.264/H.265 编码，不需要 DID 或账号信息。
- JPEG 是否可显示、RTSP TCP 是否有画面、浏览器及版本、WebRTC 是否实际播放。
- 两客户端、网络重连、服务/主机重启结果，以及连续运行时长和资源占用。

当前未完成上述 Linux 运行和真机验收，整体部署目标仍待验证。
