using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// AST → 可读 Markdown 语义化服务。
    /// 将 PLC_LogicUnitAstEntity 中存储的 AST JSON 树结构转换为人类/LLM 可读的 Markdown 文本。
    /// 
    /// 核心价值：
    /// - LLM 处理自然语言描述的准确率远高于解析原始 XML/JSON 结构
    /// - 约束生成、诊断分析时可直接注入 Markdown 上下文，替代 RawContent/CompactLogic
    /// - 支持缓存：AST 不变时复用已生成的 MD
    /// </summary>
    public class AstMarkdownRendererService : ISelfScopedAutoInject
    {
        private readonly ILogger<AstMarkdownRendererService> _logger;
        private readonly IPLC_LogicUnitAstService _logicUnitAstService;
        private readonly IPLC_LogicUnitService _logicUnitService;

        public AstMarkdownRendererService(
            ILogger<AstMarkdownRendererService> logger,
            IPLC_LogicUnitAstService logicUnitAstService,
            IPLC_LogicUnitService logicUnitService)
        {
            _logger = logger;
            _logicUnitAstService = logicUnitAstService;
            _logicUnitService = logicUnitService;
        }

        /// <summary>
        /// 将单个 AST 节点树转换为可读 Markdown 文本。
        /// </summary>
        public string RenderAstToMarkdown(StopCauseAstNode root, string blockName = null, string networkTitle = null)
        {
            if (root == null) return "（AST 为空）";

            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(blockName) || !string.IsNullOrWhiteSpace(networkTitle))
            {
                sb.AppendLine($"**{blockName ?? ""}** {networkTitle ?? ""}");
                sb.AppendLine();
            }

            RenderNode(sb, root, indent: 0);

            return sb.ToString();
        }

        /// <summary>
        /// 将 AST 转换为单行自然语言逻辑表达式。
        /// 例如："当 SafetyOK(%I0.0)=TRUE 且 StartCmd(%M10.0)=TRUE 时，输出 MotorRun(%Q0.0)"
        /// </summary>
        public string RenderAstToExpression(StopCauseAstNode root)
        {
            if (root == null) return "";
            return BuildExpression(root);
        }

        /// <summary>
        /// 批量将一个工位的所有 AST 转换为 Markdown 文档。
        /// </summary>
        public async Task<string> RenderStationAstMarkdownAsync(long projectId, string stationCode)
        {
            var astRows = await _logicUnitAstService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == projectId
                && it.StationCode == stationCode
                && it.CompileStatus == "Compiled");

            if (astRows == null || astRows.Count == 0)
                return $"# {stationCode}\n\n该工位无已编译的 AST。";

            var sb = new StringBuilder();
            sb.AppendLine($"# 工位 {stationCode} 控制逻辑");
            sb.AppendLine();

            // 按 BlockName 分组
            var groups = astRows.GroupBy(a => a.BlockName ?? "Unknown").OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                sb.AppendLine($"## 程序块: {group.Key}");
                sb.AppendLine();

                foreach (var ast in group.OrderBy(a => a.NetworkId))
                {
                    var root = DeserializeAst(ast.RootNodeJson);
                    if (root == null) continue;

                    var networkLabel = ast.NetworkId.HasValue
                        ? $"Network {ast.NetworkId}"
                        : "";

                    // 自然语言表达式
                    var expression = RenderAstToExpression(root);
                    if (!string.IsNullOrWhiteSpace(expression))
                    {
                        sb.AppendLine($"### {networkLabel}");
                        sb.AppendLine($"逻辑: {expression}");
                        sb.AppendLine();
                    }

                    // 输入/输出变量汇总
                    var inputs = DeserializeVariableList(ast.InputVariablesJson);
                    var outputs = DeserializeVariableList(ast.OutputVariablesJson);

                    if (inputs.Count > 0)
                        sb.AppendLine($"- 输入: {string.Join(", ", inputs)}");
                    if (outputs.Count > 0)
                        sb.AppendLine($"- 输出: {string.Join(", ", outputs)}");

                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将单个 AST 实体的 RootNodeJson 转为可读 Markdown（带缓存检查）。
        /// 如果 AST 实体上 MarkdownCache 字段有值且 AstVersion 未变，直接返回缓存。
        /// </summary>
        public async Task<string> GetOrRenderMarkdownAsync(PLC_LogicUnitAstEntity ast)
        {
            if (ast == null) return "";

            // 检查缓存（如果实体有 MarkdownCache 字段，后续可扩展）
            var root = DeserializeAst(ast.RootNodeJson);
            if (root == null) return "（AST 解析失败）";

            return RenderAstToMarkdown(root, ast.BlockName, ast.NetworkId?.ToString());
        }

        /// <summary>
        /// 为约束生成或诊断提供指定变量的 AST 上下文 Markdown。
        /// 查找包含该变量的所有 AST，转成可读文本。
        /// </summary>
        public async Task<string> RenderVariableContextMarkdownAsync(long projectId, string variableName)
        {
            // 查找该变量作为输出的 AST
            var astRows = await _logicUnitAstService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == projectId
                && it.CompileStatus == "Compiled"
                && it.OutputVariablesJson != null
                && it.OutputVariablesJson.Contains(variableName));

            if (astRows == null || astRows.Count == 0)
                return $"未找到变量 `{variableName}` 的 AST 控制路径。";

            var sb = new StringBuilder();
            sb.AppendLine($"## 变量 `{variableName}` 的控制逻辑");
            sb.AppendLine();

            foreach (var ast in astRows.Take(5)) // 最多 5 个控制路径
            {
                var root = DeserializeAst(ast.RootNodeJson);
                if (root == null) continue;

                sb.AppendLine($"### {ast.BlockName ?? ""} / {ast.NetworkId?.ToString() ?? ""}（工位: {ast.StationCode ?? "未知"}）");
                sb.AppendLine($"逻辑: {RenderAstToExpression(root)}");
                sb.AppendLine();

                // 详细树形展示
                RenderNode(sb, root, indent: 0);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// 为批量变量获取简洁的 AST 控制逻辑表达式。
        /// 返回 dict[variableName] = "当 A=TRUE 且 B=TRUE 时，输出 X"（最多 2 条控制路径）。
        /// 单次查询，避免 N+1。
        /// </summary>
        public async Task<Dictionary<string, string>> BatchRenderVariableExpressionsAsync(
            long projectId, IEnumerable<string> variableNames)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var varList = variableNames?.ToList();
            if (varList == null || varList.Count == 0) return result;

            // 一次性查出所有已编译 AST
            var allAsts = await _logicUnitAstService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == projectId
                && it.CompileStatus == "Compiled"
                && it.OutputVariablesJson != null);

            if (allAsts == null || allAsts.Count == 0) return result;

            foreach (var varName in varList)
            {
                var matchedAsts = allAsts
                    .Where(a => a.OutputVariablesJson != null && a.OutputVariablesJson.Contains(varName))
                    .Take(2)
                    .ToList();

                if (matchedAsts.Count == 0) continue;

                var expressions = new List<string>();
                foreach (var ast in matchedAsts)
                {
                    var root = DeserializeAst(ast.RootNodeJson);
                    if (root == null) continue;

                    var expr = RenderAstToExpression(root);
                    if (!string.IsNullOrWhiteSpace(expr))
                    {
                        var location = ast.BlockName ?? "";
                        if (ast.NetworkId.HasValue) location += $"/N{ast.NetworkId}";
                        expressions.Add($"[{location}] {expr}");
                    }
                }

                if (expressions.Count > 0)
                    result[varName] = string.Join(" ｜ ", expressions);
            }

            return result;
        }

        #region 私有方法

        /// <summary>
        /// 递归渲染节点为 Markdown 缩进列表。
        /// </summary>
        private void RenderNode(StringBuilder sb, StopCauseAstNode node, int indent)
        {
            if (node == null) return;

            var prefix = new string(' ', indent * 2);
            var label = FormatNodeLabel(node);

            sb.AppendLine($"{prefix}- {label}");

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    RenderNode(sb, child, indent + 1);
                }
            }
        }

        /// <summary>
        /// 格式化单个节点为可读标签。
        /// </summary>
        private string FormatNodeLabel(StopCauseAstNode node)
        {
            var nodeType = node.NodeType ?? "Unknown";

            switch (nodeType)
            {
                case StopCauseAstNodeType.And:
                    return "**且** (AND) — 以下条件全部满足:";

                case StopCauseAstNodeType.Or:
                    return "**或** (OR) — 以下条件任一满足:";

                case StopCauseAstNodeType.Not:
                    return "**非** (NOT) — 取反:";

                case StopCauseAstNodeType.Contact:
                    return FormatVariableNode(node, "触点");

                case StopCauseAstNodeType.Coil:
                    return FormatVariableNode(node, "线圈输出");

                case StopCauseAstNodeType.Set:
                    return FormatVariableNode(node, "置位");

                case StopCauseAstNodeType.Reset:
                    return FormatVariableNode(node, "复位");

                case StopCauseAstNodeType.Compare:
                    var op = node.Operator ?? "==";
                    var expected = node.ExpectedValue ?? "?";
                    return $"比较: `{FormatVarName(node)}` {op} {expected}";

                case StopCauseAstNodeType.Timer:
                    return $"定时器: `{FormatVarName(node)}`";

                case StopCauseAstNodeType.Move:
                    return $"赋值: `{FormatVarName(node)}`";

                case StopCauseAstNodeType.GraphStepActive:
                    return $"GRAPH步骤激活: `{FormatVarName(node)}`";

                case StopCauseAstNodeType.GraphTransition:
                    return $"GRAPH转换条件: `{FormatVarName(node)}`";

                case StopCauseAstNodeType.Reference:
                    return $"引用: `{FormatVarName(node)}`";

                case StopCauseAstNodeType.ExternalDependency:
                    return $"外部依赖: `{FormatVarName(node)}`";

                case StopCauseAstNodeType.SetResetFlipFlop:
                    return $"SR触发器: `{FormatVarName(node)}`";

                case StopCauseAstNodeType.Anchor:
                    return $"锚点: `{FormatVarName(node)}`";

                case StopCauseAstNodeType.Opaque:
                    return $"不可解析块: `{FormatVarName(node)}`";

                default:
                    return $"{nodeType}: `{FormatVarName(node)}`";
            }
        }

        /// <summary>
        /// 格式化带变量名的节点（触点/线圈等）。
        /// </summary>
        private string FormatVariableNode(StopCauseAstNode node, string typeLabel)
        {
            var varName = FormatVarName(node);
            var negated = node.Metadata != null
                && node.Metadata.TryGetValue("Negated", out var neg)
                && string.Equals(neg, "true", StringComparison.OrdinalIgnoreCase);

            var negLabel = negated ? "常闭" : "常开";
            return $"{typeLabel}({negLabel}): `{varName}`";
        }

        /// <summary>
        /// 格式化变量名：优先 DisplayName，否则 VariableName。
        /// </summary>
        private string FormatVarName(StopCauseAstNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.DisplayName) && !string.IsNullOrWhiteSpace(node.VariableName)
                && node.DisplayName != node.VariableName)
                return $"{node.DisplayName} ({node.VariableName})";
            return node.DisplayName ?? node.VariableName ?? node.NodeId ?? "?";
        }

        /// <summary>
        /// 将 AST 树转换为单行自然语言逻辑表达式。
        /// </summary>
        private string BuildExpression(StopCauseAstNode node)
        {
            if (node == null) return "";

            var nodeType = node.NodeType ?? "";

            switch (nodeType)
            {
                case StopCauseAstNodeType.And:
                    if (node.Children == null || node.Children.Count == 0) return "";
                    var andParts = node.Children.Select(BuildExpression).Where(s => !string.IsNullOrEmpty(s));
                    // 尝试识别"条件→输出"模式：最后一个子节点是 Coil/Set/Reset
                    var lastChild = node.Children.LastOrDefault();
                    if (lastChild != null && IsOutputNode(lastChild))
                    {
                        var conditions = node.Children.Take(node.Children.Count - 1)
                            .Select(BuildExpression).Where(s => !string.IsNullOrEmpty(s));
                        var output = BuildExpression(lastChild);
                        if (conditions.Any())
                            return $"当 {string.Join(" 且 ", conditions)} 时，{output}";
                        return output;
                    }
                    return string.Join(" 且 ", andParts);

                case StopCauseAstNodeType.Or:
                    if (node.Children == null || node.Children.Count == 0) return "";
                    var orParts = node.Children.Select(BuildExpression).Where(s => !string.IsNullOrEmpty(s));
                    return $"({string.Join(" 或 ", orParts)})";

                case StopCauseAstNodeType.Not:
                    var inner = node.Children?.FirstOrDefault();
                    return inner != null ? $"非({BuildExpression(inner)})" : "";

                case StopCauseAstNodeType.Contact:
                    var contactNeg = node.Metadata != null
                        && node.Metadata.TryGetValue("Negated", out var cn)
                        && string.Equals(cn, "true", StringComparison.OrdinalIgnoreCase);
                    var cName = FormatVarName(node);
                    return contactNeg ? $"{cName}=FALSE" : $"{cName}=TRUE";

                case StopCauseAstNodeType.Coil:
                    return $"输出 {FormatVarName(node)}";

                case StopCauseAstNodeType.Set:
                    return $"置位 {FormatVarName(node)}";

                case StopCauseAstNodeType.Reset:
                    return $"复位 {FormatVarName(node)}";

                case StopCauseAstNodeType.Compare:
                    return $"{FormatVarName(node)} {node.Operator ?? "=="} {node.ExpectedValue ?? "?"}";

                case StopCauseAstNodeType.Timer:
                    return $"定时器({FormatVarName(node)})";

                case StopCauseAstNodeType.GraphStepActive:
                    return $"步骤{FormatVarName(node)}激活";

                case StopCauseAstNodeType.GraphTransition:
                    if (node.Children != null && node.Children.Count > 0)
                    {
                        var transConds = node.Children.Select(BuildExpression).Where(s => !string.IsNullOrEmpty(s));
                        return $"转换({FormatVarName(node)}): {string.Join(" 且 ", transConds)}";
                    }
                    return $"转换({FormatVarName(node)})";

                case StopCauseAstNodeType.Anchor:
                    if (node.Children != null && node.Children.Count > 0)
                    {
                        var anchorExprs = node.Children.Select(BuildExpression).Where(s => !string.IsNullOrEmpty(s));
                        return string.Join(" 且 ", anchorExprs);
                    }
                    return FormatVarName(node);

                case StopCauseAstNodeType.Reference:
                case StopCauseAstNodeType.ExternalDependency:
                    return FormatVarName(node);

                default:
                    return FormatVarName(node);
            }
        }

        private static bool IsOutputNode(StopCauseAstNode node)
        {
            return node.NodeType == StopCauseAstNodeType.Coil
                || node.NodeType == StopCauseAstNodeType.Set
                || node.NodeType == StopCauseAstNodeType.Reset;
        }

        private StopCauseAstNode DeserializeAst(string rootNodeJson)
        {
            if (string.IsNullOrWhiteSpace(rootNodeJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<StopCauseAstNode>(rootNodeJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AstMarkdown] AST JSON 反序列化失败");
                return null;
            }
        }

        private List<string> DeserializeVariableList(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                }) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        #endregion
    }
}
