# HSJM 平台部署（三步 · 本地无需 Docker）

> 本地只用 `dotnet` + `pnpm` 打程序包；服务器上用 Docker 跑（以已有 hsjm-ai-api 镜像为基底叠加新程序，离线、秒级构建）。

## 第 1 步 · 本地打包（Windows，无需 Docker）

```powershell
powershell -ExecutionPolicy Bypass -File .\deploynew\build-dotnet.ps1
# 可选：-Version 2026.09.10.2   -SkipWebBuild（沿用 web\dist）   -Test（测试版）
```

`-Test` = 打包测试版 compose（数据库强制 `hsjm_ai_new_test`、Redis db1、关闭 MCP 自注册，与正式版数据级隔离）；缺省打包正式版 compose（数据库随镜像内 appsettings.json）。**都不需要手工上传 compose 文件**。

产物在 `deploynew\`（打包完自动清理临时目录，只留最终压缩包）：

| 文件 | 说明 |
|---|---|
| `hsjm-ai-app-<版本>.zip` | 程序包（后端 publish + 前端 dist + Dockerfile + compose + 启动脚本） |
| `hsjm-ai-app-<版本>.zip.sha256` | 校验文件 |
| `appdata-init-<版本>.tar.gz` | LineUp 工站图片数据（`publish_images\` 自动打包） |

## 第 2 步 · 自己上传到服务器（示例 /opt/hsjm）

```bash
scp deploynew/hsjm-ai-app-<版本>.zip deploynew/hsjm-ai-app-<版本>.zip.sha256 \
    deploynew/appdata-init-<版本>.tar.gz hsjm@192.168.6.75:/opt/hsjm/
```

## 第 3 步 · 服务器上一条命令

```bash
cd /opt/hsjm
sha256sum --check hsjm-ai-app-<版本>.zip.sha256
unzip -o hsjm-ai-app-<版本>.zip -d hsjm-ai-app-<版本> && cd hsjm-ai-app-<版本>
bash server-start.sh
```

自动完成：**以服务器已有的 hsjm-ai-api 镜像为基底 docker build 新程序层（离线秒级）→ 选端口（28000-28999）→ compose 启动 → 健康检查 → 图片灌卷**，最后打印平台地址。

可选参数：`bash server-start.sh 28500`（指定端口）、`bash server-start.sh 28500 ../appdata-init-<版本>.tar.gz`（指定图片包）。

> **部署目录任意**：不要求 /opt/hsjm——脚本按自己所在目录定位，图片包自动检测的是解压目录及其上一级。同一服务器上正式包（项目 `hsjm-ai`，容器 `hsjm-ai-api-1`）与测试包（项目 `hsjm-ai-test2`，容器 `hsjm-ai-test2-api-1`）互不干扰、可共存；老测试实例（项目 `hsjm-ai-test`，容器 `hsjm-ai-test-api-1`）也不受新部署影响。

> 前提：服务器上已有任一 `hsjm-ai-api` 镜像（6.75 上有）。全新服务器首次部署仍需用 `deploy\publish-ubuntu.ps1` 的完整离线包；之后的每次升级都可用本程序包。

## 日常操作（服务器，程序包目录内）

```bash
docker compose -f compose.yaml --env-file .env logs -f api   # 看日志
docker compose -f compose.yaml --env-file .env down          # 停止
docker compose -f compose.yaml --env-file .env up -d         # 启动
```

升级 = 重跑第 1~3 步（数据卷保留，图片合并不覆盖）。

## 排障速查

| 现象 | 处理 |
|---|---|
| 服务器没有 hsjm-ai-api 镜像 | 首次部署用 `deploy\publish-ubuntu.ps1` 完整离线包 |
| 一直不 healthy | 看日志；多为 PostgreSQL/Redis 不可达或库口令错 |
| 历史图片 404 | 图片包没传/没灌：`bash server-start.sh <端口> ../appdata-init-<版本>.tar.gz` |
| 保存图片 422 | 卷属主 root：`docker volume ls \| grep api-uploads` 查卷名（正式=`hsjm-ai_api-uploads`，测试=`hsjm-ai-test2_api-uploads`），再 `docker run --rm -v <卷名>:/data hsjm-ai-api:<版本> chown -R app:app /data/LineUpImages` |
| 数据库不对 | 镜像随 appsettings.json；正式/测试由打包时的仓库配置决定（或沿用基底镜像环境变量） |
