namespace HSJM.AI.IDbService.LineUp;

public sealed record LineUpWorkspaceLookupData(
    IReadOnlyList<LineUpWorkspaceLineLookup> Lines,
    long? SelectedLineId,
    IReadOnlyList<LineUpWorkspaceDeviceLookup> Devices,
    IReadOnlyList<LineUpWorkspaceImportLookup> RecentImports);

public sealed record LineUpWorkspaceLineLookup(long Id, string? Code, string? Name);

public sealed record LineUpWorkspaceDeviceLookup(
    long Id,
    long LineId,
    string? Name,
    string? ProjectName,
    string? DeviceType,
    int RegistrationStatus);

public sealed record LineUpWorkspaceImportLookup(
    long BatchId,
    long LineId,
    long DeviceId,
    string SourceFileName,
    string Status,
    string? ErrorCode,
    int PreviewRevision,
    string? SummaryJson,
    DateTime? CreatedAt,
    DateTime? CommittedAt);
