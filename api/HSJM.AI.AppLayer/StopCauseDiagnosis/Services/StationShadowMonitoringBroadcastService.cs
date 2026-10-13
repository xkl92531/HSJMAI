using System.Collections.Generic;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Hubs;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public class StationShadowMonitoringBroadcastService : ISelfScopedAutoInject
    {
        private readonly IHubContext<StationShadowMonitoringHub> _hubContext;
        private readonly ILogger<StationShadowMonitoringBroadcastService> _logger;
        private readonly EgressTranslationService _egressTranslation;

        public StationShadowMonitoringBroadcastService(
            IHubContext<StationShadowMonitoringHub> hubContext,
            ILogger<StationShadowMonitoringBroadcastService> logger,
            EgressTranslationService egressTranslation)
        {
            _hubContext = hubContext;
            _logger = logger;
            _egressTranslation = egressTranslation;
        }

        public async Task BroadcastCycleCompletedAsync(StationShadowRealtimeCycleMessageDto message)
        {
            if (message == null || message.ProjectId <= 0 || string.IsNullOrWhiteSpace(message.StationCode))
            {
                return;
            }

            var projectGroup = StationShadowMonitoringHub.BuildProjectGroup(message.ProjectId);
            var stationGroup = StationShadowMonitoringHub.BuildStationGroup(message.ProjectId, message.StationCode);

            await _hubContext.Clients.Group(projectGroup).SendAsync("StationShadowCycleCompleted", message);
            await _hubContext.Clients.Group(stationGroup).SendAsync("StationShadowCycleCompleted", message);

            _logger.LogInformation("广播工位监控完成消息: ProjectId={ProjectId}, StationCode={StationCode}, ChangedSignalCount={ChangedSignalCount}",
                message.ProjectId,
                message.StationCode,
                message.ChangedSignalCount);
        }

        public async Task BroadcastCycleFailedAsync(long projectId, string stationCode, string runtimeNodeName, string jobSource, string errorMessage)
        {
            if (projectId <= 0 || string.IsNullOrWhiteSpace(stationCode))
            {
                return;
            }

            var payload = new StationShadowRealtimeFailureMessageDto
            {
                ProjectId = projectId,
                StationCode = stationCode?.Trim(),
                RuntimeNodeName = runtimeNodeName?.Trim(),
                JobSource = string.IsNullOrWhiteSpace(jobSource) ? "Manual" : jobSource.Trim(),
                ErrorMessage = errorMessage,
            };

            var projectGroup = StationShadowMonitoringHub.BuildProjectGroup(projectId);
            var stationGroup = StationShadowMonitoringHub.BuildStationGroup(projectId, stationCode);

            await _hubContext.Clients.Group(projectGroup).SendAsync("StationShadowCycleFailed", payload);
            await _hubContext.Clients.Group(stationGroup).SendAsync("StationShadowCycleFailed", payload);

            _logger.LogWarning("广播工位监控失败消息: ProjectId={ProjectId}, StationCode={StationCode}, Error={ErrorMessage}",
                projectId,
                stationCode,
                errorMessage);
        }

        /// <summary>
        /// 全线诊断：推送工位列表（诊断开始时发送，让前端立即展示占位）
        /// </summary>
        public async Task BroadcastLineDiagnosisStartedAsync(string taskId, List<string> stationCodes)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return;
            var group = StationShadowMonitoringHub.BuildLineDiagnosisGroup(taskId);
            await _hubContext.Clients.Group(group).SendAsync("LineDiagnosisStarted", new
            {
                TaskId = taskId,
                StationCodes = stationCodes,
                TotalStations = stationCodes?.Count ?? 0,
            });
        }

        /// <summary>
        /// 全线诊断：推送单工位完成（每个工位诊断完就推一条）
        /// </summary>
        public async Task BroadcastLineDiagnosisStationDoneAsync(string taskId, StationDiagnosisSummaryDto summary, int completedCount, int totalCount)
        {
            if (string.IsNullOrWhiteSpace(taskId) || summary == null) return;
            var group = StationShadowMonitoringHub.BuildLineDiagnosisGroup(taskId);
            await _hubContext.Clients.Group(group).SendAsync("LineDiagnosisStationDone", new
            {
                TaskId = taskId,
                Station = summary,
                CompletedCount = completedCount,
                TotalCount = totalCount,
            });
        }

        /// <summary>
        /// 全线诊断：推送 LLM 分析阶段开始
        /// </summary>
        public async Task BroadcastLineDiagnosisLlmStartAsync(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return;
            var group = StationShadowMonitoringHub.BuildLineDiagnosisGroup(taskId);
            await _hubContext.Clients.Group(group).SendAsync("LineDiagnosisLlmStart", new { TaskId = taskId });
        }

        /// <summary>
        /// 全线诊断：推送 Agent 推理步骤（ReAct 每一步）
        /// </summary>
        public async Task BroadcastLineDiagnosisAgentStepAsync(string taskId, object stepPayload)
        {
            if (string.IsNullOrWhiteSpace(taskId) || stepPayload == null) return;
            var group = StationShadowMonitoringHub.BuildLineDiagnosisGroup(taskId);
            await _hubContext.Clients.Group(group).SendAsync("LineDiagnosisAgentStep", stepPayload);
        }

        /// <summary>
        /// 全线诊断：推送最终完成结果。
        /// 非 zh-CN 任务在原文推送后触发出口翻译跟随（LineDiagnosisContentTranslated），
        /// 译文永不阻塞原文推送。
        /// </summary>
        public async Task BroadcastLineDiagnosisCompletedAsync(
            string taskId,
            LineDiagnosisResultDto result,
            string outputLanguage = null)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return;
            var group = StationShadowMonitoringHub.BuildLineDiagnosisGroup(taskId);
            await _hubContext.Clients.Group(group).SendAsync("LineDiagnosisCompleted", new
            {
                TaskId = taskId,
                Result = result,
            });

            if (result != null && _egressTranslation != null
                && _egressTranslation.NeedsTranslation(outputLanguage))
            {
                // fire-and-forget：原文已推送，译文迟到不影响主流程（egress 内部自吞异常并总推 final 事件）
                _ = _egressTranslation.FollowUpCompletedAsync(taskId, result, outputLanguage);
            }
        }

        /// <summary>
        /// 全线诊断：推送失败
        /// </summary>
        public async Task BroadcastLineDiagnosisFailedAsync(string taskId, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return;
            var group = StationShadowMonitoringHub.BuildLineDiagnosisGroup(taskId);
            await _hubContext.Clients.Group(group).SendAsync("LineDiagnosisFailed", new
            {
                TaskId = taskId,
                ErrorMessage = errorMessage,
            });
        }
    }
}
