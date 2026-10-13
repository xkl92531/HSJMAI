using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_GraphAction")]
public sealed class LineUpGraphActionEntity : BaseField
{
    public LineUpGraphActionEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long StepId { get; set; }
    [SugarColumn(IsNullable = false)] public string SourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string SourceName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string DisplayName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)] public string? Qualifier { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? CallBlockName { get; set; }
    [SugarColumn(IsNullable = true)] public string? ExecuteTime { get; set; }
    public int Ordinal { get; set; }
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? TokensJson { get; set; }
    [SugarColumn(IsNullable = true)] public string? ActionText { get; set; }
}
