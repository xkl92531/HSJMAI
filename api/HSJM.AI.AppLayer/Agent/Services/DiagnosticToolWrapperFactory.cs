using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 诊断工具包装器工厂
    /// 
    /// 核心功能：
    /// 包装 AIFunction，在每次工具调用前后注入诊断逻辑
    /// 
    /// 工作流程：
    /// 1. Agent 框架调用工具时，实际调用的是包装后的函数
    /// 2. 包装函数先检查 ToolCallTracker（是否应该执行）
    /// 3. 如果不应该执行，直接返回拦截消息
    /// 4. 如果应该执行，调用原始函数并记录结果
    /// 
    /// 这样即使框架内部的工具调用是黑盒，我们也能在工具层实现控制
    /// </summary>
    public class DiagnosticToolWrapperFactory : ISelfScopedAutoInject
    {
        private readonly ILogger<DiagnosticToolWrapperFactory> _logger;
        private readonly ToolCallTrackerService _trackerService;

        public DiagnosticToolWrapperFactory(
            ILogger<DiagnosticToolWrapperFactory> logger,
            ToolCallTrackerService trackerService)
        {
            _logger = logger;
            _trackerService = trackerService;
        }

        /// <summary>
        /// 包装 AIFunction，注入调用追踪和诊断逻辑
        /// 使用代理模式包装原始工具
        /// </summary>
        /// <param name="originalFunction">原始 AIFunction</param>
        /// <param name="sessionId">追踪会话ID（通常是 TaskId）</param>
        /// <returns>包装后的 AIFunction</returns>
        public AIFunction WrapWithDiagnostics(AIFunction originalFunction, string sessionId)
        {
            var toolName = originalFunction.Name;
            var description = originalFunction.Description;

            _logger.LogDebug("[DiagnosticWrapper] 包装工具: {ToolName}, Session={SessionId}", toolName, sessionId);

            // 创建包装函数
            Func<string, Task<string>> wrappedFunc = async (paramsJson) =>
            {
                return await ExecuteWithDiagnosticsAsync(originalFunction, sessionId, paramsJson);
            };

            // 使用 AIFunctionFactory 创建新的 AIFunction
            var wrappedFunction = AIFunctionFactory.Create(
                wrappedFunc,
                name: toolName,
                description: description ?? $"工具: {toolName}"
            );

            return wrappedFunction;
        }

        /// <summary>
        /// 带诊断的工具执行
        /// </summary>
        private async Task<string> ExecuteWithDiagnosticsAsync(
            AIFunction originalFunction, 
            string sessionId, 
            string? paramsJson)
        {
            var toolName = originalFunction.Name;
            var startTime = DateTime.UtcNow;

            // 1. 检查是否应该执行
            var checkResult = _trackerService.ShouldExecute(sessionId, toolName, paramsJson);

            if (!checkResult.ShouldExecute)
            {
                _logger.LogWarning("[DiagnosticWrapper] 工具调用被拦截: {Tool}, 原因={Reason}, 消息={Message}",
                    toolName, checkResult.Reason, checkResult.Message);

                // 如果有缓存结果，返回缓存
                if (!string.IsNullOrEmpty(checkResult.CachedResult))
                {
                    return $"[使用缓存结果] {checkResult.CachedResult}\n\n{checkResult.Message}";
                }

                return checkResult.Message;
            }

            // 2. 如果有警告，在日志中记录
            if (checkResult.HasWarning)
            {
                _logger.LogWarning("[DiagnosticWrapper] 工具调用警告: {Tool}, {Warning}",
                    toolName, checkResult.Warning);
            }

            // 3. 解析参数并执行原始工具
            string resultStr = "[无返回值]";  // 初始化避免 CS0165
            bool success = false;

            try
            {
                _logger.LogInformation("[DiagnosticWrapper] 执行工具: {Tool}", toolName);

                // 解析 JSON 参数为字典
                Dictionary<string, object?>? argsDict = null;
                if (!string.IsNullOrEmpty(paramsJson))
                {
                    try
                    {
                        var jsonDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsJson);
                        if (jsonDict != null)
                        {
                            argsDict = new Dictionary<string, object?>();
                            foreach (var kvp in jsonDict)
                            {
                                argsDict[kvp.Key] = ConvertJsonElement(kvp.Value);
                            }
                        }
                    }
                    catch
                    {
                        _logger.LogWarning("[DiagnosticWrapper] 参数解析失败: {Params}", paramsJson);
                    }
                }

                // 调用原始工具（AIFunction.InvokeAsync 接受 AIFunctionArguments）
                var aiArgs = argsDict != null ? new AIFunctionArguments(argsDict) : null;
                var result = await originalFunction.InvokeAsync(aiArgs);
                resultStr = result?.ToString() ?? "[无返回值]";
                success = true;

                // 截断过长的结果
                if (resultStr.Length > 2000)
                {
                    resultStr = resultStr.Substring(0, 2000) + "...[截断]";
                }

                _logger.LogInformation("[DiagnosticWrapper] 工具执行成功: {Tool}, 结果长度={Len}",
                    toolName, resultStr.Length);
            }
            catch (Exception ex)
            {
                success = false;
                resultStr = $"执行失败: {ex.Message}";
                _logger.LogError(ex, "[DiagnosticWrapper] 工具执行失败: {Tool}", toolName);
            }
            finally
            {
                // 4. 记录调用
                var durationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
                _trackerService.RecordCall(sessionId, toolName, paramsJson, resultStr, success, durationMs);
            }

            // 5. 如果有警告，附加到结果中
            if (checkResult.HasWarning)
            {
                return $"{resultStr}\n\n[系统提示] {checkResult.Warning}";
            }

            return resultStr;
        }

        /// <summary>
        /// 批量包装工具列表
        /// </summary>
        public List<AIFunction> WrapAll(List<AIFunction> functions, string sessionId)
        {
            var wrapped = new List<AIFunction>();
            foreach (var func in functions)
            {
                wrapped.Add(WrapWithDiagnostics(func, sessionId));
            }

            _logger.LogInformation("[DiagnosticWrapper] 已包装 {Count} 个工具, Session={SessionId}",
                wrapped.Count, sessionId);

            return wrapped;
        }

        /// <summary>
        /// 转换 JsonElement 到 object
        /// </summary>
        private static object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.ToString()
            };
        }
    }
}
