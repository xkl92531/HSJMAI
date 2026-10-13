#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
ROOT_DIR="$(cd -- "$SCRIPT_DIR/../.." && pwd -P)"
RELEASE_DIR="$ROOT_DIR/release"

die() {
  printf '错误：%s\n' "$1" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "缺少命令：$1"
}

VERSION="${1:-}"
[[ -n "$VERSION" ]] || die "用法：$0 <版本号>"
[[ "$VERSION" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]] || die "版本号只能包含字母、数字、点、下划线和连字符。"

for command_name in docker sha256sum tar find sort xargs; do
  require_command "$command_name"
done

docker info >/dev/null 2>&1 || die "Docker Engine 未运行，或当前用户无权访问 Docker。"
docker buildx version >/dev/null 2>&1 || die "需要 Docker Buildx。"

BUNDLE_NAME="hsjm-ai-linux-amd64-$VERSION"
BUNDLE_DIR="$RELEASE_DIR/$BUNDLE_NAME"
ARCHIVE_NAME="$BUNDLE_NAME.tar.gz"
ARCHIVE_PATH="$RELEASE_DIR/$ARCHIVE_NAME"

mkdir -p "$RELEASE_DIR"
[[ ! -e "$BUNDLE_DIR" ]] || die "输出目录已存在：$BUNDLE_DIR"
[[ ! -e "$ARCHIVE_PATH" ]] || die "输出文件已存在：$ARCHIVE_PATH"

cleanup_failed_bundle() {
  local exit_code=$?
  if (( exit_code != 0 )) && [[ "$BUNDLE_DIR" == "$RELEASE_DIR"/hsjm-ai-linux-amd64-* ]]; then
    rm -rf -- "$BUNDLE_DIR"
  fi
  exit "$exit_code"
}
trap cleanup_failed_bundle EXIT

printf '构建 API 镜像：hsjm-ai-api:%s\n' "$VERSION"
docker buildx build \
  --platform linux/amd64 \
  --load \
  --file "$ROOT_DIR/deploy/docker/api.Dockerfile" \
  --tag "hsjm-ai-api:$VERSION" \
  "$ROOT_DIR"

image_platform="$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "hsjm-ai-api:$VERSION")"
[[ "$image_platform" == linux/amd64 ]] || die "hsjm-ai-api:$VERSION 架构不是 linux/amd64：$image_platform"

mkdir -p "$BUNDLE_DIR/images"
docker image save \
  --output "$BUNDLE_DIR/images/hsjm-ai-api-$VERSION-linux-amd64.tar" \
  "hsjm-ai-api:$VERSION"

cp "$ROOT_DIR/deploy/compose.yaml" "$BUNDLE_DIR/compose.yaml"
cp "$ROOT_DIR/deploy/.env.example" "$BUNDLE_DIR/.env.example"
cp "$ROOT_DIR/deploy/README-Linux离线部署.md" "$BUNDLE_DIR/README-Linux离线部署.md"
if [[ -f "$ROOT_DIR/docs/HSJM-MCP流式诊断与Docker部署手册.docx" ]]; then
  cp "$ROOT_DIR/docs/HSJM-MCP流式诊断与Docker部署手册.docx" "$BUNDLE_DIR/HSJM-MCP流式诊断与Docker部署手册.docx"
fi
cp "$ROOT_DIR/deploy/scripts/"*.sh "$BUNDLE_DIR/"
chmod 0755 "$BUNDLE_DIR/"*.sh
printf '%s\n' "$VERSION" > "$BUNDLE_DIR/VERSION"

(
  cd "$BUNDLE_DIR"
  find . -type f ! -name SHA256SUMS -print0 \
    | sort -z \
    | xargs -0 sha256sum > SHA256SUMS
)

tar -C "$RELEASE_DIR" -czf "$ARCHIVE_PATH" "$BUNDLE_NAME"
(
  cd "$RELEASE_DIR"
  sha256sum "$ARCHIVE_NAME" > "$ARCHIVE_NAME.sha256"
)

trap - EXIT
printf '离线目录：%s\n' "$BUNDLE_DIR"
printf '离线压缩包：%s\n' "$ARCHIVE_PATH"
printf '传输校验文件：%s.sha256\n' "$ARCHIVE_PATH"
