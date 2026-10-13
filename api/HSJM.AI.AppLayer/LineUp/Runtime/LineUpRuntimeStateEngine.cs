using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public enum LineUpGatewayStatus
{
    Connecting,
    Online,
    Degraded,
    Offline,
    Reloading,
    ConfigurationError
}

public enum LineUpStepRuntimeStatus
{
    Idle,
    Active,
    Waiting,
    Completed,
    Warning,
    TimedOut,
    Unknown
}

public enum LineUpTransitionRuntimeStatus
{
    Unsatisfied,
    Satisfied,
    JustFired,
    Unknown
}

public enum LineUpTriState
{
    False,
    True,
    Unknown
}

public enum LineUpRuntimeEventType
{
    StepEntered,
    StepExited,
    TransitionSatisfied,
    TransitionFired,
    StepWarning,
    StepTimedOut,
    TimeoutCleared,
    SignalBad,
    SignalRecovered,
    GatewayOffline,
    GatewayRecovered,
    BindingInvalidated,
    ConflictingActiveSteps,
    RuntimeError
}

public sealed record LineUpRuntimeStepDefinition(
    string SourceKey,
    string DisplayName,
    bool IsInitial,
    string WarningTime,
    string MaximumStepTime);

public sealed record LineUpRuntimeTransitionDefinition(
    string SourceKey,
    string DisplayName,
    ImmutableArray<string> ConditionCanonicalKeys)
{
    public LineUpRuntimeTransitionDefinition(
        string sourceKey,
        string displayName,
        string conditionCoilCanonicalKey)
        : this(sourceKey, displayName, [conditionCoilCanonicalKey])
    {
    }
}

public sealed record LineUpRuntimeEdgeDefinition(
    string SourceKey,
    string FromSourceKey,
    string ToSourceKey,
    string EdgeKind);

public sealed record LineUpRuntimeStateDefinition(
    long BindingId,
    long PublishedRevisionId,
    long StationId,
    string SequenceSourceKey,
    string SequenceDisplayName,
    string DefinitionRevision,
    ImmutableArray<LineUpRuntimeStepDefinition> Steps,
    ImmutableArray<LineUpRuntimeTransitionDefinition> Transitions,
    ImmutableArray<LineUpRuntimeEdgeDefinition> Edges,
    ImmutableArray<ImmutableArray<string>> ExclusiveStepGroups,
    ImmutableArray<LineUpRuntimeLogicNetworkDefinition> CompiledNetworks)
{
    public LineUpRuntimeStateDefinition(
        long bindingId,
        long publishedRevisionId,
        long stationId,
        string sequenceSourceKey,
        string sequenceDisplayName,
        string definitionRevision,
        ImmutableArray<LineUpRuntimeStepDefinition> steps,
        ImmutableArray<LineUpRuntimeTransitionDefinition> transitions,
        ImmutableArray<LineUpRuntimeEdgeDefinition> edges,
        ImmutableArray<ImmutableArray<string>> exclusiveStepGroups)
        : this(bindingId, publishedRevisionId, stationId, sequenceSourceKey, sequenceDisplayName,
            definitionRevision, steps, transitions, edges, exclusiveStepGroups, [])
    {
    }
}

public sealed record LineUpConnectionHealth(
    LineUpGatewayStatus Status,
    DateTimeOffset EvaluatedAtUtc,
    DateTimeOffset? LastGoodSampleAtUtc,
    string ErrorCode);

public sealed record LineUpStepRuntimeState(
    string SourceKey,
    string DisplayName,
    LineUpStepRuntimeStatus Status,
    bool? LastValidValue,
    LineUpRuntimeQuality Quality,
    long ActiveDurationMs,
    DateTimeOffset StateChangedAtUtc,
    DateTimeOffset? LastGoodAtUtc,
    string QualityCode,
    // 2026-09-11 需求①：值读取不到时保持上一状态（画面不闪 Unknown），仅记异常事件。
    // ReadBad 标记"当前处于读坏保持期"（状态字段保持上一 Good 周期原值），用于
    // SignalBad 只记一次、恢复时配对 SignalRecovered。前端快照忽略该字段。
    bool ReadBad = false);

public sealed record LineUpTransitionRuntimeState(
    string SourceKey,
    string DisplayName,
    LineUpTransitionRuntimeStatus Status,
    LineUpTriState Condition,
    LineUpRuntimeQuality Quality,
    DateTimeOffset StateChangedAtUtc,
    string QualityCode,
    bool ReadBad = false);

public sealed record LineUpConditionRuntimeValue(
    string CanonicalKey,
    string OwnerSourceKey,
    string NetworkSourceKey,
    string PartSourceKey,
    LineUpTriState State,
    LineUpRuntimeQuality Quality,
    object Value,
    DateTimeOffset? SourceTimestamp,
    string QualityCode,
    // 2026-09-12：Access(GlobalVariable) 部件的完整变量路径（派生部件 null），
    // 供快照实时条件显示变量名与采集开关定位。
    string? VariablePath = null)
{
    public string SourceKey => PartSourceKey;
}

public sealed record LineUpEdgeRuntimeState(
    string SourceKey,
    bool WasTraversed,
    DateTimeOffset? TraversedAtUtc);

public sealed record LineUpRuntimeExceptionState(
    string Code,
    string OwnerSourceKey,
    string Severity,
    string SafeMessage,
    DateTimeOffset OccurredAtUtc);

public sealed record LineUpRuntimeSnapshot(
    long BindingId,
    long PublishedRevisionId,
    long StationId,
    string SequenceSourceKey,
    string DefinitionRevision,
    long SnapshotRevision,
    DateTimeOffset EvaluatedAtUtc,
    LineUpConnectionHealth Connection,
    ImmutableArray<LineUpStepRuntimeState> Steps,
    ImmutableArray<LineUpTransitionRuntimeState> Transitions,
    ImmutableArray<LineUpConditionRuntimeValue> Conditions,
    ImmutableArray<LineUpEdgeRuntimeState> Edges,
    ImmutableArray<LineUpRuntimeExceptionState> Exceptions,
    ImmutableArray<string> ActiveStepSourceKeys,
    // 2026-09-15 用户需求：异常/质量判定只跟踪当前活动步的出边转移条件——GRAPH 运行时只有活动步的
    // 出边转移会参与转换判定，非活动步出边（如 T26）的条件位 Bad 不应把整个顺控打成 Error/Degraded。
    // 该字段 = 本轮求值后活动步（含 Waiting/Warning/TimedOut 保持期）的全部出边转移 SourceKey。
    ImmutableArray<string> ActiveTransitionSourceKeys);

public sealed record LineUpRuntimeDelta(
    long BindingId,
    long PublishedRevisionId,
    long StationId,
    string SequenceSourceKey,
    string DefinitionRevision,
    long FromSnapshotRevision,
    long ToSnapshotRevision,
    DateTimeOffset ServerTimestamp,
    LineUpConnectionHealth Connection,
    ImmutableArray<LineUpStepRuntimeState> ChangedSteps,
    ImmutableArray<LineUpTransitionRuntimeState> ChangedTransitions,
    ImmutableArray<LineUpConditionRuntimeValue> ChangedConditions,
    ImmutableArray<LineUpEdgeRuntimeState> ChangedEdges,
    ImmutableArray<LineUpRuntimeExceptionState> Exceptions,
    bool RequiresSnapshot);

public sealed record LineUpRuntimeEvent(
    long BindingId,
    string SequenceSourceKey,
    string OwnerSourceKey,
    LineUpRuntimeEventType EventType,
    string PreviousState,
    string CurrentState,
    DateTimeOffset OccurredAtUtc,
    long? DurationMs,
    LineUpRuntimeQuality? Quality,
    string QualityCode,
    string DefinitionRevision,
    string GatewayStreamId,
    long GatewaySequence,
    int EventOrdinal,
    string DetailsJson);

public sealed record LineUpRuntimeEvaluation(
    LineUpRuntimeSnapshot Snapshot,
    LineUpRuntimeDelta Delta,
    ImmutableArray<LineUpRuntimeEvent> Events);

public sealed class LineUpRuntimeStateEngine : ISelfSingletonAutoInject
{
    private static readonly Regex TiaDuration = new(
        "^(?:T|TIME)#(?:(?<h>[0-9]+)H)?(?:(?<m>[0-9]+)M)?(?:(?<s>[0-9]+)S)?(?:(?<ms>[0-9]+)MS)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly TimeProvider timeProvider;

    public LineUpRuntimeStateEngine() : this(TimeProvider.System)
    {
    }

    internal LineUpRuntimeStateEngine(TimeProvider timeProvider) =>
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public LineUpRuntimeEvaluation Evaluate(
        LineUpRuntimeStateDefinition definition,
        LineUpRuntimeSnapshot previous,
        LineUpRuntimeReadResult readResult)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(readResult);
        if (definition.BindingId <= 0 || definition.PublishedRevisionId <= 0 || definition.StationId <= 0 ||
            string.IsNullOrWhiteSpace(definition.SequenceSourceKey) ||
            readResult.BindingId != definition.BindingId)
        {
            throw new ArgumentException("The read result does not belong to the runtime definition.", nameof(readResult));
        }
        if (previous is not null &&
            (previous.BindingId != definition.BindingId ||
             !string.Equals(previous.SequenceSourceKey, definition.SequenceSourceKey, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The previous snapshot does not belong to the runtime definition.", nameof(previous));
        }

        var now = timeProvider.GetUtcNow();
        var definitionChanged = previous is not null &&
                                (previous.PublishedRevisionId != definition.PublishedRevisionId ||
                                 !string.Equals(previous.DefinitionRevision, definition.DefinitionRevision,
                                     StringComparison.Ordinal));
        var prior = definitionChanged ? null : previous;
        var snapshotRevision = (prior?.SnapshotRevision ?? 0) + 1;
        var eventDrafts = new List<EventDraft>();
        if (definitionChanged)
        {
            eventDrafts.Add(new(LineUpRuntimeEventType.BindingInvalidated, string.Empty,
                previous.DefinitionRevision, definition.DefinitionRevision, null, null, null, "{}"));
        }

        // 2026-09-15：连接质量的 Degraded 判定收窄——先从原始读值直算当前活动步及其出边转移，
        // 供 Connection 的 allGood 过滤"非活动步出边转移条件位"（读值层 OwnerSourceKey=转移 key）。
        // StepActive 角色的 SourceKey 即步 SourceKey（与 stepValues 分组同源）；仅计 Good 且为 true 的位，
        // 若 StepActive 本身有 Bad，allGood 仍会因 StepActive 非 Good 判 Degraded，收窄不引入漏报。
        var activeStepKeysFromRead = readResult.Values
            .Where(x => x.Role == LineUpRuntimeVariableRole.StepActive &&
                        x.Quality == LineUpRuntimeQuality.Good &&
                        x.Value is true)
            .Select(x => x.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        var transitionKeySet = definition.Transitions
            .Select(x => x.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        var activeOutgoingFromRead = definition.Edges
            .Where(edge => activeStepKeysFromRead.Contains(edge.FromSourceKey) &&
                           transitionKeySet.Contains(edge.ToSourceKey))
            .Select(edge => edge.ToSourceKey)
            .ToHashSet(StringComparer.Ordinal);
        var connection = Connection(readResult, prior?.Connection, now, eventDrafts, activeOutgoingFromRead);
        if (connection.Status != LineUpGatewayStatus.Online)
        {
            // 2026-09-10：连接非 Online 的诊断日志（60s/绑定节流）——任一 tag 非 Good 即 Degraded，
            // 且 SnapshotHasError 把 Degraded 判 Error（树上"异常"）。此日志点名具体坏 tag。
            var loggedAt = DegradedLogAt.GetValueOrDefault(definition.BindingId);
            if ((now - loggedAt).TotalSeconds >= 60)
            {
                DegradedLogAt[definition.BindingId] = now;
                var badSample = readResult.Values
                    .Where(x => x.Quality != LineUpRuntimeQuality.Good)
                    .Take(3)
                    .Select(x => x.CanonicalKey + "(" + x.Quality + ":" + x.ErrorCode + ")")
                    .ToArray();
                var badCount = readResult.Values.Count(x => x.Quality != LineUpRuntimeQuality.Good);
                Console.WriteLine(
                    "[LineUpMonitor] 连接非Online binding=" + definition.BindingId +
                    " status=" + connection.Status + " err=" + connection.ErrorCode +
                    " 非Good=" + badCount + "/" + readResult.Values.Length +
                    " 样例=" + string.Join(" | ", badSample));
            }
        }
        var stepValues = readResult.Values
            .Where(x => x.Role == LineUpRuntimeVariableRole.StepActive)
            .GroupBy(x => x.SourceKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var priorSteps = (prior?.Steps ?? []).ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
        var steps = definition.Steps
            .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
            .Select(step => EvaluateStep(step, priorSteps.GetValueOrDefault(step.SourceKey),
                stepValues.GetValueOrDefault(step.SourceKey), prior?.EvaluatedAtUtc, now, eventDrafts))
            .ToList();

        // 2026-09-13 互斥冲突短路（用户需求）：任一互斥组内 ≥2 步同时活动 ⇒ 本轮采集判为无效——
        // 转移求值/JustFired/等待态/边触发/步状态推进全部跳过，graph 状态（步/转移/条件/边/活动步集合/
        // 异常列表）整体冻结在上一快照（活动时长不累计、画面不闪、树上状态不变——用户拍板：树上不显示，
        // 冲突仅留痕：事件流 + 60s 节流日志）。步求值已产生的草稿事件一并丢弃（状态冻结后事件与状态
        // 必须自洽），连接/定义失效事件保留。无上一快照可冻结（首轮即冲突）时不短路，走下方正常流程。
        var conflictKeys = ExclusiveConflictKeys(definition, steps);
        if (conflictKeys.Count > 0 && prior is not null)
        {
            var conflictOwner = string.Join(',', conflictKeys);
            var throttleKey = (definition.BindingId, definition.SequenceSourceKey);
            var loggedAt = ConflictLogAt.GetValueOrDefault(throttleKey);
            if ((now - loggedAt).TotalSeconds >= 60)
            {
                ConflictLogAt[throttleKey] = now;
                Console.WriteLine(
                    "[LineUpMonitor] 互斥步冲突，本轮采集无效 binding=" + definition.BindingId +
                    " sequence=" + definition.SequenceSourceKey + " steps=" + conflictOwner);
                eventDrafts.Add(new(LineUpRuntimeEventType.ConflictingActiveSteps,
                    conflictOwner, null, "SampleDiscarded", null,
                    LineUpRuntimeQuality.Good, null, "{}"));
            }
            eventDrafts.RemoveAll(draft => draft.EventType
                is not LineUpRuntimeEventType.GatewayOffline
                and not LineUpRuntimeEventType.GatewayRecovered
                and not LineUpRuntimeEventType.BindingInvalidated
                and not LineUpRuntimeEventType.ConflictingActiveSteps);
            var frozen = prior with
            {
                SnapshotRevision = snapshotRevision,
                EvaluatedAtUtc = now,
                Connection = connection,
            };
            var frozenDelta = Delta(frozen, prior, definitionChanged, previous?.Connection);
            return new(frozen, frozenDelta, BuildEvents());
        }

        var logicContext = new LineUpRuntimeLogicEvaluationContext(
            definition.BindingId, definition.DefinitionRevision);
        var logicEvaluator = new LineUpLadNetworkEvaluator();
        var networkEvaluations = definition.CompiledNetworks
            .OrderBy(network => network.OwnerSourceKey, StringComparer.Ordinal)
            .ThenBy(network => network.NetworkSourceKey, StringComparer.Ordinal)
            .Select(network => logicEvaluator.Evaluate(network, logicContext, readResult.Values))
            .ToImmutableArray();
        var partEvaluations = networkEvaluations.SelectMany(evaluation => evaluation.PartEvaluations).ToArray();
        if (partEvaluations.Select(item => item.CanonicalKey).Distinct(StringComparer.Ordinal).Count() !=
            partEvaluations.Length)
        {
            throw new InvalidOperationException("RuntimeConditionCanonicalKeyDuplicate");
        }
        var conditions = partEvaluations
            .OrderBy(item => item.CanonicalKey, StringComparer.Ordinal)
            .Select(ToCondition)
            .ToImmutableArray();
        var conditionByCanonicalKey = conditions.ToDictionary(item => item.CanonicalKey, StringComparer.Ordinal);
        var priorTransitions = (prior?.Transitions ?? []).ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
        var transitions = definition.Transitions
            .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
            .Select(transition => EvaluateTransition(transition,
                priorTransitions.GetValueOrDefault(transition.SourceKey), conditionByCanonicalKey, now, eventDrafts))
            .ToList();

        var firedTransitions = FindFiredTransitions(definition, priorSteps, steps);
        for (var index = 0; index < transitions.Count; index++)
        {
            var transition = transitions[index];
            if (!firedTransitions.Contains(transition.SourceKey)) continue;
            transitions[index] = transition with
            {
                Status = LineUpTransitionRuntimeStatus.JustFired,
                StateChangedAtUtc = now
            };
            eventDrafts.Add(new(LineUpRuntimeEventType.TransitionFired, transition.SourceKey,
                transition.Status.ToString(), LineUpTransitionRuntimeStatus.JustFired.ToString(), null,
                transition.Quality, transition.QualityCode, "{}"));
        }

        ApplyWaitingStates(definition, transitions, steps, now);
        var activeKeys = steps
            .Where(IsActive)
            .Select(x => x.SourceKey)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToImmutableArray();
        var activeKeySet = activeKeys.ToHashSet(StringComparer.Ordinal);
        // 2026-09-15：正式版激活出边转移集合（基于求值后的活动步，含 Waiting/Warning/TimedOut 保持期）
        //——快照字段，供 Navigation 序列 Error/Online 判定收窄到"当前活动步的出边转移"。
        var activeTransitionKeys = definition.Edges
            .Where(edge => activeKeySet.Contains(edge.FromSourceKey) &&
                           transitionKeySet.Contains(edge.ToSourceKey))
            .Select(edge => edge.ToSourceKey)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToImmutableArray();
        var exceptions = BuildExceptions(definition, steps, activeKeys, connection,
            prior?.Exceptions ?? [], now, eventDrafts);
        var firedEdges = definition.Edges
            .Where(edge => firedTransitions.Contains(edge.FromSourceKey) || firedTransitions.Contains(edge.ToSourceKey))
            .Select(edge => edge.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        var edgeStates = definition.Edges
            .OrderBy(x => x.SourceKey, StringComparer.Ordinal)
            .Select(edge => new LineUpEdgeRuntimeState(
                edge.SourceKey,
                firedEdges.Contains(edge.SourceKey),
                firedEdges.Contains(edge.SourceKey) ? now : null))
            .ToImmutableArray();
        var snapshot = new LineUpRuntimeSnapshot(
            definition.BindingId,
            definition.PublishedRevisionId,
            definition.StationId,
            definition.SequenceSourceKey,
            definition.DefinitionRevision,
            snapshotRevision,
            now,
            connection,
            steps.ToImmutableArray(),
            transitions.ToImmutableArray(),
            conditions,
            edgeStates,
            exceptions,
            activeKeys,
            activeTransitionKeys);
        var delta = Delta(snapshot, prior, definitionChanged, previous?.Connection);
        return new(snapshot, delta, BuildEvents());

        ImmutableArray<LineUpRuntimeEvent> BuildEvents() =>
            eventDrafts.Select((draft, ordinal) => new LineUpRuntimeEvent(
                    definition.BindingId,
                    definition.SequenceSourceKey,
                    draft.OwnerSourceKey,
                    draft.EventType,
                    draft.PreviousState,
                    draft.CurrentState,
                    now,
                    draft.DurationMs,
                    draft.Quality,
                    draft.QualityCode,
                    definition.DefinitionRevision,
                    $"hsjm:{readResult.BindingRevision}",
                    snapshotRevision,
                    ordinal,
                    draft.DetailsJson))
                .ToImmutableArray();
    }

    // 2026-09-10：Degraded 诊断节流表（60s/绑定），点名非 Good tag。
    private static readonly ConcurrentDictionary<long, DateTimeOffset> DegradedLogAt = new();

    // 2026-09-13：互斥冲突留痕节流表（60s/绑定+顺控）——树上不显示，仅事件流+日志。
    private static readonly ConcurrentDictionary<(long BindingId, string SequenceKey), DateTimeOffset> ConflictLogAt = new();

    /// <summary>
    /// 2026-09-13：互斥组冲突检测——任一组内 ≥2 个活动步即冲突（S7 GRAPH 交替分支不变量被打破，
    /// 典型为 EXEC_BITS 脏读或顺控跑飞）。返回所有冲突步的 SourceKey（多组冲突合并，去重排序）。
    /// </summary>
    private static List<string> ExclusiveConflictKeys(
        LineUpRuntimeStateDefinition definition,
        IReadOnlyList<LineUpStepRuntimeState> steps)
    {
        var conflicts = new List<string>();
        if (definition.ExclusiveStepGroups.IsDefaultOrEmpty)
        {
            return conflicts;
        }
        var active = new HashSet<string>(
            steps.Where(IsActive).Select(x => x.SourceKey), StringComparer.Ordinal);
        if (active.Count < 2)
        {
            return conflicts;
        }
        foreach (var group in definition.ExclusiveStepGroups)
        {
            if (group.IsDefaultOrEmpty)
            {
                continue;
            }
            var hits = group.Where(active.Contains).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (hits.Length >= 2)
            {
                conflicts.AddRange(hits);
            }
        }
        return conflicts.Distinct(StringComparer.Ordinal).ToList();
    }

    private static LineUpConnectionHealth Connection(
        LineUpRuntimeReadResult readResult,
        LineUpConnectionHealth previous,
        DateTimeOffset now,
        ICollection<EventDraft> events,
        HashSet<string> activeOutgoingTransitionKeys)
    {
        // 2026-09-15：allGood 收窄——非活动步出边转移的条件位不参与 Degraded 判定
        //（用户需求：当前工步 S12 只看其出边 T14，别的转移 T26 的条件位 Bad 不算数据质量异常）。
        // StepActive/AutoMode/Interlock/Supervision 等角色仍全量必须 Good。
        var allGood = !readResult.Values.IsDefaultOrEmpty &&
                      readResult.Values.All(x => x.Quality == LineUpRuntimeQuality.Good ||
                          (x.Role == LineUpRuntimeVariableRole.TransitionCondition &&
                           activeOutgoingTransitionKeys is not null &&
                           !activeOutgoingTransitionKeys.Contains(x.OwnerSourceKey)));
        var transportFailed = readResult.Status == LineUpRuntimeReadStatus.Failed &&
                              readResult.Values.Any() &&
                              readResult.Values.All(x => x.ErrorCode is "PlcReadTimeout" or "PlcReadFailed" or "PlcReadEmpty");
        var status = transportFailed
            ? LineUpGatewayStatus.Offline
            // 2026-09-15：Online 判定放行 Partial——ValueReader 只要任一标签非 Good 就给 Partial，
            // 若该 Bad 是被豁免的非活动步出边条件位（allGood=true），不应因 Status!=Completed 打回 Degraded。
            // 未豁免角色的 Bad/Unknown 仍使 allGood=false → Degraded；Empty/Failed（goodCount==0）不在此列。
            : allGood && readResult.Status is LineUpRuntimeReadStatus.Completed
                            or LineUpRuntimeReadStatus.Partial
                ? LineUpGatewayStatus.Online
                : LineUpGatewayStatus.Degraded;
        var lastGood = allGood ? now : previous?.LastGoodSampleAtUtc;
        var errorCode = status == LineUpGatewayStatus.Online
            ? null
            : readResult.Values.Select(x => x.ErrorCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ??
              "PlcQualityDegraded";
        if (status == LineUpGatewayStatus.Offline && previous?.Status != LineUpGatewayStatus.Offline)
        {
            events.Add(new(LineUpRuntimeEventType.GatewayOffline, string.Empty,
                previous?.Status.ToString(), status.ToString(), null, null, errorCode, "{}"));
        }
        else if (status == LineUpGatewayStatus.Online && previous?.Status == LineUpGatewayStatus.Offline)
        {
            events.Add(new(LineUpRuntimeEventType.GatewayRecovered, string.Empty,
                previous.Status.ToString(), status.ToString(), null, null, null, "{}"));
        }

        return new(status, now, lastGood, errorCode);
    }

    private static LineUpStepRuntimeState EvaluateStep(
        LineUpRuntimeStepDefinition definition,
        LineUpStepRuntimeState previous,
        LineUpRuntimeValue[] matches,
        DateTimeOffset? previousEvaluatedAt,
        DateTimeOffset now,
        ICollection<EventDraft> events)
    {
        var value = matches?.Length == 1 ? matches[0] : null;
        var quality = value?.Quality ?? LineUpRuntimeQuality.Unknown;
        var qualityCode = matches is { Length: > 1 }
            ? "RuntimeStepValueDuplicate"
            : value?.ErrorCode ?? (value is null ? "RuntimeStepValueMissing" : null);
        var elapsed = previous is not null && previous.LastValidValue == true &&
                      previous.Status != LineUpStepRuntimeStatus.Unknown && previousEvaluatedAt.HasValue
            ? Math.Max(0, (long)(now - previousEvaluatedAt.Value).TotalMilliseconds)
            : 0;
        var duration = (previous?.ActiveDurationMs ?? 0) + elapsed;
        if (quality != LineUpRuntimeQuality.Good || value?.Value is not bool current)
        {
            // 2026-09-11 需求①：值读取不到 → 跳过本轮判断、保持上一状态与时长不变（画面不闪 Unknown），
            // 异常事件仍记录（每个坏读期只记一条 SignalBad，恢复时配对 SignalRecovered）。
            // 读值问题由连接层（Degraded）与 60s 节流诊断日志继续如实上报树上状态。
            // 无上一 Good 状态可保持时（首轮即坏）仍按 Unknown 上报。
            if (previous is not null && previous.Quality == LineUpRuntimeQuality.Good)
            {
                if (!previous.ReadBad)
                {
                    events.Add(new(LineUpRuntimeEventType.SignalBad, definition.SourceKey,
                        previous.Status.ToString(), previous.Status.ToString(), duration,
                        quality, qualityCode, "{}"));
                }
                return previous with { ReadBad = true };
            }
            return new(
                definition.SourceKey,
                definition.DisplayName,
                LineUpStepRuntimeStatus.Unknown,
                previous?.LastValidValue,
                quality,
                duration,
                previous?.Status == LineUpStepRuntimeStatus.Unknown ? previous.StateChangedAtUtc : now,
                previous?.LastGoodAtUtc,
                qualityCode);
        }

        if (previous is not null && previous.ReadBad)
        {
            events.Add(new(LineUpRuntimeEventType.SignalRecovered, definition.SourceKey,
                previous.Status.ToString(), current ? LineUpStepRuntimeStatus.Active.ToString() : LineUpStepRuntimeStatus.Idle.ToString(),
                duration, LineUpRuntimeQuality.Good, null, "{}"));
        }

        LineUpStepRuntimeStatus status;
        if (current)
        {
            if (previous?.LastValidValue != true) duration = 0;
            var maximum = ParseTiaDuration(definition.MaximumStepTime);
            var warning = ParseTiaDuration(definition.WarningTime);
            status = maximum.HasValue && duration >= maximum.Value.TotalMilliseconds
                ? LineUpStepRuntimeStatus.TimedOut
                : warning.HasValue && duration >= warning.Value.TotalMilliseconds
                    ? LineUpStepRuntimeStatus.Warning
                    : LineUpStepRuntimeStatus.Active;
        }
        else
        {
            status = previous?.LastValidValue == true || previous?.Status == LineUpStepRuntimeStatus.Completed
                ? LineUpStepRuntimeStatus.Completed
                : LineUpStepRuntimeStatus.Idle;
        }

        if (current && previous?.LastValidValue != true)
        {
            events.Add(new(LineUpRuntimeEventType.StepEntered, definition.SourceKey,
                previous?.Status.ToString(), status.ToString(), 0, LineUpRuntimeQuality.Good, null, "{}"));
        }
        if (!current && previous?.LastValidValue == true)
        {
            events.Add(new(LineUpRuntimeEventType.StepExited, definition.SourceKey,
                previous.Status.ToString(), status.ToString(), duration, LineUpRuntimeQuality.Good, null, "{}"));
        }
        if (status == LineUpStepRuntimeStatus.Warning && previous?.Status != LineUpStepRuntimeStatus.Warning)
        {
            events.Add(new(LineUpRuntimeEventType.StepWarning, definition.SourceKey,
                previous?.Status.ToString(), status.ToString(), duration, LineUpRuntimeQuality.Good, null, "{}"));
        }
        if (status == LineUpStepRuntimeStatus.TimedOut && previous?.Status != LineUpStepRuntimeStatus.TimedOut)
        {
            events.Add(new(LineUpRuntimeEventType.StepTimedOut, definition.SourceKey,
                previous?.Status.ToString(), status.ToString(), duration, LineUpRuntimeQuality.Good, null, "{}"));
        }
        if (previous?.Status == LineUpStepRuntimeStatus.TimedOut && status != LineUpStepRuntimeStatus.TimedOut)
        {
            events.Add(new(LineUpRuntimeEventType.TimeoutCleared, definition.SourceKey,
                previous.Status.ToString(), status.ToString(), duration, LineUpRuntimeQuality.Good, null, "{}"));
        }

        return new(
            definition.SourceKey,
            definition.DisplayName,
            status,
            current,
            LineUpRuntimeQuality.Good,
            duration,
            previous is not null && previous.Status == status ? previous.StateChangedAtUtc : now,
            now,
            null);
    }

    private static LineUpTransitionRuntimeState EvaluateTransition(
        LineUpRuntimeTransitionDefinition definition,
        LineUpTransitionRuntimeState previous,
        IReadOnlyDictionary<string, LineUpConditionRuntimeValue> values,
        DateTimeOffset now,
        ICollection<EventDraft> events)
    {
        var exactCoilKey = definition.ConditionCanonicalKeys is { Length: 1 }
            ? definition.ConditionCanonicalKeys[0]
            : null;
        var value = string.IsNullOrWhiteSpace(exactCoilKey)
            ? null
            : values.GetValueOrDefault(exactCoilKey);
        var condition = value?.State ?? LineUpTriState.Unknown;
        var quality = value?.Quality ?? LineUpRuntimeQuality.Unknown;
        var qualityCode = condition == LineUpTriState.Unknown
            ? value?.QualityCode ?? "TransitionConditionUnknown"
            : null;
        var status = condition switch
        {
            LineUpTriState.True => LineUpTransitionRuntimeStatus.Satisfied,
            LineUpTriState.False => LineUpTransitionRuntimeStatus.Unsatisfied,
            _ => LineUpTransitionRuntimeStatus.Unknown
        };
        // 2026-09-11 需求①：条件值读取不到（上一状态为 Good 而本轮质量非 Good）→
        // 跳过本轮判定、保持上一转移状态不变，仅记一次 SignalBad 事件（恢复时配对 SignalRecovered）。
        if (previous is not null && previous.Quality == LineUpRuntimeQuality.Good &&
            quality != LineUpRuntimeQuality.Good)
        {
            if (!previous.ReadBad)
            {
                events.Add(new(LineUpRuntimeEventType.SignalBad, definition.SourceKey,
                    previous.Status.ToString(), previous.Status.ToString(), null, quality, qualityCode, "{}"));
            }
            return previous with { ReadBad = true };
        }
        if (status == LineUpTransitionRuntimeStatus.Satisfied &&
            previous?.Condition != LineUpTriState.True)
        {
            events.Add(new(LineUpRuntimeEventType.TransitionSatisfied, definition.SourceKey,
                previous?.Status.ToString(), status.ToString(), null, quality, qualityCode, "{}"));
        }
        if (previous is not null && previous.ReadBad)
        {
            events.Add(new(LineUpRuntimeEventType.SignalRecovered, definition.SourceKey,
                previous.Status.ToString(), status.ToString(), null, quality, null, "{}"));
        }
        return new(
            definition.SourceKey,
            definition.DisplayName,
            status,
            condition,
            quality,
            previous is not null && previous.Status == status ? previous.StateChangedAtUtc : now,
            qualityCode);
    }

    private static HashSet<string> FindFiredTransitions(
        LineUpRuntimeStateDefinition definition,
        IReadOnlyDictionary<string, LineUpStepRuntimeState> priorSteps,
        IReadOnlyCollection<LineUpStepRuntimeState> currentSteps)
    {
        if (priorSteps.Count == 0) return [];
        var current = currentSteps.ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
        var fired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transition in definition.Transitions)
        {
            var upstream = definition.Edges
                .Where(x => x.ToSourceKey == transition.SourceKey)
                .Select(x => x.FromSourceKey)
                .ToArray();
            var downstream = definition.Edges
                .Where(x => x.FromSourceKey == transition.SourceKey)
                .Select(x => x.ToSourceKey)
                .ToArray();
            var upstreamExited = upstream.Any(key =>
                priorSteps.GetValueOrDefault(key)?.LastValidValue == true &&
                current.GetValueOrDefault(key)?.LastValidValue == false);
            var downstreamEntered = downstream.Any(key =>
                priorSteps.GetValueOrDefault(key)?.LastValidValue != true &&
                current.GetValueOrDefault(key)?.LastValidValue == true);
            if (upstreamExited && downstreamEntered) fired.Add(transition.SourceKey);
        }
        return fired;
    }

    private static void ApplyWaitingStates(
        LineUpRuntimeStateDefinition definition,
        IReadOnlyCollection<LineUpTransitionRuntimeState> transitions,
        IList<LineUpStepRuntimeState> steps,
        DateTimeOffset now)
    {
        var transitionByKey = transitions.ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Status != LineUpStepRuntimeStatus.Active) continue;
            var outgoing = definition.Edges
                .Where(x => x.FromSourceKey == step.SourceKey)
                .Select(x => transitionByKey.GetValueOrDefault(x.ToSourceKey))
                .Where(x => x is not null)
                .ToArray();
            if (outgoing.Length > 0 && outgoing.All(x => x.Status == LineUpTransitionRuntimeStatus.Unsatisfied))
            {
                steps[index] = step with { Status = LineUpStepRuntimeStatus.Waiting, StateChangedAtUtc = now };
            }
        }
    }

    private static ImmutableArray<LineUpRuntimeExceptionState> BuildExceptions(
        LineUpRuntimeStateDefinition definition,
        IReadOnlyCollection<LineUpStepRuntimeState> steps,
        ImmutableArray<string> activeKeys,
        LineUpConnectionHealth connection,
        IReadOnlyCollection<LineUpRuntimeExceptionState> previousExceptions,
        DateTimeOffset now,
        ICollection<EventDraft> events)
    {
        var exceptions = steps
            .Where(x => x.Status == LineUpStepRuntimeStatus.TimedOut)
            // 2026-09-11 语义调整：步超时降为 Info 级——TIA GRAPH 导出 XML 的步最大时间普遍是模板默认值
            // （FB261 全部 34 步齐刷刷 T#10S/T#7S，非工程师逐个组态的监护），设备待机停在回原点步是正常工况。
            // 步超时不再进异常（Error）判定，也不占告警（Warning）色；树上保持健康态、图上步用淡色标注，
            // 明细仍可见（异常定位页签 + 事件流）。真正的异常留给：读值 Bad / 互斥步冲突 / Degraded。
            .Select(x => new LineUpRuntimeExceptionState(
                "StepTimedOut", x.SourceKey, "Info", "The GRAPH step exceeded its configured maximum time.", now))
            .ToList();
        // 2026-09-13：互斥冲突不再进 Exceptions（用户拍板：树上不显示）。冲突的处置统一在 Evaluate
        // 前部的短路块——采集作废、状态冻结、留痕走 60s 节流事件+日志。此处保留空组循环仅为防御
        // （短路未覆盖的路径也不再向树上挂 Error）。
        if (connection.Status == LineUpGatewayStatus.Offline)
        {
            exceptions.Add(new(
                "GatewayOffline", string.Empty, "Warning", "PLC runtime values are currently unavailable.", now));
        }
        return exceptions.OrderBy(x => x.Code, StringComparer.Ordinal)
            .ThenBy(x => x.OwnerSourceKey, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static LineUpRuntimeDelta Delta(
        LineUpRuntimeSnapshot snapshot,
        LineUpRuntimeSnapshot previous,
        bool definitionChanged,
        LineUpConnectionHealth previousConnection)
    {
        var previousSteps = (previous?.Steps ?? []).ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
        var previousTransitions = (previous?.Transitions ?? []).ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
        var previousConditions = (previous?.Conditions ?? []).ToDictionary(x => x.CanonicalKey, StringComparer.Ordinal);
        var previousEdges = (previous?.Edges ?? []).ToDictionary(x => x.SourceKey, StringComparer.Ordinal);
        return new(
            snapshot.BindingId,
            snapshot.PublishedRevisionId,
            snapshot.StationId,
            snapshot.SequenceSourceKey,
            snapshot.DefinitionRevision,
            previous?.SnapshotRevision ?? 0,
            snapshot.SnapshotRevision,
            snapshot.EvaluatedAtUtc,
            snapshot.Connection,
            snapshot.Steps.Where(x => !Equals(previousSteps.GetValueOrDefault(x.SourceKey), x)).ToImmutableArray(),
            snapshot.Transitions.Where(x => !Equals(previousTransitions.GetValueOrDefault(x.SourceKey), x)).ToImmutableArray(),
            snapshot.Conditions.Where(x => !Equals(previousConditions.GetValueOrDefault(x.CanonicalKey), x)).ToImmutableArray(),
            snapshot.Edges.Where(x => !Equals(previousEdges.GetValueOrDefault(x.SourceKey), x)).ToImmutableArray(),
            snapshot.Exceptions,
            definitionChanged || snapshot.Connection.Status != LineUpGatewayStatus.Online ||
            previousConnection is not null && previousConnection.Status != LineUpGatewayStatus.Online);
    }

    private static bool IsActive(LineUpStepRuntimeState state) =>
        state.LastValidValue == true && state.Status is
            LineUpStepRuntimeStatus.Active or
            LineUpStepRuntimeStatus.Waiting or
            LineUpStepRuntimeStatus.Warning or
            LineUpStepRuntimeStatus.TimedOut;

    private static LineUpConditionRuntimeValue ToCondition(LineUpRuntimeLogicPartEvaluation value) =>
        new(
            value.CanonicalKey,
            value.OwnerSourceKey,
            value.NetworkSourceKey,
            value.PartSourceKey,
            value.State,
            value.Quality,
            value.TypedValue,
            value.SourceTimestamp,
            value.QualityCode,
            value.VariablePath);

    internal static TimeSpan? ParseTiaDuration(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = TiaDuration.Match(value.Trim());
        if (!match.Success || !match.Groups.Cast<Group>().Skip(1).Any(x => x.Success)) return null;
        try
        {
            return TimeSpan.FromHours(Number(match, "h")) +
                   TimeSpan.FromMinutes(Number(match, "m")) +
                   TimeSpan.FromSeconds(Number(match, "s")) +
                   TimeSpan.FromMilliseconds(Number(match, "ms"));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static double Number(Match match, string group) =>
        match.Groups[group].Success && double.TryParse(match.Groups[group].Value, out var number)
            ? number
            : 0;

    private sealed record EventDraft(
        LineUpRuntimeEventType EventType,
        string OwnerSourceKey,
        string PreviousState,
        string CurrentState,
        long? DurationMs,
        LineUpRuntimeQuality? Quality,
        string QualityCode,
        string DetailsJson);
}
