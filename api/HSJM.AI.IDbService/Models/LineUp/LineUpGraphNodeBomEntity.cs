using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

/// <summary>
/// GRAPH 节点（工步/转换）→ BOM → 点位图 关联快照。
/// 按导入批次（AI_LineUp_ImportBatch.PreviewJson）解析生成：节点聚合符号（工步=相邻转换条件+自身网络+动作 token，
/// 转换=自身条件网络）与 Lup_BomDirectory.TagCode 匹配后，落到 点位图/点位 级关联行，
/// 供外部系统直接查库读取"当前工步的 BOM 图片信息"。
/// 只存 ID 引用：点位重拖、成品图重合成后按 ImageId 取最新像素；ImageStorageKey 仅是生成时刻的快照参考。
/// </summary>
[SugarTable("AI_LineUp_GraphNodeBom", TableDescription = "LineUp GRAPH 节点 BOM 点位图关联")]
public sealed class LineUpGraphNodeBomEntity : BaseField
{
    public LineUpGraphNodeBomEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long BatchId { get; set; }
    public long LineId { get; set; }
    public long DeviceId { get; set; }
    [SugarColumn(IsNullable = false)] public string BlockName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string SequenceSourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string NodeSourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string NodeKind { get; set; } = string.Empty;
    public long SubStationId { get; set; }
    public long BomId { get; set; }
    public string? BomRefNo { get; set; }
    public string? TagCode { get; set; }
    public long ImageId { get; set; }
    public long MarkId { get; set; }
    public string? ImageStorageKey { get; set; }
}
