using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// MCP 客户端服务 - 管理与 MCP Server 的连接和工具调用
    /// 支持 stdio 和 SSE 两种传输方式
    /// </summary>
    public class McpClientService : ISelfScopedAutoInject, IDisposable
    {
        private readonly ILogger<McpClientService> _logger;
        private readonly IMcp_ServerService _mcpServerService;
        
        // 连接池：缓存已建立的 MCP 客户端连接
        private readonly ConcurrentDictionary<long, McpClient> _clientPool = new();
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        public McpClientService(
            ILogger<McpClientService> logger,
            IMcp_ServerService mcpServerService)
        {
            _logger = logger;
            _mcpServerService = mcpServerService;
        }

        #region 连接管理

        /// <summary>
        /// 获取或创建 MCP 客户端连接
        /// </summary>
        public async Task<McpClient?> GetOrCreateClientAsync(long mcpServerId, CancellationToken cancellationToken = default)
        {
            // 先检查缓存
            if (_clientPool.TryGetValue(mcpServerId, out var existingClient))
            {
                return existingClient;
            }

            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                // 双重检查
                if (_clientPool.TryGetValue(mcpServerId, out existingClient))
                {
                    return existingClient;
                }

                // 加载 MCP Server 配置
                var serverConfig = await _mcpServerService.GetByIdAsync(mcpServerId);
                if (serverConfig == null || serverConfig.IsDeleted || serverConfig.IsEnabled != true)
                {
                    _logger.LogWarning("[MCP] 服务器配置不存在或已禁用: ServerId={ServerId}", mcpServerId);
                    return null;
                }

                // 创建新连接
                var client = await CreateClientAsync(serverConfig, cancellationToken);
                if (client != null)
                {
                    _clientPool.TryAdd(mcpServerId, client);
                    _logger.LogInformation("[MCP] ✅ 客户端连接已建立: {ServerName} (ID={ServerId})", 
                        serverConfig.ServerName, mcpServerId);
                }

                return client;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// 根据配置创建 MCP 客户端
        /// </summary>
        private async Task<McpClient?> CreateClientAsync(Mcp_ServerEntity serverConfig, CancellationToken cancellationToken)
        {
            try
            {
                var serverType = serverConfig.ServerType?.ToUpperInvariant() ?? "STDIO";
                var connectionConfig = string.IsNullOrEmpty(serverConfig.ConnectionConfig)
                    ? new McpConnectionConfig()
                    : JsonSerializer.Deserialize<McpConnectionConfig>(serverConfig.ConnectionConfig) ?? new McpConnectionConfig();

                IClientTransport transport;

                if (serverType == "SSE" || serverType == "HTTP")
                {
                    // HTTP/SSE 传输
                    if (string.IsNullOrEmpty(connectionConfig.Url))
                    {
                        _logger.LogError("[MCP] HTTP 服务器缺少 URL 配置: {ServerName}", serverConfig.ServerName);
                        return null;
                    }
                    transport = new HttpClientTransport(new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(connectionConfig.Url),
                        Name = serverConfig.ServerName
                    });
                }
                else
                {
                    // STDIO 传输（默认）
                    if (string.IsNullOrEmpty(connectionConfig.Command))
                    {
                        _logger.LogError("[MCP] STDIO 服务器缺少 Command 配置: {ServerName}", serverConfig.ServerName);
                        return null;
                    }
                    transport = new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Command = connectionConfig.Command,
                        Arguments = connectionConfig.Arguments ?? Array.Empty<string>(),
                        Name = serverConfig.ServerName,
                        EnvironmentVariables = connectionConfig.Environment
                    });
                }

                _logger.LogInformation("[MCP] 正在连接: {ServerName} ({ServerType})", 
                    serverConfig.ServerName, serverType);

                var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
                return client;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MCP] 创建客户端失败: {ServerName}", serverConfig.ServerName);
                return null;
            }
        }

        #endregion

        #region 工具操作

        /// <summary>
        /// 获取 MCP Server 提供的所有工具
        /// </summary>
        public async Task<IList<McpClientTool>> ListToolsAsync(long mcpServerId, CancellationToken cancellationToken = default)
        {
            var client = await GetOrCreateClientAsync(mcpServerId, cancellationToken);
            if (client == null)
            {
                return Array.Empty<McpClientTool>();
            }

            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                _logger.LogInformation("[MCP] 获取工具列表: ServerId={ServerId}, 工具数={Count}", 
                    mcpServerId, tools.Count);
                return tools;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MCP] 获取工具列表失败: ServerId={ServerId}", mcpServerId);
                return Array.Empty<McpClientTool>();
            }
        }

        /// <summary>
        /// 调用 MCP 工具
        /// </summary>
        public async Task<string> CallToolAsync(
            long mcpServerId, 
            string toolName, 
            Dictionary<string, object?>? arguments = null,
            CancellationToken cancellationToken = default)
        {
            var client = await GetOrCreateClientAsync(mcpServerId, cancellationToken);
            if (client == null)
            {
                return JsonSerializer.Serialize(new { success = false, error = "MCP 客户端连接失败" });
            }

            try
            {
                _logger.LogInformation("[MCP] 调用工具: {ToolName} @ ServerId={ServerId}", toolName, mcpServerId);

                var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);

                // 提取文本内容 - 使用 OfType<TextContentBlock>() 过滤
                var textContent = result.Content
                    .OfType<TextContentBlock>()
                    .Select(c => c.Text)
                    .FirstOrDefault() ?? "";

                _logger.LogInformation("[MCP] ✅ 工具调用成功: {ToolName}, 结果长度={Length}", 
                    toolName, textContent.Length);

                return textContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MCP] ❌ 工具调用失败: {ToolName} @ ServerId={ServerId}", 
                    toolName, mcpServerId);
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// 将 MCP 工具包装为 AIFunction（用于注入到 Agent）
        /// </summary>
        public AIFunction WrapMcpToolAsAIFunction(long mcpServerId, McpClientTool mcpTool)
        {
            // 创建一个委托方法来调用 MCP 工具
            Func<string, Task<string>> toolFunc = async (paramsJson) =>
            {
                var arguments = string.IsNullOrEmpty(paramsJson)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, object?>>(paramsJson);
                return await CallToolAsync(mcpServerId, mcpTool.Name, arguments);
            };

            // 使用 AIFunctionFactory 创建 AIFunction
            var aiFunction = AIFunctionFactory.Create(
                toolFunc,
                name: mcpTool.Name,
                description: mcpTool.Description ?? $"MCP工具: {mcpTool.Name}"
            );

            _logger.LogDebug("[MCP] 已包装工具为AIFunction: {ToolName}", mcpTool.Name);
            return aiFunction;
        }

        /// <summary>
        /// 获取 MCP Server 的所有工具作为 AIFunction 列表
        /// 注意：McpClientTool 继承自 AIFunction，可以直接使用
        /// </summary>
        public async Task<IList<AIFunction>> GetToolsAsAIFunctionsAsync(
            long mcpServerId, 
            CancellationToken cancellationToken = default)
        {
            var mcpTools = await ListToolsAsync(mcpServerId, cancellationToken);
            // McpClientTool 继承自 AIFunction，直接转型即可
            var aiFunctions = mcpTools.Cast<AIFunction>().ToList();
            
            _logger.LogInformation("[MCP] 已获取 {Count} 个工具作为 AIFunction", aiFunctions.Count);
            return aiFunctions;
        }

        #endregion

        #region 资源清理

        /// <summary>
        /// 关闭指定服务器的连接
        /// </summary>
        public async Task DisconnectAsync(long mcpServerId)
        {
            if (_clientPool.TryRemove(mcpServerId, out var client))
            {
                try
                {
                    await client.DisposeAsync();
                    _logger.LogInformation("[MCP] 已断开连接: ServerId={ServerId}", mcpServerId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[MCP] 断开连接时发生错误: ServerId={ServerId}", mcpServerId);
                }
            }
        }

        /// <summary>
        /// 关闭所有连接
        /// </summary>
        public async Task DisconnectAllAsync()
        {
            foreach (var serverId in _clientPool.Keys.ToList())
            {
                await DisconnectAsync(serverId);
            }
        }

        public void Dispose()
        {
            DisconnectAllAsync().GetAwaiter().GetResult();
            _connectionLock.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// MCP 连接配置（对应 ConnectionConfig JSON 字段）
    /// </summary>
    public class McpConnectionConfig
    {
        /// <summary>
        /// SSE 服务 URL
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// STDIO 命令
        /// </summary>
        public string? Command { get; set; }

        /// <summary>
        /// STDIO 命令参数
        /// </summary>
        public string[]? Arguments { get; set; }

        /// <summary>
        /// 环境变量
        /// </summary>
        public Dictionary<string, string>? Environment { get; set; }
    }
}
