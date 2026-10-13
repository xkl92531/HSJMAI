using System;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// Agent 运行时测试控制器
    /// 用途：测试基于 Microsoft.Agents.AI 的 Agent 创建和执行
    /// </summary>
    [ApiExplorerSettings(GroupName = "AgentTest")]
    [ApiController]
    [Route("api/[controller]")]
    public class AgentRuntimeTestController : ControllerBase
    {
        private readonly AgentRuntimeService _agentRuntimeService;
        private readonly ILogger<AgentRuntimeTestController> _logger;

        public AgentRuntimeTestController(
            AgentRuntimeService agentRuntimeService,
            ILogger<AgentRuntimeTestController> logger)
        {
            _agentRuntimeService = agentRuntimeService;
            _logger = logger;
        }

        /// <summary>
        /// 测试1: 创建简单 Agent
        /// </summary>
        /// <param name="modelId">可选的模型ID，为null则使用默认模型</param>
        [HttpPost("create-simple-agent")]
        public async Task<IActionResult> Test1_CreateSimpleAgent(long? modelId = null)
        {
            try
            {
                var agent = await _agentRuntimeService.CreateSimpleAgentAsync(
                    instructions: "你是一个友好的助手，擅长解答技术问题。用中文回答。",
                    agentName: "TechHelper",
                    modelId: modelId
                );

                return Ok(new
                {
                    success = true,
                    message = "简单 Agent 创建成功",
                    agent = new
                    {
                        name = agent.Name,
                        instructions = agent.Instructions
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建简单 Agent 失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试2: 基于角色创建 Agent
        /// </summary>
        /// <param name="roleId">角色ID</param>
        [HttpPost("create-agent-by-role")]
        public async Task<IActionResult> Test2_CreateAgentByRole(long roleId)
        {
            try
            {
                var agent = await _agentRuntimeService.CreateAgentByRoleAsync(roleId);

                return Ok(new
                {
                    success = true,
                    message = "Agent 创建成功",
                    agent = new
                    {
                        name = agent.Name,
                        instructions = agent.Instructions
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "基于角色创建 Agent 失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试3: 执行 Agent（单轮对话）
        /// </summary>
        [HttpPost("run-agent")]
        public async Task<IActionResult> Test3_RunAgent([FromBody] RunAgentRequest request)
        {
            try
            {
                // 创建 Agent
                var agent = request.RoleId > 0
                    ? await _agentRuntimeService.CreateAgentByRoleAsync(request.RoleId, request.SystemPrompt)
                    : await _agentRuntimeService.CreateSimpleAgentAsync(
                        request.SystemPrompt ?? "你是一个智能助手。用中文回答。",
                        "TestAgent",
                        request.ModelId);

                // 执行推理
                var response = await _agentRuntimeService.RunAgentAsync(agent, request.UserInput);

                return Ok(new
                {
                    success = true,
                    message = "Agent 执行成功",
                    agent = new
                    {
                        name = agent.Name
                    },
                    response = new
                    {
                        content = response.Content,
                        executionTimeMs = response.ExecutionTimeMs,
                        success = response.Success,
                        errorMessage = response.ErrorMessage
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 执行失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试4: 流式执行 Agent
        /// </summary>
        [HttpPost("run-agent-streaming")]
        public async Task<IActionResult> Test4_RunAgentStreaming([FromBody] RunAgentRequest request)
        {
            try
            {
                // 创建 Agent
                var agent = request.RoleId > 0
                    ? await _agentRuntimeService.CreateAgentByRoleAsync(request.RoleId, request.SystemPrompt)
                    : await _agentRuntimeService.CreateSimpleAgentAsync(
                        request.SystemPrompt ?? "你是一个智能助手。用中文回答。",
                        "StreamingAgent",
                        request.ModelId);
                // 生成执行ID
                var executionId = DateTime.Now.Ticks;

                // 流式执行
                var response = await _agentRuntimeService.RunAgentStreamingAsync(
                    agent,
                    request.UserInput,
                    executionId
                );

                return Ok(new
                {
                    success = true,
                    message = "Agent 流式执行完成",
                    executionId = executionId,
                    agent = new
                    {
                        name = agent.Name
                    },
                    response = new
                    {
                        content = response.Content,
                        executionTimeMs = response.ExecutionTimeMs,
                        success = response.Success
                    },
                    tip = $"使用 SignalR 订阅执行流: agent-{executionId}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 流式执行失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试5: 创建带工具的 Agent（TODO）
        /// </summary>
        [HttpPost("create-agent-with-tools")]
        public async Task<IActionResult> Test5_CreateAgentWithTools(long roleId)
        {
            try
            {
                var agent = await _agentRuntimeService.CreateAgentWithToolsAsync(roleId);

                return Ok(new
                {
                    success = true,
                    message = "带工具的 Agent 创建成功（工具加载功能待实现）",
                    agent = new
                    {
                        name = agent.Name,
                        instructions = agent.Instructions
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建带工具的 Agent 失败");
                return StatusCode(500, new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }
    }

    #region 请求模型

    public class RunAgentRequest
    {
        public long RoleId { get; set; }
        public long? ModelId { get; set; }
        public string? SystemPrompt { get; set; }
        public string UserInput { get; set; } = "";
    }

    #endregion
}
