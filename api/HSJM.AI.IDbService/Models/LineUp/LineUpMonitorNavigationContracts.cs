#nullable enable

namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpMonitorNavigationRow(
    long LineId,
    string LineCode,
    string LineName,
    long ProcessId,
    string ProcessCode,
    string ProcessName,
    long StationId,
    string StationCode,
    string StationName,
    long SubStationId,
    string SubStationCode,
    string SubStationName,
    int SubStationSortOrder,
    long? BindingId,
    long? PublishedRevisionId,
    string? BindingStatus,
    string? GraphSourceName,
    bool MonitoringEnabled,
    int BindingIssueCount);
