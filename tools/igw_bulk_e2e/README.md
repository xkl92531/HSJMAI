# IGW 批量注册端到端测试（sub-project B task-9）

`run_e2e.ps1` 在本机起一个**临时 IGW**，用真实夹具（`api/HSJM.AI.IgwRegistration.Tests/Fixtures/plc1|plc2.json.gz`，由同目录 `make_fixtures.py` 生成）跑 HSJM 注册全流程，并用 IGW 自己的工程树 / DCS 读接口核对结果。

```powershell
# 在仓库根目录（Windows PowerShell 5.1 或 pwsh 均可）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\igw_bulk_e2e\run_e2e.ps1

# 常用参数
#   -Commit <sha>     构建指定 IGW 提交（默认 IGW worktree 的 HEAD）
#   -Rebuild          忽略该提交的发布缓存重新构建
#   -KeepData         保留临时运行目录（IGW 工程库 / 日志）便于排查
#   -NoTest           只起临时 IGW 并打印地址，创建 <运行目录>\stop 文件后停止并清理（手工调试用）
#   -IgwWorktree <p>  IGW 工作树（默认 <仓库上级>\IGW\.worktrees\bulk-tag-upsert）
```

退出码 = `dotnet test` 的退出码。结果摘要（计数、耗时、每个请求的服务端 timing）打印在末尾，并复制到 `tools/igw_bulk_e2e/last-run-results.md`。

## 脚本做了什么

1. **构建（按 IGW 提交缓存）**：`git archive <commit>` 导出 IGW 源码到 `%TEMP%\igw-e2e-cache\<commit12>\src`，`dotnet publish src/IGW.Runtime -c Release` 到同级 `publish\`，然后删除源码副本。**不在 IGW worktree / 主工作区里构建或写任何文件**。同一提交再次运行直接复用发布产物。
2. **启动临时 IGW**（只用这个进程）：
   - 数据目录 `IGW_DATA_DIR = %TEMP%\igw-e2e-<guid>\data`（从不使用 `%ProgramData%\IGW`）；
   - REST 端口取空闲端口（`IGW_REST_PORT`），并通过 `Kestrel__Endpoints__Http__Url` 只绑定 `127.0.0.1`（不对局域网开放、不触发防火墙提示）；
   - 预置 `project.igwproj`（首次启动迁移进 SQLite）：**OPC UA 服务端关闭**，不监听任何 opc.tcp 端口；日志中心端口 `IGW_OPENOBSERVE_PORT` 取空闲端口；
   - 永不使用 4840 / 48010 / 48020 / 28020 / 1502。
3. 轮询 `GET /api/v1/health` 就绪后，设置 `IGW_E2E_URL`、`IGW_E2E_RESULTS`，执行
   `dotnet test api/HSJM.AI.IgwRegistration.Tests --filter Category=E2E`。
4. **只停止自己启动的进程**（按 PID），删除临时运行目录。

### 授权

IGW `LicenseManager` 以数据目录为键记录首次试用时间，新数据目录即获得 IGW 内置的 **7 天本机试用**（`/api/v1/health` 显示 `license.status = Trial`），采集与 REST 均正常；未改动任何 IGW 代码。试用起点副本由 IGW 写在 `%ProgramData%\IGW\<目录哈希>\` 与注册表 `SOFTWARE\IGW\Trial_<哈希>`（HKLM，无权限时 HKCU）（每次运行一个极小的新条目，IGW 自身测试同样如此），不影响已安装 IGW 的授权与数据。

## 测试用例（`api/HSJM.AI.IgwRegistration.Tests/E2E/`）

`[Trait("Category","E2E")]`，`IGW_E2E_URL` 未设置时**跳过**（不算失败）。场景共享同一临时 IGW，按方法名顺序执行：

| 场景 | 内容 |
|---|---|
| A 全量 | `SupportsBulkAsync` = true；先用 `/Tag/AddTags` 在「通用」通道预置旧设备 `S1500_{PLC1 IP}`；PLC1、PLC2（假 IP 192.0.2.11 / .12，RFC 5737 文档网段，通道 E2E_PLC1 / E2E_PLC2）经生产编排 `IgwRegistrationRunner`（内存台账，其余全真实）全量注册；每个请求 200 / applied、零 `invalid_items`；实际请求 = 同输入规划器输出；台账全部 Registered；旧设备已被清理 |
| B 树 | 通道 / 设备（S7、S7Plus）/ endpoint / `dcsLogicalDevice`；DB 树接口根组集合；运行中工程完整子树逐点比对（组路径、点名、地址、类型、扫描周期、数组维度）与根组递归点数；`tags?groupId` 抽样；`summary.totalTagCount` |
| C 增量 | 改 1 块（删一行、加一行）、删 1 块、IO 刷新（删一点、加一点）→ runner 增量（台账快照由 `IgwLedgerSnapshotBuilder` 按 runner 写出的行构建，含 PreviousBlockRoots）→ 全部设备逐点比对 + 明确断言（删块根组两台设备都不在等） |
| D 读别名 | 经 `GraphIgwRegistrar` 注册一个假 GRAPH DB；用 `IgwReadAliasResolver` 为 S7 经典 / S7Plus 符号 / 深路径 / 数组元素 / IO / 增量新增点 / 重名（若有）/ GRAPH 容器拼别名 → `/Tag/ReadTagsAsList` 必须解析成功（PLC 不在线，`点位质量异常 … BadNotConnected` 属预期；`点位不存在` / `别名不唯一` 为失败）；反向：已删块旧别名必须 `点位不存在` |
| E 旧设备 | 经 `/api/v1/project/mutations` 预置 通用/S1500_{ip}、通用_S7Plus/S7Plus_{ip}（两个 IP）→ `RemoveLegacyDevicesAsync(ipA)` 只删 ipA 的 2 台，其余设备及全部点位不变；再次调用幂等 |
| F 耗时 | 每阶段每 PLC 总耗时、每个请求客户端耗时与服务端 `timing` 汇总表 |

注意：批量注册后 IGW 的 `/Tag/AddTags` 会把新设备放进**第一个已含 DCS 托管设备的同驱动通道**（即 HSJM 项目通道），不再落到「通用」，所以 E 场景的旧设备改用管理面 mutation 构造。

## 诊断读取端到端（D-1）

`api/HSJM.AI.IgwRegistration.Tests/E2E/DiagnosisReadE2ETests.cs`：用生产注册编排把 2 个 IO 点注册到临时 IGW（PLC IP 为文档网段 `192.0.2.31`，采集只会失败），再验证：

- `ReadDiagnosticSignalsAsync` 走 `POST /Tag/ReadTagsWithQuality`：已注册点为非 Good（通讯故障/首值未到类，实测 `CommFailure BadNotConnected`），未注册点 `Ghost` 为 `NotRegistered`，一次请求逐点返回、不整批失败；
- 强制旧接口（`IgwDiagnosticReader(useQualityEndpoint:false)`）时按 `ReadTagsAsList` 报文定位坏点：`Ghost` →「点位不存在」→ `NotRegistered`，`A_In` →「点位质量异常 … BadNotConnected」→ `CommFailure`。注意请求里 `Ghost` 排在前面：旧接口一次只报第一个坏点，先定位到通讯故障时读取器按设备级故障把本批其余未定位点统一标 `CommFailure`（有界请求数的既定取舍）；
- 用真实读取结果跑 `DiagnoseEnergyFlowAsync`（约束/项目数据用内存桩）：报告为「采集失败，无法诊断」，不含「未发现任何异常」。

需要 IGW ≥ 1.6.78（IGW `master`）。只跑这组用例：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\igw_bulk_e2e\run_e2e.ps1 -IgwWorktree C:\项目\IGW -Commit master -Filter "FullyQualifiedName~DiagnosisReadE2ETests"
```

`-IgwWorktree` 指向 IGW 主仓库只用于 `git archive` 读取提交对象，不在其工作区写任何文件。这组用例在禁止并行的 xunit 集合里（所有可并行集合跑完之后才执行），与批量注册场景 A–F 同跑（默认 `Category=E2E`）时不会干扰其 `summary.totalTagCount` / 设备清单核对。
