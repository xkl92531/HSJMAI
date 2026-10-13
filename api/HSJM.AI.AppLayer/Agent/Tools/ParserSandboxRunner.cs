using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    internal static class ParserSandboxRunner
    {
        public static async Task<ExecuteParserSandboxResult> RunAsync(
            string code,
            string testFilePath,
            int timeoutSeconds,
            ILogger logger)
        {
            var result = new ExecuteParserSandboxResult
            {
                TestFilePath = testFilePath
            };

            try
            {
                if (!File.Exists(testFilePath))
                {
                    result.Success = false;
                    result.Errors = new List<string> { $"测试文件不存在: {testFilePath}" };
                    logger.LogError("[沙箱Runner] 测试文件不存在: {Path}", testFilePath);
                    return result;
                }

                var language = "Python";
                var startTime = DateTime.UtcNow;

                logger.LogInformation("[沙箱Runner] 开始执行 Python 脚本，代码长度={Length}", code.Length);
                var exec = await RunPythonSandboxAsync(code, testFilePath, timeoutSeconds, logger);

                result.ExecutionTimeMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
                result.Errors = new List<string>();

                // 记录原始输出
                logger.LogInformation("[沙箱Runner] Python执行完成: Success={Success}", exec.Success);
                if (!string.IsNullOrWhiteSpace(exec.StdOut))
                {
                    logger.LogInformation("[沙箱Runner] StdOut: {Output}", exec.StdOut);
                }
                if (!string.IsNullOrWhiteSpace(exec.StdErr))
                {
                    logger.LogWarning("[沙箱Runner] StdErr: {Error}", exec.StdErr);
                }

                if (!exec.Success)
                {
                    result.Success = false;
                    result.Errors.Add(exec.ErrorMessage ?? "沙箱执行失败");
                    if (!string.IsNullOrWhiteSpace(exec.StdErr))
                    {
                        result.Errors.Add($"StdErr: {exec.StdErr}");
                    }
                    logger.LogError("[沙箱Runner] 执行失败: {Error}", exec.ErrorMessage);
                    return result;
                }

                result.Success = true;
                result.ExtractedData = exec.Data ?? new Dictionary<string, object>
                {
                    ["raw_output"] = exec.StdOut ?? string.Empty,
                    ["stderr"] = exec.StdErr ?? string.Empty
                };

                logger.LogInformation("[沙箱Runner] ✅ 完成: Language={Language}, Time={Ms}ms", language, result.ExecutionTimeMs);
                return result;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[沙箱Runner] 失败");
                result.Success = false;
                result.Errors = new List<string> { ex.Message };
                return result;
            }
        }

        private static async Task<ExecuteResult> RunPythonSandboxAsync(string code, string testFilePath, int timeoutSeconds, ILogger logger)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"parser-run-py-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var parserFile = Path.Combine(tempDir, "parser.py");
                await File.WriteAllTextAsync(parserFile, code);
                logger.LogInformation("[沙箱Python] 解析器文件: {File}", parserFile);

                var runnerCode = @"import json
import os
import sys

# Ensure local temp dir is on sys.path even under -I
sys.path.insert(0, os.path.dirname(__file__))

from parser import *

if len(sys.argv) < 2:
    print('{""error"":""missing file path""}')
    sys.exit(1)

file_path = sys.argv[1]

if 'parse' in globals():
    result = parse(file_path)
else:
    # try class with parse method
    parser_class = None
    for obj in globals().values():
        if hasattr(obj, '__name__') and hasattr(obj, 'parse'):
            parser_class = obj
            break
    if parser_class is None:
        print('{""error"":""parse not found""}')
        sys.exit(2)
    instance = parser_class()
    result = instance.parse(file_path)

print(json.dumps(result))
";

                var runnerFile = Path.Combine(tempDir, "runner.py");
                await File.WriteAllTextAsync(runnerFile, runnerCode);
                logger.LogInformation("[沙箱Python] Runner文件: {File}", runnerFile);

                var command = $"-I -B \"{runnerFile}\" \"{testFilePath}\"";
                logger.LogInformation("[沙箱Python] 执行命令: python {Command}", command);
                
                var processResult = await RunProcessAsync("python", command, tempDir, timeoutSeconds, logger);
                
                if (!processResult.Success)
                {
                    logger.LogError("[沙箱Python] 进程执行失败: {Error}", processResult.ErrorMessage);
                    return new ExecuteResult(false, processResult.ErrorMessage, processResult.StdOut, processResult.StdErr, null);
                }

                logger.LogInformation("[沙箱Python] 原始输出长度: {Length}", processResult.StdOut?.Length ?? 0);
                var data = TryParseJson(processResult.StdOut, logger);
                return new ExecuteResult(true, null, processResult.StdOut, processResult.StdErr, data);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static Dictionary<string, object>? TryParseJson(string content, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogWarning("[沙箱Python] 输出为空，无法解析JSON");
                return null;
            }

            try
            {
                var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                logger.LogInformation("[沙箱Python] JSON解析成功");
                return new Dictionary<string, object>
                {
                    ["json"] = root.Clone(),
                    ["raw"] = content
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[沙箱Python] JSON解析失败，返回原始文本: {Content}", content.Length > 200 ? content.Substring(0, 200) + "..." : content);
                return new Dictionary<string, object>
                {
                    ["raw"] = content
                };
            }
        }

        private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, string workingDirectory, int timeoutSeconds, ILogger logger)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            
            try
            {
                process.Start();
                logger.LogInformation("[沙箱进程] 进程已启动: PID={Pid}", process.Id);

                // 异步读取输出和错误流
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();

                // 使用Task.WhenAny等待进程完成或超时
                var processTask = process.WaitForExitAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                
                var completed = await Task.WhenAny(processTask, timeoutTask);
                
                if (completed == timeoutTask)
                {
                    // 超时了，强制结束进程
                    logger.LogWarning("[沙箱进程] 执行超时（{Timeout}秒），强制终止", timeoutSeconds);
                    try 
                    { 
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                        }
                    } 
                    catch { /* 忽略kill异常 */ }
                    
                    var stdOut = "";
                    var stdErr = "";
                    try
                    {
                        // 尝试读取已有的输出
                        stdOut = await stdOutTask.ConfigureAwait(false);
                        stdErr = await stdErrTask.ConfigureAwait(false);
                    }
                    catch { /* 忽略读取异常 */ }
                    
                    return new ProcessResult(false, $"执行超时（{timeoutSeconds}秒）", stdOut, stdErr);
                }

                // 进程正常结束，读取输出
                var stdOutResult = await stdOutTask;
                var stdErrResult = await stdErrTask;
                
                logger.LogInformation("[沙箱进程] 进程退出: ExitCode={Code}, StdOut长度={OutLen}, StdErr长度={ErrLen}", 
                    process.ExitCode, stdOutResult?.Length ?? 0, stdErrResult?.Length ?? 0);
                
                if (process.ExitCode != 0)
                {
                    var errorMsg = !string.IsNullOrWhiteSpace(stdErrResult) 
                        ? stdErrResult 
                        : $"进程退出码: {process.ExitCode}";
                    logger.LogError("[沙箱进程] 执行失败: {Error}", errorMsg);
                    return new ProcessResult(false, errorMsg, stdOutResult, stdErrResult);
                }

                logger.LogInformation("[沙箱进程] 执行成功");
                return new ProcessResult(true, null, stdOutResult, stdErrResult);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[沙箱进程] 进程执行异常");
                return new ProcessResult(false, $"进程执行异常: {ex.Message}", "", ex.ToString());
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        private sealed record ExecuteResult(bool Success, string? ErrorMessage, string? StdOut, string? StdErr, Dictionary<string, object>? Data);
        private sealed record ProcessResult(bool Success, string? ErrorMessage, string StdOut, string StdErr);
    }
}
