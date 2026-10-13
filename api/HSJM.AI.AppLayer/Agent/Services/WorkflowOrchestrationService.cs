using Hangfire;
using HSJM.AI.AppLayer.Agent.Models;
using HSJM.AI.AppLayer.Hubs;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HSJM.AI.Utility;
using Microsoft.Extensions.AI;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 工作流编排服务（多智能体协作）
    /// 职责：创建工作流并提交到 Hangfire 后台执行，立即返回 WorkflowId
    /// 参考: WorkflowOrchestrationApp.cs 中的编排模式
    /// 🆕 阶段1.1 优化：使用 partial class 分离辅助方法，完整利用 Entity 字段
    /// </summary>
    public partial class WorkflowOrchestrationService : ISelfScopedAutoInject
    {
        private readonly IAgent_WorkflowService _workflowService;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<WorkflowOrchestrationService> _logger;
        private readonly IHubContext<AgentStreamingHub> _hubContext;
        private readonly IAgent_RoleService _agentRoleService;
        private readonly LLMModelService _llmModelService;
        private readonly ChatSessionManagementService _chatSessionService;
        private readonly MemoryManagerService _memoryManagerService;
        private readonly DiagnosticCorrectionSkill _diagnosticCorrectionSkill;
        
        public WorkflowOrchestrationService(
            IAgent_WorkflowService workflowService,
            IBackgroundJobClient backgroundJobClient,
            IHubContext<AgentStreamingHub> hubContext,
            IAgent_RoleService agentRoleService,
            LLMModelService llmModelService,
            ChatSessionManagementService chatSessionService,
            MemoryManagerService memoryManagerService,
            DiagnosticCorrectionSkill diagnosticCorrectionSkill,
            ILogger<WorkflowOrchestrationService> logger)
        {
            _workflowService = workflowService;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
            _agentRoleService = agentRoleService;
            _llmModelService = llmModelService;
            _chatSessionService = chatSessionService;
            _memoryManagerService = memoryManagerService;
            _diagnosticCorrectionSkill = diagnosticCorrectionSkill;
            _hubContext = hubContext;
        }

        #region 意图路由返回结果

        /// <summary>
        /// 意图路由分析结果
        /// </summary>
        public class IntentRouteResult
        {
            /// <summary>目标智能体ID</summary>
            public long TargetAgentId { get; set; }
            /// <summary>强化后的用户请求（包含意图分析结果）</summary>
            public string EnhancedRequest { get; set; }
            /// <summary>意图摘要</summary>
            public string IntentSummary { get; set; }
            /// <summary>路由原因</summary>
            public string Reason { get; set; }
            /// <summary>是否成功</summary>
            public bool Success { get; set; }
        }

        #endregion

        /// <summary>
        /// 智能分析意图并路由（增强版）
        /// 🆕 改进：
        /// 1. 加载最近的会话历史，帮助Router理解上下文
        /// 2. 返回强化后的用户需求，方便后续处理
        /// </summary>
        /// <param name="userInput">用户输入</param>
        /// <param name="sessionId">会话ID（用于加载历史）</param>
        private async Task<IntentRouteResult> AnalyzeIntentAndRouteAsync(string userInput, long? sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(userInput))
                return new IntentRouteResult { Success = false, EnhancedRequest = userInput };
            
            try
            {
                // 1. 获取 Router 角色 (ID=2000)
                var routerRole = await _agentRoleService.GetByIdAsync(2000);
                if (routerRole == null)
                {
                    _logger.LogWarning("[意图路由] 未找到ID=2000的Router角色，尝试按Code查找");
                    var roles = await _agentRoleService.GetListAsync(r => r.RoleCode == "router" && r.IsDeleted == false);
                    routerRole = roles.FirstOrDefault();
                }

                // 🆕 2. 加载最近的会话历史（帮助Router理解上下文）
                string historyContext = "";
                if (sessionId.HasValue && sessionId.Value > 0)
                {
                    try
                    {
                        var history = await _chatSessionService.GetSessionHistoryAsync(sessionId.Value, maxMessages: 5);
                        if (history.Count > 0)
                        {
                            var historyLines = history.Select(m => $"[{(m.Role == ChatRole.User ? "用户" : "助手")}]: {m.Text?.Substring(0, Math.Min(m.Text?.Length ?? 0, 200))}{(m.Text?.Length > 200 ? "..." : "")}");
                            historyContext = $"### 最近对话历史（供理解上下文）\n{string.Join("\n", historyLines)}\n\n";
                            _logger.LogInformation("[意图路由] 已加载{Count}条历史消息", history.Count);
                        }
                    }
                    catch (Exception histEx)
                    {
                        _logger.LogWarning(histEx, "[意图路由] 加载历史消息失败，继续执行");
                    }
                }

                // 🆕 3. 构建增强版系统提示词
                string systemPrompt;
                if (routerRole != null && !string.IsNullOrWhiteSpace(routerRole.SystemPrompt))
                {
                    systemPrompt = routerRole.SystemPrompt;
                }
                else
                {
                    systemPrompt = @"你是智能路由助手。请分析用户输入，完成以下任务：
1. 从给定的智能体列表中选择最合适的 Agent ID
2. 强化用户需求，补充隐含的意图和上下文信息
3. 生成意图摘要

必须返回严格的 JSON 格式:
{
    ""target_agent_id"": 数字ID,
    ""enhanced_request"": ""强化后的用户需求，包含明确的任务目标和上下文"",
    ""intent_summary"": ""简短的意图摘要，不超过20字"",
    ""reason"": ""选择该智能体的原因""
}

注意：
- enhanced_request 应该比原始输入更加明确和具体
- 如果历史对话中有相关上下文，请整合到 enhanced_request 中
- 如果用户说『继续』、『还是这个』等指代性语言，请根据历史补全具体内容";
                }

                // 4. 获取候选智能体列表
                var candidates = new List<Agent_RoleEntity>();
                var generalRoles = await _agentRoleService.GetListAsync(r => (r.RoleCode == "general_assistant" || r.Id == 2002 ) && r.IsEnabled == true && r.IsDeleted == false);
                candidates.AddRange(generalRoles);
                var plannerRoles = await _agentRoleService.GetListAsync(r => (r.RoleCode.Contains("planner")) && r.IsEnabled == true && r.IsDeleted == false);
                foreach(var p in plannerRoles) { if(!candidates.Any(c => c.Id == p.Id)) candidates.Add(p); }

                // 5. 构建动态列表
                var candidateInfo = string.Join("\n", candidates.Select(c => $"- [ID={c.Id}] 【{c.RoleName}】: {c.Description ?? "无描述"}"));
                var promptWithContext = $"{systemPrompt}\n\n### 当前可用智能体列表\n{candidateInfo}";

                // 🆕 6. 构建带历史上下文的用户输入
                var contextualUserInput = string.IsNullOrEmpty(historyContext)
                    ? $"### 当前用户输入\n{userInput}"
                    : $"{historyContext}### 当前用户输入\n{userInput}";

                // 7. 调用 LLM
                // 7. 调用 LLM
                var model = await _llmModelService.RecommendModelAsync();
                var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(model?.Id ?? 0);
                var client = chatClient.AsIChatClient(); // 转换为 MEAI Client

                var messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, promptWithContext),
                    new ChatMessage(ChatRole.User, contextualUserInput)
                };

                var response = await client.GetResponseAsync(messages);
                var responseContent = response.Text ?? string.Empty;
                
                // 8. 解析 JSON
                // 8. 解析 JSON
                var jsonStr = ExtractJson(responseContent);
                var result = JsonHelper.DeserializeObject<Dictionary<string, object>>(jsonStr);
                
                if (result != null && result.TryGetValue("target_agent_id", out var idObj) && long.TryParse(idObj.ToString(), out long targetId))
                {
                    var targetAgent = candidates.FirstOrDefault(c => c.Id == targetId);
                    
                    // 🆕 提取强化后的请求和意图摘要
                    var enhancedRequest = result.TryGetValue("enhanced_request", out var enhReq) 
                        ? enhReq?.ToString() ?? userInput 
                        : userInput;
                    var intentSummary = result.TryGetValue("intent_summary", out var summary) 
                        ? summary?.ToString() ?? "" 
                        : "";
                    var reason = result.TryGetValue("reason", out var reasonObj) 
                        ? reasonObj?.ToString() ?? "" 
                        : "";
                    
                    _logger.LogInformation("[意图路由] 选中 Agent: {Name} (ID={Id}), 意图: {Intent}", 
                        targetAgent?.RoleName, targetId, intentSummary);
                    
                    return new IntentRouteResult
                    {
                        TargetAgentId = targetId,
                        EnhancedRequest = enhancedRequest,
                        IntentSummary = intentSummary,
                        Reason = reason,
                        Success = true
                    };
                }
                
                return new IntentRouteResult 
                { 
                    Success = false, 
                    EnhancedRequest = userInput,
                    Reason = "无法解析路由结果"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[意图路由] 分析异常");
                return new IntentRouteResult 
                { 
                    Success = false, 
                    EnhancedRequest = userInput,
                    Reason = ex.Message
                };
            }
        }

        private string ExtractJson(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "{}";
            var start = content.IndexOf("{");
            var end = content.LastIndexOf("}");
            if (start >= 0 && end > start) return content.Substring(start, end - start + 1);
            return content;
        }


        #region 模式1: 条件路由（Router + 专家池）

        /// <summary>
        /// 条件路由模式：创建工作流并提交到 Hangfire 后台执行
        /// 
        /// 参考: WorkflowOrchestrationApp.RunConditionalRoutingAsync
        /// 流程：创建 Workflow → 提交 Hangfire Job → 立即返回 WorkflowId
        /// </summary>
        /// <param name="userInput">用户输入</param>
        /// <param name="routerRoleId">Router 角色ID（可为 null，系统自动选择）</param>
        /// <param name="expertRoleIds">专家角色映射</param>
        /// <returns>工作流ID，可用于 SignalR 订阅进度</returns>
        public async Task<WorkflowExecutionResult> RunConditionalRoutingAsync(
            string userInput,
            long? routerRoleId, // 改为可空，支持智能选择
            Dictionary<long, string> expertRoleIds)
        {
            _logger.LogInformation("[条件路由] 开始创建工作流");

            try
            {
                // 🆕 阶段1.1：智能化 Workflow 创建
                var traceId = Guid.NewGuid().ToString();
                var workflowName = GenerateWorkflowName("条件路由", userInput);
                var thinkingMode = DetermineThinkingMode(userInput, expertRoleIds?.Count ?? 0);
                var priority = CalculatePriority(userInput);
                var estimatedDuration = EstimateWorkflowDuration(expertRoleIds?.Count ?? 0);
                var planDAG = GeneratePlanDAG(routerRoleId, expertRoleIds);
                
                // 创建 Workflow 记录（完整字段）
                var workflow = new Agent_WorkflowEntity
                {
                    Name = workflowName,
                    Description = userInput,
                    TriggerType = "User",
                    ThinkingMode = thinkingMode,
                    Status = 1, // 1=规划中（等待 Hangfire 接管）
                    Priority = priority,
                    
                    // 🆕 新增智能字段
                    PlannerAgentID = routerRoleId,
                    TraceId = traceId,
                    EstimatedDuration = estimatedDuration,
                    PlanDAG = planDAG,
                    PlanStatus = "Draft",
                    GlobalContext = JsonHelper.SerializeObject(new
                    {
                        SessionId = Guid.NewGuid().ToString(),
                        UserInput = userInput,
                        ExpertCount = expertRoleIds?.Count ?? 0,
                        StartedAt = DateTime.Now,
                        Mode = "ConditionalRouting"
                    })
                };
                await _workflowService.InsertAsync(workflow);
                
                _logger.LogInformation("[条件路由] Workflow 创建成功: TraceId={TraceId}, Priority={Priority}, 预计耗时={EstDuration}秒",
                    traceId, priority, estimatedDuration);

                // 2. 提交到 Hangfire 后台执行
                var jobId = _backgroundJobClient.Enqueue<WorkflowBackgroundJobService>(
                    job => job.ExecuteConditionalRoutingAsync(
                        workflow.Id, userInput, routerRoleId, expertRoleIds));

                _logger.LogInformation("[条件路由] 已创建 Workflow={WorkflowId}, HangfireJob={JobId}",
                    workflow.Id, jobId);

                return new WorkflowExecutionResult
                {
                    WorkflowId = workflow.Id,
                    Success = true,
                    Result = $"Workflow 已创建，正在后台执行。请通过 SignalR 订阅 'Workflow_{workflow.Id}' 查看实时进度。",
                    ExecutionTimeMs = 0 // 立即返回，不等待执行
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[条件路由] 创建失败");
                return new WorkflowExecutionResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        #endregion

        #region 模式2: FanOut/FanIn 并行处理

        public async Task<WorkflowExecutionResult> RunFanOutFanInAsync(
            string userInput,
            List<long> parallelRoleIds)
        {
            _logger.LogInformation("[FanOut/FanIn] 开始创建工作流");

            try
            {
                // 🆕 阶段1.1：智能化 Workflow 创建
                var traceId = Guid.NewGuid().ToString();
                var workflowName = GenerateWorkflowName("FanOut/FanIn并行", userInput);
                var priority = CalculatePriority(userInput);
                var estimatedDuration = EstimateWorkflowDuration(parallelRoleIds?.Count ?? 0);
                
                var workflow = new Agent_WorkflowEntity
                {
                    Name = workflowName,
                    Description = userInput,
                    TriggerType = "User",
                    ThinkingMode = "Auto",
                    Status = 1,
                    Priority = priority,
                    
                    // 🆕 新增智能字段
                    TraceId = traceId,
                    EstimatedDuration = estimatedDuration,
                    PlanStatus = "Draft",
                    GlobalContext = JsonHelper.SerializeObject(new
                    {
                        SessionId = Guid.NewGuid().ToString(),
                        UserInput = userInput,
                        ParallelCount = parallelRoleIds?.Count ?? 0,
                        StartedAt = DateTime.Now,
                        Mode = "FanOutFanIn"
                    })
                };
                await _workflowService.InsertAsync(workflow);

                var jobId = _backgroundJobClient.Enqueue<WorkflowBackgroundJobService>(
                    job => job.ExecuteFanOutFanInAsync(workflow.Id, userInput, parallelRoleIds));

                _logger.LogInformation("[FanOut/FanIn] 已创建 Workflow={WorkflowId}, HangfireJob={JobId}",
                    workflow.Id, jobId);

                return new WorkflowExecutionResult
                {
                    WorkflowId = workflow.Id,
                    Success = true,
                    Result = $"Workflow 已创建，正在后台执行。请通过 SignalR 订阅 'Workflow_{workflow.Id}' 查看实时进度。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FanOut/FanIn] 创建失败");
                return new WorkflowExecutionResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        #endregion

        #region 模式3: 顺序链（Planner → Executor → Reviewer）

        public async Task<WorkflowExecutionResult> RunSequentialChainAsync(
            string userInput,
            long plannerRoleId,
            long executorRoleId,
            long reviewerRoleId)
        {
            _logger.LogInformation("[顺序链] 开始创建工作流");

            try
            {
                // 🆕 阶段1.1：智能化 Workflow 创建
                var traceId = Guid.NewGuid().ToString();
                var workflowName = GenerateWorkflowName("顺序链", userInput);
                var priority = CalculatePriority(userInput);
                var estimatedDuration = EstimateWorkflowDuration(3); // Planner + Executor + Reviewer
                var planDAG = GenerateSequentialChainDAG(plannerRoleId, executorRoleId, reviewerRoleId);
                
                var workflow = new Agent_WorkflowEntity
                {
                    Name = workflowName,
                    Description = userInput,
                    TriggerType = "User",
                    ThinkingMode = "PlanAndExecute",
                    Status = 1,
                    Priority = priority,
                    
                    // 🆕 新增智能字段
                    PlannerAgentID = plannerRoleId,
                    TraceId = traceId,
                    EstimatedDuration = estimatedDuration,
                    PlanDAG = planDAG,
                    PlanStatus = "Draft",
                    GlobalContext = JsonHelper.SerializeObject(new
                    {
                        SessionId = Guid.NewGuid().ToString(),
                        UserInput = userInput,
                        StartedAt = DateTime.Now,
                        Mode = "SequentialChain",
                        Roles = new { Planner = plannerRoleId, Executor = executorRoleId, Reviewer = reviewerRoleId }
                    })
                };
                await _workflowService.InsertAsync(workflow);

                var jobId = _backgroundJobClient.Enqueue<WorkflowBackgroundJobService>(
                    job => job.ExecuteSequentialChainAsync(
                        workflow.Id, userInput, plannerRoleId, executorRoleId, reviewerRoleId));

                _logger.LogInformation("[顺序链] 已创建 Workflow={WorkflowId}, HangfireJob={JobId}",
                    workflow.Id, jobId);

                return new WorkflowExecutionResult
                {
                    WorkflowId = workflow.Id,
                    Success = true,
                    Result = $"Workflow 已创建，正在后台执行。请通过 SignalR 订阅 'Workflow_{workflow.Id}' 查看实时进度。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[顺序链] 创建失败");
                throw;
            }
        }

        #endregion

     

        #region 模式: Agent Orchestration（Planner 自主规划）

        /// <summary>
        /// Agent Orchestration 模式：平台通用，零硬编码
        /// 
        /// 核心理念：
        /// - 前端只传用户需求（纯文本）
        /// - Planner 智能体分析需求，自主生成执行计划
        /// - 平台动态执行，智能体自动选择工具
        /// 
        /// 适用场景：
        /// - 今天：PLC 文件解析
        /// - 明天：设备故障诊断
        /// - 后天：代码库重构
        /// - ...（任何复杂任务）
        /// 
        /// 🆕 多轮对话支持：
        /// - 传入 sessionId 可在同一会话中继续对话
        /// - 不传则创建新会话
        /// </summary>
        /// <param name="userInput">用户需求（纯文本）</param>
        /// <param name="plannerRoleId">Planner 角色ID</param>
        /// <param name="sessionId">会话ID（可选，支持多轮对话）</param>
        /// <returns></returns>
        public async Task<WorkflowExecutionResult> RunAgentOrchestrationAsync(
            string userInput,
            long? plannerRoleId = null,
            long? sessionId = null)
        {
            _logger.LogInformation("[Agent编排] 开始创建工作流, SessionId={SessionId}", sessionId);
            long tempWorkflowId = 0; // 用于错误推送
            long effectiveSessionId = 0; // 实际使用的会话ID
            
            // 🆕 分离 userId 和 sessionId 以更精细化地支持记忆
            string userIdStr = CurrentUserInfo.Instance.UserID > 0 
                ? CurrentUserInfo.Instance.UserID.ToString() 
                : "guest";

            try
            {
                // 🆕 确定最终多维度 MemoryKey (用于短期缓存)，并准备 DB 的 UserId 和 SessionId
                var shortTermMemoryKey = $"user-{userIdStr}-session-{effectiveSessionId}";
                var dbUserId = userIdStr;

                // 0. 记忆预热（短期）
                _memoryManagerService.EnsureUserMemoryLoaded(shortTermMemoryKey);

                // 🆕 会话管理：如果未提供sessionId，创建新会话
                if (!sessionId.HasValue || sessionId.Value == 0)
                {
                    effectiveSessionId = await _chatSessionService.CreateSessionAsync(new CreateSessionRequest
                    {
                        Title = userInput.Length > 10 ? userInput.Substring(0, 10) + "..." : userInput,
                        Description = userInput,
                        SessionType = "AgentOrchestration",
                        Tags = "workflow"
                    });
                    _logger.LogInformation("[Agent编排] 创建新会话: SessionId={SessionId}", effectiveSessionId);
                }
                else
                {
                    effectiveSessionId = sessionId.Value;
                    _logger.LogInformation("[Agent编排] 使用现有会话: SessionId={SessionId}", effectiveSessionId);
                }

                // 🆕 添加用户消息到会话
                await _chatSessionService.AddMessageAsync(new AddMessageRequest
                {
                    SessionId = effectiveSessionId,
                    Role = "user",
                    Content = userInput
                });

                // 1.1 短期记忆写入（最近输入）
                _memoryManagerService.SetShortTermMemory(shortTermMemoryKey, "last_user_input", userInput);

                // 1.2 异步长期记忆提取+持久化被移至后台任务（MemoryExtractionBackgroundJobService）
                // _backgroundJobClient.Enqueue 不在这里触发，而是在助理(Assistant)回复后触发，以获取完整对话。

                // 1.3 召回长期记忆并拼接上下文（最多3条）
                var recalledMemories = await _memoryManagerService.SearchLongTermMemoryAsync(dbUserId, effectiveSessionId.ToString(), userInput, topK: 3);
                var memoryContext = recalledMemories.Count > 0
                    ? string.Join("\n", recalledMemories.Select(m => $"- [{m.MemoryType}] {m.Content}"))
                    : string.Empty;

                // ✅ 智能路由：分析意图并选择最佳 Agent（🆕 增强版：加载历史+强化请求）
                var routeResult = await AnalyzeIntentAndRouteAsync(userInput, effectiveSessionId);
                
                // 🆕 使用强化后的请求（如果可用）
                var enhancedUserInput = !string.IsNullOrEmpty(routeResult.EnhancedRequest) 
                    ? routeResult.EnhancedRequest 
                    : userInput;

                // 🆕 检测诊断校正意图（多场景）
                long userId = CurrentUserInfo.Instance.UserID > 0 ? CurrentUserInfo.Instance.UserID : 0;
                var (isCorrection, sceneType) = await IsDiagnosticCorrectionAsync(enhancedUserInput, userId, null);
                if (isCorrection && !string.IsNullOrEmpty(sceneType))
                {
                    _logger.LogInformation("[Agent编排] 检测到{Scene}校正意图: {Input}", 
                        sceneType,
                        enhancedUserInput.Length > 50 ? enhancedUserInput.Substring(0, 50) + "..." : enhancedUserInput);
                    
                    var correctionResult = await HandleDiagnosticCorrectionAsync(enhancedUserInput, userId, null, sceneType);
                    
                    if (correctionResult.Success)
                    {
                        // 保存用户消息
                        await _chatSessionService.AddMessageAsync(new AddMessageRequest
                        {
                            SessionId = effectiveSessionId,
                            Role = "assistant",
                            Content = correctionResult.Result
                        });
                        
                        return correctionResult;
                    }
                    // 如果校正处理失败，继续正常流程
                    _logger.LogWarning("[Agent编排] 诊断校正处理失败，继续正常流程");
                }

                // 1.4 注入记忆上下文
                if (!string.IsNullOrWhiteSpace(memoryContext))
                {
                    enhancedUserInput = $"### 历史记忆\n{memoryContext}\n\n### 当前请求\n{enhancedUserInput}";
                }
                
                if (routeResult.Success && routeResult.TargetAgentId > 0)
                {
                    // 检查选中 Agent 的类型
                    var targetRole = await _agentRoleService.GetByIdAsync(routeResult.TargetAgentId);
                    
                    // 如果是通用助手 (RoleCode=general_assistant 或 ID=2002)，走 ExecuteSimpleQueryAsync (现已增强为 ReAct)
                    if (targetRole != null && (targetRole.RoleCode == "general_assistant" || targetRole.Id == 2002))
                    {
                        _logger.LogInformation("[Agent编排] 路由结果：通用助手 (ID={Id}), 意图={Intent}", 
                            routeResult.TargetAgentId, routeResult.IntentSummary);
                        // 🆕 使用强化后的请求
                        return await HandleSimpleQueryAsync(enhancedUserInput, effectiveSessionId);
                    }
                    
                    // 否则，如果是其他 (Planner)，则更新 plannerRoleId
                    if (targetRole != null)
                    {
                        _logger.LogInformation("[Agent编排] 路由结果：规划智能体/专家 (ID={Id}, Name={Name}), 意图={Intent}", 
                            routeResult.TargetAgentId, targetRole.RoleName, routeResult.IntentSummary);
                        plannerRoleId = routeResult.TargetAgentId;
                    }
                }
                
                // 如果路由失败或未指定，使用默认逻辑（尝试查找 Planner）
                if (!plannerRoleId.HasValue)
                {
                    plannerRoleId = await SelectBestPlannerAsync(userInput);
                }

                var traceId = Guid.NewGuid().ToString();
                var workflowName = GenerateWorkflowName("智能编排", userInput);
                var priority = CalculatePriority(userInput);

                var workflow = new Agent_WorkflowEntity
                {
                    Name = workflowName,
                    Description = userInput,
                    TriggerType = "User",
                    ThinkingMode = "AgentOrchestration",
                    Status = 1, // 规划中
                    Priority = priority,
                    PlannerAgentID = plannerRoleId,
                    TraceId = traceId,
                    CorrelationID = effectiveSessionId.ToString(), // 🆕 关联会话
                    PlanStatus = "Planning", // Planner 正在规划
                    GlobalContext = JsonHelper.SerializeObject(new
                    {
                        ChatSessionId = effectiveSessionId, // 🆕 使用实际会话ID
                        UserInput = userInput,
                        EnhancedUserInput = enhancedUserInput, // 🆕 强化后的请求
                        IntentSummary = routeResult?.IntentSummary, // 🆕 意图摘要
                        StartedAt = DateTime.Now,
                        Mode = "AgentOrchestration",
                        PlannerRoleId = plannerRoleId
                    })
                };

                await _workflowService.InsertAsync(workflow);
                tempWorkflowId = workflow.Id; // 保存ID用于错误推送

                // 🆕 关联会话与Workflow
                await _chatSessionService.LinkWorkflowToSessionAsync(workflow.Id, effectiveSessionId);

                // 提交到 Hangfire（让 Planner 生成计划并执行）
                // 🆕 使用强化后的请求，让 Planner 获得更明确的任务描述
                var jobId = _backgroundJobClient.Enqueue<WorkflowBackgroundJobService>(
                    job => job.ExecuteAgentOrchestrationAsync(
                        workflow.Id, enhancedUserInput, plannerRoleId.Value));

                _logger.LogInformation("[Agent编排] 已创建 Workflow={WorkflowId}, Planner={PlannerId}, SessionId={SessionId}, HangfireJob={JobId}",
                    workflow.Id, plannerRoleId, effectiveSessionId, jobId);

                return new WorkflowExecutionResult
                {
                    WorkflowId = workflow.Id,
                    SessionId = effectiveSessionId, // 🆕 返回会话ID
                    Success = true,
                    Result = $"智能编排已启动，Planner 正在分析需求并生成执行计划。通过 SignalR 订阅 'workflow-{workflow.Id}' 查看进度。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent编排] 创建失败");
                
                // ✅ 推送错误消息到前端
                if (tempWorkflowId > 0)
                {
                    try
                    {
                        await _hubContext.Clients.Group($"workflow-{tempWorkflowId}")
                            .SendAsync("WorkflowFailed", new 
                            { 
                                workflowId = tempWorkflowId.ToString(), 
                                error = ex.Message,
                                timestamp = DateTime.Now
                            });
                    }
                    catch (Exception pushEx)
                    {
                        _logger.LogError(pushEx, "[Agent编排] 推送失败消息异常");
                    }
                }
                else
                {
                    // 如果连Workflow都没创建，无法推送到特定组，记录详细日志
                    _logger.LogWarning("[Agent编排] Workflow未创建，无法推送SignalR消息: {Error}", ex.Message);
                }
                
                return new WorkflowExecutionResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>
        /// 智能选择最佳 Planner（根据需求特征）
        /// </summary>
        private async Task<long> SelectBestPlannerAsync(string userInput)
        {
            // TODO: 实现智能选择逻辑
            // 1. 分析需求关键词（PLC、诊断、优化等）
            // 2. 查询对应领域的专业 Planner
            // 3. 如果没有，返回通用 Planner

            // ✅ 修复：先查询 RoleCode='planner' 的角色（精确匹配）
            var planners = await _agentRoleService.GetListAsync(
                r => r.RoleCode != null && 
                     r.RoleCode.ToLower() == "planner" && 
                     r.IsEnabled == true && 
                     r.IsDeleted == false);

            if (planners.Count > 0)
            {
                _logger.LogInformation("[Agent编排] 已选择Planner: {RoleName} (ID={RoleId})", 
                    planners[0].RoleName, planners[0].Id);
                return planners[0].Id;
            }

            // 如果没有精确匹配，尝试模糊匹配（兼容性）
            var fuzzyPlanners = await _agentRoleService.GetListAsync(
                r => ((r.RoleName != null && r.RoleName.Contains("Planner", StringComparison.OrdinalIgnoreCase)) ||
                      (r.RoleCode != null && r.RoleCode.Contains("planner", StringComparison.OrdinalIgnoreCase))) &&
                     r.IsEnabled == true && 
                     r.IsDeleted == false);

            if (fuzzyPlanners.Count > 0)
            {
                _logger.LogWarning("[Agent编排] 未找到精确匹配的Planner，使用模糊匹配: {RoleName}", 
                    fuzzyPlanners[0].RoleName);
                return fuzzyPlanners[0].Id;
            }

            throw new InvalidOperationException("系统中没有可用的 Planner 角色，请先执行数据脚本创建 ID=2010 的 'planner' 角色");
        }

        /// <summary>
        /// 使用LLM判断是否为简单查询（不需要 Planner 编排）
        /// 🆕 优化：用LLM智能分类，替代关键词匹配
        /// </summary>
        private async Task<bool> IsSimpleQueryAsync(string userInput)
        {
            if (string.IsNullOrWhiteSpace(userInput)) return false;
            
            try
            {
                // 获取推荐模型
                var model = await _llmModelService.RecommendModelAsync();
                if (model == null)
                {
                    _logger.LogWarning("[意图分类] 无可用模型，降级为复杂任务处理");
                    return false;
                }

                var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(model.Id);
                
                // 🆕 优化：尝试从数据库加载 'router' 或 'classifier' 角色的提示词
                string systemPrompt;
                
                var routerRole =( await _agentRoleService.GetListAsync(
                    r => (r.RoleCode == "router" || r.RoleCode == "classifier") && r.IsDeleted ==false && r.IsEnabled == true)).FirstOrDefault();

                if (routerRole != null && !string.IsNullOrWhiteSpace(routerRole.SystemPrompt))
                {
                    systemPrompt = routerRole.SystemPrompt;
                    _logger.LogInformation("[意图分类] 使用数据库配置的 Router 提示词 (RoleID={RoleId})", routerRole.Id);
                }
                else
                {
                    // 默认硬编码提示词
                    systemPrompt = @"你是意图分类器。判断用户输入属于哪类：

【简单问答】直接回答即可，无需工具或复杂处理：
- 问候、告别、感谢
- 身份询问（你是谁、你叫什么）
- 能力询问（你能做什么）
- 简单知识问答
- 闲聊

【复杂任务】需要工具调用或多步骤处理：
- 文件操作（上传、解析、分析）
- PLC相关任务
- 代码生成/分析
- 数据处理
- 需要调用外部API

只回复一个词：simple 或 complex";
                }

                var response = await _llmModelService.ChatAsync(chatClient, userInput, systemPrompt);
                var result = response.Content?.Trim().ToLower() ?? "";
                
                var isSimple = result.Contains("simple");
                _logger.LogInformation("[意图分类] 输入=\"{Input}\", 结果={Result}, 判定={IsSimple}", 
                    userInput.Length > 30 ? userInput.Substring(0, 30) + "..." : userInput,
                    result, isSimple ? "简单问答" : "复杂任务");
                
                return isSimple;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[意图分类] LLM调用失败，降级为复杂任务处理");
                return false; // 失败时降级为复杂任务，确保功能可用
            }
        }

        /// <summary>
        /// 处理简单查询（直接调用 general_assistant）
        /// </summary>
        private async Task<WorkflowExecutionResult> HandleSimpleQueryAsync(string userInput, long sessionId)
        {
            try
            {
                // 查找 general_assistant 角色
                var assistantRoles = await _agentRoleService.GetListAsync(
                    r => (r.RoleCode == "general_assistant" || r.RoleName.Contains("通用助手")) && 
                         r.IsEnabled == true);
                
                if (assistantRoles.Count == 0)
                {
                    _logger.LogWarning("[简单查询] 未找到 general_assistant，降级使用完整编排");
                    return new WorkflowExecutionResult 
                    { 
                        Success = false, 
                        ErrorMessage = "未找到通用助手，请使用完整编排模式" 
                    };
                }

                _logger.LogInformation("[简单查询] 使用 general_assistant: {RoleName}", assistantRoles[0].RoleName);
                
                // 创建简化的 Workflow 记录
                var workflow = new Agent_WorkflowEntity
                {
                    Name = $"简单查询-{userInput.Substring(0, Math.Min(10, userInput.Length))}",
                    Description = userInput,
                    TriggerType = "User",
                    ThinkingMode = "DirectCall",
                    Status = 2, // 执行中
                    Priority = 1,
                    TraceId = Guid.NewGuid().ToString(),
                    CorrelationID = sessionId.ToString(), // 🆕 关联会话
                    GlobalContext = JsonHelper.SerializeObject(new
                    {
                        ChatSessionId = sessionId,
                        UserInput = userInput,
                        Mode = "SimpleQuery"
                    })
                };
                
                await _workflowService.InsertAsync(workflow);

                // 🆕 关联会话与Workflow
                await _chatSessionService.LinkWorkflowToSessionAsync(workflow.Id, sessionId);

                //// 直接调用 general_assistant（通过 Hangfire 异步执行）
                var jobId = _backgroundJobClient.Enqueue<WorkflowBackgroundJobService>(
                    job => job.ExecuteSimpleQueryAsync(workflow.Id, userInput, assistantRoles[0].Id, sessionId));

                return new WorkflowExecutionResult
                {
                    WorkflowId = workflow.Id,
                    SessionId = sessionId, // 🆕 返回会话ID
                    Success = true,
                    Result = $"简单查询已提交，通过 SignalR 订阅 'workflow-{workflow.Id}' 查看结果。"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[简单查询] 处理失败");
                return new WorkflowExecutionResult 
                { 
                    Success = false, 
                    ErrorMessage = ex.Message 
                };
            }
        }

        #endregion

        #region 诊断校正意图识别与处理

        /// <summary>
        /// 诊断校正关键词（通用）
        /// </summary>
        private static readonly string[] DiagnosticCorrectionKeywords = new[]
        {
            "错了", "不对", "纠正", "校正", "分析错", "判断错",
            "重新看", "你理解错了", "这个不对", "应该是", "重新分析"
        };

        /// <summary>
        /// 检测是否为诊断校正反馈，并识别场景类型
        /// </summary>
        private async Task<(bool isCorrection, string sceneType)> IsDiagnosticCorrectionAsync(string userInput, long userId, long? productionLineId)
        {
            try
            {
                // 1. 关键词快速匹配
                var hasKeyword = DiagnosticCorrectionKeywords.Any(k => 
                    userInput.Contains(k, StringComparison.OrdinalIgnoreCase));
                
                if (!hasKeyword)
                {
                    return (false, null);
                }

                _logger.LogInformation("[诊断校正检测] 命中关键词: {Input}", 
                    userInput.Length > 50 ? userInput.Substring(0, 50) + "..." : userInput);

                // 2. LLM识别场景类型
                var (sceneType, reason) = await ClassifyDiagnosticSceneAsync(userInput);
                
                if (string.IsNullOrEmpty(sceneType))
                {
                    _logger.LogWarning("[诊断校正检测] 未能识别场景类型，降级为普通处理");
                    return (false, null);
                }

                _logger.LogInformation("[诊断校正检测] 识别到校正意图: Scene={Scene}, Reason={Reason}", sceneType, reason);
                return (true, sceneType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[诊断校正检测] 检测失败");
                return (false, null);
            }
        }

        /// <summary>
        /// LLM识别诊断场景类型
        /// </summary>
        private async Task<(string sceneType, string reason)> ClassifyDiagnosticSceneAsync(string userInput)
        {
            try
            {
                var prompt = $@"分析用户输入，判断AI在哪类诊断中判断错了。

用户输入：""{userInput}""

只回复JSON：
{{
  ""scene"": ""constraint|stuck_point|energy_flow|none"",
  ""reason"": ""简单说明判断依据""
}}

场景说明：
- constraint: 约束生成分析（如：互斥关系、配对关系、安全联锁等）
- stuck_point: 卡滞点诊断（如：产线卡在哪里、为什么不动了）
- energy_flow: 能量流分析（如：输出没电、逻辑阻断）
- none: 不是诊断校正

只输出JSON";

                var response = await _llmModelService.ChatAsync(null, prompt, "你是诊断场景分类专家");

                var result = response?.Content?.Trim() ?? "";
                var jsonText = ExtractJsonFromText(result);
                
                if (!string.IsNullOrEmpty(jsonText))
                {
                    using var doc = JsonDocument.Parse(jsonText);
                    var scene = doc.RootElement.TryGetProperty("scene", out var s) ? s.GetString() : null;
                    var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    
                    if (scene == "none") return (null, reason);
                    return (scene ?? null, reason ?? "");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[诊断校正检测] LLM分类失败");
            }
            return (null, null);
        }

        private static string ExtractJsonFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start) return text.Substring(start, end - start + 1);
            return string.Empty;
        }

        /// <summary>
        /// 处理诊断校正反馈（多场景）
        /// </summary>
        private async Task<WorkflowExecutionResult> HandleDiagnosticCorrectionAsync(
            string userInput, 
            long userId, 
            long? productionLineId, 
            string sceneType,
            string signalName = null)
        {
            try
            {
                var sceneName = sceneType switch
                {
                    DiagnosticMemoryService.SceneConstraint => "约束生成",
                    DiagnosticMemoryService.SceneStuckPoint => "卡滞诊断",
                    DiagnosticMemoryService.SceneEnergyFlow => "能量流分析",
                    _ => sceneType
                };

                _logger.LogInformation("[诊断校正处理] 开始处理: UserId={UserId}, Scene={Scene}", userId, sceneName);

                var result = await _diagnosticCorrectionSkill.ProcessCorrectionAsync(
                    userId, sceneType, productionLineId, userInput, signalName);

                if (!result.Success)
                {
                    return new WorkflowExecutionResult
                    {
                        Success = false,
                        ErrorMessage = result.ErrorMessage ?? "校正处理失败"
                    };
                }

                var patternSummary = result.PatternSummary;
                var confirmMessage = $@"我理解的是：{patternSummary.Pattern}

【场景】{sceneName}
【错误本质】{patternSummary.CoreReason}
【正确做法】{patternSummary.CorrectAction}
【适用范围】{patternSummary.ApplicableScope}

这个理解对吗？
[确认] [需要补充] [重新描述]

CorrectionLogId={result.CorrectionLogId}";

                return new WorkflowExecutionResult
                {
                    Success = true,
                    Result = confirmMessage,
                    CorrectionLogId = result.CorrectionLogId,
                    Status = result.Status
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[诊断校正处理] 处理失败");
                return new WorkflowExecutionResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        #endregion
    }
}
