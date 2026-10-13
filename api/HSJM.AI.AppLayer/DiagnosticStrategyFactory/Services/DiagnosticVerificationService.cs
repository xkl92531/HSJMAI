using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// AI 诊断验证和自主优化服务
    /// 负责验证诊断结果的正确性，并根据验证结果自主优化提示词
    /// </summary>
    public class DiagnosticVerificationService : ISelfScopedAutoInject
    {
        private readonly ILogger<DiagnosticVerificationService> _logger;
        private readonly AgentRuntimeService _agentRuntimeService;
        private readonly PromptTemplateService _promptTemplateService;
        private readonly PromptOptimizerService _promptOptimizerService;

        // 验证使用的 Agent Role ID（复用 DiagnosticAgentRoleId）
        private int VerifierRoleId => AppsettingsConfig.AIAgent.DiagnosticAgentRoleId;

        /// <summary>
        /// 诊断验证服务构造函数
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="agentRuntimeService">AI Agent运行时服务（调用LLM进行验证和优化）</param>
        /// <param name="promptTemplateService">提示词模板服务（加载和渲染Prompt）</param>
        /// <param name="promptOptimizerService">提示词优化服务（优化诊断Prompt）</param>
        public DiagnosticVerificationService(
            ILogger<DiagnosticVerificationService> logger,
            AgentRuntimeService agentRuntimeService,
            PromptTemplateService promptTemplateService,
            PromptOptimizerService promptOptimizerService)
        {
            _logger = logger;
            _agentRuntimeService = agentRuntimeService;
            _promptTemplateService = promptTemplateService;
            _promptOptimizerService = promptOptimizerService;
        }

        /// <summary>
        /// 验证诊断结果的正确性
        /// 调用 AI 对比诊断结果与原始数据，确保诊断逻辑正确
        /// </summary>
        /// <param name="diagnosticResult">AI 诊断结果 JSON 字符串</param>
        /// <param name="rawContent">原始 PLC XML 内容</param>
        /// <param name="uidSymbolMap">UId 与变量名映射 JSON</param>
        /// <param name="liveSignals">实时信号数据 JSON</param>
        /// <returns>验证结果，包含是否有效、置信度、优化建议等</returns>
        public async Task<DiagnosticVerificationResultDto> VerifyDiagnosticResultAsync(
            string diagnosticResult,
            string rawContent,
            string uidSymbolMap,
            string liveSignals)
        {
            try
            {
                _logger.LogInformation("开始验证诊断结果");

                // 构建验证提示词
                var verificationPrompt = BuildVerificationPrompt(diagnosticResult, rawContent, uidSymbolMap, liveSignals);

                // 调用 AI 进行验证
                var verifierAgent = await _agentRuntimeService.CreateAgentByRoleAsync(VerifierRoleId);
                var response = await _agentRuntimeService.RunAgentAsync(verifierAgent, verificationPrompt);

                if (!response.Success)
                {
                    _logger.LogError("AI 验证失败: {Error}", response.ErrorMessage);
                    return new DiagnosticVerificationResultDto
                    {
                        Success = false,
                        IsValid = false,
                        ErrorMessage = response.ErrorMessage
                    };
                }

                // 解析验证结果
                var result = ParseVerificationResult(response.Content);

                _logger.LogInformation("诊断验证完成: IsValid={IsValid}, Score={Score}, SuggestOptimization={SuggestOptimization}",
                    result.IsValid, result.ConfidenceScore, result.SuggestOptimization);

                // 总是进行自我反思和分析
                _logger.LogInformation("开始 AI 自我反思分析...");

                var reflectionResult = await PerformSelfReflectionAsync(
                    result, diagnosticResult, rawContent, uidSymbolMap, liveSignals);

                if (reflectionResult.NeedsOptimization)
                {
                    _logger.LogWarning("AI 反思发现提示词问题: {Reason}，开始自主优化...", reflectionResult.Reason);

                    var optimizationResult = await OptimizePromptBasedOnVerificationAsync(
                        result, rawContent, uidSymbolMap, liveSignals, reflectionResult.ImprovementSuggestions);

                    result.OptimizationResult = optimizationResult;
                    result.ImprovementSuggestions = reflectionResult.ImprovementSuggestions;
                }
                else
                {
                    _logger.LogInformation("AI 反思完成，判断正确，无需优化");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "诊断验证异常");
                return new DiagnosticVerificationResultDto
                {
                    Success = false,
                    IsValid = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// AI 自我反思分析
        /// 让 AI 再次审视自己的诊断结果，判断是否需要优化提示词
        /// </summary>
        /// <param name="verification">初始验证结果</param>
        /// <param name="diagnosticResult">AI 诊断结果 JSON</param>
        /// <param name="rawContent">原始 PLC XML 内容</param>
        /// <param name="uidSymbolMap">UId 与变量名映射 JSON</param>
        /// <param name="liveSignals">实时信号数据 JSON</param>
        /// <returns>反思结果，包含是否需要优化及原因</returns>
        private async Task<SelfReflectionResult> PerformSelfReflectionAsync(
            DiagnosticVerificationResultDto verification,
            string diagnosticResult,
            string rawContent,
            string uidSymbolMap,
            string liveSignals)
        {
            try
            {
                var reflectionPrompt = BuildSelfReflectionPrompt(verification, diagnosticResult, rawContent, uidSymbolMap, liveSignals);

                var reflectionAgent = await _agentRuntimeService.CreateAgentByRoleAsync(VerifierRoleId);
                var response = await _agentRuntimeService.RunAgentAsync(reflectionAgent, reflectionPrompt);

                if (!response.Success)
                {
                    return new SelfReflectionResult { NeedsOptimization = false, Reason = "反思分析调用失败" };
                }

                return ParseSelfReflectionResult(response.Content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 反思分析异常");
                return new SelfReflectionResult { NeedsOptimization = false, Reason = $"异常: {ex.Message}" };
            }
        }

        private string BuildSelfReflectionPrompt(
            DiagnosticVerificationResultDto verification,
            string diagnosticResult,
            string rawContent,
            string uidSymbolMap,
            string liveSignals)
        {
            var validationStatus = verification.IsValid ? "通过" : "未通过";
            var issuesDescription = verification.Issues?.Any() == true
                ? string.Join("\n", verification.Issues.Select(i => $"- {i.Description}"))
                : "无";

            return $@"
# AI 诊断结果自我反思分析

## 你的诊断结果
{diagnosticResult}

## 诊断验证结果
- 验证状态: {validationStatus}
- 置信度评分: {verification.ConfidenceScore}/100
- 验证反馈: {verification.Feedback}
- 发现问题:
{issuesDescription}

## 反思任务
请分析你自己的诊断判断是否正确，以及如何改进。

## 输出要求
```json
{{
  ""isDiagnosisCorrect"": true/false,
  ""correctnessReason"": ""判断正确/错误的详细理由"",
  ""problemRootCause"": ""A/B/C/D"",
  ""problemRootCauseDescription"": ""问题根因的详细描述"",
  ""needsOptimization"": true/false,
  ""improvementSuggestions"": ""具体的提示词改进建议""
}}
```
";
        }

        private SelfReflectionResult ParseSelfReflectionResult(string content)
        {
            try
            {
                var jsonMatch = Regex.Match(content, @"\{[\s\S]*\}", RegexOptions.Multiline);
                if (jsonMatch.Success)
                {
                    var json = jsonMatch.Value;
                    return new SelfReflectionResult
                    {
                        NeedsOptimization = json.Contains("\"needsOptimization\":true", StringComparison.OrdinalIgnoreCase),
                        Reason = ExtractJsonValue(json, "problemRootCauseDescription") ?? ExtractJsonValue(json, "problemRootCause") ?? "无",
                        Analysis = ExtractJsonValue(json, "correctnessReason") ?? "无分析",
                        ImprovementSuggestions = ExtractJsonValue(json, "improvementSuggestions") ?? ""
                    };
                }
            }
            catch { }

            var lowerContent = content.ToLower();
            return new SelfReflectionResult
            {
                NeedsOptimization = lowerContent.Contains("需要优化"),
                Reason = "解析失败，使用关键词判断",
                Analysis = content.Length > 100 ? content.Substring(0, 100) : content,
                ImprovementSuggestions = ""
            };
        }

        private static string ExtractJsonValue(string json, string key)
        {
            var pattern = $@"""{key}"":\s*""([^""]+)""";
            var match = Regex.Match(json, pattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// 根据验证结果自主优化提示词
        /// 调用提示词优化服务，根据 AI 反思结果改进 Prompt
        /// </summary>
        /// <param name="verification">验证结果，包含问题分析</param>
        /// <param name="rawContent">原始 PLC XML 内容</param>
        /// <param name="uidSymbolMap">UId 与变量名映射 JSON</param>
        /// <param name="liveSignals">实时信号数据 JSON</param>
        /// <param name="improvementSuggestions">AI 反思提出的改进建议</param>
        /// <returns>优化后的提示词结果</returns>
        private async Task<PromptOptimizationResultDto> OptimizePromptBasedOnVerificationAsync(
            DiagnosticVerificationResultDto verification,
            string rawContent,
            string uidSymbolMap,
            string liveSignals,
            string improvementSuggestions = null)
        {
            try
            {
                var currentPrompt = await _promptTemplateService.RenderTemplateAsync(
                    PromptTemplateType.EnergyFlowDiagnostic,
                    new Dictionary<string, string>
                    {
                        { "RAW_CONTENT", rawContent },
                        { "UID_SYMBOL_MAP", uidSymbolMap },
                        { "LIVE_SIGNALS", liveSignals }
                    });

                var optimizationNotes = $@"
验证失败原因：{verification.FailureReasons.FirstOrDefault() ?? "未知"}
验证问题详情：
{string.Join("\n", verification.Issues.Select(i => $"- {i.Description}"))}

AI 自我反思分析：
{improvementSuggestions ?? verification.Feedback}
";

                var result = await _promptOptimizerService.OptimizeAndSaveAsync(new PromptOptimizationRequestDto
                {
                    TemplateType = PromptTemplateType.EnergyFlowDiagnostic,
                    OriginalPrompt = currentPrompt,
                    OptimizationNotes = optimizationNotes,
                    TestData = rawContent + "\n" + uidSymbolMap,
                    ExpectedOutputFormat = "JSON 数组格式"
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提示词自主优化异常");
                return null;
            }
        }

        private string BuildVerificationPrompt(string diagnosticResult, string rawContent, string uidSymbolMap, string liveSignals)
        {
            return $@"
# 诊断结果验证任务

## 原始数据
### PLC 逻辑表达式 (XML)
```xml
{rawContent}
```

### 变量映射 (JSON)
```json
{uidSymbolMap}
```

### 实时信号 (JSON)
```json
{liveSignals}
```

## 待验证的诊断结果
```json
{diagnosticResult}
```

## 验证标准
1. 逻辑正确性：诊断出的异常点是否确实导致能量流阻断？
2. 完整性：是否遗漏了其他可能导致阻断的点？
3. 根因分析：诊断出的根本原因是否准确？

## 输出格式
```json
{{
  ""isValid"": true/false,
  ""confidenceScore"": 0-100,
  ""issues"": [{{ ""type"": """", ""description"": """", ""severity"": ""HIGH/MEDIUM/LOW"" }}],
  ""failureReasons"": [],
  ""feedback"": """",
  ""suggestOptimization"": true/false
}}
```
";
        }

        private DiagnosticVerificationResultDto ParseVerificationResult(string content)
        {
            try
            {
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var node = JsonNode.Parse(jsonContent);

                    var result = new DiagnosticVerificationResultDto
                    {
                        Success = true,
                        IsValid = node?["isValid"]?.GetValue<bool>() ?? false,
                        ConfidenceScore = node?["confidenceScore"]?.GetValue<int>() ?? 0,
                        Feedback = node?["feedback"]?.ToString() ?? "",
                        SuggestOptimization = node?["suggestOptimization"]?.GetValue<bool>() ?? false,
                        FailureReasons = new List<string>(),
                        Issues = new List<VerificationIssue>()
                    };

                    var reasonsNode = node?["failureReasons"];
                    if (reasonsNode is JsonArray reasonsArray)
                    {
                        foreach (var reason in reasonsArray)
                        {
                            result.FailureReasons.Add(reason?.ToString() ?? "");
                        }
                    }

                    var issuesNode = node?["issues"];
                    if (issuesNode is JsonArray issuesArray)
                    {
                        foreach (var issueNode in issuesArray)
                        {
                            result.Issues.Add(new VerificationIssue
                            {
                                Type = issueNode?["type"]?.ToString() ?? "",
                                Description = issueNode?["description"]?.ToString() ?? "",
                                Severity = issueNode?["severity"]?.ToString() ?? "LOW"
                            });
                        }
                    }

                    return result;
                }

                return new DiagnosticVerificationResultDto { Success = true, IsValid = false };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析验证结果失败");
                return new DiagnosticVerificationResultDto { Success = false, IsValid = false };
            }
        }

        /// <summary>
        /// 验证交叉引用分析结果
        /// 验证交叉引用分析是否正确覆盖了所有相关逻辑单元
        /// </summary>
        /// <param name="crossReferenceResult">交叉引用分析结果 JSON</param>
        /// <param name="originalIssues">原始诊断问题列表</param>
        /// <param name="relatedData">相关的 LogicUnit 数据</param>
        /// <returns>交叉引用验证结果</returns>
        public async Task<CrossReferenceVerificationResultDto> VerifyCrossReferenceAsync(
            string crossReferenceResult,
            List<DiagnosticIssue> originalIssues,
            RelatedLogicData relatedData)
        {
            try
            {
                _logger.LogInformation("开始验证交叉引用分析结果");

                var issuesJson = JsonSerializer.Serialize(originalIssues);

                var prompt = $@"
# 交叉引用分析验证

## 原始诊断问题
```json
{issuesJson}
```

## 交叉引用分析结果
```json
{crossReferenceResult}
```

## 验证要点
1. 依赖链完整性
2. 根因判定准确性
3. 优先级合理性

## 输出
```json
{{
  ""isValid"": true/false,
  ""confidenceScore"": 0-100,
  ""issues"": [],
  ""feedback"": """",
  ""suggestOptimization"": true/false
}}
```
";

                var verifierAgent = await _agentRuntimeService.CreateAgentByRoleAsync(VerifierRoleId);
                var response = await _agentRuntimeService.RunAgentAsync(verifierAgent, prompt);

                if (!response.Success)
                {
                    return new CrossReferenceVerificationResultDto { Success = false, IsValid = false };
                }

                return ParseCrossReferenceVerificationResult(response.Content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "交叉引用验证异常");
                return new CrossReferenceVerificationResultDto { Success = false, IsValid = false };
            }
        }

        private CrossReferenceVerificationResultDto ParseCrossReferenceVerificationResult(string content)
        {
            try
            {
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var node = JsonNode.Parse(json);
                    return new CrossReferenceVerificationResultDto
                    {
                        Success = true,
                        IsValid = node?["isValid"]?.GetValue<bool>() ?? false,
                        ConfidenceScore = node?["confidenceScore"]?.GetValue<int>() ?? 0,
                        Feedback = node?["feedback"]?.ToString() ?? "",
                        SuggestOptimization = node?["suggestOptimization"]?.GetValue<bool>() ?? false,
                        Issues = new List<VerificationIssue>()
                    };
                }
            }
            catch { }
            return new CrossReferenceVerificationResultDto { Success = true, IsValid = false };
        }

        /// <summary>
        /// 在诊断完成后自动验证并优化
        /// 整合诊断结果验证、交叉引用验证，并在需要时自动优化提示词
        /// </summary>
        /// <param name="diagnosticResult">AI 诊断结果 JSON</param>
        /// <param name="crossReferenceResult">交叉引用分析结果 JSON</param>
        /// <param name="rawContent">原始 PLC XML 内容</param>
        /// <param name="uidSymbolMap">UId 与变量名映射 JSON</param>
        /// <param name="liveSignals">实时信号数据 JSON</param>
        /// <param name="originalIssues">原始诊断问题列表</param>
        /// <param name="relatedData">相关的 LogicUnit 数据</param>
        /// <returns>是否执行了优化操作</returns>
        public async Task<bool> AutoVerifyAndOptimizeAsync(
            string diagnosticResult,
            string crossReferenceResult,
            string rawContent,
            string uidSymbolMap,
            string liveSignals,
            List<DiagnosticIssue> originalIssues,
            RelatedLogicData relatedData)
        {
            try
            {
                _logger.LogInformation("开始自动验证和优化流程");

                // 1. 验证诊断结果
                var diagnosticVerification = await VerifyDiagnosticResultAsync(
                    diagnosticResult, rawContent, uidSymbolMap, liveSignals);

                // 2. 验证交叉引用结果
                var crossRefVerification = await VerifyCrossReferenceAsync(
                    crossReferenceResult, originalIssues, relatedData);

                // 3. 判断是否需要优化
                bool needsOptimization = !diagnosticVerification.IsValid || !crossRefVerification.IsValid
                    || !string.IsNullOrEmpty(diagnosticVerification.ImprovementSuggestions)
                    || !string.IsNullOrEmpty(crossRefVerification.ImprovementSuggestions);

                if (needsOptimization)
                {
                    _logger.LogWarning("验证不通过或 AI 反思建议优化");
                }
                else
                {
                    _logger.LogInformation("验证通过，诊断结果正确");
                }

                return needsOptimization;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动验证和优化异常");
                return false;
            }
        }

        #region AI 验证步骤1-4

        /// <summary>
        /// 快速检查结果（步骤1-4的AI验证结果）
        /// </summary>
        public class QuickCheckVerificationResult
        {
            public bool Success { get; set; }
            public bool IsComplete { get; set; }
            public int SafetyIssuesFound { get; set; }
            public int ConstraintIssuesFound { get; set; }
            public int IoTagIssuesFound { get; set; }
            public int StuckPointsFound { get; set; }
            public string AiAnalysis { get; set; }
            public List<string> MissingDataWarnings { get; set; }
            public List<string> ImprovementSuggestions { get; set; }
        }

        /// <summary>
        /// AI 验证步骤1-4的快速检查结果
        /// 验证安全检查、约束检查、IO点位检查、卡滞点定位是否完整准确
        /// </summary>
        /// <param name="safetyIssues">安全检查发现的问题列表</param>
        /// <param name="constraintIssues">约束检查发现的问题列表</param>
        /// <param name="ioTagIssues">IO点位检查发现的问题列表</param>
        /// <param name="stuckPoints">卡滞点列表</param>
        /// <param name="liveSignals">实时信号 JSON 字符串</param>
        /// <param name="ioTagsJson">IO点位数据 JSON</param>
        /// <param name="ioConstraintsJson">约束规则数据 JSON</param>
        /// <returns>快速检查验证结果</returns>
        public async Task<QuickCheckVerificationResult> VerifyQuickCheckResultsAsync(
            List<DiagnosticIssue> safetyIssues,
            List<DiagnosticIssue> constraintIssues,
            List<DiagnosticIssue> ioTagIssues,
            List<StuckPoint> stuckPoints,
            string liveSignals,
            string ioTagsJson,
            string ioConstraintsJson)
        {
            try
            {
                _logger.LogInformation("开始AI验证步骤1-4的快速检查结果");

                var safetyJson = JsonSerializer.Serialize(safetyIssues);
                var constraintJson = JsonSerializer.Serialize(constraintIssues);
                var ioTagJson = JsonSerializer.Serialize(ioTagIssues);
                var stuckPointsJson = JsonSerializer.Serialize(stuckPoints);

                var prompt = $@"
# PLC 快速检查结果 AI 验证任务

## 任务
验证步骤1-4的快速检查结果是否完整准确。

## 实时信号数据
```json
{liveSignals}
```

## 步骤1: 安全检查结果
```json
{safetyJson}
```

## 步骤2: 约束检查结果
```json
{constraintJson}
```

## 步骤3: IO点位检查结果
```json
{ioTagJson}
```

## 步骤4: 卡滞点定位结果
```json
{stuckPointsJson}
```

## IO点位数据（参考）
```json
{ioTagsJson}
```

## IO约束规则（参考）
```json
{ioConstraintsJson}
```

## 验证任务
请作为PLC诊断专家，验证以上检查结果是否完整准确：
1. 是否有遗漏的安全问题？
2. 是否有遗漏的约束违反？
3. 是否有遗漏的IO点位异常？
4. 卡滞点定位是否准确？

## 输出要求
```json
{{
  ""isComplete"": true/false,
  ""safetyIssuesFound"": 数字,
  ""constraintIssuesFound"": 数字,
  ""ioTagIssuesFound"": 数字,
  ""stuckPointsFound"": 数字,
  ""aiAnalysis"": ""AI分析"",
  ""missingDataWarnings"": [],
  ""improvementSuggestions"": []
}}
```
";

                var verifierAgent = await _agentRuntimeService.CreateAgentByRoleAsync(VerifierRoleId);
                var response = await _agentRuntimeService.RunAgentAsync(verifierAgent, prompt);

                if (!response.Success)
                {
                    _logger.LogError("AI快速检查验证失败: {Error}", response.ErrorMessage);
                    return new QuickCheckVerificationResult
                    {
                        Success = false,
                        IsComplete = false,
                        AiAnalysis = $"验证调用失败: {response.ErrorMessage}"
                    };
                }

                var result = ParseQuickCheckVerificationResult(response.Content);

                _logger.LogInformation("AI快速检查验证完成: Safety={Safety}, Constraint={Constraint}, IoTag={IoTag}, Stuck={Stuck}",
                    result.SafetyIssuesFound, result.ConstraintIssuesFound,
                    result.IoTagIssuesFound, result.StuckPointsFound);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI快速检查验证异常");
                return new QuickCheckVerificationResult
                {
                    Success = false,
                    IsComplete = false,
                    AiAnalysis = $"验证异常: {ex.Message}"
                };
            }
        }

        private QuickCheckVerificationResult ParseQuickCheckVerificationResult(string content)
        {
            try
            {
                var jsonMatch = Regex.Match(content, @"\{[\s\S]*\}", RegexOptions.Multiline);
                if (jsonMatch.Success)
                {
                    var json = jsonMatch.Value;
                    var node = JsonNode.Parse(json);

                    return new QuickCheckVerificationResult
                    {
                        Success = true,
                        IsComplete = node?["isComplete"]?.GetValue<bool>() ?? true,
                        SafetyIssuesFound = node?["safetyIssuesFound"]?.GetValue<int>() ?? 0,
                        ConstraintIssuesFound = node?["constraintIssuesFound"]?.GetValue<int>() ?? 0,
                        IoTagIssuesFound = node?["ioTagIssuesFound"]?.GetValue<int>() ?? 0,
                        StuckPointsFound = node?["stuckPointsFound"]?.GetValue<int>() ?? 0,
                        AiAnalysis = node?["aiAnalysis"]?.ToString() ?? "",
                        MissingDataWarnings = ParseStringList(node?["missingDataWarnings"]),
                        ImprovementSuggestions = ParseStringList(node?["improvementSuggestions"])
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析快速检查验证结果失败");
            }

            var lower = content.ToLower();
            return new QuickCheckVerificationResult
            {
                Success = true,
                IsComplete = !lower.Contains("不完整"),
                AiAnalysis = content.Length > 500 ? content.Substring(0, 500) : content
            };
        }

        private List<string> ParseStringList(JsonNode node)
        {
            var result = new List<string>();
            if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    result.Add(item?.ToString() ?? "");
                }
            }
            return result;
        }

        /// <summary>
        /// AI 辅助快速检查
        /// 使用 AI 直接参与判断，补充规则检查可能遗漏的问题
        /// </summary>
        /// <param name="checkType">检查类型（如 safety/constraint/io_tag）</param>
        /// <param name="existingIssues">规则检查已发现的问题列表</param>
        /// <param name="liveSignals">实时信号 JSON 字符串</param>
        /// <param name="referenceData">参考数据（如 IO点位或约束规则 JSON）</param>
        /// <returns>AI 补充发现的问题列表</returns>
        public async Task<List<DiagnosticIssue>> AiAssistQuickCheckAsync(
            string checkType,
            List<DiagnosticIssue> existingIssues,
            string liveSignals,
            string referenceData)
        {
            try
            {
                _logger.LogInformation("开始AI辅助{Type}检查", checkType);

                var prompt = $@"
# AI 辅助{checkType}检查

## 现有规则检查结果
```json
{JsonSerializer.Serialize(existingIssues)}
```

## 实时信号
```json
{liveSignals}
```

## 参考数据
```json
{referenceData}
```

## 任务
请分析是否有遗漏的问题或误报。

## 输出
```json
{{
  ""isComplete"": true/false,
  ""additionalIssues"": [{{ ""variable"": """", ""status"": """", ""reason"": """", ""severity"": """", ""category"": ""{checkType}"" }}],
  ""analysis"": """"
}}
```
";

                var verifierAgent = await _agentRuntimeService.CreateAgentByRoleAsync(VerifierRoleId);
                var response = await _agentRuntimeService.RunAgentAsync(verifierAgent, prompt);

                if (!response.Success)
                {
                    _logger.LogWarning("AI辅助检查失败: {Error}", response.ErrorMessage);
                    return existingIssues;
                }

                var additionalIssues = ParseAiAdditionalIssues(response.Content, checkType);
                if (additionalIssues.Any())
                {
                    _logger.LogInformation("AI辅助检查发现额外问题: {Count} 个", additionalIssues.Count);
                    return existingIssues.Concat(additionalIssues).ToList();
                }

                return existingIssues;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI辅助检查异常");
                return existingIssues;
            }
        }

        private List<DiagnosticIssue> ParseAiAdditionalIssues(string content, string category)
        {
            var issues = new List<DiagnosticIssue>();
            try
            {
                var jsonMatch = Regex.Match(content, @"""additionalIssues"":\s*\[[\s\S]*\]", RegexOptions.Multiline);
                if (jsonMatch.Success)
                {
                    var json = $"{{{jsonMatch.Value}}}";
                    var node = JsonNode.Parse(json);
                    var array = node?["additionalIssues"] as JsonArray;
                    if (array != null)
                    {
                        foreach (var item in array)
                        {
                            issues.Add(new DiagnosticIssue
                            {
                                Variable = item?["variable"]?.ToString() ?? "",
                                Status = item?["status"]?.ToString() ?? "",
                                Reason = item?["reason"]?.ToString() ?? "",
                                Severity = item?["severity"]?.ToString() ?? "WARNING",
                                Category = category
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析AI额外问题失败");
            }
            return issues;
        }

        #endregion
    }

    #region DTOs

    public class DiagnosticVerificationResultDto
    {
        public bool Success { get; set; }
        public bool IsValid { get; set; }
        public int ConfidenceScore { get; set; }
        public string Feedback { get; set; }
        public bool SuggestOptimization { get; set; }
        public List<string> FailureReasons { get; set; }
        public List<VerificationIssue> Issues { get; set; }
        public string ErrorMessage { get; set; }
        public PromptOptimizationResultDto OptimizationResult { get; set; }
        public string ImprovementSuggestions { get; set; }
    }

    public class SelfReflectionResult
    {
        public bool NeedsOptimization { get; set; }
        public string Reason { get; set; }
        public string Analysis { get; set; }
        public string ImprovementSuggestions { get; set; }
    }

    public class CrossReferenceVerificationResultDto
    {
        public bool Success { get; set; }
        public bool IsValid { get; set; }
        public int ConfidenceScore { get; set; }
        public string Feedback { get; set; }
        public bool SuggestOptimization { get; set; }
        public List<VerificationIssue> Issues { get; set; }
        public string ErrorMessage { get; set; }
        public string ImprovementSuggestions { get; set; }
    }

    public class VerificationIssue
    {
        public string Type { get; set; }
        public string Description { get; set; }
        public string Severity { get; set; }
        public string Location { get; set; }
        public string Suggestion { get; set; }
    }

    #endregion
}
