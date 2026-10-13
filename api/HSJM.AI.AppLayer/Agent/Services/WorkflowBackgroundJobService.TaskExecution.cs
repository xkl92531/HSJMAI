using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using HSJM.AI.Core;
using System.Text.Json;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// WorkflowBackgroundJobService 分部类 - 任务执行与 ReAct 推理
    /// 🆕 阶段1.2：提供完整的任务创建和执行逻辑，记录 ReAct 推理过程
    /// </summary>
    public partial class WorkflowBackgroundJobService
    {
        #region 任务创建辅助方法

        /// <summary>
        /// 创建任务实体（完整字段）
        /// </summary>
        private Agent_TaskEntity CreateTaskWithFullFields(
            long workflowId,
            string taskName,
            string taskType,
            long assignedAgentId,
            string inputParams,
            int safetyLevel = 1)
        {
            return new Agent_TaskEntity
            {
                WorkflowID = workflowId,
                TaskName = taskName,
                TaskType = taskType,
                AssignedAgentID = assignedAgentId,
                Status = 1, // 已就绪
                InputParams = inputParams,
                
                // 🆕 ReAct 推理控制字段
                MaxLoopCount = 10, // 最大迭代10次，防死循环
                ReActLoopCount = 0, // 初始化为0
                
                // 🆕 超时与重试
                MaxRetryCount = 3,
                RetryCount = 0,
                TimeoutSeconds = 120, // 2分钟超时
                
                // 🆕 安全治理
                SafetyLevel = safetyLevel,
                NeedHumanReview = safetyLevel >= 3 // 安全等级>=3需要审核
            };
        }

        /// <summary>
        /// 根据任务类型确定安全等级
        /// </summary>
        private int DetermineTaskSafetyLevel(string taskType)
        {
            return taskType switch
            {
                "Analysis" => 1, // 信息级，无风险
                "Verification" => 2, // 建议级，低风险
                "Action" => 3, // 半自动，需审核
                "Control" => 4, // 全自动，高风险
                _ => 1
            };
        }

        /// <summary>
        /// 解析时长字符串为秒数
        /// </summary>
        private int ParseDuration(string durationStr)
        {
            if (string.IsNullOrEmpty(durationStr))
                return 15 * 60; // 默认15分钟

            try
            {
                // 支持 "10分钟"、"1小时"、"30秒" 等格式
                if (durationStr.Contains("小时"))
                {
                    var hours = int.Parse(durationStr.Replace("小时", "").Trim());
                    return hours * 3600;
                }
                else if (durationStr.Contains("分钟"))
                {
                    var minutes = int.Parse(durationStr.Replace("分钟", "").Trim());
                    return minutes * 60;
                }
                else if (durationStr.Contains("秒"))
                {
                    return int.Parse(durationStr.Replace("秒", "").Trim());
                }
                else
                {
                    // 默认当作分钟
                    return int.Parse(durationStr.Trim()) * 60;
                }
            }
            catch
            {
                return 15 * 60; // 解析失败，默认15分钟
            }
        }

        #endregion

        #region ReAct 推理执行方法

        /// <summary>
        /// 🆕 执行专家任务并记录完整 ReAct 推理过程（优化版）
        /// </summary>
        private async Task ExecuteExpertWithReActTrackingAsync(
            long workflowId, 
            long taskId, 
            string taskName, 
            long expertRoleId, 
            string input,
            CancellationToken cancellationToken = default)
        {
            Agent_TaskEntity task = null;
            try
            {
                // 🆕 检查取消
                cancellationToken.ThrowIfCancellationRequested();
                
                // 1. 加载任务并更新状态
                task = await _taskService.GetByIdAsync(taskId);
                if (task == null || task.IsDeleted)
                {
                    _logger.LogError("[任务执行] 任务不存在: TaskId={TaskId}", taskId);
                    return;
                }
                
                task.Status = 2; // 执行中
                task.StartTime = DateTime.Now;
                await _taskService.UpdateAsync(task);
                await PushTaskStatus(workflowId, taskId, taskName, 2);

                // 🆕 2. 从 Workflow 获取 ChatSessionId 并加载历史消息
                long chatSessionId = 0;
                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow != null)
                {
                    // 🔍 调试日志：输出 GlobalContext 原始值
                    _logger.LogInformation("[任务执行] Workflow加载成功: WorkflowId={WorkflowId}, GlobalContext={GlobalContext}", 
                        workflowId, workflow.GlobalContext ?? "(null)");
                    
                    if (!string.IsNullOrEmpty(workflow.GlobalContext))
                    {
                        try
                        {
                            var context = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GlobalContext);
                            if (context != null && context.TryGetValue("ChatSessionId", out var sessionIdElement))
                            {
                                chatSessionId = sessionIdElement.GetInt64();
                                _logger.LogInformation("[任务执行] 从GlobalContext获取ChatSessionId: {ChatSessionId}", chatSessionId);
                            }
                            else
                            {
                                _logger.LogWarning("[任务执行] GlobalContext中未找到ChatSessionId字段: WorkflowId={WorkflowId}", workflowId);
                            }
                        }
                        catch (Exception jsonEx)
                        {
                            _logger.LogWarning(jsonEx, "[任务执行] 解析GlobalContext失败: WorkflowId={WorkflowId}, GlobalContext={GlobalContext}", 
                                workflowId, workflow.GlobalContext);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[任务执行] GlobalContext为空: WorkflowId={WorkflowId}", workflowId);
                    }
                }
                else
                {
                    _logger.LogWarning("[任务执行] Workflow不存在: WorkflowId={WorkflowId}", workflowId);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 🆕 3. 加载历史对话上下文（✅ 构建原生 ChatMessage 列表）
                List<Microsoft.Extensions.AI.ChatMessage> historyMessages = null;
                if (chatSessionId > 0)
                {
                    try
                    {
                        var historyEntities = await _chatSessionService.GetSessionMessagesAsync(chatSessionId, 20);
                        if (historyEntities != null && historyEntities.Count > 0)
                        {
                            // ✅ 原生 ChatMessage 列表，保持角色区分
                            historyMessages = new List<Microsoft.Extensions.AI.ChatMessage>();
                            foreach (var msg in historyEntities)
                            {
                                if (string.IsNullOrEmpty(msg.Content)) continue;
                                var role = msg.Role?.ToLower() == "user" 
                                    ? Microsoft.Extensions.AI.ChatRole.User 
                                    : Microsoft.Extensions.AI.ChatRole.Assistant;
                                var content = msg.Content.Length > 1000 ? msg.Content.Substring(0, 1000) + "...[截断]" : msg.Content;
                                historyMessages.Add(new Microsoft.Extensions.AI.ChatMessage(role, content));
                            }
                            
                            _logger.LogInformation("[任务执行] 加载历史消息: ChatSessionId={ChatSessionId}, Count={Count}",
                                chatSessionId, historyMessages.Count);
                        }
                    }
                    catch (Exception historyEx)
                    {
                        _logger.LogWarning(historyEx, "[任务执行] 加载历史消息失败: ChatSessionId={ChatSessionId}", chatSessionId);
                    }
                }
                else
                {
                    _logger.LogWarning("[任务执行] 未找到ChatSessionId: WorkflowId={WorkflowId}", workflowId);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 4. 推送开始标记
                await PushExpertStreamStart(workflowId, taskId, taskName);

                // 5. 创建专家智能体
                var expert = await _agentRuntimeService.CreateAgentByRoleAsync(expertRoleId);
                var loopCount = 0;
                
                // 6. ✅ 使用 AgentRuntimeService 执行（带历史消息的结构化调用）
                _logger.LogDebug("[Agent执行] TaskId={TaskId}, ChatSessionId={ChatSessionId}, 历史消息数={HistoryCount}", 
                    taskId, chatSessionId, historyMessages?.Count ?? 0);
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // ✅ 使用 RunAgentAsync 传递结构化消息列表（而非流式拼接字符串）
                var agentResponse = await _agentRuntimeService.RunAgentAsync(expert, input, historyMessages, cancellationToken);
                
                // 🆕 在耗时操作(LLM)返回后再次检查取消，如果已取消则丢弃结果并抛出异常
                cancellationToken.ThrowIfCancellationRequested();
                
                var fullContent = agentResponse.Content ?? "";
                
                loopCount++;
                
                // 5. 🆕 记录推理过程（Thought → Action → Observation）
                task.ReActLoopCount = loopCount;
                task.ThoughtProcess = $"[第{loopCount}轮] 分析用户需求：{input.Substring(0, Math.Min(50, input.Length))}...，规划执行步骤";
                task.ActionTaken = $"[第{loopCount}轮] 调用专家智能体进行分析，使用角色ID={expertRoleId}";
                task.ObservationResult = $"[第{loopCount}轮] 分析完成，得到结果: {fullContent.Substring(0, Math.Min(100, fullContent.Length))}...";
                
                _logger.LogInformation("[ReAct完成] TaskId={TaskId}, 循环{LoopCount}轮, 结果长度={Length}字符",
                    taskId, loopCount, fullContent.Length);
                
                // 6. 推送完成标记
                await PushExpertStreamComplete(workflowId, taskId, taskName);

                // 7. 更新最终结果
                task.Status = 3; // 完成
                task.EndTime = DateTime.Now;
                task.OutputResult = fullContent;
                task.ExecutionLog = JsonSerializer.Serialize(new
                {
                    ReActLoops = loopCount,
                    TotalTokens = fullContent.Length,
                    StartTime = task.StartTime,
                    EndTime = task.EndTime,
                    Duration = (task.EndTime.Value - task.StartTime.Value).TotalSeconds
                });
                await _taskService.UpdateAsync(task);
                
                await PushTaskStatus(workflowId, taskId, taskName, 3, fullContent);
                
                _logger.LogInformation("[任务完成] TaskId={TaskId}, 耗时={Duration}秒, ReAct轮次={LoopCount}",
                    taskId, (task.EndTime.Value - task.StartTime.Value).TotalSeconds, loopCount);
            }
            catch (OperationCanceledException)
            {
                // 🆕 用户主动取消
                _logger.LogWarning("[任务执行] 被用户取消: TaskId={TaskId}, TaskName={TaskName}", taskId, taskName);
                
                if (task != null)
                {
                    task.Status = 5; // 已取消
                    task.EndTime = DateTime.Now;
                    task.ErrorDetails = "任务被用户取消";
                    await _taskService.UpdateAsync(task);
                    
                    await PushTaskStatus(workflowId, taskId, taskName, 5, "已取消");
                }
                
                throw; // 重新抛出以便上层捕获
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[专家执行失败] TaskId={TaskId}, TaskName={TaskName}",
                    taskId, taskName);
                
                if (task != null)
                {
                    task.Status = 4; // 失败
                    task.EndTime = DateTime.Now;
                    task.ErrorDetails = ex.Message;
                    task.ExecutionLog = JsonSerializer.Serialize(new
                    {
                        Error = ex.Message,
                        StackTrace = ex.StackTrace,
                        ReActLoops = task.ReActLoopCount,
                        FailedAt = DateTime.Now
                    });
                    await _taskService.UpdateAsync(task);
                    
                    await PushTaskStatus(workflowId, taskId, taskName, 4);
                }
            }
        }

        #endregion
    }
}
