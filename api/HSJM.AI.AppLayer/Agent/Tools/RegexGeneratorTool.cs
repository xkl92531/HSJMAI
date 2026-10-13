using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HSJM.AI.AppLayer.Agent.Services;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 正则表达式生成工具
    /// 调用 LLM 生成正则表达式，并提供验证功能
    /// </summary>
    public class RegexGeneratorTool
    {
        private readonly LLMModelService _llmModelService;
        private readonly ILogger<RegexGeneratorTool> _logger;

        public RegexGeneratorTool(
            LLMModelService llmModelService,
            ILogger<RegexGeneratorTool> logger)
        {
            _llmModelService = llmModelService;
            _logger = logger;
        }

        /// <summary>
        /// 生成正则表达式
        /// 调用 LLM 根据自然语言描述生成正则表达式
        /// </summary>
        [Description("根据自然语言描述生成正则表达式，并提供测试样例")]
        public async Task<GenerateRegexResult> GenerateRegexAsync(
            [Description("正则表达式需求描述，如：匹配西门子PLC的FUNCTION_BLOCK定义")] string description,
            [Description("可选：示例文本，用于验证正则")] string? sampleText = null,
            [Description("可选：目标语言，如：C#、JavaScript、Python，默认C#")] string? targetLanguage = null)
        {
            _logger.LogInformation("[正则生成] 开始生成: {Description}", description);

            var result = new GenerateRegexResult
            {
                Description = description,
                SampleText = sampleText
            };

            try
            {
                // 1. 构建 Prompt
                var language = string.IsNullOrWhiteSpace(targetLanguage) ? "C#" : targetLanguage;
                var prompt = BuildGenerateRegexPrompt(description, sampleText, language);

                // 2. 创建临时 Agent 并调用 LLM（参考 AgentRuntimeService 模式）
                var bound = await _llmModelService.RecommendModelAsync();
                var chatClient = (await _llmModelService.CreateChatClientByModelIdAsync(bound.Id)).AsIChatClient();
                var agent = new ChatClientAgent(
                    chatClient,
                    instructions: "你是一个正则表达式专家，请严格按照要求返回JSON格式的结果。",
                    name: "RegexGenerator"
                );

                var response = await agent.RunAsync(prompt);
                var content = response.Text ?? string.Empty;

                _logger.LogInformation("[正则生成] LLM 响应: {Response}", content);

                // 3. 解析 LLM 返回的 JSON
                var llmResult = ParseLlmResponse(content);

                if (llmResult != null)
                {
                    result.Success = true;
                    result.RegexPattern = llmResult.regex_pattern;
                    result.Explanation = llmResult.explanation;
                    result.TestCases = llmResult.test_cases ?? new System.Collections.Generic.List<RegexTestCase>();
                    result.UsageExample = llmResult.usage_example;

                    // 4. 如果提供了示例文本，验证正则
                    if (!string.IsNullOrWhiteSpace(sampleText))
                    {
                        var validationResult = ValidateRegex(result.RegexPattern, sampleText);
                        result.ValidationResult = validationResult;
                    }

                    _logger.LogInformation("[正则生成] 成功生成正则: {Pattern}", result.RegexPattern);
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = "无法解析 LLM 响应，请重试";
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[正则生成] 生成失败");
                result.Success = false;
                result.ErrorMessage = $"生成失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 基于示例生成正则表达式（兼容工具定义）
        /// </summary>
        [Description("基于示例文本生成正则表达式，适用于变量/程序块/指令等模式提取")]
        public async Task<GenerateRegexResult> GenerateRegexFromExamplesAsync(
            [Description("示例文本数组(每条为一行或一个片段)")] System.Collections.Generic.List<string> examples,
            [Description("目标类型：variable/block/instruction/other")] string targetType,
            [Description("可选：配置JSON，如 {\"targetLanguage\":\"C#\"}")] string? options = null)
        {
            var sampleText = examples == null || examples.Count == 0
                ? null
                : string.Join("\n", examples);

            var language = "C#";
            if (!string.IsNullOrWhiteSpace(options))
            {
                try
                {
                    var doc = JsonDocument.Parse(options);
                    if (doc.RootElement.TryGetProperty("targetLanguage", out var langElement))
                    {
                        var langValue = langElement.GetString();
                        if (!string.IsNullOrWhiteSpace(langValue))
                            language = langValue;
                    }
                }
                catch
                {
                    // 忽略配置解析错误，使用默认语言
                }
            }

            var description = $"请根据示例生成匹配{targetType}的正则表达式";
            return await GenerateRegexAsync(description, sampleText, language);
        }

        /// <summary>
        /// 构建生成正则的 Prompt
        /// </summary>
        private string BuildGenerateRegexPrompt(string description, string? sampleText, string language)
        {
            var prompt = $@"你是一个正则表达式专家。请根据以下需求生成正则表达式。

需求描述：
{description}

目标语言：{language}

{(string.IsNullOrWhiteSpace(sampleText) ? "" : $@"
示例文本：
{sampleText}
")}

请以 JSON 格式返回结果，结构如下：
{{
  ""regex_pattern"": ""正则表达式"",
  ""explanation"": ""正则表达式解释，说明每个部分的含义"",
  ""usage_example"": ""在{language}中的使用示例代码"",
  ""test_cases"": [
    {{
      ""input"": ""测试输入1"",
      ""should_match"": true,
      ""expected_groups"": [""捕获组1"", ""捕获组2""]
    }},
    {{
      ""input"": ""测试输入2"",
      ""should_match"": false,
      ""expected_groups"": []
    }}
  ]
}}

注意：
1. 正则表达式要准确、高效
2. 如果有捕获组，请在 explanation 中说明
3. 至少提供 3 个测试用例（包含正匹配和负匹配）
4. usage_example 要完整可运行

请直接返回 JSON，不要包含任何其他文本。";

            return prompt;
        }

        /// <summary>
        /// 解析 LLM 响应
        /// </summary>
        private LlmRegexResponse? ParseLlmResponse(string content)
        {
            try
            {
                // 尝试提取 JSON（LLM 可能包含 ```json 代码块）
                var jsonContent = content.Trim();

                if (jsonContent.Contains("```json"))
                {
                    var startIndex = jsonContent.IndexOf("```json") + 7;
                    var endIndex = jsonContent.LastIndexOf("```");
                    if (endIndex > startIndex)
                    {
                        jsonContent = jsonContent.Substring(startIndex, endIndex - startIndex).Trim();
                    }
                }
                else if (jsonContent.Contains("```"))
                {
                    var startIndex = jsonContent.IndexOf("```") + 3;
                    var endIndex = jsonContent.LastIndexOf("```");
                    if (endIndex > startIndex)
                    {
                        jsonContent = jsonContent.Substring(startIndex, endIndex - startIndex).Trim();
                    }
                }

                return JsonSerializer.Deserialize<LlmRegexResponse>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[正则生成] JSON 解析失败");
                return null;
            }
        }

        /// <summary>
        /// 验证正则表达式
        /// </summary>
        private string ValidateRegex(string pattern, string testText)
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern);
                var matches = regex.Matches(testText);

                if (matches.Count > 0)
                {
                    return $"✅ 正则有效，在示例文本中找到 {matches.Count} 个匹配";
                }
                else
                {
                    return "⚠️ 正则有效，但在示例文本中未找到匹配";
                }
            }
            catch (Exception ex)
            {
                return $"❌ 正则无效: {ex.Message}";
            }
        }
    }

    #region 返回模型

    /// <summary>
    /// 正则生成结果
    /// </summary>
    public class GenerateRegexResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string Description { get; set; } = string.Empty;
        public string? SampleText { get; set; }
        public string RegexPattern { get; set; } = string.Empty;
        public string Explanation { get; set; } = string.Empty;
        public string UsageExample { get; set; } = string.Empty;
        public System.Collections.Generic.List<RegexTestCase> TestCases { get; set; } = new();
        public string? ValidationResult { get; set; }

        public override string ToString()
        {
            if (!Success)
                return $"❌ 生成失败: {ErrorMessage}";

            var result = $@"✅ 正则生成成功

需求: {Description}

正则表达式:
{RegexPattern}

说明:
{Explanation}

使用示例:
{UsageExample}

测试用例: {TestCases.Count} 个
";

            foreach (var testCase in TestCases)
            {
                result += $@"
  - 输入: ""{testCase.Input}""
    预期: {(testCase.ShouldMatch ? "✓ 匹配" : "✗ 不匹配")}
    {(testCase.ExpectedGroups?.Count > 0 ? $"捕获组: [{string.Join(", ", testCase.ExpectedGroups)}]" : "")}";
            }

            if (!string.IsNullOrWhiteSpace(ValidationResult))
            {
                result += $@"

验证结果:
{ValidationResult}";
            }

            return result;
        }
    }

    /// <summary>
    /// 正则测试用例
    /// </summary>
    public class RegexTestCase
    {
        public string Input { get; set; } = string.Empty;
        public bool ShouldMatch { get; set; }
        public System.Collections.Generic.List<string>? ExpectedGroups { get; set; }
    }

    /// <summary>
    /// LLM 返回的正则响应
    /// </summary>
    internal class LlmRegexResponse
    {
        public string regex_pattern { get; set; } = string.Empty;
        public string explanation { get; set; } = string.Empty;
        public string usage_example { get; set; } = string.Empty;
        public System.Collections.Generic.List<RegexTestCase>? test_cases { get; set; }
    }

    #endregion
}
