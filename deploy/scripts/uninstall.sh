#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/common.sh"

require_docker
require_bundle_layout
validate_optional_env_file

case "${1:-}" in
  "")
    compose down --remove-orphans
    info "容器和网络已移除；日志、Web/Skill、上传及生成文件卷仍保留。"
    ;;
  --purge)
    [[ -t 0 ]] || die "--purge 只能在交互式终端执行。"
    printf '此操作将永久删除日志、Web/Skill、上传及生成文件卷。请输入 PURGE 继续：' >&2
    read -r confirmation
    [[ "$confirmation" == PURGE ]] || die "确认文本不匹配，已取消。"
    compose down --volumes --remove-orphans
    info "容器、网络和平台持久化卷已删除；外部数据库未被操作。"
    ;;
  *)
    die "用法：$0 [--purge]"
    ;;
esac
