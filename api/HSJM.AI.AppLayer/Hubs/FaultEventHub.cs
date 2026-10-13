using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Hubs
{
    /// <summary>
    /// 故障事件推送 Hub。
    /// 用途：报警监测 Worker 检测到故障开始/结束时，向订阅方（Web 前端 + 外部 agent「小锦」）实时推送。
    /// 连接: http://<主机>:2405/hubs/fault-event
    /// 订阅: 客户端调用 SubscribeAll() 或 SubscribeProject(projectId) 进入分组。
    /// 注意：SignalR 是在线推送语义，订阅方掉线期间的消息需通过
    ///       GET /PLC_FaultEvent/Pull 按 modifytime 游标补拉（消费端按 eventId+eventType 去重）。
    /// </summary>
    public class FaultEventHub : Hub
    {
        private readonly ILogger<FaultEventHub> _logger;

        public FaultEventHub(ILogger<FaultEventHub> logger)
        {
            _logger = logger;
        }

        public static string BuildProjectGroup(long projectId)
        {
            return $"fault-project-{projectId}";
        }

        public static string BuildAllGroup()
        {
            return "fault-all";
        }

        /// <summary>
        /// 订阅指定项目的故障事件
        /// </summary>
        public async Task SubscribeProject(long projectId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, BuildProjectGroup(projectId));
            _logger.LogInformation("故障事件客户端 {ConnectionId} 订阅项目组: ProjectId={ProjectId}", Context.ConnectionId, projectId);
        }

        public async Task UnsubscribeProject(long projectId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildProjectGroup(projectId));
            _logger.LogInformation("故障事件客户端 {ConnectionId} 取消订阅项目组: ProjectId={ProjectId}", Context.ConnectionId, projectId);
        }

        /// <summary>
        /// 订阅全部项目的故障事件（小锦全量订阅入口）
        /// </summary>
        public async Task SubscribeAll()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, BuildAllGroup());
            _logger.LogInformation("故障事件客户端 {ConnectionId} 订阅全部故障事件", Context.ConnectionId);
        }

        public async Task UnsubscribeAll()
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildAllGroup());
        }

        /// <summary>
        /// 保活/连通性探测
        /// </summary>
        public async Task Ping()
        {
            await Clients.Caller.SendAsync("Pong", DateTime.Now);
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("故障事件客户端连接: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            if (exception != null)
            {
                _logger.LogWarning(exception, "故障事件客户端断开（异常）: {ConnectionId}", Context.ConnectionId);
            }
            else
            {
                _logger.LogInformation("故障事件客户端断开: {ConnectionId}", Context.ConnectionId);
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
