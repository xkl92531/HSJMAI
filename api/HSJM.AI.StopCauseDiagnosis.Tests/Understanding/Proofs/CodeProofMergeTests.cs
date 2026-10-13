#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

public class CodeProofMergeTests
{
    static readonly PipelineTag[] Tags =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("Q3", "%Q0.2", "Bool"), new("Q4", "%Q0.3", "Bool"),
        new("A", "%I0.0", "Bool"), new("B", "%I0.1", "Bool"), new("I_X", "%I0.2", "Bool"), new("I_Y", "%I0.3", "Bool"),
        new("K", "%M1.3", "Bool")
    ];

    static Original353ProjectResult Empty(PipelineSnapshot s, params (long Unit, PipelineRule Rule)[] rules) =>
        new(1, "v", "fp", s.Units.Select(u => new Original353UnitResult(u.Id, rules.Where(r => r.Unit == u.Id).Select(r => r.Rule).ToArray(), [], [])).ToArray(),
            rules.Length, 0, []);

    static readonly Dictionary<string, FbContractOutcome> NoContracts = new();

    // OB1 网络 1：Q1 := A ∧ B，Q2 := A ∧ ¬B（A、B 为程序不写的 %I，段内稳定）→ 两输出互斥。
    // 不用「Q1 := A ∧ ¬Q2」的段内互串写法：FlgNet 段里本段写过的变量每次读取都是独立的自由值（Task 4 裁定），那种互锁不能代码证明
    static PipelineUnit MutexUnit(string blockKind = "OB")
    {
        var b = new FlgNetBuilder();
        b.Series("Coil", "Q1", ("A", false), ("B", false));
        b.Series("Coil", "Q2", ("A", false), ("B", true));
        return new PipelineUnit(1, "OB1", 100, "LAD", b.Build(), "", [], 1, "", blockKind);
    }

    [Fact]
    public void PlainNetworkMutexIsProvenAndActive()
    {
        var source = new PipelineSnapshot(1, [MutexUnit()], Tags);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), NoContracts, "m1");
        var rule = Assert.Single(result.Units.Single().Rules);
        Assert.Equal("AT_MOST_ONE", rule.Operator);
        Assert.Equal(UnderstandingProofs.CodeNetwork, rule.Proof);
        Assert.Equal("Active", Original353ActivationPolicy.Classify(rule, Tags).Status);
        Assert.Equal(1, report.NoteCounts["CodeProof:AT_MOST_ONE:Code:Network"]);
    }

    [Fact]
    public void SameStateContradictingAProvenMutexIsRemoved()   // 样例 W2 的结构
    {
        var source = new PipelineSnapshot(1, [MutexUnit()], Tags);
        var old = new PipelineRule("SAME_STATE", [new("Q1", "Member"), new("Q2", "Member")], "旧");
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source, (100, old)), NoContracts, "m1");
        Assert.DoesNotContain(result.Units.Single().Rules, r => r.Operator == "SAME_STATE");
        Assert.Equal(1, report.NoteCounts["SameStateContradictsMutex"]);
    }

    [Fact]
    public void RulesOnConstantOutputsAreRemoved()   // 样例 W3 的结构
    {
        var b = new FlgNetBuilder();
        b.Series("Coil", "Q3", ("K", false));
        b.Series("Coil", "Q4", ("K", false));
        var unit = new PipelineUnit(1, "OB1", 100, "LAD", b.Build(), "", [], 1, "", "OB");
        var source = new PipelineSnapshot(1, [unit], Tags);
        var old = new PipelineRule("SAME_STATE", [new("Q3", "Member"), new("Q4", "Member")], "旧");
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source, (100, old)), NoContracts, "m1");
        Assert.Empty(result.Units.Single().Rules);
        Assert.Equal(1, report.NoteCounts["ConstantOutputRule"]);
    }

    // FB_T：两个输出；OB1 调用它并把输出接到 Q1/Q2
    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts) CallProject(bool exclusive)
    {
        var low = new FlgNetBuilder();
        if (exclusive) low.Series("Coil", "O1", ("X", false), ("Y", true));
        else low.Series("Coil", "O1", ("X", false));
        low.Series("Coil", "O2", ("Y", false));
        var ob = new FlgNetBuilder();
        ob.CallWired("FB_T", "DB_T", ("X", "Input", "Bool", "I_X"), ("Y", "Input", "Bool", "I_Y"),
            ("O1", "Output", "Bool", "Q1"), ("O2", "Output", "Bool", "Q2"));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_T", 110, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["X", "Y"], ["O1", "O2"]), "FB"),
            new(11, "OB1", 120, "LAD", ob.Build(), "", ["FB_T"], 1, "", "OB"),
        ], Tags);
        var ids = IrBlock.Load(source, "FB_T").StatementsById.Keys.ToArray();
        var mutex = new AiContractRelation("mutex", ["O1", "O2"], [], null, null, null, null, ids, null);
        return (source, new Dictionary<string, FbContractOutcome>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["FB_T"] = new("FB_T", FbContractStatus.Ok, new ValidatedFbContract("FB_T", [new ValidatedRelation(mutex, false, null)]), "ck", null)
        });
    }

    [Fact]
    public void AiMutexWithACounterexampleIsDropped()   // 样例 W1 的结构
    {
        var (source, contracts) = CallProject(exclusive: false);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.DoesNotContain(result.Units.Single(u => u.LogicUnitId == 120).Rules, r => r.Operator == "AT_MOST_ONE");
        Assert.Equal(1, report.NoteCounts["MutexRefutedByIr"]);
    }

    [Fact]
    public void ProvableCallSiteMutexIsCodeLevel()
    {
        var (source, contracts) = CallProject(exclusive: true);
        var (result, _) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        var rule = Assert.Single(result.Units.Single(u => u.LogicUnitId == 120).Rules, r => r.Operator == "AT_MOST_ONE");
        Assert.Equal(UnderstandingProofs.CodeCallSite, rule.Proof);
    }

    // ---------- 裁定补充 ----------

    [Fact]
    public void FcBlockNetworksAreNotProven()   // 用户：FC 完全不管
    {
        var source = new PipelineSnapshot(1, [MutexUnit("FC")], Tags);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), NoContracts, "m1");
        Assert.Empty(result.Units.Single().Rules);
        Assert.DoesNotContain(report.NoteCounts.Keys, k => k.StartsWith("CodeProof:"));
    }

    [Fact]
    public void CodeProofReplacesASameKeyAiRuleFromTheOldFlow()
    {
        var source = new PipelineSnapshot(1, [MutexUnit()], Tags);
        var old = new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q2", "Member")], "旧") { Proof = UnderstandingProofs.AiFbContract };
        var (result, _) = UnderstandingRuleSource.Merge(source, Empty(source, (100, old)), NoContracts, "m1");
        Assert.Equal(UnderstandingProofs.CodeNetwork, Assert.Single(result.Units.Single().Rules).Proof);
    }

    [Fact]
    public void ReviewLevelCallSiteMutexDoesNotRefuteSameState()   // F10/G8：只有 Code:* 的互斥能证伪 SAME_STATE
    {
        // FB_T 的 O1 := X ∨ Y、O2 := ¬X；调用时 Y 未接线（Bool 初值 FALSE）→ 互斥只在代入初值后成立 → Review:CallSiteDefaults
        var low = new FlgNetBuilder();
        low.Coil("Coil", "O1", FlgNetBuilder.Pin(low.Or("X", "Y"), "out"));
        low.Series("Coil", "O2", ("X", true));
        var ob = new FlgNetBuilder();
        ob.CallWired("FB_T", "DB_T", ("X", "Input", "Bool", "I_X"), ("Y", "Input", "Bool", null),
            ("O1", "Output", "Bool", "Q1"), ("O2", "Output", "Bool", "Q2"));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_T", 110, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["X", "Y"], ["O1", "O2"]), "FB"),
            new(11, "OB1", 120, "LAD", ob.Build(), "", ["FB_T"], 1, "", "OB"),
        ], Tags);
        var old = new PipelineRule("SAME_STATE", [new("Q1", "Member"), new("Q2", "Member")], "旧");
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source, (120, old)), NoContracts, "m1");
        var rules = result.Units.Single(u => u.LogicUnitId == 120).Rules;
        Assert.DoesNotContain(rules, r => r.Proof?.StartsWith("Code:") == true);
        Assert.Contains(rules, r => r.Operator == "SAME_STATE");
        Assert.False(report.NoteCounts.ContainsKey("SameStateContradictsMutex"));
        Assert.Equal(1, report.NoteCounts["CodeProof:AT_MOST_ONE:Review:CallSiteDefaults"]);   // 确有一条待确认的互斥
    }

    [Theory]
    [InlineData("AT_MOST_ONE", UnderstandingProofs.AiFbContract, false, 2, true)]
    [InlineData("AT_MOST_ONE", UnderstandingProofs.AiFbContract, true, 2, false)]   // 到位矛盾（延时规则，F16）
    [InlineData("AT_MOST_ONE", "FbContract:Mutex", false, 2, false)]
    [InlineData("AT_MOST_ONE", UnderstandingProofs.AiFbContract, false, 3, false)]
    [InlineData("SAME_STATE", UnderstandingProofs.AiFbContract, false, 2, false)]
    public void OnlyInstantAiMutexesGoThroughTheIrVerdict(string op, string proof, bool settling, int members, bool expected)
    {
        var rule = new PipelineRule(op, Enumerable.Range(1, members).Select(i => new PipelineSignal("Q" + i, "Member")).ToArray(), "x")
            { Proof = proof, RequiresSettlingEvidence = settling };
        Assert.Equal(expected, UnderstandingRuleSource.NeedsIrMutexVerdict(rule));
    }

    // K5：FC_M 网络 1 调 FB_A（Q1 := A）、网络 2 调 FB_B（Q2 := Q1）→ 在 FB_B 网络里产 Code:CallOrder 同步，判故障级
    [Fact]
    public void CrossBlockCopyAfterTheWriterIsCalledIsMergedAsCallOrderSameState()
    {
        PipelineUnit Coil(long id, string block, string target, string contact)
        {
            var b = new FlgNetBuilder();
            b.Series("Coil", target, (contact, false));
            return new PipelineUnit(id, block, id + 100, "LAD", b.Build(), "", [], 1, FlgNetBuilder.Interface([], []), "FB");
        }
        PipelineUnit Calls(long id, int network, string callee)
        {
            var b = new FlgNetBuilder { CallInstanceScope = "GlobalVariable" };
            b.CallWired(callee, "IDB_" + callee);
            return new PipelineUnit(id, "FC_M", id + 100, "LAD", b.Build(), "", [callee], network, "", "FC");
        }
        var source = new PipelineSnapshot(1, [Coil(1, "FB_A", "Q1", "A"), Coil(2, "FB_B", "Q2", "Q1"), Calls(3, 1, "FB_A"), Calls(4, 2, "FB_B")], Tags);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), NoContracts, "m1");
        var rule = Assert.Single(result.Units.Single(u => u.LogicUnitId == 102).Rules);
        Assert.Equal("SAME_STATE", rule.Operator);
        Assert.Equal(new[] { "Q1", "Q2" }, rule.Signals.Select(s => s.Name).Order().ToArray());
        Assert.Equal(UnderstandingProofs.CodeCallOrder, rule.Proof);
        Assert.Equal("Active", Original353ActivationPolicy.Classify(rule, Tags).Status);
        Assert.Equal(1, report.NoteCounts["CodeProof:SAME_STATE:Code:CallOrder"]);
        Assert.DoesNotContain(result.Units.Where(u => u.LogicUnitId != 102).SelectMany(u => u.Rules), r => r.Proof == UnderstandingProofs.CodeCallOrder);
    }
}
