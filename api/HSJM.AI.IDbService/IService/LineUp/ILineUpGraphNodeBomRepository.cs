namespace HSJM.AI.IDbService;

/// <summary>
/// GRAPH 节点 BOM 点位图关联（AI_LineUp_GraphNodeBom）仓储。
/// </summary>
public interface ILineUpGraphNodeBomRepository
{
    /// <summary>整批替换关联行（先清后写，事务包裹）。</summary>
    Task ReplaceBatchRowsAsync(long batchId, IReadOnlyList<LineUpGraphNodeBomEntity> rows, CancellationToken cancellationToken);

    /// <summary>按批次查关联行（可按节点 SourceKey 过滤）。</summary>
    Task<List<LineUpGraphNodeBomEntity>> ListByBatchAsync(long batchId, string? nodeSourceKey, CancellationToken cancellationToken);

    /// <summary>找产线+块名下最近一个已生成关联行的批次 Id；没有则返回 null。</summary>
    Task<long?> FindLatestMaterializedBatchAsync(long lineId, string blockName, CancellationToken cancellationToken);

    /// <summary>读取子工站的 BOM / 图片 / 点位 关联数据（快照生成用）。</summary>
    Task<LineUpGraphNodeBomStationData> LoadStationDataAsync(long subStationId, CancellationToken cancellationToken);
}

/// <summary>子工站点位图域数据（BOM 目录 + 图片 + 点位）。</summary>
public sealed class LineUpGraphNodeBomStationData
{
    public List<LineUpGraphNodeBomBomRow> Boms { get; } = new();
    public List<LineUpGraphNodeBomImageRow> Images { get; } = new();
    public List<LineUpGraphNodeBomMarkRow> Marks { get; } = new();
}

public sealed class LineUpGraphNodeBomBomRow
{
    public long Id { get; set; }
    public string? RefNo { get; set; }
    public string? TagCode { get; set; }
}

public sealed class LineUpGraphNodeBomImageRow
{
    public long Id { get; set; }
    public string? CurrentStorageKey { get; set; }
}

public sealed class LineUpGraphNodeBomMarkRow
{
    public long Id { get; set; }
    public long StationImageId { get; set; }
    public string? MarkTagBom { get; set; }
}
