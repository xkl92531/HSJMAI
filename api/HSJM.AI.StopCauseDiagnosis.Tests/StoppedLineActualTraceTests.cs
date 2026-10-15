using HSJM.AI.AppLayer.StopCauseDiagnosis;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class StoppedLineActualTraceTests
{
    [Fact]
    public void ConfirmedFaultSpecificPointAtEndOfLongReasonMustReachPublicSummary()
    {
        var reason = new string('x', 300) + "；实际故障点 OP030_CYL09_W(%I31.7)=false";
        var constraints = $"1. 工位: OP030\n故障点: OP030_YV09_W\n判定: 确认违规\n原因: {reason}\n";
        var model = new LineDiagnosisLlmReport { Success = true };
        LineDiagnosisLlmService.ApplyEvidenceArbitration(model,
            LineDiagnosisLlmService.BuildEvidenceProfiles([new() { ProjectId = 1, StationCode = "OP030", OverallStatus = "NoAst" }], constraints));
        Assert.Contains("实际故障点 OP030_CYL09_W(%I31.7)=false", model.RootCauseDescription);
        Assert.Contains(reason, model.StationAnalysis.Single().Summary);
    }

    static StopCauseAstNode Node(string type, params StopCauseAstNode[] children) => new() { NodeType = type, Children = children.ToList() };
    static StopCauseAstNode Signal(string name) => new() { NodeType = StopCauseAstNodeType.Contact, VariableName = name };

    [Theory]
    [InlineData("not")]
    [InlineData("or")]
    public void SatisfiedBranchFalseIsNotATransitiveWaitForTheOtherStation(string branch)
    {
        var satisfied = branch == "not" ? Node(StopCauseAstNodeType.Not, Signal("B.Ready"))
            : Node(StopCauseAstNodeType.Or, Signal("B.Ready"), Signal("C.Ready"));
        var root = Node(StopCauseAstNodeType.GraphTransition,
            Node(StopCauseAstNodeType.And, satisfied, Signal("A.Sensor")));
        var context = new StopCauseExecutionContext { CurrentValues = new() {
            ["B.Ready"] = false, ["C.Ready"] = true, ["A.Sensor"] = false },
            SignalQuality = new() { ["B.Ready"] = "Good", ["C.Ready"] = "Good", ["A.Sensor"] = "Good" } };
        var trace = new StopCauseAstRuntimeEvaluationService(null, null, null).EvaluateNode(root, context);
        Assert.False(trace.Result);
        var blockers = StopCauseDiagnosisApp.GetBlockingFalseLeaves(trace);
        Assert.Equal(new[] { "A.Sensor" }, blockers.Select(b => b.VariableName));

        var a = new StationDiagnosisSummaryDto { ProjectId = 1, StationCode = "A", OverallStatus = "SomeFalse",
            KeyFindings = blockers.Select(b => new StationAnchorFinding { VariableName = b.VariableName,
                EvaluatedValue = b.Result, EvaluationStatus = b.Status,
                FaultOwnership = b.VariableName.StartsWith("B.") ? "WaitingFor:B" : "InternalFault" }).ToList() };
        var b = new StationDiagnosisSummaryDto { ProjectId = 1, StationCode = "B", OverallStatus = "NoAst",
            ActiveAlarms = [new() { AlarmVariable = "DB_B.Type.Errors.Error1", Kind = "Latched" }] };
        var report = new LineDiagnosisLlmReport { Success = true, RootCauseStation = "B" };
        LineDiagnosisLlmService.ApplyEvidenceArbitration(report, LineDiagnosisLlmService.BuildEvidenceProfiles([a,b]));
        Assert.DoesNotContain(report.StationAnalysis, s => s.StationCode == "A" && s.Role == "Impacted");
        Assert.DoesNotContain(report.SecondaryCauses, s => s.StationCode == "A");
    }

    [Theory]
    [InlineData("NoAst")]
    [InlineData("SkippedNoActiveGraph")]
    [InlineData("Running")]
    [InlineData("Error")]
    public void StableNativeFaultStartsSummaryWithoutGraphOrIoOption(string state)
    {
        var result = new LineDiagnosisResultDto { AbnormalStations = 0,
            StationSummaries = [new() { ProjectId = 1, StationCode = "Clamp", OverallStatus = state,
                ActiveAlarms = [new() { AlarmVariable = "DB.Type.CylinderOutErrors.CYL01_FwdError", Kind = "FwdError" }] }] };
        Assert.True(StopCauseDiagnosisApp.ShouldRunLineSummary(new() {
            UseLlmAnalysis = true, IncludeConstraintAnalysis = false, ForceLlmSummaryWhenNoAbnormal = false }, result));
    }

    [Fact]
    public void StablePositiveBlockerSurvivesDoubleNegationWithoutAnySignalChange()
    {
        var root = Node(StopCauseAstNodeType.GraphTransition,
            Node(StopCauseAstNodeType.Not, Node(StopCauseAstNodeType.Not, Signal("Clamp.Ready"))));
        var context = new StopCauseExecutionContext { CurrentValues = new() { ["Clamp.Ready"] = false },
            PreviousValues = new() { ["Clamp.Ready"] = false }, SignalQuality = new() { ["Clamp.Ready"] = "Good" } };
        var evaluator = new StopCauseAstRuntimeEvaluationService(null, null, null);
        var first = evaluator.EvaluateNode(root, context);
        context.Timestamp = context.Timestamp.AddMinutes(10);
        var stopped = evaluator.EvaluateNode(root, context);
        Assert.False(first.Result);
        Assert.Equal("Clamp.Ready", Assert.Single(StopCauseDiagnosisApp.GetBlockingFalseLeaves(stopped)).VariableName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void SatisfiedOrUnknownAlternativesDoNotCreateFalseWaitingEdges(bool? other)
    {
        var context = new StopCauseExecutionContext { CurrentValues = new() { ["A.Ready"] = false },
            SignalQuality = new() { ["A.Ready"] = "Good" } };
        if (other.HasValue) { context.CurrentValues["B.Ready"] = other.Value; context.SignalQuality["B.Ready"] = "Good"; }
        var root = Node(StopCauseAstNodeType.GraphTransition, Node(StopCauseAstNodeType.Or, Signal("A.Ready"), Signal("B.Ready")));
        var trace = new StopCauseAstRuntimeEvaluationService(null, null, null).EvaluateNode(root, context);
        Assert.Equal(other, trace.Result);
        Assert.Empty(StopCauseDiagnosisApp.GetBlockingFalseLeaves(trace));
    }

    [Fact]
    public void WarningOnlyOrExplicitlyDisabledModelDoesNotTriggerModelAnalysis()
    {
        var result = new LineDiagnosisResultDto { StationSummaries = [new() { StationCode = "Clamp", OverallStatus = "NoAst",
            ActiveAlarms = [new() { Kind = "FwdLock", AlarmVariable = "DB.Type.CylinderLockErrors.Warning" }] }] };
        Assert.False(StopCauseDiagnosisApp.ShouldRunLineSummary(new() { UseLlmAnalysis = true }, result));
        result.StationSummaries[0].ActiveAlarms = [new() { Kind = "FwdError", AlarmVariable = "DB.Type.Errors.Fault" }];
        Assert.False(StopCauseDiagnosisApp.ShouldRunLineSummary(new() { UseLlmAnalysis = false }, result));
    }

    [Theory]
    [InlineData(true, "Bad")]
    [InlineData(false, "Missing")]
    [InlineData(false, "BadCommFailure")]
    public void BadQualityCachedValuesRemainUnknownInsteadOfNormalOrBlocking(bool value, string quality)
    {
        var context = new StopCauseExecutionContext { CurrentValues = new() { ["B.Ready"] = value },
            SignalQuality = new() { ["B.Ready"] = quality } };
        var trace = new StopCauseAstRuntimeEvaluationService(null, null, null).EvaluateNode(Signal("B.Ready"), context);
        Assert.Null(trace.Result);
        Assert.Equal("Unknown", trace.Status);
        Assert.Empty(StopCauseDiagnosisApp.GetBlockingFalseLeaves(trace));
    }

    [Fact]
    public void ExplicitlyMissingFullPathMustNotBorrowAnotherSignalsGoodTailValue()
    {
        var context = new StopCauseExecutionContext { CurrentValues = new() { ["Ready"] = false },
            SignalQuality = new() { ["B.Ready"] = "Missing", ["Ready"] = "Good" } };
        var trace = new StopCauseAstRuntimeEvaluationService(null, null, null).EvaluateNode(Signal("B.Ready"), context);
        Assert.Null(trace.Result);
        Assert.Empty(StopCauseDiagnosisApp.GetBlockingFalseLeaves(trace));
    }

    [Theory]
    [InlineData("~IO.Sensor", false, false, false, null)]
    [InlineData("IO.Sensor|IO.Bypass", false, false, false, null)]
    [InlineData("IO.Bypass|IO.Sensor", false, false, false, null)]
    [InlineData("IO.Sensor", false, false, false, null)]
    [InlineData("IO.Sensor|IO.Bypass", false, false, true, true)]
    [InlineData("IO.Sensor", true, true, false, true)]
    [InlineData("IO.Sensor", false, true, false, false)]
    [InlineData("~IO.Sensor", true, true, false, false)]
    [InlineData("~IO.Sensor", false, true, false, true)]
    public async Task ActualAsyncEntryPreservesThreeValuesAndQualityWhenInjectingFbInputs(
        string mapping, bool sensor, bool sensorGood, bool bypass, bool? expected)
    {
        var result = await EvaluateMappedInputAsync(mapping, sensor, sensorGood, bypass);
        Assert.Equal(expected, result.EvaluatedValue);
        Assert.Equal(expected.HasValue ? "Good" : "Unknown", result.ExecutionContext.SignalQuality["CylinderCmd.IWP"]);
        Assert.Equal(expected, result.ExecutionContext.CurrentValues["CylinderCmd.IWP"]);
        if (!expected.HasValue) Assert.Empty(StopCauseDiagnosisApp.GetBlockingFalseLeaves(result.RootEvaluation));
    }

    [Fact]
    public async Task BadNormallyClosedActualCannotCreateABlockerThroughANegatedLocalFormal()
    {
        var result = await EvaluateMappedInputAsync("~IO.Sensor", false, false, false,
            root: Node(StopCauseAstNodeType.Not, Signal("#IWP")));
        Assert.Null(result.EvaluatedValue);
        Assert.Empty(StopCauseDiagnosisApp.GetBlockingFalseLeaves(result.RootEvaluation));
    }

    [Fact]
    public async Task UnknownMappedActualOverwritesPreviouslyReadableFormalInsteadOfRevivingIt()
    {
        var result = await EvaluateMappedInputAsync("IO.Sensor", false, false, false, formalGood: true);
        Assert.Null(result.EvaluatedValue);
        Assert.Null(result.ExecutionContext.CurrentValues["CylinderCmd.IWP"]);
    }

    private static int nextProject = 921000;

    private static async Task<EvaluatePersistedAstResultDto> EvaluateMappedInputAsync(string mapping, bool sensor, bool sensorGood, bool bypass,
        StopCauseAstNode root = null, bool formalGood = false)
    {
        // Negative isolated project IDs avoid metadata caches/DB queries; snapshot acquisition and
        // exact-result conversion, FB injection, and AST evaluation still use the production entry.
        var projectId = -Interlocked.Increment(ref nextProject);
        var projects = Proxy.Create<IPLC_ProjectService>((m, _) => m.Name == "GetById"
            ? new PLC_ProjectEntity { Id = projectId, IotUrl = "http://127.0.0.1:49999" } : throw new NotSupportedException(m.Name));
        var astRows = Proxy.Create<IPLC_LogicUnitAstService>((m, _) => m.Name == "GetListAsync"
            ? Task.FromResult(new List<PLC_LogicUnitAstEntity> { new() { ProjectId = projectId, LogicUnitId = 1,
                RootNodeJson = JsonSerializer.Serialize(root ?? Signal("CylinderCmd.IWP")) } }) : throw new NotSupportedException(m.Name));
        var exact = Proxy.Create<IPLC_VariableAccessService>((m, args) =>
        {
            if (m.Name != nameof(IPLC_VariableAccessService.ReadExactDataAsync)) throw new NotSupportedException(m.Name);
            var requests = (IReadOnlyCollection<PLC_ExactReadRequest>)args[1];
            return Task.FromResult<IReadOnlyList<PLC_ExactReadResult>>(requests.Select(r =>
            {
                var good = r.RequestKey == "IO.Bypass" || (r.RequestKey == "IO.Sensor" && sensorGood)
                    || (r.RequestKey == "CylinderCmd.IWP" && formalGood);
                object value = r.RequestKey == "IO.Sensor" ? sensor : r.RequestKey == "IO.Bypass" ? bypass : true;
                return new PLC_ExactReadResult(r.RequestKey, r.BlockName, r.VariablePath, r.RequestKey, value,
                    good ? PLC_ExactReadQuality.Good : PLC_ExactReadQuality.Bad, DateTimeOffset.UtcNow, good ? null : "Missing");
            }).ToList());
        });
        var snapshots = new StopCauseSignalSnapshotService(null, projects, null, null, null, exact);
        return await new StopCauseAstRuntimeEvaluationService(snapshots, astRows, null).EvaluateAsync(new() {
            ProjectId = projectId, LogicUnitId = 1, FbInputParamMap = new() { ["IWP"] = mapping } });
    }

    public class Proxy : DispatchProxy
    {
        private Func<MethodInfo, object[], object> handler;
        public static T Create<T>(Func<MethodInfo, object[], object> handler) where T : class
        {
            var proxy = Create<T, Proxy>();
            ((Proxy)(object)proxy).handler = handler;
            return proxy;
        }
        protected override object Invoke(MethodInfo method, object[] args) => handler(method, args);
    }
}
