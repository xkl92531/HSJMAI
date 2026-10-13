#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"

"$SCRIPT_DIR/install.sh"
"$SCRIPT_DIR/start.sh"
"$SCRIPT_DIR/verify.sh"

source "$SCRIPT_DIR/common.sh"
http_port="$(deployment_http_port)"
server_ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
server_ip="${server_ip:-127.0.0.1}"
info "一键部署完成。"
info "平台地址：http://$server_ip:$http_port"
