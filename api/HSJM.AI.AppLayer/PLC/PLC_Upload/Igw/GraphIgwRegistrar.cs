using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>GRAPH 注册所需的项目信息（与 PLC_ProjectEntity 解耦，便于无数据库单测）</summary>
    public sealed record GraphIgwProjectInfo(long ProjectId, string ProjectName, string ProductionLineName,
        string PlcCode, string IotUrl, string PlcIp, int PlcPort);

    /// <summary>
    /// 一个 GRAPH 步进 DB 的注册输入。PreviousStations = 调用方已知的旧工站（AI_PLC_GraphStepTag 重建前的 StationCode；
    /// 空值代表旧行未绑定工站 → _未分组），与 IGW 台账（RootKind=Graph）记录的工站合并后参与 replaceGroups。
    /// </summary>
    public sealed record GraphIgwDbInput(string DbName, string StationCode, int? MaxBitIndex, bool IncludeStepNo,
        IReadOnlyCollection<string> PreviousStations);

    /// <summary>单个 DB 的注册结果：Registered = 承载它的请求 HTTP 200；否则 Error 给出原因</summary>
    public sealed record GraphIgwDbOutcome(bool Registered, string Error);

    /// <summary>GRAPH 注册结果（按 DB 名大小写不敏感索引）+ 人类可读摘要</summary>
    public sealed record GraphIgwResult(IReadOnlyDictionary<string, GraphIgwDbOutcome> ByDb, string Summary);

    /// <summary>
    /// GRAPH 步位注册的 IGW 分支（sub-project B spec §4.4 / task-7）：批量接口探测 → 通道名（台账列优先，缺失生成并回写）
    /// → 旧设备清理（通用/通用_S7Plus 下 S1500_{ip}/S7Plus_{ip}）→ <see cref="GraphRequestBuilder"/> 构建 GRAPH 设备请求
    /// → 串行发送（某请求失败后续不再发送）→ 写台账（RootKind=Graph）→ 读别名缓存失效。
    /// 替代原「AddTags 落通用通道再经 Configurator 搬组到 通用_S7Plus」路径。除调用方取消外不抛异常。
    /// </summary>
    public sealed class GraphIgwRegistrar
    {
        private const int MaxErrorLength = 1000;

        private readonly IgwBulkClient _client;
        private readonly IIgwLedgerStore _store;
        private readonly Func<string, Task> _writeLog;
        private readonly ILogger _logger;
        private readonly IgwRegistrationConfig _config;
        private readonly IIgwProjectStateStore _stateStore;
        private readonly Func<DateTime> _now;

        public GraphIgwRegistrar(IgwBulkClient client, IIgwLedgerStore store, Func<string, Task> writeLog, ILogger logger,
            IgwRegistrationConfig config, IIgwProjectStateStore stateStore = null, Func<DateTime> now = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _writeLog = writeLog ?? (_ => Task.CompletedTask);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? new IgwRegistrationConfig();
            _stateStore = stateStore;
            _now = now ?? (() => DateTime.Now);
        }

        /// <summary>
        /// 执行一次 GRAPH 注册；返回每个 DB 的结果（除调用方取消外不抛异常）。
        /// removeStaleDbs=true（全项目运行）：IGW 台账中 RootKind=Graph、DB 不在本次 <paramref name="dbs"/> 且不在
        /// <paramref name="retainDbs"/>（如台账构建异常、状态未知的 DB）里的根视为失效——从 GRAPH 设备清除其 [工站, DB] 组，
        /// 请求写入成功后从台账移除。范围收窄运行必须传 false（其它 DB 只是不在本次范围，不是失效）。
        /// </summary>
        public async Task<GraphIgwResult> RegisterAsync(GraphIgwProjectInfo project, IReadOnlyList<GraphIgwDbInput> dbs, CancellationToken ct,
            bool removeStaleDbs = false, IReadOnlyCollection<string> retainDbs = null)
        {
            ArgumentNullException.ThrowIfNull(project);
            var inputs = (dbs ?? Array.Empty<GraphIgwDbInput>())
                .Where(d => d != null && !string.IsNullOrWhiteSpace(d.DbName))
                .GroupBy(d => d.DbName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var outcomes = new Dictionary<string, GraphIgwDbOutcome>(StringComparer.OrdinalIgnoreCase);
            if (inputs.Count == 0 && !removeStaleDbs)
            {
                return new GraphIgwResult(outcomes, "无 GRAPH DB 需要注册");
            }

            GraphIgwResult FailAll(string error)
            {
                foreach (var input in inputs)
                {
                    outcomes[input.DbName.Trim()] = new GraphIgwDbOutcome(false, error);
                }
                return new GraphIgwResult(outcomes, error);
            }

            var retained = new HashSet<string>(
                (retainDbs ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
                StringComparer.OrdinalIgnoreCase);
            var staleOnly = inputs.Count == 0;

            // 失败出口：仅清失效 DB 的运行（本轮无目标）只记 Information，不写「GRAPH步位注册」日志（无 DB 失败可报）
            async Task<GraphIgwResult> FailAsync(string error)
            {
                if (staleOnly)
                {
                    _logger.LogInformation("[GRAPH-IGW注册] 项目 {ProjectId} 仅清失效 GRAPH DB，跳过：{Reason}", project.ProjectId, error);
                    return new GraphIgwResult(outcomes, error);
                }
                await SaveGraphFailureAsync(project.ProjectId, _now(), error).ConfigureAwait(false); // N-6：写台账前失败
                return await LogAndReturnAsync(project, FailAll(error)).ConfigureAwait(false);
            }

            var total = Stopwatch.StartNew();
            try
            {
                // 仅清失效模式：台账无失效 Graph 根 → 零 IGW 调用直接返回
                if (staleOnly)
                {
                    var peek = await _store.GetLedgerAsync(project.ProjectId).ConfigureAwait(false) ?? IgwLedgerSnapshot.Empty;
                    if (!peek.StationByDb.Keys.Any(db => !retained.Contains(db)))
                    {
                        return new GraphIgwResult(outcomes, "无 GRAPH DB 需要注册，也无失效 GRAPH DB 需要清除");
                    }
                }

                var plcIp = (project.PlcIp ?? string.Empty).Trim();
                if (plcIp.Length == 0)
                {
                    return await FailAsync("项目未配置 PLC 直连 IP（plcip）——编辑项目填写 PLC直连IP 后重新注册").ConfigureAwait(false);
                }
                if (string.IsNullOrWhiteSpace(project.IotUrl))
                {
                    return await FailAsync("项目未配置网关地址（IotUrl 为空）").ConfigureAwait(false);
                }

                // ---------- 批量接口探测 ----------
                bool supported;
                try
                {
                    supported = await _client.SupportsBulkAsync(project.IotUrl, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return await FailAsync($"IGW 批量接口探测失败：{ex.Message}").ConfigureAwait(false);
                }
                if (!supported)
                {
                    return await FailAsync(
                        "IGW 版本过低：不支持批量注册接口 POST /api/v1/project/bulk/tags，请升级 IGW 到含批量接口的版本后重试").ConfigureAwait(false);
                }

                // ---------- 通道名：项目列优先，缺失则生成并回写（与 DB/IO 注册同一通道） ----------
                var channelName = (await _store.GetChannelNameAsync(project.ProjectId).ConfigureAwait(false))?.Trim();
                if (string.IsNullOrEmpty(channelName))
                {
                    channelName = IgwTagNaming.ChannelName(project.ProductionLineName, project.PlcCode, project.ProjectName);
                    if (channelName == null)
                    {
                        return await FailAsync("无法生成 IGW 通道名（产线名 / PlcCode / 项目名皆为空）").ConfigureAwait(false);
                    }
                    await _store.SetChannelNameAsync(project.ProjectId, channelName).ConfigureAwait(false);
                }

                // ---------- 旧设备清理（失败 → 不发送：旧别名残留会让读链解析冲突） ----------
                IReadOnlyList<string> removedLegacy;
                try
                {
                    // 只清理 GRAPH 旧设备（S7Plus_{ip}）：标量旧设备 S1500_{ip} 留给 DB/IO 注册清理（其删除触发该任务升级全量）
                    removedLegacy = (await _client.RemoveLegacyDevicesAsync(project.IotUrl, plcIp, IgwLegacyDeviceScope.GraphOnly, ct)
                        .ConfigureAwait(false)).Select(x => x.Description).ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return await FailAsync($"清理旧设备失败（未发送 GRAPH 注册请求）：{ex.Message}").ConfigureAwait(false);
                }

                // ---------- 构建：旧工站 = 调用方已知旧工站 ∪ IGW 台账记录的工站 ----------
                var ledger = await _store.GetLedgerAsync(project.ProjectId).ConfigureAwait(false) ?? IgwLedgerSnapshot.Empty;
                var previousByDb = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var input in inputs)
                {
                    var dbName = input.DbName.Trim();
                    var stations = new List<string>(input.PreviousStations ?? Array.Empty<string>());
                    if (ledger.StationByDb.TryGetValue(dbName, out var ledgerStation))
                    {
                        stations.Add(ledgerStation);
                    }
                    previousByDb[dbName] = stations;
                }
                // 台账原始键（大小写与库中 RootName 一致）：DB 名 → "{工站}|{DB}"，台账替换/移除按原键精确匹配
                var ledgerRootByDb = ledger.StationByDb.ToDictionary(
                    kv => kv.Key,
                    kv => ledger.GraphRootNameByDb.TryGetValue(kv.Key, out var original)
                        ? original
                        : GraphRequestBuilder.LedgerRootName(kv.Value, kv.Key),
                    StringComparer.OrdinalIgnoreCase);
                var staleRoots = new List<(string Station, string DbName)>();
                if (removeStaleDbs)
                {
                    var live = new HashSet<string>(inputs.Select(d => d.DbName.Trim()), StringComparer.OrdinalIgnoreCase);
                    live.UnionWith(retained);
                    staleRoots.AddRange(ledger.StationByDb
                        .Where(kv => !live.Contains(kv.Key))
                        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => (kv.Value, kv.Key)));
                }
                var build = GraphRequestBuilder.Build(channelName, plcIp, project.PlcPort,
                    inputs.Select(d => new GraphTargetSpec(d.DbName.Trim(), d.StationCode, d.MaxBitIndex, d.IncludeStepNo)).ToList(),
                    previousByDb, _config, staleRoots);
                foreach (var (dbName, reason) in build.SkippedDbs)
                {
                    outcomes[dbName] = new GraphIgwDbOutcome(false, reason);
                }

                // ---------- 发送：串行，失败即停 ----------
                var requestState = new (bool Applied, bool Succeeded, string Error)[build.Requests.Count];
                string stopError = null;
                for (var i = 0; i < build.Requests.Count; i++)
                {
                    if (stopError != null)
                    {
                        requestState[i] = (false, false, $"前序 GRAPH 请求失败，未发送：{stopError}");
                        continue;
                    }
                    requestState[i] = await SendAsync(project.IotUrl, build.Requests[i].Request, ct).ConfigureAwait(false);
                    if (!requestState[i].Succeeded)
                    {
                        stopError = requestState[i].Error;
                    }
                }

                // ---------- 台账（RootKind=Graph） ----------
                var now = DateTime.Now;
                var rows = new List<PLC_IgwRegistrationEntity>();
                var removed = new List<(string RootKind, string RootName)>();
                for (var i = 0; i < build.Requests.Count; i++)
                {
                    var state = requestState[i];
                    foreach (var dbName in build.Requests[i].DbNames)
                    {
                        var input = inputs.First(d => string.Equals(d.DbName.Trim(), dbName, StringComparison.OrdinalIgnoreCase));
                        outcomes[dbName] = new GraphIgwDbOutcome(state.Succeeded, state.Succeeded ? null : state.Error);

                        var newRoot = GraphRequestBuilder.LedgerRootName(input.StationCode, dbName);
                        var hadLedger = ledger.StationByDb.TryGetValue(dbName, out var oldStation);
                        var oldRoot = hadLedger ? ledgerRootByDb[dbName] : null;
                        // 请求未写入 IGW 且台账有旧工站：旧位置仍是 IGW 的真实状态，台账沿用旧工站键（读链继续按旧工站读）
                        var keepOld = !state.Applied && hadLedger;
                        var rootName = keepOld ? oldRoot : newRoot;
                        rows.Add(new PLC_IgwRegistrationEntity
                        {
                            RootKind = IgwLedgerRootKind.Graph,
                            RootName = rootName,
                            RootGroup = keepOld ? GraphRequestBuilder.StationGroup(oldStation) : GraphRequestBuilder.StationGroup(input.StationCode),
                            DeviceKind = IgwDeviceKind.Graph.ToString(),
                            TagCount = state.Applied && build.TagCountByDb.TryGetValue(dbName, out var count) ? count : 0,
                            SkippedCount = 0,
                            Status = state.Succeeded ? IgwLedgerStatus.Registered : IgwLedgerStatus.Failed,
                            Error = Truncate(state.Succeeded ? null : state.Error, MaxErrorLength),
                            RegisteredAt = now,
                        });
                        if (state.Applied && oldRoot != null && !string.Equals(oldRoot, newRoot, StringComparison.Ordinal))
                        {
                            removed.Add((IgwLedgerRootKind.Graph, oldRoot));
                        }
                    }
                    // 失效 DB：本请求已写入 IGW（组已清除）才从台账移除；未写入则台账保持（下次全量再清）
                    if (state.Applied)
                    {
                        foreach (var staleDb in build.Requests[i].StaleDbNames)
                        {
                            removed.Add((IgwLedgerRootKind.Graph, ledgerRootByDb[staleDb]));
                        }
                    }
                }

                string ledgerError = null;
                if (rows.Count > 0 || removed.Count > 0)
                {
                    try
                    {
                        await _store.ReplaceLedgerAsync(project.ProjectId, rows, removed).ConfigureAwait(false);
                        if (rows.Count > 0)
                        {
                            await SaveGraphFailureAsync(project.ProjectId, null, null).ConfigureAwait(false); // 已有 Graph 台账行 → 清除失败记录
                        }
                    }
                    catch (Exception ex)
                    {
                        ledgerError = ex.GetBaseException().Message;
                        _logger.LogError(ex, "[GRAPH-IGW注册] 项目 {ProjectId} 台账写入失败", project.ProjectId);
                    }
                    IgwAliasCacheInvalidation.Invalidate(project.ProjectId);
                }

                var okDbs = outcomes.Values.Count(o => o.Registered);
                var summary = $"通道 {channelName}，GRAPH 设备（{IgwTagNaming.LogicalDeviceName(IgwDeviceKind.Graph, plcIp)}）："
                              + $"请求 {requestState.Count(s => s.Succeeded)}/{build.Requests.Count} 成功，DB {okDbs}/{outcomes.Count} 注册成功"
                              + (stopError != null ? $"，失败：{stopError}" : string.Empty)
                              + (build.SkippedDbs.Count > 0 ? $"，跳过 DB {build.SkippedDbs.Count} 个" : string.Empty)
                              + (staleRoots.Count > 0 ? $"，清除失效 DB {staleRoots.Count} 个" : string.Empty)
                              + (removedLegacy.Count > 0 ? $"；删除旧设备 {removedLegacy.Count} 个：{string.Join("、", removedLegacy)}" : "；无旧设备需清理")
                              + (ledgerError != null ? $"；台账写入失败：{ledgerError}" : string.Empty)
                              + $"；耗时 {total.Elapsed.TotalSeconds:0.0}s";
                return await LogAndReturnAsync(project, new GraphIgwResult(outcomes, summary),
                    stopError != null || ledgerError != null).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "[GRAPH-IGW注册] 项目 {ProjectId} 执行异常", project.ProjectId);
                var error = $"执行异常：{ex.GetBaseException().Message}";
                if (inputs.Count > 0)
                {
                    await SaveGraphFailureAsync(project.ProjectId, _now(), error).ConfigureAwait(false);
                }
                foreach (var input in inputs.Where(d => !outcomes.ContainsKey(d.DbName.Trim())))
                {
                    outcomes[input.DbName.Trim()] = new GraphIgwDbOutcome(false, error);
                }
                return await LogAndReturnAsync(project, new GraphIgwResult(outcomes, error)).ConfigureAwait(false);
            }
        }

        /// <summary>发送一个请求：200 → 成功；500 runtime_apply_failed → 已写库但失败；其余 → 失败（错误原文）</summary>
        private async Task<(bool Applied, bool Succeeded, string Error)> SendAsync(string iotUrl, IgwBulkRequest request, CancellationToken ct)
        {
            IgwBulkResponse response;
            try
            {
                response = await _client.BulkUpsertAsync(iotUrl, request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (false, false, $"请求异常：{ex.GetBaseException().Message}");
            }

            if (response.HttpStatus == 200)
            {
                return (true, true, null);
            }
            var error = $"HTTP {response.HttpStatus} {response.Error}".TrimEnd();
            if (!string.IsNullOrEmpty(response.Detail))
            {
                error += "：" + response.Detail;
            }
            if (response.HttpStatus == 500 && string.Equals(response.Error, "runtime_apply_failed", StringComparison.Ordinal))
            {
                return (true, false, "已写库但运行态应用失败，" + error);
            }
            return (false, false, error);
        }

        private async Task<GraphIgwResult> LogAndReturnAsync(GraphIgwProjectInfo project, GraphIgwResult result, bool forceFailed = false)
        {
            var failed = forceFailed || result.ByDb.Values.Any(o => !o.Registered);
            var text = $"[GRAPH-IGW注册] 项目 {project.ProjectName}({project.ProjectId}) {(failed ? "存在失败" : "完成")}：{result.Summary}";
            if (failed)
            {
                _logger.LogWarning("{Text}", text);
            }
            else
            {
                _logger.LogInformation("{Text}", text);
            }
            try
            {
                await _writeLog(text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GRAPH-IGW注册] 写日志失败");
            }
            return result;
        }

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) ? null : text.Length > max ? text.Substring(0, max) : text;

        /// <summary>记录（failedAt 非空）/ 清除 GRAPH 写台账前失败（N-6）；无状态存储时跳过，失败只记日志</summary>
        private async Task SaveGraphFailureAsync(long projectId, DateTime? failedAt, string error)
        {
            if (_stateStore == null)
            {
                return;
            }
            try
            {
                await _stateStore.SaveGraphFailureAsync(projectId, failedAt, error).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GRAPH-IGW注册] 项目 {ProjectId} 保存 GRAPH 失败记录失败", projectId);
            }
        }
    }
}
