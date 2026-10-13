using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using System.Linq;
using System.Text.Json;
using HSJM.AI.Utility;
using Microsoft.Extensions.AI;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 后台记忆处理服务：异步执行长期记忆提炼与短期上下文压缩
    /// </summary>
    public class MemoryExtractionBackgroundJobService : ISelfScopedAutoInject
    {
        private readonly ChatSessionManagementService _chatSessionService;
        private readonly IChat_MessageService _messageService;
        private readonly IAgent_MemoryService _memoryDbService;
        private readonly IAgent_SessionContextService _sessionContextService;
        private readonly MemoryManagerService _memoryManagerService;
        private readonly LLMModelService _llmModelService;
        private readonly ILogger<MemoryExtractionBackgroundJobService> _logger;

        public MemoryExtractionBackgroundJobService(
            ChatSessionManagementService chatSessionService,
            IChat_MessageService messageService,
            IAgent_MemoryService memoryDbService,
            IAgent_SessionContextService sessionContextService,
            MemoryManagerService memoryManagerService,
            LLMModelService llmModelService,
            ILogger<MemoryExtractionBackgroundJobService> logger)
        {
            _chatSessionService = chatSessionService;
            _messageService = messageService;
            _memoryDbService = memoryDbService;
            _sessionContextService = sessionContextService;
            _memoryManagerService = memoryManagerService;
            _llmModelService = llmModelService;
            _logger = logger;
        }

        /// <summary>
        /// 异步提取与压缩对话记忆
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        /// <param name="userId">用户ID</param>
        /// <param name="systemId">系统标识（用于多系统记忆隔离）</param>
        [Hangfire.JobDisplayName("会话记忆提取 · 会话{0}")]
        public async Task ProcessDialogMemoriesAsync(long sessionId, string userId, string systemId = "default")
        {
            try
            {
                // 显眼的日志，确保能记录到
                _logger.LogError("[后台记忆处理] ========== 任务触发 ======== SessionId={SessionID}, UserId={UserId}, SystemId={SystemId}", sessionId, userId, systemId);
                _logger.LogInformation("[后台记忆处理] 开始处理会话 SessionId={SessionID}, UserId={UserId}, SystemId={SystemId}", sessionId, userId, systemId);

                // ==========================================
                // 0. 保存会话完整上下文（原始对话内容，供后续记忆提取使用）
                // ==========================================
                await SaveSessionContextAsync(sessionId, userId, systemId);

                // ==========================================
                // 1. 长期记忆抽取（基于最近对话，含助理当前回复）
                // ==========================================
                var extractionHistoryCount = Math.Clamp(AppsettingsConfig.AIAgent?.MemoryExtractHistoryMessages ?? 8, 2, 30);
                var extractionHistory = await _chatSessionService.GetSessionHistoryAsync(sessionId, maxMessages: extractionHistoryCount, includeSystemMessages: false);

                if (extractionHistory.Count > 0)
                {
                    var extractionHistoryLines = extractionHistory
                        .Where(m => !string.IsNullOrWhiteSpace(m.Text))
                        .Select(m => $"[{(m.Role == ChatRole.User ? "用户" : "助手")}]: {m.Text}")
                        .ToList();

                    var extractionInput = extractionHistoryLines.Count > 0
                        ? $"### 最近对话历史（待提取记忆）\n{string.Join("\n", extractionHistoryLines)}"
                        : "";

                    if (!string.IsNullOrWhiteSpace(extractionInput))
                    {
                        _memoryManagerService.RememberDialogInWorkingMemory(userId, extractionInput, sessionId.ToString(), systemId);
                        await _memoryManagerService.ExtractAndSaveLongTermMemoriesAsync(userId, extractionInput, sessionId.ToString(), systemId);
                        _logger.LogInformation("[长期记忆提取] 后台自动抽取完成，入库条数基于大模型结果");
                    }
                }

                // ==========================================
                // 2. 短期记忆预压缩 (Context Compaction)
                // ==========================================
                // 获取当前会话下所有的非删除消息
                var allMessages = await _messageService.GetListAsync(m => m.SessionId == sessionId && m.IsDeleted == false);
                var sorted = allMessages.OrderBy(m => m.SequenceNumber).ToList();

                // 阈值：当消息条数超过多少个（如对话5轮约10条）时进行压缩
                int threshold = AppsettingsConfig.AIAgent?.MemoryExtractHistoryMessages ?? 12; // 默认超过12条（6轮）触发
                int retainCount = 6; // 保留最后6条（3轮对话）的原文

                if (sorted.Count > threshold)
                {
                    var messagesToCompress = sorted.Take(sorted.Count - retainCount).ToList();
                    
                    if (messagesToCompress.Count > 1) // 至少要压缩2条才有意义
                    {
                        _logger.LogInformation("[短期记忆压缩] 会话超过阈值，准备压缩前 {Count} 条消息", messagesToCompress.Count);
                        
                        string compressInput = string.Join("\n", messagesToCompress.Select(m => $"[{m.Role}]: {m.Content}"));

                        // 调用大模型进行摘要
                        var model = await _llmModelService.RecommendModelAsync();
                        if (model != null)
                        {
                            var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(model.Id);
                            string systemPrompt = @"你是一个专业的信息压缩与摘要助手。请将以下早期的用户和助理对话历史压缩为一个简明扼要的情境摘要（Context Summary）。
要求：
1. 包含所有的关键事实、核心实体、用户的主要意图和之前的结论。
2. 尽可能缩减不必要的语气词说明。
3. 请仅仅返回摘要文本，不需要回复多余的内容。";

                            var response = await _llmModelService.ChatAsync(chatClient, compressInput, systemPrompt);
                            string summary = response.Content?.Trim();

                            if (!string.IsNullOrWhiteSpace(summary))
                            {
                                _memoryManagerService.RememberWorkingMemory(userId, summary, "event", 7.2f, sessionId.ToString(), systemId, "context_summary");

                                // 逻辑软删除被压缩的老消息
                                foreach (var oldMsg in messagesToCompress)
                                {
                                    oldMsg.IsDeleted = true;
                                    await _messageService.UpdateAsync(oldMsg);
                                }

                                // 插入摘要消息（作为系统消息类型）以提供给 LLM 注意到之前的背景
                                var summaryMessage = new Chat_MessageEntity
                                {
                                    SessionId = sessionId,
                                    Role = "system",
                                    Content = "## 之前多轮对话压缩摘要 ## \n" + summary,
                                    SequenceNumber = messagesToCompress.Last().SequenceNumber ?? 0,
                                    SentTime = DateTime.Now,
                                    IsDeleted = false
                                };
                                await _messageService.InsertAsync(summaryMessage);
                                
                                _logger.LogInformation("[短期记忆压缩] 成功将老消息替换为系统摘要消息");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[记忆处理] 后台长期抽取与短期压缩失败，Session={SessionID}", sessionId);
            }
        }

        /// <summary>
        /// 保存会话完整上下文（原始对话内容，供后续记忆提取使用）
        /// </summary>
        private async Task SaveSessionContextAsync(long sessionId, string userId, string systemId)
        {
            try
            {
                _logger.LogInformation("[会话上下文] 开始保存 SessionId={SessionId}, UserId={UserId}", sessionId, userId);

                // 获取最近一轮完整对话（用户消息 + 助手回复）
                var allMessages = await _messageService.GetListAsync(m =>
                    m.SessionId == sessionId &&
                    m.IsDeleted == false);

                _logger.LogInformation("[会话上下文] 查询到消息数: {Count}, SessionId={SessionId}", allMessages.Count, sessionId);

                if (allMessages.Count == 0)
                {
                    _logger.LogWarning("[会话上下文] 会话无消息 SessionId={SessionId}", sessionId);
                    return;
                }

                var recentMessages = allMessages.OrderByDescending(m => m.SequenceNumber).Take(4).ToList();

                _logger.LogInformation("[会话上下文] 最近消息: Count={Count}, Roles={Roles}",
                    recentMessages.Count,
                    string.Join(",", recentMessages.Select(m => m.Role)));

                if (recentMessages.Count < 2)
                {
                    _logger.LogWarning("[会话上下文] 消息不足2条 SessionId={SessionId}, Count={Count}", sessionId, recentMessages.Count);
                    return;
                }

                // 按时间正序：用户消息在前，助手回复在后
                var userMessage = recentMessages.FirstOrDefault(m => m.Role == "user");
                var assistantMessage = recentMessages.FirstOrDefault(m => m.Role == "assistant");

                _logger.LogInformation("[会话上下文] 查找结果: user={UserFound}, assistant={AssistantFound}",
                    userMessage != null, assistantMessage != null);

                if (userMessage == null || assistantMessage == null)
                {
                    _logger.LogWarning("[会话上下文] 缺少user或assistant消息 SessionId={SessionId}", sessionId);
                    return;
                }

                // 获取下一个轮次序号
                var turnNumber = await _sessionContextService.GetNextTurnNumberAsync(sessionId);
                _logger.LogInformation("[会话上下文] TurnNumber={TurnNumber}", turnNumber);

                // 构建上下文JSON
                var contextJson = new
                {
                    userMessageId = userMessage.Id,
                    assistantMessageId = assistantMessage.Id,
                    userToolCalls = string.IsNullOrWhiteSpace(userMessage.ToolCalls) ? null : JsonSerializer.Deserialize<object>(userMessage.ToolCalls),
                    assistantToolCalls = string.IsNullOrWhiteSpace(assistantMessage.ToolCalls) ? null : JsonSerializer.Deserialize<object>(assistantMessage.ToolCalls),
                    userAttachments = string.IsNullOrWhiteSpace(userMessage.Attachments) ? null : JsonSerializer.Deserialize<object>(userMessage.Attachments),
                    assistantTokenUsage = string.IsNullOrWhiteSpace(assistantMessage.TokenUsage) ? null : JsonSerializer.Deserialize<object>(assistantMessage.TokenUsage),
                    modelName = assistantMessage.ModelName,
                    workflowId = assistantMessage.WorkflowId,
                    taskId = assistantMessage.TaskId
                };

                // 创建会话上下文实体
                var sessionContext = new Agent_SessionContextEntity
                {
                    SystemId = systemId,
                    UserId = userId,
                    SessionId = sessionId,
                    TurnNumber = turnNumber,
                    UserMessage = userMessage.Content,
                    AssistantMessage = assistantMessage.Content,
                    ContextJson = JsonSerializer.Serialize(contextJson),
                    AgentRoleId = assistantMessage.AgentRoleId,
                    WorkflowId = assistantMessage.WorkflowId,
                    IsMemoryExtracted = false,
                    TokenUsage = assistantMessage.TokenUsage,
                    IsActive = true,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };

                await _sessionContextService.InsertAsync(sessionContext);
                _logger.LogInformation("[会话上下文] 已保存 SessionId={SessionId}, TurnNumber={TurnNumber}, UserId={UserId}", sessionId, turnNumber, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[会话上下文] 保存失败 SessionId={SessionId}, UserId={UserId}", sessionId, userId);
            }
        }

        /// <summary>
        /// 全局后台异步批处理任务（如 Hangfire 定时任务调度）：对全量用户的记忆进行梳理
        /// </summary>
        /// <param name="systemId">系统标识（可选，默认处理所有系统）</param>
        public async Task RunGlobalMemoryMaintenanceJobAsync(string systemId = null)
        {
            try
            {
                _logger.LogInformation("[后台批处理] 启动全局长期记忆去重/合并/压缩任务, SystemId={SystemId}", systemId ?? "全部系统");

                var db = _memoryDbService.GetDb().Context;

                // 获取近期有活跃记忆的用户 ID 列表（去重），按系统分组
                var query = db.Queryable<Agent_MemoryEntity>()
                    .Where(m => m.IsActive == true);

                // 如果指定了 systemId，则只处理该系统
                if (!string.IsNullOrWhiteSpace(systemId))
                {
                    query = query.Where(m => m.SystemId == systemId);
                }

                var distinctUserIds = await query
                    .Select(m => new { m.SystemId, m.UserId })
                    .Distinct()
                    .ToListAsync();

                int processed = 0;
                foreach (var item in distinctUserIds)
                {
                    if (string.IsNullOrWhiteSpace(item.UserId)) continue;

                    await _memoryManagerService.MaintainAndMergeUserMemoriesAsync(item.UserId, item.SystemId);
                    processed++;
                }

                _logger.LogInformation("[后台批处理] 成功完成 {Count} 个用户的记忆归纳整理", processed);
            }
            catch(Exception ex)
            {
                _logger.LogError(ex, "[后台批处理] 全局长期记忆维护作业执行异常");
            }
        }
    }
}