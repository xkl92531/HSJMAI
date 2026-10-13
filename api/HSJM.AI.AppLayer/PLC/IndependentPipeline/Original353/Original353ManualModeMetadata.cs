#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>Requires every current XMLV2 rule of a project to carry the same source-proven mode binding.</summary>
public static class Original353ManualModeMetadata
{
    public static IReadOnlyList<Original353ManualModeBinding> Read(
        IEnumerable<PLC_IOConstraintRuleEntity> rules)
    {
        Original353ManualModeBinding[]? first = null;
        var seen = false;
        foreach (var rule in rules.Where(rule => rule.Source is "XMLV2" or "XMLV2_TIMED" or "XMLV2_REVIEW" or "XMLV2_MODE"))
        {
            seen = true;
            try
            {
                using var doc = JsonDocument.Parse(rule.ValidStates ?? "");
                if (!doc.RootElement.TryGetProperty("ManualModeBindings", out var node)
                    || node.ValueKind != JsonValueKind.Array) return [];
                var current = node.Deserialize<Original353ManualModeBinding[]>() ?? [];
                if (current.Length is 0 or > 32 || current.Any(binding => binding is null
                    || string.IsNullOrWhiteSpace(binding.BlockName)
                    || binding.VariablePath != "General.ManualMod"
                    || binding.AutoVariablePath != "General.AutoMod"
                    || binding.AutoRunVariablePath is not (null or "General.AutoRunMod"))) return [];
                var canonical = current.Distinct().OrderBy(binding => binding.BlockName, StringComparer.Ordinal).ToArray();
                if (first is null) first = canonical;
                else if (!first.SequenceEqual(canonical)) return [];
            }
            catch (JsonException) { return []; }
        }
        return seen ? first ?? [] : [];
    }
}
