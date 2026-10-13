using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_RuntimeEvent")]
public sealed class LineUpRuntimeEventEntity : BaseField
{
    public LineUpRuntimeEventEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long BindingId { get; set; }
    [SugarColumn(IsNullable = true)] public long? SequenceId { get; set; }
    [SugarColumn(IsNullable = true)] public string? SequenceSourceKey { get; set; }
    [SugarColumn(IsNullable = false)] public string OwnerSourceKey { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string EventType { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)] public string? PreviousState { get; set; }
    [SugarColumn(IsNullable = true)] public string? CurrentState { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    [SugarColumn(IsNullable = true)] public long? DurationMs { get; set; }
    [SugarColumn(IsNullable = true)] public string? Quality { get; set; }
    [SugarColumn(IsNullable = true)] public string? QualityCode { get; set; }
    [SugarColumn(IsNullable = false)] public string DefinitionRevision { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public string GatewayStreamId { get; set; } = string.Empty;
    public long GatewaySequence { get; set; }
    public int EventOrdinal { get; set; }
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = false)] public string DetailsJson { get; set; } = "{}";
}
