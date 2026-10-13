using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

/// <summary>
/// GRAPH 节点（工步/转换）成品图快照（AI_LineUp_GraphNodeImage）。
/// 保存 graph 结构时由前端按节点渲染"成品图+当前工步匹配 BOM 红点"并上传物化 PNG，
/// 查看流程图时直接读本表 + 文件，不再运行时匹配。
/// StorageKey 指向 App_Data/LineUp/graph-node-images/{sha256}.png（内容寻址，同内容只存一份）。
/// </summary>
[SugarTable("AI_LineUp_GraphNodeImage", TableDescription = "LineUp GRAPH 节点成品图快照")]
public sealed class LineUpGraphNodeImageEntity : BaseField
{
    public LineUpGraphNodeImageEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long BatchId { get; set; }
    public long LineId { get; set; }
    public long DeviceId { get; set; }
    [SugarColumn(IsNullable = false)] public string BlockName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string SequenceSourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string NodeSourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string NodeKind { get; set; } = string.Empty;
    public long SubStationId { get; set; }
    public long SourceImageId { get; set; }
    [SugarColumn(IsNullable = false)] public string StorageKey { get; set; } = string.Empty;
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
}