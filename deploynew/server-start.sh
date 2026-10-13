#!/usr/bin/env bash
# 服务器端一键启动：以已有 hsjm-ai-api 镜像为基底叠加新程序包 -> compose 启动 -> 健康检查 -> 图片灌卷。
# 用法（在解压出的程序包目录内，如 /opt/hsjm/hsjm-ai-app-<版本>/）：
#   bash server-start.sh [端口号] [图片数据包路径]
#   端口号缺省 = 28000-28999 自动选空闲；图片包缺省 = 自动检测上级目录 appdata-init-*.tar.gz
set -Eeuo pipefail
cd "$(dirname "$0")"

die() { echo "[错误] $1" >&2; exit 1; }

[ -f VERSION ] && [ -f Dockerfile ] && [ -f compose.yaml ] || die "请在解压出的程序包目录内执行。"
VER="$(cat VERSION)"
command -v docker >/dev/null || die "服务器未安装 Docker。"
docker info >/dev/null 2>&1 || die "Docker 未运行或当前用户无权限（sudo usermod -aG docker \$USER 后重新登录）。"
docker compose version >/dev/null 2>&1 || die "缺少 Docker Compose v2。"

PORT="${1:-}"
APPDATA="${2:-}"

# --- 基底镜像：只选"原始基底"（无 hsjm.layered 标签 = 完整离线包构建的）--------
# 每次升级都叠在同一个原始基底上，新镜像只多一层程序（不会链式越叠越厚）。
# 可用环境变量 BASE_TAG 手工指定基底。
if [ -n "${BASE_TAG:-}" ]; then
  BASE="$BASE_TAG"
else
  BASE="$(for t in $(docker images hsjm-ai-api --format '{{.Tag}}' 2>/dev/null | grep -v '^<none>'); do
            L="$(docker image inspect --format '{{index .Config.Labels "hsjm.layered"}}' "hsjm-ai-api:$t" 2>/dev/null || true)"
            [ "$L" = "true" ] || echo "$t"
          done | grep -E '^[0-9]{4}\.[0-9]{2}\.[0-9]{2}' | sort -V | tail -n 1 || true)"
  [ -n "$BASE" ] || BASE="$(for t in $(docker images hsjm-ai-api --format '{{.Tag}}' 2>/dev/null | grep -v '^<none>' | grep -v '^rollback'); do
            L="$(docker image inspect --format '{{index .Config.Labels "hsjm.layered"}}' "hsjm-ai-api:$t" 2>/dev/null || true)"
            [ "$L" = "true" ] || echo "$t"
          done | sort -V | tail -n 1 || true)"
fi
[ -n "$BASE" ] || die "服务器上没有可作基底的原始 hsjm-ai-api 镜像（完整离线包构建的）。首次部署请先用 deploy\\publish-ubuntu.ps1 的完整离线包；之后升级都可用本程序包。"
echo "基底镜像：hsjm-ai-api:$BASE（叠加新程序 -> hsjm-ai-api:$VER）"

# --- 端口：未指定时自动挑 28000-28999 第一个空闲 ------------------------------
if [ -z "$PORT" ]; then
  command -v ss >/dev/null || die "缺少 ss 命令，请手工指定端口：bash server-start.sh <端口>"
  PORT="$(for p in $(seq 28000 28999); do
            ss -lnt 2>/dev/null | grep -q ":$p " || { echo "$p"; break; }
          done)"
fi
[ -n "$PORT" ] || die "28000-28999 没有空闲端口，请手工指定：bash server-start.sh <端口>"
# 指定端口时校验是否空闲（避免容器起来才发现 address in use）
if [ -n "${1:-}" ] && command -v ss >/dev/null 2>&1 && ss -lnt 2>/dev/null | grep -q ":$PORT "; then
  die "端口 $PORT 已被占用，请换一个：bash server-start.sh <其他端口>"
fi

# --- [1/5] 叠加构建新镜像（离线、秒级）---------------------------------------
echo "[1/5] docker build：hsjm-ai-api:$VER（基底 hsjm-ai-api:$BASE）……"
docker build --build-arg BASE="hsjm-ai-api:$BASE" -t "hsjm-ai-api:$VER" .
[ "$?" = "0" ] || die "docker build 失败"

# --- [2/5] 写 .env ------------------------------------------------------------
echo "[2/5] 写入 .env（端口 $PORT）……"
printf 'HSJM_VERSION=%s\nHSJM_HTTP_PORT=%s\n' "$VER" "$PORT" > .env
chmod 600 .env

# --- [3/5] 启动容器 ------------------------------------------------------------
echo "[3/5] 启动容器（host 网络）……"
docker compose -f compose.yaml --env-file .env up -d --force-recreate

# --- [4/5] 等待健康检查 --------------------------------------------------------
echo "[4/5] 等待健康检查（最长 240 秒）……"
CID="$(docker compose -f compose.yaml --env-file .env ps -q api)"
ok=""
for _ in $(seq 1 80); do
  ST="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$CID" 2>/dev/null || true)"
  if [ "$ST" = "healthy" ]; then ok=1; break; fi
  if [ "$ST" = "unhealthy" ]; then break; fi
  sleep 3
done
if [ -z "$ok" ]; then
  echo "容器未进入 healthy，最近日志：" >&2
  docker compose -f compose.yaml --env-file .env logs --tail 60 api >&2 || true
  die "启动失败。常见原因：PostgreSQL/Redis 不可达（/health 要求两者全通）。"
fi

# --- [5/5] 图片数据灌卷 --------------------------------------------------------
echo "[5/5] 迁移图片数据（如有）……"
# 从运行容器读取 uploads 卷的实际名：正式包=hsjm-ai_api-uploads，测试包=hsjm-ai-test_api-uploads
UPLOADS_VOL="$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/app/Uploads"}}{{.Name}}{{end}}{{end}}' "$CID")"
[ -n "$UPLOADS_VOL" ] || die "未能从容器读取 uploads 卷名（$CID 无 /app/Uploads 挂载？）"
if [ -z "$APPDATA" ]; then
  # 自动检测：当前目录 或 上一级目录（上传目录）里最新的 appdata-init-*.tar.gz
  APPDATA="$(ls -1t ./appdata-init-*.tar.gz ../appdata-init-*.tar.gz 2>/dev/null | head -n 1 || true)"
fi
if [ -n "$APPDATA" ] && [ -f "$APPDATA" ]; then
  echo "  使用图片数据包：$APPDATA"
  TMP="$(mktemp -d)"
  tar -xzf "$APPDATA" -C "$TMP"
  [ -d "$TMP/appdata-init" ] || { rm -rf "$TMP"; die "图片包内缺少 appdata-init/ 目录。"; }
  # 2026-09-09 修复:镜像 ENTRYPOINT=hsjm-entrypoint 会吞掉 sh -c 参数并启动整个 API,
  # 导致灌卷从未执行、脚本卡死在前台容器上(用户看到的是应用启动日志)。必须 --entrypoint sh
  # 显式覆盖;-u 0:0 因为 chown 需要 root(镜像 USER=app)。
  docker run --rm --entrypoint sh -u 0:0 \
    -v "$UPLOADS_VOL":/data \
    -v "$TMP/appdata-init":/src:ro \
    "hsjm-ai-api:$VER" \
    -c 'set -e; mkdir -p /data/LineUpImages /data/lineup-imports; [ ! -d /src/LineUp ] || cp -a -n /src/LineUp/. /data/LineUpImages/; [ ! -d /src/lineup-imports ] || cp -a -n /src/lineup-imports/. /data/lineup-imports/; chown -R app:app /data/LineUpImages /data/lineup-imports'
  rm -rf "$TMP"
  N="$(docker run --rm --entrypoint sh -v "$UPLOADS_VOL":/data "hsjm-ai-api:$VER" -c 'ls -1 /data/LineUpImages/images 2>/dev/null | wc -l')"
  echo "  图片已灌卷（$UPLOADS_VOL）：工位图片 $N 张（合并不覆盖，重复执行安全）。"
else
  echo "  未找到图片数据包（找过 ./ 和 ../），跳过（可稍后：bash server-start.sh <端口> <图片包路径>）。"
fi

IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
echo ""
echo "================ 部署完成 ================"
echo " 平台地址：http://${IP:-<服务器IP>}:$PORT"
echo " 日志：docker compose -f compose.yaml --env-file .env logs -f api"
echo " 停止：docker compose -f compose.yaml --env-file .env down"
echo "=========================================="
