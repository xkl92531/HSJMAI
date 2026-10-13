#nullable enable
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class ValveAlarmDiagnosisServiceTests
{
    private static ValveAlarmDefinition Def(string variable, string station) =>
        new(1, variable, "FwdError", "Fwd", "Valve[1]", "FB220", 100, 200, 3, null, []) { StationCode = station };

    private static PLC_ExactReadResult Result(PLC_ExactReadRequest r, object? value, PLC_ExactReadQuality q = PLC_ExactReadQuality.Good) =>
        new(r.RequestKey, r.BlockName, r.VariablePath, r.RequestKey, value, q, null, null);

    private static List<StationDiagnosisSummaryDto> Summaries() =>
    [
        new() { ProjectId = 1, StationCode = "OP020", OverallStatus = "Running" },
        new() { ProjectId = 1, StationCode = "OP030", OverallStatus = "SomeFalse" },
    ];

    [Fact]
    public async Task ActiveAlarmsAreAttachedPerStationWithoutChangingStatus()
    {
        var summaries = Summaries();
        var defs = new List<ValveAlarmDefinition> { Def("DB1.A.X", "OP020"), Def("DB1.A.Y", "OP030"), Def("DB1.A.Z", "OP099") };
        IReadOnlyCollection<PLC_ExactReadRequest>? seen = null;

        var error = await ValveAlarmDiagnosisService.AttachCoreAsync(summaries,
            _ => Task.FromResult<IReadOnlyList<ValveAlarmDefinition>>(defs),
            (_, reqs) =>
            {
                seen = reqs;
                return Task.FromResult<IReadOnlyList<PLC_ExactReadResult>>(reqs
                    .Select(r => r.RequestKey == "DB1.A.Y" ? Result(r, null, PLC_ExactReadQuality.Bad) : Result(r, true))
                    .ToList());
            });

        Assert.Null(error);
        Assert.Equal("DB1.A.X", Assert.Single(summaries[0].ActiveAlarms).AlarmVariable);
        Assert.Equal("Running", summaries[0].OverallStatus);
        Assert.Empty(summaries[1].ActiveAlarms);
        Assert.Equal(1, summaries[1].AlarmReadFailedCount);
        Assert.DoesNotContain(seen!, r => r.RequestKey == "DB1.A.Z"); // 不在本次诊断工位的报警不读
    }

    [Fact]
    public async Task CatalogOrReadFailureIsReportedAndDoesNotThrow()
    {
        var summaries = Summaries();

        var error = await ValveAlarmDiagnosisService.AttachCoreAsync(summaries,
            _ => throw new InvalidOperationException("IGW 不可达"),
            (_, _) => Task.FromResult<IReadOnlyList<PLC_ExactReadResult>>([]));

        Assert.NotNull(error);
        Assert.Contains("IGW 不可达", error);
        Assert.All(summaries, s => Assert.Empty(s.ActiveAlarms));
    }

    [Fact]
    public async Task NoDefinitionsMeansNoReadAndNoError()
    {
        var reads = 0;
        var error = await ValveAlarmDiagnosisService.AttachCoreAsync(Summaries(),
            _ => Task.FromResult<IReadOnlyList<ValveAlarmDefinition>>([]),
            (_, _) => { reads++; return Task.FromResult<IReadOnlyList<PLC_ExactReadResult>>([]); });

        Assert.Null(error);
        Assert.Equal(0, reads);
    }

    // ---- 终审 I-1：先读报警位、分批、单批失败只算那批、阶段总时限 ----

    private static ValveAlarmDefinition DefWithSignals(string variable, string station) =>
        new(1, variable, "Timeout", "", "Valve[1]", "FB220", 100, 200, 3, new("YV_" + variable, "IO表", "%Q1.0"), [new("CYL_" + variable, "IO表", "%I1.0")])
            { StationCode = station };

    private static List<StationDiagnosisSummaryDto> OneStation() => [new() { ProjectId = 1, StationCode = "OP020", OverallStatus = "Running" }];

    [Fact]
    public async Task AlarmBitsAreReadFirstAndOnlyActiveAlarmsGetTheirSignalsRead()
    {
        var defs = new List<ValveAlarmDefinition> { DefWithSignals("DB1.A.X", "OP020"), DefWithSignals("DB1.A.Y", "OP020") };
        var calls = new List<string[]>();
        var summaries = OneStation();
        await ValveAlarmDiagnosisService.AttachCoreAsync(summaries,
            (p, _) => Task.FromResult<IReadOnlyList<ValveAlarmDefinition>>(defs),
            (_, reqs, _) =>
            {
                lock (calls) calls.Add(reqs.Select(r => r.RequestKey).Order(StringComparer.Ordinal).ToArray());
                return Task.FromResult<IReadOnlyList<PLC_ExactReadResult>>(reqs.Select(r => Result(r, r.RequestKey == "DB1.A.X" || r.RequestKey.StartsWith("io::"))).ToList());
            }, new ValveAlarmReadOptions(200, 4, TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "DB1.A.X", "DB1.A.Y" }, calls[0]);                                   // 第一轮只读报警位
        Assert.Equal(new[] { "io::IO表CYL_DB1.A.X", "io::IO表YV_DB1.A.X" }, calls[1]); // 只补读激活报警的输出与反馈
        var alarm = Assert.Single(summaries[0].ActiveAlarms);
        Assert.True(alarm.OutputSignal!.Value);
        Assert.Equal(0, summaries[0].AlarmReadFailedCount);
    }

    [Fact]
    public async Task AlarmBitsAreReadInBatchesAndAFailedBatchOnlyCountsItsOwnAlarms()
    {
        var defs = Enumerable.Range(0, 450).Select(i => Def($"DB1.A.X{i:000}", "OP020")).ToList();
        var batches = new List<int>();
        var summaries = OneStation();
        var error = await ValveAlarmDiagnosisService.AttachCoreAsync(summaries,
            (p, _) => Task.FromResult<IReadOnlyList<ValveAlarmDefinition>>(defs),
            (_, reqs, _) =>
            {
                lock (batches) batches.Add(reqs.Count);
                if (reqs.Any(r => r.RequestKey == "DB1.A.X200")) throw new TimeoutException("这一批超时");
                return Task.FromResult<IReadOnlyList<PLC_ExactReadResult>>(reqs.Select(r => Result(r, true)).ToList());
            }, new ValveAlarmReadOptions(200, 2, TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(new[] { 50, 200, 200 }, batches.Order().ToArray());
        Assert.Equal(200, summaries[0].AlarmReadFailedCount);   // 只有出错那一批
        Assert.Equal(250, summaries[0].ActiveAlarms.Count);
    }

    [Fact]
    public async Task ParallelBatchesNeverExceedTheLimit()
    {
        var defs = Enumerable.Range(0, 1000).Select(i => Def($"DB1.A.X{i:0000}", "OP020")).ToList();
        var running = 0; var peak = 0;
        await ValveAlarmDiagnosisService.AttachCoreAsync(OneStation(),
            (p, _) => Task.FromResult<IReadOnlyList<ValveAlarmDefinition>>(defs),
            async (_, reqs, _) =>
            {
                var now = Interlocked.Increment(ref running);
                lock (defs) peak = Math.Max(peak, now);
                await Task.Delay(30);
                Interlocked.Decrement(ref running);
                return reqs.Select(r => Result(r, false)).ToList();
            }, new ValveAlarmReadOptions(100, 3, TimeSpan.FromSeconds(10)), CancellationToken.None);
        Assert.InRange(peak, 1, 3);
    }

    [Fact]
    public async Task StageDeadlineKeepsWhatWasReadAndCountsTheRestAsFailed()
    {
        var defs = Enumerable.Range(0, 400).Select(i => Def($"DB1.A.X{i:000}", "OP020")).ToList();
        var summaries = OneStation();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await ValveAlarmDiagnosisService.AttachCoreAsync(summaries,
            (p, _) => Task.FromResult<IReadOnlyList<ValveAlarmDefinition>>(defs),
            async (_, reqs, _) =>
            {
                if (reqs.Any(r => r.RequestKey == "DB1.A.X200")) await Task.Delay(Timeout.Infinite); // 这一批卡住且不响应取消
                return reqs.Select(r => Result(r, true)).ToList();
            }, new ValveAlarmReadOptions(200, 2, TimeSpan.FromMilliseconds(300)), CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Null(error);
        Assert.Equal(200, summaries[0].ActiveAlarms.Count);
        Assert.Equal(200, summaries[0].AlarmReadFailedCount);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ValveAlarmDiagnosisService.AttachCoreAsync(OneStation(),
            (p, _) => Task.FromResult<IReadOnlyList<ValveAlarmDefinition>>([Def("DB1.A.X", "OP020")]),
            (_, reqs, ct) => Task.FromResult<IReadOnlyList<PLC_ExactReadResult>>(reqs.Select(r => Result(r, true)).ToList()),
            new ValveAlarmReadOptions(200, 2, TimeSpan.FromSeconds(5)), cts.Token));
    }
}
