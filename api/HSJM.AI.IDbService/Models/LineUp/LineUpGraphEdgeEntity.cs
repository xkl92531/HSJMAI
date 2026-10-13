using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_GraphEdge")]
public sealed class LineUpGraphEdgeEntity : BaseField
{
    public LineUpGraphEdgeEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long SequenceId { get; set; }
    [SugarColumn(IsNullable = false)] public string SourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string FromNodeType { get; set; } = string.Empty;
    public long FromNodeId { get; set; }
    [SugarColumn(IsNullable = false)] public string ToNodeType { get; set; } = string.Empty;
    public long ToNodeId { get; set; }
    [SugarColumn(IsNullable = false)] public string EdgeKind { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)] public int? BranchOrder { get; set; }
}
