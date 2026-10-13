#!/usr/bin/env bash
# 将 appdata-init.tar.gz（LineUp 工站图片 + GRAPH 导入源）灌入 hsjm-ai_api-uploads 卷。
#
# 用法（在解压后的离线包目录内执行）：
#   ./migrate-appdata.sh /opt/hsjm/appdata-init-<版本>.tar.gz
#
# 行为：
#   - 包内顶层目录必须是 appdata-init/（含 LineUp/ 与/或 lineup-imports/）
#   - LineUp/.        -> 卷内 /app/Uploads/LineUpImages  （WebConfig__LineUp__StorageRoot）
#   - lineup-imports/.-> 卷内 /app/Uploads/lineup-imports（WebConfig__LineUp__ImportRoot）
#   - cp -a -n 合并语义：不覆盖服务器上线后新增的数据，重复执行幂等
#   - chown -R app:app 修正属主（容器以 USER app 运行，root 属主目录会致保存图片 422）
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$SCRIPT_DIR/common.sh"

require_bundle_layout
require_docker

if [[ $# -ne 1 ]]; then
  die "用法：./migrate-appdata.sh <appdata-init.tar.gz 的路径>"
fi

ARCHIVE="$1"
[[ -f "$ARCHIVE" ]] || die "找不到图片数据包：$ARCHIVE"

VERSION="$(<"$VERSION_FILE")"
[[ -n "$VERSION" ]] || die "VERSION 为空。"
IMAGE="hsjm-ai-api:$VERSION"
VOLUME="hsjm-ai_api-uploads"

info "正在校验图片数据包并解压：$ARCHIVE"
EXTRACT_DIR="$(mktemp -d "$BUNDLE_DIR/.appdata-extract.XXXXXX")"
cleanup() { rm -rf "$EXTRACT_DIR"; }
trap cleanup EXIT

tar -xzf "$ARCHIVE" -C "$EXTRACT_DIR"
[[ -d "$EXTRACT_DIR/appdata-init" ]] || die "数据包内缺少 appdata-init/ 顶层目录（打包契约不符）。"

info "正在灌卷（$VOLUME，合并不覆盖 + 属主修正 app:app）……"
# 2026-09-09 修复:镜像 ENTRYPOINT=hsjm-entrypoint 会吞掉 sh -c 参数并启动整个 API,
# 灌卷从未执行。必须 --entrypoint sh 显式覆盖;-u 0:0 因为 chown 需要 root(镜像 USER=app)。
docker run --rm --entrypoint sh -u 0:0 \
  -v "$VOLUME":/data \
  -v "$EXTRACT_DIR/appdata-init":/src:ro \
  "$IMAGE" \
  -c 'set -e; mkdir -p /data/LineUpImages /data/lineup-imports; [ ! -d /src/LineUp ] || cp -a -n /src/LineUp/. /data/LineUpImages/; [ ! -d /src/lineup-imports ] || cp -a -n /src/lineup-imports/. /data/lineup-imports/; chown -R app:app /data/LineUpImages /data/lineup-imports' \
  || die "灌卷失败。常见原因：镜像 $IMAGE 未加载（先执行 ./install.sh）。"

IMAGE_COUNT="$(docker run --rm --entrypoint sh -v "$VOLUME":/data "$IMAGE" -c 'ls -1 /data/LineUpImages/images 2>/dev/null | wc -l')"
IMPORT_COUNT="$(docker run --rm --entrypoint sh -v "$VOLUME":/data "$IMAGE" -c 'ls -1 /data/lineup-imports 2>/dev/null | wc -l')"
info "图片数据迁移完成：工位图片 images/ 共 $IMAGE_COUNT 个，导入批次共 $IMPORT_COUNT 个。"
echo "APPDATA_IMAGE_COUNT=$IMAGE_COUNT"
echo "APPDATA_IMPORT_BATCH_COUNT=$IMPORT_COUNT"
echo "APPDATA_MIGRATED=1"
