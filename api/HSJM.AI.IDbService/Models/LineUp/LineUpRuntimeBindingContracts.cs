namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpRuntimeBindingData(
    long Id,
    long LineId,
    long DeviceId,
    long? SubStationId,
    long PlcProjectId,
    long SequenceId,
    long GraphBlockId,
    long? InstanceDbBlockId,
    string? RuntimeBindingJson,
    string BindingStatus,
    bool MonitoringEnabled);

public sealed record LineUpRuntimeSequenceData(
    long Id,
    string SourceKey,
    string SourceHash);

public sealed record LineUpRuntimeStepData(
    long Id,
    string SourceKey,
    string DisplayName,
    int StepNumber);

public sealed record LineUpRuntimeBindingContext(
    LineUpRuntimeBindingData Binding,
    LineUpRuntimeSequenceData Sequence,
    LineUpBlockLookup InstanceDb,
    IReadOnlyList<LineUpRuntimeStepData> Steps);
