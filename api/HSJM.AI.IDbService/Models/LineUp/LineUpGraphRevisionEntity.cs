using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_GraphRevision")]
public sealed class LineUpGraphRevisionEntity : BaseField
{
    public LineUpGraphRevisionEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = false)] public long GraphAssetId { get; set; }
    [SugarColumn(IsNullable = false)] public long PlcBlockId { get; set; }
    [SugarColumn(IsNullable = false)] public string PlcParseTaskId { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string SourceHash { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public int ParserSchemaVersion { get; set; }
    [SugarColumn(IsNullable = false)] public int RevisionNo { get; set; }
    [SugarColumn(IsNullable = false)] public string Status { get; set; } = "Draft";
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = false)] public string PreviewJson { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = false)] public string SummaryJson { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)] public string? ParseErrorCode { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? PublishedAt { get; set; }
    [SugarColumn(IsNullable = true)] public long? PublishedBy { get; set; }
}
