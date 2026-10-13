using HSJM.AI.AppLayer.LineUp.Runtime;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

/// <summary>
/// 2026-09-21 页面驱动监控：轮询集合 = 当前有活跃 SignalR 订阅者的绑定。
/// 用户决定「没人看就不采」——没有宽限期，最后一个订阅者离开即停止轮询。
/// </summary>
public sealed class LineUpMonitorSubscriptionRegistryTests
{
    private static LineUpMonitorKey Key(long bindingId, string sequence = "seq-1") => new(bindingId, sequence);

    [Fact]
    public void No_subscriber_means_nothing_is_polled()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        Assert.Empty(registry.ActiveBindingIds());
    }

    [Fact]
    public void Subscribing_activates_the_binding()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.Add("conn-1", Key(10));
        Assert.Equal(new long[] { 10 }, registry.ActiveBindingIds());
    }

    [Fact]
    public void Unsubscribing_the_last_viewer_stops_the_binding()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.Add("conn-1", Key(10));
        registry.Remove("conn-1", Key(10));
        Assert.Empty(registry.ActiveBindingIds());
    }

    [Fact]
    public void A_second_viewer_keeps_the_binding_alive()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.Add("conn-1", Key(10));
        registry.Add("conn-2", Key(10));
        registry.Remove("conn-1", Key(10));
        Assert.Equal(new long[] { 10 }, registry.ActiveBindingIds());
        registry.Remove("conn-2", Key(10));
        Assert.Empty(registry.ActiveBindingIds());
    }

    /// <summary>前端重连会对同一 key 再 Subscribe 一次；重复订阅不得把引用计数抬高，
    /// 否则一次 Unsubscribe 之后绑定会永远留在轮询集合里。</summary>
    [Fact]
    public void Re_subscribing_the_same_connection_is_idempotent()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.Add("conn-1", Key(10));
        registry.Add("conn-1", Key(10));
        registry.Remove("conn-1", Key(10));
        Assert.Empty(registry.ActiveBindingIds());
    }

    /// <summary>同一绑定下切换工步（sequenceSourceKey）是两把 key，但轮询按绑定整体做，
    /// 所以只要还有任一工步被订阅，该绑定就要继续采。</summary>
    [Fact]
    public void Different_sequences_of_one_binding_share_the_poll_slot()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.Add("conn-1", Key(10, "seq-a"));
        registry.Add("conn-1", Key(10, "seq-b"));
        registry.Remove("conn-1", Key(10, "seq-a"));
        Assert.Equal(new long[] { 10 }, registry.ActiveBindingIds());
        registry.Remove("conn-1", Key(10, "seq-b"));
        Assert.Empty(registry.ActiveBindingIds());
    }

    /// <summary>浏览器直接关页面不会调 Unsubscribe，只会断连——必须靠 OnDisconnectedAsync
    /// 清掉该连接的全部订阅，否则绑定会永久留在轮询集合里（泄漏）。</summary>
    [Fact]
    public void Disconnecting_drops_every_subscription_of_that_connection()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.Add("conn-1", Key(10));
        registry.Add("conn-1", Key(20));
        registry.Add("conn-2", Key(20));

        registry.RemoveConnection("conn-1");

        Assert.Equal(new long[] { 20 }, registry.ActiveBindingIds());
    }

    [Fact]
    public void Removing_an_unknown_subscription_is_harmless()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.Remove("ghost", Key(10));
        registry.RemoveConnection("ghost");
        Assert.Empty(registry.ActiveBindingIds());
    }

    [Fact]
    public void Active_binding_ids_are_sorted_and_deduplicated()
    {
        var registry = new LineUpMonitorSubscriptionRegistry();
        registry.Add("conn-1", Key(30, "a"));
        registry.Add("conn-2", Key(10, "b"));
        registry.Add("conn-3", Key(30, "c"));
        Assert.Equal(new long[] { 10, 30 }, registry.ActiveBindingIds());
    }
}
