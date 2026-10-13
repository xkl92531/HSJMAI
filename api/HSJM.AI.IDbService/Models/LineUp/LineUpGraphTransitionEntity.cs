using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_GraphTransition")]
public sealed class LineUpGraphTransitionEntity : BaseField
{
    public LineUpGraphTransitionEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long SequenceId { get; set; }
    [SugarColumn(IsNullable = false)] public string SourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string SourceName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string DisplayName { get; set; } = string.Empty;
    public int TransitionNumber { get; set; }
    [SugarColumn(IsNullable = true)] public string? ConditionExpression { get; set; }
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? ConditionTokensJson { get; set; }
    [SugarColumn(IsNullable = true)] public string? SourceVariablePath { get; set; }
    [SugarColumn(IsNullable = true)] public long? BlockInterfaceId { get; set; }
    public bool IsJump { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProgrammingLanguage { get; set; }
    public bool IsMissing { get; set; }
}
