#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>阀门报警的工位归属：逻辑单元绑定 → 块绑定 → 块自身工位，均须在本 PLC 工位白名单内。</summary>
    public static class ValveAlarmStationResolver
    {
        public static string? Resolve(
            long logicUnitId,
            long? blockId,
            string blockName,
            IReadOnlyList<PLC_StationLogicUnitBindingEntity> luBindings,
            IReadOnlyList<PLC_StationBlockBindingEntity> blockBindings,
            string? blockStationCode,
            HashSet<string>? allowed)
        {
            return Pick(luBindings
                       .Where(b => b.LogicUnitId == logicUnitId)
                       .Select(b => (b.StationCode, b.Confidence)), allowed)
                   ?? Pick(blockBindings
                       .Where(b => (blockId.HasValue && b.BlockId == blockId)
                                   || string.Equals(b.BlockName?.Trim(), blockName?.Trim(), StringComparison.OrdinalIgnoreCase))
                       .Select(b => (b.StationCode, b.Confidence)), allowed)
                   ?? Pick(new[] { (blockStationCode, (decimal?)null) }, allowed);
        }

        private static string? Pick(IEnumerable<(string? Code, decimal? Confidence)> rows, HashSet<string>? allowed)
        {
            var candidates = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Code) && StationPlcScopeService.IsAllowed(r.Code, allowed))
                .Select(r => (Code: r.Code!.Trim(), Confidence: r.Confidence ?? 0m))
                .ToList();
            if (candidates.Count == 0) return null;
            var best = candidates.Max(r => r.Confidence);
            var top = candidates.Where(r => r.Confidence == best)
                .Select(r => r.Code)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return top.Count == 1 ? top[0] : null;
        }
    }
}
