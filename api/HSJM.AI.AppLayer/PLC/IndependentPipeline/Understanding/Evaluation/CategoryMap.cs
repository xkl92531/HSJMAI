#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>评测报告的逻辑类别映射（人工整理，只用于报告分层，不参与识别）。</summary>
public sealed record CategoryMap(Dictionary<string, string> ByCallee, Dictionary<string, string> ByLanguage, string Default)
{
    public string Of(PipelineUnit? unit)
    {
        if (unit == null) return Default;
        foreach (var callee in unit.Callees)
            if (ByCallee.TryGetValue(callee, out var c)) return c;
        return ByLanguage.TryGetValue(unit.Language, out var l) ? l : Default;
    }

    public static CategoryMap Load(string path)
    {
        var map = JsonSerializer.Deserialize<CategoryMap>(File.ReadAllText(path), TruthJson.Options)
            ?? throw new InvalidOperationException("类别映射文件为空：" + path);
        return map with
        {
            ByCallee = new(map.ByCallee, StringComparer.OrdinalIgnoreCase),
            ByLanguage = new(map.ByLanguage, StringComparer.OrdinalIgnoreCase)
        };
    }
}
