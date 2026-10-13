using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.DiagnosisRead.DiagnosisAppHarness;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>D-1 Task 7：诊断报告——采集失败 / 部分无法判定 / 正常 / 违规含未采到信号。</summary>
public class DiagnosisReportTests
{
    private const long Plc1 = 884001;
    private const long Plc2 = 884002;

    private sealed class RecordingReporter : IDiagnosisProgressReporter
    {
        public List<DiagnosisProgressEvent> Events { get; } = new();

        public Task ReportAsync(DiagnosisProgressEvent progress, CancellationToken cancellationToken = default)
        {
            Events.Add(progress);
            return Task.CompletedTask;
        }
    }

    private static DiagnosisAppHarness BaseHarness()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddIoTag(Plc1, "T", "OP010_A", "%I1.0");
        h.AddIoTag(Plc1, "T", "OP010_B", "%I1.1");
        h.AddIoTag(Plc1, "T", "OP010_D", "%I1.3");
        return h;
    }

    private static Task<HttpResponseResultModel<string>> Diagnose(DiagnosisAppHarness h, IDiagnosisProgressReporter reporter = null) =>
        h.Build().DiagnoseEnergyFlowAsync(new EnergyFlowDiagnosticRequestDto { ProductionLineId = 1 }, reporter, CancellationToken.None);

    [Fact]
    public async Task Zero_good_signals_on_every_plc_reports_collect_failed()
    {
        var h = BaseHarness();
        h.AddPlc(Plc2, "PLC2");
        h.SetRead(Plc1, Bad("T.OP010_A", PLC_SignalReadStatus.NotRegistered, "未注册"));
        h.SetReadFailure(Plc2, new HttpRequestException("refused"));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_OP010_A", ("OP010_A", "Target"));

        var result = await Diagnose(h);

        Assert.True(result.IsSuccess);
        Assert.Contains("状态: 采集失败", result.BackResult);
        Assert.Contains("采集失败，无法诊断", result.BackResult);
        Assert.Contains("- PLC PLC1: 有效信号 0 / 共 1；点名不存在：IGW 未注册 ×1", result.BackResult);
        Assert.Contains("- PLC PLC2: 通讯故障（HttpRequestException: refused）", result.BackResult);
        Assert.DoesNotContain("未发现任何异常", result.BackResult);
    }

    [Fact]
    public async Task Undetermined_without_violation_is_partial_and_never_normal()
    {
        var h = BaseHarness();
        h.SetRead(Plc1, Good("T.OP010_A", true), Bad("T.OP010_B", PLC_SignalReadStatus.NotRegistered, "未注册"), Good("T.OP010_D", false));
        h.AddRule(Plc1, "IMPLIES", "R_IMPLIES_OP010", ("OP010_A", "Source"), ("OP010_B", "Target"));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_OP010_D", ("OP010_D", "Target"));

        var result = await Diagnose(h);

        Assert.True(result.IsSuccess);
        Assert.Contains("状态: 部分无法判定", result.BackResult);
        Assert.Contains("约束检查：已判定 1 条（违规 0 条），无法判定 1 条（点名不存在 1 / 通讯故障 0 / 首值未到 0 / 其它 0）", result.BackResult);
        Assert.Contains("结论: 没有确定的违规，但 1 条约束无法判定，不能视为正常", result.BackResult);
        Assert.Contains("1. 工位: OP010", result.BackResult);
        Assert.Contains("无法判定规则: R_IMPLIES_OP010", result.BackResult);
        Assert.Contains("未采到信号: OP010_B(%I1.1)=未采到(点名不存在：IGW 未注册)", result.BackResult);
        Assert.Contains("原因: 无法判定：点名不存在：IGW 未注册，未采到 OP010_B（R_IMPLIES_OP010）", result.BackResult);
        Assert.DoesNotContain("未发现任何异常", result.BackResult);
    }

    [Fact]
    public async Task All_rules_judged_and_passed_is_normal_with_summary()
    {
        var h = BaseHarness();
        h.SetRead(Plc1, Good("T.OP010_A", true), Good("T.OP010_D", false));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_OP010_D", ("OP010_D", "Target"));

        var result = await Diagnose(h);

        Assert.Contains("未发现任何异常", result.BackResult);
        Assert.Contains("约束检查：已判定 1 条（违规 0 条），无法判定 0 条", result.BackResult);
    }

    [Fact]
    public async Task Violation_with_unknown_signal_is_reported_with_address_and_read_status()
    {
        var h = BaseHarness();
        h.SetRead(Plc1, Good("T.OP010_A", true), Bad("T.OP010_B", PLC_SignalReadStatus.NotRegistered, "未注册"));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_OP010", ("OP010_A", "Target"), ("OP010_B", "Target"));

        var result = await Diagnose(h);

        Assert.Contains("状态: 约束问题", result.BackResult);
        Assert.Contains("约束检查：已判定 1 条（违规 1 条），无法判定 0 条", result.BackResult);
        Assert.Contains("OP010_A(%I1.0)=true", result.BackResult);
        Assert.Contains("OP010_B(%I1.1)=未采到(点名不存在：IGW 未注册)", result.BackResult);
        Assert.Contains("工位: OP010", result.BackResult);
    }

    [Fact]
    public async Task Progress_signal_count_is_good_signal_count()
    {
        var h = BaseHarness();
        h.SetRead(Plc1, Good("T.OP010_A", true), Bad("T.OP010_B", PLC_SignalReadStatus.CommFailure), Good("T.OP010_D", false));
        var reporter = new RecordingReporter();

        await Diagnose(h, reporter);

        var loaded = reporter.Events.Single(e => e.Stage == "signals_loaded");
        Assert.Equal(2, loaded.Metrics.SignalCount);
    }

    [Fact]
    public void Signal_value_formatting_includes_address_and_read_status()
    {
        Assert.Equal("A(%I1.0)=true", AppLayer.DiagnosticStrategyFactory.DiagnosticStrategyFactoryApp.FormatSignalValue(
            new SignalDetail { SignalName = "A", Address = "%I1.0", CurrentValue = true }));
        Assert.Equal("B=未采到(通讯故障)", AppLayer.DiagnosticStrategyFactory.DiagnosticStrategyFactoryApp.FormatSignalValue(
            new SignalDetail { SignalName = "B", ReadStatus = "通讯故障" }));
    }

    [Theory]
    [InlineData("原因: 无法判定：通讯故障，未采到 OP010_CYL01_W(%I1.0)")]
    [InlineData("未采到信号: OP010_CYL01_W(%I1.0)=未采到(通讯故障)")]
    [InlineData("结论: 采集失败，无法诊断（所有 PLC 均未采到有效信号）")]
    public void Undetermined_lines_are_not_physical_io_fault_evidence(string line) =>
        Assert.False(LineDiagnosisLlmService.IsPhysicalIoConstraintFaultLine(line));

    [Fact]
    public void Violation_lines_remain_physical_io_fault_evidence() =>
        Assert.True(LineDiagnosisLlmService.IsPhysicalIoConstraintFaultLine(
            "原因: 屏蔽态报警传感器违反: OP010_CYL01_W 当前为 true，应永为 false"));

    // ---- Task 5 评审遗留：未采到的信号绝不以 =true / =false 呈现 ----

    private static void AssertNoGuessedValue(string report, string unknownName)
    {
        foreach (var line in report.Split('\n'))
        {
            Assert.DoesNotMatch($@"(^|[\s,:：]){System.Text.RegularExpressions.Regex.Escape(unknownName)}(\([^)]*\))?=(true|false)", line);
        }
    }

    [Fact]
    public async Task Violation_report_never_prints_guessed_value_for_unknown_signal()
    {
        var h = BaseHarness();
        h.SetRead(Plc1, Good("T.OP010_A", true), Bad("T.OP010_B", PLC_SignalReadStatus.NotRegistered, "未注册"));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_OP010", ("OP010_A", "Target"), ("OP010_B", "Target"));

        var result = await Diagnose(h);

        AssertNoGuessedValue(result.BackResult, "OP010_B");
        Assert.Contains("应有值:", result.BackResult);
        Assert.Contains("OP010_B(%I1.1)=未采到(点名不存在：IGW 未注册)", result.BackResult.Split('\n').Single(l => l.StartsWith("应有值:")));
    }

    [Fact]
    public void Unknown_details_are_excluded_from_mismatch_and_fallback_selection()
    {
        var issue = new DiagnosticIssue
        {
            RelatedRule = "R_OP010",
            Reason = "测试",
            SignalDetails = new List<SignalDetail>
            {
                // 已知信号与期望一致（不是 mismatch），未知信号带 CurrentValue=false 与「无法判定（未采到）」
                new() { SignalName = "OP010_A", SignalAddress = "%I1.0", CurrentValue = true, ExpectedValue = "true" },
                new() { SignalName = "OP010_B", SignalAddress = "%I1.1", CurrentValue = false, ExpectedValue = "无法判定（未采到）", ReadStatus = "通讯故障" },
                new() { SignalName = "OP010_C", CurrentValue = false, ExpectedValue = "无法判定（未采到）", ReadStatus = "首值未到" },
            },
        };

        var report = AppLayer.DiagnosticStrategyFactory.DiagnosticStrategyFactoryApp.BuildDirectDiagnosticReport(
            "constraint", new List<DiagnosticIssue> { issue }, null);

        AssertNoGuessedValue(report, "OP010_B");
        AssertNoGuessedValue(report, "OP010_C");
        var current = report.Split('\n').Single(l => l.StartsWith("当前值:"));
        var expected = report.Split('\n').Single(l => l.StartsWith("应有值:"));
        Assert.Contains("OP010_A(%I1.0)=true", current);
        Assert.Contains("OP010_B(%I1.1)=未采到(通讯故障)", current);
        Assert.Contains("OP010_C=未采到(首值未到)", current);
        Assert.Contains("OP010_B(%I1.1)=未采到(通讯故障)", expected);
        Assert.Contains("OP010_C=未采到(首值未到)", expected);
        Assert.DoesNotContain("无法判定（未采到）", expected);
    }

    [Fact]
    public async Task Constraint_check_failure_is_check_failed_not_counts()
    {
        var h = BaseHarness();
        h.SetRead(Plc1, Good("T.OP010_A", true));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_OP010_A", ("OP010_A", "Target"));
        h.RuleServiceFailure = new InvalidOperationException("db down");

        var result = await Diagnose(h);

        Assert.True(result.IsSuccess);
        Assert.Contains("状态: 约束检查失败", result.BackResult);
        Assert.Contains("约束检查执行失败，无法诊断", result.BackResult);
        Assert.DoesNotContain("未发现任何异常", result.BackResult);
        Assert.DoesNotContain("约束检查：已判定", result.BackResult);
    }

    // ---- 违规条目夹带未采到信号时，已采到部分仍是 IO 物理故障证据，未采到部分不是 ----

    [Theory]
    [InlineData("当前值: OP010_CYL01_W(%I1.0)=true, OP010_CYL01_H(%I1.1)=未采到(通讯故障)")]
    [InlineData("原因: 屏蔽态报警传感器违反: OP010_CYL01_W 当前为 true，应永为 false（R） 未采到信号（其取值不影响本结论）：OP010_CYL01_H(通讯故障)")]
    public void Violation_lines_with_unread_signals_remain_evidence(string line) =>
        Assert.True(LineDiagnosisLlmService.IsPhysicalIoConstraintFaultLine(line));

    [Fact]
    public void Only_unread_signal_values_are_not_evidence() =>
        Assert.False(LineDiagnosisLlmService.IsPhysicalIoConstraintFaultLine(
            "当前值: OP010_CYL01_H(%I1.1)=未采到(通讯故障)"));

    [Fact]
    public void Unread_signal_text_is_stripped_from_evidence() =>
        Assert.Equal("当前值: OP010_CYL01_W(%I1.0)=true",
            LineDiagnosisLlmService.StripUnreadSignalText("当前值: OP010_CYL01_W(%I1.0)=true, OP010_CYL01_H(%I1.1)=未采到(点名不存在：IGW 未注册)"));

    // ---- D-1 Task 7 fix round 1 ----

    // 1. BuildPhysicalInputAddressText 不能把未采到的信号当成已确认地址列进「I点地址」证据行
    [Fact]
    public void I_point_address_line_excludes_unread_signals()
    {
        var issue = new DiagnosticIssue
        {
            RelatedRule = "R_OP010",
            Reason = "测试",
            SignalDetails = new List<SignalDetail>
            {
                new() { SignalName = "OP010_A", Address = "%I1.0", CurrentValue = true, ExpectedValue = "false" },
                new() { SignalName = "OP010_B", Address = "%I1.1", ExpectedValue = "无法判定（未采到）", ReadStatus = "通讯故障" },
            },
        };

        var report = AppLayer.DiagnosticStrategyFactory.DiagnosticStrategyFactoryApp.BuildDirectDiagnosticReport(
            "constraint", new List<DiagnosticIssue> { issue }, null);

        var iPointLine = report.Split('\n').SingleOrDefault(l => l.StartsWith("I点地址:"));
        Assert.NotNull(iPointLine);
        Assert.Contains("%I1.0", iPointLine);
        Assert.DoesNotContain("%I1.1", iPointLine);
        Assert.DoesNotContain("OP010_B", iPointLine);
    }

    // 2. 候选工位必须从剔除未采到片段后的文本取，某行唯一提到 OP020 的地方是未采到信号时不能把证据算给 OP020。
    // 用连字符命名（如现网真实存在的 OP010-FZ91 风格）而不是下划线，因为 \bOP\d{2,4}\b 在「OP020-FZ92」处能
    // 命中（连字符不是单词字符），而在「OP020_CYL01_H」处命中不了（下划线是单词字符、破坏词边界）——
    // 要复现这个 bug，被删片段里的工位提及必须是能被该正则命中的形式。
    [Fact]
    public void Evidence_line_whose_only_station_mention_is_unread_signal_is_not_attributed_to_that_station()
    {
        var constraintReport = string.Join("\n",
            "1. 工位: OP010",
            "当前值: X_CYL01_W(%I1.0)=true, OP020-FZ92(%I2.0)=未采到(通讯故障)",
            "原因: 违反约束");

        var profiles = LineDiagnosisLlmService.BuildEvidenceProfiles(new List<StationDiagnosisSummaryDto>(), constraintReport);

        Assert.Contains(profiles, p => p.StationCode == "OP010");
        Assert.DoesNotContain(profiles, p => p.StationCode == "OP020");
    }

    // 3. 停机诊断提示词必须明确告诉 LLM：无法判定/采集失败/未采到 是采集问题，不是已确认的 IO 故障
    [Fact]
    public void Prompt_tells_llm_undetermined_entries_are_not_confirmed_io_faults()
    {
        var summaries = new List<StationDiagnosisSummaryDto>
        {
            new() { StationCode = "OP010", StationOrder = 1, OverallStatus = "AllTrue" },
        };

        var prompt = LineDiagnosisLlmService.BuildPrompt(summaries, constraintReport: "约束报告示例");

        Assert.Contains("无法判定", prompt);
        Assert.Contains("不是已确认的 IO 故障", prompt);
        Assert.Contains("不能作为故障点输出", prompt);
    }

    // 4. check_failed 结论不应把「约束检查执行失败」重复拼两遍
    [Fact]
    public async Task Check_failed_conclusion_does_not_repeat_failure_phrase()
    {
        var h = BaseHarness();
        h.SetRead(Plc1, Good("T.OP010_A", true));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_OP010_A", ("OP010_A", "Target"));
        h.RuleServiceFailure = new InvalidOperationException("db down");

        var result = await Diagnose(h);

        Assert.Contains("结论: 约束检查执行失败，无法诊断（InvalidOperationException: db down）", result.BackResult);
        var occurrences = System.Text.RegularExpressions.Regex.Matches(result.BackResult, "约束检查执行失败").Count;
        Assert.Equal(1, occurrences);
    }

    // 5. StripUnreadSignalText 只能规整被删片段旁边多出来的逗号，不能把普通中文叙述里的单个「，」也改写掉
    [Fact]
    public void Strip_unread_signal_text_does_not_rewrite_unrelated_chinese_comma()
    {
        const string line = "原因: 屏蔽态报警传感器违反: OP010_CYL01_W 当前为 true，应永为 false（R） 未采到信号（其取值不影响本结论）：OP010_CYL01_H(通讯故障)";

        var stripped = LineDiagnosisLlmService.StripUnreadSignalText(line);

        Assert.Contains("当前为 true，应永为 false", stripped);
        Assert.DoesNotContain("未采到", stripped);
    }

    [Fact]
    public void Strip_unread_signal_text_collapses_dangling_comma_between_two_removed_tokens()
    {
        const string line = "A=未采到(x), B=未采到(y), C=true";

        var stripped = LineDiagnosisLlmService.StripUnreadSignalText(line);

        Assert.Equal("C=true", stripped);
    }
}
