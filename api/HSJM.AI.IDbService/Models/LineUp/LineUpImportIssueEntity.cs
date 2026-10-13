using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_ImportIssue")]
public sealed class LineUpImportIssueEntity : BaseField
{
    public LineUpImportIssueEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long ImportBatchId { get; set; }
    [SugarColumn(IsNullable = false)] public string Severity { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string IssueType { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)] public string? SourceKey { get; set; }
    [SugarColumn(IsNullable = false)] public string Message { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? ResolutionJson { get; set; }
}
