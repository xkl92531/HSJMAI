using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.LineUp.Parsing;

public static class LineUpGraphSourceKey
{
    public static string ForSequence(string blockName, string sequenceName) =>
        $"{Normalize(blockName)}/sequence:{Normalize(sequenceName)}";

    public static string ForStep(string blockName, string sequenceName, int number) =>
        $"{ForSequence(blockName, sequenceName)}/step:{number}";

    public static string ForBranch(string blockName, string sequenceName, int number) =>
        $"{ForSequence(blockName, sequenceName)}/branch:{number}";

    public static string ForTransition(string blockName, string sequenceName, int number) =>
        $"{ForSequence(blockName, sequenceName)}/transition:{number}";

    public static string ForAction(string stepKey, int ordinal) => $"{stepKey}/action:{ordinal}";

    public static string ForLogicNetwork(string ownerSourceKey, string role, int ordinal) =>
        $"{ownerSourceKey}/logic:{Normalize(role)}:{ordinal}";

    public static string ForLogicPart(string networkSourceKey, int uid) =>
        $"{networkSourceKey}/part:{uid}";

    public static string ForLogicWire(string networkSourceKey, int uid) =>
        $"{networkSourceKey}/wire:{uid}";

    public static string ForEdge(string sequenceKey, string fromKey, string toKey, int ordinal) =>
        $"{sequenceKey}/edge:{Normalize(fromKey)}>{Normalize(toKey)}:{ordinal}";

    public static string ForNormalizedEdge(
        string sequenceKey,
        string fromKey,
        string toKey,
        string edgeKind,
        int? branchOrder,
        int occurrence) =>
        $"{sequenceKey}/normalized-edge:{Normalize(fromKey)}>{Normalize(toKey)}:" +
        $"{Normalize(edgeKind)}:{branchOrder?.ToString() ?? "-"}:{occurrence}";

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
}
