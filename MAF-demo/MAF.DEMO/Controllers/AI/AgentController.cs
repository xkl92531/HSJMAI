using MAF.DEMO.AppLayer.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace MAF.DEMO.Controllers.AI
{
    /// <summary>
    /// AI智能体控制器
    /// 基于Microsoft Agent Framework官方API
    /// GitHub: https://github.com/microsoft/agent-framework
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [ApiExplorerSettings(GroupName = "AI")]
    [DisplayName("AI智能体")]
    public class AgentController : ControllerBase
    {
        private readonly AgentDemoApp _agentApp;
        private readonly ILogger<AgentController> _logger;

        public AgentController(AgentDemoApp agentApp, ILogger<AgentController> logger)
        {
            _agentApp = agentApp;
            _logger = logger;
        }

        /// <summary>
        /// 示例1: 简单Agent
        /// 基础的单智能体对话
        /// </summary>
        [HttpPost("simple")]
        [DisplayName("简单Agent")]
        [AllowAnonymous]
        public async Task<ActionResult<AgentResponse>> RunSimpleAgent([FromBody] AgentRequest request)
        {
            try
            {
                var result = await _agentApp.RunSimpleAgentAsync(request.Input);
                return Ok(new AgentResponse { Success = true, Result = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "简单Agent执行失败");
                return Ok(new AgentResponse { Success = false, Error = ex.Message });
            }
        }

        /// <summary>
        /// 示例2: 带工具的Agent（流式输出）
        /// 支持Function Calling的智能体
        /// </summary>
        [HttpPost("with-tools-streaming")]
        [DisplayName("带工具Agent(流式)")]
        [AllowAnonymous]
        public async Task RunAgentWithToolsStreaming([FromBody] AgentRequest request)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            try
            {
                await foreach (var chunk in _agentApp.RunAgentWithToolsStreamingAsync(request.Input))
                {
                    await Response.WriteAsync($"data: {chunk}\n\n");
                    await Response.Body.FlushAsync();
                }
                await Response.WriteAsync("data: [DONE]\n\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "带工具Agent流式执行失败");
                await Response.WriteAsync($"data: {{\"error\":\"{ex.Message}\"}}\n\n");
            }
        }

        /// <summary>
        /// 示例3: 顺序工作流
        /// 多智能体顺序执行: Writer -> Editor
        /// </summary>
        [HttpPost("workflow/sequential")]
        [DisplayName("顺序工作流")]
        [AllowAnonymous]
        public async Task<ActionResult<AgentResponse>> RunSequentialWorkflow([FromBody] AgentRequest request)
        {
            try
            {
                var result = await _agentApp.RunSequentialWorkflowAsync(request.Input);
                return Ok(new AgentResponse { Success = true, Result = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "顺序工作流执行失败");
                return Ok(new AgentResponse { Success = false, Error = ex.Message });
            }
        }

        /// <summary>
        /// 示例4: 并行工作流（流式+工具）
        /// FanOut/FanIn多智能体并行执行，子Agent带工具
        /// </summary>
        [HttpPost("workflow/parallel-streaming")]
        [DisplayName("并行工作流(流式)")]
        [AllowAnonymous]
        public async Task RunParallelWorkflowStreaming([FromBody] AgentRequest request)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            try
            {
                await foreach (var chunk in _agentApp.RunParallelWorkflowStreamingAsync(request.Input))
                {
                    await Response.WriteAsync($"data: {chunk}\n\n");
                    await Response.Body.FlushAsync();
                }
                await Response.WriteAsync("data: [DONE]\n\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "并行工作流流式执行失败");
                await Response.WriteAsync($"data: {{\"error\":\"{ex.Message}\"}}\n\n");
            }
        }

        /// <summary>
        /// 示例5: Planning模式
        /// 先制定计划，再执行，最后审核
        /// </summary>
        [HttpPost("planning")]
        [DisplayName("Planning模式")]
        [AllowAnonymous]
        public async Task<ActionResult<PlanningModeResponse>> RunPlanningMode([FromBody] AgentRequest request)
        {
            try
            {
                var result = await _agentApp.RunPlanningModeAsync(request.Input);
                return Ok(new PlanningModeResponse
                {
                    Success = true,
                    Plan = result.Plan,
                    ExecutionResult = result.ExecutionResult,
                    ReviewResult = result.ReviewResult
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Planning模式执行失败");
                return Ok(new PlanningModeResponse { Success = false, Error = ex.Message });
            }
        }

        /// <summary>
        /// 示例6: ReAct模式
        /// 推理+行动循环: Thought -> Action -> Observation
        /// </summary>
        [HttpPost("react")]
        [DisplayName("ReAct模式")]
        [AllowAnonymous]
        public async Task<ActionResult<ReactModeResponse>> RunReActMode([FromBody] ReactRequest request)
        {
            try
            {
                var result = await _agentApp.RunReActModeAsync(request.Input, request.MaxIterations);
                return Ok(new ReactModeResponse
                {
                    Success = true,
                    Question = result.Question,
                    Iterations = result.Iterations.Select(i => new ReactIterationDto
                    {
                        IterationNumber = i.IterationNumber,
                        Response = i.Response
                    }).ToList(),
                    FinalAnswer = result.FinalAnswer
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReAct模式执行失败");
                return Ok(new ReactModeResponse { Success = false, Error = ex.Message });
            }
        }

        /// <summary>
        /// 示例7: 任务编排系统（流式）
        /// Master分解任务→子Agent协作执行（并行/串行+A2A交互）→Coordinator汇总
        /// </summary>
        [HttpPost("orchestration-streaming")]
        [DisplayName("任务编排系统(流式)")]
        [AllowAnonymous]
        public async Task RunTaskOrchestrationStreaming([FromBody] AgentRequest request)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            try
            {
                await foreach (var chunk in _agentApp.RunTaskOrchestrationAsync(request.Input))
                {
                    await Response.WriteAsync($"data: {chunk}\n\n");
                    await Response.Body.FlushAsync();
                }
                await Response.WriteAsync("data: [DONE]\n\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "任务编排系统执行失败");
                await Response.WriteAsync($"data: {{\"error\":\"{ex.Message}\"}}\n\n");
            }
        }
    }

    #region 请求/响应模型

    /// <summary>
    /// Agent请求
    /// </summary>
    public class AgentRequest
    {
        /// <summary>
        /// 用户输入
        /// </summary>
        [Required(ErrorMessage = "输入内容不能为空")]
        public string Input { get; set; } = string.Empty;
    }

    /// <summary>
    /// ReAct模式请求
    /// </summary>
    public class ReactRequest : AgentRequest
    {
        /// <summary>
        /// 最大迭代次数
        /// </summary>
        public int MaxIterations { get; set; } = 3;
    }

    /// <summary>
    /// Agent响应
    /// </summary>
    public class AgentResponse
    {
        public bool Success { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Planning模式响应
    /// </summary>
    public class PlanningModeResponse
    {
        public bool Success { get; set; }
        public string? Plan { get; set; }
        public string? ExecutionResult { get; set; }
        public string? ReviewResult { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// ReAct模式响应
    /// </summary>
    public class ReactModeResponse
    {
        public bool Success { get; set; }
        public string? Question { get; set; }
        public List<ReactIterationDto>? Iterations { get; set; }
        public string? FinalAnswer { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// ReAct迭代DTO
    /// </summary>
    public class ReactIterationDto
    {
        public int IterationNumber { get; set; }
        public string Response { get; set; } = string.Empty;
    }

    #endregion
}
