#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;
using static HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353FlgNet;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>
/// 把 FB 契约代入调用点的实际接线（spec 2026-09-30 §4.3）。只处理无条件执行（en 未接或恒真）的调用；
/// 输入引脚只接单个常开全局信号才算接线；输出必须是全项目唯一由本调用该引脚写的 %Q；
/// 涉及引脚未接线、信号不是对应区的唯一 Bool 点 → 该关系跳过。单个调用异常只跳过该调用。
/// </summary>
public sealed class Original353FbContractRules(PipelineSnapshot source)
{
    public const string MutexProof = "FbContract:Mutex";
    public const string GateProof = "FbContract:Gate";
    public const string ContradictionProof = "FbContract:Contradiction";

    sealed record CoilWriter(string Kind, LogicTerm? Expression, long UnitId, string PartId);
    sealed record WriteSite(long UnitId, string PartId, string Port);
    sealed record WriteIndex(Dictionary<string, List<WriteSite>> Sites, IReadOnlyList<string> OpaqueText);

    readonly Dictionary<string, FbContract> contracts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> callerControlFlow = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> overlapFree = new(StringComparer.OrdinalIgnoreCase);
    readonly Lazy<Dictionary<string, List<CoilWriter>>> writers = new(() => IndexCoilWriters(source));
    readonly Lazy<WriteIndex> writes = new(() => IndexGlobalWrites(source));

    /// <summary>推断异常（畸形接口 / 程序体）按空契约缓存：不在每个调用点重算、重抛。</summary>
    public FbContract ContractFor(string fbName)
    {
        if (contracts.TryGetValue(fbName, out var cached)) return cached;
        FbContract contract;
        try { contract = FbContractInference.Infer(source, fbName); }
        catch (Exception e) when (e is not OperationCanceledException) { contract = FbContract.Empty(fbName); }
        return contracts[fbName] = contract;
    }

    /// <summary>调用所在块（同名块的所有单元）是否含跳转 / 返回：有则调用可能在某些周期不执行。无法解析的单元按有处理。</summary>
    bool CallerHasControlFlow(string blockName)
    {
        if (callerControlFlow.TryGetValue(blockName ?? "", out var cached)) return cached;
        var result = source.Units.Where(u => string.Equals(u.BlockName, blockName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(u.RawContent))
            .Any(u =>
            {
                try { return HasControlFlow(XElement.Parse(u.RawContent)); }
                catch (System.Xml.XmlException) { return true; }
            });
        return callerControlFlow[blockName ?? ""] = result;
    }

    public IReadOnlyList<PipelineRule> Extract(PipelineUnit unit)
    {
        if (string.IsNullOrWhiteSpace(unit.RawContent)) return [];
        XElement root;
        try { root = XElement.Parse(unit.RawContent); } catch (System.Xml.XmlException) { return []; }
        if (!Is(root, "FlgNet")) return [];
        Original353FlgNet net;
        try { net = new(root); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return []; }

        var rules = new List<PipelineRule>();
        foreach (var call in net.Calls)
        {
            try { rules.AddRange(ExtractCall(unit, net, call)); }
            catch (Exception e) when (e is not OperationCanceledException) { /* 畸形调用或契约推断失败：只跳过该调用 */ }
        }
        return rules.DistinctBy(r => r.Key).ToArray();
    }

    List<PipelineRule> ExtractCall(PipelineUnit unit, Original353FlgNet net, XElement call)
    {
        var rules = new List<PipelineRule>();
        var infos = call.Elements().Where(e => Is(e, "CallInfo")).ToArray();
        if (infos.Length != 1 || Original353FlgNet.A(infos[0], "BlockType") != "FB") return rules;
        var callId = Original353FlgNet.A(call, "UId");
        // 条件执行的调用：契约只在 en 为真时成立，不能当作无条件约束
        if (net.IsWired(callId, "en") && !(net.Pin(callId, "en") is { } en && PipelineTerms.IsTrue(en))) return rules;
        var contract = ContractFor(Original353FlgNet.A(infos[0], "Name"));
        if (contract.IsEmpty) return rules;

        string? Out(string pin)
        {
            var bound = net.BoundOutput(callId, pin);
            return bound != null && !net.HasOtherWrite(bound, callId, pin) && BoolTag(source, bound, "%Q")
                && SoleWriter(bound, new(unit.Id, callId, pin)) ? bound : null;
        }
        string? In(string literal)
        {
            var cut = literal.IndexOfAny(['.', '[']);
            var port = cut < 0 ? literal : literal[..cut];
            var bound = SingleSignal(net.Pin(callId, port));
            return bound == null || !net.IsGlobalSymbol(bound) ? null : bound + literal[port.Length..];
        }

        foreach (var m in contract.Mutexes)
            if (Out(m.OutputA) is { } a && Out(m.OutputB) is { } b && !a.Equals(b, StringComparison.OrdinalIgnoreCase))
                rules.Add(new PipelineRule("AT_MOST_ONE", [new(a, "Member"), new(b, "Member")],
                    $"FB {contract.FbName} 程序体证明 {m.OutputA} 与 {m.OutputB} 互斥（{string.Join("；", m.Evidence)}）")
                    { Proof = MutexProof });

        // 调用所在块有跳转 / 返回：本调用可能不执行，门控不可证（互斥、矛盾基于背景数据保持，不受影响）
        foreach (var g in contract.Gates.Where(_ => !CallerHasControlFlow(unit.BlockName)))
        {
            if (In(g.Input) is not { } gate) continue;
            var targets = g.Outputs.Select(Out).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (targets.Length == 0) continue;
            rules.Add(new PipelineRule("REQUIRES_0_WHEN_FALSE",
                [new(gate, "Source"), .. targets.Select(t => new PipelineSignal(t, "Target"))],
                $"FB {contract.FbName} 程序体证明 {g.Input} 为 false 时 {string.Join("、", g.Outputs)} 为 false（{string.Join("；", g.Evidence)}）")
                { Proof = GateProof, UpstreamSignals = BoolTag(source, gate, "%I") ? [] : Upstream(gate) });
        }

        foreach (var c in contract.Contradictions)
            if (In(c.InputA) is { } a && In(c.InputB) is { } b && !a.Equals(b, StringComparison.OrdinalIgnoreCase)
                && BoolTag(source, a, "%I") && BoolTag(source, b, "%I"))
                rules.Add(new PipelineRule("AT_MOST_ONE", [new(a, "Member"), new(b, "Member")],
                    $"FB {contract.FbName} 程序体把 {c.InputA} 与 {c.InputB} 同时成立当作故障（{string.Join("；", c.Evidence)}）")
                    { Proof = ContradictionProof, Applicability = "AfterSettling", RequiresSettlingEvidence = true });
        return rules;
    }

    /// <summary>
    /// 全项目对该全局符号的写恰好一处且就是 expected，且没有任何其它写入位置可能与它重叠（父级符号、覆盖该位的
    /// 字节 / 字 / 双字标签、换算出的绝对地址、无法确定的写入位置）；无法解析为 XML 的纯文本单元只要提到该符号、
    /// 与它可能重叠的标签名，或（该符号在 Q 区时）出现 Q 区绝对地址，就算可能写。
    /// </summary>
    bool SoleWriter(string symbol, WriteSite expected) =>
        writes.Value.Sites.TryGetValue(symbol, out var sites) && sites.Count == 1 && sites[0] == expected && OverlapFree(symbol);

    bool OverlapFree(string symbol)
    {
        if (overlapFree.TryGetValue(symbol, out var cached)) return cached;
        var index = writes.Value;
        var aliases = BusinessMemoryAliases.For(source);
        var result = !index.Sites.ContainsKey(UnknownLocation)
            && !index.Sites.Keys.Any(other => !other.Equals(symbol, StringComparison.OrdinalIgnoreCase) && aliases.MayOverlap(symbol, other));
        if (result && index.OpaqueText.Count > 0)
        {
            var names = source.Tags.Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)
                    && (n.Equals(symbol, StringComparison.OrdinalIgnoreCase) || aliases.MayOverlap(symbol, n)))
                .Append(symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var outputArea = aliases.Resolve(symbol).Range?.Area == "Q";
            result = !index.OpaqueText.Any(text => names.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase))
                || (outputArea && AbsoluteOutputText.IsMatch(text)));
        }
        return overlapFree[symbol] = result;
    }

    /// <summary>纯文本（STL 等）里的 Q 区绝对地址：Q 1.0、QB 1、%QW4 等（保守，宁可多认）。</summary>
    static readonly System.Text.RegularExpressions.Regex AbsoluteOutputText = new(@"(?<![A-Za-z0-9_])%?Q[BWDX]?\s*\d",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>引脚表达式只含一个常开信号（可带左母线常量 true）时返回该信号名。</summary>
    static string? SingleSignal(LogicTerm? term)
    {
        if (term == null) return null;
        if (term.Kind == "Signal") return term.Name;
        if (term.Kind != "And") return null;
        var rest = term.Children!.Where(c => !(c.Kind == "Constant" && c.Name == "true")).ToArray();
        return rest.Length == 1 ? SingleSignal(rest[0]) : null;
    }

    /// <summary>方案 C：DB 门控源在全项目唯一由一个全局普通线圈写时，列出该线圈条件里的 %I 点；否则空。</summary>
    IReadOnlyList<string> Upstream(string symbol)
    {
        if (!writers.Value.TryGetValue(symbol, out var list) || list.Count != 1 || list[0].Kind != "Coil" || list[0].Expression == null
            || !SoleWriter(symbol, new(list[0].UnitId, list[0].PartId, "operand")))
            return [];
        return PipelineTerms.Walk(list[0].Expression!).Where(t => t.Kind == "Signal" && BoolTag(source, t.Name!, "%I"))
            .Select(t => t.Name!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    static Dictionary<string, List<CoilWriter>> IndexCoilWriters(PipelineSnapshot source)
    {
        var index = new Dictionary<string, List<CoilWriter>>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in source.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.RawContent)) continue;
            try
            {
                XElement root;
                try { root = XElement.Parse(unit.RawContent); } catch (System.Xml.XmlException) { continue; }
                if (!Is(root, "FlgNet")) continue;
                Original353FlgNet net;
                try { net = new(root); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { continue; }
                // 先收齐再登记：单元中途异常时不留下半个单元的线圈
                var found = new List<(string Target, CoilWriter Writer)>();
                foreach (var part in net.Parts)
                {
                    var kind = Original353FlgNet.A(part, "Name");
                    if (kind is not ("Coil" or "SCoil" or "RCoil")) continue;
                    var id = Original353FlgNet.A(part, "UId");
                    if (net.OperandScope(id) != "GlobalVariable") continue;
                    var target = net.OperandSymbol(id);
                    if (target == null) continue;
                    // 取反线圈写入条件的反值：不是普通线圈，不做上游追溯
                    if (part.Elements().Any(e => Is(e, "Negated"))) kind = "Negated" + kind;
                    found.Add((target, new(kind, kind == "Coil" ? net.Pin(id, "in") : null, unit.Id, id)));
                }
                foreach (var (target, writer) in found)
                {
                    if (!index.TryGetValue(target, out var list)) index[target] = list = [];
                    list.Add(writer);
                }
            }
            // 单个单元异常只跳过该单元（上游追溯另需 SoleWriter 通过，而写入索引把异常单元记为未知写入）
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        return index;
    }

    /// <summary>
    /// 全项目全局变量写入点：FlgNet 网络按连线端口判定；其它 XML（SCL/STL/GRAPH 段）或构造失败的网络里出现的
    /// 每个全局符号保守记一处写；无法解析为 XML 的纯文本保存原文，按是否提到符号保守判定。
    /// </summary>
    static WriteIndex IndexGlobalWrites(PipelineSnapshot source)
    {
        var sites = new Dictionary<string, List<WriteSite>>(StringComparer.OrdinalIgnoreCase);
        var opaque = new List<string>();
        void Add(string symbol, WriteSite site)
        {
            if (!sites.TryGetValue(symbol, out var list)) sites[symbol] = list = [];
            list.Add(site);
        }
        foreach (var unit in source.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.RawContent)) continue;
            try
            {
                XElement root;
                try { root = XElement.Parse(unit.RawContent); } catch (System.Xml.XmlException) { opaque.Add(unit.RawContent); continue; }
                Original353FlgNet? net = null;
                if (Is(root, "FlgNet"))
                    try { net = new(root); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { }
                if (net != null)
                {
                    // 先收齐再登记：枚举中途异常时不留下半个单元的写入
                    foreach (var (symbol, partId, port) in net.GlobalWrites().ToArray()) Add(symbol, new(unit.Id, partId, port));
                    continue;
                }
                foreach (var symbol in root.Descendants().Where(e => Is(e, "Access") && Original353FlgNet.A(e, "Scope") == "GlobalVariable")
                             .Select(Location).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                    Add(symbol, new(unit.Id, "", ""));
            }
            // 单个单元异常：保守记为一处位置未知的写入（任何唯一写判定都不再成立），不让整个索引缓存异常
            catch (Exception e) when (e is not OperationCanceledException) { Add(UnknownLocation, new(unit.Id, "", "")); }
        }
        return new(sites, opaque);
    }
}
