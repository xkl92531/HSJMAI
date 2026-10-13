using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpImportRepository
{
    Task CreateBatchAsync(LineUpImportBatchCreate request, CancellationToken cancellationToken);

    Task<bool> FailUploadAsync(long batchId, string safeErrorCode, CancellationToken cancellationToken);

    Task<LineUpImportBatchData?> GetBatchAsync(long batchId, CancellationToken cancellationToken);

    /// <summary>提交被拒时把错误码与漂移明细写进批次 ErrorSummary（诊断通道，text 列不限长）。</summary>
    Task WriteCommitRejectionAsync(long batchId, string code, string detail, CancellationToken cancellationToken);

    Task<LineUpImportBatchData?> TryAcquireParsingLeaseAsync(
        long batchId,
        string leaseToken,
        DateTime now,
        DateTime expiresAt,
        CancellationToken cancellationToken);

    Task<bool> CompletePreviewAsync(
        LineUpImportPreviewCompletion completion,
        CancellationToken cancellationToken);

    Task<bool> FailParsingAsync(
        long batchId,
        string leaseToken,
        string safeErrorCode,
        CancellationToken cancellationToken);

    Task<LineUpExistingGraphState> LoadExistingGraphStateAsync(
        IReadOnlyCollection<long> plcProjectIds,
        IReadOnlyCollection<long> graphBlockIds,
        IReadOnlyCollection<string> incomingSequenceSourceKeys,
        CancellationToken cancellationToken);

    Task<LineUpCommitPersistenceOutcome> CommitAsync(
        LineUpCommitPersistencePlan plan,
        CancellationToken cancellationToken);

    Task<LineUpCommittedAssetRevision?> LoadCommittedAssetRevisionAsync(
        long plcProjectId,
        long graphBlockId,
        CancellationToken cancellationToken);

    Task<long> EnsureGraphStationBindingAsync(
        LineUpGraphStationBindingEnsure request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LineUpActiveSubStationBindingLookup>> LoadActiveSubStationBindingsAsync(
        long lineId,
        long deviceId,
        IReadOnlyCollection<string> sequenceSourceKeys,
        CancellationToken cancellationToken);

    /// <summary>
    /// 改派层级归属。hierarchyDepth=1 只更新工序；=2 更新工序+工站；=3 更新三级。
    /// 未覆盖到的层级保留数据库原值（改上级不清空下级）。
    /// </summary>
    Task<bool> ReassignSubStationBindingHierarchyAsync(
        long bindingId,
        long processId,
        long stationId,
        long subStationId,
        int hierarchyDepth,
        CancellationToken cancellationToken);
}
