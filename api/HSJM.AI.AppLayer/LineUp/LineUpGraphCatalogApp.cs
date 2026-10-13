#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.AppLayer.LineUp.Runtime;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp;

public sealed class LineUpGraphCatalogApp : ISelfScopedAutoInject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 128
    };

    private readonly ILineUpGraphCatalogRepository repository;

    public LineUpGraphCatalogApp(ILineUpGraphCatalogRepository repository)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<LineUpGraphCatalogLookupDto> GetLookupsAsync(
        long? plcProjectId,
        long? lineId,
        CancellationToken cancellationToken)
    {
        ValidateOptionalId(plcProjectId);
        ValidateOptionalId(lineId);
        var data = await repository.LoadLookupsAsync(plcProjectId, lineId, cancellationToken);
        return new(
            data.Projects.Select(item => new LineUpGraphCatalogProjectDto(item.Id, item.Name, item.PlcDeviceId)).ToArray(),
            data.Lines.Select(item => new LineUpGraphCatalogLineDto(item.Id, item.Code, item.Name)).ToArray(),
            data.Processes.Select(item => new LineUpGraphCatalogProcessDto(item.Id, item.LineId, item.Code, item.Name)).ToArray(),
            data.Stations.Select(item => new LineUpGraphCatalogStationDto(item.Id, item.LineId, item.ProcessId, item.Code, item.Name)).ToArray(),
            data.SubStations.Select(item => new LineUpGraphCatalogSubStationDto(
                item.Id, item.LineId, item.ProcessId, item.StationId,
                item.Code, item.Name, item.PlcId, item.PlcName)).ToArray(),
            data.Devices.Select(item => new LineUpGraphCatalogDeviceDto(item.Id, item.LineId, item.Name, item.ProjectName, item.DeviceType)).ToArray());
    }

    public async Task<LineUpGraphCatalogPageDto> QueryAsync(
        LineUpGraphCatalogQueryDto query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1 || query.PageSize is < 1 or > 100)
        {
            throw Error("GraphCatalogQueryInvalid", "The catalog page is invalid.");
        }

        foreach (var id in new[] { query.PlcProjectId, query.LineId, query.ProcessId, query.StationId, query.SubStationId, query.DeviceId })
        {
            ValidateOptionalId(id);
        }

        var status = NormalizeStatus(query.Status);
        var rows = await repository.QueryAssetsAsync(new(
            query.PlcProjectId,
            query.LineId,
            query.ProcessId,
            query.StationId,
            query.SubStationId,
            query.DeviceId,
            status,
            string.IsNullOrWhiteSpace(query.Keyword) ? null : query.Keyword.Trim(),
            checked((query.Page - 1) * query.PageSize),
            query.PageSize), cancellationToken);

        return new(
            rows.Select(MapAsset).ToArray(),
            rows.FirstOrDefault()?.TotalCount ?? 0,
            query.Page,
            query.PageSize);
    }

    public async Task<LineUpGraphCatalogDetailDto> GetDetailAsync(
        long assetId,
        long? bindingId,
        CancellationToken cancellationToken)
    {
        ValidateId(assetId);
        ValidateOptionalId(bindingId);
        var data = await repository.LoadDetailAsync(assetId, bindingId, cancellationToken)
            ?? throw Error("GraphAssetMissing", "The GRAPH asset does not exist.");
        return ProjectDetail(data);
    }

    public async Task<LineUpGraphBindingDto> SaveBindingAsync(
        long assetId,
        LineUpGraphBindingRequest request,
        CancellationToken cancellationToken)
    {
        ValidateId(assetId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateId(request.LineId);
        ValidateId(request.ProcessId);
        ValidateId(request.StationId);
        ValidateId(request.SubStationId);
        ValidateId(request.DeviceId);
        ValidateOptionalId(request.InstanceDbBlockId);

        var detail = await repository.LoadDetailAsync(assetId, null, cancellationToken)
            ?? throw Error("GraphAssetMissing", "The GRAPH asset does not exist.");
        var lookup = await repository.LoadLookupsAsync(detail.Asset.PlcProjectId, request.LineId, cancellationToken);
        var project = lookup.Projects.SingleOrDefault(item => item.Id == detail.Asset.PlcProjectId)
            ?? throw Error("GraphBindingProjectMismatch", "The PLC project is unavailable for this GRAPH asset.");
        var process = lookup.Processes.SingleOrDefault(item => item.Id == request.ProcessId);
        var station = lookup.Stations.SingleOrDefault(item => item.Id == request.StationId);
        var subStation = lookup.SubStations.SingleOrDefault(item => item.Id == request.SubStationId);
        var device = lookup.Devices.SingleOrDefault(item => item.Id == request.DeviceId);
        var lineExists = lookup.Lines.Any(item => item.Id == request.LineId);
        if (!lineExists || process?.LineId != request.LineId ||
            station?.LineId != request.LineId || station.ProcessId != request.ProcessId ||
            subStation?.LineId != request.LineId || subStation.ProcessId != request.ProcessId ||
            subStation.StationId != request.StationId ||
            device?.LineId != request.LineId)
        {
            throw Error("GraphBindingInvalid", "The selected line, process, station, work position and device are inconsistent.");
        }

        // 设备归属校验：项目详情页绑定的设备 ID（plcdeviceid）优先，名字精确匹配兜底（历史数据）
        if (project.PlcDeviceId != device.Id && !SameName(project.Name, device.ProjectName))
        {
            throw Error("GraphBindingProjectMismatch", "The selected device belongs to another PLC project.");
        }

        var saved = await repository.SaveBindingAsync(new(
            assetId,
            request.LineId,
            request.ProcessId,
            request.StationId,
            request.SubStationId,
            request.DeviceId,
            request.InstanceDbBlockId,
            "Unresolved"), cancellationToken);
        return MapBinding(saved);
    }

    public async Task<IReadOnlyList<LineUpGraphAliasDto>> SaveAliasesAsync(
        long bindingId,
        LineUpGraphAliasSaveRequest request,
        CancellationToken cancellationToken)
    {
        ValidateId(bindingId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateId(request.ExpectedLatestRevisionId);
        var detail = await LoadBindingAsync(bindingId, cancellationToken);
        if (detail.Asset.LatestRevisionId != request.ExpectedLatestRevisionId)
        {
            throw Error("GraphRevisionStale", "A newer GRAPH revision is available.");
        }

        var target = FindRevision(detail, request.ExpectedLatestRevisionId);
        var aliases = BuildAliasDictionary(request.Aliases);
        _ = LineUpGraphAliasProjector.Apply(DeserializePreview(target), aliases);
        IReadOnlyList<LineUpGraphAliasData> saved;
        try
        {
            saved = await repository.ReplaceAliasesAsync(new(
                bindingId,
                request.ExpectedLatestRevisionId,
                aliases.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new LineUpGraphAliasValue(item.Key, item.Value))
                    .ToArray()), cancellationToken);
        }
        catch (LineUpGraphCatalogPersistenceException error)
        {
            throw Error(error.Code, "The GRAPH aliases changed concurrently.");
        }
        return saved.Select(item => new LineUpGraphAliasDto(item.ElementSourceKey, item.DisplayName)).ToArray();
    }

    public async Task<LineUpGraphPublishPreviewDto> PreviewPublishAsync(
        long bindingId,
        long targetRevisionId,
        CancellationToken cancellationToken)
    {
        ValidateId(bindingId);
        ValidateId(targetRevisionId);
        var detail = await LoadBindingAsync(bindingId, cancellationToken);
        var target = FindRevision(detail, targetRevisionId);
        var published = detail.Binding!.PublishedRevisionId is long publishedId
            ? FindRevision(detail, publishedId)
            : null;
        var aliases = detail.Aliases.ToDictionary(item => item.ElementSourceKey, item => item.DisplayName, StringComparer.Ordinal);
        var targetPreview = DeserializePreview(target);
        ValidateForPublication(targetPreview);
        var publishedPreview = published is null ? null : DeserializePreview(published);
        if (publishedPreview is not null)
        {
            ValidateForPublication(publishedPreview);
        }
        return new(
            bindingId,
            target.Id,
            target.RevisionNo,
            published?.Id,
            published?.RevisionNo,
            LineUpGraphRevisionComparer.Compare(publishedPreview, targetPreview),
            SafePreview(target, aliases));
    }

    public async Task<LineUpGraphPublishReceiptDto> PublishAsync(
        long bindingId,
        LineUpGraphPublishRequest request,
        long? userId,
        CancellationToken cancellationToken)
    {
        ValidateId(bindingId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateId(request.TargetRevisionId);
        ValidateId(request.ExpectedLatestRevisionId);
        var detail = await LoadBindingAsync(bindingId, cancellationToken);
        if (detail.Asset.LatestRevisionId != request.ExpectedLatestRevisionId)
        {
            throw Error("GraphRevisionStale", "A newer GRAPH revision is available.");
        }
        if (detail.Binding!.PublishedRevisionId != request.ExpectedPublishedRevisionId)
        {
            throw Error("GraphPublishConflict", "The published revision changed after preview.");
        }

        var target = FindRevision(detail, request.TargetRevisionId);
        ValidateForPublication(DeserializePreview(target));
        LineUpGraphPublishData saved;
        try
        {
            saved = await repository.PublishAsync(new(
                bindingId,
                request.TargetRevisionId,
                request.ExpectedLatestRevisionId,
                request.ExpectedPublishedRevisionId,
                userId is > 0 ? userId : null,
                DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)), cancellationToken);
        }
        catch (LineUpGraphCatalogPersistenceException error)
        {
            throw Error(error.Code, "The GRAPH publication changed concurrently.");
        }
        return new(
            saved.BindingId,
            saved.PublishedRevisionId,
            saved.PublishedRevisionNo,
            saved.PreviousPublishedRevisionId,
            saved.PublishedAt,
            saved.MonitoringEnabled);
    }

    private async Task<LineUpGraphCatalogDetailData> LoadBindingAsync(
        long bindingId,
        CancellationToken cancellationToken) =>
        await repository.LoadBindingDetailAsync(bindingId, cancellationToken)
        ?? throw Error("GraphBindingMissing", "The GRAPH station binding does not exist.");

    private static LineUpGraphCatalogDetailDto ProjectDetail(LineUpGraphCatalogDetailData data)
    {
        if (data.Asset.LatestRevisionId is not long latestRevisionId)
        {
            throw Error("GraphRevisionMissing", "The GRAPH asset has no parsed revision.");
        }

        var latest = FindRevision(data, latestRevisionId);
        var aliases = data.Aliases.ToDictionary(item => item.ElementSourceKey, item => item.DisplayName, StringComparer.Ordinal);
        return new(
            MapAsset(data.Asset),
            data.Revisions.OrderByDescending(item => item.RevisionNo).Select(MapRevision).ToArray(),
            data.Binding is null ? null : MapBinding(data.Binding),
            data.Aliases.OrderBy(item => item.ElementSourceKey, StringComparer.Ordinal)
                .Select(item => new LineUpGraphAliasDto(item.ElementSourceKey, item.DisplayName)).ToArray(),
            SafePreview(latest, aliases));
    }

    private static LineUpMonitorViewerPreviewDto SafePreview(
        LineUpGraphRevisionData revision,
        IReadOnlyDictionary<string, string> aliases)
    {
        try
        {
            return LineUpPublishedGraphProjector.ProjectViewer(
                revision.PreviewJson,
                revision.ParserSchemaVersion,
                aliases);
        }
        catch (LineUpPublishedGraphProjectionException error)
        {
            throw Error(error.Code, error.SafeSummary);
        }
    }

    private static Dictionary<string, string> BuildAliasDictionary(IReadOnlyList<LineUpGraphAliasValueDto>? values)
    {
        if (values is null)
        {
            throw Error("GraphAliasInvalid", "The alias list is required.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            var key = item.ElementSourceKey?.Trim();
            if (string.IsNullOrWhiteSpace(key) || !result.TryAdd(key, item.DisplayName))
            {
                throw Error("GraphAliasInvalid", "Alias source keys must be nonempty and unique.");
            }
        }
        return result;
    }

    private static LineUpGraphRevisionData FindRevision(LineUpGraphCatalogDetailData detail, long revisionId)
    {
        var matches = detail.Revisions.Where(item => item.Id == revisionId && item.GraphAssetId == detail.Asset.AssetId).ToArray();
        if (matches.Length != 1 || string.Equals(matches[0].Status, "ParseFailed", StringComparison.Ordinal))
        {
            throw Error("GraphRevisionMissing", "The selected GRAPH revision does not exist for this asset.");
        }
        return matches[0];
    }

    private static LineUpGraphDraftPreview DeserializePreview(LineUpGraphRevisionData revision)
    {
        try
        {
            var preview = LineUpPublishedGraphProjector.Deserialize(revision.PreviewJson);
            LineUpPublishedGraphProjector.ValidateSchema(preview, revision.ParserSchemaVersion);
            return preview;
        }
        catch (LineUpPublishedGraphProjectionException error)
        {
            throw Error(error.Code, error.SafeSummary);
        }
    }

    private static void ValidateForPublication(LineUpGraphDraftPreview preview)
    {
        try
        {
            LineUpGraphPreviewValidator.ValidateForPublication(preview);
        }
        catch (LineUpGraphPreviewValidationException error)
        {
            throw Error(error.Code, error.SafeSummary);
        }
    }

    private static LineUpGraphCatalogAssetDto MapAsset(LineUpGraphCatalogAssetData item) => new(
        item.AssetId,
        item.PlcProjectId,
        item.PlcProjectName,
        item.SourceBlockName,
        item.BlockType,
        item.BlockNumber,
        item.CurrentPlcBlockId,
        item.LatestRevisionId,
        item.LatestRevisionNo,
        item.LatestRevisionStatus,
        DeserializeSummary(item.SummaryJson),
        item.BindingId,
        item.LineId,
        item.ProcessId,
        item.StationId,
        item.SubStationId,
        item.DeviceId,
        item.PublishedRevisionId,
        item.PublishedRevisionNo,
        item.BindingStatus,
        item.MonitoringEnabled,
        item.HasPreviewBatch);

    private static LineUpImportSummaryDto DeserializeSummary(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LineUpImportSummaryDto>(json, JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            throw Error("GraphPreviewInvalid", "The stored GRAPH summary cannot be read.");
        }
    }

    private static LineUpGraphRevisionDto MapRevision(LineUpGraphRevisionData item) => new(
        item.Id,
        item.RevisionNo,
        item.Status,
        item.SourceHash,
        item.ParserSchemaVersion,
        item.PublishedAt,
        item.PublishedBy);

    private static LineUpGraphBindingDto MapBinding(LineUpGraphBindingData item) => new(
        item.Id,
        item.GraphAssetId,
        item.LineId,
        item.ProcessId,
        item.StationId,
        item.SubStationId,
        item.DeviceId,
        item.InstanceDbBlockId,
        item.PublishedRevisionId,
        item.BindingStatus,
        item.MonitoringEnabled,
        item.ModifyTime);

    private static string? NormalizeStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "All", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized is not ("Unbound" or "DraftPending" or "Published"))
        {
            throw Error("GraphCatalogQueryInvalid", "The catalog status filter is invalid.");
        }
        return normalized;
    }

    private static bool SameName(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void ValidateId(long value)
    {
        if (value <= 0)
        {
            throw Error("GraphCatalogInvalidId", "The catalog identifier is invalid.");
        }
    }

    private static void ValidateOptionalId(long? value)
    {
        if (value is <= 0)
        {
            throw Error("GraphCatalogInvalidId", "The catalog identifier is invalid.");
        }
    }

    private static LineUpImportException Error(string code, string summary) =>
        new(code, "GRAPH", summary);
}
