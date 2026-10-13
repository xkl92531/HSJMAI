#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

/// <summary>
/// LineUpMonitorBinding 构建产物缓存（2026-09-14）。
/// 背景：轮询每秒经 LoadEnabledAsync/LoadByBindingAsync 全量重建绑定——每绑定拉取
/// RuntimeBindingJson（实测平均 ~400KB、最大 1MB，双途径共两次）+ PreviewJson（~550KB），
/// 反序列化 3 次、发布投影 2 次、LAD 网络全量重编 1 次；24 绑定 ≈ 30MB/s DB 流量，
/// 串行加载耗时使实际采样周期超过 PollIntervalMs（画面卡顿、CPU 常驻）。
///
/// 本缓存只存"静态结构产物"（不可变 record LineUpMonitorBinding，含 ReadPlan/Sequences/
/// ViewerDefinitions）；PLC 读值、状态求值、事件落库、快照均不经过这里。
/// 新鲜度三通道：
/// ① 版本比对——热路径每轮用轻量行（不含 jsonb 列）比对 ValidationRevision/PublishedRevisionId，
///    绑定行被重新校验/导入/重写即失配重建（自愈 ≤1 个采样周期）；
/// ② 屏蔽集版本——LineUpTagCollectionRuleCache 写操作递增的 ContentVersion，采集开关变更即重建；
/// ③ 元数据时效复查——重建走完整 LoadAsync（内含 IsCurrentAsync），条目按 30~44s 抖动到期
///    （抖动按 bindingId 摊开，避免 24 绑定同 tick 聚簇重建）；PLC 工程 zip 重传由
///    IPlcProjectMetadataChangedHandler 主动逐出（实时），30s 复查仅为兜底。
/// </summary>
public sealed class LineUpMonitorBindingCache : ISelfSingletonAutoInject
{
    /// <summary>元数据时效复查基础间隔（IsCurrentAsync 随重建执行）。</summary>
    internal static readonly TimeSpan MetadataCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>复查间隔抖动上限：按 bindingId 摊开各绑定的重建时刻。</summary>
    private static readonly TimeSpan MetadataCheckJitter = TimeSpan.FromSeconds(15);

    internal sealed record CacheEntry(
        LineUpMonitorBinding Binding,
        string ValidationRevision,
        long PublishedRevisionId,
        long PlcProjectId,
        int ExcludedRulesVersion,
        DateTimeOffset LastMetadataCheckUtc);

    private readonly ConcurrentDictionary<long, CacheEntry> entries = new();

    /// <summary>条目存在、行版本/发布版本/屏蔽集版本一致、且未超元数据复查期限时返回构建产物。</summary>
    public LineUpMonitorBinding? TryGet(
        long bindingId,
        string? validationRevision,
        long publishedRevisionId,
        int excludedRulesVersion,
        DateTimeOffset now)
    {
        if (bindingId <= 0 || string.IsNullOrWhiteSpace(validationRevision)) return null;
        if (!entries.TryGetValue(bindingId, out var entry)) return null;
        if (!string.Equals(entry.ValidationRevision, validationRevision.Trim(), StringComparison.Ordinal)) return null;
        if (entry.PublishedRevisionId != publishedRevisionId) return null;
        if (entry.ExcludedRulesVersion != excludedRulesVersion) return null;
        if (now - entry.LastMetadataCheckUtc > StalenessLimit(bindingId)) return null;
        return entry.Binding;
    }

    /// <summary>重建成功后写入条目；lastMetadataCheckUtc 取重载完成时刻（LoadAsync 内 IsCurrentAsync 刚执行过）。</summary>
    public void Set(
        LineUpMonitorBinding binding,
        string validationRevision,
        long publishedRevisionId,
        long plcProjectId,
        int excludedRulesVersion,
        DateTimeOffset lastMetadataCheckUtc)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.BindingId <= 0 || string.IsNullOrWhiteSpace(validationRevision)) return;
        entries[binding.BindingId] = new(
            binding,
            validationRevision.Trim(),
            publishedRevisionId,
            plcProjectId,
            excludedRulesVersion,
            lastMetadataCheckUtc);
    }

    /// <summary>绑定行变更（重新校验/重置/导入/开关监控）后逐出——与 compiler.Invalidate 同批调用。</summary>
    public void Invalidate(long bindingId)
    {
        if (bindingId > 0) entries.TryRemove(bindingId, out _);
    }

    /// <summary>按 PLC 项目逐出（PLC 工程 zip 重解析后，块/接口/iotag 元数据可能已变）。</summary>
    public void InvalidateByPlcProject(long plcProjectId)
    {
        if (plcProjectId <= 0) return;
        foreach (var pair in entries)
        {
            if (pair.Value.PlcProjectId == plcProjectId) entries.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>清理不再启用的绑定条目（LoadEnabledAsync 每轮对齐，防停用/删除后条目滞留）。</summary>
    public void PruneExcept(IReadOnlyCollection<long> activeBindingIds)
    {
        ArgumentNullException.ThrowIfNull(activeBindingIds);
        var active = activeBindingIds as ISet<long> ?? activeBindingIds.ToHashSet();
        foreach (var key in entries.Keys)
        {
            if (!active.Contains(key)) entries.TryRemove(key, out _);
        }
    }

    public int Count => entries.Count;

    internal static TimeSpan StalenessLimit(long bindingId) =>
        MetadataCheckInterval + TimeSpan.FromSeconds(Math.Abs(bindingId) % (long)MetadataCheckJitter.TotalSeconds);
}
