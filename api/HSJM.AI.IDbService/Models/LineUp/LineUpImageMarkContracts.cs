namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpImageHierarchyData(
    long SubStationId,
    string? SubStationCode,
    string? SubStationName,
    long WorkStationId,
    string? WorkStationCode,
    string? WorkStationName,
    long ProcessId,
    string? ProcessCode,
    string? ProcessName,
    long ProductionLineId,
    string? ProductionLineCode,
    string? ProductionLineName);

public sealed record LineUpImageData(
    long Id,
    long SubStationId,
    string? PictureName,
    string? StorageKey,
    string? CurrentStorageKey,
    string? ContentSha256,
    long? ByteLength,
    int? PixelWidth,
    int? PixelHeight,
    string? DetectedMimeType,
    long RowVersion);

public sealed record LineUpImageBomData(
    long Id,
    long? StationId,
    string? RefNo,
    string? Name,
    string? TagCode,
    string? DbAddress,
    string? ModelNo,
    string? Brand,
    string? IoAddress,
    string? PartNo,
    long? PlcId,
    string? PlcName,
    string? PlcIp,
    bool? IsAssociated);

public sealed record LineUpPlcDeviceData(
    long Id,
    string? Code,
    string? Name,
    string? Ip,
    long? ProductionLineId);

public sealed record LineUpImageWorkspaceData(
    LineUpImageHierarchyData Hierarchy,
    IReadOnlyList<LineUpImageData> Images,
    IReadOnlyList<LineUpImageBomData> Boms,
    IReadOnlyList<LineUpStationImageMarkEntity> Markers);

public sealed record LineUpImageCreateData(
    long ImageId,
    LineUpImageHierarchyData Hierarchy,
    string PictureName,
    string StorageKey,
    string ContentSha256,
    long ByteLength,
    int PixelWidth,
    int PixelHeight,
    string DetectedMimeType,
    long RowVersion);

public sealed record LineUpImageMarkerSaveData(
    long MarkerId,
    string? MarkType,
    bool IsSingleMark,
    string? McpConfig,
    string? MarkTagConfig,
    string? MarkTagLineConfig,
    string MarkTagBom,
    int MarkerSchemaVersion,
    string ParsedMarkerJson);

public sealed record LineUpImageSaveData(
    long ImageId,
    long ExpectedRowVersion,
    string? CurrentStorageKey,
    IReadOnlyList<LineUpImageMarkerSaveData> Markers,
    LineUpBomSubStationBinding? PoolBinding = null);

/// <summary>上图即绑定：本次挂上图的产线直挂（无工站）点位，在同一事务内绑定为当前子工站（工序/工站/子工站字段与该子工站层级一致）。</summary>
public sealed record LineUpBomSubStationBinding(
    IReadOnlyList<long> BomIds,
    LineUpImageHierarchyData Hierarchy);

public enum LineUpImageSaveResult
{
    Saved,
    Missing,
    VersionConflict,
    MarkerOwnershipConflict
}

public sealed record LineUpBomImportLineInfo(
    long LineId,
    string? Code,
    string? Name);

/// <summary>导入 upsert 单行。IsUpdate=true 走 UpdateBomFieldsAsync（IoAddress 传现有值以保留，Line 忽略）；false 走 CreateBomAsync（产线级点位：StationId/工站/工序字段为空，层级仅产线来自 Line，SourceFrom=ImageMarksImport）。</summary>
public sealed record LineUpBomImportUpsert(
    bool IsUpdate,
    long BomId,
    string? RefNo,
    string? Describe,
    string? TagCode,
    string? DbAddress,
    string? IoAddress,
    string? ModelNo,
    string? Brand,
    string? PartNo,
    long? PlcId,
    string? PlcCode,
    string? PlcName,
    string? PlcIp,
    LineUpBomImportLineInfo? Line);
