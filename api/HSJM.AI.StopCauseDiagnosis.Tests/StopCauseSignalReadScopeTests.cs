using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 2026-09-23：一次停机诊断内同一批点位只读一次。
/// 现场 IGW 日志：同一工位 GraphCandidates 与 ExecBitFallback、以及后续求值会在几秒内重复读同一组点。
/// </summary>
public class StopCauseSignalReadScopeTests
{
    private sealed class Reader
    {
        public readonly List<List<string>> Calls = new();
        public Func<string, PLC_ReadTagResult?> Row = symbol => new PLC_ReadTagResult
        {
            TagName = symbol,
            IsGood = true,
            Value = true,
        };

        public Task<List<PLC_ReadTagResult>> ReadAsync(IReadOnlyList<string> symbols)
        {
            lock (Calls)
            {
                Calls.Add(symbols.ToList());
            }
            return Task.FromResult(symbols.Select(Row).Where(r => r != null).Select(r => r!).ToList());
        }
    }

    [Fact]
    public void Current_is_null_outside_a_scope()
    {
        Assert.Null(StopCauseSignalReadScope.Current);
    }

    [Fact]
    public async Task Same_symbols_are_read_once_within_a_scope()
    {
        var reader = new Reader();
        using var scope = StopCauseSignalReadScope.Begin();

        var first = await scope.ReadAsync(1, new[] { "DB1.A", "DB1.B" }, reader.ReadAsync);
        var second = await scope.ReadAsync(1, new[] { "db1.a", "DB1.B" }, reader.ReadAsync);

        Assert.Single(reader.Calls);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.All(second, row => Assert.True(row.IsGood));
    }

    [Fact]
    public async Task Only_symbols_not_yet_read_go_to_the_reader()
    {
        var reader = new Reader();
        using var scope = StopCauseSignalReadScope.Begin();

        await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);
        var rows = await scope.ReadAsync(1, new[] { "DB1.A", "DB1.C" }, reader.ReadAsync);

        Assert.Equal(2, reader.Calls.Count);
        Assert.Equal(new[] { "DB1.C" }, reader.Calls[1]);
        Assert.Equal(new[] { "DB1.A", "DB1.C" }, rows.Select(r => r.TagName).OrderBy(x => x));
    }

    [Fact]
    public async Task Concurrent_reads_of_the_same_symbols_share_one_request()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var scope = StopCauseSignalReadScope.Begin();

        async Task<List<PLC_ReadTagResult>> SlowRead(IReadOnlyList<string> symbols)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return symbols.Select(s => new PLC_ReadTagResult { TagName = s, IsGood = true, Value = 1 }).ToList();
        }

        var a = scope.ReadAsync(1, new[] { "DB1.A" }, SlowRead);
        var b = scope.ReadAsync(1, new[] { "DB1.A" }, SlowRead);
        gate.SetResult();
        await Task.WhenAll(a, b);

        Assert.Equal(1, calls);
        Assert.Equal(1, Assert.Single(await b).Value);
    }

    [Fact]
    public async Task Failed_reads_are_not_cached()
    {
        var reader = new Reader
        {
            Row = s => new PLC_ReadTagResult { TagName = s, IsGood = false, ErrorMessage = "PlcExactReadFailed" },
        };
        using var scope = StopCauseSignalReadScope.Begin();

        await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);
        await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);

        Assert.Equal(2, reader.Calls.Count);
    }

    [Fact]
    public async Task Symbols_missing_from_the_reply_are_not_cached()
    {
        var reader = new Reader { Row = _ => null };
        using var scope = StopCauseSignalReadScope.Begin();

        var rows = await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);
        await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);

        Assert.Empty(rows);
        Assert.Equal(2, reader.Calls.Count);
    }

    [Fact]
    public async Task Expired_values_are_read_again()
    {
        var now = new DateTime(2026, 9, 23, 10, 0, 0);
        var reader = new Reader();
        using var scope = StopCauseSignalReadScope.Begin(TimeSpan.FromSeconds(15), () => now);

        await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);
        now = now.AddSeconds(14);
        await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);
        now = now.AddSeconds(2);
        await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);

        Assert.Equal(2, reader.Calls.Count);
    }

    [Fact]
    public async Task Projects_do_not_share_values()
    {
        var reader = new Reader();
        using var scope = StopCauseSignalReadScope.Begin();

        await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);
        await scope.ReadAsync(2, new[] { "DB1.A" }, reader.ReadAsync);

        Assert.Equal(2, reader.Calls.Count);
    }

    [Fact]
    public async Task Reader_exception_propagates_and_the_next_read_retries()
    {
        var fail = true;
        var calls = 0;
        using var scope = StopCauseSignalReadScope.Begin();

        Task<List<PLC_ReadTagResult>> Read(IReadOnlyList<string> symbols)
        {
            calls++;
            if (fail)
            {
                throw new InvalidOperationException("boom");
            }
            return Task.FromResult(symbols.Select(s => new PLC_ReadTagResult { TagName = s, IsGood = true }).ToList());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ReadAsync(1, new[] { "DB1.A" }, Read));
        fail = false;
        var rows = await scope.ReadAsync(1, new[] { "DB1.A" }, Read);

        Assert.Equal(2, calls);
        Assert.True(Assert.Single(rows).IsGood);
    }

    [Fact]
    public async Task Callers_get_their_own_copies()
    {
        var reader = new Reader();
        using var scope = StopCauseSignalReadScope.Begin();

        var first = await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);
        first[0].Value = "changed";
        var second = await scope.ReadAsync(1, new[] { "DB1.A" }, reader.ReadAsync);

        Assert.Equal(true, second[0].Value);
    }

    [Fact]
    public async Task Scope_flows_into_child_tasks_and_ends_on_dispose()
    {
        StopCauseSignalReadScope? seen = null;
        using (var scope = StopCauseSignalReadScope.Begin())
        {
            await Task.Run(() => seen = StopCauseSignalReadScope.Current);
            Assert.Same(scope, seen);
        }

        Assert.Null(StopCauseSignalReadScope.Current);
    }
}
