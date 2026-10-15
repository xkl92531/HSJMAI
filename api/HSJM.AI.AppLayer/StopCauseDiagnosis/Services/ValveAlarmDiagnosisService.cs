#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>报警阶段的读取方式：每批点数、同时在读的批数上限、整个报警阶段的总时限。</summary>
    public sealed record ValveAlarmReadOptions(int BatchSize, int MaxParallel, TimeSpan Deadline)
    {
        /// <summary>默认：每批 200 点、最多 3 批并行、报警阶段总时限 15 秒。</summary>
        public static readonly ValveAlarmReadOptions Default = new(200, 3, TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// 一键排查的「阀门报警」阶段：取清单 → 读 → 按工位写回 ActiveAlarms。只作证据，不改工位状态。
    /// 读取（终审 I-1）：只读本次参与排查工位的报警；先只读报警位（按 BatchSize 分批、最多 MaxParallel 批并行），
    /// 只对已激活的报警再补读其输出与到位反馈；任何一批失败/超时只让那批的点读不到（报警位读不到计入 AlarmReadFailedCount，
    /// 关联信号读不到显示「未读到」）；整个阶段有总时限，到时还没读回的批按读不到处理、已读到的照常用。调用方取消照常往外抛。
    /// </summary>
    public sealed class ValveAlarmDiagnosisService : ISelfScopedAutoInject
    {
        private readonly ValveAlarmCatalogService _catalog;
        private readonly IPLC_VariableAccessService _variableAccess;

        public ValveAlarmDiagnosisService(ValveAlarmCatalogService catalog, IPLC_VariableAccessService variableAccess)
        {
            _catalog = catalog;
            _variableAccess = variableAccess;
        }

        public Task<string?> AttachAsync(IReadOnlyList<StationDiagnosisSummaryDto> summaries, CancellationToken ct) =>
            AttachCoreAsync(
                summaries,
                (projectId, token) => _catalog.GetAsync(projectId, token),
                (projectId, requests, token) => _variableAccess.ReadExactDataAsync(projectId, requests, token),
                ValveAlarmReadOptions.Default,
                ct);

        /// <summary>兼容旧调用：一次读、不分批不限时（测试用）。</summary>
        public static Task<string?> AttachCoreAsync(
            IReadOnlyList<StationDiagnosisSummaryDto> summaries,
            Func<long, Task<IReadOnlyList<ValveAlarmDefinition>>> loadCatalog,
            Func<long, IReadOnlyCollection<PLC_ExactReadRequest>, Task<IReadOnlyList<PLC_ExactReadResult>>> readExact) =>
            AttachCoreAsync(summaries, (p, _) => loadCatalog(p), (p, r, _) => readExact(p, r),
                new ValveAlarmReadOptions(int.MaxValue, 1, Timeout.InfiniteTimeSpan), CancellationToken.None);

        public static async Task<string?> AttachCoreAsync(
            IReadOnlyList<StationDiagnosisSummaryDto> summaries,
            Func<long, CancellationToken, Task<IReadOnlyList<ValveAlarmDefinition>>> loadCatalog,
            Func<long, IReadOnlyCollection<PLC_ExactReadRequest>, CancellationToken, Task<IReadOnlyList<PLC_ExactReadResult>>> readExact,
            ValveAlarmReadOptions options,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var stage = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (options.Deadline != Timeout.InfiniteTimeSpan) stage.CancelAfter(options.Deadline);

            var errors = new List<string>();
            // P2（2026-10-09 检查清单 #12）：清单加载与逐项目循环都受同一阶段预算控制——
            // 原先 loadCatalog 不接 stage.Token，首个项目加载 30s 会让阶段过期后仍等满 30s，
            // 后续实时读取全部放弃却还继续加载其它项目。超时后明确记录未完成范围并停止。
            var projectGroups = summaries
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.StationCode))
                .GroupBy(s => s.ProjectId)
                .ToList();
            for (var groupIndex = 0; groupIndex < projectGroups.Count; groupIndex++)
            {
                var project = projectGroups[groupIndex];
                if (stage.IsCancellationRequested)
                {
                    // 调用方取消照常往外抛（与 try 内 await 抛 OCE 的既有语义一致）
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    var remaining = projectGroups.Count - groupIndex;
                    var unhandled = string.Join("、", projectGroups.Skip(groupIndex).Select(g => g.Key));
                    errors.Add($"报警阶段超时（{(int)options.Deadline.TotalSeconds}s），剩余 {remaining} 个项目的报警未处理（项目 {unhandled}）");
                    break;
                }
                try
                {
                    var stations = project
                        .GroupBy(s => s.StationCode.Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    // 只读本次参与排查工位的报警；清单加载受阶段时限约束（WaitAsync 兜底不响应取消的实现）
                    var definitions = (await loadCatalog(project.Key, stage.Token).WaitAsync(stage.Token))
                        .Where(d => d.StationCode != null && stations.ContainsKey(d.StationCode))
                        .ToList();
                    if (definitions.Count == 0) continue;

                    var exactReads = await ReadBatchedAsync(project.Key, ValveAlarmEvaluator.BuildAlarmRequests(definitions), readExact, options, stage.Token, ct);
                    var reads = CurrentValues(exactReads);
                    var active = definitions.Where(d => ValveAlarmEvaluator.IsActive(d, reads)).ToList();
                    if (active.Count > 0)
                    {
                        var signals = ValveAlarmEvaluator.BuildSignalRequests(active)
                            .Where(kv => !exactReads.ContainsKey(kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                        foreach (var (key, row) in await ReadBatchedAsync(project.Key, signals, readExact, options, stage.Token, ct))
                            exactReads[key] = row;
                    }

                    // Related reads can consume the rest of the stage budget; recheck the alarm's
                    // original source time now, rather than keeping a once-fresh true indefinitely.
                    reads = CurrentValues(exactReads);
                    var expiredAlarms = ValveAlarmEvaluator.BuildAlarmRequests(active.Where(d =>
                        reads.TryGetValue(ValveAlarmEvaluator.AlarmReadPath(d), out var row) && !row.IsGood));
                    if (expiredAlarms.Count > 0 && !stage.IsCancellationRequested)
                    {
                        // One bounded refresh of originally active alarms, using the original deadline.
                        // Remove the old true before reading: a failed refresh must never revive it.
                        foreach (var key in expiredAlarms.Keys) exactReads.Remove(key);
                        foreach (var (key, row) in await ReadBatchedAsync(project.Key, expiredAlarms, readExact, options, stage.Token, ct))
                            exactReads[key] = row;
                        reads = CurrentValues(exactReads);
                    }
                    foreach (var byStation in definitions.GroupBy(d => d.StationCode!, StringComparer.OrdinalIgnoreCase))
                    {
                        var (activeAlarms, failed) = ValveAlarmEvaluator.Evaluate(byStation, reads);
                        var summary = stations[byStation.Key];
                        summary.ActiveAlarms = activeAlarms;
                        summary.AlarmReadFailedCount = failed;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (stage.IsCancellationRequested)
                {
                    // 阶段超时（非调用方取消）：明确记录未完成范围，不再给 "A task was canceled" 这种无信息量报文
                    errors.Add($"项目 {project.Key}：报警阶段超时（{(int)options.Deadline.TotalSeconds}s），该项目报警未处理");
                }
                catch (Exception ex)
                {
                    errors.Add($"项目 {project.Key}：{ex.Message}");
                }
            }
            return errors.Count == 0 ? null : "报警读取失败：" + string.Join("；", errors);
        }

        /// <summary>按 PLC 原始采样时间检查本次证据，不以接收时刻或信号最后变化时刻替代。</summary>
        private static Dictionary<string, PLC_ReadTagResult> CurrentValues(IReadOnlyDictionary<string, PLC_ExactReadResult> exactReads)
        {
            var now = DateTimeOffset.UtcNow;
            return exactReads.ToDictionary(pair => pair.Key, pair =>
            {
                var row = pair.Value;
                var age = row.SourceTimestamp is { } timestamp ? now - timestamp : (TimeSpan?)null;
                var trusted = row.Quality == PLC_ExactReadQuality.Good && string.IsNullOrEmpty(row.ErrorCode)
                    && age is { } delta && delta >= TimeSpan.FromSeconds(-1) && delta <= TimeSpan.FromSeconds(5);
                return new PLC_ReadTagResult
                {
                    TagName = pair.Key,
                    Value = trusted ? row.Value : null,
                    IsGood = trusted,
                    ErrorMessage = trusted ? null : row.ErrorCode ?? "报警读数缺失、重复、质量异常或源时间戳无效",
                };
            }, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 分批读：每批 BatchSize 点，最多 MaxParallel 批同时在读；一批抛异常或到阶段时限还没回来 → 这批的点不进结果（按读不到算）。
        /// 调用方取消（ct）照常往外抛。到时限时不等仍在读的批（它们的结果丢弃）。
        /// </summary>
        private static async Task<Dictionary<string, PLC_ExactReadResult>> ReadBatchedAsync(
            long projectId,
            IReadOnlyDictionary<string, PLC_ExactReadRequest> requests,
            Func<long, IReadOnlyCollection<PLC_ExactReadRequest>, CancellationToken, Task<IReadOnlyList<PLC_ExactReadResult>>> readExact,
            ValveAlarmReadOptions options,
            CancellationToken stage,
            CancellationToken ct)
        {
            var merged = new Dictionary<string, PLC_ExactReadResult>(StringComparer.OrdinalIgnoreCase);
            if (requests.Count == 0) return merged;
            var batchSize = Math.Max(1, options.BatchSize);
            using var gate = new SemaphoreSlim(Math.Max(1, options.MaxParallel));
            async Task<List<PLC_ExactReadResult>> Reader(IReadOnlyList<string> keys)
            {
                var batch = keys.Where(requests.ContainsKey).Select(k => requests[k]).ToList();
                var results = await readExact(projectId, batch, stage);
                var expected = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                return results.Where(r => r != null && expected.Contains(r.RequestKey)).ToList();
            }

            async Task<List<PLC_ExactReadResult>> OneBatch(IReadOnlyList<string> keys)
            {
                try
                {
                    await gate.WaitAsync(stage);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return []; // 到时限还没轮到：这批按读不到
                }
                try
                {
                    // The station scope stores bare values without source timestamps. It cannot prove
                    // a current alarm after the observation window, so read this stage's evidence afresh.
                    var read = Reader(keys);
                    // 读取方不一定响应取消：到时限直接不等
                    return await read.WaitAsync(stage);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return [];
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    return []; // 这一批失败只影响这一批
                }
                finally
                {
                    gate.Release();
                }
            }

            var batches = requests.Keys.Chunk(batchSize).Select(chunk => OneBatch(chunk)).ToList();
            var rows = await Task.WhenAll(batches);
            ct.ThrowIfCancellationRequested();
            foreach (var group in rows.SelectMany(r => r).Where(r => !string.IsNullOrWhiteSpace(r.RequestKey))
                .GroupBy(r => r.RequestKey, StringComparer.OrdinalIgnoreCase))
            {
                merged[group.Key] = group.Count() == 1 ? group.Single()
                    : group.First() with { Value = null, Quality = PLC_ExactReadQuality.Unknown, ErrorCode = "报警返回重复读数" };
            }
            return merged;
        }
    }
}
