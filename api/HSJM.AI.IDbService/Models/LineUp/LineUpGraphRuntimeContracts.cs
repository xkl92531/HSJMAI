#nullable enable

namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpGraphRuntimeBindingStatusData(
    long Id,
    long? PublishedRevisionId,
    string BindingStatus,
    string? RuntimeBindingJson,
    string? RuntimeValidationJson,
    string? ValidationRevision,
    bool MonitoringEnabled,
    DateTime? LastValidatedAt);

public sealed record LineUpGraphRuntimeBindingData(
    long Id,
    long GraphAssetId,
    long LineId,
    long ProcessId,
    long StationId,
    long DeviceId,
    long? InstanceDbBlockId,
    long PublishedRevisionId,
    string? RuntimeBindingJson,
    string? RuntimeValidationJson,
    string? ValidationRevision,
    string BindingStatus,
    bool MonitoringEnabled,
    DateTime? LastValidatedAt)
{
    public LineUpGraphRuntimeBindingData(
        long id,
        long graphAssetId,
        long lineId,
        long processId,
        long stationId,
        long deviceId,
        long? instanceDbBlockId,
        long publishedRevisionId,
        string? runtimeBindingJson,
        string bindingStatus,
        bool monitoringEnabled,
        DateTime? lastValidatedAt)
        : this(id, graphAssetId, lineId, processId, stationId, deviceId, instanceDbBlockId,
            publishedRevisionId, runtimeBindingJson, null, null, bindingStatus, monitoringEnabled, lastValidatedAt)
    {
    }
}

public sealed record LineUpGraphRuntimeAssetData(
    long Id,
    long PlcProjectId,
    long CurrentPlcBlockId,
    string StableKey,
    string SourceBlockName,
    string BlockType,
    int BlockNumber,
    string ProgrammingLanguage);

public sealed record LineUpGraphRuntimeRevisionData(
    long Id,
    long GraphAssetId,
    int RevisionNo,
    string SourceHash,
    int ParserSchemaVersion,
    string PreviewJson);

public sealed record LineUpGraphRuntimeContext(
    LineUpGraphRuntimeBindingData Binding,
    LineUpGraphRuntimeAssetData Asset,
    LineUpGraphRuntimeRevisionData PublishedRevision,
    LineUpBlockLookup InstanceDb,
    IReadOnlyDictionary<string, string> Aliases);
