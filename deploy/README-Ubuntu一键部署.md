# HSJM 设备诊断平台 Ubuntu 一键部署手册

本手册对应一键部署脚本 [`deploy/publish-ubuntu.ps1`](./publish-ubuntu.ps1)：在 **Windows 打包机**上执行一条命令，自动完成「构建 Linux 镜像 → 打离线包 → 上传 Ubuntu 服务器 → 远端安装启动 → 健康验证 → 回传平台地址」全流程。

两种用法任选：

| 模式 | 命令 | 脚本与服务器的关系 |
|---|---|---|
| 全自动（默认） | `.\deploy\publish-ubuntu.ps1` | 脚本经 SSH/SCP 直连服务器：预检 → 上传 → 远端执行安装启动验证 |
| **手工上传** | `.\deploy\publish-ubuntu.ps1 -LocalOnly` | **脚本完全不连接服务器**：只本地打包并打印指引；上传由你自己做，服务器上执行包内自带的 `./server-deploy.sh` |

与既有手工流程的关系：

- 服务器端行为与 [README-Linux离线部署.md](./README-Linux离线部署.md) **完全一致**（复用离线包内自带的 `deploy.sh = install.sh + start.sh + verify.sh` 与 `server-deploy.sh = deploy.sh + 图片迁移`），一键脚本只是把"打包-传输-安装"串联自动化。
- 交付物不变：仍是 `hsjm-ai-linux-amd64-<版本>.tar.gz`（单容器：Kestrel 同端口提供 Vue 页面 + REST API + SignalR + Swagger + Hangfire + MCP，不打包 PostgreSQL/Redis）。

---

## 目录

1. [一键流程总览](#1-一键流程总览)
2. [环境要求](#2-环境要求)
3. [快速开始](#3-快速开始)
4. [SSH 连接与免密配置](#4-ssh-连接与免密配置)
5. [参数详解](#5-参数详解)
6. [构建模式说明](#6-构建模式说明)
7. [部署后自动验证内容](#7-部署后自动验证内容)
8. [固定端口与 .env 覆盖](#8-固定端口与-env-覆盖)
9. [升级与回滚](#9-升级与回滚)
10. [图片资源如何被包含（默认自动）](#10-图片资源如何被包含默认自动无需手工操作)
11. [日常运维（服务器侧）](#11-日常运维服务器侧)
12. [排障](#12-排障)
13. [安全说明](#13-安全说明)

---

## 1. 一键流程总览

```text
Windows 打包机                                   Ubuntu 服务器 (x86_64)
─────────────────────                            ─────────────────────
publish-ubuntu.ps1
 │
 ├─[1] 构建/定位离线包
 │    Online  : docker buildx 构建 hsjm-ai-api:<版本>
 │    Offline : 调 build-bundle-offline.ps1（D:\sdk 缓存，断网构建）
 │    Archive : 直接复用已有 tar.gz
 │    产物: release/hsjm-ai-linux-amd64-<版本>.tar.gz + .sha256
 │
 ├─[1.5] 图片数据自动打包（缺省开启，-NoAppData 跳过）
 │    检测仓库根目录 publish_images/：
 │      LineUp/          工站图片点位（images/、graph-node-images/）
 │      lineup-imports/  GRAPH 导入源批次
 │    产物: release/appdata-init-<版本>.tar.gz
 │
 ├─[2] 服务器预检 (ssh)          ─────────────▶  架构 x86_64 / docker / compose v2
 │                                               部署目录可写 / 磁盘 ≥ 3 倍包大小
 │
 ├─[3] 上传 (scp)                ─────────────▶  /opt/hsjm/hsjm-ai-linux-amd64-<版本>.tar.gz
 │                                               /opt/hsjm/appdata-init-<版本>.tar.gz（如有）
 │
 └─[4] 远端一键部署 (ssh)        ─────────────▶  ① sha256sum --check 校验
     实时透传远端输出                              ② tar 解压 + chmod +x
     结束回传 PLATFORM_URL                        ③ (可选) 按参数生成 .env
                                                  ④ ./deploy.sh:
                                                     install.sh  → docker image load
                                                     start.sh    → 选端口 + compose up
                                                     verify.sh   → API/MCP 全量验证
                                                   ⑤ (如有) 图片数据灌卷 hsjm-ai_api-uploads：
                                                      LineUpImages/ + lineup-imports/
                                                      （合并不覆盖 + chown app:app + 计数回显）
                                                  ⑥ 打印 PLATFORM_URL=http://IP:PORT
```

任一步失败即中止并给出错误定位；远端输出实时透传到本地终端。

> 手工上传模式（`-LocalOnly`）不走这张图的后半段：脚本在 [1.5] 之后即结束并打印指引，[2] 上传由你自己完成，[4] 由你在服务器上执行包内 `./server-deploy.sh`（内部同样是 install + start + verify + 图片灌卷）。

---

## 2. 环境要求

### 2.1 Windows 打包机

| 项 | 要求 | 检查命令 |
|---|---|---|
| 操作系统/Shell | Windows 10 1809+，Windows PowerShell 5.1 或 PowerShell 7+ | `$PSVersionTable.PSVersion` |
| OpenSSH 客户端 | `ssh` / `scp` 可用（Win10 起自带） | `ssh -V` |
| Docker（构建时） | Docker Desktop 已启动，`docker` / `buildx` 可用；`-Archive` 模式不需要 | `docker version` |
| 磁盘 | `release/` 目录约需 镜像体积 × 2（构建产物 + 压缩包） | — |
| 网络（Online 构建时） | 能访问 npm / NuGet / pypi（Docker 内拉取依赖） | — |
| 离线缓存（Offline 构建时） | `D:\sdk` 缓存（可 `-SdkCacheRoot` 指定） | `Test-Path D:\sdk` |

### 2.2 Ubuntu 服务器

| 项 | 要求 | 检查命令 |
|---|---|---|
| 架构 | x86_64 / amd64 | `uname -m` |
| Docker Engine | 已安装并运行 | `docker version` |
| Docker Compose v2 | `docker compose` 子命令可用 | `docker compose version` |
| 用户权限 | 当前 SSH 用户可执行 docker（在 `docker` 组），且对部署目录有写权限 | `docker info` |
| 部署目录 | 默认 `/opt/hsjm`（自动创建；不可写时见[排障](#12-排障)） | `test -w /opt/hsjm && echo ok` |
| 磁盘 | ≥ 离线包大小 × 3（解压 + docker load + 容器可写层） | `df -h /opt/hsjm` |
| 外部服务 | 能访问 PostgreSQL（`appsettings.json` 内配置的地址）与 Redis | `nc -vz <PG主机> 5432` |
| SSH | 开放 22 端口（或 `-ServerPort` 指定），密码或公钥认证 | — |

**全新 Ubuntu 服务器安装 Docker（管理员一次性操作）：**

```bash
# 以 Ubuntu 22.04/24.04 为例
sudo apt-get update
sudo apt-get install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
  https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# 让部署用户（如 hsjm）免 sudo 使用 docker，重新登录后生效
sudo usermod -aG docker hsjm

# 提前创建部署目录并授权
sudo mkdir -p /opt/hsjm && sudo chown hsjm:hsjm /opt/hsjm
```

> 内网无法访问 docker 官方源时，可使用国内镜像源或离线 deb 包，本文不展开。

---

## 3. 快速开始

在**仓库根目录**（`E:\HSJM_AI_PLC`）打开 PowerShell，执行：

```powershell
# 最简形式：自动版本号(yyyy.MM.dd)、Auto 构建模式、默认服务器 hsjm@192.168.6.75、目录 /opt/hsjm
powershell -ExecutionPolicy Bypass -File .\deploy\publish-ubuntu.ps1
```

若当前已在新开的 PowerShell 会话中，也可直接：

```powershell
.\deploy\publish-ubuntu.ps1
```

**首次运行会发生什么：**

1. 脚本打印构建模式与版本号，开始构建镜像（Online 模式首次约 10~30 分钟，之后有 layer 缓存会快很多）。
2. 构建完成后自动打包 `publish_images/` 图片数据（如存在），并 `scp` 上传离线包（约 2~4 GB，视网速）与图片数据包（约 45 MB）。
3. 远端自动安装、启动、验证（容器 healthcheck start_period 60s，整体等待默认最长 240s），随后把 LineUp 图片/导入源灌入 `hsjm-ai_api-uploads` 卷（合并语义，不覆盖服务器新增数据），并回显 `APPDATA_IMAGE_COUNT` 等计数。
4. 终端最终打印：

```text
===================================================
 一键部署完成！
 平台地址：http://192.168.6.75:28002
 部署目录：/opt/hsjm/hsjm-ai-linux-amd64-2026.09.08
 镜像标签：hsjm-ai-api:2026.09.08
 图片数据：已迁移 LineUp 工站图片/导入源 -> hsjm-ai_api-uploads 卷（合并，不覆盖服务器新增）
 ...
===================================================
```

**密码认证提示**：未配置免密时，脚本有 3 次 SSH 会话（预检 / 上传 / 部署），会分别提示输入密码，共 3 次。配置公钥免密后全程无交互（见下节）。

**演练模式**：加 `-DryRun` 只打印将执行的每条命令（docker/scp/ssh 与远端脚本全文），不实际执行，用于发布前检查：

```powershell
.\deploy\publish-ubuntu.ps1 -DryRun -HttpPort 28500
```

### 3.1 手工上传模式（-LocalOnly，脚本不连接服务器）

不希望脚本直接 SSH 操作服务器时用手工模式——本地只负责打包，上传与启动都由你自己执行：

```powershell
.\deploy\publish-ubuntu.ps1 -LocalOnly
# 可组合：-Version / -BuildMode / -NoAppData / -Archive / -DryRun 等
```

脚本结束打印完整的后续操作指引（可直接复制），产物在 `release\`：

| 文件 | 说明 |
|---|---|
| `hsjm-ai-linux-amd64-<版本>.tar.gz` | 离线包（镜像 + compose + 全部服务器脚本） |
| `hsjm-ai-linux-amd64-<版本>.tar.gz.sha256` | 校验文件 |
| `appdata-init-<版本>.tar.gz` | LineUp 图片/导入源数据包（`publish_images/` 自动打包，见第 10 节） |

**第 1 步 · 自己上传**（任意工具皆可：scp / WinSCP / U 盘）：

```bash
scp -P 22 release/hsjm-ai-linux-amd64-<版本>.tar.gz release/hsjm-ai-linux-amd64-<版本>.tar.gz.sha256 \
    release/appdata-init-<版本>.tar.gz hsjm@192.168.6.75:/opt/hsjm/
```

**第 2 步 · 服务器上执行**（Ubuntu，一条命令完成装镜像 → 选端口 → 启动 → 验证 → 图片灌卷）：

```bash
cd /opt/hsjm
sha256sum --check hsjm-ai-linux-amd64-<版本>.tar.gz.sha256   # 完整性校验
tar -xzf hsjm-ai-linux-amd64-<版本>.tar.gz
cd hsjm-ai-linux-amd64-<版本>
chmod +x ./*.sh
./server-deploy.sh
```

`server-deploy.sh` 是随离线包分发的**服务器端一键脚本**：自动检测包目录同级（`/opt/hsjm`）的 `appdata-init-*.tar.gz` 并迁移图片，无需传参；也可显式指定：`./server-deploy.sh /opt/hsjm/appdata-init-<版本>.tar.gz`。图片迁移想单独补跑：`./migrate-appdata.sh <包路径>`。

> 全自动与手工模式的服务器端行为**完全一致**——全自动模式远端执行的也是包内同一套 `install.sh / start.sh / verify.sh / migrate-appdata.sh`，只是执行者是脚本而非你。
>
> 手工模式同样建议先完成第 2 节的服务器环境要求（Docker + Compose v2 + docker 组权限）；固定端口方法见第 8.3 节（`cp .env.example .env` 后改 `HSJM_HTTP_PORT`，在 `./server-deploy.sh` 之前）。

---

## 4. SSH 连接与免密配置

### 4.1 连接参数

| 参数 | 默认值 | 说明 |
|---|---|---|
| `-ServerHost` | `192.168.6.75` | 服务器 IP 或主机名 |
| `-ServerUser` | `hsjm` | SSH 用户名 |
| `-ServerPort` | `22` | SSH 端口（注意 ssh 用小写 `-p`、scp 用大写 `-P`，脚本内部已处理） |

### 4.2 配置公钥免密（推荐，一次性）

在 **Windows 打包机**执行：

```powershell
# 1. 生成密钥（已有 ~/.ssh/id_ed25519 可跳过）
ssh-keygen -t ed25519 -C "hsjm-deploy"

# 2. 把公钥追加到服务器（会要求输一次密码）
type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh hsjm@192.168.6.75 "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"

# 3. 验证免密
ssh hsjm@192.168.6.75 "echo ok"
```

### 4.3 主机指纹

脚本固定使用 `-o StrictHostKeyChecking=accept-new`：

- **首次**连接新服务器：自动记录指纹，不阻塞；
- 之后指纹**变化**（可能是服务器重装或中间人攻击）：拒绝连接并报错，需人工执行
  `ssh-keygen -R <ServerHost>` 清除旧指纹后确认安全性再重试。

---

## 5. 参数详解

```powershell
.\deploy\publish-ubuntu.ps1 `
    -Version 2026.09.08 `              # 版本号；缺省=当天日期 yyyy.MM.dd
    -BuildMode Online `                 # Auto(默认) | Online | Offline
    -Archive .\release\xxx.tar.gz `     # 复用已有离线包，跳过构建（与 -Version 冲突时校验）
    -SdkCacheRoot D:\sdk `              # Offline 模式缓存根目录（默认 D:\sdk）
    -NoCache `                          # 构建强制 --no-cache（显著变慢，仅在需要时）
    -ServerHost 192.168.6.75 `          # 服务器
    -ServerUser hsjm `
    -ServerPort 22 `
    -RemoteRoot /opt/hsjm `             # 服务器部署根目录
    -HttpPort 28500 `                   # 固定端口；缺省=服务器自动在 28000-28999 选空闲端口
    -RedisConnection '' `               # 可选 Redis 覆盖：host:port,password=CHANGE_ME,defaultDatabase=0
    -TimeZone Asia/Shanghai `           # 时区覆盖（非默认值才写入 .env）
    -StartTimeoutSeconds 240 `          # 启动健康等待秒数覆盖
    -AppDataInit .\appdata-init.tar.gz `# 可选：自定义图片/导入数据迁移包（缺省自动打包 publish_images/）
    -NoAppData `                        # 跳过 publish_images/ 图片数据自动打包与迁移
    -LocalOnly `                        # 手工模式：只本地打包+打印指引，不连接/上传/操作服务器
    -SkipDiskCheck `                    # 跳过服务器磁盘 3 倍检查
    -DryRun                             # 演练：只打印命令不执行
```

约束：

- `-Version` 只能含 `字母/数字/点/下划线/连字符`，以字母或数字开头。
- `-RemoteRoot` 必须是无空格的绝对 POSIX 路径（如 `/opt/hsjm`）。
- `-RedisConnection` 不允许引号、空格、反引号等字符（避免远端注入），密码含这些字符时请在服务器上手工编辑 `.env`。
- `-LocalOnly` 与 `-ServerHost/-ServerUser/-ServerPort/-RemoteRoot` 组合时，这些参数仅用于生成打印出来的 scp 示例命令，不会发起任何连接；`-HttpPort/-RedisConnection/-TimeZone/-StartTimeoutSeconds` 在手工模式下不生效（服务器侧改用 `.env`，见第 8.3 节）。

---

## 6. 构建模式说明

`-BuildMode Auto`（默认）判定逻辑：`D:\sdk` 存在 → **Offline**；否则 → **Online**。

### 6.1 Online（联网构建）

- 前提：Docker Desktop 运行中、能访问外网（npm/NuGet/pypi 由容器内构建过程拉取）。
- 使用 `deploy/docker/api.Dockerfile`，与 `deploy/offline/build-bundle.sh` 产出**等价**（Windows 下原生 PowerShell 实现，规避 MSYS tar 路径坑）。
- 产物落在 `release\hsjm-ai-linux-amd64-<版本>\` 与同名 `.tar.gz` / `.tar.gz.sha256`。

### 6.2 Offline（离线缓存构建，断网验收）

- 前提：本机存在 `D:\sdk` 缓存（`-SdkCacheRoot` 可改），内部调用既有
  `deploy\offline\build-bundle-offline.ps1`（独立 PowerShell 5.1 子进程执行，`--network=none` 强制禁网构建）。
- 缓存输入指纹或 NuGet 子集校验失败时**直接失败**——这是设计行为：应先刷新 `D:\sdk`（见 `D:\sdk\README-HSJM-Docker-Cache.md`），不要删除 `--network=none` 绕过。
- 依赖清单变化 / `release` 下同名版本残留（`output-exists`）也会失败：同日重打包请给版本加后缀，如 `-Version 2026.09.08.2`，并先清理 `release\hsjm-ai-linux-amd64-<版本>` 残留目录。

### 6.3 Archive（复用已构建离线包）

- 场景：离线包在别的机器已构建好（或沿用历史版本重新部署）。
- 文件名必须形如 `hsjm-ai-linux-amd64-<版本>.tar.gz`，版本号从文件名解析。
- 同目录缺 `.sha256` 时自动本地补算生成（校验传输完整性用）。
- 该模式**不需要**本机 Docker。

---

## 7. 部署后自动验证内容

远端 `verify.sh` 在部署完成后自动执行，全部通过才算成功：

| 检查项 | 说明 |
|---|---|
| 首页 `GET /` | Vue 前端已由入口脚本写入运行卷 |
| `GET /live` | 存活探针（不依赖外部服务） |
| `GET /health` | 完整就绪：**PostgreSQL SELECT 1 + Redis PING** 均成功 |
| MCP `server/discover`（2026-07-28） | MCP 2.0 握手，声明协议版本 |
| MCP `tools/list` | 必须包含 `diagnose_energy_flow` 与 `diagnose_line_stoppage` 两个诊断工具 |
| 旧协议 `initialize`（2025-03-26） | 老客户端兼容路径 |

验证脚本在容器网络命名空间内执行，MCP 免 JWT 属预期（仅回环放行）；从其它主机调用 `/mcp` 必须携带平台 JWT。

成功后脚本回传 `PLATFORM_URL`（服务器首个 IP + 实际端口）。手工复核：

```bash
ssh hsjm@192.168.6.75
cd /opt/hsjm/hsjm-ai-linux-amd64-<版本>
./status.sh     # 容器/端口/健康状态
./verify.sh     # 重跑全量验证
```

---

## 8. 固定端口与 .env 覆盖

### 8.1 一键参数方式（推荐）

```powershell
.\deploy\publish-ubuntu.ps1 -HttpPort 28500
```

远端部署时自动生成 `0600` 权限的 `.env`（仅写入与默认值不同的项），端口被占用时 `start.sh` 会拒绝启动并报错。

### 8.2 可覆盖项

| 一键参数 | .env 变量 | 默认 |
|---|---|---|
| `-HttpPort` | `HSJM_HTTP_PORT` | 空 = 自动选 28000-28999 |
| `-TimeZone` | `HSJM_TIME_ZONE` | `Asia/Shanghai` |
| `-StartTimeoutSeconds` | `HSJM_START_TIMEOUT_SECONDS` | `240` |
| `-RedisConnection` | `HSJM_REDIS_CONNECTION` | 空 = 用 `127.0.0.1:6379`（host 网络） |

### 8.3 服务器手工方式

```bash
cd /opt/hsjm/hsjm-ai-linux-amd64-<版本>
cp .env.example .env && chmod 0600 .env
vi .env            # 修改后
./start.sh
```

> 数据库账号/密码/模型配置/JWT 等仍读镜像内 `appsettings.json`，ASP.NET Core 环境变量覆盖机制依然可用，但一键脚本不要求填写任何数据库变量（与原离线手册一致）。

---

## 9. 升级与回滚

### 9.1 一键升级（新版本）

```powershell
.\deploy\publish-ubuntu.ps1 -Version 2026.09.10
# 手工模式：加 -LocalOnly，上传后在新版本目录内执行 ./server-deploy.sh
```

- 每个版本独立目录 `/opt/hsjm/hsjm-ai-linux-amd64-<新版本>`；镜像按版本 tag 加载。
- `start.sh --force-recreate` 自动覆盖旧的 `hsjm-ai` 容器；具名卷（日志/上传/Skill/生成文件）保留不动。
- 手工模式升级同理：上传新版本包 → 校验解压 → 新目录内 `./server-deploy.sh`（自动检测新 appdata 包时图片按合并语义灌卷，不覆盖服务器新增）。

### 9.2 同版本重打包

本地 `release` 下同名产物会 `output-exists` 拒绝：换 `-Version`（加 `.2` 后缀）并清理残留目录后重跑。

### 9.3 回滚

前提：旧镜像未从服务器删除。新版本部署失败或有问题时：

```bash
ssh hsjm@192.168.6.75
cd /opt/hsjm/hsjm-ai-linux-amd64-<旧版本>
./start.sh && ./verify.sh

# 查看服务器上已有的版本镜像
docker image ls hsjm-ai-api
```

### 9.4 清理远端历史压缩包（可选）

部署成功后 tar.gz 留在 `/opt/hsjm`，不影响运行；定期清理：

```bash
ssh hsjm@192.168.6.75 'cd /opt/hsjm && ls -lh *.tar.gz && rm hsjm-ai-linux-amd64-<旧版本>.tar.gz*'
```

---

## 10. 图片资源如何被包含（默认自动，无需手工操作）

一键部署在三个层面保证「图片都能包含」：

| 图片类别 | 来源位置 | 进入部署的通道 | 落点 |
|---|---|---|---|
| 前端 UI 图（登录横幅、Logo、图标、favicon、浏览器升级页） | `web/src/assets/**`、`web/public/**` | Docker 内 `pnpm build` → `dist/` → 镜像 `web.seed` | `api-wwwroot` 卷 `/app/wwwroot`，**每次容器启动自动刷新** |
| 后端静态资源（Skill 脚本、品牌签名 yaml 等） | `api/HSJM.AI/wwwroot/**` | `dotnet publish` → 镜像 `wwwroot.seed` | 同上卷；升级不覆盖现场改过的 Skill 文件 |
| **LineUp 工站图片**（`Lup_StationImage` 引用，187+ 张 hash 命名 PNG） | 仓库根 `publish_images/LineUp/`（`images/` + `graph-node-images/`） | **第 1.5 步自动打包** → `release/appdata-init-<版本>.tar.gz` → 部署后远端灌卷 | `hsjm-ai_api-uploads` 卷 `/app/Uploads/LineUpImages`（compose 环境变量 `WebConfig__LineUp__StorageRoot`） |
| GRAPH 导入源批次 | 仓库根 `publish_images/lineup-imports/<批次ID>/` | 同上 | 同上卷 `/app/Uploads/lineup-imports`（`WebConfig__LineUp__ImportRoot`） |

> 数据库是共享的：库里已有 `Lup_StationImage` 历史记录而卷内没有对应图片文件时，**所有历史图片 404**——这就是图片数据默认自动包含的原因。仓库根的 `publish_images.zip` 只是原始备份压缩包，脚本不使用它。

### 10.1 自动包含的行为细节

- **检测**：脚本在第 1.5 步检查 `publish_images/LineUp` 与 `publish_images/lineup-imports`，任一存在即自动打包上传迁移；都不存在则提示跳过。
- **合并语义（不覆盖）**：远端用 `cp -a -n` 灌卷——服务器上线后用户新上传的图片/新导入批次**不会被覆盖**，重复部署幂等安全。
- **属主修正**：灌卷后执行 `chown -R app:app`。容器以 `USER app` 运行，root 属主的目录会导致新图片写入 422（`ImageStorageFailed`，见 `docs/LineUp保存点位422-ImageStorageFailed根因分析.md`）。
- **计数回显**：迁移结束打印 `APPDATA_IMAGE_COUNT=<images 目录文件数>` 与 `APPDATA_IMPORT_BATCH_COUNT=<导入批次数>`，可直接核对。
- **关闭**：加 `-NoAppData` 跳过自动打包与迁移；用 `-AppDataInit <自定义包>` 指定别的数据包（包内顶层目录必须为 `appdata-init/LineUp` 与/或 `appdata-init/lineup-imports`）。

### 10.2 部署后验证图片

```bash
ssh hsjm@192.168.6.75
# 卷内文件计数（应与 APPDATA_IMAGE_COUNT 一致，约 187+）
docker run --rm -v hsjm-ai_api-uploads:/data hsjm-ai-api:<版本> \
  sh -c 'ls -1 /data/LineUpImages/images | wc -l; ls -1 /data/lineup-imports | wc -l'
```

再登录平台打开 LineUp 工站图片页面（或带 JWT 请求 `/LineUp/ImageMarks/Images/{id}/Content`），历史图片应正常显示。

---

## 11. 日常运维（服务器侧）

```bash
cd /opt/hsjm/hsjm-ai-linux-amd64-<版本>

./server-deploy.sh        # 一键：装镜像 + 启动 + 验证 + 图片迁移（新上传的版本目录内）
./status.sh               # 容器状态、端口、健康
./logs.sh                 # 最近日志（密码/密钥/Token 已脱敏）
./logs.sh --follow        # 持续跟踪
./stop.sh                 # 停止（保留容器与卷）
./start.sh                # 再次启动
./migrate-appdata.sh <包> # 单独补迁移图片数据（可选）
./uninstall.sh            # 删除容器与网络（保留业务文件卷）
./uninstall.sh --purge    # 连业务文件卷一起删（交互确认，外部 PG/Redis 不动）
```

常用地址（端口以 `.env` 的 `HSJM_HTTP_PORT` 为准）：

| 入口 | 地址 |
|---|---|
| 平台首页 | `http://<服务器IP>:<端口>/` |
| Swagger | `http://<服务器IP>:<端口>/swagger` |
| Hangfire | `http://<服务器IP>:<端口>/hangfire` |
| MCP | `http://<服务器IP>:<端口>/mcp`（需 JWT） |

---

## 12. 排障

### 12.1 打包机侧

| 现象 | 原因与处理 |
|---|---|
| `Docker Engine 未运行` | 启动 Docker Desktop；或本机无 Docker 时改用 `-Archive` 复用离线包 |
| `输出已存在：release\...`（output-exists） | 同版本已打包过：换 `-Version` 加后缀，先删 `release\hsjm-ai-linux-amd64-<版本>` 残留 |
| `CACHE_ERROR=...`（Offline 模式） | 缓存指纹/NuGet 子集校验失败：按 `D:\sdk\README-HSJM-Docker-Cache.md` 刷新缓存，不要绕过 `--network=none` |
| `服务器预检失败` + `PREFLIGHT_FAILED: docker unavailable` | 服务器未装 Docker 或用户不在 docker 组：`sudo usermod -aG docker <user>` 后重新登录 |
| `PREFLIGHT_FAILED: directory not writable` | `sudo mkdir -p /opt/hsjm && sudo chown <user> /opt/hsjm` |
| `PREFLIGHT_FAILED: disk space low` | 清理旧 tar.gz / 旧镜像（`docker image prune`），或临时 `-SkipDiskCheck` |
| scp 上传中断 | 直接重跑脚本（scp 支持断点续传 `-C` 压缩亦可手动加；重跑会重新构建前先复用本地构建产物则用 `-Archive`） |
| ssh 报 `REMOTE HOST IDENTIFICATION HAS CHANGED` | 服务器重装过：`ssh-keygen -R <ServerHost>` 后重试；未重装则警惕中间人 |
| 远端部署失败 `REMOTE_DEPLOY_FAILED: checksum mismatch` | 传输损坏：重跑上传（脚本会重新 scp） |

### 12.2 服务器侧

沿用 [README-Linux离线部署.md 第 12 节](./README-Linux离线部署.md#12-排障)：

- **容器一直 unhealthy**：`./status.sh` + `./logs.sh`；常见原因：PG/Redis 不可达、账号密码错、外部库缺 Hangfire schema（迁移容器不自动建表）、28000-28999 端口全占用。
- **页面 404**：检查 `docker compose exec api ls -la /app/wwwroot` 是否有 `index.html`。
- **LineUp 历史图片 404**：确认部署时带了图片数据包（输出有 `APPDATA_MIGRATED=1`）；核对卷内计数 `docker run --rm -v hsjm-ai_api-uploads:/data hsjm-ai-api:<版本> sh -c 'ls -1 /data/LineUpImages/images | wc -l'`；计数正常仍 404 时检查该图片记录的 `StorageKey` 是否在 `images/`/`graph-node-images/` 命名空间内。
- **保存图片报 422 ImageStorageFailed**：卷内目录属主是 root（历史手工灌入遗留）：`docker run --rm -v hsjm-ai_api-uploads:/data hsjm-ai-api:<版本> chown -R app:app /data/LineUpImages` 修正后重试（一键脚本的迁移步骤已自动 chown）。
- **MCP 无响应**：本机 curl 验证命令见原手册；跨主机必须带 `Authorization: Bearer <JWT>`，401 属预期。
- **平台地址打不开（服务器外）**：检查 `sudo ufw status` / 云安全组是否放行所选端口。

### 12.3 想看脚本到底执行了什么

```powershell
.\deploy\publish-ubuntu.ps1 -DryRun -BuildMode Online -HttpPort 28500
```

会完整打印 docker/scp/ssh 命令与两段远端 bash 脚本全文，不执行任何操作。

---

## 13. 安全说明

1. **离线包是敏感介质**：镜像内含 `appsettings.json`（数据库账号密码、模型配置、JWT 参数）。等同于生产凭据，只允许传给授权的内网服务器与运维人员；禁止上传公共网盘/镜像仓库/Git 仓库。SHA-256 只保证完整性，不加密。
2. **传输通道**：scp/ssh 全程加密；跨不可信网络时应叠加组织批准的加密介质或跳板通道。
3. **`.env` 权限**：脚本生成的远端 `.env` 固定 `0600`；手工编辑后同样保持。
4. **不要把 `/mcp` 与平台端口直接暴露公网**：在防火墙只放行受控的 Agent/用户地址段；MCP 远程调用必须携带平台 JWT。

---

## 附：本脚本与既有部署设施的关系

| 设施 | 角色 |
|---|---|
| `deploy/publish-ubuntu.ps1`（本文） | **Windows 一键入口**：构建 + 上传 + 远端部署 + 验证；`-LocalOnly` 为手工模式入口 |
| `deploy/scripts/server-deploy.sh` | **服务器端一键脚本**（随离线包分发）：install + start + verify + 图片迁移，手工上传模式在服务器上执行 |
| `deploy/scripts/migrate-appdata.sh` | **图片数据灌卷脚本**（随离线包分发）：LineUp 图片/导入源 → `hsjm-ai_api-uploads` 卷，可单独补跑 |
| `deploy/offline/build-bundle-offline.ps1` | 离线缓存构建（一键脚本的 Offline 模式内部调用） |
| `deploy/offline/build-bundle.sh` | Linux/Git-Bash 手工构建入口（保持不动） |
| `deploy/compose.yaml` + `deploy/scripts/*.sh` | 服务器端运行与运维脚本（随离线包分发，保持不动） |
| `deploy/README-Linux离线部署.md` | 手工部署/升级/回滚手册（服务器侧细节权威文档） |
