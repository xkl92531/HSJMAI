using Hangfire;
using HSJM.AI.AppLayer.Agent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// LLM 模型服务测试控制器
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [ApiExplorerSettings(GroupName = "System")]
    public class LLMModelTestController : ControllerBase
    {
        private readonly LLMModelService _llmModelService;
        private readonly ILogger<LLMModelTestController> _logger;

        public LLMModelTestController(
            LLMModelService llmModelService,
            ILogger<LLMModelTestController> logger)
        {
            _llmModelService = llmModelService;
            _logger = logger;
        }

        /// <summary>
        /// 测试模型推荐功能
        /// </summary>
        /// <param name="modelType">模型类型（默认：Chat）</param>
        /// <param name="contextSize">所需上下文大小（默认：4096）</param>
        /// <param name="prioritizeCost">是否优先成本（默认：true）</param>
        /// <returns></returns>
        [HttpGet("recommend")]
        public async Task<IActionResult> TestRecommend(
            [FromQuery] string modelType = "Chat",
            [FromQuery] int contextSize = 4096,
            [FromQuery] bool prioritizeCost = true)
        {
            try
            {
                var model = await _llmModelService.RecommendModelAsync(modelType, contextSize, prioritizeCost);
                
                if (model == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "未找到符合条件的模型",
                        criteria = new { modelType, contextSize, prioritizeCost }
                    });
                }

                return Ok(new
                {
                    success = true,
                    message = "推荐成功",
                    model = new
                    {
                        model.Id,
                        model.ModelName,
                        model.ModelCode,
                        model.Provider,
                        model.ContextWindow,
                        model.CostPerInputToken,
                        model.CostPerOutputToken,
                        model.FallbackModelID
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测试模型推荐失败");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// 测试健康检查功能
        /// </summary>
        /// <param name="modelId">模型ID</param>
        /// <returns></returns>
        [HttpPost("health-check/{modelId}")]
        public async Task<IActionResult> TestHealthCheck(long modelId)
        {
            try
            {
                var status = await _llmModelService.CheckModelHealthAsync(modelId);

                return Ok(new
                {
                    success = true,
                    modelId = modelId,
                    healthStatus = status,
                    message = $"健康检查完成: {status}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测试健康检查失败: ModelId={ModelId}", modelId);
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// 测试使用统计功能
        /// </summary>
        /// <param name="modelId">模型ID</param>
        /// <param name="inputTokens">输入Token数（默认：1000）</param>
        /// <param name="outputTokens">输出Token数（默认：500）</param>
        /// <returns></returns>
        [HttpPost("usage/{modelId}")]
        public async Task<IActionResult> TestRecordUsage(
            long modelId,
            [FromQuery] int inputTokens = 1000,
            [FromQuery] int outputTokens = 500)
        {
            try
            {
                var duration = TimeSpan.FromSeconds(2.5);
                var cost = await _llmModelService.RecordUsageAsync(modelId, inputTokens, outputTokens, duration);

                return Ok(new
                {
                    success = true,
                    modelId = modelId,
                    usage = new
                    {
                        inputTokens,
                        outputTokens,
                        totalTokens = inputTokens + outputTokens,
                        durationSeconds = duration.TotalSeconds,
                        costUSD = cost
                    },
                    message = $"使用统计记录完成，成本: ${cost:F6}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测试使用统计失败: ModelId={ModelId}", modelId);
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// 测试降级模型功能
        /// </summary>
        /// <param name="modelId">主模型ID</param>
        /// <returns></returns>
        [HttpGet("fallback/{modelId}")]
        public async Task<IActionResult> TestFallback(long modelId)
        {
            try
            {
                var fallbackModel = await _llmModelService.GetFallbackModelAsync(modelId);

                if (fallbackModel == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "未找到可用的降级模型",
                        primaryModelId = modelId
                    });
                }

                return Ok(new
                {
                    success = true,
                    primaryModelId = modelId,
                    fallbackModel = new
                    {
                        fallbackModel.Id,
                        fallbackModel.ModelName,
                        fallbackModel.ModelCode,
                        fallbackModel.Provider,
                        fallbackModel.IsEnabled,
                        fallbackModel.FallbackModelID
                    },
                    message = "找到降级模型"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测试降级模型失败: ModelId={ModelId}", modelId);
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// 综合测试：推荐 → 健康检查 → 使用统计 → 降级
        /// </summary>
        /// <returns></returns>
        [HttpPost("full-test")]
        public async Task<IActionResult> FullTest()
        {
            var results = new Dictionary<string, object>();

            try
            {
                // 1. 测试推荐（成本优先）
                _logger.LogInformation("========== 开始综合测试 ==========");
                
                var recommendedModel = await _llmModelService.RecommendModelAsync("Chat", 4096, true);
                results["recommend"] = recommendedModel != null 
                    ? new { success = true, modelName = recommendedModel.ModelName }
                    : new { success = false, message = "推荐失败" };

                if (recommendedModel != null)
                {
                    // 2. 测试健康检查
                    var healthStatus = await _llmModelService.CheckModelHealthAsync(recommendedModel.Id);
                    results["healthCheck"] = new { success = true, status = healthStatus };

                    // 3. 测试使用统计
                    var cost = await _llmModelService.RecordUsageAsync(
                        recommendedModel.Id, 1000, 500, TimeSpan.FromSeconds(2.5));
                    results["usage"] = new { success = true, cost = $"${cost:F6}" };

                    // 4. 测试降级模型
                    var fallbackModel = await _llmModelService.GetFallbackModelAsync(recommendedModel.Id);
                    results["fallback"] = fallbackModel != null
                        ? new { success = true, fallbackModelName = fallbackModel.ModelName }
                        : new { success = false, message = "无降级模型" };
                }

                _logger.LogInformation("========== 综合测试完成 ==========");

                return Ok(new
                {
                    success = true,
                    message = "综合测试完成",
                    results = results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "综合测试失败");
                results["error"] = ex.Message;
                return StatusCode(500, new { success = false, results = results });
            }
        }

        /// <summary>
        /// 异步测试：提交到 Hangfire 后台执行
        /// </summary>
        /// <returns></returns>
        [HttpPost("async-test")]
        public IActionResult AsyncTest()
        {
            var jobId = BackgroundJob.Enqueue(() => ExecuteBackgroundTest());

            return Ok(new
            {
                success = true,
                message = "异步测试已提交",
                jobId = jobId,
                dashboardUrl = "/hangfire"
            });
        }

        /// <summary>
        /// 后台任务方法（供 Hangfire 调用）
        /// </summary>
        [NonAction]
        [Hangfire.JobDisplayName("LLM后台链路测试")]
        public async Task ExecuteBackgroundTest()
        {
            _logger.LogInformation("========== 后台测试开始 ==========");

            var model = await _llmModelService.RecommendModelAsync("Chat", 16000, true);
            if (model != null)
            {
                _logger.LogInformation("推荐模型: {ModelName}", model.ModelName);
                
                var status = await _llmModelService.CheckModelHealthAsync(model.Id);
                _logger.LogInformation("健康状态: {Status}", status);
                
                var cost = await _llmModelService.RecordUsageAsync(model.Id, 2000, 1000, TimeSpan.FromSeconds(3));
                _logger.LogInformation("成本: ${Cost:F6}", cost);
            }

            _logger.LogInformation("========== 后台测试完成 ==========");
        }
    }
}
