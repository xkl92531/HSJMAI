#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 跨 PLC 信号（2026-10-14）：PLC1 的程序会引用 PLC2 工程里的通信块（如 DB182_PLC1&lt;-&gt;PLC2），
    /// 这类块不在 PLC1 工程里——按 PLC1 读不到值，在 PLC1 也找不到写入源，以前一键排查会把它当成
    /// PLC1 工位自己的「根因终点」。这里决定：该到哪个工程去读、写入它的是哪个工位。
    /// </summary>
    internal static class CrossPlcRules
    {
        /// <summary>符号的块名（第一段，支持 "块名".成员 写法）；没有块段（如 IO 短名）返回 null。</summary>
        public static string? BlockNameOf(string? symbol)
        {
            var text = symbol?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;

            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                var close = text.IndexOf('"', 1);
                return close > 1 && close + 1 < text.Length && text[close + 1] == '.'
                    ? text.Substring(1, close - 1)
                    : null;
            }

            var dot = text.IndexOf('.');
            return dot > 0 && dot < text.Length - 1 ? text.Substring(0, dot) : null;
        }

        /// <summary>块名后面的成员路径；没有块段时返回 null。</summary>
        public static string? MemberPathOf(string? symbol)
        {
            var text = symbol?.Trim();
            var blockName = BlockNameOf(text);
            if (blockName == null || text == null)
                return null;
            var prefixLength = text.StartsWith("\"", StringComparison.Ordinal) ? blockName.Length + 3 : blockName.Length + 1;
            return prefixLength < text.Length ? text.Substring(prefixLength) : null;
        }

        /// <summary>
        /// 符号所在块属于哪个别的工程：块在本工程里有就不改道；只在另外唯一一个工程里有才返回那个工程。
        /// </summary>
        public static long? ResolveOwnerProject(
            string? symbol,
            long currentProjectId,
            IReadOnlyDictionary<string, IReadOnlyCollection<long>> projectsByBlockName)
        {
            var blockName = BlockNameOf(symbol);
            if (blockName == null || projectsByBlockName == null
                || !projectsByBlockName.TryGetValue(blockName, out var projects) || projects == null)
                return null;

            var distinct = projects.Distinct().ToList();
            if (distinct.Contains(currentProjectId))
                return null;
            return distinct.Count == 1 ? distinct[0] : null;
        }

        /// <summary>本工程读不到的符号里，块属于别的工程的，按所属工程分组（保持原顺序）。</summary>
        public static Dictionary<long, List<string>> PlanReroute(
            IEnumerable<string> symbols,
            long currentProjectId,
            IReadOnlyDictionary<string, IReadOnlyCollection<long>> projectsByBlockName)
        {
            var plan = new Dictionary<long, List<string>>();
            foreach (var symbol in symbols ?? Enumerable.Empty<string>())
            {
                var owner = ResolveOwnerProject(symbol, currentProjectId, projectsByBlockName);
                if (owner == null)
                    continue;
                if (!plan.TryGetValue(owner.Value, out var list))
                    plan[owner.Value] = list = new List<string>();
                if (!list.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                    list.Add(symbol);
            }
            return plan;
        }

        /// <summary>终端信号的归属：跨 PLC 且找到了对端写入工位 → 在等那个工位；否则仍按外部输入。</summary>
        public static string TerminalOwnership(bool isForeign, string? writerStation) =>
            isForeign && !string.IsNullOrWhiteSpace(writerStation)
                ? $"WaitingFor:{writerStation.Trim()}"
                : "ExternalInput";

        /// <summary>对端写入源所属工位：出现最多且唯一的那个；并列或没有返回 null。</summary>
        public static string? PickWriterStation(IEnumerable<string?> stations)
        {
            var ranked = (stations ?? Enumerable.Empty<string?>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Station: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .ToList();
            if (ranked.Count == 0)
                return null;
            if (ranked.Count > 1 && ranked[1].Count == ranked[0].Count)
                return null;
            return ranked[0].Station;
        }
    }
}
