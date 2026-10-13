using System;
using System.Collections.Concurrent;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// IO 约束预处理进度跟踪服务（Singleton）
    /// </summary>
    public class IOConstraintProgressService : ISelfSingletonAutoInject
    {
        private readonly ConcurrentDictionary<long, IOConstraintProgress> _progressCache = new();
        private const int PROGRESS_EXPIRY_HOURS = 4;

        public bool TryAcquireLock(long projectId)
        {
            var progress = new IOConstraintProgress
            {
                ProjectId = projectId,
                Status = IOConstraintJobStatus.Running,
                StartTime = DateTime.Now,
                Message = "正在初始化..."
            };

            var existing = _progressCache.AddOrUpdate(projectId, progress, (_, old) =>
            {
                if (old.Status == IOConstraintJobStatus.Running)
                {
                    return old;
                }

                return progress;
            });

            return ReferenceEquals(existing, progress);
        }

        public void ReleaseLock(long projectId)
        {
            // 保留进度信息供前端查询
        }

        public IOConstraintProgress GetProgress(long projectId)
        {
            _progressCache.TryGetValue(projectId, out var progress);
            return progress;
        }

        public bool IsRunning(long projectId)
        {
            return _progressCache.TryGetValue(projectId, out var p) && p.Status == IOConstraintJobStatus.Running;
        }

        public void SetTotalSignals(long projectId, int total)
        {
            if (_progressCache.TryGetValue(projectId, out var p))
            {
                p.TotalSignals = total;
                p.Message = $"待处理 {total} 个信号";
            }
        }

        public void UpdateCurrentSignal(long projectId, string signalName, int processedCount)
        {
            if (_progressCache.TryGetValue(projectId, out var p))
            {
                p.CurrentSignal = signalName;
                p.ProcessedSignals = processedCount;
                p.Percentage = p.TotalSignals > 0 ? (int)((double)processedCount / p.TotalSignals * 100) : 0;
                p.Message = $"正在分析信号 ({processedCount}/{p.TotalSignals}): {signalName}";
            }
        }

        public void IncrementConstraints(long projectId, int count)
        {
            if (_progressCache.TryGetValue(projectId, out var p))
            {
                p.GeneratedConstraints += count;
            }
        }

        public void IncrementFailed(long projectId)
        {
            if (_progressCache.TryGetValue(projectId, out var p))
            {
                p.FailedSignals++;
            }
        }

        public void IncrementNoContext(long projectId)
        {
            if (_progressCache.TryGetValue(projectId, out var p))
            {
                p.NoContextSignals++;
            }
        }

        public void IncrementZeroInsert(long projectId)
        {
            if (_progressCache.TryGetValue(projectId, out var p))
            {
                p.ZeroInsertSignals++;
            }
        }

        public void MarkCompleted(long projectId)
        {
            if (_progressCache.TryGetValue(projectId, out var p))
            {
                p.Status = IOConstraintJobStatus.Completed;
                p.EndTime = DateTime.Now;
                p.Percentage = 100;
                p.Message = $"约束分析完成：处理 {p.ProcessedSignals}/{p.TotalSignals} 个信号，生成 {p.GeneratedConstraints} 条规则，无上下文 {p.NoContextSignals} 个，零插入 {p.ZeroInsertSignals} 个" +
                            (p.FailedSignals > 0 ? $"，失败 {p.FailedSignals} 个" : "");
            }
        }

        public void MarkFailed(long projectId, string errorMessage)
        {
            if (_progressCache.TryGetValue(projectId, out var p))
            {
                p.Status = IOConstraintJobStatus.Failed;
                p.EndTime = DateTime.Now;
                p.ErrorMessage = errorMessage;
                p.Message = $"约束分析失败: {errorMessage}";
            }
        }

        public void SetMessage(long projectId, string message)
        {
            if (_progressCache.TryGetValue(projectId, out var p))
            {
                p.Message = message;
            }
        }

        public void CleanupExpired()
        {
            var threshold = DateTime.Now.AddHours(-PROGRESS_EXPIRY_HOURS);
            foreach (var kvp in _progressCache)
            {
                if (kvp.Value.StartTime < threshold && kvp.Value.Status != IOConstraintJobStatus.Running)
                {
                    _progressCache.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    public class IOConstraintProgress
    {
        public long ProjectId { get; set; }
        public IOConstraintJobStatus Status { get; set; }
        public int TotalSignals { get; set; }
        public int ProcessedSignals { get; set; }
        public int GeneratedConstraints { get; set; }
        public int FailedSignals { get; set; }
        public int NoContextSignals { get; set; }
        public int ZeroInsertSignals { get; set; }
        public int Percentage { get; set; }
        public string CurrentSignal { get; set; }
        public string Message { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }

    public enum IOConstraintJobStatus
    {
        Running = 0,
        Completed = 1,
        Failed = 2
    }
}
