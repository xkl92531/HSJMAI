using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.LineUp;

public sealed class LineUpWorkspaceApp(ILineUpWorkspaceRepository repository) : ISelfScopedAutoInject
{
    private const int RecentImportLimit = 20;

    public async Task<LineUpWorkspaceDto> GetAsync(
        long? lineId,
        CancellationToken cancellationToken)
    {
        if (lineId is <= 0)
        {
            throw new LineUpImportException(
                "InvalidSelection", "workspace", "The selected production line is invalid.");
        }

        var data = await repository.LoadAsync(lineId, RecentImportLimit, cancellationToken);
        if (lineId is > 0 && data.SelectedLineId != lineId)
        {
            throw new LineUpImportException(
                "InvalidSelection", "workspace", "The selected production line is unavailable or disabled.");
        }

        var imports = data.RecentImports.Select(x => new LineUpWorkspaceImportDto
        {
            BatchId = x.BatchId,
            LineId = x.LineId,
            DeviceId = x.DeviceId,
            SourceFileName = x.SourceFileName,
            Status = x.Status,
            ErrorCode = x.ErrorCode,
            PreviewRevision = x.PreviewRevision,
            CreatedAt = x.CreatedAt,
            CommittedAt = x.CommittedAt,
            Summary = ParseSummary(x.SummaryJson)
        }).ToList();
        return new()
        {
            Lines = data.Lines.Select(x => new LineUpWorkspaceLineDto
            {
                Id = x.Id,
                Code = x.Code?.Trim() ?? string.Empty,
                Name = x.Name?.Trim() ?? string.Empty
            }).ToList(),
            SelectedLineId = data.SelectedLineId,
            Devices = data.Devices.Select(x => new LineUpWorkspaceDeviceDto
            {
                Id = x.Id,
                Name = x.Name?.Trim() ?? string.Empty,
                ProjectName = x.ProjectName?.Trim() ?? string.Empty,
                DeviceType = x.DeviceType?.Trim() ?? string.Empty,
                RegistrationStatus = x.RegistrationStatus
            }).ToList(),
            RecentImports = imports,
            Summary = new()
            {
                LineCount = data.Lines.Count,
                DeviceCount = data.Devices.Count,
                RecentImportCount = imports.Count,
                PreviewReadyCount = imports.Count(x => string.Equals(x.Status, "PreviewReady", StringComparison.Ordinal)),
                CommittedCount = imports.Count(x => string.Equals(x.Status, "Committed", StringComparison.Ordinal)),
                FailedCount = imports.Count(x => string.Equals(x.Status, "Failed", StringComparison.Ordinal))
            }
        };
    }

    private static LineUpImportSummaryDto ParseSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<LineUpImportSummaryDto>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
