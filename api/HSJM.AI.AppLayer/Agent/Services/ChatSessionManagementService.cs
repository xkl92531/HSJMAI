using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.AI;
using HSJM.AI.Utility;
using Hangfire;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 会话管理服务：处理会话创建、消息存储、历史上下文传递
    /// </summary>
    public class ChatSessionManagementService : ISelfScopedAutoInject
    {
        private readonly IChat_SessionService _sessionService;
        private readonly IChat_MessageService _messageService;
        private readonly IAgent_WorkflowService _workflowService;
        private readonly Hangfire.IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<ChatSessionManagementService> _logger;

        public ChatSessionManagementService(
            IChat_SessionService sessionService,
            IChat_MessageService messageService,
            IAgent_WorkflowService workflowService,
            Hangfire.IBackgroundJobClient backgroundJobClient,
            ILogger<ChatSessionManagementService> logger)
        {
            _sessionService = sessionService;
            _messageService = messageService;
            _workflowService = workflowService;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
        }

        #region 会话管理

        /// <summary>
        /// 创建新会话
        /// </summary>
        public async Task<long> CreateSessionAsync(CreateSessionRequest request)
        {
            var session = new Chat_SessionEntity
            {
                Title = request.Title ?? "新对话",
                Description = request.Description,
                SessionType = request.SessionType ?? "DirectChat",
                Status = 0, // 活跃
                PrimaryAgentRoleId = request.AgentRoleId,
                SessionConfig = request.SessionConfig != null ? JsonSerializer.Serialize(request.SessionConfig) : null,
                StartTime = DateTime.Now,
                MessageCount = 0,
                WorkflowId = request.WorkflowId,
                Tags = request.Tags,
                CreaterId = CurrentUserInfo.Instance.UserID > 0 ? CurrentUserInfo.Instance.UserID : null
            };

            await _sessionService.InsertAsync(session);
            _logger.LogInformation("[会话管理] 创建会话: SessionId={SessionId}, Type={Type}", session.Id, session.SessionType);

            return session.Id;
        }

        /// <summary>
        /// 添加消息到会话
        /// </summary>
        public async Task<long> AddMessageAsync(AddMessageRequest request)
        {
            // 查询会话（验证存在性）
            var session = await _sessionService.GetByIdAsync(request.SessionId);
            if (session == null || session.IsDeleted)
            {
                throw new InvalidOperationException($"会话不存在或已删除: SessionId={request.SessionId}");
            }

            // 获取当前会话的消息序号
            var existingMessages = await _messageService.GetListAsync(
                m => m.SessionId == request.SessionId && m.IsDeleted == false);
            var nextSequence = existingMessages.Any() ? existingMessages.Max(m => m.SequenceNumber ?? 0) + 1 : 1;

            // 创建消息
            var message = new Chat_MessageEntity
            {
                SessionId = request.SessionId,
                Role = request.Role,
                Content = request.Content,
                SequenceNumber = nextSequence,
                ParentMessageId = request.ParentMessageId,
                AgentRoleId = request.AgentRoleId,
                WorkflowId = request.WorkflowId,
                TaskId = request.TaskId,
                ToolCalls = request.ToolCalls != null ? JsonSerializer.Serialize(request.ToolCalls) : null,
                Attachments = request.Attachments != null ? JsonSerializer.Serialize(request.Attachments) : null,
                TokenUsage = request.TokenUsage != null ? JsonSerializer.Serialize(request.TokenUsage) : null,
                ModelName = request.ModelName,
                Status = 0, // 正常
                SentTime = DateTime.Now
            };

            await _messageService.InsertAsync(message);

            // 更新会话的最后消息时间和消息计数
            session.LastMessageTime = DateTime.Now;
            session.MessageCount = (session.MessageCount ?? 0) + 1;
            await _sessionService.UpdateAsync(session);

            _logger.LogInformation("[会话管理] 添加消息: SessionId={SessionId}, Role={Role}, Seq={Seq}",
                request.SessionId, request.Role, nextSequence);

            // 当助理回复完毕（完成一轮对话），异步触发长短期记忆的提炼与压缩任务
            if (request.Role == "assistant")
            {
                _logger.LogInformation("[会话管理] 检测到assistant消息，准备触发后台任务 SessionId={SessionId}", request.SessionId);

                // 优先从当前HTTP请求Token解析userid（最可靠），其次使用session的CreaterId
                var currentUserId = CurrentUserInfo.Instance.UserID;
                var userIdStr = currentUserId > 0
                    ? currentUserId.ToString()
                    : (session.CreaterId?.ToString() ?? "0");

                _logger.LogInformation("[会话管理] 解析userid: currentUserId={CurrentUserId}, userIdStr={UserIdStr}, sessionCreaterId={SessionCreaterId}",
                    currentUserId, userIdStr, session.CreaterId);

                var systemId = request.SystemId ?? "default";
                _backgroundJobClient.Enqueue<MemoryExtractionBackgroundJobService>(
                    job => job.ProcessDialogMemoriesAsync(request.SessionId, userIdStr, systemId));

                _logger.LogInformation("[会话管理] Hangfire任务已投递 SessionId={SessionId}, UserId={UserId}, SystemId={SystemId}",
                    request.SessionId, userIdStr, systemId);
            }

            return message.Id;
        }

        /// <summary>
        /// 获取会话的历史消息（用于传递给LLM）
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        /// <param name="maxMessages">最大消息数（0表示全部）</param>
        /// <param name="includeSystemMessages">是否包含系统消息</param>
        /// <returns>ChatMessage列表</returns>
        public async Task<List<ChatMessage>> GetSessionHistoryAsync(
            long sessionId, 
            int maxMessages = 20,
            bool includeSystemMessages = true)
        {
            var messages = await _messageService.GetListAsync(
                m => m.SessionId == sessionId && m.IsDeleted == false);

            // 按序号排序
            var sortedMessages = messages.OrderBy(m => m.SequenceNumber).ToList();

            // 限制消息数量（保留最近的N条）
            if (maxMessages > 0 && sortedMessages.Count > maxMessages)
            {
                sortedMessages = sortedMessages.Skip(sortedMessages.Count - maxMessages).ToList();
            }

            // 转换为 ChatMessage 格式
            var chatMessages = new List<ChatMessage>();
            foreach (var msg in sortedMessages)
            {
                if (!includeSystemMessages && msg.Role == "system")
                    continue;

                ChatRole role = msg.Role switch
                {
                    "user" => ChatRole.User,
                    "assistant" => ChatRole.Assistant,
                    "system" => ChatRole.System,
                    _ => ChatRole.User
                };

                chatMessages.Add(new ChatMessage(role, msg.Content));
            }

            _logger.LogInformation("[会话管理] 获取历史消息: SessionId={SessionId}, Count={Count}",
                sessionId, chatMessages.Count);

            return chatMessages;
        }

        /// <summary>
        /// 获取会话历史（支持分页）
        /// </summary>
        public async Task<List<Chat_MessageEntity>> GetSessionMessagesAsync(long sessionId, int maxMessages = 10, long? beforeId = null)
        {
            // 验证会话所有权
            var session = await _sessionService.GetByIdAsync(sessionId);
            if (session == null || session.IsDeleted) return new List<Chat_MessageEntity>();
            
            // 如果不是创建者，且不是管理员（可视情调整权限）
            if (session.CreaterId != CurrentUserInfo.Instance.UserID && CurrentUserInfo.Instance.UserID != 0)
            {
                // 可以抛出异常或返回空
                // throw new UnauthorizedAccessException("无权访问此会话");
                return new List<Chat_MessageEntity>();
            }

            // 获取消息
            // 注意：如果 _messageService 不支持复杂查询中的 Take/Top，这里可能性能欠佳
            // 建议后续优化为 Db.Queryable<Chat_MessageEntity>()...
            List<Chat_MessageEntity> messages;
            if (beforeId.HasValue)
            {
                var bid = beforeId.Value;
                messages = await _messageService.GetListAsync(
                    m => m.SessionId == sessionId && m.IsDeleted == false && m.Id < bid);
            }
            else
            {
                messages = await _messageService.GetListAsync(
                    m => m.SessionId == sessionId && m.IsDeleted == false);
            }

            // 按倒序取最近的 N 条，然后再正序排列
            var sortedMessages = messages.OrderByDescending(m => m.SequenceNumber).Take(maxMessages).OrderBy(m => m.SequenceNumber).ToList();

            return sortedMessages;
        }

        /// <summary>
        /// 更新会话标题
        /// </summary>
        public async Task UpdateSessionTitleAsync(long sessionId, string title)
        {
            var session = await _sessionService.GetByIdAsync(sessionId);
            if (session != null && !session.IsDeleted)
            {
                session.Title = title;
                await _sessionService.UpdateAsync(session);
            }
        }

        /// <summary>
        /// 归档会话
        /// </summary>
        public async Task ArchiveSessionAsync(long sessionId)
        {
            var session = await _sessionService.GetByIdAsync(sessionId);
            if (session != null && !session.IsDeleted)
            {
                session.Status = 1; // 已归档
                session.EndTime = DateTime.Now;
                await _sessionService.UpdateAsync(session);
            }
        }

        /// <summary>
        /// 获取活跃会话列表（仅当前用户，分页）
        /// </summary>
        public async Task<List<Chat_SessionEntity>> GetActiveSessionsAsync(int pageIndex = 1, int pageSize = 20)
        {
            var userId = CurrentUserInfo.Instance.UserID;
            // TODO: 后续应改为数据库分页
            var sessions = await _sessionService.GetListAsync(
                s => s.Status == 0 && s.IsDeleted == false && s.CreaterId == userId);
            
            _logger.LogInformation("[会话管理] 获取活跃会话: UserId={UserId}, Total={Total}, Page={Page}", userId, sessions.Count, pageIndex);

            return sessions
                .OrderByDescending(s => s.LastMessageTime ?? s.CreateTime)
                .Skip((pageIndex - 1) * pageSize)
                .Take(pageSize)
                .ToList();
        }

        /// <summary>
        /// 获取会话详情
        /// </summary>
        public async Task<Chat_SessionEntity> GetSessionByIdAsync(long sessionId)
        {
            return await _sessionService.GetByIdAsync(sessionId);
        }

        #endregion

        #region 与 Workflow 集成

        /// <summary>
        /// 将 Workflow 的 CorrelationID 关联到会话
        /// </summary>
        public async Task LinkWorkflowToSessionAsync(long workflowId, long sessionId)
        {
            var workflow = await _workflowService.GetByIdAsync(workflowId);
            if (workflow != null && !workflow.IsDeleted)
            {
                workflow.CorrelationID = sessionId.ToString();
                await _workflowService.UpdateAsync(workflow);
            }

            var session = await _sessionService.GetByIdAsync(sessionId);
            if (session != null && !session.IsDeleted)
            {
                session.WorkflowId = workflowId;
                await _sessionService.UpdateAsync(session);
            }

            _logger.LogInformation("[会话管理] 关联Workflow: WorkflowId={WorkflowId} <-> SessionId={SessionId}",
                workflowId, sessionId);
        }

        /// <summary>
        /// 从 Workflow 创建会话（用于工作流模式）
        /// </summary>
        public async Task<long> CreateSessionFromWorkflowAsync(long workflowId, string userInput)
        {
            var workflow = await _workflowService.GetByIdAsync(workflowId);
            if (workflow == null || workflow.IsDeleted)
            {
                throw new InvalidOperationException($"Workflow不存在: {workflowId}");
            }

            var sessionId = await CreateSessionAsync(new CreateSessionRequest
            {
                Title = workflow.Name,
                Description = workflow.Description,
                SessionType = "WorkflowChat",
                WorkflowId = workflowId,
                Tags = workflow.ThinkingMode
            });

            // 添加首条用户消息
            await AddMessageAsync(new AddMessageRequest
            {
                SessionId = sessionId,
                Role = "user",
                Content = userInput,
                WorkflowId = workflowId
            });

            // 关联 Workflow
            await LinkWorkflowToSessionAsync(workflowId, sessionId);

            return sessionId;
        }

        #endregion
    }

    #region 请求/响应 DTO

    public class CreateSessionRequest
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string SessionType { get; set; }
        public long? AgentRoleId { get; set; }
        public long? WorkflowId { get; set; }
        public string Tags { get; set; }
        public object SessionConfig { get; set; }
    }

    public class AddMessageRequest
    {
        public long SessionId { get; set; }
        public string Role { get; set; }
        public string Content { get; set; }
        public long? ParentMessageId { get; set; }
        public long? AgentRoleId { get; set; }
        public long? WorkflowId { get; set; }
        public long? TaskId { get; set; }
        public object ToolCalls { get; set; }
        public object Attachments { get; set; }
        public object TokenUsage { get; set; }
        public string ModelName { get; set; }
        /// <summary>
        /// 系统标识（用于多系统记忆隔离）
        /// </summary>
        public string SystemId { get; set; } = "default";
    }

    #endregion
}
