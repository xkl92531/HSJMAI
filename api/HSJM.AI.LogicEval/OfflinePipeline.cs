using System.Diagnostics;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Npgsql;

namespace HSJM.AI.LogicEval;

sealed record OfflineSetup(long ProjectId, PipelineSnapshot Source, IReadOnlyList<Original353ManualModeBinding> ModeBindings);

/// <summary>CacheDir 下放 FB 契约缓存、CacheDir/networks 下放普通网络缓存。--no-ai：只用缓存，未命中的记 Failed；
/// --no-network-ai：不读普通网络；LegacyCacheDir（--legacy-cache）：计划 5 之前离线工具按旧指纹写的缓存目录，只读迁移（R2），
/// 命中的回复照常写进 CacheDir（新指纹下的键）。</summary>
sealed record OfflineAiOptions(string CacheDir, bool NoAi, bool NoNetworkAi, string? LegacyCacheDir = null)
{
    public static OfflineAiOptions From(string[] args) =>
        new(CliArgs.Req(args, "--cache"), CliArgs.Flag(args, "--no-ai"), CliArgs.Flag(args, "--no-network-ai"), CliArgs.Opt(args, "--legacy-cache"));
}

/// <summary>一次 AI 读取的结果。Networks 为 null：未读普通网络（--no-network-ai）或网络阶段整体出错（NetworkError 非空）。
/// LegacyHits：经 --legacy-cache 从旧指纹缓存迁移得到的回复数（不调模型）。</summary>
sealed record OfflineAiRead(IReadOnlyDictionary<string, FbContractOutcome> Contracts, IReadOnlyDictionary<long, NetworkAiOutcome>? Networks,
    string ModelName, string Fingerprint, TimeSpan ContractTime, TimeSpan NetworkTime, int ModelCalls, int CacheMisses, int LegacyHits,
    string? NetworkError);

/// <summary>离线跑与发布器相同的生成：旧流程（按名字规则开关）+ 模式规则 → FB 契约 → 普通网络 → 合并。
/// AI 读取与合并分开：同一份 AI 结果可以配不同的旧流程开关重复合并（名字规则下线对照）。不写数据库。
/// 模型、指纹与请求体与发布器一致（OfflineModel / UnderstandingModelSetup），离线缓存可经 cache-push 给发布器用。</summary>
static class OfflinePipeline
{
    public const string HoldoutRefusal = "保留集（测试线 PLC2）只能经 holdout-acceptance 运行一次";

    public static async Task<OfflineSetup> LoadAsync(long projectId, string archive, bool allowHoldout = false)
    {
        if (Holdout.IsHoldoutProject(projectId) && !allowHoldout) throw new InvalidOperationException(HoldoutRefusal);
        await using var db = await EvalDb.OpenAsync("hsjm_ai_new_test", readOnly: true);
        var tags = await EvalDb.TagsAsync(db, projectId);
        return new(projectId, Original353ArchiveSource.Read(archive, projectId, tags), Original353ManualModeBindings.Read(archive));
    }

    public static Original353ProjectResult Old(OfflineSetup setup, NameRuleSwitches switches) =>
        Original353ModeConflictRules.Append(OriginalConstraintWorkflow.Generate(setup.Source, CancellationToken.None, switches),
            setup.Source, setup.ModeBindings);

    sealed class Counter
    {
        int value;
        public int Value => value;
        public void Add() => Interlocked.Increment(ref value);
    }

    public static async Task<OfflineAiRead> ReadAiAsync(OfflineSetup setup, OfflineAiOptions options)
    {
        OfflineModel model;
        await using (var db = await EvalDb.OpenAsync("hsjm_ai_new_test", readOnly: true))
            model = await OfflineModel.LoadAsync(db);
        using (model)
        {
            Counter calls = new(), misses = new(), legacyHits = new();
            ContractCompletion complete = async (system, user, ct) =>
            {
                // 读取器在新缓存未命中时才走到这里：先查旧指纹缓存，命中的回复由读取器写进新缓存
                if (options.LegacyCacheDir is not null
                    && LegacyCacheMigration.TryRead(options.LegacyCacheDir, model.LegacyFingerprint, system, user) is { } old)
                {
                    legacyHits.Add();
                    return old;
                }
                if (options.NoAi)
                {
                    misses.Add();
                    throw new InvalidOperationException("--no-ai：缓存未命中");
                }
                calls.Add();
                return await model.CompleteAsync(system, user, ct);
            };
            var clock = Stopwatch.StartNew();
            var contracts = await new FbContractReader(new FileFbContractCache(options.CacheDir), complete, model.Fingerprint)
                .ReadAsync(setup.Source, RelevantFbSelector.Select(setup.Source), CancellationToken.None);
            var contractTime = clock.Elapsed;
            clock.Restart();
            IReadOnlyDictionary<long, NetworkAiOutcome>? networks = null;
            string? networkError = null;
            if (!options.NoNetworkAi)
            {
                // 与 UnderstandingRuleSource.ApplyAsync 相同：网络阶段整体出错只丢网络结果，错误写进报告，不让整次评测失败
                try
                {
                    networks = await new NetworkAiReader(new FileNetworkAiCache(Path.Combine(options.CacheDir, "networks")), complete,
                        model.Fingerprint, parallelism: 3).ReadAsync(setup.Source, contracts, CancellationToken.None);
                }
                catch (Exception ex) { networkError = ex.GetType().Name + ": " + ex.Message; }
            }
            return new(contracts, networks, model.ModelName, model.Fingerprint, contractTime, clock.Elapsed, calls.Value, misses.Value,
                legacyHits.Value, networkError);
        }
    }

    public static (Original353ProjectResult Result, UnderstandingReport Report) Merge(OfflineSetup setup, Original353ProjectResult old,
        OfflineAiRead ai)
    {
        var (result, report) = UnderstandingRuleSource.Merge(setup.Source, old, ai.Contracts, ai.ModelName, ai.Networks);
        return (result, ai.NetworkError is null ? report : report with { NetworkReadError = ai.NetworkError });
    }

    /// <summary>保留集验收占标记前的环境探测（审查 Important 2）：测试库能连上并执行只读查询、模型用途绑定取得到模型、
    /// 对该模型发一次最小请求（固定提示，不含任何项目数据）拿到非空完整回复。连库、取模型、HttpClient 与请求体全部复用离线评测本身的实现，
    /// 所以本机开代理导致的 HTTP 502、模型未绑定、测试库不通都会在这里失败。失败抛异常，由调用方拦下。
    /// 环境变量 HSJM_LOGICEVAL_PROBE_DEAD_MODEL=1 只用于冒烟验证探测失败路径：探测请求改发到本机不可达端口，只能让探测失败、不会改变真实运行。</summary>
    public static async Task<string> ProbeAsync(CancellationToken ct)
    {
        await using var db = await EvalDb.OpenAsync("hsjm_ai_new_test", readOnly: true);
        await using (var cmd = new NpgsqlCommand("select 1", db))
            if (await cmd.ExecuteScalarAsync(ct) is not 1) throw new InvalidOperationException("测试库只读查询 select 1 未返回 1");
        using var model = await OfflineModel.LoadAsync(db);
        var url = Environment.GetEnvironmentVariable("HSJM_LOGICEVAL_PROBE_DEAD_MODEL") == "1" ? "http://127.0.0.1:9/v1" : null;
        var reply = await model.CompleteAsync("你是连通性检查。只回复 ok，不要输出其它内容。", "ok", ct, url);
        if (string.IsNullOrWhiteSpace(reply)) throw new InvalidOperationException($"模型 {model.ModelName} 返回空回复");
        return $"测试库 hsjm_ai_new_test 可连接；模型 {model.ModelName}（{model.Model.BaseUrl}）回复 {reply.Trim().Length} 字";
    }
}
