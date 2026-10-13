#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record FbContractOutcome(string FbName, string Status, ValidatedFbContract? Contract, string CacheKey, string? Error);

public delegate Task<string> ContractCompletion(string system, string user, CancellationToken ct);

/// <summary>超长 FB 分段读取部分段失败时写进 FbContractOutcome.Error 的标记（读取器与按范围重跑的完整性检查共用）。</summary>
public static class FbContractSegmentFailure
{
    public const string Marker = "SegmentFailed:";
}

/// <summary>需要读契约的 FB：某个调用点的实参是 IO 点，或是「已选中块」的形参（多层透传）。纯结构判定。</summary>
public static class RelevantFbSelector
{
    const int MaxRounds = 16;

    public static IReadOnlySet<string> Select(PipelineSnapshot source)
    {
        var C = StringComparer.OrdinalIgnoreCase;
        var io = source.Tags.Where(t => t.Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)
                || t.Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name).ToHashSet(C);
        var fbs = source.Units.Where(u => string.Equals(u.BlockKind, "FB", StringComparison.OrdinalIgnoreCase))
            .Select(u => u.BlockName).ToHashSet(C);
        var formals = source.Units.GroupBy(u => u.BlockName, C).ToDictionary(g => g.Key,
            g => InterfaceReader.Read(g.Select(u => u.InterfaceXml).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "")
                .Where(m => m.Section is "Input" or "Output" or "InOut").Select(m => m.Name).ToHashSet(C), C);
        var calls = new List<(string Caller, string Callee, string[] Actuals)>();
        foreach (var u in source.Units)   // 不依赖 Callees 列表：以程序体里实际的调用为准
        {
            var ir = UnitTranslator.Translate(u, source.Tags);
            var expander = IrBlock.FromUnits(u.BlockName, [ir], []);
            foreach (var call in ir.Statements.Select(s => s.Call).OfType<IrCall>())
            {
                if (!string.Equals(call.BlockType, "FB", StringComparison.OrdinalIgnoreCase) || !fbs.Contains(call.Callee)) continue;
                var actuals = call.Pins
                    .SelectMany(p => (p.Value is null ? [] : expander.ExpandNodes(ir, p.Value).Vars()).Concat(p.Targets))
                    .ToArray();
                calls.Add((u.BlockName, call.Callee, actuals));
            }
        }
        var selected = new HashSet<string>(C);
        for (var round = 0; round < MaxRounds; round++)
        {
            var before = selected.Count;
            foreach (var (caller, callee, actuals) in calls)
            {
                if (selected.Contains(callee)) continue;
                var callerFormals = selected.Contains(caller) && formals.TryGetValue(caller, out var f) ? f : null;
                if (actuals.Any(a => io.Contains(a) || (callerFormals is not null && callerFormals.Contains(Root(a)))))
                    selected.Add(callee);
            }
            if (selected.Count == before) break;
        }
        return selected;
    }

    static string Root(string name)
    {
        var cut = name.IndexOfAny(['.', '[']);
        return cut < 0 ? name : name[..cut];
    }
}

/// <summary>
/// 按调用图自底向上读取 FB 契约（spec §4）：下层已核对的契约作为上层的输入；成功按内容哈希缓存，失败不缓存。
/// 程序体翻译超过 maxBodyChars 的块按网络分段读取（spec §3.2 规则 5），各段关系在整块 IR 上统一核对。
/// readingDeadline：读取期限（理解步骤总期限减去余量）。期限一到不再发起新的模型调用、正在进行的调用放弃，
/// 已读完的块与段照常核对使用，缓存命中照常读取；未读的块记 Failed（ReadingDeadline），未读的段记入该块的失败段。
/// </summary>
public sealed class FbContractReader(IFbContractCache cache, ContractCompletion complete, string modelFingerprint,
    int maxBodyChars = FbContractPrompt.MaxBodyChars, CancellationToken readingDeadline = default)
{
    const string DeadlineReason = "ReadingDeadline";

    public async Task<IReadOnlyDictionary<string, FbContractOutcome>> ReadAsync(PipelineSnapshot source,
        IReadOnlyCollection<string> fbNames, CancellationToken ct)
    {
        var C = StringComparer.OrdinalIgnoreCase;
        var wanted = fbNames.ToHashSet(C);
        // 与 RelevantFbSelector、Merge 同一口径：以程序体里实际的调用为准（不依赖 Callees 列表）
        var callees = source.Units.Where(u => wanted.Contains(u.BlockName)).GroupBy(u => u.BlockName, C)
            .ToDictionary(g => g.Key, g => g.SelectMany(u => UnitTranslator.Translate(u, source.Tags).Statements)
                .Select(s => s.Call?.Callee).OfType<string>().Where(wanted.Contains).ToHashSet(C), C);
        var result = new Dictionary<string, FbContractOutcome>(C);
        foreach (var fb in TopologicalOrder(wanted, callees))
        {
            ct.ThrowIfCancellationRequested();
            result[fb] = await ReadOneAsync(source, fb, callees.GetValueOrDefault(fb) ?? [], result, ct);
        }
        return result;
    }

    static IEnumerable<string> TopologicalOrder(HashSet<string> nodes, Dictionary<string, HashSet<string>> edges)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        void Visit(string n)
        {
            if (done.Contains(n) || !visiting.Add(n)) return;   // 环：直接跳过回边
            foreach (var m in (edges.GetValueOrDefault(n) ?? []).Order(StringComparer.Ordinal)) Visit(m);
            visiting.Remove(n); done.Add(n); order.Add(n);
        }
        foreach (var n in nodes.Order(StringComparer.Ordinal)) Visit(n);
        return order;
    }

    /// <summary>单个块的读取全部包在这里：任何异常（取消除外）只让本块 Failed，不丢其它块的结果。</summary>
    async Task<FbContractOutcome> ReadOneAsync(PipelineSnapshot source, string fb, HashSet<string> lowerNames,
        Dictionary<string, FbContractOutcome> done, CancellationToken ct)
    {
        var key = "";
        try { return await ReadOneCoreAsync(source, fb, lowerNames, done, k => key = k, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(fb, FbContractStatus.Failed, null, key, Describe(ex)); }
    }

    async Task<FbContractOutcome> ReadOneCoreAsync(PipelineSnapshot source, string fb, HashSet<string> lowerNames,
        Dictionary<string, FbContractOutcome> done, Action<string> reportKey, CancellationToken ct)
    {
        var block = IrBlock.Load(source, fb);
        if (!block.HasBody) return await NotReadAsync(source, fb, FbContractStatus.NoBody, reportKey, ct);
        if (block.IsTextOnly) return await NotReadAsync(source, fb, FbContractStatus.TextOnly, reportKey, ct);

        // 缓存键 = AI 实际看到的全部材料：下层结果、核对规则、翻译器任何一处变化都会让上层重读
        var lowerInfo = lowerNames.Where(done.ContainsKey).Order(StringComparer.Ordinal).Select(n => done[n])
            .Where(l => l.Contract is not null && l.Status == FbContractStatus.Ok)
            .Select(l => new LowerContractInfo(l.FbName, string.Join("；", l.Contract!.Accepted
                // 派生关系（如由超时报警派生的到位反馈）不是 AI 读出的，不进上层提示材料，缓存键不变
                .Where(v => v.DerivedFrom is null).Select(Summarize)))).ToArray();
        if (block.TranslationLength > maxBodyChars)
        {
            var plan = FbContractSegments.Split(block, maxBodyChars);
            return plan.Segments.Count == 0
                ? await NotReadAsync(source, fb, FbContractStatus.TooLarge, reportKey, ct)
                : await ReadSegmentsAsync(source, fb, block, plan, lowerInfo, reportKey, ct);
        }
        var user = FbContractPrompt.BuildUser(block, lowerInfo);
        var key = PipelineHash.Of(JsonSerializer.Serialize(new
        {
            System = FbContractPrompt.System, User = user, Model = modelFingerprint, Prompt = FbContractPrompt.Version
        }));
        reportKey(key);

        var cached = await cache.GetAsync(key, ct);
        // 缓存只存 AI 原文；校验是确定性代码，每次重新跑。缓存内容无法解析时按未命中处理。
        var parsed = cached?.Status == FbContractStatus.Ok ? Parsed(cached.ContractJson) : null;
        string? note = null;
        if (parsed is null)
        {
            var (json, error) = await CompleteAsync(user, ct);
            if (json is null) return new(fb, FbContractStatus.Failed, null, key, error);
            parsed = Parsed(json);
            if (parsed is null) return new(fb, FbContractStatus.Failed, null, key, "AiReplyUnusable");
            note = await TrySaveAsync(new(key, fb, FbContractStatus.Ok, json, null, modelFingerprint, FbContractPrompt.Version), ct);
        }
        var validated = FbContractValidator.Validate(parsed, block, Proven(source, fb));
        return new(fb, validated.Accepted.Count == 0 ? FbContractStatus.Rejected : FbContractStatus.Ok, validated, key, note);
    }

    /// <summary>不调 AI（无程序体 / 纯文字 / 超长且没有可装的段）：按块内容取稳定键，写缓存只为报告。</summary>
    async Task<FbContractOutcome> NotReadAsync(PipelineSnapshot source, string fb, string status, Action<string> reportKey, CancellationToken ct)
    {
        var units = source.Units.Where(u => string.Equals(u.BlockName, fb, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.NetworkOrder ?? int.MaxValue).ThenBy(u => u.Id).ToArray();
        var contentKey = PipelineHash.Of(JsonSerializer.Serialize(new
        {
            FbName = fb.ToUpperInvariant(),
            Interface = units.Select(u => u.InterfaceXml).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "",
            Bodies = units.Select(u => u.RawContent),
            Prompt = FbContractPrompt.Version
        }));
        reportKey(contentKey);
        var saveError = await TrySaveAsync(new(contentKey, fb, status, null, null, modelFingerprint, FbContractPrompt.Version), ct);
        return new(fb, status, null, contentKey, saveError);
    }

    /// <summary>
    /// 超长块分段读取：每段单独调用 AI、按实际发送内容单独缓存（FbName 记为「块名#k/n」，失败段不缓存），
    /// 各段关系合并后在整块 IR 上统一核对。全部段失败 → Failed；部分失败 → 照常 Ok/Rejected，Error 写明失败段。
    /// 结果的 CacheKey 由各段键拼接取哈希（任一段材料变化即变化）。
    /// </summary>
    async Task<FbContractOutcome> ReadSegmentsAsync(PipelineSnapshot source, string fb, IrBlock block, FbContractSegmentPlan plan,
        IReadOnlyList<LowerContractInfo> lower, Action<string> reportKey, CancellationToken ct)
    {
        var count = plan.Segments.Count;
        var keys = new List<string>();
        var relations = new List<AiContractRelation>();
        var understandings = new List<string>();
        var failed = new List<string>();
        var saveErrors = new List<string>();   // F17：每段独立写缓存，一段写失败不影响后续段
        for (var k = 0; k < count; k++)
        {
            var user = FbContractPrompt.BuildSegmentUser(block, plan.Segments[k], k + 1, count, plan.Skipped, lower);
            var key = PipelineHash.Of(JsonSerializer.Serialize(new
            {
                System = FbContractPrompt.System, User = user, Model = modelFingerprint, Prompt = FbContractPrompt.Version
            }));
            keys.Add(key);
            var cached = await cache.GetAsync(key, ct);
            var parsed = cached?.Status == FbContractStatus.Ok ? Parsed(cached.ContractJson) : null;
            if (parsed is null)
            {
                var (json, callError) = await CompleteAsync(user, ct);
                if (json is null) { failed.Add($"{k + 1}/{count} {callError}"); continue; }
                parsed = Parsed(json);
                if (parsed is null) { failed.Add($"{k + 1}/{count} AiReplyUnusable"); continue; }
                if (await TrySaveAsync(new(key, $"{fb}#{k + 1}/{count}", FbContractStatus.Ok, json, null, modelFingerprint,
                        FbContractPrompt.Version), ct) is { } saveError)
                    saveErrors.Add($"{k + 1}/{count} {saveError}");
            }
            relations.AddRange(parsed.Relations);
            if (parsed.Understanding.Length > 0) understandings.Add(parsed.Understanding);
        }
        var outcomeKey = PipelineHash.Of(string.Join("|", keys));
        reportKey(outcomeKey);
        var notes = new[]
        {
            failed.Count > 0 ? FbContractSegmentFailure.Marker + " " + string.Join("；", failed) : null,
            plan.Skipped.Count > 0 ? "SegmentTooLarge: " + string.Join("、", plan.Skipped) : null,
            saveErrors.Count > 0 ? string.Join("；", saveErrors) : null
        }.OfType<string>().ToArray();
        var error = notes.Length == 0 ? null : string.Join("；", notes);
        if (failed.Count == count) return new(fb, FbContractStatus.Failed, null, outcomeKey, "AllSegmentsFailed: " + error);
        var validated = FbContractValidator.Validate(new AiFbContract(fb, string.Join(" ", understandings), relations), block, Proven(source, fb));
        return new(fb, validated.Accepted.Count == 0 ? FbContractStatus.Rejected : FbContractStatus.Ok, validated, outcomeKey, error);
    }

    /// <summary>
    /// 调用模型：成功返回回复原文，失败返回说明（调用方的取消照常抛出）。读取期限已到时不再调用，
    /// 正在进行的调用随期限一起取消。
    /// </summary>
    async Task<(string? Json, string? Error)> CompleteAsync(string user, CancellationToken ct)
    {
        if (readingDeadline.IsCancellationRequested) return (null, DeadlineReason);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, readingDeadline);
        try { return (await complete(FbContractPrompt.System, user, call.Token), null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) when (readingDeadline.IsCancellationRequested) { return (null, DeadlineReason); }
        catch (Exception ex) { return (null, Describe(ex)); }
    }

    /// <summary>写缓存失败只丢缓存，不影响本次结果；返回说明文字（成功为 null）。</summary>
    async Task<string?> TrySaveAsync(FbContractCacheEntry entry, CancellationToken ct)
    {
        try { await cache.SaveAsync(entry, ct); return null; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return "CacheSaveFailed: " + Describe(ex); }
    }

    static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    static AiFbContract? Parsed(string? json)
    {
        var r = AiFbContractParser.Parse(json);
        return r.Status == AiContractParseStatus.Parsed ? r.Contract : null;
    }

    /// <summary>第一期代码证明；推断本身出错时按「没有证明」处理，不让一个块拖垮整批读取。</summary>
    static FbContract Proven(PipelineSnapshot source, string fb)
    {
        try { return FbContractInference.Infer(source, fb); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return FbContract.Empty(fb); }
    }

    internal static string Summarize(ValidatedRelation v)
    {
        var r = v.Relation;
        string At(IReadOnlyList<string> xs) => xs.Count > 0 ? xs[0] : "?";
        return r.Kind switch
        {
            "feedback" => $"feedback: {At(r.Outputs)} 发出后 {r.PositionVar} 应到位（时间 {r.DelayParam}）",
            "timeoutAlarm" => $"timeoutAlarm: {r.Alarm} ← {At(r.Outputs)} 超时未到 {r.PositionVar}",
            "gate" => $"gate: {At(r.Inputs)} 为 false 时 {string.Join("、", r.Outputs)} 为 false",
            "mutex" => $"mutex: {string.Join("/", r.Outputs)}",
            "contradiction" => $"contradiction: {string.Join("/", r.Inputs)}",
            "expectedStateAlarm" => $"expectedStateAlarm: {r.Alarm} ← {At(r.Inputs)} 偏离期望状态（配置 {string.Join("、", r.ConfigInputs)}；"
                + string.Join("；", r.ExpectedStates.Select(c => string.Join(",", c.When.OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => $"{x.Key}={(x.Value ? 1 : 0)}")) + $" 时应为 {(c.Expected ? "true" : "false")}")) + $"，时间 {r.DelayParam}）",
            _ => r.Kind
        };
    }
}
