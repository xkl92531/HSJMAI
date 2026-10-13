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
    /// 多证据工位推断服务。
    /// 用于把物理 Q 点、虚拟 Q 点以及后续逻辑片段归属到具体工位。
    /// 设计目标：不依赖变量命名规范，而是综合 block / logic-unit / IOTag 邻域 / 注释多源信息。
    /// </summary>
    public class StationInferenceService : ISelfScopedAutoInject
    {
        private static readonly string[] GenericPathSegments =
        {
            "ROOT", "IOTAG", "IO", "TAG", "TAGS", "VALVE", "SENSOR", "ACTUATOR", "CYL",
            "CYLINDER", "气缸", "输出", "输入", "变量", "信号"
        };

        private readonly LlmAnchorAnnotatorService _llmAnnotator;

        public StationInferenceService(LlmAnchorAnnotatorService llmAnnotator = null)
        {
            _llmAnnotator = llmAnnotator;
        }

        public StationInferenceResult InferForPhysicalTag(PLC_IOTagEntity tag)
        {
            var scores = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var evidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            AddMatches(scores, evidence, tag?.Path, 0.40m, "IOTag 路径命中工位");
            AddMatches(scores, evidence, tag?.TagName, 0.32m, "IOTag 名称命中工位");
            AddMatches(scores, evidence, tag?.Comment, 0.18m, "IOTag 注释命中工位");
            AddMatches(scores, evidence, tag?.TableName, 0.16m, "标签表名命中工位");
            AddMatches(scores, evidence, tag?.EquipmentId, 0.14m, "设备编号命中工位");

            return BuildResult(scores, evidence);
        }

        public async Task<Dictionary<long, StationInferenceResult>> InferForPhysicalTagsAsync(
            IReadOnlyList<PLC_IOTagEntity> tags,
            bool useLlmFallback)
        {
            var tagList = tags?
                .Where(it => it != null && it.Id > 0)
                .ToList() ?? new List<PLC_IOTagEntity>();

            if (!useLlmFallback || _llmAnnotator == null)
            {
                return tagList.ToDictionary(it => it.Id, InferForPhysicalTag);
            }

            var results = tagList.ToDictionary(
                it => it.Id,
                _ => new StationInferenceResult());

            await ApplyGroupedPhysicalLlmInferenceAsync(
                tagList,
                results,
                tag => NormalizePathGroupKey(tag.Path),
                "PathGroup");

            await ApplyGroupedPhysicalLlmInferenceAsync(
                tagList,
                results,
                tag => NormalizeTableGroupKey(tag.TableName),
                "TableGroup");

            await ApplyLlmPhysicalInferenceAsync(tagList, results);
            return results;
        }

        public StationInferenceResult InferForVariableGroup(
            string variableName,
            IReadOnlyList<PLC_VariableAccessEntity> accesses,
            IReadOnlyDictionary<long, PLC_LogicUnitEntity> logicUnitMap,
            IReadOnlyDictionary<long, PLC_BlockEntity> blockMap,
            IReadOnlyDictionary<long, List<PLC_VariableAccessEntity>> logicUnitAccessMap)
        {
            var scores = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var evidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            AddMatches(scores, evidence, variableName, 0.08m, "变量名命中工位");

            foreach (var access in accesses ?? Array.Empty<PLC_VariableAccessEntity>())
            {
                AddMatches(scores, evidence, access.Comment, 0.10m, "变量访问注释命中工位");

                if (access.BlockId.HasValue && blockMap.TryGetValue(access.BlockId.Value, out var block))
                {
                    AddExact(scores, evidence, NormalizeStationCode(block.StationCode), 0.55m, $"块 {block.BlockName} 显式工位");
                    AddMatches(scores, evidence, block.BlockName, 0.24m, $"块名 {block.BlockName} 命中工位");
                    AddMatches(scores, evidence, block.Description, 0.16m, $"块描述 {block.BlockName} 命中工位");
                }

                if (access.LogicUnitId.HasValue && logicUnitMap.TryGetValue(access.LogicUnitId.Value, out var logicUnit))
                {
                    AddMatches(scores, evidence, logicUnit.Title, 0.26m, $"网络标题 {logicUnit.Title} 命中工位");
                    AddMatches(scores, evidence, logicUnit.Comment, 0.18m, $"网络注释 {logicUnit.Title} 命中工位");

                    if (logicUnitAccessMap.TryGetValue(access.LogicUnitId.Value, out var neighborAccesses))
                    {
                        foreach (var neighbor in neighborAccesses)
                        {
                            if (string.Equals(neighbor.VariableName, variableName, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // 同网络物理 IOTag 是最强的侧向工位证据。
                            if (neighbor.IsIOTag == true)
                            {
                                AddMatches(scores, evidence, neighbor.VariableName, 0.30m, $"同网络物理点 {neighbor.VariableName} 命中工位");
                                AddMatches(scores, evidence, neighbor.Comment, 0.20m, $"同网络物理点注释 {neighbor.VariableName} 命中工位");
                            }
                            else
                            {
                                AddMatches(scores, evidence, neighbor.VariableName, 0.06m, $"同网络变量 {neighbor.VariableName} 命中工位");
                                AddMatches(scores, evidence, neighbor.Comment, 0.05m, $"同网络变量注释 {neighbor.VariableName} 命中工位");
                            }
                        }
                    }
                }
            }

            return BuildResult(scores, evidence);
        }

        private static StationInferenceResult BuildResult(
            IReadOnlyDictionary<string, decimal> scores,
            IReadOnlyDictionary<string, List<string>> evidence)
        {
            if (scores.Count == 0)
            {
                return new StationInferenceResult
                {
                    StationCode = string.Empty,
                    Confidence = 0m
                };
            }

            var best = scores
                .OrderByDescending(it => it.Value)
                .ThenByDescending(it => StationCodeHeuristics.GetStationPriority(it.Key))
                .ThenBy(it => it.Key)
                .First();

            return new StationInferenceResult
            {
                StationCode = best.Key,
                Confidence = Math.Min(best.Value, 0.98m),
                InferenceSource = "Rule",
                InferenceDetail = "ScoreAggregation",
                Evidence = evidence.TryGetValue(best.Key, out var list)
                    ? list.Take(6).ToList()
                    : new List<string>()
            };
        }

        private async Task ApplyGroupedPhysicalLlmInferenceAsync(
            IReadOnlyList<PLC_IOTagEntity> tags,
            IDictionary<long, StationInferenceResult> results,
            Func<PLC_IOTagEntity, string> keySelector,
            string groupType)
        {
            var groups = tags
                .Select(tag => new
                {
                    Tag = tag,
                    GroupKey = keySelector(tag)
                })
                .Where(it => !string.IsNullOrWhiteSpace(it.GroupKey))
                .GroupBy(it => it.GroupKey, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2)
                .ToList();

            if (groups.Count == 0)
            {
                return;
            }

            var contexts = groups
                .Select(group => BuildPhysicalGroupContext(groupType, group.Key, group.Select(it => it.Tag).ToList()))
                .ToList();

            var llmResults = await _llmAnnotator.InferStationsForPhysicalTagGroupsAsync(contexts);

            foreach (var group in groups)
            {
                var groupResultKey = $"group:{groupType}:{group.Key}";
                if (!llmResults.TryGetValue(groupResultKey, out var groupInference))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(groupInference.StationCode) || groupInference.Confidence < 0.60m)
                {
                    continue;
                }

                foreach (var tag in group.Select(it => it.Tag))
                {
                    var current = results[tag.Id];
                    if (!ShouldApplyGroupedInference(current, groupInference))
                    {
                        continue;
                    }

                    results[tag.Id] = MergeInference(current, groupInference, $"LLM{groupType}:{group.Key}");
                }
            }
        }

        private async Task ApplyLlmPhysicalInferenceAsync(
            IReadOnlyList<PLC_IOTagEntity> tags,
            IDictionary<long, StationInferenceResult> results)
        {
            var pending = tags
                .Where(tag =>
                {
                    var current = results[tag.Id];
                    return string.IsNullOrWhiteSpace(current?.StationCode) || current.Confidence < 0.45m;
                })
                .ToList();

            if (pending.Count == 0)
            {
                return;
            }

            var llmResults = await _llmAnnotator.InferStationsForPhysicalTagsAsync(pending);
            foreach (var tag in pending)
            {
                var key = $"tag:{tag.Id}";
                if (!llmResults.TryGetValue(key, out var llmInference))
                {
                    continue;
                }

                var current = results[tag.Id];
                if (!ShouldApplyLlmInference(current, llmInference))
                {
                    continue;
                }

                results[tag.Id] = MergeInference(current, llmInference, "LLMPhysical");
            }
        }

        private static PhysicalTagGroupContext BuildPhysicalGroupContext(
            string groupType,
            string groupKey,
            IReadOnlyList<PLC_IOTagEntity> tags)
        {
            return new PhysicalTagGroupContext
            {
                GroupKey = groupKey,
                GroupType = groupType,
                GroupPath = tags.Select(it => it.Path).FirstOrDefault(it => !string.IsNullOrWhiteSpace(it)) ?? string.Empty,
                GroupTableName = tags.Select(it => it.TableName).FirstOrDefault(it => !string.IsNullOrWhiteSpace(it)) ?? string.Empty,
                ItemCount = tags.Count,
                SampleTagNames = tags.Select(it => it.TagName).Where(it => !string.IsNullOrWhiteSpace(it)).Distinct().Take(8).ToList(),
                SampleComments = tags.Select(it => it.Comment).Where(it => !string.IsNullOrWhiteSpace(it)).Distinct().Take(6).ToList(),
                SampleEquipmentIds = tags.Select(it => it.EquipmentId).Where(it => !string.IsNullOrWhiteSpace(it)).Distinct().Take(6).ToList()
            };
        }

        private static bool ShouldApplyGroupedInference(
            StationInferenceResult current,
            StationInferenceResult grouped)
        {
            if (grouped == null || string.IsNullOrWhiteSpace(grouped.StationCode))
            {
                return false;
            }

            if (current == null || string.IsNullOrWhiteSpace(current.StationCode))
            {
                return true;
            }

            if (string.Equals(current.StationCode, grouped.StationCode, StringComparison.OrdinalIgnoreCase))
            {
                return grouped.Confidence > current.Confidence;
            }

            return current.Confidence < 0.35m && grouped.Confidence >= current.Confidence + 0.20m;
        }

        private static bool ShouldApplyLlmInference(
            StationInferenceResult current,
            StationInferenceResult llmInference)
        {
            if (llmInference == null || string.IsNullOrWhiteSpace(llmInference.StationCode) || llmInference.Confidence < 0.60m)
            {
                return false;
            }

            if (current == null || string.IsNullOrWhiteSpace(current.StationCode))
            {
                return true;
            }

            if (string.Equals(current.StationCode, llmInference.StationCode, StringComparison.OrdinalIgnoreCase))
            {
                return llmInference.Confidence > current.Confidence;
            }

            return llmInference.Confidence >= current.Confidence + 0.15m;
        }

        private static StationInferenceResult MergeInference(
            StationInferenceResult current,
            StationInferenceResult incoming,
            string source)
        {
            var evidence = new List<string>();
            evidence.AddRange(current?.Evidence ?? new List<string>());
            evidence.AddRange((incoming?.Evidence ?? new List<string>()).Select(it => $"{source}: {it}"));

            return new StationInferenceResult
            {
                StationCode = incoming?.StationCode ?? current?.StationCode ?? string.Empty,
                Confidence = Math.Max(current?.Confidence ?? 0m, incoming?.Confidence ?? 0m),
                InferenceSource = incoming?.InferenceSource ?? current?.InferenceSource,
                InferenceDetail = incoming?.InferenceDetail ?? current?.InferenceDetail,
                Evidence = evidence
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList()
            };
        }

        private static void AddMatches(
            IDictionary<string, decimal> scores,
            IDictionary<string, List<string>> evidence,
            string text,
            decimal score,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            foreach (var stationCode in ExtractStationCodes(text))
            {
                AddExact(scores, evidence, stationCode, score, reason);
            }
        }

        private static IEnumerable<string> ExtractStationCodes(string text)
        {
            return StationCodeHeuristics.ExtractStationCodes(text);
        }

        private static string NormalizePathGroupKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var segments = path
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(it => Regex.Replace(it.Trim().ToUpperInvariant(), @"[\s_-]+", string.Empty))
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Where(it => !GenericPathSegments.Contains(it, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (segments.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("/", segments.TakeLast(2));
        }

        private static string NormalizeTableGroupKey(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return string.Empty;
            }

            return Regex.Replace(tableName.Trim().ToUpperInvariant(), @"[\s_-]+", string.Empty);
        }

        private static void AddExact(
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

            if (!scores.ContainsKey(stationCode))
            {
                scores[stationCode] = 0m;
                evidence[stationCode] = new List<string>();
            }

            scores[stationCode] += score;
            evidence[stationCode].Add(reason);
        }

        private static string NormalizeStationCode(string stationCode)
        {
            return StationCodeHeuristics.NormalizeStationCode(stationCode);
        }
    }
}