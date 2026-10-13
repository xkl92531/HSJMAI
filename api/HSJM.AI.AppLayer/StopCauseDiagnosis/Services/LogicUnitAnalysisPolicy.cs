using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    internal static class LogicUnitAnalysisPolicy
    {
        private static readonly HashSet<string> AllowedUnitTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SCL_BODY",
            "NETWORK",
            "STL_NETWORK",
            "TRANSITION"
        };

        private static readonly Regex GraphStepActionUnitTypeRegex = new Regex(
            @"^STEP_\d+_ACTION$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsEligibleUnitType(string unitType)
        {
            if (string.IsNullOrWhiteSpace(unitType))
            {
                return false;
            }

            var normalized = unitType.Trim();
            return AllowedUnitTypes.Contains(normalized)
                || GraphStepActionUnitTypeRegex.IsMatch(normalized);
        }

        public static bool IsEligible(PLC_LogicUnitEntity logicUnit)
        {
            return logicUnit != null && IsEligibleUnitType(logicUnit.UnitType);
        }

        public static Dictionary<long, PLC_LogicUnitEntity> BuildEligibleMap(IEnumerable<PLC_LogicUnitEntity> logicUnits)
        {
            return (logicUnits ?? Enumerable.Empty<PLC_LogicUnitEntity>())
                .Where(IsEligible)
                .GroupBy(it => it.Id)
                .ToDictionary(g => g.Key, g => g.First());
        }

        public static HashSet<long> BuildEligibleIdSet(IReadOnlyDictionary<long, PLC_LogicUnitEntity> logicUnitMap)
        {
            return logicUnitMap == null
                ? new HashSet<long>()
                : logicUnitMap.Keys.ToHashSet();
        }

        public static List<PLC_VariableAccessEntity> FilterAccessesByEligibleLogicUnits(
            IEnumerable<PLC_VariableAccessEntity> accesses,
            ISet<long> eligibleLogicUnitIds)
        {
            if (accesses == null)
            {
                return new List<PLC_VariableAccessEntity>();
            }

            if (eligibleLogicUnitIds == null || eligibleLogicUnitIds.Count == 0)
            {
                return accesses.Where(it => !it.LogicUnitId.HasValue).ToList();
            }

            return accesses
                .Where(it => !it.LogicUnitId.HasValue || eligibleLogicUnitIds.Contains(it.LogicUnitId.Value))
                .ToList();
        }

        public static Dictionary<long, List<PLC_VariableAccessEntity>> BuildEligibleAccessMap(
            IEnumerable<PLC_VariableAccessEntity> accesses,
            ISet<long> eligibleLogicUnitIds)
        {
            if (accesses == null || eligibleLogicUnitIds == null || eligibleLogicUnitIds.Count == 0)
            {
                return new Dictionary<long, List<PLC_VariableAccessEntity>>();
            }

            return accesses
                .Where(it => it.LogicUnitId.HasValue && eligibleLogicUnitIds.Contains(it.LogicUnitId.Value))
                .GroupBy(it => it.LogicUnitId.Value)
                .ToDictionary(g => g.Key, g => g.ToList());
        }
    }
}