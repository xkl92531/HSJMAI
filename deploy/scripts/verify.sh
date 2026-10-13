#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/common.sh"

require_bundle_layout
require_docker
validate_optional_env_file
http_port="$(deployment_http_port)"
base_url="http://127.0.0.1:$http_port"

if ! wait_for_platform 15; then
  die "API 容器尚未健康，请先执行 ./status.sh 并检查 API 日志。"
fi

compose exec -T api curl --fail --silent --show-error \
  "$base_url/" >/dev/null
compose exec -T api curl --fail --silent --show-error \
  "$base_url/live" >/dev/null
info "API 存活检查通过。"
compose exec -T api curl --fail --silent --show-error \
  "$base_url/health" >/dev/null
info "完整就绪检查通过：PostgreSQL SELECT 1 与 Redis PING 均成功。"

modern_meta='"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"hsjm-deploy-verify","version":"2.0"},"io.modelcontextprotocol/clientCapabilities":{}}'
discover_payload="{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/discover\",\"params\":{$modern_meta}}"
discover_response="$(compose exec -T api curl --fail --silent --show-error \
  --request POST \
  --header 'Content-Type: application/json' \
  --header 'Accept: application/json, text/event-stream' \
  --header 'MCP-Protocol-Version: 2026-07-28' \
  --header 'Mcp-Method: server/discover' \
  --data "$discover_payload" \
  "$base_url/mcp")"

printf '%s' "$discover_response" | grep -Fq '"result"' || die "MCP 2.0 server/discover 未返回 Result。"
printf '%s' "$discover_response" | grep -Fq '2026-07-28' || die "MCP 2.0 server/discover 未声明 2026-07-28。"

modern_tools_payload="{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{$modern_meta}}"
modern_tools_response="$(compose exec -T api curl --fail --silent --show-error \
  --request POST \
  --header 'Content-Type: application/json' \
  --header 'Accept: application/json, text/event-stream' \
  --header 'MCP-Protocol-Version: 2026-07-28' \
  --header 'Mcp-Method: tools/list' \
  --data "$modern_tools_payload" \
  "$base_url/mcp")"

printf '%s' "$modern_tools_response" | grep -Fq '"diagnose_energy_flow"' || die "MCP 2.0 工具列表缺少 diagnose_energy_flow。"
printf '%s' "$modern_tools_response" | grep -Fq '"diagnose_line_stoppage"' || die "MCP 2.0 工具列表缺少 diagnose_line_stoppage。"

initialize_payload='{"jsonrpc":"2.0","id":11,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"hsjm-deploy-verify-legacy","version":"1.0"}}}'
initialize_response="$(compose exec -T api curl --fail --silent --show-error --include \
  --request POST \
  --header 'Content-Type: application/json' \
  --header 'Accept: application/json, text/event-stream' \
  --data "$initialize_payload" \
  "$base_url/mcp")"

printf '%s' "$initialize_response" | grep -Fq '"result"' || die "MCP initialize 未返回 Result。"

session_id="$(printf '%s\n' "$initialize_response" | tr -d '\r' | awk -F': ' 'tolower($1) == "mcp-session-id" { print $2; exit }')"
session_headers=()
if [[ -n "$session_id" ]]; then
  session_headers=(--header "Mcp-Session-Id: $session_id")
fi

compose exec -T api curl --fail --silent --show-error \
  --request POST \
  --header 'Content-Type: application/json' \
  --header 'Accept: application/json, text/event-stream' \
  --header 'MCP-Protocol-Version: 2025-03-26' \
  "${session_headers[@]}" \
  --data '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  "$base_url/mcp" >/dev/null

tools_response="$(compose exec -T api curl --fail --silent --show-error \
  --request POST \
  --header 'Content-Type: application/json' \
  --header 'Accept: application/json, text/event-stream' \
  --header 'MCP-Protocol-Version: 2025-03-26' \
  "${session_headers[@]}" \
  --data '{"jsonrpc":"2.0","id":12,"method":"tools/list","params":{}}' \
  "$base_url/mcp")"

printf '%s' "$tools_response" | grep -Fq '"diagnose_energy_flow"' || die "MCP 工具列表缺少 diagnose_energy_flow。"
printf '%s' "$tools_response" | grep -Fq '"diagnose_line_stoppage"' || die "MCP 工具列表缺少 diagnose_line_stoppage。"

info "验证通过：Web、API 存活/就绪、MCP 2.0（2026-07-28）、旧协议兼容和两个诊断工具均可访问。"
