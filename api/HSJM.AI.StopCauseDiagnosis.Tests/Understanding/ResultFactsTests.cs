#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class ResultFactsTests
{
    static readonly PipelineTag[] Tags = [new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("I1", "%I0.0", "Bool")];
    static readonly CategoryMap Map = new(new Dictionary<string, string> { ["FB_V"] = "阀门气缸" }, new Dictionary<string, string>(), "普通逻辑");

    static PipelineSnapshot Source() =>
        new(1, [new PipelineUnit(10, "FB1", 100, "LAD", "<FlgNet/>", "", ["FB_V"], 3, "", "FB")], Tags);

    static Original353ProjectResult Result(Original353UnitResult unit) => new(1, "v", "fp", [unit], unit.Rules.Count, 0, []);

    [Fact]
    public void RulesAndTimeoutAlarmsBecomeFactsWithTheirOrigin()
    {
        var mutex = new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q2", "Member")], "");
        var feedback = new PipelineRule("IMPLIES", [new("Q1", "Source"), new("I1", "Target")], "")
            { Applicability = "AfterSettling", RequiresSettlingEvidence = true, BypassSignals = ["DB.B"] };
        var alarm = new UnderstandingAlarm(new CallSiteAlarm("DB.A.Alarm1", "Timeout", "Q1", ["I1"], "10", ["FB_V/5/1"]) { Bypass = ["db.b"] },
            100, "FB1", 3, "Valve[1]", "FB_V");
        var conflict = new UnderstandingAlarm(new CallSiteAlarm("DB.A.Alarm3", "PositionConflict", "", ["I1"], "2", []),
            100, "FB1", 3, "Valve[1]", "FB_V");

        var facts = ResultFacts.From(Source(), Result(new Original353UnitResult(100, [mutex, feedback], [], [])), [alarm, conflict], Map);

        var m = Assert.Single(facts, f => f.Key == "Mutex|Q1|Q2");
        Assert.Equal("XMLV2", m.Origin);
        Assert.Equal("阀门气缸", m.Category);
        Assert.Equal(("FB1", (int?)3), (m.Block, m.Network));
        var fb = Assert.Single(facts, f => f.Key == "Feedback|Q1->I1");
        Assert.Equal("XMLV2_TIMED", fb.Origin);
        Assert.Equal(new[] { "DB.B" }, fb.Preconditions);
        var a = Assert.Single(facts, f => f.Kind == "TimeoutAlarm");
        Assert.Equal("TimeoutAlarm|DB.A.Alarm1|Q1|I1", a.Key);
        Assert.Equal("ALARM", a.Origin);
        Assert.Equal(new[] { "DB.B" }, a.Preconditions);
        Assert.DoesNotContain(facts, f => f.Key.Contains("Alarm3"));   // 到位矛盾报警不计事实
    }

    [Fact]
    public void SameKeyInOneUnitKeepsTheDelayedVariant()
    {
        var instant = new PipelineRule("IMPLIES", [new("Q1", "Source"), new("I1", "Target")], "");
        var delayed = new PipelineRule("IMPLIES", [new("Q1", "Source"), new("I1", "Target")], "")
            { Applicability = "AfterSettling", RequiresSettlingEvidence = true, BypassSignals = ["B2"] };
        var unit = new Original353UnitResult(100, [instant], [], [])
            { ConditionalFeedbackRules = [new Original353ConditionalFeedback(delayed, ["B2"], "AfterSettling", true, [])] };
        var fact = Assert.Single(ResultFacts.From(Source(), Result(unit), [], Map));
        Assert.Equal(new[] { "B2" }, fact.Preconditions);
    }

    [Fact]
    public void UnitsMissingFromTheSnapshotAreSkipped()
    {
        var unit = new Original353UnitResult(999, [new PipelineRule("AT_MOST_ONE", [new("Q1", "Member"), new("Q2", "Member")], "")], [], []);
        Assert.Empty(ResultFacts.From(Source(), Result(unit), [], Map));
    }
}
