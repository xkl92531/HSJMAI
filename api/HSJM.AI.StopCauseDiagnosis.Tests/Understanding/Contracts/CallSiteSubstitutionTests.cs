#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class CallSiteSubstitutionTests
{
    static readonly PipelineTag[] Tags =
    [
        new("Q_W", "%Q1.0", "Bool"), new("Q_H", "%Q1.1", "Bool"),
        new("S1", "%I2.0", "Bool"), new("S2", "%I2.1", "Bool"), new("S3", "%I2.2", "Bool"),
        new("AlwaysTRUE", "%M1.2", "Bool"), new("Estop", "%I3.0", "Bool")
    ];

    static IrExpr V(string n) => IrExpr.Var(n);
    static IrPin In(string n, string type, IrExpr? v) => new(n, "Input", type, v, []);
    static IrPin Out(string n, params string[] targets) => new(n, "Output", "Bool", null, targets);

    static ValidatedFbContract Contract(params AiContractRelation[] relations)
        => new("FB_V", relations.Select(r => new ValidatedRelation(r, false, null)).ToArray());

    static AiContractRelation Feedback() =>
        new("feedback", ["O_FwdOut"], [], "Cmd.IWP", "C_FwdPresetTime", null, null, ["FB_V/5/1"], null);

    static IrCall Call(IrExpr? sensorNumber, IrExpr? pos1, IrExpr? pos2, params string[] fwdTargets) =>
        new("FB_V", "FB", "Valve[1]", IrExpr.True,
        [
            In("C_SensorNumber", "Int", sensorNumber), In("I_FwdPos1", "Bool", pos1), In("I_FwdPos2", "Bool", pos2),
            In("C_FwdPresetTime", "Real", IrExpr.Const("10.0")), Out("O_FwdOut", fwdTargets),
            new IrPin("A_Word", "Output", "\"UDT_Alarm\"", null, ["DB_Alarm.Cyl[1]"])
        ]);

    static readonly HashSet<string> NoLocals = new(System.StringComparer.OrdinalIgnoreCase);

    static CallSiteResult Apply(IrCall call, params AiContractRelation[] relations) =>
        CallSiteSubstitution.Apply(Contract(relations), IrBlockTests.Valve(), call, new System.Collections.Generic.HashSet<string>(), NoLocals, Tags, "ck", "m1");

    [Fact]
    public void SingleSensorWiringYieldsImpliesWithTheWiredInput()
    {
        var r = Apply(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), Feedback());
        var rule = Assert.Single(r.Rules);
        Assert.Equal("IMPLIES", rule.Operator);
        Assert.Equal(new[] { "S1" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray());
        Assert.Equal("Q_W", rule.Signals.Single(s => s.Role == "Source").Name);
        Assert.Equal("AfterSettling", rule.Applicability);
        Assert.Equal(UnderstandingProofs.AiFbContract, rule.Proof);
        Assert.Equal("ck", rule.Trace!.CacheKey);
    }

    [Fact]
    public void TwoSensorWiringRequiresBoth()
    {
        var rule = Assert.Single(Apply(Call(IrExpr.Const("2"), V("S1"), V("S2"), "Q_W"), Feedback()).Rules);
        Assert.Equal(new[] { "S1", "S2" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).Order().ToArray());
    }

    [Fact]
    public void UnwiredConfigInputSuppressesTheFeedback()
    {
        // 未接线的非 Bool 配置输入：实例值可能不同于接口初值，不能拿初值去选分支
        var r = Apply(Call(null, V("S1"), null, "Q_W"), Feedback());
        Assert.Empty(r.Rules);
        Assert.Contains("UnwiredConfigInput:C_SensorNumber", r.Notes);
    }

    [Fact]
    public void OutputThatIsNotAUniqueQSkipsTheFeedback()
    {
        var r = Apply(Call(IrExpr.Const("1"), V("S1"), null, "DB_X.Y"), Feedback());
        Assert.Empty(r.Rules);
        Assert.Contains(r.Notes, n => n.StartsWith("OutputNotUniqueQ"));
    }

    [Fact]
    public void BranchThatIsSatisfiedByTheOutputItselfMeansNoFeedback()
    {
        // 到位输入接的是输出本身：程序有替代到位路径
        var r = Apply(Call(IrExpr.Const("1"), V("Q_W"), null, "Q_W"), Feedback());
        Assert.Empty(r.Rules);
        Assert.Contains("AlternativePathViaOutput", r.Notes);
    }

    [Fact]
    public void OrOfTwoSensorGroupsYieldsOneImpliesPerGroup()
    {
        // 到位输入接 (S1 AND S2) OR S3：两组各自满足即可 → 每组一条 IMPLIES（用户 2026-10-02：不再产 IMPLIES_ANY）
        var pos1 = IrExpr.Or([IrExpr.And([V("S1"), V("S2")]), V("S3")]);
        var r = Apply(Call(IrExpr.Const("1"), pos1, null, "Q_W"), Feedback());
        Assert.Equal(new[] { "S1&S2", "S3" }, Groups(r));
        Assert.All(r.Rules, rule =>
        {
            Assert.Equal("IMPLIES", rule.Operator);
            Assert.Null(rule.TargetGroups);
            Assert.Equal("Q_W", rule.Signals.Single(s => s.Role == "Source").Name);
            Assert.Equal("AfterSettling", rule.Applicability);
            Assert.True(rule.RequiresSettlingEvidence);
            Assert.Equal(UnderstandingProofs.AiFbContract, rule.Proof);
            Assert.Equal("ck", rule.Trace!.CacheKey);
            Assert.DoesNotContain(" 或 ", rule.Description);
        });
        Assert.Equal(2, r.Rules.Select(x => x.Key).Distinct().Count());
    }

    /// <summary>每条 IMPLIES 的 Target 集合（序数排序、&amp; 连接），按文本排序。</summary>
    static string[] Groups(CallSiteResult r) => r.Rules
        .Select(x => string.Join("&", x.Signals.Where(s => s.Role == "Target").Select(s => s.Name).Order(System.StringComparer.Ordinal)))
        .Order(System.StringComparer.Ordinal).ToArray();

    [Fact]
    public void NonIoOnlyBranchBecomesABypass()
    {
        var pos1 = IrExpr.Or([V("S1"), V("DB_User.ByPass[5]")]);
        var rule = Assert.Single(Apply(Call(IrExpr.Const("1"), pos1, null, "Q_W"), Feedback()).Rules);
        Assert.Equal("IMPLIES", rule.Operator);
        Assert.Equal(new[] { "DB_User.ByPass[5]" }, rule.BypassSignals);
    }

    [Fact]
    public void ConditionalEnSkipsEverything()
    {
        var call = Call(IrExpr.Const("1"), V("S1"), null, "Q_W") with { En = V("Enable") };
        var r = Apply(call, Feedback());
        Assert.Empty(r.Rules);
        Assert.Contains("EnConditional", r.Notes);
    }

    [Fact]
    public void TimeoutAlarmMapsTheAlarmPathThroughTheOutputPin()
    {
        var alarm = new AiContractRelation("timeoutAlarm", ["O_FwdOut"], [], "Cmd.IWP", "C_FwdPresetTime", null,
            "A_Word.Alarm1", ["FB_V/5/3"], null);
        var r = Apply(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), Feedback(), alarm);
        var a = Assert.Single(r.Alarms);
        Assert.Equal("DB_Alarm.Cyl[1].Alarm1", a.AlarmSymbol);
        Assert.Equal("Timeout", a.Kind);
        Assert.Equal("Q_W", a.Output);
        Assert.Equal(new[] { "S1" }, a.Feedback);
        Assert.Equal("10.0", a.Delay);
    }

    [Fact]
    public void TimeoutAlarmKeepsTheOrGroupsOfItsPositionPoints()
    {
        var alarm = new AiContractRelation("timeoutAlarm", ["O_FwdOut"], [], "Cmd.IWP", "C_FwdPresetTime", null,
            "A_Word.Alarm1", ["FB_V/5/3"], null);
        var pos1 = IrExpr.Or([IrExpr.And([V("S1"), V("S2")]), V("S3")]);
        var a = Assert.Single(Apply(Call(IrExpr.Const("1"), pos1, null, "Q_W"), Feedback(), alarm).Alarms);
        Assert.Equal(new[] { "S1", "S2", "S3" }, a.Feedback);
        Assert.Equal(new[] { new[] { "S1", "S2" }, new[] { "S3" } }, a.FeedbackGroups.Select(g => g.ToArray()).ToArray());
    }

    [Fact]
    public void TimeoutAlarmWithOneSensorGroupHasThatSingleGroup()
    {
        var alarm = new AiContractRelation("timeoutAlarm", ["O_FwdOut"], [], "Cmd.IWP", "C_FwdPresetTime", null,
            "A_Word.Alarm1", ["FB_V/5/3"], null);
        var a = Assert.Single(Apply(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), Feedback(), alarm).Alarms);
        Assert.Equal(new[] { new[] { "S1" } }, a.FeedbackGroups.Select(g => g.ToArray()).ToArray());
    }

    [Fact]
    public void ProvenRelationsAreLeftToPhaseOne()
    {
        var mutex = new AiContractRelation("mutex", ["O_FwdOut", "O_BkdOut"], [], null, null, null, null, ["FB_V/5/2"], null);
        var contract = new ValidatedFbContract("FB_V", [new ValidatedRelation(mutex, true, null)]);
        var r = CallSiteSubstitution.Apply(contract, IrBlockTests.Valve(), Call(IrExpr.Const("1"), V("S1"), null, "Q_W"),
            new System.Collections.Generic.HashSet<string>(), NoLocals, Tags, "ck", "m1");
        Assert.Empty(r.Rules);
    }

    [Fact]
    public void GateWhoseInputIsACallerFormalBecomesDerived()
    {
        var gate = new AiContractRelation("gate", ["O_FwdOut"], ["I_FwdCmd"], null, null, null, null, ["FB_V/6/2"], null);
        var call = Call(IrExpr.Const("1"), V("S1"), null, "Q_W");
        call = call with { Pins = [.. call.Pins, In("I_FwdCmd", "Bool", V("Mode.EstopOk"))] };
        var r = CallSiteSubstitution.Apply(Contract(gate), IrBlockTests.Valve(), call,
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "Mode" }, NoLocals, Tags, "ck", "m1");
        Assert.Empty(r.Rules);
        var d = Assert.Single(r.Derived);
        Assert.Equal(new[] { "Mode.EstopOk" }, d.InCallerTerms.Inputs);
        Assert.Equal(new[] { "Q_W" }, d.InCallerTerms.Outputs);
    }

    [Fact]
    public void BranchThroughTheInternalOutputVariableIsAnAlternativePath()
    {
        // 到位变量定义里直接用了驱动输出的内部变量 Cmd.QFwd：(I_FwdPos1) OR (Cmd.QFwd)
        var b = IrBlockTests.Valve();
        var unit = new IrUnit(9, "FB_V", 9, "LAD", "FlgNet",
            [new("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True, IrExpr.Or([V("I_FwdPos1"), V("Cmd.QFwd")]))], [], []);
        var block = IrBlock.FromUnits("FB_V", [.. b.Units, unit], b.Interface);
        var fb = new AiContractRelation("feedback", ["O_FwdOut"], [], "Cmd.Pos2", null, null, null, ["FB_V/9/1"], null);
        var r = CallSiteSubstitution.Apply(Contract(fb), block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"),
            new System.Collections.Generic.HashSet<string>(), NoLocals, Tags, "ck", "m1");
        Assert.Empty(r.Rules);
        Assert.Contains("AlternativePathViaOutput", r.Notes);
    }

    [Fact]
    public void BranchWithOnlyAConstantTableBitProducesNothing()
    {
        // 到位输入接 AlwaysTRUE（点表里的 %M 位）：可能是旁路也可能是常量位，算不清 → 不产
        var r = Apply(Call(IrExpr.Const("1"), V("AlwaysTRUE"), null, "Q_W"), Feedback());
        Assert.Empty(r.Rules);
        Assert.Contains("MemoryOnlyBranch", r.Notes);
        Assert.Empty(r.Superseded);
    }

    // ---- 以下为控制方裁定（R1、展开范围、延时文本）与「只会漏、不会错」的补充用例 ----

    [Fact]
    public void WriterlessGlobalSharedByOutputAndPositionIsNotMistakenForTheOutput()
    {
        // 夹具前提：O_FwdOut := AlwaysTRUE AND Cmd.QFwd，到位定义里每个分支也有 AlwaysTRUE，且块内没有写 AlwaysTRUE
        var b = IrBlockTests.Valve();
        Assert.Contains("AlwaysTRUE", b.DirectSources("O_FwdOut"));
        Assert.Contains("AlwaysTRUE", b.Definition("Cmd.IWP")!.Vars());
        Assert.Empty(b.Writers("AlwaysTRUE"));
        var r = Apply(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), Feedback());
        var rule = Assert.Single(r.Rules);
        Assert.Equal("IMPLIES", rule.Operator);
        Assert.DoesNotContain("AlternativePathViaOutput", r.Notes);
    }

    static IrBlock ValveWith(params IrStatement[] extra)
    {
        var b = IrBlockTests.Valve();
        var unit = new IrUnit(9, "FB_V", 9, "LAD", "FlgNet", extra, [], []);
        return IrBlock.FromUnits("FB_V", [.. b.Units, unit], b.Interface);
    }

    static CallSiteResult ApplyOn(IrBlock block, IrCall call, IReadOnlySet<string>? callerFormals = null, params AiContractRelation[] relations) =>
        CallSiteSubstitution.Apply(Contract(relations), block, call,
            callerFormals ?? new HashSet<string>(System.StringComparer.OrdinalIgnoreCase), NoLocals, Tags, "ck", "m1");

    static AiContractRelation FeedbackOn(string positionVar) =>
        new("feedback", ["O_FwdOut"], [], positionVar, null, null, null, ["FB_V/9/1"], null);

    [Fact]
    public void LocalWithAUniqueDefinitionIsExpanded()
    {
        // Cmd.Pos2 := I_FwdPos1 OR Cmd.Alt；Cmd.Alt := I_FwdPos2（本块 Static，唯一定义）→ 两组
        var block = ValveWith(
            new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True, IrExpr.Or([V("I_FwdPos1"), V("Cmd.Alt")])),
            new IrStatement("FB_V/9/2", IrStatementKinds.Assign, "Cmd.Alt", IrExpr.True, V("I_FwdPos2")));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), V("S2"), "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.All(r.Rules, x => Assert.Equal("IMPLIES", x.Operator));
        Assert.Equal(new[] { "S1", "S2" }, Groups(r));
    }

    [Fact]
    public void LocalWithoutAUniqueDefinitionIsInternalAndBlocksTheFeedback()
    {
        // Cmd.FwdErr 由 Set 写入，没有唯一定义：独立成项时无法判定要求哪些输入
        var block = ValveWith(
            new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True, IrExpr.Or([V("I_FwdPos1"), V("Cmd.FwdErr")])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Empty(r.Rules);
        Assert.Contains("InternalOnlyBranch", r.Notes);
    }

    [Fact]
    public void GlobalWrittenInsideTheBlockKeepsItsPlainName()
    {
        // DB_G.X 不是本块 Static/Temp：即使块内写它，也按全局名保留（独立成项即旁路），不加 @内部:
        var block = ValveWith(
            new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True, IrExpr.Or([V("I_FwdPos1"), V("DB_G.X")])),
            new IrStatement("FB_V/9/2", IrStatementKinds.Assign, "DB_G.X", IrExpr.True, V("I_FwdCmd")));
        var rule = Assert.Single(ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2")).Rules);
        Assert.Equal(new[] { "DB_G.X" }, rule.BypassSignals);
    }

    [Fact]
    public void NegatedOutputPinSkipsTheFeedback()
    {
        var call = Call(IrExpr.Const("1"), V("S1"), null, "Q_W");
        call = call with { Pins = call.Pins.Select(p => p.Name == "O_FwdOut" ? p with { Negated = true } : p).ToArray() };
        var r = Apply(call, Feedback());
        Assert.Empty(r.Rules);
        Assert.Contains(r.Notes, n => n.StartsWith("OutputNotUniqueQ"));
    }

    [Fact]
    public void CallerFormalInAPositionBranchIsNotTreatedAsABypass()
    {
        // 到位输入接的是调用方形参：既不是 IO 点，也不是可读的全局名 → 不派生反馈，不产
        var r = ApplyOn(IrBlockTests.Valve(), Call(IrExpr.Const("1"), V("I_Pos"), null, "Q_W"),
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "I_Pos" }, Feedback());
        Assert.Empty(r.Rules);
        Assert.Contains("DerivedFeedbackUnsupported", r.Notes);
    }

    [Fact]
    public void NonFiniteDelayConstantIsDropped()
    {
        var alarm = new AiContractRelation("timeoutAlarm", ["O_FwdOut"], [], "Cmd.IWP", "C_FwdPresetTime", null,
            "A_Word.Alarm1", ["FB_V/5/3"], null);
        var call = Call(IrExpr.Const("1"), V("S1"), null, "Q_W");
        call = call with { Pins = call.Pins.Select(p => p.Name == "C_FwdPresetTime" ? p with { Value = IrExpr.Const("1e999") } : p).ToArray() };
        var a = Assert.Single(Apply(call, Feedback(), alarm).Alarms);
        Assert.Null(a.Delay);
    }

    [Fact]
    public void UnprovenMutexOfTwoQsYieldsAtMostOneButNotForTheSameQ()
    {
        var mutex = new AiContractRelation("mutex", ["O_FwdOut", "O_BkdOut"], [], null, null, null, null, ["FB_V/5/2"], null);
        var call = Call(IrExpr.Const("1"), V("S1"), null, "Q_W");
        var both = call with { Pins = [.. call.Pins, Out("O_BkdOut", "Q_H")] };
        var rule = Assert.Single(Apply(both, mutex).Rules);
        Assert.Equal("AT_MOST_ONE", rule.Operator);
        Assert.Equal(UnderstandingProofs.AiFbContract, rule.Proof);
        Assert.Equal(new[] { "Q_H", "Q_W" }, rule.Signals.Select(s => s.Name).Order().ToArray());

        var same = call with { Pins = [.. call.Pins, Out("O_BkdOut", "Q_W")] };
        Assert.Empty(Apply(same, mutex).Rules);
    }

    // ---- Fix round 1 ----

    static CallSiteResult ApplyFull(IrBlock block, IrCall call, IReadOnlySet<string> callerFormals, IReadOnlySet<string> callerLocals,
        params ValidatedRelation[] relations) =>
        CallSiteSubstitution.Apply(new ValidatedFbContract("FB_V", relations), block, call, callerFormals, callerLocals, Tags, "ck", "m1");

    static HashSet<string> Set(params string[] names) => new(names, System.StringComparer.OrdinalIgnoreCase);

    static ValidatedRelation Unproven(AiContractRelation r) => new(r, false, null);

    static AiContractRelation Gate() =>
        new("gate", ["O_FwdOut"], ["I_FwdCmd"], null, null, null, null, ["FB_V/6/2"], null);

    static IrCall WithPin(IrCall call, IrPin pin) =>
        call with { Pins = [.. call.Pins.Where(p => p.Name != pin.Name), pin] };

    static AiContractRelation TimeoutAlarm(string positionVar) =>
        new("timeoutAlarm", ["O_FwdOut"], [], positionVar, "C_FwdPresetTime", null, "A_Word.Alarm1", ["FB_V/5/3"], null);

    [Fact]
    public void TimeoutAlarmTakesThePointsOfTheFeedbackWithTheSamePositionVar()
    {
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True, V("I_FwdPos2")));
        var r = ApplyFull(block, Call(IrExpr.Const("1"), V("S1"), V("S2"), "Q_W"), NoLocals, NoLocals,
            Unproven(Feedback()), Unproven(FeedbackOn("Cmd.Pos2")), Unproven(TimeoutAlarm("Cmd.Pos2")));
        Assert.Equal(2, r.Rules.Count);
        Assert.Equal(new[] { "S2" }, Assert.Single(r.Alarms).Feedback);
    }

    [Fact]
    public void TimeoutAlarmPairingDoesNotDependOnFeedbackOrder()
    {
        // 匹配的反馈排在前面：按输出单独配对时会被后一个反馈覆盖
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True, V("I_FwdPos2")));
        var r = ApplyFull(block, Call(IrExpr.Const("1"), V("S1"), V("S2"), "Q_W"), NoLocals, NoLocals,
            Unproven(FeedbackOn("Cmd.Pos2")), Unproven(Feedback()), Unproven(TimeoutAlarm("Cmd.Pos2")));
        Assert.Equal(new[] { "S2" }, Assert.Single(r.Alarms).Feedback);
    }

    [Fact]
    public void TimeoutAlarmWithoutAMatchingFeedbackIsNotProduced()
    {
        // 迭代 4 修正 G：没有该（输出, 到位变量）反馈的到位点 → 不产超时报警
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True, V("I_FwdPos2")));
        var r = ApplyFull(block, Call(IrExpr.Const("1"), V("S1"), V("S2"), "Q_W"), NoLocals, NoLocals,
            Unproven(Feedback()), Unproven(TimeoutAlarm("Cmd.Pos2")));
        Assert.Empty(r.Alarms);
        Assert.Contains("AlarmWithoutPositionPoints", r.Notes);
    }

    [Fact]
    public void DuplicateFeedbackKeepsTheFirst()
    {
        var r = ApplyFull(IrBlockTests.Valve(), Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), NoLocals, NoLocals,
            Unproven(Feedback()), Unproven(Feedback() with { Evidence = ["FB_V/5/1", "FB_V/5/2"] }));
        Assert.Single(r.Rules);
        Assert.Contains("DuplicateFeedback", r.Notes);
    }

    [Fact]
    public void GateYieldsRequiresZeroWhenFalseWhenTheInputIsNotWrittenInTheBody()
    {
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), In("I_FwdCmd", "Bool", V("Estop")));
        var rule = Assert.Single(ApplyFull(IrBlockTests.Valve(), call, NoLocals, NoLocals, Unproven(Gate())).Rules);
        Assert.Equal("REQUIRES_0_WHEN_FALSE", rule.Operator);
    }

    [Fact]
    public void GateInputWrittenInTheBodyIsSkipped()
    {
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "I_FwdCmd", IrExpr.True, V("AlwaysTRUE")));
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), In("I_FwdCmd", "Bool", V("Estop")));
        var r = ApplyFull(block, call, NoLocals, NoLocals, Unproven(Gate()));
        Assert.Empty(r.Rules);
        Assert.Empty(r.Derived);
        Assert.Contains("GateInputWrittenInBody", r.Notes);
    }

    [Fact]
    public void WiredConfigInputAllowsTheFeedback()
    {
        var r = Apply(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), Feedback());
        Assert.Single(r.Rules);
        Assert.DoesNotContain(r.Notes, n => n.StartsWith("UnwiredConfigInput"));
    }

    static IrBlock ValveWithDelayStart(string? startValue)
    {
        var b = IrBlockTests.Valve();
        var iface = b.Interface.Select(m => m.Name == "C_FwdPresetTime" ? m with { StartValue = startValue } : m).ToArray();
        return IrBlock.FromUnits("FB_V", b.Units, iface);
    }

    [Fact]
    public void UnwiredDelayWithoutStartValueIsNotInvented()
    {
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), In("C_FwdPresetTime", "Real", null));
        var r = ApplyFull(ValveWithDelayStart(null), call, NoLocals, NoLocals, Unproven(Feedback()), Unproven(TimeoutAlarm("Cmd.IWP")));
        Assert.Null(Assert.Single(r.Alarms).Delay);
        Assert.Contains("程序设定", Assert.Single(r.Rules).Description);
    }

    [Fact]
    public void UnwiredDelayWithAStartValueUsesItForDescription()
    {
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), In("C_FwdPresetTime", "Real", null));
        var r = ApplyFull(ValveWithDelayStart("5.0"), call, NoLocals, NoLocals, Unproven(Feedback()), Unproven(TimeoutAlarm("Cmd.IWP")));
        Assert.Equal("5.0", Assert.Single(r.Alarms).Delay);
        Assert.Single(r.Rules);
    }

    [Fact]
    public void UnresolvableTargetIsUnwired()
    {
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), new IrPin("A_Word", "Output", "\"UDT_Alarm\"", null, ["?"]));
        var r = Apply(call, Feedback(), TimeoutAlarm("Cmd.IWP"));
        Assert.Empty(r.Alarms);
        Assert.Contains("AlarmUnwired", r.Notes);

        var q = Apply(Call(IrExpr.Const("1"), V("S1"), null, "?"), Feedback());
        Assert.Empty(q.Rules);
        Assert.Contains(q.Notes, n => n.StartsWith("OutputNotUniqueQ"));
    }

    [Fact]
    public void CallerFormalBesideASensorInTheSameTermLeavesTheGroup()
    {
        var pos1 = IrExpr.And([V("S1"), V("I_Pos")]);
        var r = ApplyFull(IrBlockTests.Valve(), Call(IrExpr.Const("1"), pos1, null, "Q_W"), Set("I_Pos"), NoLocals, Unproven(Feedback()));
        var rule = Assert.Single(r.Rules);
        Assert.Equal(new[] { "S1" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray());
    }

    [Fact]
    public void SupersetGroupsAreAbsorbed()
    {
        // S1 OR (S1 AND S2) = S1
        var pos1 = IrExpr.Or([V("S1"), IrExpr.And([V("S1"), V("S2")])]);
        var rule = Assert.Single(Apply(Call(IrExpr.Const("1"), pos1, null, "Q_W"), Feedback()).Rules);
        Assert.Equal("IMPLIES", rule.Operator);
        Assert.Null(rule.TargetGroups);
        Assert.Equal(new[] { "S1" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray());
    }

    [Fact]
    public void ProvenGateWiredToACallerFormalIsDerived()
    {
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), In("I_FwdCmd", "Bool", V("Mode.EstopOk")));
        var r = ApplyFull(IrBlockTests.Valve(), call, Set("Mode"), NoLocals, new ValidatedRelation(Gate(), true, null));
        Assert.Empty(r.Rules);
        var d = Assert.Single(r.Derived);
        Assert.Equal("gate", d.Kind);
        Assert.Equal(new[] { "Mode.EstopOk" }, d.InCallerTerms.Inputs);
        Assert.Equal(new[] { "Q_W" }, d.InCallerTerms.Outputs);
    }

    [Fact]
    public void ProvenGateWiredToGlobalsIsLeftToPhaseOne()
    {
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), In("I_FwdCmd", "Bool", V("Estop")));
        var r = ApplyFull(IrBlockTests.Valve(), call, NoLocals, NoLocals, new ValidatedRelation(Gate(), true, null));
        Assert.Empty(r.Rules);
        Assert.Empty(r.Derived);
    }

    [Fact]
    public void ActualThatIsACallerLocalIsSkippedNotDerived()
    {
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), In("I_FwdCmd", "Bool", V("Mode.EstopOk")));
        var r = ApplyFull(IrBlockTests.Valve(), call, NoLocals, Set("Mode"), Unproven(Gate()));
        Assert.Empty(r.Rules);
        Assert.Empty(r.Derived);
        Assert.Contains("ActualIsCallerLocal", r.Notes);

        var proven = ApplyFull(IrBlockTests.Valve(), call, NoLocals, Set("Mode"), new ValidatedRelation(Gate(), true, null));
        Assert.Empty(proven.Derived);
    }

    [Fact]
    public void CallerLocalAloneInAPositionBranchBlocksTheFeedback()
    {
        var pos1 = IrExpr.Or([V("S1"), V("Local.Bypass")]);
        var r = ApplyFull(IrBlockTests.Valve(), Call(IrExpr.Const("1"), pos1, null, "Q_W"), NoLocals, Set("Local"), Unproven(Feedback()));
        Assert.Empty(r.Rules);
        Assert.Contains("ActualIsCallerLocal", r.Notes);
    }

    // ---- 评测迭代 2：修正 A（备用路径判定）与修正 B（到位变量是输入形参） ----

    [Theory]
    [InlineData("Cmd.QFwd")]   // 驱动输出的内部变量
    [InlineData("O_FwdOut")]   // 输出形参本身
    public void OutputSelfBesideASensorInEveryTermStillYieldsTheFeedback(string self)
    {
        // Cmd.Pos2 := (输出自身 ∧ I_FwdPos1) ∨ (Cmd.Mem ∧ I_FwdPos1)：每一项都要求 I_FwdPos1 到位
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([IrExpr.And([V(self), V("I_FwdPos1")]), IrExpr.And([V("Cmd.Mem"), V("I_FwdPos1")])])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        var rule = Assert.Single(r.Rules);
        Assert.Equal("IMPLIES", rule.Operator);
        Assert.Equal("Q_W", rule.Signals.Single(s => s.Role == "Source").Name);
        Assert.Equal(new[] { "S1" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray());
        Assert.DoesNotContain("AlternativePathViaOutput", r.Notes);
    }

    [Fact]
    public void OutputSelfAloneInATermIsStillAnAlternativePath()
    {
        // Cmd.Pos2 := Cmd.QFwd ∨ I_FwdPos1：输出自身单独成项 → 程序有替代到位路径
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([V("Cmd.QFwd"), V("I_FwdPos1")])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Empty(r.Rules);
        Assert.Contains("AlternativePathViaOutput", r.Notes);
    }

    [Theory]
    [InlineData("Cmd.Tmr.Q")]   // 本块定时器（内部变量）
    [InlineData("DB_T.Q")]      // 点表外的全局名：没有 %I 时不得当成旁路
    public void OutputSelfWithANonSensorButNoInputIsStillAnAlternativePath(string timer)
    {
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([IrExpr.And([V("Cmd.QFwd"), V(timer)]), V("I_FwdPos1")])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Empty(r.Rules);
        Assert.Contains("AlternativePathViaOutput", r.Notes);
    }

    [Fact]
    public void OutputSelfWithOnlyANegatedSensorIsStillAnAlternativePath()
    {
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([IrExpr.And([V("Cmd.QFwd"), IrExpr.Not(V("I_FwdPos2"))]), V("I_FwdPos1")])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), V("S2"), "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Empty(r.Rules);
        Assert.Contains("AlternativePathViaOutput", r.Notes);
    }

    static AiContractRelation FeedbackOnFormal(string positionVar) =>
        new("feedback", ["O_FwdOut"], [], positionVar, null, null, null, ["FB_V/5/1"], null);

    [Fact]
    public void UnwrittenInputFormalAsPositionVarIsReplacedByTheWiredExpression()
    {
        // 到位变量就是输入形参 I_FwdPos1；调用方接 (S1 ∨ 旁路) ∧ ¬调试位
        var pos1 = IrExpr.And([IrExpr.Or([V("S1"), V("DB_User.ByPass[1]")]), IrExpr.Not(V("Debug_Test.Debug[19]"))]);
        var r = ApplyOn(IrBlockTests.Valve(), Call(IrExpr.Const("1"), pos1, null, "Q_W"), null, FeedbackOnFormal("I_FwdPos1"));
        var rule = Assert.Single(r.Rules);
        Assert.Equal("IMPLIES", rule.Operator);
        Assert.Equal("Q_W", rule.Signals.Single(s => s.Role == "Source").Name);
        Assert.Equal(new[] { "S1" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray());
        Assert.Equal(new[] { "DB_User.ByPass[1]" }, rule.BypassSignals);
        Assert.DoesNotContain("PositionVarNoUniqueDefinition", r.Notes);
    }

    [Fact]
    public void UnwrittenInOutFormalAsPositionVarIsAlsoReplaced()
    {
        var b = IrBlockTests.Valve();
        var block = IrBlock.FromUnits("FB_V", b.Units,
            b.Interface.Select(m => m.Name == "I_FwdPos1" ? m with { Section = "InOut" } : m).ToArray());
        var call = Call(IrExpr.Const("1"), V("S1"), null, "Q_W");
        call = call with { Pins = call.Pins.Select(p => p.Name == "I_FwdPos1" ? p with { Section = "InOut" } : p).ToArray() };
        var rule = Assert.Single(ApplyOn(block, call, null, FeedbackOnFormal("I_FwdPos1")).Rules);
        Assert.Equal(new[] { "S1" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray());
    }

    [Fact]
    public void InputFormalWrittenInTheBodyKeepsTheOriginalDefinitionLogic()
    {
        // 形参在体内被改写：按原逻辑取其唯一定义（AlwaysTRUE，点表 %M）→ 只剩 %M 位，不产
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "I_FwdPos1", IrExpr.True, V("AlwaysTRUE")));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOnFormal("I_FwdPos1"));
        Assert.Empty(r.Rules);
        Assert.Contains("MemoryOnlyBranch", r.Notes);
    }

    [Fact]
    public void InputFormalWrittenByASetKeepsTheOriginalLogic()
    {
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Set, "I_FwdPos1", V("AlwaysTRUE"), null));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOnFormal("I_FwdPos1"));
        Assert.Empty(r.Rules);
        Assert.Contains("PositionVarNoUniqueDefinition", r.Notes);
    }

    [Fact]
    public void InputFormalWrittenInAGraphActionIsNotTrustedAsUnwritten()
    {
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Action, "", IrExpr.True, null, null, "#I_FwdPos1 := TRUE"));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOnFormal("I_FwdPos1"));
        Assert.Empty(r.Rules);
        Assert.Contains("PositionVarNoUniqueDefinition", r.Notes);
    }

    [Fact]
    public void InputFormalWrittenThroughACallPinIsNotTrustedAsUnwritten()
    {
        var inner = new IrCall("FB_Inner", "FB", "Inner", IrExpr.True, [new IrPin("Q", "Output", "Bool", null, ["I_FwdPos1"])]);
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Call, null, IrExpr.True, null, inner));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOnFormal("I_FwdPos1"));
        Assert.Empty(r.Rules);
        Assert.Contains("PositionVarNoUniqueDefinition", r.Notes);
    }

    [Fact]
    public void InputFormalMentionedInSourceTextIsNotTrustedAsUnwritten()
    {
        // 未翻译的源码文本可能写它：保守，不当作「来自输入」
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Source, null, IrExpr.True, null, null, "      = #I_FwdPos1;"));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOnFormal("I_FwdPos1"));
        Assert.Empty(r.Rules);
        Assert.Contains("PositionVarNoUniqueDefinition", r.Notes);
    }

    // ---- 评测迭代 3：修正 D（不透明文字与传感器同项） ----

    /// <summary>本块计时/计数的比较：Cmd.Cnt 是本块没有唯一定义的 Static，展开后为不透明文字。</summary>
    static IrExpr Opq() => new(IrKinds.Cmp, ">=", [V("Cmd.Cnt"), V("C_FwdPresetTime")]);

    [Fact]
    public void NegatedOpaqueBesideASensorStillYieldsTheFeedback()
    {
        // ① Cmd.Pos2 := Cmd.QFwd ∧ I_FwdPos1 ∧ ¬(Cmd.Cnt >= C_FwdPresetTime)
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.And([V("Cmd.QFwd"), V("I_FwdPos1"), IrExpr.Not(Opq())])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        var rule = Assert.Single(r.Rules);
        Assert.Equal("IMPLIES", rule.Operator);
        Assert.Equal(new[] { "S1" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray());
        Assert.DoesNotContain("OpaqueBranch", r.Notes);
    }

    [Fact]
    public void PositiveOpaqueBesideASensorStillYieldsTheFeedback()
    {
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.And([V("I_FwdPos1"), Opq()])));
        var rule = Assert.Single(ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2")).Rules);
        Assert.Equal(new[] { "S1" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray());
    }

    [Fact]
    public void OpaqueAloneInATermIsStillAnOpaqueBranch()
    {
        // ② (Opq) ∨ (I_FwdPos1)
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([Opq(), V("I_FwdPos1")])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Empty(r.Rules);
        Assert.Contains("OpaqueBranch", r.Notes);
    }

    [Fact]
    public void OpaqueWithOnlyANonSensorIsStillAnOpaqueBranch()
    {
        // 不透明 + 点表外全局名（无 %I）：既不能当旁路，也不能成组
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([IrExpr.And([Opq(), V("DB_G.X")]), V("I_FwdPos1")])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Empty(r.Rules);
        Assert.Contains("OpaqueBranch", r.Notes);
    }

    [Fact]
    public void OpaqueTermWithASensorAndAnotherSensorTermYieldOneImpliesPerGroup()
    {
        // ③ (Q ∧ Opq ∧ I1) ∨ (Q ∧ I2)
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([IrExpr.And([V("Cmd.QFwd"), Opq(), V("I_FwdPos1")]), IrExpr.And([V("Cmd.QFwd"), V("I_FwdPos2")])])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), V("S2"), "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.All(r.Rules, x => Assert.Equal("IMPLIES", x.Operator));
        Assert.Equal(new[] { "S1", "S2" }, Groups(r));
    }

    [Fact]
    public void OutputSelfWithOpaqueButNoSensorIsAnAlternativePathFirst()
    {
        // 判定顺序：先判备用路径（迭代 2），再判不透明
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([IrExpr.And([V("Cmd.QFwd"), Opq()]), V("I_FwdPos1")])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Empty(r.Rules);
        Assert.Contains("AlternativePathViaOutput", r.Notes);
        Assert.DoesNotContain("OpaqueBranch", r.Notes);
    }

    // ---- 迭代 3 审查 Minor-5 补测 ----

    [Fact]
    public void OpaqueWithOnlyANegatedSensorIsStillAnOpaqueBranch()
    {
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([IrExpr.And([Opq(), IrExpr.Not(V("I_FwdPos2"))]), V("I_FwdPos1")])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), V("S2"), "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Empty(r.Rules);
        Assert.Contains("OpaqueBranch", r.Notes);
    }

    [Fact]
    public void UnresolvedAtomBesideASensorStillYieldsTheFeedback()
    {
        // 有意为之：未解析原子（成员取自非变量实参）与正 %I 同项，只收紧该项
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.And([V("I_FwdPos1"), V("I_FwdPos2.Sub")])));
        var call = Call(IrExpr.Const("1"), V("S1"), IrExpr.And([V("S2"), V("S3")]), "Q_W");
        var rule = Assert.Single(ApplyOn(block, call, null, FeedbackOn("Cmd.Pos2")).Rules);
        Assert.Equal(new[] { "S1" }, rule.Signals.Where(s => s.Role == "Target").Select(s => s.Name).ToArray());
    }

    // ---- 迭代 4 修正 F：反馈被确定为不依赖传感器时带回「证伪」记录 ----

    [Fact]
    public void AlternativePathViaOutputSupersedesTheOutput()
    {
        var r = Apply(Call(IrExpr.Const("1"), V("Q_W"), null, "Q_W"), Feedback());
        Assert.Contains("AlternativePathViaOutput", r.Notes);
        Assert.Equal(new[] { "Q_W" }, r.Superseded);
    }

    [Fact]
    public void NoSensorBranchSupersedesTheOutput()
    {
        var r = Apply(Call(IrExpr.Const("1"), V("Q_H"), null, "Q_W"), Feedback());
        Assert.Contains("NoSensorBranch", r.Notes);
        Assert.Equal(new[] { "Q_W" }, r.Superseded);
    }

    [Fact]
    public void MemoryBitAloneInATermNeitherProducesNorSupersedes()
    {
        // 到位输入接 (S1 ∨ M_Bypass)：M_Bypass 是点表 %M 位，单独成项 → 整个反馈关系放弃，且不删旧反馈
        PipelineTag[] tags = [.. Tags, new("M_Bypass", "%M5.0", "Bool")];
        var r = CallSiteSubstitution.Apply(Contract(Feedback()), IrBlockTests.Valve(),
            Call(IrExpr.Const("1"), IrExpr.Or([V("S1"), V("M_Bypass")]), null, "Q_W"), NoLocals, NoLocals, tags, "ck", "m1");
        Assert.Empty(r.Rules);
        Assert.Contains("MemoryOnlyBranch", r.Notes);
        Assert.Empty(r.Superseded);
    }

    [Fact]
    public void AlwaysInPositionSupersedesTheOutput()
    {
        var r = ApplyOn(IrBlockTests.Valve(), Call(IrExpr.Const("1"), IrExpr.True, null, "Q_W"), null, FeedbackOnFormal("I_FwdPos1"));
        Assert.Contains("AlwaysInPosition", r.Notes);
        Assert.Equal(new[] { "Q_W" }, r.Superseded);
    }

    [Fact]
    public void SupersededUsesTheCanonicalTagName()
    {
        var r = Apply(Call(IrExpr.Const("1"), V("q_w"), null, "q_w"), Feedback());
        Assert.Equal(new[] { "Q_W" }, r.Superseded);
    }

    [Fact]
    public void UndecidedAbandonmentsDoNotSupersede()
    {
        // 算不清的放弃（不透明、调用方局部、未接线配置输入）不证伪
        var opaque = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True, IrExpr.Or([Opq(), V("I_FwdPos1")])));
        Assert.Empty(ApplyOn(opaque, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2")).Superseded);
        var local = ApplyFull(IrBlockTests.Valve(), Call(IrExpr.Const("1"), IrExpr.Or([V("S1"), V("Local.Bypass")]), null, "Q_W"),
            NoLocals, Set("Local"), Unproven(Feedback()));
        Assert.Empty(local.Superseded);
        Assert.Empty(Apply(Call(null, V("Q_W"), null, "Q_W"), Feedback()).Superseded);
    }

    [Fact]
    public void AbandonmentBeforeTheOutputIsAUniqueQDoesNotSupersede()
        => Assert.Empty(Apply(Call(IrExpr.Const("1"), V("DB_X.Y"), null, "DB_X.Y"), Feedback()).Superseded);

    [Fact]
    public void ProducedFeedbackDoesNotSupersede()
        => Assert.Empty(Apply(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), Feedback()).Superseded);

    // ---- 迭代 4 修正 G：反馈被放弃时不产超时报警 ----

    [Fact]
    public void TimeoutAlarmIsNotProducedWhenTheFeedbackWasAbandoned()
    {
        var r = Apply(Call(IrExpr.Const("1"), V("Q_W"), null, "Q_W"), Feedback(), TimeoutAlarm("Cmd.IWP"));
        Assert.Empty(r.Alarms);
        Assert.Contains("AlarmWithoutPositionPoints", r.Notes);
    }

    // ---- 迭代 4 修正 H：报警输出引脚未接线时按实例全局路径 ----

    static IrCall WithoutAlarmPin(IrCall call, string instance) =>
        call with { Instance = instance, Pins = call.Pins.Where(p => p.Name != "A_Word").ToArray() };

    static CallSiteResult ApplyAlarm(IrCall call, IReadOnlySet<string> callerFormals, IReadOnlySet<string> callerLocals, string? callerGlobalInstance) =>
        CallSiteSubstitution.Apply(Contract(Feedback(), TimeoutAlarm("Cmd.IWP")), IrBlockTests.Valve(), call,
            callerFormals, callerLocals, Tags, "ck", "m1", callerGlobalInstance);

    [Fact]
    public void UnwiredAlarmOfAGlobalInstanceUsesTheInstancePath()
    {
        var call = WithoutAlarmPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), "IDB_V");
        var a = Assert.Single(ApplyAlarm(call, NoLocals, NoLocals, null).Alarms);
        Assert.Equal("IDB_V.A_Word.Alarm1", a.AlarmSymbol);
        Assert.Equal(new[] { "S1" }, a.Feedback);
    }

    [Fact]
    public void AlarmPinWithoutTargetsCountsAsUnwired()
    {
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), new IrPin("A_Word", "Output", "\"UDT_Alarm\"", null, [])) with { Instance = "IDB_V" };
        Assert.Equal("IDB_V.A_Word.Alarm1", Assert.Single(ApplyAlarm(call, NoLocals, NoLocals, null).Alarms).AlarmSymbol);
    }

    [Fact]
    public void UnwiredAlarmOfAMultiInstanceUsesTheCallersUniqueGlobalInstance()
    {
        var call = WithoutAlarmPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), "Valve[3]");
        var a = Assert.Single(ApplyAlarm(call, NoLocals, Set("Valve"), "IDB121_Cylinder").Alarms);
        Assert.Equal("IDB121_Cylinder.Valve[3].A_Word.Alarm1", a.AlarmSymbol);
    }

    [Fact]
    public void UnwiredAlarmOfAMultiInstanceWithoutAUniqueCallerInstanceIsNotProduced()
    {
        var call = WithoutAlarmPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), "Valve[3]");
        var r = ApplyAlarm(call, NoLocals, Set("Valve"), null);
        Assert.Empty(r.Alarms);
        Assert.Contains("AlarmUnwired", r.Notes);
    }

    [Theory]
    [InlineData("")]          // 无实例
    [InlineData("I_Inst")]    // 实例是调用方形参
    public void UnwiredAlarmWithoutAResolvableInstanceIsNotProduced(string instance)
    {
        var call = WithoutAlarmPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), instance);
        var r = ApplyAlarm(call, Set("I_Inst"), NoLocals, "IDB_X");
        Assert.Empty(r.Alarms);
        Assert.Contains("AlarmUnwired", r.Notes);
    }

    [Fact]
    public void NegatedOrQuestionMarkAlarmPinIsStillUnwired()
    {
        var q = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), new IrPin("A_Word", "Output", "\"UDT_Alarm\"", null, ["?"])) with { Instance = "IDB_V" };
        Assert.Empty(ApplyAlarm(q, NoLocals, NoLocals, null).Alarms);
        var neg = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), new IrPin("A_Word", "Output", "\"UDT_Alarm\"", null, [], Negated: true)) with { Instance = "IDB_V" };
        Assert.Empty(ApplyAlarm(neg, NoLocals, NoLocals, null).Alarms);
    }

    // ---- 迭代 4 审查修正 ----

    // Important-1：算不清的放弃不证伪

    [Fact]
    public void DuplicateTagAloneInATermIsAmbiguousAndDoesNotSupersede()
    {
        // S9 在点表里出现两次（大小写变体）：认不出是哪个点，不是「到位不依赖传感器」
        PipelineTag[] tags = [.. Tags, new("S9", "%I5.0", "Bool"), new("s9", "%I5.1", "Bool")];
        var r = CallSiteSubstitution.Apply(Contract(Feedback()), IrBlockTests.Valve(), Call(IrExpr.Const("1"), V("S9"), null, "Q_W"),
            NoLocals, NoLocals, tags, "ck", "m1");
        Assert.Empty(r.Rules);
        Assert.Contains("AmbiguousTagBranch", r.Notes);
        Assert.DoesNotContain("NoSensorBranch", r.Notes);
        Assert.Empty(r.Superseded);
    }

    [Fact]
    public void AlwaysInPositionFromAnInterfaceStartValueDoesNotSupersede()
    {
        // I_FwdPos1 未接线、接口初值 TRUE：背景实例里的实际值可能被改写，算不清
        var b = IrBlockTests.Valve();
        var block = IrBlock.FromUnits("FB_V", b.Units,
            b.Interface.Select(m => m.Name == "I_FwdPos1" ? m with { StartValue = "TRUE" } : m).ToArray());
        var r = ApplyOn(block, Call(IrExpr.Const("1"), null, null, "Q_W"), null, FeedbackOnFormal("I_FwdPos1"));
        Assert.Empty(r.Rules);
        Assert.Contains("AlwaysInPosition", r.Notes);
        Assert.Empty(r.Superseded);
    }

    [Fact]
    public void AlternativePathThatNeedsAStartValueDoesNotSupersede()
    {
        // 到位定义 = (Q ∧ I_FwdPos2) ∨ I_FwdPos1：I_FwdPos2 未接线、初值 TRUE → 代入后第一项只剩 Q；
        // 把初值换成「不知道」后这一项不再算得清 → 不证伪
        var b = IrBlockTests.Valve();
        var iface = b.Interface.Select(m => m.Name == "I_FwdPos2" ? m with { StartValue = "TRUE" } : m).ToArray();
        var block = IrBlock.FromUnits("FB_V", [.. b.Units, new IrUnit(9, "FB_V", 9, "LAD", "FlgNet",
            [new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
                IrExpr.Or([IrExpr.And([V("Cmd.QFwd"), V("I_FwdPos2")]), V("I_FwdPos1")]))], [], [])], iface);
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Contains("AlternativePathViaOutput", r.Notes);
        Assert.Empty(r.Superseded);
    }

    [Fact]
    public void StartValueOnlyInAnotherTermStillSupersedes()
    {
        // 到位定义 = Q ∨ (I_FwdPos2 ∧ I_FwdPos1)：I_FwdPos2 未接线（初值 FALSE）只影响另一项；输出自身单独成项不依赖初值 → 证伪
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True,
            IrExpr.Or([V("Cmd.QFwd"), IrExpr.And([V("I_FwdPos2"), V("I_FwdPos1")])])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Contains("AlternativePathViaOutput", r.Notes);
        Assert.Equal(new[] { "Q_W" }, r.Superseded);
    }

    public static TheoryData<IrExpr> UncertainAlternativeTerms() => new()
    {
        IrExpr.And([IrExpr.Var("Cmd.QFwd"), IrExpr.Not(IrExpr.Var("Cmd.FwdErr"))]),                                     // 内部变量（无唯一定义）
        IrExpr.And([IrExpr.Var("Cmd.QFwd"), new IrExpr(IrKinds.Cmp, ">", [IrExpr.Var("Cmd.Cnt"), IrExpr.Const("3")])]),  // 不透明
    };

    [Theory]
    [MemberData(nameof(UncertainAlternativeTerms))]
    public void AlternativePathWithAnUncertainLiteralDoesNotSupersede(IrExpr term)
    {
        var block = ValveWith(new IrStatement("FB_V/9/1", IrStatementKinds.Assign, "Cmd.Pos2", IrExpr.True, IrExpr.Or([term, V("I_FwdPos1")])));
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, FeedbackOn("Cmd.Pos2"));
        Assert.Contains("AlternativePathViaOutput", r.Notes);
        Assert.Empty(r.Superseded);
    }

    [Fact]
    public void AlternativePathWithACallerGlobalActualStillSupersedes()
    {
        // 放弃项里的文字：输出自身 + 调用方全局实参（非点表）→ 算得清
        var r = Apply(Call(IrExpr.Const("1"), IrExpr.And([V("Q_W"), V("DB_User.Mode")]), null, "Q_W"), Feedback());
        Assert.Contains("AlternativePathViaOutput", r.Notes);
        Assert.Equal(new[] { "Q_W" }, r.Superseded);
    }

    // Important-2：派生矛盾的报警位换成调用方的写法

    static AiContractRelation Contradiction(string? alarm) =>
        new("contradiction", [], ["I_FwdPos1", "I_FwdPos2"], null, null, null, alarm, ["FB_V/5/1"], null);

    static IrCall ContraCall(string instance, IrPin? alarmPin) =>
        new("FB_V", "FB", instance, IrExpr.True,
        [
            In("I_FwdPos1", "Bool", V("P1")), In("I_FwdPos2", "Bool", V("P2")),
            .. alarmPin is null ? Array.Empty<IrPin>() : [alarmPin]
        ]);

    /// <summary>第 0 层的矛盾只可能是校验器接受的、第一期已证明的那种（Proven=true）：夹具直接取校验器的真实输出。</summary>
    static ValidatedFbContract ProvenContradiction(string? alarm)
    {
        // 置位目标与 TON 预设按第一期推断的形态手写（A_Word.Alarm1 在本夹具里是唯一赋值写入的 Output 成员）
        var proven = new FbContract("FB_V", [], [], [new FbContractContradiction("I_FwdPos1", "I_FwdPos2", ["FB_V/5/1"], "A_Word.Alarm1", "T#2S")]);
        var validated = FbContractValidator.Validate(new AiFbContract("FB_V", "", [Contradiction(alarm)]), IrBlockTests.Valve(), proven);
        Assert.True(Assert.Single(validated.Accepted).Proven);
        return validated;
    }

    static DerivedRelation DeriveContradiction(IrCall call, IReadOnlySet<string> statics, params string[] extraFormals) =>
        Assert.Single(CallSiteSubstitution.Apply(ProvenContradiction("A_Word.Alarm1"), IrBlockTests.Valve(), call,
            Set(["P1", "P2", .. extraFormals]), Set([.. statics]), Tags, "ck", "m1", null, statics).Derived);

    [Fact]
    public void DerivedContradictionAlarmOfAnUnwiredMultiInstanceIsRelativeToTheCallerInstance()
    {
        var d = DeriveContradiction(ContraCall("Valve[3]", null), Set("Valve"));
        Assert.Equal(new[] { "P1", "P2" }, d.InCallerTerms.Inputs);
        Assert.Equal("Valve[3].A_Word.Alarm1", d.InCallerTerms.Alarm);
    }

    [Fact]
    public void DerivedContradictionAlarmWiredToACallerFormalUsesThatFormal()
    {
        var d = DeriveContradiction(ContraCall("Valve[3]", new IrPin("A_Word", "Output", "\"UDT_Alarm\"", null, ["MyAlarm"])), Set("Valve"), "MyAlarm");
        Assert.Equal("MyAlarm.Alarm1", d.InCallerTerms.Alarm);
    }

    [Fact]
    public void DerivedContradictionAlarmOfAGlobalInstanceIsTheGlobalPath()
        => Assert.Equal("IDB_V.A_Word.Alarm1", DeriveContradiction(ContraCall("IDB_V", null), Set()).InCallerTerms.Alarm);

    [Fact]
    public void DerivedContradictionAlarmThatCannotBeExpressedIsDropped()
    {
        // 多重背景但调用方 Static 不明 / 报警接到调用方 Temp：关系照样派生，只是不带报警
        Assert.Null(DeriveContradiction(ContraCall("Valve[3]", null) with { InstanceScope = "LocalVariable" }, Set()).InCallerTerms.Alarm);
        var temp = CallSiteSubstitution.Apply(ProvenContradiction("A_Word.Alarm1"), IrBlockTests.Valve(),
            ContraCall("IDB_V", new IrPin("A_Word", "Output", "\"UDT_Alarm\"", null, ["Tmp.A"])),
            Set("P1", "P2"), Set("Tmp"), Tags, "ck", "m1", null, Set());
        Assert.Null(Assert.Single(temp.Derived).InCallerTerms.Alarm);
    }

    [Fact]
    public void AlarmRootedInACalleeStaticUsesTheInstancePath()
    {
        // 外层调用点：被调块（中间块）的派生报警以它的 Static 成员开头 → 值在它的背景实例里
        var b = IrBlockTests.Valve();
        var mid = IrBlock.FromUnits("FB_Mid", b.Units, [.. b.Interface, new IfaceMember("Static", "Valve", "\"FB_V\"", null, null, [])]);
        var contra = new AiContractRelation("contradiction", [], ["I_FwdPos1", "I_FwdPos2"], null, null, null, "Valve[3].A_Word.Alarm1", ["FB_V/5/1"], null);
        var call = new IrCall("FB_Mid", "FB", "IDB_Mid", IrExpr.True, [In("I_FwdPos1", "Bool", V("S1")), In("I_FwdPos2", "Bool", V("S2"))]);
        var r = CallSiteSubstitution.Apply(Contract(contra), mid, call, NoLocals, NoLocals, Tags, "ck", "m1");
        Assert.Equal("IDB_Mid.Valve[3].A_Word.Alarm1", Assert.Single(r.Alarms).AlarmSymbol);
    }

    // 新增：超时报警带上同一反馈的旁路

    [Fact]
    public void TimeoutAlarmCarriesTheBypassOfItsFeedback()
    {
        var pos1 = IrExpr.Or([V("S1"), V("DB_User.ByPass[5]")]);
        var r = Apply(Call(IrExpr.Const("1"), pos1, null, "Q_W"), Feedback(), TimeoutAlarm("Cmd.IWP"));
        var a = Assert.Single(r.Alarms);
        Assert.Equal(new[] { "DB_User.ByPass[5]" }, a.Bypass);
        Assert.Equal(Assert.Single(r.Rules).BypassSignals, a.Bypass);
    }

    [Fact]
    public void TimeoutAlarmWithoutBypassHasAnEmptyBypass()
        => Assert.Empty(Assert.Single(Apply(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), Feedback(), TimeoutAlarm("Cmd.IWP")).Alarms).Bypass);

    // Minor-1：实例作用域（FlgNet 的 Instance Scope）与调用方接口不一致时不拼路径

    [Theory]
    [InlineData("GlobalVariable", "Valve")]   // 声称全局，根段却是调用方 Static
    [InlineData("LocalVariable", "")]         // 声称多重背景，调用方接口里却没有
    public void InstanceScopeThatContradictsTheCallerInterfaceIsNotProduced(string scope, string callerStatic)
    {
        var call = WithoutAlarmPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), "Valve[3]") with { InstanceScope = scope };
        var locals = callerStatic.Length == 0 ? NoLocals : Set(callerStatic);
        Assert.Empty(ApplyAlarm(call, NoLocals, locals, "IDB121_Cylinder").Alarms);
    }

    [Fact]
    public void MultiInstanceInsideACallerStaticStructUsesTheFullPath()
    {
        var call = WithoutAlarmPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), "Stat.Valve[2]") with { InstanceScope = "LocalVariable" };
        Assert.Equal("IDB_X.Stat.Valve[2].A_Word.Alarm1", Assert.Single(ApplyAlarm(call, NoLocals, Set("Stat"), "IDB_X").Alarms).AlarmSymbol);
    }

    // ---- 迭代 4 修正 2：调用方里以「本次调用实例.输出形参」写出的文字就是该输出自身 ----
    // 真实例子：FB320_OP30Manual 网络 10，IWP ← CYL05_W ∨ (Valve[5].QFwd ∧ ¬CYL05_H) ∨ ByPass，Valve[5] 是本次调用的多重背景

    static IrCall InstanceCall(string instance, IrExpr pos1) =>
        Call(IrExpr.Const("1"), pos1, null, "Q_W") with { Instance = instance };

    static IrExpr ThroughOwnOutput(string self) =>
        IrExpr.Or([V("S1"), IrExpr.And([V(self), IrExpr.Not(V("S2"))]), V("DB_User.ByPass[9]")]);

    [Theory]
    [InlineData("Valve[5]", "Valve[5].O_FwdOut")]        // 多重背景实例的输出形参
    [InlineData("Valve[5]", "valve[5].o_fwdout")]        // 忽略大小写
    [InlineData("Valve[5]", "Valve[5].Cmd.QFwd")]        // 代表输出的内部变量（本块写入、输出直接读到）
    [InlineData("Stat.Valve[2]", "Stat.Valve[2].O_FwdOut")]
    public void OwnInstanceOutputReadByTheCallerIsTheOutputItself(string instance, string self)
    {
        var r = ApplyFull(IrBlockTests.Valve(), InstanceCall(instance, ThroughOwnOutput(self)), NoLocals, Set(instance.Split('.', '[')[0]), Unproven(Feedback()));
        Assert.Empty(r.Rules);
        Assert.Contains("AlternativePathViaOutput", r.Notes);
        Assert.DoesNotContain("ActualIsCallerLocal", r.Notes);
        Assert.Equal(new[] { "Q_W" }, r.Superseded);
    }

    [Fact]
    public void OwnGlobalInstanceOutputIsTheOutputItself()
    {
        var r = ApplyFull(IrBlockTests.Valve(), InstanceCall("IDB_V", ThroughOwnOutput("IDB_V.O_FwdOut")), NoLocals, NoLocals, Unproven(Feedback()));
        Assert.Contains("AlternativePathViaOutput", r.Notes);
        Assert.Equal(new[] { "Q_W" }, r.Superseded);
    }

    [Theory]
    [InlineData("Valve[6].O_FwdOut", "ActualIsCallerLocal")]   // 另一个实例的同名成员
    [InlineData("Valve[5].O_BkdOut", "ActualIsCallerLocal")]   // 本实例的另一个输出
    [InlineData("Valve[5].I_FwdCmd", "ActualIsCallerLocal")]   // 本实例的输入
    [InlineData("Valve[55].O_FwdOut", "ActualIsCallerLocal")]  // 前缀相似
    public void OtherInstanceMembersAreStillCallerLocals(string member, string note)
    {
        var r = ApplyFull(IrBlockTests.Valve(), InstanceCall("Valve[5]", ThroughOwnOutput(member)), NoLocals, Set("Valve"), Unproven(Feedback()));
        Assert.Empty(r.Rules);
        Assert.Contains(note, r.Notes);
        Assert.DoesNotContain("AlternativePathViaOutput", r.Notes);
        Assert.Empty(r.Superseded);
    }

    [Fact]
    public void OwnInstanceOutputBesideASensorStaysInTheSensorGroup()
    {
        var pos1 = IrExpr.Or([IrExpr.And([V("Valve[5].O_FwdOut"), V("S1")]), V("S2")]);
        var r = ApplyFull(IrBlockTests.Valve(), InstanceCall("Valve[5]", pos1), NoLocals, Set("Valve"), Unproven(Feedback()));
        Assert.All(r.Rules, x => Assert.Equal("IMPLIES", x.Operator));
        Assert.Equal(new[] { "S1", "S2" }, Groups(r));
    }

    [Fact]
    public void InstanceThatIsACallerFormalDoesNotMakeAnOutputSelf()
    {
        var r = ApplyFull(IrBlockTests.Valve(), InstanceCall("InstParam", ThroughOwnOutput("InstParam.O_FwdOut")), Set("InstParam"), NoLocals, Unproven(Feedback()));
        Assert.DoesNotContain("AlternativePathViaOutput", r.Notes);
        Assert.Empty(r.Superseded);
    }

    // ---- 整分支审查 I-1：已证明的到位矛盾（校验器真实输出）照样出报警、照样派生 ----

    static IrCall GlobalContraCall(IrPin? alarmPin) =>
        new("FB_V", "FB", "IDB_V", IrExpr.True,
        [
            In("I_FwdPos1", "Bool", V("S1")), In("I_FwdPos2", "Bool", V("S2")),
            .. alarmPin is null ? Array.Empty<IrPin>() : [alarmPin]
        ]) { InstanceScope = "GlobalVariable" };

    [Fact]
    public void ProvenContradictionWithGlobalActualsYieldsOnlyThePositionConflictAlarm()
    {
        // 第一期已在本调用点产过 AT_MOST_ONE：不再产规则；报警位接了线 → 照样产到位矛盾报警
        var r = CallSiteSubstitution.Apply(ProvenContradiction("A_Word.Alarm1"), IrBlockTests.Valve(),
            GlobalContraCall(new IrPin("A_Word", "Output", "\"UDT_Alarm\"", null, ["DB_Alarm.Cyl[1]"])), Set(), NoLocals, Tags, "ck", "m1");
        Assert.Empty(r.Rules);
        Assert.Empty(r.Derived);
        var a = Assert.Single(r.Alarms);
        Assert.Equal("PositionConflict", a.Kind);
        Assert.Equal("DB_Alarm.Cyl[1].Alarm1", a.AlarmSymbol);
        Assert.Equal(new[] { "S1", "S2" }, a.Feedback);
        Assert.Equal("", a.Output);
        Assert.Equal("T#2S", a.Delay);   // 程序里的 TON 预设，不是 AI 给的数
    }

    [Fact]
    public void ProvenContradictionWithAnUnwiredAlarmUsesTheGlobalInstancePath()
        => Assert.Equal("IDB_V.A_Word.Alarm1", Assert.Single(CallSiteSubstitution.Apply(ProvenContradiction("A_Word.Alarm1"),
            IrBlockTests.Valve(), GlobalContraCall(null), Set(), NoLocals, Tags, "ck", "m1").Alarms).AlarmSymbol);

    [Fact]
    public void ProvenContradictionWithoutAlarmYieldsNothingAtGlobalActuals()
    {
        var r = CallSiteSubstitution.Apply(ProvenContradiction(null), IrBlockTests.Valve(), GlobalContraCall(null), Set(), NoLocals, Tags, "ck", "m1");
        Assert.Empty(r.Rules);
        Assert.Empty(r.Alarms);
        Assert.Empty(r.Derived);
    }

    [Fact]
    public void ProvenContradictionWiredToCallerFormalsIsDerived()
    {
        var d = DeriveContradiction(ContraCall("IDB_V", null), Set());
        Assert.Equal("contradiction", d.Kind);
        Assert.Equal(new[] { "P1", "P2" }, d.InCallerTerms.Inputs);
    }

    [Fact]
    public void DerivedContradictionWhoseInputIsWrittenInTheMiddleBlockIsNotProduced()
    {
        // 派生层（Proven=false）：中间块对输入本身有写入 → 读到的不一定是实参（父/子重叠写入由 IrBlockTests 覆盖）
        var b = IrBlockTests.Valve();
        var write = new IrUnit(9, "FB_V", 9, "LAD", "FlgNet",
            [new("FB_V/9/1", IrStatementKinds.Assign, "I_FwdPos1", IrExpr.True, V("Cmd.Flag"))], [], []);
        var block = IrBlock.FromUnits("FB_V", [.. b.Units, write], b.Interface);
        var r = CallSiteSubstitution.Apply(Contract(Contradiction("A_Word.Alarm1")), block, GlobalContraCall(null), Set(), NoLocals, Tags, "ck", "m1");
        Assert.Empty(r.Rules);
        Assert.Empty(r.Alarms);
        Assert.Contains("ContradictionInputWrittenInBody", r.Notes);
    }

    // ---- 整分支审查 I-4：到位变量的父结构被整体写入 → 没有唯一定义，不产 ----

    [Fact]
    public void FeedbackWhosePositionVarParentIsWrittenWholeIsNotProduced()
    {
        var v = IrBlockTests.Valve();
        var call = new IrStatement("FB_V/9/1", IrStatementKinds.Call, null, IrExpr.True, null,
            new IrCall("FB_Sub", "FB", "Sub", IrExpr.True, [new IrPin("Out", "Output", "Struct", null, ["Cmd"])]));
        var block = IrBlock.FromUnits("FB_V", [.. v.Units, new IrUnit(9, "FB_V", 9, "LAD", "FlgNet", [call], [], [])], v.Interface);
        var r = ApplyOn(block, Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), null, Feedback());
        Assert.Empty(r.Rules);
        Assert.Contains("PositionVarNoUniqueDefinition", r.Notes);
    }

    // ---- 整分支复审 N-1：AI 指错报警位时不出报警（关系保留） ----

    [Fact]
    public void ProvenContradictionWithAWrongAlarmYieldsNoAlarm()
    {
        var proven = new FbContract("FB_V", [], [], [new FbContractContradiction("I_FwdPos1", "I_FwdPos2", ["FB_V/5/1"], "A_Word.Alarm1", null)]);
        var validated = FbContractValidator.Validate(new AiFbContract("FB_V", "", [Contradiction("O_FwdOut")]), IrBlockTests.Valve(), proven);
        Assert.True(Assert.Single(validated.Accepted).Proven);
        var r = CallSiteSubstitution.Apply(validated, IrBlockTests.Valve(), GlobalContraCall(null), Set(), NoLocals, Tags, "ck", "m1");
        Assert.Empty(r.Alarms);
        Assert.Empty(r.Rules);
    }

    // ---- 已校验的超时报警派生到位反馈：代入结果与 AI 给出的等价 feedback 相同 ----

    static string Shape(PipelineRule r) => string.Join("|", r.Operator, r.Description, r.Applicability, r.RequiresSettlingEvidence,
        r.Proof, string.Join(",", r.Signals.Select(s => s.Role + ":" + s.Name)), string.Join(",", r.BypassSignals),
        r.TargetGroups is null ? "-" : string.Join(";", r.TargetGroups.Select(g => string.Join("&", g))), r.Key);

    static string Shape(CallSiteAlarm a) => string.Join("|", a.AlarmSymbol, a.Kind, a.Output, string.Join(",", a.Feedback), a.Delay,
        string.Join(",", a.Bypass), string.Join(";", a.FeedbackGroups.Select(g => string.Join("&", g))));

    public static TheoryData<string> DerivedFeedbackWirings() => new() { "single", "any" };

    [Theory]
    [MemberData(nameof(DerivedFeedbackWirings))]
    public void FeedbackDerivedFromATimeoutAlarmSubstitutesLikeTheEquivalentFeedback(string wiring)
    {
        // single：一个传感器 → IMPLIES；any：到位输入接 (S1 AND S2) OR S3 → 每组一条 IMPLIES
        var call = wiring == "single"
            ? Call(IrExpr.Const("1"), V("S1"), null, "Q_W")
            : Call(IrExpr.Const("1"), IrExpr.Or([IrExpr.And([V("S1"), V("S2")]), V("S3")]), null, "Q_W");
        ValidatedFbContract Validated(params AiContractRelation[] rs) =>
            FbContractValidator.Validate(new AiFbContract("FB_V", "", rs), IrBlockTests.Valve(), FbContract.Empty("FB_V"));
        var alarm = TimeoutAlarm("Cmd.IWP");
        var onlyAlarm = CallSiteSubstitution.Apply(Validated(alarm), IrBlockTests.Valve(), call, Set(), NoLocals, Tags, "ck", "m1");
        var withFeedback = CallSiteSubstitution.Apply(Validated(Feedback(), alarm), IrBlockTests.Valve(), call, Set(), NoLocals, Tags, "ck", "m1");

        Assert.Equal(wiring == "single" ? 1 : 2, onlyAlarm.Rules.Count);
        Assert.All(onlyAlarm.Rules, x => Assert.Equal("IMPLIES", x.Operator));
        var rule = onlyAlarm.Rules[0];
        Assert.Equal(withFeedback.Rules.Select(Shape).ToArray(), onlyAlarm.Rules.Select(Shape).ToArray());
        Assert.Equal(withFeedback.Alarms.Select(Shape).ToArray(), onlyAlarm.Alarms.Select(Shape).ToArray());
        Assert.Single(onlyAlarm.Alarms);
        // 派生来源只在追溯与 Notes 里体现
        Assert.Equal(FbContractValidator.DerivedFeedbackText, rule.Trace!.Note);
        Assert.All(withFeedback.Rules, x => Assert.Null(x.Trace!.Note));
        Assert.Contains("FeedbackDerivedFromTimeoutAlarm", onlyAlarm.Notes);
        Assert.DoesNotContain("FeedbackDerivedFromTimeoutAlarm", withFeedback.Notes);
        Assert.DoesNotContain("DuplicateFeedback", withFeedback.Notes);
    }

    static ValidatedFbContract ValidatedOnValve(params AiContractRelation[] rs) =>
        FbContractValidator.Validate(new AiFbContract("FB_V", "", rs), IrBlockTests.Valve(), FbContract.Empty("FB_V"));

    static CallSiteResult ApplyValidated(IrCall call, ValidatedFbContract contract) =>
        CallSiteSubstitution.Apply(contract, IrBlockTests.Valve(), call, Set(), NoLocals, Tags, "ck", "m1");

    [Theory]
    [InlineData("O_FwdOut")]
    [InlineData("Cmd.QFwd")]
    public void TimeoutAlarmOnTheOutputItselfProducesNothing(string pos)
    {
        // 到位变量与输出同源：报警被拒 → 不派生反馈、不产报警位、不证伪；手动按钮直接接命令（I_FwdCmd = %I）时也不产错规则
        var call = WithPin(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), In("I_FwdCmd", "Bool", V("S3")));
        var r = ApplyValidated(call, ValidatedOnValve(TimeoutAlarm(pos)));
        Assert.Empty(r.Rules);
        Assert.Empty(r.Alarms);
        Assert.Empty(r.Superseded);
    }

    [Fact]
    public void DerivedFeedbackNeverSupersedesExistingRules()
    {
        // 到位输入接另一个输出 Q_H：到位不依赖传感器。AI 给的反馈据此证伪旧规则；由报警派生的反馈只产新规则，不证伪
        // （接 %M 位时是 MemoryOnlyBranch，本来就不证伪，测不出派生路径的差别）
        var call = Call(IrExpr.Const("1"), V("Q_H"), null, "Q_W");
        var ai = ApplyValidated(call, ValidatedOnValve(Feedback(), TimeoutAlarm("Cmd.IWP")));
        Assert.Equal(new[] { "Q_W" }, ai.Superseded);
        var derived = ApplyValidated(call, ValidatedOnValve(TimeoutAlarm("Cmd.IWP")));
        Assert.Contains("NoSensorBranch", derived.Notes);
        Assert.Contains("FeedbackDerivedFromTimeoutAlarm", derived.Notes);
        Assert.Empty(derived.Superseded);
    }

    [Fact]
    public void DerivedFeedbackDescribesAnUncheckedAiDelayAsProgramSet()
    {
        var alarm = new AiContractRelation("timeoutAlarm", ["O_FwdOut"], [], "Cmd.IWP", "7", 7, "A_Word.Alarm1", ["FB_V/5/3"], null);
        var rule = Assert.Single(ApplyValidated(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), ValidatedOnValve(alarm)).Rules);
        Assert.Contains("动作时间 程序设定", rule.Description);
        // 形参延时按调用实参常数解析
        var wired = Assert.Single(ApplyValidated(Call(IrExpr.Const("1"), V("S1"), null, "Q_W"), ValidatedOnValve(TimeoutAlarm("Cmd.IWP"))).Rules);
        Assert.Contains("动作时间 10.0", wired.Description);
    }
}
