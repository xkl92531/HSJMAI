#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record ValidatedRelation(AiContractRelation Relation, bool Proven, string? RejectReason)
{
    /// <summary>非空：本关系不是 AI 给出的，而是由同块已通过校验的另一类关系派生（取值见
    /// <see cref="FbContractValidator.DerivedFromTimeoutAlarm"/>）。派生关系不进上层 AI 的提示材料。</summary>
    public string? DerivedFrom { get; init; }

    /// <summary>AI 给的输出互斥在被调块程序体里有反例（把未被块内改写的 Bool 输入形参当自由量，只用它们就能让两个输出同时为真）：
    /// 互斥只可能由调用点的实参关系成立，代入后须在调用点由代码证明（MutexVerdict 为 Proven），否则不产。不影响是否接受、不进提示材料。</summary>
    public bool RequiresCallSiteProof { get; init; }
}

public sealed record ValidatedFbContract(string FbName, IReadOnlyList<ValidatedRelation> Relations)
{
    public IReadOnlyList<ValidatedRelation> Accepted => Relations.Where(r => r.RejectReason is null).ToArray();
    public IReadOnlyList<ValidatedRelation> Rejected => Relations.Where(r => r.RejectReason is not null).ToArray();
}

/// <summary>按 IR 逐条核对 AI 指认（spec §7.1）。只会拒绝，不会改写 AI 的结论；
/// 唯一的补全是由已通过校验的超时报警派生到位反馈（见 <see cref="DeriveFeedbackFromAlarms"/>），派生关系照样逐条校验。</summary>
public static class FbContractValidator
{
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    /// <summary><see cref="ValidatedRelation.DerivedFrom"/> 取值：由超时报警派生的到位反馈。</summary>
    public const string DerivedFromTimeoutAlarm = "timeoutAlarm";

    /// <summary>派生反馈的说明文字前缀（写进关系的 Text）。</summary>
    public const string DerivedFeedbackText = "由超时报警派生";

    public static ValidatedFbContract Validate(AiFbContract ai, IrBlock block, FbContract proven)
    {
        var relations = ai.Relations.Select(r => Check(r, block, proven)).ToList();
        relations.AddRange(DeriveFeedbackFromAlarms(relations, block, proven));
        return new(block.Name, relations);
    }

    /// <summary>已通过校验的超时报警本身就是「输出发出后到位变量应在延时内成立」的程序证据（报警依赖输出、到位变量、
    /// 延时都已由代码核对）。同一（输出形参, 到位变量）没有被接受的 feedback 时（AI 漏给，或给了但被拒），
    /// 由报警派生一条 feedback（输出、到位变量、延时、出处取自报警），再走与 AI 给出的 feedback 完全相同的校验。
    /// 已有被接受的 feedback 时不派生（按输出 + 到位变量忽略大小写），同一对只派生一次。只看契约结构，不看任何名字。</summary>
    static IEnumerable<ValidatedRelation> DeriveFeedbackFromAlarms(IReadOnlyList<ValidatedRelation> relations, IrBlock b, FbContract proven)
    {
        static string Key(string output, string positionVar) => output.ToUpperInvariant() + "\u0001" + positionVar.ToUpperInvariant();
        var covered = relations.Where(v => v.RejectReason is null && v.Relation.Kind == "feedback")
            .Select(v => Key(v.Relation.Outputs[0], v.Relation.PositionVar!)).ToHashSet(StringComparer.Ordinal);
        var result = new List<ValidatedRelation>();
        foreach (var alarm in relations.Where(v => v.RejectReason is null && v.Relation.Kind == "timeoutAlarm").Select(v => v.Relation))
        {
            var key = Key(alarm.Outputs[0], alarm.PositionVar!);
            if (covered.Contains(key)) continue;
            // 出处：报警的出处 + 本块写入到位变量的语句（代码定位，不是 AI 指认）。报警的出处只须提到报警位，
            // 「报警依赖到位变量」已由依赖闭包核对；补上写入语句，派生反馈的「出处须提到到位变量」才有据可查。
            // 到位变量是未写入的输入形参时没有写入语句，仍须报警出处本身提到它（否则不派生，只会漏）
            var evidence = alarm.Evidence.Concat(b.Writers(alarm.PositionVar!).Select(w => w.Statement.Id))
                .Distinct(StringComparer.Ordinal).ToArray();
            // 延时只用程序里能核对到的值：形参（代入时按调用实参常数 / 接口初值解析，解析不到写「程序设定」）；
            // AI 写的数字（DelayParam 为数字、DelaySeconds）不与程序核对，不带
            var delay = alarm.DelayParam is { } d && b.Formal(d) is not null ? d : null;
            var feedback = new AiContractRelation("feedback", alarm.Outputs, [], alarm.PositionVar, delay, null,
                null, evidence, $"{DerivedFeedbackText}：{alarm.Alarm}");
            var v = Check(feedback, b, proven);
            if (v.RejectReason is null) covered.Add(key);
            else v = v with { RejectReason = "DerivedFromTimeoutAlarm:" + v.RejectReason };
            result.Add(v with { DerivedFrom = DerivedFromTimeoutAlarm });
        }
        return result;
    }

    /// <summary>规范化 AI 写的出处编号：去首尾空白与方括号；把照抄提示词示例的通用块名「FB/…」换成本块名。
    /// 只做书写形式上的还原，规范化后仍须在本块存在、仍须提到主变量。</summary>
    public static string NormalizeEvidence(string id, string blockName)
    {
        var s = (id ?? "").Trim().TrimStart('[').TrimEnd(']').Trim();
        if (s.StartsWith("FB/", StringComparison.OrdinalIgnoreCase) && !string.Equals(blockName, "FB", StringComparison.Ordinal))
            s = blockName + s[2..];
        return s;
    }

    static ValidatedRelation Check(AiContractRelation r, IrBlock b, FbContract proven)
    {
        var evidence = r.Evidence.Select(id => NormalizeEvidence(id, b.Name)).ToArray();
        if (!evidence.SequenceEqual(r.Evidence, StringComparer.Ordinal)) r = r with { Evidence = evidence };
        if (r.Kind == "other") return new(r, false, "Informational");
        if (r.Evidence.Count == 0) return new(r, false, "EvidenceMissing");
        var unknown = r.Evidence.FirstOrDefault(id => !b.StatementsById.ContainsKey(id));
        if (unknown is not null) return new(r, false, "EvidenceUnknown:" + unknown);
        var result = r.Kind switch
        {
            "mutex" => Mutex(r, b, proven),
            "feedback" => Feedback(r, b),
            "timeoutAlarm" => Alarm(r, b),
            "gate" => Gate(r, b, proven),
            "contradiction" => Contradiction(r, b, proven),
            "expectedStateAlarm" => ExpectedStateCheck.Check(r, b),
            _ => new(r, false, "UnknownKind")
        };
        if (result.RejectReason is not null) return result;
        // 出处须提到关系的主变量（含第一期已证明的关系：AI 的出处同样要核对）
        var mentioned = new HashSet<string>(C);
        foreach (var id in r.Evidence) mentioned.UnionWith(b.StatementVars(id));
        // 提到主变量的任一纯拷贝别名（如 CylinderCmd.IWP := AlwaysTRUE ∧ IWP）也算提到主变量
        var missing = PrimaryVariables(r).FirstOrDefault(v => !b.Aliases(v).Any(mentioned.Contains));
        return missing is null ? result : new(r, false, "EvidenceDoesNotMention:" + missing);
    }

    static IEnumerable<string> PrimaryVariables(AiContractRelation r) => r.Kind switch
    {
        "mutex" => r.Outputs,
        "feedback" => [r.PositionVar!],
        "timeoutAlarm" => [r.Alarm!],
        "gate" => [r.Inputs[0]],
        "contradiction" => r.Inputs,
        "expectedStateAlarm" => [r.Alarm!],
        _ => []
    };

    static ValidatedRelation Mutex(AiContractRelation r, IrBlock b, FbContract proven)
    {
        if (r.Outputs.Count != 2 || C.Equals(r.Outputs[0], r.Outputs[1]) || !r.Outputs.All(o => b.IsBoolFormal(o, "Output")))
            return new(r, false, "MutexShape");
        var isProven = proven.Mutexes.Any(m =>
            (C.Equals(m.OutputA, r.Outputs[0]) && C.Equals(m.OutputB, r.Outputs[1]))
            || (C.Equals(m.OutputA, r.Outputs[1]) && C.Equals(m.OutputB, r.Outputs[0])));
        return new(r, isProven, null) { RequiresCallSiteProof = !isProven && BodyAllowsBoth(b, r.Outputs[0], r.Outputs[1]) };
    }

    /// <summary>被调块程序体里两个输出可以同时为真：两者都有唯一定义（展开本块 Static/Temp 的先行唯一定义），
    /// 只用「未被块内改写的 Bool 输入形参」（当作自由量）就能让两个定义同时成立；其余文字（全局量、未展开的内部变量、
    /// 比较/计时等不透明原子）一律不可用（只会少标）。可满足性判定，不受析取范式项数限制。</summary>
    static bool BodyAllowsBoth(IrBlock b, string outputA, string outputB)
    {
        IrExpr? Expanded(string output)
        {
            if (b.Definition(output) is not { } def) return null;
            var at = b.OrderOf(b.Writers(output)[0].Statement.Id);
            return ExpandInternal(b, def, 0, new HashSet<string>(C) { output }, at);
        }
        if (Expanded(outputA) is not { } a || Expanded(outputB) is not { } x) return false;
        var vars = new Dictionary<string, int>(C);
        PropositionalSat.Formula Build(IrExpr e) => e.Kind switch
        {
            IrKinds.And => PropositionalSat.And(e.Args!.Select(Build)),
            IrKinds.Or => PropositionalSat.Or(e.Args!.Select(Build)),
            IrKinds.Const => BoolConst(e) == true ? PropositionalSat.True : PropositionalSat.False,
            IrKinds.Not when e.Args![0].Kind == IrKinds.Var => Literal(e.Args[0].Name!, false),
            IrKinds.Var => Literal(e.Name!, true),
            _ => PropositionalSat.False   // 不透明原子（含其取反）：不可用
        };
        PropositionalSat.Formula Literal(string name, bool positive)
        {
            var key = name.Trim();
            if (!b.IsUnwrittenBoolInputFormal(key)) return PropositionalSat.False;
            if (!vars.TryGetValue(key, out var id)) vars[key] = id = vars.Count;
            return new PropositionalSat.Lit(id, positive);
        }
        var formula = PropositionalSat.And([Build(BoolSimplifier.ToNnf(a)), Build(BoolSimplifier.ToNnf(x))]);
        return PropositionalSat.Satisfiable(formula, vars.Count) == true;
    }

    static bool? BoolConst(IrExpr e) => e.Name?.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" => true, "FALSE" or "0" => false, _ => null
    };

    static ValidatedRelation Feedback(AiContractRelation r, IrBlock b)
    {
        if (r.Outputs.Count != 1 || !b.IsBoolFormal(r.Outputs[0], "Output") || r.PositionVar is null) return new(r, false, "FeedbackShape");
        // 到位变量就是本块从未写入的 Bool 输入形参：它本身即来自输入，不要求被写入
        if (!b.IsUnwrittenBoolInputFormal(r.PositionVar))
        {
            if (b.Writers(r.PositionVar).Count == 0) return new(r, false, "PositionVarNotWritten");
            if (!b.DependencyClosure(r.PositionVar).Any(v => b.IsBoolFormal(v, "Input", "InOut")))
                return new(r, false, "PositionVarNotFromInputs");
        }
        // 到位变量（或其纯拷贝别名）就是输出侧的某个变量：「输出 → 到位」是同一个量，什么都没证明 → 拒，
        // 不产规则、也不参与证伪（与超时报警的 AlarmPositionIsOutput 同口径）
        if (PositionIsOutputSide(b, r.Outputs[0], r.PositionVar)) return new(r, false, "FeedbackPositionIsOutput");
        if (r.DelayParam is { } d && b.Formal(d) is null && !IsFiniteNumber(d)) return new(r, false, "DelayParamUnknown");
        return new(r, false, null);
    }

    static ValidatedRelation Alarm(AiContractRelation r, IrBlock b)
    {
        if (r.Alarm is null || r.PositionVar is null || r.Outputs.Count != 1 || !b.IsBoolFormal(r.Outputs[0], "Output"))
            return new(r, false, "AlarmShape");
        var root = r.Alarm.Split('.')[0];
        if (b.Formal(root) is not { Section: "Output" or "InOut" }) return new(r, false, "AlarmNotOutput");
        if (b.Writers(r.Alarm).Count == 0) return new(r, false, "AlarmNotWritten");
        if (r.DelayParam is null) return new(r, false, "AlarmDelayMissing");
        var closure = b.DependencyClosure(r.Alarm);
        // 报警依赖到位变量本身或其任一纯拷贝别名（如 O_FwdPos := AlwaysTRUE ∧ CylinderCmd.FwdPos，报警读的是后者）
        if (!b.Aliases(r.PositionVar).Any(closure.Contains)) return new(r, false, "AlarmIndependentOfPosition");
        // 输出侧：输出本身，或输出直接读到、且本块确实写入的内部变量（未被写入的全局量如 AlwaysTRUE 不算）
        if (!OutputSide(b, r.Outputs[0]).Any(closure.Contains)) return new(r, false, "AlarmIndependentOfOutput");
        // 到位变量（或其纯拷贝别名）就是输出侧的某个变量（输出、其纯拷贝别名、输出直接读到且本块写入的变量）：
        // 「依赖到位变量」与「依赖输出」被同一变量满足，什么都没证明 → 拒（报警位与派生反馈都不产）
        if (PositionIsOutputSide(b, r.Outputs[0], r.PositionVar)) return new(r, false, "AlarmPositionIsOutput");
        var d = r.DelayParam;
        var delayOk = b.Formal(d) is not null ? closure.Contains(d) : IsFiniteNumber(d);
        if (!delayOk) return new(r, false, "AlarmIndependentOfDelay");
        return new(r, false, null);
    }

    /// <summary>输出侧：输出本身，及输出直接读到、且本块确实写入的变量（未被写入的全局量如 AlwaysTRUE 不算）。</summary>
    static string[] OutputSide(IrBlock b, string output) =>
        b.DirectSources(output).Where(v => b.Writers(v).Count > 0).Append(output).ToArray();

    /// <summary>到位变量的纯拷贝别名集合与输出侧（各自连同纯拷贝别名）相交。</summary>
    static bool PositionIsOutputSide(IrBlock b, string output, string positionVar)
    {
        var outputAliases = OutputSide(b, output).SelectMany(v => b.Aliases(v)).ToHashSet(C);
        return b.Aliases(positionVar).Any(outputAliases.Contains);
    }

    static ValidatedRelation Gate(AiContractRelation r, IrBlock b, FbContract proven)
    {
        if (r.Inputs.Count != 1 || !b.IsBoolFormal(r.Inputs[0], "Input") || r.Outputs.Count == 0
            || !r.Outputs.All(o => b.IsBoolFormal(o, "Output"))) return new(r, false, "GateShape");
        var input = r.Inputs[0];
        if (proven.Gates.Any(g => C.Equals(g.Input, input) && r.Outputs.All(o => g.Outputs.Contains(o, C)))) return new(r, true, null);
        if (!r.Outputs.All(o => b.DependencyClosure(o).Contains(input))) return new(r, false, "GateInputNotInOutputDependency");
        return r.Outputs.All(o => IsNecessary(b, o, input)) ? new(r, false, null) : new(r, false, "GateNotNecessary");
    }

    const int MaxExpandDepth = 6;

    /// <summary>输出的定义化成 DNF 后，input 须以正文字出现在每一项里。只代入本块 Static/Temp 变量，
    /// 且其唯一写入者须在当前被展开的语句之前执行（否则读到的是上一周期的值）；全局量、形参、
    /// 无唯一定义者保留为文字。无定义、DNF 过大一律视为不必要。</summary>
    static bool IsNecessary(IrBlock b, string output, string input)
    {
        if (b.Definition(output) is not { } def) return false;
        var stack = new HashSet<string>(C) { output };
        var at = b.OrderOf(b.Writers(output)[0].Statement.Id);
        var dnf = BoolSimplifier.ToDnf(ExpandInternal(b, def, 0, stack, at), 64);
        if (dnf is null) return false;
        return dnf.All(term => term.Any(l => l.Positive && !l.Opaque && C.Equals(l.Key, input)));
    }

    static IrExpr ExpandInternal(IrBlock b, IrExpr e, int depth, HashSet<string> stack, int readerOrder)
    {
        if (e.Kind == IrKinds.Var)
        {
            var name = e.Name!;
            if (depth >= MaxExpandDepth || !b.IsLocal(name) || stack.Contains(name)) return e;
            if (b.Definition(name) is not { } def) return e;
            var writerOrder = b.OrderOf(b.Writers(name)[0].Statement.Id);
            if (writerOrder < 0 || writerOrder >= readerOrder) return e;
            stack.Add(name);
            var expanded = ExpandInternal(b, def, depth + 1, stack, writerOrder);
            stack.Remove(name);
            return expanded;
        }
        return e.Args is null ? e : e with { Args = e.Args.Select(a => ExpandInternal(b, a, depth, stack, readerOrder)).ToArray() };
    }

    static ValidatedRelation Contradiction(AiContractRelation r, IrBlock b, FbContract proven)
    {
        if (r.Inputs.Count != 2 || C.Equals(r.Inputs[0], r.Inputs[1]) || !r.Inputs.All(i => b.IsBoolFormal(i, "Input")))
            return new(r, false, "ContradictionShape");
        var match = proven.Contradictions.FirstOrDefault(x =>
                (C.Equals(x.InputA, r.Inputs[0]) && C.Equals(x.InputB, r.Inputs[1]))
                || (C.Equals(x.InputA, r.Inputs[1]) && C.Equals(x.InputB, r.Inputs[0])));
        // 报警位与延时一律以程序体为准，AI 给的数与名字不直接采用：
        // 报警位须等于第一期证明里被 TON 置位的目标或其纯拷贝别名，否则清掉报警（关系保留）；
        // 延时取该 TON 的 PT 接线（字面量或变量，代入时再解析），取不到为空
        if (match is not null)
            return new(r with
            {
                Alarm = r.Alarm is { } alarm && match.SetTarget is { } target && IsTargetOrAlias(b, target, alarm) ? alarm : null,
                DelayParam = match.Preset, DelaySeconds = null
            }, true, null);
        // 只接受第一期代码证明过的矛盾：报警依赖两个输入不足以说明它们不能同时为真。
        // 报警位同样按超时报警的口径核对（本块 Output/InOut 写入、依赖闭包含两个输入），不通过就清掉
        var checkedAlarm = r.Alarm is { } a && AlarmDependsOn(b, a, r.Inputs) ? a : null;
        return new(r with { Alarm = checkedAlarm, DelayParam = null, DelaySeconds = null }, false, "ContradictionUnproven");
    }

    /// <summary>名字比较：去掉 # 与双引号后忽略大小写（第一期与 IR 的写法差异）。</summary>
    static string Norm(string name) => name.Replace("#", "").Replace("\"", "").Trim();

    static bool IsTargetOrAlias(IrBlock b, string target, string alarm) =>
        b.Aliases(target).Append(target).Any(x => C.Equals(Norm(x), Norm(alarm)))
        || b.Aliases(Norm(target)).Any(x => C.Equals(Norm(x), Norm(alarm)));

    /// <summary>与 Alarm（超时报警）同一口径：报警是本块 Output/InOut 形参（或其成员），本块写入了它，依赖闭包含每个变量（或其纯拷贝别名）。</summary>
    static bool AlarmDependsOn(IrBlock b, string alarm, IReadOnlyList<string> vars)
    {
        if (b.Formal(alarm.Split('.')[0]) is not { Section: "Output" or "InOut" } || b.Writers(alarm).Count == 0) return false;
        var closure = b.DependencyClosure(alarm);
        return vars.All(v => b.Aliases(v).Any(closure.Contains));
    }

    static bool IsFiniteNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v);
}
