namespace HSJM.AI.IDbService;

/// <summary>
/// Agent 只读查询仓储：按业务编码（工站/子工站/工步/BOM 编码/产线）解析 LineUp 层级与 PLC 项目。
/// 供外部 Agent 调用的两个开放接口（BOM 点位图、实时点位值）使用。
/// </summary>
public interface ILineUpAgentQueryRepository
{
    /// <summary>按 工站编码+子工站编码 找子工站（可选产线过滤）；同码多产线命中时返回多行，由上层判歧义。</summary>
    Task<List<LineUpAgentSubStationRow>> FindSubStationsAsync(
        string workStationCode,
        string subStationCode,
        long? lineId,
        CancellationToken cancellationToken);

    /// <summary>按编码或名称找产线（LOWER(TRIM) 忽略大小写）。</summary>
    Task<List<LineUpAgentLineRow>> FindLinesAsync(string lineKey, CancellationToken cancellationToken);

    /// <summary>按 Id 找产线。</summary>
    Task<LineUpAgentLineRow?> FindLineByIdAsync(long lineId, CancellationToken cancellationToken);

    /// <summary>按工站编码找工站（可选产线过滤）。</summary>
    Task<List<LineUpAgentWorkStationRow>> FindWorkStationsAsync(
        string workStationCode,
        long? lineId,
        CancellationToken cancellationToken);

    /// <summary>列出工站下全部子工站（取 PlcID）。</summary>
    Task<List<LineUpAgentSubStationBriefRow>> ListSubStationsByWorkStationAsync(
        long workStationId,
        CancellationToken cancellationToken);

    /// <summary>按 Id 列设备（取 ProjectName）。</summary>
    Task<List<LineUpAgentDeviceRow>> ListDevicesAsync(
        IReadOnlyCollection<long> deviceIds,
        CancellationToken cancellationToken);

    /// <summary>按项目名称（LOWER(TRIM) 忽略大小写）找 PLC 项目。</summary>
    Task<List<LineUpAgentProjectRow>> FindProjectsByNameAsync(
        IReadOnlyCollection<string> projectNames,
        CancellationToken cancellationToken);

    /// <summary>按 Id 找 PLC 项目（补名称用）。</summary>
    Task<List<LineUpAgentProjectRow>> FindProjectsByIdAsync(
        IReadOnlyCollection<long> projectIds,
        CancellationToken cancellationToken);

    /// <summary>按子工站列结构绑定（AI_LineUp_SubStationGraphBinding，含 PlcProjectId/SequenceId）。</summary>
    Task<List<LineUpAgentBindingRow>> ListBindingsBySubStationsAsync(
        IReadOnlyCollection<long> subStationIds,
        CancellationToken cancellationToken);

    /// <summary>按顺控 Id 集合 + 工步编号找 GRAPH 工步（AI_LineUp_GraphStep）。</summary>
    Task<List<LineUpAgentStepRow>> FindStepsAsync(
        IReadOnlyCollection<long> sequenceIds,
        int stepNumber,
        CancellationToken cancellationToken);

    /// <summary>按子工站 + 节点 SourceKey 列 GRAPH 节点→BOM/图片 关联行（AI_LineUp_GraphNodeBom，跨批次）。</summary>
    Task<List<LineUpAgentNodeBomRow>> ListNodeBomsAsync(
        long subStationId,
        IReadOnlyCollection<string> nodeSourceKeys,
        CancellationToken cancellationToken);

    /// <summary>按子工站集合 + 编码（RefNo 或 TagCode 精确匹配）查工站 BOM 清单（Lup_BomDirectory），供 BOM 编码→TagCode 解析。</summary>
    Task<List<LineUpAgentBomRow>> FindBomsByCodeAsync(
        IReadOnlyCollection<long> subStationIds,
        string code,
        CancellationToken cancellationToken);
}

// 行类型统一用 class + setter：与既有仓储一致（record 投影在引号列名下 DataReader 绑定不可靠）。

public sealed class LineUpAgentLineRow
{
    public long Id { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
}

public sealed class LineUpAgentWorkStationRow
{
    public long Id { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public long ProductionLineId { get; set; }
    public string? ProductionLineCode { get; set; }
    public string? ProductionLineName { get; set; }
}

public sealed class LineUpAgentSubStationRow
{
    public long SubStationId { get; set; }
    public string? SubStationCode { get; set; }
    public string? SubStationName { get; set; }
    public long WorkStationId { get; set; }
    public string? WorkStationCode { get; set; }
    public string? WorkStationName { get; set; }
    public long ProductionLineId { get; set; }
    public string? ProductionLineCode { get; set; }
    public string? ProductionLineName { get; set; }
    public long PlcId { get; set; }
}

public sealed class LineUpAgentSubStationBriefRow
{
    public long SubStationId { get; set; }
    public string? SubStationCode { get; set; }
    public long PlcId { get; set; }
}

public sealed class LineUpAgentDeviceRow
{
    public long Id { get; set; }
    public string? DeviceName { get; set; }
    public string? ProjectName { get; set; }
}

public sealed class LineUpAgentProjectRow
{
    public long Id { get; set; }
    public string? ProjectName { get; set; }
}

public sealed class LineUpAgentBindingRow
{
    public long Id { get; set; }
    public long LineId { get; set; }
    public long SubStationId { get; set; }
    public long DeviceId { get; set; }
    public long PlcProjectId { get; set; }
    public long SequenceId { get; set; }
    public string? BindingStatus { get; set; }
}

public sealed class LineUpAgentStepRow
{
    public long Id { get; set; }
    public long SequenceId { get; set; }
    public int StepNumber { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class LineUpAgentNodeBomRow
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public long LineId { get; set; }
    public string BlockName { get; set; } = string.Empty;
    public string SequenceSourceKey { get; set; } = string.Empty;
    public string NodeSourceKey { get; set; } = string.Empty;
    public string NodeKind { get; set; } = string.Empty;
    public long SubStationId { get; set; }
    public long BomId { get; set; }
    public string? BomRefNo { get; set; }
    public string? TagCode { get; set; }
    public long ImageId { get; set; }
    public long MarkId { get; set; }
}

public sealed class LineUpAgentBomRow
{
    public long Id { get; set; }
    public long StationId { get; set; }
    public string? RefNo { get; set; }
    public string? TagCode { get; set; }
}
