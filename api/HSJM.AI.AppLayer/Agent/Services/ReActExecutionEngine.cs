using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HSJM.AI.Core;
using HSJM.AI.Utility;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 自定义 ReAct 执行引擎
    /// 
    /// 核心功能：
    /// 1. 完全控制 ReAct 循环（不使用框架的自动循环）
    /// 2. 每一步都有完整的执行上下文
    /// 3. 支持过程诊断和干预
    /// 
    /// ReAct 循环流程：
    /// Thought（思考）→ Action（行动）→ Observation（观察）→ Reflection（反思）→ [继续或结束]
    /// 
    /// 关键优势：
    /// - 每次 LLM 调用都能看到完整的执行历史
    /// - 可以在任何阶段进行诊断干预
    /// - 支持流式输出思考过程
    /// </summary>
    public class ReActExecutionEngine : ISelfScopedAutoInject
    {
        private readonly ILogger<ReActExecutionEngine> _logger;
        private readonly ToolCallTrackerService _trackerService;
        private readonly ChatSessionManagementService _chatSessionService;
        private readonly SkillLoaderService _skillLoader;  // 🆕 技能加载服务

        public ReActExecutionEngine(
            ILogger<ReActExecutionEngine> logger,
            ToolCallTrackerService trackerService,
            ChatSessionManagementService chatSessionService,
            SkillLoaderService skillLoader)  // 🆕 注入技能加载服务
        {
            _logger = logger;
            _trackerService = trackerService;
            _chatSessionService = chatSessionService;
            _skillLoader = skillLoader;  // 🆕
        }

        /// <summary>
        /// 执行 ReAct 循环
        /// </summary>
        /// <param name="chatClient">LLM 客户端</param>
        /// <param name="config">执行配置</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>执行结果</returns>
        public async Task<ReActExecutionResult> ExecuteAsync(
            IChatClient chatClient,
            ReActExecutionConfig config,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[ReAct] 开始执行: SessionId={SessionId}, ChatSessionId={ChatSessionId}, Task={Task}",
                config.SessionId, config.ChatSessionId, config.TaskDescription?.Substring(0, Math.Min(50, config.TaskDescription?.Length ?? 0)));

            // 🆕 加载历史消息（如果有ChatSessionId）
            List<ChatMessage>? historyMessages = null;
            if (config.ChatSessionId.HasValue && config.ChatSessionId.Value > 0)
            {
                historyMessages = await LoadChatHistoryAsync(config.ChatSessionId.Value);
                _logger.LogInformation("[ReAct] 加载历史消息: {Count} 条", historyMessages?.Count ?? 0);
            }

            // 初始化执行上下文
            var context = new ReActContext
            {
                SessionId = config.SessionId,
                ChatSessionId = config.ChatSessionId,
                TaskDescription = config.TaskDescription,
                SystemPrompt = config.SystemPrompt,
                AvailableTools = config.Tools,
                Skills = config.Skills,  // 🆕 技能列表
                OriginalUserRequest = config.OriginalUserRequest,  // 🆕 用户原始请求
                StartTime = DateTime.UtcNow,
                MaxIterations = config.MaxIterations,
                RoleId = config.RoleId,
                HistoryMessages = historyMessages
            };

            // 创建追踪会话
            _trackerService.CreateSession(config.SessionId, config.CallLimits);

            var result = new ReActExecutionResult
            {
                SessionId = config.SessionId,
                Success = false,
                StartTime = context.StartTime
            };

            try
            {
                // 主循环
                while (context.CurrentIteration < context.MaxIterations && !context.ShouldTerminate)
                {
                    // 🆕 首先检查是否已取消
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    context.CurrentIteration++;
                    _logger.LogInformation("[ReAct] 第 {Iteration}/{Max} 轮迭代",
                        context.CurrentIteration, context.MaxIterations);

                    // Step 1: Thought - 让 LLM 思考下一步
                    var thoughtResult = await ExecuteThoughtPhaseAsync(chatClient, context, cancellationToken);
                    context.ExecutionHistory.Add(thoughtResult);

                    // 通知回调
                    if (config.OnStepComplete != null)
                    {
                        await config.OnStepComplete(thoughtResult);
                    }

                    // 检查是否需要终止（LLM 认为任务完成）
                    if (thoughtResult.Decision == ReActStepDecision.Complete)
                    {
                        context.ShouldTerminate = true;
                        result.FinalAnswer = thoughtResult.Content;
                        result.Success = true;
                        _logger.LogInformation("[ReAct] LLM 决定任务完成");
                        break;
                    }

                    // 检查是否需要工具调用
                    if (thoughtResult.Decision == ReActStepDecision.CallTool && thoughtResult.ToolCall != null)
                    {
                        // Step 2: Action - 执行工具调用
                        var actionResult = await ExecuteActionPhaseAsync(context, thoughtResult.ToolCall, cancellationToken);
                        context.ExecutionHistory.Add(actionResult);

                        if (config.OnStepComplete != null)
                        {
                            await config.OnStepComplete(actionResult);
                        }

                        // Step 3: Observation - 记录工具执行结果（已在 actionResult 中）

                        // Step 4: Reflection - 让 LLM 反思结果（可选，取决于配置）
                        if (config.EnableReflection)
                        {
                            var reflectionResult = await ExecuteReflectionPhaseAsync(chatClient, context, actionResult, cancellationToken);
                            context.ExecutionHistory.Add(reflectionResult);

                            if (config.OnStepComplete != null)
                            {
                                await config.OnStepComplete(reflectionResult);
                            }

                            // 🆕 检查 Reflection 决策是否为完成
                            if (reflectionResult.Decision == ReActStepDecision.Complete)
                            {
                                context.ShouldTerminate = true;
                                result.FinalAnswer = actionResult.Content; // 使用工具执行结果作为最终答案
                                result.Success = true;
                                _logger.LogInformation("[ReAct] Reflection 决策: 任务完成");
                                break;
                            }
                            // 🆕 处理反射阶段的人工协助请求
                            else if (reflectionResult.Decision == ReActStepDecision.NeedHumanHelp)
                            {
                                context.ShouldTerminate = true;
                                
                                string question = reflectionResult.Content;
                                 try {
                                      var jsonContent = ExtractJsonFromText(reflectionResult.Content);
                                      if (!string.IsNullOrEmpty(jsonContent)) {
                                          using var doc = JsonDocument.Parse(jsonContent);
                                          if (doc.RootElement.TryGetProperty("question_to_user", out var qProp))
                                          {
                                              question = qProp.GetString() ?? question;
                                          }
                                      }
                                 } catch {}
                                
                                result.FinalAnswer = $"【自动确认】{question}";
                                result.Success = true;
                                result.ErrorMessage = null;
                                _logger.LogInformation("[ReAct] Reflection 决策: 需要人工协助，已自动确认并完成");
                                break;
                            }
                        }
                    }
                    else if (thoughtResult.Decision == ReActStepDecision.NeedMoreInfo)
                    {
                        // LLM 需要更多信息但没有合适的工具
                        _logger.LogWarning("[ReAct] LLM 需要更多信息，按自动确认策略直接结束");
                        result.FinalAnswer = $"【自动确认】{thoughtResult.Content}";
                        result.Success = true;
                        result.ErrorMessage = null;
                        break;
                    }
                    else if (thoughtResult.Decision == ReActStepDecision.NeedHumanHelp)
                    {
                        // 🆕 增强：处理人工协助
                         _logger.LogInformation("[ReAct] LLM 请求人工协助");
                         
                         // 尝试提取问题
                         string question = thoughtResult.Content;
                         try {
                               var jsonContent = ExtractJsonFromText(thoughtResult.Content);
                               if (!string.IsNullOrEmpty(jsonContent)) {
                                  using var doc = JsonDocument.Parse(jsonContent);
                                  if (doc.RootElement.TryGetProperty("question_to_user", out var qProp))
                                  {
                                      question = qProp.GetString() ?? question;
                                  }
                              }
                         } catch {}

                        result.FinalAnswer = $"【自动确认】{question}";
                        result.Success = true;
                        result.ErrorMessage = null;
                        break;
                    }

                    // 检查是否达到诊断限制
                    var sessionSummary = _trackerService.GetSession(config.SessionId)?.GetSummary();
                    if (sessionSummary != null && ShouldTerminateDueToDiagnostics(sessionSummary, context))
                    {
                        context.ShouldTerminate = true;
                        result.Success = false;
                        result.ErrorMessage = "工具调用达到诊断限制";
                        result.DiagnosticReport = BuildDiagnosticReport(context, sessionSummary);
                        _logger.LogWarning("[ReAct] 因诊断限制终止执行");
                        break;
                    }
                }

                // 如果达到最大迭代次数 → 做一次 fallback 总结调用
                if (context.CurrentIteration >= context.MaxIterations && !context.ShouldTerminate)
                {
                    _logger.LogWarning("[ReAct] 达到最大迭代次数，尝试 fallback 总结调用");
                    try
                    {
                        var fallbackAnswer = await ExecuteFallbackSummaryAsync(chatClient, context, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(fallbackAnswer))
                        {
                            result.FinalAnswer = fallbackAnswer;
                            result.Success = true;
                            result.ErrorMessage = $"达到最大迭代次数 ({context.MaxIterations})，已通过 fallback 总结输出结论";
                            _logger.LogInformation("[ReAct] Fallback 总结成功");
                        }
                        else
                        {
                            result.Success = false;
                            result.ErrorMessage = $"达到最大迭代次数 ({context.MaxIterations})，fallback 总结未产生有效结论";
                        }
                    }
                    catch (Exception fbEx)
                    {
                        _logger.LogError(fbEx, "[ReAct] Fallback 总结调用失败");
                        result.Success = false;
                        result.ErrorMessage = $"达到最大迭代次数 ({context.MaxIterations})";
                    }
                }

                result.EndTime = DateTime.UtcNow;
                result.TotalIterations = context.CurrentIteration;
                result.ExecutionHistory = context.ExecutionHistory;
            }
            catch (OperationCanceledException)
            {
                // 🆕 用户主动取消
                _logger.LogWarning("[ReAct] 任务被取消: SessionId={SessionId}", config.SessionId);
                result.Success = false;
                result.ErrorMessage = "CANCELLED";
                result.EndTime = DateTime.UtcNow;
                result.TotalIterations = context.CurrentIteration;
                result.ExecutionHistory = context.ExecutionHistory;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReAct] 执行异常");
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
            }
            finally
            {
                // 关闭追踪会话
                result.ToolCallSummary = _trackerService.CloseSession(config.SessionId);
                
                // 注意：不在此处保存助手消息，由调用方（WorkflowBackgroundJobService）统一处理
                // 避免重复保存（ReActEngine 保存一次 + 调用方又保存一次）
            }

            _logger.LogInformation("[ReAct] 执行结束: Success={Success}, Iterations={Iterations}, Duration={Duration}ms",
                result.Success, result.TotalIterations, (result.EndTime - result.StartTime).TotalMilliseconds);

            return result;
        }

        #region ReAct 阶段实现

        /// <summary>
        /// Thought 阶段：让 LLM 思考下一步
        /// </summary>
        private async Task<ReActStep> ExecuteThoughtPhaseAsync(
            IChatClient chatClient,
            ReActContext context,
            CancellationToken cancellationToken)
        {
            var step = new ReActStep
            {
                Phase = ReActPhase.Thought,
                Iteration = context.CurrentIteration,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // 构建消息列表（使用原生 ChatMessage 格式，而非文本拼接）
                var messages = BuildChatMessages(context);

                _logger.LogDebug("[ReAct:Thought] 消息数量: {Count}, 历史消息: {HistoryCount}", 
                    messages.Count, context.HistoryMessages?.Count ?? 0);

                // 如果有工具，让 LLM 可以选择调用
                ChatOptions? options = null;
                if (context.AvailableTools?.Count > 0)
                {
                    options = new ChatOptions
                    {
                        Tools = context.AvailableTools.Cast<AITool>().ToList(),
                        ToolMode = ChatToolMode.Auto,
                        AllowMultipleToolCalls =false, // 多次调用工具
                    };
                }

                var response = await chatClient.GetResponseAsync(messages, options, cancellationToken);

                // 解析 LLM 响应
                step.Content = response.Text ?? "";

                // 检查是否有工具调用 (增强日志用于调试)
                _logger.LogDebug("[ReAct:Thought] 响应消息数量: {Count}", response.Messages.Count);
                foreach (var msg in response.Messages)
                {
                    _logger.LogDebug("[ReAct:Thought] 消息角色={Role}, 内容类型数={ContentCount}, 内容类型={Types}",
                        msg.Role.Value,
                        msg.Contents.Count,
                        string.Join(",", msg.Contents.Select(c => c.GetType().Name)));
                }

                var toolCalls = response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionCallContent>()
                    .ToList();

                _logger.LogInformation("[ReAct:Thought] 检测到工具调用数量: {Count}", toolCalls.Count);

                if (toolCalls.Count > 0)
                {
                    var firstCall = toolCalls.First();
                    step.Decision = ReActStepDecision.CallTool;
                    step.ToolCall = new ReActToolCall
                    {
                        ToolName = firstCall.Name,
                        Arguments = firstCall.Arguments != null
                            ? JsonHelper.SerializeObject(firstCall.Arguments)
                            : null,
                        CallId = firstCall.CallId
                    };

                    // 🆕 如果 LLM 返回工具调用但没有文本思考，生成描述性内容
                    if (string.IsNullOrWhiteSpace(step.Content))
                    {
                        var argsPreview = firstCall.Arguments != null 
                            ? JsonHelper.SerializeObject(firstCall.Arguments) 
                            : "无参数";
                        if (argsPreview.Length > 100)
                            argsPreview = argsPreview.Substring(0, 100) + "...";
                        
                        step.Content = $"分析任务后决定调用工具 [{firstCall.Name}]\n参数: {argsPreview}";
                    }

                    _logger.LogInformation("[ReAct:Thought] 决定调用工具: {Tool}", firstCall.Name);
                }
                else
                {
                    // 解析 LLM 的决策
                    step.Decision = ParseDecisionFromContent(step.Content);
                    _logger.LogInformation("[ReAct:Thought] 决策: {Decision}", step.Decision);
                }

                step.Success = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReAct:Thought] 执行失败");
                step.Success = false;
                step.ErrorMessage = ex.Message;
                step.Decision = ReActStepDecision.Error;
            }

            return step;
        }

        /// <summary>
        /// Action 阶段：执行工具调用
        /// </summary>
        private async Task<ReActStep> ExecuteActionPhaseAsync(
            ReActContext context,
            ReActToolCall toolCall,
            CancellationToken cancellationToken)
        {
            var step = new ReActStep
            {
                Phase = ReActPhase.Action,
                Iteration = context.CurrentIteration,
                Timestamp = DateTime.UtcNow,
                ToolCall = toolCall
            };

            try
            {
                // 检查追踪器是否允许执行
                var checkResult = _trackerService.ShouldExecute(
                    context.SessionId, toolCall.ToolName, toolCall.Arguments);

                if (!checkResult.ShouldExecute)
                {
                    // 被拦截，返回拦截消息和历史摘要
                    step.Content = BuildInterceptMessage(checkResult, context);
                    step.Success = true; // 拦截不算失败
                    step.WasIntercepted = true;
                    step.InterceptReason = checkResult.Reason.ToString();

                    _logger.LogWarning("[ReAct:Action] 工具调用被拦截: {Tool}, 原因={Reason}",
                        toolCall.ToolName, checkResult.Reason);
                    return step;
                }

                // 查找并执行工具
                var tool = context.AvailableTools?.FirstOrDefault(t => t.Name == toolCall.ToolName);
                if (tool == null)
                {
                    step.Success = false;
                    step.ErrorMessage = $"工具不存在: {toolCall.ToolName}";
                    _logger.LogWarning("[ReAct:Action] 工具不存在: {Tool}", toolCall.ToolName);
                    return step;
                }

                // 解析参数为字典
                Dictionary<string, object?>? argsDict = null;
                if (!string.IsNullOrEmpty(toolCall.Arguments))
                {
                    try
                    {
                        var jsonDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(toolCall.Arguments);
                        if (jsonDict != null)
                        {
                            argsDict = new Dictionary<string, object?>();
                            foreach (var kvp in jsonDict)
                            {
                                argsDict[kvp.Key] = ConvertJsonElement(kvp.Value);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[ReAct:Action] 参数解析失败");
                    }
                }

                // 执行工具（AIFunction.InvokeAsync 接受 AIFunctionArguments）
                var startTime = DateTime.UtcNow;
                var aiArgs = argsDict != null ? new AIFunctionArguments(argsDict) : null;
                var result = await tool.InvokeAsync(aiArgs, cancellationToken);
                var duration = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

                step.Content = result?.ToString() ?? "[无返回值]";
                step.Success = true;
                step.DurationMs = duration;

                // 记录到追踪器
                _trackerService.RecordCall(
                    context.SessionId,
                    toolCall.ToolName,
                    toolCall.Arguments,
                    step.Content,
                    true,
                    duration);

                _logger.LogInformation("[ReAct:Action] 工具执行成功: {Tool}, 耗时={Duration}ms",
                    toolCall.ToolName, duration);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReAct:Action] 工具执行异常: {Tool}", toolCall.ToolName);
                step.Success = false;
                step.ErrorMessage = ex.Message;
                step.Content = $"工具执行失败: {ex.Message}";

                // 记录失败调用
                _trackerService.RecordCall(
                    context.SessionId,
                    toolCall.ToolName,
                    toolCall.Arguments,
                    ex.Message,
                    false,
                    0);
            }

            return step;
        }

        /// <summary>
        /// Reflection 阶段：让 LLM 反思执行结果
        /// </summary>
        private async Task<ReActStep> ExecuteReflectionPhaseAsync(
            IChatClient chatClient,
            ReActContext context,
            ReActStep actionResult,
            CancellationToken cancellationToken)
        {
            var step = new ReActStep
            {
                Phase = ReActPhase.Reflection,
                Iteration = context.CurrentIteration,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                var prompt = BuildReflectionPrompt(context, actionResult);
                
                _logger.LogDebug("[ReAct:Reflection] Prompt: {Prompt}", prompt.Substring(0, Math.Min(500, prompt.Length)));

                var messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, context.SystemPrompt)
                };
                
                // ✅ 优化：添加简化的历史对话上下文（最近5条），帮助 LLM 理解多轮对话背景
                if (context.HistoryMessages?.Count > 0)
                {
                    var recentHistory = context.HistoryMessages.TakeLast(5);
                    foreach (var historyMsg in recentHistory)
                    {
                        var content = historyMsg.Text ?? "";
                        messages.Add(new ChatMessage(historyMsg.Role, content));
                    }
                    _logger.LogDebug("[ReAct:Reflection] 添加 {Count} 条历史消息作为上下文", recentHistory.Count());
                }
                
                messages.Add(new ChatMessage(ChatRole.User, prompt));

                var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);

                step.Content = response.Text ?? "";
                step.Success = true;
                step.Decision = ParseDecisionFromContent(step.Content);

                // 🆕 增强日志：记录 LLM 原始响应和解析结果
                _logger.LogInformation("[ReAct:Reflection] LLM响应: {Response}", 
                    step.Content.Length > 200 ? step.Content.Substring(0, 200) + "..." : step.Content);
                _logger.LogInformation("[ReAct:Reflection] 解析决策: {Decision}, 工具执行状态: {ToolSuccess}", 
                    step.Decision, actionResult.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReAct:Reflection] 执行失败");
                step.Success = false;
                step.ErrorMessage = ex.Message;
            }

            return step;
        }

        #endregion

        #region Fallback 总结

        /// <summary>
        /// 迭代耗尽后的 fallback 总结调用（不带工具，强制输出结论）
        /// </summary>
        private async Task<string?> ExecuteFallbackSummaryAsync(
            IChatClient chatClient,
            ReActContext context,
            CancellationToken cancellationToken)
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, context.SystemPrompt)
            };

            // 拼接已收集的关键信息摘要
            var sb = new StringBuilder();
            sb.AppendLine("你已经用完了所有迭代次数。下面是你之前收集到的所有工具调用结果：");
            sb.AppendLine();

            foreach (var step in context.ExecutionHistory)
            {
                if (step.Phase == ReActPhase.Action && !string.IsNullOrWhiteSpace(step.Content))
                {
                    sb.AppendLine($"[工具结果] {step.ToolCall.ToolName}: {step.Content.Substring(0, Math.Min(step.Content.Length, 2000))}");
                    sb.AppendLine();
                }
                else if (step.Phase == ReActPhase.Thought && !string.IsNullOrWhiteSpace(step.Content))
                {
                    sb.AppendLine($"[思考] {step.Content.Substring(0, Math.Min(step.Content.Length, 500))}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---");
            sb.AppendLine("请根据以上已收集的信息，**立即输出最终诊断结论**。");
            sb.AppendLine("即使证据不够充分，也必须给出你的最佳判断，并标明置信度。");
            sb.AppendLine("严格按照要求的 JSON 格式输出。");

            messages.Add(new ChatMessage(ChatRole.User, sb.ToString()));

            // 不带 tools，强制文本输出
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            return response?.Text;
        }

        #endregion

        #region Prompt 构建

        /// <summary>
        /// 构建原生 ChatMessage 列表（推荐方式）
        /// 
        /// 优势：
        /// 1. 历史消息保持原生角色（User/Assistant），LLM 能正确理解对话上下文
        /// 2. 不会因为文本拼接导致角色混淆
        /// 3. 符合 Chat API 的标准用法
        /// 
        /// ✅ 优化：
        /// - 检测并跳过与 TaskDescription 重复的最后一条历史消息
        /// - 限制历史消息总数，避免上下文溢出
        /// </summary>
        private List<ChatMessage> BuildChatMessages(ReActContext context)
        {
            var messages = new List<ChatMessage>();

            // 1. System Prompt
            messages.Add(new ChatMessage(ChatRole.System, context.SystemPrompt));

            // 2. 历史对话消息（原生格式，保持角色区分）
            // ✅ 修复：检查最后一条是否与 TaskDescription 重复，避免发送重复用户输入
            if (context.HistoryMessages?.Count > 0)
            {
                var historyCount = context.HistoryMessages.Count;
                var lastHistoryMsg = context.HistoryMessages[historyCount - 1];
                
                // 检查最后一条是否与当前任务描述重复
                var isLastMsgDuplicate = lastHistoryMsg.Role == ChatRole.User 
                    && !string.IsNullOrEmpty(lastHistoryMsg.Text)
                    && !string.IsNullOrEmpty(context.TaskDescription)
                    && lastHistoryMsg.Text.Trim() == context.TaskDescription.Trim();
                
                // 如果重复，则排除最后一条
                var messagesToAdd = isLastMsgDuplicate 
                    ? context.HistoryMessages.Take(historyCount - 1) 
                    : context.HistoryMessages;
                
                // ✅ 优化：限制历史消息总数，避免上下文溢出（保留最近10条）
                var limitedHistory = messagesToAdd.TakeLast(10);
                
                _logger.LogDebug("[ReAct:BuildMessages] 历史消息: {Total}条, 跳过重复: {Skip}, 实际添加: {Added}条", 
                    historyCount, isLastMsgDuplicate, limitedHistory.Count());
                
                foreach (var historyMsg in limitedHistory)
                {
                    // 限制单条消息长度，避免上下文溢出
                    var content = historyMsg.Text ?? "";
                    if (content.Length > 1000)
                        content = content.Substring(0, 1000) + "...[截断]";
                    
                    messages.Add(new ChatMessage(historyMsg.Role, content));
                }
            }

            // 3. 当前任务提示（作为 User 消息）
            var taskPrompt = BuildTaskPrompt(context);
            messages.Add(new ChatMessage(ChatRole.User, taskPrompt));

            return messages;
        }

        /// <summary>
        /// 构建当前任务提示（不包含历史消息，历史消息单独作为 ChatMessage）
        /// </summary>
        private string BuildTaskPrompt(ReActContext context)
        {
            var sb = new StringBuilder();

            // 🆕 用户原始请求（如果存在且与TaskDescription不同）
            if (!string.IsNullOrEmpty(context.OriginalUserRequest) && 
                context.OriginalUserRequest != context.TaskDescription)
            {
                sb.AppendLine("【用户原始请求】");
                sb.AppendLine(context.OriginalUserRequest);
                sb.AppendLine();
                sb.AppendLine("⚠️ 请始终牢记用户的原始需求，确保最终回答能够完整解决用户的问题。");
                sb.AppendLine();
            }

            // 任务描述
            sb.AppendLine("【当前任务】");
            sb.AppendLine(context.TaskDescription);
            sb.AppendLine();

            // Skills 已通过 SkillToolAdapter 作为普通工具注册，无需注入 Prompt
            // （Skill-as-Tool 模式：技能作为工具调用，而非知识注入）

            // 执行历史（ReAct 循环内的历史，不是对话历史）
            if (context.ExecutionHistory.Count > 0)
            {
                sb.AppendLine("【执行历史】");
                sb.AppendLine("以下是你之前的思考和行动记录，请基于这些信息决定下一步：");
                sb.AppendLine();

                foreach (var step in context.ExecutionHistory)
                {
                    sb.AppendLine($"--- 第 {step.Iteration} 轮 [{step.Phase}] ---");

                    if (step.Phase == ReActPhase.Thought)
                    {
                        sb.AppendLine($"思考: {step.Content}");
                        if (step.ToolCall != null)
                        {
                            sb.AppendLine($"决定调用: {step.ToolCall.ToolName}({step.ToolCall.Arguments})");
                        }
                    }
                    else if (step.Phase == ReActPhase.Action)
                    {
                        sb.AppendLine($"工具: {step.ToolCall?.ToolName}");
                        if (step.WasIntercepted)
                        {
                            sb.AppendLine($"[被拦截] 原因: {step.InterceptReason}");
                        }
                        sb.AppendLine($"结果: {TruncateContent(step.Content, 500)}");
                        if (!step.Success)
                        {
                            sb.AppendLine($"错误: {step.ErrorMessage}");
                        }
                    }
                    else if (step.Phase == ReActPhase.Reflection)
                    {
                        sb.AppendLine($"反思: {step.Content}");
                    }

                    sb.AppendLine();
                }
            }

            // 可用工具
            //if (context.AvailableTools?.Count > 0)
            //{
            //    sb.AppendLine("【可用工具】");
            //    foreach (var tool in context.AvailableTools)
            //    {
            //        sb.AppendLine($"- {tool.Name}: {tool.Description}");
            //    }
            //    sb.AppendLine();
            //}

            // 迭代信息
            sb.AppendLine($"【当前状态】");
            sb.AppendLine($"- 当前轮次: {context.CurrentIteration}/{context.MaxIterations}");
            sb.AppendLine($"- 已执行步骤: {context.ExecutionHistory.Count}");

            // 🔴 轮次预算预警
            var remaining = context.MaxIterations - context.CurrentIteration;
            if (remaining <= 2)
            {
                sb.AppendLine();
                sb.AppendLine("🚨🚨🚨 **轮次预算即将耗尽！** 🚨🚨🚨");
                sb.AppendLine($"你只剩 {remaining} 轮迭代机会。不要再调用工具！");
                sb.AppendLine("请根据已收集的信息，**立即输出最终结论**。");
                sb.AppendLine("如果证据不够充分，就降低置信度，但必须给出结论。");
            }
            else if (remaining <= 4)
            {
                sb.AppendLine();
                sb.AppendLine($"⚠️ 注意：剩余 {remaining} 轮迭代，请聚焦关键证据，准备尽快输出结论。");
            }
            sb.AppendLine();

            // 决策指引
            sb.AppendLine("【请思考】");
            
            if (context.ExecutionHistory.Count == 0)
            {
                sb.AppendLine("请分析任务并决定下一步行动：");
                sb.AppendLine("- 如果任务简单（如问答、介绍），**直接给出简洁的答案**即可");
                sb.AppendLine("- 如果需要调用工具获取信息，先简要说明原因，再调用工具");
                sb.AppendLine("- 如果任务复杂，请先分析再决定");
            }
            else
            {
                sb.AppendLine("这是第 " + (context.CurrentIteration) + " 轮迭代，请决定下一步：");
                sb.AppendLine("- 分析当前工具执行结果，判断任务是否完成");
                sb.AppendLine("- 如果任务完成，直接给出简洁的最终答案");
                sb.AppendLine("- 如果需要继续，说明原因并执行下一步");
            }
            sb.AppendLine();
            sb.AppendLine("⚠️ 重要：给用户的最终答案必须**简洁直接**，不要输出分析过程或结构化标签！");
            sb.AppendLine("如果之前的工具调用被拦截或失败，请尝试其他方法或不同参数。");

            return sb.ToString();
        }

        /// <summary>
        /// 构建 Thought 阶段的 Prompt（包含完整上下文）
        /// 【已弃用】保留作为备用方案，推荐使用 BuildChatMessages
        /// </summary>
        private string BuildThoughtPrompt(ReActContext context)
        {
            var sb = new StringBuilder();

            // 🆕 历史对话上下文（多轮对话）
            if (context.HistoryMessages?.Count > 0)
            {
                sb.AppendLine("【历史对话】");
                sb.AppendLine("以下是之前的对话记录，请结合上下文理解当前任务：");
                sb.AppendLine();
                foreach (var msg in context.HistoryMessages)
                {
                    var role = msg.Role == ChatRole.User ? "用户" : 
                               msg.Role == ChatRole.Assistant ? "助手" : "系统";
                    var content = msg.Text ?? "";
                    if (content.Length > 200)
                        content = content.Substring(0, 200) + "...";
                    sb.AppendLine($"[{role}]: {content}");
                }
                sb.AppendLine();
            }

            // 任务描述
            sb.AppendLine("【当前任务】");
            sb.AppendLine(context.TaskDescription);
            sb.AppendLine();

            // 执行历史（如果有）
            if (context.ExecutionHistory.Count > 0)
            {
                sb.AppendLine("【执行历史】");
                sb.AppendLine("以下是你之前的思考和行动记录，请基于这些信息决定下一步：");
                sb.AppendLine();

                foreach (var step in context.ExecutionHistory)
                {
                    sb.AppendLine($"--- 第 {step.Iteration} 轮 [{step.Phase}] ---");

                    if (step.Phase == ReActPhase.Thought)
                    {
                        sb.AppendLine($"思考: {step.Content}");
                        if (step.ToolCall != null)
                        {
                            sb.AppendLine($"决定调用: {step.ToolCall.ToolName}({step.ToolCall.Arguments})");
                        }
                    }
                    else if (step.Phase == ReActPhase.Action)
                    {
                        sb.AppendLine($"工具: {step.ToolCall?.ToolName}");
                        if (step.WasIntercepted)
                        {
                            sb.AppendLine($"[被拦截] 原因: {step.InterceptReason}");
                        }
                        sb.AppendLine($"结果: {TruncateContent(step.Content, 500)}");
                        if (!step.Success)
                        {
                            sb.AppendLine($"错误: {step.ErrorMessage}");
                        }
                    }
                    else if (step.Phase == ReActPhase.Reflection)
                    {
                        sb.AppendLine($"反思: {step.Content}");
                    }

                    sb.AppendLine();
                }
            }

            // 可用工具
            //if (context.AvailableTools?.Count > 0)
            //{
            //    sb.AppendLine("【可用工具】");
            //    foreach (var tool in context.AvailableTools)
            //    {
            //        sb.AppendLine($"- {tool.Name}: {tool.Description}");
            //    }
            //    sb.AppendLine();
            //}

            // 迭代信息
            sb.AppendLine($"【当前状态】");
            sb.AppendLine($"- 当前轮次: {context.CurrentIteration}/{context.MaxIterations}");
            sb.AppendLine($"- 已执行步骤: {context.ExecutionHistory.Count}");

            // 🔴 轮次预算预警（备用版本）
            var remaining2 = context.MaxIterations - context.CurrentIteration;
            if (remaining2 <= 2)
            {
                sb.AppendLine();
                sb.AppendLine("🚨🚨🚨 **轮次预算即将耗尽！** 🚨🚨🚨");
                sb.AppendLine($"你只剩 {remaining2} 轮迭代机会。不要再调用工具！");
                sb.AppendLine("请根据已收集的信息，**立即输出最终结论**。");
                sb.AppendLine("如果证据不够充分，就降低置信度，但必须给出结论。");
            }
            else if (remaining2 <= 4)
            {
                sb.AppendLine();
                sb.AppendLine($"⚠️ 注意：剩余 {remaining2} 轮迭代，请聚焦关键证据，准备尽快输出结论。");
            }
            sb.AppendLine();

            // 决策指引
            sb.AppendLine("【请思考】");
            
            // 🆕 区分首轮简单任务和多轮复杂任务
            if (context.ExecutionHistory.Count == 0)
            {
                // 首轮：可能是简单问题，不强制结构化
                sb.AppendLine("请分析任务并决定下一步行动：");
                sb.AppendLine("- 如果任务简单（如问答、介绍），**直接给出简洁的答案**即可");
                sb.AppendLine("- 如果需要调用工具获取信息，先简要说明原因，再调用工具");
                sb.AppendLine("- 如果任务复杂，请先分析再决定");
            }
            else
            {
                // 多轮：继续执行，但最终输出要简洁
                sb.AppendLine("这是第 " + (context.CurrentIteration) + " 轮迭代，请决定下一步：");
                sb.AppendLine("- 分析当前工具执行结果，判断任务是否完成");
                sb.AppendLine("- 如果任务完成，直接给出简洁的最终答案");
                sb.AppendLine("- 如果需要继续，说明原因并执行下一步");
            }
            sb.AppendLine();
            sb.AppendLine("⚠️ 重要：给用户的最终答案必须**简洁直接**，不要输出分析过程或结构化标签！");
            sb.AppendLine("如果之前的工具调用被拦截或失败，请尝试其他方法或不同参数。");

            return sb.ToString();
        }

        /// <summary>
        /// 构建 Reflection 阶段的 Prompt
        /// </summary>
        private string BuildReflectionPrompt(ReActContext context, ReActStep actionResult)
        {
            var sb = new StringBuilder();

            sb.AppendLine("【任务】");
            sb.AppendLine(context.TaskDescription);
            sb.AppendLine();

            // 🆕 注入技能上下文，帮助 LLM 在反思时结合技能知识判断
            if (context.Skills?.Count > 0)
            {
                sb.AppendLine("【相关技能参考】");
                foreach (var skill in context.Skills)
                {
                    sb.AppendLine($"- {skill.DisplayName ?? skill.Name}: {skill.Description}");
                    if (skill.AllowedTools?.Count > 0)
                    {
                        sb.AppendLine($"  推荐工具: {string.Join(", ", skill.AllowedTools)}");
                    }
                }
                sb.AppendLine("提示: 反思时请结合技能说明中的工作流程判断当前进度和下一步行动");
                sb.AppendLine();
            }

            sb.AppendLine("【刚才的行动】");
            sb.AppendLine($"工具: {actionResult.ToolCall?.ToolName}");
            sb.AppendLine($"参数: {actionResult.ToolCall?.Arguments}");
            sb.AppendLine($"结果: {TruncateContent(actionResult.Content, 1000)}");
            sb.AppendLine($"执行状态: {(actionResult.Success ? "成功" : "失败")}");
            if (actionResult.WasIntercepted)
            {
                sb.AppendLine($"[注意] 此调用被拦截，原因: {actionResult.InterceptReason}");
            }
            
            // 🆕 如果工具执行失败，明确提示 LLM 需要重试
            if (!actionResult.Success)
            {
                sb.AppendLine();
                sb.AppendLine("⚠️ 【重要提示】工具执行失败！你应该：");
                sb.AppendLine("1. 分析失败原因");
                sb.AppendLine("2. 决定是否需要重试（decision=continue）");
                sb.AppendLine("3. 考虑是否需要调整参数或换一个工具");
            }
            sb.AppendLine();

            sb.AppendLine("【请反思并决策】");
            sb.AppendLine("分析工具执行结果，判断任务是否完成，然后输出 JSON 格式的决策：");
            sb.AppendLine("⚠️ 注意：如果不确定或需要用户确认，请使用 need_human_help");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine("{");
            sb.AppendLine("  \"decision\": \"task_completed 或 continue 或 need_human_help\",");
            sb.AppendLine("  \"reason\": \"决策依据\",");
            sb.AppendLine("  \"final_result\": \"最终答案（仅 task_completed 时需要）\",");
            sb.AppendLine("  \"question_to_user\": \"如果需要人工帮助，请在此处以Markdown格式清晰地提出问题，列出选项或需要确认的内容\" ");
            sb.AppendLine("}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("决策说明：");
            sb.AppendLine("- task_completed: 任务已完成，工具返回了预期结果");
            sb.AppendLine("- continue: 需要继续执行，调用其他工具或重试");
            sb.AppendLine("- need_human_help: 遇到无法解决的问题、或必须要用户确认关键步骤、或缺少关键信息，需要人工介入");
            
            return sb.ToString();
        }

        /// <summary>
        /// 构建拦截消息（包含完整上下文）
        /// </summary>
        private string BuildInterceptMessage(ToolCallCheckResult checkResult, ReActContext context)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"[工具调用被拦截]");
            sb.AppendLine($"原因: {checkResult.Message}");
            sb.AppendLine();

            // 提供缓存结果（如果有）
            if (!string.IsNullOrEmpty(checkResult.CachedResult))
            {
                sb.AppendLine("【之前相同调用的结果】");
                sb.AppendLine(TruncateContent(checkResult.CachedResult, 500));
                sb.AppendLine();
            }

            // 提供历史摘要
            var toolHistory = context.ExecutionHistory
                .Where(s => s.Phase == ReActPhase.Action && s.ToolCall?.ToolName == checkResult.ToolName)
                .ToList();

            if (toolHistory.Count > 0)
            {
                sb.AppendLine($"【该工具历史调用记录（共 {toolHistory.Count} 次）】");
                foreach (var call in toolHistory.TakeLast(3))
                {
                    sb.AppendLine($"- 参数: {call.ToolCall?.Arguments}");
                    sb.AppendLine($"  结果: {TruncateContent(call.Content, 100)}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("【建议】");
            sb.AppendLine("- 尝试使用不同的参数");
            sb.AppendLine("- 尝试其他工具");
            sb.AppendLine("- 基于已有信息给出结论");

            return sb.ToString();
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 从文本中提取 JSON 内容
        /// </summary>
        private string? ExtractJsonFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            // 1. 尝试提取 markdown 代码块中的 JSON
            var json = ExtractContentBlock(text, "```json", "```");
            if (!string.IsNullOrEmpty(json)) return json;

            // 2. 尝试提取普通代码块
            json = ExtractContentBlock(text, "```", "```");
            if (!string.IsNullOrEmpty(json) && (json.TrimStart().StartsWith("{") || json.TrimStart().StartsWith("["))) return json;

            // 3. 尝试直接找 JSON 对象（最外层的 {}）
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                return text.Substring(start, end - start + 1);
            }

            return null;
        }

        private string ExtractContentBlock(string text, string startTag, string endTag)
        {
            var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                start += startTag.Length;
                var end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
                if (end > start)
                {
                    return text.Substring(start, end - start).Trim();
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// 从 LLM 响应内容解析决策
        /// </summary>
        private ReActStepDecision ParseDecisionFromContent(string content)
        {
            if (string.IsNullOrEmpty(content))
                return ReActStepDecision.NeedMoreInfo;

            // 优先尝试解析 JSON 格式的决策
            try
            {
                var jsonContent = ExtractJsonFromText(content);
                if (!string.IsNullOrEmpty(jsonContent))
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };

                    using var doc = JsonDocument.Parse(jsonContent, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("decision", out var decisionProp))
                    {
                        var decisionStr = decisionProp.GetString()?.ToLowerInvariant() ?? "";
                        
                        if (decisionStr.Contains("task_completed") || decisionStr.Contains("completed") || decisionStr.Contains("complete"))
                        {
                            _logger.LogInformation("[ReAct] JSON 决策解析: TaskCompleted");
                            return ReActStepDecision.Complete;
                        }
                        if (decisionStr.Contains("need_human") || decisionStr.Contains("human_help"))
                        {
                            _logger.LogInformation("[ReAct] JSON 决策解析: NeedMoreInfo (需要人工)，自动映射为完成");
                            return ReActStepDecision.Complete;
                        }
                         if (decisionStr.Contains("wait_for_user") || decisionStr.Contains("user_confirm"))
                        {
                            _logger.LogInformation("[ReAct] JSON 决策解析: NeedHumanHelp (用户确认)，自动映射为完成");
                            return ReActStepDecision.Complete;
                        }
                        if (decisionStr.Contains("continue"))
                        {
                            _logger.LogInformation("[ReAct] JSON 决策解析: Continue");
                            return ReActStepDecision.Continue;
                        }
                    }
                    
                    // 如果 JSON 中有 final_result 字段，通常意味着任务完成
                    if (root.TryGetProperty("final_result", out var finalResult) && 
                        !string.IsNullOrEmpty(finalResult.GetString()))
                    {
                        _logger.LogInformation("[ReAct] JSON 包含 final_result，判定为 Complete");
                        return ReActStepDecision.Complete;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[ReAct] JSON 解析失败，回退到关键词匹配: {Error}", ex.Message);
            }

            // 回退到关键词匹配
            var lowerContent = content.ToLowerInvariant();

            // 需要人工 / 用户确认
            string[] humanKeywords = {
                "需要人工", "人工介入", "need human", "user confirm", "用户确认", "ask user", "询问用户"
            };
            foreach (var keyword in humanKeywords)
            {
                if (lowerContent.Contains(keyword))
                {
                    _logger.LogInformation("[ReAct] 关键词匹配决策: NeedHumanHelp (匹配: {Keyword})，自动映射为完成", keyword);
                    return ReActStepDecision.Complete;
                }
            }

            // 完成标识（扩展关键词列表）
            string[] completeKeywords = {
                "最终答案", "final answer", "任务完成", "task_completed", "task completed",
                "综上所述", "总结", "已完成", "执行成功", "解析完成", "处理完成",
                "成功完成", "完成任务", "任务已完成", "successfully completed"
            };
            
            foreach (var keyword in completeKeywords)
            {
                if (lowerContent.Contains(keyword))
                {
                    _logger.LogInformation("[ReAct] 关键词匹配决策: Complete (匹配: {Keyword})", keyword);
                    return ReActStepDecision.Complete;
                }
            }

            // 继续执行标识
            string[] continueKeywords = {
                "继续执行", "需要继续", "还需要", "接下来", "下一步",
                "continue", "next step", "need to"
            };
            
            foreach (var keyword in continueKeywords)
            {
                if (lowerContent.Contains(keyword))
                {
                    _logger.LogInformation("[ReAct] 关键词匹配决策: Continue (匹配: {Keyword})", keyword);
                    return ReActStepDecision.Continue;
                }
            }

            // 🆕 优化：检查是否是直接对话回复（没有工具调用需求的场景）
            // 如果内容看起来像是一个完整的对话回复（问候、回答等），判断为Complete
            string[] dialoguePatterns = {
                "你好", "您好", "很高兴", "我是", "我可以帮", "有什么可以帮",
                "hello", "hi", "how can i help", "glad to", "nice to meet",
                "没问题", "好的", "可以", "当然", "请问", "请告诉",
                "抱歉", "对不起", "不好意思", "sorry", 
                "？", "吗？", "呢？", "什么", "怎么", "如何", "为什么"
            };
            
            foreach (var pattern in dialoguePatterns)
            {
                if (lowerContent.Contains(pattern))
                {
                    _logger.LogInformation("[ReAct] 检测到对话回复模式，判定为 Complete (匹配: {Pattern})", pattern);
                    return ReActStepDecision.Complete;
                }
            }

            // 🆕 如果内容长度合理且没有明确的继续/工具调用意图，判断为Complete
            // 避免简单对话进入无限循环
            if (content.Length >= 5 && content.Length <= 500 && 
                !content.Contains("tool") && !content.Contains("工具") &&
                !content.Contains("调用") && !content.Contains("call"))
            {
                _logger.LogInformation("[ReAct] 内容为简短回复且无工具调用意图，判定为 Complete");
                return ReActStepDecision.Complete;
            }

            // 默认继续（但记录警告）
            _logger.LogWarning("[ReAct] 无法明确解析决策，默认 Continue: {Content}", 
                content.Length > 100 ? content.Substring(0, 100) + "..." : content);
            return ReActStepDecision.Continue;
        }

        /// <summary>
        /// 检查是否因诊断限制需要终止
        /// </summary>
        private bool ShouldTerminateDueToDiagnostics(ToolCallTrackerSummary summary, ReActContext context)
        {
            // 检查重复调用次数
            var highDuplicates = summary.DuplicateCallCounts.Values.Any(v => v >= 3);
            if (highDuplicates)
            {
                _logger.LogWarning("[ReAct] 检测到高重复调用");
                return true;
            }

            // 检查连续失败
            var recentActions = context.ExecutionHistory
                .Where(s => s.Phase == ReActPhase.Action)
                .TakeLast(3)
                .ToList();

            if (recentActions.Count >= 3 && recentActions.All(a => !a.Success || a.WasIntercepted))
            {
                _logger.LogWarning("[ReAct] 连续 3 次工具调用失败或被拦截");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 构建诊断报告
        /// </summary>
        private string BuildDiagnosticReport(ReActContext context, ToolCallTrackerSummary summary)
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== 执行诊断报告 ===");
            sb.AppendLine();
            sb.AppendLine($"会话ID: {context.SessionId}");
            sb.AppendLine($"总迭代次数: {context.CurrentIteration}");
            sb.AppendLine($"总工具调用: {summary.TotalCalls}");
            sb.AppendLine();

            sb.AppendLine("【工具调用统计】");
            foreach (var kvp in summary.ToolCallCounts)
            {
                sb.AppendLine($"- {kvp.Key}: {kvp.Value} 次");
            }
            sb.AppendLine();

            if (summary.DuplicateCallCounts.Count > 0)
            {
                sb.AppendLine("【重复调用检测】");
                foreach (var kvp in summary.DuplicateCallCounts)
                {
                    sb.AppendLine($"- {kvp.Key}: 重复 {kvp.Value} 次");
                }
                sb.AppendLine();
            }

            sb.AppendLine("【执行轨迹】");
            foreach (var step in context.ExecutionHistory)
            {
                sb.AppendLine($"[{step.Iteration}] {step.Phase}: {TruncateContent(step.Content, 100)}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 截断内容
        /// </summary>
        private string TruncateContent(string? content, int maxLength)
        {
            if (string.IsNullOrEmpty(content))
                return "[空]";

            if (content.Length <= maxLength)
                return content;

            return content.Substring(0, maxLength) + "...[截断]";
        }

        /// <summary>
        /// 转换 JsonElement 到 object
        /// </summary>
        private object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
                JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
                _ => element.ToString()
            };
        }

        /// <summary>
        /// 加载聊天历史消息（从ChatSession转换为Microsoft.Extensions.AI.ChatMessage）
        /// </summary>
        private async Task<List<ChatMessage>> LoadChatHistoryAsync(long chatSessionId)
        {
            try
            {
                var messages = await _chatSessionService.GetSessionMessagesAsync(chatSessionId, 20);
                var chatMessages = new List<ChatMessage>();
                
                foreach (var msg in messages)
                {
                    ChatMessage chatMessage = msg.Role switch
                    {
                        "user" => new ChatMessage(ChatRole.User, msg.Content),
                        "assistant" => new ChatMessage(ChatRole.Assistant, msg.Content),
                        "system" => new ChatMessage(ChatRole.System, msg.Content),
                        _ => new ChatMessage(ChatRole.User, msg.Content)
                    };
                    chatMessages.Add(chatMessage);
                }
                
                return chatMessages;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ReAct] 加载历史消息失败: ChatSessionId={ChatSessionId}", chatSessionId);
                return new List<ChatMessage>();
            }
        }

        /// <summary>
        /// 保存助手回复到会话
        /// </summary>
        private async Task SaveAssistantMessageAsync(long chatSessionId, string content, long? workflowId = null)
        {
            try
            {
                await _chatSessionService.AddMessageAsync(new AddMessageRequest
                {
                    SessionId = chatSessionId,
                    Role = "assistant",
                    Content = content,
                    WorkflowId = workflowId
                });
                _logger.LogInformation("[ReAct] 已保存助手回复到会话: ChatSessionId={ChatSessionId}", chatSessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ReAct] 保存助手消息失败: ChatSessionId={ChatSessionId}", chatSessionId);
            }
        }

        #endregion
    }

    #region 数据模型

    /// <summary>
    /// ReAct 执行配置
    /// </summary>
    public class ReActExecutionConfig
    {
        /// <summary>
        /// 会话ID（用于追踪）
        /// </summary>
        public string SessionId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 聊天会话ID（用于多轮对话历史）
        /// </summary>
        public long? ChatSessionId { get; set; }

        /// <summary>
        /// 角色ID
        /// </summary>
        public long RoleId { get; set; }

        /// <summary>
        /// 任务描述
        /// </summary>
        public string TaskDescription { get; set; } = "";

        /// <summary>
        /// System Prompt
        /// </summary>
        public string SystemPrompt { get; set; } = "你是一个智能助手。";

        /// <summary>
        /// 可用工具
        /// </summary>
        public List<AIFunction>? Tools { get; set; }

        /// <summary>
        /// 最大迭代次数
        /// </summary>
        public int MaxIterations { get; set; } = 10;

        /// <summary>
        /// 工具调用限制
        /// </summary>
        public ToolCallLimits? CallLimits { get; set; }

        /// <summary>
        /// 是否启用反思阶段
        /// </summary>
        public bool EnableReflection { get; set; } = true;

        /// <summary>
        /// 步骤完成回调（用于流式输出）
        /// </summary>
        public Func<ReActStep, Task>? OnStepComplete { get; set; }

        /// <summary>
        /// 🆕 角色配置的 Skills（知识增强）
        /// </summary>
        public List<SkillContent>? Skills { get; set; }

        /// <summary>
        /// 🆕 用户原始请求（未经处理的原始输入）
        /// 用于在整个 ReAct 循环中保持用户意图的清晰可见
        /// </summary>
        public string? OriginalUserRequest { get; set; }
    }

    /// <summary>
    /// ReAct 执行上下文（内部使用）
    /// </summary>
    public class ReActContext
    {
        public string SessionId { get; set; } = "";
        public long? ChatSessionId { get; set; }
        public long RoleId { get; set; }
        public string TaskDescription { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        public List<AIFunction>? AvailableTools { get; set; }
        /// <summary>
        /// 🆕 可用技能列表
        /// </summary>
        public List<SkillContent>? Skills { get; set; }
        public DateTime StartTime { get; set; }
        public int MaxIterations { get; set; }
        public int CurrentIteration { get; set; }
        public bool ShouldTerminate { get; set; }
        public List<ReActStep> ExecutionHistory { get; set; } = new();
        public List<ChatMessage>? HistoryMessages { get; set; }
        /// <summary>
        /// 🆕 用户原始请求
        /// </summary>
        public string? OriginalUserRequest { get; set; }
    }

    /// <summary>
    /// ReAct 执行步骤
    /// </summary>
    public class ReActStep
    {
        public ReActPhase Phase { get; set; }
        public int Iteration { get; set; }
        public DateTime Timestamp { get; set; }
        public string Content { get; set; } = "";
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public ReActStepDecision Decision { get; set; }
        public ReActToolCall? ToolCall { get; set; }
        public bool WasIntercepted { get; set; }
        public string? InterceptReason { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>
    /// ReAct 工具调用
    /// </summary>
    public class ReActToolCall
    {
        public string ToolName { get; set; } = "";
        public string? Arguments { get; set; }
        public string? CallId { get; set; }
    }

    /// <summary>
    /// ReAct 阶段
    /// </summary>
    public enum ReActPhase
    {
        Thought,     // 思考
        Action,      // 行动
        Observation, // 观察（已融入 Action 结果）
        Reflection   // 反思
    }

    /// <summary>
    /// ReAct 步骤决策（用于 ReActExecutionEngine）
    /// 注意：与 WorkflowBackgroundJobService 中的 ReActDecision 不同
    /// </summary>
    public enum ReActStepDecision
    {
        Continue,     // 继续执行
        CallTool,     // 调用工具
        Complete,     // 任务完成
        NeedMoreInfo, // 需要更多信息
        NeedHumanHelp,// 需要人工帮助 (Status 6)
        Error         // 错误
    }

    /// <summary>
    /// ReAct 执行结果
    /// </summary>
    public class ReActExecutionResult
    {
        public string SessionId { get; set; } = "";
        public bool Success { get; set; }
        public string? FinalAnswer { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public int TotalIterations { get; set; }
        public List<ReActStep> ExecutionHistory { get; set; } = new();
        public ToolCallTrackerSummary? ToolCallSummary { get; set; }
        public string? DiagnosticReport { get; set; }
    }

    #endregion
}
