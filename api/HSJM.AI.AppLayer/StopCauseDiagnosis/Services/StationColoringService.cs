using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 工位染色服务（Phase 1 Step 7-8）。
    /// 将已确认锚点的工位信息传播到所有写入该锚点变量的逻辑单元（LogicUnit）。
    /// 策略：
    ///   1. 取所有非 Rejected 且有 StationCode 的锚点为种子。
    ///   2. 查询 PLC_VariableAccess 中对锚点变量的写操作。
    ///   3. 每条写操作对应的 LogicUnit 按锚点工位置信度加分。
    ///   4. 取累加分最高的工位为最终染色，并记录竞争工位以供复核。
    /// </summary>
    public class StationColoringService : ISelfScopedAutoInject
    {
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_BlockService _blockService;
        private readonly VariablePropagationBuilderService _variablePropagationBuilderService;
        private readonly StationPlcScopeService _stationPlcScopeService;

        public StationColoringService(
            IPLC_VariableAccessService variableAccessService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_BlockService blockService,
            VariablePropagationBuilderService variablePropagationBuilderService,
            StationPlcScopeService stationPlcScopeService)
        {
            _variableAccessService = variableAccessService;
            _logicUnitService = logicUnitService;
            _blockService = blockService;
            _variablePropagationBuilderService = variablePropagationBuilderService;
            _stationPlcScopeService = stationPlcScopeService;
        }

        /// <summary>
        /// 对项目下的锚点最终归属结果执行工位染色，返回每个 LogicUnit 的工位归属标签。
        /// </summary>
        public async Task<List<LogicUnitStationLabel>> ColorAsync(
            long projectId,
            IEnumerable<AnchorStationAssignment> assignments)
        {
            // 工位白名单（2026-08-26）：对齐工位基础信息（Basic_SubStation.PlcName → PLC1/PLC2），
            // 种子工位不在本项目 PLC 的工位集合内（跨 PLC 通信变量带入的线索）不参与染色
            var allowedStations = await _stationPlcScopeService.GetAllowedStationsAsync(projectId);

            // 1. 只使用有工位的稳定归属结果
            var seeds = assignments
                .Where(a => !string.IsNullOrWhiteSpace(a.StationCode)
                         && !a.HasConflict
                         && !string.IsNullOrWhiteSpace(a.VariableName)
                         && StationPlcScopeService.IsAllowed(a.StationCode, allowedStations))
                .ToList();

            if (seeds.Count == 0)
                return new List<LogicUnitStationLabel>();

            var variableNames = seeds
                .Select(a => a.VariableName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 2. 查询写访问记录
            var writeAccesses = await _variableAccessService.GetListAsync(a =>
                a.ProjectId == projectId
                && variableNames.Contains(a.VariableName)
                && (a.AccessType == "WRITE" || a.AccessType == "READWRITE"));

            if (writeAccesses == null || writeAccesses.Count == 0)
                return new List<LogicUnitStationLabel>();

            // 3. 预加载 LogicUnit 和 Block 元数据（避免 N+1）
            var luIds = writeAccesses
                .Where(a => a.LogicUnitId.HasValue)
                .Select(a => a.LogicUnitId.Value)
                .Distinct()
                .ToList();

            var blockIds = writeAccesses
                .Where(a => a.BlockId.HasValue)
                .Select(a => a.BlockId.Value)
                .Distinct()
                .ToList();

            var luMap = luIds.Count > 0
                ? (await _logicUnitService.GetListAsync(lu => luIds.Contains(lu.Id)))
                    .ToDictionary(lu => lu.Id, lu => lu)
                : new Dictionary<long, PLC_LogicUnitEntity>();

            var blockMap = blockIds.Count > 0
                ? (await _blockService.GetListAsync(b => blockIds.Contains(b.Id)))
                    .ToDictionary(b => b.Id, b => b)
                : new Dictionary<long, PLC_BlockEntity>();

            // 4. 变量名 → 锚点列表（一个变量可能对应多个锚点，但工位应一致）
            var varToAnchors = seeds
                .GroupBy(a => a.VariableName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList());

            var seedAnchors = seeds
                .Select(seed => new AnchorCandidate
                {
                    AnchorKey = seed.AnchorKey,
                    AnchorType = seed.AnchorType,
                    VariableName = seed.VariableName,
                    StationCode = seed.StationCode,
                    StationConfidence = seed.Confidence,
                    Confidence = seed.Confidence,
                    DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                    StationEvidence = seed.Evidence?.ToList() ?? new List<string>()
                })
                .ToList();

            var propagationLinks = await _variablePropagationBuilderService.BuildAsync(projectId, seedAnchors);

            // 5. 收集每个 LogicUnit 的工位候选分数
            //    key: LogicUnitId
            //    value: list of (stationCode, score, anchorKey, anchorVar, reason)
            var luCandidates = new Dictionary<long, List<(string station, decimal score, string anchorKey, string anchorVar, string reason)>>();

            var variableStationScores = seeds
                .GroupBy(seed => seed.VariableName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(x => x.StationCode, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            x => x.Key,
                            x => x.Max(y => y.Confidence > 0 ? y.Confidence : 0.50m),
                            StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var access in writeAccesses)
            {
                if (!access.LogicUnitId.HasValue) continue;

                var luId = access.LogicUnitId.Value;
                var varName = access.VariableName ?? string.Empty;

                if (!varToAnchors.TryGetValue(varName, out var matchedAnchors)) continue;

                foreach (var anchor in matchedAnchors)
                {
                    if (string.IsNullOrWhiteSpace(anchor.StationCode)) continue;

                    var score = anchor.Confidence > 0 ? anchor.Confidence : 0.50m;
                    var reason = "DirectWrite";

                    // Block.StationCode 与 anchor.StationCode 一致 → 额外加权 +0.10
                    if (access.BlockId.HasValue
                        && blockMap.TryGetValue(access.BlockId.Value, out var blk)
                        && !string.IsNullOrWhiteSpace(blk.StationCode)
                        && blk.StationCode == anchor.StationCode)
                    {
                        score = Math.Min(score + 0.10m, 0.98m);
                        reason = "DirectWrite+BlockMatch";
                    }

                    if (!luCandidates.ContainsKey(luId))
                        luCandidates[luId] = new List<(string, decimal, string, string, string)>();

                    luCandidates[luId].Add((anchor.StationCode, score, anchor.AnchorKey, varName, reason));
                }
            }

            // 5.5 沿变量传播链做二跳染色，把工位扩散到中间变量和更多 LogicUnit/Block。
            for (var iteration = 0; iteration < 2; iteration++)
            {
                var changed = false;
                foreach (var link in propagationLinks)
                {
                    if (string.IsNullOrWhiteSpace(link.FromVariable) ||
                        string.IsNullOrWhiteSpace(link.ToVariable) ||
                        !variableStationScores.TryGetValue(link.FromVariable, out var sourceStations))
                    {
                        continue;
                    }

                    if (!variableStationScores.TryGetValue(link.ToVariable, out var targetStations))
                    {
                        targetStations = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                        variableStationScores[link.ToVariable] = targetStations;
                    }

                    foreach (var station in sourceStations)
                    {
                        var propagatedScore = Math.Min(station.Value * link.Confidence * 0.85m, 0.92m);
                        if (propagatedScore < 0.22m)
                        {
                            continue;
                        }

                        if (!targetStations.TryGetValue(station.Key, out var existing) || propagatedScore > existing)
                        {
                            targetStations[station.Key] = propagatedScore;
                            changed = true;
                        }

                        if (!link.LogicUnitId.HasValue)
                        {
                            continue;
                        }

                        if (!luCandidates.ContainsKey(link.LogicUnitId.Value))
                        {
                            luCandidates[link.LogicUnitId.Value] = new List<(string, decimal, string, string, string)>();
                        }

                        luCandidates[link.LogicUnitId.Value].Add((
                            station.Key,
                            propagatedScore,
                            $"propagation:{link.PropagationType}:{link.FromVariable}->{link.ToVariable}",
                            link.ToVariable,
                            $"Propagation:{link.PropagationType}"));
                    }
                }

                if (!changed)
                {
                    break;
                }
            }

            // 6. 对每个 LogicUnit 聚合工位分数并选出获胜工位
            var result = new List<LogicUnitStationLabel>();

            foreach (var kv in luCandidates)
            {
                var luId = kv.Key;
                var candidates = kv.Value;

                // 按工位累加分数
                var stationScores = candidates
                    .GroupBy(c => c.station)
                    .Select(g => new
                    {
                        Station = g.Key,
                        TotalScore = Math.Min(g.Sum(c => c.score), 0.98m),
                        Best = g.OrderByDescending(c => c.score).First()
                    })
                    .OrderByDescending(x => x.TotalScore)
                    .ToList();

                var winner = stationScores.First();

                // 竞争工位：得分 ≥ 0.30 且不是获胜者
                var conflicting = stationScores.Skip(1)
                    .Where(x => x.TotalScore >= 0.30m)
                    .Select(x => x.Station)
                    .ToList();

                luMap.TryGetValue(luId, out var lu);
                long? blockId = lu?.BlockId;
                string blockName = null;
                if (blockId.HasValue && blockMap.TryGetValue(blockId.Value, out var winBlock))
                    blockName = winBlock.BlockName;

                result.Add(new LogicUnitStationLabel
                {
                    LogicUnitId = luId,
                    BlockId = blockId,
                    BlockName = blockName,
                    NetworkTitle = lu?.Title,
                    StationCode = winner.Station,
                    Confidence = winner.TotalScore,
                    AnchorKey = winner.Best.anchorKey,
                    AnchorVariable = winner.Best.anchorVar,
                    ColoringReason = winner.Best.reason,
                    ConflictingStations = conflicting
                });
            }

            return result;
        }
    }
}
