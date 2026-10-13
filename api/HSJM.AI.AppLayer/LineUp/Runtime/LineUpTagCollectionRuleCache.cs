using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

/// <summary>
/// 变量采集开关规则的进程内缓存（2026-09-12）。
/// 采样每秒每绑定都会构造 ReadPlan，规则表不能每轮直查——按项目缓存 30 秒；
/// API 写操作（Enable/Disable）后主动 Invalidate 立即生效。
/// 2026-09-14 ContentVersion：写操作时按项目递增的版本号，供 LineUpMonitorBindingCache
/// 比对"构建产物是否基于过期的屏蔽集"——只在写时递增、加载不动，避免 30s 刷新引发无谓重建。
/// </summary>
public sealed class LineUpTagCollectionRuleCache : ISelfSingletonAutoInject
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);

    private sealed record Entry(DateTimeOffset LoadedAtUtc, HashSet<string> DisabledPaths);

    private readonly ConcurrentDictionary<long, Entry> cache = new();
    private readonly ConcurrentDictionary<long, byte> loading = new();
    private readonly ConcurrentDictionary<long, int> contentVersions = new();

    /// <summary>项目当前屏蔽集版本号：初始 0，每次规则写入递增。绑定缓存条目记录构建时版本，失配即重建。</summary>
    public int ContentVersion(long plcProjectId) =>
        plcProjectId > 0 ? contentVersions.GetValueOrDefault(plcProjectId) : 0;

    /// <summary>取项目当前被屏蔽的变量路径集合；缓存过期或未加载时查仓储回填。查询失败返回空集（不屏蔽）。</summary>
    public async Task<IReadOnlySet<string>> GetOrLoadAsync(
        long plcProjectId,
        ILineUpTagCollectionRuleRepository repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (plcProjectId <= 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(plcProjectId, out var entry) && now - entry.LoadedAtUtc < TimeToLive)
        {
            return entry.DisabledPaths;
        }

        // 同项目并发加载去重：抢不到锁的调用方直接用旧值（最多再活一个采样周期）。
        if (!loading.TryAdd(plcProjectId, 0))
        {
            return cache.TryGetValue(plcProjectId, out var stale) ? stale.DisabledPaths
                : new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            IReadOnlyList<string> rows;
            try
            {
                rows = await repository.ListDisabledAsync(plcProjectId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 规则表查询失败不阻断采样：退化为"不屏蔽"（保持旧值优先）。
                return cache.TryGetValue(plcProjectId, out var stale)
                    ? stale.DisabledPaths
                    : new HashSet<string>(StringComparer.Ordinal);
            }

            var fresh = new HashSet<string>(rows, StringComparer.Ordinal);
            cache[plcProjectId] = new Entry(DateTimeOffset.UtcNow, fresh);
            return fresh;
        }
        finally
        {
            loading.TryRemove(plcProjectId, out _);
        }
    }

    /// <summary>规则写入后失效（下一轮采样即重查）；同时递增 ContentVersion 令绑定缓存构建产物失配重建。</summary>
    public void Invalidate(long plcProjectId)
    {
        if (plcProjectId <= 0) return;
        cache.TryRemove(plcProjectId, out _);
        contentVersions.AddOrUpdate(plcProjectId, 1, (_, current) => current + 1);
    }
}
