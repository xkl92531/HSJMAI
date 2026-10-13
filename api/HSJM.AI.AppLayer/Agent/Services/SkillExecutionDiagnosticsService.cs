using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Tools;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 技能执行诊断服务
    /// 
    /// 职责：
    /// - 验证脚本执行结果（基础验证、格式验证、语义验证）
    /// - 生成诊断报告供 Task 层决策
    /// - 提供重试建议（可恢复错误 vs 致命错误）
    /// 
    /// 设计原则：
    /// - 与 Task 层诊断独立：Skill 诊断关注"执行是否正确"，Task 诊断关注"目标是否达成"
    /// - 可复用性：同一诊断逻辑可用于不同 Task 上下文
    /// </summary>
    public class SkillExecutionDiagnosticsService : ISelfScopedAutoInject
    {
        private readonly ILogger<SkillExecutionDiagnosticsService> _logger;

        public SkillExecutionDiagnosticsService(ILogger<SkillExecutionDiagnosticsService> logger)
        {
            _logger = logger;
        }

        #region 主诊断方法

        /// <summary>
        /// 对脚本执行结果进行诊断
        /// </summary>
        /// <param name="executionResult">脚本执行结果</param>
        /// <param name="expectedOutputSchema">预期输出 JSON Schema（可选）</param>
        /// <param name="validationRules">自定义验证规则（可选）</param>
        public SkillDiagnosticReport Diagnose(
            ScriptExecutionResult executionResult,
            JsonDocument? expectedOutputSchema = null,
            List<ValidationRule>? validationRules = null)
        {
            var report = new SkillDiagnosticReport
            {
                SkillPackagePath = executionResult.SkillPackagePath,
                ScriptName = executionResult.ScriptName,
                ExecutionTimeMs = executionResult.ExecutionTimeMs,
                DiagnosticTimestamp = DateTime.UtcNow
            };

            try
            {
                _logger.LogInformation("[SkillDiagnostics] 开始诊断: {Package}/{Script}",
                    executionResult.SkillPackagePath, executionResult.ScriptName);

                // Step 1: 基础验证（ExitCode、超时、异常）
                var basicResult = PerformBasicValidation(executionResult);
                report.BasicValidation = basicResult;

                if (!basicResult.Passed)
                {
                    // 基础验证失败，确定错误类别和重试建议
                    report.IsHealthy = false;
                    report.Category = DetermineErrorCategory(basicResult);
                    report.Recommendation = DetermineRetryRecommendation(basicResult);
                    report.DiagnosticSummary = basicResult.Message;
                    report.SuggestedNextAction = GenerateSuggestedAction(report);
                    
                    _logger.LogWarning("[SkillDiagnostics] 基础验证失败: {Message}", basicResult.Message);
                    return report;
                }

                // Step 2: 格式验证（JSON Schema 校验）
                if (expectedOutputSchema != null && executionResult.OutputData != null)
                {
                    var formatResult = PerformFormatValidation(executionResult.OutputData, expectedOutputSchema);
                    report.FormatValidation = formatResult;

                    if (!formatResult.Passed)
                    {
                        report.IsHealthy = false;
                        report.Category = ErrorCategory.Warning;
                        report.Recommendation = RetryRecommendation.ShouldPivot;
                        report.DiagnosticSummary = formatResult.Message;
                        report.SuggestedNextAction = "输出格式不符合预期，建议调整脚本或参数后重试";
                        
                        _logger.LogWarning("[SkillDiagnostics] 格式验证失败: {Message}", formatResult.Message);
                        return report;
                    }
                }

                // Step 3: 自定义规则验证
                if (validationRules?.Count > 0)
                {
                    var rulesResult = PerformCustomRulesValidation(executionResult, validationRules);
                    report.CustomRulesValidation = rulesResult;

                    if (!rulesResult.Passed)
                    {
                        report.IsHealthy = false;
                        report.Category = ErrorCategory.Warning;
                        report.Recommendation = RetryRecommendation.ManualReview;
                        report.DiagnosticSummary = rulesResult.Message;
                        report.SuggestedNextAction = "自定义规则验证失败，建议人工检查";
                        
                        _logger.LogWarning("[SkillDiagnostics] 自定义规则验证失败: {Message}", rulesResult.Message);
                        return report;
                    }
                }

                // Step 4: 输出内容分析
                var contentAnalysis = AnalyzeOutputContent(executionResult);
                report.ContentAnalysis = contentAnalysis;

                // 所有验证通过
                report.IsHealthy = true;
                report.Category = ErrorCategory.None;
                report.Recommendation = RetryRecommendation.OK;
                report.DiagnosticSummary = "技能执行成功，输出符合预期";
                report.SuggestedNextAction = "可以继续下一步操作";

                _logger.LogInformation("[SkillDiagnostics] 诊断完成: IsHealthy={IsHealthy}", report.IsHealthy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SkillDiagnostics] 诊断异常");
                report.IsHealthy = false;
                report.Category = ErrorCategory.Fatal;
                report.Recommendation = RetryRecommendation.ManualReview;
                report.DiagnosticSummary = $"诊断过程异常: {ex.Message}";
            }

            return report;
        }

        /// <summary>
        /// 快速诊断（仅基础验证）
        /// </summary>
        public SkillDiagnosticReport QuickDiagnose(ScriptExecutionResult executionResult)
        {
            return Diagnose(executionResult, null, null);
        }

        #endregion

        #region 验证方法

        /// <summary>
        /// 基础验证：ExitCode、超时、异常
        /// </summary>
        private ValidationResult PerformBasicValidation(ScriptExecutionResult result)
        {
            var validation = new ValidationResult { ValidationType = "Basic" };

            // 检查是否成功
            if (!result.Success)
            {
                validation.Passed = false;
                validation.Issues = new List<string>();

                // 分析失败原因
                if (result.ErrorMessage?.Contains("超时") == true || 
                    result.ErrorMessage?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true)
                {
                    validation.FailureReason = "ExecutionTimeout";
                    validation.Message = $"脚本执行超时: {result.ErrorMessage}";
                    validation.Issues.Add("执行超时，可能是脚本逻辑过于复杂或存在死循环");
                }
                else if (result.ExitCode != 0)
                {
                    validation.FailureReason = "NonZeroExitCode";
                    validation.Message = $"脚本退出码非零: {result.ExitCode}";
                    validation.Issues.Add($"退出码: {result.ExitCode}");
                    
                    if (!string.IsNullOrEmpty(result.StdErr))
                    {
                        validation.Issues.Add($"错误输出: {TruncateString(result.StdErr, 500)}");
                    }
                }
                else
                {
                    validation.FailureReason = "UnknownError";
                    validation.Message = result.ErrorMessage ?? "未知执行错误";
                    validation.Issues.Add(result.ErrorMessage ?? "未知错误");
                }

                return validation;
            }

            // 检查是否有 stderr 输出（可能是警告）
            if (!string.IsNullOrWhiteSpace(result.StdErr))
            {
                validation.Passed = true; // 仍然通过，但记录警告
                validation.Message = "执行成功，但存在 stderr 输出";
                validation.Issues = new List<string> { $"警告信息: {TruncateString(result.StdErr, 200)}" };
                return validation;
            }

            // 检查是否有输出
            if (string.IsNullOrWhiteSpace(result.StdOut) && result.OutputData == null)
            {
                validation.Passed = true; // 空输出可能是正常的
                validation.Message = "执行成功，但没有输出";
                validation.Issues = new List<string> { "脚本没有产生任何输出" };
                return validation;
            }

            validation.Passed = true;
            validation.Message = "基础验证通过";
            return validation;
        }

        /// <summary>
        /// 格式验证：JSON Schema 校验
        /// </summary>
        private ValidationResult PerformFormatValidation(
            Dictionary<string, object> outputData,
            JsonDocument expectedSchema)
        {
            var validation = new ValidationResult { ValidationType = "Format" };
            validation.Issues = new List<string>();

            try
            {
                var schemaRoot = expectedSchema.RootElement;

                // 检查必需字段
                if (schemaRoot.TryGetProperty("required", out var requiredFields))
                {
                    foreach (var field in requiredFields.EnumerateArray())
                    {
                        var fieldName = field.GetString();
                        if (fieldName != null && !outputData.ContainsKey(fieldName))
                        {
                            validation.Issues.Add($"缺少必需字段: {fieldName}");
                        }
                    }
                }

                // 检查字段类型
                if (schemaRoot.TryGetProperty("properties", out var properties))
                {
                    foreach (var prop in properties.EnumerateObject())
                    {
                        if (outputData.TryGetValue(prop.Name, out var value))
                        {
                            var expectedType = prop.Value.TryGetProperty("type", out var typeEl)
                                ? typeEl.GetString()
                                : null;

                            if (!ValidateFieldType(value, expectedType))
                            {
                                validation.Issues.Add($"字段 '{prop.Name}' 类型不匹配，期望: {expectedType}");
                            }
                        }
                    }
                }

                validation.Passed = validation.Issues.Count == 0;
                validation.Message = validation.Passed
                    ? "格式验证通过"
                    : $"格式验证失败，发现 {validation.Issues.Count} 个问题";
            }
            catch (Exception ex)
            {
                validation.Passed = false;
                validation.Message = $"格式验证异常: {ex.Message}";
                validation.Issues.Add(ex.Message);
            }

            return validation;
        }

        /// <summary>
        /// 自定义规则验证
        /// </summary>
        private ValidationResult PerformCustomRulesValidation(
            ScriptExecutionResult result,
            List<ValidationRule> rules)
        {
            var validation = new ValidationResult { ValidationType = "CustomRules" };
            validation.Issues = new List<string>();

            foreach (var rule in rules)
            {
                try
                {
                    var passed = rule.Validate(result);
                    if (!passed)
                    {
                        validation.Issues.Add($"规则 '{rule.Name}' 验证失败: {rule.FailureMessage}");
                    }
                }
                catch (Exception ex)
                {
                    validation.Issues.Add($"规则 '{rule.Name}' 执行异常: {ex.Message}");
                }
            }

            validation.Passed = validation.Issues.Count == 0;
            validation.Message = validation.Passed
                ? "所有自定义规则验证通过"
                : $"自定义规则验证失败，{validation.Issues.Count} 个规则未通过";

            return validation;
        }

        /// <summary>
        /// 分析输出内容
        /// </summary>
        private ContentAnalysisResult AnalyzeOutputContent(ScriptExecutionResult result)
        {
            var analysis = new ContentAnalysisResult();

            // 分析 stdout
            if (!string.IsNullOrWhiteSpace(result.StdOut))
            {
                analysis.StdOutLength = result.StdOut.Length;
                analysis.StdOutLineCount = result.StdOut.Split('\n').Length;
                analysis.HasJsonOutput = result.OutputData != null;
                
                // 检测常见模式
                analysis.ContainsErrorKeywords = ContainsErrorKeywords(result.StdOut);
                analysis.ContainsWarningKeywords = ContainsWarningKeywords(result.StdOut);
            }

            // 分析 OutputData
            if (result.OutputData != null)
            {
                analysis.OutputFieldCount = result.OutputData.Count;
                analysis.OutputFields = result.OutputData.Keys.ToList();
            }

            return analysis;
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 确定错误类别
        /// </summary>
        private ErrorCategory DetermineErrorCategory(ValidationResult basicResult)
        {
            if (basicResult.FailureReason == null) return ErrorCategory.None;

            return basicResult.FailureReason switch
            {
                "ExecutionTimeout" => ErrorCategory.Recoverable,
                "NonZeroExitCode" => ErrorCategory.Fatal,
                "UnknownError" => ErrorCategory.Fatal,
                _ => ErrorCategory.Warning
            };
        }

        /// <summary>
        /// 确定重试建议
        /// </summary>
        private RetryRecommendation DetermineRetryRecommendation(ValidationResult basicResult)
        {
            if (basicResult.FailureReason == null) return RetryRecommendation.OK;

            return basicResult.FailureReason switch
            {
                "ExecutionTimeout" => RetryRecommendation.ShouldRetry, // 超时可以重试
                "NonZeroExitCode" => RetryRecommendation.ShouldPivot,  // 脚本错误需要换策略
                "UnknownError" => RetryRecommendation.ManualReview,    // 未知错误需人工
                _ => RetryRecommendation.OK
            };
        }

        /// <summary>
        /// 生成建议的下一步操作
        /// </summary>
        private string GenerateSuggestedAction(SkillDiagnosticReport report)
        {
            return report.Recommendation switch
            {
                RetryRecommendation.ShouldRetry => 
                    $"建议重试：{report.BasicValidation?.FailureReason}，可以调整超时时间或减少输入数据量",
                RetryRecommendation.ShouldPivot => 
                    "建议切换策略：当前脚本执行失败，考虑使用其他技能或工具完成任务",
                RetryRecommendation.ManualReview => 
                    "建议人工介入：出现未预期的错误，需要检查脚本和执行环境",
                _ => "可以继续下一步操作"
            };
        }

        /// <summary>
        /// 验证字段类型
        /// </summary>
        private bool ValidateFieldType(object value, string? expectedType)
        {
            if (expectedType == null) return true;

            return expectedType switch
            {
                "string" => value is string || value is JsonElement je && je.ValueKind == JsonValueKind.String,
                "number" or "integer" => IsNumericType(value),
                "boolean" => value is bool || value is JsonElement jb && jb.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => value is System.Collections.IEnumerable && !(value is string),
                "object" => value is Dictionary<string, object> || value is JsonElement jo && jo.ValueKind == JsonValueKind.Object,
                _ => true
            };
        }

        private bool IsNumericType(object value)
        {
            return value is int or long or float or double or decimal
                || (value is JsonElement je && je.ValueKind == JsonValueKind.Number);
        }

        /// <summary>
        /// 检测错误关键词
        /// </summary>
        private bool ContainsErrorKeywords(string text)
        {
            var errorPatterns = new[] { "error", "exception", "failed", "failure", "fatal", "错误", "失败", "异常" };
            return errorPatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 检测警告关键词
        /// </summary>
        private bool ContainsWarningKeywords(string text)
        {
            var warningPatterns = new[] { "warning", "warn", "deprecated", "警告", "注意" };
            return warningPatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 截断字符串
        /// </summary>
        private string TruncateString(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "...";
        }

        #endregion
    }

    #region Result Models

    /// <summary>
    /// 技能诊断报告
    /// </summary>
    public class SkillDiagnosticReport
    {
        /// <summary>
        /// 技能包路径
        /// </summary>
        public string SkillPackagePath { get; set; } = "";

        /// <summary>
        /// 脚本名称
        /// </summary>
        public string ScriptName { get; set; } = "";

        /// <summary>
        /// 执行耗时（毫秒）
        /// </summary>
        public int ExecutionTimeMs { get; set; }

        /// <summary>
        /// 诊断时间戳
        /// </summary>
        public DateTime DiagnosticTimestamp { get; set; }

        /// <summary>
        /// 技能执行是否健康
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// 错误类别
        /// </summary>
        public ErrorCategory Category { get; set; }

        /// <summary>
        /// 重试建议
        /// </summary>
        public RetryRecommendation Recommendation { get; set; }

        /// <summary>
        /// 诊断摘要
        /// </summary>
        public string DiagnosticSummary { get; set; } = "";

        /// <summary>
        /// 建议的下一步操作
        /// </summary>
        public string SuggestedNextAction { get; set; } = "";

        /// <summary>
        /// 基础验证结果
        /// </summary>
        public ValidationResult? BasicValidation { get; set; }

        /// <summary>
        /// 格式验证结果
        /// </summary>
        public ValidationResult? FormatValidation { get; set; }

        /// <summary>
        /// 自定义规则验证结果
        /// </summary>
        public ValidationResult? CustomRulesValidation { get; set; }

        /// <summary>
        /// 内容分析结果
        /// </summary>
        public ContentAnalysisResult? ContentAnalysis { get; set; }
    }

    /// <summary>
    /// 错误类别
    /// </summary>
    public enum ErrorCategory
    {
        /// <summary>
        /// 无错误
        /// </summary>
        None,

        /// <summary>
        /// 可恢复错误（如超时、网络问题）
        /// </summary>
        Recoverable,

        /// <summary>
        /// 致命错误（如脚本语法错误）
        /// </summary>
        Fatal,

        /// <summary>
        /// 警告（执行成功但输出不完整）
        /// </summary>
        Warning
    }

    /// <summary>
    /// 重试建议
    /// </summary>
    public enum RetryRecommendation
    {
        /// <summary>
        /// 无需重试
        /// </summary>
        OK,

        /// <summary>
        /// 建议重试（相同参数）
        /// </summary>
        ShouldRetry,

        /// <summary>
        /// 建议切换策略
        /// </summary>
        ShouldPivot,

        /// <summary>
        /// 需要人工介入
        /// </summary>
        ManualReview
    }

    /// <summary>
    /// 验证结果
    /// </summary>
    public class ValidationResult
    {
        /// <summary>
        /// 验证类型
        /// </summary>
        public string ValidationType { get; set; } = "";

        /// <summary>
        /// 是否通过
        /// </summary>
        public bool Passed { get; set; }

        /// <summary>
        /// 失败原因代码
        /// </summary>
        public string? FailureReason { get; set; }

        /// <summary>
        /// 消息
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// 发现的问题列表
        /// </summary>
        public List<string>? Issues { get; set; }
    }

    /// <summary>
    /// 内容分析结果
    /// </summary>
    public class ContentAnalysisResult
    {
        /// <summary>
        /// stdout 长度
        /// </summary>
        public int StdOutLength { get; set; }

        /// <summary>
        /// stdout 行数
        /// </summary>
        public int StdOutLineCount { get; set; }

        /// <summary>
        /// 是否有 JSON 输出
        /// </summary>
        public bool HasJsonOutput { get; set; }

        /// <summary>
        /// 输出字段数量
        /// </summary>
        public int OutputFieldCount { get; set; }

        /// <summary>
        /// 输出字段列表
        /// </summary>
        public List<string>? OutputFields { get; set; }

        /// <summary>
        /// 是否包含错误关键词
        /// </summary>
        public bool ContainsErrorKeywords { get; set; }

        /// <summary>
        /// 是否包含警告关键词
        /// </summary>
        public bool ContainsWarningKeywords { get; set; }
    }

    /// <summary>
    /// 自定义验证规则
    /// </summary>
    public class ValidationRule
    {
        /// <summary>
        /// 规则名称
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// 验证失败时的消息
        /// </summary>
        public string FailureMessage { get; set; } = "";

        /// <summary>
        /// 验证函数
        /// </summary>
        public Func<ScriptExecutionResult, bool> Validate { get; set; } = _ => true;
    }

    #endregion
}
