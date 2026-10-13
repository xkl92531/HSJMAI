#nullable enable

using System;
using System.Collections.Generic;
using HSJM.AI.AppLayer.LineUp.Runtime;

namespace HSJM.AI.AppLayer.LineUp.Models;

public sealed record LineUpGraphDiffCounts(
    int SequenceCount,
    int NodeCount,
    int ActionCount,
    int EdgeCount);

public sealed record LineUpGraphDiffItemDto(
    string ChangeKind,
    string ElementKind,
    string SourceKey,
    string SourceName);

public sealed record LineUpGraphRevisionDiffDto(
    bool IsInitialPublish,
    LineUpGraphDiffCounts Added,
    LineUpGraphDiffCounts Removed,
    LineUpGraphDiffCounts Changed,
    IReadOnlyList<LineUpGraphDiffItemDto> Items);

public sealed record LineUpGraphCatalogLookupDto(
    IReadOnlyList<LineUpGraphCatalogProjectDto> Projects,
    IReadOnlyList<LineUpGraphCatalogLineDto> Lines,
    IReadOnlyList<LineUpGraphCatalogProcessDto> Processes,
    IReadOnlyList<LineUpGraphCatalogStationDto> Stations,
    IReadOnlyList<LineUpGraphCatalogSubStationDto> SubStations,
    IReadOnlyList<LineUpGraphCatalogDeviceDto> Devices);

public sealed record LineUpGraphCatalogProjectDto(long Id, string Name, long? PlcDeviceId);
public sealed record LineUpGraphCatalogLineDto(long Id, string? Code, string? Name);
public sealed record LineUpGraphCatalogProcessDto(long Id, long LineId, string? Code, string? Name);
public sealed record LineUpGraphCatalogStationDto(long Id, long LineId, long ProcessId, string? Code, string? Name);
public sealed record LineUpGraphCatalogSubStationDto(
    long Id,
    long LineId,
    long ProcessId,
    long StationId,
    string? Code,
    string? Name,
    long? PlcId,
    string? PlcName);
public sealed record LineUpGraphCatalogDeviceDto(long Id, long LineId, string? Name, string? ProjectName, string? DeviceType);

public sealed record LineUpGraphCatalogQueryDto(
    long? PlcProjectId = null,
    long? LineId = null,
    long? ProcessId = null,
    long? StationId = null,
    long? SubStationId = null,
    long? DeviceId = null,
    string? Status = null,
    string? Keyword = null,
    int Page = 1,
    int PageSize = 20);

public sealed record LineUpGraphCatalogPageDto(
    IReadOnlyList<LineUpGraphCatalogAssetDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record LineUpGraphCatalogAssetDto(
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
    LineUpImportSummaryDto Summary,
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
    bool HasPreviewBatch);

public sealed record LineUpGraphRevisionDto(
    long Id,
    int RevisionNo,
    string Status,
    string SourceHash,
    int ParserSchemaVersion,
    DateTime? PublishedAt,
    long? PublishedBy);

public sealed record LineUpGraphBindingDto(
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
    DateTime? ModifyTime);

public sealed record LineUpGraphAliasDto(string ElementSourceKey, string DisplayName);

public sealed record LineUpGraphCatalogDetailDto(
    LineUpGraphCatalogAssetDto Asset,
    IReadOnlyList<LineUpGraphRevisionDto> Revisions,
    LineUpGraphBindingDto? Binding,
    IReadOnlyList<LineUpGraphAliasDto> Aliases,
    LineUpMonitorViewerPreviewDto Preview);

public sealed record LineUpGraphBindingRequest(
    long LineId,
    long ProcessId,
    long StationId,
    long SubStationId,
    long DeviceId,
    long? InstanceDbBlockId);

public sealed record LineUpGraphAliasValueDto(string ElementSourceKey, string DisplayName);

public sealed record LineUpGraphAliasSaveRequest(
    long ExpectedLatestRevisionId,
    IReadOnlyList<LineUpGraphAliasValueDto> Aliases);

public sealed record LineUpGraphPublishPreviewDto(
    long BindingId,
    long TargetRevisionId,
    int TargetRevisionNo,
    long? PublishedRevisionId,
    int? PublishedRevisionNo,
    LineUpGraphRevisionDiffDto Diff,
    LineUpMonitorViewerPreviewDto Preview);

public sealed record LineUpGraphPublishRequest(
    long TargetRevisionId,
    long ExpectedLatestRevisionId,
    long? ExpectedPublishedRevisionId);

public sealed record LineUpGraphPublishReceiptDto(
    long BindingId,
    long PublishedRevisionId,
    int PublishedRevisionNo,
    long? PreviousPublishedRevisionId,
    DateTime PublishedAt,
    bool MonitoringEnabled);
