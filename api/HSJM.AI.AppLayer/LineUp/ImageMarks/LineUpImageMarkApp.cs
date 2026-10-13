using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.AppLayer.LineUp.ImageMarks;

public sealed class LineUpImageMarkException(string code, string safeSummary) : Exception(safeSummary)
{
    public string Code { get; } = code;
}

public sealed record LineUpImageHierarchyDto(
    long Id,
    string? Code,
    string? Name,
    long WorkStationId,
    string? WorkStationCode,
    string? WorkStationName,
    long ProcessId,
    string? ProcessCode,
    string? ProcessName,
    long ProductionLineId,
    string? ProductionLineCode,
    string? ProductionLineName);

public sealed record LineUpImageDto(
    long Id,
    long SubStationId,
    string? PictureName,
    string? ContentUrl,
    string? CurrentContentUrl,
    string? ContentSha256,
    long? ByteLength,
    int? PixelWidth,
    int? PixelHeight,
    string? DetectedMimeType,
    long RowVersion);

public sealed record LineUpImageBomDto(
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

public sealed record LineUpPlcDeviceDto(
    long Id,
    string? Code,
    string? Name,
    string? Ip);

public sealed record LineUpImageMarkerDto(
    long Id,
    long ImageId,
    string? MarkType,
    bool IsSingleMark,
    LineUpMarkerPoint? Point,
    LineUpMarkerBillboard? Billboard,
    LineUpMarkerLine? Line,
    IReadOnlyList<long> BomIds,
    IReadOnlyList<LineUpMarkerIssue> Issues);

public sealed record LineUpImageWorkspaceDto(
    LineUpImageHierarchyDto SubStation,
    IReadOnlyList<LineUpImageDto> Images,
    IReadOnlyList<LineUpImageBomDto> Boms,
    IReadOnlyList<LineUpImageMarkerDto> Markers);

public sealed record LineUpImageMarkerWriteDto(
    long? Id,
    string? MarkType,
    bool IsSingleMark,
    LineUpMarkerPoint? Point,
    LineUpMarkerBillboard? Billboard,
    LineUpMarkerLine? Line,
    IReadOnlyList<long> BomIds);

public sealed record LineUpImageSaveRequest(
    long RowVersion,
    IReadOnlyList<LineUpImageMarkerWriteDto> Markers);

public sealed record LineUpImageSaveDto(long Id, long RowVersion, string? CurrentContentUrl);

public sealed record LineUpBomWriteRequest(
    string? RefNo,
    string? TagCode,
    string? Name,
    string? DbAddress,
    string? ModelNo,
    string? Brand,
    string? IoAddress,
    string? PartNo,
    long? PlcId);

public sealed record LineUpBomMutationDto(
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
    string? PlcIp);

public sealed record LineUpFaultLocationDto(
    IReadOnlyList<LineUpImageBomDto> Boms,
    IReadOnlyList<LineUpImageDto> Images,
    IReadOnlyList<LineUpImageMarkerDto> Markers);

public sealed record LineUpImageContent(
    Stream Stream,
    string ContentType,
    string DownloadName);

public sealed class LineUpImageMarkApp(
    ILineUpImageMarkRepository repository,
    ILineUpImageStorage storage,
    LineUpLegacyMarkerParser markerParser) : ISelfScopedAutoInject
{
    private const int MaxMarkers = 1000;
    private const int MaxTextLength = 2000;

    public async Task<LineUpImageWorkspaceDto> GetStationAsync(
        long subStationId,
        string? bomKeyword,
        CancellationToken cancellationToken)
    {
        if (subStationId <= 0) throw Error("LineUpInvalidSubStation", "The substation is invalid.");
        var data = await repository.GetStationWorkspaceAsync(subStationId, cancellationToken);
        if (data is null || data.Hierarchy.SubStationId != subStationId)
            throw Error("LineUpSubStationNotFound", "The substation does not exist.");
        return ToWorkspace(data, bomKeyword);
    }

    public async Task<LineUpImageDto> UploadImageAsync(
        long subStationId,
        string fileName,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (subStationId <= 0) throw Error("LineUpInvalidSubStation", "The substation is invalid.");
        var hierarchy = await repository.GetHierarchyAsync(subStationId, cancellationToken);
        if (hierarchy is null || hierarchy.SubStationId != subStationId)
            throw Error("LineUpSubStationNotFound", "The substation does not exist.");
        var normalizedName = fileName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) ||
            !string.Equals(normalizedName, Path.GetFileName(normalizedName), StringComparison.Ordinal))
            throw Error("LineUpInvalidImageFileName", "The image file name is invalid.");

        var imageId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        var stored = await storage.StoreAsync(
            source, new(imageId, normalizedName), cancellationToken);
        var create = new LineUpImageCreateData(
            imageId,
            hierarchy,
            normalizedName,
            stored.StorageKey,
            stored.ContentSha256,
            stored.ByteLength,
            stored.PixelWidth,
            stored.PixelHeight,
            stored.DetectedMimeType,
            1);
        var createdId = await repository.CreateImageAsync(create, cancellationToken);
        if (createdId != imageId)
            throw Error("LineUpImageCreateFailed", "The image could not be created.");
        return ToDto(new LineUpImageData(
            imageId, subStationId, normalizedName, stored.StorageKey, null,
            stored.ContentSha256, stored.ByteLength, stored.PixelWidth, stored.PixelHeight,
            stored.DetectedMimeType, 1));
    }

    public async Task<LineUpImageSaveDto> SaveImageAsync(
        long imageId,
        LineUpImageSaveRequest request,
        Stream? renderedImage,
        string? renderedFileName,
        CancellationToken cancellationToken)
    {
        if (imageId <= 0 || request is null)
            throw Error("LineUpInvalidImage", "The image is invalid.");
        if (request.RowVersion <= 0)
            throw Error("LineUpInvalidRowVersion", "The image version is invalid.");
        if (request.Markers is null || request.Markers.Count > MaxMarkers)
            throw Error("LineUpInvalidMarkers", "The marker collection is invalid.");
        var image = await repository.GetImageAsync(imageId, cancellationToken);
        if (image is null) throw Error("LineUpImageNotFound", "The image does not exist.");
        if (image.RowVersion != request.RowVersion)
            throw Error("LineUpImageVersionConflict", "The image was changed by another user.");

        var build = await BuildMarkerWritesAsync(image, request.Markers, cancellationToken);
        string? currentStorageKey = null;
        if (renderedImage is not null)
        {
            var normalizedName = renderedFileName?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedName) ||
                normalizedName != Path.GetFileName(normalizedName))
                throw Error("LineUpInvalidImageFileName", "The rendered image file name is invalid.");
            var rendered = await storage.StoreAsync(
                renderedImage, new(imageId, normalizedName), cancellationToken);
            currentStorageKey = rendered.StorageKey;
        }
        else if (!string.IsNullOrWhiteSpace(renderedFileName))
        {
            throw Error("LineUpInvalidImage", "The rendered image content is missing.");
        }

        // 上图即绑定：本次挂上图的产线直挂点位，随标记同事务绑定为当前子工站
        LineUpBomSubStationBinding? poolBinding = null;
        if (build.StationPoolBomIds.Count > 0)
        {
            var hierarchy = await repository.GetHierarchyAsync(image.SubStationId, cancellationToken)
                ?? throw Error("LineUpSubStationNotFound", "The substation does not exist.");
            poolBinding = new LineUpBomSubStationBinding(build.StationPoolBomIds, hierarchy);
        }

        var result = await repository.SaveImageAsync(
            new(imageId, request.RowVersion, currentStorageKey, build.Writes, poolBinding), cancellationToken);
        switch (result)
        {
            case LineUpImageSaveResult.Saved:
                return new(imageId, request.RowVersion + 1,
                    currentStorageKey is null ? ContentUrl(imageId, true, image.CurrentStorageKey) : ContentUrl(imageId, true, currentStorageKey));
            case LineUpImageSaveResult.VersionConflict:
                throw Error("LineUpImageVersionConflict", "The image was changed by another user.");
            case LineUpImageSaveResult.MarkerOwnershipConflict:
                throw Error("LineUpMarkerOwnershipMismatch", "A marker does not belong to this image.");
            default:
                throw Error("LineUpImageNotFound", "The image does not exist.");
        }
    }

    public async Task DeleteImageAsync(long imageId, CancellationToken cancellationToken)
    {
        if (imageId <= 0) throw Error("LineUpInvalidImage", "The image is invalid.");
        if (!await repository.SoftDeleteImageAsync(imageId, cancellationToken))
            throw Error("LineUpImageNotFound", "The image does not exist.");
    }

    public async Task<LineUpFaultLocationDto> GetFaultLocationAsync(
        string? tagCode,
        long? bomId,
        CancellationToken cancellationToken)
    {
        var normalizedTag = string.IsNullOrWhiteSpace(tagCode) ? null : tagCode.Trim();
        if (normalizedTag is null && bomId is not > 0)
            throw Error("LineUpFaultLookupRequired", "A tag code or BOM identity is required.");
        var data = await repository.FindFaultCandidatesAsync(normalizedTag, bomId, cancellationToken);
        if (data is null) return new([], [], []);
        var boms = data.Boms.Where(bom =>
                (!bomId.HasValue || bom.Id == bomId.Value) &&
                (normalizedTag is null || string.Equals(bom.TagCode, normalizedTag, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(bom => bom.Id)
            .ToArray();
        var bomIds = boms.Select(bom => bom.Id).ToHashSet();
        var markers = data.Markers.Select(ToDto)
            .Where(marker => marker.BomIds.Any(bomIds.Contains))
            .OrderBy(marker => marker.Id)
            .ToArray();
        var imageIds = markers.Select(marker => marker.ImageId).ToHashSet();
        var images = data.Images.Where(image => imageIds.Contains(image.Id))
            .Select(ToDto).OrderBy(image => image.Id).ToArray();
        return new(boms.Select(ToDto).ToArray(), images, markers);
    }

    public async Task<LineUpImageContent> OpenContentAsync(
        long imageId,
        bool current,
        CancellationToken cancellationToken)
    {
        if (imageId <= 0) throw Error("LineUpInvalidImage", "The image is invalid.");
        var image = await repository.GetImageAsync(imageId, cancellationToken);
        if (image is null) throw Error("LineUpImageNotFound", "The image does not exist.");
        var key = current ? image.CurrentStorageKey : image.StorageKey;
        if (string.IsNullOrWhiteSpace(key))
            throw Error("LineUpImageContentMissing", "The image content does not exist.");
        var stream = await storage.OpenReadAsync(key, cancellationToken);
        var extension = Path.GetExtension(key).ToLowerInvariant();
        var mime = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => throw Error("LineUpInvalidImage", "The image type is invalid.")
        };
        return new(stream, mime, $"lineup-{imageId}{extension}");
    }

    private sealed record MarkerBuildResult(
        IReadOnlyList<LineUpImageMarkerSaveData> Writes,
        IReadOnlyList<long> StationPoolBomIds);

    private async Task<MarkerBuildResult> BuildMarkerWritesAsync(
        LineUpImageData image,
        IReadOnlyList<LineUpImageMarkerWriteDto> markers,
        CancellationToken cancellationToken)
    {
        var duplicateIds = markers.Where(marker => marker.Id.HasValue)
            .GroupBy(marker => marker.Id!.Value).Any(group => group.Count() > 1);
        if (duplicateIds) throw Error("LineUpDuplicateMarker", "A marker identity is duplicated.");
        var bomIds = markers.SelectMany(marker => marker.BomIds ?? [])
            .Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        if (markers.Any(marker => marker.BomIds is null || marker.BomIds.Count == 0 ||
                                  marker.BomIds.Any(id => id <= 0)))
            throw Error("LineUpInvalidBom", "A marker BOM identity is invalid.");
        var boms = await repository.GetBomsByIdsAsync(bomIds, cancellationToken);
        if (boms.Select(bom => bom.Id).Distinct().Count() != bomIds.Length ||
            boms.Any(bom => bom.StationId is not null && bom.StationId != image.SubStationId))
            throw Error("LineUpBomOwnershipMismatch", "A BOM does not belong to this substation.");
        // 产线直挂（无工站）点位允许上图（2026-09-29 v2 产线级池）：校验其归属本子工站所在产线
        var stationPoolBomIds = Array.Empty<long>();
        var linePoolBoms = boms.Where(bom => bom.StationId is null).ToArray();
        if (linePoolBoms.Length > 0)
        {
            var hierarchy = await repository.GetHierarchyAsync(image.SubStationId, cancellationToken);
            var lineBomIds = hierarchy is null
                ? new HashSet<long>()
                : (await repository.GetBomsByLineAsync(hierarchy.ProductionLineId, cancellationToken))
                    .Select(bom => bom.Id).ToHashSet();
            if (linePoolBoms.Any(bom => !lineBomIds.Contains(bom.Id)))
                throw Error("LineUpBomOwnershipMismatch", "A BOM does not belong to this substation.");
            stationPoolBomIds = linePoolBoms.Select(bom => bom.Id).ToArray();
        }

        var result = new List<LineUpImageMarkerSaveData>(markers.Count);
        foreach (var marker in markers)
        {
            ValidateMarker(marker, image.PixelWidth, image.PixelHeight);
            var ids = marker.BomIds.Distinct().OrderBy(id => id).ToArray();
            var markerId = marker.Id ?? GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
            var document = new LineUpMarkerDocument
            {
                MarkerId = markerId,
                StationImageId = image.Id,
                MarkType = Limited(marker.MarkType),
                IsSingleMark = marker.IsSingleMark,
                Point = marker.Point,
                Billboard = marker.Billboard,
                Line = marker.Line,
                BomIds = ids,
                RawMcpConfig = Json(marker.Point),
                RawMarkTagConfig = Json(marker.Billboard),
                RawMarkTagLineConfig = Json(marker.Line),
                RawMarkTagBom = string.Join(',', ids)
            };
            result.Add(new(
                markerId,
                document.MarkType,
                marker.IsSingleMark,
                document.RawMcpConfig,
                document.RawMarkTagConfig,
                document.RawMarkTagLineConfig,
                document.RawMarkTagBom,
                document.SchemaVersion,
                JsonSerializer.Serialize(document)));
        }

        return new MarkerBuildResult(result, stationPoolBomIds);
    }

    private static void ValidateMarker(
        LineUpImageMarkerWriteDto marker,
        int? pixelWidth,
        int? pixelHeight)
    {
        if (marker.Point is null && marker.Billboard is null && marker.Line is null)
            throw Error("LineUpInvalidMarker", "A marker geometry is required.");
        if (marker.Point is not null &&
            (marker.Point.X is null || marker.Point.Y is null) ||
            marker.Billboard is not null &&
            (marker.Billboard.X is null || marker.Billboard.Y is null ||
             marker.Billboard.Width is null or <= 0 || marker.Billboard.Height is null or <= 0) ||
            marker.Line is not null &&
            (marker.Line.StartX is null || marker.Line.StartY is null ||
             marker.Line.EndX is null || marker.Line.EndY is null))
            throw Error("LineUpInvalidMarker", "A marker geometry is incomplete.");
        ValidateText(marker.Point?.Id);
        ValidateText(marker.Point?.Description);
        ValidateText(marker.Billboard?.Id);
        ValidateText(marker.Billboard?.Text);
        ValidateText(marker.Billboard?.Description);
        ValidateText(marker.Line?.Id);
        if (pixelWidth is not > 0 || pixelHeight is not > 0) return;
        // 点位必须落在图片内；标签与连线允许布置在图片边缘外（标注惯例：标签挂图外，连线牵引）。
        if (marker.Point is { X: { } x, Y: { } y } && !Inside(x, y, pixelWidth.Value, pixelHeight.Value))
            throw Error("LineUpMarkerOutOfBounds", "A marker is outside the image.");
    }

    private static bool Inside(decimal x, decimal y, int width, int height) =>
        x >= 0 && y >= 0 && x <= width && y <= height;

    private static void ValidateText(string? value)
    {
        if (value?.Length > MaxTextLength)
            throw Error("LineUpMarkerTextTooLong", "A marker value is too long.");
    }

    private static string? Limited(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= 100
            ? value.Trim()
            : throw Error("LineUpMarkerTextTooLong", "A marker value is too long.");

    private static string? Json<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value);

    private LineUpImageWorkspaceDto ToWorkspace(LineUpImageWorkspaceData data, string? keyword)
    {
        var normalized = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var boms = data.Boms.Where(bom => normalized is null ||
                Contains(bom.RefNo, normalized) || Contains(bom.Name, normalized) ||
                Contains(bom.TagCode, normalized))
            .OrderBy(bom => bom.RefNo).ThenBy(bom => bom.Id).Select(ToDto).ToArray();
        return new(
            ToDto(data.Hierarchy),
            data.Images.OrderBy(image => image.PictureName).ThenBy(image => image.Id).Select(ToDto).ToArray(),
            boms,
            data.Markers.OrderBy(marker => marker.Id).Select(ToDto).ToArray());
    }

    private static bool Contains(string? value, string keyword) =>
        value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;

    private static LineUpImageHierarchyDto ToDto(LineUpImageHierarchyData data) => new(
        data.SubStationId, data.SubStationCode, data.SubStationName,
        data.WorkStationId, data.WorkStationCode, data.WorkStationName,
        data.ProcessId, data.ProcessCode, data.ProcessName,
        data.ProductionLineId, data.ProductionLineCode, data.ProductionLineName);

    private static LineUpImageDto ToDto(LineUpImageData data) => new(
        data.Id,
        data.SubStationId,
        data.PictureName,
        ContentUrl(data.Id, false, data.StorageKey),
        ContentUrl(data.Id, true, data.CurrentStorageKey),
        data.ContentSha256,
        data.ByteLength,
        data.PixelWidth,
        data.PixelHeight,
        data.DetectedMimeType,
        data.RowVersion);

    private static LineUpImageBomDto ToDto(LineUpImageBomData data) => new(
        data.Id, data.StationId, data.RefNo, data.Name, data.TagCode, data.DbAddress,
        data.ModelNo, data.Brand, data.IoAddress, data.PartNo, data.PlcId, data.PlcName, data.PlcIp,
        data.IsAssociated);

    private LineUpImageMarkerDto ToDto(LineUpStationImageMarkEntity entity)
    {
        var document = markerParser.Parse(entity);
        return new(
            entity.Id,
            entity.StationImageId ?? 0,
            entity.MarkType,
            entity.IsSingleMark == true,
            document.Point,
            document.Billboard,
            document.Line,
            document.BomIds,
            document.Issues);
    }

    private static string? ContentUrl(long imageId, bool current, string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : $"/LineUp/ImageMarks/Images/{imageId}/Content" + (current ? "?current=true" : string.Empty);

    public async Task<LineUpBomMutationDto> CreateBomAsync(
        long subStationId,
        LineUpBomWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (subStationId <= 0) throw Error("LineUpInvalidSubStation", "The substation is invalid.");
        if (request is null) throw Error("LineUpInvalidBom", "The BOM payload is invalid.");
        var hierarchy = await repository.GetHierarchyAsync(subStationId, cancellationToken);
        if (hierarchy is null || hierarchy.SubStationId != subStationId)
            throw Error("LineUpSubStationNotFound", "The substation does not exist.");
        var refNo = NormalizeBomText(request.RefNo);
        var tagCode = NormalizeBomText(request.TagCode);
        var describe = NormalizeBomText(request.Name);
        var dbAddress = NormalizeBomText(request.DbAddress);
        var modelNo = NormalizeBomText(request.ModelNo);
        var brand = NormalizeBomText(request.Brand);
        var ioAddress = NormalizeBomText(request.IoAddress);
        var partNo = NormalizeBomText(request.PartNo);
        if (refNo is null && tagCode is null)
            throw Error("LineUpInvalidBom", "RefNo or TagCode is required.");
        var plc = await ResolvePlcAsync(request.PlcId, cancellationToken);
        var workspace = await GetStationAsync(subStationId, null, cancellationToken);
        if (tagCode is not null && workspace.Boms.Any(bom =>
                string.Equals(NormalizeBomText(bom.TagCode), tagCode, StringComparison.OrdinalIgnoreCase)))
            throw Error("LineUpBomTagCodeDuplicated", "The tag code already exists in this substation.");
        var entity = new LineUpBomDirectoryEntity
        {
            RefNo = refNo,
            Describe = describe,
            TagCode = tagCode,
            DBAddress = dbAddress,
            ModelNo = modelNo,
            Brand = brand,
            IOAddress = ioAddress,
            MESPN = partNo,
            PlcId = plc?.Id,
            PlcCode = plc?.Code,
            PlcName = plc?.Name,
            PLCIP = plc?.Ip,
            StationId = subStationId,
            StationCode = hierarchy.SubStationCode,
            StationName = hierarchy.SubStationName,
            ProductionLineId = hierarchy.ProductionLineId,
            ProductionLineCode = hierarchy.ProductionLineCode,
            ProductionLineName = hierarchy.ProductionLineName,
            ProcessId = hierarchy.ProcessId,
            ProcessCode = hierarchy.ProcessCode,
            ProcessName = hierarchy.ProcessName,
            SourceFrom = "ImageMarksAdmin",
        };
        var createdId = await repository.CreateBomAsync(entity, cancellationToken);
        if (createdId <= 0) throw Error("LineUpBomCreateFailed", "The BOM could not be created.");
        return new(entity.Id, subStationId, refNo, describe, tagCode, dbAddress,
            modelNo, brand, ioAddress, partNo, plc?.Id, plc?.Name, plc?.Ip);
    }

    public async Task<LineUpBomMutationDto> UpdateBomAsync(
        long bomId,
        LineUpBomWriteRequest request,
        CancellationToken cancellationToken)
    {
        if (bomId <= 0 || request is null) throw Error("LineUpInvalidBom", "The BOM payload is invalid.");
        var refNo = NormalizeBomText(request.RefNo);
        var tagCode = NormalizeBomText(request.TagCode);
        var describe = NormalizeBomText(request.Name);
        var dbAddress = NormalizeBomText(request.DbAddress);
        var modelNo = NormalizeBomText(request.ModelNo);
        var brand = NormalizeBomText(request.Brand);
        var ioAddress = NormalizeBomText(request.IoAddress);
        var partNo = NormalizeBomText(request.PartNo);
        if (refNo is null && tagCode is null)
            throw Error("LineUpInvalidBom", "RefNo or TagCode is required.");
        var plc = await ResolvePlcAsync(request.PlcId, cancellationToken);
        var bom = await repository.GetBomByIdAsync(bomId, cancellationToken);
        if (bom is null) throw Error("LineUpBomNotFound", "The BOM does not exist.");
        if (bom.StationId is > 0)
        {
            var workspace = await GetStationAsync(bom.StationId.Value, null, cancellationToken);
            if (tagCode is not null && workspace.Boms.Any(item =>
                    item.Id != bomId &&
                    string.Equals(NormalizeBomText(item.TagCode), tagCode, StringComparison.OrdinalIgnoreCase)))
                throw Error("LineUpBomTagCodeDuplicated", "The tag code already exists in this substation.");
        }
        var updated = await repository.UpdateBomFieldsAsync(
            bomId, refNo, tagCode, describe, dbAddress,
            modelNo, brand, ioAddress, partNo,
            plc?.Id, plc?.Code, plc?.Name, plc?.Ip,
            cancellationToken);
        if (!updated) throw Error("LineUpBomUpdateFailed", "The BOM could not be updated.");
        return new(bomId, bom.StationId, refNo, describe, tagCode, dbAddress,
            modelNo, brand, ioAddress, partNo, plc?.Id, plc?.Name, plc?.Ip);
    }

    public async Task<bool> SetBomAssociationAsync(long bomId, bool isAssociated, CancellationToken cancellationToken)
    {
        if (bomId <= 0) throw Error("LineUpInvalidBom", "The BOM payload is invalid.");
        var bom = await repository.GetBomByIdAsync(bomId, cancellationToken);
        if (bom is null) throw Error("LineUpBomNotFound", "The BOM does not exist.");
        var updated = await repository.UpdateBomAssociationAsync(bomId, isAssociated, cancellationToken);
        if (!updated) throw Error("LineUpBomUpdateFailed", "The BOM association could not be updated.");
        return true;
    }

    public async Task<bool> DeleteBomAsync(long bomId, CancellationToken cancellationToken)
    {
        if (bomId <= 0) throw Error("LineUpInvalidBom", "The BOM payload is invalid.");
        var bom = await repository.GetBomByIdAsync(bomId, cancellationToken);
        if (bom is null) throw Error("LineUpBomNotFound", "The BOM does not exist.");
        if (bom.StationId is > 0)
        {
            var workspace = await GetStationAsync(bom.StationId.Value, null, cancellationToken);
            if (workspace.Markers.Any(marker => marker.BomIds.Contains(bomId)))
                throw Error("LineUpBomInUse", "The BOM is still referenced by image markers.");
        }
        var deleted = await repository.SoftDeleteBomAsync(bomId, cancellationToken);
        if (!deleted) throw Error("LineUpBomDeleteFailed", "The BOM could not be deleted.");
        return true;
    }

    public async Task<IReadOnlyList<LineUpPlcDeviceDto>> GetPlcDevicesAsync(
        long? productionLineId,
        CancellationToken cancellationToken)
    {
        var devices = await repository.ListPlcDevicesAsync(productionLineId, cancellationToken);
        return devices.Select(device => new LineUpPlcDeviceDto(
            device.Id, device.Code, device.Name, device.Ip)).ToArray();
    }

    private async Task<LineUpPlcDeviceData?> ResolvePlcAsync(long? plcId, CancellationToken cancellationToken)
    {
        if (plcId is not > 0) return null;
        var device = await repository.GetPlcDeviceAsync(plcId.Value, cancellationToken);
        if (device is null)
            throw Error("LineUpInvalidPlcDevice", "The PLC device does not exist.");
        return device;
    }

    private static string? NormalizeBomText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LineUpImageMarkException Error(string code, string summary) => new(code, summary);
}
