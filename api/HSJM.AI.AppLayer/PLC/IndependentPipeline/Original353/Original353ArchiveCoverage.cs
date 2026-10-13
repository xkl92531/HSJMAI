#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>Full replacement is allowed only when every active executable block is in the archive.</summary>
public static class Original353ArchiveCoverage
{
    public static IReadOnlyList<string> MissingExecutableBlocks(PipelineSnapshot source,
        IEnumerable<PLC_BlockEntity> activeBlocks)
    {
        var included = source.Units.Select(unit => unit.BlockName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return activeBlocks.Where(block => !block.IsDeleted
                && block.ProjectId == source.ProjectId
                && (string.Equals(block.BlockType, "FB", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(block.BlockType, "FC", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(block.BlockType, "OB", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(block.BlockName)
                && !included.Contains(block.BlockName))
            .Select(block => block.BlockName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void RequireComplete(PipelineSnapshot source, IEnumerable<PLC_BlockEntity> activeBlocks)
    {
        var missing = MissingExecutableBlocks(source, activeBlocks);
        if (missing.Count > 0)
            throw new PipelineException($"IncompleteXmlArchiveForFullRuleReplace:{missing.Count}:"
                + string.Join(",", missing.Take(5)));
    }
}
