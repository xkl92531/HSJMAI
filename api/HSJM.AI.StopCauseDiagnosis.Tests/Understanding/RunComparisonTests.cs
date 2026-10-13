#nullable enable
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class RunComparisonTests
{
    static EvalFact F(string key, params string[]? pre) => new("Mutex", key, key, "c", "FB1", 1, pre, "XMLV2");

    [Fact]
    public void DiffSplitsByKeyAndFlagsPreconditionChanges()
    {
        var a = new[] { F("K1", "X"), F("K2"), F("K3") };
        var b = new[] { F("K1", "Y"), F("K2"), F("K4") };
        var d = RunComparison.Diff(a, b);
        Assert.Equal(new[] { "K3" }, d.OnlyA.Select(f => f.Key).ToArray());
        Assert.Equal(new[] { "K4" }, d.OnlyB.Select(f => f.Key).ToArray());
        Assert.Equal(new[] { "K1", "K2" }, d.Both.Select(f => f.Key).ToArray());
        var change = Assert.Single(d.PreconditionsDiffer);
        Assert.Equal(new[] { "X" }, change.A.Preconditions);
        Assert.Equal(new[] { "Y" }, change.B.Preconditions);
    }

    [Fact]
    public void DuplicateKeysCountOnceAndNullPreconditionsMatchOnlyNull()
    {
        var d = RunComparison.Diff([F("K1"), F("K1")], [new EvalFact("Mutex", "K1", "K1", "c", "FB1", 1, null, "XMLV2")]);
        Assert.Single(d.Both);
        Assert.Single(d.PreconditionsDiffer);   // [] 与 null 不同
        Assert.True(RunComparison.SamePreconditions(null, null));
        Assert.False(RunComparison.SamePreconditions(null, []));
    }

    [Fact]
    public void ReportListsBothSidesWithCounts()
    {
        var text = RunComparison.Report(RunComparison.Diff([F("K3")], [F("K4")]), "对比", "旧", "新");
        Assert.Contains("## 对比", text);
        Assert.Contains("### 只在旧（1）", text);
        Assert.Contains("### 只在新（1）", text);
        Assert.Contains("`K3`", text);
        Assert.Contains("`K4`", text);
    }
}
