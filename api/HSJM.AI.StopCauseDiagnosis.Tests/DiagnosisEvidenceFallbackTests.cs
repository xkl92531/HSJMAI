using System.Reflection;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class DiagnosisEvidenceFallbackTests
{
    private const string UntrustedClaim = "OP020通信中断已确认";
    private static StationDiagnosisSummaryDto Fault(string name, string status = "SomeFalse", int alarms = 1) => new()
    {
        StationCode = name, ProjectId = 1, OverallStatus = status,
        ActiveAlarms = Enumerable.Range(1, alarms).Select(i => new ActiveValveAlarmDto
        {
            AlarmVariable = $"DB.{name}.Errors.CYL{i:00}", Kind = "Latched", Text = $"CYL{i:00} 原生错误",
            Feedbacks = [new() { Name = $"CYL{i:00}.Pos", Address = $"%I{i}.0", Value = false }]
        }).ToList()
    };
    private static StationDiagnosisSummaryDto Wait(string name, params string[] targets) => new()
    {
        StationCode = name, ProjectId = 1, OverallStatus = "SomeFalse",
        KeyFindings = targets.Select(target => new StationAnchorFinding
        {
            VariableName = $"{target}.Ready", EvaluatedValue = false, EvaluationStatus = "False",
            FaultOwnership = $"WaitingFor:{target}", LastTrueAt = DateTime.UtcNow.AddHours(-2)
        }).ToList()
    };
    private static LineDiagnosisLlmReport Guard(List<StationDiagnosisSummaryDto> stations, bool success, string selected = "Feed")
    {
        var report = new LineDiagnosisLlmReport
        {
            Success = success, RootCauseStation = selected, RootCauseDescription = UntrustedClaim,
            Reasoning = UntrustedClaim, RawResponse = UntrustedClaim, Confidence = .9
        };
        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles(stations));
        return report;
    }
    private static void AssertEvidenceOnly(LineDiagnosisLlmReport report)
    {
        Assert.False(report.Success);
        Assert.True(report.EvidenceOnly);
        Assert.Contains("模型", report.RootCauseDescription);
        Assert.DoesNotContain(UntrustedClaim, report.RootCauseDescription + report.Reasoning + report.RawResponse);
        Assert.Null(report.RawResponse);
    }

    [Theory]
    [InlineData("Feed")]
    [InlineData("Clamp")]
    public void Multiple_independent_faults_retain_every_static_wait_chain_without_selecting_a_unique_root(string modelSelection)
    {
        var report = Guard([Fault("Clamp"), Fault("Press"), Wait("Feed", "Clamp"), Wait("Unload", "Press")], true, modelSelection);
        Assert.Null(report.RootCauseStation);
        Assert.Equal(2, report.SecondaryCauses.Count);
        Assert.Contains("Feed 等待 Clamp", report.ImpactChain);
        Assert.Contains("Unload 等待 Press", report.ImpactChain);
        Assert.Equal("Impacted", Assert.Single(report.StationAnalysis, s => s.StationCode == "Feed").Role);
        Assert.Equal("Impacted", Assert.Single(report.StationAnalysis, s => s.StationCode == "Unload").Role);
    }

    [Fact]
    public void One_waiter_reaching_multiple_candidates_lists_both_without_unique_attribution()
    {
        var report = Guard([Fault("Clamp"), Fault("Press"), Wait("Transfer", "Clamp", "Press"), Wait("Feed", "Transfer")], true);
        Assert.Null(report.RootCauseStation);
        var cause = Assert.Single(report.SecondaryCauses, s => s.StationCode == "Feed");
        Assert.Contains("Clamp", cause.Description);
        Assert.Contains("Press", cause.Description);
        Assert.Contains("无法唯一归属", cause.Description);
        Assert.DoesNotContain(report.StationAnalysis, s => s.Role == "RootCause");
    }

    [Theory]
    [InlineData("SomeFalse")]
    [InlineData("NoAst")]
    [InlineData("Skipped")]
    [InlineData("Running")]
    [InlineData("AllTrue")]
    [InlineData("Error")]
    public void Model_failure_does_not_hide_independent_alarm_in_any_station_activity_state(string status)
    {
        var report = Guard([Fault("Clamp", status), Wait("Feed", "Clamp")], false);
        Assert.Equal("Clamp", report.RootCauseStation);
        Assert.Contains("CYL01", report.RootCauseDescription);
        Assert.Contains("Feed 等待 Clamp", report.ImpactChain);
        AssertEvidenceOnly(report);
    }

    [Fact]
    public void Model_failure_without_confirmed_evidence_sanitizes_raw_error_and_has_no_root()
    {
        var report = Guard([Wait("Feed", "Clamp"), Wait("Clamp")], false);
        Assert.Null(report.RootCauseStation);
        Assert.Empty(report.SecondaryCauses);
        AssertEvidenceOnly(report);
    }

    [Fact]
    public void Public_evidence_keeps_the_seventh_and_later_specific_alarm_details()
    {
        var report = Guard([Fault("Clamp", alarms: 9)], true);
        foreach (var i in Enumerable.Range(1, 9))
        {
            Assert.Contains($"DB.Clamp.Errors.CYL{i:00}", report.RootCauseDescription);
            Assert.Contains($"CYL{i:00}.Pos(%I{i}.0)=false", Assert.Single(report.StationAnalysis).Summary);
        }
        Assert.DoesNotContain(UntrustedClaim, report.RootCauseDescription + report.Reasoning);
    }

    [Fact]
    public async Task Traditional_service_exception_preserves_alarm_and_wait_evidence()
    {
        var service = new LineDiagnosisLlmService(NullLogger<LineDiagnosisLlmService>.Instance, null, null);
        var report = await service.AnalyzeAsync([Fault("Clamp"), Wait("Feed", "Clamp")]);
        Assert.Equal("Clamp", report.RootCauseStation);
        Assert.Contains("Feed 等待 Clamp", report.ImpactChain);
        AssertEvidenceOnly(report);
    }

    [Fact]
    public async Task Agent_service_exception_preserves_alarm_and_wait_evidence()
    {
        var service = new DiagnosisAgentService(NullLogger<DiagnosisAgentService>.Instance, null, null, null, null);
        var report = await service.AnalyzeWithAgentAsync(1, [Fault("Clamp"), Wait("Feed", "Clamp")]);
        Assert.Equal("Clamp", report.RootCauseStation);
        AssertEvidenceOnly(report);
    }

    [Theory]
    [InlineData(false, "OP020通信中断已确认")]
    [InlineData(true, "")]
    public void Agent_failed_or_empty_response_reaches_evidence_guard(bool success, string content)
    {
        var parse = typeof(DiagnosisAgentService).GetMethod("ParseAgentResult", BindingFlags.NonPublic | BindingFlags.Static);
        var source = new ReActExecutionResult { Success = success, FinalAnswer = content, ErrorMessage = UntrustedClaim };
        var report = (LineDiagnosisLlmReport)parse.Invoke(null, [source, "zh-CN"]);
        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles([Fault("Clamp")]));
        Assert.Equal("Clamp", report.RootCauseStation);
        AssertEvidenceOnly(report);
        Assert.Equal(content, source.FinalAnswer); // engine audit source is not mutated
    }

    [Theory]
    [InlineData(false, "OP020通信中断已确认")]
    [InlineData(false, null)]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public void Traditional_failed_or_empty_model_response_uses_the_same_evidence_boundary(bool success, string content)
    {
        var report = LineDiagnosisLlmService.CompleteModelResponse(success, content,
            LineDiagnosisLlmService.BuildEvidenceProfiles([Fault("Clamp"), Wait("Feed", "Clamp")]));
        Assert.Equal("Clamp", report.RootCauseStation);
        Assert.Contains("Feed 等待 Clamp", report.ImpactChain);
        AssertEvidenceOnly(report);
    }

    [Fact]
    public void Multi_candidate_paths_do_not_cross_cycles_or_ambiguous_station_names()
    {
        var cyclic = Wait("Cycle", "Cycle", "Clamp");
        var duplicate = Wait("Duplicate", "Clamp");
        var otherProject = Wait("Duplicate", "Clamp");
        otherProject.ProjectId = 2;
        var report = Guard([Fault("Clamp"), Fault("Press"), cyclic, duplicate, otherProject,
            Wait("Blocked", "Duplicate"), Wait("SafeWait", "Press")], false);
        Assert.Null(report.RootCauseStation);
        var onlyProven = Assert.Single(report.SecondaryCauses);
        Assert.Equal("SafeWait", onlyProven.StationCode);
        Assert.Contains("SafeWait 等待 Press", report.ImpactChain);
        Assert.DoesNotContain("Cycle 等待", report.ImpactChain);
        Assert.DoesNotContain("Duplicate 等待", report.ImpactChain);
        AssertEvidenceOnly(report);
    }

    [Fact]
    public void Evidence_only_projection_is_idempotent_and_serializes_its_explicit_marker()
    {
        var profiles = LineDiagnosisLlmService.BuildEvidenceProfiles([Fault("Clamp"), Wait("Feed", "Clamp")]);
        var report = LineDiagnosisLlmService.CompleteModelResponse(false, UntrustedClaim, profiles);
        var firstDescription = report.RootCauseDescription;
        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, profiles);
        Assert.Equal(firstDescription, report.RootCauseDescription);
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"evidenceOnly\":true", json);
        Assert.Contains("\"success\":false", json);
        Assert.DoesNotContain(UntrustedClaim, json);
    }

    [Fact]
    public async Task Empty_station_list_does_not_discard_confirmed_constraint_report()
    {
        var service = new LineDiagnosisLlmService(NullLogger<LineDiagnosisLlmService>.Instance, null, null);
        var report = await service.AnalyzeAsync([], "1. 工位: OP030\n判定: 确认违规\n当前值: DB.CYL01.Error=true\n原因: PLC 超时报警已触发");
        Assert.Equal("OP030", report.RootCauseStation);
        AssertEvidenceOnly(report);
    }

    [Fact]
    public async Task Per_station_model_exception_returns_evidence_instead_of_throwing_it_away()
    {
        var service = new LineDiagnosisLlmService(NullLogger<LineDiagnosisLlmService>.Instance, null, null);
        var text = await service.AnalyzeStationAsync(Fault("Clamp"));
        Assert.Contains("CYL01", text);
        Assert.Contains("模型", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_agent_or_station_analysis_never_converts_cancellation_into_fallback(bool agent)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (agent)
        {
            var service = new DiagnosisAgentService(NullLogger<DiagnosisAgentService>.Instance, null, null, null, null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeWithAgentAsync(1, [Fault("Clamp")], cancellationToken: cancellation.Token));
        }
        else
        {
            var service = new LineDiagnosisLlmService(NullLogger<LineDiagnosisLlmService>.Instance, null, null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AnalyzeStationAsync(Wait("Feed", "Clamp"), cancellationToken: cancellation.Token));
        }
    }
}
