#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class UnderstandingRuleSourceTests
{
    // 下层 FB_Low：O := I（gate 关系由 AI 指认，未被第一期证明）
    // 中层 FB_Mid：调用 FB_Low，I 接 MI（FB_Mid 的形参），O 接 MO（FB_Mid 的形参）
    // OB1：调用 FB_Mid，MI 接 I_In（%I），MO 接 Q_Out（%Q）
    // 期望：在 OB1 的程序段产出 REQUIRES_0_WHEN_FALSE(I_In → Q_Out)
    static readonly PipelineTag[] DefaultTags = [new("Q_Out", "%Q0.0", "Bool"), new("I_In", "%I0.0", "Bool")];

    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts) Project(params PipelineUnit[] extra) =>
        ProjectWith(DefaultTags, extra);

    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts) ProjectWith(IReadOnlyList<PipelineTag> tags,
        params PipelineUnit[] extra)
    {
        var low = new FlgNetBuilder(); low.Series("Coil", "O", ("I", false));
        var mid = new FlgNetBuilder();
        mid.CallWired("FB_Low", "Inst", ("I", "Input", "Bool", "MI"), ("O", "Output", "Bool", "MO"));
        var ob = new FlgNetBuilder();
        ob.CallWired("FB_Mid", "DB_Mid", ("MI", "Input", "Bool", "I_In"), ("MO", "Output", "Bool", "Q_Out"));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_Low", 100, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["I"], ["O"]), "FB"),
            new(11, "FB_Mid", 110, "LAD", mid.Build(), "", ["FB_Low"], 1, FlgNetBuilder.Interface(["MI"], ["MO"]), "FB"),
            new(12, "OB1", 120, "LAD", ob.Build(), "", ["FB_Mid"], 1, "", "OB"),
            .. extra,
        ],
        tags);
        return (source, LowGate(source));
    }

    static Dictionary<string, FbContractOutcome> LowGate(PipelineSnapshot source)
    {
        var lowBlock = IrBlock.Load(source, "FB_Low");
        var stmt = lowBlock.StatementsById.Keys.First();
        var gate = new AiContractRelation("gate", ["O"], ["I"], null, null, null, null, [stmt], null);
        return new Dictionary<string, FbContractOutcome>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["FB_Low"] = new("FB_Low", FbContractStatus.Ok, new ValidatedFbContract("FB_Low", [new ValidatedRelation(gate, false, null)]), "ck-low", null)
        };
    }

    static Original353ProjectResult Empty(PipelineSnapshot s) =>
        new(1, "v", "fp", s.Units.Select(u => new Original353UnitResult(u.Id, [], [], [])).ToArray(), 0, 0, []);

    [Fact]
    public void DerivedGateIsSubstitutedAtTheOuterCallSite()
    {
        var (source, contracts) = Project();
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        var ob = result.Units.Single(u => u.LogicUnitId == 120);
        var rule = Assert.Single(ob.Rules);
        Assert.Equal("REQUIRES_0_WHEN_FALSE", rule.Operator);
        Assert.Equal("I_In", rule.Signals.Single(s => s.Role == "Source").Name);
        Assert.Equal("Q_Out", rule.Signals.Single(s => s.Role == "Target").Name);
        Assert.Equal(1, result.RuleCount);
        Assert.Equal(1, report.RulesAdded);
    }

    [Fact]
    public void NewRuleReplacesTheSameKeyInTheSameUnit()
    {
        var (source, contracts) = Project();
        var existing = new PipelineRule("REQUIRES_0_WHEN_FALSE", [new("I_In", "Source"), new("Q_Out", "Target")], "旧");
        var generated = Empty(source);
        generated = generated with
        {
            Units = generated.Units.Select(u => u.LogicUnitId == 120 ? u with { Rules = [existing] } : u).ToArray()
        };
        var (result, report) = UnderstandingRuleSource.Merge(source, generated, contracts, "m1");
        var rule = Assert.Single(result.Units.Single(u => u.LogicUnitId == 120).Rules);
        Assert.Equal(UnderstandingProofs.AiFbContract, rule.Proof);
        Assert.Equal(1, report.RulesReplaced);
    }

    [Fact]
    public void AiRuleDoesNotDowngradeAStrongerExistingRuleWithTheSameKey()
    {
        // 已有：代码产出的 2 个 %Q 互斥（Active）；新：AI 指认的同键互斥但带延时（RequiresRuntimeGuard，级别更低）→ 保留已有。
        // （2026-10-02 起 AI 指认的瞬时互斥本身判 Active，与已有同级时按「新流程为准」替换，故这里用延时版本表示「更弱」）
        var source = new PipelineSnapshot(1, [new(12, "OB1", 120, "LAD", "<FlgNet/>", "", [], 1, "", "OB")],
            [new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool")]);
        var existing = new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q2", "Member")], "代码");
        var generated = new Original353ProjectResult(1, "v", "fp", [new Original353UnitResult(120, [existing], [], [])], 1, 0, []);
        var weaker = existing with { Description = "AI", Proof = UnderstandingProofs.AiFbContract, Applicability = "AfterSettling", RequiresSettlingEvidence = true };
        var (result, report) = UnderstandingRuleSource.MergeRules(source, generated,
            new Dictionary<long, IReadOnlyList<PipelineRule>> { [120] = [weaker] });
        Assert.Same(existing, Assert.Single(result.Units[0].Rules));
        Assert.Equal(1, report.KeptStronger);
    }

    [Fact]
    public void AlarmsCarryTheirCallSiteLocation()
    {
        var (source, _) = Project();
        var lowBlock = IrBlock.Load(source, "FB_Low");
        var stmt = lowBlock.StatementsById.Keys.First();
        // 下层块直接被 OB 调用、报警位接全局变量的场景由 Task 6 覆盖；这里只验证位置字段从调用点带出
        var alarm = new CallSiteAlarm("DB_A.X", "Timeout", "Q_Out", ["I_In"], "10", [stmt]);
        var located = new UnderstandingAlarm(alarm, 120, "OB1", 1, "DB_Mid", "FB_Mid");
        Assert.Equal(120, located.UnitId);
        Assert.Equal("FB_Mid", located.Callee);
    }

    [Fact]
    public void FailedContractsProduceNothingAndAreReported()
    {
        var (source, _) = Project();
        var contracts = new Dictionary<string, FbContractOutcome>
        { ["FB_Low"] = new("FB_Low", FbContractStatus.Failed, null, "k", "AiReplyUnusable") };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(FbContractStatus.Failed, report.FbStatus["FB_Low"]);
    }

    [Fact]
    public void QWrittenAtAnotherSiteInTheProjectYieldsNoRule()
    {
        // Q_Out 既由 FB_Mid 调用的输出引脚写，又在另一个程序段被线圈赋值 → 不是唯一写入者，不产
        var other = new FlgNetBuilder(); other.Series("Coil", "Q_Out", ("X", false));
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Other", 130, "LAD", other.Build(), "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Empty(result.Units.Single(u => u.LogicUnitId == 120).Rules);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
        Assert.Equal(0, report.RulesAdded);
    }

    [Fact]
    public void QWrittenAtAnAbsoluteAddressElsewhereAlsoCounts()
    {
        var other = new FlgNetBuilder(); other.Series("Coil", "%Q0.0", ("X", false));
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Other", 130, "LAD", other.Build(), "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void QMentionedInTextOnlyUnitCountsAsAPossibleWrite()
    {
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Text", 130, "STL", "      = Q_Out;", "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void ActualThatIsACallerTempIsNotUsedAsAGlobalName()
    {
        // FB_Mid 把自己的 Temp 变量 T1 接到 FB_Low.I、Q_Out 接到 FB_Low.O：T1 运行时读不到，不能当门控源
        var low = new FlgNetBuilder(); low.Series("Coil", "O", ("I", false));
        var mid = new FlgNetBuilder();
        mid.CallWired("FB_Low", "Inst", ("I", "Input", "Bool", "T1"), ("O", "Output", "Bool", "Q_Out"));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_Low", 100, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["I"], ["O"]), "FB"),
            new(11, "FB_Mid", 110, "LAD", mid.Build(), "", ["FB_Low"], 1, FlgNetBuilder.Interface([], [], ["T1"]), "FB"),
        ],
        [new("Q_Out", "%Q0.0", "Bool"), new("I_In", "%I0.0", "Bool")]);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), LowGate(source), "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["ActualIsCallerLocal"]);
    }

    // 链：FB_L0（契约）← FB_L1 ← … ← FB_L{n} ← OB；每层把形参 A/B 原样透传给下一层
    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts) Chain(int n)
    {
        var units = new List<PipelineUnit>();
        var l0 = new FlgNetBuilder(); l0.Series("Coil", "B", ("A", false));
        units.Add(new(1000, "FB_L0", 1000, "LAD", l0.Build(), "", [], 1, FlgNetBuilder.Interface(["A"], ["B"]), "FB"));
        for (var i = 1; i <= n; i++)
        {
            var b = new FlgNetBuilder();
            b.CallWired($"FB_L{i - 1}", "Inst", ("A", "Input", "Bool", "A"), ("B", "Output", "Bool", "B"));
            units.Add(new(1000 + i, $"FB_L{i}", 1000 + i, "LAD", b.Build(), "", [$"FB_L{i - 1}"], 1, FlgNetBuilder.Interface(["A"], ["B"]), "FB"));
        }
        var ob = new FlgNetBuilder();
        ob.CallWired($"FB_L{n}", "DB_Top", ("A", "Input", "Bool", "I_In"), ("B", "Output", "Bool", "Q_Out"));
        units.Add(new(2000, "OB1", 2000, "LAD", ob.Build(), "", [$"FB_L{n}"], 1, "", "OB"));
        var source = new PipelineSnapshot(1, units, [new("Q_Out", "%Q0.0", "Bool"), new("I_In", "%I0.0", "Bool")]);
        var stmt = IrBlock.Load(source, "FB_L0").StatementsById.Keys.First();
        var gate = new AiContractRelation("gate", ["B"], ["A"], null, null, null, null, [stmt], null);
        return (source, new Dictionary<string, FbContractOutcome>
        {
            ["FB_L0"] = new("FB_L0", FbContractStatus.Ok, new ValidatedFbContract("FB_L0", [new ValidatedRelation(gate, false, null)]), "ck", null)
        });
    }

    [Fact]
    public void FourDerivedLayersStillReachTheOuterCallSite()
    {
        var (source, contracts) = Chain(4);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        var rule = Assert.Single(result.Units.Single(u => u.LogicUnitId == 2000).Rules);
        Assert.Equal("REQUIRES_0_WHEN_FALSE", rule.Operator);
        Assert.False(report.NoteCounts.ContainsKey("DerivedDepthExceeded"));
    }

    [Fact]
    public void DerivedChainDeeperThanFourLayersIsCutAndNoted()
    {
        var (source, contracts) = Chain(5);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["DerivedDepthExceeded"]);
    }

    [Fact]
    public void ConditionalFeedbackWithTheSameKeyIsDroppedAndCountsRecomputed()
    {
        var source = new PipelineSnapshot(1, [new(12, "OB1", 120, "LAD", "<FlgNet/>", "", [], 1, "", "OB")],
            [new("Q1", "%Q0.0", "Bool"), new("I1", "%I0.0", "Bool")]);
        var old = new PipelineRule("IMPLIES", [new("Q1", "Source"), new("I1", "Target")], "旧条件反馈");
        var unit = new Original353UnitResult(120, [], [], [])
        { ConditionalFeedbackRules = [new Original353ConditionalFeedback(old, ["B1"], "AfterSettling", true, [])] };
        var generated = new Original353ProjectResult(1, "v", "fp", [unit], 0, 0, []) { ConditionalFeedbackCount = 1 };
        var fresh = old with { Description = "新", Proof = UnderstandingProofs.AiFbContract };
        var (result, report) = UnderstandingRuleSource.MergeRules(source, generated,
            new Dictionary<long, IReadOnlyList<PipelineRule>> { [120] = [fresh] });
        Assert.Empty(result.Units[0].ConditionalFeedbackRules);
        Assert.Equal(0, result.ConditionalFeedbackCount);
        Assert.Equal(1, result.RuleCount);
        Assert.Equal(1, report.Added);
    }

    // ---- Fix round 1 ----

    [Fact]
    public void GraphActionMentioningQBlocksTheRule()
    {
        var raw = """<Actions><Title /><Action Qualifier="N"><Token Text="Q_Out" /></Action></Actions>""";
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Seq", 130, "GRAPH", raw, "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void AliasTagsOnOneAddressAreCountedTogether()
    {
        // Q_Out 与 Q_Alias 是同一个 %Q0.0：Q_Out 由调用写一次、Q_Alias 在别处写一次 → 同一位被写两次
        var other = new FlgNetBuilder(); other.Series("Coil", "Q_Alias", ("X", false));
        var (source, contracts) = ProjectWith([.. DefaultTags, new("Q_Alias", "%Q0.0", "Bool")],
            new PipelineUnit(13, "FB_Other", 130, "LAD", other.Build(), "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void ByteWriteBlocksARuleOnABitInsideIt()
    {
        var other = new FlgNetBuilder(); other.Series("Coil", "QB_All", ("X", false));
        var (source, contracts) = ProjectWith(
            [new("Q_Out", "%Q0.3", "Bool"), new("I_In", "%I0.0", "Bool"), new("QB_All", "%QB0", "Byte")],
            new PipelineUnit(13, "FB_Other", 130, "LAD", other.Build(), "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void ByteWriteOutsideTheBitDoesNotBlock()
    {
        var other = new FlgNetBuilder(); other.Series("Coil", "QB_Next", ("X", false));
        var (source, contracts) = ProjectWith(
            [new("Q_Out", "%Q0.3", "Bool"), new("I_In", "%I0.0", "Bool"), new("QB_Next", "%QB1", "Byte")],
            new PipelineUnit(13, "FB_Other", 130, "LAD", other.Build(), "", [], 1, "", "FB"));
        var (result, _) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
    }

    [Fact]
    public void StlAbsoluteAddressBlocksTheRule()
    {
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Stl", 130, "STL", "      = Q 0.0;", "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void NewFlowGateWhoseSourceIsNotAUniqueBoolTagIsDropped()
    {
        // 门控源 I_In 不在点表（如 DB 内部变量）：新流程门控规则不入库，计 GateSourceNotIo（spec §7.1 形式校验）
        var (source, contracts) = ProjectWith([new("Q_Out", "%Q0.0", "Bool")]);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Empty(result.Units.Single(u => u.LogicUnitId == 120).Rules);
        Assert.Equal(1, report.NoteCounts["GateSourceNotIo"]);

        // 点表里同名两条（不唯一）同样不入库
        var (dup, dupContracts) = ProjectWith([.. DefaultTags, new("I_In", "%I0.1", "Bool")]);
        var (dupResult, dupReport) = UnderstandingRuleSource.Merge(dup, Empty(dup), dupContracts, "m1");
        Assert.Empty(dupResult.Units.Single(u => u.LogicUnitId == 120).Rules);
        Assert.Equal(1, dupReport.NoteCounts["GateSourceNotIo"]);
    }

    [Fact]
    public void StlWordFormCoveringTheBitBlocksTheRule()
    {
        var (source, contracts) = ProjectWith([new("Q_Out", "%Q1.2", "Bool"), new("I_In", "%I0.0", "Bool")],
            new PipelineUnit(13, "FB_Stl", 130, "STL", "      T QW 0;", "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void StlOtherBitDoesNotBlock()
    {
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Stl", 130, "STL", "      = Q 0.1;\n      = Q 10.0;\n      T QB 1;", "", [], 1, "", "FB"));
        var (result, _) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
    }

    [Fact]
    public void DerivedOutputWrittenElsewhereInTheMiddleBlockIsNotDerived()
    {
        // FB_Mid 的 MO 除了来自内层调用，还在另一个网络被线圈赋值 → 不能把内层门控派生到外层
        var extra = new FlgNetBuilder(); extra.Series("Coil", "MO", ("X", false));
        var (source, contracts) = Project(new PipelineUnit(11, "FB_Mid", 111, "LAD", extra.Build(), "", [], 2,
            FlgNetBuilder.Interface(["MI"], ["MO"]), "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["DerivedOutputWrittenElsewhere"]);
    }

    [Fact]
    public void ConditionalFeedbackIsKeptWhenTheStrongerExistingRuleIsKept()
    {
        var source = new PipelineSnapshot(1, [new(12, "OB1", 120, "LAD", "<FlgNet/>", "", [], 1, "", "OB")],
            [new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool")]);
        var existing = new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q2", "Member")], "代码");
        var unit = new Original353UnitResult(120, [existing], [], [])
        { ConditionalFeedbackRules = [new Original353ConditionalFeedback(existing, [], "AfterSettling", true, [])] };
        var generated = new Original353ProjectResult(1, "v", "fp", [unit], 1, 0, []) { ConditionalFeedbackCount = 1 };
        var weaker = existing with { Description = "AI", Proof = UnderstandingProofs.AiFbContract, Applicability = "AfterSettling", RequiresSettlingEvidence = true };
        var (result, report) = UnderstandingRuleSource.MergeRules(source, generated,
            new Dictionary<long, IReadOnlyList<PipelineRule>> { [120] = [weaker] });
        Assert.Equal(1, report.KeptStronger);
        Assert.Single(result.Units[0].ConditionalFeedbackRules);
        Assert.Equal(1, result.ConditionalFeedbackCount);
    }

    [Fact]
    public void DerivedRelationsAreDeduplicatedIgnoringCaseAndOrder()
    {
        var a = new AiContractRelation("mutex", ["MO1", "MO2"], [], null, null, null, null, ["s1"], null);
        var b = new AiContractRelation("MUTEX", ["mo2", "mo1"], [], null, null, null, null, ["s2"], null);
        var c = new AiContractRelation("mutex", ["MO1", "MO3"], [], null, null, null, null, ["s3"], null);
        var distinct = UnderstandingRuleSource.DistinctDerived([a, b, c]);
        Assert.Equal(2, distinct.Count);
        Assert.Same(a, distinct[0]);
        Assert.Same(c, distinct[1]);
    }

    [Fact]
    public void TwoCallSitesInOneUnitWithTheSameKeyYieldOneRule()
    {
        // FB_C：两个输入 A、B 的位置矛盾；同一程序段里两次调用都接 I1、I2 → 同 Key 只留一条
        var fc = new FlgNetBuilder(); fc.Series("Coil", "Y", ("A", false), ("B", false));
        var ob = new FlgNetBuilder();
        ob.CallWired("FB_C", "DB_C1", ("A", "Input", "Bool", "I1"), ("B", "Input", "Bool", "I2"));
        ob.CallWired("FB_C", "DB_C2", ("A", "Input", "Bool", "I1"), ("B", "Input", "Bool", "I2"));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_C", 100, "LAD", fc.Build(), "", [], 1, FlgNetBuilder.Interface(["A", "B"], ["Y"]), "FB"),
            new(12, "OB1", 120, "LAD", ob.Build(), "", ["FB_C"], 1, "", "OB"),
        ],
        [new("I1", "%I0.0", "Bool"), new("I2", "%I0.1", "Bool")]);
        var stmt = IrBlock.Load(source, "FB_C").StatementsById.Keys.First();
        var rel = new AiContractRelation("contradiction", [], ["A", "B"], null, null, null, null, [stmt], null);
        // 派生层契约的形态（Proven=false，来自内层的派生关系；第 0 层的矛盾都是已证明的，不在此产规则）
        var contracts = new Dictionary<string, FbContractOutcome>
        { ["FB_C"] = new("FB_C", FbContractStatus.Ok, new ValidatedFbContract("FB_C", [new ValidatedRelation(rel, false, null)]), "ck", null) };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        var rule = Assert.Single(result.Units.Single(u => u.LogicUnitId == 120).Rules);
        Assert.Equal("AT_MOST_ONE", rule.Operator);
        Assert.Equal(1, report.RulesAdded);
    }

    // ---- Fix round 2 ----

    static PipelineUnit CoilUnit(string target)
    {
        var b = new FlgNetBuilder(); b.Series("Coil", target, ("X", false));
        return new PipelineUnit(13, "FB_Other", 130, "LAD", b.Build(), "", [], 1, "", "FB");
    }

    [Fact]
    public void BitSliceOfAByteTagBlocksARuleOnThatBit()
    {
        var (source, contracts) = ProjectWith(
            [new("Q_Out", "%Q0.3", "Bool"), new("I_In", "%I0.0", "Bool"), new("QByteTag", "%QB0", "Byte")],
            CoilUnit("QByteTag.%X3"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void BitSliceOfAnotherBitDoesNotBlock()
    {
        var (source, contracts) = ProjectWith(
            [new("Q_Out", "%Q0.3", "Bool"), new("I_In", "%I0.0", "Bool"), new("QByteTag", "%QB0", "Byte")],
            CoilUnit("QByteTag.%X4"));
        var (result, _) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
    }

    [Fact]
    public void SliceOfAWordTagCountsTheWholeWord()
    {
        // 字内位序与字节序相关，算不准 → 按整个字计
        var (source, contracts) = ProjectWith(
            [new("Q_Out", "%Q1.3", "Bool"), new("I_In", "%I0.0", "Bool"), new("QWordTag", "%QW0", "Word")],
            CoilUnit("QWordTag.%X12"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void IndirectStlQWriteBlocksEveryQRule()
    {
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Stl", 130, "STL", "      T QW [AR1,P#0.0];", "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["UnresolvedQWrite"]);
    }

    // ---- 评测迭代 1：只有写指令才算写 ----

    [Fact]
    public void IndirectStlLoadIsNotAnUnresolvedWrite()
    {
        // 泰星 6.2 FC3012_IOtoDB 网络 4：L QB[ AR1, P#0.0] 是读，不是写
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Stl", 130, "STL", "      L QB[ AR1, P#0.0];\n      T MB 0;", "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("UnresolvedQWrite"));
    }

    [Fact]
    public void IndirectStlTransferIsAnUnresolvedWrite()
    {
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Stl", 130, "STL", "      L MB 0;\n      T QB[ AR1, P#0.0];", "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["UnresolvedQWrite"]);
    }

    [Theory]
    [InlineData("      L Q 0.0;\n      T MB 0;")]
    [InlineData("      A Q 0.0;\n      = M 0.0;")]
    [InlineData("      L QB 0;\n      T MB 1;")]
    public void StlReadOfTheBitIsNotAWrite(string text)
    {
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Stl", 130, "STL", text, "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("QWrittenElsewhere"));
    }

    [Theory]
    [InlineData("      = Q 0.0;")]
    [InlineData("      S Q 0.0;")]
    [InlineData("      R %Q0.0;")]
    [InlineData("      T QB 0;")]
    [InlineData("%Q0.0 := TRUE;")]
    [InlineData("\"FC_X\"(Out => %Q0.0);")]
    public void WriteOfTheBitBlocksTheRule(string text)
    {
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Txt", 130, "STL", text, "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void GraphActionWithAbsoluteAddressStillBlocks()
    {
        var raw = """<Actions><Title /><Action Qualifier="N"><Token Text="%Q0.0" /></Action></Actions>""";
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Seq", 130, "GRAPH", raw, "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void TagNameMentionInTextStillCountsWithoutAWriteInstruction()
    {
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Stl", 130, "STL", "      A Q_Out;\n      = M 0.0;", "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void UnparseableQTargetBlocksEveryQRule()
    {
        var (source, contracts) = Project(CoilUnit("%Q:Char:96"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["UnresolvedQWrite"]);
    }

    [Fact]
    public void ProjectWithoutUnresolvedQWritesIsUnaffected()
    {
        var (source, contracts) = Project(new PipelineUnit(13, "FB_Stl", 130, "STL", "      L MW [AR1,P#0.0];\n      = Q 5.0;", "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("UnresolvedQWrite"));
    }

    [Fact]
    public void GermanOutputAddressIsTheSameBitAsQ()
    {
        var (source, contracts) = ProjectWith([.. DefaultTags, new("A_Alias", "%A0.0", "Bool")], CoilUnit("A_Alias"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Fact]
    public void GermanByteTargetCoveringTheBitBlocks()
    {
        var (source, contracts) = Project(CoilUnit("%AB0"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
    }

    [Theory]
    [InlineData("MO.x")]
    [InlineData("MO[1]")]
    public void MemberWriteOfACallerFormalOutputBlocksDerivation(string member)
    {
        var extra = new FlgNetBuilder(); extra.Series("Coil", member, ("X", false));
        var (source, contracts) = Project(new PipelineUnit(11, "FB_Mid", 111, "LAD", extra.Build(), "", [], 2,
            FlgNetBuilder.Interface(["MI"], ["MO"]), "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["DerivedOutputWrittenElsewhere"]);
    }

    // ---- 评测迭代 2：修正 C ----
    // C1（用户决定）：FC 块（按块类型 BlockKind，不看块名）里的写入一律不进 Q 写入索引——
    // TIA 导出 FC 时部分失败，FC 内容本来就不完整

    static PipelineUnit Kind(string kind, string name, string language, string raw) =>
        new(13, name, 130, language, raw, "", [], 1, "", kind);

    [Theory]
    [InlineData("FB")]
    [InlineData("OB")]
    public void IndirectStlTransferOutsideAnFcBlocksEveryQRule(string kind)
    {
        var (source, contracts) = Project(Kind(kind, "Blk", "STL", "      L MB 0;\n      T QB[AR1,P#0.0];"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["UnresolvedQWrite"]);
    }

    [Theory]
    [InlineData("FC")]
    [InlineData("fc")]
    public void IndirectStlTransferInAnFcIsIgnored(string kind)
    {
        var (source, contracts) = Project(Kind(kind, "Blk", "STL", "      L MB 0;\n      T QB[AR1,P#0.0];"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("UnresolvedQWrite"));
    }

    [Theory]
    [InlineData("      = Q 0.0;")]
    [InlineData("      = Q_Out;")]
    [InlineData("      A Q_Out;\n      = M 0.0;")]
    [InlineData("      = A [AR1,P#0.0];")]
    public void TextWritesInAnFcAreNotCounted(string text)
    {
        var (source, contracts) = Project(Kind("FC", "Blk", "STL", text));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("QWrittenElsewhere"));
        Assert.False(report.NoteCounts.ContainsKey("UnresolvedQWrite"));
    }

    [Theory]
    [InlineData("Q_Out")]
    [InlineData("%Q0.0")]
    [InlineData("%Q:Char:96")]
    public void CoilWritesInAnFcAreNotCounted(string target)
    {
        var b = new FlgNetBuilder(); b.Series("Coil", target, ("X", false));
        var (source, contracts) = Project(Kind("FC", "Blk", "LAD", b.Build()));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("QWrittenElsewhere"));
        Assert.False(report.NoteCounts.ContainsKey("UnresolvedQWrite"));
    }

    [Fact]
    public void FcIsJudgedByBlockKindNotByName()
    {
        // 名字像 FC、类型是 FB：照常计数
        var (s1, c1) = Project(Kind("FB", "FC_LooksLikeFc", "STL", "      = Q 0.0;"));
        var (r1, rep1) = UnderstandingRuleSource.Merge(s1, Empty(s1), c1, "m1");
        Assert.Equal(0, r1.RuleCount);
        Assert.Equal(1, rep1.NoteCounts["QWrittenElsewhere"]);
        // 名字像 FB、类型是 FC：不计
        var (s2, c2) = Project(Kind("FC", "FB_LooksLikeFb", "STL", "      = Q 0.0;"));
        Assert.Equal(1, UnderstandingRuleSource.Merge(s2, Empty(s2), c2, "m1").Result.RuleCount);
    }

    [Fact]
    public void GermanIndirectStlWriteOutsideAnFcBlocks()
    {
        var (source, contracts) = Project(Kind("FB", "Blk", "STL", "      = A [AR1,P#0.0];"));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["UnresolvedQWrite"]);
    }

    // 审查 Critical-1：FC 完全不管——FC 块里的调用点不做代入，不产规则、派生关系与报警
    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts) DirectCallFrom(string callerKind, params PipelineUnit[] extra)
    {
        var low = new FlgNetBuilder(); low.Series("Coil", "O", ("I", false));
        var caller = new FlgNetBuilder();
        caller.CallWired("FB_Low", "Inst", ("I", "Input", "Bool", "I_In"), ("O", "Output", "Bool", "Q_Out"));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_Low", 100, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["I"], ["O"]), "FB"),
            new(12, "Caller", 120, "LAD", caller.Build(), "", ["FB_Low"], 1, "", callerKind),
            .. extra,
        ],
        DefaultTags);
        return (source, LowGate(source));
    }

    [Theory]
    [InlineData("FB")]
    [InlineData("OB")]
    public void CallSiteOutsideAnFcYieldsTheRule(string kind)
    {
        var (source, contracts) = DirectCallFrom(kind);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal("REQUIRES_0_WHEN_FALSE", Assert.Single(result.Units.Single(u => u.LogicUnitId == 120).Rules).Operator);
        Assert.False(report.NoteCounts.ContainsKey("CallerIsFc"));
    }

    [Fact]
    public void CallSiteInsideAnFcYieldsNothing()
    {
        var (source, contracts) = DirectCallFrom("FC");
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["CallerIsFc"]);
    }

    [Fact]
    public void FcCallSiteWithAnotherFbWriterOfTheSameQYieldsNothing()
    {
        // 审查场景：FC 里调用阀门 FB 写 Q_Out，另一个 FB 的线圈也写 Q_Out → 不得产规则
        var (source, contracts) = DirectCallFrom("FC", CoilUnit("Q_Out") with { BlockKind = "FB" });
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["CallerIsFc"]);
    }

    [Fact]
    public void DerivedRelationIsNotSubstitutedAtAnFcCallSite()
    {
        // FB_Mid 的派生门控到外层调用点：外层在 FC 里 → 不产
        var (source, contracts) = Project();
        source = source with { Units = source.Units.Select(u => u.Id == 120 ? u with { BlockKind = "FC" } : u).ToArray() };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["CallerIsFc"]);
    }

    // ---- 迭代 4 修正 F：新流程证伪旧反馈 ----

    static (PipelineSnapshot Source, Original353ProjectResult Generated) OldRules(params (long Unit, PipelineRule[] Rules, PipelineRule[] Conditional)[] units)
    {
        var source = new PipelineSnapshot(1,
            units.Select(u => new PipelineUnit(12, "OB1", u.Unit, "LAD", "<FlgNet/>", "", [], 1, "", "OB")).ToArray(),
            [new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("I1", "%I0.0", "Bool"), new("I2", "%I0.1", "Bool")]);
        var generated = new Original353ProjectResult(1, "v", "fp", units.Select(u => new Original353UnitResult(u.Unit, u.Rules, [], [])
        {
            ConditionalFeedbackRules = u.Conditional.Select(r => new Original353ConditionalFeedback(r, [], "AfterSettling", true, [])).ToArray()
        }).ToArray(), units.Sum(u => u.Rules.Length), 0, []);
        return (source, generated);
    }

    static PipelineRule Implies(string q, params string[] targets) =>
        new("IMPLIES", [new(q, "Source"), .. targets.Select(t => new PipelineSignal(t, "Target"))], "旧");

    static Dictionary<long, IReadOnlySet<string>> Superseded(long unit, params string[] qs) =>
        new() { [unit] = new HashSet<string>(qs, System.StringComparer.OrdinalIgnoreCase) };

    [Fact]
    public void SupersededOutputRemovesOldImpliesInTheSameUnitOnly()
    {
        var anyRule = new PipelineRule("IMPLIES_ANY", [new("Q1", "Source"), new("I1", "Target"), new("I2", "Target")], "旧")
        { TargetGroups = [["I1"], ["I2"]] };
        var mutex = new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q2", "Member")], "旧");
        var (source, generated) = OldRules(
            (120, [Implies("Q1", "I1"), anyRule, Implies("Q2", "I1"), mutex], [Implies("Q1", "I2"), Implies("Q2", "I2")]),
            (121, [Implies("Q1", "I1")], []));
        var (result, report) = UnderstandingRuleSource.MergeRules(source, generated,
            new Dictionary<long, IReadOnlyList<PipelineRule>>(), Superseded(120, "q1"));
        var u120 = result.Units.Single(u => u.LogicUnitId == 120);
        Assert.Equal(new[] { "AT_MOST_ONE", "IMPLIES" }, u120.Rules.Select(r => r.Operator).Order().ToArray());
        Assert.Equal("Q2", u120.Rules.Single(r => r.Operator == "IMPLIES").Signals.Single(s => s.Role == "Source").Name);
        Assert.Equal("Q2", Assert.Single(u120.ConditionalFeedbackRules).Rule.Signals.Single(s => s.Role == "Source").Name);
        Assert.Single(result.Units.Single(u => u.LogicUnitId == 121).Rules);
        Assert.Equal(3, result.RuleCount);
        Assert.Equal(1, result.ConditionalFeedbackCount);
        Assert.Equal(3, report.Superseded);
    }

    [Fact]
    public void NewRulesInTheSupersededUnitAreStillAdded()
    {
        var (source, generated) = OldRules((120, [Implies("Q1", "I1")], []));
        var fresh = Implies("Q1", "I2") with { Proof = UnderstandingProofs.AiFbContract };
        var (result, report) = UnderstandingRuleSource.MergeRules(source, generated,
            new Dictionary<long, IReadOnlyList<PipelineRule>> { [120] = [fresh] }, Superseded(120, "Q1"));
        Assert.Same(fresh, Assert.Single(result.Units[0].Rules));
        Assert.Equal(1, report.Superseded);
        Assert.Equal(1, report.Added);
    }

    [Fact]
    public void FeedbackAbandonedAsAlternativePathRemovesTheOldImpliesAtThatCallSite()
    {
        // FB_Fb：到位变量是未写入的输入形参 IWP；OB1 把 IWP 接到输出本身 Q_Out → 到位可由输出自身成立
        var fb = new FlgNetBuilder(); fb.Series("Coil", "O", ("IWP", false));
        var ob = new FlgNetBuilder();
        ob.CallWired("FB_Fb", "DB_Fb", ("IWP", "Input", "Bool", "Q_Out"), ("O", "Output", "Bool", "Q_Out"));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_Fb", 100, "LAD", fb.Build(), "", [], 1, FlgNetBuilder.Interface(["IWP"], ["O"]), "FB"),
            new(12, "OB1", 120, "LAD", ob.Build(), "", ["FB_Fb"], 1, "", "OB"),
        ], DefaultTags);
        var feedback = new AiContractRelation("feedback", ["O"], [], "IWP", null, null, null, ["x"], null);
        var contracts = new Dictionary<string, FbContractOutcome>(System.StringComparer.OrdinalIgnoreCase)
        { ["FB_Fb"] = new("FB_Fb", FbContractStatus.Ok, new ValidatedFbContract("FB_Fb", [new ValidatedRelation(feedback, false, null)]), "ck", null) };
        var generated = Empty(source);
        generated = generated with
        {
            Units = generated.Units.Select(u => u.LogicUnitId == 120 ? u with { Rules = [Implies("Q_Out", "I_In")] } : u).ToArray(),
            RuleCount = 1
        };
        var (result, report) = UnderstandingRuleSource.Merge(source, generated, contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["SupersededByProof"]);
        Assert.Equal(1, report.NoteCounts["AlternativePathViaOutput"]);
    }

    // ---- 迭代 4 修正 H：报警引脚未接线时按实例全局路径（端到端） ----

    /// <summary>FB_V：到位变量 IWP（未写入输入）、输出 O、报警输出 A；FB_Mid 以多重背景 Valve 调用它（A 未接线）；
    /// 若干 FC 以全局实例调用 FB_Mid。</summary>
    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts) MultiInstance(params string[] midInstances)
    {
        var v = new FlgNetBuilder(); v.Series("Coil", "O", ("IWP", false));
        var mid = new FlgNetBuilder();
        mid.CallWired("FB_V", "Valve", ("IWP", "Input", "Bool", "I_In"), ("O", "Output", "Bool", "Q_Out"));
        var statics = new[] { new System.Xml.Linq.XElement("Member", new System.Xml.Linq.XAttribute("Name", "Valve"), new System.Xml.Linq.XAttribute("Datatype", "\"FB_V\"")) };
        var units = new List<PipelineUnit>
        {
            new(10, "FB_V", 100, "LAD", v.Build(), "", [], 1, FlgNetBuilder.Interface(["IWP"], ["O", "A"]), "FB"),
            new(11, "FB_Mid", 110, "LAD", mid.Build(), "", ["FB_V"], 1, FlgNetBuilder.Interface([], [], null, statics), "FB"),
        };
        for (var i = 0; i < midInstances.Length; i++)
        {
            var main = new FlgNetBuilder { CallInstanceScope = "GlobalVariable" }; main.CallWired("FB_Mid", midInstances[i]);
            units.Add(new(20 + i, $"Main{i}", 200 + i, "LAD", main.Build(), "", ["FB_Mid"], 1, "", "FC"));
        }
        var source = new PipelineSnapshot(1, units, DefaultTags);
        var feedback = new AiContractRelation("feedback", ["O"], [], "IWP", null, null, null, ["x"], null);
        var alarm = new AiContractRelation("timeoutAlarm", ["O"], [], "IWP", null, null, "A", ["x"], null);
        var contracts = new Dictionary<string, FbContractOutcome>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["FB_V"] = new("FB_V", FbContractStatus.Ok, new ValidatedFbContract("FB_V",
                [new ValidatedRelation(feedback, false, null), new ValidatedRelation(alarm, false, null)]), "ck", null)
        };
        return (source, contracts);
    }

    [Fact]
    public void UnwiredAlarmOfAMultiInstanceUsesTheCallersUniqueGlobalInstance()
    {
        // 调用方 FB_Mid 唯一一次被调用（在 FC 里，以全局实例 IDB_Mid）：实例查找看全部调用点，FC 只是不产规则
        var (source, contracts) = MultiInstance("IDB_Mid");
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        var a = Assert.Single(report.Alarms);
        Assert.Equal("IDB_Mid.Valve.A", a.Alarm.AlarmSymbol);
        Assert.Equal(new[] { "I_In" }, a.Alarm.Feedback);
        Assert.Equal(1, result.RuleCount);
    }

    [Fact]
    public void AlarmCarriesTheDatabaseNetworkNumberOfItsUnit()
    {
        // 归档源：NetworkOrder 是根元素序号，CompileUnitOrder 是库 networkid 口径；报警两者都带，清单用后者
        var (source, contracts) = MultiInstance("IDB_Mid");
        source = source with { Units = source.Units.Select(u => u with { CompileUnitOrder = 7 }).ToList() };
        var (_, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        var a = Assert.Single(report.Alarms);
        Assert.Equal(1, a.Network);
        Assert.Equal(7, a.DbNetwork);
        Assert.Equal(7, Assert.Single(AlarmCatalogBuilder.Build(report.Alarms)).SourceNetwork);
    }

    [Fact]
    public void UnwiredAlarmOfAMultiInstanceWhoseCallerIsCalledTwiceIsNotProduced()
    {
        var (source, contracts) = MultiInstance("IDB_Mid", "IDB_Mid2");
        var (_, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Empty(report.Alarms);
        Assert.Equal(1, report.NoteCounts["AlarmUnwired"]);
    }

    [Fact]
    public void UnwiredAlarmOfAMultiInstanceWhoseCallerIsNeverCalledIsNotProduced()
    {
        var (source, contracts) = MultiInstance();
        var (_, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Empty(report.Alarms);
    }

    [Fact]
    public void UniqueGlobalInstanceRequiresExactlyOneCallWithANonLocalInstance()
    {
        IReadOnlySet<string> Locals(string block) => block == "FB_Outer"
            ? new HashSet<string>(["Inner"], System.StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        IrCall C(string instance) => new("FB_Mid", "FB", instance, IrExpr.True, []);
        Assert.Equal("IDB_Mid", UnderstandingRuleSource.UniqueGlobalInstance("FB_Mid", [("Main", C("IDB_Mid"))], Locals));
        Assert.Equal("IDB_Mid", UnderstandingRuleSource.UniqueGlobalInstance("fb_mid", [("Main", C("IDB_Mid"))], Locals));
        Assert.Null(UnderstandingRuleSource.UniqueGlobalInstance("FB_Mid", [("Main", C("IDB_Mid")), ("Main", C("IDB_Mid"))], Locals));
        Assert.Null(UnderstandingRuleSource.UniqueGlobalInstance("FB_Mid", [("FB_Outer", C("Inner[1]"))], Locals));   // 多重背景
        Assert.Null(UnderstandingRuleSource.UniqueGlobalInstance("FB_Mid", [("Main", C(""))], Locals));
        Assert.Null(UnderstandingRuleSource.UniqueGlobalInstance("FB_Mid", [], Locals));
        // 审查 Minor-1：导出里标明是多重背景（LocalVariable）的不算全局实例
        Assert.Null(UnderstandingRuleSource.UniqueGlobalInstance("FB_Mid", [("Main", C("IDB_Mid") with { InstanceScope = "LocalVariable" })], Locals));
        Assert.Equal("IDB_Mid", UnderstandingRuleSource.UniqueGlobalInstance("FB_Mid", [("Main", C("IDB_Mid") with { InstanceScope = "GlobalVariable" })], Locals));
    }

    // ---- 迭代 4 审查修正 ----

    [Fact]
    public void InstanceScopeIsReadFromTheExport()
    {
        var local = new FlgNetBuilder(); local.CallWired("FB_X", "Valve");
        var global = new FlgNetBuilder { CallInstanceScope = "GlobalVariable" }; global.CallWired("FB_X", "IDB_X");
        IrCall CallOf(string xml) => UnitTranslator.Translate(new PipelineUnit(1, "B", 1, "LAD", xml, "", [], 1, "", "FB"))
            .Statements.Single(s => s.Call is not null).Call!;
        Assert.Equal("LocalVariable", CallOf(local.Build()).InstanceScope);
        Assert.Equal("GlobalVariable", CallOf(global.Build()).InstanceScope);
    }

    /// <summary>FB_V：契约 contradiction(I1, I2, 报警 A_Word.Alarm1)；FB_Mid：形参 P1/P2 接 FB_V 的 I1/I2，以多重背景 Valve 调用，
    /// FB_Mid 自己也有一个 Output 形参 A_Word（与内层报警同名、含义不同）；innerAlarmTarget 非空时 FB_V.A_Word 接到它。
    /// OB1（全局实例 IDB_Mid）调用 FB_Mid，outerAlarmActual 非空时 FB_Mid.A_Word 接到它。</summary>
    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts) TwoLayerContradiction(string? innerAlarmTarget,
        string? outerAlarmActual, string aiAlarm = "A_Word.Alarm1")
    {
        var v = ContradictionBody();
        var mid = new FlgNetBuilder();
        if (innerAlarmTarget is null) mid.CallWired("FB_V", "Valve", ("I1", "Input", "Bool", "P1"), ("I2", "Input", "Bool", "P2"));
        else mid.CallWired("FB_V", "Valve", ("I1", "Input", "Bool", "P1"), ("I2", "Input", "Bool", "P2"), ("A_Word", "Output", "Bool", innerAlarmTarget));
        var statics = new[] { new System.Xml.Linq.XElement("Member", new System.Xml.Linq.XAttribute("Name", "Valve"), new System.Xml.Linq.XAttribute("Datatype", "\"FB_V\"")) };
        var ob = new FlgNetBuilder { CallInstanceScope = "GlobalVariable" };
        if (outerAlarmActual is null) ob.CallWired("FB_Mid", "IDB_Mid", ("P1", "Input", "Bool", "I_In"), ("P2", "Input", "Bool", "I_In2"));
        else ob.CallWired("FB_Mid", "IDB_Mid", ("P1", "Input", "Bool", "I_In"), ("P2", "Input", "Bool", "I_In2"), ("A_Word", "Output", "Bool", outerAlarmActual));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_V", 100, "LAD", v.Build(), "", [], 1, FlgNetBuilder.Interface(["I1", "I2", "I_Rst"], ["A_Word"]), "FB"),
            new(11, "FB_Mid", 110, "LAD", mid.Build(), "", ["FB_V"], 1, FlgNetBuilder.Interface(["P1", "P2"], ["A_Word"], null, statics), "FB"),
            new(12, "OB1", 120, "LAD", ob.Build(), "", ["FB_Mid"], 1, "", "OB"),
        ], [.. DefaultTags, new("I_In2", "%I0.1", "Bool")]);
        return (source, PhaseOneContradiction(source, aiAlarm));
    }

    /// <summary>FB_V 程序体：I1 ∧ I2 经 TON（PT = T#3S）置位 A_Word.Alarm1，由输入 I_Rst 复位（第一期可证明的位置矛盾）。</summary>
    static FlgNetBuilder ContradictionBody()
    {
        var v = new FlgNetBuilder();
        var last = v.Chain(FlgNetBuilder.Rail(), ("I1", false), ("I2", false));
        var ton = v.Timer("TON", FlgNetBuilder.Pin(last, "out"));
        v.Wire(FlgNetBuilder.Id(v.Constant("T#3S")), FlgNetBuilder.Pin(ton, "PT"));
        v.Coil("SCoil", "A_Word.Alarm1", FlgNetBuilder.Pin(ton, "Q"));
        v.Series("RCoil", "A_Word.Alarm1", ("I_Rst", false));
        return v;
    }

    /// <summary>第 0 层契约：真实的第一期推断（FbContractInference）→ 真实的校验器；断言矛盾已证明。</summary>
    static Dictionary<string, FbContractOutcome> PhaseOneContradiction(PipelineSnapshot source, string aiAlarm)
    {
        var proven = FbContractInference.Infer(source, "FB_V");
        Assert.Equal("A_Word.Alarm1", Assert.Single(proven.Contradictions).SetTarget);
        var block = IrBlock.Load(source, "FB_V");
        var stmt = block.StatementsById.Keys.First(id => block.StatementVars(id).Contains("I1") && block.StatementVars(id).Contains("I2"));
        var contra = new AiContractRelation("contradiction", [], ["I1", "I2"], null, null, 9, aiAlarm, [stmt], null);
        var validated = FbContractValidator.Validate(new AiFbContract("FB_V", "", [contra]), block, proven);
        Assert.True(Assert.Single(validated.Accepted).Proven);
        return new Dictionary<string, FbContractOutcome>(System.StringComparer.OrdinalIgnoreCase)
        { ["FB_V"] = new("FB_V", FbContractStatus.Ok, validated, "ck", null) };
    }

    [Theory]
    [InlineData(null)]          // 外层 FB_Mid.A_Word 未接线：不得拼成 IDB_Mid.A_Word.Alarm1
    [InlineData("DB_X.W")]      // 外层 FB_Mid.A_Word 接线：不得解析成 DB_X.W.Alarm1
    public void DerivedContradictionAlarmOfAMultiInstanceIsResolvedThroughTheOuterInstance(string? outer)
    {
        var (source, contracts) = TwoLayerContradiction(null, outer);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        // 已证明的矛盾跨层派生：外层调用点产出 AT_MOST_ONE（来源 AI，第一期跨不了调用层）
        var rule = Assert.Single(result.Units.Single(u => u.LogicUnitId == 120).Rules);
        Assert.Equal("AT_MOST_ONE", rule.Operator);
        Assert.Equal(UnderstandingProofs.AiFbContract, rule.Proof);
        Assert.Equal(new[] { "I_In", "I_In2" }, rule.Signals.Select(s => s.Name).Order().ToArray());
        Assert.Empty(result.Units.Single(u => u.LogicUnitId == 110).Rules);
        var alarm = Assert.Single(report.Alarms);
        Assert.Equal("PositionConflict", alarm.Alarm.Kind);
        Assert.Equal("IDB_Mid.Valve.A_Word.Alarm1", alarm.Alarm.AlarmSymbol);
        Assert.Equal("T#3S", alarm.Alarm.Delay);   // 程序里的 TON 预设（AI 给的 9 秒不采用），派生两层后仍是原文
    }

    [Theory]
    [InlineData("DB_X.W", "DB_X.W.Alarm1")]           // 内层报警接到中间块自己的 Output 形参 A_Word，外层接线 → 外层实参
    [InlineData(null, "IDB_Mid.A_Word.Alarm1")]        // 外层未接线 → 外层实例路径（此时这条路径确实就是内层报警的去处）
    public void DerivedContradictionAlarmWiredToTheMiddleFormalFollowsTheOuterPin(string? outer, string expected)
    {
        var (source, contracts) = TwoLayerContradiction("A_Word", outer);
        var (_, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(expected, Assert.Single(report.Alarms).Alarm.AlarmSymbol);
    }

    [Fact]
    public void DerivedContradictionAlarmToAMiddleGlobalKeepsTheGlobalName()
    {
        var (source, contracts) = TwoLayerContradiction("DB_G.Cyl", "DB_X.W");
        var (_, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal("DB_G.Cyl.Alarm1", Assert.Single(report.Alarms).Alarm.AlarmSymbol);
    }

    // Minor-3：证伪也要过「%Q 全项目唯一写入」核对；Minor-6：FC 调用方不证伪

    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts, Original353ProjectResult Generated)
        AlternativePathProject(string callerKind, params PipelineUnit[] extra)
    {
        var fb = new FlgNetBuilder(); fb.Series("Coil", "O", ("IWP", false));
        var ob = new FlgNetBuilder();
        ob.CallWired("FB_Fb", "DB_Fb", ("IWP", "Input", "Bool", "Q_Out"), ("O", "Output", "Bool", "Q_Out"));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_Fb", 100, "LAD", fb.Build(), "", [], 1, FlgNetBuilder.Interface(["IWP"], ["O"]), "FB"),
            new(12, "Caller", 120, "LAD", ob.Build(), "", ["FB_Fb"], 1, "", callerKind),
            .. extra,
        ], DefaultTags);
        var feedback = new AiContractRelation("feedback", ["O"], [], "IWP", null, null, null, ["x"], null);
        var contracts = new Dictionary<string, FbContractOutcome>(System.StringComparer.OrdinalIgnoreCase)
        { ["FB_Fb"] = new("FB_Fb", FbContractStatus.Ok, new ValidatedFbContract("FB_Fb", [new ValidatedRelation(feedback, false, null)]), "ck", null) };
        var generated = Empty(source);
        generated = generated with
        {
            Units = generated.Units.Select(u => u.LogicUnitId == 120 ? u with { Rules = [Implies("Q_Out", "I_In")] } : u).ToArray(),
            RuleCount = 1
        };
        return (source, contracts, generated);
    }

    [Fact]
    public void SupersedingIsSkippedWhenTheQIsWrittenElsewhere()
    {
        var (source, contracts, generated) = AlternativePathProject("OB", CoilUnit("Q_Out") with { BlockKind = "FB" });
        var (result, report) = UnderstandingRuleSource.Merge(source, generated, contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("SupersededByProof"));
        Assert.Equal(1, report.NoteCounts["SupersedeSkippedQWrite"]);
    }

    [Fact]
    public void SupersedingIsSkippedWhenAQWriteIsUnresolved()
    {
        var (source, contracts, generated) = AlternativePathProject("OB",
            new PipelineUnit(13, "Blk", 130, "STL", "      T QB[AR1,P#0.0];", "", [], 1, "", "FB"));
        var (result, report) = UnderstandingRuleSource.Merge(source, generated, contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["SupersedeSkippedQWrite"]);
    }

    [Fact]
    public void FcCallSiteDoesNotSupersede()
    {
        var (source, contracts, generated) = AlternativePathProject("FC");
        var (result, report) = UnderstandingRuleSource.Merge(source, generated, contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("SupersededByProof"));
    }

    // 迭代 3 审查 Minor-5：FC 里的调用点连报警也不产
    [Fact]
    public void AlarmsAtAnFcCallSiteAreNotProduced()
    {
        var (source, contracts) = MultiInstance("IDB_Mid");
        var fc = new FlgNetBuilder();
        fc.CallWired("FB_V", "IDB_V", ("IWP", "Input", "Bool", "I_In"), ("O", "Output", "Bool", "Q_Out"), ("A", "Output", "Bool", "DB_A.X"));
        source = source with
        {
            Units = source.Units.Where(u => u.BlockName != "FB_Mid" && !u.BlockName.StartsWith("Main"))
                .Append(new PipelineUnit(30, "FcCaller", 300, "LAD", fc.Build(), "", ["FB_V"], 1, "", "FC")).ToArray()
        };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Empty(report.Alarms);
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["CallerIsFc"]);
    }

    // C2：成员访问（"DB".A[1]、#X.Q[2]、'x'.A[1]）不是绝对输出区

    [Theory]
    [InlineData("&quot;PLC=&gt;Cmd&quot;.A[1] := TRUE")]
    [InlineData("#X.Q[2] := TRUE")]
    [InlineData("&quot;DB&quot;.QB[3] := 0")]
    [InlineData("'x'.A[1] := TRUE")]
    public void GraphActionMemberAccessIsNotAnIndirectOutputWrite(string text)
    {
        var raw = $"""<Actions><Title /><Action Qualifier="N"><Token Text="{text}" /></Action></Actions>""";
        var (source, contracts) = Project(Kind("FB", "FB_Seq", "GRAPH", raw));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("UnresolvedQWrite"));
    }

    [Fact]
    public void GraphActionWithAnAbsoluteIndirectOutputStillBlocks()
    {
        var raw = """<Actions><Title /><Action Qualifier="N"><Token Text="QW[#Idx] := 0" /></Action></Actions>""";
        var (source, contracts) = Project(Kind("FB", "FB_Seq", "GRAPH", raw));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["UnresolvedQWrite"]);
    }

    [Theory]
    [InlineData("      T #Buf.QB[1];")]
    [InlineData("      = \"DB\".A[1];")]
    [InlineData("      T 'x'.QB[1];")]
    public void StlMemberAccessOperandIsNotAnIndirectOutputWrite(string text)
    {
        var (source, contracts) = Project(Kind("FB", "Blk", "STL", text));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.False(report.NoteCounts.ContainsKey("UnresolvedQWrite"));
    }

    // ---- 整分支审查 I-1：「校验器输出 → 代入 → 合并」端到端，第 0 层全局实参 ----

    [Fact]
    public void ProvenContradictionAtGlobalActualsYieldsTheAlarmButNoRule()
    {
        // FB_V 直接在 OB1 里调用（全局实例），I1/I2 接 %I：第一期已产规则，新流程只出到位矛盾报警
        var (two, _) = TwoLayerContradiction(null, null);
        var ob = new FlgNetBuilder { CallInstanceScope = "GlobalVariable" };
        ob.CallWired("FB_V", "IDB_V", ("I1", "Input", "Bool", "I_In"), ("I2", "Input", "Bool", "I_In2"), ("A_Word", "Output", "Bool", "DB_A.Cyl"));
        var source = two with
        {
            Units = two.Units.Where(u => u.BlockName == "FB_V")
                .Append(new PipelineUnit(12, "OB1", 120, "LAD", ob.Build(), "", ["FB_V"], 1, "", "OB")).ToArray()
        };
        var contracts = PhaseOneContradiction(source, "A_Word.Alarm1");
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        var a = Assert.Single(report.Alarms);
        Assert.Equal("PositionConflict", a.Alarm.Kind);
        Assert.Equal("DB_A.Cyl.Alarm1", a.Alarm.AlarmSymbol);
        Assert.Equal(new[] { "I_In", "I_In2" }, a.Alarm.Feedback);
    }

    // ---- 整分支审查 I-3：反馈规则因 %Q 多写入/间接写被丢时，超时报警保留但不带输出 ----

    [Fact]
    public void TimeoutAlarmLosesItsOutputWhenTheQIsWrittenElsewhere()
    {
        var (source, contracts) = MultiInstance("IDB_Mid");
        source = source with { Units = [.. source.Units, CoilUnit("Q_Out")] };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["QWrittenElsewhere"]);
        var a = Assert.Single(report.Alarms);
        Assert.Equal("", a.Alarm.Output);
        Assert.Equal(new[] { "I_In" }, a.Alarm.Feedback);
        Assert.Equal(1, report.NoteCounts["AlarmOutputNotUniqueQ"]);
        Assert.Null(Assert.Single(AlarmCatalogBuilder.Build(report.Alarms)).OutputSignal);
    }

    [Fact]
    public void TimeoutAlarmLosesItsOutputWhenAQWriteIsUnresolved()
    {
        var (source, contracts) = MultiInstance("IDB_Mid");
        source = source with { Units = [.. source.Units, new PipelineUnit(13, "Blk", 130, "STL", "      T QB[AR1,P#0.0];", "", [], 1, "", "FB")] };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["UnresolvedQWrite"]);
        Assert.Equal("", Assert.Single(report.Alarms).Alarm.Output);
    }

    [Fact]
    public void TimeoutAlarmKeepsItsOutputWhenTheFeedbackRuleSurvives()
    {
        var (source, contracts) = MultiInstance("IDB_Mid");
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(1, result.RuleCount);
        Assert.Equal("Q_Out", Assert.Single(report.Alarms).Alarm.Output);
        Assert.False(report.NoteCounts.ContainsKey("AlarmOutputNotUniqueQ"));
    }

    // ---- 整分支审查 Minor-1：调用方块有跳转/返回时按 EN 有条件处理 ----

    [Fact]
    public void CallerWithControlFlowYieldsNothing()
    {
        // OB1 另一个程序段里有「返回」：调用可能被跳过 → 不代入（不产规则、不派生、不出报警）
        var (source, contracts) = Project();
        var ret = new FlgNetBuilder();
        var part = ret.Part("Return");
        ret.Wire(FlgNetBuilder.Rail(), FlgNetBuilder.Pin(part, "in"));
        var unit = new PipelineUnit(14, "OB1", 121, "LAD", ret.Build(), "", [], 2, "", "OB");
        Assert.Contains(UnitTranslator.Translate(unit).Statements, s => s.Kind == IrStatementKinds.Return);
        source = source with { Units = [.. source.Units, unit] };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(0, result.RuleCount);
        Assert.Equal(1, report.NoteCounts["CallerHasControlFlow"]);
    }

    // ---- 整分支复审 N-1：AI 指错报警位 → 关系照样跨层派生，但不出报警 ----

    [Fact]
    public void WrongAiAlarmIsDroppedButTheDerivedRuleStays()
    {
        var (source, contracts) = TwoLayerContradiction(null, null, aiAlarm: "A_Word.Alarm2");
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal("AT_MOST_ONE", Assert.Single(result.Units.Single(u => u.LogicUnitId == 120).Rules).Operator);
        Assert.Empty(report.Alarms);
    }

    // ---- 已知问题第三批：锁存报警位进报警清单 ----

    static PipelineSnapshot AlarmProject(string setKind, string resetKind)
    {
        var set = new FlgNetBuilder();
        foreach (var n in new[] { "Err1", "Err2", "Err3" }) set.Series("SCoil", "AlarmDB." + n, ("I_" + n, false));
        var reset = new FlgNetBuilder();
        foreach (var n in new[] { "Err1", "Err2", "Err3" }) reset.Series("RCoil", "AlarmDB." + n, ("Ack", false));
        return new PipelineSnapshot(1,
        [
            new(10, "Blk_Set", 100, "LAD", set.Build(), "", [], 3, "", setKind) { CompileUnitOrder = 4 },
            new(11, "Blk_Reset", 101, "LAD", reset.Build(), "", [], 1, "", resetKind) { CompileUnitOrder = 1 },
        ],
        [new("I_Err1", "%I0.0", "Bool"), new("I_Err2", "%I0.1", "Bool"), new("I_Err3", "%I0.2", "Bool")])
        {
            DbInterfaces = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["AlarmDB"] = "<Interface><Sections><Section Name=\"Static\"><Member Name=\"Err1\" Datatype=\"Bool\" />"
                    + "<Member Name=\"Err2\" Datatype=\"Bool\" /><Member Name=\"Err3\" Datatype=\"Bool\" /></Section></Sections></Interface>"
            }
        };
    }

    [Fact]
    public void LatchedBitGroupInAGlobalDbBecomesCatalogAlarmsAtTheirSetUnit()
    {
        var source = AlarmProject("FB", "FB");
        var (_, report) = UnderstandingRuleSource.Merge(source, Empty(source), new Dictionary<string, FbContractOutcome>(), "m1");
        Assert.Equal(new[] { "AlarmDB.Err1", "AlarmDB.Err2", "AlarmDB.Err3" }, report.Alarms.Select(a => a.Alarm.AlarmSymbol).ToArray());
        var alarm = report.Alarms[0];
        Assert.Equal("Latched", alarm.Alarm.Kind);
        Assert.Equal(100, alarm.UnitId);
        Assert.Equal(4, alarm.DbNetwork);
        Assert.Equal("", alarm.Callee);
        Assert.Equal(3, report.NoteCounts["LatchedAlarm"]);
        var row = AlarmCatalogBuilder.Build(report.Alarms)[0];
        Assert.Equal(("Blk_Set", (int?)4), (row.SourceBlock, row.SourceNetwork));
    }

    [Fact]
    public void LatchedBitGroupWrittenInFcIsStillACatalogAlarm()
    {
        // 用户 2026-10-02「看 FC」：认报警位时 FC 里的置位/复位也算
        var source = AlarmProject("FC", "FC");
        var (_, report) = UnderstandingRuleSource.Merge(source, Empty(source), new Dictionary<string, FbContractOutcome>(), "m1");
        Assert.Equal(3, report.Alarms.Count(a => a.Alarm.Kind == "Latched"));
    }
}
