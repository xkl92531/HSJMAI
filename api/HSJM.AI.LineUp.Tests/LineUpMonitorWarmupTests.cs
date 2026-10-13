using HSJM.AI.AppLayer.LineUp.Runtime;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

/// <summary>
/// 2026-09-21 预热（用户选 B）：选中工位时就把该工位名下的绑定纳入轮询，
/// 等用户点到具体 graph 时快照已经热了，不必在 Subscribe 里现场冷读 PLC。
///
/// 预热与订阅是两类条目，但共用同一个轮询位：任一还在，绑定就继续采。
/// 预热按连接整体替换（切工位即释放上一个工位），断连一并清掉——
/// 否则页面驱动会退化回全量常轮。
/// </summary>
public sealed class LineUpMonitorWarmupTests
{
    private static LineUpMonitorKey Key(long bindingId, string sequence = "seq-1") => new(bindingId, sequence);

    [Fact]
    public void Warming_activates_the_bindings()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.SetWarm("conn-1", [11, 10]);
        Assert.Equal(new long[] { 10, 11 }, registry.ActiveBindingIds());
    }

    [Fact]
    public void Warming_reports_which_bindings_need_a_first_read()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        Assert.Equal(new long[] { 10, 11 }, registry.SetWarm("conn-1", [11, 10]));
    }

    [Fact]
    public void Re_warming_the_same_station_asks_for_no_further_reads()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.SetWarm("conn-1", [10, 11]);
        Assert.Empty(registry.SetWarm("conn-1", [10, 11]));
        Assert.Equal(new long[] { 10, 11 }, registry.ActiveBindingIds());
    }

    [Fact]
    public void Warming_a_subscribed_binding_needs_no_read_because_its_snapshot_is_already_hot()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.Add("conn-1", Key(10));
        Assert.Empty(registry.SetWarm("conn-1", [10]));
    }

    [Fact]
    public void Switching_station_releases_the_previous_stations_bindings()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.SetWarm("conn-1", [10, 11]);
        registry.SetWarm("conn-1", [12]);
        Assert.Equal(new long[] { 12 }, registry.ActiveBindingIds());
    }

    [Fact]
    public void Clearing_the_warm_set_stops_polling()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.SetWarm("conn-1", [10]);
        registry.SetWarm("conn-1", []);
        Assert.Empty(registry.ActiveBindingIds());
    }

    [Fact]
    public void A_subscription_survives_the_warm_set_being_replaced()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.SetWarm("conn-1", [10]);
        registry.Add("conn-1", Key(10));
        registry.SetWarm("conn-1", [99]);
        Assert.Equal(new long[] { 10, 99 }, registry.ActiveBindingIds());
    }

    [Fact]
    public void A_warm_entry_keeps_the_binding_after_the_viewer_unsubscribes()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.SetWarm("conn-1", [10]);
        registry.Add("conn-1", Key(10));
        registry.Remove("conn-1", Key(10));
        Assert.Equal(new long[] { 10 }, registry.ActiveBindingIds());
    }

    [Fact]
    public void Another_viewers_warm_set_keeps_the_binding_alive()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.SetWarm("conn-1", [10]);
        registry.SetWarm("conn-2", [10]);
        registry.SetWarm("conn-1", []);
        Assert.Equal(new long[] { 10 }, registry.ActiveBindingIds());
    }

    [Fact]
    public void Disconnecting_drops_the_warm_set_too()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.SetWarm("conn-1", [10, 11]);
        registry.Add("conn-1", Key(10));
        registry.RemoveConnection("conn-1");
        Assert.Empty(registry.ActiveBindingIds());
    }

    [Fact]
    public void Invalid_binding_ids_are_ignored()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        Assert.Empty(registry.SetWarm("conn-1", [0, -5]));
        Assert.Empty(registry.ActiveBindingIds());
    }

    [Fact]
    public void Warming_without_a_connection_id_is_harmless()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        Assert.Empty(registry.SetWarm("", [10]));
        Assert.Empty(registry.ActiveBindingIds());
    }

    [Fact]
    public void Duplicate_ids_in_one_warm_request_take_a_single_poll_slot()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        Assert.Equal(new long[] { 10 }, registry.SetWarm("conn-1", [10, 10, 10]));
        registry.SetWarm("conn-1", []);
        Assert.Empty(registry.ActiveBindingIds());
    }
}
