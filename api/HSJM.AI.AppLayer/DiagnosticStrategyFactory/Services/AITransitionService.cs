using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// AI转换条件验证服务实现
    /// 使用AI模型解析和验证任何格式的Transition XML
    /// </summary>
    public class AITransitionService : IAITransitionService, IAutoInject
    {
        private readonly ILogger<AITransitionService> _logger;
        // TODO: 注入实际的AI服务（OpenAI/Azure OpenAI/本地模型）
        // private readonly IAIClient _aiClient;

        /// <summary>
        /// AI转换条件验证服务构造函数
        /// </summary>
        /// <param name="logger">日志记录器</param>
        public AITransitionService(ILogger<AITransitionService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 使用 AI 验证 Transition 条件
        /// 调用 AI 模型解析 XML 并验证条件是否满足
        /// </summary>
        /// <param name="transitionXml">Transition XML 内容</param>
        /// <param name="actualValues">实际变量值字典</param>
        /// <param name="context">可选的上下文信息</param>
        /// <returns>AI 验证结果</returns>
        public async Task<AITransitionValidationResult> ValidateWithAIAsync(
            string transitionXml,
            Dictionary<string, string> actualValues,
            string? context = null)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("开始AI验证Transition条件，变量数: {Count}", actualValues.Count);

                // 构建AI Prompt
                var prompt = BuildAIPrompt(transitionXml, actualValues, context);

                // TODO: 调用实际的AI服务
                // var response = await _aiClient.ChatCompletionAsync(prompt);
                
                // 临时模拟实现
                var response = await SimulateAICallAsync(prompt);

                // 解析AI响应
                var result = ParseAIResponse(response);
                
                stopwatch.Stop();
                result.ProcessingTimeMs = stopwatch.ElapsedMilliseconds;

                _logger.LogInformation("AI验证完成 - 通过: {Passed}, 得分: {Score}, 耗时: {Time}ms",
                    result.IsPassed, result.Score, result.ProcessingTimeMs);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI验证失败");
                stopwatch.Stop();

                return new AITransitionValidationResult
                {
                    Success = false,
                    IsPassed = false,
                    Score = 0,
                    ErrorMessage = ex.Message,
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds
                };
            }
        }

        /// <summary>
        /// 构建AI Prompt
        /// </summary>
        private string BuildAIPrompt(string transitionXml, Dictionary<string, string> actualValues, string? context)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine("# 任务：西门子PLC Transition条件验证");
            sb.AppendLine();
            sb.AppendLine("你是一个专业的PLC工程师，精通西门子TIA Portal编程。");
            sb.AppendLine("请分析以下Transition条件XML，并根据提供的实际变量值，判断该转换条件是否满足。");
            sb.AppendLine();
            
            sb.AppendLine("## Transition XML（西门子TIA Portal格式）：");
            sb.AppendLine("```xml");
            sb.AppendLine(transitionXml);
            sb.AppendLine("```");
            sb.AppendLine();
            
            sb.AppendLine("## 实际变量值：");
            if (actualValues.Any())
            {
                foreach (var kvp in actualValues)
                {
                    sb.AppendLine($"- {kvp.Key} = {kvp.Value}");
                }
            }
            else
            {
                sb.AppendLine("（无实际值提供）");
            }
            sb.AppendLine();

            if (!string.IsNullOrEmpty(context))
            {
                sb.AppendLine("## 额外上下文：");
                sb.AppendLine(context);
                sb.AppendLine();
            }

            sb.AppendLine("## 分析要求：");
            sb.AppendLine("1. 识别XML的格式类型（LAD/FBD/SCL/STL等）");
            sb.AppendLine("2. 提取所有逻辑条件和比较运算");
            sb.AppendLine("3. 识别所有变量引用");
            sb.AppendLine("4. 将实际变量值代入条件");
            sb.AppendLine("5. 计算条件表达式的结果");
            sb.AppendLine("6. 判断转换条件是否满足（true/false）");
            sb.AppendLine("7. 如果XML包含函数块调用，说明函数块的功能");
            sb.AppendLine("8. 如果有缺失的变量值，明确指出");
            sb.AppendLine();
            
            sb.AppendLine("## 返回JSON格式（严格遵守）：");
            sb.AppendLine("```json");
            sb.AppendLine("{");
            sb.AppendLine("  \"isPassed\": true,  // 条件是否通过");
            sb.AppendLine("  \"score\": 0.95,     // 判断置信度(0-1)");
            sb.AppendLine("  \"reason\": \"详细的判断原因\"");
            sb.AppendLine("}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("**重要**：请只返回JSON，不要包含其他文字说明。");

            return sb.ToString();
        }

        /// <summary>
        /// 解析AI响应
        /// </summary>
        private AITransitionValidationResult ParseAIResponse(string response)
        {
            try
            {
                // 提取JSON内容（AI可能返回markdown格式）
                var jsonContent = ExtractJsonFromResponse(response);

                // 解析JSON
                var json = JsonDocument.Parse(jsonContent);
                var root = json.RootElement;

                var isPassed = root.TryGetProperty("isPassed", out var passedElement)
                    ? passedElement.GetBoolean()
                    : false;

                var score = root.TryGetProperty("score", out var scoreElement)
                    ? scoreElement.GetDouble()
                    : 0.5;

                var reason = root.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString()
                    : "AI未提供判断原因";

                return new AITransitionValidationResult
                {
                    Success = true,
                    IsPassed = isPassed,
                    Score = score,
                    Reason = reason,
                    RawResponse = response,
                    ModelUsed = "Simulated" // TODO: 使用实际的模型名称
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "解析AI响应JSON失败: {Response}", response);

                // 尝试从自然语言中提取结果
                return ParseNaturalLanguageResponse(response);
            }
        }

        /// <summary>
        /// 从响应中提取JSON
        /// </summary>
        private string ExtractJsonFromResponse(string response)
        {
            // 移除markdown代码块标记
            var cleaned = response
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            // 查找JSON对象
            var startIndex = cleaned.IndexOf('{');
            var endIndex = cleaned.LastIndexOf('}');

            if (startIndex >= 0 && endIndex > startIndex)
            {
                return cleaned.Substring(startIndex, endIndex - startIndex + 1);
            }

            return cleaned;
        }

        /// <summary>
        /// 从自然语言响应中提取结果
        /// </summary>
        private AITransitionValidationResult ParseNaturalLanguageResponse(string response)
        {
            var lowerResponse = response.ToLower();

            // 启发式判断
            var isPassed = lowerResponse.Contains("通过") ||
                          lowerResponse.Contains("满足") ||
                          lowerResponse.Contains("true") ||
                          lowerResponse.Contains("成功");

            var notPassed = lowerResponse.Contains("不通过") ||
                           lowerResponse.Contains("不满足") ||
                           lowerResponse.Contains("false") ||
                           lowerResponse.Contains("失败");

            if (notPassed)
                isPassed = false;

            return new AITransitionValidationResult
            {
                Success = true,
                IsPassed = isPassed,
                Score = 0.6, // 自然语言解析置信度较低
                Reason = response,
                RawResponse = response,
                ModelUsed = "NaturalLanguageParsing"
            };
        }

        /// <summary>
        /// 模拟AI调用（临时实现）
        /// TODO: 替换为实际的AI服务调用
        /// </summary>
        private async Task<string> SimulateAICallAsync(string prompt)
        {
            // 模拟网络延迟
            await Task.Delay(100);

            // 简单的启发式模拟
            var hasVariables = prompt.Contains("DB") || prompt.Contains("M0");
            var hasValues = prompt.Contains("实际变量值：") && !prompt.Contains("（无实际值提供）");

            if (hasVariables && hasValues)
            {
                // 模拟：随机判断条件是否通过
                var random = new Random();
                var isPassed = random.NextDouble() > 0.3;

                return $@"
```json
{{
  ""isPassed"": {isPassed.ToString().ToLower()},
  ""score"": {(isPassed ? 0.92 : 0.35):F2},
  ""reason"": ""{(isPassed ? "根据XML分析，所有条件都满足。" : "根据XML分析，部分条件不满足。")}变量值已代入计算，逻辑表达式结果为{isPassed}。""
}}
```";
            }
            else
            {
                return @"
```json
{
  ""isPassed"": false,
  ""score"": 0.1,
  ""reason"": ""无法判断：缺少必要的变量定义或实际值。请提供完整的变量值以进行条件判断。""
}
```";
            }
        }

        /// <summary>
        /// 深度分析TransitionInfo的XML结构（使用AI）
        /// </summary>
        public async Task<AIStructureAnalysis> AnalyzeXmlStructureAsync(string transitionXml)
        {
            _logger.LogInformation("[AI结构分析] 开始分析TransitionInfo XML结构");

            try
            {
                var prompt = BuildStructureAnalysisPrompt(transitionXml);

                // TODO: 调用实际的AI服务
                // var response = await _aiClient.ChatCompletionAsync(prompt);

                // 临时模拟实现
                var response = await SimulateStructureAnalysisAsync(transitionXml);

                var analysis = ParseStructureAnalysisResponse(response);

                _logger.LogInformation("[AI结构分析] 分析完成 - 格式: {Format}, 复杂度: {Complexity}",
                    analysis.XmlFormatType, analysis.LogicComplexity);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI结构分析] 分析失败");

                return new AIStructureAnalysis
                {
                    XmlFormatType = "Unknown",
                    LogicComplexity = 0,
                    SemanticDescription = $"分析失败: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 构建结构分析Prompt
        /// </summary>
        private string BuildStructureAnalysisPrompt(string transitionXml)
        {
            var sb = new StringBuilder();

            sb.AppendLine("# 任务：深度分析西门子PLC Transition XML结构");
            sb.AppendLine();
            sb.AppendLine("你是一个PLC专家，精通西门子TIA Portal的各种编程语言。");
            sb.AppendLine("请深度分析以下TransitionInfo XML的结构、语义和逻辑。");
            sb.AppendLine();

            sb.AppendLine("## TransitionInfo XML:");
            sb.AppendLine("```xml");
            sb.AppendLine(transitionXml);
            sb.AppendLine("```");
            sb.AppendLine();

            sb.AppendLine("## 分析要求:");
            sb.AppendLine("1. **格式识别**：判断XML是哪种编程语言格式（LAD梯形图/FBD功能块/SCL结构化文本/STL语句表）");
            sb.AppendLine("2. **逻辑复杂度**：评估逻辑复杂度（1=简单比较，2=多条件AND/OR，3=包含嵌套，4=包含函数块，5=复杂SCL代码）");
            sb.AppendLine("3. **语义理解**：用自然语言描述这个条件的业务含义");
            sb.AppendLine("4. **逻辑表达式**：提取等价的逻辑表达式（如：Temp > 100 AND Pressure BETWEEN [100, 200]）");
            sb.AppendLine("5. **函数块识别**：如果包含FB/FC调用，列出函数块名称、类型和功能");
            sb.AppendLine("6. **期望值推断**：推断每个变量的期望值或范围");
            sb.AppendLine();

            sb.AppendLine("## 返回JSON格式（严格遵守）:");
            sb.AppendLine("```json");
            sb.AppendLine("{");
            sb.AppendLine("  \"xmlFormatType\": \"LAD/FBD/SCL/STL\",");
            sb.AppendLine("  \"logicComplexity\": 1-5,");
            sb.AppendLine("  \"semanticDescription\": \"业务含义的自然语言描述\",");
            sb.AppendLine("  \"logicExpression\": \"等价逻辑表达式\",");
            sb.AppendLine("  \"functionBlocks\": [");
            sb.AppendLine("    {");
            sb.AppendLine("      \"blockName\": \"FB100\",");
            sb.AppendLine("      \"blockType\": \"FB\",");
            sb.AppendLine("      \"description\": \"温度控制器\",");
            sb.AppendLine("      \"inputs\": { \"SetPoint\": \"100\", \"ActualValue\": \"DB1.DBD0\" }");
            sb.AppendLine("    }");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"expectedValueRanges\": {");
            sb.AppendLine("    \"DB1.DBD0\": \">= 100\",");
            sb.AppendLine("    \"M0.0\": \"= true\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("**重要**：请只返回JSON，不要包含其他文字。");

            return sb.ToString();
        }

        /// <summary>
        /// 解析结构分析响应
        /// </summary>
        private AIStructureAnalysis ParseStructureAnalysisResponse(string response)
        {
            try
            {
                var jsonContent = ExtractJsonFromResponse(response);
                var json = JsonDocument.Parse(jsonContent);
                var root = json.RootElement;

                var analysis = new AIStructureAnalysis
                {
                    XmlFormatType = root.TryGetProperty("xmlFormatType", out var formatElement)
                        ? formatElement.GetString()
                        : "Unknown",

                    LogicComplexity = root.TryGetProperty("logicComplexity", out var complexityElement)
                        ? complexityElement.GetInt32()
                        : 0,

                    SemanticDescription = root.TryGetProperty("semanticDescription", out var semanticElement)
                        ? semanticElement.GetString()
                        : null,

                    LogicExpression = root.TryGetProperty("logicExpression", out var expressionElement)
                        ? expressionElement.GetString()
                        : null
                };

                // 解析函数块
                if (root.TryGetProperty("functionBlocks", out var fbsElement) && fbsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fbElement in fbsElement.EnumerateArray())
                    {
                        var fb = new FunctionBlockCall
                        {
                            BlockName = fbElement.TryGetProperty("blockName", out var nameEl) ? nameEl.GetString() ?? "" : "",
                            BlockType = fbElement.TryGetProperty("blockType", out var typeEl) ? typeEl.GetString() ?? "" : "",
                            Description = fbElement.TryGetProperty("description", out var descEl) ? descEl.GetString() : null
                        };

                        if (fbElement.TryGetProperty("inputs", out var inputsEl) && inputsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var input in inputsEl.EnumerateObject())
                            {
                                fb.Inputs[input.Name] = input.Value.GetString() ?? "";
                            }
                        }

                        analysis.FunctionBlocks.Add(fb);
                    }
                }

                // 解析期望值范围
                if (root.TryGetProperty("expectedValueRanges", out var rangesElement) && rangesElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var range in rangesElement.EnumerateObject())
                    {
                        analysis.ExpectedValueRanges[range.Name] = range.Value.GetString() ?? "";
                    }
                }

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AI结构分析] 解析响应失败");

                return new AIStructureAnalysis
                {
                    XmlFormatType = "Unknown",
                    LogicComplexity = 0,
                    SemanticDescription = "解析失败"
                };
            }
        }

        /// <summary>
        /// 模拟结构分析（临时实现）
        /// TODO: 替换为实际的AI服务调用
        /// </summary>
        private async Task<string> SimulateStructureAnalysisAsync(string transitionXml)
        {
            await Task.Delay(50); // 模拟延迟

            // 简单的启发式判断
            string format = "Unknown";
            int complexity = 1;

            if (transitionXml.Contains("<Part") && transitionXml.Contains("Type="))
            {
                format = "LAD/FBD";
                complexity = 2;
            }
            else if (transitionXml.Contains("<Contact") || transitionXml.Contains("<Compare"))
            {
                format = "LAD";
                complexity = 1;
            }
            else if (transitionXml.Contains("<SCL>"))
            {
                format = "SCL";
                complexity = 4;
            }
            else if (transitionXml.Contains("<STL>"))
            {
                format = "STL";
                complexity = 3;
            }

            if (transitionXml.Contains("<Call"))
            {
                complexity = Math.Max(complexity, 4);
            }

            return $@"
```json
{{
  ""xmlFormatType"": ""{format}"",
  ""logicComplexity"": {complexity},
  ""semanticDescription"": ""当温度达到设定值且启动信号为真时，允许转换到下一工步"",
  ""logicExpression"": ""Temperature >= SetPoint AND StartSignal = TRUE"",
  ""functionBlocks"": [],
  ""expectedValueRanges"": {{
    ""DB1.DBD0"": "">= 100"",
    ""M0.0"": ""= true""
  }}
}}
```";
        }
    }
}
