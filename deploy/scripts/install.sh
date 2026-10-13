#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/common.sh"

require_bundle_layout
require_docker
require_command sha256sum
validate_optional_env_file

[[ -f "$BUNDLE_DIR/SHA256SUMS" ]] || die "缺少 SHA256SUMS，不能确认离线包完整性。"
info "正在校验离线包完整性……"
(cd "$BUNDLE_DIR" && sha256sum --check SHA256SUMS)

shopt -s nullglob
image_archives=("$BUNDLE_DIR"/images/*.tar)
shopt -u nullglob
[[ ${#image_archives[@]} -eq 1 ]] || die "images 目录中必须且只能有一个镜像归档。"

info "正在加载 HSJM API 单容器镜像……"
docker image load --input "${image_archives[0]}"
info "安装完成。下一步执行：./start.sh"
