using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public readonly record struct LineUpMonitorKey(long BindingId, string SequenceSourceKey)
{
    public bool IsValid => BindingId > 0 && !string.IsNullOrWhiteSpace(SequenceSourceKey);
}

/// <summary>
/// 顺控运行态摘要。RuntimeStatus 取值与导航一致（Error/Online/Warning/Enabled，
/// 2026-09-11 B+C 分层：步超时 Info 级不参与，网关断线类 Warning，读值 Bad/互斥步 Error），
/// 无实时快照（监控未启用/未轮询）时为空字符串，前端不显示状态点。
/// </summary>
public sealed record LineUpMonitorSequenceSummaryDto(
    string SequenceSourceKey,
    string SequenceDisplayName,
    int StepCount,
    int TransitionCount,
    string RuntimeStatus);

public sealed record LineUpMonitorBindingSummaryDto(
    string BindingId,
    string GraphAssetId,
    string PublishedRevisionId,
    string LineId,
    string DeviceId,
    string ProcessId,
    string StationId,
    string SubStationId,
    string PlcProjectId,
    string GraphSourceName,
    string BindingStatus,
    bool MonitoringEnabled,
    bool RuntimeBindingReady,
    ImmutableArray<LineUpMonitorSequenceSummaryDto> Sequences);

public sealed record LineUpMonitorDefinitionDto(
    string BindingId,
    string PublishedRevisionId,
    string StationId,
    string SequenceSourceKey,
    string SequenceDisplayName,
    string DefinitionRevision,
    ImmutableArray<LineUpRuntimeStepDefinition> Steps,
    ImmutableArray<LineUpRuntimeTransitionDefinition> Transitions,
    ImmutableArray<LineUpRuntimeEdgeDefinition> Edges,
    LineUpMonitorViewerDefinition Viewer);

public sealed record LineUpMonitorSnapshotDto(
    string BindingId,
    string PublishedRevisionId,
    string StationId,
    string SequenceSourceKey,
    string DefinitionRevision,
    string SnapshotRevision,
    string EvaluatedAtUtc,
    LineUpMonitorConnectionDto Connection,
    ImmutableArray<LineUpMonitorStepStateDto> Steps,
    ImmutableArray<LineUpMonitorTransitionStateDto> Transitions,
    ImmutableArray<LineUpMonitorConditionValueDto> Conditions,
    ImmutableArray<LineUpMonitorEdgeStateDto> Edges,
    ImmutableArray<LineUpMonitorExceptionDto> Exceptions,
    // 2026-09-15：当前活动步出边转移（异常/在线判定收窄范围），供前端高亮与排查。
    ImmutableArray<string> ActiveStepSourceKeys,
    ImmutableArray<string> ActiveTransitionSourceKeys);

public sealed record LineUpMonitorDeltaDto(
    string BindingId,
    string PublishedRevisionId,
    string StationId,
    string SequenceSourceKey,
    string DefinitionRevision,
    string FromSnapshotRevision,
    string ToSnapshotRevision,
    string ServerTimestamp,
    LineUpMonitorConnectionDto Connection,
    ImmutableArray<LineUpMonitorStepStateDto> ChangedSteps,
    ImmutableArray<LineUpMonitorTransitionStateDto> ChangedTransitions,
    ImmutableArray<LineUpMonitorConditionValueDto> ChangedConditions,
    ImmutableArray<LineUpMonitorEdgeStateDto> ChangedEdges,
    ImmutableArray<LineUpMonitorExceptionDto> Exceptions);

public sealed record LineUpMonitorConnectionDto(
    string Status,
    string EvaluatedAtUtc,
    string LastGoodSampleAtUtc,
    string ErrorCode);

public sealed record LineUpMonitorStepStateDto(
    string SourceKey,
    string DisplayName,
    string Status,
    bool? LastValidValue,
    string Quality,
    string ActiveDurationMs,
    string StateChangedAtUtc,
    string LastGoodAtUtc,
    string QualityCode);

public sealed record LineUpMonitorTransitionStateDto(
    string SourceKey,
    string DisplayName,
    string Status,
    string Condition,
    string Quality,
    string StateChangedAtUtc,
    string QualityCode);

public sealed record LineUpMonitorConditionValueDto(
    string CanonicalKey,
    string OwnerSourceKey,
    string NetworkSourceKey,
    string PartSourceKey,
    object Value,
    string State,
    string Quality,
    string SourceTimestamp,
    string QualityCode,
    string VariablePath);

public sealed record LineUpMonitorEdgeStateDto(
    string SourceKey,
    bool WasTraversed,
    string TraversedAtUtc);

public sealed record LineUpMonitorExceptionDto(
    string Code,
    string OwnerSourceKey,
    string Severity,
    string SafeMessage,
    string OccurredAtUtc);

public sealed record LineUpMonitorEventDto(
    string Id,
    string BindingId,
    string SequenceSourceKey,
    string OwnerSourceKey,
    string EventType,
    string PreviousState,
    string CurrentState,
    string OccurredAtUtc,
    string DurationMs,
    string Quality,
    string QualityCode,
    string DefinitionRevision,
    string GatewayStreamId,
    string GatewaySequence,
    int EventOrdinal,
    string DetailsJson);

public sealed record LineUpMonitorEventPageDto(
    ImmutableArray<LineUpMonitorEventDto> Items,
    string NextBeforeEventId,
    bool HasMore);

public static class LineUpMonitorWireProjection
{
    public static LineUpMonitorDefinitionDto Definition(LineUpMonitorDefinitionBundle definition) =>
        new(
            definition.Runtime.BindingId.ToString(),
            definition.Runtime.PublishedRevisionId.ToString(),
            definition.Runtime.StationId.ToString(),
            definition.Runtime.SequenceSourceKey,
            definition.Runtime.SequenceDisplayName,
            definition.Runtime.DefinitionRevision,
            definition.Runtime.Steps,
            definition.Runtime.Transitions,
            definition.Runtime.Edges,
            definition.Viewer);

    public static LineUpMonitorSnapshotDto Snapshot(LineUpRuntimeSnapshot snapshot) =>
        new(
            snapshot.BindingId.ToString(),
            snapshot.PublishedRevisionId.ToString(),
            snapshot.StationId.ToString(),
            snapshot.SequenceSourceKey,
            snapshot.DefinitionRevision,
            snapshot.SnapshotRevision.ToString(),
            UtcTimestamp(snapshot.EvaluatedAtUtc),
            Connection(snapshot.Connection),
            snapshot.Steps.Select(Step).ToImmutableArray(),
            snapshot.Transitions.Select(Transition).ToImmutableArray(),
            snapshot.Conditions.Select(Condition).ToImmutableArray(),
            snapshot.Edges.Select(Edge).ToImmutableArray(),
            snapshot.Exceptions.Select(Exception).ToImmutableArray(),
            snapshot.ActiveStepSourceKeys,
            snapshot.ActiveTransitionSourceKeys);

    public static LineUpMonitorDeltaDto Delta(LineUpRuntimeDelta delta) =>
        new(
            delta.BindingId.ToString(),
            delta.PublishedRevisionId.ToString(),
            delta.StationId.ToString(),
            delta.SequenceSourceKey,
            delta.DefinitionRevision,
            delta.FromSnapshotRevision.ToString(),
            delta.ToSnapshotRevision.ToString(),
            UtcTimestamp(delta.ServerTimestamp),
            Connection(delta.Connection),
            delta.ChangedSteps.Select(Step).ToImmutableArray(),
            delta.ChangedTransitions.Select(Transition).ToImmutableArray(),
            delta.ChangedConditions.Select(Condition).ToImmutableArray(),
            delta.ChangedEdges.Select(Edge).ToImmutableArray(),
            delta.Exceptions.Select(Exception).ToImmutableArray());

    public static string UtcTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static string DecimalInteger(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    public static string DecimalInteger(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static LineUpMonitorConnectionDto Connection(LineUpConnectionHealth value) =>
        new(
            value.Status.ToString(),
            UtcTimestamp(value.EvaluatedAtUtc),
            Timestamp(value.LastGoodSampleAtUtc),
            Safe(value.ErrorCode));

    private static LineUpMonitorStepStateDto Step(LineUpStepRuntimeState value) =>
        new(
            Safe(value.SourceKey),
            Safe(value.DisplayName),
            value.Status.ToString(),
            value.LastValidValue,
            value.Quality.ToString(),
            DecimalInteger(value.ActiveDurationMs),
            UtcTimestamp(value.StateChangedAtUtc),
            Timestamp(value.LastGoodAtUtc),
            Safe(value.QualityCode));

    private static LineUpMonitorTransitionStateDto Transition(LineUpTransitionRuntimeState value) =>
        new(
            Safe(value.SourceKey),
            Safe(value.DisplayName),
            value.Status.ToString(),
            value.Condition.ToString(),
            value.Quality.ToString(),
            UtcTimestamp(value.StateChangedAtUtc),
            Safe(value.QualityCode));

    private static LineUpMonitorConditionValueDto Condition(LineUpConditionRuntimeValue value)
    {
        var projected = BrowserValue(value.Value);
        return new(
            Safe(value.CanonicalKey),
            Safe(value.OwnerSourceKey),
            Safe(value.NetworkSourceKey),
            Safe(value.PartSourceKey),
            projected.Supported ? projected.Value : null,
            projected.Supported ? value.State.ToString() : LineUpTriState.Unknown.ToString(),
            projected.Supported ? value.Quality.ToString() : LineUpRuntimeQuality.Bad.ToString(),
            Timestamp(value.SourceTimestamp),
            projected.Supported ? Safe(value.QualityCode) : "PlcValueTypeUnsupported",
            Safe(value.VariablePath));
    }

    private static LineUpMonitorEdgeStateDto Edge(LineUpEdgeRuntimeState value) =>
        new(Safe(value.SourceKey), value.WasTraversed, Timestamp(value.TraversedAtUtc));

    private static LineUpMonitorExceptionDto Exception(LineUpRuntimeExceptionState value) =>
        new(
            Safe(value.Code),
            Safe(value.OwnerSourceKey),
            Safe(value.Severity),
            Safe(value.SafeMessage),
            UtcTimestamp(value.OccurredAtUtc));

    private static BrowserWireValue BrowserValue(object value)
    {
        if (value is null) return new(true, null);
        return value switch
        {
            bool boolean => new(true, boolean),
            sbyte integer => new(true, integer.ToString(CultureInfo.InvariantCulture)),
            byte integer => new(true, integer.ToString(CultureInfo.InvariantCulture)),
            short integer => new(true, integer.ToString(CultureInfo.InvariantCulture)),
            ushort integer => new(true, integer.ToString(CultureInfo.InvariantCulture)),
            int integer => new(true, integer.ToString(CultureInfo.InvariantCulture)),
            uint integer => new(true, integer.ToString(CultureInfo.InvariantCulture)),
            long integer => new(true, integer.ToString(CultureInfo.InvariantCulture)),
            ulong integer => new(true, integer.ToString(CultureInfo.InvariantCulture)),
            float number when float.IsFinite(number) => new(true, number),
            double number when double.IsFinite(number) => new(true, number),
            decimal number => new(true, number),
            TimeSpan duration => new(true, duration.ToString("c", CultureInfo.InvariantCulture)),
            DateTimeOffset timestamp => new(true, UtcTimestamp(timestamp)),
            DateTime timestamp => new(true, UtcTimestamp(ToUtc(timestamp))),
            char character => new(true, character.ToString()),
            string text => new(true, text),
            _ => new(false, null)
        };
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new(utc, TimeSpan.Zero);
    }

    private static string Timestamp(DateTimeOffset? value) =>
        value.HasValue ? UtcTimestamp(value.Value) : null;

    private static string Safe(string value) => value ?? string.Empty;

    private readonly record struct BrowserWireValue(bool Supported, object Value);
}
