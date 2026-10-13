using HSJM.AI.AppLayer.Agent.Tools;
using HSJM.AI.AppLayer.Hubs;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// Agent 运行时服务（基于 Microsoft.Agents.AI）
    /// 职责：创建和管理 AI Agent 实例，封装 Microsoft.Agents.AI 框架
    /// 参考: WorkflowOrchestrationApp.cs 中的 ChatClientAgent 使用模式
    /// </summary>
    public class AgentRuntimeService : ISelfScopedAutoInject
    {
        private readonly LLMModelService _llmModelService;
        private readonly AgentRoleConfigService _roleConfigService;
        private readonly IAgent_RoleService _roleService;
        private readonly IPrompt_VersionService _promptVersionService;
        private readonly IHubContext<AgentStreamingHub> _hubContext;
        private readonly ILogger<AgentRuntimeService> _logger;
        private readonly IRole_Tool_RelationService _roleToolRelationService;
        private readonly ITool_DefinitionService _toolDefinitionService;
        private readonly IServiceProvider _serviceProvider;
        private readonly McpClientService _mcpClientService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SkillLoaderService _skillLoaderService;
        private readonly ToolCallTrackerService _toolCallTrackerService;
        private readonly DiagnosticToolWrapperFactory _diagnosticWrapperFactory;
        private readonly ReActExecutionEngine _reActEngine;
        private readonly SkillToolAdapter _skillToolAdapter;
        private readonly MemoryTools _memoryTools;

        public AgentRuntimeService(
            LLMModelService llmModelService,
            AgentRoleConfigService roleConfigService,
            IAgent_RoleService roleService,
            IPrompt_VersionService promptVersionService,
            IHubContext<AgentStreamingHub> hubContext,
            ILogger<AgentRuntimeService> logger,
            IRole_Tool_RelationService roleToolRelationService,
            ITool_DefinitionService toolDefinitionService,
            IServiceProvider serviceProvider,
            McpClientService mcpClientService,
            IHttpClientFactory httpClientFactory,
            SkillLoaderService skillLoaderService,
            ToolCallTrackerService toolCallTrackerService,
            DiagnosticToolWrapperFactory diagnosticWrapperFactory,
            ReActExecutionEngine reActEngine,
            SkillToolAdapter skillToolAdapter,
            MemoryTools memoryTools)
        {
            _llmModelService = llmModelService;
            _roleConfigService = roleConfigService;
            _roleService = roleService;
            _promptVersionService = promptVersionService;
            _hubContext = hubContext;
            _logger = logger;
            _roleToolRelationService = roleToolRelationService;
            _toolDefinitionService = toolDefinitionService;
            _serviceProvider = serviceProvider;
            _mcpClientService = mcpClientService;
            _httpClientFactory = httpClientFactory;
            _skillLoaderService = skillLoaderService;
            _toolCallTrackerService = toolCallTrackerService;
            _diagnosticWrapperFactory = diagnosticWrapperFactory;
            _reActEngine = reActEngine;
            _skillToolAdapter = skillToolAdapter;
            _memoryTools = memoryTools;
        }

        private async Task<IChatClient> CreateChatClientForRoleAsync(long roleId, long? roleModelId)
        {
            var choice = await AgentModelRouting.SelectAsync(
                roleId, roleModelId, AppsettingsConfig.GetDiagnosticAgentRoleId(),
                async () => (await _llmModelService.RecommendModelAsync(
                    modelType: "Chat", requiredContextSize: 4096, prioritizeCost: true))?.Id);
            _logger.LogInformation(choice.FromDefaultBinding
                    ? "[Agent创建] 使用网站 default_chat 用途绑定: ModelID={ModelId}"
                    : "[Agent创建] 使用角色指定模型: ModelID={ModelId}", choice.ModelId);
            return (await _llmModelService.CreateChatClientByModelIdAsync(choice.ModelId)).AsIChatClient();
        }

        #region Agent 创建与初始化

        /// <summary>
        /// 根据角色 ID 创建 AI Agent 实例
        /// 
        /// 实现思路（参考 WorkflowOrchestrationApp）：
        /// 1. 从数据库加载角色配置（AI_Agent_Role）
        /// 2. 诊断角色走网站 default_chat 用途绑定；其它角色优先使用自己的 ModelID，未配置时走网站默认绑定
        /// 3. 使用选定的模型创建 ChatClient
        /// 4. 加载当前激活的 Prompt 版本作为 SystemPrompt
        /// 5. 使用 ChatClientAgent 创建实例：new ChatClientAgent(chatClient, instructions, name)
        /// </summary>
        /// <param name="roleId">角色 ID</param>
        /// <returns>ChatClientAgent 实例</returns>
        public async Task<ChatClientAgent> CreateAgentByRoleAsync(long roleId)
        {
            return await CreateAgentByRoleAsync(roleId, null);
        }

        /// <summary>
        /// 根据角色 ID 创建 AI Agent 实例，并允许用后传提示词覆盖角色默认提示词。
        /// </summary>
        /// <param name="roleId">角色 ID</param>
        /// <param name="overrideInstructions">覆盖用的系统提示词；有值时以后传为准</param>
        /// <returns>ChatClientAgent 实例</returns>
        public async Task<ChatClientAgent> CreateAgentByRoleAsync(long roleId, string? overrideInstructions)
        {
            _logger.LogInformation("[Agent创建] 开始: RoleId={RoleId}", roleId);
            var startTime = DateTime.Now;

            // 1. 加载角色配置
            var role = await _roleService.GetByIdAsync(roleId);
            if (role == null || role.IsDeleted)
                throw new InvalidOperationException($"角色不存在: RoleId={roleId}");

            if (role.IsEnabled == false)
                throw new InvalidOperationException($"角色已禁用: RoleId={roleId}");

            // 诊断角色按网站 default_chat 用途选模，保留角色的提示词和技能配置；
            // 其它角色仍优先使用角色自身的模型绑定。
            IChatClient chatClient = await CreateChatClientForRoleAsync(roleId, role.ModelID);

            // 3. 加载激活的 Prompt 版本
            var activePrompt = await _promptVersionService.GetFirstAsync(
                p => p.RoleID == roleId && p.IsActive == true && p.IsDeleted == false);

            string instructions = ResolveAgentInstructions(
                role.SystemPrompt,
                activePrompt?.SystemPromptContent,
                overrideInstructions,
                roleId,
                activePrompt?.VersionNumber);

            // 3.5 加载 Skills（Skill-as-Tool 模式：不再注入 Prompt，而是作为工具加载）
            var skills = await _skillLoaderService.LoadSkillsForRoleAsync(roleId);
            if (skills.Count > 0)
            {
                _logger.LogInformation("[Agent创建] 已加载{Count}个Skills（将作为工具注册）", skills.Count);
            }

            // 4. 创建 ChatClientAgent（参考 WorkflowOrchestrationApp 模式）
            var agent = new ChatClientAgent(
                chatClient,
                instructions: instructions,
                name: role.RoleName ?? $"Agent-{roleId}"
            );

            var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            _logger.LogInformation("[Agent创建] 完成: RoleId={RoleId}, Name={Name}, 耗时={Elapsed}ms",
                roleId, agent.Name, elapsed);

            return agent;
        }

        /// <summary>
        /// 创建简单的临时 Agent（不依赖数据库角色配置）
        /// 
        /// 参考: WorkflowOrchestrationApp 中直接 new ChatClientAgent 的方式
        /// 用于快速测试或一次性任务
        /// </summary>
        /// <param name="instructions">System Prompt 指令</param>
        /// <param name="agentName">Agent 名称</param>
        /// <param name="modelId">指定 LLM 模型 ID，为 null 则使用注入的默认 ChatClient</param>
        /// <returns>ChatClientAgent 实例</returns>
        public async Task<ChatClientAgent> CreateSimpleAgentAsync(
            string instructions, 
            string agentName = "SimpleAgent",
            long? modelId = null)
        {
            _logger.LogInformation("[简单Agent] 创建: {Name}, ModelId={ModelId}", agentName, modelId);

            IChatClient chatClient;
            if (modelId.HasValue && modelId.Value > 0)
            {
                // 使用指定模型
                chatClient = (await _llmModelService.CreateChatClientByModelIdAsync(modelId.Value)).AsIChatClient();
            }
            else
            {
                // 平台默认聊天用途由网站显式绑定。
                var recommended = await _llmModelService.RecommendModelAsync();
                _logger.LogInformation("[简单Agent] 使用网站默认聊天模型: {ModelName}(Id={Id})", recommended.ModelName, recommended.Id);
                chatClient = (await _llmModelService.CreateChatClientByModelIdAsync(recommended.Id)).AsIChatClient();
            }

            // 直接创建 ChatClientAgent（参考 WorkflowOrchestrationApp 模式）
            var agent = new ChatClientAgent(
                chatClient,
                instructions: instructions,
                name: agentName
            );

            return agent;
        }

        /// <summary>
        /// 创建带工具的 Agent
        /// 
        /// 参考: WorkflowOrchestrationApp 中的工具绑定模式
        /// var tools = AIFunctionFactory.Create(GetCurrentTime);
        /// var agent = new ChatClientAgent(chatClient, instructions, name, tools: tools);
        /// </summary>
        /// <param name="roleId">角色 ID</param>
        /// <param name="maxIterations">工具调用最大迭代次数（默认1次，避免无限重试）</param>
        /// <returns>带工具的 ChatClientAgent</returns>
        public async Task<ChatClientAgent> CreateAgentWithToolsAsync(long roleId, int maxIterations = 1)
        {
            return await CreateAgentWithToolsAsync(roleId, null, maxIterations);
        }

        /// <summary>
        /// 创建带工具的 Agent，并允许用后传提示词覆盖角色默认提示词。
        /// </summary>
        /// <param name="roleId">角色 ID</param>
        /// <param name="overrideInstructions">覆盖用的系统提示词；有值时以后传为准</param>
        /// <param name="maxIterations">工具调用最大迭代次数</param>
        /// <returns>带工具的 ChatClientAgent</returns>
        public async Task<ChatClientAgent> CreateAgentWithToolsAsync(long roleId, string? overrideInstructions, int maxIterations = 1)
        {
            _logger.LogInformation("[Agent创建] 开始（含工具）: RoleId={RoleId}", roleId);
            var startTime = DateTime.Now;

            // 1. 加载角色配置
            var role = await _roleService.GetByIdAsync(roleId);
            if (role == null || role.IsDeleted)
                throw new InvalidOperationException($"角色不存在: RoleId={roleId}");

            if (role.IsEnabled == false)
                throw new InvalidOperationException($"角色已禁用: RoleId={roleId}");

            // 2. 选择 LLM 模型（同 CreateAgentByRoleAsync）
            IChatClient chatClient = await CreateChatClientForRoleAsync(roleId, role.ModelID);

            // 3. 加载 Prompt 版本
            var activePrompt = await _promptVersionService.GetFirstAsync(
                p => p.RoleID == roleId && p.IsActive == true && p.IsDeleted == false);

            string instructions = ResolveAgentInstructions(
                role.SystemPrompt,
                activePrompt?.SystemPromptContent,
                overrideInstructions,
                roleId,
                activePrompt?.VersionNumber);

            // 3.5 加载 Skills（Skill-as-Tool 模式：不再注入 Prompt，而是作为工具加载）
            var skills = await _skillLoaderService.LoadSkillsForRoleAsync(roleId);
            if (skills.Count > 0)
            {
                _logger.LogInformation("[Agent创建] 已加载{Count}个Skills（将作为工具注册）", skills.Count);
            }

            // 4. 加载工具（使用改进后的动态加载，自动保留参数元数据）
            var tools = await LoadToolsForRoleAsync(roleId, skills);

            // 5. 创建带工具的 Agent
            var agent = new ChatClientAgent(
                chatClient,
                instructions: instructions,
                name: role.RoleName ?? $"Agent-{roleId}",
                tools: tools.Cast<AITool>().ToList()
            );

            var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            _logger.LogInformation("[Agent创建] 完成（含工具）: RoleId={RoleId}, 工具数={Count}, 耗时={Elapsed}ms",
                roleId, tools.Count, elapsed);

            return agent;
        }

        /// <summary>
        /// 创建带诊断追踪的 Agent
        /// 
        /// 功能增强：
        /// 1. 工具调用追踪：记录每次工具调用的参数和结果
        /// 2. 重复调用检测：相同参数调用会被拦截并返回缓存结果
        /// 3. 调用次数限制：防止单工具或总调用次数超限
        /// 
        /// 用于解决框架内部工具调用"黑盒"问题，防止 Agent 陷入重复调用死循环
        /// </summary>
        /// <param name="roleId">角色 ID</param>
        /// <param name="sessionId">追踪会话 ID（建议使用 TaskId）</param>
        /// <param name="limits">工具调用限制配置（可选）</param>
        /// <returns>带诊断的 ChatClientAgent</returns>
        public async Task<ChatClientAgent> CreateAgentWithDiagnosticsAsync(
            long roleId, 
            string sessionId,
            ToolCallLimits? limits = null)
        {
            return await CreateAgentWithDiagnosticsAsync(roleId, sessionId, null, limits);
        }

        /// <summary>
        /// 创建带诊断追踪的 Agent，并允许用后传提示词覆盖角色默认提示词。
        /// </summary>
        public async Task<ChatClientAgent> CreateAgentWithDiagnosticsAsync(
            long roleId,
            string sessionId,
            string? overrideInstructions,
            ToolCallLimits? limits = null)
        {
            _logger.LogInformation("[Agent创建] 开始（含诊断）: RoleId={RoleId}, SessionId={SessionId}", 
                roleId, sessionId);
            var startTime = DateTime.Now;

            // 1. 加载角色配置
            var role = await _roleService.GetByIdAsync(roleId);
            if (role == null || role.IsDeleted)
                throw new InvalidOperationException($"角色不存在: RoleId={roleId}");

            if (role.IsEnabled == false)
                throw new InvalidOperationException($"角色已禁用: RoleId={roleId}");

            // 2. 选择 LLM 模型
            IChatClient chatClient = await CreateChatClientForRoleAsync(roleId, role.ModelID);

            // 3. 加载 Prompt 版本
            var activePrompt = await _promptVersionService.GetFirstAsync(
                p => p.RoleID == roleId && p.IsActive == true && p.IsDeleted == false);

            string instructions = ResolveAgentInstructions(
                role.SystemPrompt,
                activePrompt?.SystemPromptContent,
                overrideInstructions,
                roleId,
                activePrompt?.VersionNumber);

            // 4. 创建追踪会话
            _toolCallTrackerService.CreateSession(sessionId, limits);

            // 5. 加载工具并包装为诊断版本
            var rawTools = await LoadToolsForRoleAsync(roleId);
            var diagnosticTools = _diagnosticWrapperFactory.WrapAll(rawTools, sessionId);

            // 6. 创建带诊断工具的 Agent
            var agent = new ChatClientAgent(
                chatClient,
                instructions: instructions,
                name: role.RoleName ?? $"Agent-{roleId}",
                tools: diagnosticTools.Cast<AITool>().ToList()
            );

            var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            _logger.LogInformation("[Agent创建] 完成（含诊断）: RoleId={RoleId}, 工具数={Count}, 耗时={Elapsed}ms",
                roleId, diagnosticTools.Count, elapsed);

            return agent;
        }

        /// <summary>
        /// 关闭诊断会话并获取摘要
        /// 在任务完成或失败时调用
        /// </summary>
        /// <param name="sessionId">追踪会话 ID</param>
        /// <returns>调用摘要</returns>
        public ToolCallTrackerSummary CloseDiagnosticSession(string sessionId)
        {
            return _toolCallTrackerService.CloseSession(sessionId);
        }

        /// <summary>
        /// 获取当前会话的调用统计
        /// 可在任务执行过程中调用，用于监控
        /// </summary>
        public ToolCallTrackerSummary? GetSessionSummary(string sessionId)
        {
            return _toolCallTrackerService.GetSession(sessionId)?.GetSummary();
        }

        private string ResolveAgentInstructions(
            string? roleSystemPrompt,
            string? activePromptContent,
            string? overrideInstructions,
            long roleId,
            string? activePromptVersion)
        {
            string instructions = roleSystemPrompt ?? "你是一个智能助手。";

            if (!string.IsNullOrWhiteSpace(activePromptContent))
            {
                instructions = activePromptContent;
                _logger.LogInformation("[Agent创建] 使用Prompt版本: {Version}", activePromptVersion ?? "Unknown");
            }

            if (!string.IsNullOrWhiteSpace(overrideInstructions))
            {
                instructions = overrideInstructions;
                _logger.LogInformation("[Agent创建] 使用覆盖提示词: RoleId={RoleId}", roleId);
            }

            return instructions;
        }

        #endregion

        #region Agent 执行

        /// <summary>
        /// 执行 Agent 推理（单轮对话）
        /// </summary>
        /// <param name="agent">ChatClientAgent 实例</param>
        /// <param name="userInput">用户输入</param>
        /// <returns>Agent 响应</returns>
        public async Task<AgentResponse> RunAgentAsync(
            ChatClientAgent agent, 
            string userInput)
        {
            return await RunAgentAsync(agent, userInput, null);
        }

        /// <summary>
        /// 执行 Agent 推理（支持多轮对话历史）
        /// ✅ 重构：使用原生 ChatMessage 格式，保持角色区分
        /// </summary>
        /// <param name="agent">ChatClientAgent 实例</param>
        /// <param name="userInput">用户输入</param>
        /// <param name="historyMessages">历史对话消息（可选）</param>
        /// <returns>Agent 响应</returns>
        public async Task<AgentResponse> RunAgentAsync(
            ChatClientAgent agent, 
            string userInput,
            List<ChatMessage>? historyMessages,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[Agent执行] 开始: Agent={Name}, 历史消息={HistoryCount}条", 
                agent.Name, historyMessages?.Count ?? 0);
            var startTime = DateTime.Now;

            try
            {
                // ChatClientAgent 已经持有 instructions，执行时只构建用户/历史消息，避免重复注入 system prompt。
                var messages = BuildChatMessagesForAgent(userInput, historyMessages);

                WriteLogHelper.WriteLogsAsync($"智能体名称：{agent.Name}，消息数:{messages.Count}，定义：{agent.Instructions}","模型调用日志");
                
                // ✅ 使用原生消息列表调用 Agent
                // 注意：ChatClientAgent.RunAsync 需要显式传递 null 给 thread 和 options
                var response = await agent.RunAsync(messages, null, null, cancellationToken);
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                
                _logger.LogInformation("[Agent执行] 完成: Agent={Name}, 耗时={Elapsed}ms",
                    agent.Name, elapsed);

                return new AgentResponse
                {
                    AgentName = agent.Name ?? "Unknown",
                    Content = response.Text ?? "",
                    RawResponse = response,
                    ExecutionTimeMs = elapsed,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                _logger.LogError(ex, "[Agent执行] 失败: Agent={Name}", agent.Name);

                return new AgentResponse
                {
                    AgentName = agent.Name ?? "Unknown",
                    Content = $"执行失败: {ex.Message}",
                    RawResponse = null,
                    ExecutionTimeMs = elapsed,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// 为已创建的 ChatClientAgent 构建消息列表（不再注入 system prompt）
        /// 
        /// 优势：
        /// 1. 历史消息保持原生角色（User/Assistant），LLM 能正确理解对话上下文
        /// 2. 不会因为文本拼接导致角色混淆
        /// 3. 符合 Chat API 的标准用法
        /// </summary>
        private List<ChatMessage> BuildChatMessagesForAgent(
            string userInput, 
            List<ChatMessage>? historyMessages)
        {
            var messages = new List<ChatMessage>();

            // 1. 历史对话消息（原生格式，保持角色区分）
            // ✅ 修复：跳过最后一条历史消息（如果与当前输入相同，避免重复）
            if (historyMessages != null && historyMessages.Count > 0)
            {
                var historyCount = historyMessages.Count;
                var lastHistoryMsg = historyMessages[historyCount - 1];
                
                // 检查最后一条是否与当前用户输入重复
                var isLastMsgDuplicate = lastHistoryMsg.Role == ChatRole.User 
                    && !string.IsNullOrEmpty(lastHistoryMsg.Text)
                    && lastHistoryMsg.Text.Trim() == userInput.Trim();
                
                // 如果重复，则排除最后一条
                var messagesToAdd = isLastMsgDuplicate 
                    ? historyMessages.Take(historyCount - 1) 
                    : historyMessages;
                
                _logger.LogDebug("[BuildChatMessages] 历史消息: {Count}条, 跳过重复: {Skip}", 
                    historyCount, isLastMsgDuplicate);
                
                foreach (var historyMsg in messagesToAdd)
                {
                    // 限制单条消息长度，避免上下文溢出
                    var content = historyMsg.Text ?? "";
                    if (content.Length > 1000)
                        content = content.Substring(0, 1000) + "...[截断]";
                    
                    messages.Add(new ChatMessage(historyMsg.Role, content));
                }
            }

            // 2. 当前用户输入
            messages.Add(new ChatMessage(ChatRole.User, userInput));

            return messages;
        }

        /// <summary>
        /// 流式执行 Agent（实时输出）
        /// 
        /// 参考: WorkflowOrchestrationApp 中的流式模式
        /// await foreach (var update in agent.RunStreamingAsync(input))
        /// </summary>
        /// <param name="agent">ChatClientAgent 实例</param>
        /// <param name="userInput">用户输入</param>
        /// <param name="executionId">执行ID（用于 SignalR 分组）</param>
        /// <returns>流式响应</returns>
        public async Task<AgentResponse> RunAgentStreamingAsync(
            ChatClientAgent agent,
            string userInput,
            long executionId)
        {
            _logger.LogInformation("[Agent流式执行] 开始: Agent={Name}, ExecutionId={ExecutionId}",
                agent.Name, executionId);
            var startTime = DateTime.Now;

            try
            {
                var groupName = $"agent-{executionId}";
                var fullContent = "";

                // 流式调用（参考 WorkflowOrchestrationApp 中的 RunStreamingAsync）
                await foreach (var update in agent.RunStreamingAsync(userInput))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        fullContent += update.Text;

                        // 推送到 SignalR
                        await _hubContext.Clients.Group(groupName).SendAsync("ReceiveToken", new
                        {
                            token = update.Text,
                            agentName = agent.Name,
                            timestamp = DateTime.Now
                        });
                    }
                }

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;

                // 发送完成信号
                await _hubContext.Clients.Group(groupName).SendAsync("ExecutionComplete", new
                {
                    executionId = executionId,
                    agentName = agent.Name,
                    elapsed = elapsed
                });

                _logger.LogInformation("[Agent流式执行] 完成: Agent={Name}, 耗时={Elapsed}ms",
                    agent.Name, elapsed);

                return new AgentResponse
                {
                    AgentName = agent.Name ?? "Unknown",
                    Content = fullContent,
                    RawResponse = null,
                    ExecutionTimeMs = elapsed,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                _logger.LogError(ex, "[Agent流式执行] 失败: Agent={Name}", agent.Name);

                // 发送错误信号
                var groupName = $"agent-{executionId}";
                await _hubContext.Clients.Group(groupName).SendAsync("ExecutionError", new
                {
                    executionId = executionId,
                    agentName = agent.Name,
                    error = ex.Message
                });

                return new AgentResponse
                {
                    AgentName = agent.Name ?? "Unknown",
                    Content = $"执行失败: {ex.Message}",
                    RawResponse = null,
                    ExecutionTimeMs = elapsed,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// 流式执行 Agent，并通过回调返回文本分片。
        /// </summary>
        /// <param name="agent">ChatClientAgent 实例</param>
        /// <param name="userInput">用户输入</param>
        /// <param name="onToken">每个文本分片的回调</param>
        /// <returns>聚合后的完整响应</returns>
        public async Task<AgentResponse> RunAgentStreamingAsync(
            ChatClientAgent agent,
            string userInput,
            Func<string, Task>? onToken)
        {
            _logger.LogInformation("[Agent流式执行] 开始: Agent={Name}, HasTokenCallback={HasTokenCallback}",
                agent.Name, onToken != null);
            var startTime = DateTime.Now;

            try
            {
                var fullContent = "";

                await foreach (var update in agent.RunStreamingAsync(userInput))
                {
                    if (string.IsNullOrEmpty(update.Text))
                    {
                        continue;
                    }

                    fullContent += update.Text;

                    if (onToken != null)
                    {
                        await onToken(update.Text);
                    }
                }

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;

                _logger.LogInformation("[Agent流式执行] 完成: Agent={Name}, Elapsed={Elapsed}ms",
                    agent.Name, elapsed);

                return new AgentResponse
                {
                    AgentName = agent.Name ?? "Unknown",
                    Content = fullContent,
                    RawResponse = null,
                    ExecutionTimeMs = elapsed,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                _logger.LogError(ex, "[Agent流式执行] 失败: Agent={Name}", agent.Name);

                return new AgentResponse
                {
                    AgentName = agent.Name ?? "Unknown",
                    Content = $"执行失败: {ex.Message}",
                    RawResponse = null,
                    ExecutionTimeMs = elapsed,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// 使用自定义 ReAct 循环执行 Agent
        /// 
        /// 核心优势：
        /// 1. 完全控制执行循环（不依赖框架的自动循环）
        /// 2. 每一步都有完整的执行上下文
        /// 3. 支持过程诊断和干预
        /// 4. 可以在任何阶段进行流式输出
        /// 
        /// 适用场景：
        /// - 复杂任务需要多轮工具调用
        /// - 需要精细控制执行过程
        /// - 需要实时监控和诊断
        /// </summary>
        /// <param name="roleId">角色 ID</param>
        /// <param name="taskDescription">任务描述</param>
        /// <param name="sessionId">会话 ID（可选，用于追踪）</param>
        /// <param name="maxIterations">最大迭代次数</param>
        /// <param name="onStepComplete">步骤完成回调（可选，用于流式输出）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>ReAct 执行结果</returns>
        public async Task<ReActExecutionResult> RunAgentWithReActAsync(
            long roleId,
            string taskDescription,
            string? sessionId = null,
            int maxIterations = 10,
            Func<ReActStep, Task>? onStepComplete = null,
            CancellationToken cancellationToken = default,
            long? workflowId = null, // 🆕 新增
            long? taskId = null)     // 🆕 新增
        {
            _logger.LogInformation("[ReAct执行] 开始: RoleId={RoleId}, Task={Task}, WorkflowId={WorkflowId}",
                roleId, taskDescription?.Substring(0, Math.Min(50, taskDescription?.Length ?? 0)), workflowId);

            // 1. 加载角色配置
            var role = await _roleService.GetByIdAsync(roleId);
            if (role == null || role.IsDeleted)
                throw new InvalidOperationException($"角色不存在: RoleId={roleId}");

            if (role.IsEnabled == false)
                throw new InvalidOperationException($"角色已禁用: RoleId={roleId}");

            // 2. 选择 LLM 模型并创建 ChatClient
            IChatClient chatClient = await CreateChatClientForRoleAsync(roleId, role.ModelID);

            // 3. 加载 Prompt 版本
            var activePrompt = await _promptVersionService.GetFirstAsync(
                p => p.RoleID == roleId && p.IsActive == true && p.IsDeleted == false);

            string systemPrompt = role.SystemPrompt ?? "你是一个智能助手。";
            if (activePrompt != null && !string.IsNullOrWhiteSpace(activePrompt.SystemPromptContent))
            {
                systemPrompt = activePrompt.SystemPromptContent;
            }

            // 4. 加载 Skills（Skill-as-Tool 模式：不再注入 Prompt，而是作为工具加载）
            var skills = await _skillLoaderService.LoadSkillsForRoleAsync(roleId);
            if (skills.Count > 0)
            {
                _logger.LogInformation("[ReAct执行] 已加载{Count}个Skills（将作为工具注册）", skills.Count);
            }

            // 5. 加载工具（传递任务描述作为原始用户请求上下文，并传递WorkflowId/TaskId）
            var tools = await LoadToolsForRoleAsync(roleId, skills, taskDescription, workflowId, taskId);

            // 6. 构建 ReAct 执行配置
            var config = new ReActExecutionConfig
            {
                SessionId = sessionId ?? $"react-{roleId}-{DateTime.UtcNow.Ticks}",
                ChatSessionId = sessionId.ToLong(),
                RoleId = roleId,
                TaskDescription = taskDescription,
                SystemPrompt = systemPrompt,
                Tools = tools,
                MaxIterations = maxIterations,
                EnableReflection = true,
                OnStepComplete = onStepComplete,
                CallLimits = new ToolCallLimits
                {
                    MaxCallsPerTool = 5,
                    MaxTotalCalls = 20,
                    MaxDuplicateCalls = 2,
                    DuplicateWarningThreshold = 1
                }
            };

            // 7. 执行 ReAct 循环
            var result = await _reActEngine.ExecuteAsync(chatClient, config, cancellationToken);

            _logger.LogInformation("[ReAct执行] 完成: RoleId={RoleId}, Success={Success}, Iterations={Iterations}",
                roleId, result.Success, result.TotalIterations);

            return result;
        }

        /// <summary>
        /// 使用 ReAct 循环流式执行 Agent（通过 SignalR 推送）
        /// </summary>
        /// <param name="roleId">角色 ID</param>
        /// <param name="taskDescription">任务描述</param>
        /// <param name="executionId">执行 ID（用于 SignalR 分组）</param>
        /// <param name="maxIterations">最大迭代次数</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>ReAct 执行结果</returns>
        public async Task<ReActExecutionResult> RunAgentWithReActStreamingAsync(
            long roleId,
            string taskDescription,
            long executionId,
            int maxIterations = 10,
            CancellationToken cancellationToken = default)
        {
            var groupName = $"react-{executionId}";

            // 定义步骤完成回调：推送到 SignalR
            async Task OnStepComplete(ReActStep step)
            {
                await _hubContext.Clients.Group(groupName).SendAsync("ReActStep", new
                {
                    iteration = step.Iteration,
                    phase = step.Phase.ToString(),
                    content = step.Content,
                    success = step.Success,
                    toolCall = step.ToolCall != null ? new
                    {
                        toolName = step.ToolCall.ToolName,
                        arguments = step.ToolCall.Arguments
                    } : null,
                    wasIntercepted = step.WasIntercepted,
                    interceptReason = step.InterceptReason,
                    durationMs = step.DurationMs,
                    timestamp = step.Timestamp
                });
            }

            // 执行 ReAct 循环
            var result = await RunAgentWithReActAsync(
                roleId,
                taskDescription,
                sessionId: $"react-stream-{executionId}",
                maxIterations: maxIterations,
                onStepComplete: OnStepComplete,
                cancellationToken: cancellationToken);

            // 发送完成信号
            await _hubContext.Clients.Group(groupName).SendAsync("ReActComplete", new
            {
                executionId = executionId,
                success = result.Success,
                finalAnswer = result.FinalAnswer,
                totalIterations = result.TotalIterations,
                errorMessage = result.ErrorMessage,
                diagnosticReport = result.DiagnosticReport,
                durationMs = (result.EndTime - result.StartTime).TotalMilliseconds
            });

            return result;
        }

        #endregion

        #region Agent 工具绑定

        /// <summary>
        /// 为 Agent 绑定工具（Tools）
        /// 
        /// 实现思路（参考 WorkflowOrchestrationApp）：
        /// 1. 从 AI_Tool_Definition 加载工具定义
        /// 2. 使用 AIFunctionFactory.Create() 创建工具函数
        /// 3. 绑定到 ChatClientAgent 的 tools 参数
        /// 
        /// 加载角色授权的工具
        /// 
        /// 参考: WorkflowOrchestrationApp 中的 
        /// var tools = AIFunctionFactory.Create(GetCurrentTime, Calculate, GetWeather);
        /// var agent = new ChatClientAgent(_chatClient, instructions, name, tools: tools);
        /// </summary>
        /// <param name="roleId">角色 ID</param>
        /// <param name="skills">预加载的技能列表（可选）</param>
        /// <param name="originalUserRequest">用户原始请求（可选），用于传递完整上下文给技能执行</param>
        /// <param name="workflowId">当前工作流ID（可选），用于 SignalR 推送</param>
        /// <param name="taskId">当前任务ID（可选），用于 SignalR 推送</param>
        /// <returns>工具函数列表</returns>
        public async Task<List<AIFunction>> LoadToolsForRoleAsync(
            long roleId, 
            List<SkillContent>? skills = null,
            string? originalUserRequest = null,
            long? workflowId = null,
            long? taskId = null)
        {
            _logger.LogInformation("[工具加载] RoleId={RoleId}, WorkflowId={WorkflowId}, TaskId={TaskId}", roleId, workflowId, taskId);

            var tools = new List<AIFunction>();

            var runtimeUserId = CurrentUserInfo.Instance.UserID > 0
                ? CurrentUserInfo.Instance.UserID.ToString()
                : null;
            _memoryTools.SetContext(runtimeUserId, "default");

            tools.Add(AIFunctionFactory.Create(BuiltInTools.GetCurrentTime));
            tools.Add(AIFunctionFactory.Create(_memoryTools.SearchArchivalMemoryAsync));
            tools.Add(AIFunctionFactory.Create(_memoryTools.GetCoreUserProfileAsync));
            tools.Add(AIFunctionFactory.Create(_memoryTools.RememberUserProfileAsync));
            // ✅ 【Skill-as-Tool 模式】将技能转换为工具注册
            // Skills 不再注入 SystemPrompt，而是作为普通工具暴露给 LLM
            try
            {
                _skillToolAdapter.SetRoleContext(roleId);
                _skillToolAdapter.SetWorkflowContext(workflowId);
                _skillToolAdapter.SetTaskContext(taskId);
                
                // 🆕 设置原始用户请求上下文（解决技能调用丢失原始意图问题）
                if (!string.IsNullOrEmpty(originalUserRequest))
                {
                    _skillToolAdapter.SetOriginalUserRequest(originalUserRequest);
                    _logger.LogDebug("[工具加载] 已设置原始用户请求上下文");
                }
                
                var skillTools = await _skillToolAdapter.BuildSkillToolsAsync(roleId, skills);
                if (skillTools.Count > 0)
                {
                    tools.AddRange(skillTools);
                    _logger.LogInformation("[工具加载] ✅ 已加载 {Count} 个技能工具: {Names}", 
                        skillTools.Count, string.Join(", ", skillTools.Select(t => t.Name)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具加载] ❌ 加载技能工具失败");
            }

            // ✅ 使用改进后的动态加载 - 保留参数元数据
            try
            {
                // 1. 查询 Role_Tool_Relation 获取授权工具
                var roleToolRelations = await _roleToolRelationService.GetListAsync(
                    r => r.RoleID == roleId && r.IsDeleted == false);

                if (roleToolRelations.Count == 0)
                {
                    _logger.LogWarning("[工具加载] 角色{RoleId}未授权任何工具", roleId);

                }
                else
                {

                    var toolIds = roleToolRelations.Select(r => r.ToolID.Value).ToList();

                    // 2. 批量加载 Tool_Definition
                    var toolDefinitions = await _toolDefinitionService.GetListAsync(
                        t => toolIds.Contains(t.Id) && t.IsDeleted == false && t.IsEnabled == true);

                    _logger.LogInformation("[工具加载] 找到{Count}个授权工具", toolDefinitions.Count);

                    // 3. 根据 ToolType 创建 AIFunction
                    foreach (var tool in toolDefinitions)
                    {
                        try
                        {
                            AIFunction? aiFunction = tool.ToolType?.ToUpperInvariant() switch
                            {
                                "LOCAL" => CreateLocalToolFunction(tool, roleId, skills), // ✅ 传递 roleId 和 skills
                                "HTTP" => CreateHttpToolFunction(tool),
                                "MCP" => CreateMcpToolFunction(tool),
                                _ => null
                            };

                            if (aiFunction != null)
                            {
                                tools.Add(aiFunction);
                                _logger.LogInformation("[工具加载] ✅ 加载成功: {ToolName} ({ToolType})",
                                    tool.ToolName, tool.ToolType);
                            }
                            else
                            {
                                _logger.LogWarning("[工具加载] ⚠️ 不支持的工具类型: {ToolType}", tool.ToolType);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[工具加载] ❌ 加载失败: {ToolName}", tool.ToolName);
                        }
                    }

                    _logger.LogInformation("[工具加载] 完成: RoleId={RoleId}, 成功加载{Count}个工具",
                        roleId, tools.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具加载] 查询失败: RoleId={RoleId}", roleId);
            }

            return EnsureGeminiCompatibility(tools);
        }

        /// <summary>
        /// [已废弃] 手动注册工具 - 现在使用改进后的动态加载
        /// </summary>
        /// <summary>
        /// 创建本地工具（通过反射调用，支持动态类加载）
        /// </summary>
        private AIFunction? CreateLocalToolFunction(Tool_DefinitionEntity tool, long roleId, List<SkillContent>? skills = null)
        {
            try
            {
                if (string.IsNullOrEmpty(tool.LocalMethod))
                {
                    _logger.LogWarning("[工具加载] Local工具方法名为空: {ToolName}", tool.ToolName);
                    return null;
                }

                // 1. 获取目标类型
                Type? toolsType = null;
                
                if (!string.IsNullOrEmpty(tool.LocalClass))
                {
                    // 使用LocalClass指定的完整类名
                    try
                    {
                        // 从当前程序集加载类型
                        var assembly = typeof(AgentRuntimeService).Assembly;
                        toolsType = assembly.GetType(tool.LocalClass);
                        
                        if (toolsType == null)
                        {
                            _logger.LogWarning("[工具加载] 未找到类: {ClassName}", tool.LocalClass);
                            return null;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[工具加载] 加载类失败: {ClassName}", tool.LocalClass);
                        return null;
                    }
                }
                else
                {
                    // 默认使用BuiltInTools类
                    toolsType = typeof(HSJM.AI.AppLayer.Agent.Tools.BuiltInTools);
                }

                // 2. 查找方法（支持静态和实例方法）
                var method = toolsType.GetMethod(tool.LocalMethod, 
                    System.Reflection.BindingFlags.Public | 
                    System.Reflection.BindingFlags.Static | 
                    System.Reflection.BindingFlags.Instance);

                if (method == null)
                {
                    _logger.LogWarning("[工具加载] 未找到方法: {ClassName}.{MethodName}", 
                        tool.LocalClass ?? "BuiltInTools", tool.LocalMethod);
                    return null;
                }

                // 3. 创建AIFunction（根据静态/实例方法选择不同策略）
                AIFunction? aiFunction = null;
                
                if (method.IsStatic)
                {
                    // 静态方法：直接创建委托
                    aiFunction = CreateAIFunctionFromMethodInfo(method, null);
                }
                else
                {
                    // 实例方法：从DI容器获取实例
                    var toolInstance = _serviceProvider.GetService(toolsType);
                    if (toolInstance == null)
                    {
                        _logger.LogWarning("[工具加载] 无法从DI容器获取实例: {ClassName}", toolsType.Name);
                        return null;
                    }
                    
                    // ✅ 注入上下文和技能缓存
                    if (toolInstance is HSJM.AI.AppLayer.Agent.Tools.SkillAccessTool skillAccessTool)
                    {
                        // 设置上下文（RoleID），用于支持懒加载
                        skillAccessTool.SetContext(roleId);
                        
                        // 如果有预加载的 skills，直接初始化缓存（优化）
                        if (skills != null)
                        {
                            skillAccessTool.InitializeSkillCache(skills);
                            _logger.LogInformation("[工具加载] ✅ SkillAccessTool 缓存预初始化: {Count} 个技能", skills.Count);
                        }
                    }
                    
                    aiFunction = CreateAIFunctionFromMethodInfo(method, toolInstance);
                }

                if (aiFunction != null)
                {
                    _logger.LogInformation("[工具加载] ✅ Local工具加载成功: {ToolName} -> {Class}.{Method}", 
                        tool.ToolName, toolsType.Name, tool.LocalMethod);
                }

                return aiFunction;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具加载] Local工具加载失败: {ToolName}", tool.ToolName);
                return null;
            }
        }

        /// <summary>
        /// 从MethodInfo创建AIFunction（支持静态和实例方法）
        /// 直接传递方法引用，保留参数元数据
        /// </summary>
        private AIFunction? CreateAIFunctionFromMethodInfo(System.Reflection.MethodInfo method, object? instance)
        {
            try
            {
                // 直接创建委托 - 保留参数元数据
                // AIFunctionFactory.Create 可以直接接受方法引用并提取 [Description]
                
                if (instance != null)
                {
                    // 对于实例方法，创建包含实例的委托
                    // 检查是否是异步方法
                    var returnType = method.ReturnType;
                    if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        // 异步方法：使用 Func<..., Task<TResult>>
                        var resultType = returnType.GetGenericArguments()[0];
                        var parameters = method.GetParameters();
                        
                        // 根据参数数量构建正确的委托类型
                        Type delegateType = parameters.Length switch
                        {
                            0 => typeof(Func<>).MakeGenericType(returnType),
                            1 => typeof(Func<,>).MakeGenericType(parameters[0].ParameterType, returnType),
                            2 => typeof(Func<,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, returnType),
                            3 => typeof(Func<,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, returnType),
                            4 => typeof(Func<,,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, returnType),
                            5 => typeof(Func<,,,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, parameters[4].ParameterType, returnType),
                            _ => null
                        };
                        
                        if (delegateType != null)
                        {
                            var methodDelegate = method.CreateDelegate(delegateType, instance);
                            _logger.LogInformation("[工具加载] ✅ 直接创建委托成功: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return AIFunctionFactory.Create(methodDelegate);
                        }
                        else
                        {
                            _logger.LogError("[工具加载] ❌ 不支持的参数数量: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return null;
                        }
                    }
                    else if (returnType == typeof(Task))
                    {
                        // 异步无返回值方法
                        var parameters = method.GetParameters();
                        Type delegateType = parameters.Length switch
                        {
                            0 => typeof(Func<Task>),
                            1 => typeof(Func<,>).MakeGenericType(parameters[0].ParameterType, typeof(Task)),
                            2 => typeof(Func<,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, typeof(Task)),
                            3 => typeof(Func<,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, typeof(Task)),
                            4 => typeof(Func<,,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, typeof(Task)),
                            5 => typeof(Func<,,,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, parameters[4].ParameterType, typeof(Task)),
                            _ => null
                        };
                        
                        if (delegateType != null)
                        {
                            var methodDelegate = method.CreateDelegate(delegateType, instance);
                            _logger.LogInformation("[工具加载] ✅ 直接创建委托成功: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return AIFunctionFactory.Create(methodDelegate);
                        }
                        else
                        {
                            _logger.LogError("[工具加载] ❌ 不支持的参数数量: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return null;
                        }
                    }
                    else
                    {
                        // 同步方法
                        var delegateType = GetDelegateTypeForMethod(method);
                        if (delegateType != null)
                        {
                            var methodDelegate = method.CreateDelegate(delegateType, instance);
                            var parameters = method.GetParameters();
                            _logger.LogInformation("[工具加载] ✅ 直接创建委托成功: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return AIFunctionFactory.Create(methodDelegate);
                        }
                        else
                        {
                            _logger.LogError("[工具加载] ❌ 不支持的返回类型或参数数量: {Method}", method.Name);
                            return null;
                        }
                    }
                }
                else
                {
                    // 静态方法
                    var returnType = method.ReturnType;
                    if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        // 异步方法
                        var parameters = method.GetParameters();
                        Type delegateType = parameters.Length switch
                        {
                            0 => typeof(Func<>).MakeGenericType(returnType),
                            1 => typeof(Func<,>).MakeGenericType(parameters[0].ParameterType, returnType),
                            2 => typeof(Func<,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, returnType),
                            3 => typeof(Func<,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, returnType),
                            4 => typeof(Func<,,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, returnType),
                            5 => typeof(Func<,,,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, parameters[4].ParameterType, returnType),
                            _ => null
                        };
                        
                        if (delegateType != null)
                        {
                            var methodDelegate = method.CreateDelegate(delegateType);
                            _logger.LogInformation("[工具加载] ✅ 直接创建静态委托成功: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return AIFunctionFactory.Create(methodDelegate);
                        }
                        else
                        {
                            _logger.LogError("[工具加载] ❌ 不支持的参数数量: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return null;
                        }
                    }
                    else if (returnType == typeof(Task))
                    {
                        // 异步无返回值
                        var parameters = method.GetParameters();
                        Type delegateType = parameters.Length switch
                        {
                            0 => typeof(Func<Task>),
                            1 => typeof(Func<,>).MakeGenericType(parameters[0].ParameterType, typeof(Task)),
                            2 => typeof(Func<,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, typeof(Task)),
                            3 => typeof(Func<,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, typeof(Task)),
                            4 => typeof(Func<,,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, typeof(Task)),
                            5 => typeof(Func<,,,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, parameters[4].ParameterType, typeof(Task)),
                            _ => null
                        };
                        
                        if (delegateType != null)
                        {
                            var methodDelegate = method.CreateDelegate(delegateType);
                            _logger.LogInformation("[工具加载] ✅ 直接创建静态委托成功: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return AIFunctionFactory.Create(methodDelegate);
                        }
                        else
                        {
                            _logger.LogError("[工具加载] ❌ 不支持的参数数量: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return null;
                        }
                    }
                    else
                    {
                        // 同步方法
                        var delegateType = GetDelegateTypeForMethod(method);
                        if (delegateType != null)
                        {
                            var methodDelegate = method.CreateDelegate(delegateType);
                            var parameters = method.GetParameters();
                            _logger.LogInformation("[工具加载] ✅ 直接创建静态委托成功: {Method} ({ParamCount}参数)", method.Name, parameters.Length);
                            return AIFunctionFactory.Create(methodDelegate);
                        }
                        else
                        {
                            _logger.LogError("[工具加载] ❌ 不支持的返回类型或参数数量: {Method}", method.Name);
                            return null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具加载] ❌ 创建AIFunction失败: {Method}", method.Name);
                return null;
            }
        }


        
        /// <summary>
        /// 根据方法签名获取对应的委托类型
        /// 用于创建强类型委托以保留参数元数据
        /// </summary>
        private Type? GetDelegateTypeForMethod(System.Reflection.MethodInfo method)
        {
            var parameters = method.GetParameters();
            var returnType = method.ReturnType;
            
            // 简化处理：仅支持常见签名
            // Func<>系列：有返回值
            // Action<>系列：无返回值
            
            if (returnType == typeof(void))
            {
                // Action类型
                return parameters.Length switch
                {
                    0 => typeof(Action),
                    1 => typeof(Action<>).MakeGenericType(parameters[0].ParameterType),
                    2 => typeof(Action<,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType),
                    3 => typeof(Action<,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType),
                    4 => typeof(Action<,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType),
                    _ => null
                };
            }
            else if (returnType == typeof(string))
            {
                // Func<..., string>
                return parameters.Length switch
                {
                    0 => typeof(Func<string>),
                    1 => typeof(Func<,>).MakeGenericType(parameters[0].ParameterType, typeof(string)),
                    2 => typeof(Func<,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, typeof(string)),
                    3 => typeof(Func<,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, typeof(string)),
                    4 => typeof(Func<,,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, typeof(string)),
                    _ => null
                };
            }
            else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                // Task<T> - 异步方法暂不支持直接委托转换
                return null;
            }
            
            else if (returnType.IsClass)
            {
                // ✨ 自动支持任意类返回（自动序列化为 JSON）
                // Func<..., T> 适配为 Func<..., string>
                return parameters.Length switch
                {
                    0 => typeof(Func<>).MakeGenericType(returnType),
                    1 => typeof(Func<,>).MakeGenericType(parameters[0].ParameterType, returnType),
                    2 => typeof(Func<,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, returnType),
                    3 => typeof(Func<,,,>).MakeGenericType(parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, returnType),
                    _ => null
                };
            }
            
            return null;
        }


        /// <summary>
        /// 创建HTTP工具（包装API调用）
        /// </summary>
        private AIFunction? CreateHttpToolFunction(Tool_DefinitionEntity tool)
        {
            try
            {
                if (string.IsNullOrEmpty(tool.ApiUrl))
                {
                    _logger.LogWarning("[工具加载] HTTP工具缺少 ApiUrl: {ToolName}", tool.ToolName);
                    return null;
                }

                // 创建一个包装委托来调用 HTTP API
                Func<string, Task<string>> httpToolFunc = async (paramsJson) =>
                {
                    try
                    {
                        var httpClient = _httpClientFactory.CreateClient();
                        
                        // 设置请求头
                        if (!string.IsNullOrEmpty(tool.ApiHeaders))
                        {
                            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(tool.ApiHeaders);
                            if (headers != null)
                            {
                                foreach (var header in headers)
                                {
                                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                                }
                            }
                        }

                        // 设置超时
                        if (tool.TimeoutSeconds.HasValue && tool.TimeoutSeconds.Value > 0)
                        {
                            httpClient.Timeout = TimeSpan.FromSeconds(tool.TimeoutSeconds.Value);
                        }

                        HttpResponseMessage response;
                        var method = tool.ApiMethod?.ToUpperInvariant() ?? "POST";

                        if (method == "GET")
                        {
                            // GET 请求：参数作为 Query String
                            var url = tool.ApiUrl;
                            if (!string.IsNullOrEmpty(paramsJson))
                            {
                                var separator = url.Contains("?") ? "&" : "?";
                                url = $"{url}{separator}params={Uri.EscapeDataString(paramsJson)}";
                            }
                            response = await httpClient.GetAsync(url);
                        }
                        else
                        {
                            // POST 请求：参数作为 JSON Body
                            var content = new StringContent(
                                paramsJson ?? "{}", 
                                System.Text.Encoding.UTF8, 
                                "application/json");
                            response = await httpClient.PostAsync(tool.ApiUrl, content);
                        }

                        var result = await response.Content.ReadAsStringAsync();
                        
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("[HTTP工具] 请求失败: {ToolName}, 状态码={StatusCode}", 
                                tool.ToolName, response.StatusCode);
                            return JsonHelper.SerializeObject(new { 
                                success = false, 
                                error = $"HTTP {(int)response.StatusCode}", 
                                body = result 
                            });
                        }

                        return result;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[HTTP工具] 调用失败: {ToolName}", tool.ToolName);
                        return JsonHelper.SerializeObject(new { success = false, error = ex.Message });
                    }
                };

                var aiFunction = AIFunctionFactory.Create(
                    httpToolFunc,
                    name: tool.ToolCode ?? tool.ToolName,
                    description: tool.Description ?? $"HTTP工具: {tool.ToolName}"
                );

                _logger.LogInformation("[工具加载] ✅ HTTP工具加载成功: {ToolName} -> {ApiUrl}", 
                    tool.ToolName, tool.ApiUrl);
                return aiFunction;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具加载] HTTP工具加载失败: {ToolName}", tool.ToolName);
                return null;
            }
        }

        /// <summary>
        /// 创建MCP工具（通过MCP协议调用）
        /// </summary>
        private AIFunction? CreateMcpToolFunction(Tool_DefinitionEntity tool)
        {
            try
            {
                if (!tool.McpServerID.HasValue || tool.McpServerID.Value <= 0)
                {
                    _logger.LogWarning("[工具加载] MCP工具缺少 McpServerID: {ToolName}", tool.ToolName);
                    return null;
                }

                var mcpServerId = tool.McpServerID.Value;
                var mcpToolName = tool.McpToolName ?? tool.ToolCode ?? tool.ToolName;

                // 创建一个包装委托来调用 MCP 工具
                Func<string, Task<string>> mcpToolFunc = async (paramsJson) =>
                {
                    try
                    {
                        var arguments = string.IsNullOrEmpty(paramsJson)
                            ? null
                            : JsonSerializer.Deserialize<Dictionary<string, object?>>(paramsJson);

                        return await _mcpClientService.CallToolAsync(mcpServerId, mcpToolName, arguments);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MCP工具] 调用失败: {ToolName}", tool.ToolName);
                        return JsonHelper.SerializeObject(new { success = false, error = ex.Message });
                    }
                };

                var aiFunction = AIFunctionFactory.Create(
                    mcpToolFunc,
                    name: tool.ToolCode ?? mcpToolName,
                    description: tool.Description ?? $"MCP工具: {mcpToolName}"
                );

                _logger.LogInformation("[工具加载] ✅ MCP工具加载成功: {ToolName} -> Server={ServerId}, Tool={McpToolName}", 
                    tool.ToolName, mcpServerId, mcpToolName);
                return aiFunction;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工具加载] MCP工具加载失败: {ToolName}", tool.ToolName);
                return null;
            }
        }

        /// <summary>
        /// 创建示例工具（参考 WorkflowOrchestrationApp）
        /// </summary>
        private List<AIFunction> CreateDemoTools()
        {
            // 每个方法分别调用 AIFunctionFactory.Create() 创建 AIFunction
            return new List<AIFunction>
            {
                AIFunctionFactory.Create(GetCurrentTime),
                AIFunctionFactory.Create(Calculate)
            };
        }

        #endregion

        #region 示例工具函数（参考 WorkflowOrchestrationApp）

        [Description("获取当前系统时间")]
        private static string GetCurrentTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss dddd");

        [Description("执行数学表达式计算")]
        private static string Calculate([Description("数学表达式，如 2+3*4")] string expression)
        {
            try
            {
                // 简单计算器实现
                var result = new System.Data.DataTable().Compute(expression, null);
                return $"计算结果: {result}";
            }
            catch (Exception ex)
            {
                return $"计算失败: {ex.Message}";
            }
        }

        #endregion

        #region Gemini 兼容性处理

        /// <summary>
        /// 确保工具定义兼容 Gemini (Google Vertex AI)
        /// 修复 "Invalid value at ... properties[0].value ... true" 错误
        /// 原因: .NET 默认生成的 JSON Schema 可能包含 "param": true (表示任意类型)，但 Gemini 要求必须是对象类型
        /// </summary>
        private List<AIFunction> EnsureGeminiCompatibility(List<AIFunction> tools)
        {
            var result = new List<AIFunction>();
            foreach (var tool in tools)
            {
                try
                {
                    // 始终检查并尝试修复，不再依赖简单的字符串匹配（可能因格式问题失效）
                    var schemaJson = tool.JsonSchema.ValueKind == JsonValueKind.Undefined 
                        ? null 
                        : tool.JsonSchema.GetRawText();
                        
                    if (!string.IsNullOrEmpty(schemaJson))
                    {
                        var jsonNode = JsonNode.Parse(schemaJson);
                        if (jsonNode != null && FixBooleanSchemas(jsonNode))
                        {
                            _logger.LogWarning("[工具兼容性] 修复 Gemini Schema: {ToolName}", tool.Name);
                            _logger.LogDebug("[工具兼容性] 原Schema: {Old}, 新Schema: {New}", schemaJson, jsonNode.ToJsonString());
                            
                            // 创建兼容性包装器
                            result.Add(new GeminiCompatibleAIFunction(tool, jsonNode));
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[工具兼容性] 检查失败: {ToolName}", tool.Name);
                }

                result.Add(tool);
            }
            return result;
        }

        /// <summary>
        /// 递归修复 JSON Schema 中的 Boolean True
        /// </summary>
        private bool FixBooleanSchemas(JsonNode node)
        {
            bool modified = false;

            if (node is JsonObject obj)
            {
                // 检查 properties
                if (obj.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject propsObj)
                {
                    // 必须转换为列表处理以允许修改集合
                    var propList = propsObj.ToList();
                    foreach (var prop in propList)
                    {
                        // 逻辑增强：显式检查 JsonValueKind.True
                        bool isTrue = false;
                        if (prop.Value is JsonValue val)
                        {
                            if (val.GetValueKind() == JsonValueKind.True) 
                            {
                                isTrue = true;
                            }
                            else if (val.TryGetValue<bool>(out var boolVal) && boolVal == true)
                            {
                                isTrue = true;
                            }
                        }

                        if (isTrue)
                        {
                            // 发现 "param": true (Gemini 不支持)
                            // 强制替换为 {"type": "string", "description": "Auto-fixed parameter"}
                            
                            var safeSchema = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Input parameter (auto-fixed compatibility)"
                            };
                            
                            propsObj[prop.Key] = safeSchema; 
                            modified = true;
                            _logger.LogWarning("[工具兼容性] 强制修复属性 {PropName}: true -> type:string", prop.Key);
                        }
                        else if (prop.Value != null)
                        {
                            if (FixBooleanSchemas(prop.Value))
                            {
                                modified = true;
                            }
                        }
                    }
                }

                // 递归检查其他对象属性
                foreach (var property in obj)
                {
                    if (property.Key != "properties" && property.Value != null) 
                    {
                        if (FixBooleanSchemas(property.Value))
                        {
                            modified = true;
                        }
                    }
                }
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item != null && FixBooleanSchemas(item))
                    {
                        modified = true;
                    }
                }
            }

            return modified;
        }

        /// <summary>
        /// Gemini 兼容性包装器
        /// </summary>
        private class GeminiCompatibleAIFunction : AIFunction
        {
            private readonly AIFunction _inner;
            private readonly JsonElement _fixedSchema;

            public GeminiCompatibleAIFunction(AIFunction inner, JsonNode newSchema) 
            {
                _inner = inner;
                _fixedSchema = JsonSerializer.SerializeToElement(newSchema);
            }

            public override string Name => _inner.Name;
            public override string Description => _inner.Description;
            public override JsonElement JsonSchema => _fixedSchema;
            public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

            protected override ValueTask<object?> InvokeCoreAsync(
                AIFunctionArguments arguments, 
                CancellationToken cancellationToken)
            {
                // 转发调用
                return _inner.InvokeAsync(arguments, cancellationToken);
            }
        }

        #endregion
    }

    #region 数据模型

    /// <summary>
    /// Agent 执行响应
    /// </summary>
    public class AgentResponse
    {
        public string AgentName { get; set; } = "";
        public string Content { get; set; } = "";
        public object? RawResponse { get; set; }
        public double ExecutionTimeMs { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    #endregion
}
