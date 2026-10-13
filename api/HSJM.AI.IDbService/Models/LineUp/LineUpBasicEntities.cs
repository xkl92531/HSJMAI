using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("Basic_ProductionLine", tableDescription: "LineUp compatible production line")]
public sealed class LineUpProductionLineEntity : BaseField
{
    public LineUpProductionLineEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)]
    public long Id { get; set; }

    [SugarColumn(IsNullable = true)] public string? Code { get; set; }
    [SugarColumn(IsNullable = true)] public string? Name { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    [SugarColumn(IsNullable = true)] public bool? IsEnable { get; set; }
}

[SugarTable("Basic_Process", tableDescription: "LineUp compatible process")]
public sealed class LineUpProcessEntity : BaseField
{
    public LineUpProcessEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)]
    public long Id { get; set; }

    [SugarColumn(IsNullable = true)] public string? Code { get; set; }
    [SugarColumn(IsNullable = true)] public string? Name { get; set; }
    [SugarColumn(IsNullable = true)] public long ProductionLineId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineName { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    [SugarColumn(IsNullable = true)] public bool? IsEnable { get; set; }
}

[SugarTable("Basic_WorkStation", tableDescription: "LineUp compatible work station")]
public sealed class LineUpWorkStationEntity : BaseField
{
    public LineUpWorkStationEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)]
    public long Id { get; set; }

    [SugarColumn(IsNullable = true)] public string? Code { get; set; }
    [SugarColumn(IsNullable = true)] public string? Name { get; set; }
    [SugarColumn(IsNullable = true)] public long ProcessId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessName { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    [SugarColumn(IsNullable = true)] public bool? IsEnable { get; set; }
    [SugarColumn(IsNullable = true)] public long ProductionLineId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineName { get; set; }
}

[SugarTable("Basic_SubStation", tableDescription: "LineUp compatible sub station")]
public sealed class LineUpSubStationEntity : BaseField
{
    public LineUpSubStationEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)]
    public long Id { get; set; }

    [SugarColumn(IsNullable = true)] public long ProcessID { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessName { get; set; }
    [SugarColumn(IsNullable = true)] public long WorkStationID { get; set; }
    [SugarColumn(IsNullable = true)] public string? WorkStationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? WorkStationName { get; set; }
    [SugarColumn(IsNullable = true)] public long ProductionLineID { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineName { get; set; }
    [SugarColumn(IsNullable = true)] public long PlcID { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationName { get; set; }
    [SugarColumn(IsNullable = true)] public string? SubStationMode { get; set; }
    [SugarColumn(IsNullable = true)] public int SortNum { get; set; }
    [SugarColumn(IsNullable = true)] public string? FlowJson { get; set; }
    [SugarColumn(IsNullable = true)] public string? AutoModeTagCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ExceptionDb { get; set; }
}

[SugarTable("Basic_Device", tableDescription: "LineUp compatible PLC device")]
public sealed class LineUpDeviceEntity : BaseField
{
    public LineUpDeviceEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

    [SugarColumn(IsPrimaryKey = true)]
    public long Id { get; set; }

    [SugarColumn(IsNullable = true)] public string? DeviceName { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProjectName { get; set; }
    [SugarColumn(IsNullable = true)] public string? IpAddress { get; set; }
    [SugarColumn(IsNullable = true)] public string? DeviceType { get; set; }
    [SugarColumn(IsNullable = true)] public int RegistStatus { get; set; }
    [SugarColumn(IsNullable = true)] public string? Psw { get; set; }
    [SugarColumn(IsNullable = true)] public long? LineID { get; set; }
    [SugarColumn(IsNullable = true)] public string? DicCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    [SugarColumn(IsNullable = true)] public string? Ss7DeviceName { get; set; }
}
