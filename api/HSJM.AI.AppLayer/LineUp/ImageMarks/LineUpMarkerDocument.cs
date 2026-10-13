using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.LineUp.ImageMarks;

public sealed class LineUpMarkerDocument
{
    public int SchemaVersion { get; init; } = 1;
    public long MarkerId { get; init; }
    public long? StationImageId { get; init; }
    public string? MarkType { get; init; }
    public bool? IsSingleMark { get; init; }
    public LineUpMarkerPoint? Point { get; init; }
    public LineUpMarkerBillboard? Billboard { get; init; }
    public LineUpMarkerLine? Line { get; init; }
    public IReadOnlyList<long> BomIds { get; init; } = [];
    public string? RawMcpConfig { get; init; }
    public string? RawMarkTagConfig { get; init; }
    public string? RawMarkTagLineConfig { get; init; }
    public string? RawMarkTagBom { get; init; }
    public IReadOnlyList<LineUpMarkerIssue> Issues { get; init; } = [];

    public bool ContainsBom(long bomId) => BomIds.Contains(bomId);
}

public sealed record LineUpMarkerPoint(
    string? Id,
    decimal? X,
    decimal? Y,
    string? Description);

public sealed record LineUpMarkerBillboard(
    string? Id,
    decimal? X,
    decimal? Y,
    decimal? Width,
    decimal? Height,
    string? Text,
    string? Description,
    string? CombinedBomId,
    string? BackgroundColor,
    string? BorderColor);

public sealed record LineUpMarkerLine(
    string? Id,
    decimal? StartX,
    decimal? StartY,
    decimal? EndX,
    decimal? EndY,
    string? MarkId,
    string? BillboardId,
    string? CombinedBomId);

public sealed record LineUpMarkerIssue(string Code, string Field);
