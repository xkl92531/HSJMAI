using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class DiagnosisEvidenceGuardTests
{
    [Theory]
    [InlineData("FwdLock", "DB.Type.CylinderLockErrors.Warning")]
    [InlineData("Latched", "DB.Type.Warnings.Warning")]
    [InlineData("Latched", "DB.Type.Messages.Notice")]
    public void WarningOrMessageDoesNotPromoteWaitingStationToFaultRoot(string kind, string symbol)
    {
        var station = Waiting();
        station.ActiveAlarms = [new() { Kind = kind, AlarmVariable = symbol }];
        var report = Hallucinated();
        Guard(report, [station]);
        Assert.Null(report.RootCauseStation);
        Assert.DoesNotContain(UnsupportedClaim, PublicText(report));
    }

    private const string UnsupportedClaim = "OP020机器人通信故障已确认";

    private static StationDiagnosisSummaryDto Waiting(string variable = "ReachSeg33", bool? value = false) => new()
    {
        ProjectId = 1, StationCode = "OP020", OverallStatus = "SomeFalse",
        KeyFindings = [new() { VariableName = variable, EvaluatedValue = value,
            EvaluationStatus = value.HasValue ? value.Value.ToString() : "Unknown",
            FaultOwnership = "InternalFault", DiagnosisPhase = "GraphTransitionTrace",
            DiagnosisQuality = "Diagnosed", TraceDepth = 2,
            FailurePathSummary = "机器人通信异常：等待 ReachSeg33 / PickHighCache1OK=False" }]
    };

    private static LineDiagnosisLlmReport Hallucinated() => new()
    {
        Success = true, RootCauseStation = "OP020", RootCauseDescription = UnsupportedClaim,
        Reasoning = UnsupportedClaim, ImpactChain = UnsupportedClaim, Confidence = .95,
        RawResponse = UnsupportedClaim,
        SuggestedActions = [UnsupportedClaim],
        SecondaryCauses = [new() { StationCode = "OP020", Description = UnsupportedClaim }],
        StationAnalysis = [new() { StationCode = "OP020", Role = "RootCause", Summary = UnsupportedClaim }]
    };

    private static void Guard(LineDiagnosisLlmReport report, List<StationDiagnosisSummaryDto> stations,
        string constraints = null, string language = "zh-CN") =>
        LineDiagnosisLlmService.ApplyEvidenceArbitration(report,
            LineDiagnosisLlmService.BuildEvidenceProfiles(stations, constraints), language);

    private static string PublicText(LineDiagnosisLlmReport r) => string.Join("\n",
        new[] { r.RootCauseDescription, r.Reasoning, r.ImpactChain }
            .Concat(r.SuggestedActions).Concat(r.StationAnalysis.Select(s => s.Summary))
            .Concat(r.SecondaryCauses.Select(s => s.Description)));

    [Theory]
    [InlineData("ReachSeg33", false)]
    [InlineData("PickHighCache1OK", false)]
    [InlineData("PickHighCache2OK", null)]
    [InlineData("DB.RobotError", false)]
    [InlineData("DB.RobotError", null)]
    [InlineData("DB.GRAPH.execbits", true)]
    public void Waiting_or_missing_value_cannot_confirm_fault_even_with_alarm_words(string variable, bool? value)
    {
        var report = Hallucinated();
        Guard(report, [Waiting(variable, value)]);
        Assert.Null(report.RootCauseStation);
        Assert.InRange(report.Confidence, 0, .49);
        Assert.DoesNotContain(UnsupportedClaim, PublicText(report));
        Assert.DoesNotContain(report.StationAnalysis, s => s.Role == "RootCause");
        Assert.Empty(report.SecondaryCauses);
        Assert.Equal(UnsupportedClaim, report.RawResponse);
    }

    [Fact]
    public void Physical_feedback_false_and_trace_keep_the_model_root_only_as_unconfirmed()
    {
        // 2026-10-15 用户裁定：没有报警/确认违规时，执行机构反馈支撑的模型根因保留为待确认，而不是清空。
        var station = Waiting("OP020_CYL01_FwdPos");
        station.KeyFindings[0].IoTagAddress = "%I1.0";
        var report = Hallucinated();
        Guard(report, [station]);
        Assert.Equal("OP020", report.RootCauseStation);
        Assert.True(report.RootCauseTentative);
        Assert.StartsWith("待确认", report.RootCauseDescription);
        Assert.InRange(report.Confidence, 0, .5);
    }

    [Fact]
    public void Good_active_alarm_remains_candidate_even_when_graph_reports_running()
    {
        var alarm = new StationDiagnosisSummaryDto { ProjectId = 1, StationCode = "OP030", OverallStatus = "Running",
            ActiveAlarms = [new() { AlarmVariable = "DB.OP030.CYL01.Timeout", Kind = "Timeout" }] };
        var report = Hallucinated();
        Guard(report, [Waiting(), alarm]);
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.Contains("DB.OP030.CYL01.Timeout", report.RootCauseDescription);
        Assert.DoesNotContain(UnsupportedClaim, PublicText(report));
        Assert.DoesNotContain(report.SecondaryCauses, s => s.StationCode == "OP020");
        Assert.Contains(report.StationAnalysis, s => s.StationCode == "OP030" && s.Role == "RootCause");
        Assert.Contains(report.StationAnalysis, s => s.StationCode == "OP020" && s.Role == "Unknown");
    }

    [Fact]
    public void Confirmed_io_violation_remains_candidate_without_runtime_findings()
    {
        var report = Hallucinated();
        Guard(report, [Waiting(), new() { ProjectId = 1, StationCode = "OP030", OverallStatus = "Running" }],
            "异常数: 1\n1. 工位: OP030\n判定: 确认违规\n卡住对象: OP030_CYL01\n当前值: OP030_CYL01_W(%I1.0)=false\n应有值: true\n原因: PLC 超时报警已触发，违反反馈约束");
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.DoesNotContain(UnsupportedClaim, PublicText(report));
    }

    [Theory]
    [InlineData("1. 工位: OP020\n当前值: OP020_CYL01_W(%I1.0)=false")]
    [InlineData("1. 工位: OP020\n当前值: OP020_CYL01_W(%I1.0)=false\n原因: 违反约束")]
    [InlineData("1. 工位: OP020\n无法判定规则: C1\n当前值: OP020_CYL01_W(%I1.0)=false\n原因: 无法判定")]
    public void Loose_false_or_undetermined_constraint_text_is_not_confirmed_io_fault(string constraints)
    {
        var report = Hallucinated();
        Guard(report, [Waiting()], constraints);
        Assert.Null(report.RootCauseStation);
    }

    [Fact]
    public void Duplicate_station_across_projects_cannot_be_selected_by_station_code_alone()
    {
        var station = Waiting();
        station.ActiveAlarms = [new() { AlarmVariable = "DB.PLC1.Timeout", Kind = "Timeout" }];
        var other = Waiting();
        other.ProjectId = 2;
        var report = Hallucinated();
        Guard(report, [station, other]);
        Assert.Null(report.RootCauseStation);
        Assert.DoesNotContain(UnsupportedClaim, PublicText(report));
        Assert.Contains("DB.PLC1.Timeout", PublicText(report));
    }

    [Fact]
    public void Empty_evidence_and_unstructured_model_response_cannot_bypass_guard()
    {
        var report = Hallucinated();
        report.RootCauseStation = null;
        Guard(report, []);
        Assert.DoesNotContain(UnsupportedClaim, PublicText(report));
        Assert.InRange(report.Confidence, 0, .49);
    }

    [Fact]
    public void English_insufficient_evidence_report_is_localized()
    {
        var report = Hallucinated();
        Guard(report, [Waiting()], language: "en-US");
        Assert.Null(report.RootCauseStation);
        Assert.Contains("evidence", report.RootCauseDescription, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UnsupportedClaim, PublicText(report));
    }

    [Fact]
    public void Guard_does_not_raise_low_model_confidence()
    {
        var station = Waiting();
        station.ActiveAlarms = [new() { AlarmVariable = "DB.Timeout", Kind = "Timeout" }];
        var report = Hallucinated();
        report.Confidence = .1;
        Guard(report, [station]);
        Assert.Equal("OP020", report.RootCauseStation);
        Assert.Equal(.1, report.Confidence);
        Assert.DoesNotContain(UnsupportedClaim, PublicText(report));
    }

    [Fact]
    public void Constraint_reference_to_another_station_does_not_transfer_fault_ownership()
    {
        var profiles = LineDiagnosisLlmService.BuildEvidenceProfiles([Waiting()],
            "1. 工位: OP030\n判定: 确认违规\n当前值: OP020-CYL01(%I1.0)=false\n原因: 违反约束");
        Assert.Contains(profiles, p => p.StationCode == "OP030" && p.HasConstraintPhysicalIoFault);
        Assert.DoesNotContain(profiles, p => p.StationCode == "OP020" && p.HasConstraintPhysicalIoFault);
    }

    [Fact]
    public async Task Per_station_llm_does_not_need_model_call_to_confirm_insufficient_evidence()
    {
        var service = new LineDiagnosisLlmService(null, null, null);
        var result = await service.AnalyzeStationAsync(Waiting());
        Assert.Contains("证据不足", result);
        Assert.DoesNotContain(UnsupportedClaim, result);
    }

    [Fact]
    public void Confirmed_alarm_violation_survives_unknown_feedbacks_in_the_same_entry()
    {
        var report = Hallucinated();
        Guard(report, [Waiting()], "1. 工位: OP030\n判定: 确认违规\n当前值: DB.Alarm=true, OP030_CYL01_W(%I1.0)=未采到(通讯故障)\n原因: PLC 超时报警已触发（可能锁存），完整反馈=Unknown");
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.Contains("DB.Alarm=true", report.RootCauseDescription);
        Assert.DoesNotContain("%I1.0", report.RootCauseDescription);
        Assert.DoesNotContain(UnsupportedClaim, PublicText(report));
    }

    [Fact]
    public void Confirmed_io_with_duplicate_station_is_visible_but_not_assigned_to_a_project()
    {
        var first = Waiting();
        var second = Waiting();
        second.ProjectId = 2;
        var report = Hallucinated();
        Guard(report, [first, second], "1. 工位: OP020\n判定: 确认违规\n当前值: DB.Alarm=true\n原因: PLC 超时报警已触发");
        Assert.Null(report.RootCauseStation);
        Assert.Contains("DB.Alarm=true", PublicText(report));
    }
}
