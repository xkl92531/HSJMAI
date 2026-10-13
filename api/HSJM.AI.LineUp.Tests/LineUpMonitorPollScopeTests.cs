using System.Collections.Immutable;
using HSJM.AI.AppLayer.LineUp.Runtime;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

/// <summary>
/// 2026-09-21 页面驱动监控：后台轮询集合只能是「当前有人在看」的绑定。
/// 这里只验证取舍逻辑——无人订阅时连库都不查，有订阅时只按订阅到的 id 查。
/// 造一个空返回的假数据源，PollEnabledAsync 就不会走到后面的评估/持久化依赖。
/// </summary>
public sealed class LineUpMonitorPollScopeTests
{
    private sealed class RecordingBindingSource : ILineUpMonitorBindingSource
    {
        public int LoadEnabledCalls { get; private set; }
        public List<long>? LastRequestedIds { get; private set; }

        public Task<IReadOnlyList<LineUpMonitorBinding>> LoadEnabledAsync(
            IReadOnlyCollection<long> bindingIds,
            CancellationToken cancellationToken)
        {
            LoadEnabledCalls++;
            LastRequestedIds = bindingIds.ToList();
            return Task.FromResult<IReadOnlyList<LineUpMonitorBinding>>([]);
        }

        public Task<IReadOnlyList<LineUpMonitorBindingSummaryDto>> LoadCatalogAsync(
            long lineId, long deviceId, long stationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LineUpMonitorBinding?> LoadByBindingAsync(long bindingId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LineUpMonitorEventPageDto> LoadEventsAsync(
            LineUpMonitorKey key, long? beforeEventId, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task PersistAsync(
            LineUpMonitorBinding runtimeBinding,
            ImmutableArray<LineUpRuntimeEvent> events,
            ImmutableArray<LineUpRuntimeValue> samples,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static (LineUpMonitorCoordinator Coordinator, RecordingBindingSource Source, LineUpMonitorSubscriptionRegistry Registry) Build()
    {
        var source = new RecordingBindingSource();
        var registry = new LineUpMonitorSubscriptionRegistry();
        var coordinator = new LineUpMonitorCoordinator(
            source,
            valueReader: null!,
            stateEngine: null!,
            stateStore: new LineUpMonitorStateStore(),
            publisher: null!,
            publishedDefinitionSource: null!,
            subscriptions: registry);
        return (coordinator, source, registry);
    }

    /// <summary>没人打开监控页时，后台每 500ms 的一轮必须是彻底的空转：不查库、不读 PLC。</summary>
    [Fact]
    public async Task Nobody_watching_means_no_database_round_trip()
    {
        var (coordinator, source, _) = Build();
        await coordinator.PollEnabledAsync(CancellationToken.None);
        Assert.Equal(0, source.LoadEnabledCalls);
    }

    [Fact]
    public async Task Only_subscribed_bindings_are_loaded()
    {
        var (coordinator, source, registry) = Build();
        registry.Add("conn-1", new(42, "seq-a"));
        registry.Add("conn-2", new(7, "seq-b"));

        await coordinator.PollEnabledAsync(CancellationToken.None);

        Assert.Equal(1, source.LoadEnabledCalls);
        Assert.Equal(new long[] { 7, 42 }, source.LastRequestedIds);
    }

    /// <summary>最后一个订阅者离开后立刻停采（用户决定「没人看就不采」，无宽限期）。</summary>
    [Fact]
    public async Task Polling_stops_as_soon_as_the_last_viewer_leaves()
    {
        var (coordinator, source, registry) = Build();
        registry.Add("conn-1", new(42, "seq-a"));
        await coordinator.PollEnabledAsync(CancellationToken.None);
        Assert.Equal(1, source.LoadEnabledCalls);

        registry.RemoveConnection("conn-1");
        await coordinator.PollEnabledAsync(CancellationToken.None);

        Assert.Equal(1, source.LoadEnabledCalls);
    }

    /// <summary>
    /// 预热（2026-09-21 用户选 B）：选中工位时立刻预采该工位名下的绑定，不等下一个 500ms tick。
    /// 只加载指定的 id，且不依赖订阅登记表。
    /// </summary>
    [Fact]
    public async Task Warm_up_reads_exactly_the_requested_bindings()
    {
        var (coordinator, source, _) = Build();

        await coordinator.WarmUpAsync([11, 10], CancellationToken.None);

        Assert.Equal(1, source.LoadEnabledCalls);
        Assert.Equal(new long[] { 11, 10 }, source.LastRequestedIds);
    }

    [Fact]
    public async Task Warming_nothing_touches_no_database()
    {
        var (coordinator, source, _) = Build();
        await coordinator.WarmUpAsync([], CancellationToken.None);
        Assert.Equal(0, source.LoadEnabledCalls);
    }
}
