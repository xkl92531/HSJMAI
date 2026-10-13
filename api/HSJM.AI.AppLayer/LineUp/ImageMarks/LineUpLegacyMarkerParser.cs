using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.LineUp.ImageMarks;

public sealed class LineUpLegacyMarkerParser : ISelfScopedAutoInject
{
    private const int MaxFragmentCharacters = 1024 * 1024;

    public LineUpMarkerDocument Parse(LineUpStationImageMarkEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var issues = new List<LineUpMarkerIssue>();
        var pointJson = ParseObject(row.MCPConfig, "MCPConfig", issues);
        var billboardJson = ParseObject(row.MarkTagConfig, "MarkTagConfig", issues);
        var lineJson = ParseObject(row.MarkTagLineConfig, "MarkTagLineConfig", issues);
        var bomIds = ParseBomIds(row.MarkTagBom, issues);

        return new()
        {
            MarkerId = row.Id,
            StationImageId = row.StationImageId,
            MarkType = row.MarkType,
            IsSingleMark = row.IsSingleMark,
            Point = pointJson is { } point ? new(
                String(point, "id"),
                Decimal(point, "x", "MCPConfig.x", issues),
                Decimal(point, "y", "MCPConfig.y", issues),
                String(point, "description")) : null,
            Billboard = billboardJson is { } billboard ? new(
                String(billboard, "id"),
                Decimal(billboard, "x", "MarkTagConfig.x", issues),
                Decimal(billboard, "y", "MarkTagConfig.y", issues),
                Decimal(billboard, "width", "MarkTagConfig.width", issues),
                Decimal(billboard, "height", "MarkTagConfig.height", issues),
                String(billboard, "text"),
                String(billboard, "description"),
                String(billboard, "combinedBomId"),
                String(billboard, "bgColor"),
                String(billboard, "borderColor")) : null,
            Line = lineJson is { } line ? new(
                String(line, "id"),
                Decimal(line, "startX", "MarkTagLineConfig.startX", issues),
                Decimal(line, "startY", "MarkTagLineConfig.startY", issues),
                Decimal(line, "endX", "MarkTagLineConfig.endX", issues),
                Decimal(line, "endY", "MarkTagLineConfig.endY", issues),
                String(line, "markId"),
                String(line, "billboardId"),
                String(line, "combinedBomId")) : null,
            BomIds = bomIds,
            RawMcpConfig = row.MCPConfig,
            RawMarkTagConfig = row.MarkTagConfig,
            RawMarkTagLineConfig = row.MarkTagLineConfig,
            RawMarkTagBom = row.MarkTagBom,
            Issues = issues
        };
    }

    private static JsonElement? ParseObject(
        string? value,
        string field,
        ICollection<LineUpMarkerIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > MaxFragmentCharacters)
        {
            issues.Add(new("JsonTooLarge", field));
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("ExpectedObject", field));
                return null;
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            issues.Add(new("InvalidJson", field));
            return null;
        }
    }

    private static IReadOnlyList<long> ParseBomIds(
        string? value,
        ICollection<LineUpMarkerIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var result = new SortedSet<long>();
        foreach (var token in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                issues.Add(new("InvalidBomId", "MarkTagBom"));
                continue;
            }

            result.Add(id);
        }

        return result.ToArray();
    }

    private static decimal? Decimal(
        JsonElement element,
        string name,
        string field,
        ICollection<LineUpMarkerIssue> issues)
    {
        if (!Property(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        issues.Add(new("InvalidNumber", field));
        return null;
    }

    private static string? String(JsonElement element, string name)
    {
        if (!Property(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static bool Property(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
