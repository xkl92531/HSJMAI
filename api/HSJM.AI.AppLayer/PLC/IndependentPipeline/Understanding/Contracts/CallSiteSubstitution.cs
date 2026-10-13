#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>调用点的报警位。Kind：Timeout（动作超时）/ PositionConflict（到位矛盾）。</summary>
/// <summary>Bypass：超时报警对应反馈规则的旁路信号（BypassSignals）；到位矛盾报警为空。
/// Output：超时报警的输出 %Q；到位矛盾报警为空；合并时该 %Q 未通过全项目唯一写入检查（对应反馈规则被丢）也置空。
/// FeedbackGroups：超时报警对应反馈规则的到位组（任一组全部为 true 即到位，单组 = 全部到位点）；Feedback 是各组的并集。
/// 到位矛盾报警为空。</summary>
public sealed record CallSiteAlarm(string AlarmSymbol, string Kind, string Output, IReadOnlyList<string> Feedback,
    string? Delay, IReadOnlyList<string> Evidence)
{
    public IReadOnlyList<string> Bypass { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<string>> FeedbackGroups { get; init; } = [];
    /// <summary>报警文本的后备（全局 DB 成员没有注释时用，如 FB 形参注释）；可为 null。</summary>
    public string? Text { get; init; }
}

/// <summary>实参是调用方形参时，用解析后的名字（调用方形参名或全局名）重写的关系，供在调用方的调用点继续代入。</summary>
public sealed record DerivedRelation(string Kind, AiContractRelation InCallerTerms)
{
    /// <summary>同 ValidatedRelation.RequiresCallSiteProof：派生的互斥在外层调用点同样须由代码证明。</summary>
    public bool RequiresCallSiteProof { get; init; }
}

/// <summary>Superseded：本调用点上反馈关系被确定为到位不依赖传感器（AlwaysInPosition / AlternativePathViaOutput /
/// NoSensorBranch）的输出 %Q（规范名）；同一程序段里旧流程以它为 Source 的 IMPLIES（及旧库数据里的 IMPLIES_ANY）由合并步骤删除。
/// 只含点表 %M 位的项（MemoryOnlyBranch）算不清是旁路还是常量位，不在其中。</summary>
public sealed record CallSiteResult(IReadOnlyList<PipelineRule> Rules, IReadOnlyList<CallSiteAlarm> Alarms,
    IReadOnlyList<string> Notes, IReadOnlyList<DerivedRelation> Derived)
{
    public IReadOnlyList<string> Superseded { get; init; } = [];

    /// <summary>Rules 中只在调用点代码证明成立时才能产出的规则 Key（契约互斥在被调块程序体里有反例，见 ValidatedRelation.RequiresCallSiteProof）。</summary>
    public IReadOnlyList<string> RequiresCallSiteProof { get; init; } = [];
}

/// <summary>spec §5：把契约中的形参换成调用点实参，化简到位条件并按分支分类。只会少产，不会多产：
/// 任何一步拿不准都整条不产，并记 Note。</summary>
public static class CallSiteSubstitution
{
    const string Internal = "@内部:";
    const int MaxExpandDepth = 6;
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    /// <param name="callerFormals">调用方块的形参根名：实参落在这里的关系改写为派生关系，交外层调用点继续代入。</param>
    /// <param name="callerLocals">调用方块的 Static/Temp 根名：既不是可读的全局名，也不能再往外代入，相关关系整条不产。</param>
    /// <param name="callerStatics">调用方块的 Static 根名：派生关系里的报警位可以落在这里（交外层按实例路径解析）。</param>
    /// <param name="callerGlobalInstance">调用方块在全项目唯一一次被调用时所用的全局背景实例（否则 null）：
    /// 报警输出引脚未接线、本调用是多重背景时，用它拼报警位的全局路径。</param>
    /// <param name="configConstant">期望状态报警（K4）专用：全局变量在全项目恒为的布尔常数（见 ConfigConstants），解析不出为 null；
    /// 为 null 时接到全局变量的配置输入一律解析失败。</param>
    public static CallSiteResult Apply(ValidatedFbContract contract, IrBlock callee, IrCall call,
        IReadOnlySet<string> callerFormals, IReadOnlySet<string> callerLocals, IReadOnlyList<PipelineTag> tags,
        string contractCacheKey, string model, string? callerGlobalInstance = null, IReadOnlySet<string>? callerStatics = null,
        Func<string, bool?>? configConstant = null)
    {
        var rules = new List<PipelineRule>(); var alarms = new List<CallSiteAlarm>();
        var notes = new List<string>(); var derived = new List<DerivedRelation>();
        var superseded = new List<string>();
        var requiresProof = new List<string>();
        if (!IsTrue(call.En)) { notes.Add("EnConditional"); return new(rules, alarms, notes, derived); }

        var inTable = tags.Select(t => t.Name).ToHashSet(C);
        var unique = tags.GroupBy(t => t.Name, C).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), C);
        bool IsBool(string n) => unique.TryGetValue(n, out var t) && string.Equals(t.DataType, "Bool", StringComparison.OrdinalIgnoreCase);
        bool IsQ(string? n) => n is not null && IsBool(n) && unique[n].Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase);
        bool IsI(string n) => IsBool(n) && unique[n].Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase);
        bool IsMemory(string n) => IsBool(n) && unique[n].Address.StartsWith("%M", StringComparison.OrdinalIgnoreCase);
        string Canonical(string n) => unique.TryGetValue(n, out var t) ? t.Name : n;
        bool IsCallerFormal(string? n) => n is not null && callerFormals.Contains(Root(n));
        bool IsCallerLocal(string? n) => n is not null && callerLocals.Contains(Root(n));

        IrPin? Pin(string formal) => call.Pins.FirstOrDefault(p => C.Equals(p.Name, formal));
        static bool Unwired(IrPin? pin) => pin?.Value is null || (pin.Value.Kind == IrKinds.Unknown && pin.Value.Name == "未接线");

        // 代入到位定义期间，收集按接口初值解析的非 Bool 输入（实例值可能不同于接口初值）
        HashSet<string>? configDefaults = null;
        // 代入到位定义期间，收集按接口初值代入的全部未接线输入（含 Bool）；defaultsOpaque=true 时把这些输入换成
        // 不透明原子重算一遍，用来判断证伪是否依赖了初值（背景实例里的实际值可能已被改写）
        HashSet<string>? defaultsUsed = null;
        var defaultsOpaque = false;

        // 契约里的名字：被调块 Input/InOut 形参（含成员/下标后缀）→ 实参；本块 Static/Temp → 无法解析；其它 → 已是全局实参
        IrExpr ResolveInput(string name)
        {
            var root = Root(name); var suffix = name[root.Length..];
            var member = callee.Formal(root);
            if (member is not null && member.Section is not ("Input" or "InOut")) return new IrExpr(IrKinds.Unknown, "输出形参不是输入");
            if (member is null)
                return callee.IsLocal(name) ? new IrExpr(IrKinds.Unknown, "被调块内部变量") : IrExpr.Var(name);
            var isBool = callee.IsBoolFormal(root);
            var pin = Pin(root);
            if (!Unwired(pin))
            {
                var v = pin!.Value!;
                if (suffix.Length > 0)
                    return v.Kind == IrKinds.Var ? IrExpr.Var(v.Name + suffix) : new IrExpr(IrKinds.Unknown, "成员取自非变量实参");
                return v.Kind == IrKinds.Const && isBool ? BoolConst(v.Name) ?? v : v;
            }
            if (member.Section == "InOut") return new IrExpr(IrKinds.Unknown, "InOut 未接线");
            notes.Add("DefaultUsed:" + root);
            if (!isBool) configDefaults?.Add(root);
            defaultsUsed?.Add(root);
            if (suffix.Length > 0) return new IrExpr(IrKinds.Unknown, "成员取自未接线引脚");
            if (defaultsOpaque) return new IrExpr(IrKinds.Unknown, "接口初值:" + root);
            if (member.StartValue is null)
                return isBool ? IrExpr.Const("FALSE") : IrExpr.Const("0");
            return isBool ? BoolConst(member.StartValue) ?? IrExpr.Const(member.StartValue) : IrExpr.Const(member.StartValue);
        }

        // 输出侧：Output/InOut 形参 → 恰一个可解析的输出目标（带取反标记、"?" 或空目标不认）；本块 Static/Temp → null；其它 → 原名
        string? ResolveOutput(string name)
        {
            var root = Root(name);
            var formal = callee.Formal(root);
            if (formal is not null && formal.Section is not ("Output" or "InOut")) return null;
            if (formal is null) return callee.IsLocal(name) ? null : name;
            var pin = Pin(root);
            if (pin is null || pin.Negated || pin.Targets.Count != 1) return null;
            var target = pin.Targets[0];
            if (string.IsNullOrWhiteSpace(target) || target.Trim() == "?") return null;
            return target + name[root.Length..];
        }

        // 报警位（被调块写法 → 本调用点之外的写法）：
        // · 被调块 Output/InOut 形参、引脚接了线 → 按接线解析；
        // · Output 形参的引脚完全未接线（没有引脚或没有目标），或报警位就在被调块的 Static 里 → 值在背景实例里，按实例路径：
        //   全局实例 → 实例.报警路径；多重背景 → 最终产出时为「调用方唯一全局实例.实例.报警路径」，派生时为「实例.报警路径」
        //  （根段是调用方的 Static，交外层调用点继续按实例解析）；
        // · 全局名原样；被调块 Temp、接了「?」/多目标/取反、无实例、实例是调用方形参、实例作用域与调用方接口矛盾 → 不产。
        // derive=true：结果用于派生关系，只能是调用方形参、调用方 Static 或全局名；调用方 Temp 不行。
        string? AlarmPath(string alarm, bool derive)
        {
            var root = Root(alarm);
            if (callee.Formal(root) is { } formal)
            {
                if (ResolveOutput(alarm) is { } wired)
                {
                    if (!derive || IsCallerFormal(wired) || !IsCallerLocal(wired)) return wired;
                    return callerStatics?.Contains(Root(wired)) == true ? wired : null;
                }
                if (formal.Section != "Output") return null;
                var pin = Pin(root);
                if (pin is not null && (pin.Negated || pin.Targets.Count > 0)) return null;
                return InstancePath(alarm, derive);
            }
            if (callee.IsStatic(root)) return InstancePath(alarm, derive);
            return callee.IsLocal(alarm) ? null : alarm;
        }

        string? InstancePath(string alarm, bool derive)
        {
            var instance = call.Instance?.Trim();
            if (string.IsNullOrEmpty(instance) || IsCallerFormal(instance)) return null;
            var local = IsCallerLocal(instance);
            // 导出里的 Instance Scope 与调用方接口要一致才可信
            if (call.InstanceScope == "GlobalVariable" && local || call.InstanceScope == "LocalVariable" && !local) return null;
            if (!local) return instance + "." + alarm;
            if (derive) return callerStatics?.Contains(Root(instance)) == true ? instance + "." + alarm : null;
            return string.IsNullOrWhiteSpace(callerGlobalInstance) ? null : callerGlobalInstance.Trim() + "." + instance + "." + alarm;
        }

        string? ResolveAlarm(string alarm) => AlarmPath(alarm, derive: false);

        // 期望状态报警的配置输入（只用于该种类）：未接线 → 接口初值（没写初值为 false，记 DefaultUsed）；接布尔常数 → 该常数；
        // 接全局变量 → configConstant（全项目恒定）；取反接线、调用方局部/形参、表达式 → 解析不出
        bool? ConfigValue(string formalName)
        {
            if (callee.Formal(formalName) is not { Section: "Input" } formal || !callee.IsBoolFormal(formalName)) return null;
            var pin = Pin(formalName);
            if (Unwired(pin))
            {
                notes.Add("DefaultUsed:" + formalName);
                return formal.StartValue is null ? false : BoolConst(formal.StartValue) is { } d ? d.IsTrue : null;
            }
            if (pin!.Negated) return null;
            return pin.Value switch
            {
                { Kind: IrKinds.Const, Name: var k } => BoolConst(k) is { } c ? c.IsTrue : null,
                { Kind: IrKinds.Var, Name: { } v } when !IsCallerLocal(v) && !IsCallerFormal(v) => configConstant?.Invoke(v.Trim()),
                _ => null
            };
        }

        // 只用于描述：未接线且接口无初值时不编造数值（描述写「程序设定」）
        string? DelayText(string? delayParam)
        {
            if (delayParam is null) return null;
            string text;
            if (callee.Formal(Root(delayParam)) is { } formal)
            {
                if (Unwired(Pin(Root(delayParam))) && formal.StartValue is null) return null;
                var resolved = ResolveInput(delayParam);
                if (resolved.Kind is not (IrKinds.Const or IrKinds.Var)) return null;
                text = resolved.Name!;
            }
            else if (callee.IsLocal(delayParam)) return null;
            else text = delayParam;
            return FiniteOrNotNumber(text) ? text : null;
        }

        PipelineRuleTrace Trace(AiContractRelation r, string? note = null) =>
            new(UnderstandingProofs.AiFbContract, model, r.Evidence, contractCacheKey, note);
        static string FeedbackKey(string output, string? positionVar) => output + "\u0001" + (positionVar ?? "");

        // 先算反馈（超时报警需要它的到位点）。按（输出形参, 到位变量）配对，忽略大小写。
        // 由超时报警派生的反馈（FbContractValidator.DeriveFeedbackFromAlarms）与 AI 给出的走同一流程，只在 Trace.Note 标明来源
        var seenFeedback = new HashSet<string>(C);
        var feedbackPoints = new Dictionary<string, IReadOnlyList<string>>(C);
        var feedbackBypass = new Dictionary<string, IReadOnlyList<string>>(C);
        var feedbackGroups = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(C);
        foreach (var v in contract.Accepted.Where(v => v.Relation.Kind == "feedback"))
        {
            var r = v.Relation; var outFormal = r.Outputs[0];
            var key = FeedbackKey(outFormal, r.PositionVar);
            if (!seenFeedback.Add(key)) { notes.Add("DuplicateFeedback"); continue; }
            var traceNote = v.DerivedFrom == FbContractValidator.DerivedFromTimeoutAlarm ? FbContractValidator.DerivedFeedbackText : null;
            if (traceNote is not null) notes.Add("FeedbackDerivedFromTimeoutAlarm");
            var q = ResolveOutput(outFormal);
            if (IsCallerLocal(q)) { notes.Add("ActualIsCallerLocal"); continue; }
            if (IsCallerFormal(q)) { notes.Add("DerivedFeedbackUnsupported"); continue; }
            if (!IsQ(q)) { notes.Add("OutputNotUniqueQ:" + outFormal); continue; }
            // 到位变量是本块从未写入的 Bool 输入形参：定义就是它本身，下面代入时换成调用方接线表达式
            var def = r.PositionVar is null ? null
                : callee.IsUnwrittenBoolInputFormal(r.PositionVar) ? IrExpr.Var(r.PositionVar)
                : callee.Definition(r.PositionVar);
            if (def is null) { notes.Add("PositionVarNoUniqueDefinition"); continue; }

            // 代表「输出」的变量：输出形参本身 + 它直接读到、且本块确实写入（含经父/子成员写入）的变量（未被写入的全局量如 AlwaysTRUE 不算）
            var keep = callee.DirectSources(outFormal).Where(callee.IsWritten).Append(outFormal).ToHashSet(C);
            var expanded = ExpandInternals(def, callee, outFormal, keep, 0, new HashSet<string>(C));
            configDefaults = new HashSet<string>(C); defaultsUsed = new HashSet<string>(C);
            IrExpr Resolve(IrExpr e) => BoolSimplifier.Substitute(e, n =>
                n.StartsWith(Internal, StringComparison.Ordinal) ? null
                : callee.Formal(Root(n)) is { Section: "Input" or "InOut" } ? ResolveInput(n) : null);
            var substituted = Resolve(expanded);
            var unwiredConfig = configDefaults; configDefaults = null;
            var usedDefaults = defaultsUsed; defaultsUsed = null;
            if (unwiredConfig.Count > 0)
            {
                foreach (var f in unwiredConfig.OrderBy(x => x, StringComparer.Ordinal)) notes.Add("UnwiredConfigInput:" + f);
                continue;
            }
            var dnf = BoolSimplifier.ToDnf(substituted, 64);
            if (dnf is null) { notes.Add("DnfTooLarge"); continue; }

            var outputSelf = keep.SelectMany(k => new[] { k, Internal + k }).Append(q!).ToHashSet(C);
            // 调用方里写成「本次调用实例.输出形参」（或代表输出的本块 Static，含成员/下标后缀）的文字读的就是本次调用的这个输出，
            // 同样是输出自身（如 IWP ← Valve[5].QFwd ∧ …，Valve[5] 是本次调用的多重背景）；别的实例、别的成员仍按原规则
            var instanceName = call.Instance?.Trim();
            var ownPrefixes = string.IsNullOrEmpty(instanceName) || IsCallerFormal(instanceName) ? []
                : keep.Where(k => callee.Formal(Root(k)) is not null || callee.IsStatic(k)).Select(k => instanceName + "." + k).ToArray();
            bool IsOutputSelf(string key) => outputSelf.Contains(key) || ownPrefixes.Any(p => key.Equals(p, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) || key.StartsWith(p + "[", StringComparison.OrdinalIgnoreCase));
            // 一项的分类：返回放弃原因，或 null（成为传感器组 group / 旁路 byp）
            string? Classify(IReadOnlyList<Literal> term, out string[] group, out string[] byp)
            {
                group = []; byp = [];
                if (term.Count == 0) return "AlwaysInPosition";
                // 输出自身（或代表它的内部变量）单独、或只配非传感器文字成项：程序有不经传感器的到位路径。
                // 同项还有正的 %I 时，输出自身只是同项的另一个正文字，不影响「要求哪些输入到位」
                var hasSensor = term.Any(l => l.Positive && !l.Opaque && IsI(l.Key));
                if (term.Any(l => l.Positive && IsOutputSelf(l.Key)) && !hasSensor) return "AlternativePathViaOutput";
                // 不透明文字（比较、计时等）与正 %I 同项时只是同项的又一个条件，只会收紧这一项；
                // 没有正 %I 时，这一项可能不经任何传感器成立 → 放弃
                if (term.Any(l => l.Opaque) && !hasSensor) return "OpaqueBranch";
                group = term.Where(l => l.Positive && !l.Opaque && IsI(l.Key)).Select(l => Canonical(l.Key))
                    .Distinct(C).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (group.Length > 0) return null;   // 同项其它正文字（含调用方名字）不影响「要求哪些输入到位」
                // 无 %I 的项：调用方局部/形参既不是可读的全局名，也不是 IO 点，不能当旁路
                if (term.Any(l => l.Positive && IsCallerLocal(l.Key))) return "ActualIsCallerLocal";
                if (term.Any(l => l.Positive && IsCallerFormal(l.Key))) return "DerivedFeedbackUnsupported";
                byp = term.Where(l => l.Positive && !inTable.Contains(l.Key) && !l.Key.StartsWith(Internal, StringComparison.Ordinal))
                    .Select(l => l.Key).ToArray();
                if (byp.Length > 0) return null;
                // 点表里重名的点（认不出是哪个点）：不是「不依赖传感器」，而是算不清
                if (term.Any(l => l.Positive && inTable.Contains(l.Key) && !unique.ContainsKey(l.Key))) return "AmbiguousTagBranch";
                // 正文字全是点表 %M 位：可能是 HMI/程序写的旁路，也可能是系统常量位，结构上分不清 → 不产也不证伪（R3，只会漏）
                if (term.Any(l => l.Positive) && term.Where(l => l.Positive).All(l => IsMemory(l.Key))) return "MemoryOnlyBranch";
                return term.Any(l => l.Positive && l.Key.StartsWith(Internal, StringComparison.Ordinal)) ? "InternalOnlyBranch" : "NoSensorBranch";
            }
            static bool SensorFree(string? reason) => reason is "AlwaysInPosition" or "AlternativePathViaOutput" or "NoSensorBranch";
            // 算得清的文字：点表里唯一的点、输出自身、调用方全局实参（非点表、非内部、非调用方局部/形参）；不透明一律不算
            bool Certain(Literal l) => !l.Opaque && (unique.ContainsKey(l.Key) || IsOutputSelf(l.Key)
                || !inTable.Contains(l.Key) && !l.Key.StartsWith(Internal, StringComparison.Ordinal) && !IsCallerLocal(l.Key) && !IsCallerFormal(l.Key));

            var groups = new List<string[]>(); var bypass = new SortedSet<string>(StringComparer.Ordinal);
            string? abort = null;
            foreach (var term in dnf)
            {
                abort = Classify(term, out var group, out var byp);
                if (abort is not null) break;
                if (group.Length > 0) groups.Add(group);
                foreach (var x in byp) bypass.Add(x);
            }
            if (abort is not null)
            {
                notes.Add(abort);
                // 确定到位不依赖传感器（输出已是唯一 %Q）且算得清时，旧流程同段以它为 Source 的到位反馈被证伪。
                // 算得清：存在一项不依赖传感器、且每个文字都算得清；用过接口初值时，把初值换成不透明原子重算后仍须如此。
                // 由超时报警派生的反馈只产新规则，不证伪旧规则（删除路径只认 AI 给出的反馈）
                if (SensorFree(abort) && traceNote is null)
                {
                    var proof = dnf;
                    if (usedDefaults.Count > 0)
                    {
                        defaultsOpaque = true;
                        proof = BoolSimplifier.ToDnf(Resolve(expanded), 64);
                        defaultsOpaque = false;
                    }
                    if (proof is not null && proof.Any(t => SensorFree(Classify(t, out _, out _)) && t.All(Certain)))
                        superseded.Add(Canonical(q!));
                }
                continue;
            }
            var deduped = groups.DistinctBy(g => string.Join("&", g).ToUpperInvariant()).ToList();
            // 吸收律：A ∨ (A ∧ B) = A，去掉包含其它组的组
            var distinct = deduped
                .Where(g => !deduped.Any(h => h.Length < g.Length && h.All(x => g.Contains(x, C))))
                .OrderBy(g => g[0], StringComparer.Ordinal).ThenBy(g => string.Join("&", g), StringComparer.Ordinal).ToList();
            if (distinct.Count == 0) { notes.Add("NoSensorGroup"); continue; }

            var source = Canonical(q!);
            var delay = DelayText(r.DelayParam);
            var targets = distinct.SelectMany(g => g).Distinct(C).ToArray();
            // 多组到位（组间「或」）：每组一条 IMPLIES，不再产 IMPLIES_ANY（用户 2026-10-02）。
            // 代价（已接受）：走其中一组到位时，另一组那条会报违反；这类是延时类规则，不判故障
            foreach (var g in distinct)
            {
                var signals = new[] { new PipelineSignal(source, "Source") }.Concat(g.Select(t => new PipelineSignal(t, "Target"))).ToArray();
                var description = $"{source} 发出后 {string.Join("、", g)} 应到位（动作时间 {delay ?? "程序设定"}）";
                rules.Add(new PipelineRule("IMPLIES", signals, description)
                {
                    Applicability = "AfterSettling", RequiresSettlingEvidence = true, BypassSignals = bypass.ToArray(),
                    Proof = UnderstandingProofs.AiFbContract, Trace = Trace(r, traceNote)
                });
            }
            feedbackPoints[key] = targets;
            feedbackBypass[key] = bypass.ToArray();
            feedbackGroups[key] = distinct.Select(g => (IReadOnlyList<string>)g).ToArray();
        }

        foreach (var v in contract.Accepted)
        {
            var r = v.Relation;
            switch (r.Kind)
            {
                case "mutex":
                {
                    if (r.Outputs.Count != 2) break;
                    var qs = r.Outputs.Select(ResolveOutput).ToArray();
                    if (qs.Any(IsCallerLocal)) { notes.Add("ActualIsCallerLocal"); break; }
                    if (qs.Any(x => x is null))
                    {
                        if (!v.Proven) notes.Add("OutputNotUniqueQ:" + r.Outputs[Array.IndexOf(qs, null)]);
                        break;
                    }
                    // 实参落在调用方形参：无论是否已证明都派生（第一期跨不了调用层）
                    if (qs.Any(IsCallerFormal))
                    {
                        derived.Add(new("mutex", r with { Outputs = qs! }) { RequiresCallSiteProof = v.RequiresCallSiteProof });
                        break;
                    }
                    if (v.Proven) break;   // 全是全局名：第一期已产
                    if (!qs.All(IsQ)) { notes.Add("OutputNotUniqueQ:" + r.Outputs[Array.FindIndex(qs, x => !IsQ(x))]); break; }
                    var members = qs.Select(x => Canonical(x!)).ToArray();
                    if (C.Equals(members[0], members[1])) break;
                    var mutex = new PipelineRule("AT_MOST_ONE", members.Select(x => new PipelineSignal(x, "Member")).ToArray(),
                        $"{members[0]} 与 {members[1]} 互斥") { Proof = UnderstandingProofs.AiFbContract, Trace = Trace(r) };
                    rules.Add(mutex);
                    if (v.RequiresCallSiteProof) requiresProof.Add(mutex.Key);
                    break;
                }
                case "gate":
                {
                    if (r.Inputs.Count != 1 || r.Outputs.Count == 0) break;
                    // 输入在块内被改写（含父/子成员）：读到的不一定是实参
                    if (!v.Proven && callee.IsWritten(r.Inputs[0]))
                    { notes.Add("GateInputWrittenInBody"); break; }
                    var input = ResolveInput(r.Inputs[0]);
                    var qs = r.Outputs.Select(ResolveOutput).ToArray();
                    if (input.Kind != IrKinds.Var) break;
                    if (IsCallerLocal(input.Name) || qs.Any(IsCallerLocal)) { notes.Add("ActualIsCallerLocal"); break; }
                    if (qs.Any(x => x is null))
                    {
                        if (!v.Proven) notes.Add("OutputNotUniqueQ:" + r.Outputs[Array.IndexOf(qs, null)]);
                        break;
                    }
                    if (IsCallerFormal(input.Name) || qs.Any(IsCallerFormal))
                    { derived.Add(new("gate", r with { Inputs = [input.Name!], Outputs = qs! })); break; }
                    if (v.Proven) break;
                    if (!qs.All(IsQ)) { notes.Add("OutputNotUniqueQ:" + r.Outputs[Array.FindIndex(qs, x => !IsQ(x))]); break; }
                    var gateSource = Canonical(input.Name!);
                    var gateTargets = qs.Select(x => Canonical(x!)).Distinct(C).ToArray();
                    rules.Add(new PipelineRule("REQUIRES_0_WHEN_FALSE",
                        new[] { new PipelineSignal(gateSource, "Source") }.Concat(gateTargets.Select(x => new PipelineSignal(x, "Target"))).ToArray(),
                        $"{gateSource} 为 false 时 {string.Join("、", gateTargets)} 必须为 false")
                        { Proof = UnderstandingProofs.AiFbContract, Trace = Trace(r) });
                    break;
                }
                case "contradiction":
                {
                    // 与 mutex/gate 同一写法：实参落在调用方形参就派生（无论是否已证明，第一期跨不了调用层）；
                    // 全是全局实参且已证明时第一期已在本调用点产过规则，这里不再产，但报警照样产
                    if (r.Inputs.Count != 2) break;
                    // 未经第一期证明（派生层）：输入在块内被改写（含父/子成员），读到的不一定是实参
                    if (!v.Proven && r.Inputs.Any(callee.IsWritten)) { notes.Add("ContradictionInputWrittenInBody"); break; }
                    // 输入引脚未接线：读到的是接口初值（常量），不是传感器，不产（也不记 DefaultUsed，那是给反馈/延时用的）
                    if (r.Inputs.Any(i => callee.Formal(Root(i)) is not null && Unwired(Pin(Root(i)))))
                    { notes.Add("ContradictionInputUnwired"); break; }
                    var ins = r.Inputs.Select(ResolveInput).ToArray();
                    if (ins.Any(x => x.Kind != IrKinds.Var)) break;
                    var names = ins.Select(x => x.Name!).ToArray();
                    if (names.Any(IsCallerLocal)) { notes.Add("ActualIsCallerLocal"); break; }
                    // 报警位换成调用方的写法（内层形参路径不能原样带到外层）；写不出来就不带报警
                    if (names.Any(IsCallerFormal))
                    {
                        // 延时（校验时已换成程序里的 TON 预设）同样换成调用方的写法，外层再解析
                        derived.Add(new("contradiction", r with
                        {
                            Inputs = names, Alarm = r.Alarm is null ? null : AlarmPath(r.Alarm, derive: true),
                            DelayParam = DelayText(r.DelayParam), DelaySeconds = null
                        }));
                        break;
                    }
                    if (!names.All(IsI)) break;
                    names = names.Select(Canonical).ToArray();
                    if (C.Equals(names[0], names[1])) break;
                    if (!v.Proven)
                        rules.Add(new PipelineRule("AT_MOST_ONE", names.Select(n => new PipelineSignal(n, "Member")).ToArray(),
                            $"{names[0]} 与 {names[1]} 同时为 true 即为异常")
                        {
                            Applicability = "AfterSettling", RequiresSettlingEvidence = true,
                            Proof = UnderstandingProofs.AiFbContract, Trace = Trace(r)
                        });
                    if (r.Alarm is not null && ResolveAlarm(r.Alarm) is { } sym && !IsCallerFormal(sym) && !IsCallerLocal(sym))
                        alarms.Add(new(sym, "PositionConflict", "", names, DelayText(r.DelayParam), r.Evidence));
                    break;
                }
                case "expectedStateAlarm":
                {
                    // K4：被监视输入接点表唯一 Bool %I；配置输入在调用点解析成常数（常数接线 / 全项目恒定的全局变量 / 未接线取接口初值），
                    // 按组合得到期望值。期望 false → MUST_BE_FALSE（延时类）；期望 true 没有对应的 operator（无 MUST_BE_TRUE），不产
                    if (r.Inputs.Count != 1) break;
                    var monitoredPin = Pin(Root(r.Inputs[0]));
                    if (Unwired(monitoredPin) || monitoredPin!.Negated) { notes.Add("ExpectedStateInputUnwired"); break; }
                    if (monitoredPin.Value is not { Kind: IrKinds.Var, Name: { } monitored }) { notes.Add("ExpectedStateInputNotVar"); break; }
                    if (IsCallerLocal(monitored) || IsCallerFormal(monitored)) { notes.Add("ExpectedStateInputFromCaller"); break; }
                    if (!IsI(monitored)) { notes.Add("ExpectedStateInputNotUniqueI"); break; }
                    var target = Canonical(monitored);
                    // 报警位进报警清单（已知问题第三批：HMI 报警）：契约已校验它由 TON 置位；与配置解析、期望值能否成规则无关
                    if (r.Alarm is not null && ResolveAlarm(r.Alarm) is { } expectedSym && !IsCallerFormal(expectedSym) && !IsCallerLocal(expectedSym))
                        alarms.Add(new(expectedSym, "ExpectedState", "", [target], DelayText(r.DelayParam), r.Evidence));
                    var values = new Dictionary<string, bool>(C);
                    string? unresolved = null;
                    foreach (var cfg in r.ConfigInputs)
                    {
                        var value = ConfigValue(cfg);
                        if (value is null) { unresolved = cfg; break; }
                        values[cfg] = value.Value;
                    }
                    if (unresolved is not null) { notes.Add("ExpectedStateConfigUnresolved:" + unresolved); break; }
                    var match = r.ExpectedStates.FirstOrDefault(c => c.When.All(kv => values.TryGetValue(kv.Key, out var x) && x == kv.Value));
                    if (match is null) { notes.Add("ExpectedStateNoMatchingConfig"); break; }
                    // 条件输入（TON 输入另含的本块 Bool 输入，审查 I-3）：取成常数后按全部输入复核期望值，条件不成立/取不成常数都不产
                    if (r.ConditionInputs.Count > 0)
                    {
                        var all = new Dictionary<string, bool>(values, C);
                        string? unresolvedCondition = null;
                        foreach (var cond in r.ConditionInputs)
                        {
                            if (ConfigValue(cond) is not { } cv) { unresolvedCondition = cond; break; }
                            all[cond] = cv;
                        }
                        if (unresolvedCondition is not null) { notes.Add("ExpectedStateConditionUnresolved:" + unresolvedCondition); break; }
                        if (ExpectedStateCheck.ExpectedAt(r, callee, all) != match.Expected) { notes.Add("ExpectedStateConditionUnmet"); break; }
                    }
                    if (match.Expected) { notes.Add("ExpectedTrueUnsupported"); break; }
                    rules.Add(new PipelineRule("MUST_BE_FALSE", [new PipelineSignal(target, "Target")],
                        $"{target} 正常时应为 false（为 true 持续 {DelayText(r.DelayParam) ?? "程序设定"} 即置位报警）")
                    {
                        Applicability = "AfterSettling", RequiresSettlingEvidence = true,
                        Proof = UnderstandingProofs.AiFbContract, Trace = Trace(r)
                    });
                    break;
                }
                case "timeoutAlarm":
                {
                    if (r.Outputs.Count != 1) break;
                    var sym = r.Alarm is null ? null : ResolveAlarm(r.Alarm);
                    if (sym is null || IsCallerFormal(sym)) { notes.Add("AlarmUnwired"); break; }
                    if (IsCallerLocal(sym)) { notes.Add("ActualIsCallerLocal"); break; }
                    var q = ResolveOutput(r.Outputs[0]);
                    if (IsCallerLocal(q)) { notes.Add("ActualIsCallerLocal"); break; }
                    if (!IsQ(q)) { notes.Add("OutputNotUniqueQ:" + r.Outputs[0]); break; }
                    // 到位点为空（对应反馈没产出或被放弃）：说不出「等哪个传感器到位」，不产
                    if (feedbackPoints.GetValueOrDefault(FeedbackKey(r.Outputs[0], r.PositionVar)) is not { Count: > 0 } points)
                    { notes.Add("AlarmWithoutPositionPoints"); break; }
                    alarms.Add(new(sym, "Timeout", Canonical(q!), points, DelayText(r.DelayParam), r.Evidence)
                    {
                        Bypass = feedbackBypass.GetValueOrDefault(FeedbackKey(r.Outputs[0], r.PositionVar)) ?? [],
                        FeedbackGroups = feedbackGroups.GetValueOrDefault(FeedbackKey(r.Outputs[0], r.PositionVar)) ?? []
                    });
                    break;
                }
            }
        }
        return new(rules, alarms, notes.Distinct().ToArray(), derived)
        {
            Superseded = superseded.Distinct(C).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            RequiresCallSiteProof = requiresProof.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    /// <summary>根段：第一个 '.' 或 '[' 之前。</summary>
    static string Root(string name)
    {
        var cut = name.IndexOfAny(['.', '[']);
        return cut < 0 ? name : name[..cut];
    }

    static IrExpr? BoolConst(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "1" or "TRUE" => IrExpr.True,
        "0" or "FALSE" => IrExpr.Const("FALSE"),
        _ => null
    };

    /// <summary>数值文本须有限（NaN / 溢出为无穷的一律不要）；非数值文本（变量名）原样接受。</summary>
    static bool FiniteOrNotNumber(string text) =>
        !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsFinite(d);

    /// <summary>EN 恒真：未接线（翻译器写明每次扫描都执行）或化简后为 TRUE。</summary>
    internal static bool IsTrue(IrExpr en)
    {
        if (en.Kind == IrKinds.Unknown && en.Name == "未接线") return true;
        return BoolSimplifier.ToDnf(en, 4) is { Count: 1 } d && d[0].Count == 0;
    }

    /// <summary>展开本块 Static/Temp 变量的唯一定义（深度 ≤6、防环）。输出形参原样保留；其它非输入形参与
    /// 代表输出的变量、没有唯一定义的本块变量改名为「@内部:名字」；全局量（含块内写入的全局量）原名保留。</summary>
    static IrExpr ExpandInternals(IrExpr e, IrBlock callee, string outFormal, IReadOnlySet<string> keep, int depth, HashSet<string> seen)
    {
        if (e.Kind == IrKinds.Var)
        {
            var name = e.Name!;
            if (C.Equals(name, outFormal)) return e;
            if (callee.Formal(Root(name)) is { } formal)
                // 输入形参在块内被改写过时，读到的不一定是实参：按内部变量处理
                return formal.Section is "Input" or "InOut" && !keep.Contains(name)
                    && !callee.IsWritten(name)
                    ? e : IrExpr.Var(Internal + name);
            if (!callee.IsLocal(name)) return e;
            if (keep.Contains(name)) return IrExpr.Var(Internal + name);
            if (depth < MaxExpandDepth && seen.Add(name))
            {
                var def = callee.Definition(name);
                if (def is not null)
                {
                    var r = ExpandInternals(def, callee, outFormal, keep, depth + 1, seen);
                    seen.Remove(name);
                    return r;
                }
                seen.Remove(name);
            }
            return IrExpr.Var(Internal + name);
        }
        return e.Args is null ? e : e with { Args = e.Args.Select(a => ExpandInternals(a, callee, outFormal, keep, depth, seen)).ToArray() };
    }
}
