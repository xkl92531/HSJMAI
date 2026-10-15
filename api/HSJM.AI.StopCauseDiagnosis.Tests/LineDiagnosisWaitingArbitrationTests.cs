using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 典型场景：OP030 气缸卡住，OP010/OP020 因等 OP030 而连带异常。
/// 一键排查应该把主因定到 OP030，而不是排在前面、证据看起来也很多的 OP010/OP020。
/// </summary>
public class LineDiagnosisWaitingArbitrationTests
{
    private static StationAnchorFinding CylinderStuck(string station, string cyl, string address) => new()
    {
        VariableName = $"{station}_{cyl}_FwdPos",
        DisplayName = $"{station} {cyl} 伸出到位",
        EvaluatedValue = false,
        EvaluationStatus = "False",
        DiagnosisPhase = "GraphTransitionTrace",
        FaultOwnership = "InternalFault",
        TraceDepth = 1,
        FailurePathSummary = $"🔍 根因终点（I点）：{station}_{cyl}_FwdPos，当前值=False",
        DiagnosisQuality = "Diagnosed",
        IoTagAddress = address,
    };

    private static StationAnchorFinding WaitsOnSignal(string variable, string ownership = "InternalFault") => new()
    {
        VariableName = variable,
        DisplayName = variable,
        EvaluatedValue = false,
        EvaluationStatus = "False",
        DiagnosisPhase = "GraphTransitionTrace",
        FaultOwnership = ownership,
        TraceDepth = 1,
        FailurePathSummary = $"[Transition] {variable}=False",
        DiagnosisQuality = "Diagnosed",
    };

    private static StationDiagnosisSummaryDto Station(string code, int order, params StationAnchorFinding[] findings) => new()
    {
        StationCode = code,
        StationOrder = order,
        OverallStatus = "SomeFalse",
        KeyFindings = findings.ToList(),
    };

    /// <summary>OP010/OP020 自己也有气缸没到位（被停下的连带现象），同时转移条件卡在 OP030 的信号上。</summary>
    private static List<StationDiagnosisSummaryDto> Op030StuckLine(params StationAnchorFinding[] op030Findings) =>
    [
        Station("OP010", 1,
            CylinderStuck("OP010", "CYL01", "%I1.0"),
            CylinderStuck("OP010", "CYL02", "%I1.1"),
            CylinderStuck("OP010", "CYL03", "%I1.2"),
            WaitsOnSignal("OP030-V90-OverTurn-Z1")),
        Station("OP020", 2,
            CylinderStuck("OP020", "CYL01", "%I2.0"),
            CylinderStuck("OP020", "CYL02", "%I2.1"),
            WaitsOnSignal("DB345_OP30Cylinder.Valve[5].Position")),
        Station("OP030", 3, op030Findings),
    ];

    [Fact]
    public void Inferred_waits_point_to_the_station_named_in_the_blocked_signal()
    {
        var profiles = LineDiagnosisLlmService.BuildEvidenceProfiles(
            Op030StuckLine(CylinderStuck("OP030", "CYL05", "%I3.0")));

        Assert.Equal(["OP030"], profiles.Single(p => p.StationCode == "OP010").WaitingOn);
        // OP30 这种两位写法也要对上 OP030
        Assert.Equal(["OP030"], profiles.Single(p => p.StationCode == "OP020").WaitingOn);
        Assert.Empty(profiles.Single(p => p.StationCode == "OP030").WaitingOn);
        Assert.Equal("OP030", profiles[0].StationCode);
    }

    [Fact]
    public void Llm_pick_of_a_waiting_station_is_corrected_to_the_station_it_waits_on()
    {
        var summaries = Op030StuckLine(CylinderStuck("OP030", "CYL05", "%I3.0"));
        var report = new LineDiagnosisLlmReport { Success = true, RootCauseStation = "OP010", Confidence = 0.6 };

        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles(summaries));

        Assert.Equal("OP030", report.RootCauseStation);
        Assert.Contains("等待", report.RootCauseDescription);
        Assert.Equal("Impacted", report.StationAnalysis.Single(s => s.StationCode == "OP010").Role);
        Assert.Equal("RootCause", report.StationAnalysis.Single(s => s.StationCode == "OP030").Role);
    }

    [Fact]
    public void Correct_llm_pick_is_not_overridden_by_a_waiting_station_with_more_actuator_evidence()
    {
        // OP030 卡在机器人完成信号上：按词义只算流程/握手证据，分数比 OP010 低
        var summaries = Op030StuckLine(WaitsOnSignal("OP030_Robot_WorkDone"));
        var report = new LineDiagnosisLlmReport { Success = true, RootCauseStation = "OP030", Confidence = 0.7 };

        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles(summaries));

        Assert.Equal("OP030", report.RootCauseStation);
    }

    [Fact]
    public void Without_waiting_relations_the_0615_evidence_review_still_applies()
    {
        // 看不出谁在等谁时，保持 06-15 的证据复核：握手类结论让位给执行机构卡滞更明确的工位
        var summaries = new List<StationDiagnosisSummaryDto>
        {
            Station("OP010", 1,
                CylinderStuck("OP010", "CYL01", "%I1.0"),
                CylinderStuck("OP010", "CYL02", "%I1.1"),
                CylinderStuck("OP010", "CYL03", "%I1.2")),
            Station("OP030", 3, WaitsOnSignal("OP030_Robot_WorkDone")),
        };
        var report = new LineDiagnosisLlmReport { Success = true, RootCauseStation = "OP030" };

        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles(summaries));

        Assert.Equal("OP010", report.RootCauseStation);
        Assert.StartsWith("结构化证据复核", report.RootCauseDescription);
    }

    [Fact]
    public void A_waiting_station_is_never_picked_by_the_evidence_review()
    {
        var summaries = Op030StuckLine(WaitsOnSignal("OP030_Robot_WorkDone"));
        var report = new LineDiagnosisLlmReport { Success = true, RootCauseStation = null };

        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles(summaries));

        Assert.Null(report.RootCauseStation);
    }

    [Fact]
    public void Prompt_has_no_robot_stopped_with_the_line_rule()
    {
        var summaries = Op030StuckLine(CylinderStuck("OP030", "CYL05", "%I3.0"));

        var prompt = LineDiagnosisLlmService.BuildPrompt(summaries, evidenceProfiles: LineDiagnosisLlmService.BuildEvidenceProfiles(summaries));

        Assert.DoesNotContain("伴随停机", prompt);
        Assert.DoesNotContain("IN_HOME", prompt);
    }

    [Fact]
    public void Explicit_waiting_ownership_is_followed_through_a_chain()
    {
        var summaries = new List<StationDiagnosisSummaryDto>
        {
            Station("OP010", 1, CylinderStuck("OP010", "CYL01", "%I1.0"), WaitsOnSignal("DB10.Step3.T", "WaitingFor:OP020")),
            Station("OP020", 2, CylinderStuck("OP020", "CYL01", "%I2.0"), WaitsOnSignal("DB20.Step5.T", "WaitingFor:OP030")),
            Station("OP030", 3, CylinderStuck("OP030", "CYL05", "%I3.0")),
        };
        var report = new LineDiagnosisLlmReport { Success = true, RootCauseStation = "OP010" };

        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles(summaries));

        Assert.Equal("OP030", report.RootCauseStation);
    }

    [Fact]
    public void Mutual_waits_do_not_force_a_correction()
    {
        var summaries = new List<StationDiagnosisSummaryDto>
        {
            Station("OP010", 1, CylinderStuck("OP010", "CYL01", "%I1.0"), WaitsOnSignal("OP030-V90-OverTurn-Z2")),
            Station("OP030", 3, CylinderStuck("OP030", "CYL05", "%I3.0"), WaitsOnSignal("OP010-V90-OverTurn-Z1")),
        };
        var report = new LineDiagnosisLlmReport { Success = true, RootCauseStation = "OP030" };

        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles(summaries));

        Assert.Equal("OP030", report.RootCauseStation);
    }

    [Fact]
    public void Waits_on_a_normal_station_are_ignored()
    {
        var summaries = Op030StuckLine(CylinderStuck("OP030", "CYL05", "%I3.0"));
        summaries[2].OverallStatus = "Running";

        var profiles = LineDiagnosisLlmService.BuildEvidenceProfiles(summaries);

        Assert.Empty(profiles.Single(p => p.StationCode == "OP010").WaitingOn);
    }

    [Fact]
    public void Constraint_violation_at_a_waiting_station_does_not_outrank_the_blocking_station()
    {
        var summaries = Op030StuckLine(CylinderStuck("OP030", "CYL05", "%I3.0"));
        var constraintReport = string.Join("\n",
            "1. 工位: OP010",
            "原因: 屏蔽态报警传感器违反: OP010_CYL01_W(%I1.0) 当前为 true，应永为 false");

        var profiles = LineDiagnosisLlmService.BuildEvidenceProfiles(summaries, constraintReport);
        var op010 = profiles.Single(p => p.StationCode == "OP010");

        Assert.Equal("OP030", profiles[0].StationCode);
        Assert.Equal(1, op010.ConstraintPhysicalIoFaultCount);

        var report = new LineDiagnosisLlmReport { Success = true, RootCauseStation = "OP010" };
        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, profiles);
        Assert.Equal("OP030", report.RootCauseStation);
    }

    [Fact]
    public void Prompt_shows_waits_and_no_longer_tells_the_model_to_prefer_constraint_stations()
    {
        var summaries = Op030StuckLine(CylinderStuck("OP030", "CYL05", "%I3.0"));
        var constraintReport = "1. 工位: OP010\n原因: 屏蔽态报警传感器违反: OP010_CYL01_W(%I1.0) 当前为 true，应永为 false";
        var profiles = LineDiagnosisLlmService.BuildEvidenceProfiles(summaries, constraintReport);

        var prompt = LineDiagnosisLlmService.BuildPrompt(summaries, constraintReport: constraintReport, evidenceProfiles: profiles);

        Assert.Contains("WaitingOn=OP030", prompt);
        Assert.DoesNotContain("优先把它作为根因候选", prompt);
        Assert.DoesNotContain("重要参考依据", prompt);
    }
}
