using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 没有激活报警或确认违规时，保留模型按执行机构反馈、GRAPH 卡步和等待链给出的根因工位，标成待确认。
/// 有报警或违规时仍按证据门槛的原规则。
/// </summary>
public class DiagnosisTentativeRootTests
{
    private static StationDiagnosisSummaryDto CylinderStuck(string station = "OP030", long projectId = 1) => new()
    {
        ProjectId = projectId, StationCode = station, OverallStatus = "SomeFalse", StationOrder = 3,
        KeyFindings = [new() { VariableName = $"{station}_CYL05_W", IoTagAddress = "%I10.1",
            EvaluatedValue = false, EvaluationStatus = "False", FaultOwnership = "InternalFault",
            DiagnosisPhase = "GraphTransitionTrace", DiagnosisQuality = "Diagnosed", TraceDepth = 1,
            FailurePathSummary = $"等待气缸伸出到位 {station}_CYL05_W=False" }]
    };

    private static StationDiagnosisSummaryDto WaitingFor(string station, string target, int order) => new()
    {
        ProjectId = 1, StationCode = station, OverallStatus = "SomeFalse", StationOrder = order,
        KeyFindings = [new() { VariableName = $"DB_{station}.Wait{target}", EvaluatedValue = false,
            EvaluationStatus = "False", FaultOwnership = $"WaitingFor:{target}",
            DiagnosisPhase = "GraphTransitionTrace", DiagnosisQuality = "Diagnosed",
            FailurePathSummary = $"等待 {target} 放行" }]
    };

    private static LineDiagnosisLlmReport Model(string root, double confidence = .9) => new()
    {
        Success = true, RootCauseStation = root, Confidence = confidence,
        RootCauseDescription = $"{root} 的气缸 CYL05 伸出未到位，导致上游工位等待。",
        ImpactChain = $"根因:{root} → 受影响:[OP010,OP020]",
        Reasoning = "OP010、OP020 都在等 OP030，OP030 卡在气缸伸出到位。",
        SuggestedActions = ["检查 OP030 CYL05 气缸与伸出到位传感器"],
        StationAnalysis = [new() { StationCode = root ?? "OP030", Role = "RootCause", Summary = "CYL05 伸出未到位" }],
    };

    private static void Guard(LineDiagnosisLlmReport report, List<StationDiagnosisSummaryDto> stations,
        string constraints = null, string language = "zh-CN") =>
        LineDiagnosisLlmService.ApplyEvidenceArbitration(report,
            LineDiagnosisLlmService.BuildEvidenceProfiles(stations, constraints), language);

    private static List<StationDiagnosisSummaryDto> Line() =>
        [WaitingFor("OP010", "OP030", 1), WaitingFor("OP020", "OP030", 2), CylinderStuck()];

    [Fact]
    public void Model_root_backed_by_actuator_feedback_is_kept_as_unconfirmed()
    {
        var report = Model("OP030");
        Guard(report, Line());

        Assert.Equal("OP030", report.RootCauseStation);
        Assert.True(report.RootCauseTentative);
        Assert.StartsWith("待确认", report.RootCauseDescription);
        Assert.Contains("CYL05", report.RootCauseDescription);
        Assert.Contains(report.StationAnalysis, s => s.StationCode == "OP030" && s.Role == "RootCause");
        Assert.Contains(report.StationAnalysis, s => s.StationCode == "OP010" && s.Role == "Impacted");
        Assert.Contains(report.StationAnalysis, s => s.StationCode == "OP020" && s.Role == "Impacted");
        Assert.Contains(report.SecondaryCauses, s => s.StationCode == "OP010");
        Assert.Contains("OP010 等待 OP030", report.ImpactChain);
        Assert.Contains(report.SuggestedActions, a => a.Contains("CYL05"));
        Assert.InRange(report.Confidence, 0, .5);
    }

    [Fact]
    public void Low_model_confidence_is_not_raised()
    {
        var report = Model("OP030", .2);
        Guard(report, Line());
        Assert.True(report.RootCauseTentative);
        Assert.Equal(.2, report.Confidence);
    }

    [Fact]
    public void Station_named_in_lower_case_is_matched_to_the_profile_code()
    {
        var report = Model("op030");
        Guard(report, Line());
        Assert.Equal("OP030", report.RootCauseStation);
    }

    [Fact]
    public void Root_waited_on_by_others_is_kept_even_without_actuator_finding()
    {
        var target = new StationDiagnosisSummaryDto
        {
            ProjectId = 1, StationCode = "OP030", OverallStatus = "SomeFalse", StationOrder = 3,
            KeyFindings = [new() { VariableName = "DB_OP030.Step12_Cond", EvaluatedValue = false,
                EvaluationStatus = "False", FaultOwnership = "InternalFault", DiagnosisPhase = "AnchorScan" }]
        };
        var report = Model("OP030");
        Guard(report, [WaitingFor("OP010", "OP030", 1), target]);
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.True(report.RootCauseTentative);
    }

    [Fact]
    public void Station_that_only_waits_for_another_station_is_not_kept()
    {
        var report = Model("OP020");
        Guard(report, Line());
        Assert.Null(report.RootCauseStation);
        Assert.False(report.RootCauseTentative);
    }

    [Fact]
    public void Robot_handshake_stuck_step_alone_is_not_kept()
    {
        var robot = new StationDiagnosisSummaryDto
        {
            ProjectId = 1, StationCode = "OP020", OverallStatus = "SomeFalse",
            KeyFindings = [new() { VariableName = "ReachSeg33", EvaluatedValue = false, EvaluationStatus = "False",
                FaultOwnership = "InternalFault", DiagnosisPhase = "GraphTransitionTrace",
                DiagnosisQuality = "Diagnosed", TraceDepth = 2,
                FailurePathSummary = "等待机器人 ReachSeg33 / PickHighCache1OK=False" }]
        };
        var report = Model("OP020");
        Guard(report, [robot]);
        Assert.Null(report.RootCauseStation);
        Assert.False(report.RootCauseTentative);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("AllTrue")]
    public void Station_that_is_not_abnormal_is_not_kept(string status)
    {
        var station = CylinderStuck();
        station.OverallStatus = status;
        var report = Model("OP030");
        Guard(report, [WaitingFor("OP010", "OP030", 1), station]);
        Assert.Null(report.RootCauseStation);
    }

    [Fact]
    public void Station_missing_from_the_diagnosis_is_not_kept()
    {
        var report = Model("OP090");
        Guard(report, Line());
        Assert.Null(report.RootCauseStation);
        Assert.False(report.RootCauseTentative);
    }

    [Fact]
    public void Station_code_repeated_across_projects_is_not_kept()
    {
        var report = Model("OP030");
        Guard(report, [.. Line(), CylinderStuck(projectId: 2)]);
        Assert.Null(report.RootCauseStation);
    }

    [Fact]
    public void Failed_model_call_keeps_the_insufficient_evidence_report()
    {
        var report = Model("OP030");
        report.Success = false;
        Guard(report, Line());
        Assert.Null(report.RootCauseStation);
        Assert.False(report.RootCauseTentative);
        Assert.Contains("证据不足", report.RootCauseDescription);
    }

    [Fact]
    public void Model_without_root_station_keeps_the_insufficient_evidence_report()
    {
        var report = Model(null);
        Guard(report, Line());
        Assert.Null(report.RootCauseStation);
        Assert.Contains("证据不足", report.RootCauseDescription);
    }

    [Fact]
    public void Active_alarm_elsewhere_still_wins_over_the_model_pick()
    {
        var alarm = new StationDiagnosisSummaryDto
        {
            ProjectId = 1, StationCode = "OP040", OverallStatus = "SomeFalse", StationOrder = 4,
            ActiveAlarms = [new() { AlarmVariable = "DB.OP040.CYL01.Timeout", Kind = "Timeout" }]
        };
        var report = Model("OP030");
        Guard(report, [.. Line(), alarm]);
        Assert.Equal("OP040", report.RootCauseStation);
        Assert.False(report.RootCauseTentative);
    }

    [Fact]
    public void Confirmed_violation_at_the_picked_station_is_not_marked_unconfirmed()
    {
        var report = Model("OP030");
        Guard(report, Line(),
            "1. 工位: OP030\n判定: 确认违规\n当前值: OP030_CYL05_W(%I10.1)=false\n原因: PLC 超时报警已触发");
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.False(report.RootCauseTentative);
    }

    [Fact]
    public void English_output_labels_the_unconfirmed_root()
    {
        var report = Model("OP030");
        Guard(report, Line(), language: "en-US");
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.StartsWith("Unconfirmed", report.RootCauseDescription);
    }
}
