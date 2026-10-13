using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Runtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Hubs;

[Authorize]
public sealed class LineUpMonitorHub(
    LineUpMonitorCoordinator coordinator,
    LineUpMonitorSubscriptionRegistry subscriptions,
    IServiceScopeFactory scopeFactory,
    ILogger<LineUpMonitorHub> logger) : Hub
{
    public const string SnapshotMessage = "LineUpMonitorSnapshot";
    public const string SnapshotRequiredMessage = "LineUpSnapshotRequired";

    /// <summary>一个工位正常只有几个 graph；封顶纯粹是防异常调用占住轮询与 PLC 带宽。</summary>
    public const int MaxWarmBindings = 64;

    /// <summary>预热请求来自前端（选中工位的绑定列表），当不可信输入处理：滤非法、去重、封顶。</summary>
    public static IReadOnlyList<long> ParseWarmBindingIds(IReadOnlyCollection<string>? bindingIds)
    {
        if (bindingIds is null || bindingIds.Count == 0) return [];
        var parsed = new List<long>();
        var seen = new HashSet<long>();
        foreach (var raw in bindingIds)
        {
            if (parsed.Count >= MaxWarmBindings) break;
            if (!long.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bindingId)
                || bindingId <= 0 || !seen.Add(bindingId))
            {
                continue;
            }
            parsed.Add(bindingId);
        }
        return parsed;
    }

    /// <summary>
    /// 预热（2026-09-21 用户选 B）：前端选中工位时把该工位名下全部绑定报上来，整体替换本连接的
    /// 预热集合（传空集即取消）。这样用户点到具体 graph 时 <c>Subscribe</c> 能命中热快照，
    /// 不必现场冷读 PLC——那是进页面顿挫的主因。
    ///
    /// 立即预采只对「本次真正新纳入轮询」的绑定发起，且不阻塞本次调用：前端还要接着做
    /// previewPublish / getDefinition，让这一次 PLC 读与它们并行跑掉。
    /// </summary>
    public Task Warm(IReadOnlyCollection<string>? bindingIds)
    {
        var parsed = ParseWarmBindingIds(bindingIds);
        var activated = subscriptions.SetWarm(Context.ConnectionId, parsed);
        if (activated.Count > 0) WarmUpInBackground(activated);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 后台预采。<see cref="LineUpMonitorCoordinator"/> 是 scoped，而 SignalR 每次 Hub 调用一个 scope——
    /// 调用返回后注入进来的那个实例就被释放了，所以这里必须另开 scope 取，不能捕获 <c>coordinator</c>。
    /// </summary>
    private void WarmUpInBackground(IReadOnlyList<long> bindingIds)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var scoped = scope.ServiceProvider.GetRequiredService<LineUpMonitorCoordinator>();
                await scoped.WarmUpAsync(bindingIds, CancellationToken.None);
            }
            catch (Exception error)
            {
                // 预热失败不影响功能，正常轮询下一个 tick 会补上。
                logger.LogWarning(error,
                    "LineUp monitor warm-up failed for bindings {BindingIds}.",
                    string.Join(",", bindingIds));
            }
        });
    }

    public static string BuildGroup(LineUpMonitorKey key)
    {
        if (!key.IsValid) throw new ArgumentException("The LineUp monitor identity is invalid.", nameof(key));
        var identity = $"{key.BindingId}\n{key.SequenceSourceKey}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"lineup-monitor-{hash}";
    }

    public async Task Subscribe(
        string bindingId,
        string sequenceSourceKey,
        string knownDefinitionRevision,
        string knownSnapshotRevision)
    {
        var key = Key(bindingId, sequenceSourceKey);
        var snapshot = await coordinator.GetSnapshotAsync(key, Context.ConnectionAborted);
        if (snapshot is null) throw new HubException("LineUpMonitorUnavailable");

        await Groups.AddToGroupAsync(Context.ConnectionId, BuildGroup(key));
        // 2026-09-21 页面驱动监控：Group 成员不可枚举，后台轮询答不出「现在谁在看」，
        // 所以同一份订阅另外登记一次；GetSnapshotAsync 上面已经把首帧轮出来了。
        subscriptions.Add(Context.ConnectionId, key);
        if ((!string.IsNullOrWhiteSpace(knownDefinitionRevision) &&
             !string.Equals(knownDefinitionRevision, snapshot.DefinitionRevision, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(knownSnapshotRevision) &&
             !string.Equals(knownSnapshotRevision, snapshot.SnapshotRevision.ToString(), StringComparison.Ordinal)))
        {
            await Clients.Caller.SendAsync(SnapshotRequiredMessage, new
            {
                bindingId = key.BindingId.ToString(CultureInfo.InvariantCulture),
                sequenceSourceKey = key.SequenceSourceKey,
                definitionRevision = snapshot.DefinitionRevision,
                snapshotRevision = snapshot.SnapshotRevision.ToString()
            });
        }
        await Clients.Caller.SendAsync(SnapshotMessage, LineUpMonitorWireProjection.Snapshot(snapshot));
        logger.LogInformation(
            "LineUp monitor client {ConnectionId} subscribed to binding {BindingId} sequence {SequenceSourceKey}.",
            Context.ConnectionId,
            key.BindingId,
            key.SequenceSourceKey);
    }

    public Task Unsubscribe(string bindingId, string sequenceSourceKey)
    {
        var key = TryKey(bindingId, sequenceSourceKey);
        if (key is null) return Task.CompletedTask;
        subscriptions.Remove(Context.ConnectionId, key.Value);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildGroup(key.Value));
    }

    /// <summary>
    /// 关页面/刷新/断网都不会调 Unsubscribe，只会断连——不在这里清掉该连接的订阅，
    /// 绑定会永远留在轮询集合里，页面驱动就退化回了全量常轮。
    /// </summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private static LineUpMonitorKey Key(string bindingId, string sequenceSourceKey) =>
        TryKey(bindingId, sequenceSourceKey) ?? throw new HubException("LineUpMonitorInvalidIdentity");

    private static LineUpMonitorKey? TryKey(string bindingId, string sequenceSourceKey)
    {
        var normalizedBindingId = bindingId?.Trim();
        var normalized = sequenceSourceKey?.Trim();
        return long.TryParse(normalizedBindingId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBindingId) &&
               parsedBindingId > 0 && !string.IsNullOrWhiteSpace(normalized) && normalized.Length <= 1_024
            ? new(parsedBindingId, normalized)
            : null;
    }
}
