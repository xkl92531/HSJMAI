using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// 诊断验证器实现
    /// 负责调用 AI 模型对数据进行诊断验证
    /// </summary>
    public class DiagnosticValidator : IDiagnosticValidator, IScopedAutoInject, ISelfScopedAutoInject
    {
        private readonly ILogger<DiagnosticValidator> _logger;
        // TODO: 注入 AI 服务（如 OpenAI/Azure OpenAI）
        // private readonly IAIService _aiService;

        /// <summary>
        /// 诊断验证器构造函数
        /// </summary>
        /// <param name="logger">日志记录器</param>
        public DiagnosticValidator(ILogger<DiagnosticValidator> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 批量验证诊断数据
        /// 使用 Parallel.ForEachAsync 并发验证，支持自定义最大并发数
        /// </summary>
        /// <param name="dataList">待验证的数据列表</param>
        /// <param name="maxDegreeOfParallelism">最大并发数，默认5</param>
        /// <returns>批量验证结果</returns>
        public async Task<DiagnosticStrategyFactoryResponseDto> ValidateBatchAsync(
            List<string> dataList,
            int maxDegreeOfParallelism = 5)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = new List<ValidationResultDto>();
            var successResults = new List<ValidationResultDto>();
            var failedResults = new List<ValidationResultDto>();
            var notPassedResults = new List<ValidationResultDto>();

            // 使用 Parallel.ForEachAsync 进行并发验证
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism
            };

            await Parallel.ForEachAsync(dataList, options, async (data, cancellationToken) =>
            {
                try
                {
                    var result = await ValidateSingleAsync(data);
                    lock (results)
                    {
                        results.Add(result);

                        if (result.Success)
                        {
                            if (result.Response?.IsValid == true)
                            {
                                successResults.Add(result);
                            }
                            else
                            {
                                notPassedResults.Add(result);
                            }
                        }
                        else
                        {
                            failedResults.Add(result);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "验证数据失败: {Data}", data);
                    lock (results)
                    {
                        failedResults.Add(new ValidationResultDto
                        {
                            FilePath = data,
                            Success = false,
                            ErrorMessage = ex.Message
                        });
                    }
                }
            });

            stopwatch.Stop();

            var totalCount = results.Count;
            var successCount = successResults.Count;
            var failureCount = failedResults.Count;
            var passedCount = successResults.Count(r => r.Response?.IsValid == true);
            var notPassedCount = notPassedResults.Count;

            var successRate = totalCount > 0 ? (double)successCount / totalCount : 0;
            var averageScore = successResults.Any()
                ? successResults.Average(r => r.Response?.Score ?? 0)
                : 0;

            return new DiagnosticStrategyFactoryResponseDto
            {
                TotalCount = totalCount,
                SuccessCount = successCount,
                FailureCount = failureCount,
                PassedCount = passedCount,
                NotPassedCount = notPassedCount,
                SuccessRate = successRate,
                AverageScore = averageScore,
                TotalDurationSeconds = stopwatch.Elapsed.TotalSeconds,
                SuccessResults = successResults,
                FailedResults = failedResults,
                NotPassedResults = notPassedResults
            };
        }

        /// <summary>
        /// 验证单条诊断数据
        /// </summary>
        /// <param name="data">待验证的数据字符串</param>
        /// <returns>验证结果</returns>
        public async Task<ValidationResultDto> ValidateSingleAsync(string data)
        {
            try
            {
                _logger.LogDebug("验证数据: {Data}", data);

                // TODO: 调用 AI 模型进行验证
                // 这里是模拟实现，实际应该调用 OpenAI/Azure OpenAI API
                var aiResponse = await CallAIModelAsync(data);

                return new ValidationResultDto
                {
                    FilePath = data,
                    Response = aiResponse,
                    Success = true,
                    ErrorMessage = null
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证单条数据失败: {Data}", data);

                return new ValidationResultDto
                {
                    FilePath = data,
                    Response = null,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// 调用 AI 模型进行验证
        /// TODO: 实现实际的 AI 调用逻辑
        /// </summary>
        private async Task<AiModelResponse> CallAIModelAsync(string data)
        {
            // 模拟 AI 调用延迟
            await Task.Delay(100);

            // TODO: 实际实现
            // 1. 构建 Prompt（包含数据和验证规则）
            // 2. 调用 OpenAI/Azure OpenAI API
            // 3. 解析 AI 响应
            // 4. 返回结构化结果

            // 示例实现（模拟）
            var prompt = BuildValidationPrompt(data);
            // var rawResponse = await _aiService.ChatCompletionAsync(prompt);
            var rawResponse = $"模拟响应: {data}";

            // 模拟解析
            var isValid = !data.Contains("error", StringComparison.OrdinalIgnoreCase);
            var score = isValid ? 0.95 : 0.3;
            var reason = isValid ? "数据格式正确，所有字段验证通过" : "数据包含错误或异常值";

            return new AiModelResponse(
                IsValid: isValid,
                Score: score,
                Reason: reason,
                Parsed: null,
                RawResponse: rawResponse
            );
        }

        /// <summary>
        /// 构建验证 Prompt
        /// </summary>
        private string BuildValidationPrompt(string data)
        {
            return $@"
请验证以下 PLC 数据的有效性和合法性：

数据: {data}

请从以下方面进行验证：
1. 数据格式是否正确
2. 地址范围是否合法
3. 数据类型是否匹配
4. 是否存在潜在的逻辑错误

返回 JSON 格式：
{{
  ""isValid"": true/false,
  ""score"": 0-1,
  ""reason"": ""验证说明"",
  ""parsed"": {{
    ""projectName"": ""项目名称"",
    ""lineId"": 行号,
    ""crossReferences"": []
  }}
}}
";
        }
    }
}
