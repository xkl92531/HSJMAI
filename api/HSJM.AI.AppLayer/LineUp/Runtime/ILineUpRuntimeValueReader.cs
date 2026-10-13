using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public interface ILineUpRuntimeValueReader
{
    Task<LineUpRuntimeReadResult> ReadAsync(
        LineUpRuntimeReadPlan plan,
        CancellationToken cancellationToken);
}

public enum LineUpRuntimeVariableRole
{
    StepActive,
    TransitionCondition,
    AutoMode,
    Interlock,
    Supervision
}

public enum LineUpRuntimeAddressMode
{
    StandardDb,
    OptimizedSymbolic
}

public enum LineUpRuntimeQuality
{
    Good,
    Bad,
    Unknown,
    Stale
}

public enum LineUpRuntimeValueKind
{
    Null,
    Boolean,
    Integer,
    FloatingPoint,
    Text,
    DateTime,
    Duration
}

public enum LineUpRuntimeReadStatus
{
    Empty,
    Completed,
    Partial,
    Failed
}

public sealed record LineUpRuntimeSourceVariable(
    string OwnerSourceKey,
    string NetworkSourceKey,
    string PartSourceKey,
    string DisplayName,
    LineUpRuntimeVariableRole Role,
    int? StepNumber,
    long? ResolvedBlockId,
    string ResolvedBlockName,
    long? ResolvedBlockInterfaceId,
    string VariablePath,
    string CanonicalPlcPath,
    string DataType,
    string NumericAddress,
    bool IsResolved)
{
    public string SourceKey => PartSourceKey;
}

public sealed record LineUpReadPlanCompileRequest(
    long BindingId,
    string BindingRevision,
    long PlcProjectId,
    string InstanceDbName,
    LineUpRuntimeAddressMode AddressMode,
    IReadOnlyList<LineUpRuntimeSourceVariable> Sources);

public sealed record LineUpReadPlanIssue(
    string Code,
    string SafeMessage,
    string SourceKey);

public sealed record LineUpRuntimeReadItem(
    string CanonicalKey,
    string OwnerSourceKey,
    string NetworkSourceKey,
    string PartSourceKey,
    LineUpRuntimeVariableRole Role,
    long ResolvedBlockId,
    string ResolvedBlockName,
    long ResolvedBlockInterfaceId,
    string VariablePath,
    string CanonicalPlcPath,
    string DataType,
    string NumericAddress,
    PLC_ExactReadRequest ExactReadRequest)
{
    public string SourceKey => PartSourceKey;

    public LineUpRuntimeReadItem(
        string canonicalKey,
        string sourceKey,
        LineUpRuntimeVariableRole role,
        long blockInterfaceId,
        string canonicalSymbolicPath,
        string tagName,
        string dataType,
        string numericAddress)
        : this(
            canonicalKey,
            sourceKey,
            sourceKey,
            sourceKey,
            role,
            blockInterfaceId,
            LegacyBlockName(canonicalSymbolicPath),
            blockInterfaceId,
            tagName,
            canonicalSymbolicPath,
            dataType,
            numericAddress,
            new(canonicalKey, LegacyBlockName(canonicalSymbolicPath), tagName))
    {
    }

    private static string LegacyBlockName(string canonicalSymbolicPath)
    {
        var separator = canonicalSymbolicPath?.IndexOf('.') ?? -1;
        return separator > 0 ? canonicalSymbolicPath[..separator] : string.Empty;
    }
}

public sealed record LineUpRuntimeReadPlan(
    long BindingId,
    string BindingRevision,
    long PlcProjectId,
    ImmutableArray<LineUpRuntimeReadItem> Items,
    ImmutableArray<PLC_ExactReadRequest> ExactReadRequests,
    TimeSpan MaxSampleAge)
{
    public static LineUpRuntimeReadPlan Create(
        long bindingId,
        string bindingRevision,
        long plcProjectId,
        IEnumerable<LineUpRuntimeReadItem> items,
        TimeSpan maxSampleAge)
    {
        var immutableItems = items?.ToImmutableArray() ?? [];
        var exactRequests = immutableItems
            .Select(x => x.ExactReadRequest)
            .Where(x => x is not null)
            .ToImmutableArray();
        return new(
            bindingId,
            bindingRevision?.Trim() ?? string.Empty,
            plcProjectId,
            immutableItems,
            exactRequests,
            maxSampleAge > TimeSpan.Zero ? maxSampleAge : TimeSpan.FromSeconds(5));
    }
}

public sealed record LineUpReadPlanCompilation(
    LineUpRuntimeReadPlan Plan,
    ImmutableArray<LineUpReadPlanIssue> Issues);

public sealed record LineUpRuntimeValue(
    string CanonicalKey,
    string OwnerSourceKey,
    string NetworkSourceKey,
    string PartSourceKey,
    LineUpRuntimeVariableRole Role,
    object Value,
    LineUpRuntimeValueKind ValueKind,
    LineUpRuntimeQuality Quality,
    DateTimeOffset? SourceTimestamp,
    DateTimeOffset ReceivedTimestamp,
    string ErrorCode)
{
    public string SourceKey => PartSourceKey;

    public LineUpRuntimeValue(
        string canonicalKey,
        string sourceKey,
        LineUpRuntimeVariableRole role,
        object value,
        LineUpRuntimeValueKind valueKind,
        LineUpRuntimeQuality quality,
        DateTimeOffset? sourceTimestamp,
        DateTimeOffset receivedTimestamp,
        string errorCode)
        : this(
            canonicalKey,
            sourceKey,
            sourceKey,
            sourceKey,
            role,
            value,
            valueKind,
            quality,
            sourceTimestamp,
            receivedTimestamp,
            errorCode)
    {
    }
}

public sealed record LineUpRuntimeReadResult(
    long BindingId,
    string BindingRevision,
    long PlcProjectId,
    LineUpRuntimeReadStatus Status,
    ImmutableArray<LineUpRuntimeValue> Values,
    DateTimeOffset ReceivedTimestamp);
