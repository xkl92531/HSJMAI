using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_GraphStep")]
public sealed class LineUpGraphStepEntity : BaseField
{
    public LineUpGraphStepEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long SequenceId { get; set; }
    [SugarColumn(IsNullable = false)] public string SourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string SourceName { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string DisplayName { get; set; } = string.Empty;
    public int StepNumber { get; set; }
    public bool IsInitial { get; set; }
    public bool IsFinal { get; set; }
    public bool IsSafety { get; set; }
    [SugarColumn(IsNullable = true)] public string? Comment { get; set; }
    [SugarColumn(IsNullable = true)] public string? SourceVariablePath { get; set; }
    [SugarColumn(IsNullable = true)] public long? BlockInterfaceId { get; set; }
    [SugarColumn(IsNullable = true)] public string? MaximumStepTime { get; set; }
    [SugarColumn(IsNullable = true)] public string? WarningTime { get; set; }
}
