using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 将工位提取结果反向回填到程序块和工位绑定表。
    /// </summary>
    public class StationBackfillService : ISelfScopedAutoInject
    {
        private readonly IPLC_BlockService _blockService;
        private readonly StationMapPersistenceService _stationMapPersistenceService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly ILogger<StationBackfillService> _logger;
        private readonly IPLC_StationBlockBindingService _stationBlockBindingService;
        private readonly StationPlcScopeService _stationPlcScopeService;

        public StationBackfillService(
            IPLC_BlockService blockService,
            StationMapPersistenceService stationMapPersistenceService,
            IPLC_VariableAccessService variableAccessService,
            ILogger<StationBackfillService> logger,
            IPLC_StationBlockBindingService stationBlockBindingService,
            StationPlcScopeService stationPlcScopeService)
        {
            _stationBlockBindingService = stationBlockBindingService;
            _stationPlcScopeService = stationPlcScopeService;
            _blockService = blockService;
            _stationMapPersistenceService = stationMapPersistenceService;
            _variableAccessService = variableAccessService;
            _logger = logger;
        }

        public async Task<StationBackfillSummary> ApplyAsync(
            long projectId,
            StationExtractionResultDto extractionResult,
            bool persistStationBindings,
            bool updateBlockStationCode)
        {
            extractionResult ??= new StationExtractionResultDto();
            var summary = new StationBackfillSummary
            {
                CandidateBlockCount = extractionResult.StationColoringResult
                    .Where(it => it.BlockId.HasValue && !string.IsNullOrWhiteSpace(it.StationCode))
                    .Select(it => it.BlockId.Value)
                    .Distinct()
                    .Count(),
                LogicUnitBindingCount = extractionResult.StationColoringResult.Count(it => !string.IsNullOrWhiteSpace(it.StationCode))
            };

            if (persistStationBindings)
            {
                await _stationMapPersistenceService.PersistAsync(
                    projectId,
                    BuildStationMap(extractionResult),
                    extractionResult.StationColoringResult);
                summary.PersistedBindings = true;
            }

            if (updateBlockStationCode)
            {
                summary.UpdatedBlockCount = await ApplyBlockStationCodesAsync(projectId, extractionResult.StationColoringResult);
                summary.UpdatedBlockStationCode = summary.UpdatedBlockCount > 0;
            }

            return summary;
        }

        /// <summary>
        /// 回填块工位字段（2026-10-14 改为一次遍历）：
        /// 有手动绑定的块一律以手动绑定为准——以前重新提取会按染色投票直接覆盖，把人工绑好的工位改乱；
        /// 否则按染色投票，块名/描述里明确的工位号优先；只有文本线索时只填空字段；白名单外的工位不写。
        /// 规则见 StationBlockBindingRules.DecideBackfillStation。
        /// </summary>
        private async Task<int> ApplyBlockStationCodesAsync(long projectId, IReadOnlyCollection<LogicUnitStationLabel> labels)
        {
            var coloringWinners = (labels ?? Array.Empty<LogicUnitStationLabel>())
                .Where(it => it.BlockId.HasValue && !string.IsNullOrWhiteSpace(it.StationCode))
                .GroupBy(it => it.BlockId.Value)
                .ToDictionary(group => group.Key, group =>
                {
                    var ordered = group
                        .GroupBy(it => it.StationCode, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new
                        {
                            StationCode = g.Key,
                            Score = g.Sum(it => it.Confidence)
                        })
                        .OrderByDescending(it => it.Score)
                        .ToList();

                    var winner = ordered.FirstOrDefault();
                    var hasConflict = ordered.Count > 1 && ordered[1].Score >= Math.Max(0.30m, winner?.Score - 0.15m ?? 0m);
                    return hasConflict ? null : winner?.StationCode;
                });

            var blocks = await _blockService.GetListAsync(it => it.ProjectId == projectId && it.IsDeleted == false);
            if (blocks.Count == 0)
            {
                return 0;
            }

            var manualStationByName = await LoadManualStationByBlockNameAsync(projectId);
            var allowed = await _stationPlcScopeService.GetAllowedStationsAsync(projectId);

            var updatedCount = 0;
            foreach (var block in blocks)
            {
                coloringWinners.TryGetValue(block.Id, out var coloringWinner);
                manualStationByName.TryGetValue((block.BlockName ?? string.Empty).Trim(), out var manualStation);
                var textStation = TryInferStationFromBlockText(block, out var inferred) ? inferred : null;

                var decided = StationBlockBindingRules.DecideBackfillStation(
                    block.StationCode, coloringWinner, textStation, manualStation, allowed);
                if (decided == null)
                {
                    continue;
                }

                block.StationCode = decided;
                if (await _blockService.UpdateAsync(block))
                {
                    updatedCount++;
                }
            }

            return updatedCount;
        }

        /// <summary>项目内各块名的手动绑定工站（同名多条取最近一条）</summary>
        private async Task<Dictionary<string, string>> LoadManualStationByBlockNameAsync(long projectId)
        {
            const string manualReason = StationBlockBindingRules.ManualReason;
            var manualRows = await _stationBlockBindingService.GetListAsync(it =>
                it.ProjectId == projectId && it.IsDeleted == false && it.Reason == manualReason);
            return manualRows
                .Where(it => !string.IsNullOrWhiteSpace(it.BlockName) && !string.IsNullOrWhiteSpace(it.StationCode))
                .GroupBy(it => it.BlockName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(it => it.ModifyTime ?? it.CreateTime ?? DateTime.MinValue).First().StationCode.Trim(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static bool TryInferStationFromBlockText(PLC_BlockEntity block, out string stationCode)
        {
            stationCode = string.Empty;
            if (block == null)
            {
                return false;
            }

            var candidates = new[] { block.Description, block.BlockName }
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .SelectMany(StationCodeHeuristics.ExtractStationCodes)
                .Where(StationCodeHeuristics.IsLikelyStationCode)
                .Select(StationCodeHeuristics.NormalizeStationCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
            {
                return false;
            }

            var aliasFamilies = candidates
                .Select(code => new
                {
                    Code = code,
                    Family = StationCodeHeuristics.GetAliasFamilyKey(code),
                    Priority = StationCodeHeuristics.GetStationPriority(code)
                })
                .Where(it => !string.IsNullOrWhiteSpace(it.Family))
                .GroupBy(it => it.Family, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliasFamilies.Count != 1)
            {
                return false;
            }

            stationCode = aliasFamilies[0]
                .OrderByDescending(it => it.Priority)
                .ThenBy(it => it.Code.Length)
                .ThenBy(it => it.Code)
                .First()
                .Code;

            return !string.IsNullOrWhiteSpace(stationCode);
        }

        /// <summary>
        /// 第三层回填：利用块内写入变量的工位映射进行投票，为仍然没有工位的块补充工位。
        /// 原理：如果一个块的写入变量大多数指向同一个工位，那这个块就属于该工位。
        /// 在一键分析流程中，应在 Step 1（基础回填）之后、Step 4（AST种子构建）之前执行。
        /// </summary>
        public async Task<int> ApplyWriteVariableVotingAsync(
            long projectId,
            IReadOnlyDictionary<string, StationInferenceResult> variableStations)
        {
            if (variableStations == null || variableStations.Count == 0)
            {
                return 0;
            }

            // 查出所有仍无工位的块
            var unassignedBlocks = await _blockService.GetListAsync(it =>
                it.ProjectId == projectId &&
                it.IsDeleted == false &&
                string.IsNullOrEmpty(it.StationCode));

            if (unassignedBlocks.Count == 0)
            {
                return 0;
            }

            var unassignedBlockIds = unassignedBlocks.Select(b => b.Id).ToList();
            var allowedForVoting = await _stationPlcScopeService.GetAllowedStationsAsync(projectId);

            // 一次性查出这些块的所有写入变量访问
            var writeAccesses = await _variableAccessService.GetListAsync(it =>
                it.IsDeleted == false &&
                it.ProjectId == projectId &&
                it.BlockId.HasValue &&
                unassignedBlockIds.Contains(it.BlockId.Value) &&
                !string.IsNullOrEmpty(it.VariableName) &&
                (it.AccessType == "Write" || it.AccessType == "ReadWrite"));

            if (writeAccesses.Count == 0)
            {
                return 0;
            }

            // 按 Block 分组，对每个 Block 的写入变量做工位投票
            var accessesByBlock = writeAccesses
                .Where(a => a.BlockId.HasValue)
                .GroupBy(a => a.BlockId.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            var blockMap = unassignedBlocks.ToDictionary(b => b.Id, b => b);
            var updatedCount = 0;

            foreach (var (blockId, accesses) in accessesByBlock)
            {
                if (!blockMap.TryGetValue(blockId, out var block))
                {
                    continue;
                }

                // 收集每个写入变量对应的工位
                var stationVotes = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                var totalVotes = 0;

                foreach (var access in accesses)
                {
                    if (!variableStations.TryGetValue(access.VariableName, out var station) ||
                        string.IsNullOrWhiteSpace(station?.StationCode))
                    {
                        continue;
                    }

                    if (!stationVotes.ContainsKey(station.StationCode))
                    {
                        stationVotes[station.StationCode] = 0m;
                    }

                    stationVotes[station.StationCode] += station.Confidence;
                    totalVotes++;
                }

                if (totalVotes == 0 || stationVotes.Count == 0)
                {
                    continue;
                }

                // 取票数最高的工位
                var ordered = stationVotes
                    .OrderByDescending(kv => kv.Value)
                    .ToList();

                var winner = ordered[0];
                var winnerRatio = winner.Value / stationVotes.Values.Sum();

                // 过半数投票 + 没有强势竞争者时才赋值
                if (winnerRatio < 0.5m)
                {
                    continue;
                }

                // 如果有第二名且得分接近（>= winner 的 70%），说明有冲突，跳过
                if (ordered.Count > 1 && ordered[1].Value >= winner.Value * 0.7m)
                {
                    continue;
                }

                // 2026-10-14：白名单外的工位不写，并统一成白名单里的写法
                var votedStation = StationBlockBindingRules.Canonicalize(winner.Key, allowedForVoting);
                if (votedStation == null)
                {
                    continue;
                }

                block.StationCode = votedStation;
                if (await _blockService.UpdateAsync(block))
                {
                    updatedCount++;
                }
            }

            if (updatedCount > 0)
            {
                _logger.LogInformation(
                    "[StationBackfill] 写入变量投票回填: {Updated}/{Total} 个无工位块获得工位分配",
                    updatedCount, unassignedBlocks.Count);

                try
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[StationBackfill] 写入变量投票回填完成: {updatedCount}/{unassignedBlocks.Count} 个块获得工位",
                        "StationBackfill");
                }
                catch { }
            }

            return updatedCount;
        }

        private static StationMapResult BuildStationMap(StationExtractionResultDto extractionResult)
        {
            var stationMap = new StationMapResult();

            stationMap.StationBuckets = extractionResult.StationBuckets
                .Select(it => new StationBucket
                {
                    StationCode = it.StationCode,
                    Confidence = it.Confidence,
                    AnchorCount = it.TotalSourceCount,
                    LogicUnitCount = it.LogicUnitCount,
                    BlockCount = it.BlockCount,
                    AnchorKeys = it.SourceKeys.ToList(),
                    VariableNames = extractionResult.PhysicalTagItems
                        .Concat(extractionResult.VariableGroupItems)
                        .Where(x => string.Equals(x.StationCode, it.StationCode, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.VariableName)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(20)
                        .ToList(),
                    Evidence = it.Evidence.ToList()
                })
                .ToList();

            stationMap.AnchorAssignments = extractionResult.PhysicalTagItems
                .Concat(extractionResult.VariableGroupItems)
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode) && !string.IsNullOrWhiteSpace(it.VariableName))
                .Select(it => new AnchorStationAssignment
                {
                    AnchorKey = it.SourceKey,
                    VariableName = it.VariableName,
                    AnchorType = it.SourceType,
                    StationCode = it.StationCode,
                    Confidence = it.Confidence,
                    Evidence = it.Evidence.Take(6).ToList()
                })
                .ToList();

            return stationMap;
        }
    }
}