#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353ModeConflict(string BlockName, string ManualSignal, string AutomaticSignal);

/// <summary>Never infer manual mode from AutoMod=false or an unavailable PLC observation.</summary>
public static class Original353ManualModePolicy
{
    public static string Classify(IReadOnlyList<Original353ManualModeBinding> bindings,
        IReadOnlyDictionary<string, bool?> values)
    {
        if (bindings is null || bindings.Count == 0 || values is null) return "Unknown";
        if (FindConflicts(bindings, values).Count > 0) return "Conflict";
        var states = new List<(bool Manual, bool Auto)>(bindings.Count);
        foreach (var binding in bindings.Distinct())
        {
            var manualKey = binding.BlockName + "." + binding.VariablePath;
            var autoKey = binding.BlockName + "." + binding.AutoVariablePath;
            if (!values.TryGetValue(manualKey, out var manual) || !values.TryGetValue(autoKey, out var auto)
                || manual is null || auto is null) return "Unknown";
            if (binding.AutoRunVariablePath is { } runPath
                && (!values.TryGetValue(binding.BlockName + "." + runPath, out var run) || run is null)) return "Unknown";
            states.Add((manual.Value, auto.Value));
        }
        return states.All(state => state.Manual && !state.Auto) ? "Manual" : "Other";
    }

    public static IReadOnlyList<Original353ModeConflict> FindConflicts(
        IReadOnlyList<Original353ManualModeBinding> bindings, IReadOnlyDictionary<string, bool?> values)
    {
        var conflicts = new List<Original353ModeConflict>();
        foreach (var binding in bindings.Distinct())
        {
            var manual = binding.BlockName + "." + binding.VariablePath;
            if (!values.TryGetValue(manual, out var man) || man != true) continue;
            foreach (var path in new[] { binding.AutoVariablePath, binding.AutoRunVariablePath }.Where(path => path is not null))
            {
                var automatic = binding.BlockName + "." + path;
                if (values.TryGetValue(automatic, out var value) && value == true)
                    conflicts.Add(new(binding.BlockName, manual, automatic));
            }
        }
        return conflicts;
    }

    public static IReadOnlyList<string> ManualGroups(IReadOnlyList<Original353ManualModeBinding> bindings,
        IReadOnlyDictionary<string, bool?> values) => bindings.Where(binding =>
            values.TryGetValue(binding.BlockName + "." + binding.VariablePath, out var manual) && manual == true
            && values.TryGetValue(binding.BlockName + "." + binding.AutoVariablePath, out var auto) && auto == false
            && (binding.AutoRunVariablePath is null || values.TryGetValue(binding.BlockName + "." + binding.AutoRunVariablePath, out var run) && run == false))
            .Select(binding => binding.BlockName).Distinct().ToArray();
}
