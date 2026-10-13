using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class StationSubTimingsTests
{
    [Fact]
    public void Add_outside_a_scope_is_ignored()
    {
        StationSubTimings.Add("Trace", 10);
        Assert.Null(StationSubTimings.Current);
    }

    [Fact]
    public void Values_accumulate_in_first_seen_order()
    {
        using var scope = StationSubTimings.Begin();
        StationSubTimings.Add("Trace", 10);
        StationSubTimings.Add("History", 5);
        StationSubTimings.Add("Trace", 7);

        Assert.Equal(
            new[] { new KeyValuePair<string, long>("Trace", 17), new KeyValuePair<string, long>("History", 5) },
            scope.Snapshot());
    }

    [Fact]
    public async Task Parallel_stations_do_not_see_each_others_timings()
    {
        async Task<IReadOnlyList<KeyValuePair<string, long>>> Station(string name)
        {
            using var scope = StationSubTimings.Begin();
            await Task.Yield();
            StationSubTimings.Add(name, 1);
            await Task.Yield();
            return scope.Snapshot();
        }

        var results = await Task.WhenAll(Station("A"), Station("B"));

        Assert.Equal("A", Assert.Single(results[0]).Key);
        Assert.Equal("B", Assert.Single(results[1]).Key);
        Assert.Null(StationSubTimings.Current);
    }

    [Fact]
    public async Task Measure_records_elapsed_time()
    {
        using var scope = StationSubTimings.Begin();
        using (StationSubTimings.Measure("Wait"))
        {
            await Task.Delay(30);
        }

        Assert.True(Assert.Single(scope.Snapshot()).Value >= 20);
    }
}
