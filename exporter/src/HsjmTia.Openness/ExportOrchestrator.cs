using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HsjmTia.Core.Analyze;
using HsjmTia.Core.Discovery;
using HsjmTia.Core.Export;
using HsjmTia.Core.Layout;
using HsjmTia.Core.Manifest;
using HsjmTia.Core.Naming;
using HsjmTia.Core.Packaging;
using HsjmTia.Openness.Exporters;

namespace HsjmTia.Openness
{
    /// <summary>
    /// 工程里"没有找到任何 CPU"是唯一一种在连接/枚举阶段就该优雅终止（而非直接崩溃）的情况——
    /// 一个独立的异常类型，让 CLI 能精确捕获它并转成退出码 4，而 Connect/OpenProject/EnumerateCpus
    /// 抛出的其它异常继续原样冒泡（与改造前行为一致，不在这里额外兜底）。
    /// </summary>
    public class NoCpusFoundException : Exception
    {
        public NoCpusFoundException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// 连接 → 枚举 CPU → 逐 PLC 清目录/导出/写 manifest/打包 的共享编排。
    /// 从 HsjmTia.Cli/ExportPipeline.Run 原样搬移而来，CLI 与 WinForms 界面共用同一份逻辑，
    /// 避免两端各写一份导致行为漂移。本类型不做任何 Console/UI 相关的展示，
    /// 只负责跑完整个导出并把结果交给调用方去呈现。
    /// </summary>
    public static class ExportOrchestrator
    {
        private static readonly Encoding Utf8Bom = new UTF8Encoding(true);

        public static ExportRunResult Run(
            string projectPath, OpennessInstallation installation, string outputRoot, AnalyzerRuntime runtime)
        {
            var stamp = DateTime.Now;

            using (var session = TiaSession.Connect())
            {
                session.OpenProject(projectPath);

                var discoveryGaps = new List<ExportGap>();
                var cpus = session.EnumerateCpus(discoveryGaps);
                if (cpus.Count == 0)
                {
                    throw new NoCpusFoundException("工程里没有找到任何 CPU。");
                }

                // 本方法一律导出工程里的全部 CPU，因此可以清理孤儿目录
                CleanOrphans(outputRoot, session.ProjectName, cpus);

                var allGaps = new List<ExportGap>();
                var packages = new List<ExportPackage>();
                var analyzeTotal = 0;
                var analyzeSucceeded = 0;

                foreach (var cpu in cpus)
                {
                    var layout = ExportLayout.For(outputRoot, session.ProjectName, cpu.Identity);
                    var gaps = new List<ExportGap>(
                        discoveryGaps.Where(g => g.Target == cpu.Identity.Name));

                    try
                    {
                        ResetDirectory(layout.PlcDir);
                        Directory.CreateDirectory(layout.BlocksDir);
                        Directory.CreateDirectory(layout.TagsDir);
                        Directory.CreateDirectory(layout.UdtDir);

                        var blockEntries = BlockExporter.Export(cpu.Software, layout, gaps);
                        using (var writer = new StreamWriter(layout.BlockPathTxtPath, false, Utf8Bom))
                        {
                            BlockPathWriter.Write(blockEntries, writer);
                        }

                        var tags = TagExporter.Collect(cpu.Software, gaps);
                        using (var writer = new StreamWriter(layout.TagsCsvPath, false, Utf8Bom))
                        {
                            TagCsvWriter.Write(tags, writer);
                        }

                        var udtIndex = UdtExporter.Export(cpu.Software, layout, gaps);
                        // udt_index.json 必须不带 BOM：parser.py 用 Python 'utf-8' 编解码器读它，
                        // 该编解码器不剥离 BOM，带 BOM 会让 json.load 抛异常，被静默吞掉后整个索引
                        // 失效（见 §六 C1）。tags.csv / block_path.txt 的 BOM 是后端解析所需，不能类比。
                        File.WriteAllBytes(layout.UdtIndexPath, UdtIndexWriter.SerializeToBytes(udtIndex));

                        var analyze = RunLocalAnalysis(runtime, layout, gaps);
                        analyzeTotal += analyze.Total;
                        analyzeSucceeded += analyze.Succeeded;

                        var manifest = new ExportManifest
                        {
                            ExportedAt = new DateTimeOffset(stamp),
                            ToolVersion = typeof(ExportOrchestrator).Assembly.GetName().Version.ToString(),
                            AnalyzerCommit = runtime.AnalyzerCommit,
                            TiaProjectName = session.ProjectName,
                            TiaProjectPath = projectPath,
                            TiaVersion = installation.ApiVersion,
                            Machine = Environment.MachineName,
                            Plc = cpu.Identity,
                            Gaps = gaps
                        };
                        manifest.Counts.Blocks = blockEntries.Count;
                        manifest.Counts.Tags = tags.Count;
                        manifest.Counts.Udts = udtIndex.Count;

                        // 先落盘一版 manifest：PackageValidator 要求 manifest.json 必须存在才能校验，
                        // 但此时 manifest 里还不包含校验器自己发现的问题（这份文件随后会被覆盖）。
                        // 不带 BOM：manifest.json 与 udt_index.json 同类——JSON、parser.py 用
                        // 'utf-8' 编解码器打开，带 BOM 会让 json.load 悄悄失败（见 §六 C1）。
                        File.WriteAllBytes(layout.ManifestPath, ManifestSerializer.SerializeToBytes(manifest));

                        var problems = PackageValidator.Validate(EnumerateRelative(layout.PlcDir));
                        foreach (var problem in problems)
                        {
                            gaps.Add(ExportGap.Create(GapKinds.ExportFailed, "package", problem));
                        }

                        // gaps 与 manifest.Gaps 是同一个 List 引用，上面的 Add 已经反映在 manifest 里；
                        // 重新序列化写盘，确保磁盘上的 manifest.json、以及马上要打进 zip 的那份都带上校验结果。
                        File.WriteAllBytes(layout.ManifestPath, ManifestSerializer.SerializeToBytes(manifest));

                        var zipPath = Path.Combine(
                            layout.TiaProjectDir,
                            PlcNamer.BuildPackageFileName(session.ProjectName, cpu.Identity, stamp));
                        ZipPackager.Create(layout.PlcDir, zipPath);

                        if (problems.Count == 0)
                        {
                            // 校验器判定这个包有结构问题时（如混入了另一个 PLC 的层级、CSV 会被后端
                            // 重复合并等）——文件仍然落盘方便工程师排查，但绝不能把它送去上传，
                            // 因此不加入 packages。
                            // 快照当前这份 gaps：调用方（--full / 整工程覆盖更新的安全检查）要按
                            // 单个 PLC 判断是否有块被知产保护/导出失败而缺席这次导出，不能被后面
                            // allGaps.AddRange 之后跨全部 PLC 拍平的那份列表污染。
                            packages.Add(new ExportPackage
                            {
                                Plc = cpu.Identity,
                                ZipPath = zipPath,
                                Gaps = new List<ExportGap>(gaps)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        // 任何未预期异常（COM 抖动、异常工程结构导致的 NullReferenceException 等）都不能
                        // 打断整个循环：已经导出成功的其它 PLC 不该因为这一个 PLC 出错而全部作废。
                        gaps.Add(ExportGap.Create(
                            GapKinds.ExportFailed, cpu.Identity.Name, $"{ex.GetType().Name}: {ex.Message}"));
                    }

                    allGaps.AddRange(gaps);
                }

                return new ExportRunResult
                {
                    ProjectName = session.ProjectName,
                    Plcs = cpus.Select(c => c.Identity).ToList(),
                    Gaps = allGaps,
                    AnalyzeTotal = analyzeTotal,
                    AnalyzeSucceeded = analyzeSucceeded,
                    Packages = packages
                };
            }
        }

        /// <summary>目录整体重建：博图里删掉/改名的块，旧 XML 必须消失，否则会当成幽灵块上传。</summary>
        private static void ResetDirectory(string dir)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
            Directory.CreateDirectory(dir);
        }

        private static void CleanOrphans(string outputRoot, string projectName, IReadOnlyList<CpuHandle> cpus)
        {
            var projectDir = Path.Combine(outputRoot, FileNameSanitizer.Sanitize(projectName));
            if (!Directory.Exists(projectDir))
            {
                return;
            }

            // OrphanCleanupPlanner.Plan 的空值处理不对称：existingPlcDirNames==null 会返回空计划，
            // 但 currentPlcDirNames==null 在 isFullExport=true 时会把"全部目录"都判定为待删除孤儿。
            // 这里两个序列都来自实际枚举/映射，任何情况下都不会是 null（cpus 为空时上层已提前抛出）。
            var existing = Directory.GetDirectories(projectDir).Select(Path.GetFileName).ToList();
            var current = cpus.Select(c => PlcNamer.BuildFolderName(c.Identity)).ToList();

            foreach (var orphan in OrphanCleanupPlanner.Plan(existing, current, isFullExport: true))
            {
                Directory.Delete(Path.Combine(projectDir, orphan), true);
            }
        }

        private static AnalyzeReport RunLocalAnalysis(
            AnalyzerRuntime runtime, ExportLayout layout, IList<ExportGap> gaps)
        {
            if (runtime.EngineScript == null)
            {
                gaps.Add(ExportGap.Create(GapKinds.CapabilityMissing, "LocalAnalyze",
                    "未找到内嵌解析器，跳过本地解析校验"));
                return new AnalyzeReport();
            }

            var xmlFiles = Directory.GetFiles(layout.BlocksDir, "*.xml", SearchOption.AllDirectories);
            var report = AnalyzerRunner
                .RunAsync(runtime.PythonExe, runtime.EngineScript, xmlFiles, 4, CancellationToken.None)
                .GetAwaiter().GetResult();

            foreach (var failure in report.Failures)
            {
                gaps.Add(ExportGap.Create(
                    GapKinds.AnalyzeFailed, Path.GetFileName(failure.File), failure.Error));
            }
            return report;
        }

        private static IEnumerable<string> EnumerateRelative(string root)
            => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/'));
    }
}
