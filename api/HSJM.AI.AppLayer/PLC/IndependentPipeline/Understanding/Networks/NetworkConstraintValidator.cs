#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>Constraint 为规范化后的版本（点表规范名、规范化出处）；Proof 为 Code:Network 或 AI:Network；被拒时 Proof 为 null。</summary>
public sealed record ValidatedNetworkConstraint(AiNetworkConstraint Constraint, string? Proof, string? RejectReason);

/// <summary>
/// 普通网络 AI 结论的代码核对（spec §7.1）。只会拒绝或升级为代码证明，不补全、不改写 AI 的结论。
/// 新流程瞬时规则直接判故障（用户决定 2026-10-02），所以核对必须严格，任一不过整条丢弃并记原因，顺序：
/// ① Shape 形式（operator 与角色、信号去重）；② SignalNotIo：每个信号是点表里唯一（按去空白的名字）、Bool、地址 %I/%Q 的点
/// （门控 Source 与 GateSourceNotIo 同口径；AI 抄了地址时按唯一地址换成点名）；③ 出处：规范化后非空、都是本程序段的语句、所引语句（含纯拷贝别名）提到每个信号；
/// ④ OutputNotWrittenHere：Member/Target 的 %Q 本段写入；⑤ EdgeSourceOnly：本段只在上升/下降沿里出现、且本段不写；
/// ⑥ ConstantOutput：常量输出；⑦ FeedbackMustBeDelayed：瞬时 IMPLIES 的 Source 为 %Q 而 Target 含 %I；其余瞬时 IMPLIES → InstantImplication
/// （spec §13.2 的约束种类里蕴含只有输出→到位反馈这一种，且是延时类；瞬时蕴含诊断也不判，入库只会是无法核对的噪声）；
/// ⑧ NoSignalWrittenHere：瞬时关系（MUST_BE_FALSE 除外）里没有一个信号由本段写入——本段逻辑定不下只读信号之间的关系；
/// ⑨ 分级与反驳（RefutedByIr）：瞬时 AT_MOST_ONE / EXACTLY_ONE 的每对 %Q 成员做 IR 互斥判定，任一对有反例即反驳；
/// 其余瞬时关系（SAME_STATE、REQUIRES_0_WHEN_FALSE、含非 %Q 成员的互斥对）做可满足性反驳（见 RefutedBySatisfiability）；
/// AT_MOST_ONE 全为 %Q 且各对都证明（不依赖接口初值）→ Code:Network；瞬时两成员 SAME_STATE 被证明 → Code:Network；其余 AI:Network。
/// </summary>
/// <param name="isConstantLike">反驳时当作「取值未知但固定」的变量（见 <see cref="UnknownConstants"/>）：只有对它们的每种取值都找得到反例
/// 才反驳；为 null 时没有这类变量（全部当自由值）。</param>
public sealed class NetworkConstraintValidator(IReadOnlyList<PipelineTag> tags, NetworkProofs proofs, IReadOnlySet<string> constantOutputs,
    Func<string, bool>? isConstantLike = null)
{
    const int MaxSubstitutionDepth = 8, MaxDnfTerms = 256, MaxConstants = 6;
    /// <summary>反驳时一个信号代入定义后表达式的节点数上限（同 SCL IF 合并的上限）：超出按「判不了」处理（不反驳，保持原核对结论）。</summary>
    internal const int MaxExpandedNodes = 2000;

    /// <summary>核对器版本：进普通网络 AI 的缓存键（审查 M1）。缓存只存 AI 回复原文，核对每次重跑，所以核对规则改动不需要升级它
    /// （升级只会让全部缓存失效、重新调用 AI）；只有核对器改动影响发给 AI 的材料时才升级。</summary>
    public const string Version = "network-check-1";

    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    /// <summary>点表里唯一的 Bool %I/%Q 点（按去空白的名字，忽略大小写）。</summary>
    readonly IReadOnlyDictionary<string, PipelineTag> io = tags.Where(t => !string.IsNullOrWhiteSpace(t.Name))
        .GroupBy(t => t.Name.Trim(), C).Where(g => g.Count() == 1).Select(g => g.Single())
        .Where(t => string.Equals(t.DataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase)
            && (IsArea(t, "%I") || IsArea(t, "%Q")))
        .ToDictionary(t => t.Name.Trim(), C);

    /// <summary>同上，按地址（规范为带 % 的写法）索引，地址唯一的才收：AI 把点表里的地址当点名抄时据此换成点名。</summary>
    readonly IReadOnlyDictionary<string, PipelineTag> ioByAddress = tags.Where(t => !string.IsNullOrWhiteSpace(t.Name))
        .GroupBy(t => t.Name.Trim(), C).Where(g => g.Count() == 1).Select(g => g.Single())
        .Where(t => string.Equals(t.DataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase) && (IsArea(t, "%I") || IsArea(t, "%Q")))
        .GroupBy(t => NormalizeAddress(t.Address), C).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), C);

    static string NormalizeAddress(string? address)
    {
        var a = (address ?? "").Trim();
        return a.StartsWith('%') ? a : "%" + a;
    }

    /// <summary>信号名 → 点表唯一 Bool %I/%Q 点：先按点名，再按地址（点名找不到时）。</summary>
    PipelineTag? Resolve(string name) =>
        io.TryGetValue(name.Trim(), out var byName) ? byName : ioByAddress.TryGetValue(NormalizeAddress(name), out var byAddress) ? byAddress : null;

    static bool IsArea(PipelineTag t, string prefix) => (t.Address ?? "").Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 反驳用的「取值未知但固定」变量：点表里唯一的 Bool %M 点，程序里没有确定的写入（只有不确定写入——间接寻址、认不出的目标等——或没有写入）。
    /// 系统存储位（如恒为真/恒为假位）属于此类，所以「Q := 恒真位 ∧ I」与 I 同步不被误反驳；代价（只会少反驳）：时钟存储位也被当作可能恒定，
    /// 依赖时钟闪烁的错误同步不能靠这一步剔除（不按名字区分，同裁定 F9）。
    /// </summary>
    public static Func<string, bool> UnknownConstants(IReadOnlyList<PipelineTag> tags, ProjectWrites writes)
    {
        var unique = ConstantOutputs.UniqueTags(tags);
        return name => unique.TryGetValue(name.Trim(), out var t) && string.Equals(t.DataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase)
            && IsArea(t, "%M") && writes.Writers(name).Count == writes.UncertainWriters(name).Count;
    }

    public IReadOnlyList<ValidatedNetworkConstraint> Validate(AiNetworkReply reply, IrUnit unit, IrBlock block) =>
        reply.Constraints.Select(c => Check(c, unit, block)).ToArray();

    bool IsQ(string name) => io.TryGetValue(name, out var t) && IsArea(t, "%Q");
    bool IsI(string name) => io.TryGetValue(name, out var t) && IsArea(t, "%I");

    static bool ShapeOk(AiNetworkConstraint c)
    {
        int Count(string role) => c.Signals.Count(s => s.Role == role);
        return c.Operator switch
        {
            "AT_MOST_ONE" or "SAME_STATE" or "EXACTLY_ONE" => Count("Member") >= 2 && Count("Member") == c.Signals.Count,
            "IMPLIES" or "REQUIRES_0_WHEN_FALSE" => Count("Source") == 1 && Count("Target") >= 1 && Count("Member") == 0,
            "MUST_BE_FALSE" => Count("Target") >= 1 && Count("Target") == c.Signals.Count,
            _ => false
        };
    }

    ValidatedNetworkConstraint Check(AiNetworkConstraint c, IrUnit unit, IrBlock block)
    {
        ValidatedNetworkConstraint Reject(string reason) => new(c, null, reason);
        if (!AiNetworkReplyParser.Operators.Contains(c.Operator) || !ShapeOk(c)) return Reject("Shape");
        var signals = new List<AiNetworkSignal>();
        foreach (var s in c.Signals)
        {
            if (Resolve(s.Name) is not { } tag) return Reject("SignalNotIo:" + s.Name);
            signals.Add(s with { Name = tag.Name.Trim() });
        }
        if (signals.Select(s => s.Name).Distinct(C).Count() != signals.Count) return Reject("Shape");
        c = c with
        {
            Signals = signals,
            Evidence = c.Evidence.Select(e => FbContractValidator.NormalizeEvidence(e, unit.BlockName))
                .Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToArray()
        };
        if (c.Evidence.Count == 0) return Reject("EvidenceMissing");
        var ids = unit.Statements.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var unknownId = c.Evidence.FirstOrDefault(e => !ids.Contains(e));
        if (unknownId is not null) return Reject("EvidenceUnknown:" + unknownId);
        var mentioned = new HashSet<string>(C);
        foreach (var id in c.Evidence) mentioned.UnionWith(block.StatementVars(id).Select(v => v.Trim()));
        var missing = signals.FirstOrDefault(s => !block.Aliases(s.Name).Any(mentioned.Contains));
        if (missing is not null) return Reject("EvidenceDoesNotMention:" + missing.Name);
        var written = WrittenIn(unit);
        bool Written(string name) => written.Contains(name) || io.TryGetValue(name, out var t) && written.Contains(t.Address.Trim());
        var notWritten = signals.FirstOrDefault(s => IsQ(s.Name) && s.Role is "Member" or "Target" && !Written(s.Name));
        if (notWritten is not null) return Reject("OutputNotWrittenHere:" + notWritten.Name);
        var edge = signals.FirstOrDefault(s => !Written(s.Name) && EdgeOnly(unit, s.Name));
        if (edge is not null) return Reject("EdgeSourceOnly:" + edge.Name);
        var constant = signals.FirstOrDefault(s => constantOutputs.Contains(s.Name));
        if (constant is not null) return Reject("ConstantOutput:" + constant.Name);
        if (c.Operator == "IMPLIES" && !c.Delayed && IsQ(signals.Single(s => s.Role == "Source").Name)
            && signals.Any(s => s.Role == "Target" && IsI(s.Name))) return Reject("FeedbackMustBeDelayed");
        if (c.Operator == "IMPLIES" && !c.Delayed) return Reject("InstantImplication");
        if (!c.Delayed && c.Operator != "MUST_BE_FALSE" && !signals.Any(s => Written(s.Name))) return Reject("NoSignalWrittenHere");
        var (proof, reject) = Grade(c, unit, block, written);
        return reject is not null ? Reject(reject) : new(c, proof, null);
    }

    (string? Proof, string? Reject) Grade(AiNetworkConstraint c, IrUnit unit, IrBlock block, IReadOnlySet<string> written)
    {
        if (c.Delayed) return (UnderstandingProofs.AiNetwork, null);
        var names = c.Signals.Select(s => s.Name).ToArray();
        if (RefutedBySatisfiability(c, block, written)) return (null, "RefutedByIr");
        if (c.Operator is "AT_MOST_ONE" or "EXACTLY_ONE")
        {
            // 恰好一个也蕴含最多一个：任一对 %Q 成员能同时为真即为反例
            var outputs = names.Where(IsQ).ToArray();
            var allProven = outputs.Length == names.Length;
            for (var i = 0; i < outputs.Length; i++)
                for (var j = i + 1; j < outputs.Length; j++)
                {
                    var verdict = proofs.MutexVerdict(unit, block, outputs[i], outputs[j], out var needsDefaults);
                    if (verdict == ProofVerdict.Counterexample) return (null, "RefutedByIr");
                    if (verdict != ProofVerdict.Proven || needsDefaults) allProven = false;
                }
            return (c.Operator == "AT_MOST_ONE" && allProven ? UnderstandingProofs.CodeNetwork : UnderstandingProofs.AiNetwork, null);
        }
        if (c.Operator == "SAME_STATE" && names.Length == 2 && proofs.SameStateProven(unit, block, names[0], names[1]))
            return (UnderstandingProofs.CodeNetwork, null);
        return (UnderstandingProofs.AiNetwork, null);
    }

    /// <summary>
    /// 可满足性反驳（只拒绝、不证明）：把关系里的信号代入它在本块的唯一定义（IrBlock.Definition，递归代入，成环判不了），
    /// 其余变量当作相互独立的自由值，常量样变量当作取值未知但固定；对常量样变量的每种取值都能找到使关系不成立的取值 → 反驳。
    /// 逐对检查：SAME_STATE 首成员与其余各成员（A≠B 可满足）；REQUIRES_0_WHEN_FALSE Source 与各 Target（¬S ∧ T）；
    /// AT_MOST_ONE / EXACTLY_ONE 中至少一个不是 %Q 的成员对（A ∧ B；两个 %Q 交给 IR 互斥判定，FlgNet 互锁在这里会被误反驳）。
    /// 一对里须至少一个信号有定义；本段写入却没有唯一定义的信号（置位/复位、有条件或多处写入）→ 这一对判不了。
    /// </summary>
    bool RefutedBySatisfiability(AiNetworkConstraint c, IrBlock block, IReadOnlySet<string> written)
    {
        var members = c.Signals.Select(s => s.Name).ToArray();
        string Of(string role) => c.Signals.First(s => s.Role == role).Name;
        var targets = c.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray();
        IEnumerable<(string A, string B, Func<IrExpr, IrExpr, IrExpr> Violation)> pairs = c.Operator switch
        {
            "SAME_STATE" => members.Skip(1).Select(m => (members[0], m, (Func<IrExpr, IrExpr, IrExpr>)Differ)),
            "REQUIRES_0_WHEN_FALSE" => targets.Select(t => (Of("Source"), t, (Func<IrExpr, IrExpr, IrExpr>)((s, x) => IrExpr.And([IrExpr.Not(s), x])))),
            "AT_MOST_ONE" or "EXACTLY_ONE" => members.SelectMany((a, i) => members.Skip(i + 1).Select(b => (A: a, B: b)))
                .Where(p => !(IsQ(p.A) && IsQ(p.B)))
                .Select(p => (p.A, p.B, (Func<IrExpr, IrExpr, IrExpr>)((x, y) => IrExpr.And([x, y])))),
            _ => []
        };
        foreach (var (a, b, violation) in pairs)
        {
            if (Expand(a, block, written) is not { } ea || Expand(b, block, written) is not { } eb || !ea.Defined && !eb.Defined) continue;
            if (ViolationAlwaysPossible(violation(ea.Expr, eb.Expr))) return true;
        }
        return false;

        static IrExpr Differ(IrExpr x, IrExpr y) => IrExpr.Or([IrExpr.And([x, IrExpr.Not(y)]), IrExpr.And([IrExpr.Not(x), y])]);
    }

    /// <summary>信号在本块的展开式：有唯一定义则递归代入（Defined=true）；本段写入却没有唯一定义、代入成环、或展开超过
    /// MaxExpandedNodes 个节点 → null（判不了）；否则为变量本身（自由值或常量样）。</summary>
    static (IrExpr Expr, bool Defined)? Expand(string name, IrBlock block, IReadOnlySet<string> written)
    {
        if (block.Definition(name) is not { } def) return written.Contains(name) ? null : (IrExpr.Var(name), false);
        var visiting = new HashSet<string>(C) { name };
        var nodes = 0;
        var failed = false;
        IrExpr Sub(IrExpr e, int depth)
        {
            if (failed) return e;
            if (++nodes > MaxExpandedNodes) { failed = true; return e; }
            if (e.Kind == IrKinds.Var)
            {
                var key = e.Name!.Trim();
                if (depth >= MaxSubstitutionDepth || block.Definition(key) is not { } inner) return e;
                if (!visiting.Add(key)) { failed = true; return e; }
                var result = Sub(inner, depth + 1);
                visiting.Remove(key);
                return result;
            }
            return e.Args is null ? e : e with { Args = e.Args.Select(a => Sub(a, depth)).ToArray() };
        }
        var expanded = Sub(def, 1);
        return failed ? null : (expanded, true);
    }

    /// <summary>对常量样变量的每种取值（至多 2^MaxConstants 种），其余变量自由时 violation 都可满足。化简过大或常量过多 → false（不反驳）。</summary>
    bool ViolationAlwaysPossible(IrExpr violation)
    {
        var constants = violation.Vars().Select(v => v.Trim()).Distinct(C).Where(v => isConstantLike?.Invoke(v) == true).ToArray();
        if (constants.Length > MaxConstants) return false;
        for (var mask = 0; mask < 1 << constants.Length; mask++)
        {
            var values = constants.Select((name, i) => (name, value: (mask >> i & 1) == 1)).ToDictionary(x => x.name, x => x.value, C);
            var assigned = BoolSimplifier.Substitute(violation, v =>
                values.TryGetValue(v.Trim(), out var value) ? (value ? IrExpr.True : IrExpr.Const("FALSE")) : null);
            if (Satisfiable(assigned) is not true) return false;
        }
        return true;
    }

    /// <summary>析取范式里有一项没有同一文字的正负两面 → 可满足；化简过大 → null。</summary>
    static bool? Satisfiable(IrExpr e)
    {
        if (BoolSimplifier.ToDnfNnf(e, MaxDnfTerms) is not { } dnf) return null;
        return dnf.Any(term => term.GroupBy(l => l.Key.Trim(), C).All(g => g.Select(l => l.Positive).Distinct().Count() == 1));
    }

    /// <summary>本段写入的目标（与 ProjectWrites 同一枚举：赋值/置位/复位目标、调用引脚目标、背景实例、指令盒顺带写入）。</summary>
    static HashSet<string> WrittenIn(IrUnit unit)
    {
        var set = new HashSet<string>(C);
        foreach (var s in unit.Statements)
            foreach (var w in ProjectWrites.StatementWrites(s))
                if (!string.IsNullOrWhiteSpace(w.Target)) set.Add(w.Target.Trim());
        return set;
    }

    /// <summary>信号在本段（节点已展开）每次出现都在上升沿/下降沿里。</summary>
    static bool EdgeOnly(IrUnit unit, string name)
    {
        int total = 0, inEdge = 0;
        void Walk(IrExpr e, bool edge)
        {
            if (e.Kind == IrKinds.Var && C.Equals(e.Name?.Trim(), name)) { total++; if (edge) inEdge++; }
            if (e.Args is null) return;
            foreach (var a in e.Args) Walk(a, edge || e.Kind is IrKinds.Rise or IrKinds.Fall);
        }
        foreach (var s in unit.Statements)
            foreach (var e in ProjectWrites.Expressions(s))
                Walk(IrBlock.ExpandNodesIn(unit, e), false);
        return total > 0 && total == inEdge;
    }

    public static PipelineRule ToRule(ValidatedNetworkConstraint v, string model, string cacheKey)
    {
        var c = v.Constraint;
        var proof = v.Proof ?? UnderstandingProofs.AiNetwork;
        string Names(string? role = null) => string.Join("、", c.Signals.Where(s => role is null || s.Role == role).Select(s => s.Name));
        var description = c.Operator switch
        {
            "AT_MOST_ONE" => $"{Names()} 互斥",
            "SAME_STATE" => $"{Names()} 同步",
            "EXACTLY_ONE" => $"{Names()} 恰好一个为真",
            "MUST_BE_FALSE" => $"{Names()} 必须为 false",
            "IMPLIES" => $"{Names("Source")} 为 true 时 {Names("Target")} 为 true",
            "REQUIRES_0_WHEN_FALSE" => $"{Names("Source")} 为 false 时 {Names("Target")} 必须为 false",
            _ => $"{Names()} {c.Operator}"
        };
        var rule = new PipelineRule(c.Operator, c.Signals.Select(s => new PipelineSignal(s.Name, s.Role)).ToArray(), description)
        { Proof = proof, Trace = new PipelineRuleTrace(proof, model, c.Evidence, cacheKey) };
        return c.Delayed ? rule with { Applicability = "AfterSettling", RequiresSettlingEvidence = true } : rule;
    }
}
