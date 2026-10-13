#nullable enable

namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpExistingGraphMetadataAudit(
    LineUpExistingPlcProject? Project,
    IReadOnlyList<LineUpExistingGraphBlock> Blocks,
    IReadOnlyList<LineUpExistingBlockInterface> Interfaces,
    IReadOnlyList<LineUpExistingDeviceEvidence> Devices,
    IReadOnlyList<LineUpExistingStationEvidence> Stations,
    IReadOnlyList<LineUpExistingProcessEvidence> Processes,
    IReadOnlyList<LineUpExistingLineEvidence> ProductionLines,
    IReadOnlyList<string> StationCodeEvidence);

public sealed class LineUpExistingPlcProject
{
    public long PlcProjectId { get; set; }
    public string? ProjectName { get; set; }
    public string? StationCode { get; set; }
}

public sealed class LineUpExistingGraphBlock
{
    public long PlcBlockId { get; set; }
    public long PlcProjectId { get; set; }
    public string? SourceBlockName { get; set; }
    public string? BlockType { get; set; }
    public int? BlockNumber { get; set; }
    public string? ProgrammingLanguage { get; set; }
    public string? StationCode { get; set; }
}

public sealed class LineUpExistingBlockInterface
{
    public long InterfaceId { get; set; }
    public long PlcBlockId { get; set; }
    public long? ParentInterfaceId { get; set; }
    public string? VariableName { get; set; }
    public string? VariablePath { get; set; }
    public string? DataType { get; set; }
    public string? Scope { get; set; }
    public decimal? OffsetByte { get; set; }
    public decimal? OffsetBit { get; set; }
    public decimal? SizeBits { get; set; }
    public string? Offset { get; set; }
}

public sealed class LineUpExistingDeviceEvidence
{
    public long DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? ProjectName { get; set; }
    public long? ProductionLineId { get; set; }
    public string? DeviceCode { get; set; }
    public string? S7DeviceName { get; set; }
}

public sealed class LineUpExistingStationEvidence
{
    public long WorkStationId { get; set; }
    public long ProcessId { get; set; }
    public long ProductionLineId { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
}

public sealed class LineUpExistingProcessEvidence
{
    public long ProcessId { get; set; }
    public long ProductionLineId { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
}

public sealed class LineUpExistingLineEvidence
{
    public long ProductionLineId { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
}
