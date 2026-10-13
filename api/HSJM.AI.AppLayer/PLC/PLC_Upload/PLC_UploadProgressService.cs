using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload
{
    /// <summary>
    /// PLC上传进度跟踪服务
    /// 使用内存缓存存储上传进度，支持后台任务与前端轮询
    /// </summary>
    public class PLC_UploadProgressService : ISelfSingletonAutoInject
    {
        /// <summary>
        /// 任务进度缓存 (taskId -> progress)
        /// </summary>
        private readonly ConcurrentDictionary<string, UploadProgress> _progressCache = new();

        /// <summary>
        /// 进度过期时间（小时）：已结束的任务按结束时间算。
        /// </summary>
        private const int PROGRESS_EXPIRY_HOURS = 2;

        /// <summary>
        /// 仍在处理的任务按开始时间的过期上限（小时）。上传可能要等同项目的约束重置 / 重跑结束，首次全量发布还要重写全部 AI 说明，
        /// 总时长可能超过 2 小时，不能在处理中途把进度清掉（审查复核 M-2）；超过一天视为已丢失。
        /// </summary>
        private const int RUNNING_PROGRESS_EXPIRY_HOURS = 24;

        /// <summary>
        /// 创建新的上传任务进度
        /// </summary>
        public UploadProgress CreateProgress(string taskId, long projectId, string fileName)
        {
            var progress = new UploadProgress
            {
                TaskId = taskId,
                ProjectId = projectId,
                FileName = fileName,
                Status = UploadStatus.Pending,
                TotalFiles = 0,
                ProcessedFiles = 0,
                Percentage = 0,
                Message = "等待处理...",
                StartTime = DateTime.Now
            };
            _progressCache[taskId] = progress;
            CleanupExpiredProgress(); // 清理过期进度
            return progress;
        }

        /// <summary>
        /// 获取上传进度
        /// </summary>
        public UploadProgress GetProgress(string taskId)
        {
            _progressCache.TryGetValue(taskId, out var progress);
            return progress;
        }

        /// <summary>
        /// 更新进度：设置总文件数
        /// </summary>
        public void SetTotalFiles(string taskId, int totalFiles)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.TotalFiles = totalFiles;
                    progress.ProcessedFiles = 0;
                    progress.Percentage = 0;
                    progress.CurrentFile = null;
                    progress.Status = UploadStatus.Processing;
                    progress.Message = $"正在解析文件 (0/{totalFiles})...";
                }
            }
        }


        /// <summary>
        /// 更新进度：设置总文件数
        /// </summary>
        public void SetTotalFiles(string taskId, int totalFiles, string mes)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.TotalFiles = totalFiles;
                    progress.ProcessedFiles = 0;
                    progress.Percentage = 0;
                    progress.CurrentFile = null;
                    progress.Status = UploadStatus.Processing;
                    progress.Message = $"{mes} (0/{totalFiles})...";
                }
            }
        }
        /// <summary>
        /// 更新进度：增加已处理文件数
        /// </summary>
        public void IncrementProcessed(string taskId, string currentFileName = null, string mes = null)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.ProcessedFiles++;
                    progress.CurrentFile = currentFileName;
                    if (progress.TotalFiles > 0)
                    {
                        progress.Percentage = (int)((double)progress.ProcessedFiles / progress.TotalFiles * 100);
                    }
                    progress.Message = $"{mes} ({progress.ProcessedFiles}/{progress.TotalFiles})...";
                }
            }
        }

        /// <summary>
        /// 更新进度：增加已处理文件数
        /// </summary>
        public void IncrementProcessed(string taskId, string currentFileName = null)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.ProcessedFiles++;
                    progress.CurrentFile = currentFileName;
                    if (progress.TotalFiles > 0)
                    {
                        progress.Percentage = (int)((double)progress.ProcessedFiles / progress.TotalFiles * 100);
                    }
                    progress.Message = $"正在解析文件 ({progress.ProcessedFiles}/{progress.TotalFiles})...";
                }
            }
        }

        /// <summary>
        /// 更新进度：设置状态消息
        /// </summary>
        public void SetMessage(string taskId, string message)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.Message = message;
                }
            }
        }

        /// <summary>
        /// 保存 GRAPH 草稿生成的安全计数摘要。
        /// </summary>
        public void SetGraphSummary(string taskId, UploadGraphSummary summary)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.GraphSummary = summary;
                }
            }
        }

        /// <summary>
        /// 上传完成后的后处理任务状态（2026-09-11 标签增量注册前端可见化）：
        /// 上传主体 MarkCompleted 之后并行执行的 Hangfire 任务（如标签增量注册）按 key 上报状态，
        /// 前端在完成态继续轮询本接口直到后处理全部终态（Succeeded/Failed/Skipped）或超时。
        /// </summary>
        /// <param name="taskId">上传任务ID</param>
        /// <param name="key">后处理条目唯一键（如 tag-registration）</param>
        /// <param name="title">展示名（如 标签增量注册）</param>
        /// <param name="status">Running / Succeeded / Failed / Skipped</param>
        /// <param name="detail">成功统计 / 失败原因 / 跳过原因</param>
        public void SetPostProcess(string taskId, string key, string title, string status, string detail = null)
        {
            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(key))
            {
                return;
            }
            if (!_progressCache.TryGetValue(taskId, out var progress))
            {
                return;
            }
            lock (progress)
            {
                var list = progress.PostProcesses ??= new List<UploadPostProcess>();
                var entry = list.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
                if (entry == null)
                {
                    entry = new UploadPostProcess { Key = key, Title = title };
                    list.Add(entry);
                }
                entry.Status = status;
                entry.Detail = detail;
                if (status is "Succeeded" or "Failed" or "Skipped")
                {
                    entry.FinishTime = DateTime.Now;
                }
                else
                {
                    entry.FinishTime = null;
                }
            }
        }

        /// <summary>
        /// 更新进度：设置为保存数据阶段
        /// </summary>
        public void SetSavingPhase(string taskId)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.Status = UploadStatus.Saving;
                    progress.Message = "正在保存数据到数据库...";
                    progress.Percentage = 90;
                }
            }
        }

        /// <summary>
        /// 保存阶段步骤明细：只更新文案不推进百分比（用于清理旧数据等子步骤）
        /// </summary>
        public void SetSavingDetail(string taskId, string detail)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.Message = $"正在保存数据到数据库：{detail}...";
                }
            }
        }

        /// <summary>
        /// 保存阶段子进度：按已插入行数推进，映射到 90%~98% 区间
        /// </summary>
        /// <param name="doneRows">已插入行数（跨表累计）</param>
        /// <param name="totalRows">本次待插入总行数</param>
        /// <param name="label">当前表标签（如"接口变量"）</param>
        public void SetSavingSubProgress(string taskId, long doneRows, long totalRows, string label)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    var pct = 90;
                    if (totalRows > 0)
                    {
                        pct = 90 + (int)Math.Round(8.0 * Math.Min(doneRows, totalRows) / totalRows);
                    }
                    if (pct > 98) pct = 98;
                    if (pct >= progress.Percentage)
                    {
                        progress.Percentage = pct;
                    }
                    progress.Message = totalRows > 0
                        ? $"正在保存数据到数据库：{label}（累计 {doneRows}/{totalRows} 行）..."
                        : $"正在保存数据到数据库：{label}...";
                }
            }
        }

        /// <summary>
        /// 标记任务完成
        /// </summary>
        public void MarkCompleted(string taskId, string resultProjectId = null)
        {
            MarkCompleted(taskId, resultProjectId, null);
        }

        /// <summary>
        /// 标记任务完成（带结果摘要）。summary 非空时替换默认"上传解析完成!"文案，
        /// 供前端完成态展示本次上传的增量结果（变更/跳过/清理统计）。
        /// </summary>
        public void MarkCompleted(string taskId, string resultProjectId, string summary)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.Status = UploadStatus.Completed;
                    progress.Percentage = 100;
                    progress.Message = string.IsNullOrWhiteSpace(summary) ? "上传解析完成!" : summary;
                    progress.EndTime = DateTime.Now;
                    progress.ResultProjectId = resultProjectId;
                }
            }
        }

        /// <summary>
        /// 约束生成阶段（2026-10-02 审查 P1）：解析入库已提交，XMLV2 发布进行中。沿用 Saving 状态（前端视为处理中、继续轮询），
        /// 只更新文案与百分比；发布成功后才 <see cref="MarkCompleted(string, string, string)"/>。
        /// </summary>
        public void SetConstraintPhase(string taskId)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.Status = UploadStatus.Saving;
                    progress.CurrentFile = null;
                    progress.Percentage = Math.Max(progress.Percentage, 99);
                    progress.Message = "正在生成约束与白话文描述（XMLV2）...";
                }
            }
        }

        /// <summary>
        /// 只在任务尚未终态时标记失败：已失败的保留第一条失败原因（更具体），已完成的不翻成失败。返回是否写入。
        /// </summary>
        public bool TryMarkFailed(string taskId, string errorMessage)
        {
            if (!_progressCache.TryGetValue(taskId, out var progress))
            {
                return false;
            }
            lock (progress)
            {
                if (progress.Status is UploadStatus.Completed or UploadStatus.Failed)
                {
                    return false;
                }
                progress.Status = UploadStatus.Failed;
                progress.Message = errorMessage;
                progress.ErrorDetails = errorMessage;
                progress.EndTime = DateTime.Now;
                return true;
            }
        }

        /// <summary>
        /// 标记任务失败
        /// </summary>
        public void MarkFailed(string taskId, string errorMessage)
        {
            if (_progressCache.TryGetValue(taskId, out var progress))
            {
                lock (progress)
                {
                    progress.Status = UploadStatus.Failed;
                    progress.Message = errorMessage;
                    progress.ErrorDetails = errorMessage;
                    progress.EndTime = DateTime.Now;
                }
            }
        }

        /// <summary>
        /// 清理过期进度
        /// </summary>
        private void CleanupExpiredProgress()
        {
            var now = DateTime.Now;
            var finishedThreshold = now.AddHours(-PROGRESS_EXPIRY_HOURS);
            var runningThreshold = now.AddHours(-RUNNING_PROGRESS_EXPIRY_HOURS);
            foreach (var kvp in _progressCache)
            {
                var p = kvp.Value;
                var finished = p.Status is UploadStatus.Completed or UploadStatus.Failed;
                var expired = finished
                    ? (p.EndTime ?? p.StartTime) < finishedThreshold
                    : p.StartTime < runningThreshold;
                if (expired)
                {
                    _progressCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// 移除进度记录
        /// </summary>
        public void RemoveProgress(string taskId)
        {
            _progressCache.TryRemove(taskId, out _);
        }
    }

    #region 数据模型

    /// <summary>
    /// 上传进度模型
    /// </summary>
    public class UploadProgress
    {
        /// <summary>
        /// 任务ID（唯一标识）
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// 项目ID
        /// </summary>
        public long ProjectId { get; set; }

        /// <summary>
        /// 上传的文件名
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// 当前状态
        /// </summary>
        public UploadStatus Status { get; set; }

        /// <summary>
        /// 总文件数
        /// </summary>
        public int TotalFiles { get; set; }

        /// <summary>
        /// 已处理文件数
        /// </summary>
        public int ProcessedFiles { get; set; }

        /// <summary>
        /// 当前正在处理的文件
        /// </summary>
        public string CurrentFile { get; set; }

        /// <summary>
        /// 进度百分比 (0-100)
        /// </summary>
        public int Percentage { get; set; }

        /// <summary>
        /// 状态描述消息
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// 开始时间
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// 结束时间
        /// </summary>
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// 错误详情（失败时）
        /// </summary>
        public string ErrorDetails { get; set; }

        /// <summary>
        /// 结果项目ID（成功时返回）
        /// </summary>
        public string ResultProjectId { get; set; }

        /// <summary>
        /// GRAPH 草稿生成摘要，不包含源文件路径或解析内容。
        /// </summary>
        public UploadGraphSummary GraphSummary { get; set; }

        /// <summary>
        /// 上传完成后的后处理任务状态（标签增量注册等）；前端完成态继续轮询直到全部终态。
        /// </summary>
        public List<UploadPostProcess> PostProcesses { get; set; }
    }

    /// <summary>
    /// 上传后处理任务状态条目（2026-09-11）。Status: Running / Succeeded / Failed / Skipped。
    /// </summary>
    public class UploadPostProcess
    {
        /// <summary>条目唯一键（如 tag-registration）。</summary>
        public string Key { get; set; }

        /// <summary>展示名（如 标签增量注册）。</summary>
        public string Title { get; set; }

        /// <summary>Running / Succeeded / Failed / Skipped。</summary>
        public string Status { get; set; }

        /// <summary>成功统计 / 失败原因 / 跳过原因。</summary>
        public string Detail { get; set; }

        /// <summary>终态时间；Running 时为 null。</summary>
        public DateTime? FinishTime { get; set; }
    }

    public sealed record UploadGraphSummary(
        int DetectedBlockCount,
        int DraftCreatedCount,
        int ReusedRevisionCount,
        int FailedCount,
        int IssueCount);

    /// <summary>
    /// 上传状态枚举
    /// </summary>
    public enum UploadStatus
    {
        /// <summary>
        /// 待处理
        /// </summary>
        Pending = 0,

        /// <summary>
        /// 正在处理
        /// </summary>
        Processing = 1,

        /// <summary>
        /// 正在保存
        /// </summary>
        Saving = 2,

        /// <summary>
        /// 已完成
        /// </summary>
        Completed = 3,

        /// <summary>
        /// 失败
        /// </summary>
        Failed = 4
    }

    #endregion
}
