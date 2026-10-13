using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

[assembly: InternalsVisibleTo("HsjmTia.Core.Tests")]

namespace HsjmTia.Core.Analyze
{
    /// <summary>
    /// 调用后端同一份 engine.py 做本地解析校验。
    /// 参数与 PLC_UploadApp 里的调用逐字一致，保证现场看到的结果与入库后一致。
    /// </summary>
    public static class AnalyzerRunner
    {
        public static async Task<AnalyzeReport> RunAsync(
            string pythonExe,
            string engineScript,
            IEnumerable<string> xmlFiles,
            int parallelism,
            CancellationToken cancellationToken)
        {
            var files = (xmlFiles ?? Enumerable.Empty<string>()).ToList();
            var failures = new ConcurrentBag<AnalyzeFailure>();
            var succeeded = 0;

            if (files.Count == 0)
            {
                return new AnalyzeReport { Total = 0, Succeeded = 0, Failures = new List<AnalyzeFailure>() };
            }

            using (var gate = new SemaphoreSlim(Math.Max(1, parallelism)))
            {
                var tasks = files.Select(async file =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var error = await Task.Run(
                            () => RunOne(pythonExe, engineScript, file), cancellationToken).ConfigureAwait(false);
                        if (error == null)
                        {
                            Interlocked.Increment(ref succeeded);
                        }
                        else
                        {
                            failures.Add(new AnalyzeFailure { File = file, Error = error });
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            return new AnalyzeReport
            {
                Total = files.Count,
                Succeeded = succeeded,
                Failures = failures.OrderBy(f => f.File, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        /// <summary>返回 null 表示成功，否则返回错误首行。</summary>
        private static string RunOne(string pythonExe, string engineScript, string file)
        {
            var outputJson = Path.Combine(
                Path.GetTempPath(), Path.GetRandomFileName() + ".parsed.json");

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{engineScript}\" --file \"{file}\" --format json-v2 --output \"{outputJson}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(engineScript)
            };

            try
            {
                using (var process = Process.Start(psi))
                {
                    var stderr = process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        return FirstLine(stderr) ?? $"engine.py 退出码 {process.ExitCode}";
                    }
                }

                return File.Exists(outputJson) ? EvaluateParseResult(outputJson) : "engine.py 未产出解析结果";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            finally
            {
                if (File.Exists(outputJson))
                {
                    try { File.Delete(outputJson); } catch { /* 临时文件删不掉不影响结论 */ }
                }
            }
        }

        /// <summary>
        /// engine.py 对无法识别/处理失败的文件也以退出码 0 收尾，把结果全部记在输出 JSON 里
        /// （而不是进程退出码或 stderr）。要判断"能不能用"，必须读这个 JSON。
        /// 返回 null 表示无错误（成功），否则返回错误信息。
        /// </summary>
        private static string EvaluateParseResult(string outputJson)
        {
            try
            {
                var json = File.ReadAllText(outputJson);
                return InterpretParseJson(json);
            }
            catch (Exception ex)
            {
                return $"解析结果 JSON 读取失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 纯函数：判定 engine.py 输出的 json-v2 是否代表"可入库"。
        /// 判定信号必须与后端网关逐字一致：PLC_UploadApp.cs（约 264 / 2290 两处）用
        /// <c>parseResult.blocks != null &amp;&amp; parseResult.blocks.Count > 0</c> 决定是否接受该文件，
        /// 从不读 errors 字段。parser.py 的 extract()（及 _extract_from_source_text 同款结构）会先把
        /// blocks 填好，随后调用关系/GRAPH/逻辑单元/交叉引用/语义分析等后续阶段的任何异常都被外层
        /// try/except 捕获后追加进 errors，此时 blocks 已经非空——服务器仍会完整入库该文件。
        /// 所以 errors 非空绝不能单独当作失败信号，否则本地结论会比服务器更悲观，
        /// 现场工程师会被引导去"修复"一个服务器其实能正常接收的文件。
        /// 未产出任何 blocks 才是真正的失败：此时优先用 errors[0] 作为报告文本
        /// ——对完全解析不出来的文件，engine.py 退出码是 0、stderr 是空的，
        /// 问题只写在 JSON 的 errors 数组里（现有测试 RunAsync_ReportsTheFileNameAndStderrForABadFile
        /// 这个名字容易误导：它拿到的消息其实来自 errors[0]，不是 stderr）。
        /// 返回 null 表示成功，否则返回失败信息。
        /// </summary>
        internal static string InterpretParseJson(string json)
        {
            var obj = JObject.Parse(json);

            var blocks = obj["blocks"] as JArray;
            if (blocks != null && blocks.Count > 0)
            {
                return null;
            }

            var errors = obj["errors"] as JArray;
            var first = errors != null && errors.Count > 0 ? errors[0]?.ToString() : null;
            return string.IsNullOrWhiteSpace(first) ? "engine.py 未产出任何程序块" : first;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            var line = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
    }
}
