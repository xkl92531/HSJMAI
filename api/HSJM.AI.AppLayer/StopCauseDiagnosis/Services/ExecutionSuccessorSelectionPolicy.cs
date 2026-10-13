using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public static class ExecutionSuccessorSelectionPolicy
    {
        public static List<ExecutionSuccessorSource> SelectActiveRuntimeAnchors(
            IEnumerable<PLC_StationShadowSignalEntity> shadowSignals,
            IReadOnlyDictionary<string, StationShadowSignalCacheModel> signalMirrors)
        {
            return SelectActiveRuntimeAnchorsCore(shadowSignals, signalMirrors, graphOnly: true);
        }

        /// <summary>
        /// 非GRAPH场景：从所有运行时核心锚点（PhysicalAction、VirtualAction、HandshakeState）中
        /// 筛选当前处于激活态的锚点作为后继链搜索起点。
        /// </summary>
        public static List<ExecutionSuccessorSource> SelectActiveNonGraphAnchors(
            IEnumerable<PLC_StationShadowSignalEntity> shadowSignals,
            IReadOnlyDictionary<string, StationShadowSignalCacheModel> signalMirrors)
        {
            return SelectActiveRuntimeAnchorsCore(shadowSignals, signalMirrors, graphOnly: false);
        }

        private static List<ExecutionSuccessorSource> SelectActiveRuntimeAnchorsCore(
            IEnumerable<PLC_StationShadowSignalEntity> shadowSignals,
            IReadOnlyDictionary<string, StationShadowSignalCacheModel> signalMirrors,
            bool graphOnly)
        {
            var runtimeSignals = RuntimeAnchorSelectionPolicy.SelectAnchorScanSignals(shadowSignals);
            if (runtimeSignals.Count == 0 || signalMirrors == null || signalMirrors.Count == 0)
            {
                return new List<ExecutionSuccessorSource>();
            }

            var mirrorByVariable = BuildMirrorByVariable(signalMirrors);

            return runtimeSignals
                .Where(signal => IsEligibleAnchorType(signal.AnchorType, graphOnly)
                    && signal.DiagnosisExcluded != true
                    && mirrorByVariable.TryGetValue(Normalize(signal.VariableName), out var mirror)
                    && IsTrueValue(mirror?.CurrentValue)
                    && !IsStableAlwaysOnGraphAction(signal, mirror))
                .GroupBy(signal => Normalize(signal.VariableName), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var signal = group.First();
                    return new ExecutionSuccessorSource
                    {
                        VariableName = signal.VariableName.Trim(),
                        AnchorType = signal.AnchorType?.Trim(),
                        DisplayLabel = BuildDisplayLabel(signal)
                    };
                })
                .OrderByDescending(item => RuntimeAnchorSelectionPolicy.IsRuntimeCoreAnchorType(item.AnchorType) ? 1 : 0)
                .ThenBy(item => item.DisplayLabel)
                .ToList();
        }

        private static bool IsEligibleAnchorType(string anchorType, bool graphOnly)
        {
            if (graphOnly)
            {
                return string.Equals(anchorType, "GraphAction", StringComparison.OrdinalIgnoreCase);
            }

            return RuntimeAnchorSelectionPolicy.IsRuntimeCoreAnchorType(anchorType)
                || RuntimeAnchorSelectionPolicy.IsRuntimeContextAnchorType(anchorType);
        }

        /// <summary>
        /// 自动检测“只要运行就常亮”的 GraphAction 步骤节点。
        /// 规则：GraphAction + 当前值为 TRUE + 最近变化时间距现在超过阈值（10分钟）。
        /// 说明：停机时卡住的步骤 LastChangeTime 通常在停机前几秒/几分钟内，而"常亮"步骤
        ///       （如机器通电状态、自动模式等）可能持续数小时甚至更久，10分钟可有效区分两者。
        /// </summary>
        private static bool IsStableAlwaysOnGraphAction(PLC_StationShadowSignalEntity signal, StationShadowSignalCacheModel mirror)
        {
            if (!string.Equals(signal?.AnchorType, "GraphAction", StringComparison.OrdinalIgnoreCase))
                return false;
            if (mirror?.LastChangeTime == null)
                return false;
            const int stableSecondsThreshold = 600; // 10 分钟：真正常亮信号通常已稳定数小时以上
            return (DateTime.Now - mirror.LastChangeTime.Value).TotalSeconds >= stableSecondsThreshold;
        }

        public static List<ExecutionSuccessorCandidate> BuildCandidates(
            IEnumerable<ExecutionSuccessorSource> activeAnchors,
            IEnumerable<VariablePropagationLink> links,
            IEnumerable<PLC_StationShadowSignalEntity> shadowSignals,
            IReadOnlyDictionary<string, StationShadowSignalCacheModel> signalMirrors,
            int maxDepth = 3,
            int maxCandidates = 12)
        {
            var sources = (activeAnchors ?? Enumerable.Empty<ExecutionSuccessorSource>())
                .Where(item => !string.IsNullOrWhiteSpace(item?.VariableName))
                .GroupBy(item => Normalize(item.VariableName), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (sources.Count == 0)
            {
                return new List<ExecutionSuccessorCandidate>();
            }

            var targetSignals = RuntimeAnchorSelectionPolicy.SelectAnchorScanSignals(shadowSignals)
                .GroupBy(signal => Normalize(signal.VariableName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            if (targetSignals.Count == 0)
            {
                return new List<ExecutionSuccessorCandidate>();
            }

            var edgesBySource = (links ?? Enumerable.Empty<VariablePropagationLink>())
                .Where(link => !string.IsNullOrWhiteSpace(link?.FromVariable) && !string.IsNullOrWhiteSpace(link.ToVariable))
                .GroupBy(link => Normalize(link.FromVariable), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            if (edgesBySource.Count == 0)
            {
                return new List<ExecutionSuccessorCandidate>();
            }

            var mirrorByVariable = BuildMirrorByVariable(signalMirrors);
            var effectiveMaxDepth = maxDepth <= 0 ? 3 : maxDepth;
            var effectiveMaxCandidates = maxCandidates <= 0 ? 12 : maxCandidates;
            var candidateMap = new Dictionary<string, ExecutionSuccessorCandidate>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                var sourceVariable = Normalize(source.VariableName);
                if (string.IsNullOrWhiteSpace(sourceVariable))
                {
                    continue;
                }

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceVariable };
                var queue = new Queue<TraversalState>();
                queue.Enqueue(new TraversalState
                {
                    VariableName = sourceVariable,
                    Depth = 0,
                    PathConfidence = 1m,
                    Path = new List<string> { sourceVariable },
                    Evidence = new List<string> { $"Source: {source.DisplayLabel ?? sourceVariable}" }
                });

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (current.Depth >= effectiveMaxDepth)
                    {
                        continue;
                    }

                    if (!edgesBySource.TryGetValue(current.VariableName, out var outgoingEdges) || outgoingEdges.Count == 0)
                    {
                        continue;
                    }

                    foreach (var edge in outgoingEdges)
                    {
                        var nextVariable = Normalize(edge.ToVariable);
                        if (string.IsNullOrWhiteSpace(nextVariable) || !visited.Add(nextVariable))
                        {
                            continue;
                        }

                        var nextDepth = current.Depth + 1;
                        var nextConfidence = ComputeNextConfidence(current.PathConfidence, edge.Confidence, nextDepth);
                        var nextPath = new List<string>(current.Path) { nextVariable };
                        var nextEvidence = new List<string>(current.Evidence);
                        nextEvidence.Add(BuildEdgeEvidence(edge));

                        if (targetSignals.TryGetValue(nextVariable, out var targetSignal)
                            && !string.Equals(nextVariable, sourceVariable, StringComparison.OrdinalIgnoreCase)
                            && !IsTrueValue(GetCurrentValue(nextVariable, mirrorByVariable)))
                        {
                            UpsertCandidate(candidateMap, nextVariable, targetSignal, source, nextConfidence, nextDepth, nextPath, nextEvidence);
                        }

                        if (nextDepth < effectiveMaxDepth)
                        {
                            queue.Enqueue(new TraversalState
                            {
                                VariableName = nextVariable,
                                Depth = nextDepth,
                                PathConfidence = nextConfidence,
                                Path = nextPath,
                                Evidence = nextEvidence
                            });
                        }
                    }
                }
            }

            return candidateMap.Values
                .OrderByDescending(item => RuntimeAnchorSelectionPolicy.IsRuntimeCoreAnchorType(item.AnchorType) ? 1 : 0)
                .ThenByDescending(item => item.Confidence)
                .ThenBy(item => item.Depth)
                .ThenBy(item => item.VariableName)
                .Take(effectiveMaxCandidates)
                .ToList();
        }

        private static void UpsertCandidate(
            IDictionary<string, ExecutionSuccessorCandidate> candidateMap,
            string variableName,
            PLC_StationShadowSignalEntity targetSignal,
            ExecutionSuccessorSource source,
            decimal confidence,
            int depth,
            IReadOnlyCollection<string> path,
            IReadOnlyCollection<string> evidence)
        {
            if (!candidateMap.TryGetValue(variableName, out var candidate))
            {
                candidate = new ExecutionSuccessorCandidate
                {
                    VariableName = variableName,
                    DisplayLabel = BuildDisplayLabel(targetSignal),
                    AnchorType = targetSignal.AnchorType?.Trim(),
                    Confidence = confidence,
                    Depth = depth,
                    SourceVariables = new List<string>(),
                    Evidence = new List<string>()
                };
                candidateMap[variableName] = candidate;
            }

            candidate.Confidence = Math.Max(candidate.Confidence, confidence);
            candidate.Depth = Math.Min(candidate.Depth, depth);

            if (!candidate.SourceVariables.Contains(source.VariableName, StringComparer.OrdinalIgnoreCase))
            {
                candidate.SourceVariables.Add(source.VariableName);
            }

            var pathSummary = $"Path: {string.Join(" -> ", path)}";
            if (!candidate.Evidence.Contains(pathSummary, StringComparer.OrdinalIgnoreCase))
            {
                candidate.Evidence.Add(pathSummary);
            }

            foreach (var item in evidence.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                if (!candidate.Evidence.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    candidate.Evidence.Add(item);
                }
            }
        }

        private static Dictionary<string, StationShadowSignalCacheModel> BuildMirrorByVariable(
            IReadOnlyDictionary<string, StationShadowSignalCacheModel> signalMirrors)
        {
            return (signalMirrors ?? new Dictionary<string, StationShadowSignalCacheModel>())
                .Values
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.VariableName))
                .GroupBy(item => Normalize(item.VariableName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(item => IsTrueValue(item.CurrentValue) ? 1 : 0)
                        .ThenByDescending(item => item.LastChangeTime ?? DateTime.MinValue)
                        .First(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static object GetCurrentValue(string variableName, IReadOnlyDictionary<string, StationShadowSignalCacheModel> mirrorByVariable)
        {
            return mirrorByVariable != null
                && mirrorByVariable.TryGetValue(Normalize(variableName), out var mirror)
                    ? mirror?.CurrentValue
                    : null;
        }

        private static string BuildDisplayLabel(PLC_StationShadowSignalEntity signal)
        {
            if (!string.IsNullOrWhiteSpace(signal?.SignalKey))
            {
                return signal.SignalKey.Trim();
            }

            if (!string.IsNullOrWhiteSpace(signal?.SemanticLabel))
            {
                return signal.SemanticLabel.Trim();
            }

            return signal?.VariableName?.Trim() ?? string.Empty;
        }

        private static string BuildEdgeEvidence(VariablePropagationLink edge)
        {
            var compact = $"{edge.FromVariable} -> {edge.ToVariable} ({edge.PropagationType})";
            var evidence = edge.Evidence?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
            return string.IsNullOrWhiteSpace(evidence) ? compact : $"{compact}: {evidence}";
        }

        private static decimal ComputeNextConfidence(decimal currentConfidence, decimal edgeConfidence, int depth)
        {
            var normalizedEdgeConfidence = edgeConfidence > 0 ? edgeConfidence : 0.35m;
            var depthPenalty = depth <= 1 ? 1m : Math.Max(0.55m, 1m - ((depth - 1) * 0.15m));
            return Math.Round(currentConfidence * normalizedEdgeConfidence * depthPenalty, 4, MidpointRounding.AwayFromZero);
        }

        public static bool IsFreshlyActivatedGraphAction(StationShadowSignalCacheModel mirror, int graceSeconds, DateTime now)
        {
            if (graceSeconds <= 0 || mirror?.LastChangeTime == null || !IsTrueValue(mirror.CurrentValue))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(mirror.AnchorType)
                && !string.Equals(mirror.AnchorType, "GraphAction", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var ageSeconds = (now - mirror.LastChangeTime.Value).TotalSeconds;
            return ageSeconds >= -1 && ageSeconds <= graceSeconds;
        }

        private static bool IsTrueValue(object value)
        {
            if (value is bool boolValue)
            {
                return boolValue;
            }

            var text = value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (bool.TryParse(text, out var parsedBool))
            {
                return parsedBool;
            }

            return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedNumber)
                && parsedNumber != 0;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private sealed class TraversalState
        {
            public string VariableName { get; set; }

            public int Depth { get; set; }

            public decimal PathConfidence { get; set; }

            public List<string> Path { get; set; } = new List<string>();

            public List<string> Evidence { get; set; } = new List<string>();
        }
    }
}
