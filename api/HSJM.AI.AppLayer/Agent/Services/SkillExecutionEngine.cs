using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Tools;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using HSJM.AI.AppLayer.Hubs;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 技能执行引擎 v3 - Function Call 驱动的 ReAct 循环
    /// 
    /// 核心思路：
    /// 1. LLM 读着 SKILL.md 指导，边思考边执行
    /// 2. 思考出的动作 → 通过 function call 转换成工具调用
    /// 3. 执行拿结果 → 继续思考
    /// 4. 直接传原始需求，不做额外处理
    /// 
    /// 工具集：
    /// - ReadFileLines: 读取文件内容
    /// - ExecuteScript: 运行技能脚本
    /// - ListScripts: 列出可用脚本
    /// </summary>
    public class SkillExecutionEngine : ISelfScopedAutoInject
    {
        private readonly ILogger<SkillExecutionEngine> _logger;
        private readonly SkillLoaderService _skillLoader;
        private readonly LLMModelService _llmModelService;
        private readonly IAgent_RoleService _roleService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ScriptExecutorTool _scriptExecutor;
        private readonly SkillManagementTool _skillManagementTool;
        private readonly IHubContext<AgentStreamingHub> _hubContext;
        private const int MAX_ITERATIONS = 20;
        private const int TIMEOUT_SECONDS = 600;

        public SkillExecutionEngine(
            ILogger<SkillExecutionEngine> logger,
            SkillLoaderService skillLoader,
            LLMModelService llmModelService,
            IAgent_RoleService roleService,
            SkillManagementTool skillManagementTool,
            IServiceProvider serviceProvider,
            ScriptExecutorTool scriptExecutor,
            IHubContext<AgentStreamingHub> hubContext,
            Plc_diagnosis.PlcDiagnosisService plcDiagnosisService)
        {
            _logger = logger;
            _skillLoader = skillLoader;
            _llmModelService = llmModelService;
            _roleService = roleService;
            _serviceProvider = serviceProvider;
            _scriptExecutor = scriptExecutor;
            _skillManagementTool = skillManagementTool;
            _hubContext = hubContext;
            _plcDiagnosisService = plcDiagnosisService;
        }

        private readonly Plc_diagnosis.PlcDiagnosisService _plcDiagnosisService;

        #region 主执行方法

        /// <summary>
        /// 执行技能 - 兼容旧接口
        /// </summary>
        public async Task<SkillEngineResult> ExecuteWithMiniAgentAsync(
            SkillContent skill,
            string userInput,
            long parentRoleId,
            CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(skill, userInput, parentRoleId, null, null, null, cancellationToken);
        }

        /// <summary>
        /// 执行技能 - Function Call 驱动的 ReAct 循环
        /// </summary>
        /// <param name="skill">技能内容（包含 SKILL.md 指导）</param>
        /// <param name="userInput">LLM 提炼的参数（已经过主 Agent 处理）</param>
        /// <param name="parentRoleId">角色 ID（用于获取模型配置）</param>
        /// <param name="originalUserRequest">用户原始请求（可选，用于提供完整上下文）</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<SkillEngineResult> ExecuteAsync(
            SkillContent skill,
            string userInput,
            long parentRoleId,
            string? originalUserRequest = null,
            long? workflowId = null,
            long? taskId = null,
            CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            var result = new SkillEngineResult
            {
                SkillId = skill.SkillId,
                SkillName = skill.Name,
                StartTime = startTime
            };

            _logger.LogInformation("[SkillEngine] 开始执行: {SkillName}, 输入: {Input}",
                skill.Name, userInput.Length > 100 ? userInput[..100] + "..." : userInput);

            try
            {
                // 0. 确保 Skill 内容是最新的（尤其是 SKILL.md）
                // 这确保了用户修改 SKILL.md 后无需重启即可生效
                await _skillLoader.ReloadSkillContentAsync(skill);

                // 1. 构建 SystemPrompt（包含原始用户请求上下文）
                var systemPrompt = BuildSystemPrompt(skill, originalUserRequest);

                // 2. 构建工具集（通过 function call 暴露）
                var tools = BuildTools(skill);
                _logger.LogInformation("[SkillEngine] 可用工具: {Tools}",
                    string.Join(", ", tools.Select(t => t.Name)));

                // 3. 获取 ChatClient
                var chatClient = await GetChatClientAsync(parentRoleId);

                // 4. 执行 ReAct 循环
                var loopResult = await RunReActLoopAsync(
                    chatClient, systemPrompt, userInput, tools, workflowId, taskId, cancellationToken);

                result.Success = loopResult.Success;
                result.Output = loopResult.FinalAnswer;
                result.ErrorMessage = loopResult.ErrorMessage;
                result.Iterations = loopResult.Iterations;

                _logger.LogInformation("[SkillEngine] 执行完成: Success={Success}, Iterations={Iter}",
                    result.Success, result.Iterations);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "执行被取消";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"执行异常: {ex.Message}";
                _logger.LogError(ex, "[SkillEngine] 执行异常: {SkillName}", skill.Name);
            }

            result.EndTime = DateTime.UtcNow;
            result.DurationMs = (long)(result.EndTime - result.StartTime).TotalMilliseconds;
            return result;
        }

        #endregion

        #region ReAct 循环

        /// <summary>
        /// ReAct 循环：思考 → function call → 执行 → 继续思考
        /// </summary>
        private async Task<LoopResult> RunReActLoopAsync(
            IChatClient chatClient,
            string systemPrompt,
            string userInput,
            List<AIFunction> tools,
            long? workflowId,
            long? taskId,
            CancellationToken cancellationToken)
        {
            var result = new LoopResult();

            // 初始化对话
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userInput)
            };

            var chatOptions = new ChatOptions
            {
                Tools = tools.Cast<AITool>().ToList(),
                ToolMode = ChatToolMode.Auto,
                AllowMultipleToolCalls = false  // 一次只调用一个工具，便于追踪
            };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(TIMEOUT_SECONDS));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                for (int i = 1; i <= MAX_ITERATIONS; i++)
                {
                    result.Iterations = i;
                    _logger.LogDebug("[SkillEngine] 迭代 {Iter}/{Max}", i, MAX_ITERATIONS);

                    if (workflowId.HasValue && taskId.HasValue)
                    {
                        await _hubContext.Clients.Group($"workflow-{workflowId}").SendAsync("ReActLoopStarted", new { taskId, loopCount = i });
                    }

                    // 调用 LLM
                    var response = await chatClient.GetResponseAsync(messages, chatOptions, linkedCts.Token);

                    // SignalR 推送思考过程
                    if (workflowId.HasValue && taskId.HasValue && !string.IsNullOrEmpty(response.Text))
                    {
                         await _hubContext.Clients.Group($"workflow-{workflowId}").SendAsync("ReActThought", new { taskId, loopCount = i, thought = response.Text });
                    }

                    // 检查 function call
                    var toolCalls = response.Messages
                        .SelectMany(m => m.Contents)
                        .OfType<FunctionCallContent>()
                        .ToList();

                    if (toolCalls.Count > 0)
                    {
                        // 1. 将 LLM 返回的完整消息加入历史（保留原始 tool call）
                        messages.AddRange(response.Messages);

                        // 2. 执行工具调用并构建结果消息
                        foreach (var call in toolCalls)
                        {
                            _logger.LogInformation("[SkillEngine] 调用工具: {Tool}, 参数: {Args}",
                                call.Name, call.Arguments != null ? JsonHelper.SerializeObject(call.Arguments) : "无");

                            if (workflowId.HasValue && taskId.HasValue)
                            {
                                await _hubContext.Clients.Group($"workflow-{workflowId}").SendAsync("ReActAction", new { taskId, loopCount = i, toolName = call.Name, parameters = call.Arguments });
                            }

                            var tool = tools.FirstOrDefault(t => t.Name == call.Name);
                            if (tool == null)
                            {
                                var errorMsg = new ChatMessage(ChatRole.Tool, $"错误：工具 {call.Name} 不存在");
                                errorMsg.Contents.Add(new FunctionResultContent(call.CallId, $"错误：工具 {call.Name} 不存在"));
                                messages.Add(errorMsg);
                                continue;
                            }

                            try
                            {
                                var args = call.Arguments != null
                                    ? new AIFunctionArguments(call.Arguments)
                                    : null;
                                var toolResult = await tool.InvokeAsync(args, linkedCts.Token);
                                var resultStr = toolResult?.ToString() ?? "[无返回值]";

                                // 将工具结果作为 Tool 角色消息加入
                                var resultMsg = new ChatMessage(ChatRole.Tool, resultStr);
                                resultMsg.Contents.Add(new FunctionResultContent(call.CallId, resultStr));
                                messages.Add(resultMsg);

                                _logger.LogDebug("[SkillEngine] 工具结果: {Result}",
                                    resultStr.Length > 500 ? resultStr[..500] + "..." : resultStr);

                                if (workflowId.HasValue && taskId.HasValue)
                                {
                                     await _hubContext.Clients.Group($"workflow-{workflowId}").SendAsync("ReActObservation", new { taskId, loopCount = i, observation = resultStr, success = true });
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[SkillEngine] 工具执行失败: {Tool}", call.Name);
                                var errorMsg = new ChatMessage(ChatRole.Tool, $"执行失败: {ex.Message}");
                                errorMsg.Contents.Add(new FunctionResultContent(call.CallId, $"执行失败: {ex.Message}"));
                                messages.Add(errorMsg);

                                if (workflowId.HasValue && taskId.HasValue)
                                {
                                     await _hubContext.Clients.Group($"workflow-{workflowId}").SendAsync("ReActObservation", new { taskId, loopCount = i, observation = $"执行失败: {ex.Message}", success = false });
                                }
                            }
                        }

                        // 继续下一轮
                        continue;
                    }

                    // 没有 function call = 最终答案
                    var responseText = response.Text;
                    if (!string.IsNullOrWhiteSpace(responseText))
                    {
                        result.Success = true;
                        result.FinalAnswer = responseText;
                        return result;
                    }
                }

                // 达到最大迭代 - 强制总结
                _logger.LogWarning("[SkillEngine] 达到最大迭代次数 ({Max})，请求强制总结", MAX_ITERATIONS);
                
                // 添加强制总结指令
                messages.Add(new ChatMessage(ChatRole.System, 
                    "已达到最大执行步骤限制。请根据当前已有的执行结果，总结通过工具获得的所有信息，并说明任务的完成程度。请直接返回总结内容，不要再调用任何工具。"));
                
                try 
                {
                    // 获取总结
                    var summaryResponse = await chatClient.GetResponseAsync(messages, new ChatOptions
                    {
                        Temperature = 0.3f, // 降低创造性，侧重事实总结
                        Tools = null // 禁用工具，强制输出文本
                    }, linkedCts.Token);
                    
                    result.Success = true; // 标记为成功（因为返回了有效信息）
                    result.FinalAnswer = summaryResponse.Text ?? "未能生成总结";
                    result.ErrorMessage = $"达到最大迭代次数 ({MAX_ITERATIONS})，返回中间总结";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = $"达到最大迭代次数且生成总结失败: {ex.Message}";
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                result.Success = false;
                result.ErrorMessage = $"执行超时 ({TIMEOUT_SECONDS}秒)";
            }

            return result;
        }

        #endregion

        #region SystemPrompt 构建

        /// <summary>
        /// 构建 SystemPrompt - 包含技能指导和原始用户请求上下文
        /// </summary>
        /// <param name="skill">技能内容</param>
        /// <param name="originalUserRequest">用户原始请求（可选），用于提供完整上下文</param>
        private string BuildSystemPrompt(SkillContent skill, string? originalUserRequest = null)
        {
            var sb = new StringBuilder();

            // 角色定义
            sb.AppendLine($"你是「{skill.DisplayName ?? skill.Name}」技能执行助手。");
            sb.AppendLine();

            // 🆕 注入运行时环境信息 (OS Context injection)
            var osInfo = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            sb.AppendLine($"[Runtime Environment: {osInfo}]");
            sb.AppendLine();

            // 🆕 用户原始请求上下文（如果有）
            // 解决问题：ReAct 调用技能时 LLM 提炼的参数可能丢失原始意图
            if (!string.IsNullOrEmpty(originalUserRequest))
            {
                sb.AppendLine("## 用户原始需求");
                sb.AppendLine("以下是用户的原始请求，请在执行技能时始终牢记这个目标：");
                sb.AppendLine($"> {originalUserRequest}");
                sb.AppendLine();
                sb.AppendLine("⚠️ 重要提示：");
                sb.AppendLine("- 你收到的具体参数是经过上层 Agent 处理后的");
                sb.AppendLine("- 执行结果应该能够完整回答用户的原始问题");
                sb.AppendLine("- 如果发现参数与原始需求有偏差，请以原始需求为准");
                sb.AppendLine();
            }

            // 技能说明（直接使用 SKILL.md 内容作为指导）
            if (!string.IsNullOrEmpty(skill.MarkdownBody))
            {
                sb.AppendLine("## 技能说明");
                sb.AppendLine(skill.MarkdownBody);
                sb.AppendLine();
            }
            else if (!string.IsNullOrEmpty(skill.Description))
            {
                sb.AppendLine("## 技能描述");
                sb.AppendLine(skill.Description);
                sb.AppendLine();
            }

            // 技能包路径（如果有）
            if (!string.IsNullOrEmpty(skill.SkillPackagePath))
            {
                sb.AppendLine("## 技能包信息");
                sb.AppendLine($"技能包路径: {skill.SkillPackagePath}");
                sb.AppendLine("可以使用 ExecuteSkillScriptAsync 工具执行技能包中的脚本。");
                sb.AppendLine();
            }

            // 执行指引
            sb.AppendLine("## 执行指引");
            sb.AppendLine("1. 仔细阅读上面的技能说明");
            sb.AppendLine("2. 根据用户请求，使用提供的工具一步步完成任务");
            sb.AppendLine("3. 如果需要读取文件，使用 ReadFileLinesAsync");
            sb.AppendLine("4. 如果需要运行脚本，使用 ExecuteSkillScriptAsync");
            sb.AppendLine("5. 如果需要写入文件，使用 WriteFileContentAsync");
            sb.AppendLine("   - ⚠️ 对于 skill-creator: 不创建 README.md，main.py 作为入口，requirements.txt 只在 scripts/");
            sb.AppendLine("6. 如果需要查看目录结构，使用 ListDirectoryStructureAsync");
            sb.AppendLine("7. ⚠️ 重要：当你认为任务完成或无法继续时，**必须**输出一份详细的执行总结。说明已完成的步骤、获取的关键数据以及最终结论。不要只说'完成'。");
            
            if (originalUserRequest != null)
            {
                sb.AppendLine();
                sb.AppendLine("8. ⚠️ 最终输出必须能够直接回答用户的原始需求，不要只输出技术细节。");
            }

            // 🆕 Self-Evolution Protocol (自我进化协议)
            sb.AppendLine();
            sb.AppendLine("## 自我进化协议 (Self-Evolution Protocol)");
            sb.AppendLine("如果你在执行过程中发现代码存在 Bug 或进行了功能增强，并使用了 WriteFileContentAsync 修改了代码 (如 main.py)，你必须：");
            sb.AppendLine("1. 检查 SKILL.md 中的描述、输入/输出 Schema 是否还需要更新。");
            sb.AppendLine("2. 如果需要，请使用 WriteFileContentAsync 更新 SKILL.md，使其与最新的代码保持一致。");
            sb.AppendLine("3. 这是为了确保技能文档永远反映最新的代码行为。");

            return sb.ToString();
        }

        #endregion

        #region 工具构建

        /// <summary>
        /// 构建工具集 - 通过 function call 暴露给 LLM
        /// </summary>
        private List<AIFunction> BuildTools(SkillContent skill)
        {
            var tools = new List<AIFunction>();

            // 1. 脚本执行工具
            tools.Add(AIFunctionFactory.Create(_scriptExecutor.ExecuteSkillScriptAsync));
            tools.Add(AIFunctionFactory.Create(BuiltInTools.GetCurrentTime));
            // 2. 文件读取工具
            try
            {
                var fileTools = _serviceProvider.GetService<FileOperationTools>(); 
                    var scriptExecutorTool = _serviceProvider.GetService<ScriptExecutorTool>();
                if (fileTools != null)
                {
                    tools.Add(AIFunctionFactory.Create(fileTools.ReadFileLinesAsync));
                    //tools.Add(AIFunctionFactory.Create(fileTools.WriteFileContentAsync));
                    tools.Add(AIFunctionFactory.Create(fileTools.ListDirectoryStructureAsync));
                    tools.Add(AIFunctionFactory.Create(_skillManagementTool.SaveSkillDefinitionAsync));
                    tools.Add(AIFunctionFactory.Create(scriptExecutorTool.InstallSkillDependenciesAsync));
                    
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SkillEngine] 加载文件工具失败");
            }

            // 3. PLC 诊断工具 (根据 Skill 类型判断?) - 暂时直接添加
            try 
            {
                if (_plcDiagnosisService != null&& skill.Name== "plc-diagnosis")
                {
                    tools.Add(AIFunctionFactory.Create(_plcDiagnosisService.GetMachineStatusAsync));
                    tools.Add(AIFunctionFactory.Create(_plcDiagnosisService.FindLogicForVariableAsync));
                    tools.Add(AIFunctionFactory.Create(_plcDiagnosisService.GetNetworkWithValuesAsync));
                    tools.Add(AIFunctionFactory.Create(_plcDiagnosisService.GetVariableHistoryAsync));
                    // IO 约束分析工具（预处理 + 生产）
                    tools.Add(AIFunctionFactory.Create(_plcDiagnosisService.GetSignalLogicContextAsync));
                    tools.Add(AIFunctionFactory.Create((long projectId, string signalValues) => _plcDiagnosisService.ValidateIOSignalsAsync(projectId, signalValues)));
                    tools.Add(AIFunctionFactory.Create((string signalName) => _plcDiagnosisService.GetIORelationshipAsync(signalName)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SkillEngine] 加载 PLC 诊断工具失败");
            }

            return tools;
        }

        #endregion

        #region 辅助方法

        private async Task<IChatClient> GetChatClientAsync(long roleId)
        {
            var role = await _roleService.GetByIdAsync(roleId);

            if (role?.ModelID != null && role.ModelID.Value > 0)
            {
                return (await _llmModelService.CreateChatClientByModelIdAsync(role.ModelID.Value)).AsIChatClient();
            }

            var recommendedModel = await _llmModelService.RecommendModelAsync();
            if (recommendedModel == null)
            {
                throw new InvalidOperationException("无可用的 LLM 模型");
            }

            return (await _llmModelService.CreateChatClientByModelIdAsync(recommendedModel.Id)).AsIChatClient();
        }

        #endregion
    }

    #region 结果模型

    internal class LoopResult
    {
        public bool Success { get; set; }
        public string? FinalAnswer { get; set; }
        public string? ErrorMessage { get; set; }
        public int Iterations { get; set; }
    }

    public class SkillEngineResult
    {
        public long SkillId { get; set; }
        public string SkillName { get; set; } = "";
        public bool Success { get; set; }
        public string? Output { get; set; }
        public string? ErrorMessage { get; set; }
        public int Iterations { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public long DurationMs { get; set; }

        public override string ToString() => Success ? Output ?? "[成功]" : $"[失败] {ErrorMessage}";
    }

    #endregion
}
