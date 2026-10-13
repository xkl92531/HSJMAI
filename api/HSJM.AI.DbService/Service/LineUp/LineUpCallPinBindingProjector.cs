using System.Text.Json;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.DbService.Service.LineUp;

internal static class LineUpCallPinBindingProjector
{
    internal static LineUpCallPinBindingProjection Project(
        string? rawJson,
        string calleeName,
        string calleeBlockType)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson ?? string.Empty);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Failure(LineUpCallBindingParseStatus.InvalidJson);
            }

            var matches = new List<string?>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryReadString(item, "callee_name", out var itemName) ||
                    !TryReadString(item, "callee_block_type", out var itemType) ||
                    !EqualsNormalized(itemName, calleeName) ||
                    !EqualsNormalized(itemType, calleeBlockType))
                {
                    continue;
                }

                matches.Add(TryReadString(item, "instance_db", out var instanceDb)
                    ? instanceDb?.Trim()
                    : null);
            }

            if (matches.Count == 0)
            {
                return Failure(LineUpCallBindingParseStatus.MatchingBindingMissing);
            }

            var hasEmpty = matches.Any(string.IsNullOrWhiteSpace);
            var distinct = matches
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x, StringComparer.Ordinal)
                .ToArray();
            if (distinct.Length == 0)
            {
                return Failure(LineUpCallBindingParseStatus.InstanceDbMissing);
            }

            if (hasEmpty || distinct.Length != 1)
            {
                return Failure(LineUpCallBindingParseStatus.MultipleMatchingBindings);
            }

            return new(
                LineUpCallBindingParseStatus.Parsed,
                distinct[0],
                matches.Count > 1);
        }
        catch (JsonException)
        {
            return Failure(LineUpCallBindingParseStatus.InvalidJson);
        }
        catch (ArgumentException)
        {
            return Failure(LineUpCallBindingParseStatus.InvalidJson);
        }
    }

    private static bool TryReadString(JsonElement item, string propertyName, out string? value)
    {
        value = null;
        if (!item.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool EqualsNormalized(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static LineUpCallPinBindingProjection Failure(LineUpCallBindingParseStatus status) =>
        new(status, null, false);
}

internal sealed record LineUpCallPinBindingProjection(
    LineUpCallBindingParseStatus Status,
    string? InstanceDbName,
    bool HasEquivalentDuplicates);
