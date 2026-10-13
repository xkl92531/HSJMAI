using System;
using System.Runtime.CompilerServices;
using HsjmTia.Core.Analyze;
using HsjmTia.Core.Discovery;
using HsjmTia.Core.Manifest;

namespace HsjmTia.App
{
    /// <summary>
    /// 界面到共享编排层（HsjmTia.Openness.ExportOrchestrator）的唯一桥接点。
    ///
    /// <see cref="Run"/> 标 NoInlining 且只能在 <c>OpennessAssemblyResolver.Install</c> 已经执行之后
    /// 才会被调用（MainForm 只在成功选定工程、探测出匹配版本后才启用"导出"按钮）——
    /// 与 CLI 侧 Program.cs 顶部的隔离方式一致：任何触碰 SDK 的方法都必须禁止内联、
    /// 且在 Install 之前不可达，避免 JIT 在挂钩生效前就尝试解析 Siemens.Engineering。
    /// </summary>
    internal static class AppExportRunner
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ExportRunResult Run(string projectPath, OpennessInstallation installation, string outputRoot)
        {
            var runtime = AnalyzerRuntime.Resolve(AppDomain.CurrentDomain.BaseDirectory);
            return HsjmTia.Openness.ExportOrchestrator.Run(projectPath, installation, outputRoot, runtime);
        }
    }
}
