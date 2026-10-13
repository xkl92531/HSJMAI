#nullable enable
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class RetirementAuditTests
{
    static EvalFact F(string key, string block = "FB1", int? network = 1) => new("Mutex", key, key, "c", block, network, null, "XMLV2");

    static readonly EvalFact[] OldAll = [F("A"), F("B"), F("C"), F("D", "FB9", 9), F("E")];
    static readonly EvalFact[] OldWithout = [F("E")];          // A~D 只由该族产出
    static readonly EvalFact[] NewWithout = [F("A"), F("E")];  // 关掉该族后新流程仍产出 A

    static string Verdict(System.Collections.Generic.IReadOnlyList<RetirementItem> items, string key) =>
        items.Single(i => i.Fact.Key == key).Verdict;

    [Fact]
    public void SampledTruthGivesFourVerdicts()
    {
        var truth = new TruthFile("t", "s", "now", [F("B")], [new EvalUnitRef("FB1", 1)]);
        var items = RetirementAudit.Classify(NameRuleFamily.PinNameAugment, OldAll, OldWithout, NewWithout, truth);
        Assert.Equal(RetirementVerdict.Covered, Verdict(items, "A"));
        Assert.Equal(RetirementVerdict.Keep, Verdict(items, "B"));
        Assert.Equal(RetirementVerdict.SuspectWrong, Verdict(items, "C"));
        Assert.Equal(RetirementVerdict.Undecided, Verdict(items, "D"));      // FB9 网络 9 不在抽样范围
        Assert.DoesNotContain(items, i => i.Fact.Key == "E");               // 关掉后仍由别的族产出，不算独有
        Assert.All(items, i => Assert.Equal(NameRuleFamily.PinNameAugment, i.Family));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameKeyInSeveralUnitsIsInScopeWhenAnyOccurrenceIs(bool inScopeFirst)
    {
        // 同一约束在两个程序段各出一次，一个在抽样范围内（FB1/1）一个不在（FB9/9）：结论不随出现顺序变化
        var inScope = F("M"); var outOfScope = F("M", "FB9", 9);
        EvalFact[] oldAll = inScopeFirst ? [inScope, outOfScope] : [outOfScope, inScope];
        var truth = new TruthFile("t", "s", "now", [F("B")], [new EvalUnitRef("FB1", 1)]);
        var item = Assert.Single(RetirementAudit.Classify("X", oldAll, [], [], truth));
        Assert.Equal(RetirementVerdict.SuspectWrong, item.Verdict);
        Assert.Equal(("FB1", (int?)1), (item.Fact.Block, item.Fact.Network));   // 列出的是落在范围内的那一处
    }

    [Fact]
    public void WholeProjectTruthPutsEveryUnitInScope()
    {
        var truth = new TruthFile("t", "s", "now", [F("B")]);
        Assert.Equal(RetirementVerdict.SuspectWrong, Verdict(RetirementAudit.Classify("X", OldAll, OldWithout, NewWithout, truth), "D"));
    }

    [Fact]
    public void WithoutTruthOnlyCoverageIsJudged()
    {
        var items = RetirementAudit.Classify("X", OldAll, OldWithout, NewWithout, null);
        Assert.Equal(RetirementVerdict.Covered, Verdict(items, "A"));
        Assert.All(items.Where(i => i.Fact.Key != "A"), i => Assert.Equal(RetirementVerdict.Undecided, i.Verdict));
    }
}
