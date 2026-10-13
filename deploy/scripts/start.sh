#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/common.sh"

require_bundle_layout
require_docker
validate_optional_env_file

http_port="$(select_http_port)"
# select_http_port may create .env; enforce the deployment secret-file mode.
validate_optional_env_file

timeout_seconds="$(env_value HSJM_START_TIMEOUT_SECONDS)"
timeout_seconds="${timeout_seconds:-240}"
[[ "$timeout_seconds" =~ ^[0-9]+$ ]] || die "HSJM_START_TIMEOUT_SECONDS 必须是正整数。"

info "正在覆盖并启动 HSJM 平台单容器……"
compose up --detach --remove-orphans --force-recreate

if ! wait_for_platform "$timeout_seconds"; then
  printf 'API 未在 %s 秒内进入 healthy 状态。\n' "$timeout_seconds" >&2
  compose ps >&2 || true
  printf 'API 最近日志（敏感字段已脱敏）：\n' >&2
  compose logs --no-color --tail 120 api 2>&1 | redact_stream >&2 || true
  exit 1
fi

info "平台已就绪：http://127.0.0.1:$http_port"
