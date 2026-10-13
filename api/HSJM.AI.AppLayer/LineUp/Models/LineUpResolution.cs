using System.Collections.Generic;

namespace HSJM.AI.AppLayer.LineUp.Models;

public enum LineUpIssueSeverity
{
    Info,
    Warning,
    Blocking
}

public enum LineUpConfidenceLevel
{
    Automatic,
    Suggested,
    NeedsReview
}

public enum LineUpHierarchyCandidateKind
{
    ExistingBinding,
    ExistingSubStation,
    ProposedSubStation,
    StationCandidate
}

public sealed record LineUpResolutionIssue(
    LineUpIssueSeverity Severity,
    string IssueType,
    string? SourceKey,
    string Message);

public sealed record LineUpHierarchyCandidate(
    string CandidateKey,
    LineUpHierarchyCandidateKind CandidateKind,
    string SequenceSourceKey,
    long ProcessId,
    long StationId,
    long? SubStationId,
    string? ProposedSubStationCode,
    string? ProposedSubStationName,
    LineUpConfidenceLevel ConfidenceLevel,
    IReadOnlyList<string> EvidenceCodes)
{
    public string? ProcessCode { get; init; }
    public string? ProcessName { get; init; }
    public string? StationCode { get; init; }
    public string? StationName { get; init; }
    public string? SubStationCode { get; init; }
    public string? SubStationName { get; init; }
}

public sealed record LineUpHierarchySequenceResolution(
    string SequenceSourceKey,
    LineUpConfidenceLevel ConfidenceLevel,
    long? ProcessId,
    long? StationId,
    long? SubStationId,
    string? SelectedCandidateKey,
    IReadOnlyList<LineUpHierarchyCandidate> Candidates);

public sealed record LineUpHierarchyResolution(
    long LineId,
    long DeviceId,
    long? ResolvedPlcProjectId,
    bool CanCommitStructure,
    IReadOnlyList<LineUpHierarchySequenceResolution> Sequences,
    IReadOnlyList<LineUpResolutionIssue> Issues);

public enum LineUpStructuralStatus
{
    Ready,
    Blocked
}

public enum LineUpBindingStatus
{
    Unresolved,
    NeedsReview,
    Resolved
}

public sealed record LineUpAddressCandidate(
    string CandidateKey,
    string CandidateKind,
    long Id,
    string Name,
    IReadOnlyList<string> EvidenceCodes);

public sealed record LineUpSequenceAddressResolution(
    string SequenceSourceKey,
    LineUpBindingStatus BindingStatus);

public sealed record LineUpAddressResolution(
    long LineId,
    long DeviceId,
    long? PlcProjectId,
    string? PlcProjectName,
    long? GraphBlockId,
    string? GraphBlockName,
    long? InstanceDbBlockId,
    string? InstanceDbBlockName,
    LineUpStructuralStatus StructuralStatus,
    bool CanCommitStructure,
    bool CanCommitForMonitoring,
    IReadOnlyList<LineUpSequenceAddressResolution> Sequences,
    IReadOnlyList<LineUpAddressCandidate> Candidates,
    IReadOnlyList<LineUpResolutionIssue> Issues);

public enum LineUpAddressMode
{
    Unknown,
    Standard,
    Optimized
}

public sealed record LineUpVariableAddressResolution(
    long PlcProjectId,
    long InstanceDbBlockId,
    long? InterfaceId,
    string? VariablePath,
    string? DataType,
    decimal? OffsetByte,
    decimal? OffsetBit,
    decimal? SizeBits,
    string? Offset,
    string? CanonicalSymbolicPath,
    LineUpAddressMode AddressMode,
    string? CanonicalNumericAddress,
    LineUpBindingStatus BindingStatus,
    IReadOnlyList<LineUpResolutionIssue> Issues);
