# HSJM Linux 单容器离线部署手册

## 1. 交付内容

本离线包用于把原先部署在 IIS 上的 HSJM API 与 Vue Web 整体迁移到 Linux Docker。

运行架构只有一个容器：

- Kestrel 同时提供 Vue 页面、REST API、SignalR、Swagger、Hangfire 和 MCP Streamable HTTP。
- 镜像内部包含 .NET 10 Runtime、API、编译后的 Vue、Python 虚拟环境、Playwright Chromium、中文字体和五类 Skill 依赖。
- Node.js、pnpm 和 .NET SDK 只参与制作镜像，不进入最终运行镜像。
- 不使用 Nginx。
- 不打包 PostgreSQL 和 Redis；PostgreSQL 使用 `appsettings.json` 中现有 `192.168.6.75` 连接。
- 针对目标机 `192.168.6.75`，API 使用 Linux host 网络访问本机 `127.0.0.1:6379`，因此无需修改已有 Redis protected-mode。
- 首次启动会在 Linux 服务器本机从 `28000-28999` 自动选择空闲端口，并以 `0600` 权限写入 `.env`。

目标 Linux 启动时不会从 NuGet、npm、pip 或镜像仓库下载应用依赖。

### 在 Windows 打包机使用 D 盘离线缓存制作交付包

当打包机已经准备好 `D:\sdk` 时，使用 PowerShell 离线入口：

```powershell
Set-Location 'E:\项目\HSJM_AI_PLC'

# 只校验缓存、镜像归档、NuGet 子集和项目输入指纹
.\deploy\offline\build-bundle-offline.ps1 `
    -Version 2026.09.03 `
    -SdkCacheRoot 'D:\sdk' `
    -ValidateOnly

# 使用缓存制作完整交付包；所有 RUN 指令强制禁网
.\deploy\offline\build-bundle-offline.ps1 `
    -Version 2026.09.03 `
    -SdkCacheRoot 'D:\sdk'
```

脚本会自动校验并在本机缺失时从 `D:\sdk\docker\images` 导入基础镜像，使用 `D:\sdk\nuget\hsjm-packages` 的 HSJM 专用 NuGet 子集，并通过 `deploy/docker/api.offline.Dockerfile` 执行 `--network=none` 构建。输出仍位于 `release`，文件名和 Linux 安装方式与原 `build-bundle.sh` 一致。

需要忽略 Docker 内部构建层、完整重做一次断网验收时，加 `-NoCache`。该选项会显著增加前端编译时间，但不会恢复已删除的 `D:\sdk\docker\buildx-cache`。

若项目依赖清单变化，缓存输入指纹或 NuGet 子集校验会直接失败；应先刷新 `D:\sdk`，不能删除 `--network=none` 或改用在线源绕过失败。缓存使用细节见 `D:\sdk\README-HSJM-Docker-Cache.md`。

## 2. 服务器要求

目标服务器必须满足：

- Linux `x86_64/amd64`。
- 已安装并启动 Docker Engine。
- 已安装 Docker Compose v2，即 `docker compose` 命令可用。
- 当前用户可执行 Docker，或者在下述命令前加 `sudo`。
- 在 `192.168.6.75` 上能访问 PostgreSQL 现有地址和本机 Redis `127.0.0.1:6379`。
- 磁盘至少预留“离线压缩包大小的 3 倍”用于解压、`docker load` 和容器可写层。

检查：

```bash
uname -m
docker version
docker compose version
docker info
```

`uname -m` 应返回 `x86_64`。

## 3. 把文件传到 Linux

需要传输两个文件：

```text
hsjm-ai-linux-amd64-<版本>.tar.gz
hsjm-ai-linux-amd64-<版本>.tar.gz.sha256
```

如需把既有 LineUp 图片点位数据迁移到新部署（数据库中已有 `Lup_StationImage` 记录时），另加可选数据包：

```text
appdata-init.tar.gz   # LineUp 受控图片 + 在途导入批次源文件，见第 9 节初始化说明
```

示例：

```bash
scp hsjm-ai-linux-amd64-2026.08.13.tar.gz user@服务器IP:/opt/hsjm/
scp hsjm-ai-linux-amd64-2026.08.13.tar.gz.sha256 user@服务器IP:/opt/hsjm/
```

## 4. 完整性校验和解压

```bash
cd /opt/hsjm
sha256sum --check hsjm-ai-linux-amd64-2026.08.13.tar.gz.sha256
tar -xzf hsjm-ai-linux-amd64-2026.08.13.tar.gz
cd hsjm-ai-linux-amd64-2026.08.13
```

校验必须显示 `OK`。如果失败，不要执行安装，应重新传输文件。

目录包含：

```text
images/hsjm-ai-api-<版本>-linux-amd64.tar
compose.yaml
.env.example
install.sh
deploy.sh
server-deploy.sh
migrate-appdata.sh
start.sh
stop.sh
status.sh
verify.sh
uninstall.sh
VERSION
SHA256SUMS
README-Linux离线部署.md
```

## 5. 一键安装和启动

不需要手工创建 `.env`。推荐直接执行**服务器端一键脚本**（装镜像 + 启动 + 验证 + 自动迁移同级目录的图片数据包）：

```bash
chmod +x ./*.sh
./server-deploy.sh
```

不需要图片迁移时，等价入口是：

```bash
chmod +x ./*.sh && ./deploy.sh
```

脚本分别完成：

1. `deploy.sh` 调用 `install.sh` 校验 `SHA256SUMS` 并通过 `docker image load` 导入镜像。
2. `start.sh` 使用 `ss`/Docker 端口表选出空闲端口，持久化到 `.env`，并使用 `--force-recreate` 直接覆盖同一套 `hsjm-ai` API 容器。
3. `verify.sh` 验证 API 存活、PostgreSQL `SELECT 1`、Redis `PING`、首页、MCP 2.0 `server/discover`、旧协议兼容和两个诊断工具。
4. 终端最后直接打印实际平台地址。

程序启动时会在 MCP Manager 中 upsert 名为 `hsjm-diagnosis` 的服务：存在时保留原 ID 并更新 URL，不存在时才新增。如果历史上已有多条同名记录，会把 `AI_Tool_Definition.McpServerID` 迁移到保留的 ID，再软删除多余同名记录。

重复部署会强制重建旧容器，但保留平台 Docker 卷，不删除外部 PostgreSQL/Redis 数据。

访问地址示例（以终端实际输出为准）：

```text
http://192.168.6.75:28002
```

## 6. appsettings.json 与可选 .env

镜像保留 API 原有 `appsettings.json`。数据库账号、密码、模型配置、JWT 与平台参数仍直接读取该文件；脚本自动生成的 `.env` 只保存运行端口等可选值。

当前外部服务边界：

- PostgreSQL：`DefultDataBase`，主机 `192.168.6.75`。
- Redis：容器在 `192.168.6.75` 上通过 host 网络访问 `127.0.0.1:6379`，对应同一个现有 Redis。

启动不会自动创建或修改 Hangfire 表结构（`PrepareSchemaIfNecessary=false`）。外部业务库应已经包含当前 IIS 平台使用的 Hangfire schema；若没有，必须由数据库管理员在明确授权的维护窗口单独初始化，不能让迁移容器自行执行 DDL。

如只需修改宿主机端口、时区或启动等待时间：

```bash
cp .env.example .env
chmod 0600 .env
vi .env
./start.sh
```

示例内容：

```dotenv
HSJM_HTTP_PORT=28505
HSJM_TIME_ZONE=Asia/Shanghai
HSJM_START_TIMEOUT_SECONDS=240
```

ASP.NET Core 环境变量仍然可以覆盖 `appsettings.json`，但部署脚本不要求填写任何数据库或模型环境变量。

## 7. 常用运维命令

查看状态：

```bash
./status.sh
```

查看脱敏后的最近日志：

```bash
./logs.sh
```

持续查看日志：

```bash
./logs.sh --follow
```

`logs.sh` 会过滤常见数据库密码、API/JWT 密钥和 Bearer Token；不要用原始 `docker compose logs` 输出替代它后再把日志发送给他人。

停止但保留容器和卷：

```bash
./stop.sh
```

再次启动：

```bash
./start.sh
```

删除容器和网络但保留业务文件卷：

```bash
./uninstall.sh
```

删除容器、网络和平台文件卷：

```bash
./uninstall.sh --purge
```

`--purge` 会要求在交互式终端输入 `PURGE`。它不会删除或修改外部 PostgreSQL、Redis，但会删除容器保存的日志、上传、动态 Skill 和生成文件。

## 8. MCP 地址

外部 Agent 使用：

```text
http://Linux服务器IP:<.env中的HSJM_HTTP_PORT>/mcp
```

两个诊断工具保持为：

- `diagnose_energy_flow`
- `diagnose_line_stoppage`

MCP Manager 注册名固定为 `hsjm-diagnosis`，类型为 `HTTP`，连接配置由程序启动时自动覆盖为当前实际端口的 `/mcp` URL。

- MCP C# SDK：`ModelContextProtocol 2.0.0` / `ModelContextProtocol.AspNetCore 2.0.0`
- 默认协议：`2026-07-28`
- 服务标识：`hsjm-diagnosis / 2.0.0`
- 传输：Stateless Streamable HTTP
- 兼容性：仍接受旧客户端的 `initialize` 协商

使用 `2026-07-28` 的 HTTP 客户端时，每个 JSON-RPC 请求还必须发送与正文匹配的标准路由头：

- `Mcp-Method: server/discover`、`Mcp-Method: tools/list` 或 `Mcp-Method: tools/call`
- `tools/call` 还要发送 `Mcp-Name: diagnose_energy_flow` 或 `Mcp-Name: diagnose_line_stoppage`

官方 MCP 2.0 客户端 SDK 会自动添加这些请求头；只有手写 `curl` 或自研 HTTP 客户端时需要显式处理。

请求带唯一 `progressToken` 时，服务端通过 `notifications/progress` 同时返回阶段状态和安全结果片段；结果片段的 `message` 固定以 `结果片段：` 开头。最终 Result 仍只返回一次，并保持原诊断业务结构。去掉 Nginx 后，Progress 直接由 Kestrel 发送，不经过反向代理缓冲。

`diagnose_line_stoppage` 开启 `includeConstraintAnalysis` 时，内部仍调用原 EnergyFlow 流程；子流程进度映射到父流程 70%-82%，不会产生第二个最终 Result。测试页面地址为 `http://服务器IP:<端口>/#/mcp-progress-test`，默认打开“实时结果流”，并把最终完整结果单独显示在完成后标签页。

远程 MCP 客户端必须在 HTTP 请求头携带平台现有 JWT：

```text
Authorization: Bearer <登录平台后取得的JWT>
```

只有服务器本机回环地址可免 JWT 执行部署验收。不要把 `/mcp` 直接暴露到互联网；在 Linux 防火墙或上级网关中只允许受控的外部 Agent 地址访问实际选中端口。

## 9. 持久化目录

Compose 创建：

- `hsjm-ai_api-logs`
- `hsjm-ai_api-wwwroot`
- `hsjm-ai_api-uploads`
- `hsjm-ai_api-parser-templates`
- `hsjm-ai_api-generated`

`api-wwwroot` 首次启动会写入镜像内置 Skill 和 Vue 文件。升级镜像后，入口脚本会更新 Vue 构建产物，但不会覆盖现场已经修改的 Skill 文件。

列出卷：

```bash
docker volume ls | grep hsjm-ai
```

### LineUp 图片与导入数据（api-uploads 卷）

容器为只读根文件系统，compose 通过环境变量把两类运行数据映射到 `api-uploads` 卷（挂载点 `/app/Uploads`），容器重建/镜像升级不丢失（`uninstall.sh --purge` 会删除）：

- `LineUpImages/`：图片点位受控存储（`Lup_StationImage` 引用的 `images/`、`graph-node-images/`；`WebConfig__LineUp__StorageRoot=/app/Uploads/LineUpImages`）。数据库是共享的，**不初始化此目录则所有历史图片 404**。
- `lineup-imports/`：GRAPH 导入源文件暂存（在途批次的解析/刷新预览依赖；`WebConfig__LineUp__ImportRoot=/app/Uploads/lineup-imports`）。

首次部署后如需迁移历史数据，传输 `appdata-init.tar.gz`（内含 `appdata-init/LineUp` 与/或 `appdata-init/lineup-imports`）并执行：

```bash
tar -xzf appdata-init.tar.gz
HSJM_VERSION="$(cat VERSION)"
docker run --rm -v hsjm-ai_api-uploads:/data -v "$(pwd)/appdata-init:/src:ro" \
  hsjm-ai-api:"$HSJM_VERSION" sh -c '
    set -e
    mkdir -p /data/LineUpImages /data/lineup-imports
    [ ! -d /src/LineUp ]         || cp -a -n /src/LineUp/. /data/LineUpImages/
    [ ! -d /src/lineup-imports ] || cp -a -n /src/lineup-imports/. /data/lineup-imports/
    chown -R app:app /data/LineUpImages /data/lineup-imports
  '
```

说明：`cp -a -n` 为合并语义（不覆盖服务器上线后新增的数据，重复迁移幂等）；`chown app:app` 修正属主——容器以 `USER app` 运行，root 属主目录会导致新图片写入 422（`ImageStorageFailed`）。`docker cp` 方式已废弃：受容器 `read_only` 限制且会留下 root 属主文件。

验证：登录平台打开 LineUp 工站图片页面（或带 JWT 请求 `/LineUp/ImageMarks/Images/{id}/Content`），历史图片应正常显示。注意：appdata-init 包中的 `LineUp/manifest.json` 仅是备份核对清单，留在卷内不影响运行。

## 10. 升级

将新版本压缩包传到独立目录，然后执行：

```bash
sha256sum --check hsjm-ai-linux-amd64-新版本.tar.gz.sha256
tar -xzf hsjm-ai-linux-amd64-新版本.tar.gz
cd hsjm-ai-linux-amd64-新版本
chmod +x ./*.sh
./install.sh
./start.sh
./verify.sh
```

Compose 使用固定版本标签，新包的 `VERSION` 决定启动哪张镜像。具名卷名称保持不变，升级不会自动删除日志、上传、Skill 或生成文件。

## 11. 回滚

进入旧版本解压目录：

```bash
cd /opt/hsjm/hsjm-ai-linux-amd64-旧版本
./start.sh
./verify.sh
```

前提是旧镜像尚未从 Docker 中删除。查看已有版本：

```bash
docker image ls hsjm-ai-api
```

## 12. 排障

### 容器一直 unhealthy

```bash
./status.sh
./logs.sh
```

常见原因：

- Linux 无法访问 `192.168.6.75:5432`。
- PostgreSQL账号、密码、库名或授权不正确。
- 目标机无法访问 `appsettings.json` 中配置的 Redis 地址。
- Redis 端口虽然能建立 TCP，但标准 `PING` 没有返回成功响应。
- 外部库缺少已有 Hangfire schema/table；迁移容器不会自动建表。
- 自动范围 `28000-28999` 全部被占用。

网络检查（系统安装了 `nc` 时）：

```bash
nc -vz 192.168.6.75 5432
nc -vz 127.0.0.1 6379
```

### 页面返回 404

检查前端是否在运行卷中：

```bash
HSJM_VERSION="$(cat VERSION)" docker compose -f compose.yaml exec -T api test -f /app/wwwroot/index.html
HSJM_VERSION="$(cat VERSION)" docker compose -f compose.yaml exec -T api ls -la /app/wwwroot
```

### MCP 无响应

```bash
PORT="$(sed -n 's/^HSJM_HTTP_PORT=//p' .env)"
curl -i -X POST "http://127.0.0.1:$PORT/mcp" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: server/discover' \
  --data '{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"manual-check","version":"2.0"},"io.modelcontextprotocol/clientCapabilities":{}}}}'
```

上面的命令必须在 Linux 服务器本机执行。若从其他主机测试，请额外添加 `-H 'Authorization: Bearer <JWT>'`；未携带有效 JWT 时返回 401 属于预期安全行为。

如需验证旧客户端兼容，可继续使用 `initialize` 并传入 `2025-03-26`；正式接入的新客户端应优先使用 `2026-07-28`。

### 固定使用指定端口

创建可选 `.env`：

```bash
cp .env.example .env
chmod 0600 .env
sed -i 's/^HSJM_HTTP_PORT=.*/HSJM_HTTP_PORT=28505/' .env
./start.sh
```

启动前脚本会在服务器本机复查该端口；被占用时会拒绝启动。然后访问 `http://服务器IP:28505`。

## 13. 安全说明

为实现“无需 `.env` 即可直接迁移”，镜像包含当前 `appsettings.json`。因此：

- 镜像 TAR 和外层压缩包等同于敏感部署介质。
- 只能传给被授权的内网服务器和运维人员。
- 不要上传公共网盘、公共镜像仓库或公共 Git 仓库。
- SHA-256 只能证明文件未损坏，不能加密文件。
- 如需跨不可信网络传输，应额外使用组织批准的加密介质或加密通道。
