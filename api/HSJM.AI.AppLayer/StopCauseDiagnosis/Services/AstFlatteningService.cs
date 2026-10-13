using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 跨边界AST展平服务
    ///
    /// 核心算法（用户描述的"具象化替换"与"逻辑树嫁接"）：
    ///
    ///   外层 Network 写入 Q0.5，方式是调用 FB_Cylinder(DB10_Cyl_Push)：
    ///     OUT_Push → Q0.5  (output_pins)
    ///     IN_Enable → I0.1 (input_pins)
    ///
    ///   展平步骤：
    ///   1. 发现 Q0.5 有写入来源 = Network_N (包含 Call_FB_Cylinder)
    ///   2. 找到 FB_Cylinder 内部写 OUT_Push 的逻辑单元
    ///   3. 对该逻辑单元的 AST 做"具象化"：
    ///      - 形参 #IN_Enable  → 实参 I0.1              (via input_pins map)
    ///      - 静态 #Stat_Ready → DB10_Cyl_Push.Stat_Ready (via instance_db + local var prefix)
    ///      - 全局 I0.2        → I0.2                   (原封不动)
    ///   4. 将具象化后的子树嫁接到外层 Q0.5 节点上
    ///   5. 递归：若 DB10_Cyl_Push.Stat_Ready 本身也来自某个写逻辑，继续展平
    /// </summary>
    public class AstFlatteningService : ISelfScopedAutoInject
    {
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_BlockService _blockService;

        // 防止循环展平的最大递归深度
        private const int MaxInlineDepth = 5;

        public AstFlatteningService(
            IPLC_LogicUnitService logicUnitService,
            IPLC_VariableAccessService variableAccessService,
            IPLC_BlockService blockService)
        {
            _logicUnitService = logicUnitService;
            _variableAccessService = variableAccessService;
            _blockService = blockService;
        }

        /// <summary>
        /// 尝试对一个已编译但仍包含 Call 的 LAD 节点进行跨边界展平。
        /// 如果该 Network 包含 FB/FC 调用且其 output_pins 包含目标锚点变量，
        /// 则深入调用目标块，构建具象化树并嫁接回来。
        /// </summary>
        public async Task<StopCauseAstNode> TryFlattenAsync(
            long projectId,
            StopCauseAstNode anchorNode,
            AstCoverageReport report,
            int depth = 0)
        {
            if (depth > MaxInlineDepth)
                return anchorNode;

            if (string.IsNullOrWhiteSpace(report.CallPinBindingsJson))
                return anchorNode;

            List<BlockCallPinBindingRecord> bindings;
            try
            {
                bindings = JsonSerializer.Deserialize<List<BlockCallPinBindingRecord>>(
                    report.CallPinBindingsJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return anchorNode;
            }

            if (bindings == null || bindings.Count == 0)
                return anchorNode;

            var anchorVar = report.OutputVariables?.FirstOrDefault() ?? anchorNode.VariableName;

            // 找到输出引脚包含锚点变量的调用
            var relevantBinding = bindings.FirstOrDefault(b =>
                b.output_pins != null &&
                b.output_pins.Values.Any(v => string.Equals(v, anchorVar, StringComparison.OrdinalIgnoreCase)));

            if (relevantBinding == null)
                return anchorNode;

            // FormParameter 对应 anchorVar 的形参名
            var formalOutputPin = relevantBinding.output_pins
                .FirstOrDefault(kv => string.Equals(kv.Value, anchorVar, StringComparison.OrdinalIgnoreCase)).Key;

            if (string.IsNullOrWhiteSpace(formalOutputPin))
                return anchorNode;

            // 找到被调用块
            var calleeBlock = await _blockService.GetListAsync(b =>
                b.ProjectId == projectId &&
                b.BlockName == relevantBinding.callee_name &&
                !b.IsDeleted);
            var callee = calleeBlock?.FirstOrDefault();
            if (callee == null)
                return anchorNode; // 被调用块未入库，降级保留

            // 找到被调用块内部写 formalOutputPin 的逻辑单元
            var calleeWriteAccesses = await _variableAccessService.GetListAsync(va =>
                va.BlockId == callee.Id &&
                va.VariableName == formalOutputPin &&
                (va.AccessType == "WRITE" || va.AccessType == "READWRITE") &&
                !va.IsDeleted);

            if (!calleeWriteAccesses.Any())
            {
                // 尝试带 # 前缀查找（FB 内部形参）
                var formalWithHash = formalOutputPin.StartsWith("#") ? formalOutputPin : "#" + formalOutputPin;
                calleeWriteAccesses = await _variableAccessService.GetListAsync(va =>
                    va.BlockId == callee.Id &&
                    va.VariableName == formalWithHash &&
                    (va.AccessType == "WRITE" || va.AccessType == "READWRITE") &&
                    !va.IsDeleted);
            }

            if (!calleeWriteAccesses.Any())
                return anchorNode;

            var calleeLogicUnitIds = calleeWriteAccesses.Select(va => va.LogicUnitId).Distinct().ToList();

            var calleeLogicUnits = await _logicUnitService.GetListAsync(lu =>
                calleeLogicUnitIds.Contains(lu.Id) && !lu.IsDeleted);

            if (!calleeLogicUnits.Any())
                return anchorNode;

            // 构建具象化参数替换表
            var substitution = BuildSubstitutionMap(relevantBinding);

            // 对每个被调用逻辑单元，构建已具象化的子节点
            var inlinedChildren = new List<StopCauseAstNode>();
            foreach (var calleeUnit in calleeLogicUnits)
            {
                var calleeReport = new AstCoverageReport
                {
                    SourceType = calleeUnit.ProgrammingLanguage,
                    UnitType = calleeUnit.UnitType,
                    Title = calleeUnit.Title ?? relevantBinding.callee_name,
                    BlockName = callee.BlockName,
                    LogicUnitId = calleeUnit.Id,
                    NetworkId = calleeUnit.NetworkId,
                    RawContent = calleeUnit.RawContent ?? string.Empty,
                    RawContentExcerpt = TruncateContent(calleeUnit.RawContent, 4000),
                    OutputVariables = new List<string> { formalOutputPin },
                    CallPinBindingsJson = calleeUnit.CallPinBindingsJson,
                };

                // 对 raw_content 做文本级具象化替换（快速路径）
                // 精细的 AST 内变量替换由 ConcretizeNode 完成
                var concreteRawContent = ApplyTextSubstitution(calleeUnit.RawContent, substitution, relevantBinding.instance_db);
                calleeReport.RawContent = concreteRawContent;
                calleeReport.RawContentExcerpt = TruncateContent(concreteRawContent, 4000);

                var calleeNode = new StopCauseAstNode
                {
                    NodeId = $"inline:{callee.BlockName}:{calleeUnit.Id}",
                    NodeType = StopCauseAstNodeType.Reference,
                    DisplayName = $"{relevantBinding.callee_name}[{relevantBinding.instance_db}].{formalOutputPin}",
                    ExpectedValue = "True",
                    Metadata = new Dictionary<string, string>
                    {
                        ["InlinedFrom"] = relevantBinding.callee_name,
                        ["InstanceDb"] = relevantBinding.instance_db ?? string.Empty,
                        ["FormalPin"] = formalOutputPin,
                        ["ActualVar"] = anchorVar,
                        ["InlineDepth"] = depth.ToString(),
                    }
                };

                // 递归展平（若内部还有 Call）
                var deeperNode = await TryFlattenAsync(projectId, calleeNode, calleeReport, depth + 1);
                inlinedChildren.Add(deeperNode);
            }

            if (inlinedChildren.Count == 0)
                return anchorNode;

            // 将内联结果嫁接为 anchorNode 的 Children
            var grafted = new StopCauseAstNode
            {
                NodeId = anchorNode.NodeId,
                NodeType = anchorNode.NodeType,
                VariableName = anchorNode.VariableName,
                DisplayName = anchorNode.DisplayName,
                Operator = anchorNode.Operator,
                ExpectedValue = anchorNode.ExpectedValue,
                Metadata = new Dictionary<string, string>(anchorNode.Metadata ?? new Dictionary<string, string>())
                {
                    ["FlattenedFrom"] = relevantBinding.callee_name,
                    ["InstanceDb"] = relevantBinding.instance_db ?? string.Empty,
                    ["FlattenDepth"] = depth.ToString(),
                },
                Children = inlinedChildren,
            };

            return grafted;
        }

        /// <summary>
        /// 构建具象化替换表：
        ///   形参 → 实参（含 # 前缀的 FB 内部变量）
        ///   局部静态变量 → {instance_db}.VarName
        /// </summary>
        private static Dictionary<string, string> BuildSubstitutionMap(BlockCallPinBindingRecord binding)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void AddPins(Dictionary<string, string> pins)
            {
                if (pins == null) return;
                foreach (var kv in pins)
                {
                    // formal name as-is
                    map[kv.Key] = kv.Value;
                    // with # prefix (FB local param form)
                    if (!kv.Key.StartsWith("#"))
                        map["#" + kv.Key] = kv.Value;
                }
            }

            AddPins(binding.input_pins);
            AddPins(binding.output_pins);
            AddPins(binding.inout_pins);

            return map;
        }

        /// <summary>
        /// 在 raw XML/text 中做正则级文本替换：
        ///  1. 将形参名替换为实参名
        ///  2. 将本地/静态变量 (#VarName) 替换为 {instanceDb}.VarName
        /// 注意：这是快速近似路径。精确路径需要解析 XML AST 再替换。
        /// </summary>
        private static string ApplyTextSubstitution(string rawContent, Dictionary<string, string> substitution, string instanceDb)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
                return rawContent ?? string.Empty;

            var result = rawContent;

            // Step 1: 替换已知引脚绑定（形参→实参）
            foreach (var kv in substitution.OrderByDescending(k => k.Key.Length)) // 从长到短，避免前缀误替换
            {
                // 匹配 Component Name="xxx" 或直接文本 #xxx，整词匹配
                var safeFormal = Regex.Escape(kv.Key.TrimStart('#'));
                result = Regex.Replace(result, $@"(?<![A-Za-z0-9_]){safeFormal}(?![A-Za-z0-9_])", kv.Value);
            }

            // Step 2: 将剩余的 #LocalVar 静态变量具象化为 {instanceDb}.LocalVar
            if (!string.IsNullOrWhiteSpace(instanceDb))
            {
                result = Regex.Replace(result, @"#([A-Za-z_][A-Za-z0-9_]*)",
                    m => $"{instanceDb}.{m.Groups[1].Value}");
            }

            return result;
        }

        private static string TruncateContent(string content, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;
            return content.Length > maxLength ? content.Substring(0, maxLength) : content;
        }

        /// <summary>
        /// 内部 DTO，对应 parser.py 序列化的 BlockCallPinBinding
        /// </summary>
        private class BlockCallPinBindingRecord
        {
            public string callee_name { get; set; }
            public string callee_block_type { get; set; }
            public string instance_db { get; set; }
            public string call_uid { get; set; }
            public Dictionary<string, string> input_pins { get; set; }
            public Dictionary<string, string> output_pins { get; set; }
            public Dictionary<string, string> inout_pins { get; set; }
        }
    }
}
