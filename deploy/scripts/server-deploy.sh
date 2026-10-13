#!/usr/bin/env bash
# 服务器端一键部署脚本（手工上传模式入口）。
#
# 前提：已把离线包 hsjm-ai-linux-amd64-<版本>.tar.gz 上传到服务器并解压，
#       当前目录就是解压出的 hsjm-ai-linux-amd64-<版本>/ 目录。
#
# 用法：
#   ./server-deploy.sh                                # 不带图片数据（或自动检测同级目录）
#   ./server-deploy.sh /opt/hsjm/appdata-init-<版本>.tar.gz   # 显式指定图片数据包
#
# 步骤：install.sh（校验+装镜像）-> start.sh（选端口+启动）-> verify.sh（全量验证）
#       -> （如有图片数据包）migrate-appdata.sh（LineUp 图片/导入源灌卷）
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/common.sh"

require_bundle_layout
require_docker
validate_optional_env_file

APPDATA_ARCHIVE="${1:-}"
if [[ -z "$APPDATA_ARCHIVE" ]]; then
  # 自动检测离线包同级目录（通常 /opt/hsjm）下的 appdata-init-*.tar.gz，多个时取最新。
  APPDATA_ARCHIVE="$(ls -1t "$BUNDLE_DIR"/../appdata-init-*.tar.gz 2>/dev/null | head -n 1 || true)"
  if [[ -n "$APPDATA_ARCHIVE" ]]; then
    info "自动检测到图片数据包：$APPDATA_ARCHIVE"
  fi
fi
if [[ -n "$APPDATA_ARCHIVE" && ! -f "$APPDATA_ARCHIVE" ]]; then
  die "指定的图片数据包不存在：$APPDATA_ARCHIVE"
fi

"$SCRIPT_DIR/install.sh"
"$SCRIPT_DIR/start.sh"
"$SCRIPT_DIR/verify.sh"

if [[ -n "$APPDATA_ARCHIVE" ]]; then
  "$SCRIPT_DIR/migrate-appdata.sh" "$APPDATA_ARCHIVE"
else
  info "未提供图片数据包，跳过 LineUp 图片迁移（需要时执行 ./migrate-appdata.sh <包路径>）。"
fi

http_port="$(deployment_http_port)"
server_ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
server_ip="${server_ip:-127.0.0.1}"
info "服务器端一键部署完成。"
info "平台地址：http://$server_ip:$http_port"
