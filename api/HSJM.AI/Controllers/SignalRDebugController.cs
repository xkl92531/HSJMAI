using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using HSJM.AI.AppLayer.Agent.Services;
using Microsoft.Extensions.Logging;
using Hangfire;
using HSJM.AI.Utility;
using HSJM.AI.AppLayer.Hubs;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// SignalR 调试控制器
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class SignalRDebugController : ControllerBase
    {
        private readonly IHubContext<AgentStreamingHub> _hubContext;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<SignalRDebugController> _logger;

        public SignalRDebugController(
            IHubContext<AgentStreamingHub> hubContext,
            IBackgroundJobClient backgroundJobClient,
            ILogger<SignalRDebugController> logger)
        {
            _hubContext = hubContext;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
        }

        /// <summary>
        /// 测试推送消息到指定 Workflow 分组
        /// </summary>
        [HttpPost("test-push")]
        public async Task<IActionResult> TestPush([FromQuery] string workflowId, [FromQuery] string message = "测试消息")
        {
            try
            {
                var groupName = $"Workflow_{workflowId}";
                
                _logger.LogInformation("[调试] 准备推送到分组: {GroupName}", groupName);
                
                await _hubContext.Clients.Group(groupName).SendAsync("WorkflowProgress", new
                {
                    workflowId = workflowId,
                    stage = "TestDebug",
                    message = message,
                    data = new { timestamp = DateTime.Now, test = true },
                    timestamp = DateTime.Now
                });

                _logger.LogInformation("[调试] 推送成功到分组: {GroupName}", groupName);

                return Ok(new
                {
                    success = true,
                    message = $"已推送测试消息到分组 {groupName}",
                    groupName = groupName,
                    workflowId = workflowId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[调试] 推送失败");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// 广播消息到所有连接的客户端
        /// </summary>
        [HttpPost("broadcast")]
        public async Task<IActionResult> Broadcast([FromQuery] string message = "广播测试")
        {
            try
            {
                _logger.LogInformation("[调试] 准备广播消息");
                
                await _hubContext.Clients.All.SendAsync("WorkflowProgress", new
                {
                    workflowId = "broadcast",
                    stage = "Broadcast",
                    message = message,
                    data = new { timestamp = DateTime.Now, broadcast = true },
                    timestamp = DateTime.Now
                });

                _logger.LogInformation("[调试] 广播成功");

                return Ok(new
                {
                    success = true,
                    message = "已广播消息到所有客户端"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[调试] 广播失败");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// 测试通过 Hangfire Job 推送消息（关键测试）
        /// </summary>
        [HttpPost("test-hangfire-push")]
        public IActionResult TestHangfirePush([FromQuery] string workflowId, [FromQuery] string message = "Hangfire测试")
        {
            try
            {
                _logger.LogInformation("[调试-Hangfire] 提交后台任务推送");
                
                var jobId = _backgroundJobClient.Enqueue(() => 
                    TestPushFromHangfireJob(workflowId, message));

                return Ok(new
                {
                    success = true,
                    message = $"已提交Hangfire任务: {jobId}",
                    jobId = jobId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[调试-Hangfire] 提交失败");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Hangfire Job 方法（测试在后台任务中推送 SignalR 消息）
        /// </summary>
        [Hangfire.JobDisplayName("SignalR推送测试 · {0}")]
        public static async Task TestPushFromHangfireJob(string workflowId, string message)
        {
            // 注意：这里无法直接注入依赖，需要从 ServiceProvider 获取
            var hubContext = CommonServiceProvider.ServiceProvider.GetService<IHubContext<AgentStreamingHub>>();
            var logger = CommonServiceProvider.ServiceProvider.GetService<ILogger<SignalRDebugController>>();
            
            logger?.LogInformation("[Hangfire-Job] 开始推送, HubContext是否为null={IsNull}", hubContext == null);
            
            if (hubContext == null)
            {
                logger?.LogError("[Hangfire-Job] HubContext为null，无法推送");
                return;
            }
            
            var groupName = $"Workflow_{workflowId}";
            
            await hubContext.Clients.Group(groupName).SendAsync("WorkflowProgress", new
            {
                workflowId = workflowId,
                stage = "HangfireTest",
                message = $"[来自Hangfire Job] {message}",
                data = new { timestamp = DateTime.Now, fromHangfire = true },
                timestamp = DateTime.Now
            });
            
            logger?.LogInformation("[Hangfire-Job] 推送完成");
        }
    }
}

