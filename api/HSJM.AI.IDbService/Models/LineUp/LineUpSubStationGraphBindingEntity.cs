using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("AI_LineUp_SubStationGraphBinding")]
public sealed class LineUpSubStationGraphBindingEntity : BaseField
{
    public LineUpSubStationGraphBindingEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    public long LineId { get; set; }
    [SugarColumn(IsNullable = true)] public long? ProcessId { get; set; }
    [SugarColumn(IsNullable = true)] public long? StationId { get; set; }
    [SugarColumn(IsNullable = true)] public long? SubStationId { get; set; }
    public long DeviceId { get; set; }
    public long PlcProjectId { get; set; }
    public long SequenceId { get; set; }
    public long GraphBlockId { get; set; }
    [SugarColumn(IsNullable = true)] public long? InstanceDbBlockId { get; set; }
    [SugarColumn(ColumnDataType = "jsonb", IsNullable = true)] public string? RuntimeBindingJson { get; set; }
    [SugarColumn(IsNullable = false)] public string BindingStatus { get; set; } = string.Empty;
    public bool MonitoringEnabled { get; set; }
    [SugarColumn(IsNullable = true)] public DateTime? LastValidatedAt { get; set; }
}
