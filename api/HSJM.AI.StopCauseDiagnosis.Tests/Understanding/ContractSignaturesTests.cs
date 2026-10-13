#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class ContractSignaturesTests
{
    static AiContractRelation Feedback(string evidence) =>
        new("feedback", ["O_FwdOut"], [], "Cmd.IWP", "C_FwdPresetTime", null, null, [evidence], null);

    static FbContractOutcome Ok(string fb, params ValidatedRelation[] relations) =>
        new(fb, FbContractStatus.Ok, new ValidatedFbContract(fb, relations), "k", null);

    [Fact]
    public void SameRelationsWithDifferentEvidenceAreConsistent()
    {
        var a = new Dictionary<string, FbContractOutcome> { ["FB_A"] = Ok("FB_A", new ValidatedRelation(Feedback("FB_A/5/1"), false, null)) };
        var b = new Dictionary<string, FbContractOutcome> { ["FB_A"] = Ok("FB_A", new ValidatedRelation(Feedback("FB_A/6/2"), false, null)) };
        Assert.Empty(ContractSignatures.Diff(a, b));
    }

    [Fact]
    public void StatusOrAcceptedRelationChangesAreReported()
    {
        var gate = new AiContractRelation("gate", ["O_FwdOut"], ["I_Estop"], null, null, null, null, ["FB_A/7/1"], null);
        var a = new Dictionary<string, FbContractOutcome>
        {
            ["FB_A"] = Ok("FB_A", new ValidatedRelation(Feedback("e"), false, null), new ValidatedRelation(gate, false, null)),
            ["FB_B"] = Ok("FB_B", new ValidatedRelation(Feedback("e"), false, null)),
        };
        var b = new Dictionary<string, FbContractOutcome>
        {
            ["FB_A"] = Ok("FB_A", new ValidatedRelation(Feedback("e"), false, null), new ValidatedRelation(gate, false, "GateNotNecessary")),   // 被拒的不计
            ["FB_B"] = new("FB_B", FbContractStatus.Failed, null, "k", "超时"),
        };
        var diff = ContractSignatures.Diff(a, b);
        Assert.Equal(new[] { "FB_A", "FB_B" }, diff.Select(d => d.Fb).ToArray());
        Assert.Contains(diff[0].OnlyA, s => s.StartsWith("gate|", System.StringComparison.Ordinal));
        Assert.Empty(diff[0].OnlyB);
        Assert.Contains("status:Failed", diff[1].OnlyB);
    }
}
