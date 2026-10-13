#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>库里一个逻辑单元的位置（工位归属用）：Id、所在块、块名、networkid。</summary>
    public sealed record LogicUnitLocation(long LogicUnitId, long BlockId, string BlockName, int? NetworkId);

    /// <summary>
    /// 报警清单行（plc_independent.alarm_catalog）→ 一键排查用的报警定义。
    /// 种类直接用清单的 Timeout / PositionConflict，不从任何名字推断方向（Direction 一律空串）。
    /// </summary>
    public static class AlarmCatalogMapping
    {
        /// <summary>
        /// unitsById：清单行 LogicUnitId 指向的、当前仍存在的库逻辑单元。
        /// 定位只认清单行发布时回查到的 LogicUnitId（且块名一致）；不再按「块名 + 网络号」现查——
        /// 旧清单行的网络号是归档根元素序号，在含空网络的块里与库 networkid 错位，按它定位会把报警挂到相邻程序段的工位上。
        /// 定位不到（旧行、发布时回查不唯一、逻辑单元已被重新上传替换）→ 不定位（LogicUnitId=0、BlockId 与 NetworkId 为空），
        /// 工位归属只剩按块名的块绑定；只会漏，不会挂错。
        /// </summary>
        public static List<ValveAlarmDefinition> ToDefinitions(
            long projectId,
            IReadOnlyList<AlarmCatalogRow> rows,
            IReadOnlyDictionary<string, PLC_IOTagEntity> ioTagsByName,
            IReadOnlyDictionary<long, LogicUnitLocation> unitsById)
        {
            var result = new List<ValveAlarmDefinition>(rows.Count);
            foreach (var row in rows)
            {
                var blockName = row.SourceBlock?.Trim() ?? string.Empty;
                LogicUnitLocation? location = null;
                if (row.LogicUnitId is long id && unitsById.TryGetValue(id, out var found) && found != null
                    && string.Equals(found.BlockName?.Trim(), blockName, StringComparison.OrdinalIgnoreCase))
                    location = found;

                var feedbacks = new List<ValveAlarmSignalRef>(row.FeedbackSignals.Count);
                foreach (var name in row.FeedbackSignals)
                {
                    var signal = ToSignal(name, ioTagsByName);
                    if (signal != null) feedbacks.Add(signal);
                }

                // 到位组按与 Feedbacks 相同的名字（点表里的点取点表写法）引用，值在排查时从 Feedbacks 里取
                var groups = row.FeedbackGroups
                    .Select(g => (IReadOnlyList<string>)g.Select(n => ToSignal(n, ioTagsByName)?.Name ?? n?.Trim() ?? string.Empty).ToArray())
                    .ToArray();

                result.Add(new ValveAlarmDefinition(
                    projectId,
                    row.AlarmSymbol,
                    row.Kind,
                    string.Empty,
                    row.SourceInstance,
                    blockName,
                    location?.LogicUnitId ?? 0,
                    location?.BlockId,
                    location?.NetworkId,
                    ToSignal(row.OutputSignal, ioTagsByName),
                    feedbacks)
                { FeedbackGroups = groups, Text = string.IsNullOrWhiteSpace(row.FallbackText) ? null : row.FallbackText.Trim() });
            }
            return result;
        }

        /// <summary>点表里的点带变量表与地址（按 IO 点读）；其余保留原名（DB 路径仍可读）；空名不产引用。</summary>
        private static ValveAlarmSignalRef? ToSignal(string? name, IReadOnlyDictionary<string, PLC_IOTagEntity> ioTagsByName)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();
            return ioTagsByName.TryGetValue(trimmed, out var tag) && tag != null
                ? new ValveAlarmSignalRef(tag.TagName, tag.TableName, tag.Address)
                : new ValveAlarmSignalRef(trimmed, null, null);
        }

        /// <summary>
        /// 新旧并行期（spec §9）：清单定义 + 旧提取里报警位不在清单中的定义。
        /// 按报警位（去首尾空格、忽略大小写）去重，清单优先；旧提取内部已按报警位去重。
        /// </summary>
        public static List<ValveAlarmDefinition> MergeWithLegacy(
            IReadOnlyList<ValveAlarmDefinition> fromCatalog, IReadOnlyList<ValveAlarmDefinition> fromLegacy)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<ValveAlarmDefinition>(fromCatalog.Count + fromLegacy.Count);
            foreach (var d in fromCatalog.Concat(fromLegacy))
            {
                if (seen.Add(d.AlarmVariable?.Trim() ?? string.Empty)) merged.Add(d);
            }
            return merged;
        }
    }
}
