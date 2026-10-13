using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Hubs
{
    /// <summary>
    /// 工作流状态推送 Hub
    /// 用途：推送工作流和任务的状态变更、CoT 步骤进度
    /// </summary>
    public class WorkflowHub : Hub
    {
        private readonly ILogger<WorkflowHub> _logger;

        public WorkflowHub(ILogger<WorkflowHub> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 订阅工作流状态
        /// </summary>
        /// <param name="workflowId">工作流ID</param>
        public async Task SubscribeToWorkflow(long workflowId)
        {
            var groupName = $"workflow-{workflowId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            _logger.LogInformation("客户端 {ConnectionId} 订阅了工作流: {WorkflowId}", 
                Context.ConnectionId, workflowId);
        }

        /// <summary>
        /// 取消订阅工作流
        /// </summary>
        /// <param name="workflowId">工作流ID</param>
        public async Task UnsubscribeFromWorkflow(long workflowId)
        {
            var groupName = $"workflow-{workflowId}";
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
            _logger.LogInformation("客户端 {ConnectionId} 取消订阅工作流: {WorkflowId}", 
                Context.ConnectionId, workflowId);
        }

        /// <summary>
        /// 订阅 CoT 步骤进度
        /// </summary>
        /// <param name="taskId">任务ID</param>
        public async Task SubscribeToCoTSteps(long taskId)
        {
            var groupName = $"cot-task-{taskId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            _logger.LogInformation("客户端 {ConnectionId} 订阅了 CoT 步骤: TaskId={TaskId}", 
                Context.ConnectionId, taskId);
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
