#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;
using static HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353FlgNet;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record FbContractMutex(string OutputA, string OutputB, IReadOnlyList<string> Evidence);
public sealed record FbContractGate(string Input, IReadOnlyList<string> Outputs, IReadOnlyList<string> Evidence);
/// <summary>SetTarget：被 TON 消抖后置位的那个变量（报警位，第一期写法）；Preset：该 TON 的 PT 接线
/// （字面量原文如 T#2S，或变量符号），取不到为 null。</summary>
public sealed record FbContractContradiction(string InputA, string InputB, IReadOnlyList<string> Evidence,
    string? SetTarget = null, string? Preset = null);

public sealed record FbContract(string FbName, IReadOnlyList<FbContractMutex> Mutexes,
    IReadOnlyList<FbContractGate> Gates, IReadOnlyList<FbContractContradiction> Contradictions)
{
    public static FbContract Empty(string fbName) => new(fbName, [], [], []);
    public bool IsEmpty => Mutexes.Count == 0 && Gates.Count == 0 && Contradictions.Count == 0;
}

/// <summary>
/// 被调 FB 程序体契约推断（spec 2026-09-30 §4.2）：只看线圈/触点/OR/定时器拓扑与接口 Section，
/// 不看任何名字。内部变量只有「唯一普通线圈写、别处不写」才代换；证明不了的一律不产出。
/// </summary>
public static class FbContractInference
{
    sealed record Lit(string Name, bool Positive);
    /// <summary>Network = 网络序号（仅用于证据文字）；UnitId = 所在网络的唯一标识（判定是否同一网络）。</summary>
    /// <summary>Preset：置位线圈的 in 直接是 TON.Q 时该 TON 的 PT 接线（见 Original353FlgNet.SetTimerPreset）。</summary>
    sealed record Write(string Kind, LogicTerm? Expression, int Network, long UnitId, string? Preset = null);
    const int MaxDnfTerms = 64;

    public static FbContract Infer(PipelineSnapshot source, string fbName)
    {
        var body = source.Units.Where(u => string.Equals(u.BlockName, fbName, StringComparison.OrdinalIgnoreCase)).ToArray();
        // 体内有内容未知（RawContent 为空）的网络：写了什么无从得知，整体不推断
        if (body.Length == 0 || body.Select(u => u.BlockId).Distinct().Count() != 1
            || body.Any(u => !string.Equals(u.BlockKind, "FB", StringComparison.OrdinalIgnoreCase))
            || body.Any(u => string.IsNullOrWhiteSpace(u.RawContent)))
            return FbContract.Empty(fbName);
        var interfaces = body.Select(u => u.InterfaceXml).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (interfaces.Length != 1) return FbContract.Empty(fbName);
        XElement contract;
        try { contract = XElement.Parse(interfaces[0]); } catch (System.Xml.XmlException) { return FbContract.Empty(fbName); }
        var inputs = Members(contract, "Input").Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        // 调用方每次调用都会重新赋值的接口成员：体内的线圈永远不是它们唯一的写入方
        var externallyWritten = Members(contract, "Input").Concat(Members(contract, "InOut"))
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        // Temp 成员每次调用都从未定义的局部栈开始，定义之前的读取是垃圾值：同样不参与代换
        var notSubstitutable = new HashSet<string>(externallyWritten, StringComparer.Ordinal);
        notSubstitutable.UnionWith(Members(contract, "Temp").Select(m => m.Name));
        var outputs = Members(contract, "Output").Where(m => m.Type == "Bool").Select(m => m.Name).ToArray();

        var writes = new Dictionary<string, List<Write>>(StringComparer.Ordinal);
        var opaque = new HashSet<string>(StringComparer.Ordinal);
        // 跳转 / 返回会让后续网络在某些周期不执行：门控（「输出为真 ⇒ 输入为真」）不再成立
        var controlFlow = false;
        foreach (var unit in body)
        {
            XElement root;
            try { root = XElement.Parse(unit.RawContent); } catch (System.Xml.XmlException) { return FbContract.Empty(fbName); }
            if (HasControlFlow(root)) controlFlow = true;
            if (!Is(root, "FlgNet"))
            {
                // SCL/STL 段：其中出现的变量一律视为可能被写，不参与代换（保守）
                foreach (var access in root.Descendants().Where(e => Is(e, "Access") && Original353FlgNet.A(e, "Scope") != "Label"
                             && !Original353FlgNet.A(e, "Scope").EndsWith("Constant", StringComparison.Ordinal)))
                    opaque.Add(Location(access));
                continue;
            }
            Original353FlgNet net;
            try { net = new(root, timerPassthrough: true); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return FbContract.Empty(fbName); }
            foreach (var part in net.Parts)
            {
                var kind = Original353FlgNet.A(part, "Name");
                if (kind is not ("Coil" or "SCoil" or "RCoil")) continue;
                var id = Original353FlgNet.A(part, "UId");
                var target = net.OperandSymbol(id);
                if (target == null) continue;
                if (!writes.TryGetValue(target, out var list)) writes[target] = list = [];
                // 取反线圈写入的是条件的反值：记为一次写入（满足「唯一写」判定），但永不作为定义
                var negated = part.Elements().Any(e => Is(e, "Negated"));
                list.Add(new(kind, negated ? null : net.Pin(id, "in"), unit.NetworkOrder ?? 0, unit.Id,
                    kind == "SCoil" ? net.SetTimerPreset(id) : null));
                if (negated) opaque.Add(target);
            }
            foreach (var (symbol, part, port) in net.AccessPins())
            {
                if (part == null) { opaque.Add(symbol); continue; }
                // 线圈 operand 只有当它就是该线圈已登记的唯一目标时才跳过（写入已记在 writes）；否则按写入处理
                if (Is(part, "Part") && Original353FlgNet.A(part, "Name") is "Coil" or "SCoil" or "RCoil" && port == "operand"
                    && net.OperandSymbol(Original353FlgNet.A(part, "UId")) == symbol) continue;
                if (!IsReadPort(part, port)) opaque.Add(symbol);
            }
        }
        // 写入位置无法确定：任何内部变量都可能被改写
        if (opaque.Contains(UnknownLocation)) return FbContract.Empty(fbName);

        // 与被写 / 不透明变量符号重叠（父结构与成员、数组与元素）的变量同样可能被改写：一并不透明
        var touched = writes.Keys.Concat(opaque).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var name in writes.Keys)
            if (touched.Any(other => !string.Equals(other, name, StringComparison.Ordinal)
                    && BusinessMemoryAliases.SymbolicallyOverlaps(name, other)))
                opaque.Add(name);

        var defs = new Dictionary<string, LogicTerm>(StringComparer.Ordinal);
        foreach (var (name, list) in writes)
            if (list.Count == 1 && list[0].Kind == "Coil" && list[0].Expression != null && !opaque.Contains(name)
                && !notSubstitutable.Contains(Port(name)))
                defs[name] = list[0].Expression!;

        // 体内被写过的接口输入不再是纯外部输入，不参与门控 / 位置矛盾
        var writtenPorts = writes.Keys.Concat(opaque).Select(Port).ToHashSet(StringComparer.Ordinal);
        inputs.RemoveWhere(writtenPorts.Contains);

        var memo = new Dictionary<string, HashSet<Lit>>(StringComparer.Ordinal);
        HashSet<Lit> Closure(string variable, HashSet<string> path)
        {
            if (memo.TryGetValue(variable, out var done)) return done;
            var result = new HashSet<Lit> { new(variable, true) };
            if (defs.TryGetValue(variable, out var def) && path.Add(variable))
            {
                if (Necessary(def) is { } necessary)
                    foreach (var lit in necessary)
                    {
                        result.Add(lit);
                        if (lit.Positive && lit.Name != variable) result.UnionWith(Closure(lit.Name, path));
                    }
                path.Remove(variable);
            }
            // 环上截断时结果只会少文字（证明更少），不会多出错误关系
            memo[variable] = result;
            return result;
        }

        var closures = outputs.Where(defs.ContainsKey).ToDictionary(o => o,
            o => Closure(o, new HashSet<string>(StringComparer.Ordinal)), StringComparer.Ordinal);

        // 互斥只认「命令变量直接互锁」：V_A 自己的定义直接含 ¬V_B、V_B 自己的定义直接含 ¬V_A（不做闭包代换）。
        // 两个变量都只有这一处写入，因此扫描中任一时刻，后写的那个总是读到对方的当前值，二者在任一时刻都互斥；
        // 经中间变量转手（可能是上一周期的旧拷贝）的否定不算。
        // 输出 O 的命令变量候选：O 自身，以及 O 定义的必要条件里直接出现的、有定义的常开变量（O 被写的那一刻 O ⇒ 该变量）。
        var mutexes = new List<FbContractMutex>();
        var defined = outputs.Where(defs.ContainsKey).ToArray();
        IEnumerable<string> Commands(string output) =>
            new[] { output }.Concat((Necessary(defs[output]) ?? [])
                .Where(l => l.Positive && l.Name != output && defs.ContainsKey(l.Name))
                .Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal));
        for (var i = 0; i < defined.Length; i++)
            for (var j = i + 1; j < defined.Length; j++)
            {
                var (oa, ob) = (defined[i], defined[j]);
                var found = (from va in Commands(oa) from vb in Commands(ob) where va != vb && Interlocked(oa, ob, va, vb)
                    select (va, vb)).FirstOrDefault();
                if (found.va != null)
                    mutexes.Add(new(oa, ob, [$"{found.va} 的定义直接含 ¬{found.vb}", $"{found.vb} 的定义直接含 ¬{found.va}"]));
            }

        bool Interlocked(string oa, string ob, string va, string vb)
        {
            // 输出经复制取自命令变量时，两个复制线圈必须在同一网络：同一时刻复制的一对值才受互斥约束；
            // 且两个命令变量的定义线圈都不能在这个网络里（网络内复制与定义交错时会复制到不同时刻的值）
            if (va != oa || vb != ob)
            {
                var copiesUnit = writes[oa][0].UnitId;
                if (writes[ob][0].UnitId != copiesUnit
                    || writes[va][0].UnitId == copiesUnit || writes[vb][0].UnitId == copiesUnit) return false;
            }
            return Necessary(defs[va]) is { } na && Necessary(defs[vb]) is { } nb
                && na.Contains(new Lit(vb, false)) && nb.Contains(new Lit(va, false));
        }

        var gateOutputs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (output, closure) in closures.Where(_ => !controlFlow))
            foreach (var lit in closure.Where(l => l.Positive && inputs.Contains(Port(l.Name))))
            {
                if (!gateOutputs.TryGetValue(lit.Name, out var list)) gateOutputs[lit.Name] = list = [];
                list.Add(output);
            }
        var gates = gateOutputs.Select(g => (Input: g.Key, Outputs: g.Value.OrderBy(o => o, StringComparer.Ordinal).ToArray()))
            .Select(g => new FbContractGate(g.Input, g.Outputs,
                g.Outputs.Select(o => $"{g.Input} 常开出现在 {o} 的必要条件中").ToArray())).ToList();

        var contradictions = new List<FbContractContradiction>();
        foreach (var (target, list) in writes)
        {
            if (opaque.Contains(target)) continue;
            var sets = list.Where(w => w.Kind == "SCoil").ToArray();
            var resets = list.Where(w => w.Kind == "RCoil").ToArray();
            if (sets.Length != 1 || resets.Length == 0 || list.Any(w => w.Kind == "Coil") || sets[0].Expression is not { } setExpr) continue;
            // 置位表达式本身必须就是一个 TON（TON.Q 直连 SCoil 的 in）；与未消抖支路并联/串联的一律不产出
            if (setExpr is not { Kind: TimerKind, Name: "TON", Children: [var debounced] }) continue;
            if (!resets.Any(r => r.Expression != null && DependsOnInput(r.Expression, inputs, defs))) continue;
            // 只取 TON 的 IN 子树；其中若再嵌定时器，Dnf 返回 null，不产出
            if (Dnf(debounced) is not { } terms) continue;
            foreach (var term in terms)
            {
                if (term.Count != 2 || term.Any(l => !l.Positive || !inputs.Contains(Port(l.Name)))) continue;
                var pair = term.Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                if (contradictions.Any(c => c.InputA == pair[0] && c.InputB == pair[1])) continue;
                contradictions.Add(new(pair[0], pair[1],
                    [$"{target} 在 {pair[0]} ∧ {pair[1]} 持续成立时置位（第 {sets[0].Network} 网络，经 TON 消抖），由输入复位"],
                    target, sets[0].Preset));
            }
        }
        return new(fbName, mutexes, gates, contradictions);
    }

    /// <summary>
    /// 本 FB 自己的接口成员：只取根（Interface）下 Sections 的直接子 Section。Static 区的多重背景成员会内嵌子 FB 的
    /// Sections，其 Input/Output 不是本 FB 的端口（混进来会重名崩溃或把内部实例引脚当成本 FB 的端口）。
    /// </summary>
    static IEnumerable<(string Name, string Type)> Members(XElement contract, string section) =>
        (Is(contract, "Sections") ? [contract] : contract.Elements().Where(e => Is(e, "Sections")))
            .SelectMany(s => s.Elements().Where(e => Is(e, "Section") && Original353FlgNet.A(e, "Name") == section))
            .SelectMany(e => e.Elements().Where(m => Is(m, "Member"))).Select(m => (Original353FlgNet.A(m, "Name"), Original353FlgNet.A(m, "Datatype")));

    static string Port(string name) => name.Split('.')[0].Split('[')[0];

    /// <summary>表达式为真时必然成立的文字集合；null = 表达式不可满足。</summary>
    static HashSet<Lit>? Necessary(LogicTerm t)
    {
        switch (t.Kind)
        {
            case "Constant": return t.Name == "false" ? null : new HashSet<Lit>();
            case "Signal": return new HashSet<Lit> { new(t.Name!, true) };
            case "Not":
                return t.Children is [{ Kind: "Signal" } s] ? new HashSet<Lit> { new(s.Name!, false) } : new HashSet<Lit>();
            case "And":
            {
                var all = new HashSet<Lit>();
                foreach (var child in t.Children!) { if (Necessary(child) is not { } n) return null; all.UnionWith(n); }
                return all;
            }
            case "Or":
            {
                HashSet<Lit>? common = null;
                foreach (var child in t.Children!)
                {
                    if (Necessary(child) is not { } n) continue;
                    if (common == null) common = new HashSet<Lit>(n); else common.IntersectWith(n);
                }
                return common;
            }
            case TimerKind:
                // 只有接通延时 TON：Q 为真 ⇒ IN 为真；TOF/TP/TONR 不成立
                return t.Name == "TON" ? Necessary(t.Children![0]) : new HashSet<Lit>();
            default: return new HashSet<Lit>();
        }
    }

    static List<HashSet<Lit>>? Dnf(LogicTerm t)
    {
        switch (t.Kind)
        {
            case "Constant": return t.Name == "false" ? [] : [new HashSet<Lit>()];
            case "Signal": return [new HashSet<Lit> { new(t.Name!, true) }];
            case "Not": return t.Children is [{ Kind: "Signal" } s] ? [new HashSet<Lit> { new(s.Name!, false) }] : null;
            case "Or":
            {
                var all = new List<HashSet<Lit>>();
                foreach (var child in t.Children!)
                {
                    if (Dnf(child) is not { } d) return null;
                    all.AddRange(d);
                    if (all.Count > MaxDnfTerms) return null;
                }
                return all;
            }
            case "And":
            {
                var acc = new List<HashSet<Lit>> { new() };
                foreach (var child in t.Children!)
                {
                    if (Dnf(child) is not { } d) return null;
                    var next = new List<HashSet<Lit>>();
                    foreach (var x in acc) foreach (var y in d) { var z = new HashSet<Lit>(x); z.UnionWith(y); next.Add(z); }
                    if (next.Count > MaxDnfTerms) return null;
                    acc = next;
                }
                return acc;
            }
            default: return null;
        }
    }

    static bool DependsOnInput(LogicTerm term, HashSet<string> inputs, Dictionary<string, LogicTerm> defs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<LogicTerm>(); stack.Push(term);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Kind == "Signal")
            {
                if (inputs.Contains(Port(current.Name!))) return true;
                if (seen.Add(current.Name!) && defs.TryGetValue(current.Name!, out var def)) stack.Push(def);
                continue;
            }
            foreach (var child in current.Children ?? []) stack.Push(child);
        }
        return false;
    }
}
