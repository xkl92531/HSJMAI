// Copyright (c) Microsoft. All rights reserved.
// 基于官方示例: https://github.com/microsoft/agent-framework/tree/main/dotnet/samples

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MAF.DEMO.AppLayer.AI.Models;

namespace MAF.DEMO.AppLayer.AI
{
    /// <summary>
    /// 基于Microsoft Agent Framework官方示例的智能体Demo
    /// 参考: https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted
    /// </summary>
    public class AgentDemoApp
    {
        private readonly IChatClient _chatClient;
        private readonly ILogger<AgentDemoApp> _logger;

        public AgentDemoApp(IChatClient chatClient, ILogger<AgentDemoApp> logger)
        {
            _chatClient = chatClient;
            _logger = logger;
        }

        #region 示例1: 简单Agent (参考Agent_Step01_Running)
        /// <summary>
        /// 示例1: 创建简单的单智能体
        /// 参考: Agent_Step01_Running/Program.cs
        /// </summary>
        public async Task<string> RunSimpleAgentAsync(string userInput)
        {
            _logger.LogInformation("执行简单Agent示例, 输入: {Input}", userInput);

            // 官方写法: 使用ChatClientAgent构造函数
            AIAgent agent = new ChatClientAgent(
                _chatClient,
                instructions: "你是一个有帮助的AI助手，请用中文回答问题。",
                name: "SimpleAgent");

            var response = await agent.RunAsync(userInput);
            return response.Text ?? "无响应";
        }
        #endregion

        #region 示例2: 带工具的Agent (参考Agent_Step03_UsingFunctionTools)
        /// <summary>
        /// 示例2: 创建带工具的智能体（流式输出）
        /// 参考: Agent_Step03_UsingFunctionTools/Program.cs
        /// </summary>
        public async IAsyncEnumerable<string> RunAgentWithToolsStreamingAsync(string userInput)
        {
            _logger.LogInformation("执行带工具的Agent示例(流式), 输入: {Input}", userInput);

            AIAgent agent = new ChatClientAgent(
                _chatClient,
                instructions: @"你是一个有帮助的AI助手。你可以使用以下工具：
1. GetCurrentTime - 获取当前系统时间
2. Calculate - 执行数学计算
3. GetWeather - 查询城市天气

当用户询问时间、计算或天气时，请先调用对应工具获取信息，然后用中文回答。",
                tools: [
                    AIFunctionFactory.Create(GetCurrentTime),
                    AIFunctionFactory.Create(Calculate),
                    AIFunctionFactory.Create(GetWeather)
                ]);

            var fullResponse = new System.Text.StringBuilder();
            
            // 流式接收
            await foreach (var update in agent.RunStreamingAsync(userInput))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    fullResponse.Append(update.Text);
                    yield return update.Text;
                }
            }

           
        }


        [Description("获取当前系统时间")]
        private static string GetCurrentTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

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
            var random = new Random();
            var temps = new[] { 18, 22, 25, 28, 32 };
            var conditions = new[] { "晴", "多云", "阴", "小雨" };
            return $"{city}天气: {conditions[random.Next(conditions.Length)]}, 温度: {temps[random.Next(temps.Length)]}°C";
        }
        #endregion

        #region 示例3: 多智能体工作流 (参考Workflows/Agents/WorkflowAsAnAgent)
        /// <summary>
        /// 示例3: 多智能体顺序工作流
        /// 参考: WorkflowAsAnAgent/Program.cs + WorkflowFactory.cs
        /// 模式: Writer -> Editor
        /// </summary>
        public async Task<string> RunSequentialWorkflowAsync(string userInput)
        {
            _logger.LogInformation("执行顺序工作流示例, 输入: {Input}", userInput);

            // 创建Agent (官方写法)
            var writer = new ChatClientAgent(
                _chatClient,
                instructions: "你是一个创意写作者，请根据用户要求创作内容。用中文回答。",
                name: "WriterAgent");

            var editor = new ChatClientAgent(
                _chatClient,
                instructions: "你是一个编辑，负责改进和润色前一位写作者的内容，使其更加精炼。用中文回答。",
                name: "EditorAgent");

            // 使用WorkflowBuilder构建工作流 (官方写法)
            var startExecutor = new ChatForwardingExecutor("Start");
            var workflow = new WorkflowBuilder(startExecutor)
                .AddEdge(startExecutor, writer)
                .AddEdge(writer, editor)
                .WithOutputFrom(editor)
                .Build();

            // 将工作流转为Agent执行
            var workflowAgent = workflow.AsAgent("workflow-agent", "Sequential Workflow");
            var response = await workflowAgent.RunAsync(userInput);
            return response.Text ?? "无响应";
        }
        #endregion

        #region 示例4: 并行多智能体工作流（流式+工具）
        /// <summary>
        /// 示例4: 并行多智能体工作流 (FanOut/FanIn) - 流式输出，子Agent带工具
        /// 参考: WorkflowAsAnAgent/WorkflowFactory.cs
        /// </summary>
        public async IAsyncEnumerable<string> RunParallelWorkflowStreamingAsync(string userInput)
        {
            _logger.LogInformation("执行并行工作流示例(流式), 输入: {Input}", userInput);

            yield return "[工作流启动] 创建多个专家Agent并行处理...\n\n";

            // 数据分析Agent（带计算工具）
            var dataAnalyst = new ChatClientAgent(
                _chatClient,
                instructions: "你是数据分析专家。分析问题中的数据，进行必要计算，用中文详细解释。",
                tools: [AIFunctionFactory.Create(Calculate)],
                name: "DataAnalyst");

            // 时间管理Agent（带时间工具）
            var timeExpert = new ChatClientAgent(
                _chatClient,
                instructions: "你是时间管理专家。关注问题中的时间因素，提供时间相关见解，用中文回答。",
                tools: [AIFunctionFactory.Create(GetCurrentTime)],
                name: "TimeExpert");

            // 天气顾问Agent（带天气工具）
            var weatherAdvisor = new ChatClientAgent(
                _chatClient,
                instructions: "你是天气顾问。如果问题涉及地点，查询天气并提供建议，用中文回答。",
                tools: [AIFunctionFactory.Create(GetWeather)],
                name: "WeatherAdvisor");

            yield return "[并行执行] 3个专家Agent开始工作：\n";
            yield return "  - 数据分析专家 (带计算工具)\n";
            yield return "  - 时间管理专家 (带时间工具)\n";
            yield return "  - 天气顾问 (带天气工具)\n\n";

            // 手动并行执行（流式观察）
            var tasks = new[]
            {
                Task.Run(async () => await ExecuteAgentAsync(dataAnalyst, userInput, "数据分析专家")),
                Task.Run(async () => await ExecuteAgentAsync(timeExpert, userInput, "时间管理专家")),
                Task.Run(async () => await ExecuteAgentAsync(weatherAdvisor, userInput, "天气顾问"))
            };

            var results = await Task.WhenAll(tasks);

            // 输出各Agent结果
            for (int i = 0; i < results.Length; i++)
            {
                yield return $"[Agent {i + 1} 完成] {results[i].AgentName}:\n";
                yield return $"{results[i].Response}\n\n";
            }

            // 汇总Agent
            yield return "[最终汇总] 协调Agent整合结果...\n\n";
            var coordinator = new ChatClientAgent(
                _chatClient,
                instructions: "你是协调专家。综合以下多个专家意见，给出完整、连贯的最终答案。用中文回答。",
                name: "Coordinator");

            var summary = string.Join("\n\n", results.Select(r => $"{r.AgentName}: {r.Response}"));
            var finalResponse = await coordinator.RunAsync(
                $"原始问题: {userInput}\n\n各专家分析结果:\n{summary}\n\n请整合以上意见给出完整答案。");

            yield return "[最终答案]\n";
            yield return finalResponse.Text ?? "无响应";
        }

        /// <summary>
        /// 执行单个Agent并捕获结果
        /// </summary>
        private async Task<AgentExecutionResult> ExecuteAgentAsync(AIAgent agent, string input, string agentName)
        {
            try
            {
                var response = await agent.RunAsync(input);
                return new AgentExecutionResult
                {
                    AgentName = agentName,
                    Response = response.Text ?? "无响应",
                    Success = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{AgentName} 执行失败", agentName);
                return new AgentExecutionResult
                {
                    AgentName = agentName,
                    Response = $"执行失败: {ex.Message}",
                    Success = false
                };
            }
        }
        #endregion

        #region 示例5: Planning模式 (多Agent手动编排)
        /// <summary>
        /// 示例5: Planning模式 - Planner -> Executor -> Reviewer
        /// </summary>
        public async Task<PlanningResult> RunPlanningModeAsync(string userInput)
        {
            _logger.LogInformation("执行Planning模式示例, 输入: {Input}", userInput);

            var result = new PlanningResult();

            // Planner Agent
            var planner = new ChatClientAgent(
                _chatClient,
                instructions: "你是任务规划专家。分析请求，制定执行计划。输出格式: 1. 步骤1 2. 步骤2 ...",
                name: "Planner");

            var planResponse = await planner.RunAsync($"请为以下任务制定执行计划: {userInput}");
            result.Plan = planResponse.Text ?? "";

            // Executor Agent
            var executor = new ChatClientAgent(
                _chatClient,
                instructions: "你是任务执行者，按照计划逐步执行并给出结果。用中文回答。",
                name: "Executor");

            var executeResponse = await executor.RunAsync($"请执行以下计划:\n{result.Plan}\n\n原始任务: {userInput}");
            result.ExecutionResult = executeResponse.Text ?? "";

            // Reviewer Agent
            var reviewer = new ChatClientAgent(
                _chatClient,
                instructions: "你是质量审核专家，审核执行结果是否完整达成目标。用中文回答。",
                name: "Reviewer");

            var reviewResponse = await reviewer.RunAsync($"原始任务: {userInput}\n执行结果:\n{result.ExecutionResult}");
            result.ReviewResult = reviewResponse.Text ?? "";

            return result;
        }
        #endregion

        #region 示例6: ReAct模式 (带工具的迭代推理)
        /// <summary>
        /// 示例6: ReAct模式 - Reasoning + Acting
        /// </summary>
        public async Task<ReactResult> RunReActModeAsync(string userInput, int maxIterations = 3)
        {
            _logger.LogInformation("执行ReAct模式示例, 输入: {Input}", userInput);

            var result = new ReactResult { Question = userInput };

            // ReAct Agent with tools
            var reactAgent = new ChatClientAgent(
                _chatClient,
                instructions: @"你是一个遵循ReAct模式的智能体。对于每个问题:
1. Thought: 思考当前状况
2. Action: 决定使用哪个工具
3. Observation: 观察结果
4. 重复直到得出答案

最终答案用 'Final Answer: [答案]' 格式输出。用中文回答。",
                tools: [
                    AIFunctionFactory.Create(GetCurrentTime),
                    AIFunctionFactory.Create(Calculate),
                    AIFunctionFactory.Create(GetWeather)
                ],
                name: "ReActAgent");

            var context = $"问题: {userInput}";
            for (int i = 0; i < maxIterations; i++)
            {
                var iteration = new ReactIteration { IterationNumber = i + 1 };

                var response = await reactAgent.RunAsync(context);
                var responseText = response.Text ?? "";

                iteration.Response = responseText;
                result.Iterations.Add(iteration);

                if (responseText.Contains("Final Answer:") || responseText.Contains("最终答案:"))
                {
                    result.FinalAnswer = responseText;
                    break;
                }

                context = $"之前的思考:\n{responseText}\n\n请继续完成任务。";
            }

            if (string.IsNullOrEmpty(result.FinalAnswer))
            {
                result.FinalAnswer = result.Iterations.LastOrDefault()?.Response ?? "未能得出结论";
            }

            return result;
        }
        #endregion

        #region 示例7: 任务分解→动态编排→协作执行→结果汇总（完整流程）
        /// <summary>
        /// 示例7: 高级任务编排系统
        /// 1. Master分解任务并决定执行策略（并行/串行）
        /// 2. 子Agent按策略执行，可A2A交互
        /// 3. Coordinator汇总结果
        /// </summary>
        public async IAsyncEnumerable<string> RunTaskOrchestrationAsync(string userInput)
        {
            _logger.LogInformation("执行任务编排系统, 输入: {Input}", userInput);

            yield return "=== 阶段1: 任务分解与策略规划 ===\n\n";

            // 1. Master Agent - 任务分解器
            var masterAgent = new ChatClientAgent(
                _chatClient,
                instructions: @"你是任务分解大师。分析用户请求，输出JSON格式的执行计划：
{
  ""tasks"": [
    {
      ""id"": ""task1"",
      ""description"": ""任务描述"",
      ""assignTo"": ""agent类型: DataAnalyst/TimeExpert/WeatherAdvisor/Developer"",
      ""requiredTools"": [""工具名称""],
      ""dependencies"": [""依赖的taskId""]
    }
  ],
  ""executionMode"": ""parallel或sequential"",
  ""reasoning"": ""策略解释""
}

规则：
- 无依赖的任务可并行（parallel）
- 有依赖的任务必须串行（sequential）
- 任务需明确分配给合适的Agent",
                name: "MasterAgent");

            var planResponse = await masterAgent.RunAsync(
                $"请为以下请求制定执行计划：{userInput}");
            var planJson = planResponse.Text ?? "";

            yield return $"[Master规划]\n{planJson}\n\n";

            // 2. 解析执行计划
            var plan = ParseExecutionPlan(planJson);
            if (plan == null || plan.Tasks.Count == 0)
            {
                yield return "[错误] 无法解析执行计划，使用默认单Agent执行\n";
                yield break;
            }

            yield return $"[策略] 执行模式: {plan.ExecutionMode}\n";
            yield return $"[策略] 任务数量: {plan.Tasks.Count}\n";
            yield return $"[原因] {plan.Reasoning}\n\n";

            yield return "=== 阶段2: 子智能体协作执行 ===\n\n";

            // 3. 创建专家Agent池
            var agentPool = CreateAgentPool();

            // 4. 执行任务（支持并行/串行 + A2A交互）
            var taskResults = new Dictionary<string, TaskExecutionResult>();

            if (plan.ExecutionMode == "parallel")
            {
                yield return "[执行] 并行模式启动...\n\n";
                await foreach (var chunk in ExecuteParallelTasksAsync(plan.Tasks, agentPool, taskResults))
                {
                    yield return chunk;
                }
            }
            else
            {
                yield return "[执行] 串行模式启动...\n\n";
                await foreach (var chunk in ExecuteSequentialTasksAsync(plan.Tasks, agentPool, taskResults, userInput))
                {
                    yield return chunk;
                }
            }

            yield return "\n=== 阶段3: 结果汇总与输出 ===\n\n";

            // 5. Coordinator汇总
            var coordinator = new ChatClientAgent(
                _chatClient,
                instructions: "你是结果汇总专家。整合所有子任务结果，给出完整、连贯的最终答案。用中文回答。",
                name: "Coordinator");

            var summaryInput = $"原始请求: {userInput}\n\n任务执行结果:\n" +
                string.Join("\n\n", taskResults.Select(r =>
                    $"任务[{r.Key}] - {r.Value.TaskDescription}:\n{r.Value.Result}"));

            yield return "[Coordinator] 开始汇总...\n\n";

            await foreach (var chunk in coordinator.RunStreamingAsync(summaryInput))
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    yield return chunk.Text;
                }
            }

            yield return "\n\n=== 执行完成 ===\n";
        }

        /// <summary>
        /// 创建Agent池
        /// </summary>
        private Dictionary<string, AIAgent> CreateAgentPool()
        {
            return new Dictionary<string, AIAgent>
            {
                ["DataAnalyst"] = new ChatClientAgent(
                    _chatClient,
                    instructions: "你是数据分析专家。进行数据分析和计算，用中文详细说明。",
                    tools: [AIFunctionFactory.Create(Calculate)],
                    name: "DataAnalyst"),

                ["TimeExpert"] = new ChatClientAgent(
                    _chatClient,
                    instructions: "你是时间管理专家。处理时间相关问题，用中文回答。",
                    tools: [AIFunctionFactory.Create(GetCurrentTime)],
                    name: "TimeExpert"),

                ["WeatherAdvisor"] = new ChatClientAgent(
                    _chatClient,
                    instructions: "你是天气顾问。查询天气并提供建议，用中文回答。",
                    tools: [AIFunctionFactory.Create(GetWeather)],
                    name: "WeatherAdvisor"),

                ["Developer"] = new ChatClientAgent(
                    _chatClient,
                    instructions: "你是开发专家。提供技术方案和代码建议，用中文回答。",
                    name: "Developer")
            };
        }

        /// <summary>
        /// 并行执行任务（支持流式输出）
        /// </summary>
        private async IAsyncEnumerable<string> ExecuteParallelTasksAsync(
            List<TaskDefinition> tasks,
            Dictionary<string, AIAgent> agentPool,
            Dictionary<string, TaskExecutionResult> results)
        {
            var parallelTasks = tasks.Select(task =>
                Task.Run(async () =>
                {
                    var agent = agentPool.GetValueOrDefault(task.AssignTo);
                    if (agent == null)
                    {
                        return new TaskExecutionResult
                        {
                            TaskId = task.Id,
                            TaskDescription = task.Description,
                            Result = $"未找到Agent: {task.AssignTo}",
                            Success = false
                        };
                    }

                    // 并行任务也需要流式收集结果
                    var taskResult = new System.Text.StringBuilder();
                    await foreach (var update in agent.RunStreamingAsync(task.Description))
                    {
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            taskResult.Append(update.Text);
                        }
                    }
                    
                    return new TaskExecutionResult
                    {
                        TaskId = task.Id,
                        TaskDescription = task.Description,
                        AssignedAgent = task.AssignTo,
                        Result = taskResult.ToString() ?? "无响应",
                        Success = true
                    };
                })).ToArray();

            var completedResults = await Task.WhenAll(parallelTasks);

            foreach (var result in completedResults)
            {
                results[result.TaskId] = result;
                yield return $"✓ [{result.AssignedAgent}] 完成任务: {result.TaskDescription}\n";
                yield return $"  结果: {result.Result}\n\n";
            }
        }

        /// <summary>
        /// 串行执行任务（支持A2A交互 + 流式输出）
        /// </summary>
        private async IAsyncEnumerable<string> ExecuteSequentialTasksAsync(
            List<TaskDefinition> tasks,
            Dictionary<string, AIAgent> agentPool,
            Dictionary<string, TaskExecutionResult> results,
            string originalInput)
        {
            foreach (var task in tasks)
            {
                yield return $"→ [{task.AssignTo}] 开始任务: {task.Description}\n";

                var agent = agentPool.GetValueOrDefault(task.AssignTo);
                if (agent == null)
                {
                    yield return $"  ✗ 错误: 未找到Agent {task.AssignTo}\n\n";
                    continue;
                }

                // 构建上下文（包含依赖任务结果 - A2A交互）
                var context = task.Description;
                if (task.Dependencies.Count > 0)
                {
                    yield return $"  [A2A交互] 依赖任务: {string.Join(", ", task.Dependencies)}\n";
                    var dependencyResults = task.Dependencies
                        .Where(depId => results.ContainsKey(depId))
                        .Select(depId => $"任务[{depId}]结果: {results[depId].Result}")
                        .ToList();

                    if (dependencyResults.Any())
                    {
                        context = $"原始请求: {originalInput}\n\n前置任务结果:\n" +
                            string.Join("\n", dependencyResults) +
                            $"\n\n你的任务: {task.Description}";
                    }
                }

                // 流式执行Agent（实时输出中间结果）
                var taskResult = new System.Text.StringBuilder();
                yield return $"  [流式输出开始]\n";
                
                await foreach (var update in agent.RunStreamingAsync(context))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        yield return $"    {update.Text}";  // 实时输出Agent的中间结果
                        taskResult.Append(update.Text);
                    }
                }
                
                var finalResult = taskResult.ToString();
                var executionResult = new TaskExecutionResult
                {
                    TaskId = task.Id,
                    TaskDescription = task.Description,
                    AssignedAgent = task.AssignTo,
                    Result = finalResult ?? "无响应",
                    Success = true
                };

                results[task.Id] = executionResult;
                yield return $"\n  ✓ 任务完成: {executionResult.Result}\n\n";
            }
        }

        /// <summary>
        /// 解析执行计划（简单JSON解析）
        /// </summary>
        private ExecutionPlan ParseExecutionPlan(string planJson)
        {
            try
            {
                // 简单正则提取（生产环境应使用System.Text.Json）
                var plan = new ExecutionPlan();

                // 提取executionMode
                var modeMatch = System.Text.RegularExpressions.Regex.Match(
                    planJson, @"""executionMode""\s*:\s*""(parallel|sequential)""");
                plan.ExecutionMode = modeMatch.Success ? modeMatch.Groups[1].Value : "sequential";

                // 提取reasoning
                var reasonMatch = System.Text.RegularExpressions.Regex.Match(
                    planJson, @"""reasoning""\s*:\s*""([^""]+)""");
                plan.Reasoning = reasonMatch.Success ? reasonMatch.Groups[1].Value : "未提供";

                // 提取tasks（简化处理）
                var taskMatches = System.Text.RegularExpressions.Regex.Matches(
                    planJson,
                    @"""id""\s*:\s*""([^""]+)""[^}]*""description""\s*:\s*""([^""]+)""[^}]*""assignTo""\s*:\s*""([^""]+)""");

                foreach (System.Text.RegularExpressions.Match match in taskMatches)
                {
                    plan.Tasks.Add(new TaskDefinition
                    {
                        Id = match.Groups[1].Value,
                        Description = match.Groups[2].Value,
                        AssignTo = match.Groups[3].Value,
                        Dependencies = new List<string>() // 简化：暂不解析依赖
                    });
                }

                return plan.Tasks.Count > 0 ? plan : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析执行计划失败");
                return null;
            }
        }
        #endregion
    }

    #region 辅助类

    /// <summary>
    /// 简单聚合执行器 (参考官方ConcurrentAggregationExecutor)
    /// </summary>
    internal sealed class SimpleAggregationExecutor() :
        Executor<List<ChatMessage>>("SimpleAggregationExecutor"), IResettableExecutor
    {
        private readonly List<ChatMessage> _messages = [];

        public override async ValueTask HandleAsync(List<ChatMessage> message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _messages.AddRange(message);

            if (_messages.Count >= 2)
            {
                var combined = string.Join("\n\n---\n\n", _messages.Select(m => m.Text));
                await context.YieldOutputAsync(combined, cancellationToken);
            }
        }

        public ValueTask ResetAsync()
        {
            _messages.Clear();
            return default;
        }
    }

    #endregion

    #region 结果模型

    public class PlanningResult
    {
        public string Plan { get; set; } = string.Empty;
        public string ExecutionResult { get; set; } = string.Empty;
        public string ReviewResult { get; set; } = string.Empty;
    }

    public class ReactResult
    {
        public string Question { get; set; } = string.Empty;
        public List<ReactIteration> Iterations { get; set; } = new();
        public string FinalAnswer { get; set; } = string.Empty;
    }

    public class ReactIteration
    {
        public int IterationNumber { get; set; }
        public string Response { get; set; } = string.Empty;
    }

    /// <summary>
    /// Agent执行结果
    /// </summary>
    public class AgentExecutionResult
    {
        public string AgentName { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
        public bool Success { get; set; }
    }

    #endregion
}
