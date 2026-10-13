#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/common.sh"

require_bundle_layout
require_docker
validate_optional_env_file
http_port="$(deployment_http_port)"

compose ps
printf 'api=%s\n' "$(service_health api)"

if compose exec -T api curl --fail --silent --show-error "http://127.0.0.1:$http_port/live" >/dev/null 2>&1; then
  info "API 进程存活：是"
else
  info "API 进程存活：否"
  exit 1
fi

if compose exec -T api curl --fail --silent --show-error "http://127.0.0.1:$http_port/health" >/dev/null 2>&1; then
  info "平台完整就绪（PostgreSQL + Redis）：是"
else
  info "平台完整就绪（PostgreSQL + Redis）：否；执行 ./logs.sh 查看脱敏日志"
  exit 1
fi
