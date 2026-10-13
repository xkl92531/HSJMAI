#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public static class NetworkAiStatus
{
    public const string Ok = "Ok", Failed = "Failed", TooLarge = "TooLarge", Deadline = "Deadline";
}

public sealed record NetworkAiOutcome(long UnitId, string BlockName, int? Network, string Status, AiNetworkReply? Reply, string CacheKey,
    string? Error);

/// <summary>spec §6 普通网络（裁定 F6/F21，用户：FC 完全不管）：块类型不是 FC、不是缺程序体、程序段里没有任何调用语句、
/// 没有任何源码语句（SCL 里的块调用会成为不透明源码）、至少有一条非文字语句、读写的唯一 Bool %I/%Q 点 ≥ 2；
/// 所在块有跳转/返回的不选（合并时也不用，见 UnderstandingRuleSource.Merge，提前排除免得白调 AI）。</summary>
public static class NetworkAiSelector
{
    public static IReadOnlyList<(PipelineUnit Unit, IrUnit Ir)> Select(IReadOnlyList<(PipelineUnit Unit, IrUnit Ir)> translated,
        IReadOnlyList<PipelineTag> tags)
    {
        var io = ConstantOutputs.UniqueTags(tags).Values
            .Where(t => string.Equals(t.DataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase)
                && (t.Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase) || t.Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 与 IrBlock.HasControlFlow 同口径：块内任一程序段有跳转/返回
        var controlFlowBlocks = translated
            .Where(x => x.Ir.Statements.Any(s => s.Kind is IrStatementKinds.Jump or IrStatementKinds.JumpNot or IrStatementKinds.Return))
            .Select(x => x.Unit.BlockName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return translated.Where(x =>
                !string.Equals(x.Unit.BlockKind?.Trim(), "FC", StringComparison.OrdinalIgnoreCase)
                && !controlFlowBlocks.Contains(x.Unit.BlockName)
                && x.Unit.UnitKind != "MissingBody" && x.Ir.UnitKind != "MissingBody"
                && x.Ir.Statements.All(s => s.Call is null && s.Kind is not (IrStatementKinds.Call or IrStatementKinds.Source))
                && x.Ir.Statements.Any(s => s.Kind is not (IrStatementKinds.Label or IrStatementKinds.Note))
                && UnitLocator.IoTouched(x.Ir, n => io.Contains(n.Trim())) >= 2)
            .ToArray();
    }
}

/// <summary>材料拼装：所在块接口、上一层调用（前 3 处）、本段读到的被调实例的契约、点表行。只在读取前单线程调用。</summary>
sealed class NetworkAiMaterials
{
    const int MaxCallers = 3;
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    readonly PipelineSnapshot source;
    readonly IReadOnlyDictionary<string, FbContractOutcome> contracts;
    readonly ILookup<string, IrCall> callsByBlock;
    readonly ILookup<string, string> callersByCallee;
    readonly Dictionary<string, IrBlock> blocks = new(C);
    readonly Dictionary<string, PipelineTag> tags;

    public NetworkAiMaterials(PipelineSnapshot source, IReadOnlyList<(PipelineUnit Unit, IrUnit Ir)> translated,
        IReadOnlyDictionary<string, FbContractOutcome> contracts)
    {
        this.source = source; this.contracts = contracts;
        tags = source.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Name)).GroupBy(t => t.Name.Trim(), C)
            .ToDictionary(g => g.Key, g => g.First(), C);
        var calls = translated.SelectMany(x => x.Ir.Statements.Where(s => s.Call is not null).Select(s => (x.Unit, Statement: s))).ToArray();
        callsByBlock = calls.ToLookup(c => c.Unit.BlockName, c => c.Statement.Call!, C);
        callersByCallee = calls.ToLookup(c => c.Statement.Call!.Callee,
            c => $"**{c.Unit.BlockName}** 网络 {c.Unit.NetworkOrder}：{IrText.Statement(c.Statement)}", C);
    }

    IrBlock Block(string name) => blocks.TryGetValue(name, out var b) ? b : blocks[name] = IrBlock.Load(source, name);

    public string BuildUser(PipelineUnit unit, IrUnit ir)
    {
        var block = Block(unit.BlockName);
        var vars = Vars(ir);
        var io = vars.Where(tags.ContainsKey).Select(v => tags[v]).DistinctBy(t => t.Name, C)
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        return NetworkAiPrompt.BuildUser(ir, block, callersByCallee[unit.BlockName].Take(MaxCallers).ToArray(), io,
            InstanceContracts(unit.BlockName, vars));
    }

    /// <summary>本段提到的变量（写入目标与节点展开后的读取，去空白）。</summary>
    static HashSet<string> Vars(IrUnit ir)
    {
        var set = new HashSet<string>(C);
        foreach (var s in ir.Statements)
        {
            foreach (var w in ProjectWrites.StatementWrites(s))
                if (!string.IsNullOrWhiteSpace(w.Target)) set.Add(w.Target.Trim());
            foreach (var e in ProjectWrites.Expressions(s))
                foreach (var v in IrBlock.ExpandNodesIn(ir, e).Vars()) set.Add(v.Trim());
        }
        return set;
    }

    /// <summary>本段读到「某调用实例.成员」时，该实例所属被调块已核对的契约（契约读取成功才附）。</summary>
    IReadOnlyList<string> InstanceContracts(string blockName, IReadOnlySet<string> vars)
    {
        var lines = new List<string>();
        foreach (var call in callsByBlock[blockName])
        {
            var instance = call.Instance?.Trim();
            if (string.IsNullOrEmpty(instance) || !vars.Any(v => v.StartsWith(instance + ".", StringComparison.OrdinalIgnoreCase))) continue;
            if (!contracts.TryGetValue(call.Callee, out var outcome) || outcome.Status != FbContractStatus.Ok || outcome.Contract is null) continue;
            var accepted = outcome.Contract.Accepted.Where(v => v.DerivedFrom is null).Select(FbContractReader.Summarize).ToArray();
            if (accepted.Length > 0) lines.Add($"- 实例 {instance}（{call.Callee}）：{string.Join("；", accepted)}");
        }
        return lines.Distinct(StringComparer.Ordinal).ToArray();
    }
}

/// <summary>
/// 普通网络 AI 读取（spec §6）：命中缓存直接用（期限过后也用）；材料超长 → TooLarge；期限已过不再发起新的 AI 调用 → Deadline；
/// AI 失败或回复不可用 → Failed 且不写缓存；单段任何异常只让本段 Failed，调用方取消照常抛出。并发数 parallelism。
/// budget：普通网络阶段自己的期限（从读取开始算，只限制发起新调用）。readingDeadline：理解步骤的读取期限（裁定 F11，与 FB 契约读取
/// 同一个：总期限 − 余量）——到期不再发起新调用、正在进行的调用放弃并记 Deadline，不连坐已读好的契约与报警清单。
/// 缓存键 = 系统提示词 + 用户材料 + 模型指纹 + 提示词版本 + 核对器版本；缓存只存回复原文，核对与分级每次重新跑。
/// </summary>
public sealed class NetworkAiReader(INetworkAiCache cache, ContractCompletion complete, string modelFingerprint,
    int parallelism = 3, TimeSpan? budget = null, int maxChars = NetworkAiPrompt.MaxChars, CancellationToken readingDeadline = default,
    Func<long, bool>? bypassCache = null)
{
    // bypassCache（审查 P3 refreshAi，2026-10-02）：对这些程序段不读缓存、重新问 AI；成功后照常覆盖写缓存（等价于清掉该段缓存再跑）
    const string DeadlineReason = "ReadingDeadline";

    public async Task<IReadOnlyDictionary<long, NetworkAiOutcome>> ReadAsync(PipelineSnapshot source,
        IReadOnlyDictionary<string, FbContractOutcome> contracts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var translated = source.Units.Select(u => (Unit: u, Ir: UnitTranslator.Translate(u, source.Tags))).ToArray();
        var materials = new NetworkAiMaterials(source, translated, contracts);
        var jobs = new List<(PipelineUnit Unit, string? User, string? Error)>();
        foreach (var (unit, ir) in NetworkAiSelector.Select(translated, source.Tags))
        {
            ct.ThrowIfCancellationRequested();
            try { jobs.Add((unit, materials.BuildUser(unit, ir), null)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { jobs.Add((unit, null, Describe(ex))); }
        }
        var stopAt = budget is { } b ? DateTime.UtcNow + b : DateTime.MaxValue;
        using var gate = new SemaphoreSlim(Math.Max(1, parallelism));
        var results = await Task.WhenAll(jobs.Select(async job =>
        {
            if (job.User is null)
                return new NetworkAiOutcome(job.Unit.Id, job.Unit.BlockName, job.Unit.NetworkOrder, NetworkAiStatus.Failed, null, "", job.Error);
            await gate.WaitAsync(ct);
            try { return await ReadOneAsync(job.Unit, job.User, stopAt, ct); }
            finally { gate.Release(); }
        }));
        return results.GroupBy(r => r.UnitId).ToDictionary(g => g.Key, g => g.First());
    }

    async Task<NetworkAiOutcome> ReadOneAsync(PipelineUnit unit, string user, DateTime stopAt, CancellationToken ct)
    {
        NetworkAiOutcome Outcome(string status, AiNetworkReply? reply, string key, string? error) =>
            new(unit.Id, unit.BlockName, unit.NetworkOrder, status, reply, key, error);
        var key = PipelineHash.Of(JsonSerializer.Serialize(new
        {
            System = NetworkAiPrompt.System, User = user, Model = modelFingerprint, Prompt = NetworkAiPrompt.Version,
            Check = NetworkConstraintValidator.Version
        }));
        try
        {
            if (user.Length > maxChars) return Outcome(NetworkAiStatus.TooLarge, null, key, null);
            NetworkAiCacheEntry? cached = null;
            if (bypassCache?.Invoke(unit.Id) != true)
            {
                try { cached = await cache.GetAsync(key, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }   // 读缓存失败按未命中
            }
            if (cached?.Status == NetworkAiStatus.Ok && Parsed(cached.ReplyJson) is { } hit) return Outcome(NetworkAiStatus.Ok, hit, key, null);
            if (DateTime.UtcNow >= stopAt || readingDeadline.IsCancellationRequested)
                return Outcome(NetworkAiStatus.Deadline, null, key, DeadlineReason);
            string json;
            using (var call = CancellationTokenSource.CreateLinkedTokenSource(ct, readingDeadline))
            {
                try { json = await complete(NetworkAiPrompt.System, user, call.Token); }
                catch (Exception) when (!ct.IsCancellationRequested && readingDeadline.IsCancellationRequested)
                { return Outcome(NetworkAiStatus.Deadline, null, key, DeadlineReason); }
            }
            if (Parsed(json) is not { } reply) return Outcome(NetworkAiStatus.Failed, null, key, "AiReplyUnusable");
            string? note = null;
            try
            {
                await cache.SaveAsync(new(key, unit.BlockName, unit.NetworkOrder, NetworkAiStatus.Ok, json, null, modelFingerprint,
                    NetworkAiPrompt.Version), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { note = "CacheSaveFailed: " + Describe(ex); }
            return Outcome(NetworkAiStatus.Ok, reply, key, note);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Outcome(NetworkAiStatus.Failed, null, key, Describe(ex)); }
    }

    static AiNetworkReply? Parsed(string? json)
    {
        var r = AiNetworkReplyParser.Parse(json);
        return r.Status == AiReplyStatus.Parsed ? r.Reply : null;
    }

    static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;
}
