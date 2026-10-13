using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// AST LLM 辅助推理服务。
    /// 当确定性 AST 求值产生 DiagnosedPartial（含 Unsupported / Unknown 节点）时，
    /// 将已确定的事实 + 未解析的原始代码 + 在线信号交给 LLM 做补充推理。
    /// 结果作为辅助解读附加到诊断结果中，不覆盖确定性结果。
    /// </summary>
    public class AstLlmFallbackService : ISelfScopedAutoInject
    {
        private readonly ILogger<AstLlmFallbackService> _logger;
        private readonly AgentRuntimeService _agentRuntimeService;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public AstLlmFallbackService(
            ILogger<AstLlmFallbackService> logger,
            AgentRuntimeService agentRuntimeService)
        {
            _logger = logger;
            _agentRuntimeService = agentRuntimeService;
        }

        /// <summary>
        /// 对 DiagnosedPartial 的 AST 求值结果进行 LLM 辅助推理。
        /// </summary>
        /// <param name="astResult">AST 求值结果（包含 trace tree）</param>
        /// <returns>LLM 辅助解读结果，null 表示跳过或失败</returns>
        public async Task<AstLlmFallbackResultDto> TryFallbackAsync(EvaluatePersistedAstResultDto astResult)
        {
            if (astResult == null) return null;

            // 仅在存在 Unsupported/Unknown 节点时触发 LLM
            if (astResult.UnsupportedNodeCount == 0 && astResult.UnknownNodeCount == 0)
                return null;

            try
            {
                _logger.LogInformation(
                    "[LLM兜底] 开始辅助推理: Station={Station}, Variable={Var}, Unsupported={U}, Unknown={K}",
                    astResult.StationCode, astResult.VariableName,
                    astResult.UnsupportedNodeCount, astResult.UnknownNodeCount);

                var prompt = BuildFallbackPrompt(astResult);
                var agent = await _agentRuntimeService.CreateAgentByRoleAsync(
                    AppsettingsConfig.GetDiagnosticAgentRoleId());
                var response = await _agentRuntimeService.RunAgentAsync(agent, prompt);

                if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
                {
                    _logger.LogWarning("[LLM兜底] 调用失败或返回为空: {Error}", response.ErrorMessage);
                    return null;
                }

                var result = ParseFallbackResponse(response.Content);
                result.ExecutionTimeMs = response.ExecutionTimeMs;

                _logger.LogInformation(
                    "[LLM兜底] 完成: Confidence={Conf}, Conclusion={Conc}, Time={Time}ms",
                    result.Confidence, result.Conclusion, result.ExecutionTimeMs);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LLM兜底] 辅助推理异常");
                return null;
            }
        }

        /// <summary>
        /// 构建 LLM 辅助推理 prompt。
        /// 包含：确定性求值树摘要 + Unsupported 节点详情 + 在线信号 + 任务要求。
        /// </summary>
        internal static string BuildFallbackPrompt(EvaluatePersistedAstResultDto astResult)
        {
            var sb = new StringBuilder();

            sb.AppendLine("# 任务：PLC 停机诊断 AST 补充推理");
            sb.AppendLine();
            sb.AppendLine("你是一名资深西门子 PLC 工程师。以下是一棵停机原因 AST 的在线求值结果，");
            sb.AppendLine("其中大部分节点已通过确定性计算获得结果，但仍有部分节点无法自动求值。");
            sb.AppendLine("请根据已知事实和提供的在线信号值，对未解析节点给出推理判断。");
            sb.AppendLine();

            // 1. 确定性结果摘要
            sb.AppendLine("## 已知事实");
            sb.AppendLine($"- 目标变量: {astResult.VariableName}");
            sb.AppendLine($"- 根节点求值状态: {astResult.EvaluationStatus}");
            sb.AppendLine($"- 根节点布尔值: {(astResult.EvaluatedValue.HasValue ? astResult.EvaluatedValue.Value.ToString() : "未决")}");
            sb.AppendLine($"- 总 Unsupported 节点: {astResult.UnsupportedNodeCount}");
            sb.AppendLine($"- 总 Unknown 节点: {astResult.UnknownNodeCount}");
            sb.AppendLine();

            // 2. Unsupported / Unknown 节点详情
            var problemNodes = CollectProblemNodes(astResult.RootEvaluation);
            if (problemNodes.Count > 0)
            {
                sb.AppendLine("## 待推理节点");
                foreach (var (node, depth) in problemNodes.Take(15))
                {
                    sb.AppendLine($"### 节点 [{node.NodeType}] {node.DisplayName ?? node.VariableName ?? node.NodeId}");
                    sb.AppendLine($"- 状态: {node.Status}");
                    sb.AppendLine($"- 消息: {node.Message}");
                    if (!string.IsNullOrWhiteSpace(node.VariableName))
                        sb.AppendLine($"- 变量: {node.VariableName}");
                    if (node.ActualValue != null)
                        sb.AppendLine($"- 当前在线值: {FormatValue(node.ActualValue)}");
                    if (node.Metadata?.Count > 0)
                    {
                        foreach (var kv in node.Metadata.Where(m => !string.IsNullOrWhiteSpace(m.Value)))
                        {
                            sb.AppendLine($"- {kv.Key}: {kv.Value}");
                        }
                    }
                    sb.AppendLine();
                }
            }

            // 3. Failure Path 摘要
            if (astResult.FailurePath?.Count > 0)
            {
                sb.AppendLine("## 失败路径");
                foreach (var fp in astResult.FailurePath)
                {
                    sb.AppendLine($"→ [{fp.NodeType}] {fp.DisplayName ?? fp.VariableName ?? fp.NodeId}: {fp.Status} - {fp.Message}");
                }
                sb.AppendLine();
            }

            // 4. 相关在线信号
            var signals = astResult.ExecutionContext?.CurrentValues;
            if (signals?.Count > 0)
            {
                sb.AppendLine("## 在线信号快照（相关信号）");
                var relevantSignals = signals
                    .OrderBy(s => s.Key)
                    .Take(50);
                foreach (var kv in relevantSignals)
                {
                    sb.AppendLine($"- {kv.Key} = {FormatValue(kv.Value)}");
                }
                sb.AppendLine();
            }

            // 5. 缺失信号
            if (astResult.MissingSymbols?.Count > 0)
            {
                sb.AppendLine($"## 缺失信号（共 {astResult.MissingSymbols.Count} 个）");
                foreach (var m in astResult.MissingSymbols.Take(20))
                {
                    sb.AppendLine($"- {m}");
                }
                sb.AppendLine();
            }

            // 6. 输出要求
            sb.AppendLine("## 输出要求");
            sb.AppendLine("请以 JSON 格式回答，不要包含其它文字：");
            sb.AppendLine("```json");
            sb.AppendLine("{");
            sb.AppendLine("  \"conclusion\": \"True 或 False 或 Uncertain\",");
            sb.AppendLine("  \"confidence\": 0.85,");
            sb.AppendLine("  \"reasoning\": \"具体推理过程\",");
            sb.AppendLine("  \"unsupportedNodeAnalysis\": [");
            sb.AppendLine("    { \"nodeId\": \"...\", \"inferredStatus\": \"True/False/Unknown\", \"reason\": \"...\" }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            sb.AppendLine("```");

            return sb.ToString();
        }

        /// <summary>
        /// 收集 Unsupported / Unknown 节点及其深度。
        /// </summary>
        internal static List<(StopCauseAstTraceNode Node, int Depth)> CollectProblemNodes(StopCauseAstTraceNode root, int depth = 0)
        {
            var result = new List<(StopCauseAstTraceNode, int)>();
            if (root == null) return result;

            if (string.Equals(root.Status, "Unsupported", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(root.Status, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                result.Add((root, depth));
            }

            foreach (var child in root.Children ?? new List<StopCauseAstTraceNode>())
            {
                result.AddRange(CollectProblemNodes(child, depth + 1));
            }

            return result;
        }

        private AstLlmFallbackResultDto ParseFallbackResponse(string content)
        {
            try
            {
                var json = ExtractJsonFromResponse(content);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                return new AstLlmFallbackResultDto
                {
                    Success = true,
                    Conclusion = root.TryGetProperty("conclusion", out var c) ? c.GetString() : "Uncertain",
                    Confidence = root.TryGetProperty("confidence", out var conf) && conf.TryGetDouble(out var confVal) ? confVal : 0.5,
                    Reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() : content,
                    UnsupportedNodeAnalysis = ParseNodeAnalysis(root),
                    RawResponse = content
                };
            }
            catch (JsonException)
            {
                return new AstLlmFallbackResultDto
                {
                    Success = true,
                    Conclusion = "Uncertain",
                    Confidence = 0.3,
                    Reasoning = content,
                    RawResponse = content
                };
            }
        }

        private static List<AstLlmNodeAnalysisDto> ParseNodeAnalysis(JsonElement root)
        {
            if (!root.TryGetProperty("unsupportedNodeAnalysis", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<AstLlmNodeAnalysisDto>();

            return arr.EnumerateArray().Select(item => new AstLlmNodeAnalysisDto
            {
                NodeId = item.TryGetProperty("nodeId", out var nid) ? nid.GetString() : null,
                InferredStatus = item.TryGetProperty("inferredStatus", out var s) ? s.GetString() : "Unknown",
                Reason = item.TryGetProperty("reason", out var re) ? re.GetString() : null
            }).ToList();
        }

        private static string ExtractJsonFromResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            var trimmed = text.Trim();

            // 尝试提取 ```json ... ``` 块
            var jsonStart = trimmed.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (jsonStart >= 0)
            {
                var contentStart = trimmed.IndexOf('\n', jsonStart);
                if (contentStart >= 0)
                {
                    var jsonEnd = trimmed.IndexOf("```", contentStart, StringComparison.Ordinal);
                    if (jsonEnd > contentStart)
                    {
                        return trimmed.Substring(contentStart + 1, jsonEnd - contentStart - 1).Trim();
                    }
                }
            }

            // 尝试找第一个 { 到最后一个 }
            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            return trimmed;
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "(null)";
            if (value is JsonElement el)
            {
                return el.ValueKind switch
                {
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Number => el.GetRawText(),
                    JsonValueKind.String => el.GetString() ?? "",
                    _ => el.GetRawText()
                };
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>
    /// LLM 辅助推理结果 DTO。
    /// </summary>
    public class AstLlmFallbackResultDto
    {
        public bool Success { get; set; }

        /// <summary>LLM 推断结论：True / False / Uncertain</summary>
        public string Conclusion { get; set; }

        /// <summary>置信度 0-1</summary>
        public double Confidence { get; set; }

        /// <summary>推理过程文字</summary>
        public string Reasoning { get; set; }

        /// <summary>各 Unsupported 节点的分析</summary>
        public List<AstLlmNodeAnalysisDto> UnsupportedNodeAnalysis { get; set; } = new();

        /// <summary>原始 LLM 响应</summary>
        public string RawResponse { get; set; }

        /// <summary>执行耗时（ms）</summary>
        public double ExecutionTimeMs { get; set; }
    }

    /// <summary>
    /// 单个未支持节点的 LLM 分析结果。
    /// </summary>
    public class AstLlmNodeAnalysisDto
    {
        public string NodeId { get; set; }
        public string InferredStatus { get; set; }
        public string Reason { get; set; }
    }
}
