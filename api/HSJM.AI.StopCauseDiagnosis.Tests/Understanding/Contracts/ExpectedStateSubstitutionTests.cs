#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

/// <summary>K4：期望状态报警在调用点代入（6.2 FB130_Sensor 网络 36 调 FB3007_Sensor 的结构）。</summary>
public class ExpectedStateSubstitutionTests
{
    static readonly PipelineTag[] Tags =
    [
        new("SQ40", "%I10.0", "Bool"), new("Dup", "%I10.1", "Bool"), new("Dup", "%I10.2", "Bool"), new("Q1", "%Q1.0", "Bool")
    ];

    static IrExpr V(string n) => IrExpr.Var(n);
    static IrPin In(string n, IrExpr? v, string type = "Bool") => new(n, "Input", type, v, []);

    static IrCall Call(IrExpr? signal, IrExpr? on, IrExpr? off, IrExpr? enable = null) =>
        new("FB_S", "FB", "Sensor[36]", IrExpr.Unwired(), [In("Signal", signal), In("Signal_ON", on), In("Signal_OFF", off), In("DelayDiag", null, "Time"),
            In("Enable", enable)]);

    // 审查 I-3：TON 输入另含 NOT Enable（Enable 是本块从未写入的 Bool 输入）——只有调用点把 Enable 取成常数且条件成立时才产
    [Theory]
    [InlineData(null, true)]      // 未接线：接口初值 false → NOT Enable 恒成立
    [InlineData("FALSE", true)]
    [InlineData("TRUE", false)]   // 报警永不置位：不产
    [InlineData("var", false)]    // 接动态变量：取不成常数，不产
    public void ConditionInputIsResolvedAtTheCallSite(string? enable, bool produced)
    {
        var relation = ExpectedStateCheckTests.Relation("CheckOffAlarm", null, Case(false, true, false));
        var callee = ExpectedStateCheckTests.SensorWithCondition(IrExpr.Not(V("Enable")));
        var contract = FbContractValidator.Validate(new AiFbContract("FB_S", "", [relation]), callee, FbContract.Empty("FB_S"));
        Assert.Single(contract.Accepted);
        var wiring = enable switch { null => null, "var" => V("DB_A.Enable"), _ => IrExpr.Const(enable) };
        var r = CallSiteSubstitution.Apply(contract, callee, Call(V("SQ40"), IrExpr.Const("FALSE"), IrExpr.Const("TRUE"), wiring),
            new HashSet<string>(), new HashSet<string>(), Tags, "ck", "m1", configConstant: _ => null);
        Assert.Equal(produced, r.Rules.Any(x => x.Operator == "MUST_BE_FALSE"));
    }

    static ValidatedFbContract Contract(AiContractRelation r) =>
        FbContractValidator.Validate(new AiFbContract("FB_S", "", [r]), ExpectedStateCheckTests.Sensor(), FbContract.Empty("FB_S"));

    static ExpectedStateCase Case(bool on, bool off, bool expected) =>
        new(new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase) { ["Signal_ON"] = on, ["Signal_OFF"] = off }, expected);

    static readonly AiContractRelation OffAlarm = ExpectedStateCheckTests.Relation("CheckOffAlarm", null, Case(false, true, false));

    static CallSiteResult Apply(IrCall call, AiContractRelation? r = null, System.Func<string, bool?>? constant = null)
    {
        var contract = Contract(r ?? OffAlarm);
        Assert.Single(contract.Accepted);
        return CallSiteSubstitution.Apply(contract, ExpectedStateCheckTests.Sensor(), call, new HashSet<string>(), new HashSet<string>(), Tags,
            "ck", "m1", configConstant: constant);
    }

    [Fact]
    public void ConstantConfigYieldsADelayedMustBeFalse()
    {
        var r = Apply(Call(V("SQ40"), IrExpr.Const("FALSE"), IrExpr.Const("TRUE")));
        var rule = Assert.Single(r.Rules);
        Assert.Equal("MUST_BE_FALSE", rule.Operator);
        Assert.Equal("SQ40", Assert.Single(rule.Signals).Name);
        Assert.Equal("Target", rule.Signals[0].Role);
        Assert.Equal("AfterSettling", rule.Applicability);
        Assert.True(rule.RequiresSettlingEvidence);
        Assert.Equal(UnderstandingProofs.AiFbContract, rule.Proof);
        Assert.Contains("T#5s", rule.Description);   // 延时取程序值：未接线取接口初值
        Assert.Equal("RequiresRuntimeGuard", Original353ActivationPolicy.Classify(rule, Tags).Status);
        Assert.True(Original353RuleText.IsValidAiText(rule,
            Original353RuleText.Render(rule, Original353ActivationPolicy.Classify(rule, Tags), Tags).Name,
            Original353RuleText.Render(rule, Original353ActivationPolicy.Classify(rule, Tags), Tags).Description));
    }

    [Fact]
    public void GlobalConfigResolvedToAConstantIsUsed()
    {
        bool? Constant(string name) => name switch { "DB_A.Act[36].ON" => false, "DB_A.Act[36].OFF" => true, _ => null };
        var r = Apply(Call(V("SQ40"), V("DB_A.Act[36].ON"), V("DB_A.Act[36].OFF")), constant: Constant);
        Assert.Equal("MUST_BE_FALSE", Assert.Single(r.Rules).Operator);
    }

    [Fact]
    public void UnresolvableConfigProducesNothing()
    {
        var r = Apply(Call(V("SQ40"), V("DB_A.Act[36].ON"), IrExpr.Const("TRUE")), constant: _ => null);
        Assert.Empty(r.Rules);
        Assert.Contains("ExpectedStateConfigUnresolved:Signal_ON", r.Notes);
        // 没有给常量解析器时同样不产
        Assert.Empty(Apply(Call(V("SQ40"), V("DB_A.Act[36].ON"), IrExpr.Const("TRUE"))).Rules);
    }

    [Fact]
    public void UnwiredConfigUsesTheInterfaceStartValue()
    {
        var r = Apply(Call(V("SQ40"), null, IrExpr.Const("TRUE")));   // Signal_ON 未接线、接口没写初值 → false
        Assert.Equal("MUST_BE_FALSE", Assert.Single(r.Rules).Operator);
        Assert.Contains("DefaultUsed:Signal_ON", r.Notes);
    }

    [Fact]
    public void ConfigWithoutAMatchingCaseProducesNothing()
    {
        var r = Apply(Call(V("SQ40"), IrExpr.Const("TRUE"), IrExpr.Const("FALSE")));
        Assert.Empty(r.Rules);
        Assert.Contains("ExpectedStateNoMatchingConfig", r.Notes);
    }

    [Theory]
    [InlineData("Dup")]     // 点表里重名
    [InlineData("Q1")]      // 不是 %I
    [InlineData("DB_X.Y")]  // 不在点表
    public void MonitoredInputThatIsNotAUniqueInputProducesNothing(string signal)
    {
        var r = Apply(Call(V(signal), IrExpr.Const("FALSE"), IrExpr.Const("TRUE")));
        Assert.Empty(r.Rules);
        Assert.Contains("ExpectedStateInputNotUniqueI", r.Notes);
    }

    [Fact]
    public void ExpectedTrueHasNoOperatorAndProducesNothing()
    {
        var on = ExpectedStateCheckTests.Relation("CheckOnAlarm", null, Case(true, false, true));
        var r = Apply(Call(V("SQ40"), IrExpr.Const("TRUE"), IrExpr.Const("FALSE")), on);
        Assert.Empty(r.Rules);
        Assert.Contains("ExpectedTrueUnsupported", r.Notes);
    }

    [Fact]
    public void NegatedConfigWiringIsNotResolved()
    {
        var call = Call(V("SQ40"), IrExpr.Not(V("DB_A.Act[36].ON")), IrExpr.Const("TRUE"));
        var r = Apply(call, constant: _ => false);
        Assert.Empty(r.Rules);
        Assert.Contains("ExpectedStateConfigUnresolved:Signal_ON", r.Notes);
    }

    [Fact]
    public void AcceptedExpectedStateAlarmGoesToTheAlarmCatalog()
    {
        var r = Apply(Call(V("SQ40"), IrExpr.Const("FALSE"), IrExpr.Const("TRUE")));
        var alarm = Assert.Single(r.Alarms);
        Assert.Equal("ExpectedState", alarm.Kind);
        Assert.Equal("Sensor[36].CheckOffAlarm", alarm.AlarmSymbol);   // 输出引脚未接线 → 实例路径
        Assert.Equal(new[] { "SQ40" }, alarm.Feedback);
        Assert.Equal("", alarm.Output);
        Assert.Equal("T#5s", alarm.Delay);
    }

    [Fact]
    public void ExpectedTrueAlarmIsStillAnHmiAlarmAndUnresolvedConfigToo()
    {
        var on = ExpectedStateCheckTests.Relation("CheckOnAlarm", null, Case(true, false, true));
        Assert.Equal("Sensor[36].CheckOnAlarm", Assert.Single(Apply(Call(V("SQ40"), IrExpr.Const("TRUE"), IrExpr.Const("FALSE")), on).Alarms).AlarmSymbol);
        Assert.Single(Apply(Call(V("SQ40"), V("DB_A.Act[36].ON"), IrExpr.Const("TRUE")), constant: _ => null).Alarms);
    }

    [Fact]
    public void MonitoredInputThatIsNotAUniqueInputProducesNoAlarm()
    {
        Assert.Empty(Apply(Call(V("Dup"), IrExpr.Const("FALSE"), IrExpr.Const("TRUE"))).Alarms);
        Assert.Empty(Apply(Call(null, IrExpr.Const("FALSE"), IrExpr.Const("TRUE"))).Alarms);
    }
}
