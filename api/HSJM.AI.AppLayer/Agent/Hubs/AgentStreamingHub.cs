using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Hubs
{
    /// <summary>
    /// Agent 流式输出 Hub
    /// 用途：实时推送 Agent 的 token 级输出
    /// </summary>
    public class AgentStreamingHub : Hub
    {
        private readonly ILogger<AgentStreamingHub> _logger;

        public AgentStreamingHub(ILogger<AgentStreamingHub> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 订阅 Agent 执行流
        /// </summary>
        /// <param name="executionId">执行ID</param>
        public async Task SubscribeToAgent(long executionId)
        {
            var groupName = $"agent-{executionId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            _logger.LogInformation("客户端 {ConnectionId} 订阅了 Agent 执行流: {ExecutionId}",
                Context.ConnectionId, executionId);
        }

        /// <summary>
        /// 取消订阅
        /// </summary>
        /// <param name="executionId">执行ID</param>
        public async Task UnsubscribeFromAgent(long executionId)
        {
            var groupName = $"agent-{executionId}";
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
            _logger.LogInformation("客户端 {ConnectionId} 取消订阅 Agent 执行流: {ExecutionId}",
                Context.ConnectionId, executionId);
        }

        /// <summary>
        /// 加入工作流分组（用于实时推送工作流进度）
        /// </summary>
        /// <param name="workflowId">工作流ID（支持字符串格式以避免JS精度丢失）</param>
        public async Task JoinWorkflowGroup(string workflowId)
        {
            var groupName = $"workflow-{workflowId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            _logger.LogInformation("客户端 {ConnectionId} 加入工作流分组: Workflow_{WorkflowId}",
                Context.ConnectionId, workflowId);
        }

        /// <summary>
        /// 离开工作流分组
        /// </summary>
        /// <param name="workflowId">工作流ID（支持字符串格式）</param>
        public async Task LeaveWorkflowGroup(string workflowId)
        {
            var groupName = $"workflow-{workflowId}";  // ✅ 修复：统一格式
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
            _logger.LogInformation("客户端 {ConnectionId} 离开工作流分组: workflow-{WorkflowId}",
                Context.ConnectionId, workflowId);
        }

        /// <summary>
        /// 连接建立时触发
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("客户端连接: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        /// <summary>
        /// 连接断开时触发
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (exception != null)
            {
                _logger.LogWarning(exception, "客户端断开（异常）: {ConnectionId}", Context.ConnectionId);
            }
            else
            {
                _logger.LogInformation("客户端断开: {ConnectionId}", Context.ConnectionId);
            }
            await base.OnDisconnectedAsync(exception);
        }
    }
}
