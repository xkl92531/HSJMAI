namespace HSJM.AI.IDbService;

/// <summary>
/// GRAPH 节点成品图快照仓储（AI_LineUp_GraphNodeImage）。
/// </summary>
public interface ILineUpGraphNodeImageRepository
{
    /// <summary>按批次+节点整组替换（同节点旧快照物理删除后重插，幂等）。</summary>
    Task ReplaceNodeRowsAsync(
        long batchId,
        string nodeSourceKey,
        IReadOnlyList<LineUpGraphNodeImageEntity> rows,
        CancellationToken cancellationToken);

    /// <summary>按产线+块名+节点查最近一次物化批次的快照行。</summary>
    Task<List<LineUpGraphNodeImageEntity>> ListByNodeAsync(
        long lineId,
        string blockName,
        string nodeSourceKey,
        CancellationToken cancellationToken);
}