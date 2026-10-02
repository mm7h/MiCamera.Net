#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)/deploy.sh"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
LAN_IP=192.168.1.100

# A saved configuration can run non-interactively; missing user input must not hang.
interactive() { return 1; }
if (prompt 'Missing input:' >/dev/null 2>&1); then fail 'non-interactive prompt unexpectedly succeeded'; fi

# Exercise the actual bounded retry loop, without sleeping or contacting Docker.
attempts=0
helper() { attempts=$((attempts+1)); (( attempts >= 2 )); }
sleep() { SECONDS=$((SECONDS+1)); }
wait_probe ready 3 || fail 'retry did not reach success'
[[ "$attempts" == 2 ]] || fail 'retry count incorrect'
helper() { return 1; }
if wait_probe ready 2 >/dev/null 2>&1; then fail 'timeout unexpectedly succeeded'; fi

# Existing project containers must not be reported as unrelated occupied ports.
compose() { printf 'owned-container\n'; }
docker() { printf 'true\n'; }
ss() { fail 'port probe should be skipped for running project container'; }
check_ports

# An unrelated listener must fail before startup rather than silently changing a port.
compose() { return 0; }
ss() { printf 'LISTEN 0 128 192.168.1.100:8000 0.0.0.0:*\n'; }
if (check_ports >/dev/null 2>&1); then fail 'external port conflict was not detected'; fi

STATE_DIR="$(mktemp -d /tmp/micamera-deploy-test.XXXXXX)"
cleanup() {
    # Remove only these test-owned files, never recursively delete a derived directory.
    rm -f -- "$STATE_DIR/deployment.env" "$STATE_DIR/committed" "$STATE_DIR/rolled-back" "$STATE_DIR/restarted" "$STATE_DIR/built"
    rmdir -- "$STATE_DIR"
}
trap cleanup EXIT

# Reject unsupported network layouts before persisting an address or building images.
interactive() { return 0; }
ip() {
    printf '%s\n' '1: eth0 inet 8.8.8.8/24 scope global eth0' \
        '2: eth1 inet 192.168.1.100/24 scope global eth1' \
        '3: docker0 inet 10.0.0.1/16 scope global docker0'
}
select_ip <<< '1' >/dev/null
[[ "$LAN_IP" == 192.168.1.100 ]] || fail 'wizard selected a public or Docker address'
ip() { printf '%s\n' '1: eth0 inet 8.8.8.8/24 scope global eth0'; }
if (select_ip </dev/null >/dev/null 2>&1); then fail 'unsupported network was accepted'; fi

# A Linux/WSL shell alone is not proof of a native Linux Docker host network.
uname() { printf 'Linux\n'; }
flock() { return 0; }
docker() {
    [[ "$1" == info ]] || fail 'unexpected Docker operation during environment check'
    if [[ "${3:-}" == '{{.OSType}}' ]]; then printf 'linux\n';
    elif [[ "${3:-}" == '{{.OperatingSystem}}' ]]; then printf 'Docker Desktop (containerized)\n'; fi
}
if environment_failure="$(check_environment 2>&1)"; then fail 'Desktop LAN bind assumptions were accepted'; fi
[[ "$environment_failure" == *'Docker Desktop'* ]] || fail 'Desktop was not rejected with a specific explanation'

printf 'LAN_IP=192.168.1.100\n' > "$STATE_DIR/deployment.env"
check_environment() { return 0; }
compose() { [[ "$1" == stop ]] || fail 'stop performed an unexpected Compose operation'; }
main stop >/dev/null
[[ -f "$STATE_DIR/deployment.env" && ! -e "$STATE_DIR/bridge.env" ]] || fail 'stop did not preserve the new state layout'

# A non-interactive run must validate pre-provisioned state instead of prompting or initializing it.
select_ip() { LAN_IP=192.168.1.100; }
build_images() { : > "$STATE_DIR/built"; }
helper() {
    [[ "$1" == validate-state && "$2" == 192.168.1.100 ]] || fail 'non-interactive run did not validate the pre-provisioned state'
}
check_ports() { :; }
validate_current_config() { :; }
main build --non-interactive >/dev/null
[[ -f "$STATE_DIR/built" ]] || fail 'non-interactive build did not run after state validation'

# Adding a camera validates first, then rolls back if media never becomes ready.
interactive() { return 0; }
prompt() {
    case "$1" in
        '摄像头 DID：') REPLY=device2 ;;
        '流名称 '* ) REPLY=camera-2 ;;
        '通道 '* ) REPLY=0 ;;
        '视频编码 '* ) REPLY=1 ;;
        *) fail 'unexpected prompt' ;;
    esac
}
helper() {
    case "$1" in
        stream-count) printf '1\n' ;;
        cameras) return 1 ;;
        stage-stream) return 0 ;;
        commit-stream) : > "$STATE_DIR/committed" ;;
        rollback-stream) : > "$STATE_DIR/rolled-back" ;;
        *) fail 'unexpected helper command' ;;
    esac
}
validate_pending() { return 0; }
compose() { : > "$STATE_DIR/restarted"; }
wait_probe() { return 1; }
if (add_camera >/dev/null 2>&1); then fail 'failed new stream was incorrectly successful'; fi
[[ -f "$STATE_DIR/committed" && -f "$STATE_DIR/rolled-back" && -f "$STATE_DIR/restarted" ]] || fail 'failed stream did not roll back'
printf 'Bash deployment control-flow checks passed.\n'
