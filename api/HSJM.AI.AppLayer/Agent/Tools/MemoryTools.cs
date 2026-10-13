#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.Core;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 记忆检索工具集
    /// 为 Agent 提供主动翻阅归档记忆和核心工作记忆的能力
    /// </summary>
    public class MemoryTools : ISelfScopedAutoInject
    {
        private readonly MemoryManagerService _memoryManagerService;
        private readonly ILogger<MemoryTools> _logger;

        private string? _contextUserId;
        private string? _contextSystemId;
        private string? _contextSessionId;

        public MemoryTools(
            MemoryManagerService memoryManagerService,
            ILogger<MemoryTools> logger)
        {
            _memoryManagerService = memoryManagerService;
            _logger = logger;
        }

        /// <summary>
        /// 设置当前工具上下文
        /// </summary>
        public void SetContext(string? userId, string? systemId = "default", string? sessionId = null)
        {
            _contextUserId = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
            _contextSystemId = string.IsNullOrWhiteSpace(systemId) ? "default" : systemId.Trim();
            _contextSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        }

        [Description("主动检索历史归档记忆。当需要回忆上周/之前讨论过的结论、偏好、事实时调用。支持关键词+时间范围检索，例如：前端框架 结论 + last_week。")]
        public async Task<MemoryToolSearchResult> SearchArchivalMemoryAsync(
            [Description("检索关键词或问题，例如：前端框架 结论、上次讨论的PLC方案")] string query,
            [Description("时间范围，可选：today/yesterday/last_7_days/last_week/last_month/all，也支持中文如 今天/昨天/上周/最近7天")] string? timeRange = null,
            [Description("记忆类型，可选：preference/fact/event/core_profile")] string? memoryType = null,
            [Description("返回条数，默认5")] int topK = 5,
            [Description("是否包含核心工作记忆，默认true")] bool includeCoreMemory = true,
            [Description("优先按关键词匹配，默认true")] bool preferKeywordSearch = true,
            [Description("可选：用户ID；不传则使用当前登录上下文")] string? userId = null,
            [Description("可选：系统ID；不传默认default")] string? systemId = null)
        {
            var result = new MemoryToolSearchResult
            {
                Query = query,
                TimeRange = string.IsNullOrWhiteSpace(timeRange) ? "all" : timeRange,
                TopK = topK
            };

            try
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    result.Success = false;
                    result.Message = "query 不能为空";
                    return result;
                }

                var (resolvedUserId, resolvedSystemId) = ResolveContext(userId, systemId);
                if (string.IsNullOrWhiteSpace(resolvedUserId))
                {
                    result.Success = false;
                    result.Message = "无法解析 userId，请在已登录上下文中调用，或显式传入 userId";
                    return result;
                }

                result.UserId = resolvedUserId;
                result.SystemId = resolvedSystemId;

                var memories = await _memoryManagerService.SearchLongTermMemoryAsync(
                    resolvedUserId,
                    _contextSessionId,
                    query,
                    topK,
                    resolvedSystemId,
                    timeRange,
                    null,
                    null,
                    preferKeywordSearch);

                if (!string.IsNullOrWhiteSpace(memoryType))
                {
                    memories = memories
                        .Where(m => string.Equals(m.MemoryType, memoryType, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                var coreMemories = includeCoreMemory
                    ? await _memoryManagerService.GetCoreMemoriesAsync(resolvedUserId, resolvedSystemId, Math.Min(topK, 8))
                    : new List<HSJM.AI.IDbService.Agent_MemoryEntity>();

                result.CoreMemorySummary = coreMemories.Count > 0
                    ? await _memoryManagerService.GetCoreMemoryPromptBlockAsync(resolvedUserId, resolvedSystemId, Math.Min(topK, 8))
                    : string.Empty;

                result.CoreMemories = coreMemories.Select(ToItem).ToList();
                result.Memories = memories.Select(ToItem).ToList();
                result.Success = true;
                result.Message = $"已检索到 {result.Memories.Count} 条归档记忆" +
                                 (result.CoreMemories.Count > 0 ? $"，并附带 {result.CoreMemories.Count} 条核心工作记忆" : string.Empty);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemoryTools] 主动记忆检索失败");
                result.Success = false;
                result.Message = $"主动记忆检索失败: {ex.Message}";
                return result;
            }
        }

        [Description("读取当前用户的核心工作记忆(User Profile / Persona)。当需要确认用户身份、长期偏好、技术栈时调用。")]
        public async Task<CoreUserProfileResult> GetCoreUserProfileAsync(
            [Description("返回条数，默认6")] int topK = 6,
            [Description("可选：用户ID；不传则使用当前登录上下文")] string? userId = null,
            [Description("可选：系统ID；不传默认default")] string? systemId = null)
        {
            var result = new CoreUserProfileResult();

            try
            {
                var (resolvedUserId, resolvedSystemId) = ResolveContext(userId, systemId);
                if (string.IsNullOrWhiteSpace(resolvedUserId))
                {
                    result.Success = false;
                    result.Message = "无法解析 userId，请在已登录上下文中调用，或显式传入 userId";
                    return result;
                }

                result.UserId = resolvedUserId;
                result.SystemId = resolvedSystemId;
                result.UserProfile = await _memoryManagerService.GetCoreMemoryPromptBlockAsync(resolvedUserId, resolvedSystemId, topK);
                result.Success = !string.IsNullOrWhiteSpace(result.UserProfile);
                result.Message = result.Success ? "已加载核心工作记忆" : "当前暂无核心工作记忆";
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemoryTools] 获取核心工作记忆失败");
                result.Success = false;
                result.Message = $"获取核心工作记忆失败: {ex.Message}";
                return result;
            }
        }

        [Description("将稳定的用户身份信息、长期偏好或Persona写入核心工作记忆区。适用于：职业、常用技术栈、代码风格偏好、长期目标等。")]
        public async Task<RememberUserProfileResult> RememberUserProfileAsync(
            [Description("要写入核心工作记忆的内容，例如：用户偏好简洁清晰的代码风格")] string content,
            [Description("重要性得分，默认8.5")] float importanceScore = 8.5f,
            [Description("可选：来源会话ID")] string? sessionId = null,
            [Description("可选：用户ID；不传则使用当前登录上下文")] string? userId = null,
            [Description("可选：系统ID；不传默认default")] string? systemId = null)
        {
            var result = new RememberUserProfileResult();

            try
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    result.Success = false;
                    result.Message = "content 不能为空";
                    return result;
                }

                var (resolvedUserId, resolvedSystemId) = ResolveContext(userId, systemId);
                if (string.IsNullOrWhiteSpace(resolvedUserId))
                {
                    result.Success = false;
                    result.Message = "无法解析 userId，请在已登录上下文中调用，或显式传入 userId";
                    return result;
                }

                await _memoryManagerService.UpsertCoreMemoryAsync(
                    resolvedUserId,
                    content.Trim(),
                    Math.Max(importanceScore, 8.0f),
                    sessionId ?? _contextSessionId,
                    resolvedSystemId);

                result.Success = true;
                result.UserId = resolvedUserId;
                result.SystemId = resolvedSystemId;
                result.Message = "已写入核心工作记忆";
                result.UserProfile = await _memoryManagerService.GetCoreMemoryPromptBlockAsync(resolvedUserId, resolvedSystemId);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MemoryTools] 写入核心工作记忆失败");
                result.Success = false;
                result.Message = $"写入核心工作记忆失败: {ex.Message}";
                return result;
            }
        }

        private (string? userId, string systemId) ResolveContext(string? userId, string? systemId)
        {
            var resolvedUserId = !string.IsNullOrWhiteSpace(userId)
                ? userId.Trim()
                : !string.IsNullOrWhiteSpace(_contextUserId)
                    ? _contextUserId
                    : CurrentUserInfo.Instance.UserID > 0
                        ? CurrentUserInfo.Instance.UserID.ToString()
                        : null;

            var resolvedSystemId = !string.IsNullOrWhiteSpace(systemId)
                ? systemId.Trim()
                : !string.IsNullOrWhiteSpace(_contextSystemId)
                    ? _contextSystemId!
                    : "default";

            return (resolvedUserId, resolvedSystemId);
        }

        private static MemoryToolItem ToItem(HSJM.AI.IDbService.Agent_MemoryEntity memory)
        {
            return new MemoryToolItem
            {
                Id = memory.Id,
                Content = memory.Content,
                MemoryType = memory.MemoryType,
                ImportanceScore = memory.ImportanceScore,
                IsHighQuality = memory.IsHighQuality,
                CreatedAt = memory.CreatedAt
            };
        }
    }

    public class MemoryToolSearchResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? Query { get; set; }
        public string? TimeRange { get; set; }
        public int TopK { get; set; }
        public string? UserId { get; set; }
        public string? SystemId { get; set; }
        public string? CoreMemorySummary { get; set; }
        public List<MemoryToolItem> CoreMemories { get; set; } = new();
        public List<MemoryToolItem> Memories { get; set; } = new();
    }

    public class CoreUserProfileResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? UserId { get; set; }
        public string? SystemId { get; set; }
        public string? UserProfile { get; set; }
    }

    public class RememberUserProfileResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? UserId { get; set; }
        public string? SystemId { get; set; }
        public string? UserProfile { get; set; }
    }

    public class MemoryToolItem
    {
        public long Id { get; set; }
        public string? Content { get; set; }
        public string? MemoryType { get; set; }
        public float ImportanceScore { get; set; }
        public bool IsHighQuality { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
