#nullable enable
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class TruthFileScopeTests
{
    static EvalFact Fact(string key, string block, int? network) =>
        new("Mutex", key, key, "阀门", block, network, [], "test");

    [Fact]
    public void TruthFileWithoutUnitsStillDeserializes()
    {
        const string json = """
        {"Project":"formal:1","Source":"s","CreatedAt":"2026-10-01 10:00",
         "Facts":[{"Kind":"Mutex","Key":"Mutex|A|B","Display":"d","Category":"阀门","Block":"FB1","Network":2,"Preconditions":null}]}
        """;
        var file = JsonSerializer.Deserialize<TruthFile>(json, TruthJson.Options)!;
        Assert.Null(file.Units);
        Assert.Equal("Mutex|A|B", Assert.Single(file.Facts).Key);
    }

    [Fact]
    public void TruthFileUnitsRoundTrip()
    {
        var file = new TruthFile("p", "s", "t", [Fact("Mutex|A|B", "FB1", 2)], [new EvalUnitRef("FB1", 2), new EvalUnitRef("FB #2", 5)]);
        var back = JsonSerializer.Deserialize<TruthFile>(JsonSerializer.Serialize(file, TruthJson.Options), TruthJson.Options)!;
        Assert.Equal([new EvalUnitRef("FB1", 2), new EvalUnitRef("FB #2", 5)], back.Units!);
        Assert.Equal(file.Facts.Select(f => f.Key), back.Facts.Select(f => f.Key));
    }

    [Fact]
    public void TruthFileWithoutUnitsRoundTripKeepsNull()
    {
        var file = new TruthFile("p", "s", "t", [Fact("Mutex|A|B", "FB1", 2)]);
        var back = JsonSerializer.Deserialize<TruthFile>(JsonSerializer.Serialize(file, TruthJson.Options), TruthJson.Options)!;
        Assert.Null(back.Units);
    }

    [Fact]
    public void ScopeWithoutUnitsUsesProgramUnitsOfTruthFacts()
    {
        var truth = new TruthFile("p", "s", "t", [Fact("Mutex|A|B", "FB1", 2)]);
        var produced = new[] { Fact("Mutex|A|B", "FB1", 2), Fact("Mutex|C|D", "FB1", 3), Fact("Mutex|E|F", "FB9", 2) };
        Assert.Equal(["Mutex|A|B"], LogicEvaluator.Scope(truth, produced).Select(f => f.Key));
    }

    [Fact]
    public void ScopeWithUnitsKeepsProductionOfSampledUnitsThatHaveNoTruthFacts()
    {
        // FB1 网络 3 抽样后标注为零条约束：它的产出全部是错的，仍须计入准确率
        var truth = new TruthFile("p", "s", "t", [Fact("Mutex|A|B", "FB1", 2)], [new EvalUnitRef("FB1", 2), new EvalUnitRef("FB1", 3)]);
        var produced = new[] { Fact("Mutex|A|B", "FB1", 2), Fact("Mutex|C|D", "FB1", 3), Fact("Mutex|E|F", "FB9", 2) };
        Assert.Equal(["Mutex|A|B", "Mutex|C|D"], LogicEvaluator.Scope(truth, produced).Select(f => f.Key));
    }

    [Fact]
    public void ScopeWithUnitsIsExactlyThoseUnits()
    {
        // 标准答案事实所在的程序段不在 Units 里时不纳入（Units 即抽样范围）
        var truth = new TruthFile("p", "s", "t", [Fact("Mutex|A|B", "FB1", 2)], [new EvalUnitRef("FB1", 3)]);
        var produced = new[] { Fact("Mutex|A|B", "FB1", 2), Fact("Mutex|C|D", "FB1", 3), Fact("Mutex|G|H", "FB1", null) };
        Assert.Equal(["Mutex|C|D"], LogicEvaluator.Scope(truth, produced).Select(f => f.Key));
    }

    [Fact]
    public void EmptyUnitsListScopesToNothing()
    {
        var truth = new TruthFile("p", "s", "t", [Fact("Mutex|A|B", "FB1", 2)], []);
        Assert.Empty(LogicEvaluator.Scope(truth, [Fact("Mutex|A|B", "FB1", 2)]));
    }
}
