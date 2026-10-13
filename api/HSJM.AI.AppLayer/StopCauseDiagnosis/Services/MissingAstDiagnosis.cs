using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 2026-09-22：工位状态停在「无AST」时，说清楚是哪个条件没满足。
    ///
    /// 背景：<c>DiagnoseStationForLineAsync</c> 的 OverallStatus 初始值就是 "NoAst"，Phase 2 的
    /// <c>projectid + stationcode + compilestatus='Compiled'</c> 查不到行就直接 return，
    /// 页面上只剩一个灰方块，既不说是"没跑过锚点发现"还是"编译失败"还是"工位号对不上"。
    /// 现场连不上库时，这行原因是唯一能拿到的线索，所以同时写进日志和工位卡片的 errorMessage。
    ///
    /// 纯函数，不碰数据库——调用方负责把本项目的 (工位号, 编译状态) 清单查出来传进来。
    /// </summary>
    public static class MissingAstDiagnosis
    {
        /// <summary>日志/卡片里最多列几个已登记工位，避免大项目糊出几百个工位号。</summary>
        private const int MaxListedStations = 8;

        private const string Compiled = "Compiled";

        /// <param name="stationCode">本次诊断的工位号。</param>
        /// <param name="projectAstRows">本项目未删除的全部 AST 行，只需要 (StationCode, CompileStatus) 两列。</param>
        /// <returns>人话原因；若确实存在该工位的 Compiled 行（不该走到这里）返回空串。</returns>
        public static string Describe(
            string stationCode,
            IReadOnlyList<(string StationCode, string CompileStatus)> projectAstRows)
        {
            var station = (stationCode ?? string.Empty).Trim();

            if (projectAstRows == null || projectAstRows.Count == 0)
            {
                return "本项目没有任何 AST 记录 —— 从未对它跑过锚点发现（上传解析不会自动生成 AST）。";
            }

            var ofStation = projectAstRows
                .Where(row => Same(row.StationCode, station))
                .ToList();

            if (ofStation.Count > 0)
            {
                if (ofStation.Any(row => Same(row.CompileStatus, Compiled))) return string.Empty;

                var statuses = ofStation
                    .GroupBy(row => string.IsNullOrWhiteSpace(row.CompileStatus) ? "(空)" : row.CompileStatus.Trim(),
                        StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key}={g.Count()}");

                return $"工位「{station}」有 {ofStation.Count} 行 AST，但没有一行编译成功（{string.Join("、", statuses)}）"
                     + "；只有 Compiled 才会被诊断采用，需要重跑锚点发现 / 重新编译。";
            }

            var blank = projectAstRows.Count(row => string.IsNullOrWhiteSpace(row.StationCode));
            var known = projectAstRows
                .Select(row => (row.StationCode ?? string.Empty).Trim())
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var listed = known.Count > MaxListedStations
                ? string.Join("、", known.Take(MaxListedStations)) + $"… (共 {known.Count} 个)"
                : string.Join("、", known);

            var head = $"本项目有 {projectAstRows.Count} 行 AST，但没有一行挂在工位「{station}」上";
            var tail = known.Count > 0 ? $"；已登记的工位：{listed}（工位号对不上？）" : "。";

            return blank > 0
                ? head + $"；其中 {blank} 行的工位为空（锚点发现时没带工位）" + (known.Count > 0 ? tail : "。")
                : head + tail;
        }

        private static bool Same(string a, string b) =>
            string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
