using System;
using System.Collections.Generic;
using System.Linq;

namespace HsjmTia.Core.Packaging
{
    /// <summary>
    /// 打包前的结构自检。三条规则直接对应后端的真实行为：
    /// - 多个 tag CSV：后端遍历包内所有 .csv 全部 AddRange，会重复入库
    /// - 多个 PLC 混在一个包：LoadBlockPathMappings 只按块名去重，同名块会张冠李戴，
    ///   所以任何条目的第一层路径段必须是已知根之一，否则判定为嵌套的第二个 PLC 层级
    /// - 已知根之外的顶层目录/文件一律拒绝（含上面两条覆盖不到的其它嵌套/杂项条目）
    /// </summary>
    public static class PackageValidator
    {
        // 与 HsjmTia.Core.Layout.ExportLayout 保持一致：blocks/plcTags/udt 是当前阶段产出，
        // alarms/hardware/tech_objects 是后续阶段产出，均为合法根，不能拒绝。
        private static readonly string[] KnownRootDirectories =
        {
            "blocks",
            "plcTags",
            "udt",
            "alarms",
            "hardware",
            "tech_objects",
        };

        public static IReadOnlyList<string> Validate(IEnumerable<string> entryPaths)
        {
            var problems = new List<string>();
            var entries = (entryPaths ?? Enumerable.Empty<string>())
                .Select(e => (e ?? string.Empty).Replace('\\', '/'))
                .ToList();

            if (!entries.Any(e => string.Equals(e, "manifest.json", StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add("缺少 manifest.json");
            }

            if (!entries.Any(e => e.StartsWith("blocks/", StringComparison.OrdinalIgnoreCase)
                                  && e.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add("blocks/ 下没有任何块 XML");
            }

            var csvCount = entries.Count(e => e.StartsWith("plcTags/", StringComparison.OrdinalIgnoreCase)
                                              && e.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            if (csvCount > 1)
            {
                problems.Add($"plcTags/ 下有 {csvCount} 个 CSV，后端会全部合并导致标签重复入库");
            }

            foreach (var entry in entries)
            {
                if (string.Equals(entry, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var firstSegment = entry.Split('/')[0];
                var isKnownRoot = KnownRootDirectories.Any(root =>
                    string.Equals(root, firstSegment, StringComparison.OrdinalIgnoreCase));
                if (!isKnownRoot)
                {
                    problems.Add($"意外的顶层条目 \"{entry}\"：可能是混入了另一个 PLC 的嵌套层级，一个包只能包含一个 PLC");
                }
            }

            return problems;
        }
    }
}
