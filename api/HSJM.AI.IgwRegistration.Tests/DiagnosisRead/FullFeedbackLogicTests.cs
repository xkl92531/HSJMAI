using System.Text.Json;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

public class FullFeedbackLogicTests
{
    const long Project = 301;
    internal const string Xml = """
        <FlgNet><Parts>
        <Access Scope="GlobalVariable" UId="1"><Symbol><Component Name="I"/></Symbol></Access>
        <Access Scope="GlobalVariable" UId="2"><Symbol><Component Name="DB"/><Component Name="Bypass"/></Symbol></Access>
        <Access Scope="GlobalVariable" UId="3"><Symbol><Component Name="Debug_Test"/><Component Name="Debug[19]"/></Symbol></Access>
        <Access Scope="GlobalVariable" UId="4"><Symbol><Component Name="Q"/></Symbol></Access>
        <Access Scope="GlobalVariable" UId="5"><Symbol><Component Name="DB_Alarm"/><Component Name="FwdError"/></Symbol></Access>
        <Part Name="O" UId="10"><TemplateValue Name="Card" Type="Cardinality">2</TemplateValue></Part>
        <Part Name="Contact" UId="11"><Negated Name="operand"/></Part>
        <Call UId="20"><CallInfo Name="FB3002_Valve" BlockType="FB">
        <Parameter Name="IWP" Section="Input" Type="Bool"/>
        <Parameter Name="QFwd" Section="Output" Type="Bool"/>
        <Parameter Name="FwdErr" Section="Output" Type="Bool"/>
        </CallInfo></Call>
        </Parts><Wires>
        <Wire UId="101"><IdentCon UId="1"/><NameCon UId="10" Name="in1"/></Wire>
        <Wire UId="102"><IdentCon UId="2"/><NameCon UId="10" Name="in2"/></Wire>
        <Wire UId="103"><NameCon UId="10" Name="out"/><NameCon UId="11" Name="in"/></Wire>
        <Wire UId="104"><IdentCon UId="3"/><NameCon UId="11" Name="operand"/></Wire>
        <Wire UId="105"><NameCon UId="11" Name="out"/><NameCon UId="20" Name="IWP"/></Wire>
        <Wire UId="106"><NameCon UId="20" Name="QFwd"/><IdentCon UId="4"/></Wire>
        <Wire UId="107"><NameCon UId="20" Name="FwdErr"/><IdentCon UId="5"/></Wire>
        </Wires></FlgNet>
        """;

    static DiagnosisAppHarness Harness(bool raw, bool bypass, bool debug, bool alarm, bool command = true)
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Project, "PLC");
        h.AddIoTag(Project, "IO", "I", "%I1.0");
        h.AddIoTag(Project, "IO", "Q", "%Q1.0");
        h.SetRead(Project, DiagnosisAppHarness.Good("IO.I", raw), DiagnosisAppHarness.Good("IO.Q", command));
        var rule = h.AddRule(Project, "IMPLIES", "OP030_CYL01_到位反馈", ("Q", "Source"), ("I", "Target"));
        rule.Source = "XMLV2_TIMED";
        rule.ValidStates = JsonSerializer.Serialize(new { BlockName = "FB320_OP30Manual", LogicUnitId = "999999" });
        h.Blocks.Add(new PLC_BlockEntity { Id = 80, ProjectId = Project, BlockName = "FB320_OP30Manual" });
        h.Units.Add(new PLC_LogicUnitEntity { Id = 501, BlockId = 80, RawContent = Xml });
        foreach (var (id, name, path) in new[] { (81L, "DB", "Bypass"),
            (82L, "Debug_Test", "Debug.Debug[19]"), (83L, "DB_Alarm", "FwdError") })
        {
            h.Blocks.Add(new PLC_BlockEntity { Id = id, ProjectId = Project, BlockName = name, BlockType = "DB" });
            h.BlockInterfaces.Add(new PLC_BlockInterfaceEntity { BlockId = id, VariablePath = path, DataType = "Bool" });
        }
        var values = new Dictionary<(string, string), bool> { [("IO", "I")] = raw, [("IO", "Q")] = command,
            [("DB", "Bypass")] = bypass, [("Debug_Test", "Debug.Debug[19]")] = debug, [("DB_Alarm", "FwdError")] = alarm };
        h.ExactReads.Add(requests => requests.Where(r => values.ContainsKey((r.BlockName, r.VariablePath)))
            .Select(r => new PLC_ExactReadResult(r.RequestKey, r.BlockName,
            r.VariablePath, r.BlockName + "." + r.VariablePath, values[(r.BlockName, r.VariablePath)],
            PLC_ExactReadQuality.Good, DateTimeOffset.UtcNow, null)).ToArray());
        return h;
    }

    static async Task<ConstraintCheckOutcome> Check(DiagnosisAppHarness h)
    {
        var app = h.Build();
        return await app.CheckConstraintsAsync([Project], await app.GetLiveSignalsByPlcAsync(h.Projects, default), default);
    }

    [Theory]
    [InlineData("DB")]
    [InlineData("GlobalDB")]
    [InlineData("InstanceDB")]
    public async Task DbArrayLeavesUseExistingParserPathsAndKeepXmlRequestKeys(string blockKind)
    {
        var h = Harness(true, false, true, true);
        h.Units[0].RawContent = Xml.Replace("Name=\"Bypass\"", "Name=\"ByPass_CYL[4]\"")
            .Replace("Name=\"FwdError\"", "Name=\"Type\"/><Component Name=\"1.Cylinder[1]\"/><Component Name=\"FwdError\"");
        h.BlockInterfaces.Single(m => m.BlockId == 81).VariablePath = "ByPass_CYL.ByPass_CYL[4]";
        h.BlockInterfaces.Single(m => m.BlockId == 83).VariablePath = "Type.1.Cylinder.1.Cylinder[1].FwdError";
        foreach (var block in h.Blocks.Where(b => b.Id != 80)) block.BlockType = blockKind;
        h.Blocks.Add(new() { Id = 84, ProjectId = Project + 1, BlockName = "Debug_Test", BlockType = blockKind });
        h.BlockInterfaces.Add(new() { BlockId = 84, VariablePath = "Debug[19]", DataType = "Bool" });
        h.AddIoTag(Project, "LegacyTable", "Debug_Test.Debug[19]", "DB82.DBX2.3");
        var registered = new Dictionary<(string, string), bool> {
            [("IO", "I")] = true, [("IO", "Q")] = true,
            [("DB", "ByPass_CYL.ByPass_CYL[4]")] = false,
            [("Debug_Test", "Debug.Debug[19]")] = true,
            [("DB_Alarm", "Type.1.Cylinder.1.Cylinder[1].FwdError")] = true
        };
        var sent = new List<PLC_ExactReadRequest>();
        h.ExactReads.Clear();
        h.ExactReads.Add(requests => {
            sent.AddRange(requests);
            // Return data only for the actual registered parser path; RequestKey never determines the value.
            return requests.Where(r => registered.ContainsKey((r.BlockName, r.VariablePath)))
                .Select(r => new PLC_ExactReadResult(r.RequestKey, r.BlockName, r.VariablePath,
                    r.BlockName + "." + r.VariablePath, registered[(r.BlockName, r.VariablePath)],
                    PLC_ExactReadQuality.Good, DateTimeOffset.UtcNow, null)).ToArray();
        });
        var result = await Check(h);
        Assert.Contains(sent, r => r.RequestKey == "Debug_Test.Debug[19]" && r.VariablePath == "Debug.Debug[19]");
        Assert.Contains(sent, r => r.RequestKey == "DB.ByPass_CYL[4]" && r.VariablePath == "ByPass_CYL.ByPass_CYL[4]");
        Assert.Contains(sent, r => r.RequestKey == "DB_Alarm.Type.1.Cylinder[1].FwdError"
            && r.VariablePath == "Type.1.Cylinder.1.Cylinder[1].FwdError");
        var issue = Assert.Single(result.Violations);
        Assert.Contains("有效反馈=False", issue.Reason);
        Assert.Contains("Debug_Test.Debug[19]=True", issue.Reason);
    }

    [Fact]
    public void ConfirmedFeedbackReasonKeepsEverySignalAfterTheSixth()
    {
        var leaves = Enumerable.Range(1, 9).Select(i => LogicTerm.Signal("DB.Sensor[" + i + "]")).ToArray();
        var plan = new FeedbackLogicPlan("Q", new LogicTerm("And", Children: leaves), "DB.Alarm");
        var reads = plan.Symbols.Select(name => new PLC_ExactReadResult(name, "DB", name, name, true,
            PLC_ExactReadQuality.Good, DateTimeOffset.UtcNow, null)).ToArray();
        var rule = ConstraintTestData.Rule("IMPLIES", "all feedback", Project);
        var result = FullFeedbackLogic.Judge(rule, plan, reads, new Dictionary<string, PLC_IOTagEntity>(), DateTimeOffset.UtcNow);
        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        Assert.Equal(11, result.Issue.SignalDetails.Count);
        foreach (var symbol in plan.Symbols) Assert.Contains(symbol + "=True", result.Issue.Reason);
    }

    [Theory]
    [InlineData("missing_member")]
    [InlineData("foreign_project")]
    [InlineData("duplicate_block")]
    [InlineData("ambiguous_member")]
    [InlineData("deleted_member")]
    [InlineData("wrong_block_kind")]
    public async Task UnprovenDbMemberMappingIsNeverGuessed(string kind)
    {
        var h = Harness(true, false, false, false);
        var member = h.BlockInterfaces.Single(m => m.BlockId == 82);
        if (kind == "missing_member") h.BlockInterfaces.Remove(member);
        if (kind == "foreign_project") h.Blocks.Single(b => b.Id == 82).ProjectId = Project + 1;
        if (kind == "duplicate_block") h.Blocks.Add(new() { Id = 84, ProjectId = Project, BlockName = "Debug_Test", BlockType = "DB" });
        if (kind == "ambiguous_member") h.BlockInterfaces.Add(new() { BlockId = 82, VariablePath = "Debug[19]", DataType = "Bool" });
        if (kind == "deleted_member") member.IsDeleted = true;
        if (kind == "wrong_block_kind") h.Blocks.Single(b => b.Id == 82).BlockType = "FB";
        var sent = new List<PLC_ExactReadRequest>();
        var original = h.ExactReads[0];
        h.ExactReads[0] = requests => { sent.AddRange(requests); return original(requests); };
        var result = await Check(h);
        Assert.DoesNotContain(sent, r => r.RequestKey == "Debug_Test.Debug[19]");
        Assert.Equal(0, result.Passed);
        Assert.Contains("Debug_Test.Debug[19]", Assert.Single(result.Undetermined).Reason);
    }

    [Fact]
    public async Task RawSensorTrueButNegatedSimulationAndPlcTimeoutReportsOp030Fault()
    {
        var h = Harness(true, false, true, true);
        var outcome = await Check(h);
        var issue = Assert.Single(outcome.Violations);
        Assert.Contains("OP030", issue.RelatedRule);
        Assert.Contains("Debug_Test.Debug[19]", issue.Reason);
        Assert.Contains("PLC 超时报警已触发", issue.Reason);
        Assert.Equal(1, outcome.Violated);
        Assert.Equal(1, h.ExactReadCalls);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task CompleteAndOrNotLogicAcceptsSensorOrBypass(bool raw, bool bypass, bool debug)
    {
        var outcome = await Check(Harness(raw, bypass, debug, false));
        Assert.Equal(1, outcome.Passed);
        Assert.Empty(outcome.Undetermined);
    }

    [Fact]
    public async Task NegatedSimulationAlsoOverridesBypassButNeedsTimeoutEvidence()
    {
        var outcome = await Check(Harness(false, true, true, false));
        Assert.Empty(outcome.Violations);
        Assert.Contains("有效反馈=False", Assert.Single(outcome.Undetermined).Reason);
    }

    [Fact]
    public async Task NoCommandAndNoPlcAlarmIsNotMissingArrivalFault()
    {
        var outcome = await Check(Harness(false, false, true, false, command: false));
        Assert.Equal(1, outcome.Passed);
        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public async Task LatchedPlcAlarmSurvivesCommandTurningOff()
    {
        var outcome = await Check(Harness(false, false, true, true, command: false));
        Assert.Equal(1, outcome.Violated);
        Assert.Contains("锁存", Assert.Single(outcome.Violations).Reason);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("bad")]
    [InlineData("stale")]
    [InlineData("no_timestamp")]
    [InlineData("duplicate")]
    public async Task UntrustedAlarmValueCannotBecomeConfirmedFault(string kind)
    {
        var h = Harness(true, false, true, true);
        var original = h.ExactReads[0];
        h.ExactReads[0] = requests => original(requests).SelectMany(r => r.RequestKey != "DB_Alarm.FwdError" ? [r]
            : kind switch {
                "missing" => Array.Empty<PLC_ExactReadResult>(),
                "bad" => [r with { Quality = PLC_ExactReadQuality.Bad, ErrorCode = "BadCommFailure" }],
                "stale" => [r with { SourceTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5) }],
                "no_timestamp" => [r with { SourceTimestamp = null }],
                _ => new[] { r, r }
            }).ToArray();
        var outcome = await Check(h);
        Assert.Empty(outcome.Violations);
        Assert.Equal(0, outcome.Passed);
        Assert.Contains("证据不全", Assert.Single(outcome.Undetermined).Reason);
    }

    [Fact]
    public async Task UnreadSimulationSignalNeverInheritsRawInputTrue()
    {
        var h = Harness(true, false, true, false);
        var original = h.ExactReads[0];
        h.ExactReads[0] = requests => original(requests).Where(r => r.RequestKey != "Debug_Test.Debug[19]").ToArray();
        var outcome = await Check(h);
        Assert.Empty(outcome.Violations);
        Assert.Contains("有效反馈=Unknown", Assert.Single(outcome.Undetermined).Reason);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("ambiguous")]
    [InlineData("foreign_project")]
    [InlineData("unsupported")]
    public async Task UnprovenOrCrossProjectWiringIsNeverEvaluatedAsPhysicalFault(string kind)
    {
        var h = Harness(true, false, true, true);
        if (kind == "local") h.Units[0].RawContent = Xml.Replace("Scope=\"GlobalVariable\" UId=\"3\"", "Scope=\"LocalVariable\" UId=\"3\"");
        if (kind == "ambiguous") h.Units.Add(new PLC_LogicUnitEntity { Id = 502, BlockId = 80, RawContent = Xml });
        if (kind == "foreign_project") h.Blocks[0].ProjectId = Project + 1;
        if (kind == "unsupported") h.Units[0].RawContent = Xml.Replace("Name=\"O\" UId=\"10\"", "Name=\"UnknownOp\" UId=\"10\"");
        var outcome = await Check(h);
        Assert.Empty(outcome.Violations);
        Assert.Single(outcome.Undetermined);
        Assert.Equal(0, h.ExactReadCalls);
    }

    [Fact]
    public async Task ConfirmedMarkerIsAbsentFromUnknownEvidence()
    {
        var outcome = await Check(Harness(true, false, true, false));
        var report = DiagnosticStrategyFactoryApp.BuildDirectDiagnosticReport("constraint", [], [], outcome);
        Assert.DoesNotContain("判定: 确认违规", report);
        var confirmed = await Check(Harness(true, false, true, true));
        var confirmedReport = DiagnosticStrategyFactoryApp.BuildDirectDiagnosticReport("constraint", confirmed.Violations, [], confirmed);
        Assert.Contains("判定: 确认违规", confirmedReport);
        var stations = new List<HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS.StationDiagnosisSummaryDto> {
            new() { ProjectId = Project, StationCode = "OP030", OverallStatus = "SomeFalse" },
            new() { ProjectId = Project, StationCode = "OP020", OverallStatus = "SomeFalse" }
        };
        var model = new HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS.LineDiagnosisLlmReport {
            Success = true, RootCauseStation = "OP020", RootCauseDescription = "机器人通信故障已确认", Confidence = .95
        };
        HSJM.AI.AppLayer.StopCauseDiagnosis.Services.LineDiagnosisLlmService.ApplyEvidenceArbitration(model,
            HSJM.AI.AppLayer.StopCauseDiagnosis.Services.LineDiagnosisLlmService.BuildEvidenceProfiles(stations, confirmedReport));
        Assert.Equal("OP030", model.RootCauseStation);
        Assert.DoesNotContain("机器人通信故障已确认", model.RootCauseDescription);
        Assert.Contains("Debug_Test.Debug[19]", model.RootCauseDescription);
    }

    [Fact]
    public async Task ManualModeDoesNotHideAnActiveNativeTimeoutAlarm()
    {
        var h = Harness(true, false, true, true);
        h.Rules[0].ValidStates = JsonSerializer.Serialize(new { BlockName = "FB320_OP30Manual",
            ManualModeBindings = new[] { new HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353ManualModeBinding("DB_Mode", "General.ManualMod") } });
        var app = h.Build();
        app.ManualModeReadOverride = (p, b, ct) => Task.FromResult(
            new HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353ManualModeRead(p, "Manual", "test"));
        var outcome = await app.CheckConstraintsAsync([Project], await app.GetLiveSignalsByPlcAsync(h.Projects, default), default);
        Assert.Single(outcome.Violations);
        Assert.Single(outcome.IoIssues);
    }

    [Fact]
    public async Task UnsupportedSecondWriterCannotBeIgnoredWhenCountingCandidates()
    {
        var h = Harness(true, false, true, true);
        h.Units.Add(new PLC_LogicUnitEntity { Id = 502, BlockId = 80,
            RawContent = Xml.Replace("Name=\"O\" UId=\"10\"", "Name=\"UnknownOp\" UId=\"10\"") });
        var outcome = await Check(h);
        Assert.Empty(outcome.Violations);
        Assert.Single(outcome.Undetermined);
    }

    [Fact]
    public async Task AlarmWrittenByAnotherNetworkIsNotProofOfThisValvesTimeout()
    {
        var h = Harness(true, false, true, true);
        h.Units.Add(new PLC_LogicUnitEntity { Id = 502, BlockId = 80, RawContent = """
            <FlgNet><Parts><Access Scope="GlobalVariable" UId="1"><Symbol><Component Name="DB_Alarm"/><Component Name="FwdError"/></Symbol></Access>
            <Part Name="SCoil" UId="2"/></Parts><Wires><Wire UId="3"><IdentCon UId="1"/><NameCon UId="2" Name="operand"/></Wire></Wires></FlgNet>
            """ });
        var outcome = await Check(h);
        Assert.Empty(outcome.Violations);
        Assert.Single(outcome.Undetermined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossBlockAlarmWriterFoundByProjectWriteIndexVetoesAttribution(bool missingSource)
    {
        var h = Harness(true, false, true, true);
        h.ExtraAccesses.Add(new PLC_VariableAccessEntity { ProjectId = Project, LogicUnitId = 777,
            BlockId = 90, VariableName = "DB_Alarm.FwdError", AccessType = "WRITE" });
        h.Units.Add(new PLC_LogicUnitEntity { Id = 777, BlockId = 90, RawContent = """
            <FlgNet><Parts><Access Scope="GlobalVariable" UId="1"><Symbol><Component Name="DB_Alarm"/><Component Name="FwdError"/></Symbol></Access>
            <Part Name="SCoil" UId="2"/></Parts><Wires><Wire UId="3"><IdentCon UId="1"/><NameCon UId="2" Name="operand"/></Wire></Wires></FlgNet>
            """ });
        if (missingSource) h.Units.Last().RawContent = null;
        var result = await Check(h);
        Assert.Empty(result.Violations);
        Assert.Single(result.Undetermined);
        Assert.Equal(0, h.ExactReadCalls);
    }

    [Fact]
    public async Task TransportFailureDoesNotBecomeNormalOrAFault()
    {
        var h = Harness(true, false, true, true);
        h.ExactReads[0] = _ => throw new InvalidOperationException("test connection failure");
        var result = await Check(h);
        Assert.Equal(0, result.Passed);
        Assert.Empty(result.Violations);
        Assert.Contains("证据不全", Assert.Single(result.Undetermined).Reason);
    }
}
