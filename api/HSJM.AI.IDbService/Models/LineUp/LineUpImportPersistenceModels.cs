namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpImportBatchCreate(
    long BatchId,
    string StorageKey,
    string SourceFileName,
    string SourceFileHash,
    long LineId,
    long DeviceId);

public sealed record LineUpImportBatchData(
    long BatchId,
    string StorageKey,
    string SourceFileName,
    string SourceFileHash,
    long LineId,
    long DeviceId,
    long? PlcProjectId,
    string Status,
    string? ErrorSummary,
    string? ParsingLeaseToken,
    DateTime? ParsingLeaseExpiresAt,
    int PreviewSchemaVersion,
    int PreviewRevision,
    string? PreviewHash,
    string? PreviewJson,
    string? SummaryJson,
    string? CommitRequestHash,
    string? CommitReceiptJson,
    string? CommitMode,
    DateTime? CommittedAt);

public sealed record LineUpImportIssueWrite(
    string Severity,
    string IssueType,
    string? SourceKey,
    string Message,
    string? ResolutionJson = null);

public sealed record LineUpImportPreviewCompletion(
    long BatchId,
    string LeaseToken,
    int PreviewSchemaVersion,
    string PreviewJson,
    string PreviewHash,
    string SummaryJson,
    long? PlcProjectId,
    IReadOnlyList<LineUpImportIssueWrite> Issues);

public sealed record LineUpExistingGraphState(
    string StateHash,
    IReadOnlyList<LineUpExistingSequenceState> Sequences,
    IReadOnlyList<LineUpExistingNodeState> Nodes,
    IReadOnlyList<LineUpExistingActionState> Actions,
    IReadOnlyList<LineUpExistingEdgeState> Edges,
    IReadOnlyList<LineUpExistingBindingState> Bindings,
    // owner 指向的 PLC 块已软删/不存在的序列集合（项目重传替换块后遗留）：
    // 这些序列的 source key 与新块语义上是同一张图的新版本，预览/提交应允许接管而非判冲突。
    IReadOnlyCollection<long>? DeadOwnerGraphBlockIds = null);

public sealed record LineUpExistingSequenceState(
    long Id,
    long PlcProjectId,
    long GraphBlockId,
    string SourceKey,
    string DisplayName,
    string? LayoutJson);

public sealed record LineUpExistingNodeState(
    long Id,
    long SequenceId,
    string NodeKind,
    string SourceKey,
    string DisplayName);

public sealed record LineUpExistingActionState(
    long Id,
    long StepId,
    string SourceKey,
    string DisplayName);

public sealed record LineUpExistingEdgeState(long Id, long SequenceId, string SourceKey);

public sealed record LineUpExistingBindingState(
    long Id,
    long LineId,
    long DeviceId,
    long SequenceId,
    long PlcProjectId,
    long GraphBlockId,
    string BindingStatus);

/// <summary>commit 落库后的 GRAPH 资产与最新修订（供监控绑定编排定位）。</summary>
public sealed record LineUpCommittedAssetRevision(
    long GraphAssetId,
    long LatestRevisionId,
    long PlcProjectId,
    long GraphBlockId,
    long? InstanceDbBlockId);

/// <summary>为 commit 产物确保一条运行时绑定（AI_LineUp_GraphStationBinding）并指向新发布修订。</summary>
public sealed record LineUpGraphStationBindingEnsure(
    long GraphAssetId,
    long RevisionId,
    long LineId,
    long ProcessId,
    long StationId,
    long SubStationId,
    long DeviceId,
    long? InstanceDbBlockId);

/// <summary>既有结构绑定行（AI_LineUp_SubStationGraphBinding）按序列 SourceKey 定位结果，供层级改派。</summary>
public sealed record LineUpActiveSubStationBindingLookup(
        long BindingId,
        string SequenceSourceKey,
        long ProcessId,
        long StationId,
        long SubStationId);

public sealed record LineUpCommitPersistencePlan(
    long BatchId,
    long LineId,
    long DeviceId,
    int ExpectedPreviewSchemaVersion,
    int ExpectedPreviewRevision,
    string ExpectedPreviewHash,
    string ExpectedGraphStateHash,
    string DiffToken,
    string CommitMode,
    string CommitRequestHash,
    string CommitReceiptJson,
    DateTime CommittedAt,
    IReadOnlyList<LineUpCommitDocumentWrite> Documents);

public sealed record LineUpCommitDocumentWrite(
    long PlcProjectId,
    long GraphBlockId,
    long? InstanceDbBlockId,
    string SourceHash,
    IReadOnlyList<LineUpCommitSequenceWrite> Sequences);

public sealed record LineUpCommitSequenceWrite(
    string SourceKey,
    string SourceName,
    string InitialDisplayName,
    string? RequestedDisplayName,
    string BindingStatus,
    long ProcessId,
    long StationId,
    long? SubStationId,
    string CandidateKind,
    string? ProposedSubStationCode,
    string? ProposedSubStationName,
    IReadOnlyList<LineUpCommitNodeWrite> Nodes,
    IReadOnlyList<LineUpCommitActionWrite> Actions,
    IReadOnlyList<LineUpCommitEdgeWrite> Edges);

public sealed record LineUpCommitNodeWrite(
    string NodeKind,
    string SourceKey,
    string SourceName,
    string InitialDisplayName,
    string? RequestedDisplayName,
    int Number,
    bool IsInitial,
    bool IsFinal,
    string MaximumStepTime,
    string WarningTime,
    string Comment,
    string ProgrammingLanguage,
    bool IsMissing,
    bool IsJump);

public sealed record LineUpCommitActionWrite(
    string SourceKey,
    string StepSourceKey,
    string SourceName,
    string InitialDisplayName,
    string? RequestedDisplayName,
    int Ordinal,
    string Qualifier,
    IReadOnlyList<string> Tokens,
    string ActionText);

public sealed record LineUpCommitEdgeWrite(
    string SourceKey,
    string FromSourceKey,
    string ToSourceKey,
    string FromNodeKind,
    string ToNodeKind,
    string EdgeKind,
    int? BranchOrder);

public sealed record LineUpCommitPersistenceOutcome(bool IsIdempotentReplay, string CommitReceiptJson);
