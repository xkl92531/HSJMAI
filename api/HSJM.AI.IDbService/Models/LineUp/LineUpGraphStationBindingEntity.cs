using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_GraphStationBinding")]
public sealed class LineUpGraphStationBindingEntity : BaseField
{
    public LineUpGraphStationBindingEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = false)] public long GraphAssetId { get; set; }
    [SugarColumn(IsNullable = false)] public long LineId { get; set; }
    [SugarColumn(IsNullable = false)] public long ProcessId { get; set; }
    [SugarColumn(IsNullable = false)] public long StationId { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    [SugarColumn(IsNullable = false)] public long DeviceId { get; set; }
    [SugarColumn(IsNullable = true)] public long? InstanceDbBlockId { get; set; }
    [SugarColumn(IsNullable = true)] public long? PublishedRevisionId { get; set; }
    [SugarColumn(IsNullable = false)] public string BindingStatus { get; set; } = "Unresolved";
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? RuntimeBindingJson { get; set; }
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? RuntimeValidationJson { get; set; }
    [SugarColumn(Length = 64, IsNullable = true)] public string? ValidationRevision { get; set; }
    [SugarColumn(IsNullable = false)] public bool MonitoringEnabled { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? LastValidatedAt { get; set; }
}
