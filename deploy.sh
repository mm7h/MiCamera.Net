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

say() { printf '%s\n' "$*"; }
die() { say "错误：$*" >&2; exit 1; }
interactive() { [[ "$NON_INTERACTIVE" != true && -t 0 && -t 1 ]]; }
prompt() {
    local message="$1" default="${2:-}" answer
    interactive || die "缺少配置且当前没有交互终端。请先通过 SSH 终端运行 bash deploy.sh。"
    read -r -p "$message" answer || die "输入已中断，配置已保留，可稍后继续。"
    REPLY="${answer:-$default}"
}

compose() { docker compose --project-directory "$SCRIPT_DIR" --env-file "$STATE_DIR/deployment.env" -f "$SCRIPT_DIR/docker-compose.yml" "$@"; }

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
    for tool in docker ip ss awk flock id mkdir chmod; do
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
    say '[2/5] 构建桥接服务与前端（首次需要下载镜像和依赖）'
    compose build --pull bridge web
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

authorize() {
    say '[3/5] Miloco 本地登录与小米账号绑定'
    say "请在电脑浏览器打开 https://$LAN_IP:8000，设置本地密码并完成小米账号绑定。"
    say '该页面使用自签名证书。这里只处理你本机的 Miloco，不收集小米账号密码。'
    if helper authorize 2>/dev/null; then return; fi
    interactive || die '缺少有效的 Miloco 密码或小米授权。请通过交互终端重新运行。'
    local password action
    while true; do
        prompt '完成网页配置后按 Enter；输入 q 可稍后继续：'
        [[ "$REPLY" != q ]] || die '已保留当前部署，可稍后重新运行继续。'
        read -r -s -p '请输入 Miloco 本地密码（不是小米账号密码）：' password || die '密码输入已中断。'
        printf '\n'
        [[ -n "$password" ]] || { say '密码不能为空。'; continue; }
        printf '%s' "$password" | helper set-password
        unset password
        if helper authorize; then return; fi
        say '  1. 重新检查网页授权（复用已输入的本地密码）'
        say '  2. 重新输入 Miloco 本地密码'
        say '  3. 退出并稍后继续'
        while true; do
            prompt '请选择 [1]：' '1'; action="$REPLY"
            case "$action" in
                1) if helper authorize; then return; fi ;;
                2) break ;;
                3) die '已保留部署配置，可稍后继续。' ;;
                *) say '选项不正确。' ;;
            esac
        done
    done
}

validate_config_file() {
    local config_file="$1"
    docker run --rm --network none \
        --mount "type=bind,source=$config_file,target=/run/configs/MiCameraConfig.json,readonly" \
        --mount "type=bind,source=$STATE_DIR/secrets/miloco_password_md5,target=/run/secrets/miloco_password_md5,readonly" \
        --mount "type=bind,source=$STATE_DIR/secrets/rtsp_api_token,target=/run/secrets/rtsp_api_token,readonly" \
        --mount "type=bind,source=$STATE_DIR/secrets/rtsp_password,target=/run/secrets/rtsp_password,readonly" \
        --mount "type=bind,source=$STATE_DIR/miloco/cert/cert.pem,target=/run/configs/miloco-server-cert.pem,readonly" \
        "$BRIDGE_IMAGE" --validate-config
}

validate_pending() {
    validate_config_file "$STATE_DIR/MiCameraConfig.pending.json"
}

validate_current_config() {
    validate_config_file "$STATE_DIR/MiCameraConfig.json"
}

add_camera() {
    interactive || die '添加摄像头需要交互终端。'
    say '[4/5] 配置摄像头'
    local did stream channel codec choice count
    count="$(helper stream-count)"
    if helper cameras; then
        while true; do
            prompt '请选择摄像头 [0=手动]：' '0'; choice="$REPLY"
            if [[ "$choice" == 0 ]]; then prompt '摄像头 DID：'; did="$REPLY"; break; fi
            if [[ "$choice" =~ ^[1-9][0-9]{0,2}$ ]] && did="$(helper select-camera "$choice")"; then break; fi
            say '选项不正确，请重新选择。'
        done
    else
        say '无法获取摄像头列表。可以在 Miloco 页面 F12 网络请求中核对 DID。'
        prompt '摄像头 DID：'; did="$REPLY"
    fi
    while [[ ! "$did" =~ ^[A-Za-z0-9._:-]{1,128}$ ]]; do
        say 'DID 格式不正确，请重新输入。'
        prompt '摄像头 DID：'; did="$REPLY"
    done
    while true; do
        prompt "流名称 [camera-$((count+1))]：" "camera-$((count+1))"; stream="$REPLY"
        prompt '通道 [0]：' '0'; channel="$REPLY"
        say '  1. H.265（默认）  2. H.264'
        prompt '视频编码 [1]：' '1'; choice="$REPLY"
        case "$choice" in 1) codec=H265 ;; 2) codec=H264 ;; *) say '编码选项不正确。'; continue ;; esac
        if helper stage-stream "$did" "$stream" "$channel" "$codec" && validate_pending; then break; fi
        say '配置未通过校验，原配置未修改，请重新填写。'
    done
    helper commit-stream
    say '配置已保存。启动/重建桥接容器会中断现有播放连接。'
    if compose up -d --no-deps --force-recreate bridge && wait_probe ready 120; then return; fi
    if (( count > 0 )); then
        say '新增配置未就绪，恢复上一份配置并重建桥接服务。'
        helper rollback-stream
        compose up -d --no-deps --force-recreate bridge
    fi
    die '视频尚未就绪。首次配置保留供重试；新增配置已回退。请运行 bash deploy.sh status 和 logs 排查。'
}

summary() {
    say "前端：http://$LAN_IP:5081"
    say "Miloco：https://$LAN_IP:8000"
    say "RTSP：rtsp://$LAN_IP:8554/live/{streamId}（客户端须使用 TCP 和 Digest 认证）"
    say '凭据已保存在 .deploy/secrets。运行 bash deploy.sh credentials 在终端查看 RTSP 凭据。'
    say '前端通过内置同源代理访问后端，浏览器无需输入 Token。'
    say '打开页面后仍须确认实际画面；服务就绪不等于 WebRTC 端到端验收完成。'
    say '防火墙须允许可信局域网访问 TCP 8000/5080/5081/8554 和 UDP 50000–50100。脚本不会修改防火墙。'
}

main() {
    local operation="up" count argument
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
    if [[ "$operation" == status || "$operation" == logs || "$operation" == stop || "$operation" == credentials ]]; then
        [[ -f "$STATE_DIR/deployment.env" ]] || die '尚未初始化部署。'
        LAN_IP="$(awk -F= '$1=="LAN_IP" {print $2}' "$STATE_DIR/deployment.env")"
        case "$operation" in
            status) compose ps; helper status; summary ;;
            logs) compose logs --tail=100 ;;
            stop) compose stop; say '服务已停止；账号授权、配置和凭据均保留。' ;;
            credentials) interactive || die '凭据只能在交互终端显示，避免被重定向到日志。'; helper credentials ;;
        esac
        return
    fi
    select_ip
    build_images
    if [[ "$NON_INTERACTIVE" == true ]]; then
        helper validate-state "$LAN_IP"
        validate_current_config
    else
        helper init "$LAN_IP"
        license_notice
        helper ack-license
    fi
    check_ports
    [[ "$operation" != build ]] || { say '镜像构建完成，尚未启动服务。'; return; }
    compose up -d miloco web
    wait_probe miloco-live 120 || die 'Miloco 启动检查失败，账号数据已保留，请检查日志。'
    authorize
    count="$(helper stream-count)"
    if [[ "$operation" == add-camera ]] || (( count == 0 )); then
        add_camera
    else
        compose up -d bridge
        wait_probe ready 120 || die '容器已启动，但媒体尚未就绪。请运行 status 和 logs。'
    fi
    say '[5/5] 服务、授权与当前视频检查通过。'
    summary
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
