using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory
{
    /// <summary>
    /// 一个 PLC 的诊断实时信号（D-1）：Values 只含 Good 值；Statuses 为非 Good 信号的原因；ProjectStatus 为 PLC 级读取状态。
    /// 键 = ReadDiagnosticSignalsAsync 的 TagName（TableName.TagName 去 "IQ."），忽略大小写。
    /// </summary>
    internal sealed class PlcLiveSignals
    {
        public long ProjectId { get; init; }

        public string PlcName { get; init; }

        public PLC_DiagnosticProjectStatus ProjectStatus { get; init; }

        public string ProjectError { get; init; }

        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, SignalUnknownReason> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>裸点名（PLC_DiagnosticSignal.OldTagName = ai_plc_iotag.TagName）→ 键；同一裸点名可能出现在多个变量表。</summary>
        private readonly Dictionary<string, List<string>> _keysByTagName = new(StringComparer.OrdinalIgnoreCase);

        internal const string AmbiguousTagNameDetail = "点名在多个变量表中存在且取值不一致";

        internal const string AmbiguousSuffixDetail = "点名后缀匹配到多个点且取值不一致";

        internal const string CaseVariantCollisionDetail = "大小写不同的重名点，无法确定取值";

        internal const string NotReadForDiagnosisDetail = "该点未参与诊断读取";

        public int GoodCount => Values.Count;

        public int TotalCount => Values.Count + Statuses.Count;

        public static PlcLiveSignals FromReadResult(long projectId, string plcName, PLC_DiagnosticReadResult result)
        {
            var live = new PlcLiveSignals
            {
                ProjectId = projectId,
                PlcName = plcName,
                ProjectStatus = result?.ProjectStatus ?? PLC_DiagnosticProjectStatus.CommFailure,
                ProjectError = result == null ? "实时信号读取无结果" : result.ProjectError,
            };
            // 忽略大小写的键 → 首个写入它的原始拼写（Ordinal）。大小写不同的另一拼写再写入同一键 = 两个不同的点
            // 共用了一个字典槽（如 T.flag Good / T.Flag Bad），后写覆盖先写会让坏点继承别的点的 Good 值。
            var spellingByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var caseCollidedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var signal in result?.Signals ?? Array.Empty<PLC_DiagnosticSignal>())
            {
                if (signal == null || string.IsNullOrEmpty(signal.TagName))
                {
                    continue;
                }
                var key = signal.TagName;
                if (!spellingByKey.TryGetValue(key, out var firstSpelling))
                {
                    spellingByKey[key] = key;
                    live.Store(signal);
                }
                else if (string.Equals(firstSpelling, key, StringComparison.Ordinal))
                {
                    // 同一拼写重复出现：沿用原规则（Good 优先）；已判定重名冲突的键不再被改写
                    if (!caseCollidedKeys.Contains(key))
                    {
                        live.Store(signal);
                    }
                }
                else if (!caseCollidedKeys.Contains(key) && !live.AgreesWithStoredGood(signal))
                {
                    // 大小写不同的重名点：两者都 Good 且值相同才可沿用，否则无法确定取值
                    caseCollidedKeys.Add(key);
                    live.Values.Remove(key);
                    live.Statuses[key] = SignalUnknownReason.FromStatus(PLC_SignalReadStatus.DeviceError,
                        $"{CaseVariantCollisionDetail}：{firstSpelling}, {key}");
                }
                if (!string.IsNullOrWhiteSpace(signal.OldTagName))
                {
                    if (!live._keysByTagName.TryGetValue(signal.OldTagName, out var keys))
                    {
                        live._keysByTagName[signal.OldTagName] = keys = new List<string>();
                    }
                    // 每种拼写都登记（Ordinal 去重）：大小写不同的重名点都指向同一个已判冲突的键
                    if (!keys.Contains(key, StringComparer.Ordinal))
                    {
                        keys.Add(key);
                    }
                }
            }
            return live;
        }

        /// <summary>按原规则写入一个信号：Good 写值并清状态；非 Good 仅在该键尚无 Good 值时记原因。</summary>
        private void Store(PLC_DiagnosticSignal signal)
        {
            if (signal.Status == PLC_SignalReadStatus.Good)
            {
                Values[signal.TagName] = signal.Value;
                Statuses.Remove(signal.TagName);
            }
            else if (!Values.ContainsKey(signal.TagName))
            {
                Statuses[signal.TagName] = SignalUnknownReason.FromStatus(signal.Status, signal.Detail);
            }
        }

        /// <summary>信号为 Good 且与该键已存的 Good 值相等（按布尔语义或 Equals）。</summary>
        private bool AgreesWithStoredGood(PLC_DiagnosticSignal signal)
        {
            if (signal.Status != PLC_SignalReadStatus.Good || !Values.TryGetValue(signal.TagName, out var stored))
            {
                return false;
            }
            if (TryConvertToBool(stored, out var a) && TryConvertToBool(signal.Value, out var b))
            {
                return a == b;
            }
            return Equals(stored, signal.Value);
        }

        public static PlcLiveSignals Failed(long projectId, string plcName, PLC_DiagnosticProjectStatus status, string error) =>
            new() { ProjectId = projectId, PlcName = plcName, ProjectStatus = status, ProjectError = error };

        /// <summary>
        /// 为规则信号给出 Good 布尔值或未采到原因（每个非空信号名必落其一）。匹配顺序：
        /// ① 精确键（TableName.TagName）；② 精确裸点名（OldTagName，可能跨多个变量表）；
        /// ③ 前两者都没有才后缀兜底（值与状态一起找候选，".X" 一档优先于 "]X" / "_X" 一档）。
        /// ②③ 命中多个点时：全部 Good 且布尔值一致 → 该值；全部非 Good 且原因码相同 → 该原因；
        /// 否则 → 无法判定（DeviceError「取值不一致」）——绝不让别的点的 Good 值顶替本点。
        /// <paramref name="isKnownIoTagName"/>：本 PLC IO 表里已有的点名（如被诊断口径过滤未读取）不走 ③，
        /// 直接判 Missing「该点未参与诊断读取」——后缀兜底只给 IO 表里没有的名字用。
        /// </summary>
        public void Resolve(
            IEnumerable<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> goodValues,
            Dictionary<string, SignalUnknownReason> unknownSignals,
            Func<string, bool> isKnownIoTagName = null)
        {
            foreach (var signal in signals ?? Enumerable.Empty<PLC_IOConstraintSignalEntity>())
            {
                var name = signal?.SignalName;
                if (string.IsNullOrWhiteSpace(name) || goodValues.ContainsKey(name) || unknownSignals.ContainsKey(name))
                {
                    continue;
                }

                List<string> candidates;
                string ambiguousDetail;
                var knownButNotRead = false;
                if (Values.ContainsKey(name) || Statuses.ContainsKey(name))
                {
                    candidates = new List<string> { name };
                    ambiguousDetail = null;
                }
                else if (_keysByTagName.TryGetValue(name, out var byTagName))
                {
                    candidates = byTagName;
                    ambiguousDetail = AmbiguousTagNameDetail;
                }
                else if (isKnownIoTagName != null && isKnownIoTagName(name))
                {
                    candidates = new List<string>();
                    ambiguousDetail = null;
                    knownButNotRead = true;
                }
                else
                {
                    candidates = FindSuffixCandidates(name);
                    ambiguousDetail = AmbiguousSuffixDetail;
                }

                if (candidates.Count == 0)
                {
                    unknownSignals[name] = ProjectStatus switch
                    {
                        PLC_DiagnosticProjectStatus.Ok when knownButNotRead =>
                            SignalUnknownReason.FromStatus(PLC_SignalReadStatus.NotRead, NotReadForDiagnosisDetail),
                        PLC_DiagnosticProjectStatus.Ok =>
                            SignalUnknownReason.FromStatus(PLC_SignalReadStatus.Missing, "本 PLC 读取结果中没有该信号"),
                        PLC_DiagnosticProjectStatus.ProjectUnavailable =>
                            new SignalUnknownReason(SignalUnknownReason.ProjectUnavailable, ProjectError ?? "PLC 项目不可读"),
                        _ => SignalUnknownReason.FromStatus(PLC_SignalReadStatus.CommFailure, ProjectError ?? "PLC 读取失败"),
                    };
                    continue;
                }

                var results = candidates.Select(ResolveKey).ToList();
                if (results.All(r => r.Reason == null) && results.Select(r => r.Value).Distinct().Count() == 1)
                {
                    goodValues[name] = results[0].Value;
                }
                else if (results.All(r => r.Reason != null) && results.Select(r => r.Reason.Code).Distinct().Count() == 1)
                {
                    unknownSignals[name] = results[0].Reason;
                }
                else
                {
                    unknownSignals[name] = SignalUnknownReason.FromStatus(PLC_SignalReadStatus.DeviceError,
                        $"{ambiguousDetail}：{string.Join(", ", candidates)}");
                }
            }
        }

        /// <summary>单个键的解析结果：Reason 为 null 表示 Good 布尔值。</summary>
        private readonly record struct KeyResolution(bool Value, SignalUnknownReason Reason);

        private KeyResolution ResolveKey(string key)
        {
            if (Values.TryGetValue(key, out var raw))
            {
                return TryConvertToBool(raw, out var value)
                    ? new KeyResolution(value, null)
                    : new KeyResolution(false, new SignalUnknownReason(SignalUnknownReason.NotBoolean, $"{key}={raw}"));
            }
            return new KeyResolution(false, Statuses.TryGetValue(key, out var reason)
                ? reason
                : SignalUnknownReason.FromStatus(PLC_SignalReadStatus.Missing, "本 PLC 读取结果中没有该信号"));
        }

        /// <summary>后缀兜底：值与状态键一起找候选；".X" 一档优先，没有才用 "]X" / "_X" 一档。</summary>
        private List<string> FindSuffixCandidates(string name)
        {
            var keys = Values.Keys.Concat(Statuses.Keys).ToList();
            var dotTier = keys.Where(k => k.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (dotTier.Count > 0)
            {
                return dotTier;
            }
            return keys.Where(k =>
                    k.EndsWith("]" + name, StringComparison.OrdinalIgnoreCase)
                    || k.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>实时值 → 布尔（bool / "true"/"false" / "1"/"0"），与旧 TryConvertToBool 同规则。</summary>
        internal static bool TryConvertToBool(object raw, out bool value)
        {
            value = false;
            if (raw == null)
            {
                return false;
            }
            if (raw is bool boolValue)
            {
                value = boolValue;
                return true;
            }
            var text = raw.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            if (bool.TryParse(text, out var parsed))
            {
                value = parsed;
                return true;
            }
            if (text == "1")
            {
                value = true;
                return true;
            }
            if (text == "0")
            {
                value = false;
                return true;
            }
            return false;
        }
    }
}
