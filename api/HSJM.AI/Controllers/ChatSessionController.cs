using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using HSJM.AI.Utility.JWTHelper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// 会话管理控制器：支持多轮对话
    /// </summary>
    [ApiExplorerSettings(GroupName = "AgentTest")]
    [ApiController]
    [Route("api/chat")]
    public class ChatSessionController : ControllerBase
    {
        private readonly ChatSessionManagementService _sessionService;
        private readonly WorkflowOrchestrationService _workflowService;
        private readonly IAgent_WorkflowService _agentworkflow;
        private readonly WorkflowCancellationService _cancellationService;
        private readonly ILogger<ChatSessionController> _logger;

        public ChatSessionController(
            ChatSessionManagementService sessionService,
            IAgent_WorkflowService agentworkflow,
            WorkflowOrchestrationService workflowService,
            WorkflowCancellationService cancellationService,
            ILogger<ChatSessionController> logger)
        {
            _sessionService = sessionService;
            _workflowService = workflowService;
            _cancellationService = cancellationService;
            _agentworkflow = agentworkflow;
            _logger = logger;
        }

        /// <summary>
        /// 创建新会话
        /// </summary>
        [HttpPost("session/create")]
        public async Task<IActionResult> CreateSession([FromBody] CreateSessionApiRequest request)
        {
            try
            {
                var sessionId = await _sessionService.CreateSessionAsync(new CreateSessionRequest
                {
                    Title = request.Title,
                    Description = request.Description,
                    SessionType = request.SessionType ?? "DirectChat",
                    AgentRoleId = request.AgentRoleId,
                    Tags = request.Tags
                });

                return Ok(new
                {
                    success = true,
                    sessionId = sessionId.ToString(),
                    message = "会话创建成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[会话] 创建失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 发送消息（多轮对话）
        /// 在现有会话中发送新消息并获取回复
        /// </summary>
        [HttpPost("message/send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageApiRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Content))
                {
                    return BadRequest(new { success = false, error = "消息内容不能为空" });
                }

                _logger.LogInformation("[会话] 发送消息: SessionId={SessionId}, Content={Content}",
                    request.SessionId, request.Content.Substring(0, Math.Min(50, request.Content.Length)));

                // 调用 Workflow 编排（支持多轮对话）
                var result = await _workflowService.RunAgentOrchestrationAsync(
                    request.Content,
                    request.PlannerRoleId,
                    request.SessionId
                  );

                return Ok(new
                {
                    success = result.Success,
                    workflowId = result.WorkflowId.ToString(),
                    sessionId = result.SessionId?.ToString() ?? request.SessionId?.ToString(),
                    result = result.Result,
                    errorMessage = result.ErrorMessage
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[会话] 发送消息失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 获取会话历史消息
        /// </summary>
        [HttpGet("session/{sessionId}/history")]
        public async Task<IActionResult> GetSessionHistory(long sessionId, [FromQuery] int maxMessages = 50, [FromQuery] long? beforeId = null)
        {
            try
            {
                var messages = await _sessionService.GetSessionMessagesAsync(sessionId, maxMessages, beforeId);

                return Ok(new
                {
                    success = true,
                    sessionId = sessionId.ToString(),
                    messages = messages.ConvertAll(m => new
                    {
                        id = m.Id.ToString(),
                        role = m.Role,
                        content = m.Content,
                        sequenceNumber = m.SequenceNumber,
                        sentTime = m.SentTime,
                        modelName = m.ModelName
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[会话] 获取历史失败: SessionId={SessionId}", sessionId);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 获取活跃会话列表
        /// </summary>
        [HttpGet("sessions/active")]
        public async Task<IActionResult> GetActiveSessions([FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20)
        {
            try
            {
                var sessions = await _sessionService.GetActiveSessionsAsync(pageIndex, pageSize);

                return Ok(new
                {
                    success = true,
                    sessions = sessions.ConvertAll(s => new
                    {
                        id = s.Id.ToString(),
                        title = s.Title,
                        description = s.Description,
                        sessionType = s.SessionType,
                        messageCount = s.MessageCount,
                        lastMessageTime = s.LastMessageTime,
                        createTime = s.CreateTime
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[会话] 获取活跃会话失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 归档会话
        /// </summary>
        [HttpPost("session/{sessionId}/archive")]
        public async Task<IActionResult> ArchiveSession(long sessionId)
        {
            try
            {
                await _sessionService.ArchiveSessionAsync(sessionId);

                return Ok(new
                {
                    success = true,
                    message = "会话已归档"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[会话] 归档失败: SessionId={SessionId}", sessionId);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 更新会话标题
        /// </summary>
        [HttpPost("session/{sessionId}/rename")]
        public async Task<IActionResult> UpdateSessionTitle(long sessionId, [FromBody] UpdateSessionTitleRequest request)
        {
            try
            {
                await _sessionService.UpdateSessionTitleAsync(sessionId, request.Title);

                return Ok(new
                {
                    success = true,
                    message = "会话标题更新成功"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[会话] 更新标题失败: SessionId={SessionId}, Title={Title}", sessionId, request.Title);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 获取会话详情
        /// </summary>
        [HttpGet("session/{sessionId}")]
        public async Task<IActionResult> GetSession(long sessionId)
        {
            try
            {
                var session = await _sessionService.GetSessionByIdAsync(sessionId);
                if (session == null || session.IsDeleted)
                {
                    return NotFound(new { success = false, error = "会话不存在" });
                }

                return Ok(new
                {
                    success = true,
                    session = new
                    {
                        id = session.Id.ToString(),
                        title = session.Title,
                        description = session.Description,
                        sessionType = session.SessionType,
                        status = session.Status,
                        messageCount = session.MessageCount,
                        workflowId = session.WorkflowId?.ToString(),
                        lastMessageTime = session.LastMessageTime,
                        startTime = session.StartTime,
                        endTime = session.EndTime,
                        createTime = session.CreateTime
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[会话] 获取详情失败: SessionId={SessionId}", sessionId);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 取消/中止工作流执行（停止响应按钮）
        /// </summary>
        [HttpPost("workflow/{workflowId}/cancel")]
        public async Task<IActionResult> CancelWorkflow(long workflowId)
        {
            try
            {
                _logger.LogInformation("[取消] 收到取消请求: WorkflowId={WorkflowId}", workflowId);

                // 调用取消服务
                var cancelled = _cancellationService.CancelWorkflow(workflowId);

                if (cancelled)
                {
                    // 更新工作流状态
                    await _workflowService.MarkWorkflowCancelledAsync(workflowId);

                    return Ok(new
                    {
                        success = true,
                        message = "工作流已取消",
                        workflowId = workflowId.ToString()
                    });
                }

                // 🆕 增强逻辑：如果 CancelWorkflow 返回 false (未找到CTS)，可能是任务还在排队未启动
                // 此时直接更新数据库状态，确保任务启动时能看到已取消状态
                var workflow = await _agentworkflow.GetByIdAsync(workflowId);
                // 状态: 0=Initial, 1=Running, 2=Wait, 3=Success, 4=Fail, 5=Cancelled (假设状态码，根据之前的代码推断 3是完成, 4是失败, 5是取消)
                // 只要不是完成(3)、失败(4)、已取消(5)，都认为是可取消的
                if (workflow != null && workflow.Status != 3 && workflow.Status != 4 && workflow.Status != 5)
                {
                    await _workflowService.MarkWorkflowCancelledAsync(workflowId);
                    _logger.LogInformation("[取消] 工作流未启动但存在，强制标记取消: WorkflowId={WorkflowId}", workflowId);

                    return Ok(new
                    {
                        success = true,
                        message = "工作流已标记取消 (尚未启动)",
                        workflowId = workflowId.ToString()
                    });
                }

                return Ok(new
                {
                    success = false,
                    message = "工作流不存在或已完成",
                    workflowId = workflowId.ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[取消] 取消工作流失败: WorkflowId={WorkflowId}", workflowId);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        ///// <summary>
        ///// 记忆存储接口 - 供外部系统 Agent 调用
        ///// </summary>
        //[HttpPost("memory/save")]
        //public async Task<IActionResult> SaveMemory([FromBody] SaveMemoryApiRequest request)
        //{
        //    try
        //    {
        //        if (request == null || string.IsNullOrWhiteSpace(request.Content))
        //        {
        //            return BadRequest(new { success = false, error = "content 不能为空" });
        //        }

        //        var userId = GetUserIdFromToken();
        //        if (string.IsNullOrWhiteSpace(userId))
        //        {
        //            return BadRequest(new { success = false, error = "无法从 Token 中解析用户信息，请检查 Authorization 头" });
        //        }

        //        var content = request.Content.Trim();
        //        var memoryType = string.IsNullOrWhiteSpace(request.MemoryType) ? "fact" : request.MemoryType.Trim();
        //        var importanceScore = Math.Clamp(request.ImportanceScore <= 0 ? 5.0f : request.ImportanceScore, 1.0f, 10.0f);
        //        var sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId.Trim();
        //        var systemId = string.IsNullOrWhiteSpace(request.SystemId) ? "default" : request.SystemId.Trim();

        //        var result = await _workflowService.SaveMemoryAsync(
        //            userId,
        //            content,
        //            memoryType,
        //            importanceScore,
        //            request.IsHighQuality,
        //            sessionId,
        //            systemId,
        //            request.PinToCore,
        //            request.TrackLineage,
        //            request.ConflictKey,
        //            request.ExtractGraph);

        //        return Ok(new
        //        {
        //            success = result.Success,
        //            message = result.Message,
        //            error = result.ErrorMessage,
        //            isCoreMemory = result.IsCoreMemory,
        //            normalizedMemoryType = result.NormalizedMemoryType,
        //            memoryId = result.MemoryId,
        //            lineageId = result.LineageId,
        //            conflictKey = result.ConflictKey,
        //            graphRelationCount = result.GraphRelationCount
        //        });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "[记忆存储] 保存失败");
        //        return StatusCode(500, new { success = false, error = ex.Message });
        //    }
        //}

        ///// <summary>
        ///// 记忆提取接口 - 根据对话信息提取记忆并自动保存
        ///// </summary>
        //[HttpPost("memory/extract")]
        //public async Task<IActionResult> ExtractMemory([FromBody] ExtractMemoryApiRequest request)
        //{
        //    try
        //    {
        //        if (request == null)
        //        {
        //            return BadRequest(new { success = false, error = "请求体不能为空" });
        //        }

        //        var dialogText = string.IsNullOrWhiteSpace(request.DialogText) ? null : request.DialogText.Trim();
        //        var messages = request.Messages?
        //            .Where(m => !string.IsNullOrWhiteSpace(m?.UserMessage) || !string.IsNullOrWhiteSpace(m?.AssistantMessage))
        //            .Select(m => ((m.UserMessage ?? string.Empty).Trim(), (m.AssistantMessage ?? string.Empty).Trim()))
        //            .ToList();

        //        var hasDialogText = !string.IsNullOrWhiteSpace(dialogText);
        //        var hasMessages = messages != null && messages.Count > 0;

        //        if (!hasDialogText && !hasMessages)
        //        {
        //            return BadRequest(new { success = false, error = "dialogText 或 messages 不能同时为空" });
        //        }

        //        var userId = GetUserIdFromToken();
        //        if (string.IsNullOrWhiteSpace(userId))
        //        {
        //            return BadRequest(new { success = false, error = "无法从 Token 中解析用户信息，请检查 Authorization 头" });
        //        }

        //        var result = await _workflowService.ExtractMemoryAsync(
        //            userId,
        //            dialogText,
        //            string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId.Trim(),
        //            string.IsNullOrWhiteSpace(request.SystemId) ? "default" : request.SystemId.Trim(),
        //            messages,
        //            request.PromoteToCore,
        //            request.TrackLineage,
        //            request.ExtractRelations);

        //        return Ok(new
        //        {
        //            success = result.Success,
        //            message = result.Message,
        //            error = result.ErrorMessage,
        //            extractedMemories = result.ExtractedMemories,
        //            savedCount = result.SavedCount,
        //            coreMemoryCount = result.CoreMemoryCount,
        //            coreMemorySummary = result.CoreMemorySummary,
        //            graphRelations = result.GraphRelations
        //        });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "[记忆提取] 失败");
        //        return StatusCode(500, new { success = false, error = ex.Message });
        //    }
        //}

        /// <summary>
        /// 从请求头Token中解析userId
        /// </summary>
        private string? GetUserIdFromToken()
        {
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader.Substring(7);
                try
                {
                    var payload = JWTEncryption.Decrypt(token);
                    if (payload.TryGetValue("CLAINM_USERID", out var userId) && !string.IsNullOrWhiteSpace(userId))
                    {
                        return userId;
                    }
                }
                catch
                {
                    // Token解析失败，返回null
                }
            }
            return null;
        }

        ///// <summary>
        ///// 记忆检索接口 - 供外部系统 Agent 调用
        ///// </summary>
        //[HttpPost("memory/search")]
        //public async Task<IActionResult> SearchMemory([FromBody] SearchMemoryApiRequest request)
        //{
        //    try
        //    {
        //        if (request == null || string.IsNullOrWhiteSpace(request.QueryText))
        //        {
        //            return BadRequest(new { success = false, error = "queryText 不能为空" });
        //        }

        //        var userId = GetUserIdFromToken();
        //        if (string.IsNullOrWhiteSpace(userId))
        //        {
        //            return BadRequest(new { success = false, error = "无法从Token中解析用户信息，请检查Authorization头" });
        //        }

        //        var queryText = request.QueryText.Trim();
        //        var systemId = string.IsNullOrWhiteSpace(request.SystemId) ? "default" : request.SystemId.Trim();
        //        var topK = Math.Clamp(request.TopK ?? 5, 1, 20);
        //        var coreTopK = Math.Clamp(request.CoreTopK ?? 5, 1, 20);

        //        var result = await _workflowService.SearchMemoryAsync(
        //            userId,
        //            queryText,
        //            topK,
        //            request.MemoryType,
        //            request.IsHighQualityOnly,
        //            systemId,
        //            request.IncludeCoreMemory,
        //            coreTopK,
        //            request.TimeRange,
        //            request.StartTime,
        //            request.EndTime,
        //            request.PreferKeywordSearch,
        //            request.IncludeHistory,
        //            request.EnableGraphSearch,
        //            request.GraphHop);

        //        return Ok(new
        //        {
        //            success = result.Success,
        //            error = result.ErrorMessage,
        //            timeRangeApplied = result.TimeRangeApplied,
        //            preferKeywordSearch = result.PreferKeywordSearch,
        //            includeHistory = result.IncludeHistory,
        //            enableGraphSearch = result.EnableGraphSearch,
        //            coreMemorySummary = result.CoreMemorySummary,
        //            graphFactsSummary = result.GraphFactsSummary,
        //            coreMemories = result.CoreMemories?.Select(m => new
        //            {
        //                id = m.Id,
        //                content = m.Content,
        //                memoryType = m.MemoryType,
        //                importanceScore = m.ImportanceScore,
        //                isHighQuality = m.IsHighQuality,
        //                createdAt = m.CreatedAt,
        //                lineageId = m.LineageId,
        //                supersedesMemoryId = m.SupersedesMemoryId,
        //                conflictKey = m.ConflictKey,
        //                memoryStatus = m.MemoryStatus,
        //                validFrom = m.ValidFrom,
        //                validTo = m.ValidTo
        //            }).ToList(),
        //            memories = result.Memories?.Select(m => new
        //            {
        //                id = m.Id,
        //                content = m.Content,
        //                memoryType = m.MemoryType,
        //                importanceScore = m.ImportanceScore,
        //                isHighQuality = m.IsHighQuality,
        //                createdAt = m.CreatedAt,
        //                lineageId = m.LineageId,
        //                supersedesMemoryId = m.SupersedesMemoryId,
        //                conflictKey = m.ConflictKey,
        //                memoryStatus = m.MemoryStatus,
        //                validFrom = m.ValidFrom,
        //                validTo = m.ValidTo
        //            }).ToList(),
        //            lineageMemories = result.LineageMemories?.Select(m => new
        //            {
        //                id = m.Id,
        //                content = m.Content,
        //                memoryType = m.MemoryType,
        //                importanceScore = m.ImportanceScore,
        //                isHighQuality = m.IsHighQuality,
        //                createdAt = m.CreatedAt,
        //                lineageId = m.LineageId,
        //                supersedesMemoryId = m.SupersedesMemoryId,
        //                conflictKey = m.ConflictKey,
        //                memoryStatus = m.MemoryStatus,
        //                validFrom = m.ValidFrom,
        //                validTo = m.ValidTo
        //            }).ToList(),
        //            graphRelations = result.GraphRelations
        //        });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "[记忆检索] 搜索失败: Query={Query}", request?.QueryText);
        //        return StatusCode(500, new { success = false, error = ex.Message });
        //    }
        //}

        /// <summary>
        /// 测试接口：手动触发记忆处理后台任务
        /// </summary>
        [HttpPost("test/trigger-memory-job")]
        public async Task<IActionResult> TestTriggerMemoryJob([FromQuery] long sessionId, [FromQuery] string userId = "0", [FromQuery] string systemId = "default")
        {
            try
            {
                _logger.LogInformation("[测试] 手动触发记忆处理任务 SessionId={SessionId}, UserId={UserId}, SystemId={SystemId}",
                    sessionId, userId, systemId);

                var memoryService = HttpContext.RequestServices.GetService<MemoryExtractionBackgroundJobService>();
                if (memoryService == null)
                {
                    return Ok(new { success = false, error = "MemoryExtractionBackgroundJobService未注册" });
                }

                await memoryService.ProcessDialogMemoriesAsync(sessionId, userId, systemId);

                return Ok(new { success = true, message = "记忆处理任务执行完成" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[测试] 记忆处理任务执行失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
    }

    public class UpdateSessionTitleRequest
    {
        public string Title { get; set; } = string.Empty;
    }

    public class CreateSessionApiRequest
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SessionType { get; set; } = "DirectChat";
        public long? AgentRoleId { get; set; }
        public string Tags { get; set; } = string.Empty;
    }

    public class SendMessageApiRequest
    {
        /// <summary>
        /// 会话ID（可选，不传则创建新会话）
        /// </summary>
        public long? SessionId { get; set; }

        /// <summary>
        /// 消息内容
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Planner角色ID（可选）
        /// </summary>
        public long? PlannerRoleId { get; set; }

        /// <summary>
        /// 系统标识（用于多系统记忆隔离）
        /// </summary>
        public string SystemId { get; set; } = "default";
    }

    /// <summary>
    /// 记忆存储请求模型
    /// </summary>
    public class SaveMemoryApiRequest
    {
        /// <summary>
        /// 记忆内容（必填）
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 记忆类型：preference/fact/event（默认 fact）
        /// </summary>
        public string? MemoryType { get; set; } = "fact";

        /// <summary>
        /// 重要性得分 1-10（默认 5）
        /// </summary>
        public float ImportanceScore { get; set; } = 5.0f;

        /// <summary>
        /// 是否高质量记忆（默认 false）
        /// </summary>
        public bool IsHighQuality { get; set; }

        /// <summary>
        /// 来源会话ID（可选）
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// 是否固定到核心工作记忆（Persona / User Profile）
        /// </summary>
        public bool PinToCore { get; set; }

        /// <summary>
        /// 是否启用版本溯源（默认 true）
        /// </summary>
        public bool TrackLineage { get; set; } = true;

        /// <summary>
        /// 外部显式传入的冲突主题键（可选）
        /// </summary>
        public string? ConflictKey { get; set; }

        /// <summary>
        /// 是否抽取并保存实体关系（默认 true）
        /// </summary>
        public bool ExtractGraph { get; set; } = true;

        /// <summary>
        /// 系统标识（用于多系统记忆隔离，默认 default）
        /// </summary>
        public string SystemId { get; set; } = "default";
    }

    /// <summary>
    /// 记忆检索请求模型
    /// </summary>
    public class SearchMemoryApiRequest
    {
        /// <summary>
        /// 检索关键词/问题（必填）
        /// </summary>
        public string QueryText { get; set; } = string.Empty;

        /// <summary>
        /// 返回数量（默认 5）
        /// </summary>
        public int? TopK { get; set; }

        /// <summary>
        /// 记忆类型过滤：preference/fact/event（可选，不传则返回所有类型）
        /// </summary>
        public string? MemoryType { get; set; }

        /// <summary>
        /// 仅检索高质量记忆（默认 false）
        /// </summary>
        public bool IsHighQualityOnly { get; set; }

        /// <summary>
        /// 是否同时返回核心工作记忆（默认 true）
        /// </summary>
        public bool IncludeCoreMemory { get; set; } = true;

        /// <summary>
        /// 核心工作记忆返回条数（默认 5）
        /// </summary>
        public int? CoreTopK { get; set; } = 5;

        /// <summary>
        /// 时间范围过滤，可选：today/yesterday/last_7_days/last_week/last_month/all，也支持中文描述
        /// </summary>
        public string? TimeRange { get; set; }

        /// <summary>
        /// 自定义开始时间（可选）
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// 自定义结束时间（可选）
        /// </summary>
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// 是否优先按关键词匹配检索（默认 false）
        /// </summary>
        public bool PreferKeywordSearch { get; set; }

        /// <summary>
        /// 是否带出记忆历史链（默认 false，历史类问题会自动启用）
        /// </summary>
        public bool IncludeHistory { get; set; }

        /// <summary>
        /// 是否启用 GraphRAG 关系检索（默认 false，关系类问题会自动启用）
        /// </summary>
        public bool EnableGraphSearch { get; set; }

        /// <summary>
        /// 图关系扩展跳数（1-2，默认 1）
        /// </summary>
        public int GraphHop { get; set; } = 1;

        /// <summary>
        /// 系统标识（用于多系统记忆隔离，默认 default）
        /// </summary>
        public string SystemId { get; set; } = "default";
    }

    /// <summary>
    /// 单轮对话消息
    /// </summary>
    public class DialogTurnRequest
    {
        /// <summary>
        /// 用户消息内容（必填）
        /// </summary>
        public string UserMessage { get; set; } = string.Empty;

        /// <summary>
        /// 助手回复内容（可选）
        /// </summary>
        public string AssistantMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// 记忆提取请求模型
    /// </summary>
    public class ExtractMemoryApiRequest
    {
        /// <summary>
        /// 对话内容文本（与 Messages 二选一，若同时传入则优先使用 Messages 拼接）
        /// </summary>
        public string? DialogText { get; set; }

        /// <summary>
        /// 结构化对话轮次列表（推荐使用，每轮包含 userMessage 和 assistantMessage）
        /// </summary>
        public List<DialogTurnRequest>? Messages { get; set; }

        /// <summary>
        /// 会话ID（可选）
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// 是否将提取出的偏好/身份信息提升到核心工作记忆（默认 true）
        /// </summary>
        public bool PromoteToCore { get; set; } = true;

        /// <summary>
        /// 是否启用版本溯源（默认 true）
        /// </summary>
        public bool TrackLineage { get; set; } = true;

        /// <summary>
        /// 是否抽取实体关系（默认 true）
        /// </summary>
        public bool ExtractRelations { get; set; } = true;

        /// <summary>
        /// 系统标识（用于多系统记忆隔离，默认 default）
        /// </summary>
        public string? SystemId { get; set; } = "default";
    }
}