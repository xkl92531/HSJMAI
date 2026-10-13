using System;
using System.Collections.Concurrent;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public class AstSemanticProgressService : ISelfScopedAutoInject
    {
        private static readonly ConcurrentDictionary<string, AstSemanticProgressDto> Store = new ConcurrentDictionary<string, AstSemanticProgressDto>(StringComparer.OrdinalIgnoreCase);

        public AstSemanticProgressDto Start(string key, int totalCandidates, int totalBatches)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var now = DateTime.Now;
            var snapshot = new AstSemanticProgressDto
            {
                Key = key,
                Status = "Running",
                TotalCandidates = Math.Max(totalCandidates, 0),
                CompletedCandidates = 0,
                TotalBatches = Math.Max(totalBatches, 0),
                CompletedBatches = 0,
                FoundCount = 0,
                Message = totalCandidates <= 0 ? "没有可供语义分析的候选输出变量" : "正在准备 AST 语义分析",
                StartedAt = now,
                UpdatedAt = now
            };

            Store.AddOrUpdate(key, snapshot, (_, __) => snapshot);
            return snapshot;
        }

        public AstSemanticProgressDto Advance(string key, int completedCandidates, int completedBatches, int foundCount, string message = null)
        {
            if (string.IsNullOrWhiteSpace(key) || !Store.TryGetValue(key, out var snapshot))
            {
                return null;
            }

            snapshot.CompletedCandidates = Math.Min(snapshot.TotalCandidates, Math.Max(completedCandidates, 0));
            snapshot.CompletedBatches = Math.Min(snapshot.TotalBatches, Math.Max(completedBatches, 0));
            snapshot.FoundCount = Math.Max(foundCount, 0);
            snapshot.Message = string.IsNullOrWhiteSpace(message)
                ? $"已分析 {snapshot.CompletedCandidates}/{snapshot.TotalCandidates} 个候选输出变量"
                : message;
            snapshot.UpdatedAt = DateTime.Now;
            return snapshot;
        }

        public AstSemanticProgressDto Complete(string key, int foundCount, string message = null)
        {
            if (string.IsNullOrWhiteSpace(key) || !Store.TryGetValue(key, out var snapshot))
            {
                return null;
            }

            snapshot.Status = "Completed";
            snapshot.CompletedCandidates = snapshot.TotalCandidates;
            snapshot.CompletedBatches = snapshot.TotalBatches;
            snapshot.FoundCount = Math.Max(foundCount, 0);
            snapshot.Message = message ?? $"AST 语义分析完成，共识别 {snapshot.FoundCount} 条结果";
            snapshot.UpdatedAt = DateTime.Now;
            snapshot.CompletedAt = snapshot.UpdatedAt;
            return snapshot;
        }

        public AstSemanticProgressDto Fail(string key, string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(key) || !Store.TryGetValue(key, out var snapshot))
            {
                return null;
            }

            snapshot.Status = "Failed";
            snapshot.ErrorMessage = errorMessage ?? string.Empty;
            snapshot.Message = "AST 语义分析失败";
            snapshot.UpdatedAt = DateTime.Now;
            snapshot.CompletedAt = snapshot.UpdatedAt;
            return snapshot;
        }

        public AstSemanticProgressDto Get(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !Store.TryGetValue(key, out var snapshot))
            {
                return null;
            }

            return snapshot;
        }
    }
}