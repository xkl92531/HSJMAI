using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;

namespace HSJM.AI.Mcp
{
    /// <summary>
    /// MCP 调用日志的持久化边界。
    /// </summary>
    public interface IMcpInvocationLogSink
    {
        /// <summary>
        /// 写入一条独立的 JSON 日志。
        /// </summary>
        Task WriteAsync(string line);
    }

    /// <summary>
    /// 创建并维护可通过 CallId 关联的 MCP 调用日志作用域。
    /// </summary>
    public sealed class McpInvocationLogger
    {
        /// <summary>
        /// 单个输入或输出载荷的默认最大字符数。
        /// </summary>
        public const int DefaultMaxPayloadCharacters = 32 * 1024;
        private const string Redacted = "REDACTED";

        private static readonly HashSet<string> SensitiveNames = new HashSet<string>(
            new[]
            {
                "password",
                "pwd",
                "authorization",
                "cookie",
                "setcookie",
                "token",
                "accesstoken",
                "refreshtoken",
                "securitykey",
                "apikey",
                "connectionstring",
                "prompt",
                "systemprompt",
                "reasoning",
                "chainofthought",
                "process",
                "rawresult",
                "rawresponse",
            },
            StringComparer.Ordinal);

        private static readonly Regex BearerRegex = new Regex(
            @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ConnectionStringRegex = new Regex(
            @"(?i)\b(?:Host|Server|Data\s+Source)\s*=\s*[^;\r\n]+(?:\s*;\s*[^;=\r\n]+\s*=\s*[^;\r\n]+){2,}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ConnectionUriRegex = new Regex(
            @"(?i)\b(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|redis)://[^\s;]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex EmbeddedCredentialRegex = new Regex(
            @"(?i)\b(password|pwd|api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|security[_-]?key)\b\s*[:=]\s*[^;\r\n]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            WriteIndented = false,
        };

        private readonly IMcpInvocationLogSink _sink;
        private readonly int _maxPayloadCharacters;

        /// <summary>
        /// 初始化 MCP 调用日志记录器。
        /// </summary>
        public McpInvocationLogger(
            IMcpInvocationLogSink sink,
            int maxPayloadCharacters = DefaultMaxPayloadCharacters)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            if (maxPayloadCharacters <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPayloadCharacters));
            }

            _maxPayloadCharacters = maxPayloadCharacters;
        }

        /// <summary>
        /// 创建调用作用域并写入输入事件。日志故障不会向调用方传播。
        /// </summary>
        public async Task<McpInvocationScope> StartAsync(string toolName, object input)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                throw new ArgumentException("MCP tool name is required.", nameof(toolName));
            }

            var scope = new McpInvocationScope(
                this,
                Guid.NewGuid().ToString("N"),
                toolName.Trim());
            await WriteEventAsync(scope, "input", input).ConfigureAwait(false);
            return scope;
        }

        internal async Task WriteEventAsync(
            McpInvocationScope scope,
            string eventName,
            object data,
            int? sequence = null,
            long? durationMs = null)
        {
            try
            {
                var payload = PreparePayload(data);
                var record = new JsonObject
                {
                    ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["callId"] = scope.CallId,
                    ["tool"] = scope.ToolName,
                    ["event"] = eventName,
                    ["sequence"] = sequence,
                    ["durationMs"] = durationMs,
                    ["data"] = payload.Data,
                    ["payloadTruncated"] = payload.Truncated ? true : null,
                    ["payloadOriginalCharacters"] = payload.Truncated
                        ? payload.OriginalCharacters
                        : null,
                };

                await _sink.WriteAsync(record.ToJsonString(JsonOptions)).ConfigureAwait(false);
            }
            catch
            {
                // 调用日志仅用于可观测性，写入失败不能改变 MCP 业务结果。
            }
        }

        private Payload PreparePayload(object data)
        {
            JsonNode node;
            try
            {
                node = JsonSerializer.SerializeToNode(data, JsonOptions) ?? JsonValue.Create((string)null);
                RedactNode(node);
            }
            catch
            {
                node = JsonValue.Create($"[UNSERIALIZABLE:{data?.GetType().Name ?? "null"}]");
            }

            var serialized = node?.ToJsonString(JsonOptions) ?? "null";
            if (serialized.Length <= _maxPayloadCharacters)
            {
                return new Payload(node, false, serialized.Length);
            }

            return new Payload(
                JsonValue.Create(serialized.Substring(0, _maxPayloadCharacters)),
                true,
                serialized.Length);
        }

        private static void RedactNode(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var property in obj.ToList())
                {
                    if (IsSensitiveName(property.Key))
                    {
                        obj[property.Key] = Redacted;
                        continue;
                    }

                    if (property.Value is JsonValue value
                        && value.TryGetValue<string>(out var text))
                    {
                        obj[property.Key] = SanitizeText(text);
                        continue;
                    }

                    if (property.Value != null)
                    {
                        RedactNode(property.Value);
                    }
                }

                return;
            }

            if (node is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue value
                        && value.TryGetValue<string>(out var text))
                    {
                        array[index] = SanitizeText(text);
                    }
                    else if (array[index] != null)
                    {
                        RedactNode(array[index]);
                    }
                }
            }
        }

        private static bool IsSensitiveName(string name)
        {
            var normalized = new string(name
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
            if (normalized.StartsWith("include", StringComparison.Ordinal))
            {
                return false;
            }

            if (SensitiveNames.Contains(normalized))
            {
                return true;
            }

            if (normalized.Contains("password", StringComparison.Ordinal)
                || normalized.Contains("apikey", StringComparison.Ordinal)
                || normalized.Contains("authorization", StringComparison.Ordinal)
                || normalized.Contains("securitykey", StringComparison.Ordinal)
                || normalized.Contains("connectionstring", StringComparison.Ordinal)
                || normalized.Contains("accesstoken", StringComparison.Ordinal)
                || normalized.Contains("refreshtoken", StringComparison.Ordinal)
                || normalized.Contains("reasoning", StringComparison.Ordinal)
                || normalized.Contains("chainofthought", StringComparison.Ordinal)
                || normalized.Contains("prompt", StringComparison.Ordinal)
                || normalized.Contains("process", StringComparison.Ordinal)
                || normalized.Contains("rawresult", StringComparison.Ordinal)
                || normalized.Contains("rawresponse", StringComparison.Ordinal)
                || normalized.EndsWith("token", StringComparison.Ordinal)
                || normalized.EndsWith("cookie", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static string SanitizeText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var trimmed = text.Trim();
            if ((trimmed.StartsWith('{') && trimmed.EndsWith('}'))
                || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
            {
                try
                {
                    var embeddedJson = JsonNode.Parse(text);
                    if (embeddedJson != null)
                    {
                        RedactNode(embeddedJson);
                        return embeddedJson.ToJsonString(JsonOptions);
                    }
                }
                catch (JsonException)
                {
                    // 普通文本继续使用下面的正则脱敏。
                }
            }

            var withoutConnectionString = ConnectionStringRegex.Replace(
                text,
                "CONNECTION_STRING_REDACTED");
            var withoutConnectionUri = ConnectionUriRegex.Replace(
                withoutConnectionString,
                "CONNECTION_STRING_REDACTED");
            var withoutBearer = BearerRegex.Replace(withoutConnectionUri, "Bearer REDACTED");
            return EmbeddedCredentialRegex.Replace(
                withoutBearer,
                match => $"{match.Groups[1].Value}=REDACTED");
        }

        private sealed class Payload
        {
            public Payload(JsonNode data, bool truncated, int originalCharacters)
            {
                Data = data;
                Truncated = truncated;
                OriginalCharacters = originalCharacters;
            }

            public JsonNode Data { get; }

            public bool Truncated { get; }

            public int OriginalCharacters { get; }
        }
    }

    /// <summary>
    /// 单次 MCP 调用的关联日志作用域。
    /// </summary>
    public sealed class McpInvocationScope
    {
        private readonly McpInvocationLogger _logger;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _progressSequence;
        private int _terminalWritten;

        internal McpInvocationScope(McpInvocationLogger logger, string callId, string toolName)
        {
            _logger = logger;
            CallId = callId;
            ToolName = toolName;
        }

        /// <summary>
        /// 当前调用的关联 ID。
        /// </summary>
        public string CallId { get; }

        /// <summary>
        /// 当前 MCP 工具名。
        /// </summary>
        public string ToolName { get; }

        /// <summary>
        /// 记录一条实际发出的 MCP Progress。
        /// </summary>
        public Task ReportProgressAsync(ProgressNotificationValue value)
        {
            if (value == null || Volatile.Read(ref _terminalWritten) != 0)
            {
                return Task.CompletedTask;
            }

            var sequence = Interlocked.Increment(ref _progressSequence);
            return _logger.WriteEventAsync(
                this,
                "progress",
                new
                {
                    value.Progress,
                    value.Total,
                    value.Message,
                },
                sequence);
        }

        /// <summary>
        /// 记录成功或业务失败响应的最终输出。
        /// </summary>
        public Task CompleteAsync(object output) => WriteTerminalAsync("output", output);

        /// <summary>
        /// 记录异常终态；不记录堆栈。
        /// </summary>
        public Task FailAsync(Exception exception)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            return WriteTerminalAsync(
                "error",
                new
                {
                    ExceptionType = exception.GetType().Name,
                    exception.Message,
                });
        }

        /// <summary>
        /// 记录取消终态。
        /// </summary>
        public Task CancelAsync() => WriteTerminalAsync("cancelled", new { Reason = "cancelled" });

        private Task WriteTerminalAsync(string eventName, object data)
        {
            if (Interlocked.CompareExchange(ref _terminalWritten, 1, 0) != 0)
            {
                return Task.CompletedTask;
            }

            _stopwatch.Stop();
            return _logger.WriteEventAsync(
                this,
                eventName,
                data,
                durationMs: Math.Max(0, (long)_stopwatch.Elapsed.TotalMilliseconds));
        }
    }
}
