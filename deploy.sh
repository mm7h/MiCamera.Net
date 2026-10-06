#!/usr/bin/env bash
# Chinese, SSH-friendly deployment wizard. No Xiaomi account password is collected.
set +x
set -Eeuo pipefail
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
STATE_DIR="$SCRIPT_DIR/.deploy"
BRIDGE_IMAGE="micamera-net-bridge:local"
LAN_IP=""
NON_INTERACTIVE=false
PREVIOUS_IMAGES=()
PACKAGE_MODE=false

say() { printf '%s\n' "$*"; }
die() { say "错误：$*" >&2; exit 1; }
interactive() { [[ "$NON_INTERACTIVE" != true && -t 0 && -t 1 ]]; }
prompt() {
    local message="$1" default="${2:-}" answer
    interactive || die "缺少配置且当前没有交互终端。请先通过 SSH 终端运行 bash deploy.sh。"
    read -r -p "$message" answer || die "输入已中断，配置已保留，可稍后继续。"
    REPLY="${answer:-$default}"
}

compose() {
    local operation="$1"; shift
    local flags=()
    if [[ "$PACKAGE_MODE" == true && "$operation" == up ]]; then flags+=(--no-build); fi
    docker compose --project-directory "$SCRIPT_DIR" --env-file "$STATE_DIR/deployment.env" -f "$SCRIPT_DIR/docker-compose.yml" "$operation" "${flags[@]}" "$@"
}

load_package_metadata() {
    [[ -f "$SCRIPT_DIR/deployment/package.env" ]] || return 0
    PACKAGE_MODE=true
    local key value architecture
    while IFS='=' read -r key value; do
        case "$key" in
            PACKAGE_ARCH) PACKAGE_ARCH="$value" ;;
            MICAMERA_BRIDGE_IMAGE) export MICAMERA_BRIDGE_IMAGE="$value"; BRIDGE_IMAGE="$value" ;;
            MICAMERA_WEB_IMAGE) export MICAMERA_WEB_IMAGE="$value" ;;
            BRIDGE_ID) BRIDGE_ID="$value" ;;
            WEB_ID) WEB_ID="$value" ;;
            PACKAGE_VERSION) ;;
            *) die '体验包元数据包含未知字段。' ;;
        esac
    done < "$SCRIPT_DIR/deployment/package.env"
    case "$(uname -m)" in x86_64) architecture=amd64 ;; aarch64|arm64) architecture=arm64 ;; esac
    [[ "${PACKAGE_ARCH:-}" == "$architecture" ]] || die '体验包架构与当前主机不匹配。'
    [[ "$BRIDGE_IMAGE" =~ ^micamera-net-bridge:[A-Za-z0-9._-]+$ && "${MICAMERA_WEB_IMAGE:-}" =~ ^micamera-net-web:[A-Za-z0-9._-]+$ ]] || die '体验包镜像名称无效。'
    [[ "${BRIDGE_ID:-}" =~ ^sha256:[0-9a-f]{64}$ && "${WEB_ID:-}" =~ ^sha256:[0-9a-f]{64}$ ]] || die '体验包镜像标识无效。'
}

prepare_application_state() {
    [[ ! -L "$STATE_DIR/micamera-state" ]] || die '应用状态目录不能是符号链接。'
    mkdir -p "$STATE_DIR/micamera-state"
    docker run --rm --network none --user 0:0 --entrypoint python3 \
        --mount "type=bind,source=$STATE_DIR/micamera-state,target=/run/micamera-state" "$BRIDGE_IMAGE" \
        -c 'import os; os.chown("/run/micamera-state",10001,10001); os.chmod("/run/micamera-state",0o700)'
}

helper() {
    docker run --rm -i --network host --user "$(id -u):$(id -g)" \
        -e "LAN_IP=$LAN_IP" \
        -e "MILOCO_BASE_URL=https://$LAN_IP:8000" -e "RTSP_HTTP_LISTEN_URL=http://$LAN_IP:5080" \
        -e PYTHONDONTWRITEBYTECODE=1 \
        --mount "type=bind,source=$STATE_DIR,target=/state" \
        --entrypoint python3 "$BRIDGE_IMAGE" /app/deployment/helper.py "$@"
}

check_environment() {
    [[ "$(uname -s)" == Linux ]] || die "此部署脚本仅支持 Linux Docker Engine。"
    [[ "${BASH_VERSINFO[0]}" -ge 4 ]] || die "需要 Bash 4 或更新版本。"
    local tool version major minor endpoint security engine_os
    for tool in docker ip ss awk flock id mkdir chmod sha256sum; do
        command -v "$tool" >/dev/null || die "缺少系统工具 $tool。请安装 Docker Compose v2、iproute2、awk、coreutils 和 util-linux。"
    done
    docker info >/dev/null 2>&1 || die "Docker Engine 不可用，或当前用户无访问权限。"
    [[ "$(docker info --format '{{.OSType}}')" == linux ]] || die "需要 Linux 容器运行环境。"
    engine_os="$(docker info --format '{{.OperatingSystem}}')"
    [[ "$engine_os" != *'Docker Desktop'* ]] || die 'Docker Desktop 可用于镜像和隔离测试，但此向导需要原生 Linux Docker Engine 的 LAN host 网络；不能直接绑定 Desktop 宿主机 IP。'
    security="$(docker info --format '{{json .SecurityOptions}}')"
    [[ "$security" != *rootless* ]] || die "首版的 host 网络方案不支持 rootless Docker。"
    endpoint="${DOCKER_HOST:-$(docker context inspect --format '{{.Endpoints.docker.Host}}')}"
    [[ "$endpoint" == unix://* ]] || die "请使用本机 Docker Unix socket，远程 Docker context 不支持本地配置挂载。"
    version="$(docker compose version --short)" || die "请安装 Docker Compose v2。"
    version="${version#v}"
    IFS=. read -r major minor _ <<< "$version"
    [[ "$major" =~ ^[0-9]+$ && "$minor" =~ ^[0-9]+$ ]] || die "无法识别 Compose 版本。"
    (( major > 2 || (major == 2 && minor >= 20) )) || die "需要 Docker Compose 2.20 或更新版本。"
    case "$(uname -m)" in x86_64|aarch64|arm64) ;; *) die "仅支持 amd64/arm64。" ;; esac
    [[ ! -L "$STATE_DIR" ]] || die ".deploy 不得是符号链接。"
    mkdir -p "$STATE_DIR"
    chmod 700 "$STATE_DIR"
    local file
    for file in deployment.env settings.json license-ack MiCameraConfig.json MiCameraConfig.pending.json MiCameraConfig.previous.json secrets miloco .lock; do
        [[ ! -L "$STATE_DIR/$file" ]] || die "部署文件不能是符号链接：$file"
    done
    [[ ! -e "$STATE_DIR/bridge.env" && ! -e "$STATE_DIR/miloco.env" ]] || \
        die '检测到旧版 .deploy/*.env 凭据布局。此版本不迁移旧凭据；请先完成受控备份、移除旧文件并重新初始化。'
    exec 9> "$STATE_DIR/.lock"
    flock -n 9 || die "另一部署操作正在运行，请等待其结束。"
}

select_ip() {
    local saved="" choice index
    local -a addresses=()
    mapfile -t addresses < <(ip -o -4 addr show scope global | awk '
        $2 !~ /^(docker|br-|veth)/ {
            split($4,a,"/"); split(a[1],b,".");
            if (b[1] == 10 || (b[1] == 172 && b[2] >= 16 && b[2] <= 31) || (b[1] == 192 && b[2] == 168)) print a[1]
        }')
    (( ${#addresses[@]} > 0 )) || die '本机没有可用的 RFC1918 局域网 IPv4。请先配置与摄像头互通的 LAN 网卡。'
    if [[ -f "$STATE_DIR/deployment.env" ]]; then
        saved="$(awk -F= '$1=="LAN_IP" {print $2}' "$STATE_DIR/deployment.env")"
        [[ -n "$saved" ]] || die "部署地址文件缺少 LAN_IP。"
        for choice in "${addresses[@]}"; do
            if [[ "$choice" == "$saved" ]]; then LAN_IP="$saved"; return; fi
        done
        die "已保存的局域网 IP 不在当前网卡上。请按 README 的地址迁移步骤处理。"
    fi
    interactive || die "首次部署需要交互终端选择服务器的局域网 IP。"
    say "[1/5] 确认服务器局域网 IPv4 地址（RFC1918，须与摄像头互通）"
    index=0
    for choice in "${addresses[@]}"; do index=$((index+1)); say "  $index. $choice"; done
    say "  0. 手动输入"
    while true; do
        prompt '请选择 [1]：' '1'; choice="$REPLY"
        if [[ "$choice" == 0 ]]; then
            prompt '请输入本机局域网 IPv4：'; LAN_IP="$REPLY"
            for choice in "${addresses[@]}"; do [[ "$choice" != "$LAN_IP" ]] || break; done
            [[ "$choice" == "$LAN_IP" && -n "$LAN_IP" ]] && break
            say '该地址未出现在本机网卡列表中，请重新输入。'
        elif [[ "$choice" =~ ^[1-9][0-9]{0,2}$ ]] && (( choice <= ${#addresses[@]} )); then
            LAN_IP="${addresses[choice-1]}"; break
        else say '选项不正确，请重新选择。'; fi
    done
    printf 'LAN_IP=%s\n' "$LAN_IP" > "$STATE_DIR/deployment.env"
}

check_ports() {
    local entry service port container
    for entry in miloco:8000 bridge:5080 bridge:8554 web:5081; do
        service="${entry%:*}"; port="${entry#*:}"
        container="$(compose ps -q "$service")"
        if [[ -n "$container" ]] && [[ "$(docker inspect --format '{{.State.Running}}' "$container")" == true ]]; then continue; fi
        if ss -H -ltn | awk -v port="$port" -v ip="$LAN_IP" \
            '$4 ~ (":" port "$") && ($4 == ip ":" port || $4 == "0.0.0.0:" port || $4 == "*:" port || $4 == "[::]:" port) {found=1} END {exit !found}'; then
            die "默认 TCP 端口 $port 已被其他服务占用。请先处理冲突；脚本不会静默修改端口。"
        fi
    done
}

license_notice() {
    [[ ! -f "$STATE_DIR/license-ack" ]] || return 0
    say '部署使用用途受限的 Miloco 和含附加限制的 SIPSorcery；个人使用不自动代表获准开发桥接应用。'
    say '请先阅读 deployment/THIRD-PARTY-NOTICES.md 并确认你的使用已获所需授权。'
    prompt '确认已核对许可和使用授权请输入 YES（否则退出）：'
    [[ "$REPLY" == YES ]] || die '未确认使用授权，尚未启动或拉取 Miloco。'
}

build_images() {
    local container
    say '[2/5] 构建桥接服务与前端（首次需要下载镜像和依赖）'
    mapfile -t PREVIOUS_IMAGES < <(
        docker image ls --quiet micamera-net-bridge
        docker image ls --quiet micamera-net-web
        docker image ls --quiet --filter label=com.docker.compose.project=micamera-net
        # A previous build may have moved the tag while the old container still uses
        # its now-untagged image. Record that image before Compose replaces the container.
        while IFS= read -r container; do
            [[ -z "$container" ]] || docker inspect --format '{{.Image}}' "$container"
        done < <(docker ps -aq --filter label=com.docker.compose.project=micamera-net)
    )
    if [[ "$PACKAGE_MODE" == true ]]; then
        say '校验并导入预构建镜像，无需源码编译。'
        (cd "$SCRIPT_DIR" && sha256sum -c SHA256SUMS) || die '体验包校验失败。'
        if [[ "$(docker image inspect --format '{{.Id}}' "$BRIDGE_IMAGE" 2>/dev/null || true)" != "$BRIDGE_ID" || \
              "$(docker image inspect --format '{{.Id}}' "$MICAMERA_WEB_IMAGE" 2>/dev/null || true)" != "$WEB_ID" ]]; then
            docker load -i "$SCRIPT_DIR/images.tar"
        fi
        [[ "$(docker image inspect --format '{{.Id}}' "$BRIDGE_IMAGE")" == "$BRIDGE_ID" && \
           "$(docker image inspect --format '{{.Id}}' "$MICAMERA_WEB_IMAGE")" == "$WEB_ID" ]] || die '导入的镜像与体验包不匹配。'
        [[ "$(docker image inspect --format '{{.Architecture}}' "$BRIDGE_IMAGE")" == "$PACKAGE_ARCH" && \
           "$(docker image inspect --format '{{.Architecture}}' "$MICAMERA_WEB_IMAGE")" == "$PACKAGE_ARCH" ]] || die '镜像架构不匹配。'
    else
        compose build --pull bridge web
    fi
}

cleanup_previous_deployment() {
    local container image full_id tag referenced
    local used_images=() tags=()
    while IFS= read -r container; do
        [[ -n "$container" ]] || continue
        if [[ "$(docker inspect --format '{{.State.Status}}' "$container")" != running ]]; then
            docker rm "$container"
        fi
    done < <(docker ps -aq --filter label=com.docker.compose.project=micamera-net)
    while IFS= read -r container; do
        [[ -n "$container" ]] || continue
        used_images+=("$(docker inspect --format '{{.Image}}' "$container")")
    done < <(docker ps -aq)
    for image in "${PREVIOUS_IMAGES[@]}"; do
        full_id="$(docker image inspect --format '{{.Id}}' "$image" 2>/dev/null)" || continue
        referenced=false
        for tag in "${used_images[@]}"; do
            [[ "$tag" != "$full_id" ]] || referenced=true
        done
        [[ "$referenced" == false ]] || continue
        mapfile -t tags < <(docker image inspect --format '{{range .RepoTags}}{{println .}}{{end}}' "$image")
        for tag in "${tags[@]}"; do
            case "$tag" in ''|micamera-net-bridge:*|micamera-net-web:*) ;; *) referenced=true ;; esac
        done
        [[ "$referenced" == false ]] || continue
        for tag in "${tags[@]}"; do
            [[ -z "$tag" ]] || docker image rm "$tag"
        done
        if docker image inspect "$image" >/dev/null 2>&1; then docker image rm "$image"; fi
    done
}

wait_probe() {
    local command="$1" seconds="$2" deadline
    deadline=$((SECONDS+seconds))
    while (( SECONDS < deadline )); do
        if helper "$command" >/dev/null 2>&1; then return 0; fi
        sleep 3
    done
    helper "$command" || return 1
}

validate_config_file() {
    local config_file="$1"
    docker run --rm --network none \
        --mount "type=bind,source=$config_file,target=/run/configs/MiCameraConfig.json,readonly" \
        --mount "type=bind,source=$STATE_DIR/secrets/rtsp_api_token,target=/run/secrets/rtsp_api_token,readonly" \
        --mount "type=bind,source=$STATE_DIR/miloco/cert/cert.pem,target=/run/configs/miloco-server-cert.pem,readonly" \
        "$BRIDGE_IMAGE" --validate-config
}

validate_current_config() {
    validate_config_file "$STATE_DIR/MiCameraConfig.json"
}

summary() {
    say "前端：http://$LAN_IP:5081"
    say "Miloco：https://$LAN_IP:8000"
    say "RTSP：rtsp://$LAN_IP:8554/live/{streamId}（客户端须使用 TCP 和 Digest 认证）"
    say '先进入 Miloco 完成本地 PIN 设置和小米账号绑定，再在前端完成连接、摄像头与 RTSP 配置；初始化前 RTSP 端口保持关闭。'
    say '配置保存在 .deploy/micamera-state/settings.db；网页重新配置后立即生效，无需重启后端。'
    say '前端通过内置同源代理访问后端，浏览器无需输入 Token。'
    say '打开页面后仍须确认实际画面；服务就绪不等于 WebRTC 端到端验收完成。'
    say '防火墙须允许可信局域网访问 TCP 8000/5080/5081/8554 和 UDP 50000–50100。脚本不会修改防火墙。'
}

main() {
    local operation="up" argument
    for argument in "$@"; do
        case "$argument" in
            up|add-camera|build|status|logs|stop|credentials)
                [[ "$operation" == up ]] || die '一次只能指定一个操作。'
                operation="$argument"
                ;;
            --non-interactive) NON_INTERACTIVE=true ;;
            *) die '用法：bash deploy.sh [up|add-camera|build|status|logs|stop|credentials] [--non-interactive]' ;;
        esac
    done
    check_environment
    load_package_metadata
    if [[ "$operation" == status || "$operation" == logs || "$operation" == stop || "$operation" == credentials || "$operation" == add-camera ]]; then
        [[ -f "$STATE_DIR/deployment.env" ]] || die '尚未初始化部署。'
        LAN_IP="$(awk -F= '$1=="LAN_IP" {print $2}' "$STATE_DIR/deployment.env")"
        case "$operation" in
            status) compose ps; helper status; summary ;;
            logs) compose logs --tail=100 ;;
            stop) compose stop; say '服务已停止；账号授权、配置和凭据均保留。' ;;
            credentials|add-camera) say "请打开 http://$LAN_IP:5081 的重新配置入口；保存后立即应用新配置，无需重启后端。" ;;
        esac
        return
    fi
    select_ip
    build_images
    if [[ "$NON_INTERACTIVE" == true ]]; then
        helper prepare-web-config
        helper validate-state "$LAN_IP"
        validate_current_config
    else
        helper init "$LAN_IP"
        license_notice
        helper ack-license
    fi
    prepare_application_state
    check_ports
    [[ "$operation" != build ]] || { say '镜像构建完成，尚未启动服务。'; return; }
    compose up -d miloco web
    wait_probe miloco-live 120 || die 'Miloco 启动检查失败，账号数据已保留，请检查日志。'
    compose up -d bridge
    wait_probe deployment-ready 120 || die '后端未启动，请运行 status 和 logs 排查。'
    say '[5/5] 服务已启动，请进入网页完成初始化配置。'
    cleanup_previous_deployment
    summary
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
