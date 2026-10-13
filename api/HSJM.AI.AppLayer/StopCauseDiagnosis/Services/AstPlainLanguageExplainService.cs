using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public class AstPlainLanguageExplainService : ISelfScopedAutoInject
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IPLC_LogicUnitAstService _logicUnitAstService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_VariableAccessService _variableAccessService;

        public AstPlainLanguageExplainService(
            IPLC_LogicUnitAstService logicUnitAstService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_BlockService blockService,
            IPLC_VariableAccessService variableAccessService)
        {
            _logicUnitAstService = logicUnitAstService;
            _logicUnitService = logicUnitService;
            _blockService = blockService;
            _variableAccessService = variableAccessService;
        }

        public async Task<HttpResponseResultModel<AstPlainExplainResultDto>> ExplainAsync(AstPlainExplainRequestDto request)
        {
            var response = new HttpResponseResultModel<AstPlainExplainResultDto>();
            if (request == null || request.LogicUnitId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "LogicUnitId 不能为空";
                return response;
            }

            var normalized = new AstPlainExplainRequestDto
            {
                LogicUnitId = request.LogicUnitId,
                ExpandCalls = request.ExpandCalls,
                MaxCallDepth = Math.Clamp(request.MaxCallDepth < 0 ? 3 : request.MaxCallDepth, 0, 8),
                MaxExpandedUnitsPerCall = Math.Clamp(request.MaxExpandedUnitsPerCall <= 0 ? 80 : request.MaxExpandedUnitsPerCall, 1, 300),
                IncludeTree = request.IncludeTree,
                IncludeRawAstJson = request.IncludeRawAstJson
            };

            var warnings = new List<string>();
            var root = await BuildUnitAsync(
                normalized.LogicUnitId,
                normalized,
                depth: 0,
                inheritedSubstitution: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                inheritedInstanceDb: null,
                path: new HashSet<long>(),
                warnings);

            if (root == null)
            {
                response.IsSuccess = false;
                response.ErrorMessage = $"未找到 LogicUnitId={normalized.LogicUnitId} 对应的已编译 AST";
                return response;
            }

            response.BackResult = new AstPlainExplainResultDto
            {
                LogicUnitId = normalized.LogicUnitId,
                ExpandCalls = normalized.ExpandCalls,
                MaxCallDepth = normalized.MaxCallDepth,
                Root = root,
                Warnings = warnings
            };
            response.IsSuccess = true;
            return response;
        }

        private async Task<AstPlainExplainUnitDto> BuildUnitAsync(
            long logicUnitId,
            AstPlainExplainRequestDto request,
            int depth,
            Dictionary<string, string> inheritedSubstitution,
            string inheritedInstanceDb,
            HashSet<long> path,
            List<string> globalWarnings)
        {
            if (path.Contains(logicUnitId))
            {
                globalWarnings.Add($"检测到递归调用环，已跳过 LogicUnitId={logicUnitId}");
                return null;
            }

            var ast = await LoadAstAsync(logicUnitId);
            if (ast == null || string.IsNullOrWhiteSpace(ast.RootNodeJson))
            {
                globalWarnings.Add($"LogicUnitId={logicUnitId} 没有 RootNodeJson 或尚未编译 AST");
                return null;
            }

            StopCauseAstNode root;
            try
            {
                root = JsonSerializer.Deserialize<StopCauseAstNode>(ast.RootNodeJson, JsonOptions);
            }
            catch (Exception ex)
            {
                globalWarnings.Add($"LogicUnitId={logicUnitId} 的 RootNodeJson 解析失败：{ex.Message}");
                return null;
            }

            if (root == null)
            {
                globalWarnings.Add($"LogicUnitId={logicUnitId} 的 RootNodeJson 为空树");
                return null;
            }

            var logicUnit = await _logicUnitService.GetByIdAsync(logicUnitId);
            PLC_BlockEntity block = null;
            if (logicUnit?.BlockId != null)
            {
                block = await _blockService.GetByIdAsync(logicUnit.BlockId.Value);
            }

            var substitution = CloneDictionary(inheritedSubstitution);
            var stats = CountNodes(root);
            var unit = new AstPlainExplainUnitDto
            {
                LogicUnitId = logicUnitId,
                ProjectId = ast.ProjectId ?? block?.ProjectId,
                StationCode = ast.StationCode ?? block?.StationCode,
                BlockId = ast.BlockId ?? logicUnit?.BlockId,
                BlockName = ast.BlockName ?? block?.BlockName,
                UnitType = ast.UnitType ?? logicUnit?.UnitType,
                NetworkId = ast.NetworkId ?? logicUnit?.NetworkId,
                Title = ast.Title ?? logicUnit?.Title,
                Comment = ast.Comment ?? logicUnit?.Comment,
                ProgrammingLanguage = ast.ProgrammingLanguage ?? logicUnit?.ProgrammingLanguage,
                CompileStatus = ast.CompileStatus,
                InputVariables = SubstituteList(DeserializeStringList(ast.InputVariablesJson), substitution, inheritedInstanceDb),
                OutputVariables = SubstituteList(DeserializeStringList(ast.OutputVariablesJson), substitution, inheritedInstanceDb),
                NodeCount = stats.Count,
                MaxDepth = stats.MaxDepth,
                Expression = DescribeExpression(root, substitution, inheritedInstanceDb),
                TreeLines = request.IncludeTree ? BuildTreeLines(root, substitution, inheritedInstanceDb) : new List<string>(),
                RawAstJson = request.IncludeRawAstJson ? ast.RootNodeJson : null
            };

            unit.PlainText = BuildUnitPlainText(unit);

            if (request.ExpandCalls && depth < request.MaxCallDepth && logicUnit != null)
            {
                path.Add(logicUnitId);
                unit.Calls = await BuildCallsAsync(logicUnit, ast, request, depth, path, globalWarnings);
                path.Remove(logicUnitId);
            }

            return unit;
        }

        private async Task<List<AstPlainExplainCallDto>> BuildCallsAsync(
            PLC_LogicUnitEntity caller,
            PLC_LogicUnitAstEntity callerAst,
            AstPlainExplainRequestDto request,
            int depth,
            HashSet<long> path,
            List<string> globalWarnings)
        {
            var bindings = DeserializeCallBindings(caller.CallPinBindingsJson);
            if (bindings.Count == 0)
                return new List<AstPlainExplainCallDto>();

            var result = new List<AstPlainExplainCallDto>();
            var projectId = callerAst.ProjectId;

            foreach (var binding in bindings)
            {
                var call = new AstPlainExplainCallDto
                {
                    CalleeName = binding.callee_name,
                    CalleeBlockType = binding.callee_block_type,
                    InstanceDb = binding.instance_db,
                    CallUid = binding.call_uid,
                    InputPins = CloneDictionary(binding.input_pins),
                    OutputPins = CloneDictionary(binding.output_pins),
                    InOutPins = CloneDictionary(binding.inout_pins),
                    SubstitutionMap = BuildSubstitutionMap(binding)
                };

                call.RelevantFormalPins = BuildRelevantFormalPins(binding);

                if (string.IsNullOrWhiteSpace(binding.callee_name))
                {
                    call.Warnings.Add("调用缺少 callee_name，无法下钻");
                    result.Add(call);
                    continue;
                }

                var calleeBlock = await FindCalleeBlockAsync(projectId, binding.callee_name);
                if (calleeBlock == null)
                {
                    call.Warnings.Add($"未找到被调用块 {binding.callee_name}");
                    result.Add(call);
                    continue;
                }

                var calleeLogicUnitIds = await FindCalleeLogicUnitIdsAsync(
                    calleeBlock.Id,
                    call.RelevantFormalPins,
                    binding,
                    request.MaxExpandedUnitsPerCall);

                if (calleeLogicUnitIds.Count == 0)
                {
                    call.Warnings.Add($"被调用块 {binding.callee_name} 未找到与引脚相关的 AST 写入/读取逻辑");
                    result.Add(call);
                    continue;
                }

                foreach (var calleeLogicUnitId in calleeLogicUnitIds)
                {
                    var child = await BuildUnitAsync(
                        calleeLogicUnitId,
                        request,
                        depth + 1,
                        call.SubstitutionMap,
                        binding.instance_db,
                        path,
                        globalWarnings);

                    if (child != null)
                        call.ExpandedUnits.Add(child);
                }

                if (call.ExpandedUnits.Count == 0 && call.Warnings.Count == 0)
                    call.Warnings.Add($"被调用块 {binding.callee_name} 的相关逻辑未能生成 AST 白话解释");

                result.Add(call);
            }

            return result;
        }

        private async Task<PLC_LogicUnitAstEntity> LoadAstAsync(long logicUnitId)
        {
            var rows = await _logicUnitAstService.GetListAsync(it =>
                it.IsDeleted == false &&
                it.LogicUnitId == logicUnitId);

            return rows?
                .OrderByDescending(it => string.Equals(it.CompileStatus, "Compiled", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(it => it.ModifyTime ?? it.CreateTime ?? DateTime.MinValue)
                .FirstOrDefault();
        }

        private async Task<PLC_BlockEntity> FindCalleeBlockAsync(long? projectId, string calleeName)
        {
            var blocks = await _blockService.GetListAsync(it =>
                it.IsDeleted == false &&
                it.BlockName == calleeName &&
                (projectId == null || it.ProjectId == projectId));

            return blocks?.FirstOrDefault()
                ?? (await _blockService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.BlockName == calleeName))?.FirstOrDefault();
        }

        private async Task<List<long>> FindCalleeLogicUnitIdsAsync(
            long calleeBlockId,
            List<string> relevantFormalPins,
            BlockCallPinBindingRecord binding,
            int maxCount)
        {
            var formalNames = ExpandFormalNames(relevantFormalPins);
            var useOutputSemantics = (binding.output_pins?.Count ?? 0) > 0 || (binding.inout_pins?.Count ?? 0) > 0;

            if (formalNames.Count > 0)
            {
                var accesses = await _variableAccessService.GetListAsync(va =>
                    va.IsDeleted == false &&
                    va.BlockId == calleeBlockId &&
                    formalNames.Contains(va.VariableName));

                var matched = (accesses ?? new List<PLC_VariableAccessEntity>())
                    .Where(va => va.LogicUnitId.HasValue)
                    .Where(va => useOutputSemantics
                        ? IsWriteAccess(va.AccessType)
                        : IsReadOrWriteAccess(va.AccessType))
                    .Select(va => va.LogicUnitId.Value)
                    .Distinct()
                    .Take(maxCount)
                    .ToList();

                if (matched.Count > 0)
                    return matched;
            }

            var fallbackRows = await _logicUnitAstService.GetListAsync(it =>
                it.IsDeleted == false &&
                it.BlockId == calleeBlockId &&
                !string.IsNullOrWhiteSpace(it.RootNodeJson));

            return (fallbackRows ?? new List<PLC_LogicUnitAstEntity>())
                .Where(it => it.LogicUnitId.HasValue)
                .OrderBy(it => it.NetworkId ?? int.MaxValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .Take(maxCount)
                .ToList();
        }

        private static List<string> BuildRelevantFormalPins(BlockCallPinBindingRecord binding)
        {
            var pins = new List<string>();
            if (binding.output_pins != null)
                pins.AddRange(binding.output_pins.Keys);
            if (binding.inout_pins != null)
                pins.AddRange(binding.inout_pins.Keys);
            if (pins.Count == 0 && binding.input_pins != null)
                pins.AddRange(binding.input_pins.Keys);

            return pins
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(it => it, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ExpandFormalNames(IEnumerable<string> pins)
        {
            return (pins ?? Enumerable.Empty<string>())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .SelectMany(pin =>
                {
                    var trimmed = pin.Trim();
                    return trimmed.StartsWith("#", StringComparison.Ordinal)
                        ? new[] { trimmed, trimmed.TrimStart('#') }
                        : new[] { trimmed, "#" + trimmed };
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsWriteAccess(string accessType)
        {
            return string.Equals(accessType, "WRITE", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(accessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReadOrWriteAccess(string accessType)
        {
            return string.Equals(accessType, "READ", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(accessType, "WRITE", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(accessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildUnitPlainText(AstPlainExplainUnitDto unit)
        {
            var location = $"{unit.BlockName ?? "未知块"}";
            if (unit.NetworkId.HasValue)
                location += $" / Network {unit.NetworkId}";

            var output = unit.OutputVariables.Count > 0
                ? string.Join(", ", unit.OutputVariables.Take(6))
                : "未识别输出";

            var expr = string.IsNullOrWhiteSpace(unit.Expression)
                ? "AST 暂无可读表达式"
                : unit.Expression;

            return $"{location}：该逻辑主要写入 {output}。{expr}";
        }

        private static string DescribeExpression(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            if (node == null)
                return string.Empty;

            switch (node.NodeType)
            {
                case StopCauseAstNodeType.Anchor:
                {
                    var children = node.Children ?? new List<StopCauseAstNode>();
                    if (children.Count == 0)
                        return DescribeNode(node, substitution, instanceDb);

                    var childExpressions = children
                        .Select(child => DescribeExpression(child, substitution, instanceDb))
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .ToList();

                    return childExpressions.Count == 0
                        ? DescribeNode(node, substitution, instanceDb)
                        : string.Join("；", childExpressions);
                }
                case StopCauseAstNodeType.And:
                    return DescribeAnd(node, substitution, instanceDb);
                case StopCauseAstNodeType.Or:
                    return DescribeOr(node, substitution, instanceDb);
                case StopCauseAstNodeType.Not:
                {
                    var inner = node.Children?.FirstOrDefault();
                    var innerText = DescribeExpression(inner, substitution, instanceDb);
                    return string.IsNullOrWhiteSpace(innerText) ? "条件取反" : $"不满足（{innerText}）";
                }
                case StopCauseAstNodeType.Coil:
                case StopCauseAstNodeType.Set:
                case StopCauseAstNodeType.Reset:
                case StopCauseAstNodeType.Move:
                case StopCauseAstNodeType.SetResetFlipFlop:
                    return DescribeOutputAction(node, substitution, instanceDb);
                case StopCauseAstNodeType.GraphTransition:
                    return DescribeGraphTransition(node, substitution, instanceDb);
                default:
                    return DescribeNode(node, substitution, instanceDb);
            }
        }

        private static string DescribeAnd(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var children = node.Children ?? new List<StopCauseAstNode>();
            if (children.Count == 0)
                return string.Empty;

            if (IsParallelOutputAggregate(node))
            {
                var actions = children
                    .Select(child => DescribeExpression(child, substitution, instanceDb))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                return actions.Count == 0
                    ? string.Empty
                    : $"本网络并列更新：{string.Join("；", actions)}";
            }

            var outputChildren = children.Where(IsOutputNode).ToList();
            if (outputChildren.Count > 0)
            {
                var conditions = children
                    .Where(child => !IsOutputNode(child))
                    .Select(child => DescribeCondition(child, substitution, instanceDb))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                var outputs = outputChildren
                    .Select(child => DescribeOutputAction(child, substitution, instanceDb))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                if (conditions.Count > 0 && outputs.Count > 0)
                    return $"当 {string.Join(" 且 ", conditions)} 时，{string.Join("，并且", outputs)}";
                if (outputs.Count > 0)
                    return string.Join("；", outputs);
            }

            var parts = children
                .Select(child => DescribeCondition(child, substitution, instanceDb))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return parts.Count == 0 ? string.Empty : $"同时满足：{string.Join(" 且 ", parts)}";
        }

        private static string DescribeOr(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var parts = (node.Children ?? new List<StopCauseAstNode>())
                .Select(child => DescribeCondition(child, substitution, instanceDb))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return parts.Count == 0 ? string.Empty : $"满足任一：{string.Join(" 或 ", parts)}";
        }

        private static string DescribeGraphTransition(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var target = ResolveName(node, substitution, instanceDb);
            var parts = (node.Children ?? new List<StopCauseAstNode>())
                .Select(child => DescribeCondition(child, substitution, instanceDb))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return parts.Count == 0
                ? $"GRAPH 转换 {target} 成立"
                : $"当 {string.Join(" 且 ", parts)} 时，GRAPH 转换 {target} 成立";
        }

        private static string DescribeCondition(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            if (node == null)
                return string.Empty;

            switch (node.NodeType)
            {
                case StopCauseAstNodeType.Contact:
                {
                    var expected = ResolveContactExpectedValue(node);
                    return $"{ResolveName(node, substitution, instanceDb)} 为 {(expected ? "TRUE" : "FALSE")}";
                }
                case StopCauseAstNodeType.Not:
                {
                    var inner = DescribeCondition(node.Children?.FirstOrDefault(), substitution, instanceDb);
                    return string.IsNullOrWhiteSpace(inner) ? "条件不成立" : $"不满足（{inner}）";
                }
                case StopCauseAstNodeType.And:
                {
                    var parts = (node.Children ?? new List<StopCauseAstNode>())
                        .Select(child => DescribeCondition(child, substitution, instanceDb))
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .ToList();
                    return parts.Count == 0 ? string.Empty : string.Join(" 且 ", parts);
                }
                case StopCauseAstNodeType.Or:
                {
                    var parts = (node.Children ?? new List<StopCauseAstNode>())
                        .Select(child => DescribeCondition(child, substitution, instanceDb))
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .ToList();
                    return parts.Count == 0 ? string.Empty : $"({string.Join(" 或 ", parts)})";
                }
                case StopCauseAstNodeType.Compare:
                {
                    var left = FirstNonEmpty(ResolveVariableName(node, substitution, instanceDb), TryGetMetadata(node, "Left"));
                    var right = FirstNonEmpty(TryGetMetadata(node, "Right"), node.ExpectedValue, "?");
                    return $"{left} {NormalizeOperator(node.Operator)} {right}";
                }
                case StopCauseAstNodeType.Coil:
                case StopCauseAstNodeType.Set:
                case StopCauseAstNodeType.Reset:
                case StopCauseAstNodeType.Move:
                case StopCauseAstNodeType.SetResetFlipFlop:
                    return DescribeOutputAction(node, substitution, instanceDb);
                default:
                    return DescribeNode(node, substitution, instanceDb);
            }
        }

        private static string DescribeOutputAction(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var target = ResolveName(node, substitution, instanceDb);
            return node.NodeType switch
            {
                StopCauseAstNodeType.Set => $"置位 {target}",
                StopCauseAstNodeType.Reset => $"复位 {target}",
                StopCauseAstNodeType.GraphTransition => DescribeGraphTransition(node, substitution, instanceDb),
                StopCauseAstNodeType.Move => DescribeMove(node, substitution, instanceDb),
                StopCauseAstNodeType.SetResetFlipFlop => DescribeSetResetFlipFlop(node, substitution, instanceDb),
                _ => $"写入/输出 {target}"
            };
        }

        private static string DescribeNode(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            if (node == null)
                return string.Empty;

            return node.NodeType switch
            {
                StopCauseAstNodeType.Compare => DescribeCondition(node, substitution, instanceDb),
                StopCauseAstNodeType.Timer => DescribeTimer(node, substitution, instanceDb),
                StopCauseAstNodeType.Move => DescribeMove(node, substitution, instanceDb),
                StopCauseAstNodeType.GraphStepActive => $"GRAPH 步 {ResolveName(node, substitution, instanceDb)} 激活",
                StopCauseAstNodeType.Reference => $"引用片段 {ResolveName(node, substitution, instanceDb)}",
                StopCauseAstNodeType.ExternalDependency => $"依赖外部逻辑 {ResolveName(node, substitution, instanceDb)}",
                StopCauseAstNodeType.SetResetFlipFlop => DescribeSetResetFlipFlop(node, substitution, instanceDb),
                StopCauseAstNodeType.Opaque => $"暂不可解析片段 {ResolveName(node, substitution, instanceDb)}",
                _ => $"{node.NodeType} {ResolveName(node, substitution, instanceDb)}".Trim()
            };
        }

        private static string DescribeTimer(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var timerType = FirstNonEmpty(TryGetMetadata(node, "TimerType"), node.Operator, "Timer");
            var q = ApplySubstitution(FirstNonEmpty(TryGetMetadata(node, "Q"), node.VariableName), substitution, instanceDb);
            var pt = ApplySubstitution(TryGetMetadata(node, "PT"), substitution, instanceDb);
            var et = ApplySubstitution(TryGetMetadata(node, "ET"), substitution, instanceDb);
            var input = node.Children?.Count > 0
                ? DescribeCondition(node.Children[0], substitution, instanceDb)
                : string.Empty;

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(input))
                details.Add($"IN 条件：{input}");
            if (!string.IsNullOrWhiteSpace(pt))
                details.Add($"PT={pt}");
            if (!string.IsNullOrWhiteSpace(et))
                details.Add($"ET={et}");
            if (!string.IsNullOrWhiteSpace(q))
                details.Add($"Q 输出={q}");

            var prefix = $"{timerType} 定时器 {ResolveName(node, substitution, instanceDb)}";
            return details.Count == 0 ? prefix : $"{prefix}（{string.Join("，", details)}）";
        }

        private static string DescribeMove(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var source = ApplySubstitution(
                FirstNonEmpty(TryGetMetadata(node, "Source"), TryGetMetadata(node, "IN"), TryGetMetadata(node, "In")),
                substitution,
                instanceDb);
            var destination = ApplySubstitution(
                FirstNonEmpty(TryGetMetadata(node, "Destination"), TryGetMetadata(node, "OUT1"), TryGetMetadata(node, "Target"), node.VariableName),
                substitution,
                instanceDb);
            var enable = node.Children?.Count > 0
                ? DescribeCondition(node.Children[0], substitution, instanceDb)
                : string.Empty;

            var action = string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)
                ? $"执行 MOVE {ResolveName(node, substitution, instanceDb)}"
                : $"将 {source} 传送到 {destination}";

            return string.IsNullOrWhiteSpace(enable)
                ? action
                : $"当 {enable} 时，{action}";
        }

        private static string DescribeSetResetFlipFlop(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var flipFlopType = FirstNonEmpty(TryGetMetadata(node, "FlipFlopType"), node.Operator, "SR");
            var q = ApplySubstitution(FirstNonEmpty(TryGetMetadata(node, "Q"), node.VariableName), substitution, instanceDb);
            if (string.IsNullOrWhiteSpace(q))
                q = ResolveName(node, substitution, instanceDb);

            var children = node.Children ?? new List<StopCauseAstNode>();
            var setCondition = children.Count > 0
                ? DescribeCondition(children[0], substitution, instanceDb)
                : "未识别";
            var resetCondition = children.Count > 1
                ? DescribeCondition(children[1], substitution, instanceDb)
                : "未识别";
            var priority = string.Equals(flipFlopType, "RS", StringComparison.OrdinalIgnoreCase)
                ? "复位优先"
                : "置位优先";

            return $"{flipFlopType} 触发器输出 {q}（{priority}）：S 条件={setCondition}，R 条件={resetCondition}，两者都不满足时保持 Q 当前值";
        }

        private static List<string> BuildTreeLines(
            StopCauseAstNode root,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var lines = new List<string>();
            Walk(root, 0, lines, substitution, instanceDb);
            return lines;
        }

        private static void Walk(
            StopCauseAstNode node,
            int depth,
            List<string> lines,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            if (node == null)
                return;

            var prefix = new string(' ', depth * 2);
            lines.Add($"{prefix}- {NodeTypeLabel(node.NodeType)}：{DescribeExpression(node, substitution, instanceDb)}");

            foreach (var child in node.Children ?? new List<StopCauseAstNode>())
            {
                Walk(child, depth + 1, lines, substitution, instanceDb);
            }
        }

        private static string NodeTypeLabel(string nodeType)
        {
            return nodeType switch
            {
                StopCauseAstNodeType.And => "与逻辑",
                StopCauseAstNodeType.Or => "或逻辑",
                StopCauseAstNodeType.Not => "取反",
                StopCauseAstNodeType.Contact => "触点",
                StopCauseAstNodeType.Coil => "线圈",
                StopCauseAstNodeType.Set => "置位",
                StopCauseAstNodeType.Reset => "复位",
                StopCauseAstNodeType.Compare => "比较",
                StopCauseAstNodeType.GraphTransition => "GRAPH转换",
                StopCauseAstNodeType.GraphStepActive => "GRAPH步激活",
                StopCauseAstNodeType.ExternalDependency => "外部依赖",
                StopCauseAstNodeType.Reference => "引用",
                StopCauseAstNodeType.Anchor => "锚点",
                StopCauseAstNodeType.Timer => "定时器",
                StopCauseAstNodeType.Move => "赋值",
                StopCauseAstNodeType.SetResetFlipFlop => "SR触发器",
                StopCauseAstNodeType.Opaque => "不可解析",
                _ => string.IsNullOrWhiteSpace(nodeType) ? "未知节点" : nodeType
            };
        }

        private static (int Count, int MaxDepth) CountNodes(StopCauseAstNode root)
        {
            var count = 0;
            var maxDepth = 0;

            void Walk(StopCauseAstNode node, int depth)
            {
                if (node == null)
                    return;

                count++;
                maxDepth = Math.Max(maxDepth, depth);
                foreach (var child in node.Children ?? new List<StopCauseAstNode>())
                    Walk(child, depth + 1);
            }

            Walk(root, 0);
            return (count, maxDepth);
        }

        private static bool IsOutputNode(StopCauseAstNode node)
        {
            return node?.NodeType == StopCauseAstNodeType.Coil
                   || node?.NodeType == StopCauseAstNodeType.Set
                   || node?.NodeType == StopCauseAstNodeType.Reset
                   || node?.NodeType == StopCauseAstNodeType.GraphTransition
                   || node?.NodeType == StopCauseAstNodeType.Move
                   || node?.NodeType == StopCauseAstNodeType.SetResetFlipFlop;
        }

        private static bool IsParallelOutputAggregate(StopCauseAstNode node)
        {
            if (node == null
                || node.NodeType != StopCauseAstNodeType.And
                || (node.Children?.Count ?? 0) <= 1)
            {
                return false;
            }

            var sourceType = TryGetMetadata(node, "SourceType");
            var semantics = TryGetMetadata(node, "Semantics");
            var isLadLike = string.Equals(sourceType, "LAD", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(sourceType, "FBD", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(semantics, "ParallelOutputs", StringComparison.OrdinalIgnoreCase)
                            || (node.NodeId?.IndexOf(":lad-root", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

            return isLadLike && node.Children.Any(child =>
                IsOutputNode(child) || child.NodeType == StopCauseAstNodeType.ExternalDependency);
        }

        private static string ResolveName(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var variable = ResolveVariableName(node, substitution, instanceDb);
            var display = node?.DisplayName;

            if (string.IsNullOrWhiteSpace(display) || string.Equals(display, node?.VariableName, StringComparison.OrdinalIgnoreCase))
                return FirstNonEmpty(variable, node?.NodeId, "?");

            if (string.IsNullOrWhiteSpace(variable) || string.Equals(display, variable, StringComparison.OrdinalIgnoreCase))
                return display;

            return $"{display}（{variable}）";
        }

        private static string ResolveVariableName(
            StopCauseAstNode node,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            var raw = FirstNonEmpty(node?.VariableName, TryGetMetadata(node, "Target"), node?.DisplayName, node?.NodeId);
            return ApplySubstitution(raw, substitution, instanceDb);
        }

        private static string ApplySubstitution(
            string value,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            var trimmed = value.Trim();
            if (substitution != null)
            {
                if (substitution.TryGetValue(trimmed, out var mapped))
                    return mapped;

                var noHash = trimmed.TrimStart('#');
                if (substitution.TryGetValue(noHash, out mapped))
                    return mapped;

                var withHash = trimmed.StartsWith("#", StringComparison.Ordinal) ? trimmed : "#" + trimmed;
                if (substitution.TryGetValue(withHash, out mapped))
                    return mapped;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(instanceDb))
                return $"{instanceDb}.{trimmed.TrimStart('#')}";

            return trimmed;
        }

        private static Dictionary<string, string> BuildSubstitutionMap(BlockCallPinBindingRecord binding)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(Dictionary<string, string> source)
            {
                if (source == null)
                    return;

                foreach (var kv in source)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        continue;

                    result[kv.Key] = kv.Value;
                    if (!kv.Key.StartsWith("#", StringComparison.Ordinal))
                        result["#" + kv.Key] = kv.Value;
                }
            }

            Add(binding.input_pins);
            Add(binding.output_pins);
            Add(binding.inout_pins);
            return result;
        }

        private static List<string> SubstituteList(
            IEnumerable<string> values,
            Dictionary<string, string> substitution,
            string instanceDb)
        {
            return (values ?? Enumerable.Empty<string>())
                .Select(value => ApplySubstitution(value, substitution, instanceDb))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> DeserializeStringList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<string>();

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static List<BlockCallPinBindingRecord> DeserializeCallBindings(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<BlockCallPinBindingRecord>();

            try
            {
                return JsonSerializer.Deserialize<List<BlockCallPinBindingRecord>>(json, JsonOptions)
                       ?? new List<BlockCallPinBindingRecord>();
            }
            catch
            {
                return new List<BlockCallPinBindingRecord>();
            }
        }

        private static Dictionary<string, string> CloneDictionary(Dictionary<string, string> source)
        {
            return source == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        }

        private static bool TryGetMetadataBool(StopCauseAstNode node, string key)
        {
            var text = TryGetMetadata(node, key);
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(text, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ResolveContactExpectedValue(StopCauseAstNode node)
        {
            var expectedText = FirstNonEmpty(TryGetMetadata(node, "ExpectedValue"), TryGetMetadata(node, "Expected"), node?.ExpectedValue);
            var expected = string.IsNullOrWhiteSpace(expectedText) || !IsFalseLiteral(expectedText);
            if (TryGetMetadataBool(node, "Negated"))
                expected = !expected;

            return expected;
        }

        private static bool IsFalseLiteral(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim().Trim('"', '\'').ToLowerInvariant();
            return normalized.StartsWith("false", StringComparison.Ordinal)
                   || normalized == "0"
                   || normalized == "off"
                   || normalized == "no"
                   || normalized.Contains("断", StringComparison.Ordinal)
                   || normalized == "否"
                   || normalized == "低";
        }

        private static string TryGetMetadata(StopCauseAstNode node, string key)
        {
            return node?.Metadata != null && node.Metadata.TryGetValue(key, out var value)
                ? value
                : string.Empty;
        }

        private static string NormalizeOperator(string op)
        {
            return op switch
            {
                "Eq" => "==",
                "Ne" => "!=",
                "Gt" => ">",
                "Ge" => ">=",
                "Lt" => "<",
                "Le" => "<=",
                null => "==",
                "" => "==",
                _ => op
            };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

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
