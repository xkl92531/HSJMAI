using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 工位地图构建服务。
    /// 负责把锚点发现阶段产出的原始工位证据聚合成稳定的工位桶、冲突列表和最终归属结果。
    /// </summary>
    public class StationMapBuilderService : ISelfScopedAutoInject
    {
        private static readonly Regex StationCodeRegex = new Regex(@"OP\d{3}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_BlockService _blockService;

        public StationMapBuilderService(
            IPLC_VariableAccessService variableAccessService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_BlockService blockService)
        {
            _variableAccessService = variableAccessService;
            _logicUnitService = logicUnitService;
            _blockService = blockService;
        }

        public async Task<StationMapResult> BuildAsync(long projectId, IEnumerable<AnchorCandidate> anchors)
        {
            var candidates = anchors?
                .Where(it => it != null && !string.IsNullOrWhiteSpace(it.AnchorKey) && !string.IsNullOrWhiteSpace(it.VariableName))
                .ToList() ?? new List<AnchorCandidate>();

            if (candidates.Count == 0)
            {
                return new StationMapResult();
            }

            var variableNames = candidates
                .Select(it => it.VariableName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var variableAccesses = await _variableAccessService.GetListAsync(it =>
                it.ProjectId == projectId
                && it.IsDeleted == false
                && variableNames.Contains(it.VariableName));

            var logicUnitIds = variableAccesses
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            var blockIds = variableAccesses
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();

            var logicUnitMap = logicUnitIds.Count > 0
                ? LogicUnitAnalysisPolicy.BuildEligibleMap(
                    await _logicUnitService.GetListAsync(it => logicUnitIds.Contains(it.Id) && it.IsDeleted == false))
                : new Dictionary<long, PLC_LogicUnitEntity>();

            var eligibleLogicUnitIds = LogicUnitAnalysisPolicy.BuildEligibleIdSet(logicUnitMap);
            variableAccesses = LogicUnitAnalysisPolicy.FilterAccessesByEligibleLogicUnits(variableAccesses, eligibleLogicUnitIds);

            var blockMap = blockIds.Count > 0
                ? (await _blockService.GetListAsync(it => blockIds.Contains(it.Id) && it.IsDeleted == false))
                    .ToDictionary(it => it.Id, it => it)
                : new Dictionary<long, PLC_BlockEntity>();

            var accessByVariable = variableAccesses
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var anchorGroups = candidates
                .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new StationMapResult();

            foreach (var group in anchorGroups)
            {
                var variableName = group.Key;
                var groupedAnchors = group.ToList();
                var stationScores = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                var stationEvidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (var anchor in groupedAnchors)
                {
                    if (!string.IsNullOrWhiteSpace(anchor.StationCode))
                    {
                        AddScore(
                            stationScores,
                            stationEvidence,
                            anchor.StationCode,
                            anchor.StationConfidence > 0 ? anchor.StationConfidence : 0.20m,
                            $"锚点 {anchor.AnchorKey} 原始工位");
                    }

                    foreach (var evidence in (anchor.StationEvidence ?? new List<string>()).Take(6))
                    {
                        var match = ExtractStationCodes(evidence).FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(match))
                        {
                            AddScore(stationScores, stationEvidence, match, 0.05m, $"锚点证据: {evidence}");
                        }
                    }
                }

                if (accessByVariable.TryGetValue(variableName, out var accesses))
                {
                    foreach (var access in accesses)
                    {
                        if (access.BlockId.HasValue && blockMap.TryGetValue(access.BlockId.Value, out var block))
                        {
                            if (!string.IsNullOrWhiteSpace(block.StationCode))
                            {
                                AddScore(stationScores, stationEvidence, block.StationCode, 0.35m, $"块 {block.BlockName} 显式工位");
                            }

                            foreach (var station in ExtractStationCodes(block.BlockName).Concat(ExtractStationCodes(block.Description)).Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                AddScore(stationScores, stationEvidence, station, 0.10m, $"块 {block.BlockName} 命中工位");
                            }
                        }

                        if (access.LogicUnitId.HasValue && logicUnitMap.TryGetValue(access.LogicUnitId.Value, out var logicUnit))
                        {
                            foreach (var station in ExtractStationCodes(logicUnit.Title).Concat(ExtractStationCodes(logicUnit.Comment)).Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                AddScore(stationScores, stationEvidence, station, 0.08m, $"网络 {logicUnit.Title} 命中工位");
                            }
                        }
                    }
                }

                if (stationScores.Count == 0)
                {
                    result.OrphanAnchors.AddRange(groupedAnchors.Select(anchor => new OrphanAnchor
                    {
                        AnchorKey = anchor.AnchorKey,
                        VariableName = anchor.VariableName,
                        AnchorType = anchor.AnchorType,
                        Reason = "未形成稳定工位证据",
                        Evidence = anchor.StationEvidence.Take(6).ToList()
                    }));
                    continue;
                }

                var orderedScores = stationScores
                    .OrderByDescending(it => it.Value)
                    .ThenBy(it => it.Key)
                    .ToList();

                var winner = orderedScores[0];
                var conflictingStations = orderedScores
                    .Skip(1)
                    .Where(it => it.Value >= 0.30m && winner.Value - it.Value <= 0.20m)
                    .Select(it => it.Key)
                    .ToList();

                var hasConflict = conflictingStations.Count > 0;
                var evidenceList = stationEvidence.TryGetValue(winner.Key, out var winnerEvidence)
                    ? winnerEvidence.Distinct().Take(8).ToList()
                    : new List<string>();

                result.AnchorAssignments.AddRange(groupedAnchors.Select(anchor => new AnchorStationAssignment
                {
                    AnchorKey = anchor.AnchorKey,
                    VariableName = anchor.VariableName,
                    AnchorType = anchor.AnchorType,
                    StationCode = winner.Key,
                    Confidence = Math.Min(winner.Value, 0.98m),
                    HasConflict = hasConflict,
                    ConflictingStations = conflictingStations,
                    Evidence = evidenceList
                }));

                if (hasConflict)
                {
                    result.StationConflicts.Add(new StationConflict
                    {
                        VariableName = variableName,
                        AnchorKeys = groupedAnchors.Select(it => it.AnchorKey).Distinct().ToList(),
                        RecommendedStation = winner.Key,
                        RecommendedConfidence = Math.Min(winner.Value, 0.98m),
                        CandidateStations = new[] { winner.Key }.Concat(conflictingStations).ToList(),
                        Reason = "存在多个高分工位候选",
                        Evidence = orderedScores.Select(it => $"{it.Key}: {Math.Round(it.Value, 3)}").Take(6).ToList()
                    });
                }
            }

            result.StationBuckets = BuildBuckets(result.AnchorAssignments, accessByVariable);
            return result;
        }

        private static List<StationBucket> BuildBuckets(
            IEnumerable<AnchorStationAssignment> assignments,
            IReadOnlyDictionary<string, List<PLC_VariableAccessEntity>> accessByVariable)
        {
            return assignments
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode))
                .GroupBy(it => it.StationCode, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupAssignments = group.ToList();
                    var relatedAccesses = groupAssignments
                        .Where(it => accessByVariable.ContainsKey(it.VariableName))
                        .SelectMany(it => accessByVariable[it.VariableName])
                        .ToList();

                    return new StationBucket
                    {
                        StationCode = group.Key,
                        Confidence = Math.Round(groupAssignments.Average(it => it.Confidence), 3),
                        AnchorCount = groupAssignments.Select(it => it.AnchorKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        LogicUnitCount = relatedAccesses.Where(it => it.LogicUnitId.HasValue).Select(it => it.LogicUnitId.Value).Distinct().Count(),
                        BlockCount = relatedAccesses.Where(it => it.BlockId.HasValue).Select(it => it.BlockId.Value).Distinct().Count(),
                        AnchorKeys = groupAssignments.Select(it => it.AnchorKey).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList(),
                        VariableNames = groupAssignments.Select(it => it.VariableName).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList(),
                        Evidence = groupAssignments.SelectMany(it => it.Evidence).Distinct().Take(8).ToList()
                    };
                })
                .OrderByDescending(it => it.AnchorCount)
                .ThenBy(it => it.StationCode)
                .ToList();
        }

        private static IEnumerable<string> ExtractStationCodes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Enumerable.Empty<string>();
            }

            return StationCodeRegex.Matches(text)
                .Cast<Match>()
                .Where(it => it.Success)
                .Select(it => it.Value.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static void AddScore(
            IDictionary<string, decimal> scores,
            IDictionary<string, List<string>> evidence,
            string stationCode,
            decimal score,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(stationCode))
            {
                return;
            }

            var normalized = stationCode.Trim().ToUpperInvariant();
            if (!scores.ContainsKey(normalized))
            {
                scores[normalized] = 0m;
                evidence[normalized] = new List<string>();
            }

            scores[normalized] += score;
            evidence[normalized].Add(reason);
        }
    }
}
