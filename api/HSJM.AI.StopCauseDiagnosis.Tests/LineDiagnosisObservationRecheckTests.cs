using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class LineDiagnosisObservationRecheckTests
{
    static StationDiagnosisSummaryDto Station(int n = 1) => new() { ProjectId = 7,
        StationCode = "Station-" + n, StationOrder = n, OverallStatus = "SomeFalse" };

    [Fact]
    public async Task ObservationRecheckReadsNewValuesRatherThanFirstPassCache()
    {
        using var firstPass = StopCauseSignalReadScope.Begin();
        var value = true;
        var calls = 0;
        Task<List<PLC_ReadTagResult>> Read(IReadOnlyList<string> names)
        {
            calls++;
            return Task.FromResult(names.Select(n => new PLC_ReadTagResult { TagName = n, IsGood = true, Value = value }).ToList());
        }
        await firstPass.ReadAsync(7, ["DB.Condition"], Read);
        value = false; // The observed physical condition changed during the shared observation window.
        var rows = await LineDiagnosisObservationRecheck.RefreshAsync([Station()], 3, async (s, ct) => {
            var read = await StopCauseSignalReadScope.Current.ReadAsync(7, ["DB.Condition"], Read);
            return new() { ProjectId = s.ProjectId, StationCode = s.StationCode, StationOrder = s.StationOrder,
                OverallStatus = (bool)read[0].Value ? "Running" : "SomeFalse" };
        }, default);
        Assert.Equal("SomeFalse", rows[0].OverallStatus);
        Assert.Equal(2, calls);
        Assert.Same(firstPass, StopCauseSignalReadScope.Current);
    }

    [Fact]
    public async Task IndependentStationsStartTogetherWithBoundedConcurrency()
    {
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = LineDiagnosisObservationRecheck.RefreshAsync([Station(1), Station(2), Station(3)], 2, async (s, ct) => {
            if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult();
            await release.Task;
            return s;
        }, default);
        try { await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(2, started); }
        finally { release.TrySetResult(); }
        Assert.Equal(3, (await work).Count);
    }

    [Fact]
    public async Task FailedFreshReadCannotKeepOldWaitingEvidence()
    {
        var station = Station();
        station.KeyFindings = [new() { EvaluatedValue = false, FaultOwnership = "WaitingFor:Other" }];
        var result = await LineDiagnosisObservationRecheck.RefreshAsync([station], 2,
            (s, ct) => throw new InvalidOperationException("test read failure"), default);
        Assert.Equal("Error", result[0].OverallStatus);
        Assert.Empty(result[0].KeyFindings);
        Assert.NotNull(result[0].ErrorMessage);
    }

    [Fact]
    public async Task CancellationStopsBeforeReading()
    {
        var calls = 0;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LineDiagnosisObservationRecheck.RefreshAsync(
            [Station()], 2, (s, ct) => { calls++; return Task.FromResult(s); }, cancelled.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConfirmedActivityIsNotUndoneByATransientFalseDuringRefresh()
    {
        var station = Station();
        station.OverallStatus = "Running";
        var result = await LineDiagnosisObservationRecheck.RefreshAsync([station], 2,
            (s, ct) => Task.FromResult(Station()), default);
        Assert.Equal("Running", result[0].OverallStatus);
    }

    [Fact]
    public async Task MidObservationCancellationKeepsCompletedFreshResultsAndClearsUnfinishedOldEvidence()
    {
        var published = new System.Collections.Concurrent.ConcurrentDictionary<int, StationDiagnosisSummaryDto>();
        using var cancelled = new CancellationTokenSource();
        var a = Station(1); var b = Station(2);
        b.KeyFindings = [new() { EvaluatedValue = false, FaultOwnership = "WaitingFor:Other" }];
        var work = LineDiagnosisObservationRecheck.RefreshAsync([a, b], 2, async (s, ct) => {
            if (s.StationOrder == 2) await Task.Delay(Timeout.Infinite, ct);
            var current = Station(s.StationOrder); current.OverallStatus = "Running";
            return current;
        }, cancelled.Token, s => published[s.StationOrder] = s);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(2, published.Count);
        Assert.Equal("Running", published[1].OverallStatus);
        Assert.Equal("Pending", published[2].OverallStatus);
        Assert.Empty(published[2].KeyFindings);
    }
}
