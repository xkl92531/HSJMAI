using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 工位提取与标记前置阶段。
    /// 对原始 IOTag 与变量访问进行工位归属，不依赖 Q 点识别结果。
    /// </summary>
    public class StationExtractionService : ISelfScopedAutoInject
    {
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_BlockService _blockService;
        private readonly StationInferenceService _stationInferenceService;

        public StationExtractionService(
            IPLC_IOTagService ioTagService,
            IPLC_VariableAccessService variableAccessService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_BlockService blockService,
            StationInferenceService stationInferenceService)
        {
            _ioTagService = ioTagService;
            _variableAccessService = variableAccessService;
            _logicUnitService = logicUnitService;
            _blockService = blockService;
            _stationInferenceService = stationInferenceService;
        }

        public async Task<StationExtractionResultDto> BuildAsync(StationExtractionRequestDto request)
        {
            var result = new StationExtractionResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode
            };

            if (request.IncludePhysicalTags)
            {
                result.PhysicalTagItems = await BuildPhysicalTagItemsAsync(request);
            }

            if (request.IncludeVariableGroups)
            {
                result.VariableGroupItems = await BuildVariableGroupItemsAsync(request);
            }

            var allItems = result.PhysicalTagItems
                .Concat(result.VariableGroupItems)
                .ToList();

            var canonicalStationCodes = BuildCanonicalStationCodeMap(allItems);
            result.PhysicalTagItems = ApplyCanonicalStationCodes(result.PhysicalTagItems, canonicalStationCodes);
            result.VariableGroupItems = ApplyCanonicalStationCodes(result.VariableGroupItems, canonicalStationCodes);

            var trustedStationCodes = BuildTrustedStationCodes(result.PhysicalTagItems, result.VariableGroupItems);
            result.PhysicalTagItems = ApplyTrustedStationFilter(result.PhysicalTagItems, trustedStationCodes, isPhysicalTag: true);
            result.VariableGroupItems = ApplyTrustedStationFilter(result.VariableGroupItems, trustedStationCodes, isPhysicalTag: false);

            allItems = result.PhysicalTagItems
                .Concat(result.VariableGroupItems)
                .ToList();

            result.StationBuckets = BuildBuckets(allItems);
            result.Warnings = BuildWarnings(request, result, trustedStationCodes.Count);
            return result;
        }

        private async Task<List<StationMarkedSourceItem>> BuildPhysicalTagItemsAsync(StationExtractionRequestDto request)
        {
            HashSet<string> scopedVariableNames = null;
            if (request.BlockId.HasValue)
            {
                var blockId = request.BlockId.Value;
                scopedVariableNames = (await _variableAccessService.GetListAsync(it =>
                        it.IsDeleted == false &&
                        it.ProjectId == request.ProjectId &&
                        it.BlockId == blockId &&
                        !string.IsNullOrEmpty(it.VariableName)))
                    .Select(it => it.VariableName)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (scopedVariableNames.Count == 0)
                {
                    return new List<StationMarkedSourceItem>();
                }
            }

            var tags = await _ioTagService.GetListAsync(it =>
                it.IsDeleted == false &&
                it.ProjectId == request.ProjectId &&
                (!string.IsNullOrEmpty(it.TagName) || !string.IsNullOrEmpty(it.Address)));

            if (scopedVariableNames != null)
            {
                tags = tags
                    .Where(it => !string.IsNullOrWhiteSpace(it.TagName) && scopedVariableNames.Contains(it.TagName))
                    .ToList();
            }

            var inferenceMap = await _stationInferenceService.InferForPhysicalTagsAsync(tags, request.UseLlmAnnotation);
            var items = new List<StationMarkedSourceItem>();

            foreach (var tag in tags)
            {
                inferenceMap.TryGetValue(tag.Id, out var inference);
                inference ??= _stationInferenceService.InferForPhysicalTag(tag);

                if (!string.IsNullOrWhiteSpace(request.StationCode) &&
                    !string.Equals(inference.StationCode, request.StationCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                items.Add(new StationMarkedSourceItem
                {
                    SourceKey = $"tag:{tag.Id}",
                    SourceType = "IOTag",
                    VariableName = tag.TagName,
                    Address = tag.Address,
                    Path = tag.Path,
                    TableName = tag.TableName,
                    EquipmentId = tag.EquipmentId,
                    StationCode = inference.StationCode,
                    Confidence = inference.Confidence,
                    StationSource = inference.InferenceSource,
                    StationSourceDetail = inference.InferenceDetail,
                    Evidence = inference.Evidence.Take(8).ToList()
                });
            }

            return items
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .ToList();
        }

        private async Task<List<StationMarkedSourceItem>> BuildVariableGroupItemsAsync(StationExtractionRequestDto request)
        {
            var blockId = request.BlockId;
            var variableAccesses = blockId.HasValue
                ? await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.BlockId == blockId.Value &&
                    !string.IsNullOrEmpty(it.VariableName))
                : await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    !string.IsNullOrEmpty(it.VariableName));

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

            var logicUnitAccessMap = LogicUnitAnalysisPolicy.BuildEligibleAccessMap(variableAccesses, eligibleLogicUnitIds);

            var items = new List<StationMarkedSourceItem>();
            foreach (var group in variableAccesses
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase))
            {
                var accesses = group.ToList();
                var inference = _stationInferenceService.InferForVariableGroup(
                    group.Key,
                    accesses,
                    logicUnitMap,
                    blockMap,
                    logicUnitAccessMap);

                if (!string.IsNullOrWhiteSpace(request.StationCode) &&
                    !string.Equals(inference.StationCode, request.StationCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                items.Add(new StationMarkedSourceItem
                {
                    SourceKey = $"var:{group.Key}",
                    SourceType = "VariableGroup",
                    VariableName = group.Key,
                    StationCode = inference.StationCode,
                    Confidence = inference.Confidence,
                    StationSource = inference.InferenceSource,
                    StationSourceDetail = inference.InferenceDetail,
                    ReadCount = accesses.Count(IsReadAccess),
                    WriteCount = accesses.Count(IsWriteAccess),
                    LogicUnitCount = accesses.Where(it => it.LogicUnitId.HasValue).Select(it => it.LogicUnitId.Value).Distinct().Count(),
                    BlockCount = accesses.Where(it => it.BlockId.HasValue).Select(it => it.BlockId.Value).Distinct().Count(),
                    Evidence = inference.Evidence.Take(8).ToList()
                });
            }

            return items
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .ToList();
        }

        private static Dictionary<string, string> BuildCanonicalStationCodeMap(IEnumerable<StationMarkedSourceItem> items)
        {
            return items
                .Where(it => StationCodeHeuristics.IsLikelyStationCode(it.StationCode))
                .GroupBy(it => StationCodeHeuristics.GetAliasFamilyKey(it.StationCode), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .SelectMany(group =>
                {
                    var canonical = group
                        .GroupBy(it => StationCodeHeuristics.NormalizeStationCode(it.StationCode), StringComparer.OrdinalIgnoreCase)
                        .Select(g => new
                        {
                            StationCode = g.Key,
                            Count = g.Count(),
                            Confidence = g.Sum(it => Math.Max(it.Confidence, 0.1m)),
                            Priority = StationCodeHeuristics.GetStationPriority(g.Key)
                        })
                        .OrderByDescending(it => it.Count)
                        .ThenByDescending(it => it.Confidence)
                        .ThenByDescending(it => it.Priority)
                        .ThenBy(it => it.StationCode.Length)
                        .ThenBy(it => it.StationCode)
                        .First()
                        .StationCode;

                    return group
                        .Select(it => StationCodeHeuristics.NormalizeStationCode(it.StationCode))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(code => new KeyValuePair<string, string>(code, canonical));
                })
                .GroupBy(it => it.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().Value,
                    StringComparer.OrdinalIgnoreCase);
        }

        private static List<StationMarkedSourceItem> ApplyCanonicalStationCodes(
            IEnumerable<StationMarkedSourceItem> items,
            IReadOnlyDictionary<string, string> canonicalStationCodes)
        {
            return items
                .Select(item =>
                {
                    if (!StationCodeHeuristics.IsLikelyStationCode(item.StationCode))
                    {
                        return item;
                    }

                    var normalized = StationCodeHeuristics.NormalizeStationCode(item.StationCode);
                    if (!canonicalStationCodes.TryGetValue(normalized, out var canonical) ||
                        string.Equals(normalized, canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        return item;
                    }

                    return new StationMarkedSourceItem
                    {
                        SourceKey = item.SourceKey,
                        SourceType = item.SourceType,
                        VariableName = item.VariableName,
                        Address = item.Address,
                        Path = item.Path,
                        TableName = item.TableName,
                        EquipmentId = item.EquipmentId,
                        StationCode = canonical,
                        Confidence = item.Confidence,
                        StationSource = item.StationSource,
                        StationSourceDetail = item.StationSourceDetail,
                        ReadCount = item.ReadCount,
                        WriteCount = item.WriteCount,
                        LogicUnitCount = item.LogicUnitCount,
                        BlockCount = item.BlockCount,
                        Evidence = item.Evidence
                            .Where(it => !string.IsNullOrWhiteSpace(it))
                            .Take(6)
                            .Concat(new[] { $"工位别名归并: {normalized} -> {canonical}" })
                            .ToList()
                    };
                })
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .ToList();
        }

        private static HashSet<string> BuildTrustedStationCodes(
            IReadOnlyCollection<StationMarkedSourceItem> physicalItems,
            IReadOnlyCollection<StationMarkedSourceItem> variableItems)
        {
            var stats = new Dictionary<string, StationTrustStat>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in variableItems.Where(it => StationCodeHeuristics.IsLikelyStationCode(it.StationCode)))
            {
                if (!stats.TryGetValue(item.StationCode, out var stat))
                {
                    stat = new StationTrustStat();
                    stats[item.StationCode] = stat;
                }

                stat.VariableGroupCount++;
                stat.StructuralSupport += Math.Max(1, item.BlockCount) + Math.Max(0, item.LogicUnitCount);
                stat.MaxConfidence = Math.Max(stat.MaxConfidence, item.Confidence);
                stat.ConfidenceSum += item.Confidence;
                stat.TotalCount++;
            }

            foreach (var item in physicalItems.Where(it => StationCodeHeuristics.IsLikelyStationCode(it.StationCode) && it.Confidence >= 0.70m))
            {
                if (!stats.TryGetValue(item.StationCode, out var stat))
                {
                    stat = new StationTrustStat();
                    stats[item.StationCode] = stat;
                }

                stat.PhysicalTagCount++;
                stat.MaxConfidence = Math.Max(stat.MaxConfidence, item.Confidence);
                stat.ConfidenceSum += item.Confidence;
                stat.TotalCount++;

                if (!string.IsNullOrWhiteSpace(item.StationSourceDetail) &&
                    (item.StationSourceDetail.StartsWith("PathGroup:", StringComparison.OrdinalIgnoreCase) ||
                     item.StationSourceDetail.StartsWith("TableGroup:", StringComparison.OrdinalIgnoreCase)))
                {
                    stat.GroupLlmSupportCount++;
                }
            }

            return stats
                .Where(it =>
                    it.Value.VariableGroupCount >= 2 ||
                    (it.Value.VariableGroupCount >= 1 && it.Value.StructuralSupport >= 2 && it.Value.MaxConfidence >= 0.55m) ||
                    (it.Value.PhysicalTagCount >= 5 && it.Value.GroupLlmSupportCount >= 2) ||
                    it.Value.TotalCount >= 8)
                .Select(it => it.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<StationMarkedSourceItem> ApplyTrustedStationFilter(
            IEnumerable<StationMarkedSourceItem> items,
            ISet<string> trustedStationCodes,
            bool isPhysicalTag)
        {
            return items
                .Select(item =>
                {
                    if (string.IsNullOrWhiteSpace(item.StationCode))
                    {
                        return item;
                    }

                    if (!StationCodeHeuristics.IsLikelyStationCode(item.StationCode))
                    {
                        return CreateDeferredItem(item, isPhysicalTag, "当前命中更像部件/执行元件编号，暂不作为工位");
                    }

                    var isTrusted = trustedStationCodes.Contains(item.StationCode);
                    var allowDirectKeep = !isPhysicalTag && item.Confidence >= 0.65m && (item.BlockCount > 0 || item.LogicUnitCount > 0);
                    if (isTrusted || allowDirectKeep)
                    {
                        return item;
                    }

                    return CreateDeferredItem(item, isPhysicalTag, isPhysicalTag ? "延后到逻辑块染色阶段再判定工位" : "当前缺少全局稳定工位证据，暂不标记");
                })
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .ToList();
        }

        private static StationMarkedSourceItem CreateDeferredItem(StationMarkedSourceItem item, bool isPhysicalTag, string reason)
        {
            return new StationMarkedSourceItem
            {
                SourceKey = item.SourceKey,
                SourceType = item.SourceType,
                VariableName = item.VariableName,
                Address = item.Address,
                Path = item.Path,
                TableName = item.TableName,
                EquipmentId = item.EquipmentId,
                StationCode = string.Empty,
                Confidence = 0m,
                StationSource = string.Empty,
                StationSourceDetail = string.Empty,
                ReadCount = item.ReadCount,
                WriteCount = item.WriteCount,
                LogicUnitCount = item.LogicUnitCount,
                BlockCount = item.BlockCount,
                Evidence = item.Evidence
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Take(6)
                    .Concat(new[] { reason })
                    .ToList()
            };
        }

        private static List<StationExtractionBucket> BuildBuckets(IEnumerable<StationMarkedSourceItem> items)
        {
            return items
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode))
                .GroupBy(it => it.StationCode, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var groupedItems = group.ToList();
                    return new StationExtractionBucket
                    {
                        StationCode = group.Key,
                        Confidence = Math.Round(groupedItems.Average(it => it.Confidence), 3),
                        TotalSourceCount = groupedItems.Count,
                        PhysicalTagCount = groupedItems.Count(it => it.SourceType == "IOTag"),
                        VariableGroupCount = groupedItems.Count(it => it.SourceType == "VariableGroup"),
                        LogicUnitCount = groupedItems.Sum(it => it.LogicUnitCount),
                        BlockCount = groupedItems.Sum(it => it.BlockCount),
                        SourceKeys = groupedItems.Select(it => it.SourceKey).Take(20).ToList(),
                        Evidence = groupedItems.SelectMany(it => it.Evidence).Distinct().Take(8).ToList()
                    };
                })
                .OrderByDescending(it => it.TotalSourceCount)
                .ThenBy(it => it.StationCode)
                .ToList();
        }

        private static List<string> BuildWarnings(StationExtractionRequestDto request, StationExtractionResultDto result, int trustedStationCount)
        {
            var warnings = new List<string>();
            if (request.IncludePhysicalTags && result.PhysicalTagItems.Count == 0)
            {
                warnings.Add("未发现可用于工位识别的 IOTag。请检查导入数据是否完整。");
            }

            if (request.IncludeVariableGroups && result.VariableGroupItems.Count == 0)
            {
                warnings.Add("未发现可用于工位识别的变量访问记录。请检查解析结果是否已入库。");
            }

            if (result.StationBuckets.Count == 0)
            {
                warnings.Add("当前未聚合出稳定工位桶，建议检查 path、标签表名、网络标题或开启 LLM 兜底。");
            }

            if (trustedStationCount > 0 && result.StationBuckets.Count < trustedStationCount)
            {
                warnings.Add($"已从全局候选中收敛出 {trustedStationCount} 个可信工位，仅保留其中有稳定证据的工位桶。");
            }

            var deferredPhysical = result.PhysicalTagItems.Count(it => string.IsNullOrWhiteSpace(it.StationCode));
            if (request.IncludePhysicalTags && deferredPhysical > 0)
            {
                warnings.Add($"已延后 {deferredPhysical} 个低确定性 IOTag，等待逻辑块染色或人工确认后再归属工位。");
            }

            return warnings;
        }

        private sealed class StationTrustStat
        {
            public int TotalCount { get; set; }

            public int PhysicalTagCount { get; set; }

            public int VariableGroupCount { get; set; }

            public int GroupLlmSupportCount { get; set; }

            public int StructuralSupport { get; set; }

            public decimal MaxConfidence { get; set; }

            public decimal ConfidenceSum { get; set; }
        }

        private static bool IsReadAccess(PLC_VariableAccessEntity entity)
        {
            return string.Equals(entity.AccessType, "READ", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entity.AccessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWriteAccess(PLC_VariableAccessEntity entity)
        {
            return string.Equals(entity.AccessType, "WRITE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entity.AccessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }
    }
}