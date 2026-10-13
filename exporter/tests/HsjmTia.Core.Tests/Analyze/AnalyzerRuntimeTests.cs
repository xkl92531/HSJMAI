using System.IO;
using HsjmTia.Core.Analyze;
using Xunit;

namespace HsjmTia.Core.Tests.Analyze
{
    public class AnalyzerRuntimeTests
    {
        private static string NewDir()
        {
            var d = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(d);
            return d;
        }

        [Fact]
        public void Resolve_PrefersTheEmbeddedRuntime_SoFieldMachinesNeedNoPythonInstall()
        {
            var app = NewDir();
            Directory.CreateDirectory(Path.Combine(app, "runtime", "python"));
            File.WriteAllText(Path.Combine(app, "runtime", "python", "python.exe"), "");
            Directory.CreateDirectory(Path.Combine(app, "runtime", "plc-analyzer", "scripts"));
            File.WriteAllText(Path.Combine(app, "runtime", "plc-analyzer", "scripts", "engine.py"), "");

            var rt = AnalyzerRuntime.Resolve(app);

            Assert.True(rt.IsEmbedded);
            Assert.EndsWith("python.exe", rt.PythonExe);
            Assert.EndsWith("engine.py", rt.EngineScript);
            Directory.Delete(app, true);
        }

        [Fact]
        public void Resolve_FallsBackToSystemPython_WhenNothingIsEmbedded()
        {
            var app = NewDir();
            var rt = AnalyzerRuntime.Resolve(app);
            Assert.False(rt.IsEmbedded);
            Assert.Equal("python", rt.PythonExe);
            // 下游编排靠 EngineScript 为 null 来记 CapabilityMissing 缺口，不能有回退路径
            Assert.Null(rt.EngineScript);
            Directory.Delete(app, true);
        }

        [Fact]
        public void Resolve_ReadsTheAnalyzerCommitFromTheVersionFile()
        {
            var app = NewDir();
            Directory.CreateDirectory(Path.Combine(app, "runtime"));
            File.WriteAllText(Path.Combine(app, "runtime", "analyzer.version.json"),
                "{\"analyzerCommit\":\"7cec19d\"}");

            Assert.Equal("7cec19d", AnalyzerRuntime.Resolve(app).AnalyzerCommit);
            Directory.Delete(app, true);
        }

        [Fact]
        public void Resolve_LeavesCommitNull_WhenTheVersionFileIsMissing()
        {
            var app = NewDir();
            Assert.Null(AnalyzerRuntime.Resolve(app).AnalyzerCommit);
            Directory.Delete(app, true);
        }

        [Fact]
        public void Resolve_IgnoresABrokenVersionFile()
        {
            var app = NewDir();
            Directory.CreateDirectory(Path.Combine(app, "runtime"));
            File.WriteAllText(Path.Combine(app, "runtime", "analyzer.version.json"), "{not json");
            Assert.Null(AnalyzerRuntime.Resolve(app).AnalyzerCommit);
            Directory.Delete(app, true);
        }
    }
}
