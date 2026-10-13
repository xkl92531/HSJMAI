using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Logging;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.SignalR;
using System.Threading;
using Npgsql.TypeHandlers;
using HSJM.AI.Utility;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// WorkflowBackgroundJobService 分部类 - 动态顺序链执行引擎
    /// 🚀 核心功能：支持动态数量的智能体按顺序执行，支持条件分支
    /// 适用场景：PLC 解析工厂两层架构
    /// </summary>
    public partial class WorkflowBackgroundJobService
    {
        #region 动态顺序链执行

        /// <summary>
        /// 执行动态顺序链工作流
        /// 支持：
        /// 1. 任意数量的智能体角色
        /// 2. 条件分支（某个步骤的结果决定后续路径）
        /// 3. 每个智能体可选择使用 ReAct 循环或直接执行
        /// </summary>
        /// <param name="workflowId">工作流ID</param>
        /// <param name="chainConfig">链配置（JSON）</param>
        [AutomaticRetry(Attempts = 0)]
        //public async Task ExecuteDynamicSequentialChainAsync(long workflowId, string chainConfig)
        //{
        //    _logger.LogInformation("[动态链] 开始执行: WorkflowId={WorkflowId}", workflowId);
        //    var startTime = DateTime.Now;

        //    try
        //    {
        //        // 🆕 注册取消令牌
        //        var cancellationToken = _cancellationService.RegisterWorkflow(workflowId);

        //        var workflow = await _workflowService.GetByIdAsync(workflowId);
        //        if (workflow == null)
        //        {
        //            _logger.LogError("[动态链] Workflow 不存在: {WorkflowId}", workflowId);
        //            return;
        //        }

        //        // 解析链配置
        //        var config = JsonSerializer.Deserialize<DynamicChainConfig>(chainConfig);
        //        if (config == null || config.Steps == null || config.Steps.Count == 0)
        //        {
        //            throw new ArgumentException("链配置无效：Steps 不能为空");
        //        }

        //        workflow.Status = 2; // 执行中
        //        await _workflowService.UpdateAsync(workflow);
        //        await _hubContext.Clients.Group($"workflow-{workflowId}")
        //            .SendAsync("WorkflowStarted", new { workflowId, totalSteps = config.Steps.Count });

        //        // 执行上下文（存储每个步骤的输出，供后续步骤使用）
        //        var executionContext = new Dictionary<string, object>
        //        {
        //            ["workflow_id"] = workflowId,
        //            ["user_input"] = config.InitialInput ?? "",
        //            ["started_at"] = DateTime.Now
        //        };

        //        // 按顺序执行每个步骤
        //        for (int i = 0; i < config.Steps.Count; i++)
        //        {
        //            var step = config.Steps[i];
        //            _logger.LogInformation("[动态链] 执行步骤 {StepIndex}/{Total}: {StepName}",
        //                i + 1, config.Steps.Count, step.StepName);

        //            // 推送步骤开始
        //            await _hubContext.Clients.Group($"workflow-{workflowId}")
        //                .SendAsync("StepStarted", new
        //                {
        //                    workflowId,
        //                    stepIndex = i + 1,
        //                    stepName = step.StepName,
        //                    agentId = step.AgentRoleId
        //                });

        //            // 执行步骤
        //            cancellationToken.ThrowIfCancellationRequested();
        //            var stepResult = await ExecuteChainStepAsync(workflow, step, executionContext, cancellationToken);

        //            // 检查步骤是否成功
        //            if (!stepResult.Success)
        //            {
        //                _logger.LogError("[动态链] 步骤失败: {StepName}, 错误: {Error}",
        //                    step.StepName, stepResult.ErrorMessage);

        //                workflow.Status = 4; // 失败
        //                workflow.ResultSummary = $"步骤 {i + 1}/{config.Steps.Count} 失败: {stepResult.ErrorMessage}";
        //                await _workflowService.UpdateAsync(workflow);

        //                await _hubContext.Clients.Group($"workflow-{workflowId}")
        //                    .SendAsync("WorkflowFailed", new
        //                    {
        //                        workflowId,
        //                        failedStep = step.StepName,
        //                        error = stepResult.ErrorMessage
        //                    });

        //                return; // 终止执行
        //            }

        //            // 存储步骤输出到上下文
        //            executionContext[$"step_{i + 1}_output"] = stepResult.Output;
        //            executionContext[$"{step.StepName}_output"] = stepResult.Output;

        //            // 推送步骤完成
        //            await _hubContext.Clients.Group($"workflow-{workflowId}")
        //                .SendAsync("StepCompleted", new
        //                {
        //                    workflowId,
        //                    stepIndex = i + 1,
        //                    stepName = step.StepName,
        //                    output = stepResult.Output
        //                });

        //            // 条件分支：检查是否需要跳转
        //            if (step.ConditionalBranch != null)
        //            {
        //                var shouldBranch = EvaluateCondition(step.ConditionalBranch.Condition, stepResult.Output);
        //                if (shouldBranch)
        //                {
        //                    _logger.LogInformation("[动态链] 触发条件分支: {Condition} → {Action}",
        //                        step.ConditionalBranch.Condition, step.ConditionalBranch.Action);

        //                    // 根据 Action 决定后续流程
        //                    if (step.ConditionalBranch.Action == "skip_to_end")
        //                    {
        //                        _logger.LogInformation("[动态链] 跳过剩余步骤，直接完成");
        //                        break; // 跳出循环
        //                    }
        //                    else if (step.ConditionalBranch.Action.StartsWith("jump_to_step_"))
        //                    {
        //                        var targetStep = int.Parse(step.ConditionalBranch.Action.Replace("jump_to_step_", ""));
        //                        i = targetStep - 2; // -2 因为 for 循环会 +1
        //                        _logger.LogInformation("[动态链] 跳转到步骤: {TargetStep}", targetStep);
        //                        continue;
        //                    }
        //                }
        //            }
        //        }

        //        // 所有步骤完成
        //        var elapsed = (DateTime.Now - startTime).TotalSeconds;
        //        workflow.Status = 3; // 完成
        //        workflow.ActualDuration = (int)elapsed;
        //        workflow.ResultSummary = BuildResultSummary(executionContext, config.Steps.Count);
        //        await _workflowService.UpdateAsync(workflow);

        //        // 🆕 保存AI回复到会话
        //        await SaveAssistantMessageToSessionAsync(workflow, workflow.ResultSummary);

        //        await _hubContext.Clients.Group($"workflow-{workflowId}")
        //            .SendAsync("WorkflowCompleted", new
        //            {
        //                workflowId,
        //                totalSteps = config.Steps.Count,
        //                duration = elapsed,
        //                summary = workflow.ResultSummary
        //            });

        //        _logger.LogInformation("[动态链] 执行完成: WorkflowId={WorkflowId}, 耗时={Duration}秒",
        //            workflowId, elapsed);
        //    }
        //    catch (OperationCanceledException)
        //    {
        //        // 🆕 用户主动取消
        //        _logger.LogWarning("[动态链] 被用户取消: WorkflowId={WorkflowId}", workflowId);
                
        //        var workflow = await _workflowService.GetByIdAsync(workflowId);
        //        if (workflow != null)
        //        {
        //            workflow.Status = 5; // 已取消
        //            await _workflowService.UpdateAsync(workflow);
        //        }

        //        await _hubContext.Clients.Group($"workflow-{workflowId}")
        //            .SendAsync("WorkflowCancelled", new { workflowId, message = "任务已被取消" });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "[动态链] 执行失败: WorkflowId={WorkflowId}", workflowId);

        //        var workflow = await _workflowService.GetByIdAsync(workflowId);
        //        if (workflow != null)
        //        {
        //            workflow.Status = 4; // 失败
        //            workflow.ResultSummary = $"执行异常: {ex.Message}";
        //            await _workflowService.UpdateAsync(workflow);
        //        }

        //        await _hubContext.Clients.Group($"workflow-{workflowId}")
        //            .SendAsync("WorkflowFailed", new { workflowId, error = ex.Message });
        //    }
        //    finally
        //    {
        //        _cancellationService.UnregisterWorkflow(workflowId);
        //    }
        //}

        #endregion

        #region 步骤执行逻辑

        /// <summary>
        /// 执行单个链步骤
        /// </summary>
        private async Task<ChainStepResult> ExecuteChainStepAsync(
            Agent_WorkflowEntity workflow,
            ChainStep step,
            Dictionary<string, object> context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // 创建任务记录
                var task = CreateTaskWithFullFields(
                    workflow.Id,
                    step.StepName,
                    step.TaskType ?? "Action",
                    step.AgentRoleId,
                    BuildStepInput(step, context),
                    safetyLevel: step.SafetyLevel ?? 2
                );

                // 🔧 将完整上下文序列化存储到task，供agent访问
                var contextForAgent = new Dictionary<string, object>
                {
                    ["user_input"] = context["user_input"],
                    ["current_step_input"] = BuildStepInput(step, context)
                };
                // 添加所有前序步骤的输出
                foreach (var kv in context.Where(kv => kv.Key.EndsWith("_output")))
                {
                    contextForAgent[kv.Key] = kv.Value;
                }
                task.InputSnapshotJson = JsonHelper.SerializeObject(contextForAgent);

                // 🔧 优先从Agent配置读取是否使用ReAct，Planner规划为次优先级
                var agentRole = await _agentRoleService.GetByIdAsync(step.AgentRoleId);
                bool isComplexTask = agentRole?.Description?.Contains("ReAct") == true 
                    ? true 
                    : step.EnableReAct;
                
                // 🆕 统一使用 ReAct 模式，通过 MaxLoopCount 控制复杂度
                // - 简单任务: maxIterations = 5（至少 1 次 Thought + Action + Reflection）
                // - 复杂任务: maxIterations = 3-10（完整的多轮 ReAct 循环）
                task.MaxLoopCount = isComplexTask ? (step.MaxReActLoops ?? 10) : 5;

                await _taskService.InsertAsync(task);

                // 推送任务创建
                await _hubContext.Clients.Group($"workflow-{workflow.Id}")
                    .SendAsync("TaskCreated", new { taskId = task.Id, taskName = task.TaskName });

                // 🆕 统一使用 ReAct 引擎执行（通过 MaxLoopCount 控制复杂度）
                // - 简单任务 (MaxLoops=2): 保证至少有 Thought + Action + Reflection，失败可反思
                // - 复杂任务 (MaxLoops=3-10): 完整的多轮 ReAct 循环
                _logger.LogInformation("[动态链-步骤] 使用 ReAct 模式: {StepName} (MaxLoops={Max}, 复杂任务={IsComplex})", 
                    step.StepName, task.MaxLoopCount, isComplexTask);
                bool success = await ExecuteReActTaskAsync(task.Id, ReActExecutionMode.Custom, cancellationToken);

                // 读取更新后的任务
                task = await _taskService.GetByIdAsync(task.Id);

                if (success && task.Status == 3)
                {
                    return new ChainStepResult
                    {
                        Success = true,
                        Output = task.OutputResult ?? "",
                        TaskId = task.Id
                    };
                }
                else
                {
                    return new ChainStepResult
                    {
                        Success = false,
                        ErrorMessage = task.ErrorDetails ?? "任务执行失败",
                        TaskId = task.Id
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[动态链-步骤] 执行异常: {StepName}", step.StepName);
                return new ChainStepResult
                {
                    Success = false,
                    ErrorMessage = $"执行异常: {ex.Message}"
                };
            }
        }

        // 🆕 已移除 ExecuteSimpleTaskAsync 方法
        // 所有任务统一使用 ReAct 模式（通过 MaxLoopCount 控制复杂度）
        // - 简单任务: MaxLoops=2（至少 1 次完整循环 + 失败可反思）
        // - 复杂任务: MaxLoops=3-10（完整的多轮 ReAct 循环）

        #endregion

        #region 辅助方法

        /// <summary>
        /// 构建步骤输入（支持引用上下文）
        /// </summary>
        private string BuildStepInput(ChainStep step, Dictionary<string, object> context)
        {
            if (string.IsNullOrEmpty(step.InputTemplate))
            {
                // 默认：使用上一步的输出
                if (context.ContainsKey("step_1_output"))
                {
                    var lastKey = context.Keys.Where(k => k.StartsWith("step_")).OrderByDescending(k => k).FirstOrDefault();
                    return context[lastKey]?.ToString() ?? "";
                }
                return context["user_input"]?.ToString() ?? "";
            }

            // 替换模板中的变量（如: {user_input}, {step_1_output}）
            var input = step.InputTemplate;
            foreach (var kv in context)
            {
                input = input.Replace($"{{{kv.Key}}}", kv.Value?.ToString() ?? "");
            }

            return input;
        }

        /// <summary>
        /// 评估条件分支
        /// </summary>
        private bool EvaluateCondition(string condition, string stepOutput)
        {
            if (string.IsNullOrEmpty(condition)) return false;

            // 简单条件解析（可扩展为更复杂的表达式）
            if (condition.Contains("contains"))
            {
                var keyword = condition.Split("contains")[1].Trim().Trim('\'', '\"');
                return stepOutput.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            }
            else if (condition == "parser_exists")
            {
                // 特殊条件：解析器是否存在
                return stepOutput.Contains("\"parser_exists\": true", StringComparison.OrdinalIgnoreCase);
            }
            else if (condition == "parser_not_exists")
            {
                return stepOutput.Contains("\"parser_exists\": false", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <summary>
        /// 构建结果摘要
        /// </summary>
        private string BuildResultSummary(Dictionary<string, object> context, int totalSteps)
        {
            var summary = $"动态顺序链执行完成，共 {totalSteps} 个步骤。\n\n";

            for (int i = 1; i <= totalSteps; i++)
            {
                var key = $"step_{i}_output";
                if (context.ContainsKey(key))
                {
                    var output = context[key]?.ToString() ?? "";
                    summary += $"步骤 {i} 输出: {(output.Length > 100 ? output.Substring(0, 100) + "..." : output)}\n";
                }
            }

            return summary;
        }

        #endregion
    }

    #region 数据模型

    /// <summary>
    /// 动态链配置
    /// </summary>
    public class DynamicChainConfig
    {
        /// <summary>
        /// 初始输入（用户需求）
        /// </summary>
        public string InitialInput { get; set; } = "";

        /// <summary>
        /// 步骤列表（按顺序执行）
        /// </summary>
        public List<ChainStep> Steps { get; set; } = new();
    }

    /// <summary>
    /// 链步骤配置
    /// </summary>
    public class ChainStep
    {
        /// <summary>
        /// 步骤名称
        /// </summary>
        public string StepName { get; set; } = "";

        /// <summary>
        /// 智能体角色ID
        /// </summary>
        public long AgentRoleId { get; set; }

        /// <summary>
        /// 任务类型（Analysis/Action/Verification）
        /// </summary>
        public string? TaskType { get; set; }

        /// <summary>
        /// 输入模板（支持变量：{user_input}, {step_1_output}）
        /// </summary>
        public string? InputTemplate { get; set; }

        /// <summary>
        /// 是否启用 ReAct 循环
        /// </summary>
        public bool EnableReAct { get; set; } = false;

        /// <summary>
        /// ReAct 最大循环次数
        /// </summary>
        public int? MaxReActLoops { get; set; } = 10;

        /// <summary>
        /// 安全等级（1-4）
        /// </summary>
        public int? SafetyLevel { get; set; } = 2;

        /// <summary>
        /// 条件分支（可选）
        /// </summary>
        public ConditionalBranch? ConditionalBranch { get; set; }
    }

    /// <summary>
    /// 条件分支配置
    /// </summary>
    public class ConditionalBranch
    {
        /// <summary>
        /// 条件表达式（如: "contains 'parser_exists'", "parser_not_exists"）
        /// </summary>
        public string Condition { get; set; } = "";

        /// <summary>
        /// 满足条件时的动作（skip_to_end, jump_to_step_X）
        /// </summary>
        public string Action { get; set; } = "";
    }

    /// <summary>
    /// 步骤执行结果
    /// </summary>
    public class ChainStepResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = "";
        public string? ErrorMessage { get; set; }
        public long TaskId { get; set; }
    }

    #endregion
}
