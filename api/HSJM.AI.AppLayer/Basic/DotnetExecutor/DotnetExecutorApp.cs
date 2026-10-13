using HSJM.AI.AppLayer.Basic.DotnetExecutor.DTOS;
using HSJM.AI.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Basic.DotnetExecutor
{
    public class DotnetExecutorApp : ISelfScopedAutoInject
    {
        private readonly ILogger<DotnetExecutorApp> _logger;
        private readonly string _uploadDirectory;
        private readonly int _timeoutSeconds;
        private readonly long _maxFileSizeBytes;
        public DotnetExecutorApp(ILogger<DotnetExecutorApp> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 执行.NET代码
        /// </summary>
        public async Task<HttpResponseResultModel<string>> ExecuteAsync(DotnetExecutionConditionDTO request)
        {
            _logger.LogInformation("========== 收到新请求 ==========");
            _logger.LogInformation("执行模式: {Mode}", request.Mode);
            _logger.LogInformation("文件路径: {FilePath}", request.FilePath);
            _logger.LogInformation("项目路径: {ProjectPath}", request.ProjectPath);
            _logger.LogInformation("参数: {Parameters}", request.Parameters);
            _logger.LogInformation("文件类型: {FileType}", request.FileType);
            _logger.LogInformation("上传文件数量: {FileCount}", request.Files?.Count ?? 0);

            // 保存上传的文件
            var uploadedFiles = new List<string>();
            try
            {
                if (request.Files != null && request.Files.Count > 0)
                {
                    uploadedFiles = await SaveUploadedFilesAsync(request.Files);
                }

                // 构建命令
                var command = BuildCommand(request, uploadedFiles);

                // 确定工作目录
                string? workingDirectory = null;
                if (request.Mode == "singlefile" && !string.IsNullOrEmpty(request.FilePath))
                {
                    // 单文件模式：使用文件所在目录作为工作目录，避免继承当前项目配置
                    workingDirectory = Path.GetDirectoryName(request.FilePath);
                }

                _logger.LogInformation("执行命令: {Command}", command);
                _logger.LogInformation("工作目录: {WorkingDirectory}", workingDirectory ?? Directory.GetCurrentDirectory());
                _logger.LogInformation("================================");

                // 执行命令
                var result = await ExecuteCommandAsync(command, workingDirectory);

                _logger.LogInformation("执行完成 - 成功: {Success}", result.IsSuccess);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行过程中发生错误");
                return new HttpResponseResultModel<string>
                {
                    IsSuccess = false,
                    ErrorMessage = ex.Message
                };
            }
            finally
            {
                // 清理上传的临时文件
                CleanupUploadedFiles(uploadedFiles);
            }
        }

        /// <summary>
        /// 保存上传的文件
        /// </summary>
        private async Task<List<string>> SaveUploadedFilesAsync(List<IFormFile> files)
        {
            var savedFiles = new List<string>();

            foreach (var file in files)
            {
                if (file.Length > _maxFileSizeBytes)
                {
                    throw new InvalidOperationException($"文件 {file.FileName} 超过最大限制 {_maxFileSizeBytes / 1024 / 1024}MB");
                }

                var uniqueName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{file.FileName}";
                var filePath = Path.Combine(_uploadDirectory, uniqueName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                savedFiles.Add(filePath);
                _logger.LogInformation("上传文件: {OriginalName} -> {SavedPath}", file.FileName, filePath);
            }

            return savedFiles;
        }

        /// <summary>
        /// 构建执行命令
        /// </summary>
        private string BuildCommand(DotnetExecutionConditionDTO request, List<string> uploadedFiles)
        {
            var commandBuilder = new StringBuilder();

            if (request.Mode == "singlefile")
            {
                // .NET 10 单文件脚本模式
                if (string.IsNullOrEmpty(request.FilePath))
                {
                    throw new InvalidOperationException("单文件模式需要指定 .cs 文件路径");
                }

                if (!File.Exists(request.FilePath))
                {
                    throw new FileNotFoundException($"文件不存在: {request.FilePath}\n当前工作目录: {Directory.GetCurrentDirectory()}");
                }

                commandBuilder.Append($"dotnet run \"{request.FilePath}\"");
            }
            else if (request.Mode == "project")
            {
                // 传统项目模式
                if (string.IsNullOrEmpty(request.ProjectPath))
                {
                    throw new InvalidOperationException("项目模式需要指定项目路径");
                }

                commandBuilder.Append("dotnet run");

                if (request.ProjectPath.EndsWith(".csproj") || Directory.Exists(request.ProjectPath))
                {
                    commandBuilder.Append($" --project \"{request.ProjectPath}\"");
                }
            }
            else
            {
                throw new InvalidOperationException($"不支持的执行模式: {request.Mode}");
            }

            // 添加参数分隔符
            commandBuilder.Append(" --");

            // 添加用户自定义参数
            if (!string.IsNullOrWhiteSpace(request.Parameters))
            {
                var paramArray = request.Parameters
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                foreach (var param in paramArray)
                {
                    if (param.Contains(' ') && !param.StartsWith("\"") && !param.StartsWith("'"))
                    {
                        commandBuilder.Append($" \"{param}\"");
                    }
                    else
                    {
                        commandBuilder.Append($" {param}");
                    }
                }
            }

            // 添加上传的文件路径作为参数
            foreach (var filePath in uploadedFiles)
            {
                commandBuilder.Append($" \"{filePath}\"");
            }

            // 添加文件类型参数
            if (!string.IsNullOrEmpty(request.FileType))
            {
                commandBuilder.Append($" --filetype {request.FileType}");
            }

            return commandBuilder.ToString();
        }

        /// <summary>
        /// 执行命令
        /// </summary>
        private async Task<HttpResponseResultModel<string>> ExecuteCommandAsync(string command, string? workingDirectory = null)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
            };

            using var process = new Process { StartInfo = processStartInfo };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 等待进程完成或超时
            var completedInTime = await Task.Run(() => process.WaitForExit(_timeoutSeconds * 1000));

            if (!completedInTime)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "终止超时进程时发生错误");
                }

                return new HttpResponseResultModel<string>
                {
                    IsSuccess = false,
                    ErrorMessage = $"执行超时 ({_timeoutSeconds}秒)",
                };
            }

            var output = outputBuilder.ToString();
            var errorOutput = errorBuilder.ToString();

            if (process.ExitCode != 0)
            {
                _logger.LogError("执行失败，退出码: {ExitCode}", process.ExitCode);
                _logger.LogError("错误输出: {Error}", errorOutput);

                return new HttpResponseResultModel<string>
                {
                    IsSuccess = false,
                    ErrorMessage = errorOutput,
                };
            }

            _logger.LogInformation("执行成功");
            if (!string.IsNullOrEmpty(output))
            {
                _logger.LogInformation("输出: {Output}", output.Length > 200 ? output[..200] : output);
            }

            return new HttpResponseResultModel<string>
            {
                IsSuccess = true,
                BackResult = string.IsNullOrEmpty(output) ? "执行完成（无输出）" : output,
            };
        }

        /// <summary>
        /// 清理上传的临时文件
        /// </summary>
        private void CleanupUploadedFiles(List<string> filePaths)
        {
            foreach (var filePath in filePaths)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        _logger.LogInformation("已删除临时文件: {FilePath}", filePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "删除临时文件失败: {FilePath}", filePath);
                }
            }
        }

    }
}
