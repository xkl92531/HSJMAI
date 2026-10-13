using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_GraphSequence")]
public sealed class LineUpGraphSequenceEntity : BaseField
{
    public LineUpGraphSequenceEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = false)] public string SourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string SourceName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string DisplayName { get; set; } = string.Empty;
    public long PlcProjectId { get; set; }
    public long GraphBlockId { get; set; }
    [SugarColumn(IsNullable = true)] public long? InstanceDbBlockId { get; set; }
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? LayoutJson { get; set; }
    [SugarColumn(IsNullable = false)] public string SourceHash { get; set; } = string.Empty;
}
