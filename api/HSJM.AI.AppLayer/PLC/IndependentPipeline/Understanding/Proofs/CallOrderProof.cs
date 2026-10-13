#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>跨块执行顺序的同步证明（已知问题 K5，Proof = Code:CallOrder）：块 B 里无条件纯拷贝 <c>Q_t := Q_s</c>，
/// <c>Q_s</c> 只由块 A 写入，A、B 各被同一个调用方无条件调用一次且 A 在 B 之前 → 扫描结束时 <c>Q_t</c> 与 <c>Q_s</c> 同值。
/// 只会漏不会错，下列任一不满足都不产：
/// ① <c>Q_t</c>、<c>Q_s</c> 都是点表里唯一的 Bool %Q，且不同；
/// ② <c>Q_t</c> 全项目唯一写入是 B 里这条无条件（条件恒真）赋值，值（节点展开后）就是 <c>Q_s</c> 本身（不经取反）；
///    B 不是 FC、无跳转/返回，该程序段在写入索引里；
/// ③ <c>Q_s</c> 全项目（写入索引含 FC）唯一写入者在块 A（A ≠ B，A 不是 FC），没有能写到它的不确定写入；
///    两个 %Q 都不被文字段提到，Q 区写入索引各计 1、没有算不出范围的 Q 区写入；
/// ④ A、B 在全项目各恰好被调用一次（按 IR 调用语句计，含 FC 里的调用），两次调用在同一个调用方块里、EN 与条件都恒真，
///    A 的调用排在 B 之前：同一程序段内只认语句顺序可信的段（SCL/STL），否则按程序段号先后；调用方块无跳转/返回、
///    不含顺控结构与动作（顺控块的程序段号不是执行顺序）；A、B 的块名与背景实例名都不被文字段提到（文字里可能另有调用）。
/// 调用方可以是 FC：只用 FC 里的调用顺序，不用 FC 的约束内容（用户 2026-10-02）。
/// 已知限制（与其它代码证明同口径，裁定 F8）：调用方在不同优先级的组织块里被调用、中断恰好打断在 B 的读写之间不建模；
/// 诊断读取（IGW 读过程映像输出）若落在 A 已写、B 未拷贝之间或分几个 PDU 读完，可能读到不同值——跨块时这个窗口可占扫描周期的相当一部分，
/// 比同段 Code:Network 大（审查 M-1）。</summary>
public sealed class CallOrderProof
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    static readonly HashSet<string> SequenceKinds = new(StringComparer.Ordinal)
    {
        IrStatementKinds.Step, IrStatementKinds.Transition, IrStatementKinds.Branch, IrStatementKinds.Connection,
        IrStatementKinds.Condition, IrStatementKinds.Action
    };

    readonly IReadOnlyDictionary<string, PipelineTag> unique;
    readonly ProjectWrites writes;
    readonly Func<string, int> qWriteCount;
    readonly bool qUnresolved;
    readonly Func<string, bool> isFcBlock;
    readonly Dictionary<long, IrUnit> unitById = new();
    readonly Dictionary<string, List<IrUnit>> unitsByBlock = new(C);
    readonly Dictionary<string, List<(IrUnit Unit, int Index)>> callsOf = new(C);

    /// <param name="units">全项目程序段（含 FC，用于调用计数与写入者归属）。</param>
    /// <param name="isFcBlock">按块类型判断 FC（不看块名）。</param>
    public CallOrderProof(IEnumerable<IrUnit> units, IReadOnlyList<PipelineTag> tags, ProjectWrites writes,
        Func<string, int> qWriteCount, bool qUnresolved, Func<string, bool> isFcBlock)
    {
        ArgumentNullException.ThrowIfNull(isFcBlock);
        unique = ConstantOutputs.UniqueTags(tags);
        this.writes = writes; this.qWriteCount = qWriteCount; this.qUnresolved = qUnresolved; this.isFcBlock = isFcBlock;
        foreach (var u in units)
        {
            unitById.TryAdd(u.UnitId, u);
            var block = u.BlockName?.Trim() ?? "";
            if (!unitsByBlock.TryGetValue(block, out var list)) unitsByBlock[block] = list = [];
            list.Add(u);
            for (var i = 0; i < u.Statements.Count; i++)
                if (u.Statements[i].Call is { } call && !string.IsNullOrWhiteSpace(call.Callee))
                {
                    if (!callsOf.TryGetValue(call.Callee.Trim(), out var calls)) callsOf[call.Callee.Trim()] = calls = [];
                    calls.Add((u, i));
                }
        }
    }

    bool IsQ(string n) => unique.TryGetValue(n.Trim(), out var t)
        && string.Equals(t.DataType, "Bool", StringComparison.OrdinalIgnoreCase) && t.Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase);
    string Canonical(string n) => unique.TryGetValue(n.Trim(), out var t) ? t.Name : n.Trim();

    bool HasControlFlow(string block) => unitsByBlock.GetValueOrDefault(block)?.SelectMany(u => u.Statements)
        .Any(s => s.Kind is IrStatementKinds.Jump or IrStatementKinds.JumpNot or IrStatementKinds.Return) ?? true;

    /// <summary>%Q 在全项目只有一处确定写入（返回它），没有不确定写入、不被文字段提到、Q 区索引计 1。</summary>
    (long UnitId, string StatementId)? SoleWriter(string q)
    {
        if (qUnresolved || qWriteCount(Canonical(q)) != 1 || writes.HasUncertainWriter(q) || writes.MentionedInText(q)) return null;
        return writes.Writers(q) is { Count: 1 } ws ? ws[0] : null;
    }

    /// <summary>块的唯一一次调用（EN 与条件恒真）；调用多次、没有调用、有条件 → null。</summary>
    (IrUnit Unit, int Index)? SoleUnconditionalCall(string block)
    {
        if (callsOf.GetValueOrDefault(block) is not [var only]) return null;
        var s = only.Unit.Statements[only.Index];
        return CallSiteSubstitution.IsTrue(s.Call!.En) && CallSiteSubstitution.IsTrue(s.Guard) ? only : null;
    }

    bool Mentioned(string? name) => !string.IsNullOrWhiteSpace(name) && writes.MentionedInText(name.Trim());

    /// <summary>以 unit（块 B 的程序段）里的拷贝为 Q_t 写入点的同步证明。</summary>
    public IReadOnlyList<ProvenRelation> Prove(IrUnit unit)
    {
        var b = unit.BlockName?.Trim() ?? "";
        if (b.Length == 0 || isFcBlock(b) || !writes.Indexes(unit.UnitId) || HasControlFlow(b)) return [];
        var result = new List<ProvenRelation>();
        foreach (var s in unit.Statements)
        {
            if (s.Kind != IrStatementKinds.Assign || !CallSiteSubstitution.IsTrue(s.Guard) || string.IsNullOrWhiteSpace(s.Target)
                || s.Value is null || s.SideWrites is { Count: > 0 }) continue;
            if (IrBlock.ExpandNodesIn(unit, s.Value) is not { Kind: IrKinds.Var, Name: { } read }) continue;
            var qt = s.Target.Trim(); var qs = read.Trim();
            if (!IsQ(qt) || !IsQ(qs) || C.Equals(Canonical(qt), Canonical(qs))) continue;
            if (SoleWriter(qt) is not { } tw || tw.UnitId != unit.UnitId || tw.StatementId != s.Id) continue;
            if (SoleWriter(qs) is not { } sw || !unitById.TryGetValue(sw.UnitId, out var writerUnit)) continue;
            var a = writerUnit.BlockName?.Trim() ?? "";
            if (a.Length == 0 || C.Equals(a, b) || isFcBlock(a)) continue;
            if (SoleUnconditionalCall(a) is not { } callA || SoleUnconditionalCall(b) is not { } callB) continue;
            var caller = callA.Unit.BlockName?.Trim() ?? "";
            if (!C.Equals(caller, callB.Unit.BlockName?.Trim()) || C.Equals(caller, a) || C.Equals(caller, b)) continue;
            if (HasControlFlow(caller) || unitsByBlock[caller].SelectMany(u => u.Statements).Any(x => SequenceKinds.Contains(x.Kind))) continue;
            if (!Before(callA, callB)) continue;
            var sa = callA.Unit.Statements[callA.Index]; var sb = callB.Unit.Statements[callB.Index];
            if (Mentioned(a) || Mentioned(b) || Mentioned(sa.Call!.Instance) || Mentioned(sb.Call!.Instance)) continue;
            var members = new[] { Canonical(qs), Canonical(qt) }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            result.Add(new("SAME_STATE", members, UnderstandingProofs.CodeCallOrder,
                new[] { s.Id, sw.StatementId, sa.Id, sb.Id }.Distinct(StringComparer.Ordinal).ToArray()));
        }
        return result;
    }

    /// <summary>调用 x 在调用 y 之前执行：同一程序段只认语句顺序可信的段；不同程序段按段号（都要有且不同）。</summary>
    static bool Before((IrUnit Unit, int Index) x, (IrUnit Unit, int Index) y)
    {
        if (x.Unit.UnitId == y.Unit.UnitId) return ExclusionProof.StatementOrderTrusted(x.Unit) && x.Index < y.Index;
        return x.Unit.Network is { } nx && y.Unit.Network is { } ny && nx < ny;
    }
}
