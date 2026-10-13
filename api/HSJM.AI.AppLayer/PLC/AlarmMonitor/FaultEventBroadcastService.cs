using System;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Hubs;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.AlarmMonitor
{
    /// <summary>
    /// 故障事件广播服务：把故障开始/结束事件实时推送到 FaultEventHub 的订阅分组
    /// （Web 前端 + 外部 agent「小锦」）。
    /// 消息 schema 见 docs/报警变量自动识别与故障推送方案.md §6.2：
    /// eventId/eventType(FaultStarted|FaultEnded)/projectId/projectName/stationCode/
    /// variableName/address/alarmCategory/alarmLevel/description/startedAt/endedAt/durationSeconds。
    /// 广播永不抛异常（内部吞掉并记录），失败信息由调用方写入 push_error。
    /// </summary>
    public class FaultEventBroadcastService : ISelfScopedAutoInject
    {
        private readonly IHubContext<FaultEventHub> _hubContext;
        private readonly ILogger<FaultEventBroadcastService> _logger;

        public FaultEventBroadcastService(IHubContext<FaultEventHub> hubContext, ILogger<FaultEventBroadcastService> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
        }

        /// <summary>
        /// 广播故障开始事件。返回是否成功（失败时 error 输出原因）。
        /// </summary>
        public async Task<bool> BroadcastFaultStartedAsync(PLC_FaultEventEntity evt, string projectName,
            string address = null, string alarmCategory = null)
        {
            return await BroadcastAsync(evt, projectName, "FaultStarted", address, alarmCategory);
        }

        /// <summary>
        /// 广播故障结束（恢复）事件。返回是否成功（失败时 error 输出原因）。
        /// </summary>
        public async Task<bool> BroadcastFaultEndedAsync(PLC_FaultEventEntity evt, string projectName,
            string address = null, string alarmCategory = null)
        {
            return await BroadcastAsync(evt, projectName, "FaultEnded", address, alarmCategory);
        }

        private async Task<bool> BroadcastAsync(PLC_FaultEventEntity evt, string projectName, string eventType,
            string address, string alarmCategory)
        {
            try
            {
                if (evt == null) return false;

                // 最小化消息体：只拷贝叶子字段，不序列化实体对象本身
                var message = new
                {
                    eventId = evt.Id,
                    eventType,
                    projectId = evt.ProjectId,
                    projectName = projectName ?? string.Empty,
                    stationCode = evt.StationCode ?? string.Empty,
                    variableName = evt.VariableName ?? string.Empty,
                    address = address ?? string.Empty,
                    alarmCategory = alarmCategory ?? string.Empty,
                    alarmLevel = evt.AlarmLevel ?? string.Empty,
                    description = evt.Description ?? string.Empty,
                    startedAt = evt.StartedAt,
                    endedAt = evt.EndedAt,
                    durationSeconds = evt.DurationSeconds,
                    rowModifyTime = evt.ModifyTime ?? evt.CreateTime ?? DateTime.Now
                };

                var projectGroup = FaultEventHub.BuildProjectGroup(evt.ProjectId);
                await _hubContext.Clients.Group(projectGroup).SendAsync(eventType, message);
                await _hubContext.Clients.Group(FaultEventHub.BuildAllGroup()).SendAsync(eventType, message);

                _logger.LogInformation(
                    "广播故障事件: Type={EventType}, ProjectId={ProjectId}, Variable={VariableName}, EventId={EventId}",
                    eventType, evt.ProjectId, evt.VariableName, evt.Id);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "广播故障事件失败: Type={EventType}, ProjectId={ProjectId}, Variable={VariableName}, EventId={EventId}",
                    eventType, evt?.ProjectId, evt?.VariableName, evt?.Id);
                return false;
            }
        }
    }
}
