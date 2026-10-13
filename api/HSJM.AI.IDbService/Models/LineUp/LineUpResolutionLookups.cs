namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpHierarchyLookupContext(
    LineUpLineLookup? Line,
    LineUpDeviceLookup? Device,
    IReadOnlyList<LineUpProcessLookup> Processes,
    IReadOnlyList<LineUpStationLookup> Stations,
    IReadOnlyList<LineUpSubStationLookup> SubStations,
    IReadOnlyList<LineUpExistingBindingLookup> ExistingBindings,
    IReadOnlyList<LineUpBlockStationBindingLookup> BlockStationBindings);

public sealed record LineUpLineLookup(long Id, string? Code, string? Name);

public sealed record LineUpDeviceLookup(
    long Id,
    long? LineId,
    string? DeviceName,
    string? ProjectName);

public sealed record LineUpProcessLookup(
    long Id,
    long LineId,
    string? Code,
    string? Name);

public sealed record LineUpStationLookup(
    long Id,
    long LineId,
    long ProcessId,
    string? Code,
    string? Name);

public sealed record LineUpSubStationLookup(
    long Id,
    long LineId,
    long ProcessId,
    long StationId,
    long PlcId,
    string? Code,
    string? Name,
    int SortNum);

public sealed record LineUpExistingBindingLookup(
    string SequenceSourceKey,
    long SequencePlcProjectId,
    long BindingPlcProjectId,
    long? ProcessId,
    long? StationId,
    long? SubStationId,
    string? BindingStatus);

/// <summary>
/// GRAPH 块 → 工站的人工绑定行（ai_plc_stationblockbinding，「批量绑定工站」弹框写入）。
/// 供层级解析器作块级工站证据：块绑到哪个工站，其顺控就归属该工站。
/// </summary>
public sealed record LineUpBlockStationBindingLookup(
    long? ProjectId,
    string? StationCode,
    string? BlockName);

public enum LineUpCallBindingParseStatus
{
    Parsed,
    InvalidJson,
    MatchingBindingMissing,
    InstanceDbMissing,
    MultipleMatchingBindings,
    CallerBlockMissing,
    CallerBlockProjectMismatch,
    LogicUnitMissing,
    LogicUnitBlockMismatch
}

public sealed record LineUpPlcResolutionData(
    IReadOnlyList<LineUpProjectLookup> Projects,
    IReadOnlyList<LineUpBlockLookup> Blocks,
    IReadOnlyList<LineUpCallSiteLookup> CallSites);

public sealed record LineUpProjectLookup(long Id, string? ProjectName);

public sealed record LineUpBlockLookup(
    long Id,
    long ProjectId,
    string? BlockName,
    string? BlockType,
    int? BlockNumber,
    string? ProgrammingLanguage);

public sealed record LineUpCallSiteLookup(
    long CallId,
    long ProjectId,
    long CallerBlockId,
    long LogicUnitId,
    string? CalleeBlockName,
    string? CalleeBlockType,
    string? InstanceDbName,
    LineUpCallBindingParseStatus BindingParseStatus,
    bool HasEquivalentDuplicateBindings = false);

public sealed record LineUpVariableLookupData(
    IReadOnlyList<LineUpBlockLookup> Blocks,
    IReadOnlyList<LineUpBlockInterfaceLookup> Interfaces);

public sealed record LineUpBlockInterfaceLookup(
    long Id,
    long BlockId,
    string? VariableName,
    string? VariablePath,
    string? DataType,
    decimal? OffsetByte,
    decimal? OffsetBit,
    decimal? SizeBits,
    string? Offset);

public sealed record LineUpGraphSymbolLookupRequest(
    string BlockName,
    string VariablePath);

public sealed record LineUpGraphSymbolLookupResult(
    string BlockName,
    string VariablePath,
    IReadOnlyList<LineUpBlockLookup> Blocks,
    IReadOnlyList<LineUpBlockInterfaceLookup> Interfaces);

public sealed record LineUpGraphSymbolLookupData(
    IReadOnlyList<LineUpGraphSymbolLookupResult> Symbols);

/// <summary>GRAPH 步位台账（ai_plc_graphsteptag）就绪度：EXEC_BITS 数组注册行与最大步号。</summary>
public sealed record LineUpGraphStepLedgerLookup(
    bool HasExecBitsArray,
    int ExecBitsRowCount,
    int MaxStepNumber,
    string? ExecBitsArrayDbAddress);
