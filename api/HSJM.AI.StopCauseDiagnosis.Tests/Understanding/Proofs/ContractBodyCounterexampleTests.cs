#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;
using static HSJM.AI.StopCauseDiagnosis.Tests.FlgNetBuilder;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Proofs;

/// <summary>Task 16 迭代 1：AI 契约互斥与被调块程序体核对——块内只用输入形参就能让两个输出同时为真时，
/// 互斥只可能由调用点实参成立，调用点代码证明不成立就不产（与可满足性判定同时上线）。</summary>
public class ContractBodyCounterexampleTests
{
    static readonly PipelineTag[] Tags =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("I_X", "%I0.2", "Bool"), new("I_Y", "%I0.3", "Bool"),
    ];

    static Original353ProjectResult Empty(PipelineSnapshot s) =>
        new(1, "v", "fp", s.Units.Select(u => new Original353UnitResult(u.Id, [], [], [])).ToArray(), 0, 0, []);

    /// <summary>FB_T：O1 := X（∧ ¬Y 时块内互斥）、O2 := Y。</summary>
    static PipelineUnit Body(bool exclusive)
    {
        var low = new FlgNetBuilder();
        if (exclusive) low.Series("Coil", "O1", ("X", false), ("Y", true));
        else low.Series("Coil", "O1", ("X", false));
        low.Series("Coil", "O2", ("Y", false));
        return new(10, "FB_T", 110, "LAD", low.Build(), "", [], 1, FlgNetBuilder.Interface(["X", "Y"], ["O1", "O2"]), "FB");
    }

    static AiContractRelation Mutex(PipelineSnapshot source) =>
        new("mutex", ["O1", "O2"], [], null, null, null, null, IrBlock.Load(source, "FB_T").StatementsById.Keys.ToArray(), null);

    static ValidatedRelation Validate(PipelineSnapshot source) =>
        Assert.Single(FbContractValidator.Validate(new AiFbContract("FB_T", "", [Mutex(source)]), IrBlock.Load(source, "FB_T"),
            FbContract.Empty("FB_T")).Accepted);

    [Fact]
    public void MutexThatTheBodyAllowsToBreakRequiresACallSiteProof()
    {
        Assert.True(Validate(new PipelineSnapshot(1, [Body(exclusive: false)], Tags)).RequiresCallSiteProof);
        Assert.False(Validate(new PipelineSnapshot(1, [Body(exclusive: true)], Tags)).RequiresCallSiteProof);
    }

    /// <summary>OB1 调用 FB_T：X ← I_X；Y ← ¬I_X（negatedY）或 OB 的 Temp T1（程序不写、取值说不清）。</summary>
    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts) Project(bool exclusive, bool negatedY)
    {
        var ob = new FlgNetBuilder();
        var call = ob.CallWired("FB_T", "DB_T", ("X", "Input", "Bool", "I_X"), ("Y", "Input", "Bool", negatedY ? null : "T1"),
            ("O1", "Output", "Bool", "Q1"), ("O2", "Output", "Bool", "Q2"));
        if (negatedY)
        {
            var c = ob.Chain(Rail(), ("I_X", true));
            ob.Wire(Pin(c, "out"), Pin(call, "Y"));
        }
        var source = new PipelineSnapshot(1,
        [
            Body(exclusive),
            new(11, "OB1", 120, "LAD", ob.Build(), "", ["FB_T"], 1, FlgNetBuilder.Interface([], [], temps: ["T1"]), "OB"),
        ], Tags);
        var validated = FbContractValidator.Validate(new AiFbContract("FB_T", "", [Mutex(source)]), IrBlock.Load(source, "FB_T"), FbContract.Empty("FB_T"));
        return (source, new Dictionary<string, FbContractOutcome>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["FB_T"] = new("FB_T", FbContractStatus.Ok, validated, "ck", null)
        });
    }

    static PipelineRule? CallerMutex(Original353ProjectResult result) =>
        result.Units.Single(u => u.LogicUnitId == 120).Rules.SingleOrDefault(r => r.Operator == "AT_MOST_ONE");

    [Fact]
    public void ProvenAtTheCallSiteIsKept()
    {
        var (source, contracts) = Project(exclusive: false, negatedY: true);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Equal(UnderstandingProofs.CodeCallSite, CallerMutex(result)?.Proof);
        Assert.DoesNotContain("MutexBodyCounterexampleUnproven", report.NoteCounts.Keys);
    }

    [Fact]
    public void NotProvenAtTheCallSiteIsDroppedWhenTheBodyAllowsBoth()
    {
        var (source, contracts) = Project(exclusive: false, negatedY: false);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Null(CallerMutex(result));
        Assert.Equal(1, report.NoteCounts["MutexBodyCounterexampleUnproven"]);
        Assert.Contains(report.RefutedRules, l => l.Contains("MutexBodyCounterexampleUnproven") && l.Contains("Q1") && l.Contains("Q2"));
    }

    [Fact]
    public void BodyEnforcedMutexIsNotFlagged()
    {
        // 块内互斥（O1 := X ∧ ¬Y）：不要求调用点证明；同一次调用的两个输出在调用点也证得出
        var (source, contracts) = Project(exclusive: true, negatedY: false);
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.NotNull(CallerMutex(result));
        Assert.DoesNotContain("MutexBodyCounterexampleUnproven", report.NoteCounts.Keys);
    }

    [Fact]
    public void CallSiteCounterexampleIsListedAsRefuted()
    {
        // Y ← I_X：两个输出都等于 I_X，调用点有反例
        var ob = new FlgNetBuilder();
        ob.CallWired("FB_T", "DB_T", ("X", "Input", "Bool", "I_X"), ("Y", "Input", "Bool", "I_X"),
            ("O1", "Output", "Bool", "Q1"), ("O2", "Output", "Bool", "Q2"));
        var source = new PipelineSnapshot(1, [Body(false), new(11, "OB1", 120, "LAD", ob.Build(), "", ["FB_T"], 1, "", "OB")], Tags);
        var validated = FbContractValidator.Validate(new AiFbContract("FB_T", "", [Mutex(source)]), IrBlock.Load(source, "FB_T"), FbContract.Empty("FB_T"));
        var contracts = new Dictionary<string, FbContractOutcome> { ["FB_T"] = new("FB_T", FbContractStatus.Ok, validated, "ck", null) };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.Null(CallerMutex(result));
        Assert.Contains(report.RefutedRules, l => l.Contains("MutexRefutedByIr"));
    }
}
