using System.Text.Json;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>
/// 2026-10-15：完整反馈判定不再只认 FB3002_Valve。命令、到位反馈（含多组与旁路）和 PLC 超时报警位
/// 取自报警清单的 Timeout 行（由 FB 程序体契约在调用点代入得到，任何 FB 库都适用）。
/// </summary>
public class FeedbackCatalogPlanTests
{
    const long Project = 302;
    const string Alarm = "DB_Alarm.FwdTimeout";

    sealed class Line
    {
        public readonly DiagnosisAppHarness H = new();
        public readonly Dictionary<(string, string), bool> Values = new();
        public List<AlarmCatalogRow> Catalog = new();
        public Exception CatalogFailure;

        public Line()
        {
            H.AddPlc(Project, "PLC");
            foreach (var tag in new[] { "Q", "I1", "I2" }) H.AddIoTag(Project, "IO", tag, tag == "Q" ? "%Q1.0" : "%I1." + tag[1]);
            // 调用方块是 SCL 文字（不是 FlgNet）：旧的 FB3002_Valve XML 解析与全局写入扫描都用不上
            H.Blocks.Add(new PLC_BlockEntity { Id = 80, ProjectId = Project, BlockName = "FB320_OP30Manual" });
            H.Units.Add(new PLC_LogicUnitEntity { Id = 501, BlockId = 80,
                RawContent = "\"Valve1\"(I_FwdPos := \"I1\", O_FwdOut => \"Q\");" });
            foreach (var (id, name, path) in new[] { (81L, "DB", "Bypass"), (83L, "DB_Alarm", "FwdTimeout") })
            {
                H.Blocks.Add(new PLC_BlockEntity { Id = id, ProjectId = Project, BlockName = name, BlockType = "DB" });
                H.BlockInterfaces.Add(new PLC_BlockInterfaceEntity { BlockId = id, VariablePath = path, DataType = "Bool" });
            }
            H.ExactReads.Add(requests => requests.Where(r => Values.ContainsKey((r.BlockName, r.VariablePath)))
                .Select(r => new PLC_ExactReadResult(r.RequestKey, r.BlockName, r.VariablePath,
                    r.BlockName + "." + r.VariablePath, Values[(r.BlockName, r.VariablePath)],
                    PLC_ExactReadQuality.Good, DateTimeOffset.UtcNow, null)).ToArray());
        }

        public PLC_IOConstraintRuleEntity Rule(string target, string[] bypass = null)
        {
            var rule = H.AddRule(Project, "IMPLIES", "OP030_CYL01_到位反馈_" + target, ("Q", "Source"), (target, "Target"));
            rule.Source = "XMLV2_TIMED";
            rule.ValidStates = JsonSerializer.Serialize(new { BlockName = "FB320_OP30Manual", LogicUnitId = "999999",
                BypassSignals = bypass ?? Array.Empty<string>() });
            return rule;
        }

        public void Set(bool q, bool i1, bool alarm, bool i2 = false, bool bypass = false)
        {
            Values[("IO", "Q")] = q; Values[("IO", "I1")] = i1; Values[("IO", "I2")] = i2;
            Values[("DB", "Bypass")] = bypass; Values[("DB_Alarm", "FwdTimeout")] = alarm;
            H.SetRead(Project, DiagnosisAppHarness.Good("IO.Q", q), DiagnosisAppHarness.Good("IO.I1", i1),
                DiagnosisAppHarness.Good("IO.I2", i2));
        }

        public async Task<ConstraintCheckOutcome> Check()
        {
            var app = H.Build();
            app.TimeoutAlarmCatalogOverride = (_, _) => CatalogFailure != null
                ? Task.FromException<IReadOnlyList<AlarmCatalogRow>>(CatalogFailure)
                : Task.FromResult<IReadOnlyList<AlarmCatalogRow>>(Catalog);
            return await app.CheckConstraintsAsync([Project], await app.GetLiveSignalsByPlcAsync(H.Projects, default), default);
        }
    }

    static AlarmCatalogRow Timeout(string output = "Q", string[] feedback = null, string[][] groups = null,
        string[] preconditions = null, string alarm = Alarm) =>
        new(alarm, "Timeout", output, feedback ?? ["I1"], "T#3s", "FB320_OP30Manual", 2, "Valve1",
            "FB3100_ValveControl", [])
        {
            FeedbackGroups = groups ?? [],
            Preconditions = preconditions ?? [],
        };

    [Fact]
    public async Task Timeout_alarm_from_the_catalog_confirms_the_violation_for_any_fb_library()
    {
        var line = new Line();
        line.Rule("I1");
        line.Catalog = [Timeout()];
        line.Set(q: true, i1: false, alarm: true);
        var result = await line.Check();
        var issue = Assert.Single(result.Violations);
        Assert.Contains("PLC 超时报警已触发", issue.Reason);
        Assert.Contains(Alarm + "=True", issue.Reason);
        Assert.Empty(result.Undetermined);
    }

    [Fact]
    public async Task Command_without_timeout_alarm_stays_undetermined()
    {
        var line = new Line();
        line.Rule("I1");
        line.Catalog = [Timeout()];
        line.Set(q: true, i1: false, alarm: false);
        var result = await line.Check();
        Assert.Empty(result.Violations);
        Assert.Contains("超时报警未触发", Assert.Single(result.Undetermined).Reason);
    }

    [Fact]
    public async Task Reached_feedback_passes()
    {
        var line = new Line();
        line.Rule("I1");
        line.Catalog = [Timeout()];
        line.Set(q: true, i1: true, alarm: false);
        var result = await line.Check();
        Assert.Equal(1, result.Passed);
        Assert.Empty(result.Undetermined);
    }

    [Fact]
    public async Task Active_bypass_counts_as_reached_feedback()
    {
        var line = new Line();
        line.Rule("I1", bypass: ["DB.Bypass"]);
        line.Catalog = [Timeout(preconditions: ["DB.BYPASS"])];
        line.Set(q: true, i1: false, alarm: false, bypass: true);
        var result = await line.Check();
        Assert.Equal(1, result.Passed);
        Assert.Empty(result.Undetermined);
    }

    [Fact]
    public async Task Split_group_rules_are_judged_once_against_all_feedback_groups()
    {
        var line = new Line();
        line.Rule("I1");
        line.Rule("I2");
        line.Catalog = [Timeout(feedback: ["I1", "I2"], groups: [["I1"], ["I2"]])];
        line.Set(q: true, i1: false, alarm: false, i2: true);
        var result = await line.Check();
        Assert.Empty(result.Violations);
        Assert.Empty(result.Undetermined);
        Assert.Equal(1, result.Passed);
    }

    [Fact]
    public async Task Split_group_rules_report_one_violation_when_the_alarm_is_active()
    {
        var line = new Line();
        line.Rule("I1");
        line.Rule("I2");
        line.Catalog = [Timeout(feedback: ["I1", "I2"], groups: [["I1"], ["I2"]])];
        line.Set(q: true, i1: false, alarm: true, i2: false);
        var result = await line.Check();
        Assert.Single(result.Violations);
    }

    [Fact]
    public async Task Without_a_catalog_row_a_missing_feedback_is_never_a_violation()
    {
        var line = new Line();
        line.Rule("I1");
        line.Set(q: true, i1: false, alarm: true);
        var result = await line.Check();
        Assert.Empty(result.Violations);
        Assert.Single(result.Undetermined);
    }

    [Fact]
    public async Task Timeout_row_for_another_output_is_not_used()
    {
        var line = new Line();
        line.Rule("I1");
        line.Catalog = [Timeout(output: "Q_Other")];
        line.Set(q: true, i1: false, alarm: true);
        var result = await line.Check();
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task Two_timeout_rows_for_the_same_output_are_not_guessed()
    {
        var line = new Line();
        line.Rule("I1");
        line.Catalog = [Timeout(), Timeout(alarm: "DB_Alarm.Other")];
        line.Set(q: true, i1: false, alarm: true);
        var result = await line.Check();
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task Catalog_read_failure_does_not_break_the_constraint_check()
    {
        var line = new Line();
        line.Rule("I1");
        line.CatalogFailure = new InvalidOperationException("catalog down");
        line.Set(q: true, i1: false, alarm: true);
        var result = await line.Check();
        Assert.Null(result.FailureMessage);
        Assert.Empty(result.Violations);
        Assert.Single(result.Undetermined);
    }
}
