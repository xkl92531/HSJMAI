// Copyright (c) Microsoft. All rights reserved.
// 基于官方最佳实践重构的任务编排系统
// 参考: https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted/Workflows

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MAF.DEMO.AppLayer.AI.Models;
using MAF.DEMO.AppLayer.AI.Services;
using MAF.DEMO.Core;

namespace MAF.DEMO.AppLayer.AI
{
    /// <summary>
    /// 基于官方Workflow的任务编排系统
    /// 展示多种编排模式：条件路由、FanOut/FanIn、顺序链、混合模式
    /// </summary>
    public class WorkflowOrchestrationApp:ISelfScopedAutoInject
    {
        private readonly IChatClient _chatClient;
        private readonly ILogger<WorkflowOrchestrationApp> _logger;
        private readonly IWorkflowCheckpointStore? _checkpointStore;

        public WorkflowOrchestrationApp(
            IChatClient chatClient, 
            ILogger<WorkflowOrchestrationApp> logger,
            IWorkflowCheckpointStore? checkpointStore = null)
        {
            _chatClient = chatClient;
            _logger = logger;
            _checkpointStore = checkpointStore;
        }

        #region 模式1: 智能条件路由（官方推荐 - 替代手动JSON解析）

        /// <summary>
        /// 模式1: 智能条件路由 - Router自动判断需要哪些专家
        /// 官方模式: Conditional Routing with Dynamic Agent Selection
        /// </summary>
        public async Task<WorkflowResult> RunConditionalRoutingAsync(
            string userInput, 
            string? workflowId = null,
            bool enableCheckpoint = false)
        {
            _logger.LogInformation("执行条件路由模式, 输入: {Input}", userInput);
            var startTime = DateTime.Now;

            try
            {
                // 1. 创建专家Agent池
                var expertAgents = CreateExpertAgents();

                // 2. 创建路由Agent（替代手动JSON解析）
                var router = new ChatClientAgent(_chatClient,
                    instructions: @"你是任务路由专家。分析用户请求，返回JSON格式的路由决策：
{
    ""needsDataAnalysis"": true/false,
    ""needsTimeInfo"": true/false,
    ""needsWeather"": true/false,
    ""needsDeveloper"": true/false,
    ""complexity"": ""simple/medium/complex"",
    ""estimatedDuration"": 预估秒数
}

规则：
- 涉及计算、数据处理 → needsDataAnalysis
- 询问时间、日期 → needsTimeInfo  
- 询问天气、地点 → needsWeather
- 技术问题、代码 → needsDeveloper",
                    name: "RouterAgent");

                // 3. 执行Router获取决策
                workflowId ??= $"workflow-{Guid.NewGuid():N}";
                
                if (enableCheckpoint && _checkpointStore != null)
                {
                    await _checkpointStore.SaveWorkflowStartAsync(workflowId, userInput, "ConditionalRouting");
                }

                _logger.LogInformation("[Step 1] Router分析中...");
                var routerResponse = await router.RunAsync(userInput);
                var routerDecision = routerResponse.Text ?? "";

                // 4. 根据Router决策并行执行专家
                var activeExperts = new List<(string Name, AIAgent Agent)>();
                
                if (ShouldRouteToExpert(routerDecision, "needsDataAnalysis"))
                    activeExperts.Add(("DataAnalyst", expertAgents["DataAnalyst"]));
                
                if (ShouldRouteToExpert(routerDecision, "needsTimeInfo"))
                    activeExperts.Add(("TimeExpert", expertAgents["TimeExpert"]));
                
                if (ShouldRouteToExpert(routerDecision, "needsWeather"))
                    activeExperts.Add(("WeatherAdvisor", expertAgents["WeatherAdvisor"]));
                
                if (ShouldRouteToExpert(routerDecision, "needsDeveloper"))
                    activeExperts.Add(("Developer", expertAgents["Developer"]));

                _logger.LogInformation("[Step 2] 激活专家: {Experts}", 
                    string.Join(", ", activeExperts.Select(e => e.Name)));

                // 5. 并行执行所有激活的专家
                var expertTasks = activeExperts.Select(async expert =>
                {
                    _logger.LogInformation("[{Expert}] 开始分析", expert.Name);
                    var response = await expert.Agent.RunAsync(userInput);
                    _logger.LogInformation("[{Expert}] 完成分析", expert.Name);
                    return new { Expert = expert.Name, Response = response.Text ?? "" };
                }).ToList();

                var expertResults = await Task.WhenAll(expertTasks);

                // 6. 汇总专家意见
                var coordinator = new ChatClientAgent(_chatClient,
                    instructions: "你是结果汇总协调员。整合所有专家意见，给出完整、连贯的最终答案。用中文回答。",
                    name: "CoordinatorAgent");

                var summary = string.Join("\n\n---专家意见分隔---\n\n", 
                    expertResults.Select(r => $"[{r.Expert}]:\n{r.Response}"));

                _logger.LogInformation("[Step 3] Coordinator汇总中...");
                var finalResponse = await coordinator.RunAsync(
                    $"原始问题: {userInput}\n\n各专家分析:\n{summary}\n\n请整合以上意见给出完整答案。");

                var result = new WorkflowResult
                {
                    WorkflowId = workflowId,
                    WorkflowType = "ConditionalRouting",
                    Status = "Completed",
                    FinalOutput = finalResponse.Text ?? "无响应",
                    ExecutionTimeMs = (int)(DateTime.Now - startTime).TotalMilliseconds,
                    StepsExecuted = new List<string> { 
                        "Router", 
                        string.Join(", ", activeExperts.Select(e => e.Name)), 
                        "Coordinator" 
                    }
                };

                // 保存完成状态
                if (enableCheckpoint && _checkpointStore != null)
                {
                    await _checkpointStore.SaveWorkflowCompletionAsync(workflowId, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "条件路由执行失败");
                return new WorkflowResult
                {
                    WorkflowId = workflowId ?? "unknown",
                    WorkflowType = "ConditionalRouting",
                    Status = "Failed",
                    ErrorMessage = ex.Message,
                    ExecutionTimeMs = (int)(DateTime.Now - startTime).TotalMilliseconds
                };
            }
        }

        #endregion

        #region 模式2: FanOut/FanIn并行处理（官方ConcurrentAggregationExecutor）

        /// <summary>
        /// 模式2: FanOut/FanIn - 所有专家并行处理，自动聚合
        /// 官方模式: Concurrent Fan-Out/Fan-In Pattern
        /// </summary>
        public async Task<WorkflowResult> RunFanOutFanInAsync(
            string userInput, 
            string? workflowId = null)
        {
            _logger.LogInformation("执行FanOut/FanIn模式, 输入: {Input}", userInput);
            var startTime = DateTime.Now;

            try
            {
                var expertAgents = CreateExpertAgents();
                workflowId ??= $"workflow-{Guid.NewGuid():N}";

                _logger.LogInformation("[FanOut] 4个专家并行执行...");

                // 所有专家并行执行
                var expertTasks = new[]
                {
                    Task.Run(async () => 
                    {
                        var response = await expertAgents["DataAnalyst"].RunAsync(userInput);
                        return new { Expert = "DataAnalyst", Response = response.Text ?? "" };
                    }),
                    Task.Run(async () => 
                    {
                        var response = await expertAgents["TimeExpert"].RunAsync(userInput);
                        return new { Expert = "TimeExpert", Response = response.Text ?? "" };
                    }),
                    Task.Run(async () => 
                    {
                        var response = await expertAgents["WeatherAdvisor"].RunAsync(userInput);
                        return new { Expert = "WeatherAdvisor", Response = response.Text ?? "" };
                    }),
                    Task.Run(async () => 
                    {
                        var response = await expertAgents["Developer"].RunAsync(userInput);
                        return new { Expert = "Developer", Response = response.Text ?? "" };
                    })
                };

                var expertResults = await Task.WhenAll(expertTasks);
                
                _logger.LogInformation("[FanIn] 所有专家完成，开始汇总...");

                // Coordinator汇总
                var coordinator = new ChatClientAgent(_chatClient,
                    instructions: "整合所有专家的并行分析结果，给出综合答案。用中文回答。",
                    name: "CoordinatorAgent");

                var summary = string.Join("\n\n---专家意见---\n\n", 
                    expertResults.Select(r => $"[{r.Expert}]:\n{r.Response}"));

                var finalResponse = await coordinator.RunAsync(
                    $"原始问题: {userInput}\n\n各专家并行分析结果:\n{summary}\n\n请整合给出综合答案。");

                return new WorkflowResult
                {
                    WorkflowId = workflowId,
                    WorkflowType = "FanOutFanIn",
                    Status = "Completed",
                    FinalOutput = finalResponse.Text ?? "无响应",
                    ExecutionTimeMs = (int)(DateTime.Now - startTime).TotalMilliseconds,
                    StepsExecuted = new List<string> { "4 Experts (Parallel)", "Coordinator" }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FanOut/FanIn执行失败");
                return new WorkflowResult
                {
                    WorkflowId = workflowId ?? "unknown",
                    WorkflowType = "FanOutFanIn",
                    Status = "Failed",
                    ErrorMessage = ex.Message,
                    ExecutionTimeMs = (int)(DateTime.Now - startTime).TotalMilliseconds
                };
            }
        }

        #endregion

        #region 模式3: 顺序链（Plan → Execute → Review）

        /// <summary>
        /// 模式3: 顺序链 - Planner → Executor → Reviewer
        /// 官方模式: Sequential Chain Pattern
        /// </summary>
        public async Task<WorkflowResult> RunSequentialChainAsync(
            string userInput, 
            string? workflowId = null)
        {
            _logger.LogInformation("执行顺序链模式, 输入: {Input}", userInput);
            var startTime = DateTime.Now;

            try
            {
                // 创建顺序Agent链
                var planner = new ChatClientAgent(_chatClient,
                    instructions: "你是任务规划专家。分析请求，制定详细执行计划。输出格式：1. 步骤1 2. 步骤2...",
                    name: "PlannerAgent");

                var executor = new ChatClientAgent(_chatClient,
                    instructions: "你是任务执行者。按照前序规划逐步执行，输出执行结果。用中文回答。",
                    tools: [
                        AIFunctionFactory.Create(GetCurrentTime),
                        AIFunctionFactory.Create(Calculate),
                        AIFunctionFactory.Create(GetWeather)
                    ],
                    name: "ExecutorAgent");

                var reviewer = new ChatClientAgent(_chatClient,
                    instructions: "你是质量审核专家。审核执行结果是否完整达成目标，提出改进建议。用中文回答。",
                    name: "ReviewerAgent");

                // 构建顺序链Workflow
                var startExecutor = new ChatForwardingExecutor("Start");
                var workflow = new WorkflowBuilder(startExecutor)
                    .AddEdge(startExecutor, planner)      // Step 1: 规划
                    .AddEdge(planner, executor)           // Step 2: 执行（自动接收planner输出）
                    .AddEdge(executor, reviewer)          // Step 3: 审核（自动接收executor输出）
                    .WithOutputFrom(reviewer)
                    .Build();

                workflowId ??= $"workflow-{Guid.NewGuid():N}";
                var workflowAgent = workflow.AsAgent($"sequential-{workflowId}", "Plan-Execute-Review");
                var response = await workflowAgent.RunAsync(userInput);

                return new WorkflowResult
                {
                    WorkflowId = workflowId,
                    WorkflowType = "SequentialChain",
                    Status = "Completed",
                    FinalOutput = response.Text ?? "无响应",
                    ExecutionTimeMs = (int)(DateTime.Now - startTime).TotalMilliseconds,
                    StepsExecuted = new List<string> { "Planner", "Executor", "Reviewer" }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "顺序链执行失败");
                return new WorkflowResult
                {
                    WorkflowId = workflowId ?? "unknown",
                    WorkflowType = "SequentialChain",
                    Status = "Failed",
                    ErrorMessage = ex.Message,
                    ExecutionTimeMs = (int)(DateTime.Now - startTime).TotalMilliseconds
                };
            }
        }

        #endregion

        #region 模式4: 混合编排（Router → 条件并行 → 汇总）

        /// <summary>
        /// 模式4: 混合编排 - 结合条件路由和并行处理
        /// 官方模式: Hybrid Orchestration (Conditional + Concurrent)
        /// </summary>
        public async Task<WorkflowResult> RunHybridOrchestrationAsync(
            string userInput, 
            string? workflowId = null,
            bool enableCheckpoint = false)
        {
            _logger.LogInformation("执行混合编排模式, 输入: {Input}", userInput);
            var startTime = DateTime.Now;

            try
            {
                // 1. 创建分析器（第一阶段：单独分析复杂度）
                var analyzer = new ChatClientAgent(_chatClient,
                    instructions: @"分析请求复杂度，返回JSON：
{
    ""complexity"": ""simple/medium/complex"",
    ""needsPlanning"": true/false,
    ""recommendedExperts"": [""DataAnalyst"", ""TimeExpert"", etc]
}",
                    name: "AnalyzerAgent");

                // 2. 创建规划器（仅复杂任务需要）
                var planner = new ChatClientAgent(_chatClient,
                    instructions: "制定详细执行计划（仅复杂任务）。用中文回答。",
                    name: "PlannerAgent");

                // 3. 专家池
                var expertAgents = CreateExpertAgents();

                // 4. 聚合器和协调器
                var aggregator = new SmartAggregationExecutor();
                var coordinator = new ChatClientAgent(_chatClient,
                    instructions: "整合分析结果，给出最终答案。用中文回答。",
                    name: "CoordinatorAgent");

                // 5. 构建混合Workflow
                var startExecutor = new ChatForwardingExecutor("Start");
                var workflowBuilder = new WorkflowBuilder(startExecutor)
                    .AddEdge(startExecutor, analyzer);

                // 条件：复杂任务 → Planner
                workflowBuilder.AddEdge<ChatMessage>(analyzer, planner,
                    condition: msg => msg != null && msg.Text?.Contains("\"needsPlanning\": true") == true);

                // 条件：专家路由（Analyzer或Planner输出后触发）
                foreach (var expert in expertAgents)
                {
                    workflowBuilder
                        .AddEdge<ChatMessage>(analyzer, expert.Value,
                            condition: msg => msg != null && msg.Text?.Contains($"\"{expert.Key}\"") == true)
                        .AddEdge<ChatMessage>(planner, expert.Value,
                            condition: msg => msg != null && msg.Text?.Contains(expert.Key) == true)
                        .AddEdge(expert.Value, aggregator);
                }

                // 汇总
                workflowBuilder.AddEdge(aggregator, coordinator);

                var workflow = workflowBuilder
                    .WithOutputFrom(coordinator)
                    .Build();

                // 6. 执行
                workflowId ??= $"workflow-{Guid.NewGuid():N}";
                
                if (enableCheckpoint && _checkpointStore != null)
                {
                    await _checkpointStore.SaveWorkflowStartAsync(workflowId, userInput, "HybridOrchestration");
                }

                var workflowAgent = workflow.AsAgent($"hybrid-{workflowId}", "Hybrid Orchestration");
                var response = await workflowAgent.RunAsync(userInput);

                var result = new WorkflowResult
                {
                    WorkflowId = workflowId,
                    WorkflowType = "HybridOrchestration",
                    Status = "Completed",
                    FinalOutput = response.Text ?? "无响应",
                    ExecutionTimeMs = (int)(DateTime.Now - startTime).TotalMilliseconds,
                    StepsExecuted = new List<string> { "Analyzer", "Conditional Planner", "Experts", "Coordinator" }
                };

                if (enableCheckpoint && _checkpointStore != null)
                {
                    await _checkpointStore.SaveWorkflowCompletionAsync(workflowId, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "混合编排执行失败");
                return new WorkflowResult
                {
                    WorkflowId = workflowId ?? "unknown",
                    WorkflowType = "HybridOrchestration",
                    Status = "Failed",
                    ErrorMessage = ex.Message,
                    ExecutionTimeMs = (int)(DateTime.Now - startTime).TotalMilliseconds
                };
            }
        }

        #endregion

        #region 模式5: 流式输出（实时反馈）

        /// <summary>
        /// 模式5: 流式条件路由 - 实时输出Workflow执行进度
        /// </summary>
        public async IAsyncEnumerable<WorkflowStreamChunk> RunConditionalRoutingStreamingAsync(
            string userInput, 
            string? workflowId = null)
        {
            _logger.LogInformation("执行流式条件路由, 输入: {Input}", userInput);
            workflowId ??= $"workflow-{Guid.NewGuid():N}";

            yield return new WorkflowStreamChunk
            {
                Type = "status",
                Content = $"[工作流启动] ID: {workflowId}\n模式: 条件路由\n"
            };

            var expertAgents = CreateExpertAgents();
            var router = new ChatClientAgent(_chatClient,
                instructions: "分析请求，返回需要的专家列表JSON",
                name: "RouterAgent");

            yield return new WorkflowStreamChunk
            {
                Type = "step",
                Content = "[Step 1] Router分析中...\n"
            };

            // Router阶段
            var routerResponse = await router.RunAsync(userInput);
            var routerDecision = routerResponse.Text ?? "";

            yield return new WorkflowStreamChunk
            {
                Type = "step",
                Content = $"[Router决策]\n{routerDecision}\n\n"
            };

            // 解析需要的专家
            var activeExperts = new List<string>();
            if (ShouldRouteToExpert(routerDecision, "needsDataAnalysis")) activeExperts.Add("DataAnalyst");
            if (ShouldRouteToExpert(routerDecision, "needsTimeInfo")) activeExperts.Add("TimeExpert");
            if (ShouldRouteToExpert(routerDecision, "needsWeather")) activeExperts.Add("WeatherAdvisor");
            if (ShouldRouteToExpert(routerDecision, "needsDeveloper")) activeExperts.Add("Developer");

            yield return new WorkflowStreamChunk
            {
                Type = "status",
                Content = $"[激活专家] {string.Join(", ", activeExperts)}\n\n"
            };

            // 并行执行激活的专家
            var expertTasks = activeExperts.Select(async expertName =>
            {
                var agent = expertAgents[expertName];
                var response = await agent.RunAsync(userInput);
                return new { Expert = expertName, Response = response.Text ?? "" };
            }).ToList();

            var expertResults = await Task.WhenAll(expertTasks);

            foreach (var result in expertResults)
            {
                yield return new WorkflowStreamChunk
                {
                    Type = "agent_output",
                    AgentName = result.Expert,
                    Content = $"[{result.Expert}]\n{result.Response}\n\n"
                };
            }

            // 最终汇总
            yield return new WorkflowStreamChunk
            {
                Type = "step",
                Content = "[最终汇总] Coordinator整合中...\n"
            };

            var coordinator = new ChatClientAgent(_chatClient,
                instructions: "整合专家意见",
                name: "CoordinatorAgent");

            var summary = string.Join("\n\n", expertResults.Select(r => $"{r.Expert}: {r.Response}"));
            var finalResponse = await coordinator.RunAsync(
                $"原始问题: {userInput}\n\n专家分析:\n{summary}\n\n请整合给出完整答案");

            yield return new WorkflowStreamChunk
            {
                Type = "final",
                Content = finalResponse.Text ?? "无响应"
            };

            yield return new WorkflowStreamChunk
            {
                Type = "status",
                Content = "\n[工作流完成]"
            };
        }

        #endregion

        #region 模式6: 完整任务编排系统（官方Workflow + 详细流式日志 + 持久化就绪）

        /// <summary>
        /// 模式6: 完整任务编排系统 - 最复杂场景
        /// 特点：
        /// 1. Master智能分解任务（JSON格式计划）
        /// 2. 官方WorkflowBuilder处理复杂依赖关系
        /// 3. 支持并行/串行混合执行
        /// 4. 详细流式日志（每个Agent执行细节）
        /// 5. A2A交互（依赖任务结果传递）
        /// 6. 持久化就绪（checkpoint接口）
        /// 
        /// 对比示例7: 使用官方Workflow替代手动Task.WhenAll，但保留所有日志能力
        /// </summary>
        public async IAsyncEnumerable<WorkflowStreamChunk> RunCompleteOrchestrationAsync(
            string userInput,
            string? workflowId = null,
            bool enableCheckpoint = false)
        {
            _logger.LogInformation("执行完整任务编排系统, 输入: {Input}", userInput);
            workflowId ??= $"workflow-{Guid.NewGuid():N}";
            var startTime = DateTime.Now;

            yield return new WorkflowStreamChunk
            {
                Type = "header",
                Content = "╔══════════════════════════════════════════════════════════╗\n" +
                         $"║  完整任务编排系统 (官方Workflow + 详细日志)             ║\n" +
                         $"║  工作流ID: {workflowId,-40} ║\n" +
                         $"║  启动时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}                        ║\n" +
                         "╚══════════════════════════════════════════════════════════╝\n\n"
            };

            // ============ 阶段1: Master任务分解 ============
            yield return new WorkflowStreamChunk
            {
                Type = "phase",
                Content = "═══════════════ 阶段1: Master任务分解与策略规划 ═══════════════\n\n"
            };

            if (enableCheckpoint && _checkpointStore != null)
            {
                await _checkpointStore.SaveCheckpointAsync(workflowId, "MasterAnalysis", 
                    JsonSerializer.Serialize(new { phase = "Master分解", input = userInput }));
                yield return new WorkflowStreamChunk
                {
                    Type = "checkpoint",
                    Content = "[Checkpoint] 已保存: MasterAnalysis\n"
                };
            }

            var masterAgent = new ChatClientAgent(_chatClient,
                instructions: @"你是任务分解大师。分析用户请求，输出JSON格式的执行计划：
{
  ""tasks"": [
    {
      ""id"": ""task1"",
      ""description"": ""任务描述"",
      ""assignTo"": ""agent类型: DataAnalyst/TimeExpert/WeatherAdvisor/Developer"",
      ""priority"": 1-10,
      ""estimatedDurationSeconds"": 预估秒数,
      ""dependencies"": [""依赖的taskId""]
    }
  ],
  ""executionMode"": ""parallel或sequential或hybrid"",
  ""reasoning"": ""策略解释"",
  ""complexity"": ""simple/medium/complex""
}

规则：
- 无依赖的任务推荐parallel（并行）
- 有依赖的任务必须sequential（串行）
- 混合场景用hybrid（部分并行+串行）
- 任务需明确分配给合适的Agent
- priority越高越重要",
                name: "MasterAgent");

            yield return new WorkflowStreamChunk
            {
                Type = "agent_start",
                AgentName = "MasterAgent",
                Content = "[Master Agent] 开始分析任务...\n"
            };

            var planResponse = await masterAgent.RunAsync(
                $"请为以下请求制定详细执行计划：{userInput}");
            var planJson = planResponse.Text ?? "";

            yield return new WorkflowStreamChunk
            {
                Type = "agent_output",
                AgentName = "MasterAgent",
                Content = $"[Master规划结果]\n{planJson}\n\n"
            };

            // ============ 阶段2: 解析执行计划 ============
            yield return new WorkflowStreamChunk
            {
                Type = "phase",
                Content = "═══════════════ 阶段2: 解析执行计划 ═══════════════\n\n"
            };

            var plan = ParseExecutionPlan(planJson);
            if (plan == null || plan.Tasks.Count == 0)
            {
                yield return new WorkflowStreamChunk
                {
                    Type = "error",
                    Content = "[错误] 无法解析执行计划，回退到简单模式\n"
                };
                
                // 回退到简单单Agent执行
                var fallbackAgent = new ChatClientAgent(_chatClient,
                    instructions: "你是通用助手，回答用户问题。用中文回答。",
                    name: "FallbackAgent");
                var fallbackResponse = await fallbackAgent.RunAsync(userInput);
                
                yield return new WorkflowStreamChunk
                {
                    Type = "final",
                    Content = fallbackResponse.Text ?? "无响应"
                };
                yield break;
            }

            yield return new WorkflowStreamChunk
            {
                Type = "plan_summary",
                Content = $"[计划摘要]\n" +
                         $"  执行模式: {plan.ExecutionMode}\n" +
                         $"  任务数量: {plan.Tasks.Count}\n" +
                         $"  复杂度: {plan.Complexity}\n" +
                         $"  策略原因: {plan.Reasoning}\n\n"
            };

            yield return new WorkflowStreamChunk
            {
                Type = "task_list",
                Content = "[任务列表]\n" + string.Join("\n", plan.Tasks.Select(t =>
                    $"  [{t.Id}] {t.Description}\n" +
                    $"      分配给: {t.AssignTo} | 优先级: {t.Priority} | " +
                    $"预估: {t.EstimatedDurationSeconds}秒" +
                    (t.Dependencies.Count > 0 ? $" | 依赖: {string.Join(", ", t.Dependencies)}" : ""))) + "\n\n"
            };

            // ============ 阶段3: 创建Agent池和准备执行 ============
            yield return new WorkflowStreamChunk
            {
                Type = "phase",
                Content = "═══════════════ 阶段3: 初始化Agent池 ═══════════════\n\n"
            };

            var agentPool = CreateAgentPool();
            
            yield return new WorkflowStreamChunk
            {
                Type = "status",
                Content = $"[Agent池] 已创建 {agentPool.Count} 个专家Agent: " +
                         $"{string.Join(", ", agentPool.Keys)}\n\n"
            };

            // ============ 阶段4: 执行任务（核心）============
            yield return new WorkflowStreamChunk
            {
                Type = "phase",
                Content = "═══════════════ 阶段4: 任务执行（详细日志）═══════════════\n\n"
            };

            var taskResults = new Dictionary<string, TaskExecutionResult>();

            if (plan.ExecutionMode == "parallel")
            {
                yield return new WorkflowStreamChunk
                {
                    Type = "execution_mode",
                    Content = "[执行策略] 🚀 全并行模式 - 所有任务同时启动\n\n"
                };

                await foreach (var chunk in ExecuteParallelTasksWithDetailedLoggingAsync(
                    plan.Tasks, agentPool, taskResults, userInput, workflowId, enableCheckpoint))
                {
                    yield return chunk;
                }
            }
            else if (plan.ExecutionMode == "sequential")
            {
                yield return new WorkflowStreamChunk
                {
                    Type = "execution_mode",
                    Content = "[执行策略] 📋 全串行模式 - 任务依次执行（支持A2A交互）\n\n"
                };

                await foreach (var chunk in ExecuteSequentialTasksWithDetailedLoggingAsync(
                    plan.Tasks, agentPool, taskResults, userInput, workflowId, enableCheckpoint))
                {
                    yield return chunk;
                }
            }
            else // hybrid
            {
                yield return new WorkflowStreamChunk
                {
                    Type = "execution_mode",
                    Content = "[执行策略] 🔀 混合模式 - 根据依赖关系动态并行/串行\n\n"
                };

                await foreach (var chunk in ExecuteHybridTasksWithDetailedLoggingAsync(
                    plan.Tasks, agentPool, taskResults, userInput, workflowId, enableCheckpoint))
                {
                    yield return chunk;
                }
            }

            // ============ 阶段5: 结果汇总 ============
            yield return new WorkflowStreamChunk
            {
                Type = "phase",
                Content = "\n═══════════════ 阶段5: 结果汇总与输出 ═══════════════\n\n"
            };

            if (enableCheckpoint && _checkpointStore != null)
            {
                await _checkpointStore.SaveCheckpointAsync(workflowId, "CoordinatorSummary",
                    JsonSerializer.Serialize(new { phase = "结果汇总", taskCount = taskResults.Count }));
                yield return new WorkflowStreamChunk
                {
                    Type = "checkpoint",
                    Content = "[Checkpoint] 已保存: CoordinatorSummary\n"
                };
            }

            var coordinator = new ChatClientAgent(_chatClient,
                instructions: "你是结果汇总协调员。整合所有子任务结果，给出完整、连贯、结构化的最终答案。用中文回答。",
                name: "CoordinatorAgent");

            yield return new WorkflowStreamChunk
            {
                Type = "agent_start",
                AgentName = "CoordinatorAgent",
                Content = "[Coordinator] 开始整合所有任务结果...\n"
            };

            var summaryInput = $"原始用户请求: {userInput}\n\n" +
                              $"执行模式: {plan.ExecutionMode}\n" +
                              $"任务总数: {taskResults.Count}\n\n" +
                              "各任务执行结果:\n" +
                              string.Join("\n\n", taskResults.Select(r =>
                                  $"【任务{r.Key}】 {r.Value.TaskDescription}\n" +
                                  $"  执行Agent: {r.Value.AssignedAgent}\n" +
                                  $"  执行状态: {(r.Value.Success ? "✓ 成功" : "✗ 失败")}\n" +
                                  $"  执行时长: {r.Value.ExecutionTimeMs}ms\n" +
                                  $"  结果内容:\n{r.Value.Result}\n" +
                                  new string('-', 60)));

            // 流式输出Coordinator的汇总过程
            var coordinatorOutput = new System.Text.StringBuilder();
            await foreach (var update in coordinator.RunStreamingAsync(summaryInput))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    coordinatorOutput.Append(update.Text);
                    yield return new WorkflowStreamChunk
                    {
                        Type = "coordinator_stream",
                        AgentName = "CoordinatorAgent",
                        Content = update.Text
                    };
                }
            }

            var finalOutput = coordinatorOutput.ToString();

            // ============ 阶段6: 执行摘要 ============
            var totalTime = (int)(DateTime.Now - startTime).TotalMilliseconds;

            yield return new WorkflowStreamChunk
            {
                Type = "summary",
                Content = $"\n\n╔══════════════════════════════════════════════════════════╗\n" +
                         $"║  执行摘要                                                ║\n" +
                         $"╠══════════════════════════════════════════════════════════╣\n" +
                         $"║  工作流ID: {workflowId,-40} ║\n" +
                         $"║  执行模式: {plan.ExecutionMode,-44} ║\n" +
                         $"║  任务数量: {plan.Tasks.Count,-44} ║\n" +
                         $"║  成功任务: {taskResults.Count(r => r.Value.Success),-44} ║\n" +
                         $"║  失败任务: {taskResults.Count(r => !r.Value.Success),-44} ║\n" +
                         $"║  总耗时: {totalTime}ms{new string(' ', 48 - totalTime.ToString().Length)} ║\n" +
                         $"║  完成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}                        ║\n" +
                         $"╚══════════════════════════════════════════════════════════╝\n"
            };

            // 保存最终结果
            if (enableCheckpoint && _checkpointStore != null)
            {
                var result = new WorkflowResult
                {
                    WorkflowId = workflowId,
                    WorkflowType = "CompleteOrchestration",
                    Status = "Completed",
                    FinalOutput = finalOutput,
                    ExecutionTimeMs = totalTime,
                    StepsExecuted = new List<string> { 
                        "Master", 
                        string.Join(", ", plan.Tasks.Select(t => t.Id)), 
                        "Coordinator" 
                    }
                };

                await _checkpointStore.SaveWorkflowCompletionAsync(workflowId, result);
                
                yield return new WorkflowStreamChunk
                {
                    Type = "checkpoint",
                    Content = "[Checkpoint] 已保存最终结果\n"
                };
            }

            yield return new WorkflowStreamChunk
            {
                Type = "final",
                Content = finalOutput
            };

            yield return new WorkflowStreamChunk
            {
                Type = "complete",
                Content = "\n✅ [工作流完成]\n"
            };
        }

        #endregion

        #region 详细日志执行方法（模式6核心）

        /// <summary>
        /// 并行执行任务（带详细流式日志）
        /// </summary>
        private async IAsyncEnumerable<WorkflowStreamChunk> ExecuteParallelTasksWithDetailedLoggingAsync(
            List<TaskDefinition> tasks,
            Dictionary<string, AIAgent> agentPool,
            Dictionary<string, TaskExecutionResult> results,
            string originalInput,
            string workflowId,
            bool enableCheckpoint)
        {
            yield return new WorkflowStreamChunk
            {
                Type = "parallel_start",
                Content = $"[并行启动] {tasks.Count} 个任务同时开始执行...\n\n"
            };

            var parallelTasks = tasks.Select(task => Task.Run(async () =>
            {
                var taskStartTime = DateTime.Now;
                var agent = agentPool.GetValueOrDefault(task.AssignTo);
                
                if (agent == null)
                {
                    return new TaskExecutionResult
                    {
                        TaskId = task.Id,
                        TaskDescription = task.Description,
                        AssignedAgent = task.AssignTo,
                        Result = $"错误: 未找到Agent类型 {task.AssignTo}",
                        Success = false,
                        ExecutionTimeMs = 0
                    };
                }

                // 执行Agent（收集完整结果）
                var taskResult = new System.Text.StringBuilder();
                await foreach (var update in agent.RunStreamingAsync(task.Description))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        taskResult.Append(update.Text);
                    }
                }

                var executionTime = (int)(DateTime.Now - taskStartTime).TotalMilliseconds;

                return new TaskExecutionResult
                {
                    TaskId = task.Id,
                    TaskDescription = task.Description,
                    AssignedAgent = task.AssignTo,
                    Result = taskResult.ToString() ?? "无响应",
                    Success = true,
                    ExecutionTimeMs = executionTime
                };
            })).ToArray();

            // 等待所有任务完成
            var completedResults = await Task.WhenAll(parallelTasks);

            foreach (var result in completedResults)
            {
                results[result.TaskId] = result;

                yield return new WorkflowStreamChunk
                {
                    Type = "task_complete",
                    AgentName = result.AssignedAgent,
                    Content = $"✓ [任务完成] {result.TaskId}\n" +
                             $"  Agent: {result.AssignedAgent}\n" +
                             $"  描述: {result.TaskDescription}\n" +
                             $"  耗时: {result.ExecutionTimeMs}ms\n" +
                             $"  状态: {(result.Success ? "成功" : "失败")}\n" +
                             $"  结果:\n{IndentText(result.Result, 4)}\n\n"
                };

                if (enableCheckpoint && _checkpointStore != null)
                {
                    await _checkpointStore.SaveCheckpointAsync(workflowId, $"Task_{result.TaskId}",
                        JsonSerializer.Serialize(new { taskId = result.TaskId, result = result.Result }));
                    
                    yield return new WorkflowStreamChunk
                    {
                        Type = "checkpoint",
                        Content = $"[Checkpoint] 已保存任务 {result.TaskId}\n"
                    };
                }
            }

            yield return new WorkflowStreamChunk
            {
                Type = "parallel_complete",
                Content = $"\n[并行完成] 所有 {tasks.Count} 个任务已完成\n\n"
            };
        }

        /// <summary>
        /// 串行执行任务（带详细流式日志 + A2A交互）
        /// </summary>
        private async IAsyncEnumerable<WorkflowStreamChunk> ExecuteSequentialTasksWithDetailedLoggingAsync(
            List<TaskDefinition> tasks,
            Dictionary<string, AIAgent> agentPool,
            Dictionary<string, TaskExecutionResult> results,
            string originalInput,
            string workflowId,
            bool enableCheckpoint)
        {
            yield return new WorkflowStreamChunk
            {
                Type = "sequential_start",
                Content = $"[串行启动] {tasks.Count} 个任务将依次执行（支持A2A交互）...\n\n"
            };

            foreach (var (task, index) in tasks.Select((t, i) => (t, i)))
            {
                var taskStartTime = DateTime.Now;

                yield return new WorkflowStreamChunk
                {
                    Type = "task_start",
                    AgentName = task.AssignTo,
                    Content = $"→ [任务 {index + 1}/{tasks.Count}] {task.Id} 开始执行\n" +
                             $"  描述: {task.Description}\n" +
                             $"  Agent: {task.AssignTo}\n" +
                             $"  优先级: {task.Priority}\n" +
                             (task.Dependencies.Count > 0 
                                 ? $"  依赖任务: {string.Join(", ", task.Dependencies)}\n" 
                                 : "") +
                             "\n"
                };

                var agent = agentPool.GetValueOrDefault(task.AssignTo);
                if (agent == null)
                {
                    yield return new WorkflowStreamChunk
                    {
                        Type = "error",
                        Content = $"  ✗ 错误: 未找到Agent {task.AssignTo}\n\n"
                    };
                    continue;
                }

                // 构建上下文（A2A交互：包含依赖任务结果）
                var context = task.Description;
                if (task.Dependencies.Count > 0)
                {
                    yield return new WorkflowStreamChunk
                    {
                        Type = "a2a_interaction",
                        Content = $"  [A2A交互] 加载依赖任务结果: {string.Join(", ", task.Dependencies)}\n"
                    };

                    var dependencyResults = task.Dependencies
                        .Where(depId => results.ContainsKey(depId))
                        .Select(depId => $"【前置任务 {depId}】\n{results[depId].Result}")
                        .ToList();

                    if (dependencyResults.Any())
                    {
                        context = $"原始用户请求: {originalInput}\n\n" +
                                 $"前置任务结果:\n{string.Join("\n\n", dependencyResults)}\n\n" +
                                 $"你的任务: {task.Description}";
                        
                        yield return new WorkflowStreamChunk
                        {
                            Type = "a2a_context",
                            Content = $"  [上下文] 已加载 {dependencyResults.Count} 个依赖任务的结果\n"
                        };
                    }
                }

                // 流式执行Agent（实时输出）
                yield return new WorkflowStreamChunk
                {
                    Type = "agent_stream_start",
                    AgentName = task.AssignTo,
                    Content = $"  [{task.AssignTo}] 开始输出:\n  ┌{'─'.Repeat(56)}┐\n"
                };

                var taskResult = new System.Text.StringBuilder();
                await foreach (var update in agent.RunStreamingAsync(context))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        taskResult.Append(update.Text);
                        
                        // 实时流式输出Agent的思考过程
                        yield return new WorkflowStreamChunk
                        {
                            Type = "agent_stream_chunk",
                            AgentName = task.AssignTo,
                            Content = $"  │ {update.Text.Replace("\n", "\n  │ ")}"
                        };
                    }
                }

                yield return new WorkflowStreamChunk
                {
                    Type = "agent_stream_end",
                    AgentName = task.AssignTo,
                    Content = $"\n  └{'─'.Repeat(56)}┘\n"
                };

                var executionTime = (int)(DateTime.Now - taskStartTime).TotalMilliseconds;
                var finalResult = taskResult.ToString();

                var executionResult = new TaskExecutionResult
                {
                    TaskId = task.Id,
                    TaskDescription = task.Description,
                    AssignedAgent = task.AssignTo,
                    Result = finalResult ?? "无响应",
                    Success = true,
                    ExecutionTimeMs = executionTime
                };

                results[task.Id] = executionResult;

                yield return new WorkflowStreamChunk
                {
                    Type = "task_complete",
                    AgentName = task.AssignTo,
                    Content = $"  ✓ [任务完成] 耗时: {executionTime}ms\n\n"
                };

                if (enableCheckpoint && _checkpointStore != null)
                {
                    await _checkpointStore.SaveCheckpointAsync(workflowId, $"Task_{task.Id}",
                        JsonSerializer.Serialize(new { taskId = task.Id, result = finalResult }));
                    
                    yield return new WorkflowStreamChunk
                    {
                        Type = "checkpoint",
                        Content = $"  [Checkpoint] 已保存任务 {task.Id}\n"
                    };
                }
            }

            yield return new WorkflowStreamChunk
            {
                Type = "sequential_complete",
                Content = $"\n[串行完成] 所有 {tasks.Count} 个任务已按序完成\n\n"
            };
        }

        /// <summary>
        /// 混合执行任务（根据依赖关系动态并行/串行）
        /// </summary>
        private async IAsyncEnumerable<WorkflowStreamChunk> ExecuteHybridTasksWithDetailedLoggingAsync(
            List<TaskDefinition> tasks,
            Dictionary<string, AIAgent> agentPool,
            Dictionary<string, TaskExecutionResult> results,
            string originalInput,
            string workflowId,
            bool enableCheckpoint)
        {
            yield return new WorkflowStreamChunk
            {
                Type = "hybrid_start",
                Content = $"[混合模式] 分析依赖关系，构建执行图...\n\n"
            };

            // 构建依赖图
            var remainingTasks = new HashSet<string>(tasks.Select(t => t.Id));
            var executedTasks = new HashSet<string>();
            var wave = 0;

            while (remainingTasks.Count > 0)
            {
                wave++;
                
                // 找出当前可执行的任务（依赖已满足）
                var readyTasks = tasks
                    .Where(t => remainingTasks.Contains(t.Id) &&
                               t.Dependencies.All(dep => executedTasks.Contains(dep)))
                    .ToList();

                if (readyTasks.Count == 0)
                {
                    yield return new WorkflowStreamChunk
                    {
                        Type = "error",
                        Content = "[错误] 检测到循环依赖或未满足的依赖，剩余任务无法执行\n"
                    };
                    break;
                }

                yield return new WorkflowStreamChunk
                {
                    Type = "wave_start",
                    Content = $"╔═══ Wave {wave} ═══╗\n" +
                             $"  可执行任务数: {readyTasks.Count}\n" +
                             $"  任务列表: {string.Join(", ", readyTasks.Select(t => t.Id))}\n" +
                             (readyTasks.Count > 1 ? "  执行方式: 🚀 并行\n" : "  执行方式: 📋 单任务\n") +
                             "\n"
                };

                // 并行执行这一波的所有任务
                var waveTasks = readyTasks.Select(task => Task.Run(async () =>
                {
                    var taskStartTime = DateTime.Now;
                    var agent = agentPool.GetValueOrDefault(task.AssignTo);

                    if (agent == null)
                    {
                        return new TaskExecutionResult
                        {
                            TaskId = task.Id,
                            TaskDescription = task.Description,
                            AssignedAgent = task.AssignTo,
                            Result = $"错误: Agent {task.AssignTo} 不存在",
                            Success = false,
                            ExecutionTimeMs = 0
                        };
                    }

                    // 构建上下文（包含依赖任务结果）
                    var context = task.Description;
                    if (task.Dependencies.Count > 0)
                    {
                        var dependencyResults = task.Dependencies
                            .Where(depId => results.ContainsKey(depId))
                            .Select(depId => $"【{depId}】\n{results[depId].Result}")
                            .ToList();

                        if (dependencyResults.Any())
                        {
                            context = $"原始请求: {originalInput}\n\n" +
                                     $"依赖任务结果:\n{string.Join("\n\n", dependencyResults)}\n\n" +
                                     $"当前任务: {task.Description}";
                        }
                    }

                    // 执行
                    var taskResult = new System.Text.StringBuilder();
                    await foreach (var update in agent.RunStreamingAsync(context))
                    {
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            taskResult.Append(update.Text);
                        }
                    }

                    var executionTime = (int)(DateTime.Now - taskStartTime).TotalMilliseconds;

                    return new TaskExecutionResult
                    {
                        TaskId = task.Id,
                        TaskDescription = task.Description,
                        AssignedAgent = task.AssignTo,
                        Result = taskResult.ToString() ?? "无响应",
                        Success = true,
                        ExecutionTimeMs = executionTime
                    };
                })).ToArray();

                var waveResults = await Task.WhenAll(waveTasks);

                foreach (var result in waveResults)
                {
                    results[result.TaskId] = result;
                    executedTasks.Add(result.TaskId);
                    remainingTasks.Remove(result.TaskId);

                    yield return new WorkflowStreamChunk
                    {
                        Type = "task_complete",
                        AgentName = result.AssignedAgent,
                        Content = $"  ✓ [{result.TaskId}] {result.AssignedAgent} 完成 ({result.ExecutionTimeMs}ms)\n" +
                                 $"    {result.TaskDescription}\n" +
                                 $"    结果: {TruncateText(result.Result, 80)}\n\n"
                    };

                    if (enableCheckpoint && _checkpointStore != null)
                    {
                        await _checkpointStore.SaveCheckpointAsync(workflowId, $"Task_{result.TaskId}",
                            JsonSerializer.Serialize(new { taskId = result.TaskId, wave = wave }));
                    }
                }

                yield return new WorkflowStreamChunk
                {
                    Type = "wave_complete",
                    Content = $"╚═══ Wave {wave} 完成 ═══╝\n" +
                             $"  已执行: {executedTasks.Count}/{tasks.Count}\n" +
                             $"  剩余: {remainingTasks.Count}\n\n"
                };
            }

            yield return new WorkflowStreamChunk
            {
                Type = "hybrid_complete",
                Content = $"[混合模式完成] 总计 {wave} 个Wave，{tasks.Count} 个任务全部完成\n\n"
            };
        }

        /// <summary>
        /// 解析执行计划（从Master Agent的JSON输出）
        /// </summary>
        private ExecutionPlan ParseExecutionPlan(string planJson)
        {
            try
            {
                // 简单正则提取（生产环境应使用System.Text.Json）
                var plan = new ExecutionPlan();

                // 提取executionMode
                var modeMatch = System.Text.RegularExpressions.Regex.Match(
                    planJson, @"""executionMode""\s*:\s*""(parallel|sequential|hybrid)""");
                plan.ExecutionMode = modeMatch.Success ? modeMatch.Groups[1].Value : "sequential";

                // 提取reasoning
                var reasonMatch = System.Text.RegularExpressions.Regex.Match(
                    planJson, @"""reasoning""\s*:\s*""([^""]+)""");
                plan.Reasoning = reasonMatch.Success ? reasonMatch.Groups[1].Value : "未提供";

                // 提取complexity
                var complexityMatch = System.Text.RegularExpressions.Regex.Match(
                    planJson, @"""complexity""\s*:\s*""(simple|medium|complex)""");
                plan.Complexity = complexityMatch.Success ? complexityMatch.Groups[1].Value : "medium";

                // 提取tasks
                var taskMatches = System.Text.RegularExpressions.Regex.Matches(
                    planJson,
                    @"""id""\s*:\s*""([^""]+)""[^}]*""description""\s*:\s*""([^""]+)""[^}]*""assignTo""\s*:\s*""([^""]+)""");

                foreach (System.Text.RegularExpressions.Match match in taskMatches)
                {
                    var task = new TaskDefinition
                    {
                        Id = match.Groups[1].Value,
                        Description = match.Groups[2].Value,
                        AssignTo = match.Groups[3].Value,
                        Dependencies = new List<string>()
                    };

                    // 尝试提取priority
                    var priorityMatch = System.Text.RegularExpressions.Regex.Match(
                        planJson, $@"""id""\s*:\s*""{task.Id}""[^}}]*""priority""\s*:\s*(\d+)");
                    if (priorityMatch.Success)
                    {
                        task.Priority = int.Parse(priorityMatch.Groups[1].Value);
                    }

                    // 尝试提取dependencies（简化处理）
                    var depsMatch = System.Text.RegularExpressions.Regex.Match(
                        planJson, $@"""id""\s*:\s*""{task.Id}""[^}}]*""dependencies""\s*:\s*\[(.*?)\]");
                    if (depsMatch.Success)
                    {
                        var depsStr = depsMatch.Groups[1].Value;
                        var deps = System.Text.RegularExpressions.Regex.Matches(depsStr, @"""([^""]+)""");
                        task.Dependencies = deps.Select(m => m.Groups[1].Value).ToList();
                    }

                    plan.Tasks.Add(task);
                }

                return plan.Tasks.Count > 0 ? plan : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析执行计划失败");
                return null;
            }
        }

        /// <summary>
        /// 文本缩进（用于格式化输出）
        /// </summary>
        private static string IndentText(string text, int spaces)
        {
            var indent = new string(' ', spaces);
            return indent + text.Replace("\n", $"\n{indent}");
        }

        /// <summary>
        /// 文本截断（用于简化显示）
        /// </summary>
        private static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "...";
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 创建专家Agent池
        /// </summary>
        private Dictionary<string, AIAgent> CreateExpertAgents()
        {
            return new Dictionary<string, AIAgent>
            {
                ["DataAnalyst"] = new ChatClientAgent(
                    _chatClient,
                    instructions: "你是数据分析专家。进行数据分析、统计计算，给出数据驱动的见解。用中文详细说明。",
                    tools: [AIFunctionFactory.Create(Calculate)],
                    name: "DataAnalyst"),

                ["TimeExpert"] = new ChatClientAgent(
                    _chatClient,
                    instructions: "你是时间管理专家。处理时间相关问题，提供时间规划建议。用中文回答。",
                    tools: [AIFunctionFactory.Create(GetCurrentTime)],
                    name: "TimeExpert"),

                ["WeatherAdvisor"] = new ChatClientAgent(
                    _chatClient,
                    instructions: "你是天气顾问。查询天气信息并提供出行建议。用中文回答。",
                    tools: [AIFunctionFactory.Create(GetWeather)],
                    name: "WeatherAdvisor"),

                ["Developer"] = new ChatClientAgent(
                    _chatClient,
                    instructions: "你是开发专家。提供技术方案、代码示例和架构建议。用中文回答。",
                    name: "Developer")
            };
        }

        /// <summary>
        /// 创建Agent池（用于模式6）
        /// 与CreateExpertAgents相同，提供别名以便代码复用
        /// </summary>
        private Dictionary<string, AIAgent> CreateAgentPool()
        {
            return CreateExpertAgents();
        }

        /// <summary>
        /// 判断是否应该路由到特定专家
        /// </summary>
        private bool ShouldRouteToExpert(string? routerOutput, string expertField)
        {
            if (string.IsNullOrEmpty(routerOutput))
                return false;

            try
            {
                // 尝试解析JSON
                var doc = JsonDocument.Parse(routerOutput);
                if (doc.RootElement.TryGetProperty(expertField, out var value))
                {
                    return value.GetBoolean();
                }
            }
            catch
            {
                // JSON解析失败，使用字符串匹配作为后备
                return routerOutput.Contains($"\"{expertField}\": true", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        #endregion

        #region 工具函数

        [Description("获取当前系统时间")]
        private static string GetCurrentTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss dddd");

        [Description("执行数学表达式计算")]
        private static string Calculate([Description("数学表达式，如 2+3*4")] string expression)
        {
            try
            {
                var dataTable = new System.Data.DataTable();
                var result = dataTable.Compute(expression, "");
                return $"计算结果: {result}";
            }
            catch (Exception ex)
            {
                return $"计算错误: {ex.Message}";
            }
        }

        [Description("查询指定城市的天气")]
        private static string GetWeather([Description("城市名称")] string city)
        {
            // 模拟天气查询
            var random = new Random();
            var temps = new[] { 18, 22, 25, 28, 32 };
            var conditions = new[] { "晴", "多云", "阴", "小雨" };
            var humidity = random.Next(40, 90);
            return $"{city}天气: {conditions[random.Next(conditions.Length)]}, " +
                   $"温度: {temps[random.Next(temps.Length)]}°C, " +
                   $"湿度: {humidity}%";
        }

        #endregion
    }

    #region 自定义Executor

    /// <summary>
    /// 智能聚合执行器（替代手动Task.WhenAll）
    /// 参考官方ConcurrentAggregationExecutor
    /// </summary>
    public sealed class SmartAggregationExecutor : 
        Executor<ChatMessage>, 
        IResettableExecutor
    {
        private readonly List<ChatMessage> _messages = new();
        private readonly int _minimumMessages = 1; // 至少收集1条消息

        public SmartAggregationExecutor() : base("SmartAggregationExecutor")
        {
        }

        public override async ValueTask HandleAsync(
            ChatMessage message, 
            IWorkflowContext context, 
            CancellationToken cancellationToken = default)
        {
            _messages.Add(message);

            // 当收集到足够消息时，聚合并输出
            if (_messages.Count >= _minimumMessages)
            {
                var aggregated = string.Join("\n\n---专家意见分隔---\n\n", 
                    _messages.Select((m, i) => $"专家{i + 1}:\n{m.Text}"));
                
                var outputMessage = new ChatMessage(ChatRole.Assistant, aggregated);
                await context.YieldOutputAsync(outputMessage, cancellationToken);
                
                // 清空已聚合的消息
                _messages.Clear();
            }
        }

        public ValueTask ResetAsync()
        {
            _messages.Clear();
            return default;
        }
    }

    #endregion
}

/// <summary>
/// 字符串扩展方法
/// </summary>
internal static class StringExtensions
{
    public static string Repeat(this char c, int count)
    {
        return new string(c, count);
    }
}
