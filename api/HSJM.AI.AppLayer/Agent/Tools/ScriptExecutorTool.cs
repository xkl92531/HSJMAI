using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 技能包脚本执行器工具
    /// 用于执行 Agent Skills 技能包中的 Python/Shell/PowerShell 脚本
    /// 安全限制：只能执行 wwwroot/skills 目录下的脚本
    /// </summary>
    public class ScriptExecutorTool : ISelfScopedAutoInject
    {
        private readonly ILogger<ScriptExecutorTool> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly IConfiguration _configuration;
        
        /// <summary>
        /// Skills 根目录（相对于 wwwroot）
        /// </summary>
        private const string SKILLS_ROOT_FOLDER = "skills";
        
        /// <summary>
        /// 默认超时时间（秒）
        /// </summary>
        private const int DEFAULT_TIMEOUT_SECONDS = 60;

        /// <summary>
        /// Python 解释器路径 (从配置读取，默认为 python)
        /// </summary>
        private string PythonPath => _configuration["AI:ExternalTools:PythonPath"] ?? "python";

        public ScriptExecutorTool(
            ILogger<ScriptExecutorTool> logger,
            IWebHostEnvironment environment,
            IConfiguration configuration)
        {
            _logger = logger;
            _environment = environment;
            _configuration = configuration;
        }

        /// <summary>
        /// 执行技能包中的脚本
        /// 支持 Python(.py)、Shell(.sh)、PowerShell(.ps1)、Batch(.bat/.cmd)、Node.js(.js) 多种脚本类型
        /// </summary>
        /// <param name="skillPackagePath">技能包目录路径（相对于 skills 目录，如：plc-analyzer/）</param>
        /// <param name="scriptName">脚本文件名（如：main.py、analyze.sh、run.js）</param>
        /// <param name="inputJson">传递给脚本的 JSON 格式输入参数</param>
        /// <param name="timeoutSeconds">执行超时时间（秒），默认60秒</param>
        /// <returns>脚本执行结果</returns>
        [Description("执行技能包中的脚本。支持: Python(.py), Shell(.sh), PowerShell(.ps1), Batch(.bat/.cmd), Node.js(.js)。\n" +
            "参数传递说明（智能模式）：\n" +
            "1. 扁平 JSON 自动展开: {\"file\": \"a.xml\", \"format\": \"json\"} => --file a.xml --format json\n" +
            "2. 复杂嵌套 JSON 回退: 通过 --input-base64 传递 Base64 编码的 JSON。\n" +
            "3. Shell/Batch/PowerShell 额外支持: 临时 JSON 文件路径作为第一个位置参数。\n" +
            "脚本必须将结果（包括错误信息）以 JSON 格式输出到 stdout。")]
        public async Task<ScriptExecutionResult> ExecuteSkillScriptAsync(
            [Description("技能包目录路径，如：generic-skill-factory/")] string skillPackagePath,
            [Description("脚本文件名，如：main.py、run.sh、index.js")] string scriptName,
            [Description("传递给脚本的 JSON 格式输入参数，将通过 --input-base64 传递")] string? inputJson = null,
            [Description("执行超时时间（秒），默认60")] int timeoutSeconds = DEFAULT_TIMEOUT_SECONDS)
        {
            var result = new ScriptExecutionResult
            {
                SkillPackagePath = skillPackagePath,
                ScriptName = scriptName,
                StartTime = DateTime.UtcNow
            };

            try
            {
                _logger.LogInformation("[ScriptExecutor] 开始执行: {Package}/{Script}", 
                    skillPackagePath, scriptName);

                // 1. 智能脚本探测 (Smart Script Detection)
                // 如果 scriptName 为 "auto" 或空，则根据当前 OS 自动查找最佳入口脚本
                if (string.IsNullOrWhiteSpace(scriptName) || scriptName.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[ScriptExecutor] 自动探测脚本 (Auto-Detect mode)...");
                    var detectedScript = DetectBestScriptForOS(skillPackagePath);
                    if (detectedScript == null)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"在 {skillPackagePath} 中未找到适配当前操作系统的入口脚本 (如 main.py, main.sh, start.bat 等)";
                        return result;
                    }
                    scriptName = detectedScript;
                    result.ScriptName = scriptName;
                    _logger.LogInformation("[ScriptExecutor] 自动选定脚本: {Script}", scriptName);
                }

                // 2. 构建并验证脚本路径（安全检查）
                var scriptPath = BuildAndValidateScriptPath(skillPackagePath, scriptName);
                if (scriptPath == null)
                {
                    result.Success = false;
                    result.ErrorMessage = $"脚本路径无效或不在 skills 目录下: {skillPackagePath}/{scriptName}";
                    _logger.LogError("[ScriptExecutor] {Error}", result.ErrorMessage);
                    return result;
                }

                if (!File.Exists(scriptPath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"脚本文件不存在: {scriptPath}";
                    _logger.LogError("[ScriptExecutor] {Error}", result.ErrorMessage);
                    return result;
                }

                // 3. 确定脚本类型
                var scriptType = DetermineScriptType(scriptName);
                result.ScriptType = scriptType.ToString();
                _logger.LogInformation("[ScriptExecutor] 脚本类型: {Type}", scriptType);

                // 3. 执行脚本
                var workingDir = Path.GetDirectoryName(scriptPath)!;
                var processResult = await ExecuteScriptAsync(
                    scriptType, scriptPath, inputJson, workingDir, timeoutSeconds);

                // 4. 填充结果
                result.Success = processResult.Success;
                result.ExitCode = processResult.ExitCode;
                result.StdOut = processResult.StdOut;
                result.StdErr = processResult.StdErr;
                result.ErrorMessage = processResult.ErrorMessage;
                result.ExecutionTimeMs = (int)(DateTime.UtcNow - result.StartTime).TotalMilliseconds;

                // 5. 尝试解析 JSON 输出 (无论成功失败，只要有输出都尝试解析，因为脚本可能在失败时返回 JSON 格式的错误信息)
                if (!string.IsNullOrWhiteSpace(result.StdOut))
                {
                    result.OutputData = TryParseJsonOutput(result.StdOut);
                }

                _logger.LogInformation("[ScriptExecutor] 执行完成: Success={Success}, Time={Ms}ms",
                    result.Success, result.ExecutionTimeMs);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScriptExecutor] 执行异常: {Package}/{Script}",
                    skillPackagePath, scriptName);
                result.Success = false;
                result.ErrorMessage = $"执行异常: {ex.Message}";
                result.ExecutionTimeMs = (int)(DateTime.UtcNow - result.StartTime).TotalMilliseconds;
                return result;
            }
        }

        /// <summary>
        /// 列出技能包中的可用脚本
        /// </summary>
        [Description("列出技能包目录中的可执行脚本文件")]
        public List<string> ListSkillScripts(
            [Description("技能包目录路径")] string skillPackagePath)
        {
            var scripts = new List<string>();
            
            try
            {
                var skillDir = GetSkillPackageFullPath(skillPackagePath);
                if (!Directory.Exists(skillDir))
                {
                    _logger.LogWarning("[ScriptExecutor] 技能包目录不存在: {Path}", skillDir);
                    return scripts;
                }

                var supportedExtensions = new[] { ".py", ".sh", ".ps1", ".bat", ".cmd" };
                foreach (var ext in supportedExtensions)
                {
                    var files = Directory.GetFiles(skillDir, $"*{ext}", SearchOption.TopDirectoryOnly);
                    foreach (var file in files)
                    {
                        scripts.Add(Path.GetFileName(file));
                    }
                }

                _logger.LogInformation("[ScriptExecutor] 找到 {Count} 个脚本: {Package}",
                    scripts.Count, skillPackagePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScriptExecutor] 列出脚本失败: {Package}", skillPackagePath);
            }

            return scripts;
        }

        /// <summary>
        /// 安装技能包依赖
        /// 自动检测 scripts/requirements.txt 并尝试安装
        /// </summary>
        [Description("安装技能包的依赖项。会自动查找 scripts/requirements.txt 并使用 pip 安装。")]
        public async Task<string> InstallSkillDependenciesAsync(
            [Description("技能包目录路径")] string skillPackagePath)
        {
            try
            {
                var skillDir = GetSkillPackageFullPath(skillPackagePath);
                var requirementsPath = Path.Combine(skillDir, "scripts", "requirements.txt");

                if (!File.Exists(requirementsPath))
                {
                    return $"未找到依赖文件: {requirementsPath}，无需安装。";
                }

                _logger.LogInformation("[ScriptExecutor] 开始安装依赖: {Path}", requirementsPath);

                // 构建 pip 命令
                // 使用配置的 Python 路径 -m pip install -r ...
                var args = $"-m pip install -r \"{requirementsPath}\"";
                
                // 这里我们复用 RunProcessAsync，但不仅限于 skills 目录下的脚本执行
                // Create a temporary "working dir" context
                var workingDir = Path.GetDirectoryName(requirementsPath)!;

                // 增加超时时间用于安装
                var result = await RunProcessAsync(PythonPath, args, workingDir, 300);

                if (result.Success)
                {
                    return $"依赖安装成功。\n{result.StdOut}";
                }
                else
                {
                    _logger.LogError("[ScriptExecutor] 依赖安装失败: {Error}", result.StdErr);
                    return $"依赖安装失败 (ExitCode={result.ExitCode}):\n{result.StdErr}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScriptExecutor] 安装依赖异常: {Package}", skillPackagePath);
                return $"安装依赖异常: {ex.Message}";
            }
        }

        #region Private Methods

        /// <summary>
        /// 根据当前操作系统探测最佳脚本
        /// </summary>
        private string? DetectBestScriptForOS(string skillPackagePath)
        {
            try
            {
                var skillsRoot = Path.Combine(
                    _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"),
                    SKILLS_ROOT_FOLDER);
                
                var packagePath = Path.Combine(skillsRoot, skillPackagePath.TrimStart('/').TrimEnd('/'));
                var scriptsPath = Path.Combine(packagePath, "scripts");

                // 定义候选脚本列表 (按优先级排序)
                var candidates = new List<string>();

                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    // Windows 优先
                    candidates.AddRange(new[] { "run.bat", "start.bat", "main.bat", "run.cmd", "start.cmd", "main.cmd", "run.ps1", "main.ps1" });
                }
                else
                {
                    // Linux/macOS 优先
                    candidates.AddRange(new[] { "run.sh", "start.sh", "main.sh" });
                }

                // 通用跨平台脚本 (作为后备)
                candidates.AddRange(new[] { "main.py", "index.py", "run.py", "main.js", "index.js", "run.js" });

                // 检查文件是否存在 (优先检查根目录，然后检查 scripts/ 目录)
                foreach (var filename in candidates)
                {
                    // 1. Check Root
                    if (File.Exists(Path.Combine(packagePath, filename)))
                        return filename;
                    
                    // 2. Check scripts/ folder
                    if (File.Exists(Path.Combine(scriptsPath, filename)))
                        return filename;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScriptExecutor] 自动探测脚本失败");
                return null;
            }
        }

        /// <summary>
        /// 构建并验证脚本完整路径（防止路径遍历攻击）
        /// </summary>
        private string? BuildAndValidateScriptPath(string skillPackagePath, string scriptName)
        {
            try
            {
                // 获取 skills 根目录
                var skillsRoot = Path.Combine(
                    _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"),
                    SKILLS_ROOT_FOLDER);

                // 1. 尝试直接路径
                var fullPath = Path.GetFullPath(Path.Combine(
                    skillsRoot,
                    skillPackagePath.TrimStart('/').TrimEnd('/'),
                    scriptName));

                // 2. 如果文件不存在，尝试在 scripts/ 子目录下查找
                if (!File.Exists(fullPath))
                {
                    var scriptsSubPath = Path.GetFullPath(Path.Combine(
                        skillsRoot,
                        skillPackagePath.TrimStart('/').TrimEnd('/'),
                        "scripts",
                        scriptName));
                    
                    if (File.Exists(scriptsSubPath))
                    {
                        fullPath = scriptsSubPath;
                        _logger.LogInformation("[ScriptExecutor] 在 scripts/ 子目录找到脚本: {Path}", fullPath);
                    }
                }

                // 安全检查：确保路径在 skills 目录内
                var normalizedSkillsRoot = Path.GetFullPath(skillsRoot);
                if (!fullPath.StartsWith(normalizedSkillsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[ScriptExecutor] 安全警告: 路径遍历攻击尝试: {Path}", fullPath);
                    return null;
                }

                return fullPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScriptExecutor] 路径构建失败");
                return null;
            }
        }

        /// <summary>
        /// 获取技能包完整路径
        /// </summary>
        private string GetSkillPackageFullPath(string skillPackagePath)
        {
            return Path.Combine(
                _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"),
                SKILLS_ROOT_FOLDER,
                skillPackagePath.TrimStart('/').TrimEnd('/'));
        }

        /// <summary>
        /// 根据文件扩展名确定脚本类型
        /// </summary>
        private ScriptType DetermineScriptType(string scriptName)
        {
            var ext = Path.GetExtension(scriptName).ToLowerInvariant();
            return ext switch
            {
                ".py" => ScriptType.Python,
                ".sh" => ScriptType.Shell,
                ".ps1" => ScriptType.PowerShell,
                ".bat" or ".cmd" => ScriptType.Batch,
                ".js" => ScriptType.NodeJS,
                _ => ScriptType.Unknown
            };
        }

        /// <summary>
        /// 执行脚本
        /// </summary>
        private async Task<ProcessExecutionResult> ExecuteScriptAsync(
            ScriptType scriptType,
            string scriptPath,
            string? inputJson,
            string workingDir,
            int timeoutSeconds)
        {
            string fileName;
            string arguments;
            string? tempInputFilePath = null;

            // 1. 准备参数文件 (Input File)
            // 为了兼容性和避免命令行长度限制，我们将 inputJson 写入临时文件
            // 并将其路径作为第一个位置参数传递给脚本
            if (!string.IsNullOrEmpty(inputJson))
            {
                try
                {
                    // 在 temp 目录下创建 .json 文件
                    var tempDir = Path.Combine(Path.GetTempPath(), "HSJM_AI_Skills_Temp");
                    if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                    
                    tempInputFilePath = Path.Combine(tempDir, $"input_{Guid.NewGuid()}.json");
                    await File.WriteAllTextAsync(tempInputFilePath, inputJson, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ScriptExecutor] 创建临时输入文件失败，将仅依赖 --input-base64");
                }
            }

            // 2. 构建基础命令
            switch (scriptType)
            {
                case ScriptType.Python:
                    fileName = PythonPath;
                    // -u: unbuffered stdout
                    arguments = $"-u \"{scriptPath}\"";
                    break;

                case ScriptType.Shell:
                    fileName = OperatingSystem.IsWindows() ? "bash" : "/bin/bash";
                    arguments = $"\"{scriptPath}\"";
                    break;

                case ScriptType.PowerShell:
                    // 兼容性优化：优先使用 PowerShell Core (pwsh)，如果没有则回退到 Windows PowerShell
                    fileName = "pwsh";
                    if (OperatingSystem.IsWindows())
                    {
                        // 简单检测一下 pwsh 是否在一个路径里，或者直接尝试捕获异常？ 
                        // 由于 RunProcessAsync 内部会处理异常，我们这里可以用更稳妥的方式：
                        // 默认用 pwsh，但在文档中说明。或者我们可以做得更智能：
                        // 这里我们简单做一个判定，如果非 Windows 必须用 pwsh，如果是 Windows 尝试 powershell
                        // 但为了统一，我们先保持 pwsh，如果失败（比如用户只装了老版），可能需要用户自行配置
                        // 更好的做法是：
                        fileName = "pwsh"; 
                        // 如果我们在 Windows 上且找不到 pwsh，可能需要回退到 powershell.exe
                        // 但为了代码简洁，这里暂时保留 pwsh，建议用户安装 PowerShell 7+
                        // 或者：
                        // fileName = "powershell"; // Windows 自带
                    }
                    // 最终决定：为了最好的跨平台兼容性，在 Windows 上优先尝试 powershell (老版兼容好), 
                    // 但新版技能可能用到 pwsh 特性。
                    // 让我们做一个折中：
                    fileName = "pwsh"; 
                    if (OperatingSystem.IsWindows())
                    {
                         // 检查环境变量或者直接假定：如果想要更好的兼容性，应该允许配置
                         // 这里改为：优先读取配置，否则默认 pwsh
                         fileName = _configuration["AI:ExternalTools:PowerShellPath"] ?? "pwsh";
                    }
                    arguments = $"-NoProfile -NonInteractive -File \"{scriptPath}\"";
                    break;

                case ScriptType.Batch:
                    fileName = "cmd.exe";
                    arguments = $"/c \"{scriptPath}\"";
                    break;

                case ScriptType.NodeJS:
                    fileName = "node";
                    arguments = $"\"{scriptPath}\"";
                    break;

                default:
                    return new ProcessExecutionResult
                    {
                        Success = false,
                        ErrorMessage = $"不支持的脚本类型: {scriptType}"
                    };
            }

            // 3. 追加参数 (智能参数展开)
            if (!string.IsNullOrEmpty(inputJson))
            {
                // 优先尝试：将扁平 JSON 展开为命令行参数 --key "value"
                // 例如: {"file": "a.xml", "format": "json"} => --file "a.xml" --format json
                var expandedArgs = TryExpandJsonToArgs(inputJson);
                if (!string.IsNullOrEmpty(expandedArgs))
                {
                    arguments += " " + expandedArgs;
                    _logger.LogInformation("[ScriptExecutor] JSON 展开为命令行参数: {Args}",
                        expandedArgs.Length > 200 ? expandedArgs.Substring(0, 200) + "..." : expandedArgs);
                }
                else
                {
                    // 回退方案：对于复杂嵌套 JSON 或解析失败的情况
                    // Shell/Batch/PowerShell: 传递临时文件路径
                    if (!string.IsNullOrEmpty(tempInputFilePath))
                    {
                        if (scriptType != ScriptType.Python && scriptType != ScriptType.NodeJS)
                        {
                            arguments += $" \"{tempInputFilePath}\"";
                        }
                    }

                    // 同时传递 --input-base64 作为备用
                    var base64Input = Convert.ToBase64String(Encoding.UTF8.GetBytes(inputJson));
                    arguments += $" --input-base64 \"{base64Input}\"";
                    _logger.LogInformation("[ScriptExecutor] 使用 --input-base64 传递复杂参数");
                }
            }

            try
            {
                // 4. 执行
                return await RunProcessAsync(fileName, arguments, workingDir, timeoutSeconds, inputJson);
            }
            finally
            {
                // 5. 清理临时文件
                if (!string.IsNullOrEmpty(tempInputFilePath) && File.Exists(tempInputFilePath))
                {
                    try
                    {
                        File.Delete(tempInputFilePath);
                    }
                    catch { /* 忽略清理错误 */ }
                }
            }
        }

        /// <summary>
        /// 运行进程
        /// </summary>
        private async Task<ProcessExecutionResult> RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            int timeoutSeconds,
            string? inputData = null)
        {
            var result = new ProcessExecutionResult();

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false) // 确保输入也是 UTF-8 且无 BOM
            };

            // 强制 Python 使用 UTF-8 编码，防止中文乱码
            if (fileName.Contains("python", StringComparison.OrdinalIgnoreCase) || fileName == PythonPath)
            {
                startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            }

            using var process = new Process { StartInfo = startInfo };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                process.Start();
                _logger.LogInformation("[ScriptExecutor] 进程启动: PID={Pid}, Cmd={Cmd} {Args}",
                    process.Id, fileName, arguments.Length > 100 ? arguments.Substring(0, 100) + "..." : arguments);

                // 写入标准输入 (Stdin)
                if (!string.IsNullOrEmpty(inputData))
                {
                    await process.StandardInput.WriteAsync(inputData);
                    await process.StandardInput.FlushAsync();
                    process.StandardInput.Close(); // 写入完成后关闭输入流，通知脚本输入结束
                }

                // 异步读取输出
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();

                // 等待进程完成或超时
                var processTask = process.WaitForExitAsync(cts.Token);

                try
                {
                    await processTask;
                }
                catch (OperationCanceledException)
                {
                    // 超时
                    _logger.LogWarning("[ScriptExecutor] 执行超时 ({Timeout}秒)，强制终止", timeoutSeconds);
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                        }
                    }
                    catch { /* 忽略 */ }

                    result.Success = false;
                    result.ErrorMessage = $"执行超时（{timeoutSeconds}秒）";
                    result.TimedOut = true;
                    return result;
                }

                // 读取输出
                result.StdOut = await stdOutTask;
                result.StdErr = await stdErrTask;
                result.ExitCode = process.ExitCode;

                if (process.ExitCode == 0)
                {
                    result.Success = true;
                    _logger.LogInformation("[ScriptExecutor] 执行成功: ExitCode=0, StdOut长度={Len}",
                        result.StdOut?.Length ?? 0);
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = !string.IsNullOrWhiteSpace(result.StdErr)
                        ? result.StdErr
                        : $"进程退出码: {process.ExitCode}";
                    _logger.LogWarning("[ScriptExecutor] 执行失败: ExitCode={Code}, Error={Error}",
                        result.ExitCode, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScriptExecutor] 进程执行异常");
                result.Success = false;
                result.ErrorMessage = $"进程执行异常: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 尝试将扁平 JSON 展开为命令行参数
        /// {"file": "a.xml", "format": "json"} => --file "a.xml" --format json
        /// 如果 JSON 包含数组或嵌套对象，返回 null 表示需要回退到 base64 方式
        /// </summary>
        private string? TryExpandJsonToArgs(string inputJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(inputJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                var args = new StringBuilder();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var key = prop.Name;
                    var value = prop.Value;

                    // 跳过复杂类型（数组、嵌套对象）- 回退到 base64
                    if (value.ValueKind == JsonValueKind.Array || value.ValueKind == JsonValueKind.Object)
                    {
                        _logger.LogDebug("[ScriptExecutor] JSON 包含复杂类型 ({Key})，回退到 base64", key);
                        return null;
                    }

                    // 跳过 null 值
                    if (value.ValueKind == JsonValueKind.Null)
                        continue;

                    // 布尔值特殊处理
                    if (value.ValueKind == JsonValueKind.True)
                    {
                        // --verbose (flag 参数)
                        args.Append($" --{key}");
                    }
                    else if (value.ValueKind == JsonValueKind.False)
                    {
                        // false 则不添加该参数
                        continue;
                    }
                    else
                    {
                        // 字符串、数字等
                        var strValue = value.ToString();
                        
                        // 单字符 key 使用短参数格式 -f，否则使用长参数格式 --file
                        var prefix = key.Length == 1 ? "-" : "--";
                        
                        // 值包含空格、反斜杠或特殊字符时加引号
                        if (strValue.Contains(' ') || strValue.Contains('\\') || 
                            strValue.Contains('"') || strValue.Contains('&') ||
                            strValue.Contains('|') || strValue.Contains('<') || 
                            strValue.Contains('>'))
                        {
                            // 转义内部引号
                            strValue = strValue.Replace("\"", "\\\"");
                            args.Append($" {prefix}{key} \"{strValue}\"");
                        }
                        else
                        {
                            args.Append($" {prefix}{key} {strValue}");
                        }
                    }
                }

                var result = args.ToString().Trim();
                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ScriptExecutor] JSON 解析失败，回退到 base64");
                return null;
            }
        }

        /// <summary>
        /// 尝试将输出解析为 JSON
        /// </summary>
        private Dictionary<string, object>? TryParseJsonOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            try
            {
                // 尝试找到 JSON 内容（可能有其他输出在前面）
                var jsonStart = output.IndexOf('{');
                var jsonEnd = output.LastIndexOf('}');
                
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonStr = output.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var doc = JsonDocument.Parse(jsonStr);
                    
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.Clone();
                    }
                    
                    _logger.LogDebug("[ScriptExecutor] JSON 解析成功，{Count} 个字段", dict.Count);
                    return dict;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ScriptExecutor] 输出不是有效 JSON，返回原始文本");
            }

            return null;
        }

    }

    #region Result Models

    /// <summary>
    /// 脚本类型枚举
    /// </summary>
    public enum ScriptType
    {
        Unknown,
        Python,
        Shell,
        PowerShell,
        Batch,
        NodeJS
    }

    /// <summary>
    /// 脚本执行结果
    /// </summary>
    public class ScriptExecutionResult
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 技能包路径
        /// </summary>
        public string SkillPackagePath { get; set; } = "";

        /// <summary>
        /// 脚本名称
        /// </summary>
        public string ScriptName { get; set; } = "";

        /// <summary>
        /// 脚本类型
        /// </summary>
        public string ScriptType { get; set; } = "";

        /// <summary>
        /// 进程退出码
        /// </summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// 标准输出
        /// </summary>
        public string? StdOut { get; set; }

        /// <summary>
        /// 标准错误
        /// </summary>
        public string? StdErr { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 解析后的输出数据（如果输出是 JSON）
        /// </summary>
        public Dictionary<string, object>? OutputData { get; set; }

        /// <summary>
        /// 执行开始时间
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// 执行耗时（毫秒）
        /// </summary>
        public int ExecutionTimeMs { get; set; }
    }

    /// <summary>
    /// 进程执行结果（内部使用）
    /// </summary>
    internal class ProcessExecutionResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string? StdOut { get; set; }
        public string? StdErr { get; set; }
        public string? ErrorMessage { get; set; }
        public bool TimedOut { get; set; }
    }

    #endregion
}
#endregion