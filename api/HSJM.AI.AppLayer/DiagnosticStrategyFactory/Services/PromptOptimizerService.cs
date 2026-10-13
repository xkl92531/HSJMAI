using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// 提示词优化服务
    /// 负责使用 AI 优化提示词并验证结果
    /// </summary>
    public class PromptOptimizerService : ISelfScopedAutoInject
    {
        private readonly ILogger<PromptOptimizerService> _logger;
        private readonly AgentRuntimeService _agentRuntimeService;
        private readonly PromptTemplateService _promptTemplateService;

        // 优化使用的 Agent Role ID（复用 DiagnosticAgentRoleId）
        private int OptimizerRoleId => AppsettingsConfig.AIAgent.DiagnosticAgentRoleId;

        /// <summary>
        /// 提示词优化服务构造函数
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="agentRuntimeService">AI Agent运行时服务（调用LLM进行优化）</param>
        /// <param name="promptTemplateService">提示词模板服务（加载和渲染Prompt）</param>
        public PromptOptimizerService(
            ILogger<PromptOptimizerService> logger,
            AgentRuntimeService agentRuntimeService,
            PromptTemplateService promptTemplateService)
        {
            _logger = logger;
            _agentRuntimeService = agentRuntimeService;
            _promptTemplateService = promptTemplateService;
        }

        /// <summary>
        /// 优化提示词
        /// </summary>
        /// <param name="request">优化请求</param>
        /// <returns>优化结果</returns>
        public async Task<PromptOptimizationResultDto> OptimizePromptAsync(PromptOptimizationRequestDto request)
        {
            try
            {
                _logger.LogInformation("开始优化提示词，类型: {TemplateType}", request.TemplateType);

                // 1. 构建优化提示词
                var optimizationPrompt = BuildOptimizationPrompt(request);

                // 2. 调用 AI 进行优化
                var optimizerAgent = await _agentRuntimeService.CreateAgentByRoleAsync(OptimizerRoleId);
                var response = await _agentRuntimeService.RunAgentAsync(optimizerAgent, optimizationPrompt);

                if (!response.Success)
                {
                    _logger.LogError("AI 优化失败: {Error}", response.ErrorMessage);
                    return new PromptOptimizationResultDto
                    {
                        Success = false,
                        ErrorMessage = response.ErrorMessage
                    };
                }

                // 3. 从响应中提取优化后的提示词
                var optimizedPrompt = ExtractOptimizedPrompt(response.Content);
                if (string.IsNullOrEmpty(optimizedPrompt))
                {
                    return new PromptOptimizationResultDto
                    {
                        Success = false,
                        ErrorMessage = "无法从 AI 响应中提取优化后的提示词"
                    };
                }

                // 4. 如果有测试数据，进行验证
                PromptValidationResultDto validationResult = null;
                if (!string.IsNullOrEmpty(request.TestData))
                {
                    validationResult = await ValidatePromptAsync(new PromptValidationRequestDto
                    {
                        Prompt = optimizedPrompt,
                        TestData = request.TestData,
                        ExpectedOutputFormat = request.ExpectedOutputFormat,
                        AIResponse = null
                    });
                }

                _logger.LogInformation("提示词优化完成，验证得分: {Score}", validationResult?.Score ?? 0);

                return new PromptOptimizationResultDto
                {
                    Success = true,
                    OptimizedPrompt = optimizedPrompt,
                    OptimizationExplanation = ExtractOptimizationExplanation(response.Content),
                    ValidationResult = validationResult
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提示词优化异常");
                return new PromptOptimizationResultDto
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// 验证提示词
        /// </summary>
        /// <param name="request">验证请求</param>
        /// <returns>验证结果</returns>
        public async Task<PromptValidationResultDto> ValidatePromptAsync(PromptValidationRequestDto request)
        {
            try
            {
                _logger.LogInformation("开始验证提示词");

                // 1. 先用优化后的提示词执行一次 AI 调用
                string aiResponse;
                if (!string.IsNullOrEmpty(request.AIResponse))
                {
                    // 如果已提供 AI 响应，直接使用
                    aiResponse = request.AIResponse;
                }
                else if (!string.IsNullOrEmpty(request.TestData))
                {
                    // 需要调用 AI 执行测试
                    var testAgent = await _agentRuntimeService.CreateAgentByRoleAsync(OptimizerRoleId);
                    var testRequest = $"{request.Prompt}\n\n## 测试数据\n{request.TestData}";
                    var response = await _agentRuntimeService.RunAgentAsync(testAgent, testRequest);
                    aiResponse = response.Content;
                }
                else
                {
                    return new PromptValidationResultDto
                    {
                        IsValid = false,
                        Score = 0,
                        Evaluation = "缺少测试数据，无法验证"
                    };
                }

                // 2. 构建验证提示词，让 AI 评估结果
                var validationPrompt = BuildValidationPrompt(request, aiResponse);
                var validatorAgent = await _agentRuntimeService.CreateAgentByRoleAsync(OptimizerRoleId);
                var validationResponse = await _agentRuntimeService.RunAgentAsync(validatorAgent, validationPrompt);

                if (!validationResponse.Success)
                {
                    _logger.LogWarning("验证过程失败: {Error}", validationResponse.ErrorMessage);
                    return new PromptValidationResultDto
                    {
                        IsValid = false,
                        Score = 50,
                        Evaluation = $"验证过程出错: {validationResponse.ErrorMessage}"
                    };
                }

                // 3. 解析验证结果
                var result = ParseValidationResult(validationResponse.Content);

                _logger.LogInformation("验证完成，得分: {Score}, 通过: {IsValid}", result.Score, result.IsValid);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提示词验证异常");
                return new PromptValidationResultDto
                {
                    IsValid = false,
                    Score = 0,
                    Evaluation = $"验证异常: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 构建优化提示词
        /// </summary>
        private string BuildOptimizationPrompt(PromptOptimizationRequestDto request)
        {
            var templateName = GetTemplateName(request.TemplateType);

            return $@"
# 提示词优化任务

## 任务描述
你是一个专业的提示词工程专家。请优化以下提示词，使其更准确、更清晰、更易于被 AI 理解和执行。

## 当前提示词
```
{request.OriginalPrompt}
```

## 优化说明（可选）
{request.OptimizationNotes ?? "无"}

## 测试数据（用于验证优化效果）
```
{request.TestData ?? "无"}
```

## 预期输出格式
```
{request.ExpectedOutputFormat ?? "JSON 格式"}
```

## 优化要求

1. **明确角色和任务**：确保 Role 和 Task 描述清晰准确
2. **优化输入格式**：使输入数据的格式更加规范
3. **完善分析规则**：补充遗漏的分析逻辑和边界情况
4. **改进输出要求**：明确输出格式，确保 JSON 结构完整
5. **添加必要约束**：确保 AI 严格遵守给定规则

## 输出格式

请严格按照以下 JSON 格式输出：

```json
{{
  ""optimizedPrompt"": ""优化后的完整提示词内容"",
  ""explanation"": ""优化说明，简要描述做了哪些改进及其原因"",
  ""improvements"": [
    ""改进点1"",
    ""改进点2"",
    ""改进点3""
  ]
}}
```

请立即开始优化。
";
        }

        /// <summary>
        /// 构建验证提示词
        /// </summary>
        private string BuildValidationPrompt(PromptValidationRequestDto request, string aiResponse)
        {
            return $@"
# 提示词验证任务

## 任务描述
请评估以下提示词的质量和 AI 执行结果。

## 待验证的提示词
```
{request.Prompt}
```

## 测试数据
```
{request.TestData}
```

## AI 执行结果
```
{aiResponse}
```

## 预期输出格式
```
{request.ExpectedOutputFormat ?? "JSON 格式"}
```

## 验证标准

请从以下维度评估提示词质量：

1. **准确性**：提示词是否准确描述了任务要求？
2. **完整性**：是否遗漏了重要的规则或约束？
3. **可执行性**：AI 能否正确理解和执行提示词？
4. **输出质量**：AI 输出是否符合预期格式？
5. **清晰度**：描述是否清晰无歧义？

## 输出格式

请严格按照以下 JSON 格式输出：

```json
{{
  ""isValid"": true 或 false,
  ""score"": 0-100 的评分,
  ""issues"": [
    {{
      ""type"": ""问题类型"",
      ""description"": ""问题描述"",
      ""severity"": ""HIGH/MEDIUM/LOW"",
      ""location"": ""问题位置（如适用）""
    }}
  ],
  ""evaluation"": ""总体评估说明"",
  ""suggestedImprovements"": ""建议的改进方向""
}}
```

请立即开始验证。
";
        }

        /// <summary>
        /// 从 AI 响应中提取优化后的提示词
        /// </summary>
        private string ExtractOptimizedPrompt(string content)
        {
            try
            {
                // 尝试解析 JSON
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var node = JsonNode.Parse(jsonContent);

                    if (node?["optimizedPrompt"] != null)
                    {
                        return node["optimizedPrompt"].ToString();
                    }
                }

                // 如果 JSON 解析失败，尝试从文本中提取
                var promptStart = content.IndexOf("""optimizedPrompt""");
                if (promptStart >= 0)
                {
                    var start = content.IndexOf('"', promptStart + 20);
                    var end = content.IndexOf('"', start + 1);
                    if (start >= 0 && end > start)
                    {
                        return content.Substring(start + 1, end - start - 1);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析优化提示词失败");
                return null;
            }
        }

        /// <summary>
        /// 从 AI 响应中提取优化说明
        /// </summary>
        private string ExtractOptimizationExplanation(string content)
        {
            try
            {
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var node = JsonNode.Parse(jsonContent);

                    if (node?["explanation"] != null)
                    {
                        return node["explanation"].ToString();
                    }
                }

                return "优化完成";
            }
            catch
            {
                return "优化完成";
            }
        }

        /// <summary>
        /// 解析验证结果
        /// </summary>
        private PromptValidationResultDto ParseValidationResult(string content)
        {
            try
            {
                var jsonStart = content.IndexOf('{');
                var jsonEnd = content.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonContent = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var node = JsonNode.Parse(jsonContent);

                    var result = new PromptValidationResultDto
                    {
                        IsValid = node?["isValid"]?.GetValue<bool>() ?? false,
                        Score = node?["score"]?.GetValue<int>() ?? 0,
                        Evaluation = node?["evaluation"]?.ToString() ?? "",
                        SuggestedImprovements = node?["suggestedImprovements"]?.ToString() ?? "",
                        Issues = new List<ValidationIssue>()
                    };

                    // 解析问题列表
                    var issuesNode = node?["issues"];
                    if (issuesNode is JsonArray issuesArray)
                    {
                        foreach (var issueNode in issuesArray)
                        {
                            result.Issues.Add(new ValidationIssue
                            {
                                Type = issueNode?["type"]?.ToString() ?? "",
                                Description = issueNode?["description"]?.ToString() ?? "",
                                Severity = issueNode?["severity"]?.ToString() ?? "LOW",
                                Location = issueNode?["location"]?.ToString() ?? ""
                            });
                        }
                    }

                    return result;
                }

                return new PromptValidationResultDto
                {
                    IsValid = false,
                    Score = 0,
                    Evaluation = "无法解析验证结果"
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析验证结果失败");
                return new PromptValidationResultDto
                {
                    IsValid = false,
                    Score = 0,
                    Evaluation = $"解析错误: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 获取模板名称
        /// </summary>
        private string GetTemplateName(PromptTemplateType type)
        {
            return type switch
            {
                PromptTemplateType.EnergyFlowDiagnostic => "能量流诊断",
                PromptTemplateType.QueryStrategy => "查询策略生成",
                PromptTemplateType.CrossReference => "交叉引用分析",
                PromptTemplateType.SummaryReport => "摘要报告生成",
                _ => "未知模板"
            };
        }

        /// <summary>
        /// 优化并保存提示词
        /// </summary>
        public async Task<PromptOptimizationResultDto> OptimizeAndSaveAsync(PromptOptimizationRequestDto request)
        {
            var result = await OptimizePromptAsync(request);

            if (result.Success && !string.IsNullOrEmpty(result.OptimizedPrompt))
            {
                // 保存优化后的提示词
                await _promptTemplateService.SaveOptimizedPromptAsync(
                    request.TemplateType,
                    result.OptimizedPrompt,
                    isOptimized: true);

                _logger.LogInformation("已保存优化后的提示词: {TemplateType}", request.TemplateType);
            }

            return result;
        }

        /// <summary>
        /// 恢复为原始提示词
        /// </summary>
        public async Task<bool> RestoreOriginalAsync(PromptTemplateType templateType)
        {
            try
            {
                // 重新加载原始模板并保存（覆盖优化版本）
                var template = await _promptTemplateService.LoadTemplateAsync(templateType);
                if (template != null)
                {
                    await _promptTemplateService.SaveOptimizedPromptAsync(
                        templateType,
                        template.Content,
                        isOptimized: true);

                    _logger.LogInformation("已恢复原始提示词: {TemplateType}", templateType);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复原始提示词失败");
                return false;
            }
        }
    }
}
