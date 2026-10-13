using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed record LineUpMonitorBinding(
    long BindingId,
    long PublishedRevisionId,
    long StationId,
    string DefinitionRevision,
    ImmutableArray<LineUpRuntimeStateDefinition> Sequences,
    LineUpRuntimeReadPlan ReadPlan,
    IReadOnlyDictionary<string, string> ValueSequenceOwners,
    TimeSpan PollInterval,
    bool PersistSamples)
{
    public long? SubStationId { get; init; }

    public ImmutableDictionary<string, LineUpMonitorViewerDefinition> ViewerDefinitions { get; init; } =
        ImmutableDictionary<string, LineUpMonitorViewerDefinition>.Empty.WithComparers(StringComparer.Ordinal);
}

public sealed record LineUpMonitorDefinitionBundle(
    LineUpRuntimeStateDefinition Runtime,
    LineUpMonitorViewerDefinition Viewer);

public interface ILineUpMonitorBindingSource
{
    Task<IReadOnlyList<LineUpMonitorBindingSummaryDto>> LoadCatalogBySubStationAsync(
        long subStationId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LineUpMonitorBindingSummaryDto>>([]);

    Task<long?> ResolveSubStationAsync(
        long lineId,
        long deviceId,
        long stationId,
        CancellationToken cancellationToken) => Task.FromResult<long?>(null);

    Task<IReadOnlyList<LineUpMonitorBindingSummaryDto>> LoadCatalogAsync(
        long lineId,
        long deviceId,
        long stationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 载入待轮询的绑定。2026-09-21 页面驱动监控：<paramref name="bindingIds"/> 是当前有人在看的绑定，
    /// 调用方保证非空（空集时根本不该调到这里）。
    /// </summary>
    Task<IReadOnlyList<LineUpMonitorBinding>> LoadEnabledAsync(
        IReadOnlyCollection<long> bindingIds,
        CancellationToken cancellationToken);

    Task<LineUpMonitorBinding?> LoadByBindingAsync(
        long bindingId,
        CancellationToken cancellationToken);

    Task<LineUpMonitorEventPageDto> LoadEventsAsync(
        LineUpMonitorKey key,
        long? beforeEventId,
        int limit,
        CancellationToken cancellationToken);

    Task PersistAsync(
        LineUpMonitorBinding runtimeBinding,
        ImmutableArray<LineUpRuntimeEvent> events,
        ImmutableArray<LineUpRuntimeValue> samples,
        CancellationToken cancellationToken);
}

public interface ILineUpMonitorDeltaPublisher
{
    bool TryPublish(LineUpRuntimeDelta delta);
}

public sealed class LineUpMonitorStateStore : ISelfSingletonAutoInject, IDisposable
{
    private readonly ConcurrentDictionary<LineUpMonitorKey, LineUpRuntimeSnapshot> snapshots = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> bindingGates = new();

    public LineUpRuntimeSnapshot? TryGet(LineUpMonitorKey key) =>
        key.IsValid ? snapshots.GetValueOrDefault(key) : null;

    public IReadOnlyList<LineUpRuntimeSnapshot> ForBinding(long bindingId) =>
        bindingId > 0
            ? snapshots.Where(item => item.Key.BindingId == bindingId)
                .OrderBy(item => item.Key.SequenceSourceKey, StringComparer.Ordinal)
                .Select(item => item.Value)
                .ToArray()
            : [];

    internal SemaphoreSlim BindingGate(long bindingId) =>
        bindingGates.GetOrAdd(bindingId, _ => new(1, 1));

    internal void Set(LineUpRuntimeSnapshot snapshot)
    {
        var key = new LineUpMonitorKey(snapshot.BindingId, snapshot.SequenceSourceKey);
        if (!key.IsValid) throw new ArgumentException("The runtime snapshot identity is invalid.", nameof(snapshot));
        snapshots[key] = snapshot;
    }

    public void Dispose()
    {
        foreach (var gate in bindingGates.Values) gate.Dispose();
        bindingGates.Clear();
        snapshots.Clear();
    }
}

public sealed class LineUpMonitorCoordinator(
    ILineUpMonitorBindingSource bindingSource,
    ILineUpRuntimeValueReader valueReader,
    LineUpRuntimeStateEngine stateEngine,
    LineUpMonitorStateStore stateStore,
    ILineUpMonitorDeltaPublisher publisher,
    LineUpMonitorPublishedDefinitionSource publishedDefinitionSource,
    LineUpMonitorSubscriptionRegistry subscriptions) : ISelfScopedAutoInject
{
    public async Task<IReadOnlyList<LineUpMonitorBindingSummaryDto>> GetBindingsAsync(
        long subStationId,
        CancellationToken cancellationToken)
    {
        if (subStationId <= 0) return [];
        var bindings = await bindingSource.LoadCatalogBySubStationAsync(subStationId, cancellationToken);
        return bindings.Select(WithSequenceRuntimeStatus).ToArray();
    }

    public Task<long?> ResolveSubStationAsync(
        long lineId,
        long deviceId,
        long stationId,
        CancellationToken cancellationToken) =>
        bindingSource.ResolveSubStationAsync(lineId, deviceId, stationId, cancellationToken);

    public async Task<IReadOnlyList<LineUpMonitorBindingSummaryDto>> GetBindingsAsync(
        long lineId,
        long deviceId,
        long stationId,
        CancellationToken cancellationToken)
    {
        var bindings = await bindingSource.LoadCatalogAsync(lineId, deviceId, stationId, cancellationToken);
        return bindings.Select(WithSequenceRuntimeStatus).ToArray();
    }

    /// <summary>
    /// 用 StateStore 中的顺控快照补齐每个顺控的运行态（Error/Online/Warning/Enabled），
    /// 无快照（监控未启用或尚未轮询）保持空字符串。与导航树 Graph 节点共用
    /// LineUpMonitorNavigationApp.SequenceStatus 的分层判定（2026-09-11 B+C：
    /// 步超时为 Info 级不参与分层，网关断线类为 Warning，读值 Bad/互斥步为 Error）。
    /// </summary>
    private LineUpMonitorBindingSummaryDto WithSequenceRuntimeStatus(LineUpMonitorBindingSummaryDto binding)
    {
        if (!long.TryParse(binding.BindingId, out var bindingId) || bindingId <= 0
            || binding.Sequences.IsDefaultOrEmpty)
        {
            return binding;
        }
        var snapshots = stateStore.ForBinding(bindingId);
        if (snapshots.Count == 0)
        {
            return binding;
        }
        var statusBySequence = snapshots.ToDictionary(
            snapshot => snapshot.SequenceSourceKey,
            LineUpMonitorNavigationApp.SequenceStatus,
            StringComparer.Ordinal);
        return binding with
        {
            Sequences = binding.Sequences
                .Select(sequence => statusBySequence.TryGetValue(sequence.SequenceSourceKey, out var status)
                    ? sequence with { RuntimeStatus = status }
                    : sequence with { RuntimeStatus = "Enabled" })
                .ToImmutableArray(),
        };
    }

    public async Task<LineUpRuntimeSnapshot?> GetSnapshotAsync(
        LineUpMonitorKey key,
        CancellationToken cancellationToken)
    {
        if (!key.IsValid) return null;
        var binding = await bindingSource.LoadByBindingAsync(key.BindingId, cancellationToken);
        if (binding is null || FindDefinition(binding, key) is null) return null;
        var cached = stateStore.TryGet(key);
        if (Matches(cached, binding, key)) return cached;

        var gate = stateStore.BindingGate(key.BindingId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            cached = stateStore.TryGet(key);
            if (Matches(cached, binding, key)) return cached;
            await PollCoreAsync(binding, cancellationToken);
            return stateStore.TryGet(key);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LineUpMonitorDefinitionBundle?> GetDefinitionAsync(
        LineUpMonitorKey key,
        CancellationToken cancellationToken)
    {
        if (!key.IsValid) return null;
        var definition = BuildDefinition(
            await bindingSource.LoadByBindingAsync(key.BindingId, cancellationToken), key);
        if (definition is not null) return definition;

        // 2026-09-10：运行时管线（requireEnabled 全链路校验）未就绪时，降级为发布版本投影静态构造。
        // Definition 是静态数据，不应被采样管线的"本轮跳过"一票否决成 404（页面连底图都打不开）。
        return await publishedDefinitionSource.LoadAsync(key, cancellationToken);
    }

    private static LineUpMonitorDefinitionBundle? BuildDefinition(
        LineUpMonitorBinding? binding,
        LineUpMonitorKey key)
    {
        if (binding is null) return null;
        var runtime = FindDefinition(binding, key);
        if (runtime is null ||
            !binding.ViewerDefinitions.TryGetValue(key.SequenceSourceKey, out var viewer) ||
            !string.Equals(viewer.SequenceSourceKey, runtime.SequenceSourceKey, StringComparison.Ordinal) ||
            runtime.BindingId != binding.BindingId ||
            runtime.PublishedRevisionId != binding.PublishedRevisionId ||
            runtime.StationId != binding.StationId)
        {
            return null;
        }
        return new(runtime, viewer);
    }

    public Task<LineUpMonitorEventPageDto> GetEventsAsync(
        LineUpMonitorKey key,
        long? beforeEventId,
        int limit,
        CancellationToken cancellationToken) =>
        bindingSource.LoadEventsAsync(key, beforeEventId, Math.Clamp(limit, 1, 200), cancellationToken);

    /// <summary>
    /// 轮询一轮。2026-09-21 起只采「当前有活跃 SignalR 订阅者」的绑定：没人打开监控页时
    /// 整轮空转（不查库、不读 PLC），对应用户决定的「没人看就不采」。
    /// 首帧不靠这里——订阅时 <see cref="GetSnapshotAsync"/> 自己会立刻轮一次该绑定。
    /// </summary>
    public Task PollEnabledAsync(CancellationToken cancellationToken) =>
        PollBindingsAsync(subscriptions.ActiveBindingIds(), cancellationToken);

    /// <summary>
    /// 立刻预采指定绑定（2026-09-21 预热，用户选 B）：前端选中工位时 Hub 调用这里，
    /// 不等下一个 500ms tick，这样用户点到具体 graph 时 <see cref="GetSnapshotAsync"/>
    /// 已经能命中热快照，不必现场冷读 PLC——冷读要重新拉整份 ReadPlan、对 IGW 也是冷的
    /// （点位不在 30 秒活动窗口里，得逐点 ReadNowAsync），是进页面顿挫的主因。
    ///
    /// 与正常轮询共用 BindingGate：若该绑定此刻正被轮询，这里直接跳过，不会并发重复读。
    /// </summary>
    public Task WarmUpAsync(IReadOnlyCollection<long> bindingIds, CancellationToken cancellationToken) =>
        PollBindingsAsync(bindingIds, cancellationToken);

    private async Task PollBindingsAsync(
        IReadOnlyCollection<long> bindingIds,
        CancellationToken cancellationToken)
    {
        if (bindingIds is null || bindingIds.Count == 0) return;
        var bindings = await bindingSource.LoadEnabledAsync(bindingIds, cancellationToken) ?? [];
        var unique = bindings
            .Where(binding => binding is not null && binding.BindingId > 0)
            .GroupBy(binding => binding.BindingId)
            .Select(group => group.First())
            .ToArray();
        await Task.WhenAll(unique.Select(binding => PollBindingIfAvailableAsync(binding, cancellationToken)));
    }

    private async Task PollBindingIfAvailableAsync(
        LineUpMonitorBinding binding,
        CancellationToken cancellationToken)
    {
        var gate = stateStore.BindingGate(binding.BindingId);
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken)) return;
        try
        {
            await PollCoreAsync(binding, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PollCoreAsync(
        LineUpMonitorBinding binding,
        CancellationToken cancellationToken)
    {
        var read = await valueReader.ReadAsync(binding.ReadPlan, cancellationToken);
        var evaluations = new List<LineUpRuntimeEvaluation>(binding.Sequences.Length);
        foreach (var definition in binding.Sequences.OrderBy(x => x.SequenceSourceKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new LineUpMonitorKey(binding.BindingId, definition.SequenceSourceKey);
            var sequenceRead = ForSequence(
                read,
                binding.ValueSequenceOwners,
                definition.SequenceSourceKey,
                binding.DefinitionRevision);
            evaluations.Add(stateEngine.Evaluate(definition, stateStore.TryGet(key), sequenceRead));
        }

        var events = evaluations
            .SelectMany(evaluation => evaluation.Events)
            .Select((runtimeEvent, ordinal) => runtimeEvent with { EventOrdinal = ordinal })
            .ToImmutableArray();
        var samples = binding.PersistSamples ? read.Values : ImmutableArray<LineUpRuntimeValue>.Empty;
        if (!events.IsDefaultOrEmpty || !samples.IsDefaultOrEmpty)
        {
            await bindingSource.PersistAsync(binding, events, samples, cancellationToken);
        }

        foreach (var evaluation in evaluations)
        {
            stateStore.Set(evaluation.Snapshot);
            if (evaluation.Delta.RequiresSnapshot ||
                evaluation.Delta.FromSnapshotRevision > 0 &&
                evaluation.Delta.ToSnapshotRevision == evaluation.Delta.FromSnapshotRevision + 1)
            {
                publisher.TryPublish(evaluation.Delta);
            }
        }
    }

    private static LineUpRuntimeReadResult ForSequence(
        LineUpRuntimeReadResult read,
        IReadOnlyDictionary<string, string> owners,
        string sequenceSourceKey,
        string definitionRevision)
    {
        var values = read.Values
            .Where(value => owners.TryGetValue(value.SourceKey, out var owner) &&
                            string.Equals(owner, sequenceSourceKey, StringComparison.Ordinal))
            .ToImmutableArray();
        var status = read.Status == LineUpRuntimeReadStatus.Failed
            ? LineUpRuntimeReadStatus.Failed
            : values.IsDefaultOrEmpty
                ? LineUpRuntimeReadStatus.Empty
                : values.All(value => value.Quality == LineUpRuntimeQuality.Good)
                    ? LineUpRuntimeReadStatus.Completed
                    : values.Any(value => value.Quality == LineUpRuntimeQuality.Good)
                        ? LineUpRuntimeReadStatus.Partial
                        : LineUpRuntimeReadStatus.Failed;
        return read with { BindingRevision = definitionRevision, Status = status, Values = values };
    }

    private static LineUpRuntimeStateDefinition? FindDefinition(
        LineUpMonitorBinding binding,
        LineUpMonitorKey key) =>
        binding.Sequences.SingleOrDefault(definition =>
            definition.BindingId == key.BindingId &&
            string.Equals(definition.SequenceSourceKey, key.SequenceSourceKey, StringComparison.Ordinal));

    private static bool Matches(
        LineUpRuntimeSnapshot? snapshot,
        LineUpMonitorBinding binding,
        LineUpMonitorKey key)
    {
        var definition = FindDefinition(binding, key);
        return snapshot is not null && definition is not null &&
               snapshot.BindingId == binding.BindingId &&
               snapshot.PublishedRevisionId == binding.PublishedRevisionId &&
               string.Equals(snapshot.SequenceSourceKey, key.SequenceSourceKey, StringComparison.Ordinal) &&
               string.Equals(snapshot.DefinitionRevision, definition.DefinitionRevision, StringComparison.Ordinal);
    }
}
