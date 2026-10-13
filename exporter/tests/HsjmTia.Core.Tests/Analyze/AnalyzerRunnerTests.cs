using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HsjmTia.Core.Analyze;
using Xunit;

namespace HsjmTia.Core.Tests.Analyze
{
    public class AnalyzerRunnerTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "api")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName;
        }

        private static string Engine() => Path.Combine(RepoRoot(),
            "api", "HSJM.AI", "wwwroot", "skills", "plc-analyzer", "scripts", "engine.py");

        private static string Sample(string name) => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Fixtures", name);

        [Fact]
        public async Task RunAsync_ParsesRealTiaExports()
        {
            var report = await AnalyzerRunner.RunAsync(
                "python", Engine(), new[] { Sample("LAD.xml"), Sample("SCL.xml") }, 2, CancellationToken.None);

            Assert.Equal(2, report.Total);
            Assert.Equal(2, report.Succeeded);
            Assert.Empty(report.Failures);
        }

        [Fact]
        public async Task RunAsync_ReportsTheFileNameAndStderrForABadFile()
        {
            var bad = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
            File.WriteAllText(bad, "<not-closed");
            try
            {
                var report = await AnalyzerRunner.RunAsync(
                    "python", Engine(), new[] { bad }, 1, CancellationToken.None);

                Assert.Equal(1, report.Total);
                Assert.Equal(0, report.Succeeded);
                Assert.Single(report.Failures);
                Assert.Equal(bad, report.Failures[0].File);
                Assert.False(string.IsNullOrWhiteSpace(report.Failures[0].Error));
            }
            finally
            {
                File.Delete(bad);
            }
        }

        [Fact]
        public async Task RunAsync_ReturnsAnEmptyReportForNoFiles()
        {
            var report = await AnalyzerRunner.RunAsync(
                "python", Engine(), Enumerable.Empty<string>(), 4, CancellationToken.None);
            Assert.Equal(0, report.Total);
            Assert.Empty(report.Failures);
        }

        [Fact]
        public async Task RunAsync_CleansUpTheIntermediateParsedJson()
        {
            var expected = Path.ChangeExtension(Sample("STL.xml"), ".parsed.json");
            await AnalyzerRunner.RunAsync("python", Engine(), new[] { Sample("STL.xml") }, 1, CancellationToken.None);
            Assert.False(File.Exists(expected), "解析产生的中间 JSON 不应留在源目录里");
        }

        // engine.py 的 parser.py 会先把 blocks 填好，随后调用关系/GRAPH/逻辑单元/交叉引用/语义分析
        // 等后续阶段的任何异常都被外层 try/except 捕获后追加进 errors——此时 blocks 已经非空。
        // 后端 PLC_UploadApp.cs 只看 blocks.Count > 0 决定是否入库，从不读 errors，所以这种
        // "blocks 非空 + errors 非空" 的组合必须判定为成功，否则本地结论会比服务器更悲观。
        // 真实 engine.py 不容易稳定复现这个组合，所以直接测被抽出来的纯函数 InterpretParseJson。
        [Fact]
        public void InterpretParseJson_TreatsNonEmptyBlocksAsSuccessEvenWithErrors()
        {
            const string json = @"{
                ""blocks"": [ { ""name"": ""FC1"", ""language"": ""SCL"" } ],
                ""errors"": [ ""解析错误: 交叉引用构建失败"" ]
            }";

            var result = AnalyzerRunner.InterpretParseJson(json);

            Assert.Null(result);
        }

        [Fact]
        public void InterpretParseJson_TreatsEmptyBlocksAsFailureAndUsesFirstError()
        {
            const string json = @"{
                ""blocks"": [],
                ""errors"": [ ""XML解析失败"" ]
            }";

            var result = AnalyzerRunner.InterpretParseJson(json);

            Assert.Equal("XML解析失败", result);
        }
    }
}
