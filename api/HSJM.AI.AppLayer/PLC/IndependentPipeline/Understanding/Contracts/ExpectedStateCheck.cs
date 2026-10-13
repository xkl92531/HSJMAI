#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>期望状态报警（已知问题 K4，契约种类 expectedStateAlarm）的程序体核对：AI 只「指认」报警位、被监视输入、
/// 配置输入及「配置组合 → 期望值」，代码按 IR 逐项核对，只会拒绝或丢掉核对不了的组合，不会改写 AI 的结论：
/// ① 被监视输入与配置输入都是本块从未写入的 Bool Input 形参，组合覆盖全部配置输入；
/// ② 报警位只有一条置位写入（其余写入只能是复位），没有认不出的写入、文字段不提到它；置位条件（节点展开后）就是一个 TON 的 Q，
///    或无条件赋值 alarm := TON.Q；TON 的 PT 是形参或常数，AI 给的延时须与它一致（延时一律取程序里的 PT）；
/// ③ 每个组合：TON 的 IN 里配置输入代入常数；本块变量（Static/Temp/输出形参）若全部写入都是「条件只含配置输入的常数赋值」，
///    按组合算出常数（恰一条条件成立，或成立的几条写同一个值；全不成立 → 保持旧值，算不出），否则按先行唯一定义展开（深度 ≤6、防环）；
///    被监视输入及只由它经 TON/TOF 滤波的定时器输出记为「被监视量」。之后 TON 输入须恰好等价于被监视量（期望值 false）
///    或其取反（期望值 true）——用可满足性判定，冗余项（如 (n ∧ ¬报警) ∨ n）不影响；还依赖别的条件（Enable、别的定时器、
///    判不出的状态字等）→ 整条拒绝（ExpectedStateExtraCondition，审查 I-3：否则 MUST_BE_FALSE 会丢前提）；
///    常数假 → 该组合下报警不会置位（丢掉该组合）；本组合下状态字无写入、可满足性判不了 → 丢掉该组合。
///    AI 写的期望值与代码判出的不一致 → 整条拒绝（AI 读错了）。没有一个组合通过 → 拒绝。</summary>
public static class ExpectedStateCheck
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    const string Mark = "〔被监视输入〕";
    const int MaxDepth = 6;

    enum Verdict { ExpectFalse, ExpectTrue, NeverFires, Undetermined, ExtraCondition }

    /// <summary>核对一条 expectedStateAlarm（见类说明）；通过时延时换成程序里的 PT、组合只留核对通过的。</summary>
    public static ValidatedRelation Check(AiContractRelation r, IrBlock b)
    {
        if (r.Alarm is null || r.Inputs.Count != 1 || r.ExpectedStates.Count == 0) return new(r, false, "ExpectedStateShape");
        var input = r.Inputs[0];
        if (!b.IsUnwrittenBoolInputFormal(input)) return new(r, false, "ExpectedStateInputShape");
        var configs = r.ConfigInputs;
        if (configs.Any(c => C.Equals(c, input)) || configs.Distinct(C).Count() != configs.Count || !configs.All(b.IsUnwrittenBoolInputFormal))
            return new(r, false, "ExpectedStateConfigShape");
        if (r.ExpectedStates.Any(c => c.When.Count != configs.Count || !configs.All(c.When.ContainsKey)))
            return new(r, false, "ExpectedStateCasesIncomplete");

        if (Locate(r, b, out var tonIn, out var readerOrder, out var programDelay) is { } reason) return new(r, false, reason);
        if (r.DelayParam is { } d && !SameDelay(d, programDelay!)) return new(r, false, "ExpectedStateDelayMismatch");

        var kept = new List<ExpectedStateCase>();
        var conditionInputs = new List<string>();
        var undetermined = false;
        foreach (var c in r.ExpectedStates)
        {
            var when = new Dictionary<string, bool>(c.When, C);
            var verdict = Evaluate(b, tonIn!, readerOrder, input, when, out var extras);
            // TON 输入另含本块从未写入的 Bool 输入（条件输入，如 NOT DryRunMode）：逐个取值核对（≤4 个），期望值留到调用点取成常数后再判
            if (verdict == Verdict.ExtraCondition && extras.Count is > 0 and <= 4)
            {
                var verdicts = new List<Verdict>();
                for (var mask = 0; mask < (1 << extras.Count); mask++)
                {
                    var full = new Dictionary<string, bool>(when, C);
                    for (var k = 0; k < extras.Count; k++) full[extras[k]] = ((mask >> k) & 1) == 1;
                    verdicts.Add(Evaluate(b, tonIn!, readerOrder, input, full, out _));
                }
                var wanted = c.Expected ? Verdict.ExpectTrue : Verdict.ExpectFalse;
                var opposite = c.Expected ? Verdict.ExpectFalse : Verdict.ExpectTrue;
                if (verdicts.Contains(Verdict.ExtraCondition)) return new(r, false, "ExpectedStateExtraCondition");
                if (verdicts.Contains(opposite)) return new(r, false, "ExpectedStateMismatch");
                if (!verdicts.Contains(wanted)) { undetermined = true; continue; }
                kept.Add(c);
                conditionInputs.AddRange(extras.Where(x => !conditionInputs.Contains(x, C)));
                continue;
            }
            switch (verdict)
            {
                case Verdict.ExpectFalse when !c.Expected:
                case Verdict.ExpectTrue when c.Expected:
                    kept.Add(c); break;
                case Verdict.ExpectFalse or Verdict.ExpectTrue:
                    return new(r, false, "ExpectedStateMismatch");
                case Verdict.Undetermined:
                    undetermined = true; break;
                case Verdict.ExtraCondition:
                    return new(r, false, "ExpectedStateExtraCondition");
            }
        }
        if (kept.Count == 0) return new(r, false, undetermined ? "ExpectedStateUndetermined" : "ExpectedStateNeverFires");
        return new(r with { DelayParam = programDelay, DelaySeconds = null, ExpectedStates = kept, ConditionInputs = conditionInputs }, false, null);
    }

    /// <summary>调用点复核（有条件输入时）：全部输入形参取定后 TON 输入等价于被监视量 → false（期望 OFF），等价于其取反 → true；
    /// 其它（报警不会置位、仍有别的条件、判不了）→ null。</summary>
    internal static bool? ExpectedAt(AiContractRelation r, IrBlock b, IReadOnlyDictionary<string, bool> inputs)
    {
        if (r.Inputs.Count != 1 || Locate(r, b, out var tonIn, out var readerOrder, out _) is not null) return null;
        return Evaluate(b, tonIn!, readerOrder, r.Inputs[0], new Dictionary<string, bool>(inputs, C), out _) switch
        {
            Verdict.ExpectFalse => false,
            Verdict.ExpectTrue => true,
            _ => null
        };
    }

    /// <summary>找报警位唯一的置位写入及其 TON：返回拒绝原因，或 null（tonIn/读取时刻/程序延时有效）。</summary>
    static string? Locate(AiContractRelation r, IrBlock b, out IrExpr? tonIn, out int readerOrder, out string? programDelay)
    {
        tonIn = null; readerOrder = -1; programDelay = null;
        var alarm = r.Alarm!.Trim();
        var writers = b.Writers(alarm);
        if (writers.Count == 0) return "AlarmNotWritten";
        if (b.UncertainWriters(alarm).Count > 0 || b.OverlappingTargets(alarm).Count > 0 || b.TextMentions(alarm))
            return "ExpectedStateAlarmUncertain";
        var sets = writers.Where(w => w.Statement.Kind != IrStatementKinds.Reset).ToArray();
        if (sets.Length != 1) return "ExpectedStateAlarmNotSingleSet";
        var set = sets[0];
        var st = set.Statement;
        var condition = st.Kind == IrStatementKinds.Set ? st.Guard
            : st.Kind == IrStatementKinds.Assign && CallSiteSubstitution.IsTrue(st.Guard) ? st.Value : null;
        if (condition is null || !C.Equals(st.Target?.Trim(), alarm)) return "ExpectedStateAlarmNotTon";
        var ton = b.ExpandNodes(set.Unit, condition);
        if (ton is not { Kind: IrKinds.Timer, Name: { } tonName } || !tonName.StartsWith("TON|", StringComparison.Ordinal)
            || !tonName.EndsWith("|Q", StringComparison.Ordinal) || Port(ton, "IN") is not { } found)
            return "ExpectedStateAlarmNotTon";
        tonIn = found;
        var pt = Port(ton, "PT");
        programDelay = pt switch
        {
            { Kind: IrKinds.Var, Name: { } n } when b.Formal(n.Trim()) is { Section: "Input" or "InOut" } => n.Trim(),
            { Kind: IrKinds.Const, Name: { } k } when !string.IsNullOrWhiteSpace(k) => k.Trim(),
            _ => null
        };
        if (programDelay is null) return "ExpectedStateDelayUnknown";
        readerOrder = b.OrderOf(st.Id);
        return null;
    }

    static IrExpr? Port(IrExpr timer, string port) =>
        timer.Args?.FirstOrDefault(x => x.Kind == IrKinds.Port && x.Name == port)?.Args is [var value] ? value : null;

    static bool SameDelay(string ai, string program)
    {
        if (C.Equals(ai.Trim(), program)) return true;
        return Seconds(ai) is { } x && Seconds(program) is { } y && Math.Abs(x - y) < 1e-9;
    }

    /// <summary>常数延时换算成秒：T#5s / T#500ms / T#1m30s / 纯数字（秒）；其它写法 null。</summary>
    internal static double? Seconds(string text)
    {
        var t = text.Trim();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)) return plain;
        if (!t.StartsWith("T#", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = t[2..].Replace("_", "");
        var total = 0.0;
        var m = System.Text.RegularExpressions.Regex.Matches(rest, @"(\d+(?:\.\d+)?)(ms|d|h|m|s)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Count == 0 || string.Concat(m.Select(x => x.Value)).Length != rest.Length) return null;
        foreach (System.Text.RegularExpressions.Match x in m)
        {
            var v = double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture);
            total += x.Groups[2].Value.ToLowerInvariant() switch { "ms" => v / 1000, "s" => v, "m" => v * 60, "h" => v * 3600, _ => v * 86400 };
        }
        return total;
    }

    /// <param name="when">取定的输入形参（配置输入与条件输入）→ 常数。</param>
    /// <param name="extraInputs">代入后 TON 输入里仍剩下的、本块从未写入的 Bool 输入形参（被监视输入除外）。</param>
    static Verdict Evaluate(IrBlock b, IrExpr tonIn, int readerOrder, string input,
        IReadOnlyDictionary<string, bool> when, out IReadOnlyList<string> extraInputs)
    {
        extraInputs = [];
        var unresolvedConfigDependency = false;
        var configSet = when.Keys.ToHashSet(C);

        // 本块变量在该组合下的常数：全部写入都是常数赋值，条件只含配置输入（代入后折成常数）
        IrExpr? ConstUnderConfig(string name)
        {
            var ws = b.Writers(name);
            if (ws.Count == 0 || b.UncertainWriters(name).Count > 0 || b.OverlappingTargets(name).Count > 0 || b.TextMentions(name)) return null;
            IrExpr? value = null;
            var configDependent = false;
            foreach (var w in ws)
            {
                var s = w.Statement;
                if (s.Kind != IrStatementKinds.Assign || !C.Equals(s.Target?.Trim(), name) || s.Value is null) return null;
                if (b.ExpandNodes(w.Unit, s.Value) is not { Kind: IrKinds.Const } k) return null;
                var guard = b.ExpandNodes(w.Unit, s.Guard);
                if (guard.Vars().Any(v => !configSet.Contains(v.Trim()))) return null;
                configDependent |= guard.Vars().Any();
                var folded = BoolSimplifier.Fold(BoolSimplifier.Substitute(guard, v => when.TryGetValue(v.Trim(), out var x) ? Bool(x) : null));
                if (BoolValue(folded) is not { } fires) return null;
                if (!fires) continue;
                if (value is not null && !string.Equals(value.Name?.Trim(), k.Name?.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
                value = k;
            }
            if (value is null && configDependent) unresolvedConfigDependency = true;   // 本组合下没有写入：保持旧值，算不出
            return value;
        }

        IrExpr Resolve(IrExpr e, int depth, int reader, HashSet<string> stack)
        {
            switch (e.Kind)
            {
                case IrKinds.Var:
                {
                    var name = e.Name!.Trim();
                    if (C.Equals(name, input)) return IrExpr.Var(Mark);
                    if (when.TryGetValue(name, out var cfg)) return Bool(cfg);
                    var root = IrBlock.RootOf(name);
                    var formal = b.Formal(root);
                    if (formal is { Section: "Input" or "InOut" }) return e;
                    if (!b.IsLocal(name) && formal is null) return e;   // 全局量：原样当文字
                    if (ConstUnderConfig(name) is { } k) return k;
                    if (depth >= MaxDepth || stack.Contains(name) || b.Definition(name) is not { } def) return e;
                    var writerOrder = b.OrderOf(b.Writers(name)[0].Statement.Id);
                    if (writerOrder < 0 || writerOrder >= reader) return e;
                    stack.Add(name);
                    var expanded = Resolve(def, depth + 1, writerOrder, stack);
                    stack.Remove(name);
                    return expanded;
                }
                case IrKinds.Timer when e.Name is { } n && (n.StartsWith("TON|", StringComparison.Ordinal) || n.StartsWith("TOF|", StringComparison.Ordinal))
                                        && n.EndsWith("|Q", StringComparison.Ordinal):
                {
                    // 只由被监视输入经延时滤波的定时器输出仍是「被监视量」（接通/断开延时不改变它代表的状态）
                    var resolved = e with { Args = e.Args?.Select(a => Resolve(a, depth, reader, stack)).ToArray() };
                    return Port(resolved, "IN") is { Kind: IrKinds.Var, Name: Mark } ? IrExpr.Var(Mark) : resolved;
                }
                default:
                    return e.Args is null ? e : e with { Args = e.Args.Select(a => Resolve(a, depth, reader, stack)).ToArray() };
            }
        }

        var expr = Resolve(tonIn, 0, readerOrder, new HashSet<string>(C));
        if (unresolvedConfigDependency) return Verdict.Undetermined;
        extraInputs = expr.Vars().Select(v => v.Trim()).Where(v => v != Mark && b.IsUnwrittenBoolInputFormal(v)).Distinct(C).ToArray();
        // 审查 I-3：配置取定后 TON 输入须恰好等价于被监视量（或其取反），不得再依赖别的条件（Enable、别的定时器、判不出的状态字……），
        // 否则 MUST_BE_FALSE 会丢掉前提 → 整条拒绝（只会漏）。用可满足性判定等价：expr ≠ 被监视量 不可满足 ⇔ 两者等价
        if (BoolSimplifier.Fold(expr) is { Kind: IrKinds.Const } folded && BoolValue(folded) == false) return Verdict.NeverFires;
        var mark = IrExpr.Var(Mark);
        var differsFromMark = ConstantBits.Satisfiable(IrExpr.Or([IrExpr.And([expr, IrExpr.Not(mark)]), IrExpr.And([IrExpr.Not(expr), mark])]));
        if (differsFromMark == false) return Verdict.ExpectFalse;
        var differsFromNotMark = ConstantBits.Satisfiable(IrExpr.Or([IrExpr.And([expr, mark]), IrExpr.And([IrExpr.Not(expr), IrExpr.Not(mark)])]));
        if (differsFromNotMark == false) return Verdict.ExpectTrue;
        return differsFromMark is null || differsFromNotMark is null ? Verdict.Undetermined : Verdict.ExtraCondition;
    }

    static IrExpr Bool(bool v) => v ? IrExpr.True : IrExpr.Const("FALSE");

    static bool? BoolValue(IrExpr e) => e.Kind != IrKinds.Const ? null : e.Name?.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" => true, "FALSE" or "0" => false, _ => null
    };
}
