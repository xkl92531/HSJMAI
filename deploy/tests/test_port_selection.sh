#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
TEMP_DIR="$(mktemp -d)"
trap 'rm -rf -- "$TEMP_DIR"' EXIT

source "$ROOT_DIR/deploy/scripts/common.sh"

BUNDLE_DIR="$TEMP_DIR"
ENV_FILE="$TEMP_DIR/.env"
VERSION_FILE="$TEMP_DIR/VERSION"
cp "$ROOT_DIR/deploy/.env.example" "$TEMP_DIR/.env.example"

# Simulate a Linux host listener and a Docker-published port.
is_listening_port() {
  [[ "$1" == 28000 ]]
}

docker_publishes_port() {
  [[ "$1" == 28001 ]]
}

chosen="$(select_http_port)"
[[ "$chosen" == 28002 ]]
grep -Fq 'HSJM_HTTP_PORT=28002' "$ENV_FILE"
[[ "$(stat -c '%a' "$ENV_FILE")" == 600 ]]

printf 'port-selection=ok selected=%s mode=0600\n' "$chosen"
