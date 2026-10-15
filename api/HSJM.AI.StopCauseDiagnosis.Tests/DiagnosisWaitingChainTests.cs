using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class DiagnosisWaitingChainTests
{
    [Fact]
    public void ActiveStationWithTransientFalseIsNotMarkedAsBlocked()
    {
        var active = Station("OP020", "OP030");
        active.OverallStatus = "Running";
        var report = Diagnose([active, Fault("OP030")]);
        Assert.Empty(report.SecondaryCauses);
        Assert.NotEqual("Impacted", Analysis(report, "OP020").Role);
    }

    private static StationDiagnosisSummaryDto Station(string code, string waitsFor = null, bool? condition = false,
        long projectId = 1, int order = 0) => new()
    {
        StationCode = code, ProjectId = projectId, StationOrder = order, OverallStatus = "SomeFalse",
        KeyFindings = waitsFor == null ? [] : [new() { VariableName = $"{code}.Permit", EvaluatedValue = condition,
            EvaluationStatus = condition.HasValue ? condition.Value.ToString() : "Unknown",
            FaultOwnership = $"WaitingFor:{waitsFor}" }]
    };

    private static StationDiagnosisSummaryDto Fault(string code, long projectId = 1)
    {
        var station = Station(code, projectId: projectId);
        station.ActiveAlarms = [new()
        {
            AlarmVariable = "DB.Alarms.Type.CylinderOutErrors.CYL01", Text = "CYL01 气缸伸出超时",
            Kind = "FwdError", ValveInstance = "CYL01",
            OutputSignal = new() { Name = "CYL01.Command", Address = "%Q31.0", Value = true },
            Feedbacks = [new() { Name = "CYL01.Extended", Address = "%I31.0", Value = false },
                new() { Name = "CYL01.Home", Address = "%I31.1", Value = null }]
        }];
        return station;
    }

    private static LineDiagnosisLlmReport Diagnose(List<StationDiagnosisSummaryDto> stations, string selected = "OP020",
        string language = "zh-CN")
    {
        var report = new LineDiagnosisLlmReport { Success = true, RootCauseStation = selected,
            RootCauseDescription = "模型误判通信故障", Reasoning = "模型误判通信故障", Confidence = .9 };
        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles(stations), language);
        return report;
    }

    private static LineDiagnosisStationAnalysis Analysis(LineDiagnosisLlmReport report, string code) =>
        Assert.Single(report.StationAnalysis, item => item.StationCode == code);

    [Theory]
    [InlineData("OP010", "OP020", "OP030")]
    [InlineData("Load-Z", "Transfer-A", "Clamp-X")]
    [InlineData("OP090", "OP010", "OP005")]
    public void Explicit_false_wait_chain_identifies_fault_and_dependents_without_station_order_assumptions(
        string first, string middle, string root)
    {
        var report = Diagnose([Station(middle, root, order: 99), Fault(root), Station(first, middle, order: 3)], middle);
        Assert.Equal(root, report.RootCauseStation);
        Assert.Equal("RootCause", Analysis(report, root).Role);
        Assert.Equal("Impacted", Analysis(report, middle).Role);
        Assert.Equal("Impacted", Analysis(report, first).Role);
        Assert.Contains($"{first} 等待 {middle}", report.ImpactChain);
        Assert.Contains($"{middle} 等待 {root}", report.ImpactChain);
        Assert.Contains($"主要异常位于 {root}", report.RootCauseDescription);
        Assert.Contains("CYL01 气缸伸出超时", report.RootCauseDescription);
        Assert.Contains("CYL01.Command(%Q31.0)=true", report.RootCauseDescription);
        Assert.Contains("CYL01.Extended(%I31.0)=false", report.RootCauseDescription);
        Assert.Contains("CYL01.Home(%I31.1)=Unknown", report.RootCauseDescription);
        Assert.Contains("可能为锁存", report.RootCauseDescription);
        Assert.Equal(new[] { first, middle }.OrderBy(x => x), report.SecondaryCauses.Select(x => x.StationCode).OrderBy(x => x));
        Assert.DoesNotContain("模型误判", report.RootCauseDescription + report.Reasoning + report.ImpactChain);
    }

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(null, "False")]
    [InlineData(false, "Unknown")]
    [InlineData(true, "True")]
    public void Unknown_inconsistent_or_satisfied_condition_does_not_establish_wait_edge(bool? value, string status)
    {
        var middle = Station("OP020", "OP030", value);
        middle.KeyFindings[0].EvaluationStatus = status;
        var report = Diagnose([Station("OP010", "OP020"), middle, Fault("OP030")]);
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.Equal("Unknown", Analysis(report, "OP020").Role);
        Assert.Equal("Unknown", Analysis(report, "OP010").Role);
        Assert.Empty(report.SecondaryCauses);
    }

    [Fact]
    public void Station_order_or_arbitrary_text_referencing_root_does_not_establish_an_edge()
    {
        var station = Station("OP020", order: 2);
        station.KeyFindings = [new() { VariableName = "OP030.Permit", EvaluatedValue = false,
            EvaluationStatus = "False", FaultOwnership = "InternalFault", FailurePathSummary = "等待OP030" }];
        var report = Diagnose([Station("OP010", order: 1), station, Fault("OP030")]);
        Assert.Equal("Unknown", Analysis(report, "OP010").Role);
        Assert.Equal("Unknown", Analysis(report, "OP020").Role);
        Assert.Empty(report.SecondaryCauses);
    }

    [Fact]
    public void Cycle_members_are_not_downgraded_to_impacted_even_with_an_outgoing_edge_to_root()
    {
        var middle = Station("OP020", "OP010");
        middle.KeyFindings.Add(Station("OP020", "OP030").KeyFindings[0]);
        var report = Diagnose([Station("OP010", "OP020"), middle, Fault("OP030")]);
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.Equal("Unknown", Analysis(report, "OP010").Role);
        Assert.Equal("Unknown", Analysis(report, "OP020").Role);
        Assert.Empty(report.SecondaryCauses);
    }

    [Fact]
    public void Wait_cycle_involving_root_does_not_prove_direction_of_blocking()
    {
        var root = Fault("OP030");
        root.KeyFindings = Station("OP030", "OP020").KeyFindings;
        var report = Diagnose([Station("OP020", "OP030"), root]);
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.Equal("Unknown", Analysis(report, "OP020").Role);
        Assert.Empty(report.SecondaryCauses);
    }

    [Fact]
    public void Cross_project_duplicate_intermediate_station_breaks_attribution_chain()
    {
        var report = Diagnose([Station("OP010", "OP020"), Station("OP020", "OP030"),
            Station("OP020", "OP030", projectId: 2), Fault("OP030")]);
        Assert.Equal("OP030", report.RootCauseStation);
        Assert.Equal("Unknown", Analysis(report, "OP010").Role);
        Assert.Equal("Unknown", Analysis(report, "OP020").Role);
        Assert.Empty(report.SecondaryCauses);
    }

    [Fact]
    public void Independent_fault_at_intermediate_station_is_not_relabelled_as_merely_impacted()
    {
        var middle = Fault("OP020");
        middle.KeyFindings = Station("OP020", "OP030").KeyFindings;
        var report = Diagnose([Station("OP010", "OP020"), middle, Fault("OP030")], "OP030");
        Assert.Null(report.RootCauseStation); // Two independent faults; model preference is not a unique-root proof.
        Assert.NotEqual("Impacted", Analysis(report, "OP020").Role);
        Assert.Equal("Impacted", Analysis(report, "OP010").Role);
        var dependent = Assert.Single(report.SecondaryCauses);
        Assert.Equal("OP010", dependent.StationCode);
        Assert.Contains("OP010 等待 OP020", dependent.Description);
        Assert.DoesNotContain("OP020 等待 OP030", dependent.Description);
        Assert.Contains("CYL01", Analysis(report, "OP020").Summary);
    }

    [Fact]
    public void Wait_graph_without_any_independent_fault_cannot_create_a_root()
    {
        var report = Diagnose([Station("Feed", "Transfer"), Station("Transfer", "Clamp"), Station("Clamp")], "Clamp");
        Assert.Null(report.RootCauseStation);
        Assert.All(report.StationAnalysis, station => Assert.Equal("Unknown", station.Role));
        Assert.Empty(report.SecondaryCauses);
    }

    [Fact]
    public void Explicit_wait_can_cross_projects_when_station_identities_are_unique()
    {
        var report = Diagnose([Station("Feed", "Clamp", projectId: 1), Fault("Clamp", projectId: 2)], "Feed");
        Assert.Equal("Clamp", report.RootCauseStation);
        Assert.Equal("Impacted", Analysis(report, "Feed").Role);
        Assert.Contains("Feed 等待 Clamp", report.ImpactChain);
    }

    [Fact]
    public void Multiple_independent_faults_keep_waiters_for_each_candidate_without_model_selected_unique_root()
    {
        var report = Diagnose([Station("Feed", "Clamp"), Fault("Clamp"), Fault("Press"), Station("Unload", "Press")], "Clamp");
        Assert.Null(report.RootCauseStation);
        Assert.Equal("Impacted", Analysis(report, "Feed").Role);
        Assert.NotEqual("Impacted", Analysis(report, "Press").Role);
        Assert.Equal("Impacted", Analysis(report, "Unload").Role);
        Assert.Equal(2, report.SecondaryCauses.Count);
    }

    [Fact]
    public void English_waiting_chain_is_localized_and_keeps_signal_identifiers()
    {
        var report = Diagnose([Station("Load", "Clamp"), Fault("Clamp")], "Load", "en-US");
        Assert.Equal("Clamp", report.RootCauseStation);
        Assert.Equal("Impacted", Analysis(report, "Load").Role);
        Assert.Contains("Load waits for Clamp", report.ImpactChain);
        Assert.Contains("CYL01.Command(%Q31.0)=true", report.RootCauseDescription);
    }

    [Theory]
    [InlineData(false, "FALSE")]
    [InlineData(true, "TRUE")]
    [InlineData(null, "UNKNOWN")]
    public void Agent_prompt_preserves_three_valued_signal_state(bool? value, string expected)
    {
        var method = typeof(DiagnosisAgentService).GetMethod("BuildTaskDescription",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var prompt = (string)method.Invoke(null, [new List<StationDiagnosisSummaryDto> { Station("Feed", "Clamp", value) }, null, "zh-CN"]);
        Assert.Contains($"Feed.Permit = {expected}", prompt);
        Assert.DoesNotContain("尤其关注 FaultOwnership 标记为 InternalFault", prompt);
    }
}
