#nullable enable

namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpGraphCatalogLookupData(
    IReadOnlyList<LineUpGraphCatalogProjectLookup> Projects,
    IReadOnlyList<LineUpGraphCatalogLineLookup> Lines,
    IReadOnlyList<LineUpGraphCatalogProcessLookup> Processes,
    IReadOnlyList<LineUpGraphCatalogStationLookup> Stations,
    IReadOnlyList<LineUpGraphCatalogSubStationLookup> SubStations,
    IReadOnlyList<LineUpGraphCatalogDeviceLookup> Devices);

public sealed record LineUpGraphCatalogProjectLookup(long Id, string Name, long? PlcDeviceId);
public sealed record LineUpGraphCatalogLineLookup(long Id, string? Code, string? Name);
public sealed record LineUpGraphCatalogProcessLookup(long Id, long LineId, string? Code, string? Name);
public sealed record LineUpGraphCatalogStationLookup(long Id, long LineId, long ProcessId, string? Code, string? Name);
public sealed record LineUpGraphCatalogSubStationLookup(
    long Id,
    long LineId,
    long ProcessId,
    long StationId,
    string? Code,
    string? Name,
    long? PlcId,
    string? PlcName);
public sealed record LineUpGraphCatalogDeviceLookup(long Id, long LineId, string? Name, string? ProjectName, string? DeviceType);

public sealed record LineUpGraphCatalogQuery(
    long? PlcProjectId,
    long? LineId,
    long? ProcessId,
    long? StationId,
    long? SubStationId,
    long? DeviceId,
    string? Status,
    string? Keyword,
    int Offset,
    int Limit);

public sealed record LineUpGraphCatalogAssetData(
    long AssetId,
    long PlcProjectId,
    string PlcProjectName,
    string SourceBlockName,
    string BlockType,
    int BlockNumber,
    long CurrentPlcBlockId,
    long? LatestRevisionId,
    int LatestRevisionNo,
    string LatestRevisionStatus,
    string SummaryJson,
    long? BindingId,
    long? LineId,
    long? ProcessId,
    long? StationId,
    long? SubStationId,
    long? DeviceId,
    long? PublishedRevisionId,
    int? PublishedRevisionNo,
    string? BindingStatus,
    bool MonitoringEnabled,
    bool HasPreviewBatch,
    int TotalCount);

public sealed record LineUpGraphRevisionData(
    long Id,
    long GraphAssetId,
    int RevisionNo,
    string Status,
    string SourceHash,
    int ParserSchemaVersion,
    string PreviewJson,
    string SummaryJson,
    DateTime? PublishedAt,
    long? PublishedBy);

public sealed record LineUpGraphBindingData(
    long Id,
    long GraphAssetId,
    long LineId,
    long ProcessId,
    long StationId,
    long? SubStationId,
    long DeviceId,
    long? InstanceDbBlockId,
    long? PublishedRevisionId,
    string BindingStatus,
    bool MonitoringEnabled,
    DateTime? ModifyTime)
{
    public LineUpGraphBindingData(
        long id,
        long graphAssetId,
        long lineId,
        long processId,
        long stationId,
        long deviceId,
        long? instanceDbBlockId,
        long? publishedRevisionId,
        string bindingStatus,
        bool monitoringEnabled,
        DateTime? modifyTime)
        : this(id, graphAssetId, lineId, processId, stationId, null, deviceId,
            instanceDbBlockId, publishedRevisionId, bindingStatus, monitoringEnabled, modifyTime)
    {
    }
}

public sealed record LineUpGraphAliasData(long Id, long BindingId, string ElementSourceKey, string DisplayName);

public sealed record LineUpGraphCatalogDetailData(
    LineUpGraphCatalogAssetData Asset,
    IReadOnlyList<LineUpGraphRevisionData> Revisions,
    LineUpGraphBindingData? Binding,
    IReadOnlyList<LineUpGraphAliasData> Aliases);

public sealed record LineUpGraphBindingWrite(
    long GraphAssetId,
    long LineId,
    long ProcessId,
    long StationId,
    long? SubStationId,
    long DeviceId,
    long? InstanceDbBlockId,
    string BindingStatus)
{
    public LineUpGraphBindingWrite(
        long graphAssetId,
        long lineId,
        long processId,
        long stationId,
        long deviceId,
        long? instanceDbBlockId,
        string bindingStatus)
        : this(graphAssetId, lineId, processId, stationId, null, deviceId, instanceDbBlockId, bindingStatus)
    {
    }
}

public sealed record LineUpGraphAliasValue(string ElementSourceKey, string DisplayName);

public sealed record LineUpGraphAliasWrite(
    long BindingId,
    long ExpectedLatestRevisionId,
    IReadOnlyList<LineUpGraphAliasValue> Aliases);

public sealed record LineUpGraphPublishWrite(
    long BindingId,
    long TargetRevisionId,
    long ExpectedLatestRevisionId,
    long? ExpectedPublishedRevisionId,
    long? PublishedBy,
    DateTime PublishedAt);

public sealed record LineUpGraphPublishData(
    long BindingId,
    long PublishedRevisionId,
    int PublishedRevisionNo,
    long? PreviousPublishedRevisionId,
    DateTime PublishedAt,
    bool MonitoringEnabled);
