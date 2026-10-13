using System;
using System.Collections.Concurrent;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 一键分析入库进度跟踪服务（内存级，静态字典）。
    /// 复用 AstSemanticProgressService 的模式，前端通过轮询查询进度。
    /// </summary>
    public class FullAnalysisProgressService : ISelfScopedAutoInject
    {
        private static readonly ConcurrentDictionary<string, FullAnalysisProgressDto> Store
            = new(StringComparer.OrdinalIgnoreCase);

        public FullAnalysisProgressDto Start(string taskId, long projectId, string stationCode)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return null;

            var now = DateTime.Now;
            var dto = new FullAnalysisProgressDto
            {
                TaskId = taskId,
                ProjectId = projectId,
                StationCode = stationCode,
                Status = "Running",
                CurrentStep = 0,
                TotalSteps = 5,
                StepName = "初始化",
                Message = "正在准备一键分析...",
                Percentage = 0,
                StartedAt = now,
                UpdatedAt = now
            };

            Store.AddOrUpdate(taskId, dto, (_, __) => dto);
            return dto;
        }

        /// <summary>
        /// 该项目当前是否有一轮全量分析在跑。
        ///
        /// 2026-09-22 上传自动分析用：上传是高频动作、带 LLM 的全量分析很贵，
        /// 没有这道判断连传几次就会堆起一摞重复任务。进度按 taskId 存，所以要反查 ProjectId。
        /// </summary>
        public bool IsRunning(long projectId)
        {
            foreach (var item in Store.Values)
            {
                if (item != null && item.ProjectId == projectId
                    && string.Equals(item.Status, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 原子占位（2026-10-09 检查清单 #14）：IsRunning 检查与 Start 之间存在竞态——
        /// 同项目两次并发上传会双双通过检查、重复入队两个全量分析任务。
        /// 锁内完成「检查 + 占位」；返回 false 表示该项目已有 Running 记录（未占位）。
        /// </summary>
        public bool TryStartExclusive(string taskId, long projectId, string stationCode)
        {
            if (string.IsNullOrWhiteSpace(taskId)) return false;
            lock (Store)
            {
                if (IsRunning(projectId)) return false;
                Start(taskId, projectId, stationCode);
                return true;
            }
        }

        public FullAnalysisProgressDto Advance(string taskId, int step, string stepName, string message, int percentage)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !Store.TryGetValue(taskId, out var dto))
                return null;

            dto.CurrentStep = step;
            dto.StepName = stepName;
            dto.Message = message;
            dto.Percentage = Math.Clamp(percentage, 0, 99);
            dto.UpdatedAt = DateTime.Now;
            return dto;
        }

        public FullAnalysisProgressDto Complete(string taskId, FullAnalysisSummaryDto summary)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !Store.TryGetValue(taskId, out var dto))
                return null;

            dto.Status = "Completed";
            dto.CurrentStep = dto.TotalSteps;
            dto.StepName = "完成";
            dto.Message = "一键分析入库完成";
            dto.Percentage = 100;
            dto.UpdatedAt = DateTime.Now;
            dto.CompletedAt = dto.UpdatedAt;
            dto.Summary = summary;
            return dto;
        }

        public FullAnalysisProgressDto Fail(string taskId, int step, string stepName, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !Store.TryGetValue(taskId, out var dto))
                return null;

            dto.Status = "Failed";
            dto.CurrentStep = step;
            dto.StepName = stepName;
            dto.Message = $"分析失败: {errorMessage}";
            dto.ErrorMessage = errorMessage;
            dto.UpdatedAt = DateTime.Now;
            dto.CompletedAt = dto.UpdatedAt;
            return dto;
        }

        public FullAnalysisProgressDto Get(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !Store.TryGetValue(taskId, out var dto))
                return null;
            return dto;
        }

        /// <summary>
        /// 清理 2 小时前已完成/失败的进度
        /// </summary>
        public void Cleanup()
        {
            var cutoff = DateTime.Now.AddHours(-2);
            foreach (var kv in Store)
            {
                if (kv.Value.CompletedAt.HasValue && kv.Value.CompletedAt.Value < cutoff)
                {
                    Store.TryRemove(kv.Key, out _);
                }
            }
        }
    }
}
