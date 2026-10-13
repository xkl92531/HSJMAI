using HSJM.AI.AppLayer.Agent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// LLM 聊天测试控制器
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [ApiExplorerSettings(GroupName = "System")]
    public class ChatTestController : ControllerBase
    {
        private readonly LLMModelService _llmModelService;
        private readonly ILogger<ChatTestController> _logger;

        public ChatTestController(
            LLMModelService llmModelService,
            ILogger<ChatTestController> logger)
        {
            _llmModelService = llmModelService;
            _logger = logger;
        }

        /// <summary>
        /// 非流式聊天测试
        /// </summary>
        /// <param name="modelId">模型ID（默认：10003 DeepSeek-V3）</param>
        /// <param name="message">用户消息</param>
        /// <param name="temperature">温度（0-2，默认1）</param>
        /// <returns></returns>
        [HttpPost("chat")]
        public async Task<IActionResult> Chat(
            [FromQuery] long modelId = 10003,
            [FromBody] string message = "你好，请介绍一下你自己",
            [FromQuery] float temperature = 1.0f)
        {
            try
            {
                // Step 1: 根据模型ID创建 ChatClient
                var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(modelId);

                // Step 2: 调用聊天
                var systemPrompt = "你是一个工业自动化领域的AI助手，专门解答PLC编程相关问题。";
                var response = await _llmModelService.ChatAsync(
                    chatClient,
                    message,
                    systemPrompt,
                    cancellationToken: HttpContext.RequestAborted
                );

                return Ok(new
                {
                    success = true,
                    modelId = modelId,
                    response = new
                    {
                        content = response.Content,
                        finishReason = response.FinishReason,
                        usage = new
                        {
                            inputTokens = response.InputTokens,
                            outputTokens = response.OutputTokens,
                            totalTokens = response.TotalTokens
                        },
                        durationMs = response.Duration.TotalMilliseconds
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "聊天失败: ModelId={ModelId}", modelId);
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// 流式聊天测试（Server-Sent Events）
        /// </summary>
        /// <param name="modelId">模型ID（默认：10003 DeepSeek-V3）</param>
        /// <param name="message">用户消息</param>
        /// <param name="temperature">温度</param>
        /// <returns></returns>
        [HttpPost("chat-streaming")]
        public async Task ChatStreaming(
            [FromQuery] long modelId = 10003,
            [FromBody] string message = "请用200字介绍一下西门子S7-1200 PLC的特点",
            [FromQuery] float temperature = 1.0f)
        {
            try
            {
                // 设置 SSE 响应头
                Response.ContentType = "text/event-stream";
                Response.Headers.Add("Cache-Control", "no-cache");
                Response.Headers.Add("X-Accel-Buffering", "no");

                // Step 1: 创建 ChatClient
                var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(modelId);

                // Step 2: 流式输出
                var systemPrompt = "你是一个工业自动化领域的AI助手。";
                await foreach (var chunk in _llmModelService.ChatStreamingAsync(
                    chatClient,
                    message,
                    systemPrompt,
                    cancellationToken: HttpContext.RequestAborted))
                {
                    // SSE 格式：data: {content}\n\n
                    await Response.WriteAsync($"data: {chunk}\n\n");
                    await Response.Body.FlushAsync();
                }

                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "流式聊天失败: ModelId={ModelId}", modelId);
                await Response.WriteAsync($"data: {{\"error\":\"{ex.Message}\"}}\n\n");
            }
        }

        /// <summary>
        /// 多轮对话测试
        /// </summary>
        /// <param name="modelId">模型ID</param>
        /// <returns></returns>
        [HttpPost("multi-turn")]
        public async Task<IActionResult> MultiTurnChat([FromQuery] long modelId = 10003)
        {
            try
            {
                var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(modelId);

                var systemPrompt = "你是一个工业自动化专家。已知PLC是可编程逻辑控制器（Programmable Logic Controller）的缩写，是一种专门为工业自动化设计的数字计算机。";
                var userMessage = "PLC有哪些主要品牌？";

                var response = await _llmModelService.ChatAsync(
                    chatClient,
                    userMessage,
                    systemPrompt,
                    cancellationToken: HttpContext.RequestAborted
                );

                return Ok(new
                {
                    success = true,
                    systemPrompt = systemPrompt,
                    question = userMessage,
                    response = response.Content
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "多轮对话失败");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// 温度对比测试（同一个问题不同温度）
        /// </summary>
        /// <param name="modelId">模型ID</param>
        /// <param name="message">测试消息</param>
        /// <returns></returns>
        [HttpPost("temperature-test")]
        public async Task<IActionResult> TemperatureTest(
            [FromQuery] long modelId = 10003,
            [FromBody] string message = "用一句话形容PLC")
        {
            try
            {
                var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(modelId);
                var results = new List<object>();

                // 注意：温度参数当前API不支持，使用默认值
                var response = await _llmModelService.ChatAsync(
                    chatClient,
                    message,
                    "你是一个简洁的AI助手。",
                    cancellationToken: HttpContext.RequestAborted
                );

                results.Add(new
                {
                    temperature = 1.0f,
                    response = response.Content,
                    note = "当前简化实现暂不支持温度参数调整"
                });

                return Ok(new
                {
                    success = true,
                    modelId = modelId,
                    question = message,
                    results = results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "温度测试失败");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }
    }
}
