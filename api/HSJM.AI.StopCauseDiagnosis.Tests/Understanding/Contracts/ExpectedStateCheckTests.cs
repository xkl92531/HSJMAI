#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

/// <summary>K4：期望状态报警（6.2 FB3007_Sensor 的结构：Signal 经 TON/TOF 滤波为 SignalOn；Signal_ON/Signal_OFF 决定 Status.Sensor；
/// CheckOffAlarm := TON((Status.Sensor == 2) ∧ SignalOn, DelayDiag)，CheckOnAlarm := TON((Status.Sensor == 1) ∧ ¬SignalOn, DelayDiag)）。</summary>
public class ExpectedStateCheckTests
{
    static IrExpr V(string n) => IrExpr.Var(n);
    static IrExpr Cmp(string var, string value) => new(IrKinds.Cmp, "==", [V(var), IrExpr.Const(value)]);
    internal static IrExpr Timer(string kind, string instance, IrExpr input, IrExpr pt) =>
        new(IrKinds.Timer, $"{kind}|{instance}|Q", [new IrExpr(IrKinds.Port, "IN", [input]), new IrExpr(IrKinds.Port, "PT", [pt])]);

    /// <param name="offCondition">CheckOffAlarm 的置位条件（默认 TON((Status.Sensor == 2) ∧ SignalOn, DelayDiag)）。</param>
    /// <param name="extra">附加到网络 1 的语句（如额外写入）。</param>
    internal static IrBlock Sensor(IrExpr? offCondition = null, params IrStatement[] extra)
    {
        var filtered = Timer("TOF", "Timer.DelayOff", Timer("TON", "Timer.DelayOn", V("Signal"), V("DelayOn")), V("DelayOff"));
        var u1 = new IrUnit(1, "FB_S", 1, "LAD", "FlgNet",
        [
            new("FB_S/1/1", IrStatementKinds.Assign, "SignalOn", IrExpr.True,
                IrExpr.Or([IrExpr.And([filtered, IrExpr.Not(V("CheckOnAlarm")), IrExpr.Not(V("CheckOffAlarm")), Cmp("DiagAlarmID", "0")]),
                    IrExpr.And([filtered, IrExpr.Const("1")])])),
            new("FB_S/1/2", IrStatementKinds.Assign, "Status.Sensor", IrExpr.And([V("Signal_ON"), IrExpr.Not(V("Signal_OFF"))]), IrExpr.Const("1")),
            new("FB_S/1/3", IrStatementKinds.Assign, "Status.Sensor", IrExpr.And([V("Signal_OFF"), IrExpr.Not(V("Signal_ON"))]), IrExpr.Const("2")),
            new("FB_S/1/4", IrStatementKinds.Assign, "Status.Sensor", IrExpr.And([IrExpr.Not(V("Signal_ON")), IrExpr.Not(V("Signal_OFF"))]), IrExpr.Const("0")),
            .. extra
        ], [], []);
        var u2 = new IrUnit(2, "FB_S", 2, "LAD", "FlgNet",
        [
            new("FB_S/2/1", IrStatementKinds.Set, "CheckOnAlarm",
                Timer("TON", "Timer.Delay[1]", IrExpr.And([Cmp("Status.Sensor", "1"), IrExpr.Not(V("SignalOn"))]), V("DelayDiag")), null),
        ], [], []);
        var u3 = new IrUnit(3, "FB_S", 3, "LAD", "FlgNet",
        [
            new("FB_S/3/1", IrStatementKinds.Set, "CheckOffAlarm",
                offCondition ?? Timer("TON", "Timer.Delay[2]", IrExpr.And([Cmp("Status.Sensor", "2"), V("SignalOn")]), V("DelayDiag")), null),
        ], [], []);
        var u4 = new IrUnit(4, "FB_S", 4, "LAD", "FlgNet",
        [
            new("FB_S/4/1", IrStatementKinds.Reset, "CheckOnAlarm", IrExpr.And([new IrExpr(IrKinds.Rise, null, [V("Reset")]), V("CheckOnAlarm")]), null),
            new("FB_S/4/2", IrStatementKinds.Reset, "CheckOffAlarm", IrExpr.And([new IrExpr(IrKinds.Rise, null, [V("Reset")]), V("CheckOffAlarm")]), null),
        ], [], []);
        var iface = new[]
        {
            new IfaceMember("Input", "Signal", "Bool", null, null, []),
            new IfaceMember("Input", "Signal_ON", "Bool", null, null, []),
            new IfaceMember("Input", "Signal_OFF", "Bool", null, null, []),
            new IfaceMember("Input", "Reset", "Bool", null, null, []),
            new IfaceMember("Input", "Enable", "Bool", null, null, []),
            new IfaceMember("Input", "DelayOn", "Time", "T#100ms", null, []),
            new IfaceMember("Input", "DelayOff", "Time", "T#100ms", null, []),
            new IfaceMember("Input", "DelayDiag", "Time", "T#5s", null, []),
            new IfaceMember("Output", "CheckOnAlarm", "Bool", null, null, []),
            new IfaceMember("Output", "CheckOffAlarm", "Bool", null, null, []),
            new IfaceMember("Output", "SignalOn", "Bool", null, null, []),
            new IfaceMember("Output", "DiagAlarmID", "DWord", null, null, []),
            new IfaceMember("Static", "Status", "Struct", null, null, []),
        };
        return IrBlock.FromUnits("FB_S", [u1, u2, u3, u4], iface);
    }

    static ExpectedStateCase Case(bool on, bool off, bool expected) =>
        new(new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase) { ["Signal_ON"] = on, ["Signal_OFF"] = off }, expected);

    internal static AiContractRelation Relation(string alarm, string? delay, params ExpectedStateCase[] cases) =>
        new("expectedStateAlarm", [], ["Signal"], null, delay, null, alarm, [alarm == "CheckOffAlarm" ? "FB_S/3/1" : "FB_S/2/1"], null)
        { ConfigInputs = ["Signal_ON", "Signal_OFF"], ExpectedStates = cases };

    static ValidatedRelation Validate(AiContractRelation r, IrBlock? b = null) =>
        Assert.Single(FbContractValidator.Validate(new AiFbContract("FB_S", "", [r]), b ?? Sensor(), FbContract.Empty("FB_S")).Relations);

    [Fact]
    public void OffAlarmWithOffExpectedIsAcceptedAndTakesTheProgramDelay()
    {
        var v = Validate(Relation("CheckOffAlarm", null, Case(false, true, false)));
        Assert.Null(v.RejectReason);
        Assert.Equal("DelayDiag", v.Relation.DelayParam);
        Assert.Single(v.Relation.ExpectedStates);
    }

    [Fact]
    public void OnAlarmWithOnExpectedIsAccepted()   // 报警只在 ¬SignalOn 时置位 → 期望 true
    {
        var v = Validate(Relation("CheckOnAlarm", "DelayDiag", Case(true, false, true)));
        Assert.Null(v.RejectReason);
    }

    [Fact]
    public void ExpectedValueThatContradictsTheProgramRejectsTheRelation()
    {
        Assert.Equal("ExpectedStateMismatch", Validate(Relation("CheckOffAlarm", null, Case(false, true, true))).RejectReason);
    }

    [Fact]
    public void CombinationUnderWhichTheAlarmCannotFireIsDropped()
    {
        Assert.Equal("ExpectedStateNeverFires", Validate(Relation("CheckOffAlarm", null, Case(true, false, true))).RejectReason);
        var v = Validate(Relation("CheckOffAlarm", null, Case(true, false, true), Case(false, true, false)));
        Assert.Null(v.RejectReason);
        Assert.False(Assert.Single(v.Relation.ExpectedStates).When["Signal_ON"]);
    }

    [Fact]
    public void CombinationLeavingTheStateVariableUnwrittenIsUndetermined()   // ON=OFF=true：三条写入都不执行，保持旧值
    {
        Assert.Equal("ExpectedStateUndetermined", Validate(Relation("CheckOffAlarm", null, Case(true, true, false))).RejectReason);
    }

    [Fact]
    public void StateVariableWithANonConstantWriteIsRejected()
    {
        var b = Sensor(null, new IrStatement("FB_S/1/5", IrStatementKinds.Assign, "Status.Sensor", V("Reset"), V("DiagAlarmID")));
        Assert.Equal("ExpectedStateExtraCondition", Validate(Relation("CheckOffAlarm", null, Case(false, true, false)), b).RejectReason);
    }

    // 审查 I-3：配置取定后 TON 输入须只依赖被监视输入（含其滤波定时器）；多出别的条件会丢前提，整条拒绝
    [Theory]
    [InlineData("DB_G.Mode")]                    // 全局量
    [InlineData("Status.Other")]                 // 本块别的状态
    public void TonInputWithAnExtraConditionIsRejected(string extra)
    {
        var b = Sensor(Timer("TON", "Timer.Delay[2]", IrExpr.And([Cmp("Status.Sensor", "2"), V("SignalOn"), V(extra)]), V("DelayDiag")));
        Assert.Equal("ExpectedStateExtraCondition", Validate(Relation("CheckOffAlarm", null, Case(false, true, false)), b).RejectReason);
    }

    /// <summary>TON 输入另含本块从未写入的 Bool Input 形参（PLC2 FB3007 的 NOT DryRunMode、审查例子里的 Enable）。</summary>
    internal static IrBlock SensorWithCondition(IrExpr condition) =>
        Sensor(Timer("TON", "Timer.Delay[2]", IrExpr.And([Cmp("Status.Sensor", "2"), V("SignalOn"), condition]), V("DelayDiag")));

    [Fact]
    public void ExtraConditionOnAnInputIsDeferredToTheCallSite()
    {
        // 别的条件只是本块从未写入的 Bool 输入：要在调用点取成常数后再判（见 ExpectedStateSubstitutionTests），这里记为条件输入
        var v = Validate(Relation("CheckOffAlarm", null, Case(false, true, false)), SensorWithCondition(IrExpr.Not(V("Enable"))));
        Assert.Null(v.RejectReason);
        Assert.Equal(new[] { "Enable" }, v.Relation.ConditionInputs);
    }

    [Fact]
    public void ConditionInputThatFlipsTheExpectationIsRejected()
    {
        // Enable 取不同值时期望值相反：说明 AI 的单一期望值不成立
        var flip = IrExpr.Or([IrExpr.And([V("SignalOn"), V("Enable")]), IrExpr.And([IrExpr.Not(V("SignalOn")), IrExpr.Not(V("Enable"))])]);
        var b = Sensor(Timer("TON", "Timer.Delay[2]", IrExpr.And([Cmp("Status.Sensor", "2"), flip]), V("DelayDiag")));
        Assert.Equal("ExpectedStateMismatch", Validate(Relation("CheckOffAlarm", null, Case(false, true, false)), b).RejectReason);
    }

    [Fact]
    public void TonInputWithAnotherTimerIsRejected()
    {
        var other = Timer("TON", "Timer.Other", V("Enable"), V("DelayOn"));
        var b = Sensor(Timer("TON", "Timer.Delay[2]", IrExpr.And([Cmp("Status.Sensor", "2"), V("SignalOn"), other]), V("DelayDiag")));
        Assert.Equal("ExpectedStateExtraCondition", Validate(Relation("CheckOffAlarm", null, Case(false, true, false)), b).RejectReason);
    }

    [Fact]
    public void AlarmNotSetThroughATonIsRejected()
    {
        var b = Sensor(IrExpr.And([Cmp("Status.Sensor", "2"), V("SignalOn")]));
        Assert.Equal("ExpectedStateAlarmNotTon", Validate(Relation("CheckOffAlarm", null, Case(false, true, false)), b).RejectReason);
    }

    [Fact]
    public void DelayOtherThanTheTonPresetIsRejected()
    {
        Assert.Equal("ExpectedStateDelayMismatch", Validate(Relation("CheckOffAlarm", "DelayOn", Case(false, true, false))).RejectReason);
    }

    [Fact]
    public void AlarmIndependentOfTheMonitoredInputIsRejected()
    {
        var b = Sensor(Timer("TON", "Timer.Delay[2]", IrExpr.And([Cmp("Status.Sensor", "2"), V("Reset")]), V("DelayDiag")));
        Assert.Equal("ExpectedStateExtraCondition", Validate(Relation("CheckOffAlarm", null, Case(false, true, false)), b).RejectReason);
    }

    [Fact]
    public void MonitoredInputMustBeAnUnwrittenBoolInput()
    {
        var r = Relation("CheckOffAlarm", null, Case(false, true, false)) with { Inputs = ["DelayDiag"] };
        Assert.Equal("ExpectedStateInputShape", Validate(r).RejectReason);
        var written = Sensor(null, new IrStatement("FB_S/1/5", IrStatementKinds.Assign, "Signal", IrExpr.True, IrExpr.True));
        Assert.Equal("ExpectedStateInputShape", Validate(Relation("CheckOffAlarm", null, Case(false, true, false)), written).RejectReason);
    }

    [Fact]
    public void CaseMustAssignEveryConfigInput()
    {
        var partial = new ExpectedStateCase(new Dictionary<string, bool> { ["Signal_OFF"] = true }, false);
        Assert.Equal("ExpectedStateCasesIncomplete", Validate(Relation("CheckOffAlarm", null, partial)).RejectReason);
    }

    [Theory]
    [InlineData("T#5s", 5.0)]
    [InlineData("T#500ms", 0.5)]
    [InlineData("T#1m30s", 90.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("DelayDiag", null)]
    public void DelayTextIsConvertedToSeconds(string text, double? seconds) => Assert.Equal(seconds, ExpectedStateCheck.Seconds(text));

    [Fact]
    public void ParserReadsTheExpectedStateFields()
    {
        var (status, contract, skipped) = AiFbContractParser.Parse("""
            {"fb":"FB_S","understanding":"x","relations":[{"kind":"expectedStateAlarm","alarm":"CheckOffAlarm","input":"Signal",
             "configInputs":["Signal_ON","Signal_OFF"],"expectedStates":[{"when":{"Signal_ON":false,"Signal_OFF":true},"expected":false}],
             "delayParam":"DelayDiag","evidence":["FB_S/3/1"]},
             {"kind":"expectedStateAlarm","alarm":"A","input":"B","expectedStates":[{"when":{"X":1},"expected":false}],"evidence":["FB_S/3/1"]}]}
            """);
        Assert.Equal(AiContractParseStatus.Parsed, status);
        Assert.Equal(1, skipped);   // when 的取值不是 true/false → 整条跳过
        var r = Assert.Single(contract!.Relations);
        Assert.Equal("expectedStateAlarm", r.Kind);
        Assert.Equal(new[] { "Signal" }, r.Inputs);
        Assert.Equal(new[] { "Signal_ON", "Signal_OFF" }, r.ConfigInputs);
        var c = Assert.Single(r.ExpectedStates);
        Assert.False(c.Expected);
        Assert.True(c.When["signal_off"]);
        Assert.Contains("expectedStateAlarm", FbContractPrompt.System);
    }
}
