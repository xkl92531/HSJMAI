#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/common.sh"

require_bundle_layout
require_docker
validate_optional_env_file

if [[ "${1:-}" == "--follow" ]]; then
  compose logs --follow api 2>&1 | redact_stream
else
  compose logs --tail "${HSJM_LOG_TAIL:-200}" api 2>&1 | redact_stream
fi
