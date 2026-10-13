#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
if [[ -f "$SCRIPT_DIR/../compose.yaml" ]]; then
  BUNDLE_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd -P)"
elif [[ -f "$SCRIPT_DIR/compose.yaml" ]]; then
  BUNDLE_DIR="$SCRIPT_DIR"
else
  printf '错误：无法定位 compose.yaml，请从完整的 HSJM 离线包中运行脚本。\n' >&2
  exit 1
fi

ENV_FILE="$BUNDLE_DIR/.env"
VERSION_FILE="$BUNDLE_DIR/VERSION"

die() {
  printf '错误：%s\n' "$1" >&2
  exit 1
}

info() {
  printf '%s\n' "$1"
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "缺少命令：$1"
}

require_bundle_layout() {
  [[ -f "$BUNDLE_DIR/compose.yaml" ]] || die "缺少 compose.yaml"
  [[ -f "$BUNDLE_DIR/.env.example" ]] || die "缺少 .env.example"
  [[ -f "$VERSION_FILE" ]] || die "缺少 VERSION"
}

require_docker() {
  require_command docker
  docker info >/dev/null 2>&1 || die "Docker Engine 未运行，或当前用户无权访问 Docker。"
  docker compose version >/dev/null 2>&1 || die "需要 Docker Compose v2（docker compose）。"
}

env_value() {
  local key="$1"
  [[ -f "$ENV_FILE" ]] || return 0

  awk -v wanted="$key" '
    index($0, wanted "=") == 1 {
      value = substr($0, length(wanted) + 2)
      sub(/\r$/, "", value)
      if (value ~ /^".*"$/ || value ~ /^\047.*\047$/) {
        value = substr(value, 2, length(value) - 2)
      }
      print value
      exit
    }
  ' "$ENV_FILE"
}

validate_optional_env_file() {
  [[ -f "$ENV_FILE" ]] || return 0

  local mode
  mode="$(stat -c '%a' "$ENV_FILE" 2>/dev/null || true)"
  [[ "$mode" == "600" ]] || die ".env 权限必须为 0600；请执行：chmod 0600 .env"
}

is_listening_port() {
  local port="$1"

  if command -v ss >/dev/null 2>&1; then
    ss -H -lnt 2>/dev/null \
      | awk -v wanted="$port" '{ address=$4; sub(/^.*:/, "", address); if (address == wanted) found=1 } END { exit !found }'
    return
  fi

  if command -v netstat >/dev/null 2>&1; then
    netstat -lnt 2>/dev/null \
      | awk -v wanted="$port" 'NR > 2 { address=$4; sub(/^.*:/, "", address); if (address == wanted) found=1 } END { exit !found }'
    return
  fi

  die "无法检查端口占用：服务器需要 ss（推荐）或 netstat。"
}

docker_publishes_port() {
  local port="$1"
  docker ps --format '{{.Ports}}' 2>/dev/null \
    | grep -Eq "(^|[,:])${port}->|:${port}->"
}

port_is_available() {
  local port="$1"
  [[ "$port" =~ ^[0-9]+$ ]] || return 1
  (( port >= 1024 && port <= 65535 )) || return 1
  ! is_listening_port "$port" && ! docker_publishes_port "$port"
}

write_env_value() {
  local key="$1"
  local value="$2"
  local temporary_file

  if [[ ! -f "$ENV_FILE" ]]; then
    cp "$BUNDLE_DIR/.env.example" "$ENV_FILE"
  fi

  temporary_file="${ENV_FILE}.tmp.$$"
  awk -v wanted="$key" -v replacement="$key=$value" '
    BEGIN { replaced=0 }
    index($0, wanted "=") == 1 {
      if (!replaced) print replacement
      replaced=1
      next
    }
    { print }
    END { if (!replaced) print replacement }
  ' "$ENV_FILE" > "$temporary_file"
  chmod 0600 "$temporary_file"
  mv -f -- "$temporary_file" "$ENV_FILE"
}

select_http_port() {
  local configured_port
  local candidate

  configured_port="$(env_value HSJM_HTTP_PORT)"
  if [[ -n "$configured_port" ]]; then
    [[ "$configured_port" =~ ^[0-9]+$ ]] || die "HSJM_HTTP_PORT 必须是 1024-65535 的整数。"
    (( configured_port >= 1024 && configured_port <= 65535 )) \
      || die "HSJM_HTTP_PORT 必须在 1024-65535 之间。"

    if compose ps -q api 2>/dev/null | grep -q .; then
      printf '%s' "$configured_port"
      return
    fi

    port_is_available "$configured_port" \
      || die "端口 $configured_port 已被占用；请清空 .env 中的 HSJM_HTTP_PORT 后重试自动选择。"
    printf '%s' "$configured_port"
    return
  fi

  for (( candidate=28000; candidate<=28999; candidate++ )); do
    if port_is_available "$candidate"; then
      write_env_value HSJM_HTTP_PORT "$candidate"
      printf '%s' "$candidate"
      return
    fi
  done

  die "28000-28999 没有可用端口；请在 .env 中设置一个空闲的 HSJM_HTTP_PORT。"
}

deployment_http_port() {
  local port
  port="$(env_value HSJM_HTTP_PORT)"
  [[ "$port" =~ ^[0-9]+$ ]] || die ".env 中缺少有效的 HSJM_HTTP_PORT；请先执行 ./start.sh。"
  printf '%s' "$port"
}

compose() {
  local version
  local redis_override
  version="$(<"$VERSION_FILE")"
  [[ -n "$version" ]] || die "VERSION 为空。"

  redis_override="$(env_value HSJM_REDIS_CONNECTION)"
  if [[ -n "$redis_override" ]]; then
    export WebConfig__RedisServerUrl="$redis_override"
  else
    unset WebConfig__RedisServerUrl || true
  fi

  if [[ -f "$ENV_FILE" ]]; then
    HSJM_VERSION="$version" docker compose --env-file "$ENV_FILE" -f "$BUNDLE_DIR/compose.yaml" "$@"
  else
    HSJM_VERSION="$version" docker compose -f "$BUNDLE_DIR/compose.yaml" "$@"
  fi
}

redact_stream() {
  sed -E \
    -e 's/((Password|Pwd)[[:space:]]*=[[:space:]]*)[^;[:space:]]+/\1REDACTED/Ig' \
    -e 's/(("?(password|pwd|accessToken|refreshToken|securityKey|apiKey)"?)[[:space:]]*:[[:space:]]*")[^"]*"/\1REDACTED"/Ig' \
    -e 's/((ApiKey|SecurityKey|AccessToken)[[:space:]]*[:=][[:space:]]*)[^,;[:space:]"}]+/\1REDACTED/Ig' \
    -e 's/(Authorization:[[:space:]]*Bearer[[:space:]]+)[^[:space:]]+/\1REDACTED/Ig'
}

service_health() {
  local service="$1"
  local container_id
  container_id="$(compose ps -q "$service" 2>/dev/null || true)"
  if [[ -z "$container_id" ]]; then
    printf 'missing'
    return
  fi

  docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container_id" 2>/dev/null || printf 'unknown'
}

wait_for_platform() {
  local timeout_seconds="${1:-240}"
  local deadline=$(( $(date +%s) + timeout_seconds ))
  local api_state

  while (( $(date +%s) < deadline )); do
    api_state="$(service_health api)"

    if [[ "$api_state" == healthy ]]; then
      return 0
    fi

    if [[ "$api_state" == unhealthy ]]; then
      return 1
    fi

    sleep 3
  done

  return 1
}
