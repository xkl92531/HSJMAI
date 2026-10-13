#nullable enable
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>Skipped：单个网络本身超过预算、未放进任何段的网络（「块名/网络号」）。</summary>
public sealed record FbContractSegmentPlan(IReadOnlyList<IReadOnlyList<IrUnit>> Segments, IReadOnlyList<string> Skipped);

/// <summary>spec §3.2 规则 5：超长程序体按网络顺序分段，每段翻译总长不超过预算。</summary>
public static class FbContractSegments
{
    public static FbContractSegmentPlan Split(IrBlock block, int budget)
    {
        var segments = new List<IReadOnlyList<IrUnit>>();
        var skipped = new List<string>();
        var current = new List<IrUnit>();
        var size = 0;
        foreach (var unit in block.Units)
        {
            var length = IrText.Unit(unit).Length;
            if (length > budget) { skipped.Add($"{unit.BlockName}/{unit.Network?.ToString() ?? "?"}"); continue; }
            if (current.Count > 0 && size + length > budget)
            {
                segments.Add(current);
                current = [];
                size = 0;
            }
            current.Add(unit);
            size += length;
        }
        if (current.Count > 0) segments.Add(current);
        return new(segments, skipped);
    }
}
