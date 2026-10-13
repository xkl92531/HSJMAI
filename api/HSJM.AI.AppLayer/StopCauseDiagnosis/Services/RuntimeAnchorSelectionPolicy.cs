using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 运行时锚点筛选策略：
    /// 排除 ReaderLogicUnit 这类仅适合离线编译的 AST，
    /// 同时把 AnchorScan 收紧到核心位置锚点和上下文锚点。
    /// </summary>
    public static class RuntimeAnchorSelectionPolicy
    {
        public static bool IsRuntimeEligibleAst(PLC_LogicUnitAstEntity row)
        {
            if (row == null)
            {
                return false;
            }

            var anchorType = TryReadRootAnchorType(row.RootNodeJson);
            return !string.Equals(anchorType, "ReaderLogicUnit", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsRuntimeCoreAnchorType(string anchorType)
        {
            return string.Equals(anchorType, "PhysicalAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(anchorType, "VirtualAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(anchorType, "GraphAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(anchorType, "GraphExecBit", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsRuntimeContextAnchorType(string anchorType)
        {
            return string.Equals(anchorType, "HandshakeState", StringComparison.OrdinalIgnoreCase);
        }

        public static List<PLC_StationShadowSignalEntity> SelectAnchorScanSignals(IEnumerable<PLC_StationShadowSignalEntity> signals)
        {
            return (signals ?? Enumerable.Empty<PLC_StationShadowSignalEntity>())
                .Where(it => it != null
                    && it.IsDeleted == false
                    && it.Enabled != false
                    && it.IsWatchOnly != true
                    && !string.IsNullOrWhiteSpace(it.VariableName))
                .Where(it => it.IsCore == true
                    || it.IsContext == true
                    || IsRuntimeCoreAnchorType(it.AnchorType)
                    || IsRuntimeContextAnchorType(it.AnchorType))
                .GroupBy(it => it.VariableName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderBy(it => it.Priority ?? int.MaxValue)
                    .ThenByDescending(it => IsRuntimeCoreAnchorType(it.AnchorType) ? 1 : 0)
                    .ThenByDescending(it => it.IsContext == true ? 1 : 0)
                    .ThenBy(it => it.SortOrder ?? int.MaxValue)
                    .First())
                .OrderBy(it => it.Priority ?? int.MaxValue)
                .ThenByDescending(it => IsRuntimeCoreAnchorType(it.AnchorType) ? 1 : 0)
                .ThenByDescending(it => it.IsContext == true ? 1 : 0)
                .ThenBy(it => it.SortOrder ?? int.MaxValue)
                .ToList();
        }

        private static string TryReadRootAnchorType(string rootNodeJson)
        {
            if (string.IsNullOrWhiteSpace(rootNodeJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(rootNodeJson);
                if (!document.RootElement.TryGetProperty("Metadata", out var metadataElement)
                    || metadataElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (metadataElement.TryGetProperty("AnchorType", out var anchorTypeElement)
                    && anchorTypeElement.ValueKind == JsonValueKind.String)
                {
                    return anchorTypeElement.GetString()?.Trim();
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
