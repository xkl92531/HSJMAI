#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>锁存报警位：Symbol 为全局 DB 里的写法；SetUnitId 为工位归属用的程序段（全局 DB 里直接锁存的位是置位所在段，
/// 经 FB 输出引脚得到的位是调用所在段）；Evidence 为置位/复位条件与确认条件。
/// Instance / Callee：经 FB 输出引脚得到时为调用的实例与被调 FB，否则为空串；Text：全局 DB 成员没有注释时用的文字（FB 形参注释），可为 null。</summary>
public sealed record LatchedAlarmBit(string Symbol, long SetUnitId, IReadOnlyList<string> Evidence)
{
    public string Instance { get; init; } = "";
    public string Callee { get; init; } = "";
    public string? Text { get; init; }
}

/// <summary>锁存报警位判定的输入。BlockInterface：块接口（判局部变量、形参种类）；MentionedInText：全项目文字段是否提到该名字；
/// FunctionBlocks：FB 块名（经 FB 输出引脚的判定只看这些块）；BlockInterfaceXml：块接口 XML（取 FB 形参注释），可为 null。</summary>
public sealed record LatchedAlarmProject(IReadOnlyList<IrUnit> Units, IReadOnlyDictionary<string, string> GlobalDbInterfaces,
    Func<string, IReadOnlyList<IfaceMember>> BlockInterface, Func<string, bool> MentionedInText)
{
    public IReadOnlySet<string> FunctionBlocks { get; init; } = new HashSet<string>();
    public Func<string, string?>? BlockInterfaceXml { get; init; }
}

/// <summary>
/// 已知问题第三批（用户 2026-10-02：一键排查要查出 HMI 上显示的报警，范围「所有报警 DB 的报警位」；同日用户选「看 FC」，
/// 协调方裁定「成组复位」、状态编码排除、全局确认信号跨 DB 成组、经 FB 输出引脚的锁存报警）：按程序结构认锁存报警位，不看任何名字。
///
/// 一、全局 DB 里直接锁存的位，全部满足才算：
/// ①位于全局 DB（导出里的 SW.Blocks.GlobalDB，不是背景 DB）：根段是全局 DB 名，且写入它的块接口里没有同名成员（局部变量遮蔽）；
/// ②是一个位：Bool 成员（含结构/UDT 成员）、Bool 数组的整数字面量下标元素、Byte/Word/DWord/LWord 成员的位切片 %Xn（n 在位宽内）；
/// ③既有置位（SCoil / SR、RS 的 S 端 / SCL 条件赋 TRUE）也有复位写它——FC 里的也算（只用于认报警位，约束生成仍不管 FC）；
/// ④全项目没有别的写法写它：赋值、调用输出引脚、指令盒顺带写入、它的子成员、可能同址的非字面量下标写法都算别的写法；
/// 父结构/字被整体写入只放过「赋常数 0 / FALSE」（清除报警），其它父写入同样排除；文字段（源码、顺控动作）提到它也排除；
/// ⑤置位条件不是恒真；置位只在一个程序段里（工位按该程序段归属）；
/// ⑥不是状态编码：同 DB 的候选之间，它的某条复位路径（或整条复位条件）与另一位的置位条件相同（结果二选一，如质量 OK/NG），
/// 或它在另一位的置位条件里被取反读（只记一次的流程标志）→ 排除（LatchedStateEncoding）；
/// ⑦成组复位：确认条件与至少另外 2 个满足 ①~⑥ 的位相同（≥3 位共用）。确认条件全是全局变量时全项目成组（跨 DB），
/// 含块局部量/形参时只在同一 DB 内成组。
/// 确认条件 = 复位驱动表达式（展开节点；「自己」按 true 代入化简——为 false 时复位无效果）按顶层「或」拆成复位路径，
/// 每条路径按顶层「与」拆项后去掉：只是在重述置位条件的项（等于置位条件的取反，或去掉取反/边沿后与置位条件的必要项相同——报警条件消失、
/// 与置位同时成立的前提，见 RestatesSet）、仍读到自己的项、没有变量的项（常数、指令 ENO）；
/// 剩下的项（块接口成员名前加「块名#」）排序后忽略大小写比较。每条路径各是一个确认条件。
///
/// 二、经 FB 输出引脚得到的位（6.2：报警在 FB 背景数据里锁存、经输出引脚接全局报警字）：
/// ①在 FB 体内，某输出/InOut 形参（或其成员、位切片）被置位与复位，FB 体内没有别的写法（同一 ④ 口径，只看本 FB 的程序段），
/// 本 FB 的文字段不提到该形参，置位不恒真，不是状态编码（同 ⑥，在本 FB 的候选之间）；
/// ②是锁存报警：每条复位路径（去掉只重述置位条件的项后）要么为空（报警条件消失即复位），要么是确认信号——每一项都只是一个变量
/// 或其上升沿（有比较、取反、计时等流程判断的路径 → 完成/结果/握手等流程状态，计 FbLatchedProcessReset）；
/// 且每个确认信号都成组：全是本 FB 输入/InOut 形参或全局量时本 FB 内至少 2 位共用，含 FB 内部量时至少 3 位共用（计 FbLatchedNotAlarm）。
/// 这两条是在协调方裁定（「确认信号来自输入形参/全局量或成组复位」）之上的收紧：泰星三项目只按裁定判定时，MES 握手完成、测量完成/OK/NG、
/// 拍照完成、电机正反转记忆等流程状态输出都被认作报警；
/// ③调用点：调用该 FB（FC 里的调用也算）的输出引脚接线恰好一个目标、不取反、目标根段是全局 DB 且不是调用方局部量；
/// 全局位 = 目标 + 形参里的成员路径，须是一个位（同 ②），全项目对它的写入只有这一次调用、文字段不提到它。
/// FB 契约已识别的超时 / 位置矛盾 / 期望状态报警输出在调用点代入时已进清单（同一报警位清单取契约报警）。
/// 输出引脚接整个字而 FB 内按位组装：只有 FB 内按位切片置位/复位且满足上面条件的位入清单，确认不了的位不入。
///
/// 每条排除计数：LatchedNotGlobalDb / LatchedShadowedByLocal / LatchedNotBit / LatchedOtherWriter / LatchedTextMention /
/// LatchedAlwaysSet / LatchedSetInSeveralUnits / LatchedNoAcknowledge / LatchedStateEncoding / LatchedNotInResetGroup，产出计 LatchedAlarm；
/// FB 输出：FbLatched* 同名原因与 FbLatchedProcessReset / FbLatchedNotAlarm（FB 层）、FbLatchedCall*（调用点：NotGlobalDb / NotBit / NotUniqueWriter / TextMention），产出计 FbLatchedAlarm。
/// </summary>
public static class LatchedAlarmBits
{
    public const string Kind = "Latched";
    /// <summary>成组复位的最小位数（含自己）。</summary>
    public const int MinResetGroup = 3;
    const int MaxConditionText = 240;
    const char Sep = '\u0001';
    static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
    static readonly Regex BitSlice = new(@"^%X(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<LatchedAlarmBit> Find(LatchedAlarmProject project, Action<string> count)
        => Find(project, (reason, _) => count(reason));

    /// <summary>verdict 对每个「既有置位又有复位」的候选调用一次：（排除原因或 LatchedAlarm / FbLatchedAlarm, 名字），供离线核对列明细。</summary>
    public static IReadOnlyList<LatchedAlarmBit> Find(LatchedAlarmProject project, Action<string, string> verdict)
    {
        var writesByRoot = WritesByRoot(project.Units);
        var result = GlobalDbBits(project, writesByRoot, verdict).ToList();
        var seen = result.Select(r => Key(r.Symbol)).ToHashSet(StringComparer.Ordinal);
        foreach (var bit in FbOutputBits(project, writesByRoot, verdict))
            if (seen.Add(Key(bit.Symbol))) result.Add(bit);
        return result;
    }

    sealed record Latch(string Symbol, string Key, string Root, List<(IrUnit Unit, IrStatement Statement)> Sets,
        List<(IrUnit Unit, IrStatement Statement)> Resets);

    /// <summary>确认条件：Text 为规范化文字；Local 为含块接口成员（局部量 / 形参）；InputsOrGlobals 为全是输入/InOut 形参或全局量；
    /// Signal 为每一项都只是一个信号（变量或变量的上升沿）——确认/复位信号，不是比较、取反、计时等流程判断。</summary>
    sealed record Ack(string Text, bool Local, bool InputsOrGlobals, bool Signal);

    sealed record Candidate(Latch Latch, IReadOnlyList<Ack> Acks);

    static Dictionary<string, List<(string Target, IrStatement Statement, ProjectWrites.Spread Spread)>> WritesByRoot(IEnumerable<IrUnit> units)
    {
        var writesByRoot = new Dictionary<string, List<(string Target, IrStatement Statement, ProjectWrites.Spread Spread)>>(C);
        foreach (var unit in units)
            foreach (var s in unit.Statements)
                foreach (var w in ProjectWrites.StatementWrites(s))
                {
                    if (string.IsNullOrWhiteSpace(w.Target)) continue;
                    var target = w.Target.Trim();
                    var root = RootName(target);
                    if (!writesByRoot.TryGetValue(root, out var list)) writesByRoot[root] = list = [];
                    list.Add((target, s, w.Spread));
                }
        return writesByRoot;
    }

    static List<Latch> Latches(IEnumerable<IrUnit> units, Func<string, bool> rootFilter)
    {
        var latches = new Dictionary<string, Latch>(StringComparer.Ordinal);
        foreach (var unit in units)
            foreach (var s in unit.Statements)
            {
                if (s.Kind is not (IrStatementKinds.Set or IrStatementKinds.Reset) || string.IsNullOrWhiteSpace(s.Target)) continue;
                var root = RootName(s.Target);
                if (!rootFilter(root)) continue;
                var key = Key(s.Target);
                if (!latches.TryGetValue(key, out var entry)) latches[key] = entry = new(s.Target.Trim(), key, root, [], []);
                (s.Kind == IrStatementKinds.Set ? entry.Sets : entry.Resets).Add((unit, s));
            }
        return latches.Values.Where(l => l.Sets.Count > 0 && l.Resets.Count > 0).OrderBy(l => l.Symbol, StringComparer.Ordinal).ToList();
    }

    // ---------- 一、全局 DB 里直接锁存的位 ----------

    static IEnumerable<LatchedAlarmBit> GlobalDbBits(LatchedAlarmProject p,
        Dictionary<string, List<(string Target, IrStatement Statement, ProjectWrites.Spread Spread)>> writesByRoot, Action<string, string> verdict)
    {
        var candidates = new List<Candidate>();
        foreach (var l in Latches(p.Units, _ => true))
        {
            var segs = IrBlock.Segments(l.Symbol).Select(x => x.Trim()).ToArray();
            if (segs.Length < 2 || !p.GlobalDbInterfaces.TryGetValue(l.Root, out var xml)) { verdict("LatchedNotGlobalDb", l.Symbol); continue; }
            if (l.Sets.Concat(l.Resets).Select(x => x.Unit.BlockName).Distinct(C).Any(b => p.BlockInterface(b).Any(m => C.Equals(m.Name, l.Root))))
            { verdict("LatchedShadowedByLocal", l.Symbol); continue; }
            if (!IsBit(xml, segs[1..])) { verdict("LatchedNotBit", l.Symbol); continue; }
            if (writesByRoot.TryGetValue(l.Root, out var writes) && writes.Any(w => !Allowed(w, l.Symbol, l.Key))) { verdict("LatchedOtherWriter", l.Symbol); continue; }
            if (p.MentionedInText(l.Symbol)) { verdict("LatchedTextMention", l.Symbol); continue; }
            if (l.Sets.Any(x => IrBlock.ExpandNodesIn(x.Unit, x.Statement.Guard).IsTrue)) { verdict("LatchedAlwaysSet", l.Symbol); continue; }
            if (l.Sets.Select(x => x.Unit.UnitId).Distinct().Count() != 1) { verdict("LatchedSetInSeveralUnits", l.Symbol); continue; }
            var acks = AcksOf(l, p.BlockInterface);
            if (acks.Count == 0) { verdict("LatchedNoAcknowledge", l.Symbol); continue; }
            candidates.Add(new(l, acks));
        }
        candidates = ExcludeStateEncoding(candidates, c => c.Latch.Root, verdict, "LatchedStateEncoding");

        string GroupKey(Candidate c, Ack a) => (a.Local ? c.Latch.Root : "*") + Sep + a.Text;
        var groupSize = candidates.SelectMany(c => c.Acks.Select(a => GroupKey(c, a))).GroupBy(k => k, C).ToDictionary(g => g.Key, g => g.Count(), C);
        foreach (var c in candidates)
        {
            var shared = c.Acks.Where(a => groupSize[GroupKey(c, a)] >= MinResetGroup).ToArray();
            if (shared.Length == 0) { verdict("LatchedNotInResetGroup", c.Latch.Symbol + "（确认条件：" + string.Join(" ‖ ", c.Acks.Select(a => Clip(a.Text))) + "）"); continue; }
            verdict("LatchedAlarm", c.Latch.Symbol);
            yield return new LatchedAlarmBit(c.Latch.Symbol, c.Latch.Sets[0].Unit.UnitId,
            [
                .. c.Latch.Sets.Select(x => Evidence("置位", x.Unit, x.Statement)), .. c.Latch.Resets.Select(x => Evidence("复位", x.Unit, x.Statement)),
                .. shared.Select(a => $"确认条件（{(a.Local ? "同 DB" : "全项目")} {groupSize[GroupKey(c, a)]} 位共用）：{Clip(a.Text)}")
            ]);
        }
    }

    // ---------- 二、经 FB 输出引脚得到的位 ----------

    static IEnumerable<LatchedAlarmBit> FbOutputBits(LatchedAlarmProject p,
        Dictionary<string, List<(string Target, IrStatement Statement, ProjectWrites.Spread Spread)>> writesByRoot, Action<string, string> verdict)
    {
        var unitsByBlock = p.Units.GroupBy(u => u.BlockName, C).ToDictionary(g => g.Key, g => g.ToArray(), C);
        // FB → 被认作锁存报警的输出形参成员（形参名 → [(成员写法, 依据, 形参注释)]）
        var latchedOutputs = new Dictionary<string, List<(Latch Latch, IReadOnlyList<string> Evidence, string? Comment)>>(C);
        foreach (var fb in p.FunctionBlocks.Where(unitsByBlock.ContainsKey).Order(StringComparer.Ordinal))
        {
            var iface = p.BlockInterface(fb);
            var outputs = iface.Where(m => m.Section is "Output" or "InOut").Select(m => m.Name).ToHashSet(C);
            if (outputs.Count == 0) continue;
            var units = unitsByBlock[fb];
            var bodyWrites = WritesByRoot(units);
            var texts = units.SelectMany(u => u.Statements).Where(IsText).Select(s => SclTranslator.WrittenText(s.Text!)).ToArray();
            var candidates = new List<Candidate>();
            foreach (var l in Latches(units, outputs.Contains))
            {
                var name = fb + ":" + l.Symbol;
                if (bodyWrites.TryGetValue(l.Root, out var writes) && writes.Any(w => !Allowed(w, l.Symbol, l.Key))) { verdict("FbLatchedOtherWriter", name); continue; }
                if (texts.Any(t => Mentions(t, l.Root))) { verdict("FbLatchedTextMention", name); continue; }
                if (l.Sets.Any(x => IrBlock.ExpandNodesIn(x.Unit, x.Statement.Guard).IsTrue)) { verdict("FbLatchedAlwaysSet", name); continue; }
                var acks = AcksOf(l, p.BlockInterface);
                if (acks.Count == 0) { verdict("FbLatchedNoAcknowledge", name); continue; }
                candidates.Add(new(l, acks));
            }
            candidates = ExcludeStateEncoding(candidates, _ => fb, (r, n) => verdict(r, fb + ":" + n), "FbLatchedStateEncoding");
            // 复位路径里有流程判断（比较、取反、计时、组合条件，如「步号 == 5」「¬启动请求」）：完成/结果/握手等流程状态，不是报警
            candidates = candidates.Where(c =>
            {
                if (c.Acks.All(a => a.Signal)) return true;
                verdict("FbLatchedProcessReset", fb + ":" + c.Latch.Symbol);
                return false;
            }).ToList();
            var groupSize = candidates.SelectMany(c => c.Acks.Select(a => a.Text)).GroupBy(k => k, C).ToDictionary(g => g.Key, g => g.Count(), C);
            foreach (var c in candidates)
            {
                // 每条确认路径都要是成组确认：来自输入形参/全局量的确认信号至少 2 位共用，FB 内部量至少 3 位共用（只确认自己一位的，
                // 如电机正转记忆被「正转到位」复位，是流程记忆）
                bool Shared(Ack a) => groupSize[a.Text] >= (a.InputsOrGlobals ? 2 : MinResetGroup);
                if (!c.Acks.All(Shared)) { verdict("FbLatchedNotAlarm", fb + ":" + c.Latch.Symbol); continue; }
                var external = c.Acks.Where(a => a.InputsOrGlobals).ToArray();
                var grouped = c.Acks.Where(a => !a.InputsOrGlobals).ToArray();
                if (!latchedOutputs.TryGetValue(fb, out var list)) latchedOutputs[fb] = list = [];
                list.Add((c.Latch,
                [
                    .. c.Latch.Sets.Select(x => Evidence("FB 内置位", x.Unit, x.Statement)), .. c.Latch.Resets.Select(x => Evidence("FB 内复位", x.Unit, x.Statement)),
                    .. external.Select(a => $"确认条件（输入形参/全局量，FB 内 {groupSize[a.Text]} 位共用）：{Clip(a.Text)}"),
                    .. grouped.Select(a => $"确认条件（FB 内部量，{groupSize[a.Text]} 位共用）：{Clip(a.Text)}")
                ], MemberComment(p.BlockInterfaceXml?.Invoke(fb), IrBlock.Segments(c.Latch.Symbol).Select(x => x.Trim()).ToArray())));
            }
        }
        if (latchedOutputs.Count == 0) yield break;

        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in p.Units)
            foreach (var s in unit.Statements)
            {
                if (s.Call is not { } call || !latchedOutputs.TryGetValue(call.Callee?.Trim() ?? "", out var latched)) continue;
                var callerMembers = p.BlockInterface(unit.BlockName);
                foreach (var pin in call.Pins.Where(x => x.Section is "Output" or "InOut"))
                {
                    var outs = latched.Where(x => C.Equals(x.Latch.Root, pin.Name)).ToArray();
                    if (outs.Length == 0) continue;
                    if (pin.Negated || pin.Targets.Count != 1 || string.IsNullOrWhiteSpace(pin.Targets[0]) || pin.Targets[0].Trim() == "?") continue;
                    var target = pin.Targets[0].Trim();
                    var root = RootName(target);
                    foreach (var (latch, evidence, comment) in outs)
                    {
                        var memberPath = IrBlock.Segments(latch.Symbol).Skip(1).ToArray();
                        var symbol = target + string.Concat(memberPath.Select(seg => seg.StartsWith('[') ? seg : "." + seg));
                        if (!p.GlobalDbInterfaces.TryGetValue(root, out var xml) || callerMembers.Any(m => C.Equals(m.Name, root)))
                        { verdict("FbLatchedCallNotGlobalDb", symbol); continue; }
                        var segs = IrBlock.Segments(symbol).Select(x => x.Trim()).ToArray();
                        if (segs.Length < 2 || !IsBit(xml, segs[1..])) { verdict("FbLatchedCallNotBit", symbol); continue; }
                        var writers = writesByRoot.TryGetValue(root, out var ws)
                            ? ws.Where(w => ProjectWrites.Overlaps(w.Target, symbol)).Select(w => w.Statement).Distinct().ToArray() : [];
                        if (writers.Length != 1 || !ReferenceEquals(writers[0], s)) { verdict("FbLatchedCallNotUniqueWriter", symbol); continue; }
                        if (p.MentionedInText(symbol)) { verdict("FbLatchedCallTextMention", symbol); continue; }
                        if (!produced.Add(Key(symbol))) continue;
                        verdict("FbLatchedAlarm", symbol);
                        yield return new LatchedAlarmBit(symbol, unit.UnitId,
                        [
                            $"调用：{unit.BlockName} 网络 {unit.Network?.ToString() ?? "—"} 实例 {call.Instance} 的输出 {pin.Name} → {target}",
                            .. evidence
                        ]) { Instance = call.Instance ?? "", Callee = call.Callee!.Trim(), Text = comment };
                    }
                }
            }
    }

    // ---------- 共用 ----------

    /// <summary>状态编码排除（见类说明 ⑥）：同组（scope 相同）候选之间，某条复位路径或整条复位条件与另一位的置位条件相同，
    /// 或在另一位的置位条件里被取反读。</summary>
    static List<Candidate> ExcludeStateEncoding(List<Candidate> candidates, Func<Candidate, string> scope, Action<string, string> verdict, string reason)
    {
        var setTexts = candidates.ToDictionary(c => c, c => c.Latch.Sets
            .Select(x => BoolSimplifier.Fold(IrBlock.ExpandNodesIn(x.Unit, x.Statement.Guard))).ToArray());
        var kept = new List<Candidate>();
        foreach (var a in candidates)
        {
            var others = candidates.Where(b => !ReferenceEquals(a, b) && C.Equals(scope(a), scope(b))).ToArray();
            var resetTexts = a.Latch.Resets.SelectMany(x =>
            {
                var g = BoolSimplifier.Fold(IrBlock.ExpandNodesIn(x.Unit, x.Statement.Guard));
                return g.Kind == IrKinds.Or ? [g, .. g.Args!] : new[] { g };
            }).Select(IrText.Expr).ToHashSet(C);
            var encodes = others.Any(b => setTexts[b].Any(sg => resetTexts.Contains(IrText.Expr(sg)) || ReadsNegated(sg, a.Latch.Key)));
            if (encodes) { verdict(reason, a.Latch.Symbol); continue; }
            kept.Add(a);
        }
        return kept;
    }

    static bool ReadsNegated(IrExpr e, string key)
    {
        if (e.Kind == IrKinds.Not && e.Args![0] is { Kind: IrKinds.Var, Name: { } n } && Key(n) == key) return true;
        return e.Args?.Any(a => ReadsNegated(a, key)) == true;
    }

    static List<Ack> AcksOf(Latch l, Func<string, IReadOnlyList<IfaceMember>> blockInterface)
    {
        var setGuards = l.Sets.Select(x => IrBlock.ExpandNodesIn(x.Unit, x.Statement.Guard)).Select(g => (g, BoolSimplifier.Fold(g))).ToArray();
        return l.Resets.SelectMany(x => AcknowledgeKeys(x.Unit, x.Statement, setGuards, l.Symbol, l.Key, blockInterface(x.Unit.BlockName)))
            .GroupBy(a => a.Text, C).Select(g => g.First()).ToList();
    }

    /// <summary>复位条件里的确认条件（见类说明 一 ⑦），每条复位路径一个；剩不下任何项的路径不出。</summary>
    static IEnumerable<Ack> AcknowledgeKeys(IrUnit unit, IrStatement reset, IReadOnlyList<(IrExpr Raw, IrExpr Folded)> setGuards, string symbol, string key,
        IReadOnlyList<IfaceMember> members)
    {
        // 「自己」在复位条件里按 true 看：为 false 时复位没有效果（(自己 ∧ Ack) 与 Ack 等价）
        var guard = BoolSimplifier.Fold(BoolSimplifier.Substitute(IrBlock.ExpandNodesIn(unit, reset.Guard),
            v => Key(v) == key ? IrExpr.True : null));
        IReadOnlyList<IrExpr> paths = guard.Kind == IrKinds.Or ? guard.Args! : [guard];
        var sections = members.GroupBy(m => m.Name, C).ToDictionary(g => g.Key, g => g.First().Section, C);
        foreach (var path in paths)
        {
            IReadOnlyList<IrExpr> conjuncts = path.Kind == IrKinds.And ? path.Args! : [path];
            var kept = new List<string>();
            var local = false; var inputsOrGlobals = true; var signal = true;
            foreach (var c in conjuncts)
            {
                var vars = c.Vars().Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
                if (vars.Length == 0) continue;                                       // 常数、指令 ENO
                if (vars.Any(v => ProjectWrites.Overlaps(v, symbol))) continue;       // 仍读到自己（如自身定时）
                if (RestatesSet(c, setGuards)) continue;                              // 只重述置位条件：报警条件消失
                foreach (var v in vars)
                    if (sections.TryGetValue(RootName(v), out var section))
                    {
                        local = true;
                        if (section is not ("Input" or "InOut")) inputsOrGlobals = false;
                    }
                if (!(c.Kind == IrKinds.Var || c.Kind == IrKinds.Rise && c.Args is [{ Kind: IrKinds.Var }])) signal = false;
                kept.Add(IrText.Expr(Qualify(c, unit.BlockName, sections)));
            }
            if (kept.Count > 0) yield return new(string.Join(" ∧ ", kept.Distinct(C).Order(StringComparer.OrdinalIgnoreCase)), local, inputsOrGlobals, signal);
        }
    }

    /// <summary>复位路径里的一项只是在重述置位条件（报警条件消失 / 与置位同时成立的前提）：对每处置位，它等于置位条件的取反，
    /// 或去掉取反、上升沿、下降沿后与置位条件的某个必要项（同样去掉这些后）相同——如置位 ¬OKn、复位 OKn；置位 同步中 ∧ …、
    /// 复位 下降沿(同步中)；置位 上升沿(F)、复位 下降沿(F)。必要项取化简前后的置位条件各算一遍（置位条件被常数屏蔽、化简成 FALSE 时
    /// 仍按原式认）。比较按文字。置位条件把确认信号包在计时器等部件里时不算重述（PLC2 版 FB3007：置位 TON(… ∧ ¬Reset)、复位 上升沿(Reset)）。</summary>
    static bool RestatesSet(IrExpr c, IReadOnlyList<(IrExpr Raw, IrExpr Folded)> setGuards)
    {
        var folded = BoolSimplifier.Fold(c);
        var text = IrText.Expr(folded);
        var core = IrText.Expr(Core(folded));
        return setGuards.Count > 0 && setGuards.All(s =>
            text == IrText.Expr(BoolSimplifier.Fold(IrExpr.Not(s.Folded)))
            || NecessaryLiterals(s.Folded).Concat(NecessaryLiterals(s.Raw)).Any(l => core == IrText.Expr(Core(l))));
    }

    /// <summary>去掉外层的取反、上升沿、下降沿。</summary>
    static IrExpr Core(IrExpr e) =>
        e.Kind is IrKinds.Not or IrKinds.Rise or IrKinds.Fall && e.Args is [var inner] ? Core(inner) : e;

    /// <summary>置位条件成立时必然成立的项：「与」的各项；「或」各分支共有的项（按文字）；其它为条件本身。</summary>
    static IReadOnlyList<IrExpr> NecessaryLiterals(IrExpr s)
    {
        if (s.Kind == IrKinds.And) return s.Args!;
        if (s.Kind != IrKinds.Or) return [s];
        var branches = s.Args!.Select(NecessaryLiterals).ToArray();
        return branches[0].Where(l => branches.Skip(1).All(b => b.Any(x => IrText.Expr(x) == IrText.Expr(l)))).ToArray();
    }

    /// <summary>根段是块接口成员（局部变量 / 形参）的变量名前加「块名#」，不同块的同名局部量不算同一个信号。</summary>
    static IrExpr Qualify(IrExpr e, string block, IReadOnlyDictionary<string, string> members)
    {
        if (e.Kind == IrKinds.Var && e.Name is { } n && members.ContainsKey(RootName(n))) return e with { Name = block + "#" + n.Trim() };
        return e.Args is null ? e : e with { Args = e.Args.Select(a => Qualify(a, block, members)).ToArray() };
    }

    static bool IsText(IrStatement s) =>
        s.Kind is IrStatementKinds.Source or IrStatementKinds.Action or IrStatementKinds.Unrecognized && !string.IsNullOrEmpty(s.Text);

    static bool Mentions(string text, string name) =>
        Regex.IsMatch(text, @"(?<![A-Za-z0-9_])#?""?" + Regex.Escape(name) + @"""?(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static string Clip(string text) => text.Length > MaxConditionText ? text[..MaxConditionText] + "…" : text;

    /// <summary>写入是否不影响「只由置位/复位写」：同一位的置位/复位；与它不重叠的写入（兄弟成员等）；父结构/字被整体赋常数 0 / FALSE。</summary>
    static bool Allowed((string Target, IrStatement Statement, ProjectWrites.Spread Spread) w, string symbol, string key)
    {
        if (!ProjectWrites.Overlaps(w.Target, symbol)) return true;
        var s = w.Statement;
        var isStatementTarget = s.Kind is IrStatementKinds.Assign or IrStatementKinds.Set or IrStatementKinds.Reset
            && s.Target is not null && string.Equals(s.Target.Trim(), w.Target, StringComparison.Ordinal);
        if (!isStatementTarget) return false;
        if (s.Kind is IrStatementKinds.Set or IrStatementKinds.Reset) return Key(w.Target) == key;
        return w.Spread == ProjectWrites.Spread.None && IsStrictAncestor(w.Target, symbol) && IsZero(s.Value);
    }

    static bool IsStrictAncestor(string parent, string child)
    {
        var p = IrBlock.Segments(parent).Select(Norm).ToArray();
        var c = IrBlock.Segments(child).Select(Norm).ToArray();
        return p.Length < c.Length && p.SequenceEqual(c.Take(p.Length), C);
    }

    static bool IsZero(IrExpr? value) =>
        value is { Kind: IrKinds.Const, Name: { } n } && n.Trim().ToUpperInvariant() is "0" or "FALSE" or "16#0" or "W#16#0" or "DW#16#0" or "B#16#0";

    static string Evidence(string what, IrUnit unit, IrStatement s) =>
        $"{what}：{unit.BlockName} 网络 {unit.Network?.ToString() ?? "—"}：{Clip(IrText.Expr(IrBlock.ExpandNodesIn(unit, s.Guard)))}";

    static string Norm(string seg) =>
        seg.StartsWith('[') ? new string(seg.Where(ch => !char.IsWhiteSpace(ch)).ToArray()) : seg.Trim().Trim('"');

    /// <summary>比较键：逐段规整（名字去引号、下标去空白）后忽略大小写。</summary>
    static string Key(string name) => string.Join("\u0001", IrBlock.Segments(name.Trim()).Select(Norm)).ToUpperInvariant();

    static string RootName(string name) => Norm(IrBlock.Segments(name.Trim()).FirstOrDefault() ?? "");

    static bool Is(XElement e, string local) => e.Name.LocalName == local;

    static IEnumerable<XElement> ChildMembers(XElement e) =>
        e.Elements().Where(x => Is(x, "Member"))
            .Concat(e.Elements().Where(x => Is(x, "Sections")).Elements().Where(x => Is(x, "Section")).Elements().Where(x => Is(x, "Member")));

    static readonly Dictionary<string, int> BitStringWidths = new(StringComparer.OrdinalIgnoreCase)
        { ["Byte"] = 8, ["Word"] = 16, ["DWord"] = 32, ["LWord"] = 64 };

    /// <summary>一种走通的拆法：终点类型、终点成员（数组元素为数组成员，位切片为被切的成员）、是否位切片。</summary>
    sealed record Resolution(string? Type, XElement? Member, bool Slice)
    {
        public bool IsBit => Slice || C.Equals(Type, "Bool");
    }

    static List<Resolution> Resolve(string? interfaceXml, IReadOnlyList<string> path)
    {
        var results = new List<Resolution>();
        if (string.IsNullOrWhiteSpace(interfaceXml) || path.Count == 0) return results;
        XElement root;
        try { root = XElement.Parse(interfaceXml); } catch (XmlException) { return results; }
        var sections = Is(root, "Sections") ? root : root.Elements().FirstOrDefault(e => Is(e, "Sections"));
        if (sections is null) return results;
        // 成员名本身可含 '.'（如 "0.System"）或就是 "[1]"：程序里的写法拼接后分不出段界，逐种拆法试
        Walk(sections.Elements().Where(e => Is(e, "Section")).Elements().Where(e => Is(e, "Member")), null, null, path, 0, results);
        return results;
    }

    /// <summary>成员路径（不含块名）在接口里是否是一个位（见类说明 一 ②）：恰好一种拆法走通且终点是位。认不出一律 false。</summary>
    internal static bool IsBit(string interfaceXml, IReadOnlyList<string> path)
    {
        var r = Resolve(interfaceXml, path);
        return r.Count == 1 && r[0].IsBit;
    }

    /// <summary>成员路径（不含块名）的注释（恰好一种拆法走通时）；没有为 null。</summary>
    internal static string? MemberComment(string? interfaceXml, IReadOnlyList<string> path)
    {
        var r = Resolve(interfaceXml, path);
        if (r.Count != 1 || r[0].Slice || r[0].Member is not { } m) return null;
        var text = m.Elements().FirstOrDefault(e => Is(e, "Comment"))?.Descendants().FirstOrDefault(e => Is(e, "MultiLanguageText"))?.Value.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>从第 i 段起走通的拆法（到 2 种即停）。</summary>
    static void Walk(IEnumerable<XElement> level, string? type, XElement? member, IReadOnlyList<string> path, int i, List<Resolution> results)
    {
        if (results.Count >= 2) return;
        if (i == path.Count) { results.Add(new(type, member, false)); return; }
        var seg = path[i];
        if (seg.StartsWith('[') && type is not null && type.StartsWith("Array[", StringComparison.OrdinalIgnoreCase))
        {
            var inner = new string(seg.Trim('[', ']').Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            var of = type.LastIndexOf(" of ", StringComparison.OrdinalIgnoreCase);
            // 非字面量 / 多维下标不认
            if (!int.TryParse(inner, out _) || of < 0 || type[..of].Contains(',')) return;
            Walk(level, type[(of + 4)..].Trim(), member, path, i + 1, results);
            return;
        }
        var slice = BitSlice.Match(seg);
        if (slice.Success)
        {
            if (i == path.Count - 1 && type is not null && BitStringWidths.TryGetValue(type, out var width)
                && int.TryParse(slice.Groups[1].Value, out var bit) && bit < width)
                results.Add(new(type, member, true));
            return;
        }
        var members = level.ToArray();
        for (var k = i + 1; k <= path.Count && results.Count < 2; k++)
        {
            var name = string.Join(".", path.Skip(i).Take(k - i)).Trim('"');
            var found = members.Where(m => C.Equals((string?)m.Attribute("Name"), name)).ToArray();
            if (found.Length > 1) { results.Add(new(null, null, false)); results.Add(new(null, null, false)); return; }   // 同名成员：认不出
            if (found.Length == 1)
                Walk(ChildMembers(found[0]), ((string?)found[0].Attribute("Datatype"))?.Trim(), found[0], path, k, results);
        }
    }
}
