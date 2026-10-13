using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

/// <summary>
/// 页面驱动监控的订阅登记表（2026-09-21）：记录「当前有哪些前端连接正在看哪些绑定」，
/// <see cref="LineUpMonitorCoordinator.PollEnabledAsync"/> 只轮询这里列出的绑定。
///
/// 为什么需要它：SignalR 的 Group 成员是不可枚举的，Hub 自己答不出「现在谁在看」；
/// 而轮询集合必须能被后台服务查询。所以订阅在进 Group 的同时也登记到这里。
///
/// 语义（用户 2026-09-21 决定「没人看就不采」）：没有宽限期，最后一个订阅者离开即停止轮询。
/// 轮询以「绑定」为单位（一次读整份 ReadPlan 再评估其下全部工步），所以同一绑定的不同
/// sequenceSourceKey 共用一个轮询位，任一工步还被订阅就继续采。
///
/// 两类条目共用同一个轮询位（<see cref="Add"/> 的订阅 + <see cref="SetWarm"/> 的预热），
/// 任一还在就继续采。预热是 2026-09-21 为消除「首次进入某绑定要现场冷读 PLC」的顿挫加的：
/// 前端选中工位时把该工位名下的绑定整批报上来，等用户点到具体 graph 时快照已经热了。
/// 预热按连接整体替换（切工位即释放上一个工位），断连与订阅一并清掉。
///
/// 线程安全：Hub 回调与后台轮询在不同线程上并发访问，全部走并发集合 + 每键锁。
/// </summary>
public sealed class LineUpMonitorSubscriptionRegistry : ISelfSingletonAutoInject
{
    /// <summary>connectionId → 该连接订阅的全部 key（同一 key 重复 Subscribe 不重复计数）。</summary>
    private readonly ConcurrentDictionary<string, HashSet<LineUpMonitorKey>> byConnection = new(StringComparer.Ordinal);

    /// <summary>connectionId → 该连接预热的 bindingId 集合（选中工位时整体替换）。</summary>
    private readonly ConcurrentDictionary<string, HashSet<long>> warmByConnection = new(StringComparer.Ordinal);

    private readonly object gate = new();

    /// <summary>bindingId → 正在看它的订阅条目数（连接 × sequenceSourceKey）。降到 0 即移除。</summary>
    private readonly Dictionary<long, int> bindingRefCount = [];

    public void Add(string connectionId, LineUpMonitorKey key)
    {
        if (string.IsNullOrEmpty(connectionId) || !key.IsValid) return;
        lock (gate)
        {
            var keys = byConnection.GetOrAdd(connectionId, _ => []);
            // 前端重连会对同一 key 再订阅一次；重复订阅不得抬高引用计数，
            // 否则一次 Unsubscribe 之后绑定会永远留在轮询集合里。
            if (!keys.Add(key)) return;
            bindingRefCount[key.BindingId] = bindingRefCount.GetValueOrDefault(key.BindingId) + 1;
        }
    }

    /// <summary>
    /// 整体替换该连接的预热集合（前端选中工位时调用，传该工位名下全部绑定；传空集即取消预热）。
    /// </summary>
    /// <returns>
    /// 本次真正从「未被轮询」变为「被轮询」的 bindingId（升序）——调用方据此立刻预采一次，
    /// 不必等下一个轮询 tick。已被订阅或已被别的连接预热的绑定快照本来就是热的，不在返回值里。
    /// </returns>
    public IReadOnlyList<long> SetWarm(string connectionId, IReadOnlyCollection<long> bindingIds)
    {
        if (string.IsNullOrEmpty(connectionId)) return [];
        var next = new HashSet<long>();
        foreach (var bindingId in bindingIds ?? [])
        {
            if (bindingId > 0) next.Add(bindingId);
        }
        lock (gate)
        {
            var previous = warmByConnection.GetValueOrDefault(connectionId) ?? [];
            var activated = new List<long>();
            foreach (var bindingId in next)
            {
                if (previous.Contains(bindingId)) continue;
                // 先看有没有别的条目（本连接的订阅 / 别的连接）已经让它在轮询里——
                // 在的话快照已经热，不需要调用方再预采一次。
                if (!bindingRefCount.ContainsKey(bindingId)) activated.Add(bindingId);
                bindingRefCount[bindingId] = bindingRefCount.GetValueOrDefault(bindingId) + 1;
            }
            foreach (var bindingId in previous)
            {
                if (!next.Contains(bindingId)) Release(bindingId);
            }
            if (next.Count == 0) warmByConnection.TryRemove(connectionId, out _);
            else warmByConnection[connectionId] = next;
            activated.Sort();
            return activated;
        }
    }

    public void Remove(string connectionId, LineUpMonitorKey key)
    {
        if (string.IsNullOrEmpty(connectionId) || !key.IsValid) return;
        lock (gate)
        {
            if (!byConnection.TryGetValue(connectionId, out var keys) || !keys.Remove(key)) return;
            if (keys.Count == 0) byConnection.TryRemove(connectionId, out _);
            Release(key.BindingId);
        }
    }

    /// <summary>
    /// 连接断开时清掉它的全部订阅。浏览器直接关页面/刷新不会调 Unsubscribe，
    /// 只会断连——不在 OnDisconnectedAsync 里调用这个方法，绑定就会永久留在轮询集合里。
    /// </summary>
    public void RemoveConnection(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId)) return;
        lock (gate)
        {
            if (byConnection.TryRemove(connectionId, out var keys))
            {
                foreach (var key in keys) Release(key.BindingId);
            }
            if (warmByConnection.TryRemove(connectionId, out var warm))
            {
                foreach (var bindingId in warm) Release(bindingId);
            }
        }
    }

    /// <summary>当前需要轮询的绑定，升序去重；无人订阅时为空集（后台一次 PLC 读都不发）。</summary>
    public IReadOnlyList<long> ActiveBindingIds()
    {
        lock (gate)
        {
            if (bindingRefCount.Count == 0) return [];
            var ids = new List<long>(bindingRefCount.Keys);
            ids.Sort();
            return ids;
        }
    }

    /// <summary>调用方必须持有 <see cref="gate"/>。</summary>
    private void Release(long bindingId)
    {
        if (!bindingRefCount.TryGetValue(bindingId, out var count)) return;
        if (count <= 1) bindingRefCount.Remove(bindingId);
        else bindingRefCount[bindingId] = count - 1;
    }
}
