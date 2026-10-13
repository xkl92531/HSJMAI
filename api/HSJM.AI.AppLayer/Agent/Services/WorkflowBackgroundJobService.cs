using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.Agent.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using HSJM.AI.Utility;
using Microsoft.Extensions.DependencyInjection;
using HSJM.AI.AppLayer.Hubs;
using System.Text.Json;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 工作流后台任务执行器（Hangfire Job）
    /// 职责：在后台异步执行工作流，通过 SignalR 实时推送进度
    /// 🆕 阶段1.2：使用 partial class 分离任务执行逻辑
    /// 🚀 阶段1.3：实现 ReAct 引擎（边思考边行动）
    /// </summary>
    public partial class WorkflowBackgroundJobService : ISelfScopedAutoInject
    {
        private readonly AgentRuntimeService _agentRuntimeService;
        private readonly AgentRuntimeService _agentRuntime; // 别名，供 ReActExecution 使用
        private readonly ReActExecutionEngine _reActEngine; // 自定义 ReAct 引擎
        private readonly LLMModelService _llmModelService;  // LLM 模型服务（用于创建 ChatClient）
        private readonly ChatSessionManagementService _chatSessionService; // 🆕 会话管理服务
        private readonly WorkflowCancellationService _cancellationService; // 🆕 取消服务
        private readonly SkillLoaderService _skillLoader;  // 🆕 技能加载服务
        private readonly IAgent_WorkflowService _workflowService;
        private readonly IAgent_TaskService _taskService;
        private readonly ITask_DependencyService _dependencyService;
        private readonly IAgent_RoleService _roleService;
        private readonly IAgent_RoleService _agentRoleService; // 别名，供 ReActExecution 使用
        private readonly IAgent_RelationService _agentRelationService; // 🆕 智能体关系服务
        private readonly IHubContext<AgentStreamingHub> _hubContext;
        private readonly ILogger<WorkflowBackgroundJobService> _logger;

        public WorkflowBackgroundJobService(
            AgentRuntimeService agentRuntimeService,
            ReActExecutionEngine reActEngine,
            LLMModelService llmModelService,
            ChatSessionManagementService chatSessionService,
            WorkflowCancellationService cancellationService,
            SkillLoaderService skillLoader,  // 🆕 技能加载服务
            IAgent_WorkflowService workflowService,
            IAgent_TaskService taskService,
            ITask_DependencyService dependencyService,
            IAgent_RoleService roleService,
            IAgent_RelationService agentRelationService, // 🆕 智能体关系服务
            IHubContext<AgentStreamingHub> hubContext,
            ILogger<WorkflowBackgroundJobService> logger)
        {
            _agentRuntimeService = agentRuntimeService;
            _agentRuntime = agentRuntimeService; // 别名
            _reActEngine = reActEngine;
            _llmModelService = llmModelService;
            _chatSessionService = chatSessionService;
            _cancellationService = cancellationService;
            _skillLoader = skillLoader;  // 🆕
            _workflowService = workflowService;
            _taskService = taskService;
            _dependencyService = dependencyService;
            _roleService = roleService;
            _agentRoleService = roleService; // 别名
            _agentRelationService = agentRelationService; // 🆕
            _hubContext = hubContext;
            _logger = logger;
        }

        #region 简单查询执行器

        /// <summary>
        /// 执行简单查询（不需要 Planner 编排）
        /// </summary>
        [Hangfire.JobDisplayName("Workflow·简单查询 · WF{0}")]
        public async Task ExecuteSimpleQueryAsync(long workflowId, string userInput, long assistantRoleId, long sessionId)
        {
            var startTime = DateTime.Now;
            
            // 🆕 注册工作流到取消服务
            var cancellationToken = _cancellationService.RegisterWorkflow(workflowId);
            _logger.LogInformation("[简单查询] 工作流已注册到取消服务: WorkflowId={WorkflowId}", workflowId);
            
            try
            {
                _logger.LogInformation("[简单查询] 开始执行: WorkflowId={WorkflowId}, Assistant={AssistantId}, SessionId={SessionId} (类型={SessionIdType})", 
                    workflowId, assistantRoleId, sessionId, sessionId.GetType().Name);

                // 检查是否已取消
                cancellationToken.ThrowIfCancellationRequested();

                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow == null)
                {
                    _logger.LogError("[简单查询] Workflow不存在: {WorkflowId}", workflowId);
                    return;
                }

                // 推送开始消息
                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("WorkflowStarted", new { workflowId, mode = "SimpleQuery" });

                // 检查是否已取消
                cancellationToken.ThrowIfCancellationRequested();

                // 🆕 升级：使用 ReAct 引擎执行 (支持工具调用和自动总结)
                // 即使是简单查询，使用 ReAct 也能获得更好的回复质量和一致性 (特别是总结退出机制)
                
                // 1. 生成临时 taskId (简单查询没有实际的 Task 记录，使用负数或特定哈希)
                var simpleTaskId = -(DateTime.UtcNow.Ticks); 
                
                // 2. 定义步骤回调：实时推送 ReAct 过程
                Func<ReActStep, Task> onStepComplete = async (step) =>
                {
                    // 推送 ReActStep 详情
                    await PushReActStep(workflowId, simpleTaskId, "SimpleAssistant", step);
                };

                // 生成临时会话ID用于追踪 (如果传入的sessionId无效)
                var traceSessionId = sessionId > 0 ? sessionId.ToString() : $"simple-query-{workflowId}-{DateTime.UtcNow.Ticks}";
                
                var reactResult = await _agentRuntime.RunAgentWithReActAsync(
                    assistantRoleId,
                    userInput,
                    sessionId: traceSessionId,
                    maxIterations: 10, // 给予一定迭代空间
                    onStepComplete: onStepComplete, // 🆕 注入回调
                    cancellationToken: cancellationToken,
                    workflowId: workflowId,   // 🆕 注入工作流上下文
                    taskId: simpleTaskId      // 🆕 注入任务上下文
                );

                // 🆕 耗时操作后检查取消
                cancellationToken.ThrowIfCancellationRequested();

                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                var assistantContent = reactResult.Success ? reactResult.FinalAnswer : $"执行异常: {reactResult.ErrorMessage}";
                if (string.IsNullOrWhiteSpace(assistantContent)) assistantContent = "抱歉，未能生成有效回复。";
                
                // 🆕 保存AI回复到会话
                if (sessionId > 0)
                {
                    try
                    {
                        var contentToSave = assistantContent;
                        // 如果有诊断报告，视情况追加 (一般不追加给用户看，除非是调试模式)
                        
                        await _chatSessionService.AddMessageAsync(new AddMessageRequest
                        {
                            SessionId = sessionId,
                            Role = "assistant",
                            Content = contentToSave,
                            WorkflowId = workflowId,
                            ModelName = "general_assistant_react"
                        });
                        _logger.LogInformation("[简单查询] AI回复已保存到会话: SessionId={SessionId}", sessionId);
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogError(saveEx, "[简单查询] 保存AI回复失败: SessionId={SessionId}", sessionId);
                    }
                }

                // 更新 Workflow
                workflow.Status = reactResult.Success ? 3 : 4; // 3=完成, 4=失败
                workflow.ResultSummary = assistantContent;
                workflow.ActualDuration = (int)elapsed;
                // 保存 ReAct 诊断报告到上下文，供后续分析
                if (reactResult.DiagnosticReport != null)
                {
                     // 尝试合并到 GlobalContext (如果需要)
                }
                await _workflowService.UpdateAsync(workflow);

                // 推送完成消息
                if (reactResult.Success)
                {
                    await _hubContext.Clients.Group($"workflow-{workflowId}")
                        .SendAsync("WorkflowCompleted", new
                        {
                            workflowId,
                            duration = elapsed,
                            summary = assistantContent
                        });
                }
                else
                {
                    await _hubContext.Clients.Group($"workflow-{workflowId}")
                         .SendAsync("WorkflowFailed", new { workflowId, error = reactResult.ErrorMessage });
                }

                _logger.LogInformation("[简单查询] 执行完成: WorkflowId={WorkflowId}, Success={Success}, 耗时={Duration}秒",
                    workflowId, reactResult.Success, elapsed);
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消
                _logger.LogWarning("[简单查询] 被用户取消: WorkflowId={WorkflowId}", workflowId);
                
                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("WorkflowCancelled", new { workflowId, message = "任务已被取消" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[简单查询] 执行失败: WorkflowId={WorkflowId}", workflowId);

                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow != null)
                {
                    workflow.Status = 4; // 失败
                    workflow.ResultSummary = $"执行失败: {ex.Message}";
                    await _workflowService.UpdateAsync(workflow);
                }

                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("WorkflowFailed", new { workflowId, error = ex.Message });
            }
            finally
            {
                // 🆕 注销工作流
                _cancellationService.UnregisterWorkflow(workflowId);
            }
        }

        #endregion

        #region 会话消息保存辅助方法

        /// <summary>
        /// 保存AI回复到会话（从Workflow的GlobalContext中提取SessionId）
        /// </summary>
        private async Task SaveAssistantMessageToSessionAsync(Agent_WorkflowEntity workflow, string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                _logger.LogWarning("[会话保存] AI回复内容为空，跳过保存");
                return;
            }

            try
            {
                // 从 GlobalContext 或 CorrelationID 获取 SessionId
                long sessionId = 0;
                
                if (!string.IsNullOrEmpty(workflow.GlobalContext))
                {
                    try
                    {
                        var context = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GlobalContext);
                        if (context != null && context.TryGetValue("ChatSessionId", out var sessionIdElement))
                        {
                            sessionId = sessionIdElement.GetInt64();
                        }
                    }
                    catch (Exception jsonEx)
                    {
                        _logger.LogWarning(jsonEx, "[会话保存] 解析GlobalContext失败");
                    }
                }
                
                // 备选：从 CorrelationID 获取
                if (sessionId == 0 && !string.IsNullOrEmpty(workflow.CorrelationID))
                {
                    long.TryParse(workflow.CorrelationID, out sessionId);
                }

                if (sessionId <= 0)
                {
                    _logger.LogWarning("[会话保存] 未找到SessionId，跳过保存: WorkflowId={WorkflowId}", workflow.Id);
                    return;
                }

                await _chatSessionService.AddMessageAsync(new AddMessageRequest
                {
                    SessionId = sessionId,
                    Role = "assistant",
                    Content = content,
                    WorkflowId = workflow.Id,
                    ModelName = workflow.ThinkingMode ?? "AgentOrchestration"
                });

                _logger.LogInformation("[会话保存] AI回复已保存: SessionId={SessionId}, WorkflowId={WorkflowId}",
                    sessionId, workflow.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[会话保存] 保存AI回复失败: WorkflowId={WorkflowId}", workflow.Id);
            }
        }

        #endregion

        #region SignalR 推送辅助方法

        /// <summary>
        /// 获取 HubContext（从 ServiceProvider 动态获取，避免 Hangfire 作用域问题）
        /// </summary>
        private IHubContext<AgentStreamingHub> GetHubContext()
        {
            return CommonServiceProvider.ServiceProvider.GetService<IHubContext<AgentStreamingHub>>();
        }

        /// <summary>
        /// 推送工作流进度到前端（按 WorkflowId 分组）
        /// </summary>
        private async Task PushWorkflowProgress(long workflowId, string stage, string message, object? data = null)
        {
            try
            {
                // 从 ServiceProvider 动态获取 HubContext（解决 Hangfire 作用域问题）
                var hubContext = GetHubContext();
                if (hubContext == null)
                {
                    _logger.LogError("[SignalR推送] HubContext为null，无法推送消息");
                    return;
                }
                
                var workflowIdString = workflowId.ToString();
                var groupName = $"workflow-{workflowIdString}";  // ✅ 修复：统一格式为 workflow-{id}
                
                _logger.LogInformation("[SignalR推送-开始] GroupName={GroupName}, Stage={Stage}", 
                    groupName, stage);
                
                await hubContext.Clients.Group(groupName).SendAsync("WorkflowProgress", new
                {
                    workflowId = workflowIdString,
                    stage,
                    message,
                    data,
                    timestamp = DateTime.Now
                });
                
                _logger.LogInformation("[SignalR推送-完成] GroupName={GroupName}", groupName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SignalR推送-异常] 推送失败");
            }
        }

        /// <summary>
        /// 推送任务状态变更
        /// </summary>
        private async Task PushTaskStatus(long workflowId, long taskId, string taskName, int status, string? output = null)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null)
                {
                    _logger.LogError("[SignalR推送-任务] HubContext为null");
                    return;
                }
                
                var workflowIdString = workflowId.ToString();
                var groupName = $"workflow-{workflowIdString}";  // ✅ 修复格式
                
                await hubContext.Clients.Group(groupName).SendAsync("TaskStatusChanged", new
                {
                    workflowId = workflowIdString,
                    taskId,
                    taskName,
                    status,
                    statusText = GetStatusText(status),
                    output,
                    timestamp = DateTime.Now
                });
                
                _logger.LogInformation("[SignalR推送-任务] GroupName={GroupName}, TaskName={TaskName}, Status={Status}", 
                    groupName, taskName, GetStatusText(status));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SignalR推送-任务-异常] 推送失败");
            }
        }

        /// <summary>
        /// 推送流式总结（分块发送）
        /// </summary>
        private async Task PushStreamingSummary(long workflowId, string chunk, bool isComplete)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null)
                {
                    _logger.LogError("[SignalR推送-流式] HubContext为null");
                    return;
                }
                
                var workflowIdString = workflowId.ToString();
                var groupName = $"workflow-{workflowIdString}";  // ✅ 修复格式
                
                await hubContext.Clients.Group(groupName).SendAsync("StreamingSummary", new
                {
                    workflowId = workflowIdString,
                    chunk,
                    isComplete,
                    timestamp = DateTime.Now
                });
                
                if (isComplete)
                {
                    _logger.LogInformation("[SignalR推送-流式] 总结流式输出完成");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SignalR推送-流式-异常] 推送失败");
            }
        }

        /// <summary>
        /// 推送专家开始生成标记
        /// </summary>
        private async Task PushExpertStreamStart(long workflowId, long taskId, string taskName)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;
                
                var groupName = $"workflow-{workflowId}";
                await hubContext.Clients.Group(groupName).SendAsync("ExpertStreamStart", new
                {
                    workflowId = workflowId.ToString(),
                    taskId,
                    taskName,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SignalR推送-专家流式开始-异常]");
            }
        }

        #region 🆕 过程可视化 - 新增推送方法

        /// <summary>
        /// 推送智能体思考过程
        /// </summary>
        private async Task PushAgentThinking(long workflowId, long taskId, string agentName, string thought)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;

                await hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("AgentThinking", new
                    {
                        workflowId,
                        taskId,
                        agentName,
                        thought,
                        timestamp = DateTime.Now
                    });

                _logger.LogInformation("[Agent思考] WorkflowId={WorkflowId}, TaskId={TaskId}, Agent={AgentName}",
                    workflowId, taskId, agentName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent思考-推送异常]");
            }
        }

        /// <summary>
        /// 推送工具调用（调用前）
        /// </summary>
        private async Task PushToolCalling(long workflowId, long taskId, string toolName, object parameters)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;

                await hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("ToolCalling", new
                    {
                        workflowId,
                        taskId,
                        toolName,
                        parameters = JsonHelper.SerializeObject(parameters),
                        timestamp = DateTime.Now
                    });

                _logger.LogInformation("[工具调用] WorkflowId={WorkflowId}, TaskId={TaskId}, Tool={ToolName}",
                    workflowId, taskId, toolName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具调用-推送异常]");
            }
        }

        /// <summary>
        /// 推送工具返回结果
        /// </summary>
        private async Task PushToolResult(long workflowId, long taskId, string toolName, object result, double durationMs, bool success)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;

                await hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("ToolResult", new
                    {
                        workflowId,
                        taskId,
                        toolName,
                        result = JsonHelper.SerializeObject(result),
                        durationMs,  // 修复：字段名与前端保持一致
                        success,
                        timestamp = DateTime.Now
                    });

                _logger.LogInformation("[工具结果] WorkflowId={WorkflowId}, TaskId={TaskId}, Tool={ToolName}, Duration={Duration}ms, Success={Success}",
                    workflowId, taskId, toolName, durationMs, success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具结果-推送异常]");
            }
        }

        /// <summary>
        /// 推送智能体决策
        /// </summary>
        private async Task PushAgentDecision(long workflowId, long taskId, string decision, string reasoning, List<string>? alternatives = null)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;

                await hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("AgentDecision", new
                    {
                        workflowId,
                        taskId,
                        decision,
                        reasoning,
                        alternatives = alternatives ?? new List<string>(),
                        timestamp = DateTime.Now
                    });

                _logger.LogInformation("[Agent决策] WorkflowId={WorkflowId}, TaskId={TaskId}, Decision={Decision}",
                    workflowId, taskId, decision);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent决策-推送异常]");
            }
        }

        /// <summary>
        /// 推送智能体输出结果
        /// </summary>
        private async Task PushAgentOutput(long workflowId, long taskId, string agentName, string output)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;

                await hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("TaskCompleted", new
                    {
                        workflowId,
                        taskId,
                        agentName,
                        output,
                        timestamp = DateTime.Now
                    });

                _logger.LogInformation("[Agent输出] WorkflowId={WorkflowId}, TaskId={TaskId}, Agent={AgentName}, 输出长度={Length}",
                    workflowId, taskId, agentName, output?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent输出-推送异常]");
            }
        }

        /// <summary>
        /// 推送任务重试
        /// </summary>
        private async Task PushTaskRetrying(long workflowId, long taskId, int retryCount, string reason, string nextAction)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;

                await hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("TaskRetrying", new
                    {
                        workflowId,
                        taskId,
                        retryCount,
                        reason,
                        nextAction,
                        timestamp = DateTime.Now
                    });

                _logger.LogInformation("[任务重试] WorkflowId={WorkflowId}, TaskId={TaskId}, RetryCount={RetryCount}, Reason={Reason}",
                    workflowId, taskId, retryCount, reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[任务重试-推送异常]");
            }
        }

        /// <summary>
        /// 推送任务依赖等待
        /// </summary>
        private async Task PushTaskDependencyWaiting(long workflowId, long taskId, string taskName, List<string> waitingFor, int estimatedSeconds)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;

                await hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("TaskDependencyWaiting", new
                    {
                        workflowId,
                        taskId,
                        taskName,
                        waitingFor,
                        estimatedTime = estimatedSeconds,
                        timestamp = DateTime.Now
                    });

                _logger.LogInformation("[依赖等待] WorkflowId={WorkflowId}, TaskId={TaskId}, TaskName={TaskName}, WaitingFor={WaitingFor}",
                    workflowId, taskId, taskName, string.Join(", ", waitingFor));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[依赖等待-推送异常]");
            }
        }

        #endregion

        /// <summary>
        /// 推送专家开始生成标记（保留原有方法）
        /// </summary>
        private async Task PushExpertStreamStart_Old(long workflowId, long taskId, string taskName)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;
                
                var groupName = $"workflow-{workflowId}";
                await hubContext.Clients.Group(groupName).SendAsync("ExpertStreamStart", new
                {
                    workflowId = workflowId.ToString(),
                    taskId,
                    taskName,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SignalR推送-专家流式开始-异常]");
            }
        }

        /// <summary>
        /// 推送专家生成的 token（实时流式输出）
        /// </summary>
        private async Task PushExpertStreamToken(long workflowId, long taskId, string taskName, string token)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;
                
                var groupName = $"workflow-{workflowId}";
                await hubContext.Clients.Group(groupName).SendAsync("ExpertStreamToken", new
                {
                    workflowId = workflowId.ToString(),
                    taskId,
                    taskName,
                    token,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SignalR推送-专家流式Token-异常]");
            }
        }

        /// <summary>
        /// 推送专家流式完成标记
        /// </summary>
        private async Task PushExpertStreamComplete(long workflowId, long taskId, string taskName)
        {
            try
            {
                var hubContext = GetHubContext();
                if (hubContext == null) return;
                
                var groupName = $"workflow-{workflowId}";
                await hubContext.Clients.Group(groupName).SendAsync("ExpertStreamComplete", new
                {
                    workflowId = workflowId.ToString(),
                    taskId,
                    taskName,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SignalR推送-专家流式完成-异常]");
            }
        }

        private string GetStatusText(int status)
        {
            return status switch
            {
                0 => "待调度",
                1 => "已就绪",
                2 => "执行中",
                3 => "完成",
                4 => "失败",
                5 => "阻塞",
                6 => "待用户确认",
                _ => "未知"
            };
        }

        #endregion

        #region 智能路由选择

        /// <summary>
        /// 智能选择 Router 智能体
        /// 规则：优先选择 RoleCode 包含 "ROUTER" 的角色，或者第一个可用的角色
        /// </summary>
        private async Task<long> SelectSmartRouterAsync()
        {
            // 1. 查找所有启用的角色
            var allRoles = await _roleService.GetListAsync(r => r.IsEnabled == true && r.IsDeleted == false);
            
            // 2. 优先选择 RoleCode 包含 ROUTER 的
            var routerRole = allRoles.FirstOrDefault(r => 
                r.RoleCode != null && r.RoleCode.Contains("ROUTER", StringComparison.OrdinalIgnoreCase));
            
            if (routerRole != null)
            {
                _logger.LogInformation("[智能路由] 选择Router: {RoleName} (ID={RoleId})", 
                    routerRole.RoleName, routerRole.Id);
                return routerRole.Id;
            }

            // 3. 如果没有Router，选择第一个可用角色（作为通用路由）
            var fallbackRole = allRoles.FirstOrDefault();
            if (fallbackRole != null)
            {
                _logger.LogWarning("[智能路由] 未找到Router角色，使用通用角色: {RoleName}", fallbackRole.RoleName);
                return fallbackRole.Id;
            }

            throw new InvalidOperationException("系统中没有可用的智能体角色，请先创建角色");
        }

        /// <summary>
        /// 加载所有可用的专家角色（排除Router）
        /// </summary>
        private async Task<Dictionary<long, Agent_RoleEntity>> LoadAvailableExpertsAsync(long routerId)
        {
            var expertRoleIds = new Dictionary<long, Agent_RoleEntity>();
            
            // 查找所有启用的角色，排除Router本身
            var allRoles = await _roleService.GetListAsync(r => 
                r.IsEnabled == true && 
                r.IsDeleted == false && 
                r.Id != routerId);
            
            foreach (var role in allRoles)
            {
                // 使用RoleCode作为key（如果没有则用RoleName）
                var key = role.Id;
                
                expertRoleIds[key] = role;
            }

            _logger.LogInformation("[智能推荐] 已加载{Count}个可用专家: {Experts}", 
                expertRoleIds.Count, string.Join(", ", expertRoleIds.Keys));
            
            return expertRoleIds;
        }

        #endregion

        #region Hangfire 后台任务：条件路由

        /// <summary>
        /// 【Hangfire Job】后台执行条件路由工作流
        /// </summary>
        [AutomaticRetry(Attempts = 0)] // 不自动重试，避免重复执行
        [Hangfire.JobDisplayName("Workflow·条件路由 · WF{0}")]
        public async Task ExecuteConditionalRoutingAsync(
            long workflowId,
            string userInput,
            long? routerRoleId,
            Dictionary<long, string> expertRoleIds)
        {
            // 🆕 注册取消令牌
            var cancellationToken = _cancellationService.RegisterWorkflow(workflowId);
            _logger.LogInformation("[条件路由Job] 工作流已注册到取消服务: WorkflowId={WorkflowId}", workflowId);
            
            _logger.LogInformation("[条件路由Job] 开始: WorkflowId={WorkflowId}", workflowId);
            var startTime = DateTime.Now;

            try
            {
                // 🆕 检查取消
                cancellationToken.ThrowIfCancellationRequested();
                
                // 1. 加载 Workflow
                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow == null)
                {
                    _logger.LogError("[条件路由Job] Workflow不存在: {WorkflowId}", workflowId);
                    return;
                }

                // 🆕 检查是否已在启动前被强制取消
                if (workflow.Status == 5)
                {
                    throw new OperationCanceledException("工作流在启动前已被取消");
                }

                await PushWorkflowProgress(workflowId, "Started", "工作流已启动");

                cancellationToken.ThrowIfCancellationRequested();

                // 2. 智能选择 Router（如果未指定）
                long actualRouterId = routerRoleId>0 ? routerRoleId.Value: await SelectSmartRouterAsync();
                await PushWorkflowProgress(workflowId, "RouterSelected", 
                    $"已选择Router智能体", new { routerId = actualRouterId });

                // 3. 智能推荐专家列表（如果未指定）
                Dictionary<long, string> actualExpertRoleIds = expertRoleIds;
                if (expertRoleIds == null || expertRoleIds.Count == 0)
                {
                    _logger.LogInformation("[条件路由Job] expertRoleIds为空，自动加载所有可用专家角色");
                    var aviRoles = await LoadAvailableExpertsAsync(actualRouterId);
                    actualExpertRoleIds = aviRoles.Select(x => new KeyValuePair<long, string>(x.Key, x.Value.RoleName)).ToDictionary();
                    await PushWorkflowProgress(workflowId, "ExpertsLoaded", 
                        $"已自动加载{actualExpertRoleIds.Count}个可用专家", 
                        new { experts = actualExpertRoleIds.Keys });
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 4. 🆕 使用智能路由（真正利用 Router 的推理能力）
                await PushWorkflowProgress(workflowId, "RouterAnalyzing", "Router正在智能分析需求...");
                
                var routerDecision = await SmartRouterDecisionAsync(
                    actualRouterId, 
                    userInput, 
                    actualExpertRoleIds,
                    cancellationToken);

                _logger.LogInformation("[条件路由] Router推荐: {Count}个专家, 复杂度={Complexity}, 信心={Confidence}",
                    routerDecision.SelectedExperts.Count,
                    routerDecision.TaskComplexity,
                    routerDecision.Confidence);

                cancellationToken.ThrowIfCancellationRequested();

                // 5. 根据 Router 决策创建任务（使用智能化任务名）
                var tasksToCreate = new List<Agent_TaskEntity>();
                var activeExperts = new List<string>();

                foreach (var (expertId, expertName) in routerDecision.SelectedExperts)
                {
                    activeExperts.Add(expertName);
                    
                    // 获取任务详细信息
                    var taskInfo = routerDecision.ExpertTaskInfos.GetValueOrDefault(expertId);
                    var taskDescription = taskInfo?.TaskDescription ?? "分析任务";
                    
                    // 🆕 生成智能化任务名称
                    var smartTaskName = GenerateSmartTaskName(userInput, expertName, taskDescription);
                    
                    var task = CreateTaskWithFullFields(
                        workflowId,
                        smartTaskName,  // ✅ 使用智能化任务名
                        "Analysis",
                        expertId,
                        userInput,
                        safetyLevel: 1
                    );
                    
                    // 🆕 记录 Router 的推理过程
                    task.ThoughtProcess = $"[Router决策] {routerDecision.ReasoningProcess}";
                    task.EstimatedDurationSec = ParseDuration(taskInfo?.EstimatedDuration);
                    
                    tasksToCreate.Add(task);
                }

                // 5. 批量插入任务
                if (tasksToCreate.Count > 0)
                {
                    await _taskService.InsertRangeAsync(tasksToCreate);
                }

                await PushWorkflowProgress(workflowId, "ExpertsActivated", 
                    $"已激活{activeExperts.Count}个专家（复杂度：{routerDecision.TaskComplexity}）", 
                    new { 
                        experts = activeExperts,
                        complexity = routerDecision.TaskComplexity,
                        reasoning = routerDecision.ReasoningProcess
                    });

                cancellationToken.ThrowIfCancellationRequested();

                // 6. 🆕 阶段1.2：并行执行专家（每个完成后立即推送进度）
                var expertTasks = new List<Task>();
                foreach(var task in tasksToCreate)
                {
                    // 使用分部类方法执行，记录 ReAct 推理过程
                    expertTasks.Add(ExecuteExpertWithReActTrackingAsync(
                        workflowId, task.Id, task.TaskName, task.AssignedAgentID.Value, userInput, cancellationToken));
                }

                await Task.WhenAll(expertTasks);

                // 7. 收集所有任务结果（专家结果已通过流式输出实时显示）
                var completedTasks = await _taskService.GetListAsync(t => t.WorkflowID == workflowId);
                var aggregatedResult = "【多专家协作结果汇总】\n\n";
                foreach (var task in completedTasks)
                {
                    aggregatedResult += $"## {task.TaskName}：\n{task.OutputResult}\n\n";
                }

                // 8. 更新Workflow状态
                workflow.Status = 3;
                workflow.ResultSummary = aggregatedResult;
                await _workflowService.UpdateAsync(workflow);

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                await PushWorkflowProgress(workflowId, "Completed", 
                    $"工作流执行完成，耗时{elapsed:F0}ms", 
                    new { result = aggregatedResult });

                _logger.LogInformation("[条件路由Job] 完成: WorkflowId={WorkflowId}, 耗时={Elapsed}ms", 
                    workflowId, elapsed);
            }
            catch (OperationCanceledException)
            {
                // 🆕 用户主动取消
                _logger.LogWarning("[条件路由Job] 被用户取消: WorkflowId={WorkflowId}", workflowId);
                
                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow != null)
                {
                    workflow.Status = 5; // 已取消
                    await _workflowService.UpdateAsync(workflow);
                }

                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("WorkflowCancelled", new { workflowId, message = "任务已被取消" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[条件路由Job] 失败: WorkflowId={WorkflowId}", workflowId);
                
                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow != null)
                {
                    workflow.Status = 4; // 失败
                    await _workflowService.UpdateAsync(workflow);
                }

                await PushWorkflowProgress(workflowId, "Failed", 
                    $"工作流执行失败: {ex.Message}");
            }
            finally
            {
                // 🆕 注销工作流
                _cancellationService.UnregisterWorkflow(workflowId);
            }
        }

        /// <summary>
        /// 执行单个专家并推送进度（流式输出）
        /// </summary>
        private async Task ExecuteExpertWithProgressAsync(
            long workflowId, long taskId, string taskName, long expertRoleId, string input)
        {
            try
            {
                await PushTaskStatus(workflowId, taskId, taskName, 2); // 执行中
                
                // 推送开始生成标记
                await PushExpertStreamStart(workflowId, taskId, taskName);

                var expert = await _agentRuntimeService.CreateAgentByRoleAsync(expertRoleId);
                var fullContent = "";
                
                // 使用流式 API 实时推送每个 token
                await foreach (var update in expert.RunStreamingAsync(input))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        fullContent += update.Text;
                        
                        // 实时推送 token
                        await PushExpertStreamToken(workflowId, taskId, taskName, update.Text);
                    }
                }
                
                // 推送流式完成标记
                await PushExpertStreamComplete(workflowId, taskId, taskName);

                var task = await _taskService.GetByIdAsync(taskId);
                if (task != null)
                {
                    task.Status = 3; // 完成
                    task.OutputResult = fullContent;
                    await _taskService.UpdateAsync(task);

                    await PushTaskStatus(workflowId, taskId, taskName, 3, fullContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[专家执行] 失败: TaskId={TaskId}", taskId);
                
                var task = await _taskService.GetByIdAsync(taskId);
                if (task != null)
                {
                    task.Status = 4; // 失败
                    task.ErrorDetails = ex.Message;
                    await _taskService.UpdateAsync(task);
                    await PushTaskStatus(workflowId, taskId, taskName, 4);
                }
            }
        }

        #endregion

        #region Hangfire 后台任务：FanOut/FanIn

        [AutomaticRetry(Attempts = 0)]
        [Hangfire.JobDisplayName("Workflow·扇入扇出并行 · WF{0}")]
        public async Task ExecuteFanOutFanInAsync(long workflowId, string userInput, List<long> parallelRoleIds)
        {
            // 🆕 注册取消令牌
            var cancellationToken = _cancellationService.RegisterWorkflow(workflowId);
            _logger.LogInformation("[FanOut/FanInJob] 工作流已注册到取消服务: WorkflowId={WorkflowId}", workflowId);
            
            _logger.LogInformation("[FanOut/FanInJob] 开始: WorkflowId={WorkflowId}", workflowId);
            var startTime = DateTime.Now;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow == null) return;

                // 🆕 检查是否已在启动前被强制取消
                if (workflow.Status == 5)
                {
                    throw new OperationCanceledException("工作流在启动前已被取消");
                }

                await PushWorkflowProgress(workflowId, "Started", "并行工作流已启动");

                // 🆕 阶段1.2：使用分部类方法创建任务
                var tasksToCreate = new List<Agent_TaskEntity>();
                foreach (var roleId in parallelRoleIds)
                {
                    var task = CreateTaskWithFullFields(
                        workflowId,
                        $"并行任务-{roleId}",
                        "Action",
                        roleId,
                        userInput,
                        safetyLevel: 2 // 建议级
                    );
                    tasksToCreate.Add(task);
                }

                await _taskService.InsertRangeAsync(tasksToCreate);
                await PushWorkflowProgress(workflowId, "TasksCreated", 
                    $"已创建{tasksToCreate.Count}个并行任务");

                cancellationToken.ThrowIfCancellationRequested();

                // 🆕 并行执行（使用 ReAct 跟踪，传递取消令牌）
                var executionTasks = tasksToCreate.Select(task =>
                    ExecuteExpertWithReActTrackingAsync(workflowId, task.Id, task.TaskName, 
                        task.AssignedAgentID.Value, userInput, cancellationToken)
                ).ToList();

                await Task.WhenAll(executionTasks);

                // 收集任务结果（专家结果已通过流式输出实时显示）
                var completedTasks = await _taskService.GetListAsync(t => t.WorkflowID == workflowId);
                var aggregatedResult = "【并行处理结果汇总】\n\n";
                for (int i = 0; i < completedTasks.Count; i++)
                {
                    aggregatedResult += $"## 任务 {i + 1}：\n{completedTasks[i].OutputResult}\n\n";
                }

                workflow.Status = 3;
                workflow.ResultSummary = aggregatedResult;
                await _workflowService.UpdateAsync(workflow);

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                await PushWorkflowProgress(workflowId, "Completed", 
                    $"并行工作流完成，耗时{elapsed:F0}ms", 
                    new { result = aggregatedResult });
            }
            catch (OperationCanceledException)
            {
                // 🆕 用户主动取消
                _logger.LogWarning("[FanOut/FanInJob] 被用户取消: WorkflowId={WorkflowId}", workflowId);
                
                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow != null)
                {
                    workflow.Status = 5; // 已取消
                    await _workflowService.UpdateAsync(workflow);
                }

                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("WorkflowCancelled", new { workflowId, message = "任务已被取消" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FanOut/FanInJob] 失败: WorkflowId={WorkflowId}", workflowId);
                await PushWorkflowProgress(workflowId, "Failed", $"执行失败: {ex.Message}");
            }
            finally
            {
                _cancellationService.UnregisterWorkflow(workflowId);
            }
        }

        #endregion

        #region Hangfire 后台任务：顺序链

        [AutomaticRetry(Attempts = 0)]
        [Hangfire.JobDisplayName("Workflow·顺序链执行 · WF{0}")]
        public async Task ExecuteSequentialChainAsync(
            long workflowId, 
            string userInput, 
            long plannerRoleId, 
            long executorRoleId, 
            long reviewerRoleId)
        {
            // 🆕 注册取消令牌
            var cancellationToken = _cancellationService.RegisterWorkflow(workflowId);
            _logger.LogInformation("[顺序链Job] 工作流已注册到取消服务: WorkflowId={WorkflowId}", workflowId);
            
            _logger.LogInformation("[顺序链Job] 开始: WorkflowId={WorkflowId}", workflowId);
            var startTime = DateTime.Now;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow == null) return;

                // 🆕 检查是否已在启动前被强制取消
                if (workflow.Status == 5)
                {
                    throw new OperationCanceledException("工作流在启动前已被取消");
                }

                await PushWorkflowProgress(workflowId, "Started", "顺序链工作流已启动");

                // 🆕 阶段1.2：使用分部类方法创建3个任务（完整字段）
                var plannerTask = CreateTaskWithFullFields(
                    workflowId,
                    "Planner-规划任务",
                    "Analysis",
                    plannerRoleId,
                    userInput,
                    safetyLevel: 1
                );
                
                var executorTask = CreateTaskWithFullFields(
                    workflowId,
                    "Executor-执行任务",
                    "Action",
                    executorRoleId,
                    "等待Planner输出",
                    safetyLevel: 3 // Action 类型，较高风险
                );
                executorTask.Status = 0; // 待调度
                
                var reviewerTask = CreateTaskWithFullFields(
                    workflowId,
                    "Reviewer-审核任务",
                    "Verification",
                    reviewerRoleId,
                    "等待Executor输出",
                    safetyLevel: 2
                );
                reviewerTask.Status = 0; // 待调度

                await _taskService.InsertRangeAsync(new List<Agent_TaskEntity> 
                    { plannerTask, executorTask, reviewerTask });

                cancellationToken.ThrowIfCancellationRequested();

                // Step 1: Planner
                await PushWorkflowProgress(workflowId, "Step1_Planning", "步骤1: 任务规划中...");
                await PushTaskStatus(workflowId, plannerTask.Id, plannerTask.TaskName, 2);

                var plannerAgent = await _agentRuntimeService.CreateAgentByRoleAsync(plannerRoleId);
                var plannerResponse = await _agentRuntimeService.RunAgentAsync(plannerAgent, userInput);
                plannerTask.Status = 3;
                plannerTask.OutputResult = plannerResponse.Content;
                await _taskService.UpdateAsync(plannerTask);
                await PushTaskStatus(workflowId, plannerTask.Id, plannerTask.TaskName, 3, plannerResponse.Content);

                cancellationToken.ThrowIfCancellationRequested();

                // Step 2: Executor
                await PushWorkflowProgress(workflowId, "Step2_Executing", "步骤2: 任务执行中...");
                await PushTaskStatus(workflowId, executorTask.Id, executorTask.TaskName, 2);

                var executorAgent = await _agentRuntimeService.CreateAgentByRoleAsync(executorRoleId);
                executorTask.InputParams = $"根据以下规划执行任务：\n\n{plannerResponse.Content}";
                var executorResponse = await _agentRuntimeService.RunAgentAsync(executorAgent, executorTask.InputParams);
                executorTask.Status = 3;
                executorTask.OutputResult = executorResponse.Content;
                await _taskService.UpdateAsync(executorTask);
                await PushTaskStatus(workflowId, executorTask.Id, executorTask.TaskName, 3, executorResponse.Content);

                cancellationToken.ThrowIfCancellationRequested();

                // Step 3: Reviewer
                await PushWorkflowProgress(workflowId, "Step3_Reviewing", "步骤3: 结果审核中...");
                await PushTaskStatus(workflowId, reviewerTask.Id, reviewerTask.TaskName, 2);

                var reviewerAgent = await _agentRuntimeService.CreateAgentByRoleAsync(reviewerRoleId);
                reviewerTask.InputParams = $"审核以下执行结果：\n\n原始任务: {userInput}\n\n执行结果: {executorResponse.Content}";
                var reviewerResponse = await _agentRuntimeService.RunAgentAsync(reviewerAgent, reviewerTask.InputParams);
                reviewerTask.Status = 3;
                reviewerTask.OutputResult = reviewerResponse.Content;
                await _taskService.UpdateAsync(reviewerTask);
                await PushTaskStatus(workflowId, reviewerTask.Id, reviewerTask.TaskName, 3, reviewerResponse.Content);

                // 更新Workflow
                var finalResult = $@"【顺序链执行结果】

## 1. 规划阶段 (Planner)
{plannerResponse.Content}

## 2. 执行阶段 (Executor)
{executorResponse.Content}

## 3. 审核阶段 (Reviewer)
{reviewerResponse.Content}";

                workflow.Status = 3;
                workflow.ResultSummary = finalResult;
                await _workflowService.UpdateAsync(workflow);

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                await PushWorkflowProgress(workflowId, "Completed", 
                    $"顺序链完成，耗时{elapsed:F0}ms", new { result = finalResult });
            }
            catch (OperationCanceledException)
            {
                // 🆕 用户主动取消
                _logger.LogWarning("[顺序链Job] 被用户取消: WorkflowId={WorkflowId}", workflowId);
                
                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow != null)
                {
                    workflow.Status = 5; // 已取消
                    await _workflowService.UpdateAsync(workflow);
                }

                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("WorkflowCancelled", new { workflowId, message = "任务已被取消" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[顺序链Job] 失败: WorkflowId={WorkflowId}", workflowId);
                await PushWorkflowProgress(workflowId, "Failed", $"执行失败: {ex.Message}");
            }
            finally
            {
                _cancellationService.UnregisterWorkflow(workflowId);
            }
        }

        #endregion
    }
}
