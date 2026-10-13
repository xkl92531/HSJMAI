using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Logging;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.Threading;
using HSJM.AI.Utility;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// WorkflowBackgroundJobService 分部类 - Agent Orchestration 引擎
    /// 🚀 核心功能：让 Planner 智能体自主规划任务，动态生成执行流程
    /// 🎯 设计理念：平台通用，流程不硬编码，完全由 AI 决定
    /// </summary>
    public partial class WorkflowBackgroundJobService
    {
        #region Agent Orchestration（智能体自主编排）

        /// <summary>
        /// Agent Orchestration 模式执行
        /// 
        /// 核心流程：
        /// 1. Planner 分析用户需求
        /// 2. Planner 调用 LLM 生成执行计划（JSON 格式的 DynamicChainConfig）
        /// 3. 平台解析 JSON，动态执行
        /// 4. 子智能体根据授权表自动选择工具
        /// 
        /// 优势：
        /// - 完全动态：今天解析 PLC，明天诊断设备，后天优化代码
        /// - 零硬编码：流程完全由 Planner 决定
        /// - 工具自动匹配：通过 AI_Agent_Role_Tool_Relation 授权
        /// </summary>
        /// <param name="workflowId">工作流ID</param>
        /// <param name="userInput">用户需求（纯文本）</param>
        /// <param name="plannerRoleId">Planner 角色ID</param>
        [AutomaticRetry(Attempts = 0)]
        [Hangfire.JobDisplayName("Workflow·Agent编排 · WF{0}")]
        public async Task ExecuteAgentOrchestrationAsync(
            long workflowId, 
            string userInput, 
            long plannerRoleId)
        {
            _logger.LogInformation("[Agent编排] 开始执行: WorkflowId={WorkflowId}, Planner={PlannerId}",
                workflowId, plannerRoleId);
            var startTime = DateTime.Now;

            try
            {
                // 🆕 注册取消令牌
                var cancellationToken = _cancellationService.RegisterWorkflow(workflowId);

                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow == null)
                {
                    _logger.LogError("[Agent编排] Workflow 不存在: {WorkflowId}", workflowId);
                    return;
                }

                workflow.Status = 2; // 执行中
                await _workflowService.UpdateAsync(workflow);
                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("WorkflowStarted", new { workflowId, mode = "AgentOrchestration" });

                // ============================================
                // 阶段1：Planner 生成执行计划
                // ============================================
                _logger.LogInformation("[Agent编排] 阶段1: Planner 开始规划任务...");
                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("PlanningStarted", new { workflowId, planner = plannerRoleId });

                cancellationToken.ThrowIfCancellationRequested();
                var executionPlan = await GenerateExecutionPlanAsync(workflow, userInput, plannerRoleId, cancellationToken);

                if (executionPlan == null || executionPlan.Steps == null || executionPlan.Steps.Count == 0)
                {
                    throw new InvalidOperationException("Planner 未能生成有效的执行计划");
                }

                _logger.LogInformation("[Agent编排] Planner 生成计划: {StepCount} 个步骤",
                    executionPlan.Steps.Count);

                // 保存计划到 Workflow
                workflow.PlanDAG = JsonHelper.SerializeObject(executionPlan);
                workflow.PlanStatus = "Approved"; // 自动批准（高风险任务需人工审批）
                await _workflowService.UpdateAsync(workflow);

                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("PlanGenerated", new
                    {
                        workflowId,
                        stepCount = executionPlan.Steps.Count,
                        steps = executionPlan.Steps.Select(s => new
                        {
                            s.StepName,
                            agentRole = s.AgentRoleId,
                            enableReAct = s.EnableReAct
                        })
                    });

                // ============================================
                // 阶段2：执行动态链
                // ============================================
                _logger.LogInformation("[Agent编排] 阶段2: 开始执行动态链...");

                // 复用已有的 ExecuteDynamicSequentialChainAsync 逻辑
                // 但不重新创建 Workflow，直接执行
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteDynamicChainSteps(workflow, executionPlan, cancellationToken);

                // ============================================
                // 阶段3：Planner 汇总结果
                // ============================================
                // 阶段3：Planner 汇总结果
                // ============================================
                _logger.LogInformation("[Agent编排] 阶段3: Planner 汇总结果...");
                cancellationToken.ThrowIfCancellationRequested();
                await SummarizeWorkflowResultsAsync(workflow, cancellationToken);

                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                workflow.Status = 3; // 完成
                workflow.ActualDuration = (int)elapsed;
                await _workflowService.UpdateAsync(workflow);

                // 🆕 保存AI回复到会话
                await SaveAssistantMessageToSessionAsync(workflow, workflow.ResultSummary);

                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("WorkflowCompleted", new
                    {
                        workflowId,
                        duration = elapsed,
                        summary = workflow.ResultSummary
                    });

                _logger.LogInformation("[Agent编排] 执行完成: WorkflowId={WorkflowId}, 耗时={Duration}秒",
                    workflowId, elapsed);
            }
            catch (OperationCanceledException)
            {
                // 🆕 用户主动取消
                _logger.LogWarning("[Agent编排] 被用户取消: WorkflowId={WorkflowId}", workflowId);
                
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
                _logger.LogError(ex, "[Agent编排] 执行失败: WorkflowId={WorkflowId}", workflowId);

                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow != null)
                {
                    workflow.Status = 4; // 失败
                    workflow.ResultSummary = $"Agent 编排失败: {ex.Message}";
                    await _workflowService.UpdateAsync(workflow);
                }

                await _hubContext.Clients.Group($"workflow-{workflowId}")
                    .SendAsync("WorkflowFailed", new { workflowId, error = ex.Message });
            }
            finally
            {
                _cancellationService.UnregisterWorkflow(workflowId);
            }
        }

        #endregion

        #region Planner 执行计划生成

        /// <summary>
        /// 让 Planner 智能体生成执行计划
        /// 
        /// Planner 的职责：
        /// 1. 分析用户需求
        /// 2. 决定需要哪些专家智能体（从数据库查询可用角色）
        /// 3. 决定执行顺序和依赖关系
        /// 4. 决定哪些步骤需要 ReAct 循环
        /// 5. 输出结构化的 JSON 计划
        /// </summary>
        private async Task<DynamicChainConfig> GenerateExecutionPlanAsync(
            Agent_WorkflowEntity workflow,
            string userInput,
            long plannerRoleId,
            CancellationToken cancellationToken)
        {
            try
            {
                // 1. 获取 Planner 角色配置
                var plannerRole = await _agentRoleService.GetByIdAsync(plannerRoleId);
                if (plannerRole == null)
                {
                    throw new InvalidOperationException($"Planner 角色不存在: {plannerRoleId}");
                }

                // 2. 🆕 优先从 AI_Agent_Relation 表获取团队成员
                var teamMembers = await GetTeamMembersForPlannerAsync(plannerRoleId);
                List<Agent_RoleEntity> availableRoles;
                
                if (teamMembers != null && teamMembers.Count > 0)
                {
                    availableRoles = teamMembers;
                    _logger.LogInformation("[Agent编排-Planning] 从关系表加载团队成员: {Count} 个", teamMembers.Count);
                }
                else
                {
                    // 降级：查询所有可用角色
                    availableRoles = await _agentRoleService.GetListAsync(r => r.IsEnabled == true && r.IsDeleted == false);
                    _logger.LogWarning("[Agent编排-Planning] 未配置团队关系，降级为所有可用角色");
                }
                
                // ✅ 明确展示 ID（数字）和 RoleCode（仅供参考），并加载技能信息
                // 🆕 同时收集所有团队成员的技能，供 Planner 的 SkillAccessTool 使用
                var summaryList = new List<string>();
                var allTeamSkills = new List<SkillContent>();  // 收集所有团队技能
                
                foreach (var r in availableRoles)
                {
                    var skills = await _skillLoader.LoadSkillsForRoleAsync(r.Id, cancellationToken);
                    if (skills.Count > 0)
                    {
                        allTeamSkills.AddRange(skills);  // 汇聚团队技能
                    }
                    var skillInfo = skills.Count > 0 
                        ? $"| 技能: {string.Join(", ", skills.Select(s => s.Name+$"({s.Description})"))}" 
                        : "";
                    
                    summaryList.Add($"- agent_role_id={r.Id} (数字ID), role_code={r.RoleCode}, 名称={r.RoleName}, 描述={r.Description} {skillInfo}");
                }
                var rolesSummary = string.Join("\n", summaryList);
                
                _logger.LogInformation("[Agent编排-Planning] 汇聚团队技能: {Count} 个", allTeamSkills.Count);

                // 3. 🆕 构建 Planner 的 SystemPrompt（含团队成员）
                var basePrompt = !string.IsNullOrWhiteSpace(plannerRole.SystemPrompt) 
                    ? plannerRole.SystemPrompt 
                    : GetDefaultPlannerPrompt();
                
                // 🆕 注入运行时环境信息
                var osInfo = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                basePrompt = $"[Runtime Environment: {osInfo}]\n\n" + basePrompt;

                // 替换 {{AVAILABLE_TEAM_MEMBERS}} 占位符
                var systemPromptWithTeam = basePrompt.Replace("{{AVAILABLE_TEAM_MEMBERS}}", rolesSummary);
                
                _logger.LogDebug("[Agent编排-Planning] SystemPrompt 长度: {Length}, 使用数据库配置: {UseDb}", 
                    systemPromptWithTeam.Length, !string.IsNullOrWhiteSpace(plannerRole.SystemPrompt));

                // 4. 🆕 创建 Planner Agent（使用自定义 SystemPrompt，并传入团队技能）
                var plannerAgent = await CreatePlannerAgentAsync(plannerRoleId, systemPromptWithTeam, allTeamSkills);
                
                // ============================================
                // 🆕 增加重试机制 (Max 3次)
                // ============================================
                int maxRetries = 3;
                string lastError = "";

                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        // 5. 🆕 构建用户消息（只包含任务需求）
                        var userMessage = $@"请为以下用户需求生成执行计划：

{userInput}

请严格按照 JSON 格式输出执行计划。";

                        // 如果是重试，追加错误提示
                        if (attempt > 1)
                        {
                            userMessage += $"\n\n【系统警告】你上一次生成的计划格式不正确（Attempt {attempt - 1} Failed）。\n错误原因：{lastError}\n请务必严格遵循 JSON 格式，不要输出 Markdown 代码块标记（```），不要输出任何多余的分析文本，只输出纯 JSON 字符串。";
                            _logger.LogWarning("[Agent编排-Planning] 第 {Attempt} 次重试，追加错误提示...", attempt);
                        }
                        var history = await _chatSessionService.GetSessionHistoryAsync(workflow.CorrelationID.ToLong());
                        // 6. 调用 Planner LLM
                        var response = await _agentRuntime.RunAgentAsync(plannerAgent, userMessage, history, cancellationToken);

                        _logger.LogDebug("[Agent编排-Planning] Planner 原始输出 (Attempt {Attempt}):\n{Response}", attempt, response.Content);

                        // 7. 解析 JSON 响应
                        var planJson = ExtractJsonFromResponse(response.Content);
                        PlannerResponse planResponse;
                        try 
                        {
                            planResponse = JsonSerializer.Deserialize<PlannerResponse>(planJson);
                        }
                        catch (JsonException jsonEx)
                        {
                            lastError = $"JSON 解析失败: {jsonEx.Message}";
                            throw new Exception(lastError);
                        }

                        if (planResponse == null || planResponse.Steps == null || planResponse.Steps.Count == 0)
                        {
                            lastError = "生成的计划为空或没有步骤";
                            throw new Exception(lastError);
                        }

                        _logger.LogInformation("[Agent编排-Planning] Planner 推理成功: {Reasoning}", planResponse.Reasoning);

                        // 8. 转换为 DynamicChainConfig
                        var config = new DynamicChainConfig
                        {
                            InitialInput = userInput,
                            Steps = planResponse.Steps.Select(s => new ChainStep
                            {
                                StepName = s.StepName,
                                AgentRoleId = s.AgentRoleId,
                                TaskType = s.TaskType,
                                InputTemplate = s.InputTemplate,
                                EnableReAct = s.EnableReAct,
                                MaxReActLoops = s.MaxReActLoops ?? 10,
                                SafetyLevel = s.SafetyLevel ?? 2,
                                ConditionalBranch = s.ConditionalBranch != null
                                    ? new ConditionalBranch
                                    {
                                        Condition = s.ConditionalBranch.Condition,
                                        Action = s.ConditionalBranch.Action
                                    }
                                    : null
                            }).ToList()
                        };

                        return config;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Agent编排-Planning] 第 {Attempt} 次生成失败: {Error}", attempt, ex.Message);
                        lastError = ex.Message;

                        if (attempt == maxRetries)
                        {
                            throw new InvalidOperationException($"Planner 生成计划失败，已重试 {maxRetries} 次。Last Error: {lastError}", ex);
                        }
                        
                        // 等待一小会儿再重试，避免并发限制
                        await Task.Delay(500, cancellationToken);
                    }
                }

                throw new InvalidOperationException("Unreachable code");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent编排-Planning] Planner 生成计划失败");
                throw;
            }
        }

        /// <summary>
        /// 从 LLM 响应中提取 JSON（处理 Markdown 代码块）
        /// </summary>
        private string ExtractJsonFromResponse(string response)
        {
            if (string.IsNullOrEmpty(response)) return "{}";

            // 移除 Markdown 代码块标记
            var json = response.Trim();
            if (json.StartsWith("```json"))
            {
                json = json.Substring(7);
            }
            else if (json.StartsWith("```"))
            {
                json = json.Substring(3);
            }

            if (json.EndsWith("```"))
            {
                json = json.Substring(0, json.Length - 3);
            }

            return json.Trim();
        }

        /// <summary>
        /// 🆕 从 AI_Agent_Relation 表获取 Planner 的团队成员
        /// </summary>
        private async Task<List<Agent_RoleEntity>> GetTeamMembersForPlannerAsync(long plannerRoleId)
        {
            try
            {
                // 1. 查询关系表：ParentRoleID = plannerRoleId
                var relations = await _agentRelationService.GetListAsync(
                    r => r.ParentRoleID == plannerRoleId && r.IsDeleted == false);
                
                if (relations == null || relations.Count == 0)
                {
                    _logger.LogDebug("[Agent编排-团队] Planner {PlannerId} 未配置团队关系", plannerRoleId);
                    return null;
                }
                
                // 2. 获取子角色详情
                var childRoleIds = relations
                    .Where(r => r.ChildRoleID.HasValue)
                    .Select(r => r.ChildRoleID.Value)
                    .ToList();
                
                if (childRoleIds.Count == 0)
                    return null;
                    
                var roles = await _agentRoleService.GetListAsync(
                    r => childRoleIds.Contains(r.Id) && r.IsEnabled == true && r.IsDeleted == false);
                
                _logger.LogInformation("[Agent编排-团队] Planner {PlannerId} 的团队成员: {Members}", 
                    plannerRoleId, string.Join(", ", roles.Select(r => $"{r.RoleName}({r.Id})")));
                
                return roles;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Agent编排-团队] 获取团队成员失败，将使用所有可用角色");
                return null;
            }
        }

        /// <summary>
        /// 🆕 默认 Planner Prompt（数据库 SystemPrompt 为空时使用）
        /// </summary>
        private string GetDefaultPlannerPrompt()
        {
            return @"你是流程规划师 (Process Planner)，负责将用户需求拆解为子任务进行智能编排。

【核心决策原则】
1. **引用优先 (Data by Reference)**: 严禁把大文件内容直接放入 input_template，必须传递文件绝对路径。
2. **强制 ReAct 模式**: 涉及代码分析、文件处理、技能调用的任务，必须开启 enable_react: true，max_react_loops >= 15。
3. **优先使用元技能 (Meta-Skill Priority)**: 
   - 如果用户请求**创建新技能/能力**（如“创建一个PPT解析技能”），**严禁**手动拆解为分析/生成/打包步骤。
   - **必须**直接检查团队中是否有 **Skill Creator (skill-creator)** 或类似元技能。
   - 如果有，直接生成**单个步骤**，分配给通用助手 (2012)，并在 input_template 中明确指示使用 skill-creator。

【可用团队成员】
{{AVAILABLE_TEAM_MEMBERS}}

【输出要求】
严格按照以下 JSON 格式输出：
{
  ""reasoning"": ""分析思路：为何选择这些人？数据流是如何设计的？为何需要开启 ReAct？"",
  ""steps"": [
    {
      ""step_name"": ""步骤名称"",
      ""agent_role_id"": 2012,
      ""task_type"": ""Generation"", 
      ""input_template"": ""请使用 skill-creator 为我创建一个...技能"",
      ""enable_react"": true,
      ""max_react_loops"": 15,
      ""safety_level"": 3
    }
  ]
}

【安全等级说明】
- 1=信息查询 (无副作用)
- 2=数据分析 (读取/计算)
- 3=数据生成 (写入文件/代码)
- 4=系统控制 (高风险操作)";
        }

        /// <summary>
        /// 🆕 创建 Planner Agent（使用自定义 SystemPrompt）
        /// </summary>
        /// <param name="plannerRoleId">Planner 角色 ID</param>
        /// <param name="customSystemPrompt">自定义 SystemPrompt</param>
        /// <param name="teamSkills">团队成员的技能列表（仅用于在 SystemPrompt 中展示，Planner 不执行工具）</param>
        private async Task<ChatClientAgent> CreatePlannerAgentAsync(
            long plannerRoleId, 
            string customSystemPrompt,
            List<SkillContent>? teamSkills = null)
        {
            // 1. 加载角色配置
            var role = await _agentRoleService.GetByIdAsync(plannerRoleId);
            if (role == null || role.IsDeleted)
                throw new InvalidOperationException($"Planner 角色不存在: {plannerRoleId}");

            // 2. 选择 LLM 模型
            IChatClient chatClient;
            if (role.ModelID.HasValue && role.ModelID.Value > 0)
            {
                chatClient = (await _llmModelService.CreateChatClientByModelIdAsync(role.ModelID.Value)).AsIChatClient();
            }
            else
            {
                var recommendedModel = await _llmModelService.RecommendModelAsync();
                if (recommendedModel == null)
                    throw new InvalidOperationException("无可用的 LLM 模型");
                
                chatClient = (await _llmModelService.CreateChatClientByModelIdAsync(recommendedModel.Id)).AsIChatClient();
            }

            // 3. 🔧 Planner 不需要工具！
            // Planner 的职责是规划，不是执行。团队成员信息已通过 SystemPrompt 提供。
            // 如果给 Planner 工具，它会调用工具而不是输出 JSON 计划。
            // var tools = await _agentRuntime.LoadToolsForRoleAsync(plannerRoleId, teamSkills);

            // 4. 创建 Agent（无工具，只输出 JSON 计划）
            var agent = new ChatClientAgent(
                chatClient,
                instructions: customSystemPrompt,
                name: role.RoleName ?? $"Planner-{plannerRoleId}"
                // 注意：不传入 tools 参数，Planner 专注于输出 JSON 计划
            );

            _logger.LogInformation("[Agent编排-Planning] 创建Planner Agent: {Name}, 无工具（规划模式）, TeamSkills={SkillCount}", 
                agent.Name, teamSkills?.Count ?? 0);

            return agent;
        }

        #endregion

        #region 执行动态链步骤（复用逻辑）

        /// <summary>
        /// 执行动态链的所有步骤（从 DynamicChain.cs 复用逻辑）
        /// </summary>
        private async Task ExecuteDynamicChainSteps(
            Agent_WorkflowEntity workflow,
            DynamicChainConfig config,
            CancellationToken cancellationToken)
        {
            var executionContext = new Dictionary<string, object>
            {
                ["workflow_id"] = workflow.Id,
                ["user_input"] = config.InitialInput ?? "",
                ["started_at"] = DateTime.Now
            };

            for (int i = 0; i < config.Steps.Count; i++)
            {
                var step = config.Steps[i];
                _logger.LogInformation("[Agent编排-执行] 步骤 {StepIndex}/{Total}: {StepName}",
                    i + 1, config.Steps.Count, step.StepName);

                await _hubContext.Clients.Group($"workflow-{workflow.Id}")
                    .SendAsync("StepStarted", new
                    {
                        workflowId = workflow.Id,
                        stepIndex = i + 1,
                        stepName = step.StepName,
                        agentId = step.AgentRoleId
                    });

                cancellationToken.ThrowIfCancellationRequested();
                var stepResult = await ExecuteChainStepAsync(workflow, step, executionContext, cancellationToken);

                if (!stepResult.Success)
                {
                    _logger.LogError("[Agent编排-执行] 步骤失败: {StepName}, 错误: {Error}",
                        step.StepName, stepResult.ErrorMessage);

                    workflow.Status = 4;
                    workflow.ResultSummary = $"步骤 '{step.StepName}' 失败: {stepResult.ErrorMessage}";
                    await _workflowService.UpdateAsync(workflow);

                    await _hubContext.Clients.Group($"workflow-{workflow.Id}")
                        .SendAsync("WorkflowFailed", new
                        {
                            workflowId = workflow.Id,
                            failedStep = step.StepName,
                            error = stepResult.ErrorMessage
                        });

                    return;
                }

                executionContext[$"step_{i + 1}_output"] = stepResult.Output;
                executionContext[$"{step.StepName}_output"] = stepResult.Output;

                await _hubContext.Clients.Group($"workflow-{workflow.Id}")
                    .SendAsync("StepCompleted", new
                    {
                        workflowId = workflow.Id,
                        stepIndex = i + 1,
                        stepName = step.StepName,
                        output = stepResult.Output
                    });

                // 条件分支处理
                if (step.ConditionalBranch != null)
                {
                    var shouldBranch = EvaluateCondition(step.ConditionalBranch.Condition, stepResult.Output);
                    if (shouldBranch)
                    {
                        _logger.LogInformation("[Agent编排-执行] 触发条件分支: {Condition} → {Action}",
                            step.ConditionalBranch.Condition, step.ConditionalBranch.Action);

                        if (step.ConditionalBranch.Action == "skip_to_end")
                        {
                            break;
                        }
                        else if (step.ConditionalBranch.Action.StartsWith("jump_to_step_"))
                        {
                            var targetStep = int.Parse(step.ConditionalBranch.Action.Replace("jump_to_step_", ""));
                            i = targetStep - 2;
                            continue;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Planner 汇总工作流结果
        /// </summary>
        /// <summary>
        /// Determine if summary is needed and execute it
        /// </summary>
        private async Task SummarizeWorkflowResultsAsync(Agent_WorkflowEntity workflow, CancellationToken cancellationToken)
        {
            try
            {
                var tasks = await _taskService.GetListAsync(t => t.WorkflowID == workflow.Id);
                var lastTask = tasks.OrderByDescending(t => t.Id).FirstOrDefault();
                string finalOutput = lastTask?.OutputResult ?? "";

                // 🆕 智能跳过总结逻辑 (Smart Skip Logic)
                bool shouldSkipSummary = false;
                string skipReason = "";

                // 1. 单步骤任务 -> 直接返回结果 (除非用户显式要求总结)
                if (tasks.Count == 1)
                {
                    shouldSkipSummary = true;
                    skipReason = "Single step task";
                }
                // 2. 结构化输出 (JSON) -> 保持原样 (前端可能需要解析)
                else if (finalOutput.Trim().StartsWith("{") || finalOutput.Trim().StartsWith("["))
                {
                    shouldSkipSummary = true;
                    skipReason = "Structured JSON output";
                }
                // 3. 长篇 Markdown 报告 -> 已经是很好的结果了
                else if (finalOutput.Length > 200 && (finalOutput.Contains("# ") || finalOutput.Contains("```")))
                {
                    shouldSkipSummary = true;
                    skipReason = "Already a detailed Markdown report";
                }

                if (shouldSkipSummary)
                {
                    workflow.ResultSummary = finalOutput;
                    await _workflowService.UpdateAsync(workflow);
                    _logger.LogInformation("[Agent编排-汇总] 跳过总结 ({Reason})，直接使用最后一步输出", skipReason);
                    return;
                }

                // ============================================
                // 需要总结：调用 Summarizer (2011) 或 Planner
                // ============================================

                // 查找 Summarizer 角色
                long summarizerId = 0;
                var summarizerRoles = await _agentRoleService.GetListAsync(
                    r => (r.RoleCode == "summarizer" || r.RoleName.Contains("Summarizer")) && r.IsEnabled == true);
                
                if (summarizerRoles.Count > 0)
                    summarizerId = summarizerRoles[0].Id;
                else
                    summarizerId = workflow.PlannerAgentID ?? 0; // 降级

                if (summarizerId == 0)
                {
                    workflow.ResultSummary = finalOutput; // 无可用Agent，直接返回
                    await _workflowService.UpdateAsync(workflow);
                    return;
                }

                // 构建 "Answer-First" 上下文
                // 这里不再强调 "总结任务"，而是 "利用结果回答问题"
                var taskType = DetermineTaskType(workflow.Description, tasks);
                var promptTemplate = GetDefaultSummaryPrompt(taskType);
                
                var contextData = new
                {
                    user_request = workflow.Description,
                    task_results = string.Join("\n\n", tasks.Select(t => 
                        $"### 步骤: {t.TaskName} (Status: {t.Status})\n输出:\n{t.OutputResult}"))
                };

                var userPrompt = promptTemplate
                    .Replace("{{user_request}}", contextData.user_request)
                    .Replace("{{task_results}}", contextData.task_results);

                _logger.LogInformation("[Agent编排-汇总] 调用 Summarizer 生成最终回答...");

                var agent = await _agentRuntime.CreateAgentWithToolsAsync(summarizerId);
                var summaryResponse = await _agentRuntime.RunAgentAsync(agent, userPrompt, null, cancellationToken);
                
                workflow.ResultSummary = summaryResponse.Content;
                await _workflowService.UpdateAsync(workflow);
                _logger.LogInformation("[Agent编排-汇总] 最终回答已生成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Agent编排-汇总] 失败");
                workflow.ResultSummary = $"执行完成，但汇总失败: {ex.Message}";
                await _workflowService.UpdateAsync(workflow);
            }
        }

        /// <summary>
        /// 获取 "Answer-First" 风格的 Prompt
        /// </summary>
        private string GetDefaultSummaryPrompt(string taskType)
        {
            // 核心思想：User Ask -> Context (Results) -> AI Answer
            return @"
用户的问题/需求:
{{user_request}}

---
执行过程与结果数据 (Context):
{{task_results}}

---
【指令】
请忽略中间过程的琐碎细节，**直接回答用户的问题**。
- 如果用户想要代码，直接给代码。
- 如果用户想要结论，直接给结论。
- 如果任务失败，请分析原因并给出建议。
- 不要说 ""经过分析..."" 或 ""第一步...""，直接给出最终交付物。
";
        }

        /// <summary>
        /// 智能判断任务类型
        /// </summary>
        private string DetermineTaskType(string userInput, List<Agent_TaskEntity> tasks)
        {
            var input = userInput.ToLower();
            var taskNames = string.Join(" ", tasks.Select(t => t.TaskName.ToLower()));
            
            // 代码生成类：包含"生成"、"解析器"、"适配器"、"转换器"等关键词
            if (input.Contains("生成") && (
                input.Contains("解析器") || input.Contains("parser") ||
                input.Contains("适配器") || input.Contains("adapter") ||
                input.Contains("转换器") || input.Contains("converter") ||
                input.Contains("代码") || input.Contains("code") ||
                taskNames.Contains("生成代码") || taskNames.Contains("code generator")))
            {
                return "CodeGeneration";
            }
            
            // 分析诊断类：包含"分析"、"诊断"、"定位"、"故障"等关键词
            if (input.Contains("分析") || input.Contains("诊断") || input.Contains("定位") ||
                input.Contains("故障") || input.Contains("问题") || input.Contains("原因") ||
                taskNames.Contains("分析") || taskNames.Contains("诊断"))
            {
                return "Analysis";
            }
            
            // 数据处理类：包含"解析"、"转换"、"入库"、"提取"等关键词
            if (input.Contains("解析") || input.Contains("转换") || input.Contains("入库") ||
                input.Contains("提取") || input.Contains("导入") || input.Contains("处理") ||
                taskNames.Contains("数据提取") || taskNames.Contains("入库"))
            {
                return "DataProcessing";
            }
            
            // 默认为通用任务
            return "General";
        }

        #endregion
    }

    #region Planner 响应模型

    /// <summary>
    /// Planner 响应（JSON 格式）
    /// ✅ 使用 JsonPropertyName 支持 snake_case 映射
    /// </summary>
    public class PlannerResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("reasoning")]
        public string Reasoning { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("steps")]
        public List<PlannerStep> Steps { get; set; } = new();
    }

    public class PlannerStep
    {
        [System.Text.Json.Serialization.JsonPropertyName("step_name")]
        public string StepName { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("agent_role_id")]
        public long AgentRoleId { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("task_type")]
        public string? TaskType { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("input")]
        public string? InputTemplate { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("enable_react")]
        public bool EnableReAct { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("max_react_loops")]
        public int? MaxReActLoops { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("safety_level")]
        public int? SafetyLevel { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("conditional_branch")]
        public PlannerConditionalBranch? ConditionalBranch { get; set; }
    }

    public class PlannerConditionalBranch
    {
        [System.Text.Json.Serialization.JsonPropertyName("condition")]
        public string Condition { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("action")]
        public string Action { get; set; } = "";
    }

    #endregion
}
