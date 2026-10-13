using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 第一版 AST 编译器骨架。
    /// 当前先输出编译状态、引用节点和覆盖报告，为后续 GRAPH/LAD 编译器承接输入。
    /// </summary>
    public class StopCauseAstCompilerService : ISelfScopedAutoInject
    {
        private static readonly Regex XmlTagRegex = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex GraphActionAssignRegex = new Regex(@"(?<lhs>[A-Za-z0-9_\.\[\]]+)\s*(:=|=)\s*(?<rhs>[^;\r\n]+)", RegexOptions.Compiled);
        private static readonly Regex SclAssignmentRegex = new Regex(@"(?<lhs>[#\""A-Za-z0-9_\.\[\]]+)\s*:=\s*(?<rhs>[^;\r\n]+)", RegexOptions.Compiled);
        private static readonly Regex SclCallRegex = new Regex(@"(?<callee>[#A-Za-z0-9_\.]+)\s*\((?<args>.*)\)", RegexOptions.Compiled);
        private static readonly Regex VariableReferenceRegex = new Regex(@"(#?[A-Za-z_][A-Za-z0-9_]*(?:\[[0-9]+\])?(?:\.[A-Za-z0-9_\u4e00-\u9fff]+(?:\[[0-9]+\])?)*)|\""([^\""\r\n]+)\""((?:\.[A-Za-z0-9_\u4e00-\u9fff]+(?:\[[0-9]+\])?)*)", RegexOptions.Compiled);
        private static readonly HashSet<string> SclLogicalKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "AND_THEN", "OR", "OR_ELSE", "NOT"
        };
        private static readonly string[] SclComparisonOperators = { "<>", ">=", "<=", "=", ">", "<" };
        private static readonly HashSet<string> LadComparePartNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Eq", "Ne", "Gt", "Ge", "Lt", "Le"
        };
        private static readonly HashSet<string> LadTimerPartNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TON", "TOF", "TP"
        };
        private static readonly HashSet<string> LadAndPartNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "A", "AND"
        };
        private static readonly HashSet<string> LadSupportedPartNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Contact", "NContact", "Not", "Eq", "Ne", "Gt", "Ge", "Lt", "Le", "O", "OR", "A", "AND", "Coil", "SCoil", "RCoil", "TrCoil",
            "TON", "TOF", "TP", "Move", "SR", "RS"
        };

        public Task<List<StopCauseAstCompileResult>> CompileSeedsAsync(IEnumerable<StopCauseAstSeed> seeds)
        {
            var results = seeds?.Select(BuildCompileResult).ToList() ?? new List<StopCauseAstCompileResult>();
            return Task.FromResult(results);
        }

        private static StopCauseAstCompileResult BuildCompileResult(StopCauseAstSeed seed)
        {
            var reports = seed.CoverageReports ?? new List<AstCoverageReport>();
            var recognizedReports = reports
                .Select(BuildRecognizedReport)
                .ToList();

            var unsupportedCount = recognizedReports.Count(it => it.CompileStatus == AstCompileStatus.Unsupported);
            var partialCount = recognizedReports.Count(it => it.CompileStatus == AstCompileStatus.PartiallyCompiled);

            var compileStatus = AstCompileStatus.Pending;
            if (recognizedReports.Count == 0)
            {
                compileStatus = AstCompileStatus.Failed;
            }
            else if (recognizedReports.All(it => it.CompileStatus == AstCompileStatus.Compiled))
            {
                compileStatus = AstCompileStatus.Compiled;
            }
            else if (unsupportedCount == recognizedReports.Count)
            {
                compileStatus = AstCompileStatus.Unsupported;
            }
            else if (partialCount > 0 || unsupportedCount > 0)
            {
                compileStatus = AstCompileStatus.PartiallyCompiled;
            }

            var rootNode = new StopCauseAstNode
            {
                NodeId = $"anchor:{seed.Anchor?.AnchorKey}",
                NodeType = StopCauseAstNodeType.Anchor,
                VariableName = seed.Anchor?.VariableName,
                DisplayName = seed.Anchor?.SemanticLabel ?? seed.Anchor?.VariableName,
                ExpectedValue = "True",
                Metadata = new Dictionary<string, string>
                {
                    ["AnchorType"] = seed.Anchor?.AnchorType ?? string.Empty,
                    ["CompileStatus"] = compileStatus
                },
                Children = recognizedReports.Select(BuildChildNode).ToList()
            };

            var messages = new List<string>();
            if (compileStatus == AstCompileStatus.PartiallyCompiled)
            {
                messages.Add("已生成首版编译树，其中 GRAPH 与简单 LAD 网络可输出可执行节点，其余结构仍以引用树承接。");
            }

            if (compileStatus == AstCompileStatus.Compiled)
            {
                messages.Add("当前锚点已生成首版可执行 GRAPH / LAD 编译结果。");
            }

            if (compileStatus == AstCompileStatus.Unsupported)
            {
                messages.Add("当前锚点对应逻辑尚未进入支持范围，需要优先补充编译器规则。");
            }

            if (compileStatus == AstCompileStatus.Failed)
            {
                messages.Add("未找到可用于编译的写入逻辑单元。请先检查入库完整性或锚点筛选条件。");
            }

            seed.CompileStatus = compileStatus;
            seed.CoverageReports = recognizedReports;

            return new StopCauseAstCompileResult
            {
                Anchor = seed.Anchor,
                CompileStatus = compileStatus,
                CompilerName = "PhaseOneReferenceCompiler",
                RootNode = rootNode,
                CoverageReports = recognizedReports,
                Messages = messages
            };
        }

        private static AstCoverageReport BuildRecognizedReport(AstCoverageReport report)
        {
            var clone = new AstCoverageReport
            {
                SourceType = report.SourceType,
                UnitType = report.UnitType,
                Title = report.Title,
                BlockName = report.BlockName,
                LogicUnitId = report.LogicUnitId,
                NetworkId = report.NetworkId,
                RawContent = report.RawContent,
                RawContentExcerpt = report.RawContentExcerpt,
                InputVariables = report.InputVariables?.ToList() ?? new List<string>(),
                OutputVariables = report.OutputVariables?.ToList() ?? new List<string>(),
                SuggestedUpgradeCategory = report.SuggestedUpgradeCategory,
                UnknownPartTypes = report.UnknownPartTypes?.ToList() ?? new List<string>(),
                CallPinBindingsJson = report.CallPinBindingsJson,
                GraphOutgoingTransitionsJson = report.GraphOutgoingTransitionsJson,
                GraphTransitionConditionsJson = report.GraphTransitionConditionsJson
            };

            clone.CompileStatus = DetermineStatus(clone);
            clone.UnsupportedReason = DetermineUnsupportedReason(clone);

            return clone;
        }

        private static string DetermineStatus(AstCoverageReport report)
        {
            if (string.Equals(report.SourceType, "GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                if (IsGraphExecutableUnit(report.UnitType))
                {
                    return AstCompileStatus.Compiled;
                }

                return AstCompileStatus.PartiallyCompiled;
            }

            if (string.Equals(report.SourceType, "LAD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(report.SourceType, "FBD", StringComparison.OrdinalIgnoreCase))
            {
                if (!CanCompileLad(report))
                {
                    return AstCompileStatus.PartiallyCompiled;
                }

                return report.UnknownPartTypes?.Count > 0
                    ? AstCompileStatus.PartiallyCompiled
                    : AstCompileStatus.Compiled;
            }

            if (string.Equals(report.SourceType, "SCL", StringComparison.OrdinalIgnoreCase))
            {
                return CanCompileScl(report)
                    ? AstCompileStatus.Compiled
                    : AstCompileStatus.PartiallyCompiled;
            }

            if (string.Equals(report.SourceType, "STL", StringComparison.OrdinalIgnoreCase))
            {
                return AstCompileStatus.PartiallyCompiled;
            }

            return AstCompileStatus.Unsupported;
        }

        private static string DetermineUnsupportedReason(AstCoverageReport report)
        {
            if (string.Equals(report.CompileStatus, AstCompileStatus.Compiled, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (string.Equals(report.SourceType, "GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                return "已识别 GRAPH 语言，但当前仅对 Transition 和 Step Action 输出可执行节点。";
            }

            if (string.Equals(report.SourceType, "LAD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(report.SourceType, "FBD", StringComparison.OrdinalIgnoreCase))
            {
                if (CanCompileLad(report))
                {
                    if (report.UnknownPartTypes?.Count > 0)
                    {
                        return $"已编译 {report.SourceType} 可执行分支，但仍存在暂未识别的 Part：{string.Join(", ", report.UnknownPartTypes)}。";
                    }

                    return string.Empty;
                }

                return $"已识别 {report.SourceType} 语言，但当前仍以引用树承接，尚未完成可执行节点编译。";
            }

            if (string.Equals(report.SourceType, "SCL", StringComparison.OrdinalIgnoreCase))
            {
                return CanCompileScl(report)
                    ? string.Empty
                    : "已识别 SCL 语言，但当前片段未解析出可执行赋值或调用语句。";
            }

            if (string.Equals(report.SourceType, "STL", StringComparison.OrdinalIgnoreCase))
            {
                return "已识别 STL 语言，当前以引用树承接。";
            }

            return "当前仅进入 GRAPH/LAD/FBD/SCL/STL 编译范围，其它语言和结构暂未支持。";
        }

        private static StopCauseAstNode BuildChildNode(AstCoverageReport report)
        {
            if (string.Equals(report.CompileStatus, AstCompileStatus.Compiled, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(report.SourceType, "GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                return BuildGraphExecutableNode(report);
            }

            if ((string.Equals(report.CompileStatus, AstCompileStatus.Compiled, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(report.CompileStatus, AstCompileStatus.PartiallyCompiled, StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(report.SourceType, "LAD", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(report.SourceType, "FBD", StringComparison.OrdinalIgnoreCase)))
            {
                return BuildLadExecutableNode(report);
            }

            if (string.Equals(report.CompileStatus, AstCompileStatus.Compiled, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(report.SourceType, "SCL", StringComparison.OrdinalIgnoreCase))
            {
                return BuildSclExecutableNode(report);
            }

            var nodeType = report.CompileStatus == AstCompileStatus.Unsupported
                ? StopCauseAstNodeType.Opaque
                : StopCauseAstNodeType.Reference;

            return new StopCauseAstNode
            {
                NodeId = $"logic-unit:{report.LogicUnitId}",
                NodeType = nodeType,
                DisplayName = report.Title ?? report.BlockName,
                ExpectedValue = report.CompileStatus,
                Metadata = new Dictionary<string, string>
                {
                    ["SourceType"] = report.SourceType ?? string.Empty,
                    ["UnitType"] = report.UnitType ?? string.Empty,
                    ["NetworkId"] = report.NetworkId?.ToString() ?? string.Empty,
                    ["UnsupportedReason"] = report.UnsupportedReason ?? string.Empty,
                    ["SuggestedUpgradeCategory"] = report.SuggestedUpgradeCategory ?? string.Empty
                }
            };
        }

        private static bool IsGraphExecutableUnit(string unitType)
        {
            if (string.IsNullOrWhiteSpace(unitType))
            {
                return false;
            }

            return unitType.IndexOf("TRANSITION", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (unitType.IndexOf("STEP_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    unitType.IndexOf("_ACTION", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool CanCompileLad(AstCoverageReport report)
        {
            try
            {
                var xml = ParseXmlFragment(report.RawContent, report.RawContentExcerpt);
                if (xml == null)
                {
                    return false;
                }

                var parts = xml.Descendants().Where(it => it.Name.LocalName == "Part").ToList();
                var hasCall = xml.Descendants().Any(it => it.Name.LocalName == "Call");
                if (parts.Count == 0 && !hasCall)
                {
                    return false;
                }

                var unsupported = parts
                    .Select(it => (string)it.Attribute("Name"))
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Where(it => !LadSupportedPartNames.Contains(it))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                report.UnknownPartTypes = unsupported;

                return parts.Any(it => IsCoilPart((string)it.Attribute("Name"))) || hasCall;
            }
            catch
            {
                return false;
            }
        }

        private static bool CanCompileScl(AstCoverageReport report)
        {
            var text = ExtractReadableText(report.RawContentExcerpt);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return SclAssignmentRegex.IsMatch(text) || SclCallRegex.IsMatch(text);
        }

        private static StopCauseAstNode BuildGraphExecutableNode(AstCoverageReport report)
        {
            if (!string.IsNullOrWhiteSpace(report.UnitType) &&
                report.UnitType.IndexOf("TRANSITION", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return BuildGraphTransitionNode(report);
            }

            var actionText = ExtractActionText(!string.IsNullOrWhiteSpace(report.RawContent)
                ? report.RawContent
                : report.RawContentExcerpt);
            var actionOperator = ResolveActionOperator(actionText);
            var actionTarget = ResolveActionTarget(actionText, report.OutputVariables);

            // 出口 Transition 编号（如 [7, 33]），运行时用于找到对应 Transition LogicUnit 并评估转移条件
            var outgoingMeta = string.Empty;
            if (!string.IsNullOrWhiteSpace(report.GraphOutgoingTransitionsJson))
            {
                try
                {
                    // 验证合法性并直通（JSON int array）
                    var nums = JsonSerializer.Deserialize<List<int>>(report.GraphOutgoingTransitionsJson);
                    if (nums != null && nums.Count > 0)
                        outgoingMeta = report.GraphOutgoingTransitionsJson;
                }
                catch { /* 保持空字符串 */ }
            }

            return new StopCauseAstNode
            {
                NodeId = $"logic-unit:{report.LogicUnitId}:action",
                NodeType = actionOperator,
                VariableName = actionTarget,
                DisplayName = report.Title ?? actionTarget ?? report.BlockName,
                ExpectedValue = "True",
                Operator = actionOperator,
                Metadata = new Dictionary<string, string>
                {
                    ["SourceType"] = report.SourceType ?? string.Empty,
                    ["UnitType"] = report.UnitType ?? string.Empty,
                    ["ActionText"] = actionText,
                    ["ActionRawContentExcerpt"] = report.RawContentExcerpt ?? string.Empty,
                    ["GraphStepNumber"] = ResolveGraphStepNumber(report.UnitType),
                    ["NetworkId"] = report.NetworkId?.ToString() ?? string.Empty,
                    ["OutgoingTransitionNetworkIds"] = outgoingMeta,
                    ["GraphTransitionConditions"] = report.GraphTransitionConditionsJson ?? string.Empty
                }
            };
        }

        private static string ResolveGraphStepNumber(string unitType)
        {
            var match = Regex.Match(unitType ?? string.Empty, @"STEP_(\d+)_ACTION", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        /// <summary>
        /// GRAPH TRANSITION — 将转移条件编译为可执行 AST 子树。
        /// 
        /// 策略（按优先级）：
        ///   1. **LAD XML**：TRANSITION RawContent 含 FlgNet/Part/Wire（西门子 TIA 标准格式）
        ///      → 复用 BuildLadExecutableNode 完整 LAD 编译器（Contact/Compare/Timer/TrCoil 全支持）
        ///   2. **SCL 文本**：StructuredText 或纯文本 AND/OR/比较
        ///      → 复用 BuildSclExpressionNode 递归下降解析器
        ///   3. **降级**：两者均失败 → 无子节点的 GraphTransition（运行时回退到信号读取或 Unsupported）
        /// </summary>
        private static StopCauseAstNode BuildGraphTransitionNode(AstCoverageReport report)
        {
            var fallbackVariable = report.InputVariables?.FirstOrDefault();

            // ── 策略 1: LAD XML 编译（优先，TRANSITION 绝大多数为标准 LAD） ──
            StopCauseAstNode ladResult = null;
            var rawXml = !string.IsNullOrWhiteSpace(report.RawContent)
                ? report.RawContent
                : report.RawContentExcerpt;
            if (!string.IsNullOrWhiteSpace(rawXml) && IsLadXmlContent(rawXml))
            {
                ladResult = TryBuildLadTransitionNode(rawXml, report);
            }

            if (ladResult != null)
            {
                // LAD 编译成功 → 包装为 GraphTransition 节点
                return WrapAsGraphTransitionNode(report, ladResult, fallbackVariable, rawXml, "LAD");
            }

            // ── 策略 2: SCL 文本表达式解析 ──
            var expressionText = ExtractReadableText(report.RawContentExcerpt);
            StopCauseAstNode expressionNode = null;
            if (!string.IsNullOrWhiteSpace(expressionText))
            {
                expressionNode = BuildSclExpressionNode(expressionText, report.LogicUnitId);
            }

            if (expressionNode != null)
            {
                return WrapAsGraphTransitionNode(report, expressionNode, fallbackVariable, expressionText, "SCL");
            }

            // ── 策略 3: 降级 ──
            return new StopCauseAstNode
            {
                NodeId = $"logic-unit:{report.LogicUnitId}:transition",
                NodeType = StopCauseAstNodeType.GraphTransition,
                VariableName = fallbackVariable,
                DisplayName = report.Title ?? report.BlockName,
                ExpectedValue = "True",
                Children = new List<StopCauseAstNode>(),
                Metadata = new Dictionary<string, string>
                {
                    ["SourceType"] = report.SourceType ?? string.Empty,
                    ["UnitType"] = report.UnitType ?? string.Empty,
                    ["ExpressionText"] = expressionText ?? string.Empty,
                    ["NetworkId"] = report.NetworkId?.ToString() ?? string.Empty,
                    ["ExpressionParsed"] = "false"
                }
            };
        }

        /// <summary>
        /// 检测 RawContent 是否包含 LAD XML 结构（FlgNet 或 Part 元素）。
        /// </summary>
        private static bool IsLadXmlContent(string rawXml)
        {
            return rawXml.Contains("<FlgNet", StringComparison.OrdinalIgnoreCase)
                || rawXml.Contains("<Part ", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 尝试将 TRANSITION 的 LAD XML 编译为 AST 节点。
        /// 复用 BuildLadExecutableNode 的核心逻辑（Wire 拓扑追踪 + Part 编译），
        /// 但以 TrCoil 为线圈入口，输出其上游条件链。
        /// </summary>
        private static StopCauseAstNode TryBuildLadTransitionNode(string rawXml, AstCoverageReport report)
        {
            try
            {
                var xml = ParseXmlFragment(rawXml, rawXml);
                if (xml == null) return null;

                var accesses = xml.Descendants()
                    .Where(it => it.Name.LocalName == "Access")
                    .Select(it => new { UId = (string)it.Attribute("UId"), Element = it })
                    .Where(it => !string.IsNullOrWhiteSpace(it.UId))
                    .ToDictionary(it => it.UId, it => ResolveAccessText(it.Element), StringComparer.OrdinalIgnoreCase);

                var parts = xml.Descendants()
                    .Where(it => it.Name.LocalName == "Part")
                    .Select(it => new { UId = (string)it.Attribute("UId"), Name = (string)it.Attribute("Name"), Element = it })
                    .Where(it => !string.IsNullOrWhiteSpace(it.UId) && !string.IsNullOrWhiteSpace(it.Name))
                    .ToDictionary(it => it.UId, it => (it.Name, it.Element), StringComparer.OrdinalIgnoreCase);

                var wires = xml.Descendants()
                    .Where(it => it.Name.LocalName == "Wire")
                    .ToList();

                // TRANSITION 中的线圈是 TrCoil（也可能有普通 Coil），找到入口
                var coilUids = parts
                    .Where(it => IsCoilPart(it.Value.Name))
                    .Select(it => it.Key)
                    .ToList();

                if (coilUids.Count == 0) return null;

                // 对每个 Coil 构建上游条件链
                var compiledConditions = new List<StopCauseAstNode>();
                foreach (var coilUid in coilUids)
                {
                    var inputExpr = ResolveLadInputExpression(coilUid, parts, accesses, wires, "in");
                    if (inputExpr != null)
                    {
                        compiledConditions.Add(inputExpr);
                    }
                }

                if (compiledConditions.Count == 0) return null;
                if (compiledConditions.Count == 1) return compiledConditions[0];

                return new StopCauseAstNode
                {
                    NodeId = $"logic-unit:{report.LogicUnitId}:lad-transition-and",
                    NodeType = StopCauseAstNodeType.And,
                    DisplayName = report.Title ?? report.BlockName,
                    ExpectedValue = "True",
                    Children = compiledConditions
                };
            }
            catch
            {
                return null; // LAD 解析失败 → 回退到 SCL
            }
        }

        /// <summary>
        /// 将编译好的条件节点包装为 GraphTransition 节点。
        /// </summary>
        private static StopCauseAstNode WrapAsGraphTransitionNode(
            AstCoverageReport report, StopCauseAstNode conditionNode,
            string fallbackVariable, string expressionText, string parseMethod)
        {
            // 展平 AND（转移条件本身即 AND 语义）
            List<StopCauseAstNode> children;
            if (string.Equals(conditionNode.NodeType, StopCauseAstNodeType.And, StringComparison.OrdinalIgnoreCase)
                && conditionNode.Children?.Count > 0)
            {
                children = conditionNode.Children;
            }
            else
            {
                children = new List<StopCauseAstNode> { conditionNode };
            }

            return new StopCauseAstNode
            {
                NodeId = $"logic-unit:{report.LogicUnitId}:transition",
                NodeType = StopCauseAstNodeType.GraphTransition,
                VariableName = fallbackVariable,
                DisplayName = report.Title ?? report.BlockName,
                ExpectedValue = "True",
                Children = children,
                Metadata = new Dictionary<string, string>
                {
                    ["SourceType"] = report.SourceType ?? string.Empty,
                    ["UnitType"] = report.UnitType ?? string.Empty,
                    ["ExpressionText"] = expressionText ?? string.Empty,
                    ["NetworkId"] = report.NetworkId?.ToString() ?? string.Empty,
                    ["ExpressionParsed"] = "true",
                    ["ParseMethod"] = parseMethod
                }
            };
        }

        private static StopCauseAstNode BuildLadExecutableNode(AstCoverageReport report)
        {
            var xml = ParseXmlFragment(report.RawContent, report.RawContentExcerpt);
            if (xml == null)
            {
                return BuildReferenceFallback(report, "LAD XML 片段解析失败");
            }

            var accesses = xml.Descendants()
                .Where(it => it.Name.LocalName == "Access")
                .Select(it => new { UId = (string)it.Attribute("UId"), Element = it })
                .Where(it => !string.IsNullOrWhiteSpace(it.UId))
                .ToDictionary(it => it.UId, it => ResolveAccessText(it.Element), StringComparer.OrdinalIgnoreCase);

            var parts = xml.Descendants()
                .Where(it => it.Name.LocalName == "Part")
                .Select(it => new { UId = (string)it.Attribute("UId"), Name = (string)it.Attribute("Name"), Element = it })
                .Where(it => !string.IsNullOrWhiteSpace(it.UId) && !string.IsNullOrWhiteSpace(it.Name))
                .ToDictionary(it => it.UId, it => (it.Name, it.Element), StringComparer.OrdinalIgnoreCase);

            var wires = xml.Descendants()
                .Where(it => it.Name.LocalName == "Wire")
                .ToList();

            var coilUids = parts
                .Where(it => IsCoilPart(it.Value.Name))
                .Select(it => it.Key)
                .ToList();

            var compiledChildren = new List<StopCauseAstNode>();
            foreach (var coilUid in coilUids)
            {
                var node = BuildLadCoilNode(coilUid, parts, accesses, wires);
                if (node != null)
                {
                    compiledChildren.Add(node);
                }
            }

            // Handle Call (FB/FC) elements — compile output parameters as ExternalDependency nodes
            // 构建 Access Scope 映射 & XElement 映射（供递归回溯输入参数时判断 GlobalVariable）
            var accessScopeMap = xml.Descendants()
                .Where(it => it.Name.LocalName == "Access" && !string.IsNullOrWhiteSpace((string)it.Attribute("UId")))
                .ToDictionary(it => (string)it.Attribute("UId"), it => (string)it.Attribute("Scope") ?? "", StringComparer.OrdinalIgnoreCase);

            var callElements = xml.Descendants()
                .Where(it => it.Name.LocalName == "Call")
                .ToList();
            foreach (var callElement in callElements)
            {
                var callNodes = BuildLadCallOutputNodes(callElement, accesses, accessScopeMap, parts, wires);
                compiledChildren.AddRange(callNodes);
            }

            if (compiledChildren.Count == 0)
            {
                return BuildReferenceFallback(report, "LAD 网络中未识别到线圈节点或功能块输出");
            }

            if (compiledChildren.Count == 1)
            {
                return compiledChildren[0];
            }

            return new StopCauseAstNode
            {
                NodeId = $"logic-unit:{report.LogicUnitId}:lad-root",
                NodeType = StopCauseAstNodeType.And,
                DisplayName = report.Title ?? report.BlockName,
                ExpectedValue = "True",
                Children = compiledChildren,
                Metadata = new Dictionary<string, string>
                {
                    ["SourceType"] = report.SourceType ?? string.Empty,
                    ["UnitType"] = report.UnitType ?? string.Empty,
                    ["NetworkId"] = report.NetworkId?.ToString() ?? string.Empty,
                    ["CompileStatus"] = report.CompileStatus ?? string.Empty,
                    ["Semantics"] = "ParallelOutputs"
                }
            };
        }

        /// <summary>
        /// 将 LAD/FBD 中的 Call (FB/FC 功能块调用) 的每个输出参数编译为 ExternalDependency 节点。
        /// 运行时求值器通过读取输出信号判定 true/false。
        /// </summary>
        private static List<StopCauseAstNode> BuildLadCallOutputNodes(
            XElement callElement,
            IReadOnlyDictionary<string, string> accesses,
            IReadOnlyDictionary<string, string> accessScopeMap,
            IReadOnlyDictionary<string, (string Name, XElement Element)> parts,
            IReadOnlyList<XElement> wires)
        {
            var nodes = new List<StopCauseAstNode>();
            var callUid = (string)callElement.Attribute("UId");
            if (string.IsNullOrWhiteSpace(callUid)) return nodes;

            var callInfo = callElement.Descendants().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
            if (callInfo == null) return nodes;

            var calleeName = (string)callInfo.Attribute("Name") ?? "UnknownFB";
            var blockType = (string)callInfo.Attribute("BlockType") ?? "FB";

            // ── 收集输入/InOut参数映射（形参→实参）,供 FB 穿透时注入信号覆盖值 ──
            // 通用方案：递归回溯 Wire→Part 链（Contact、OR/AND 门等），收集所有 GlobalVariable 叶子
            var inputParamMetadata = new Dictionary<string, string>();
            var inputParams = callInfo.Descendants()
                .Where(e => e.Name.LocalName == "Parameter")
                .Where(e =>
                {
                    var section = (string)e.Attribute("Section");
                    return string.Equals(section, "Input", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(section, "InOut", StringComparison.OrdinalIgnoreCase);
                })
                .Select(e => (Name: (string)e.Attribute("Name"), Type: (string)e.Attribute("Type")))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            foreach (var (paramName, _) in inputParams)
            {
                var wire = wires.FirstOrDefault(w =>
                    w.Descendants().Any(d =>
                        d.Name.LocalName == "NameCon" &&
                        string.Equals((string)d.Attribute("UId"), callUid, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)d.Attribute("Name"), paramName, StringComparison.OrdinalIgnoreCase)));

                if (wire == null) continue;

                // 情况1: 输入端直接由 Access（全局变量）通过 IdentCon 连接
                var sourceIdentCon = wire.Descendants()
                    .FirstOrDefault(d => d.Name.LocalName == "IdentCon" &&
                        !string.Equals((string)d.Attribute("UId"), callUid, StringComparison.OrdinalIgnoreCase));
                if (sourceIdentCon != null)
                {
                    var sourceUid = (string)sourceIdentCon.Attribute("UId");
                    if (!string.IsNullOrWhiteSpace(sourceUid) && accesses.TryGetValue(sourceUid, out var sourceVariable))
                    {
                        inputParamMetadata[$"InputParam:{paramName}"] = sourceVariable;
                        continue;
                    }
                }

                // 情况2/3: 输入端来自 Part 的输出（Contact/OR/AND 等任意逻辑门）
                // → 递归回溯整条 Part 链，收集所有叶子 GlobalVariable Access
                var sourceNameCon = wire.Descendants()
                    .FirstOrDefault(d => d.Name.LocalName == "NameCon" &&
                        !string.Equals((string)d.Attribute("UId"), callUid, StringComparison.OrdinalIgnoreCase));
                if (sourceNameCon != null)
                {
                    var partUid = (string)sourceNameCon.Attribute("UId");
                    if (!string.IsNullOrWhiteSpace(partUid))
                    {
                        var leafVars = new List<string>();
                        TracePartLeafGlobalVariables(partUid, parts, accesses, accessScopeMap, wires, leafVars, maxDepth: 4);
                        if (leafVars.Count > 0)
                        {
                            // 多个变量用 | 分隔（如 OR(sensor, bypass) → "sensor|bypass"）
                            inputParamMetadata[$"InputParam:{paramName}"] = string.Join("|", leafVars);
                        }
                    }
                }
            }

            // ── 收集输出参数（Output + InOut）──
            var outputParams = callInfo.Descendants()
                .Where(e => e.Name.LocalName == "Parameter")
                .Where(e =>
                {
                    var section = (string)e.Attribute("Section");
                    return string.Equals(section, "Output", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(section, "InOut", StringComparison.OrdinalIgnoreCase);
                })
                .Select(e => (Name: (string)e.Attribute("Name"), Type: (string)e.Attribute("Type")))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            foreach (var (paramName, paramType) in outputParams)
            {
                // 查找从 Call 输出引脚到全局变量的 Wire
                var wire = wires.FirstOrDefault(w =>
                    w.Descendants().Any(d =>
                        d.Name.LocalName == "NameCon" &&
                        string.Equals((string)d.Attribute("UId"), callUid, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((string)d.Attribute("Name"), paramName, StringComparison.OrdinalIgnoreCase)));

                if (wire == null) continue;

                // 输出端：IdentCon 指向 Access（全局变量）
                var targetIdentCon = wire.Descendants()
                    .FirstOrDefault(d => d.Name.LocalName == "IdentCon" &&
                        !string.Equals((string)d.Attribute("UId"), callUid, StringComparison.OrdinalIgnoreCase));
                if (targetIdentCon == null) continue;

                var targetUid = (string)targetIdentCon.Attribute("UId");
                if (string.IsNullOrWhiteSpace(targetUid) || !accesses.TryGetValue(targetUid, out var targetVariable))
                    continue;

                var metadata = new Dictionary<string, string>
                {
                    ["Callee"] = calleeName,
                    ["BlockType"] = blockType,
                    ["OutputParam"] = paramName,
                    ["ParamType"] = paramType ?? string.Empty
                };

                // 将输入参数映射附加到每个输出节点的 Metadata，供穿透时使用
                foreach (var kv in inputParamMetadata)
                {
                    metadata[kv.Key] = kv.Value;
                }

                nodes.Add(new StopCauseAstNode
                {
                    NodeId = $"lad:{callUid}:call:{paramName}",
                    NodeType = StopCauseAstNodeType.ExternalDependency,
                    VariableName = targetVariable,
                    DisplayName = $"{calleeName}.{paramName} → {targetVariable}",
                    ExpectedValue = "True",
                    Metadata = metadata
                });
            }

            return nodes;
        }

        private static StopCauseAstNode BuildLadCoilNode(
            string coilUid,
            IReadOnlyDictionary<string, (string Name, XElement Element)> parts,
            IReadOnlyDictionary<string, string> accesses,
            IReadOnlyList<XElement> wires)
        {
            if (!parts.TryGetValue(coilUid, out var part))
            {
                return null;
            }

            var target = ResolveOperandAccess(coilUid, wires, accesses);
            var input = ResolveLadInputExpression(coilUid, parts, accesses, wires, "in");
            var nodeType = MapCoilNodeType(part.Name);

            return new StopCauseAstNode
            {
                NodeId = $"lad:{coilUid}",
                NodeType = nodeType,
                VariableName = target,
                DisplayName = target,
                Operator = nodeType,
                ExpectedValue = "True",
                Children = input == null ? new List<StopCauseAstNode>() : new List<StopCauseAstNode> { input },
                Metadata = new Dictionary<string, string>
                {
                    ["PartName"] = part.Name,
                    ["Target"] = target ?? string.Empty
                }
            };
        }

        private static StopCauseAstNode ResolveLadInputExpression(
            string partUid,
            IReadOnlyDictionary<string, (string Name, XElement Element)> parts,
            IReadOnlyDictionary<string, string> accesses,
            IReadOnlyList<XElement> wires,
            string pinName)
        {
            var wire = FindWireForNameCon(wires, partUid, pinName);
            if (wire == null)
            {
                return null;
            }

            var upstreamPartUid = wire.Descendants().FirstOrDefault(it => it.Name.LocalName == "NameCon" &&
                                                                     IsLadOutputPin((string)it.Attribute("Name")) &&
                                                                     !string.Equals((string)it.Attribute("UId"), partUid, StringComparison.OrdinalIgnoreCase))?.Attribute("UId")?.Value;
            if (!string.IsNullOrWhiteSpace(upstreamPartUid) && parts.ContainsKey(upstreamPartUid))
            {
                return BuildLadPartExpression(upstreamPartUid, parts, accesses, wires);
            }

            var directAccess = ResolveIdentAccessFromWire(wire, accesses);
            if (!string.IsNullOrWhiteSpace(directAccess))
            {
                return BuildLadContactNode($"lad:{partUid}:{pinName}:direct-contact", directAccess);
            }

            return null;
        }

        private static List<StopCauseAstNode> ResolveLadNamedInputExpressions(
            string partUid,
            IReadOnlyDictionary<string, (string Name, XElement Element)> parts,
            IReadOnlyDictionary<string, string> accesses,
            IReadOnlyList<XElement> wires,
            params string[] pinNames)
        {
            var results = new List<StopCauseAstNode>();
            foreach (var pinName in pinNames.Where(it => !string.IsNullOrWhiteSpace(it)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var child = ResolveLadInputExpression(partUid, parts, accesses, wires, pinName);
                if (child != null)
                {
                    results.Add(child);
                }
            }

            return results;
        }

        private static bool IsLadOutputPin(string pinName)
        {
            return string.Equals(pinName, "out", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(pinName, "Q", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(pinName, "ENO", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLadContactPart(string partName)
        {
            return string.Equals(partName, "Contact", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(partName, "NContact", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNegatedLadContact(string partName, XElement partElement)
        {
            return string.Equals(partName, "NContact", StringComparison.OrdinalIgnoreCase) ||
                   (partElement?.Descendants().Any(it =>
                       it.Name.LocalName == "Negated" &&
                       (string.IsNullOrWhiteSpace((string)it.Attribute("Name")) ||
                        string.Equals((string)it.Attribute("Name"), "operand", StringComparison.OrdinalIgnoreCase))) ?? false);
        }

        private static StopCauseAstNode BuildLadPartExpression(
            string partUid,
            IReadOnlyDictionary<string, (string Name, XElement Element)> parts,
            IReadOnlyDictionary<string, string> accesses,
            IReadOnlyList<XElement> wires)
        {
            if (!parts.TryGetValue(partUid, out var part))
            {
                return null;
            }

            if (IsLadContactPart(part.Name))
            {
                var operand = ResolveOperandAccess(partUid, wires, accesses);
                var current = BuildLadContactNode($"lad:{partUid}:contact", operand);
                if (current == null)
                {
                    return null;
                }

                if (IsNegatedLadContact(part.Name, part.Element))
                {
                    current = new StopCauseAstNode
                    {
                        NodeId = $"lad:{partUid}:negated-contact",
                        NodeType = StopCauseAstNodeType.Not,
                        Children = new List<StopCauseAstNode> { current }
                    };
                }

                var previous = ResolveLadInputExpression(partUid, parts, accesses, wires, "in");
                return previous == null ? current : BuildAndNode(previous, current, partUid);
            }

            if (string.Equals(part.Name, "O", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "OR", StringComparison.OrdinalIgnoreCase))
            {
                var cardinality = ResolvePartCardinality(part.Element);
                var pinNames = Enumerable.Range(1, Math.Max(2, cardinality))
                    .Select(it => $"in{it}")
                    .ToArray();
                var inputs = ResolveLadNamedInputExpressions(partUid, parts, accesses, wires, pinNames);
                return BuildLogicalNode(StopCauseAstNodeType.Or, inputs, $"lad:{partUid}:or");
            }

            if (LadAndPartNames.Contains(part.Name))
            {
                var cardinality = ResolvePartCardinality(part.Element);
                var pinNames = Enumerable.Range(1, Math.Max(2, cardinality))
                    .Select(it => $"in{it}")
                    .ToArray();
                var inputs = ResolveLadNamedInputExpressions(partUid, parts, accesses, wires, pinNames);
                return BuildLogicalNode(StopCauseAstNodeType.And, inputs, $"lad:{partUid}:and");
            }

            if (string.Equals(part.Name, "Not", StringComparison.OrdinalIgnoreCase))
            {
                var previous = ResolveLadInputExpression(partUid, parts, accesses, wires, "in");
                if (previous == null)
                {
                    return null;
                }

                return new StopCauseAstNode
                {
                    NodeId = $"lad:{partUid}:not",
                    NodeType = StopCauseAstNodeType.Not,
                    Children = new List<StopCauseAstNode> { previous }
                };
            }

            if (LadComparePartNames.Contains(part.Name))
            {
                var left = ResolveNamedAccess(partUid, "in1", wires, accesses);
                var right = ResolveNamedAccess(partUid, "in2", wires, accesses);
                var compareNode = new StopCauseAstNode
                {
                    NodeId = $"lad:{partUid}:{part.Name.ToLowerInvariant()}",
                    NodeType = StopCauseAstNodeType.Compare,
                    Operator = part.Name,
                    VariableName = left,
                    ExpectedValue = right,
                    DisplayName = part.Name,
                    Metadata = new Dictionary<string, string>
                    {
                        ["Left"] = left ?? string.Empty,
                        ["Right"] = right ?? string.Empty
                    }
                };

                var previous = ResolveLadInputExpression(partUid, parts, accesses, wires, "pre");
                return previous == null ? compareNode : BuildAndNode(previous, compareNode, partUid);
            }

            // Timer (TON / TOF / TP)
            if (LadTimerPartNames.Contains(part.Name))
            {
                return BuildLadTimerExpression(partUid, part.Name, parts, accesses, wires);
            }

            // Move
            if (string.Equals(part.Name, "Move", StringComparison.OrdinalIgnoreCase))
            {
                return BuildLadMoveExpression(partUid, parts, accesses, wires);
            }

            // SR / RS flip-flop
            if (string.Equals(part.Name, "SR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part.Name, "RS", StringComparison.OrdinalIgnoreCase))
            {
                return BuildLadSetResetExpression(partUid, part.Name, parts, accesses, wires);
            }

            return null;
        }

        /// <summary>
        /// Timer (TON/TOF/TP) — 编译为 Timer 节点。
        /// 引脚：IN（启动条件）、PT（预设时间）、Q（输出）、ET（当前经过时间）。
        /// 求值策略：读 Q 输出信号做快速判定，同时携带 ET/PT 方便比较。
        /// </summary>
        private static StopCauseAstNode BuildLadTimerExpression(
            string partUid, string timerType,
            IReadOnlyDictionary<string, (string Name, XElement Element)> parts,
            IReadOnlyDictionary<string, string> accesses,
            IReadOnlyList<XElement> wires)
        {
            var inExpr = ResolveLadInputExpression(partUid, parts, accesses, wires, "in");
            var ptAccess = ResolveNamedAccess(partUid, "pt", wires, accesses);
            var qAccess = ResolveNamedAccess(partUid, "Q", wires, accesses);
            if (string.IsNullOrWhiteSpace(qAccess))
            {
                qAccess = ResolveNamedAccess(partUid, "q", wires, accesses);
            }
            var etAccess = ResolveNamedAccess(partUid, "et", wires, accesses);

            var children = new List<StopCauseAstNode>();
            if (inExpr != null) children.Add(inExpr);

            return new StopCauseAstNode
            {
                NodeId = $"lad:{partUid}:timer:{timerType.ToLowerInvariant()}",
                NodeType = StopCauseAstNodeType.Timer,
                VariableName = qAccess,
                DisplayName = $"{timerType}({qAccess})",
                ExpectedValue = "True",
                Operator = timerType,
                Children = children,
                Metadata = new Dictionary<string, string>
                {
                    ["TimerType"] = timerType,
                    ["PT"] = ptAccess ?? string.Empty,
                    ["ET"] = etAccess ?? string.Empty,
                    ["Q"] = qAccess ?? string.Empty
                }
            };
        }

        /// <summary>
        /// Move — 数据传送，编译为 Move 节点。
        /// 引脚：EN（使能条件）、IN（源值）、OUT1（目标）、ENO（使能输出）。
        /// 求值策略：先判 EN 条件，再读 IN/OUT1 比较是否一致。
        /// </summary>
        private static StopCauseAstNode BuildLadMoveExpression(
            string partUid,
            IReadOnlyDictionary<string, (string Name, XElement Element)> parts,
            IReadOnlyDictionary<string, string> accesses,
            IReadOnlyList<XElement> wires)
        {
            var enExpr = ResolveLadInputExpression(partUid, parts, accesses, wires, "en");
            var inAccess = ResolveNamedAccess(partUid, "in", wires, accesses);
            var outAccess = ResolveNamedAccess(partUid, "out1", wires, accesses);

            var children = new List<StopCauseAstNode>();
            if (enExpr != null) children.Add(enExpr);

            return new StopCauseAstNode
            {
                NodeId = $"lad:{partUid}:move",
                NodeType = StopCauseAstNodeType.Move,
                VariableName = outAccess,
                DisplayName = $"MOVE → {outAccess}",
                ExpectedValue = "True",
                Children = children,
                Metadata = new Dictionary<string, string>
                {
                    ["Source"] = inAccess ?? string.Empty,
                    ["Destination"] = outAccess ?? string.Empty
                }
            };
        }

        /// <summary>
        /// SR / RS 触发器 — 编译为 SetResetFlipFlop 节点。
        /// SR 引脚：S（Set输入）、R1（Reset输入）、Q（输出）。
        /// RS 引脚：R（Reset输入）、S1（Set输入）、Q（输出）。
        /// 求值策略：S/R 各有一路上游条件，先独立求值，再结合优先级和 Q 值判定。
        /// </summary>
        private static StopCauseAstNode BuildLadSetResetExpression(
            string partUid, string flipFlopType,
            IReadOnlyDictionary<string, (string Name, XElement Element)> parts,
            IReadOnlyDictionary<string, string> accesses,
            IReadOnlyList<XElement> wires)
        {
            StopCauseAstNode setExpr, resetExpr;
            if (string.Equals(flipFlopType, "SR", StringComparison.OrdinalIgnoreCase))
            {
                setExpr = ResolveLadInputExpression(partUid, parts, accesses, wires, "S");
                resetExpr = ResolveLadInputExpression(partUid, parts, accesses, wires, "R1");
            }
            else
            {
                setExpr = ResolveLadInputExpression(partUid, parts, accesses, wires, "S1");
                resetExpr = ResolveLadInputExpression(partUid, parts, accesses, wires, "R");
            }

            var qAccess = ResolveNamedAccess(partUid, "Q", wires, accesses);
            if (string.IsNullOrWhiteSpace(qAccess))
            {
                qAccess = ResolveNamedAccess(partUid, "q", wires, accesses);
            }

            var children = new List<StopCauseAstNode>();
            if (setExpr != null) children.Add(setExpr);
            if (resetExpr != null) children.Add(resetExpr);

            return new StopCauseAstNode
            {
                NodeId = $"lad:{partUid}:{flipFlopType.ToLowerInvariant()}",
                NodeType = StopCauseAstNodeType.SetResetFlipFlop,
                VariableName = qAccess,
                DisplayName = $"{flipFlopType}({qAccess})",
                ExpectedValue = "True",
                Operator = flipFlopType,
                Children = children,
                Metadata = new Dictionary<string, string>
                {
                    ["FlipFlopType"] = flipFlopType,
                    ["Q"] = qAccess ?? string.Empty
                }
            };
        }

        private static StopCauseAstNode BuildAndNode(StopCauseAstNode left, StopCauseAstNode right, string seed)
        {
            return new StopCauseAstNode
            {
                NodeId = $"lad:{seed}:and",
                NodeType = StopCauseAstNodeType.And,
                Children = new List<StopCauseAstNode> { left, right }
            };
        }

        private static StopCauseAstNode BuildLogicalNode(string nodeType, IReadOnlyList<StopCauseAstNode> children, string nodeId)
        {
            var normalized = children?.Where(it => it != null).ToList() ?? new List<StopCauseAstNode>();
            if (normalized.Count == 0)
            {
                return null;
            }

            if (normalized.Count == 1)
            {
                return normalized[0];
            }

            return new StopCauseAstNode
            {
                NodeId = nodeId,
                NodeType = nodeType,
                Children = normalized
            };
        }

        private static XElement FindWireForNameCon(IReadOnlyList<XElement> wires, string uid, string name)
        {
            return wires.FirstOrDefault(wire => wire.Descendants().Any(it =>
                it.Name.LocalName == "NameCon" &&
                string.Equals((string)it.Attribute("UId"), uid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)it.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase)));
        }

        private static string ResolveIdentAccessFromWire(XElement wire, IReadOnlyDictionary<string, string> accesses)
        {
            var identUid = wire?.Descendants()
                .FirstOrDefault(it => it.Name.LocalName == "IdentCon")?
                .Attribute("UId")?.Value;
            return !string.IsNullOrWhiteSpace(identUid) && accesses.TryGetValue(identUid, out var value)
                ? value
                : string.Empty;
        }

        private static StopCauseAstNode BuildLadContactNode(string nodeId, string operand)
        {
            if (string.IsNullOrWhiteSpace(operand))
            {
                return null;
            }

            return new StopCauseAstNode
            {
                NodeId = nodeId,
                NodeType = StopCauseAstNodeType.Contact,
                VariableName = operand,
                DisplayName = operand,
                ExpectedValue = "True"
            };
        }

        private static string ResolveOperandAccess(string partUid, IReadOnlyList<XElement> wires, IReadOnlyDictionary<string, string> accesses)
        {
            return ResolveNamedAccess(partUid, "operand", wires, accesses);
        }

        private static string ResolveNamedAccess(string partUid, string pinName, IReadOnlyList<XElement> wires, IReadOnlyDictionary<string, string> accesses)
        {
            var wire = FindWireForNameCon(wires, partUid, pinName);
            if (wire == null)
            {
                return string.Empty;
            }

            return ResolveIdentAccessFromWire(wire, accesses);
        }

        /// <summary>
        /// 递归回溯 Part 输入链，收集所有 GlobalVariable Scope 的叶子 Access 变量名。
        /// 可穿透 Contact、OR(O)、AND(A) 等任意逻辑门组合，通用于所有 FB 输入参数接线拓扑。
        /// 例如：IHP = OR(Contact(sensor), Contact(bypass)) → 返回 ["sensor", "bypass"]
        /// </summary>
        private static void TracePartLeafGlobalVariables(
            string partUid,
            IReadOnlyDictionary<string, (string Name, XElement Element)> parts,
            IReadOnlyDictionary<string, string> accesses,
            IReadOnlyDictionary<string, string> accessScopeMap,
            IReadOnlyList<XElement> wires,
            List<string> result,
            int maxDepth)
        {
            if (maxDepth <= 0 || string.IsNullOrWhiteSpace(partUid)) return;
            if (!parts.TryGetValue(partUid, out var part)) return;

            var partName = part.Name;
            var isContact = string.Equals(partName, "Contact", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(partName, "NContact", StringComparison.OrdinalIgnoreCase);

            if (isContact)
            {
                // Contact/NContact: 查找 operand 引脚 → Access
                // 极性判断：
                //   1. Part 名为 "NContact"（西门子 SCL 风格否定触点）
                //   2. 或 Part 元素含 <Negated Name="operand" /> 子元素（LAD 常闭触点）
                // 带极性的变量加 "~" 前缀，穿透求值时取反：~VarName 表示"该信号为 True 时触点断开"
                var isNegated = string.Equals(partName, "NContact", StringComparison.OrdinalIgnoreCase)
                    || part.Element.Descendants().Any(it =>
                        it.Name.LocalName == "Negated"
                        && string.Equals((string)it.Attribute("Name"), "operand", StringComparison.OrdinalIgnoreCase));

                var operandWire = FindWireForNameCon(wires, partUid, "operand");
                if (operandWire != null)
                {
                    var identCon = operandWire.Descendants()
                        .FirstOrDefault(d => d.Name.LocalName == "IdentCon");
                    if (identCon != null)
                    {
                        var uid = (string)identCon.Attribute("UId");
                        if (!string.IsNullOrWhiteSpace(uid)
                            && accesses.TryGetValue(uid, out var varName)
                            && !string.IsNullOrWhiteSpace(varName)
                            && accessScopeMap.TryGetValue(uid, out var scope)
                            && string.Equals(scope, "GlobalVariable", StringComparison.OrdinalIgnoreCase))
                        {
                            // "~" 前缀标记常闭触点极性：该信号 True 时触点断开（逻辑取反）
                            var entry = isNegated ? "~" + varName : varName;
                            if (!result.Contains(entry, StringComparer.OrdinalIgnoreCase))
                                result.Add(entry);
                        }
                    }
                }
                return;
            }

            // 其他 Part（O/OR、A/AND、Coil 等逻辑门）：遍历所有输入引脚（in, in1, in2, ...）
            // 查找所有 Wire 中 NameCon 目标是当前 Part 且引脚名以 "in" 开头
            foreach (var wire in wires)
            {
                var targetNameCons = wire.Descendants()
                    .Where(d => d.Name.LocalName == "NameCon"
                        && string.Equals((string)d.Attribute("UId"), partUid, StringComparison.OrdinalIgnoreCase)
                        && ((string)d.Attribute("Name") ?? "").StartsWith("in", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (targetNameCons.Count == 0) continue;

                // 该 Wire 的源：可能是 IdentCon（Access）或 NameCon（另一个 Part 的输出）
                var sourceIdentCon = wire.Descendants()
                    .FirstOrDefault(d => d.Name.LocalName == "IdentCon");
                if (sourceIdentCon != null)
                {
                    var uid = (string)sourceIdentCon.Attribute("UId");
                    if (!string.IsNullOrWhiteSpace(uid)
                        && accesses.TryGetValue(uid, out var varName)
                        && !string.IsNullOrWhiteSpace(varName)
                        && accessScopeMap.TryGetValue(uid, out var scope)
                        && string.Equals(scope, "GlobalVariable", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!result.Contains(varName, StringComparer.OrdinalIgnoreCase))
                            result.Add(varName);
                    }
                    continue;
                }

                // 源是另一个 Part 的输出 → 递归
                var sourceNameCon = wire.Descendants()
                    .FirstOrDefault(d => d.Name.LocalName == "NameCon"
                        && !string.Equals((string)d.Attribute("UId"), partUid, StringComparison.OrdinalIgnoreCase));
                if (sourceNameCon != null)
                {
                    var sourcePartUid = (string)sourceNameCon.Attribute("UId");
                    TracePartLeafGlobalVariables(sourcePartUid, parts, accesses, accessScopeMap, wires, result, maxDepth - 1);
                }
            }
        }

        private static int ResolvePartCardinality(XElement partElement)
        {
            var raw = partElement?
                .Descendants()
                .FirstOrDefault(it => it.Name.LocalName == "TemplateValue" && string.Equals((string)it.Attribute("Name"), "Card", StringComparison.OrdinalIgnoreCase))?
                .Value;

            return int.TryParse(raw, out var value) && value > 0
                ? value
                : 2;
        }

        private static string ResolveAccessText(XElement access)
        {
            var components = access
                .Descendants()
                .Where(it => it.Name.LocalName == "Component")
                .Select(it =>
                {
                    var name = (string)it.Attribute("Name") ?? string.Empty;
                    var nestedConstant = it.Descendants().FirstOrDefault(x => x.Name.LocalName == "ConstantValue")?.Value;
                    return string.IsNullOrWhiteSpace(nestedConstant) ? name : $"{name}[{nestedConstant}]";
                })
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .ToList();

            if (components.Count > 0)
            {
                return string.Join(".", components);
            }

            return access.Descendants().FirstOrDefault(it => it.Name.LocalName == "ConstantValue")?.Value ?? string.Empty;
        }

        private static XDocument ParseXmlFragment(params string[] candidates)
        {
            foreach (var rawContent in candidates)
            {
                if (string.IsNullOrWhiteSpace(rawContent))
                {
                    continue;
                }

                try
                {
                    return XDocument.Parse($"<Root>{rawContent}</Root>");
                }
                catch
                {
                }
            }

            return null;
        }

        private static bool IsCoilPart(string partName)
        {
            return string.Equals(partName, "Coil", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(partName, "SCoil", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(partName, "RCoil", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(partName, "TrCoil", StringComparison.OrdinalIgnoreCase);
        }

        private static string MapCoilNodeType(string partName)
        {
            if (string.Equals(partName, "SCoil", StringComparison.OrdinalIgnoreCase))
            {
                return StopCauseAstNodeType.Set;
            }

            if (string.Equals(partName, "RCoil", StringComparison.OrdinalIgnoreCase))
            {
                return StopCauseAstNodeType.Reset;
            }

            return StopCauseAstNodeType.Coil;
        }

        private static StopCauseAstNode BuildReferenceFallback(AstCoverageReport report, string reason)
        {
            return new StopCauseAstNode
            {
                NodeId = $"logic-unit:{report.LogicUnitId}:fallback",
                NodeType = StopCauseAstNodeType.Reference,
                DisplayName = report.Title ?? report.BlockName,
                ExpectedValue = AstCompileStatus.PartiallyCompiled,
                Metadata = new Dictionary<string, string>
                {
                    ["SourceType"] = report.SourceType ?? string.Empty,
                    ["UnitType"] = report.UnitType ?? string.Empty,
                    ["UnsupportedReason"] = reason
                }
            };
        }

        private static string ExtractActionText(string rawContent)
        {
            var text = ExtractReadableText(rawContent);
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return Regex.Replace(text, "\\s+", " ").Trim();
        }

        private static string ExtractReadableText(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return string.Empty;
            }

            var xml = ParseXmlFragment(rawContent);
            var structuredText = xml?.Descendants().FirstOrDefault(it => it.Name.LocalName == "StructuredText");
            if (structuredText != null)
            {
                var rendered = RenderStructuredText(structuredText);
                if (!string.IsNullOrWhiteSpace(rendered))
                {
                    return rendered;
                }
            }

            var stripped = XmlTagRegex.Replace(rawContent, " ");
            stripped = stripped
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&");

            return Regex.Replace(stripped, "\\s+", " ").Trim();
        }

        private static string RenderStructuredText(XElement structuredText)
        {
            if (structuredText == null)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var node in structuredText.Nodes())
            {
                if (node is not XElement element)
                {
                    continue;
                }

                switch (element.Name.LocalName)
                {
                    case "Access":
                        builder.Append(ResolveAccessText(element));
                        break;
                    case "Token":
                        builder.Append((string)element.Attribute("Text") ?? string.Empty);
                        break;
                    case "Blank":
                    {
                        var num = int.TryParse((string)element.Attribute("Num"), out var value) ? Math.Max(1, value) : 1;
                        builder.Append(' ', num);
                        break;
                    }
                    case "NewLine":
                    {
                        var num = int.TryParse((string)element.Attribute("Num"), out var value) ? Math.Max(1, value) : 1;
                        builder.Append(string.Concat(Enumerable.Repeat(Environment.NewLine, num)));
                        break;
                    }
                }
            }

            return builder.ToString();
        }

        private static string ResolveActionOperator(string actionText)
        {
            if (string.IsNullOrWhiteSpace(actionText))
            {
                return StopCauseAstNodeType.Reference;
            }

            if (actionText.IndexOf(":= FALSE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return StopCauseAstNodeType.Reset;
            }

            if (actionText.IndexOf(":= TRUE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return StopCauseAstNodeType.Set;
            }

            if (!string.IsNullOrWhiteSpace(actionText))
            {
                return GraphActionAssignRegex.IsMatch(actionText)
                    ? StopCauseAstNodeType.Coil
                    : StopCauseAstNodeType.Coil;
            }

            return StopCauseAstNodeType.Reference;
        }

        private static string ResolveActionTarget(string actionText, List<string> outputVariables)
        {
            var match = GraphActionAssignRegex.Match(actionText ?? string.Empty);
            if (match.Success)
            {
                return match.Groups["lhs"].Value;
            }

            return outputVariables?.FirstOrDefault()
                ?? Regex.Replace(actionText ?? string.Empty, "\\s+", " ").Trim();
        }

        private static StopCauseAstNode BuildSclExpressionNode(string expression, long? logicUnitId)
        {
            var tokens = TokenizeSclExpression(expression);
            if (tokens.Count == 0)
            {
                return null;
            }

            var position = 0;
            var nodeCounter = 0;
            var node = ParseSclOrExpression(tokens, logicUnitId, ref position, ref nodeCounter);
            return position == tokens.Count ? node : null;
        }

        private static StopCauseAstNode ParseSclOrExpression(IReadOnlyList<SclToken> tokens, long? logicUnitId, ref int position, ref int nodeCounter)
        {
            var left = ParseSclAndExpression(tokens, logicUnitId, ref position, ref nodeCounter);
            if (left == null)
            {
                return null;
            }

            while (MatchSclToken(tokens, ref position, SclTokenType.Or))
            {
                var right = ParseSclAndExpression(tokens, logicUnitId, ref position, ref nodeCounter);
                if (right == null)
                {
                    return null;
                }

                left = BuildSclLogicalNode(StopCauseAstNodeType.Or, logicUnitId, ref nodeCounter, left, right);
            }

            return left;
        }

        private static StopCauseAstNode ParseSclAndExpression(IReadOnlyList<SclToken> tokens, long? logicUnitId, ref int position, ref int nodeCounter)
        {
            var left = ParseSclComparisonExpression(tokens, logicUnitId, ref position, ref nodeCounter);
            if (left == null)
            {
                return null;
            }

            while (MatchSclToken(tokens, ref position, SclTokenType.And))
            {
                var right = ParseSclComparisonExpression(tokens, logicUnitId, ref position, ref nodeCounter);
                if (right == null)
                {
                    return null;
                }

                left = BuildSclLogicalNode(StopCauseAstNodeType.And, logicUnitId, ref nodeCounter, left, right);
            }

            return left;
        }

        private static StopCauseAstNode ParseSclComparisonExpression(IReadOnlyList<SclToken> tokens, long? logicUnitId, ref int position, ref int nodeCounter)
        {
            var left = ParseSclUnaryExpression(tokens, logicUnitId, ref position, ref nodeCounter);
            if (left == null)
            {
                return null;
            }

            if (position >= tokens.Count || tokens[position].Type != SclTokenType.ComparisonOperator)
            {
                return left;
            }

            var op = tokens[position++].Text;
            var right = ParseSclUnaryExpression(tokens, logicUnitId, ref position, ref nodeCounter);
            if (right == null)
            {
                return null;
            }

            var leftOperand = ExtractSclScalarOperand(left);
            var rightOperand = ExtractSclScalarOperand(right);
            if (string.IsNullOrWhiteSpace(leftOperand) || string.IsNullOrWhiteSpace(rightOperand))
            {
                return null;
            }

            return new StopCauseAstNode
            {
                NodeId = $"scl:{logicUnitId}:expr:{nodeCounter++}",
                NodeType = StopCauseAstNodeType.Compare,
                VariableName = leftOperand,
                DisplayName = leftOperand,
                Operator = MapSclComparisonOperator(op),
                ExpectedValue = rightOperand,
                Metadata = new Dictionary<string, string>
                {
                    ["Left"] = leftOperand,
                    ["Right"] = rightOperand,
                    ["RawOperator"] = op
                }
            };
        }

        private static StopCauseAstNode ParseSclUnaryExpression(IReadOnlyList<SclToken> tokens, long? logicUnitId, ref int position, ref int nodeCounter)
        {
            if (MatchSclToken(tokens, ref position, SclTokenType.Not))
            {
                var child = ParseSclUnaryExpression(tokens, logicUnitId, ref position, ref nodeCounter);
                if (child == null)
                {
                    return null;
                }

                return BuildSclLogicalNode(StopCauseAstNodeType.Not, logicUnitId, ref nodeCounter, child);
            }

            return ParseSclPrimaryExpression(tokens, logicUnitId, ref position, ref nodeCounter);
        }

        private static StopCauseAstNode ParseSclPrimaryExpression(IReadOnlyList<SclToken> tokens, long? logicUnitId, ref int position, ref int nodeCounter)
        {
            if (MatchSclToken(tokens, ref position, SclTokenType.OpenParen))
            {
                var inner = ParseSclOrExpression(tokens, logicUnitId, ref position, ref nodeCounter);
                if (inner == null || !MatchSclToken(tokens, ref position, SclTokenType.CloseParen))
                {
                    return null;
                }

                return inner;
            }

            if (position >= tokens.Count || tokens[position].Type != SclTokenType.Operand)
            {
                return null;
            }

            var operand = tokens[position++].Text;
            return BuildSclLeafNode(operand, logicUnitId, ref nodeCounter);
        }

        private static StopCauseAstNode BuildSclLeafNode(string rawToken, long? logicUnitId, ref int nodeCounter)
        {
            var token = NormalizeSclOperand(rawToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            if (TryParseSclComparison(token, out var left, out var op, out var right))
            {
                return new StopCauseAstNode
                {
                    NodeId = $"scl:{logicUnitId}:expr:{nodeCounter++}",
                    NodeType = StopCauseAstNodeType.Compare,
                    VariableName = left,
                    DisplayName = left,
                    Operator = MapSclComparisonOperator(op),
                    ExpectedValue = right,
                    Metadata = new Dictionary<string, string>
                    {
                        ["Left"] = left,
                        ["Right"] = right,
                        ["RawOperator"] = op
                    }
                };
            }

            return new StopCauseAstNode
            {
                NodeId = $"scl:{logicUnitId}:expr:{nodeCounter++}",
                NodeType = StopCauseAstNodeType.Contact,
                VariableName = token,
                DisplayName = token,
                ExpectedValue = "True"
            };
        }

        private static string ExtractSclScalarOperand(StopCauseAstNode node)
        {
            return string.Equals(node?.NodeType, StopCauseAstNodeType.Contact, StringComparison.OrdinalIgnoreCase)
                ? node.VariableName
                : string.Empty;
        }

        private static StopCauseAstNode BuildSclLogicalNode(string nodeType, long? logicUnitId, ref int nodeCounter, params StopCauseAstNode[] children)
        {
            if (string.Equals(nodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase))
            {
                var child = children?.FirstOrDefault(it => it != null);
                return child == null
                    ? null
                    : new StopCauseAstNode
                    {
                        NodeId = $"scl:{logicUnitId}:expr:{nodeCounter++}",
                        NodeType = StopCauseAstNodeType.Not,
                        Children = new List<StopCauseAstNode> { child }
                    };
            }

            return BuildLogicalNode(nodeType, children?.Where(it => it != null).ToList(), $"scl:{logicUnitId}:expr:{nodeCounter++}");
        }

        private static List<SclToken> TokenizeSclExpression(string expression)
        {
            var tokens = new List<SclToken>();
            var text = expression ?? string.Empty;
            var index = 0;
            while (index < text.Length)
            {
                var current = text[index];
                if (char.IsWhiteSpace(current))
                {
                    index++;
                    continue;
                }

                if (current == '(')
                {
                    tokens.Add(new SclToken(SclTokenType.OpenParen, "("));
                    index++;
                    continue;
                }

                if (current == ')')
                {
                    tokens.Add(new SclToken(SclTokenType.CloseParen, ")"));
                    index++;
                    continue;
                }

                var comparisonOperator = MatchSclComparisonOperatorAt(text, index);
                if (!string.IsNullOrEmpty(comparisonOperator))
                {
                    tokens.Add(new SclToken(SclTokenType.ComparisonOperator, comparisonOperator));
                    index += comparisonOperator.Length;
                    continue;
                }

                if (current == '\'' || current == '"')
                {
                    var quote = current;
                    var end = index + 1;
                    while (end < text.Length && text[end] != quote)
                    {
                        end++;
                    }

                    if (end < text.Length)
                    {
                        end++;
                    }

                    while (end < text.Length &&
                           !char.IsWhiteSpace(text[end]) &&
                           text[end] != '(' &&
                           text[end] != ')' &&
                           string.IsNullOrEmpty(MatchSclComparisonOperatorAt(text, end)))
                    {
                        end++;
                    }

                    tokens.Add(new SclToken(SclTokenType.Operand, text.Substring(index, end - index)));
                    index = end;
                    continue;
                }

                var start = index;
                while (index < text.Length &&
                       !char.IsWhiteSpace(text[index]) &&
                       text[index] != '(' &&
                       text[index] != ')' &&
                       string.IsNullOrEmpty(MatchSclComparisonOperatorAt(text, index)))
                {
                    index++;
                }

                var tokenText = text.Substring(start, index - start);
                var tokenType = ResolveSclTokenType(tokenText);
                tokens.Add(new SclToken(tokenType, tokenText));
            }

            return tokens;
        }

        private static string MatchSclComparisonOperatorAt(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length)
            {
                return string.Empty;
            }

            foreach (var candidate in SclComparisonOperators)
            {
                if (index + candidate.Length <= text.Length &&
                    string.Compare(text, index, candidate, 0, candidate.Length, StringComparison.Ordinal) == 0)
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static SclTokenType ResolveSclTokenType(string tokenText)
        {
            if (string.Equals(tokenText, "OR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tokenText, "OR_ELSE", StringComparison.OrdinalIgnoreCase))
            {
                return SclTokenType.Or;
            }

            if (string.Equals(tokenText, "AND", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tokenText, "AND_THEN", StringComparison.OrdinalIgnoreCase))
            {
                return SclTokenType.And;
            }

            if (string.Equals(tokenText, "NOT", StringComparison.OrdinalIgnoreCase))
            {
                return SclTokenType.Not;
            }

            return SclTokenType.Operand;
        }

        private static bool MatchSclToken(IReadOnlyList<SclToken> tokens, ref int position, SclTokenType expected)
        {
            if (position >= tokens.Count || tokens[position].Type != expected)
            {
                return false;
            }

            position++;
            return true;
        }

        private static string NormalizeSclOperand(string token)
        {
            return (token ?? string.Empty).Trim();
        }

        private static bool TryParseSclComparison(string token, out string left, out string op, out string right)
        {
            left = string.Empty;
            op = string.Empty;
            right = string.Empty;

            foreach (var candidate in SclComparisonOperators)
            {
                var index = token.IndexOf(candidate, StringComparison.Ordinal);
                if (index <= 0 || index >= token.Length - candidate.Length)
                {
                    continue;
                }

                left = token.Substring(0, index).Trim();
                right = token.Substring(index + candidate.Length).Trim();
                if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                {
                    continue;
                }

                op = candidate;
                return true;
            }

            return false;
        }

        private static string MapSclComparisonOperator(string op)
        {
            return op switch
            {
                "=" => "Eq",
                "<>" => "Ne",
                ">" => "Gt",
                ">=" => "Ge",
                "<" => "Lt",
                "<=" => "Le",
                _ => op
            };
        }

        private static StopCauseAstNode BuildSclExecutableNode(AstCoverageReport report)
        {
            var readable = ExtractReadableText(report.RawContentExcerpt);
            if (string.IsNullOrWhiteSpace(readable))
            {
                return BuildReferenceFallback(report, "SCL 文本提取为空");
            }

            var statements = readable
                .Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(it => it.Trim())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .ToList();

            var nodes = new List<StopCauseAstNode>();
            foreach (var statement in statements)
            {
                var assignMatch = SclAssignmentRegex.Match(statement);
                if (assignMatch.Success)
                {
                    var left = assignMatch.Groups["lhs"].Value.Trim();
                    var right = assignMatch.Groups["rhs"].Value.Trim();
                    var nodeType = ResolveSclAssignmentNodeType(right);
                    var expressionNode = BuildSclExpressionNode(right, report.LogicUnitId);
                    var children = expressionNode == null
                        ? ExtractVariableReferences(right)
                            .Select((refVar, index) => new StopCauseAstNode
                            {
                                NodeId = $"scl:{report.LogicUnitId}:input:{index}",
                                NodeType = StopCauseAstNodeType.Contact,
                                VariableName = refVar,
                                DisplayName = refVar,
                                ExpectedValue = "True"
                            })
                            .ToList()
                        : new List<StopCauseAstNode> { expressionNode };

                    nodes.Add(new StopCauseAstNode
                    {
                        NodeId = $"scl:{report.LogicUnitId}:{left}",
                        NodeType = nodeType,
                        VariableName = left,
                        DisplayName = left,
                        Operator = nodeType,
                        ExpectedValue = "True",
                        Children = children,
                        Metadata = new Dictionary<string, string>
                        {
                            ["Statement"] = statement,
                            ["RightExpression"] = right
                        }
                    });

                    continue;
                }

                var callMatch = SclCallRegex.Match(statement);
                if (callMatch.Success)
                {
                    nodes.Add(new StopCauseAstNode
                    {
                        NodeId = $"scl:{report.LogicUnitId}:call:{callMatch.Groups["callee"].Value}",
                        NodeType = StopCauseAstNodeType.ExternalDependency,
                        VariableName = report.OutputVariables?.FirstOrDefault() ?? string.Empty,
                        DisplayName = callMatch.Groups["callee"].Value,
                        ExpectedValue = "Call",
                        Metadata = new Dictionary<string, string>
                        {
                            ["Statement"] = statement,
                            ["Callee"] = callMatch.Groups["callee"].Value,
                            ["Arguments"] = callMatch.Groups["args"].Value
                        }
                    });
                }
            }

            if (nodes.Count == 0)
            {
                return BuildReferenceFallback(report, "SCL 片段未识别出赋值或调用");
            }

            if (nodes.Count == 1)
            {
                return nodes[0];
            }

            return new StopCauseAstNode
            {
                NodeId = $"scl:{report.LogicUnitId}:root",
                NodeType = StopCauseAstNodeType.And,
                DisplayName = report.Title ?? report.BlockName,
                ExpectedValue = "True",
                Children = nodes,
                Metadata = new Dictionary<string, string>
                {
                    ["SourceType"] = report.SourceType ?? string.Empty,
                    ["UnitType"] = report.UnitType ?? string.Empty,
                    ["NetworkId"] = report.NetworkId?.ToString() ?? string.Empty
                }
            };
        }

        private static string ResolveSclAssignmentNodeType(string right)
        {
            if (string.Equals(right?.Trim(), "TRUE", StringComparison.OrdinalIgnoreCase))
            {
                return StopCauseAstNodeType.Set;
            }

            if (string.Equals(right?.Trim(), "FALSE", StringComparison.OrdinalIgnoreCase))
            {
                return StopCauseAstNodeType.Reset;
            }

            return StopCauseAstNodeType.Coil;
        }

        private static List<string> ExtractVariableReferences(string text)
        {
            var results = new List<string>();
            var tokenizedOperands = TokenizeSclExpression(text)
                .Where(it => it.Type == SclTokenType.Operand)
                .Select(it => NormalizeSclOperand(it.Text))
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .ToList();

            foreach (var operand in tokenizedOperands)
            {
                if (SclLogicalKeywords.Contains(operand) || TryParseLiteralSclKeyword(operand))
                {
                    continue;
                }

                if (TryParseSclComparison(operand, out var left, out _, out var right))
                {
                    results.Add(left);
                    results.Add(right);
                    continue;
                }

                results.Add(operand);
            }

            if (results.Count == 0)
            {
                foreach (Match match in VariableReferenceRegex.Matches(text ?? string.Empty))
                {
                    if (match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                    {
                        results.Add(match.Groups[1].Value);
                        continue;
                    }

                    if (match.Groups[2].Success)
                    {
                        results.Add(match.Groups[2].Value + (match.Groups[3].Value ?? string.Empty));
                    }
                }
            }

            return results
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Where(it => !SclLogicalKeywords.Contains(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryParseLiteralSclKeyword(string token)
        {
            return string.Equals(token, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "FALSE", StringComparison.OrdinalIgnoreCase);
        }

        private enum SclTokenType
        {
            Operand,
            And,
            Or,
            Not,
            ComparisonOperator,
            OpenParen,
            CloseParen
        }

        private sealed class SclToken
        {
            public SclToken(SclTokenType type, string text)
            {
                Type = type;
                Text = text;
            }

            public SclTokenType Type { get; }

            public string Text { get; }
        }
    }
}
