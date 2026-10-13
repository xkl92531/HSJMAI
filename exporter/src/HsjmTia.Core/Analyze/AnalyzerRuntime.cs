using System.IO;
using Newtonsoft.Json.Linq;

namespace HsjmTia.Core.Analyze
{
    /// <summary>
    /// 定位本地解析所需的 Python 与 engine.py。
    /// 优先用随工具分发的内嵌运行时（现场机器无需装 Python）；
    /// 没有内嵌时回退系统 PATH 上的 python，便于开发机调试。
    /// </summary>
    public class AnalyzerRuntime
    {
        /// <summary>相对于 appDirectory 的版本文件路径（runtime/analyzer.version.json）。</summary>
        public const string VersionFilePath = "runtime/analyzer.version.json";

        public string PythonExe { get; private set; }
        public string EngineScript { get; private set; }
        public string AnalyzerCommit { get; private set; }
        public bool IsEmbedded { get; private set; }

        public static AnalyzerRuntime Resolve(string appDirectory)
        {
            var runtimeDir = Path.Combine(appDirectory, "runtime");
            var embeddedPython = Path.Combine(runtimeDir, "python", "python.exe");
            var embeddedEngine = Path.Combine(runtimeDir, "plc-analyzer", "scripts", "engine.py");

            var isEmbedded = File.Exists(embeddedPython) && File.Exists(embeddedEngine);

            return new AnalyzerRuntime
            {
                IsEmbedded = isEmbedded,
                PythonExe = isEmbedded ? embeddedPython : "python",
                EngineScript = isEmbedded ? embeddedEngine : null,
                AnalyzerCommit = ReadCommit(Path.Combine(runtimeDir, "analyzer.version.json"))
            };
        }

        private static string ReadCommit(string versionFile)
        {
            if (!File.Exists(versionFile))
            {
                return null;
            }

            try
            {
                return (string)JObject.Parse(File.ReadAllText(versionFile))["analyzerCommit"];
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // 版本文件损坏不该让导出失败，降级为"未知版本"
                return null;
            }
        }
    }
}
