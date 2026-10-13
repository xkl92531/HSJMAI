using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HsjmTia.Core.Export;
using HsjmTia.Core.Layout;
using HsjmTia.Core.Manifest;
using HsjmTia.Core.Naming;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;

namespace HsjmTia.Openness.Exporters
{
    public static class BlockExporter
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IReadOnlyList<BlockPathEntry> Export(
            PlcSoftware software, ExportLayout layout, IList<ExportGap> gaps)
        {
            var entries = new List<BlockPathEntry>();
            ExportGroup(software.BlockGroup, string.Empty, software, layout, gaps, entries);
            return entries;
        }

        private static void ExportGroup(
            PlcBlockGroup group, string folderPath, PlcSoftware software,
            ExportLayout layout, IList<ExportGap> gaps, List<BlockPathEntry> entries)
        {
            foreach (var block in group.Blocks)
            {
                if (block.IsKnowHowProtected)
                {
                    // BlockMissing：这个块这次没有进 zip，FullUpdateSafety 靠这个 Kind 拦截 --full。
                    gaps.Add(ExportGap.Create(GapKinds.BlockMissing, block.Name,
                        "块受知识产权保护，无法导出"));
                    continue;
                }

                var language = block.ProgrammingLanguage.ToString();
                var safeName = FileNameSanitizer.Sanitize(block.Name);
                if (!string.Equals(safeName, block.Name, StringComparison.Ordinal))
                {
                    gaps.Add(ExportGap.Create(GapKinds.NameSanitized, block.Name,
                        "块名含非法字符，文件名已清洗为 " + safeName));
                }

                var languageDir = Path.Combine(layout.BlocksDir, FileNameSanitizer.Sanitize(language));
                Directory.CreateDirectory(languageDir);

                var xmlPath = Path.Combine(languageDir, safeName + ".xml");
                try
                {
                    if (File.Exists(xmlPath)) File.Delete(xmlPath);
                    block.Export(new FileInfo(xmlPath), ExportOptions.WithDefaults);
                    entries.Add(new BlockPathEntry { FolderPath = folderPath, BlockName = block.Name });
                }
                catch (EngineeringException ex)
                {
                    // 块 XML 导出本身抛异常：entries.Add 还没跑到，这个块真的没有进 zip——
                    // 同上一处一样记 BlockMissing，不能用 ExportFailed（那个 Kind 被下面
                    // "XML 已经导出成功、只是源码导出失败"的场景复用，语义完全不同）。
                    gaps.Add(ExportGap.Create(GapKinds.BlockMissing, block.Name, ex.Message));
                    continue;
                }

                ExportSource(block, software, languageDir, safeName, gaps);
            }

            foreach (var subGroup in group.Groups)
            {
                var childPath = string.IsNullOrEmpty(folderPath)
                    ? subGroup.Name
                    : folderPath + "/" + subGroup.Name;
                ExportGroup(subGroup, childPath, software, layout, gaps, entries);
            }
        }

        /// <summary>SCL/STL 块额外导出源码：block.Export 的 XML 里 SCL 代码体可能缺失。</summary>
        private static void ExportSource(
            PlcBlock block, PlcSoftware software, string languageDir, string safeName, IList<ExportGap> gaps)
        {
            var isScl = block.ProgrammingLanguage == ProgrammingLanguage.SCL;
            var isStl = block.ProgrammingLanguage == ProgrammingLanguage.STL;
            if (!isScl && !isStl)
            {
                return;
            }

            var sourcePath = Path.Combine(languageDir, safeName + (isStl ? ".awl" : ".scl"));
            try
            {
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
                software.ExternalSourceGroup.GenerateSource(
                    new PlcBlock[] { block }, new FileInfo(sourcePath));
            }
            catch (EngineeringException ex)
            {
                // 仍然是 ExportFailed，不是 BlockMissing：块 XML 已经导出成功、entries.Add
                // 在上面已经跑过了，这个块本身在 zip 里，只是缺了额外的源码文件。
                gaps.Add(ExportGap.Create(GapKinds.ExportFailed, block.Name, "源码导出失败：" + ex.Message));
            }
        }
    }
}
