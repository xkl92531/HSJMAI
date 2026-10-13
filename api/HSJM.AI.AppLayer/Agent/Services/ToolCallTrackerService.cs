using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 工具调用追踪服务
    /// 
    /// 核心功能：
    /// 1. 追踪 Agent 执行期间的所有工具调用
    /// 2. 检测重复调用模式（相同工具+相同参数）
    /// 3. 控制单个工具的调用次数上限
    /// 4. 提供诊断信息供 Agent/ReAct 循环决策
    /// 
    /// 设计目的：
    /// - 解决框架 agent.RunAsync() 内部工具调用"黑盒"问题
    /// - 防止 LLM 陷入重复调用同一工具的死循环
    /// - 在工具层实现过程诊断，而非等执行完才诊断
    /// </summary>
    public class ToolCallTrackerService : ISelfScopedAutoInject
    {
        private readonly ILogger<ToolCallTrackerService> _logger;

        /// <summary>
        /// 追踪器字典：SessionId -> 追踪器实例
        /// 每个 Agent 执行会话有独立的追踪器
        /// </summary>
        private readonly ConcurrentDictionary<string, ToolCallTracker> _trackers = new();

        /// <summary>
        /// 默认配置
        /// </summary>
        private static readonly ToolCallLimits _defaultLimits = new()
        {
            MaxCallsPerTool = 5,           // 单个工具最多调用5次
            MaxTotalCalls = 20,            // 总调用次数上限
            MaxDuplicateCalls = 2,         // 相同参数最多重复2次
            DuplicateWarningThreshold = 1  // 重复1次就警告
        };

        public ToolCallTrackerService(ILogger<ToolCallTrackerService> logger)
        {
            _logger = logger;
        }

        #region 会话管理

        /// <summary>
        /// 创建新的追踪会话
        /// 在 Agent 开始执行任务时调用
        /// </summary>
        /// <param name="sessionId">会话ID（推荐使用 TaskId 或 WorkflowId）</param>
        /// <param name="limits">自定义限制（可选）</param>
        public ToolCallTracker CreateSession(string sessionId, ToolCallLimits? limits = null)
        {
            var tracker = new ToolCallTracker(sessionId, limits ?? _defaultLimits);
            _trackers[sessionId] = tracker;
            
            _logger.LogInformation("[ToolTracker] 创建会话: {SessionId}, 限制: MaxPerTool={MaxPerTool}, MaxTotal={MaxTotal}",
                sessionId, tracker.Limits.MaxCallsPerTool, tracker.Limits.MaxTotalCalls);
            
            return tracker;
        }

        /// <summary>
        /// 获取现有会话
        /// </summary>
        public ToolCallTracker? GetSession(string sessionId)
        {
            _trackers.TryGetValue(sessionId, out var tracker);
            return tracker;
        }

        /// <summary>
        /// 获取或创建会话
        /// </summary>
        public ToolCallTracker GetOrCreateSession(string sessionId, ToolCallLimits? limits = null)
        {
            return _trackers.GetOrAdd(sessionId, _ => new ToolCallTracker(sessionId, limits ?? _defaultLimits));
        }

        /// <summary>
        /// 关闭会话
        /// 在 Agent 任务完成时调用
        /// </summary>
        public ToolCallTrackerSummary CloseSession(string sessionId)
        {
            if (_trackers.TryRemove(sessionId, out var tracker))
            {
                var summary = tracker.GetSummary();
                _logger.LogInformation("[ToolTracker] 关闭会话: {SessionId}, 总调用={Total}, 工具数={ToolCount}",
                    sessionId, summary.TotalCalls, summary.ToolCallCounts.Count);
                return summary;
            }
            
            return new ToolCallTrackerSummary { SessionId = sessionId };
        }

        #endregion

        #region 调用控制

        /// <summary>
        /// 检查是否应该执行工具调用
        /// 在实际执行工具前调用
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        /// <param name="toolName">工具名称</param>
        /// <param name="parameters">调用参数（JSON）</param>
        /// <returns>检查结果</returns>
        public ToolCallCheckResult ShouldExecute(string sessionId, string toolName, string? parameters)
        {
            var tracker = GetSession(sessionId);
            if (tracker == null)
            {
                // 没有追踪器，默认允许
                _logger.LogWarning("[ToolTracker] 会话不存在: {SessionId}，允许执行", sessionId);
                return ToolCallCheckResult.Allow();
            }

            return tracker.ShouldExecute(toolName, parameters);
        }

        /// <summary>
        /// 记录工具调用
        /// 在工具执行后调用
        /// </summary>
        public void RecordCall(string sessionId, string toolName, string? parameters, string? result, bool success, long durationMs)
        {
            var tracker = GetSession(sessionId);
            if (tracker == null)
            {
                _logger.LogWarning("[ToolTracker] 会话不存在，无法记录: {SessionId}", sessionId);
                return;
            }

            tracker.RecordCall(toolName, parameters, result, success, durationMs);
        }

        #endregion
    }

    /// <summary>
    /// 单个会话的工具调用追踪器
    /// </summary>
    public class ToolCallTracker
    {
        public string SessionId { get; }
        public ToolCallLimits Limits { get; }
        public DateTime StartTime { get; }

        /// <summary>
        /// 调用记录列表
        /// </summary>
        private readonly List<ToolCallRecord> _records = new();
        private readonly object _lock = new();

        /// <summary>
        /// 参数哈希计数：用于检测重复调用
        /// Key = "{toolName}:{paramHash}"
        /// </summary>
        private readonly Dictionary<string, int> _duplicateCounts = new();

        /// <summary>
        /// 每个工具的调用次数
        /// </summary>
        private readonly Dictionary<string, int> _toolCallCounts = new();

        public ToolCallTracker(string sessionId, ToolCallLimits limits)
        {
            SessionId = sessionId;
            Limits = limits;
            StartTime = DateTime.UtcNow;
        }

        /// <summary>
        /// 检查是否应该执行
        /// </summary>
        public ToolCallCheckResult ShouldExecute(string toolName, string? parameters)
        {
            lock (_lock)
            {
                var result = new ToolCallCheckResult { ToolName = toolName };

                // 1. 检查总调用次数
                if (_records.Count >= Limits.MaxTotalCalls)
                {
                    result.ShouldExecute = false;
                    result.Reason = ToolCallBlockReason.TotalLimitExceeded;
                    result.Message = $"总调用次数已达上限 ({Limits.MaxTotalCalls})，请停止调用工具并给出结论。";
                    return result;
                }

                // 2. 检查单工具调用次数
                _toolCallCounts.TryGetValue(toolName, out var toolCount);
                if (toolCount >= Limits.MaxCallsPerTool)
                {
                    result.ShouldExecute = false;
                    result.Reason = ToolCallBlockReason.ToolLimitExceeded;
                    result.Message = $"工具 '{toolName}' 已调用 {toolCount} 次，达到上限。请尝试其他工具或给出结论。";
                    return result;
                }

                // 3. 检查重复调用
                var paramHash = ComputeHash(parameters ?? "");
                var duplicateKey = $"{toolName}:{paramHash}";
                _duplicateCounts.TryGetValue(duplicateKey, out var duplicateCount);

                if (duplicateCount >= Limits.MaxDuplicateCalls)
                {
                    result.ShouldExecute = false;
                    result.Reason = ToolCallBlockReason.DuplicateCall;
                    result.Message = $"相同参数已调用 {duplicateCount} 次，结果不会改变。请使用不同参数或其他工具。";
                    result.CachedResult = FindCachedResult(toolName, paramHash);
                    return result;
                }

                // 4. 重复调用警告（但仍允许执行）
                if (duplicateCount >= Limits.DuplicateWarningThreshold)
                {
                    result.ShouldExecute = true;
                    result.HasWarning = true;
                    result.Warning = $"注意：相同参数已调用 {duplicateCount} 次，再次调用结果很可能相同。";
                }
                else
                {
                    result.ShouldExecute = true;
                }

                return result;
            }
        }

        /// <summary>
        /// 记录调用
        /// </summary>
        public void RecordCall(string toolName, string? parameters, string? result, bool success, long durationMs)
        {
            lock (_lock)
            {
                var record = new ToolCallRecord
                {
                    ToolName = toolName,
                    Parameters = parameters,
                    ParameterHash = ComputeHash(parameters ?? ""),
                    Result = result,
                    Success = success,
                    DurationMs = durationMs,
                    Timestamp = DateTime.UtcNow
                };

                _records.Add(record);

                // 更新计数
                var duplicateKey = $"{toolName}:{record.ParameterHash}";
                _duplicateCounts.TryGetValue(duplicateKey, out var dupCount);
                _duplicateCounts[duplicateKey] = dupCount + 1;

                _toolCallCounts.TryGetValue(toolName, out var toolCount);
                _toolCallCounts[toolName] = toolCount + 1;
            }
        }

        /// <summary>
        /// 获取会话摘要
        /// </summary>
        public ToolCallTrackerSummary GetSummary()
        {
            lock (_lock)
            {
                return new ToolCallTrackerSummary
                {
                    SessionId = SessionId,
                    TotalCalls = _records.Count,
                    ToolCallCounts = new Dictionary<string, int>(_toolCallCounts),
                    DuplicateCallCounts = _duplicateCounts.Where(kv => kv.Value > 1).ToDictionary(kv => kv.Key, kv => kv.Value),
                    Duration = DateTime.UtcNow - StartTime,
                    Records = _records.ToList()
                };
            }
        }

        /// <summary>
        /// 查找缓存的结果
        /// </summary>
        private string? FindCachedResult(string toolName, string paramHash)
        {
            return _records
                .Where(r => r.ToolName == toolName && r.ParameterHash == paramHash && r.Success)
                .OrderByDescending(r => r.Timestamp)
                .FirstOrDefault()?.Result;
        }

        /// <summary>
        /// 计算参数哈希
        /// </summary>
        private static string ComputeHash(string input)
        {
            if (string.IsNullOrEmpty(input)) return "empty";
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).Substring(0, 16);
        }
    }

    #region 数据模型

    /// <summary>
    /// 工具调用限制配置
    /// </summary>
    public class ToolCallLimits
    {
        /// <summary>
        /// 单个工具最大调用次数
        /// </summary>
        public int MaxCallsPerTool { get; set; } = 5;

        /// <summary>
        /// 总调用次数上限
        /// </summary>
        public int MaxTotalCalls { get; set; } = 20;

        /// <summary>
        /// 相同参数最大重复调用次数
        /// </summary>
        public int MaxDuplicateCalls { get; set; } = 2;

        /// <summary>
        /// 重复调用警告阈值
        /// </summary>
        public int DuplicateWarningThreshold { get; set; } = 1;
    }

    /// <summary>
    /// 工具调用检查结果
    /// </summary>
    public class ToolCallCheckResult
    {
        /// <summary>
        /// 工具名称
        /// </summary>
        public string ToolName { get; set; } = "";

        /// <summary>
        /// 是否应该执行
        /// </summary>
        public bool ShouldExecute { get; set; } = true;

        /// <summary>
        /// 阻止原因（如果 ShouldExecute=false）
        /// </summary>
        public ToolCallBlockReason Reason { get; set; } = ToolCallBlockReason.None;

        /// <summary>
        /// 消息（返回给 LLM 的提示）
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// 是否有警告
        /// </summary>
        public bool HasWarning { get; set; }

        /// <summary>
        /// 警告消息
        /// </summary>
        public string? Warning { get; set; }

        /// <summary>
        /// 缓存的结果（如果是重复调用）
        /// </summary>
        public string? CachedResult { get; set; }

        public static ToolCallCheckResult Allow() => new() { ShouldExecute = true };
        
        public static ToolCallCheckResult Block(ToolCallBlockReason reason, string message) => new()
        {
            ShouldExecute = false,
            Reason = reason,
            Message = message
        };
    }

    /// <summary>
    /// 工具调用阻止原因
    /// </summary>
    public enum ToolCallBlockReason
    {
        /// <summary>
        /// 无阻止
        /// </summary>
        None,

        /// <summary>
        /// 总调用次数超限
        /// </summary>
        TotalLimitExceeded,

        /// <summary>
        /// 单工具调用次数超限
        /// </summary>
        ToolLimitExceeded,

        /// <summary>
        /// 重复调用（相同参数）
        /// </summary>
        DuplicateCall
    }

    /// <summary>
    /// 工具调用记录
    /// </summary>
    public class ToolCallRecord
    {
        public string ToolName { get; set; } = "";
        public string? Parameters { get; set; }
        public string ParameterHash { get; set; } = "";
        public string? Result { get; set; }
        public bool Success { get; set; }
        public long DurationMs { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// 追踪器会话摘要
    /// </summary>
    public class ToolCallTrackerSummary
    {
        public string SessionId { get; set; } = "";
        public int TotalCalls { get; set; }
        public Dictionary<string, int> ToolCallCounts { get; set; } = new();
        public Dictionary<string, int> DuplicateCallCounts { get; set; } = new();
        public TimeSpan Duration { get; set; }
        public List<ToolCallRecord> Records { get; set; } = new();
    }

    #endregion
}
