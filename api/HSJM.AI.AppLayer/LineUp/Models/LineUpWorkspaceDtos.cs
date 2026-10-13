using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.LineUp.Models;

public sealed class LineUpWorkspaceDto
{
    public List<LineUpWorkspaceLineDto> Lines { get; init; } = new();
    public long? SelectedLineId { get; init; }
    public List<LineUpWorkspaceDeviceDto> Devices { get; init; } = new();
    public List<LineUpWorkspaceImportDto> RecentImports { get; init; } = new();
    public LineUpWorkspaceSummaryDto Summary { get; init; } = new();
}

public sealed class LineUpWorkspaceLineDto
{
    public long Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed class LineUpWorkspaceDeviceDto
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string DeviceType { get; init; } = string.Empty;
    public int RegistrationStatus { get; init; }
}

public sealed class LineUpWorkspaceImportDto
{
    public long BatchId { get; init; }
    public long LineId { get; init; }
    public long DeviceId { get; init; }
    public string SourceFileName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public int PreviewRevision { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? CommittedAt { get; init; }
    public LineUpImportSummaryDto Summary { get; init; } = new();
}

public sealed class LineUpWorkspaceSummaryDto
{
    public int LineCount { get; init; }
    public int DeviceCount { get; init; }
    public int RecentImportCount { get; init; }
    public int PreviewReadyCount { get; init; }
    public int CommittedCount { get; init; }
    public int FailedCount { get; init; }
}
