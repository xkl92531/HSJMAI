#nullable enable
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding;

public class LogicEvaluatorTests
{
    static readonly Dictionary<string, string> Addr = new(StringComparer.OrdinalIgnoreCase)
    { ["YV_W"] = "%Q1.0", ["YV_H"] = "%Q1.1", ["CYL_W"] = "%I2.1", ["CYL_H"] = "%I2.0", ["CYL_W2"] = "%I2.3", ["Byp"] = "" };

    static IReadOnlyList<EvalFact> Facts(string op, (string, string)[] signals, RuleMeta? meta = null, bool known = true, string category = "阀门") =>
        ConstraintFacts.FromRule(op, signals, Addr, meta ?? RuleMeta.None, known, category, "B", 1, "test");

    [Fact]
    public void AllTrueAndMultiTargetImpliesBothBecomeAtomicFeedback()
    {
        var a = Facts("ALL_TRUE", [("YV_W", "Source"), ("CYL_W", "Member"), ("CYL_W2", "Member")]);
        var b = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target"), ("CYL_W2", "Target")]);
        Assert.Equal(["Feedback|YV_W->CYL_W", "Feedback|YV_W->CYL_W2"], a.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal(a.Select(f => f.Key).Order(StringComparer.Ordinal), b.Select(f => f.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MutexIsSplitIntoCaseInsensitivePairs()
    {
        var f = Facts("AT_MOST_ONE", [("cyl_w", "Member"), ("CYL_H", "Member"), ("CYL_W2", "Member")]);
        Assert.Equal(["Mutex|CYL_H|CYL_W", "Mutex|CYL_H|CYL_W2", "Mutex|CYL_W|CYL_W2"], f.Select(x => x.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ImpliesAnyKeepsGroups()
    {
        var f = Assert.Single(Facts("IMPLIES_ANY", [("YV_W", "Source"), ("CYL_W", "Target"), ("CYL_W2", "Target")],
            new RuleMeta([], [["CYL_W2"], ["CYL_W"]])));
        Assert.Equal("FeedbackAny|YV_W->CYL_W|CYL_W2", f.Key);
    }

    [Fact]
    public void RecallAndPrecisionPerCategory()
    {
        var truth = Facts("AT_MOST_ONE", [("YV_W", "Member"), ("YV_H", "Member")])
            .Concat(Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")]))
            .Concat(Facts("IMPLIES", [("YV_H", "Source"), ("CYL_H", "Target")])).ToArray();
        var produced = Facts("AT_MOST_ONE", [("YV_H", "Member"), ("YV_W", "Member")])
            .Concat(Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target"), ("CYL_W2", "Target")])).ToArray();
        var r = LogicEvaluator.Evaluate(truth, produced);
        var valve = Assert.Single(r.Categories);
        Assert.Equal((3, 2, 3, 2), (valve.Truth, valve.Found, valve.Produced, valve.Correct));
        Assert.Equal(2.0 / 3, valve.Recall, 3);
        Assert.Equal(["Feedback|YV_H->CYL_H"], r.Missed.Select(m => m.Key));
        Assert.Equal(["Feedback|YV_W->CYL_W2"], r.Wrong.Select(m => m.Key));
    }

    [Fact]
    public void PreconditionsAreComparedOnlyWhenTruthIsCurated()
    {
        var curated = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")], new RuleMeta(["Byp"], null));
        var uncurated = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")], known: false);
        var producedNoBypass = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")]);

        var strict = LogicEvaluator.Evaluate(curated, producedNoBypass);
        Assert.Equal((0, 0), (strict.Overall.Found, strict.Overall.Correct));

        var loose = LogicEvaluator.Evaluate(uncurated, producedNoBypass);
        Assert.Equal((1, 1, 1), (loose.Overall.Found, loose.Overall.Correct, loose.UncuratedPreconditions));
    }

    [Fact]
    public void ReportListsMetricsMissedAndWrong()
    {
        var truth = Facts("AT_MOST_ONE", [("YV_W", "Member"), ("YV_H", "Member")]);
        var text = LogicEvaluator.Report(LogicEvaluator.Evaluate(truth, []), "测试");
        Assert.Contains("| 阀门 | 1 | 0 | 0 | 0 | 0.0% | — |", text);
        Assert.Contains("Mutex|YV_H|YV_W", text);
    }

    // ---- fix round 1 ----

    [Fact]
    public void ProducedDuplicateWithMismatchingPreconditionsMakesKeyWrong()
    {
        var truth = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")], new RuleMeta(["Byp"], null));
        var produced = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")], new RuleMeta(["Byp"], null))
            .Concat(Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")])).ToArray();
        var r = LogicEvaluator.Evaluate(truth, produced);
        Assert.Equal((1, 0, 1, 0), (r.Overall.Truth, r.Overall.Found, r.Overall.Produced, r.Overall.Correct));
        Assert.Equal(["Feedback|YV_W->CYL_W"], r.Wrong.Select(w => w.Key));
        Assert.Equal(["Feedback|YV_W->CYL_W"], r.Missed.Select(m => m.Key));
    }

    [Fact]
    public void ProducedFactMatchesAnyTruthVariantOfTheSameKey()
    {
        var truth = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")])
            .Concat(Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")], new RuleMeta(["Byp"], null))).ToArray();
        var withBypass = LogicEvaluator.Evaluate(truth, Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")], new RuleMeta(["Byp"], null)));
        var withoutBypass = LogicEvaluator.Evaluate(truth, Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")]));
        Assert.Equal((1, 1, 1, 1), (withBypass.Overall.Truth, withBypass.Overall.Found, withBypass.Overall.Produced, withBypass.Overall.Correct));
        Assert.Equal((1, 1, 1, 1), (withoutBypass.Overall.Truth, withoutBypass.Overall.Found, withoutBypass.Overall.Produced, withoutBypass.Overall.Correct));
    }

    [Fact]
    public void UncuratedCounterCountsMissedTruthToo()
    {
        var uncurated = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")], known: false)
            .Concat(Facts("AT_MOST_ONE", [("YV_W", "Member"), ("YV_H", "Member")], known: false))
            .Concat(Facts("IMPLIES", [("YV_H", "Source"), ("CYL_H", "Target")])).ToArray();
        var r = LogicEvaluator.Evaluate(uncurated, []);
        Assert.Equal(2, r.UncuratedPreconditions);
        Assert.Equal(0, r.Overall.Found);
    }

    [Fact]
    public void FeedbackOrImpliesIsDecidedPerTarget()
    {
        var f = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target"), ("X", "Target")]);
        Assert.Equal(["Feedback|YV_W->CYL_W", "Implies|YV_W->X"], f.Select(x => x.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MultiSourceGateIsOneGatePerSourceAndTarget()
    {
        var both = Facts("REQUIRES_0_WHEN_FALSE", [("ES1", "Source"), ("ES2", "Source"), ("YV_W", "Target"), ("YV_H", "Target")]);
        var single = Facts("REQUIRES_0_WHEN_FALSE", [("ES1", "Source"), ("YV_W", "Target"), ("YV_H", "Target")])
            .Concat(Facts("REQUIRES_0_WHEN_FALSE", [("ES2", "Source"), ("YV_W", "Target"), ("YV_H", "Target")]));
        Assert.Equal(4, both.Count);
        Assert.All(both, x => Assert.Equal("Gate", x.Kind));
        Assert.Equal(single.Select(x => x.Key).Order(StringComparer.Ordinal), both.Select(x => x.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MatchedKeyIsCountedUnderTruthCategory()
    {
        var truth = Facts("AT_MOST_ONE", [("YV_W", "Member"), ("YV_H", "Member")], category: "A");
        var produced = Facts("AT_MOST_ONE", [("YV_W", "Member"), ("YV_H", "Member")], category: "B");
        var r = LogicEvaluator.Evaluate(truth, produced);
        var a = Assert.Single(r.Categories, c => c.Category == "A");
        Assert.Equal((1, 1, 1, 1), (a.Truth, a.Found, a.Produced, a.Correct));
        Assert.DoesNotContain(r.Categories, c => c.Category == "B" && (c.Produced > 0 || c.Correct > 0));
        Assert.Empty(r.Wrong);
    }

    [Fact]
    public void DelayedQMutexDoesNotMatchInstantMutex()
    {
        var truth = Facts("AT_MOST_ONE", [("YV_W", "Member"), ("YV_H", "Member")], new RuleMeta([], null, true));
        var produced = Facts("AT_MOST_ONE", [("YV_W", "Member"), ("YV_H", "Member")]);
        var r = LogicEvaluator.Evaluate(truth, produced);
        Assert.Equal((0, 0), (r.Overall.Found, r.Overall.Correct));
    }

    [Fact]
    public void DelayedInputMutexMatchesInstantMutex()
    {
        var truth = Facts("AT_MOST_ONE", [("CYL_W", "Member"), ("CYL_H", "Member")], new RuleMeta([], null, true));
        var produced = Facts("AT_MOST_ONE", [("CYL_W", "Member"), ("CYL_H", "Member")]);
        var r = LogicEvaluator.Evaluate(truth, produced);
        Assert.Equal((1, 1), (r.Overall.Found, r.Overall.Correct));
    }

    [Fact]
    public void DelayedFeedbackMatchesInstantFeedback()
    {
        var truth = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")], new RuleMeta([], null, true));
        var produced = Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")]);
        var r = LogicEvaluator.Evaluate(truth, produced);
        Assert.Equal((1, 1), (r.Overall.Found, r.Overall.Correct));
    }

    [Fact]
    public void DelayedImpliesDoesNotMatchInstantImplies()
    {
        var truth = Facts("IMPLIES", [("YV_W", "Source"), ("X", "Target")], new RuleMeta([], null, true));
        var produced = Facts("IMPLIES", [("YV_W", "Source"), ("X", "Target")]);
        Assert.Equal("Implies|YV_W->X|延时", Assert.Single(truth).Key);
        Assert.Equal(0, LogicEvaluator.Evaluate(truth, produced).Overall.Found);
    }

    // ---- 迭代 4 审查修正：超时报警事实带旁路前提（与反馈事实同一口径） ----

    [Fact]
    public void TimeoutAlarmFactCarriesBypassAsPreconditions()
    {
        var f = ConstraintFacts.FromTimeoutAlarm("DB690.Warnings.W2", "yv_w", ["cyl_w", "CYL_W"], ["db_u.Byp[2]", " DB_U.Byp[1] ", "DB_U.BYP[2]"],
            "阀门", "B", 1, "ALARM");
        Assert.Equal("TimeoutAlarm", f.Kind);
        Assert.Equal("TimeoutAlarm|DB690.Warnings.W2|YV_W|CYL_W", f.Key);
        Assert.Equal(new[] { "DB_U.BYP[1]", "DB_U.BYP[2]" }, f.Preconditions);
        Assert.Equal("ALARM", f.Origin);
    }

    [Fact]
    public void TimeoutAlarmFactPreconditionsMatchTheFeedbackFactOfTheSameRule()
    {
        var feedback = Assert.Single(Facts("IMPLIES", [("YV_W", "Source"), ("CYL_W", "Target")],
            new RuleMeta(["Byp", "byp2"], null, true)));
        var alarm = ConstraintFacts.FromTimeoutAlarm("A.X", "YV_W", ["CYL_W"], ["Byp", "byp2"], "阀门", "B", 1, "ALARM");
        Assert.Equal(feedback.Preconditions, alarm.Preconditions);
    }

    [Fact]
    public void TimeoutAlarmWithBypassMatchesATruthWithTheSamePreconditions()
    {
        var produced = ConstraintFacts.FromTimeoutAlarm("A.X", "YV_W", ["CYL_W"], ["Byp"], "阀门", "B", 1, "ALARM");
        var truth = produced with { Origin = "truth" };
        Assert.Equal(1, LogicEvaluator.Evaluate([truth], [produced]).Overall.Found);
        var noBypass = ConstraintFacts.FromTimeoutAlarm("A.X", "YV_W", ["CYL_W"], [], "阀门", "B", 1, "ALARM");
        Assert.Equal(0, LogicEvaluator.Evaluate([truth], [noBypass]).Overall.Found);
    }
}
