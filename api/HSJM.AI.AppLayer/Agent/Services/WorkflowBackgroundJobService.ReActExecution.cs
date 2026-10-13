using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using HSJM.AI.AppLayer.Hubs;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using HSJM.AI.Utility;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// ReAct 执行模式
    /// </summary>
    public enum ReActExecutionMode
    {
        /// <summary>
        /// 自定义引擎模式（默认）- 完全控制工具调用流程
        /// </summary>
        Custom,

        /// <summary>
        /// 传统框架模式 - 使用 Microsoft.Agents.AI 框架自动调用工具
        /// </summary>
        Legacy
    }

    /// <summary>
    /// WorkflowBackgroundJobService 分部类 - ReAct 执行引擎
    /// 🚀 核心功能：实现智能体 "边思考边行动" 的自主循环能力
    /// 🔧 支持两种执行模式：Custom（完全自定义）和 Legacy（框架自动）
    /// </summary>
    public partial class WorkflowBackgroundJobService
    {
        #region ReAct 核心执行流程

        /// <summary>
        /// ReAct 模式执行任务
        /// </summary>
        /// <param name="taskId">任务ID</param>
        /// <param name="mode">执行模式，默认使用 Custom（完全自定义引擎）</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<bool> ExecuteReActTaskAsync(
            long taskId, 
            ReActExecutionMode mode = ReActExecutionMode.Custom,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[ReAct] 开始执行任务: TaskID={TaskId}, Mode={Mode}", taskId, mode);

            return mode switch
            {
                ReActExecutionMode.Custom => await ExecuteReActTaskCustomAsync(taskId, cancellationToken),
                ReActExecutionMode.Legacy => await ExecuteReActTaskLegacyAsync(taskId, cancellationToken),
                _ => await ExecuteReActTaskCustomAsync(taskId, cancellationToken)
            };
        }

        #endregion

        #region Custom 模式 - 使用自定义 ReActExecutionEngine

        /// <summary>
        /// 使用自定义 ReActExecutionEngine 执行任务
        /// 优势：完全控制工具调用流程，每次调用可见、可拦截
        /// </summary>
        private async Task<bool> ExecuteReActTaskCustomAsync(long taskId, CancellationToken cancellationToken)
        {
            var task = await _taskService.GetByIdAsync(taskId);
            if (task == null)
            {
                _logger.LogError("[ReAct-Custom] 任务不存在: TaskID={TaskId}", taskId);
                return false;
            }

            _logger.LogInformation("[ReAct-Custom] 使用自定义引擎执行: {TaskName} (ID={TaskId})", task.TaskName, taskId);

            // 加载角色
            var agentRole = await _agentRoleService.GetByIdAsync(task.AssignedAgentID ?? 0);
            var agentName = agentRole?.RoleName ?? "未知智能体";
            if (agentRole == null)
            {
                _logger.LogError("[ReAct-Custom] 智能体角色不存在: AgentID={AgentId}", task.AssignedAgentID);
                return false;
            }

            // 初始化任务状态
            task.ReActLoopCount = 0;
            task.MaxLoopCount = task.MaxLoopCount > 0 ? task.MaxLoopCount : 10;
            task.ThoughtProcess = "";
            task.ActionTaken = "";
            task.ObservationResult = "";
            task.Status = 2; // 执行中
            task.StartTime = DateTime.Now;
            await _taskService.UpdateAsync(task);

            // SignalR 推送任务开始
            await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                .SendAsync("TaskStarted", new { taskId, taskName = task.TaskName, mode = "Custom", timestamp = DateTime.Now });

            try
            {
                // 创建 IChatClient
                IChatClient chatClient;
                if (agentRole.ModelID.HasValue && agentRole.ModelID.Value > 0)
                {
                    chatClient = (await _llmModelService.CreateChatClientByModelIdAsync(agentRole.ModelID.Value)).AsIChatClient();
                }
                else
                {
                    var recommendedModel = await _llmModelService.RecommendModelAsync();
                    if (recommendedModel == null)
                    {
                        throw new InvalidOperationException($"无法为角色 {agentRole.RoleName} 找到合适的模型");
                    }
                    chatClient = (await _llmModelService.CreateChatClientByModelIdAsync(recommendedModel.Id)).AsIChatClient();
                }

                // 🆕 先提取用户原始输入（用于传递给技能执行引擎）
                string? originalUserInput = null;
                if (!string.IsNullOrEmpty(task.InputSnapshotJson))
                {
                    try
                    {
                        var snapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(task.InputSnapshotJson);
                        if (snapshot != null && snapshot.TryGetValue("user_input", out var userInputElement))
                        {
                            originalUserInput = userInputElement.GetString();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ReAct-Custom] 解析 InputSnapshot 失败");
                    }
                }
                // 如果没有从 snapshot 获取到，使用 InputParams
                if (string.IsNullOrEmpty(originalUserInput))
                {
                    originalUserInput = task.InputParams;
                }

                // 🆕 加载角色关联的 Skills（知识增强）
                var skills = await _skillLoader.LoadSkillsForRoleAsync(agentRole.Id);
                if (skills.Count > 0)
                {
                    _logger.LogInformation("[ReAct-Custom] 加载了 {Count} 个 Skills: {Names}", 
                        skills.Count, string.Join(", ", skills.Select(s => s.Name)));
                }

                // 加载工具（传递原始用户请求上下文）
                var tools = await _agentRuntime.LoadToolsForRoleAsync(
                    agentRole.Id, 
                    skills,
                    originalUserInput,
                    task.WorkflowID,  // 🆕 传递 WorkflowID
                    taskId);          // 🆕 传递 TaskID
                
                // 🆕 可选：根据 Skills 的 allowed-tools 过滤工具
                if (skills.Count > 0)
                {
                    var allowedTools = _skillLoader.GetAllowedToolsFromSkills(skills);
                    if (allowedTools.Count > 0)
                    {
                        var originalCount = tools.Count;
                        tools = tools.Where(t => allowedTools.Contains(t.Name)).ToList();
                        _logger.LogInformation("[ReAct-Custom] Skills 限制工具从 {Original} 过滤为 {Filtered}: {Tools}",
                            originalCount, tools.Count, string.Join(", ", allowedTools));
                    }
                }

                // 🆕 从 Workflow.GlobalContext 获取 ChatSessionId
                long? chatSessionId = null;
                if (task.WorkflowID.HasValue)
                {
                    var workflow = await _workflowService.GetByIdAsync(task.WorkflowID.Value);
                    if (workflow != null && !string.IsNullOrEmpty(workflow.GlobalContext))
                    {
                        try
                        {
                            var globalContext = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GlobalContext);
                            if (globalContext != null && globalContext.TryGetValue("ChatSessionId", out var sessionIdElement))
                            {
                                chatSessionId = sessionIdElement.GetInt64();
                                _logger.LogInformation("[ReAct-Custom] 获取到ChatSessionId: {ChatSessionId}", chatSessionId);
                            }
                            else {
                                chatSessionId = workflow.CorrelationID.ToLong();
                            }
                        }
                        catch (Exception jsonEx)
                        {
                            _logger.LogWarning(jsonEx, "[ReAct-Custom] 解析GlobalContext失败: WorkflowId={WorkflowId}", task.WorkflowID);
                        }
                    }
                }

                // 构建上下文信息
                var contextInfo = BuildContextInfo(task);

                // 配置 ReAct 执行
                var config = new ReActExecutionConfig
                {
                    SessionId = $"task-{taskId}",
                    ChatSessionId = chatSessionId, // 🆕 传递 ChatSessionId 以加载历史对话
                    RoleId = agentRole.Id,
                    TaskDescription = $"{contextInfo}\n【当前任务】\n{task.TaskName}\n{task.InputParams}",
                    SystemPrompt = agentRole.SystemPrompt ?? "你是一个智能助手。",
                    Tools = tools,
                    Skills = skills,  // 🆕 传递技能列表
                    OriginalUserRequest = originalUserInput,  // 🆕 传递用户原始请求
                    MaxIterations = task.MaxLoopCount ?? 10,
                    EnableReflection = true,
                    CallLimits = new ToolCallLimits
                    {
                        MaxCallsPerTool = 5,
                        MaxTotalCalls = 20,
                        MaxDuplicateCalls = 2,
                        DuplicateWarningThreshold = 1
                    },
                    OnStepComplete = async step =>
                    {
                        // 实时推送每个步骤
                        await PushReActStep(task.WorkflowID.Value, taskId, agentName, step);
                        
                        // 更新任务记录
                        if (step.Phase == ReActPhase.Thought)
                        {
                            task.ThoughtProcess += $"\n[第{step.Iteration}轮] {step.Content}";
                            task.ReActLoopCount = step.Iteration;
                        }
                        else if (step.Phase == ReActPhase.Action)
                        {
                            task.ActionTaken += $"\n[第{step.Iteration}轮] 工具={step.ToolCall?.ToolName}, 状态={(step.Success ? "成功" : "失败")}";
                            task.ObservationResult += $"\n[第{step.Iteration}轮] {step.Content}";
                        }
                    }
                };

                // 执行 ReAct 循环
                var result = await _reActEngine.ExecuteAsync(chatClient, config, cancellationToken);

                // 处理执行结果
                if (result.Success)
                {
                    task.Status = 3; // 已完成
                    task.OutputResult = result.FinalAnswer;
                    
                    _logger.LogInformation("[ReAct-Custom] ✅ 任务完成: {TaskName}, 迭代{Iterations}次", 
                        task.TaskName, result.TotalIterations);

                    // 推送任务完成
                    await PushAgentOutput(task.WorkflowID.Value, taskId, agentName, result.FinalAnswer);
                    await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                        .SendAsync("TaskCompleted", new { taskId, taskName = task.TaskName, result = result.FinalAnswer, loopCount = result.TotalIterations });
                }
                else if (result.ErrorMessage == "NEED_HUMAN_HELP")
                {
                     // 🆕 处理人工协助请求
                     task.Status = 6; // 待人工确认
                     task.NeedHumanReview = true;
                     task.HumanInteractionData = JsonSerializer.Serialize(new
                     {
                         question = result.FinalAnswer, // 之前这里已提取了问题
                         context = new { iterations = result.TotalIterations, last_thought = task.ThoughtProcess },
                         timestamp = DateTime.Now
                     });

                     _logger.LogInformation("[ReAct-Custom] 🤚 请求人工协助: {TaskName}", task.TaskName);
                     
                     await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                         .SendAsync("HumanReviewRequired", new { taskId, taskName = task.TaskName, interactionData = task.HumanInteractionData });
                     
                     await PushTaskStatus(task.WorkflowID.Value, taskId, task.TaskName, 6, "等待人工确认: " + result.FinalAnswer);
                     
                     // ⚠️ 重要：直接返回 false，但不要标记为失败，这样 Workflow 会暂停但不是 Fail
                     // 但根据 ExecuteReActTaskAsync 的返回值，false 可能会导致外部认为失败？
                     // 可以在 TaskService 里检查 task.Status == 6
                     return false; 
                }
                else
                {
                    task.Status = 4; // 失败
                    task.ErrorDetails = result.ErrorMessage;
                    
                    _logger.LogWarning("[ReAct-Custom] ❌ 任务失败: {TaskName}, 原因={Error}", 
                        task.TaskName, result.ErrorMessage);

                    await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                        .SendAsync("TaskFailed", new { taskId, reason = result.ErrorMessage });
                }

                // 输出诊断报告
                if (result.ToolCallSummary != null && result.ToolCallSummary.TotalCalls > 0)
                {
                    _logger.LogInformation("[ReAct-Custom-诊断] 工具调用统计: 总计={Total}, 工具分布={Tools}, 重复调用={Duplicates}",
                        result.ToolCallSummary.TotalCalls,
                        string.Join(", ", result.ToolCallSummary.ToolCallCounts.Select(kv => $"{kv.Key}:{kv.Value}")),
                        string.Join(", ", result.ToolCallSummary.DuplicateCallCounts.Select(kv => $"{kv.Key}:{kv.Value}")));
                }

                await PushTaskStatus(task.WorkflowID.Value, taskId, task.TaskName, task.Status ?? 0, task.OutputResult ?? task.ErrorDetails);
            }
            catch (Exception ex)
            {
                task.Status = 4;
                task.ErrorDetails = $"ReAct-Custom 执行异常: {ex.Message}\n堆栈: {ex.StackTrace}";
                _logger.LogError(ex, "[ReAct-Custom] 任务执行失败: {TaskName}", task.TaskName);

                await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                    .SendAsync("TaskFailed", new { taskId, taskName = task.TaskName, error = ex.Message });
                await PushTaskStatus(task.WorkflowID.Value, taskId, task.TaskName, 4, task.ErrorDetails);
            }
            finally
            {
                task.EndTime = DateTime.Now;
                await _taskService.UpdateAsync(task);
            }

            // status 3=完成, 6=待人工确认
            return task.Status == 3 || task.Status == 6;
        }

        /// <summary>
        /// 推送 ReAct 步骤到前端
        /// </summary>
        private async Task PushReActStep(long workflowId, long taskId, string agentName, ReActStep step)
        {
            var eventName = step.Phase switch
            {
                ReActPhase.Thought => "ReActThought",
                ReActPhase.Action => "ReActAction",
                ReActPhase.Reflection => "ReActReflection",
                _ => "ReActStep"
            };

            await _hubContext.Clients.Group($"workflow-{workflowId}")
                .SendAsync(eventName, new
                {
                    taskId,
                    loopCount = step.Iteration,
                    phase = step.Phase.ToString(),
                    content = step.Content,
                    toolName = step.ToolCall?.ToolName,
                    success = step.Success,
                    wasIntercepted = step.WasIntercepted,
                    interceptReason = step.InterceptReason,
                    durationMs = step.DurationMs
                });

            // 推送思考过程
            if (step.Phase == ReActPhase.Thought)
            {
                await PushAgentThinking(workflowId, taskId, agentName, $"[第{step.Iteration}轮]\n{step.Content}");
            }
            
            // 推送工具调用
            if (step.Phase == ReActPhase.Action && step.ToolCall != null)
            {
                object parametersPayload = step.ToolCall.Arguments ?? "";
                try
                {
                    if (!string.IsNullOrWhiteSpace(step.ToolCall.Arguments))
                    {
                        parametersPayload = JsonSerializer.Deserialize<object>(step.ToolCall.Arguments) ?? step.ToolCall.Arguments;
                    }
                }
                catch { }

                await PushToolCalling(workflowId, taskId, step.ToolCall.ToolName, parametersPayload);
                await PushToolResult(workflowId, taskId, step.ToolCall.ToolName, step.Content, step.DurationMs, step.Success);
            }
        }

        /// <summary>
        /// 构建上下文信息
        /// </summary>
        private string BuildContextInfo(Agent_TaskEntity task)
        {
            if (string.IsNullOrEmpty(task.InputSnapshotJson))
                return "";

            try
            {
                var contextSnapshot = JsonSerializer.Deserialize<Dictionary<string, object>>(task.InputSnapshotJson);
                if (contextSnapshot == null)
                    return "";

                var contextInfo = $"【完整上下文信息】\n用户原始输入: {contextSnapshot.GetValueOrDefault("user_input")}\n";

                var prevOutputs = contextSnapshot.Where(kv => kv.Key.EndsWith("_output")).ToList();
                if (prevOutputs.Any())
                {
                    contextInfo += "上游任务输出:\n";
                    foreach (var prev in prevOutputs)
                    {
                        var output = prev.Value?.ToString() ?? "";
                        contextInfo += $"- {prev.Key}: {(output.Length > 200 ? output.Substring(0, 200) + "..." : output)}\n";
                    }
                }

                return contextInfo;
            }
            catch
            {
                return "";
            }
        }

        #endregion

        #region Legacy 模式 - 使用框架自动工具调用

        /// <summary>
        /// 使用框架模式执行任务（原有实现）
        /// 特点：工具调用由框架自动处理，属于黑盒
        /// </summary>
        private async Task<bool> ExecuteReActTaskLegacyAsync(long taskId, CancellationToken cancellationToken)
        {
            var task = await _taskService.GetByIdAsync(taskId);
            if (task == null)
            {
                _logger.LogError("[ReAct-Legacy] 任务不存在: TaskID={TaskId}", taskId);
                return false;
            }

            _logger.LogInformation("[ReAct-Legacy] 使用框架模式执行: {TaskName} (ID={TaskId})", task.TaskName, taskId);

            // 预加载角色和工具
            var agentRole = await _agentRoleService.GetByIdAsync(task.AssignedAgentID ?? 0);
            var agentName = agentRole?.RoleName ?? "未知智能体";
            if (agentRole == null)
            {
                _logger.LogError("[ReAct-Legacy] 智能体角色不存在: AgentID={AgentId}", task.AssignedAgentID);
                return false;
            }

            // 预加载工具列表（传递原始用户请求上下文）
            var aiTools = await _agentRuntime.LoadToolsForRoleAsync(
                agentRole.Id, 
                null,
                task.InputParams,
                task.WorkflowID,  // 🆕 传递 WorkflowID
                taskId);          // 🆕 传递 TaskID
            
            // 创建诊断会话 ID
            var diagnosticSessionId = $"task-{taskId}";
            
            // 预创建带诊断的Agent实例
            var agentWithTools = await _agentRuntime.CreateAgentWithDiagnosticsAsync(
                agentRole.Id, 
                diagnosticSessionId,
                new ToolCallLimits
                {
                    MaxCallsPerTool = 5,
                    MaxTotalCalls = 20,
                    MaxDuplicateCalls = 2,
                    DuplicateWarningThreshold = 1
                });
            
            // 预创建不带工具的Agent实例
            var agentForThinking = await _agentRuntime.CreateAgentByRoleAsync(agentRole.Id);

            // 初始化 ReAct 字段
            task.ReActLoopCount = 0;
            task.MaxLoopCount = task.MaxLoopCount > 0 ? task.MaxLoopCount : 10;
            task.ThoughtProcess = "";
            task.ActionTaken = "";
            task.ObservationResult = "";
            task.Status = 2;
            task.StartTime = DateTime.Now;
            await _taskService.UpdateAsync(task);

            // SignalR 推送任务开始
            await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                .SendAsync("TaskStarted", new { taskId, taskName = task.TaskName, mode = "Legacy", timestamp = DateTime.Now });

            bool isFinished = false;
            string finalResult = "";

            try
            {
                while (!isFinished && task.ReActLoopCount < task.MaxLoopCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    task.ReActLoopCount++;
                    _logger.LogInformation("[ReAct-Legacy] 第 {LoopCount} 轮循环开始...", task.ReActLoopCount);

                    await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                        .SendAsync("ReActLoopStarted", new { taskId, loopCount = task.ReActLoopCount });

                    // Step 1: Thought
                    var thought = await ThinkAsync(task, agentForThinking, aiTools);
                    task.ThoughtProcess += $"\n[第{task.ReActLoopCount}轮] {thought.Reasoning}";

                    await PushAgentThinking(task.WorkflowID.Value, task.Id, agentName, $"[第{task.ReActLoopCount}轮循环]\n{thought.Reasoning}");
                    await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                        .SendAsync("ReActThought", new { taskId, loopCount = task.ReActLoopCount, thought = thought.Reasoning });

                    // Step 2: Action
                    string observation = "";
                    bool actionSuccess = true;
                    
                    if (thought.NeedToolCall)
                    {
                        _logger.LogInformation("[ReAct-Legacy-循环{LoopCount}] 开始执行Action: 工具={ToolName}", 
                            task.ReActLoopCount, thought.ToolName);
                        
                        var actionResult = await ExecuteToolCallAsync(task, thought.ToolName, thought.ToolParameters, agentWithTools);
                        observation = actionResult.Result;
                        actionSuccess = actionResult.Success;
                        
                        task.ActionTaken += $"\n[第{task.ReActLoopCount}轮] 工具={thought.ToolName}, 状态={(actionSuccess ? "成功" : "失败")}";
                        task.ObservationResult += $"\n[第{task.ReActLoopCount}轮] {observation}";

                        await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                            .SendAsync("ReActAction", new { taskId, loopCount = task.ReActLoopCount, toolName = thought.ToolName, parameters = thought.ToolParameters });
                        await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                            .SendAsync("ReActObservation", new { taskId, loopCount = task.ReActLoopCount, observation, success = actionSuccess });
                    }
                    else
                    {
                        observation = thought.DirectAnswer ?? thought.Reasoning;
                        _logger.LogInformation("[ReAct-Legacy-循环{LoopCount}] 无需工具调用", task.ReActLoopCount);
                    }

                    // Step 4: Reflection
                    var reflection = await ReflectAsync(task, thought, observation, actionSuccess, agentForThinking, aiTools);

                    _logger.LogInformation("[ReAct-Legacy-循环{LoopCount}] Reflection决策: {Decision}", 
                        task.ReActLoopCount, reflection.Decision);

                    await PushAgentDecision(task.WorkflowID.Value, task.Id, reflection.Decision.ToString(), reflection.Reason, null);
                    await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                        .SendAsync("ReActReflection", new { taskId, loopCount = task.ReActLoopCount, decision = reflection.Decision, reason = reflection.Reason, confidence = reflection.Confidence });

                    if (reflection.Decision == ReActDecision.TaskCompleted)
                    {
                        isFinished = true;
                        finalResult = reflection.FinalResult;
                        task.OutputResult = finalResult;
                        
                        await PushAgentOutput(task.WorkflowID.Value, task.Id, agentName, finalResult);
                    }
                    else if (reflection.Decision == ReActDecision.NeedHumanHelp)
                    {
                        task.NeedHumanReview = true;
                        task.HumanInteractionData = JsonSerializer.Serialize(new
                        {
                            question = "智能体遇到困难，需要您的帮助",
                            context = new { thought = thought.Reasoning, observation, loopCount = task.ReActLoopCount },
                            options = new[] { "提供更多信息", "更换工具", "跳过此步骤", "终止任务" }
                        });
                        task.Status = 6;
                        await _taskService.UpdateAsync(task);
                        
                        await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                            .SendAsync("HumanReviewRequired", new { taskId, taskName = task.TaskName, interactionData = task.HumanInteractionData });
                        
                        return false;
                    }

                    if (!thought.NeedToolCall && !string.IsNullOrEmpty(thought.DirectAnswer))
                    {
                        isFinished = true;
                        finalResult = thought.DirectAnswer;
                    }
                    
                    await _taskService.UpdateAsync(task);
                }

                if (task.ReActLoopCount >= task.MaxLoopCount && !isFinished)
                {
                    task.Status = 4;
                    task.ErrorDetails = $"ReAct 循环达到上限 {task.MaxLoopCount} 次，强制终止";
                    
                    await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                        .SendAsync("TaskFailed", new { taskId, reason = "超出最大循环次数" });
                }
                else
                {
                    task.Status = 3;
                    task.OutputResult = finalResult;
                    await _taskService.UpdateAsync(task);
                    
                    _logger.LogInformation("[ReAct-Legacy] ✅ 任务执行成功: {TaskName}, 循环{LoopCount}次", 
                        task.TaskName, task.ReActLoopCount);
                    
                    await PushAgentOutput(task.WorkflowID.Value, task.Id, agentName, finalResult);
                    await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                        .SendAsync("TaskCompleted", new { taskId, taskName = task.TaskName, result = finalResult, loopCount = task.ReActLoopCount });

                    await PushTaskStatus(task.WorkflowID.Value, task.Id, task.TaskName, 3, finalResult);
                }
            }
            catch (Exception ex)
            {
                task.Status = 4;
                task.ErrorDetails = $"ReAct-Legacy 执行异常: {ex.Message}\n堆栈: {ex.StackTrace}";
                _logger.LogError(ex, "[ReAct-Legacy] 任务执行失败: {TaskName}", task.TaskName);
                
                await _hubContext.Clients.Group($"workflow-{task.WorkflowID}")
                    .SendAsync("TaskFailed", new { taskId, taskName = task.TaskName, error = ex.Message });
                await PushTaskStatus(task.WorkflowID.Value, task.Id, task.TaskName, 4, task.ErrorDetails);
            }
            finally
            {
                var diagnosticSummary = _agentRuntime.CloseDiagnosticSession(diagnosticSessionId);
                if (diagnosticSummary.TotalCalls > 0)
                {
                    _logger.LogInformation("[ReAct-Legacy-诊断] 工具调用统计: 总计={Total}, 工具分布={Tools}, 重复调用={Duplicates}",
                        diagnosticSummary.TotalCalls,
                        string.Join(", ", diagnosticSummary.ToolCallCounts.Select(kv => $"{kv.Key}:{kv.Value}")),
                        string.Join(", ", diagnosticSummary.DuplicateCallCounts.Select(kv => $"{kv.Key}:{kv.Value}")));
                }
                
                task.EndTime = DateTime.Now;
                await _taskService.UpdateAsync(task);
            }
            
            return task.Status == 3;
        }

        #endregion

        #region Legacy 模式辅助方法

        /// <summary>
        /// 执行工具调用（Legacy模式 - Agent自动调用工具）
        /// </summary>
        private async Task<(bool Success, string Result)> ExecuteToolCallAsync(
            Agent_TaskEntity task, 
            string toolName, 
            string toolParameters,
            ChatClientAgent agent)
        {
            try
            {
                var contextSnapshot = string.IsNullOrEmpty(task.InputSnapshotJson) 
                    ? null 
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(task.InputSnapshotJson);
                
                var contextInfo = "";
                if (contextSnapshot != null)
                {
                    contextInfo = $"[上下文信息]\n用户原始输入: {contextSnapshot.GetValueOrDefault("user_input")}\n";
                    var prevOutputs = contextSnapshot.Where(kv => kv.Key.EndsWith("_output")).ToList();
                    if (prevOutputs.Any())
                    {
                        contextInfo += "上游任务输出:\n";
                        foreach (var prev in prevOutputs)
                        {
                            contextInfo += $"- {prev.Key}: {prev.Value}\n";
                        }
                    }
                    contextInfo += "\n";
                }

                var actionPrompt = $@"{contextInfo}[当前任务]
{task.InputParams}

根据之前的思考，需要执行以下操作：
{task.ThoughtProcess}

【强制要求】
1. 只允许调用一次且仅允许调用指定工具：{toolName}
2. 工具参数如下（原样使用，不要改写）：
```json
{toolParameters}
```
3. 工具返回后，直接输出最终结果，不要再次调用任何工具。
";

                object parametersPayload = toolParameters;
                try
                {
                    if (!string.IsNullOrWhiteSpace(toolParameters))
                    {
                        parametersPayload = JsonSerializer.Deserialize<object>(toolParameters) ?? toolParameters;
                    }
                }
                catch { }

                await PushToolCalling(task.WorkflowID.Value, task.Id, toolName, parametersPayload);

                var startTime = DateTime.UtcNow;
                var response = await _agentRuntime.RunAgentAsync(agent, actionPrompt);
                var durationMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

                var resultContent = response.Content ?? string.Empty;
                await PushToolResult(task.WorkflowID.Value, task.Id, toolName, resultContent, durationMs, true);

                _logger.LogInformation("[ReAct-Legacy-Action] ✅ Agent执行成功: 工具={ToolName}", toolName);
                return (true, resultContent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReAct-Legacy-Action] ❌ Agent执行失败: 工具={ToolName}", toolName);
                try
                {
                    await PushToolResult(task.WorkflowID.Value, task.Id, toolName, $"执行失败: {ex.Message}", 0, false);
                }
                catch { }
                return (false, $"执行失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Step 1: Thought（思考）- 智能体分析当前状态并决定下一步动作
        /// </summary>
        private async Task<ReActThought> ThinkAsync(
            Agent_TaskEntity task, 
            ChatClientAgent agent, 
            List<AIFunction> aiTools)
        {
            _logger.LogDebug("[ReAct-Legacy-Thought] 智能体开始思考...");

            var contextSnapshot = string.IsNullOrEmpty(task.InputSnapshotJson)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(task.InputSnapshotJson);

            var contextInfo = "";
            if (contextSnapshot != null)
            {
                contextInfo = $"\n【完整上下文信息】\n用户原始输入: {contextSnapshot.GetValueOrDefault("user_input")}\n";
                var prevOutputs = contextSnapshot.Where(kv => kv.Key.EndsWith("_output")).ToList();
                if (prevOutputs.Any())
                {
                    contextInfo += "上游任务输出:\n";
                    foreach (var prev in prevOutputs)
                    {
                        var output = prev.Value?.ToString() ?? "";
                        contextInfo += $"- {prev.Key}: {(output.Length > 200 ? output.Substring(0, 200) + "..." : output)}\n";
                    }
                }
            }
           
            var thinkPrompt = $@"
{contextInfo}
【当前任务】
任务名称: {task.TaskName}
任务输入: {task.InputParams}
当前状态: 第 {task.ReActLoopCount} 轮循环

【历史执行记录】
思考过程: {task.ThoughtProcess ?? "无"}
已执行动作: {task.ActionTaken ?? "无"}
观察结果: {task.ObservationResult ?? "无"}

【本轮可用工具】
{JsonHelper.SerializeObject(aiTools.Select(x => new { 工具名称 = x.Name, 描述 = x.Description }))}

【你的任务】
请分析当前状态，决定下一步动作。

请以 JSON 格式输出：
```json
{{
  ""need_tool_call"": true/false,
  ""tool_name"": ""工具名称"",
  ""reasoning"": ""你的思考过程和决策依据"",
  ""direct_answer"": ""直接答案""
}}
```
";

            var llmResponse = await _agentRuntime.RunAgentAsync(agent, thinkPrompt);
            var thought = ParseThoughtResponse(llmResponse.Content);
            _logger.LogDebug("[ReAct-Legacy-Thought] 智能体思考结果: {Reasoning}", thought.Reasoning);
            return thought;
        }

        /// <summary>
        /// Step 4: Reflection（反思）- 智能体分析执行结果并决定下一步策略
        /// </summary>
        private async Task<ReActReflection> ReflectAsync(
            Agent_TaskEntity task, 
            ReActThought thought, 
            string observation, 
            bool actionSuccess,
            ChatClientAgent agent,
            List<AIFunction> aiTools)
        {
            _logger.LogDebug("[ReAct-Legacy-Reflection] 智能体开始反思...");

            var reflectPrompt = $@"
【执行情况回顾】
任务名称: {task.TaskName}
当前循环: 第 {task.ReActLoopCount} 轮
你的思考: {thought.Reasoning}
执行的动作: 调用 {thought.ToolName}
观察到的结果: {observation}
执行状态: {(actionSuccess ? "成功" : "失败")}

【本轮可用工具】
{JsonHelper.SerializeObject(aiTools.Select(x => new { 工具名称 = x.Name, 描述 = x.Description }))}

【决策要求】
请判断当前状态并决定下一步策略：
1. task_completed - 任务已完成
2. continue - 继续尝试
3. switch_strategy - 切换策略
4. need_human_help - 需要人工帮助

输出 JSON 格式：
```json
{{
  ""decision"": ""task_completed/continue/switch_strategy/need_human_help"",
  ""reason"": ""决策依据"",
  ""final_result"": ""最终答案"" (仅 task_completed 时需要),
  ""confidence"": 0.0-1.0
}}
```
";

            var llmResponse = await _agentRuntime.RunAgentAsync(agent, reflectPrompt);
            var reflection = ParseReflectionResponse(llmResponse.Content);
            _logger.LogDebug("[ReAct-Legacy-Reflection] 智能体决策: {Decision}", reflection.Decision);
            return reflection;
        }

        #endregion

        #region 解析辅助方法

        private ReActThought ParseThoughtResponse(string llmResponse)
        {
            try
            {
                var jsonContent = ExtractJsonFromMarkdown(llmResponse);
                var json = JsonDocument.Parse(jsonContent);
                var root = json.RootElement;

                return new ReActThought
                {
                    NeedToolCall = root.GetProperty("need_tool_call").GetBoolean(),
                    ToolName = root.TryGetProperty("tool_name", out var toolName) ? toolName.GetString() : null,
                    ToolParameters = root.TryGetProperty("tool_parameters", out var toolParams) ? toolParams.ToString() : null,
                    Reasoning = root.GetProperty("reasoning").GetString() ?? "",
                    DirectAnswer = root.TryGetProperty("direct_answer", out var directAnswer) ? directAnswer.GetString() : null
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReAct] 解析思考结果失败: {Response}", llmResponse);
                return new ReActThought
                {
                    NeedToolCall = false,
                    Reasoning = "解析失败，智能体返回格式错误",
                    DirectAnswer = llmResponse
                };
            }
        }

        private ReActReflection ParseReflectionResponse(string llmResponse)
        {
            try
            {
                var jsonContent = ExtractJsonFromMarkdown(llmResponse);
                var json = JsonDocument.Parse(jsonContent);
                var root = json.RootElement;

                // 尝试获取 decision 字段
                if (root.TryGetProperty("decision", out var decisionProp))
                {
                    var decisionStr = decisionProp.GetString() ?? "continue";
                    var decision = decisionStr switch
                    {
                        "task_completed" => ReActDecision.TaskCompleted,
                        "need_human_help" => ReActDecision.NeedHumanHelp,
                        "switch_strategy" => ReActDecision.SwitchStrategy,
                        _ => ReActDecision.Continue
                    };

                    string finalResultStr = null;
                    if (root.TryGetProperty("final_result", out var finalResult))
                    {
                        finalResultStr = finalResult.ValueKind == JsonValueKind.String 
                            ? finalResult.GetString()
                            : finalResult.GetRawText();
                    }
                    
                    return new ReActReflection
                    {
                        Decision = decision,
                        Reason = root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "",
                        FinalResult = finalResultStr,
                        Confidence = root.TryGetProperty("confidence", out var confidence) ? confidence.GetDouble() : 0.5
                    };
                }
                
                // 🆕 如果没有 decision 字段，尝试从其他字段推断决策
                // 这种情况通常是 LLM 返回了工具执行结果而非反思决策
                _logger.LogWarning("[ReAct] 响应中无 decision 字段，尝试推断决策: {Response}", llmResponse.Substring(0, Math.Min(200, llmResponse.Length)));
                
                // 如果包含 code_file_path、notes、file_path 等字段，说明工具已成功执行
                bool hasSuccessIndicators = root.TryGetProperty("code_file_path", out _) ||
                                           root.TryGetProperty("notes", out _) ||
                                           root.TryGetProperty("result", out _) ||
                                           root.TryGetProperty("output", out _);
                
                if (hasSuccessIndicators)
                {
                    var notes = root.TryGetProperty("notes", out var notesProp) ? notesProp.GetString() : "";
                    return new ReActReflection
                    {
                        Decision = ReActDecision.TaskCompleted,
                        Reason = notes ?? "工具执行成功，任务已完成",
                        FinalResult = jsonContent,
                        Confidence = 0.8
                    };
                }
                
                // 如果有 error 字段，说明执行失败，需要继续尝试
                if (root.TryGetProperty("error", out var errorProp))
                {
                    return new ReActReflection
                    {
                        Decision = ReActDecision.Continue,
                        Reason = $"执行出错: {errorProp.GetString()}，需要重新尝试",
                        Confidence = 0.5
                    };
                }
                
                // 默认认为任务完成（返回了结果就算完成）
                return new ReActReflection
                {
                    Decision = ReActDecision.TaskCompleted,
                    Reason = "返回了执行结果，任务视为完成",
                    FinalResult = jsonContent,
                    Confidence = 0.7
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReAct] 解析反思结果失败: {Response}", llmResponse);
                
                // 🆕 如果解析失败但响应包含成功标志，仍然认为任务完成
                if (llmResponse.Contains("成功") || llmResponse.Contains("完成") || 
                    llmResponse.Contains("success") || llmResponse.Contains("completed"))
                {
                    return new ReActReflection
                    {
                        Decision = ReActDecision.TaskCompleted,
                        Reason = "响应包含成功标志，任务视为完成",
                        FinalResult = llmResponse,
                        Confidence = 0.6
                    };
                }
                
                return new ReActReflection
                {
                    Decision = ReActDecision.Continue,
                    Reason = "解析失败，默认继续尝试",
                    Confidence = 0.3
                };
            }
        }

        private string ExtractJsonFromMarkdown(string text)
        {
            var start = text.IndexOf("```json");
            if (start >= 0)
            {
                start += 7;
                var end = text.IndexOf("```", start);
                if (end > start)
                {
                    return text.Substring(start, end - start).Trim();
                }
            }

            start = text.IndexOf('{');
            var end2 = text.LastIndexOf('}');
            if (start >= 0 && end2 > start)
            {
                return text.Substring(start, end2 - start + 1);
            }

            return text;
        }

        #endregion
    }

    #region ReAct 数据模型（Legacy模式使用）

    /// <summary>
    /// ReAct 思考结果
    /// </summary>
    public class ReActThought
    {
        public bool NeedToolCall { get; set; }
        public string? ToolName { get; set; }
        public string? ToolParameters { get; set; }
        public string Reasoning { get; set; } = "";
        public string? DirectAnswer { get; set; }
        public string? ToolResult { get; set; }
    }

    /// <summary>
    /// ReAct 反思结果
    /// </summary>
    public class ReActReflection
    {
        public ReActDecision Decision { get; set; }
        public string Reason { get; set; } = "";
        public string? FinalResult { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// ReAct 决策类型（Legacy模式使用）
    /// </summary>
    public enum ReActDecision
    {
        TaskCompleted,
        Continue,
        SwitchStrategy,
        NeedHumanHelp
    }

    #endregion
}
