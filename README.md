# HSJMAI 源码

这是 HSJM_AI_PLC 在 2026-10-13 从 `master` 的 `9f650e167049061c9623e0f87b59d4c77c9b8ad8` 整理出的源码副本。主平台在 `api/` 和 `web/`；`exporter/`、`MAF-demo/`、`博图项目读取/`、`deploy/`、`deploynew/`、`tools/` 等附属源码也保留。

仓库不包含 `bin/`、`obj/`、`node_modules/`、`dist/`、打包目录、运行时上传、导入 XML、历史缓存和构建日志。依赖与构建产物在本地生成，并被 `.gitignore` 排除。

## 构建主平台

先安装 .NET 10 SDK、Python 3，以及符合 `web/package.json` 中 `engines` 的 Node.js。前端使用仓库指定的 pnpm 版本，可用 Corepack 启用。

```powershell
# 后端
dotnet restore .\api\HSJM.AI.sln
dotnet build .\api\HSJM.AI.sln -c Release --no-restore

# 前端
cd .\web
corepack pnpm install --frozen-lockfile
corepack pnpm build
```

前端产物位于 `web/dist/`，后端产物位于各项目的 `bin/`。它们只在本地生成，不提交到 Git。

## 运行前配置

公开仓库中的 `api/HSJM.AI/appsettings.json` 和 `MAF-demo/MAF.DEMO/appsettings.json` 已清空口令、JWT 签名密钥、API Key 与数据库连接串。运行前通过环境变量或未提交的本地配置提供真实值。例如主 API 至少需要：

- `WebConfig__DBList__0__ConnectionString`：业务 PostgreSQL 连接串。
- `WebConfig__DBList__1__ConnectionString`：Hangfire 与元数据数据库连接串。
- `WebConfig__JWTConfig__SecurityKey`：私有 JWT 签名密钥。
- `WebConfig__RedisServerUrl`、`WebConfig__RabbitMQConfig__RabbitMQPassword`、`WebConfig__MongoDBConfig__MongoDbUrl` 等：按启用功能配置。

`deploy/compose-test.yaml` 中的数据库连接串改为读取 `HSJM_DB_CONNECTION_0` 和 `HSJM_DB_CONNECTION_1` 环境变量。请不要把真实凭据写入 Git。编译通过不代表外部数据库、PLC、IGW、Redis 或 AI 模型已经就绪。

本机开发可在配置外部服务后运行：

```powershell
dotnet run --project .\api\HSJM.AI\HSJM.AI.csproj
cd .\web
corepack pnpm dev
```

附属项目可能需要各自的 SDK；`博图项目读取/20dll/` 和 `exporter/refs/` 包含构建所需的 Siemens DLL 依赖。TIA 工具运行时仍要求本机安装匹配大版本的 TIA Portal。

## 本次源码验证

在 Windows 上从本目录重新还原并构建：主 API Release、前端生产构建、`exporter/HsjmTia.sln` 和 `博图项目读取/博图项目读取.sln` 均完成。验证生成的 `bin/obj/node_modules/dist` 已从这份源码目录移出。

`MAF-demo/` 是独立参考项目，源码保留；当前还原的 Agents AI 包与示例中的 `Workflow.AsAgent` 调用不兼容，单独构建该示例有 3 个编译错误。它不在主平台解决方案内。运行时还需按上一节提供真实配置，并验证外部服务连通性。
