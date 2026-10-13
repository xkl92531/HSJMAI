using HSJM.AI.Core;
using SqlSugar;

namespace HSJM.AI.IDbService;

[SugarTable("Lup_BomDirectory", tableDescription: "LineUp BOM directory")]
public sealed class LineUpBomDirectoryEntity : BaseField
{
    public LineUpBomDirectoryEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public string? RefNo { get; set; }
    [SugarColumn(IsNullable = true)] public string? BomType { get; set; }
    [SugarColumn(IsNullable = true)] public string? BomStatus { get; set; }
    [SugarColumn(IsNullable = true)] public string? MESPN { get; set; }
    [SugarColumn(IsNullable = true)] public long? StationId { get; set; }
    [SugarColumn(IsNullable = true)] public string? StationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? StationName { get; set; }
    [SugarColumn(IsNullable = true)] public string? PLCIP { get; set; }
    [SugarColumn(IsNullable = true)] public string? Mark { get; set; }
    [SugarColumn(IsNullable = true)] public string? Annotation { get; set; }
    [SugarColumn(IsNullable = true)] public string? IOAddress { get; set; }
    [SugarColumn(IsNullable = true)] public string? DBAddress { get; set; }
    [SugarColumn(IsNullable = true)] public string? FunctionParameter { get; set; }
    [SugarColumn(IsNullable = true)] public string? Describe { get; set; }
    [SugarColumn(IsNullable = true)] public string? ModelNo { get; set; }
    [SugarColumn(IsNullable = true)] public string? Brand { get; set; }
    [SugarColumn(IsNullable = true)] public string? HomePosition { get; set; }
    [SugarColumn(IsNullable = true)] public string? InstallationMethod { get; set; }
    [SugarColumn(IsNullable = true)] public string? SensorInterfaceType { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    [SugarColumn(IsNullable = true)] public string? SourceFrom { get; set; }
    [SugarColumn(IsNullable = true)] public string? Specification { get; set; }
    [SugarColumn(IsNullable = true)] public int? PerGroupQuantity { get; set; }
    [SugarColumn(IsNullable = true)] public int? TotalQuantity { get; set; }
    [SugarColumn(IsNullable = true)] public int? PendingQuantity { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProjectCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProjectName { get; set; }
    [SugarColumn(IsNullable = true)] public bool? IsMark { get; set; }
    [SugarColumn(IsNullable = true)] public bool? IsAssociated { get; set; }
    [SugarColumn(IsNullable = true)] public long? ProductionLineId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineName { get; set; }
    [SugarColumn(IsNullable = true)] public long? ProcessId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessName { get; set; }
    [SugarColumn(IsNullable = true)] public long? TagId { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagDbAdress { get; set; }
    [SugarColumn(IsNullable = true)] public long? PlcId { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
}

[SugarTable("Lup_BomDirectoryHistory", tableDescription: "LineUp BOM history")]
public sealed class LineUpBomDirectoryHistoryEntity : BaseField
{
    public LineUpBomDirectoryHistoryEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public string? HistoryNo { get; set; }
    [SugarColumn(IsNullable = true)] public string? OperationType { get; set; }
    [SugarColumn(IsNullable = true)] public string? RefNo { get; set; }
    [SugarColumn(IsNullable = true)] public string? BomType { get; set; }
    [SugarColumn(IsNullable = true)] public string? MESPN { get; set; }
    [SugarColumn(IsNullable = true)] public long? StationId { get; set; }
    [SugarColumn(IsNullable = true)] public string? StationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? StationName { get; set; }
    [SugarColumn(IsNullable = true)] public string? PLCIP { get; set; }
    [SugarColumn(IsNullable = true)] public string? Mark { get; set; }
    [SugarColumn(IsNullable = true)] public string? Annotation { get; set; }
    [SugarColumn(IsNullable = true)] public string? IOAddress { get; set; }
    [SugarColumn(IsNullable = true)] public string? DBAddress { get; set; }
    [SugarColumn(IsNullable = true)] public string? FunctionParameter { get; set; }
    [SugarColumn(IsNullable = true)] public string? Describe { get; set; }
    [SugarColumn(IsNullable = true)] public string? ModelNo { get; set; }
    [SugarColumn(IsNullable = true)] public string? Brand { get; set; }
    [SugarColumn(IsNullable = true)] public string? HomePosition { get; set; }
    [SugarColumn(IsNullable = true)] public string? InstallationMethod { get; set; }
    [SugarColumn(IsNullable = true)] public string? SensorInterfaceType { get; set; }
    [SugarColumn(IsNullable = true)] public string? Remark { get; set; }
    [SugarColumn(IsNullable = true)] public string? SourceFrom { get; set; }
    [SugarColumn(IsNullable = true)] public string? Specification { get; set; }
    [SugarColumn(IsNullable = true)] public int? PerGroupQuantity { get; set; }
    [SugarColumn(IsNullable = true)] public int? TotalQuantity { get; set; }
    [SugarColumn(IsNullable = true)] public int? PendingQuantity { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProjectCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProjectName { get; set; }
    [SugarColumn(IsNullable = true)] public long? ProductionLineId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineName { get; set; }
    [SugarColumn(IsNullable = true)] public long? ProcessId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessName { get; set; }
    [SugarColumn(IsNullable = true)] public long? TagId { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? TagDbAdress { get; set; }
    [SugarColumn(IsNullable = true)] public long? PlcId { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? PlcName { get; set; }
}

[SugarTable("Lup_StationImage", tableDescription: "LineUp substation images")]
public sealed class LineUpStationImageEntity : BaseField
{
    public LineUpStationImageEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? StationId { get; set; }
    [SugarColumn(IsNullable = true)] public string? StationCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? StationName { get; set; }
    [SugarColumn(IsNullable = true)] public string? PictureName { get; set; }
    [SugarColumn(IsNullable = true)] public string? PictureSize { get; set; }
    [SugarColumn(IsNullable = true)] public string? OriginalPictureUrl { get; set; }
    [SugarColumn(IsNullable = true)] public string? CurrentPictureUrl { get; set; }
    [SugarColumn(IsNullable = true)] public string? CanvasParameter { get; set; }
    [SugarColumn(IsNullable = true)] public bool? HasSubitem { get; set; }
    [SugarColumn(IsNullable = true)] public long? ProductionLineId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProductionLineName { get; set; }
    [SugarColumn(IsNullable = true)] public long? ProcessId { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessCode { get; set; }
    [SugarColumn(IsNullable = true)] public string? ProcessName { get; set; }
    [SugarColumn(IsNullable = true)] public string? StorageKey { get; set; }
    [SugarColumn(IsNullable = true)] public string? CurrentStorageKey { get; set; }
    [SugarColumn(IsNullable = true)] public string? ContentSha256 { get; set; }
    [SugarColumn(IsNullable = true)] public long? ByteLength { get; set; }
    [SugarColumn(IsNullable = true)] public int? PixelWidth { get; set; }
    [SugarColumn(IsNullable = true)] public int? PixelHeight { get; set; }
    [SugarColumn(IsNullable = true)] public string? DetectedMimeType { get; set; }
    public long RowVersion { get; set; }
}

[SugarTable("Lup_StationImageMark", tableDescription: "LineUp image point marks")]
public sealed class LineUpStationImageMarkEntity : BaseField
{
    public LineUpStationImageMarkEntity() => Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
    [SugarColumn(IsPrimaryKey = true)] public long Id { get; set; }
    [SugarColumn(IsNullable = true)] public long? StationImageId { get; set; }
    [SugarColumn(IsNullable = true)] public string? MarkType { get; set; }
    [SugarColumn(IsNullable = true)] public string? Mark { get; set; }
    [SugarColumn(IsNullable = true)] public string? MCPConfig { get; set; }
    [SugarColumn(IsNullable = true)] public string? MarkTag { get; set; }
    [SugarColumn(IsNullable = true)] public string? MarkTagConfig { get; set; }
    [SugarColumn(IsNullable = true)] public string? MarkTagLine { get; set; }
    [SugarColumn(IsNullable = true)] public string? MarkTagLineConfig { get; set; }
    [SugarColumn(IsNullable = true)] public string? MarkTagBom { get; set; }
    [SugarColumn(IsNullable = true)] public bool? IsSingleMark { get; set; }
    public int MarkerSchemaVersion { get; set; }
    [SugarColumn(
        ColumnDataType = "jsonb",
        IsNullable = true,
        IsOnlyIgnoreInsert = true,
        IsOnlyIgnoreUpdate = true)]
    public string? ParsedMarkerJson { get; set; }
}
