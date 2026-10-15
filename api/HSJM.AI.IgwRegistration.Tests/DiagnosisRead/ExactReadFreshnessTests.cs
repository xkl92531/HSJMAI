using System.Net;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Fake = HSJM.AI.IgwRegistration.Tests.IgwReadChainRoutingTests.Fake;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>Real exact-read routing and conversion, with only metadata/network boundaries substituted.</summary>
public class ExactReadFreshnessTests
{
    private static int nextProject = 893000;
    private static int nextPort = 49300;
    private const string Alarm = "DB_Alarm.FwdError";
    private static readonly PLC_ExactReadRequest[] Requests =
    [new("Q", "IO", "Q"), new("I", "IO", "I"), new(Alarm, "DB_Alarm", "FwdError")];

    private sealed class Harness
    {
        public readonly long Project = Interlocked.Increment(ref nextProject);
        public readonly List<string> Aliases = new();
        public int QualityCalls;
        public int LegacyCalls;
        public bool QualityUnsupported;
        public DateTime? Timestamp = DateTime.UtcNow.AddSeconds(-1);
        public bool BadAlarm;
        public bool DuplicateAlarm;
        public readonly PLC_VariableAccessService Service;

        public Harness()
        {
            CommonServiceProvider.ServiceProvider ??= new ServiceCollection().BuildServiceProvider();
            var project = new PLC_ProjectEntity { Id = Project, StationId = "PLC1", PlcIp = "10.0.0.1",
                IotUrl = "http://127.0.0.1:" + Interlocked.Increment(ref nextPort) };
            var projects = Fake.Create<IPLC_ProjectService>((m, _) => m.Name == nameof(IPLC_ProjectService.GetById) ? project : null);
            var iot = Fake.Create<IIotService>((m, args) =>
            {
                var aliases = ((List<string>)args[1]).ToList();
                Aliases.AddRange(aliases);
                if (m.Name == nameof(IIotService.ReadTagsWithQualityAsync))
                {
                    QualityCalls++;
                    if (QualityUnsupported) return Task.FromResult(new IgwTransportResult<List<DcsTagQualityItem>>
                        { HttpStatusCode = HttpStatusCode.NotFound });
                    var rows = aliases.Select(a => new DcsTagQualityItem { Key = a,
                        Value = a.EndsWith(".FwdError", StringComparison.Ordinal),
                        IsGood = !(BadAlarm && a.EndsWith(".FwdError", StringComparison.Ordinal)),
                        Status = BadAlarm ? "Bad" : "Good", Reason = BadAlarm ? "CommFailure" : null,
                        TimestampUtc = Timestamp }).ToList();
                    if (DuplicateAlarm) rows.AddRange(rows.Where(r => r.Key.EndsWith(".FwdError", StringComparison.Ordinal)).ToArray());
                    return Envelope(rows);
                }
                if (m.Name == nameof(IIotService.ReadTagsAsListRawAsync))
                {
                    LegacyCalls++;
                    return Envelope(aliases.Select(a => new DcsTagReadItem { Key = a, Value = true }).ToList());
                }
                // Existing exact transport before the fix: values are Good but source timestamps are lost.
                if (m.Name == nameof(IIotService.ReadTagsAsList))
                    return Task.FromResult(new HttpResponseResultModel<List<PLC_ReadTagResult>> { IsSuccess = true,
                        BackResult = aliases.Select(a => new PLC_ReadTagResult { TagName = a, IsGood = true,
                            Value = a.EndsWith(".FwdError", StringComparison.Ordinal) }).ToList() });
                throw new NotSupportedException(m.Name);
            });
            var ledger = Fake.Create<IPLC_IgwRegistrationService>((m, _) =>
                m.Name == nameof(IPLC_IgwRegistrationService.GetLedgerForReadAsync)
                    ? Task.FromResult(new IgwLedgerSnapshot(
                        new Dictionary<string, IgwDeviceKind> { ["DB_Alarm"] = IgwDeviceKind.S7 },
                        new Dictionary<string, string>(), new Dictionary<(string, string), string>(),
                        new Dictionary<(string, string), string> { [("IO", "Q")] = "Renamed_Q" }, new Dictionary<string, string>()))
                    : throw new NotSupportedException(m.Name));
            var tags = new PLC_IOTagEntity[] {
                new() { ProjectId = Project, TableName = "IO", TagName = "Q", DataType = "Bool" },
                new() { ProjectId = Project, TableName = "IO", TagName = "I", DataType = "Bool" },
                PLC_VariableAccessService.SynthesizeCandidate(Project, "DB_Alarm", "FwdError", "Bool") };
            Service = new(projects, iot, (_, _, _) => Task.FromResult<IReadOnlyList<PLC_IOTagEntity>>(tags), ledger);
        }

        public Task<IReadOnlyList<PLC_ExactReadResult>> Read() => Service.ReadExactDataAsync(Project, Requests, default);
        public async Task<StationDiagnosisSummaryDto> Native()
        {
            var station = new StationDiagnosisSummaryDto { ProjectId = Project, StationCode = "OP030", OverallStatus = "Skipped" };
            var definition = new ValveAlarmDefinition(Project, Alarm, "FwdError", "Fwd", "Valve[1]", "FB320", 100, 200, 3, null, [])
                { StationCode = "OP030" };
            await ValveAlarmDiagnosisService.AttachCoreAsync([station], (_, _) => Task.FromResult<IReadOnlyList<ValveAlarmDefinition>>([definition]),
                (pid, reqs, ct) => Service.ReadExactDataAsync(pid, reqs, ct), ValveAlarmReadOptions.Default, default);
            return station;
        }
    }

    private static Task<IgwTransportResult<List<T>>> Envelope<T>(List<T> rows) => Task.FromResult(new IgwTransportResult<List<T>> {
        HttpStatusCode = HttpStatusCode.OK, IsHttpSuccess = true,
        Envelope = new HttpResponseResultModel<List<T>> { IsSuccess = true, BackResult = rows } });

    private static RuleEvaluation Judge(IReadOnlyList<PLC_ExactReadResult> reads) => FullFeedbackLogic.Judge(
        new PLC_IOConstraintRuleEntity { RuleName = "OP030_CYL01", Operator = "IMPLIES" },
        new FeedbackLogicPlan("Q", LogicTerm.Signal("I"), Alarm), reads, new Dictionary<string, PLC_IOTagEntity>(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Stable_latched_alarm_keeps_real_source_timestamp_through_exact_read_and_feedback_judgment()
    {
        var h = new Harness();
        var reads = await h.Read();
        Assert.All(reads, r => Assert.Equal(new DateTimeOffset(h.Timestamp!.Value), r.SourceTimestamp));
        Assert.Equal(RuleVerdict.Violated, Judge(reads).Verdict);
        Assert.Equal(false, reads.Single(r => r.RequestKey == "Q").Value);
        Assert.Contains("通用.S7_10.0.0.1.IO.IO.Renamed_Q", h.Aliases);
        Assert.Contains("通用.S7_10.0.0.1.DB_Alarm.FwdError", h.Aliases);
        // The value stays true while a new PLC sample arrives: source time, not value-change time, controls freshness.
        h.Timestamp = DateTime.UtcNow;
        var next = await h.Read();
        Assert.Equal(RuleVerdict.Violated, Judge(next).Verdict);
        Assert.All(next, r => Assert.Equal(new DateTimeOffset(h.Timestamp.Value), r.SourceTimestamp));
    }

    [Fact]
    public async Task Good_fresh_native_alarm_survives_no_active_graph()
    {
        var h = new Harness();
        var station = await h.Native();
        Assert.Single(station.ActiveAlarms);
        Assert.Equal(0, station.AlarmReadFailedCount);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("missing_timestamp")]
    [InlineData("future")]
    [InlineData("bad")]
    [InlineData("duplicate")]
    public async Task Untrusted_transport_alarm_is_unknown_in_both_feedback_and_native_paths(string kind)
    {
        var h = new Harness();
        if (kind == "stale") h.Timestamp = DateTime.UtcNow.AddMinutes(-5);
        if (kind == "missing_timestamp") h.Timestamp = null;
        if (kind == "future") h.Timestamp = DateTime.UtcNow.AddMinutes(5);
        h.BadAlarm = kind == "bad";
        h.DuplicateAlarm = kind == "duplicate";
        Assert.Equal(RuleVerdict.Undetermined, Judge(await h.Read()).Verdict);
        var station = await h.Native();
        Assert.Empty(station.ActiveAlarms);
        Assert.Equal(1, station.AlarmReadFailedCount);
    }

    [Fact]
    public async Task Unsupported_quality_endpoint_falls_back_without_inventing_source_timestamp()
    {
        var h = new Harness { QualityUnsupported = true };
        var reads = await h.Read();
        Assert.All(reads, r => Assert.Null(r.SourceTimestamp));
        Assert.Equal(RuleVerdict.Undetermined, Judge(reads).Verdict);
        var station = await h.Native();
        Assert.Empty(station.ActiveAlarms);
        Assert.Equal(1, station.AlarmReadFailedCount);
        Assert.Equal(1, h.QualityCalls);
        Assert.Equal(2, h.LegacyCalls);
    }
}
