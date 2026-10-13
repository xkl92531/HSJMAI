using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Hubs
{
    public class StationShadowMonitoringHub : Hub
    {
        private readonly ILogger<StationShadowMonitoringHub> _logger;

        public StationShadowMonitoringHub(ILogger<StationShadowMonitoringHub> logger)
        {
            _logger = logger;
        }

        public static string BuildProjectGroup(long projectId)
        {
            return $"station-shadow-project-{projectId}";
        }

        public static string BuildStationGroup(long projectId, string stationCode)
        {
            return $"station-shadow-station-{projectId}-{stationCode?.Trim()?.ToUpperInvariant()}";
        }

        public static string BuildLineDiagnosisGroup(string taskId)
        {
            return $"line-diagnosis-{taskId}";
        }

        public async Task SubscribeProject(long projectId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, BuildProjectGroup(projectId));
            _logger.LogInformation("客户端 {ConnectionId} 订阅工位监控项目组: {ProjectId}", Context.ConnectionId, projectId);
        }

        public async Task UnsubscribeProject(long projectId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildProjectGroup(projectId));
            _logger.LogInformation("客户端 {ConnectionId} 取消订阅工位监控项目组: {ProjectId}", Context.ConnectionId, projectId);
        }

        public async Task SubscribeStation(long projectId, string stationCode)
        {
            if (string.IsNullOrWhiteSpace(stationCode))
            {
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, BuildStationGroup(projectId, stationCode));
            _logger.LogInformation("客户端 {ConnectionId} 订阅工位监控工位组: ProjectId={ProjectId}, StationCode={StationCode}", Context.ConnectionId, projectId, stationCode);
        }

        public async Task UnsubscribeStation(long projectId, string stationCode)
        {
            if (string.IsNullOrWhiteSpace(stationCode))
            {
                return;
            }

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildStationGroup(projectId, stationCode));
            _logger.LogInformation("客户端 {ConnectionId} 取消订阅工位监控工位组: ProjectId={ProjectId}, StationCode={StationCode}", Context.ConnectionId, projectId, stationCode);
        }

        public async Task SubscribeLineDiagnosis(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return;
            await Groups.AddToGroupAsync(Context.ConnectionId, BuildLineDiagnosisGroup(taskId));
            _logger.LogInformation("客户端 {ConnectionId} 订阅全线诊断: TaskId={TaskId}", Context.ConnectionId, taskId);
        }

        public async Task UnsubscribeLineDiagnosis(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return;
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildLineDiagnosisGroup(taskId));
            _logger.LogInformation("客户端 {ConnectionId} 取消订阅全线诊断: TaskId={TaskId}", Context.ConnectionId, taskId);
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("工位监控客户端连接: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            if (exception != null)
            {
                _logger.LogWarning(exception, "工位监控客户端断开（异常）: {ConnectionId}", Context.ConnectionId);
            }
            else
            {
                _logger.LogInformation("工位监控客户端断开: {ConnectionId}", Context.ConnectionId);
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
