# MiCamera.Net Jenkins 部署

四个独立 Job 位于 `AI Agent Practice/MiCamera.Net`，默认只手动运行：

| Job | SCM Script Path | 作用 |
| --- | --- | --- |
| Frontend | `deployment/jenkins/Jenkinsfile.web` | 构建并更新 `web` |
| Backend | `deployment/jenkins/Jenkinsfile.bridge` | 构建并更新 `bridge` |
| Miloco | `deployment/jenkins/Jenkinsfile.miloco` | 导入固定摘要的上游镜像并更新启动适配器 |
| Reset | `deployment/jenkins/Jenkinsfile.reset` | 将三个服务的应用配置和凭据恢复到首次初始化状态 |

构建在102执行，104只导入和运行镜像。目标固定为 Linux amd64、`hang@192.168.3.104`、`/home/hang/micamera-net`，Compose 项目为 `micamera-net`。脚本要求已有正常部署，不负责首次安装 Docker、迁移数据库或更新基础 Compose。

## 接入顺序

1. 审查并将本目录和两个 Dockerfile 的改动发布到私有仓库 `develop`。本任务脚本不会提交或推送源码。
2. 补齐 Jenkins 管理访问，在102配置下述构建节点、GitHub 主机密钥验证和文件夹凭据。
3. 运行 `configure.py` 检查环境，再使用 `--apply` 创建四个 SCM Job。已有同名 Job 不会被覆盖。
4. 先依次运行 Frontend、Backend、Miloco，核对镜像、健康状态和实际视频播放。不要把 Reset 纳入验证发布的自动联动。

## 102 构建节点

使用一个在线节点，名称和标签均为 `micamera-build`，一个 executor，工作目录 `/home/hang/jenkins-agent`，采用 inbound agent 启动方式。节点必须在102，并连接102本机 Linux Docker Engine，不能连接104的 Docker daemon。

当前102为 Ubuntu，使用原生 systemd agent，无需额外构建 agent 容器。在102安装构建依赖：

```bash
sudo apt-get install -y docker.io docker-buildx git python3 openssh-client
```

Jenkins创建 inbound 节点后，从 `/jnlpJars/agent.jar` 下载其 agent，并把连接 secret 存入 `/home/hang/jenkins-agent/agent.secret`，目录权限0700、secret权限0600，由 `hang` 持有。不要把值写入仓库或命令历史。然后安装本目录的 systemd unit：

```bash
sudo install -m 0644 deployment/jenkins/micamera-jenkins-agent.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now micamera-jenkins-agent.service
```

节点需具备 Git、Python 3.10+、SSH/SCP、Docker CLI/buildx，以及访问 GitHub、基础镜像、NuGet、npm 和 Debian 软件源的能力。不需要宿主机 .NET 或 Node.js。

## 凭据与 SSH 主机验证

在 `AI Agent Practice/MiCamera.Net` 文件夹范围配置以下凭据：

| Credentials ID | 类型 | 内容 |
| --- | --- | --- |
| `micamera-github-readonly` | SSH Username with private key | 用户 `git`，MiCamera.Net 仓库专用只读 Deploy Key 私钥 |
| `micamera-deploy-104` | SSH Username with private key | 用户 `hang`，获准登录104及使用 Docker 的独立部署密钥 |
| `micamera-deploy-known-hosts` | Secret file | 经核验的104 SSH known_hosts 文件 |

GitHub 仓库 Settings → Deploy keys 中添加对应公钥，不勾选写权限。Jenkins Git Host Key Verification 使用已核验 GitHub SSH 主机密钥，不能选择不验证；可通过 GitHub 官方 API `https://api.github.com/meta` 核对公布的 `ssh_keys`。

104的主机密钥必须与实际服务器核对后保存。运行脚本强制 `StrictHostKeyChecking=yes`，不会自动接受新主机密钥。Jenkins 管理账号/API Token 不传入业务 Pipeline。

## 创建 Job

离线导出可审查的 Job XML：

```bash
python3 deployment/jenkins/configure.py --export deployment/build_output/jenkins/job-configs
```

通过受保护环境设置 `JENKINS_USER`、`JENKINS_API_TOKEN`（或 `JENKINS_PASSWORD`）后执行：

```bash
python3 deployment/jenkins/configure.py
python3 deployment/jenkins/configure.py --apply
```

脚本通过 HTTP Basic 认证，并自动取得 session crumb；不会禁用 CSRF。它检查插件、父文件夹和在线节点，不安装插件、不重启 Jenkins，也不触发构建。若手工配置 Job，选择 Pipeline script from SCM，仓库 `git@github.com:mm7h/MiCamera.Net.git`、凭据 `micamera-github-readonly`、分支 `*/develop`，Script Path 按表填写。首次使用 Reset 时即可看到空的 `CONFIRM_RESET` 参数。

## 发布行为

前后端分别执行依赖恢复、源码构建和最终镜像构建；Dockerfile 的 `dependencies`、`build` 阶段复用 BuildKit 缓存。前端执行 npm ci、lint、TypeScript/Vite 构建；后端执行 .NET 8 restore、自包含 Release publish。Miloco跳过这两步，按仓库固定上游摘要拉取并赋予本次构建标签。

102直连 GHCR 的大镜像层下载很慢。Miloco先通过 `ghcr.nju.edu.cn` 按完全相同的 SHA256 摘要预热镜像层，再从原始 GHCR 地址确认并拉取固定镜像；代理失败时回退到原始地址。Docker校验各层摘要，原始上游地址仍记录在 manifest 中。

镜像标签为 `micamera-net-<service>:<Git短SHA>-<BUILD_NUMBER>-amd64`。归档包含镜像、完整 Git SHA、镜像 ID、平台、SHA256 校验和；Miloco额外包含适配器和上游摘要。镜像通过 SSH/SCP 上传，不使用镜像仓库中转。Jenkins只归档无凭据的 manifest 和 SHA256SUMS。

102当前使用 Docker 29/containerd 镜像存储，104使用 Docker 28/classic，两者的 inspect ID 表示不同。manifest 的 `source_image_id` 保留构建端 ID，`image_id` 使用归档内镜像配置的 SHA256，与104导入后的 ID 比较；不以构建端 manifest/index ID 判断导入失败。

104状态位于 `.deploy/ci/`：

- `incoming/<service>-<version>/` 保存上传文件、本次 Compose override、回退 override 和成功记录；Miloco的当前适配器直接挂载其中的版本文件。
- `current.json` 记录最近成功发布的三个镜像与适配器覆盖项。每次发布以实际运行容器为基线，避免覆盖手工部署后的镜像。
- `backups/reset-<version>/` 保存 Reset 的旧应用状态，包含秘密，仅留在104。

发布在现有 `.deploy/.lock` 上最多等待10分钟，随后仅更新当前服务，不调用 Compose down 或重建其他服务。启动验证失败时回退该服务的镜像及适配器。当前镜像及回退镜像不自动删除，禁止全局 prune。历史归档和备份需在确认不被当前挂载引用后由管理员清理；Jenkins构建记录保留最近10次。

启动其他维护命令时也应使用 `current.json`，避免基础 Compose 默认标签覆盖 Jenkins 版本：

```bash
cd /home/hang/micamera-net
docker compose -p micamera-net --project-directory "$PWD" \
  --env-file .deploy/deployment.env -f docker-compose.yml -f .deploy/ci/current.json ps
```

`deploy.sh` 不会自动读取 Jenkins override，Jenkins接管后不要直接用它重新发布。镜像回退不负责数据库格式迁移；需要迁移时应另行制定数据兼容方案。

## Reset

只有 `CONFIRM_RESET` 精确等于 `MiCamera.Net` 才执行。Reset 不构建或替换镜像，不更改部署 IP、端口、许可确认、CI 历史和部署锁。

同一远程事务持有部署锁，依次停止三个服务、把活动应用状态移入权限受限的备份、复用 `helper.initialize` 生成默认配置和新 secrets、启动 Miloco/Web、等待新证书、启动后端并检查未配置状态。把这些操作放在同一个 Jenkins stage 中，可保证锁和失败恢复覆盖整个过程；控制台显示各子步骤进度。

重置内容包括 SQLite 摄像头/RTSP/Miloco设置、Miloco本地 PIN/授权资料/证书、API Token、JWT secret、所有顶层 `MiCameraConfig*` 文件及旧版 `cameras.json`、`rtsp-state`、凭据 env。历史备份不参与新服务运行，也不会被删除。Reset 清除的是104上的本地小米授权资料，不撤销小米云端的账号或设备绑定。

成功标准：三个容器 healthy，后端 live 正常，`/api/setup` 返回 `configured: false`、`listening: false`，ready 返回503且 `ready: false`。随后重新设置 Miloco PIN、授权小米账号，在前端选择摄像头和设置 RTSP 凭据。失败时恢复旧配置/凭据并重建当前版本容器。备份失败则不继续初始化。

断电或强制 SIGKILL 无法由异常处理自动回退。若发生在 Reset 中途，先阻止其他发布，在104使用受保护备份及本次 `rollback.json` 人工恢复；不要把备份复制到 Jenkins或公开位置。

## 验证

```bash
python3 -m unittest discover -s deployment/jenkins -p test_ci.py -v
```

Windows 会跳过 Linux flock 用例；需在 Linux 执行完整测试。离线测试覆盖配置生成、校验失败、镜像身份校验、单服务更新、回退、部署锁、Reset 新密钥和旧状态恢复。它们不接触生产容器，不能替代真实 Pipeline 构建、SSH发布和视频实播。

部署后核对容器实际镜像与 manifest、另外两服务的容器 ID 未变化、网页/API 可用，以及已有授权、证书、摄像头配置保持有效。再人工验证 WebRTC、截图和 RTSP 播放。Reset 的首次执行单独安排，不能作为普通发布验收的自动步骤。
