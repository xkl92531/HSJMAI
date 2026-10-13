// Copyright (c) Microsoft. All rights reserved.
// Workflow编排控制器 - 基于官方最佳实践

using System;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MAF.DEMO.AppLayer.AI;
using MAF.DEMO.AppLayer.AI.Models;
using System.Text.Json;

namespace MAF.DEMO.Controllers.AI
{
    /// <summary>
    /// Workflow任务编排控制器
    /// 展示多种官方推荐的编排模式
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [ApiExplorerSettings(GroupName = "AI")]
    [DisplayName("Workflow任务编排")]
    public class WorkflowController : ControllerBase
    {
        private readonly WorkflowOrchestrationApp _workflowApp;
        private readonly ILogger<WorkflowController> _logger;

        public WorkflowController(
            WorkflowOrchestrationApp workflowApp, 
            ILogger<WorkflowController> logger)
        {
            _workflowApp = workflowApp;
            _logger = logger;
        }

        /// <summary>
        /// 模式1: 智能条件路由
        /// Router自动判断需要哪些专家，Workflow自动并行执行并汇总
        /// </summary>
        /// <remarks>
        /// 官方模式: Conditional Routing with Dynamic Agent Selection
        /// 
        /// 示例请求:
        /// ```json
        /// {
        ///   "input": "帮我分析一下：明天北京的天气如何？适合出行吗？如果要出行建议几点出发？",
        ///   "enableCheckpoint": true
        /// }
        /// ```
        /// 
        /// 执行流程:
        /// 1. Router分析请求 → 决定需要WeatherAdvisor + TimeExpert
        /// 2. 两个专家并行执行（自动）
        /// 3. Aggregator自动汇总结果
        /// 4. Coordinator给出最终答案
        /// </remarks>
        [HttpPost("conditional-routing")]
        [DisplayName("条件路由模式")]
        [AllowAnonymous]
        public async Task<ActionResult<WorkflowResult>> RunConditionalRouting([FromBody] WorkflowRequest request)
        {
            try
            {
                _logger.LogInformation("收到条件路由请求: {Input}", request.Input);
                
                var result = await _workflowApp.RunConditionalRoutingAsync(
                    request.Input, 
                    request.WorkflowId,
                    request.EnableCheckpoint);
                
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "条件路由执行失败");
                return Ok(new WorkflowResult
                {
                    Status = "Failed",
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// 模式2: FanOut/FanIn并行处理
        /// 所有专家同时分析，自动聚合结果
        /// </summary>
        /// <remarks>
        /// 官方模式: Concurrent Fan-Out/Fan-In Pattern
        /// 
        /// 示例请求:
        /// ```json
        /// {
        ///   "input": "全面分析：当前时间是多少？北京和上海的天气对比如何？2+3*4等于多少？"
        /// }
        /// ```
        /// 
        /// 执行流程:
        /// 1. Start → 4个专家并行执行（DataAnalyst, TimeExpert, WeatherAdvisor, Developer）
        /// 2. 4个结果自动汇总到Aggregator
        /// 3. Coordinator整合给出完整答案
        /// 
        /// 特点: 最大化并行度，适合需要多方面分析的场景
        /// </remarks>
        [HttpPost("fanout-fanin")]
        [DisplayName("FanOut/FanIn并行模式")]
        [AllowAnonymous]
        public async Task<ActionResult<WorkflowResult>> RunFanOutFanIn([FromBody] WorkflowRequest request)
        {
            try
            {
                var result = await _workflowApp.RunFanOutFanInAsync(
                    request.Input, 
                    request.WorkflowId);
                
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FanOut/FanIn执行失败");
                return Ok(new WorkflowResult
                {
                    Status = "Failed",
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// 模式3: 顺序链（Plan → Execute → Review）
        /// 先规划、再执行、后审核的经典三阶段模式
        /// </summary>
        /// <remarks>
        /// 官方模式: Sequential Chain Pattern
        /// 
        /// 示例请求:
        /// ```json
        /// {
        ///   "input": "帮我制定一个学习Python的计划，并评估这个计划的可行性"
        /// }
        /// ```
        /// 
        /// 执行流程:
        /// 1. Planner制定详细计划
        /// 2. Executor执行计划（可调用工具）
        /// 3. Reviewer审核结果并提出改进建议
        /// 
        /// 特点: 消息自动传递，无需手动拼接上下文
        /// </remarks>
        [HttpPost("sequential-chain")]
        [DisplayName("顺序链模式")]
        [AllowAnonymous]
        public async Task<ActionResult<WorkflowResult>> RunSequentialChain([FromBody] WorkflowRequest request)
        {
            try
            {
                var result = await _workflowApp.RunSequentialChainAsync(
                    request.Input, 
                    request.WorkflowId);
                
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "顺序链执行失败");
                return Ok(new WorkflowResult
                {
                    Status = "Failed",
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// 模式4: 混合编排（智能分析 + 条件路由 + 并行处理）
        /// 最复杂的模式，结合分析器、规划器、专家池
        /// </summary>
        /// <remarks>
        /// 官方模式: Hybrid Orchestration (Conditional + Concurrent)
        /// 
        /// 示例请求:
        /// ```json
        /// {
        ///   "input": "我想开发一个天气预报小程序，需要什么技术栈？帮我规划开发流程并评估时间成本",
        ///   "enableCheckpoint": true
        /// }
        /// ```
        /// 
        /// 执行流程:
        /// 1. Analyzer分析复杂度 → 判断是否需要Planner
        /// 2. 根据分析结果条件路由到Planner（复杂任务）或直接到专家
        /// 3. 推荐的专家并行执行
        /// 4. Coordinator汇总最终答案
        /// 
        /// 特点: 自适应复杂度，智能决策执行路径
        /// </remarks>
        [HttpPost("hybrid-orchestration")]
        [DisplayName("混合编排模式")]
        [AllowAnonymous]
        public async Task<ActionResult<WorkflowResult>> RunHybridOrchestration([FromBody] WorkflowRequest request)
        {
            try
            {
                var result = await _workflowApp.RunHybridOrchestrationAsync(
                    request.Input, 
                    request.WorkflowId,
                    request.EnableCheckpoint);
                
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "混合编排执行失败");
                return Ok(new WorkflowResult
                {
                    Status = "Failed",
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// 模式5: 流式条件路由（实时输出执行进度）
        /// 支持SSE（Server-Sent Events）流式返回
        /// </summary>
        /// <remarks>
        /// 示例请求:
        /// ```json
        /// {
        ///   "input": "分析当前时间、北京天气，并计算 123*456 的结果"
        /// }
        /// ```
        /// 
        /// 返回格式（SSE流）:
        /// ```
        /// data: {"type":"status","content":"[工作流启动] ID: xxx"}
        /// data: {"type":"step","content":"[Step 1] Router分析中..."}
        /// data: {"type":"agent_output","agentName":"TimeExpert","content":"..."}
        /// data: {"type":"final","content":"最终答案"}
        /// ```
        /// </remarks>
        [HttpPost("conditional-routing-streaming")]
        [DisplayName("流式条件路由")]
        [AllowAnonymous]
        public async Task RunConditionalRoutingStreaming([FromBody] WorkflowRequest request)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            try
            {
                await foreach (var chunk in _workflowApp.RunConditionalRoutingStreamingAsync(
                    request.Input, 
                    request.WorkflowId))
                {
                    var json = JsonSerializer.Serialize(chunk);
                    await Response.WriteAsync($"data: {json}\n\n");
                    await Response.Body.FlushAsync();
                }

                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "流式条件路由执行失败");
                var errorChunk = new WorkflowStreamChunk
                {
                    Type = "error",
                    Content = ex.Message
                };
                var json = JsonSerializer.Serialize(errorChunk);
                await Response.WriteAsync($"data: {json}\n\n");
            }
        }

        /// <summary>
        /// 自动模式: 根据输入自动选择最佳Workflow类型
        /// </summary>
        /// <remarks>
        /// 智能分析输入，自动决定使用：
        /// - 简单问题 → ConditionalRouting
        /// - 多维度分析 → FanOutFanIn
        /// - 需要规划执行 → SequentialChain
        /// - 复杂任务 → HybridOrchestration
        /// </remarks>
        [HttpPost("auto")]
        [DisplayName("自动选择模式")]
        [AllowAnonymous]
        public async Task<ActionResult<WorkflowResult>> RunAutoMode([FromBody] WorkflowRequest request)
        {
            try
            {
                // 简单启发式规则（实际可用LLM判断）
                var workflowType = DetermineWorkflowType(request.Input);
                
                _logger.LogInformation("自动选择模式: {WorkflowType}, 输入: {Input}", 
                    workflowType, request.Input);

                return workflowType switch
                {
                    "FanOutFanIn" => await RunFanOutFanIn(request),
                    "SequentialChain" => await RunSequentialChain(request),
                    "HybridOrchestration" => await RunHybridOrchestration(request),
                    _ => await RunConditionalRouting(request)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动模式执行失败");
                return Ok(new WorkflowResult
                {
                    Status = "Failed",
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// 查询Workflow执行状态（用于异步任务）
        /// </summary>
        [HttpGet("status/{workflowId}")]
        [DisplayName("查询Workflow状态")]
        [AllowAnonymous]
        public async Task<ActionResult<WorkflowResult>> GetWorkflowStatus(string workflowId)
        {
            try
            {
                // TODO: 从数据库查询Workflow状态
                // var workflow = await _workflowRepository.GetById(workflowId);
                
                return Ok(new WorkflowResult
                {
                    WorkflowId = workflowId,
                    Status = "Running",
                    FinalOutput = "查询功能待实现"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询Workflow状态失败");
                return NotFound();
            }
        }

        /// <summary>
        /// 恢复Workflow执行（从Checkpoint）
        /// </summary>
        [HttpPost("resume/{workflowId}")]
        [DisplayName("恢复Workflow执行")]
        [AllowAnonymous]
        public async Task<ActionResult<WorkflowResult>> ResumeWorkflow(string workflowId)
        {
            try
            {
                // TODO: 从Checkpoint恢复执行
                // var checkpoint = await _checkpointStore.LoadCheckpointAsync(workflowId);
                
                return Ok(new WorkflowResult
                {
                    WorkflowId = workflowId,
                    Status = "Resumed",
                    FinalOutput = "恢复功能待实现"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复Workflow失败");
                return Ok(new WorkflowResult
                {
                    Status = "Failed",
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// 模式6: 完整任务编排系统（流式输出 + 详细日志）
        /// Master分解 → 动态路由 → A2A交互 → 流式进度 → 持久化就绪
        /// </summary>
        /// <remarks>
        /// 最复杂的编排模式，结合官方Workflow和详细流式日志
        /// 
        /// 示例请求:
        /// ```json
        /// {
        ///   "input": "我需要完成一个复杂项目：1) 分析当前时间和天气是否适合开会 2) 根据天气情况计算出行成本 3) 基于前两步结果制定会议安排方案",
        ///   "enableCheckpoint": true
        /// }
        /// ```
        /// 
        /// 特点:
        /// - Master智能分解任务为多个子任务
        /// - 自动决策并行/串行/混合执行策略
        /// - 支持任务依赖关系（A2A交互）
        /// - 每个阶段详细流式日志（类似示例7的RunTaskOrchestrationAsync）
        /// - 持久化就绪（checkpoint接口）
        /// - 实时观察每个Agent的执行细节
        /// 
        /// 执行流程:
        /// 1. Master Agent分析请求 → 输出JSON执行计划
        /// 2. 解析计划并构建Workflow执行图
        /// 3. 根据executionMode（parallel/sequential/hybrid）执行任务
        /// 4. 每个任务流式输出执行细节（Agent思考过程）
        /// 5. 支持任务间依赖传递（前置任务结果作为上下文）
        /// 6. Coordinator汇总所有任务结果
        /// 7. 输出完整执行摘要（耗时、任务数、成功率等）
        /// 
        /// 返回格式: Server-Sent Events (text/event-stream)
        /// </remarks>
        [HttpPost("complete-orchestration-streaming")]
        [DisplayName("完整任务编排（流式）")]
        [AllowAnonymous]
        public async Task RunCompleteOrchestrationStreaming([FromBody] WorkflowRequest request)
        {
            try
            {
                Response.ContentType = "text/event-stream";
                Response.Headers.Add("Cache-Control", "no-cache");
                Response.Headers.Add("X-Accel-Buffering", "no");

                // JSON序列化选项：不转义中文
                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    PropertyNamingPolicy = null
                };

                await foreach (var chunk in _workflowApp.RunCompleteOrchestrationAsync(
                    request.Input, 
                    request.WorkflowId,
                    request.EnableCheckpoint))
                {
                    var json = JsonSerializer.Serialize(chunk, jsonOptions);
                    await Response.WriteAsync($"data: {json}\n\n");
                    await Response.Body.FlushAsync();
                }

                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "完整任务编排执行失败");
                var errorChunk = new WorkflowStreamChunk
                {
                    Type = "error",
                    Content = $"执行失败: {ex.Message}"
                };
                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var json = JsonSerializer.Serialize(errorChunk, jsonOptions);
                await Response.WriteAsync($"data: {json}\n\n");
            }
        }

        #region 辅助方法

        /// <summary>
        /// 启发式判断Workflow类型
        /// </summary>
        private string DetermineWorkflowType(string input)
        {
            var lower = input.ToLower();

            // 关键词匹配
            if (lower.Contains("全面分析") || lower.Contains("综合分析") || lower.Contains("多角度"))
                return "FanOutFanIn";

            if (lower.Contains("规划") || lower.Contains("计划") || lower.Contains("方案") || lower.Contains("步骤"))
                return "SequentialChain";

            if (lower.Contains("开发") || lower.Contains("系统") || lower.Contains("架构") || 
                (lower.Contains("评估") && input.Length > 50))
                return "HybridOrchestration";

            // 默认条件路由
            return "ConditionalRouting";
        }

        #endregion
    }
}
