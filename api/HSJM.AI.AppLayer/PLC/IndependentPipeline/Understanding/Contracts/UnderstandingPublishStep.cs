#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public static class UnderstandingPublishGuard
{
    /// <summary>spec §10：新流程任何失败都不能让发布失败；取消照常传播。</summary>
    public static Task<T> RunContainedAsync<T>(Func<Task<T>> step, T fallback, Action<Exception> log, CancellationToken ct)
        => RunContainedAsync(_ => step(), fallback, log, ct);

    /// <summary>
    /// 同上，另给整步一个期限：期限到了而调用方未取消 → 记日志（TimeoutException）并返回 fallback。
    /// 只有调用方的取消才往外抛。
    /// </summary>
    public static async Task<T> RunContainedAsync<T>(Func<CancellationToken, Task<T>> step,
        T fallback, Action<Exception> log, CancellationToken ct, TimeSpan? deadline = null)
    {
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (deadline is { } d) limited.CancelAfter(d);
        try { return await step(limited.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) when (limited.IsCancellationRequested)
        {
            log(new TimeoutException($"understanding step exceeded {deadline}", ex));
            return fallback;
        }
        catch (Exception ex) { log(ex); return fallback; }
    }

    /// <summary>
    /// 单次模型调用限时：超时而调用方未取消 → TimeoutException（读取器把该 FB 记 Failed，其它 FB 继续）。
    /// </summary>
    public static ContractCompletion WithCallTimeout(ContractCompletion inner, TimeSpan perCall)
        => async (system, user, ct) =>
        {
            using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limited.CancelAfter(perCall);
            try { return await inner(system, user, limited.Token); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && limited.IsCancellationRequested)
            {
                throw new TimeoutException($"model call exceeded {perCall}", ex);
            }
        };

    /// <summary>CustomRequestParams 不是 JSON 对象（或不是合法 JSON）时按空对象处理。</summary>
    public static JsonObject ParseParameters(string? customRequestParams)
    {
        if (string.IsNullOrWhiteSpace(customRequestParams)) return new JsonObject();
        try { return JsonNode.Parse(customRequestParams) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}

/// <summary>理解步骤的产出。理解步骤失败（含超时）时 Report 为 null，Result 为原生成结果。</summary>
public sealed record UnderstandingStepResult(Original353ProjectResult Result, UnderstandingReport? Report);

/// <summary>发布器用：FB 契约（AI）+ 调用点代入。模型取网站用途绑定「独立 PLC 解析与候选分析」。</summary>
public sealed class UnderstandingPublishStep(PostgresPipelineStore store, PipelineModelCatalog catalog,
    IAiModelPurposeResolver purposeResolver, IConfiguration configuration, ILogger<UnderstandingPublishStep> logger) : ISelfScopedAutoInject
{
    TimeSpan Minutes(string key, double fallback)
    {
        var value = configuration.GetValue<double?>($"IndependentPlcPipeline:{key}");
        return TimeSpan.FromMinutes(value is > 0 ? value.Value : fallback);
    }

    /// <summary>
    /// FB 契约读取期限 = 理解步骤总期限 − 余量（余量取 5 分钟与总期限 20% 中较小者），
    /// 余量留给读完之后的核对、调用点代入与合并。
    /// </summary>
    public static TimeSpan ReadingBudget(TimeSpan deadline) =>
        deadline - TimeSpan.FromMinutes(Math.Min(5, deadline.TotalMinutes * 0.2));

    /// <summary>普通网络并发读取数：配置为正数时用它，否则 3。</summary>
    public static int NetworkAiParallelism(int? configured) => configured is > 0 ? configured.Value : 3;

    /// <summary>理解步骤失败时不能用空清单覆盖旧清单；成功但没有报警时要清空。</summary>
    public static bool ShouldReplaceCatalog(UnderstandingReport? report) => report is not null;

    /// <summary>读取失败（Failed）与全部关系被拒（Rejected）的 FB 名，各自按序数排序。</summary>
    public static (IReadOnlyList<string> Failed, IReadOnlyList<string> Rejected) UnanalysedFbs(UnderstandingReport report)
    {
        string[] Of(string status) => report.FbStatus.Where(s => s.Value == status).Select(s => s.Key).Order(StringComparer.Ordinal).ToArray();
        return (Of(FbContractStatus.Failed), Of(FbContractStatus.Rejected));
    }

    /// <summary>refreshNetworkUnits（审查 P3 refreshAi）：这些程序段的普通网络 AI 不读缓存、重新读取；FB 契约缓存不受影响。</summary>
    public Task<UnderstandingStepResult> ApplyAsync(PipelineSnapshot source, Original353ProjectResult generated, CancellationToken ct,
        IReadOnlySet<long>? refreshNetworkUnits = null)
    {
        var perCall = Minutes("UnderstandingCallTimeoutMinutes", 10);
        var deadline = Minutes("UnderstandingTimeoutMinutes", 45);
        return UnderstandingPublishGuard.RunContainedAsync(async token =>
        {
            // F11：契约读取（含超长 FB 分段）先于总期限停下，已读完的契约照常核对入库，不让总期限把整步回退
            using var stopReading = new CancellationTokenSource(ReadingBudget(deadline));
            // ResolveAsync 每次从库反序列化出新实体，Prepare 原地改参数不会影响别处；离线评测走同一个 Prepare，缓存键一致
            var (model, fingerprint) = UnderstandingModelSetup.Prepare(
                (await purposeResolver.ResolveAsync(AiModelPurposes.IndependentPlc, token)).Model);
            var complete = UnderstandingPublishGuard.WithCallTimeout(
                (system, user, callToken) => catalog.CompleteAsync(model, system, user, callToken), perCall);
            var reader = new FbContractReader(new PostgresFbContractCache(store.ConnectionString), complete, fingerprint,
                readingDeadline: stopReading.Token);
            // 普通网络与契约共用读取期限（F11）：到期只丢未读的网络；NetworkAiTimeoutMinutes 另限普通网络阶段发起新调用
            var networks = new NetworkAiReader(new PostgresNetworkAiCache(store.ConnectionString), complete, fingerprint,
                NetworkAiParallelism(configuration.GetValue<int?>("IndependentPlcPipeline:NetworkAiParallelism")),
                Minutes("NetworkAiTimeoutMinutes", 20), readingDeadline: stopReading.Token,
                bypassCache: refreshNetworkUnits is { Count: > 0 } refresh ? refresh.Contains : null);
            // 合并阶段：调用方取消（ct）照常往外抛；总期限（token）到期只停代码证明，契约规则、网络 AI 结果与报警清单照常输出
            var (result, report) = await UnderstandingRuleSource.ApplyAsync(source, generated, reader, model.ModelCode ?? fingerprint, token, networks,
                cancelMerge: ct, codeProofDeadline: token);
            logger.LogInformation("XMLV2 understanding project={ProjectId} fbs={Fbs} ok={Ok} failed={Failed} noBody={NoBody} textOnly={TextOnly} tooLarge={TooLarge} rejected={Rejected} rulesAdded={Added} rulesReplaced={Replaced} alarms={Alarms} notes={Notes} networks={Networks} networkOk={NetworkOk} networkDeadline={NetworkDeadline} networkRejected={NetworkRejected}",
                source.ProjectId, report.FbStatus.Count,
                report.FbStatus.Count(s => s.Value == FbContractStatus.Ok), report.FbStatus.Count(s => s.Value == FbContractStatus.Failed),
                report.FbStatus.Count(s => s.Value == FbContractStatus.NoBody), report.FbStatus.Count(s => s.Value == FbContractStatus.TextOnly),
                report.FbStatus.Count(s => s.Value == FbContractStatus.TooLarge), report.FbStatus.Count(s => s.Value == FbContractStatus.Rejected),
                report.RulesAdded, report.RulesReplaced, report.Alarms.Count,
                string.Join(",", report.NoteCounts.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => $"{n.Key}={n.Value}")),
                report.NetworkStatus.Count, report.NetworkStatus.Count(s => s.Value == NetworkAiStatus.Ok),
                report.NetworkStatus.Count(s => s.Value == NetworkAiStatus.Deadline), report.NetworkRejectReasons.Count);
            if (report.NetworkReadError is not null)
                logger.LogWarning("XMLV2 understanding network stage failed project={ProjectId}; network rules dropped: {Error}",
                    source.ProjectId, report.NetworkReadError);
            // spec §10：列出未分析的 FB（读取失败 / 全部关系被拒）
            var unanalysed = UnanalysedFbs(report);
            if (unanalysed.Failed.Count > 0 || unanalysed.Rejected.Count > 0)
                logger.LogWarning("XMLV2 understanding unanalysed FBs project={ProjectId} failed=[{Failed}] rejected=[{Rejected}]",
                    source.ProjectId, string.Join(",", unanalysed.Failed), string.Join(",", unanalysed.Rejected));
            return new UnderstandingStepResult(result, report);
        }, new UnderstandingStepResult(generated, null), ex => logger.LogWarning(ex, "XMLV2 understanding step failed project={ProjectId}; publishing without it", source.ProjectId),
        ct, deadline);
    }

    /// <summary>
    /// 约束事务提交之后调用：整体替换本项目报警清单。理解步骤失败（report 为 null）→ 保留旧清单；
    /// 写入失败只记日志，不影响已提交的约束。取消照常传播。
    /// loadUnits：读本项目库里的逻辑单元（块名、networkid、被调块），用于给清单行填 LogicUnitId（<see cref="AlarmCatalogLocator"/>）；
    /// 为 null 或读取失败时清单行不带 LogicUnitId（排查时只按块名归属工位），只记日志。
    /// replaceScope（2026-10-02 审查 P2/P3/P4）：非 null 时只替换该范围内的行——范围外的旧行原样保留（<see cref="AlarmCatalogMerge"/>），
    /// 用于按范围重跑与缺程序体块的旧行保留；为 null 时整体替换。
    /// </summary>
    public async Task SaveAlarmCatalogAsync(long projectId, UnderstandingReport? report, string generatorVersion, CancellationToken ct,
        Func<IReadOnlyList<AlarmCatalogRow>, CancellationToken, Task<IReadOnlyList<AlarmCatalogUnitCandidate>>>? loadUnits = null,
        Func<AlarmCatalogRow, bool>? replaceScope = null)
    {
        if (!ShouldReplaceCatalog(report))
        {
            logger.LogInformation("XMLV2 alarm catalog kept (understanding step failed) project={ProjectId}", projectId);
            return;
        }
        try
        {
            var rows = AlarmCatalogBuilder.Build(report!.Alarms);
            if (loadUnits != null && rows.Count > 0)
            {
                try { rows = AlarmCatalogLocator.Locate(rows, await loadUnits(rows, ct)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "XMLV2 alarm catalog: locating logic units failed project={ProjectId}; rows saved without LogicUnitId", projectId);
                }
            }
            var catalog = new AlarmCatalogStore(store.ConnectionString);
            if (replaceScope is not null)
                rows = AlarmCatalogMerge.Merge((await catalog.LoadAsync(projectId, ct)).Rows, rows, replaceScope);
            await catalog.ReplaceForProjectAsync(projectId, rows, generatorVersion, ct);
            // 部分 FB 读取失败时清单可能比上次小（下次发布自愈）：把失败 FB 数与行数写在同一条日志里
            logger.LogInformation("XMLV2 alarm catalog project={ProjectId} rows={Rows} located={Located} duplicatesDropped={Dropped} failedFbs={FailedFbs} version={Version}",
                projectId, rows.Count, rows.Count(r => r.LogicUnitId != null), report.Alarms.Count - rows.Count,
                report.FbStatus.Count(s => s.Value == FbContractStatus.Failed), generatorVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "XMLV2 alarm catalog write failed project={ProjectId}", projectId); }
    }
}
