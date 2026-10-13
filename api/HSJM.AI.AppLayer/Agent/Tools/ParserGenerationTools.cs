using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.Utility;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 解析器代码生成工具集
    /// 第一层智能体使用：生成、格式化、验证、沙箱执行、部署解析器
    /// </summary>
    public class ParserGenerationTools
    {
        private const string ParserCodeGeneratorRoleCode = "parser_code_generator";

        private readonly ILogger<ParserGenerationTools> _logger;
        private readonly ITool_DefinitionService _toolDefinitionService;
        private readonly LLMModelService _llmModelService;
        private readonly IAgent_RoleService _agentRoleService;

        public ParserGenerationTools(
            ILogger<ParserGenerationTools> logger,
            ITool_DefinitionService toolDefinitionService,
            LLMModelService llmModelService,
            IAgent_RoleService agentRoleService)
        {
            _logger = logger;
            _toolDefinitionService = toolDefinitionService;
            _llmModelService = llmModelService;
            _agentRoleService = agentRoleService;
        }

        /// <summary>
        /// 生成解析器代码（Python脚本）
        /// </summary>
        [Description("根据PLC文件结构信息生成Python解析器脚本。⚠️ structureInfoJson必须从ExtractStructureInfo工具的返回结果中获取(包含PlcBrand/FileFormat/BlockPatterns等字段)。如果上游已提供了结构信息，直接使用，不要再次调用ExtractStructureInfo")]
        public async Task<GenerateParserCodeResult> GenerateParserCodeAsync(
            [Description("结构信息JSON(必须包含PlcBrand/FileFormat/BlockPatterns,来自ExtractStructureInfo的输出)")] string structureInfoJson,
            [Description("目标语言(CSharp/Python),默认Python；当前仅支持Python")]
            string targetLanguage = "Python",
            [Description("可选：生成选项JSON")] string? optionsJson = null)
        {
            if (!string.Equals(targetLanguage, "Python", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[生成解析器] 仅支持Python，忽略目标语言: {Language}", targetLanguage);
                targetLanguage = "Python";
            }

            _logger.LogInformation("[生成解析器] 开始: 语言={Language}", targetLanguage);

            var result = new GenerateParserCodeResult
            {
                Language = targetLanguage
            };

            try
            {
                //result.Success = false;
                //result.ErrorMessage = $"生成失败: 生成异常！";
                //return result;
                // 🔧 正确反序列化 ExtractStructureInfoResult
                var structureInfo = JsonSerializer.Deserialize<ExtractStructureInfoResult>(structureInfoJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (structureInfo == null)
                {
                    throw new Exception("无法解析结构信息JSON");
                }

                var brand = structureInfo.PlcBrand;
                var format = structureInfo.FileFormat;
                var strategy = structureInfo.ParsingStrategy;

                _logger.LogInformation("[生成解析器] 品牌={Brand}, 格式={Format}, 策略={Strategy}", brand, format, strategy);

                // 🚀 使用LLM根据实际结构生成解析器代码（Python脚本）
                var prompt = BuildParserGenerationPrompt(structureInfo, targetLanguage);
                
                var agent = new ChatClientAgent(
                    await ResolveChatClientByRoleCodeAsync(ParserCodeGeneratorRoleCode),
                    instructions: "你是一个PLC解析器代码生成专家，优先生成Python解析器代码。",
                    name: "ParserGenerator"
                );

                var response = await agent.RunAsync(prompt);
                var generatedCode = ExtractCodeFromResponse(response.Text ?? "", targetLanguage);

                if (IsCSharpCode(generatedCode))
                {
                    _logger.LogWarning("[生成解析器] LLM返回C#代码，尝试强制Python重试一次");
                    var retryPrompt = prompt + "\n\n⚠️ 注意：上一次输出为C#，本次必须只输出Python代码，禁止C#。";
                    var retryResponse = await agent.RunAsync(retryPrompt);
                    generatedCode = ExtractCodeFromResponse(retryResponse.Text ?? "", targetLanguage);
                }

                if (string.IsNullOrWhiteSpace(generatedCode))
                {
                    throw new Exception("LLM未能生成有效代码");
                }

                // 落盘保存生成的Python脚本，供后续执行
                var (outputDir, fileName) = ParseGenerationOptions(optionsJson, structureInfo);
                Directory.CreateDirectory(outputDir);
                var safeFileName = SanitizeFileName(fileName);
                var codeFilePath = Path.Combine(outputDir, safeFileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                    ? safeFileName
                    : safeFileName + ".py");
                await File.WriteAllTextAsync(codeFilePath, generatedCode);

                result.Success = true;
                result.Code = codeFilePath;
                result.CodeFilePath = codeFilePath;
                // 添加明确的提示信息，防止智能体重复调用
                result.Message = $"解析器代码已成功生成并保存(长度={generatedCode.Length})。文件路径: {codeFilePath}。任务完成，请停止调用 GenerateParserCodeAsync 工具。请直接将代码展示给用户或进行下一步验证。";

                _logger.LogInformation("[生成解析器] 成功: 代码长度={Length}", generatedCode.Length);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[生成解析器] 失败");
                result.Success = false;
                result.ErrorMessage = $"生成失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 验证解析器语法（Python）
        /// </summary>
        [Description("验证生成的Python解析器脚本语法正确性")]
        public async Task<ValidateParserCodeResult> ValidateParserSyntaxAsync(
            [Description("解析器代码")] string code,
            [Description("语言类型(CSharp/Python)，当前仅支持Python")] string language)
        {
            if (!string.Equals(language, "Python", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[验证解析器] 仅支持Python，忽略语言: {Language}", language);
                language = "Python";
            }

            _logger.LogInformation("[验证解析器] 开始: 语言={Language}, 代码长度={Length}", language, code.Length);

            var result = new ValidateParserCodeResult
            {
                Language = language
            };

            try
            {
                var errors = new List<string>();
                var warnings = new List<string>();

                if (string.IsNullOrWhiteSpace(code))
                {
                    errors.Add("代码为空");
                }
                else
                {
                    var pythonResult = await TryPythonSyntaxCheckAsync(code, warnings);
                    if (!pythonResult.Success)
                    {
                        errors.AddRange(pythonResult.Errors);
                    }
                }

                result.IsValid = errors.Count == 0;
                result.Errors = errors;
                result.Warnings = warnings;
                result.SyntaxScore = result.IsValid ? (warnings.Count == 0 ? 100 : 85) : 40;

                _logger.LogInformation("[验证解析器] 完成: 有效={Valid}, 错误数={Errors}, 警告数={Warnings}",
                    result.IsValid, errors.Count, warnings.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[验证解析器] 失败");
                result.IsValid = false;
                result.Errors = new List<string> { $"验证异常: {ex.Message}" };
                return result;
            }
        }

        /// <summary>
        /// 格式化解析器代码（Python）
        /// </summary>
        [Description("格式化Python解析器脚本。优先使用black，若不可用则尝试autopep8；失败时返回原始代码并给出提示。")]
        public async Task<FormatParserCodeResult> FormatParserCodeAsync(
            [Description("解析器代码")] string code,
            [Description("可选：超时秒数(默认30)")] int timeoutSeconds = 30)
        {
            var result = new FormatParserCodeResult();
            if (string.IsNullOrWhiteSpace(code))
            {
                result.Success = false;
                result.FormattedCode = code;
                result.Errors.Add("代码为空");
                return result;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"parser-format-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var codeFile = Path.Combine(tempDir, "parser.py");
                await File.WriteAllTextAsync(codeFile, code);

                _logger.LogInformation("[代码格式化] 开始格式化，文件: {File}, 超时: {Timeout}s", codeFile, timeoutSeconds);

                // 1) 尝试使用 black（最流行的Python格式化工具）
                _logger.LogInformation("[代码格式化] 尝试使用 black");
                var blackResult = await RunProcessAsync("python", $"-m black --quiet \"{codeFile}\"", tempDir, timeoutSeconds);
                if (blackResult.Success)
                {
                    result.Success = true;
                    result.FormattedCode = await File.ReadAllTextAsync(codeFile);
                    result.Formatter = "black";
                    _logger.LogInformation("[代码格式化] black 格式化成功");
                    return result;
                }
                _logger.LogWarning("[代码格式化] black 格式化失败: {Error}", blackResult.ErrorMessage);

                // 2) 回退到 autopep8
                _logger.LogInformation("[代码格式化] 尝试使用 autopep8");
                var autopep8Result = await RunProcessAsync("python", $"-m autopep8 -i \"{codeFile}\"", tempDir, timeoutSeconds);
                if (autopep8Result.Success)
                {
                    result.Success = true;
                    result.FormattedCode = await File.ReadAllTextAsync(codeFile);
                    result.Formatter = "autopep8";
                    _logger.LogInformation("[代码格式化] autopep8 格式化成功");
                    return result;
                }
                _logger.LogWarning("[代码格式化] autopep8 格式化失败: {Error}", autopep8Result.ErrorMessage);

                // 3) 都失败了，返回原始代码
                _logger.LogWarning("[代码格式化] 所有格式化工具均失败，返回原始代码");
                result.Success = false;
                result.FormattedCode = code;
                result.Errors.Add("格式化失败：black/autopep8 不可用或执行失败");
                if (!string.IsNullOrWhiteSpace(blackResult.ErrorMessage))
                    result.Warnings.Add($"black: {blackResult.ErrorMessage}");
                if (!string.IsNullOrWhiteSpace(autopep8Result.ErrorMessage))
                    result.Warnings.Add($"autopep8: {autopep8Result.ErrorMessage}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[代码格式化] 格式化异常");
                result.Success = false;
                result.FormattedCode = code;
                result.Errors.Add($"格式化异常: {ex.Message}");
                return result;
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        /// <summary>
        /// 沙箱执行解析器
        /// </summary>
        [Description("在隔离环境中执行解析器并返回提取结果")]
        public async Task<ExecuteParserSandboxResult> ExecuteParserInSandboxAsync(
            [Description("解析器代码")] string code,
            [Description("测试文件路径")] string testFilePath,
            [Description("可选：超时秒数(默认30)")] int timeoutSeconds = 30)
        {
            _logger.LogInformation("[沙箱执行] 开始: 文件={File}, 超时={Timeout}s", testFilePath, timeoutSeconds);

            var result = new ExecuteParserSandboxResult
            {
                TestFilePath = testFilePath
            };

            try
            {
                var sandboxResult = await ParserSandboxRunner.RunAsync(code, testFilePath, timeoutSeconds, _logger);
                
                // 记录详细的执行结果
                _logger.LogInformation("[沙箱执行] 完成: 成功={Success}, 耗时={Ms}ms, 错误数={Errors}", 
                    sandboxResult.Success, sandboxResult.ExecutionTimeMs, sandboxResult.Errors?.Count ?? 0);
                
                if (sandboxResult.Success)
                {
                    _logger.LogInformation("[沙箱执行] 提取结果: {Data}", 
                        JsonHelper.SerializeObject(sandboxResult.ExtractedData));
                }
                else
                {
                    _logger.LogWarning("[沙箱执行] 执行失败: {Errors}", 
                        string.Join("; ", sandboxResult.Errors ?? new List<string>()));
                }
                
                return sandboxResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[沙箱执行] 失败");
                result.Success = false;
                result.Errors = new List<string> { $"执行异常: {ex.Message}" };
                return result;
            }
        }

        /// <summary>
        /// 部署解析器到规则集
        /// </summary>
        [Description("将验证通过的解析器存储到AI_PLC_RuleSet并注册工具定义")]
        public async Task<DeployParserResult> DeployParserAsync(
            [Description("解析器代码")] string code,
            [Description("解析器元数据JSON(包含brand/format/version等)")] string metadataJson,
            [Description("测试结果JSON")] string testResultJson)
        {
            _logger.LogInformation("[部署解析器] 开始");

            var result = new DeployParserResult();

            try
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    result.Success = false;
                    result.ErrorMessage = "解析器代码为空";
                    return result;
                }

                var metadata = ParseMetadata(metadataJson);
                var brand = metadata.Brand ?? "Unknown";
                var format = metadata.Format ?? "Unknown";
                var version = metadata.Version ?? "1.0.0";
                var language = metadata.Language ?? DetectLanguage(code);

                var parserName = metadata.ParserName ?? $"{brand}_{format}_Parser_v{version.Replace(".", "_")}";
                var ruleSetId = DateTime.UtcNow.Ticks;

                var outputDir = Path.Combine(AppContext.BaseDirectory, "GeneratedParsers");
                Directory.CreateDirectory(outputDir);

                var extension = language.Equals("Python", StringComparison.OrdinalIgnoreCase) ? "py" : "cs";
                var safeFileName = SanitizeFileName(parserName);
                var codeFilePath = Path.Combine(outputDir, $"{safeFileName}.{extension}");
                await File.WriteAllTextAsync(codeFilePath, code);

                var manifest = new
                {
                    ParserName = parserName,
                    Brand = brand,
                    Format = format,
                    Version = version,
                    Language = language,
                    CodeFilePath = codeFilePath,
                    TestResult = JsonDocument.Parse(string.IsNullOrWhiteSpace(testResultJson) ? "{}" : testResultJson).RootElement,
                    DeployedAtUtc = DateTime.UtcNow
                };

                var manifestPath = Path.Combine(outputDir, $"{safeFileName}.manifest.json");
                await File.WriteAllTextAsync(manifestPath, JsonHelper.SerializeObject(manifest));

                long toolId = 0;
                if (metadata.RegisterTool)
                {
                    var toolEntity = new Tool_DefinitionEntity
                    {
                        ToolName = metadata.ToolName ?? parserName,
                        ToolCode = metadata.ToolCode ?? $"dynamic_parser_{safeFileName.ToLowerInvariant()}",
                        Description = metadata.ToolDescription ?? "动态生成的PLC解析器",
                        ToolType = "Local",
                        LocalAssembly = "HSJM.AI.AppLayer",
                        LocalClass = "HSJM.AI.AppLayer.Agent.Tools.GeneratedParserTool",
                        LocalMethod = "ExecuteAsync",
                        ParametersSchema = "{\"type\":\"object\",\"properties\":{\"parserCodeFilePath\":{\"type\":\"string\"},\"filePath\":{\"type\":\"string\"},\"timeoutSeconds\":{\"type\":\"integer\"}},\"required\":[\"parserCodeFilePath\",\"filePath\"]}",
                        ResponseSchema = "{\"type\":\"object\"}",
                        ErrorHandlingStrategy = "Retry",
                        TimeoutSeconds = 60,
                        AuthType = 0,
                        RequiresConfirmation = false,
                        IsEnabled = true
                    };

                    await _toolDefinitionService.InsertAsync(toolEntity);
                    toolId = toolEntity.Id;
                }

                result.Success = true;
                result.RuleSetId = ruleSetId;
                result.ToolId = toolId;
                result.ParserName = parserName;
                result.Version = version;
                result.ApplicableBrands = new List<string> { brand };
                result.ApplicableFormats = new List<string> { format };
                result.DeploymentTime = DateTime.UtcNow;

                _logger.LogInformation("[部署解析器] 成功: RuleSetId={RuleSetId}, ToolId={ToolId}, Name={Name}",
                    ruleSetId, toolId, parserName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[部署解析器] 失败");
                result.Success = false;
                result.ErrorMessage = $"部署失败: {ex.Message}";
                return result;
            }
        }

        #region 私有辅助方法

        private static string DetectLanguage(string code)
        {
            if (code.Contains("import ") || code.Contains("def "))
                return "Python";
            return "CSharp";
        }

        private static string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        }

        private static (string OutputDir, string FileName) ParseGenerationOptions(string? optionsJson, ExtractStructureInfoResult structureInfo)
        {
            var defaultDir = Path.Combine(AppContext.BaseDirectory, "GeneratedParsers", "GeneratedCandidates");
            var defaultName = $"{structureInfo.PlcBrand}_{structureInfo.FileFormat}_Parser_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

            if (string.IsNullOrWhiteSpace(optionsJson))
            {
                return (defaultDir, defaultName);
            }

            try
            {
                using var doc = JsonDocument.Parse(optionsJson);
                var root = doc.RootElement;

                string? outputDir = null;
                if (root.TryGetProperty("outputDir", out var dirProp) && dirProp.ValueKind == JsonValueKind.String)
                {
                    outputDir = dirProp.GetString();
                }

                string? fileName = null;
                if (root.TryGetProperty("fileName", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    fileName = nameProp.GetString();
                }

                return (
                    string.IsNullOrWhiteSpace(outputDir) ? defaultDir : outputDir!,
                    string.IsNullOrWhiteSpace(fileName) ? defaultName : fileName!
                );
            }
            catch
            {
                return (defaultDir, defaultName);
            }
        }

        private static (string? Brand, string? Format, string? Version, string? Language, string? ParserName, bool RegisterTool, string? ToolName, string? ToolCode, string? ToolDescription) ParseMetadata(string metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson))
                return (null, null, null, null, null, false, null, null, null);

            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                var root = doc.RootElement;

                string? GetString(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                            return value.GetString();
                    }
                    return null;
                }

                var registerTool = false;
                if (root.TryGetProperty("RegisterTool", out var registerValue) && registerValue.ValueKind == JsonValueKind.True)
                    registerTool = true;

                return (
                    GetString("Brand", "brand"),
                    GetString("Format", "format", "FileFormat"),
                    GetString("Version", "version"),
                    GetString("Language", "language"),
                    GetString("ParserName", "parserName"),
                    registerTool,
                    GetString("ToolName", "toolName"),
                    GetString("ToolCode", "toolCode"),
                    GetString("ToolDescription", "toolDescription")
                );
            }
            catch
            {
                return (null, null, null, null, null, false, null, null, null);
            }
        }

        private async Task<SyntaxCheckResult> TryPythonSyntaxCheckAsync(string code, List<string> warnings)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"parser-py-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var codeFile = Path.Combine(tempDir, "parser.py");
                await File.WriteAllTextAsync(codeFile, code);

                _logger.LogInformation("[语法检查] 开始验证Python代码，文件: {File}", codeFile);

                // 增加超时时间到30秒，避免Python启动慢导致超时
                var result = await RunProcessAsync("python", $"-m py_compile \"{codeFile}\"", tempDir, 30);
                
                if (!result.Success)
                {
                    _logger.LogWarning("[语法检查] Python验证失败: {Error}", result.ErrorMessage);
                    return new SyntaxCheckResult(false, new List<string> { result.ErrorMessage ?? "python py_compile 失败" });
                }

                if (!string.IsNullOrWhiteSpace(result.StdErr))
                {
                    warnings.Add(result.StdErr.Trim());
                }

                _logger.LogInformation("[语法检查] Python代码验证通过");
                return new SyntaxCheckResult(true, new List<string>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[语法检查] 验证异常");
                return new SyntaxCheckResult(false, new List<string> { ex.Message });
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, string workingDirectory, int timeoutSeconds)
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
                
                if (process.ExitCode != 0)
                {
                    var errorMsg = !string.IsNullOrWhiteSpace(stdErrResult) 
                        ? stdErrResult 
                        : $"进程退出码: {process.ExitCode}";
                    return new ProcessResult(false, errorMsg, stdOutResult, stdErrResult);
                }

                return new ProcessResult(true, null, stdOutResult, stdErrResult);
            }
            catch (Exception ex)
            {
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

        /// <summary>
        /// 构建解析器生成Prompt
        /// </summary>
        private string BuildParserGenerationPrompt(ExtractStructureInfoResult structureInfo, string targetLanguage)
        {
            var brand = structureInfo.PlcBrand;
            var format = structureInfo.FileFormat;
            var strategy = structureInfo.ParsingStrategy;
            var encoding = structureInfo.Encoding;
            var commentStyle = structureInfo.CommentStyle;
            
            // 序列化复杂对象为JSON字符串
            var blockPatternsJson = JsonHelper.SerializeObject(structureInfo.BlockPatterns);
            var variablePatternsJson = JsonHelper.SerializeObject(structureInfo.VariablePatterns);
            var namespacesJson = JsonHelper.SerializeObject(structureInfo.Namespaces);
            var hierarchyPathsJson = JsonHelper.SerializeObject(structureInfo.HierarchyPaths);
            var codeSamplesJson = JsonHelper.SerializeObject(structureInfo.CodeSamples);
            var structureFeaturesJson = JsonHelper.SerializeObject(structureInfo.StructureFeatures);
            var recommendationsJson = JsonHelper.SerializeObject(structureInfo.ParsingRecommendations);

            return $@"请根据以下PLC文件结构信息，生成一个完整的Python解析器脚本。必须输出Python代码，不要输出C#。

【文件基本信息】
- PLC品牌: {brand}
- 文件格式: {format}
- 解析策略: {strategy}
- 文件编码: {encoding}
- 注释风格: {commentStyle}

【解析策略说明】
{(strategy == "XPath" ? "该文件为XML格式，使用XPath定位元素" : strategy == "Regex" ? "该文件为文本格式，使用正则表达式匹配" : "混合策略")}

{(structureInfo.Namespaces.Any() ? $@"【XML命名空间】
{namespacesJson}

" : "")}{(structureInfo.HierarchyPaths.Any() ? $@"【文件层级结构】
{hierarchyPathsJson}

" : "")}【程序块定位规则】
{blockPatternsJson}

【变量定位规则】
{variablePatternsJson}

{(structureInfo.CodeSamples.Any() ? $@"【真实代码示例】（重要：参考这些实际片段生成解析逻辑）
{codeSamplesJson}

" : "")}【结构特征】
{structureFeaturesJson}

【解析建议】
{recommendationsJson}

【生成要求】
1. 语言: Python
2. 生成Python脚本（单文件），直接可执行
3. 核心方法: parse(file_path: str) -> dict
4. **严格按照解析策略实现**:
    - XPath策略: 使用 xml.etree.ElementTree 或 lxml + XPath
    - Regex策略: 使用正则表达式逐行或全文匹配
    - LineByLine策略: 逐行读取并模式匹配
    - Mixed策略: 结合多种方法
5. **必须参考代码示例**: 如果提供了 CodeSamples，务必根据实际代码结构编写解析逻辑
6. **命名空间处理**: 如果有 Namespaces 字段，XML解析时必须正确处理命名空间
7. **层级结构**: 如果有 HierarchyPaths，按照路径定位元素
8. 提取内容: 程序块列表（名称、类型、起始行）、变量定义（名称、类型、作用域）、调用关系
9. 异常处理: 文件不存在、格式错误、编码问题等
10. 代码质量: 清晰注释、高性能、易维护、符合{targetLanguage}最佳实践

【返回格式】
请直接输出完整代码，用```python包裹。不要添加额外说明。

示例结构:
```python
import re
from typing import Dict, List

def parse(file_path: str) -> Dict:
    # TODO: 实现解析逻辑
    return {{""success"": True, ""blocks"": [], ""variables"": [], ""call_relations"": []}}
```
";
        }

        /// <summary>
        /// 从LLM响应中提取代码
        /// </summary>
        private string ExtractCodeFromResponse(string response, string targetLanguage)
        {
            // 优先提取Python代码块
            var pythonBlockPattern = @"```python\s*\n(.*?)\n```";
            var pythonMatch = System.Text.RegularExpressions.Regex.Match(response, pythonBlockPattern, System.Text.RegularExpressions.RegexOptions.Singleline);
            if (pythonMatch.Success)
            {
                return pythonMatch.Groups[1].Value.Trim();
            }

            // 兜底提取任意代码块
            var codeBlockPattern = @"```(?:csharp|python|cs|py)?\s*\n(.*?)\n```";
            var match = System.Text.RegularExpressions.Regex.Match(response, codeBlockPattern, System.Text.RegularExpressions.RegexOptions.Singleline);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            // 如果没有代码块标记，尝试查找using或import开头的代码
            if (response.Contains("import ") || response.Contains("def "))
            {
                return response.Trim();
            }

            if (targetLanguage.Equals("Python", StringComparison.OrdinalIgnoreCase) && (response.Contains("using System") || response.Contains("namespace")))
            {
                return string.Empty;
            }

            return response;
        }

        private static bool IsCSharpCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            return code.Contains("using System");
        }

        private async Task<IChatClient> ResolveChatClientByRoleCodeAsync(string roleCode)
        {
            var role = await _agentRoleService.GetFirstAsync(r => r.RoleCode == roleCode && r.IsDeleted == false);
            if (role != null && role.ModelID.HasValue && role.ModelID.Value > 0)
            {
                return (await _llmModelService.CreateChatClientByModelIdAsync(role.ModelID.Value)).AsIChatClient();
            }

            var bound = await _llmModelService.RecommendModelAsync();
            return (await _llmModelService.CreateChatClientByModelIdAsync(bound.Id)).AsIChatClient();
        }

       
        #endregion
    }

    #region 返回类型

    public class GenerateParserCodeResult
    {
        public bool Success { get; set; }
        [Description("执行结果说明及下一步建议")]
        public string Message { get; set; } = "";
        public string Language { get; set; } = "Python";
        public string Code { get; set; } = "";
        [Description("生成代码保存路径")]
        public string? CodeFilePath { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class ValidateParserCodeResult
    {
        public bool IsValid { get; set; }
        public string Language { get; set; } = "Python";
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int SyntaxScore { get; set; }
    }

    public class FormatParserCodeResult
    {
        public bool Success { get; set; }
        public string Formatter { get; set; } = "";
        public string FormattedCode { get; set; } = "";
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public class ExecuteParserSandboxResult
    {
        public bool Success { get; set; }
        public string TestFilePath { get; set; } = "";
        public Dictionary<string, object> ExtractedData { get; set; } = new();
        public int ExecutionTimeMs { get; set; }
        public long MemoryUsedBytes { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public class DeployParserResult
    {
        public bool Success { get; set; }
        public long RuleSetId { get; set; }
        public long ToolId { get; set; }
        public string ParserName { get; set; } = "";
        public string Version { get; set; } = "1.0.0";
        public List<string> ApplicableBrands { get; set; } = new();
        public List<string> ApplicableFormats { get; set; } = new();
        public DateTime DeploymentTime { get; set; }
        public string? ErrorMessage { get; set; }
    }

    internal record SyntaxCheckResult(bool Success, List<string> Errors);
    internal record ProcessResult(bool Success, string? ErrorMessage, string StdOut, string StdErr);

    #endregion
}
