using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HsjmTia.Core.Layout;
using HsjmTia.Core.Manifest;
using HsjmTia.Core.Naming;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Types;

namespace HsjmTia.Openness.Exporters
{
    public static class UdtExporter
    {
        /// <summary>
        /// 返回 类型名 → 相对 udt/ 的路径，供 UdtIndexWriter 写索引。
        /// 分组层级予以保留：扁平导出会让不同分组的同名 UDT 互相覆盖。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IReadOnlyList<KeyValuePair<string, string>> Export(
            PlcSoftware software, ExportLayout layout, IList<ExportGap> gaps)
        {
            var index = new List<KeyValuePair<string, string>>();
            var seen = new Dictionary<string, string>();
            ExportGroup(software.TypeGroup, string.Empty, layout, gaps, index, seen);
            return index;
        }

        private static void ExportGroup(
            PlcTypeGroup group, string relativeDir, ExportLayout layout,
            IList<ExportGap> gaps, List<KeyValuePair<string, string>> index,
            Dictionary<string, string> seen)
        {
            var targetDir = string.IsNullOrEmpty(relativeDir)
                ? layout.UdtDir
                : Path.Combine(layout.UdtDir, relativeDir);
            Directory.CreateDirectory(targetDir);

            foreach (var type in group.Types)
            {
                var safeName = FileNameSanitizer.Sanitize(type.Name);
                var fullPath = Path.Combine(targetDir, safeName + ".xml");
                try
                {
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    type.Export(new FileInfo(fullPath), ExportOptions.WithDefaults);

                    var relative = string.IsNullOrEmpty(relativeDir)
                        ? safeName + ".xml"
                        : relativeDir.Replace('\\', '/') + "/" + safeName + ".xml";

                    // UdtIndexWriter 把这份列表建成 Dictionary，重复键按后写覆盖。
                    // 不同分组下允许同名 UDT（正是这些子目录存在的原因），但索引只能记一个 ——
                    // 悄悄覆盖会让先注册的那个 UDT 在下游重新变得不可解析（回到本索引要修的那个 bug）。
                    // 因此这里显式检测重名：只保留第一次出现的映射，重复的记成缺口，不覆盖也不丢弃已导出的文件。
                    if (seen.TryGetValue(type.Name, out var existingPath))
                    {
                        gaps.Add(ExportGap.Create(GapKinds.ExportFailed, type.Name,
                            "UDT 类型名在多个分组下重复，索引仅保留 " + existingPath +
                            "，" + relative + " 已导出但未登记到索引，无法被下游按类型名解析"));
                    }
                    else
                    {
                        seen[type.Name] = relative;
                        index.Add(new KeyValuePair<string, string>(type.Name, relative));
                    }
                }
                catch (EngineeringException ex)
                {
                    gaps.Add(ExportGap.Create(GapKinds.ExportFailed, type.Name, "UDT 导出失败：" + ex.Message));
                }
            }

            foreach (var sub in group.Groups)
            {
                var childDir = string.IsNullOrEmpty(relativeDir)
                    ? FileNameSanitizer.Sanitize(sub.Name)
                    : Path.Combine(relativeDir, FileNameSanitizer.Sanitize(sub.Name));
                ExportGroup(sub, childDir, layout, gaps, index, seen);
            }
        }
    }
}
