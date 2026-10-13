#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Alarms;

public class AlarmCatalogReportTests
{
    static AlarmCatalogRow Row(string symbol, string kind, string block, int? network, string instance,
        string? output = "Q1", string[]? feedback = null, string[][]? groups = null, string[]? pre = null, string? delay = "T#10S",
        string callee = "FB_X") =>
        new(symbol, kind, output, feedback ?? ["I1"], delay, block, network, instance, callee, [])
        {
            FeedbackGroups = (groups ?? []).Select(g => (IReadOnlyList<string>)g).ToArray(),
            Preconditions = pre ?? [],
        };

    [Fact]
    public void TimeoutsByCallSiteGroupsByBlockNetworkInstanceAndCountsOnlyTimeouts()
    {
        var sites = AlarmCatalogReport.TimeoutsByCallSite([
            Row("A1", "Timeout", "FB1", 3, "V[1]"),
            Row("A2", "Timeout", "fb1", 3, "v[1]"),
            Row("A3", "PositionConflict", "FB1", 3, "V[1]"),
            Row("B1", "Timeout", "FB1", 4, "V[1]"),
            Row("C1", "PositionConflict", "FB2", null, "V[2]"),
        ]);
        Assert.Equal(3, sites.Count);
        var first = sites.Single(s => s.Network == 3);
        Assert.Equal((2, 3), (first.Timeouts, first.Total));
        Assert.Equal(1, sites.Single(s => s.Network == 4).Timeouts);
        var noTimeout = sites.Single(s => s.Block == "FB2");
        Assert.Equal((0, 1), (noTimeout.Timeouts, noTimeout.Total));
    }

    [Fact]
    public void TimeoutsByCallSiteIsSortedByBlockNetworkInstance()
    {
        var sites = AlarmCatalogReport.TimeoutsByCallSite([
            Row("X", "Timeout", "FB2", 1, "A"), Row("Y", "Timeout", "FB1", 9, "B"), Row("Z", "Timeout", "FB1", 2, "C"),
        ]);
        Assert.Equal(new[] { "FB1/2", "FB1/9", "FB2/1" }, sites.Select(s => $"{s.Block}/{s.Network}").ToArray());
    }

    [Fact]
    public void MarkdownSummaryCountsRowsAndKinds()
    {
        var md = AlarmCatalogReport.ToMarkdown("t", [
            Row("A1", "Timeout", "FB1", 3, "V[1]"), Row("A2", "Timeout", "FB1", 3, "V[1]"), Row("A3", "PositionConflict", "FB1", 3, "V[1]"),
        ], callSiteNotes: null, notesUnavailableReason: "r");
        Assert.Contains("报警清单行数：3", md);
        Assert.Contains("| Timeout | 2 |", md);
        Assert.Contains("| PositionConflict | 1 |", md);
    }

    [Fact]
    public void MarkdownCatalogRowWritesGroupsPreconditionsAndPlaceholders()
    {
        var md = AlarmCatalogReport.ToMarkdown("t", [
            Row("DB.Alarm1", "Timeout", "FB1", 3, "V[1]", output: null, feedback: ["A", "B", "C"], groups: [["A", "B"], ["C"]],
                pre: ["DB.BYPASS"], delay: null, callee: "FB3100"),
        ], null, "r");
        Assert.Contains("| `DB.Alarm1` | Timeout | — | A&B \\| C | DB.BYPASS | — | FB1 / 3 | V[1] | FB3100 |", md);
    }

    [Fact]
    public void EmptyGroupsFallBackToAllFeedbackAsOneGroup()
    {
        var md = AlarmCatalogReport.ToMarkdown("t", [Row("A", "PositionConflict", "FB1", null, "V", feedback: ["I1", "I2"])], null, "r");
        Assert.Contains("| `A` | PositionConflict | Q1 | I1&I2 |  | T#10S | FB1 / — | V | FB_X |", md);
    }

    [Fact]
    public void PipesInCellsAreEscaped()
    {
        var md = AlarmCatalogReport.ToMarkdown("t", [Row("A|B", "Timeout", "FB1", 1, "V|1")], null, "r");
        Assert.Contains("`A\\|B`", md);
        Assert.Contains("| V\\|1 |", md);
    }

    [Fact]
    public void CallSitesWithTimeoutCountOtherThanTwoAreListedWithReasonWhenNotesUnavailable()
    {
        var md = AlarmCatalogReport.ToMarkdown("t", [
            Row("A1", "Timeout", "FB1", 3, "Ok"), Row("A2", "Timeout", "FB1", 3, "Ok"),
            Row("B1", "Timeout", "FB1", 4, "One"),
            Row("C1", "PositionConflict", "FB2", 5, "None"),
        ], null, "报告里只有汇总计数");
        var mismatch = md[md.IndexOf("超时报警行数不等于 2 的调用点", System.StringComparison.Ordinal)..];
        Assert.Contains("FB1 网络 4，实例 `One`", mismatch);
        Assert.Contains("FB2 网络 5，实例 `None`", mismatch);
        Assert.DoesNotContain("实例 `Ok`", mismatch);
        Assert.Contains("无逐调用点说明", mismatch);
        Assert.Contains("报告里只有汇总计数", md);
        Assert.Contains("超时报警行数不等于 2 的调用点（2 个）", md);
    }

    [Fact]
    public void CallSiteNotesAreWrittenWhenProvided()
    {
        var notes = new Dictionary<(string, int?, string), IReadOnlyList<string>>
        {
            [("FB1", 4, "One")] = ["OpaqueBranch", "BypassUnresolved"],
        };
        var md = AlarmCatalogReport.ToMarkdown("t", [Row("B1", "Timeout", "FB1", 4, "One")], notes, null);
        Assert.Contains("OpaqueBranch", md);
        Assert.Contains("BypassUnresolved", md);
        Assert.DoesNotContain("无逐调用点说明", md);
    }

    [Fact]
    public void NoMismatchWritesNone()
    {
        var md = AlarmCatalogReport.ToMarkdown("t", [Row("A1", "Timeout", "FB1", 3, "V"), Row("A2", "Timeout", "FB1", 3, "V")], null, "r");
        Assert.Contains("超时报警行数不等于 2 的调用点（0 个）", md);
    }

    static UnderstandingAlarm Alarm(string symbol, string block, int? network, int? dbNetwork = null) =>
        new(new CallSiteAlarm(symbol, "Timeout", "Q1", ["I1"], "T#10S", []), 1, block, network, "V", "FB_X") { DbNetwork = dbNetwork };

    [Fact]
    public void ScopeKeepsAlarmsInTruthUnits()
    {
        var truth = new TruthFile("p", "s", "c", [], [new EvalUnitRef("FB1", 3)]);
        var alarms = AlarmCatalogReport.Scope(truth, [Alarm("A", "FB1", 3), Alarm("B", "FB1", 4), Alarm("C", "FB1", null)]);
        Assert.Equal(new[] { "A" }, alarms.Select(a => a.Alarm.AlarmSymbol).ToArray());
    }

    [Fact]
    public void ScopeUsesTheEvaluationNumberNotTheDatabaseNumber()
    {
        // 评测编号 3（归档序号）在库里是 4：按评测编号过滤，清单行再存库口径
        var truth = new TruthFile("p", "s", "c", [], [new EvalUnitRef("FB1", 3)]);
        var alarms = AlarmCatalogReport.Scope(truth, [Alarm("A", "FB1", 3, dbNetwork: 4), Alarm("B", "FB1", 4, dbNetwork: 5)]);
        Assert.Equal(new[] { "A" }, alarms.Select(a => a.Alarm.AlarmSymbol).ToArray());
        Assert.Equal(4, AlarmCatalogBuilder.Build(alarms)[0].SourceNetwork);
    }

    [Fact]
    public void ScopeFallsBackToFactUnitsWhenTruthHasNoUnits()
    {
        var truth = new TruthFile("p", "s", "c", [new EvalFact("k", "key", "d", "cat", "FB2", 7, null)]);
        var alarms = AlarmCatalogReport.Scope(truth, [Alarm("A", "FB1", 3), Alarm("B", "FB2", 7)]);
        Assert.Equal(new[] { "B" }, alarms.Select(a => a.Alarm.AlarmSymbol).ToArray());
    }

    [Fact]
    public void LatchedAndExpectedStateRowsAreNotValveCallSites()
    {
        var latched = new AlarmCatalogRow("DB_A.Err1", "Latched", null, [], null, "FB_Alarm", 7, "", "", ["置位：FB_Alarm 网络 7：X", "复位：FB_Alarm 网络 9：Ack"]);
        var expected = Row("S[1].CheckOffAlarm", "ExpectedState", "FB130", 36, "Sensor[1]", output: null);
        var sites = AlarmCatalogReport.TimeoutsByCallSite([latched, expected, Row("A1", "Timeout", "FB1", 3, "V[1]")]);
        Assert.Equal("FB1", Assert.Single(sites).Block);
        var md = AlarmCatalogReport.ToMarkdown("t", [latched, expected], callSiteNotes: null, notesUnavailableReason: "r");
        Assert.Contains("| Latched | 1 |", md);
        Assert.Contains("| ExpectedState | 1 |", md);
        Assert.Contains("## 锁存报警位（1）", md);
        Assert.Contains("`DB_A.Err1`（FB_Alarm 网络 7）", md);
        Assert.Contains("复位：FB_Alarm 网络 9：Ack", md);
    }
}
