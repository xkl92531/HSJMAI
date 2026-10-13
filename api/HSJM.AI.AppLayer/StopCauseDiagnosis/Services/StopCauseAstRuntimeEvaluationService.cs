using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 负责把已持久化 AST 与在线快照拼接成可执行求值结果。
    /// 当前先覆盖首版可执行节点：Anchor/And/Or/Not/Contact/Compare/Coil/Set/Reset。
    /// </summary>
    public class StopCauseAstRuntimeEvaluationService : ISelfScopedAutoInject
    {
        private const string StatusTrue = "True";
        private const string StatusFalse = "False";
        private const string StatusUnknown = "Unknown";
        private const string StatusUnsupported = "Unsupported";
        private static readonly HashSet<string> ReservedSymbolKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "AND_THEN", "OR", "OR_ELSE", "NOT"
        };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly StopCauseSignalSnapshotService _signalSnapshotService;
        private readonly IPLC_LogicUnitAstService _logicUnitAstService;
        private readonly IPLC_LogicUnitService _logicUnitService;

        public StopCauseAstRuntimeEvaluationService(
            StopCauseSignalSnapshotService signalSnapshotService,
            IPLC_LogicUnitAstService logicUnitAstService,
            IPLC_LogicUnitService logicUnitService)
        {
            _signalSnapshotService = signalSnapshotService;
            _logicUnitAstService = logicUnitAstService;
            _logicUnitService = logicUnitService;
        }

        public async Task<EvaluatePersistedAstResultDto> EvaluateAsync(EvaluatePersistedAstRequestDto request)
        {
            var astRow = await LoadAstRowAsync(request);
            if (astRow == null)
            {
                throw new InvalidOperationException("未找到匹配的已持久化 AST，请先完成离线编译并确认 LogicUnitId / VariableName 条件。");
            }

            if (string.IsNullOrWhiteSpace(astRow.RootNodeJson))
            {
                throw new InvalidOperationException($"LogicUnit {astRow.LogicUnitId} 缺少 RootNodeJson，当前无法执行在线求值。");
            }

            var rootNode = JsonSerializer.Deserialize<StopCauseAstNode>(astRow.RootNodeJson, JsonOptions);
            if (rootNode == null)
            {
                throw new InvalidOperationException($"LogicUnit {astRow.LogicUnitId} 的 RootNodeJson 反序列化失败。");
            }
            var evaluationRootNode = FocusRootForTargetVariable(rootNode, request.VariableName);

            var inputVariables = DeserializeStringList(astRow.InputVariablesJson)
                .Select(NormalizeRuntimeSymbolPath)
                .Where(IsRuntimeReadableSymbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var outputVariables = DeserializeStringList(astRow.OutputVariablesJson)
                .Select(NormalizeRuntimeSymbolPath)
                .Where(IsRuntimeReadableSymbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var astReferencedSymbols = CollectRuntimeSymbols(evaluationRootNode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var requestedSymbols = astReferencedSymbols
                .Concat(string.IsNullOrWhiteSpace(evaluationRootNode.VariableName) ? Array.Empty<string>() : new[] { NormalizeRuntimeSymbolPath(evaluationRootNode.VariableName) })
                .Concat(string.IsNullOrWhiteSpace(request.VariableName) ? Array.Empty<string>() : new[] { NormalizeRuntimeSymbolPath(request.VariableName) })
                .Concat((request.AdditionalSymbolPaths ?? new List<string>()).Select(NormalizeRuntimeSymbolPath))
                .Where(IsRuntimeReadableSymbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // FB 穿透：将输入形参对应的外部实参加入信号读取列表
            // 支持 | 分隔的多变量映射（如 OR(sensor, bypass) → "sensor|bypass"）
            // "~" 前缀表示常闭触点极性，读取时去掉前缀，得到实际物理信号名
            if (request.FbInputParamMap != null && request.FbInputParamMap.Count > 0)
            {
                var externalActuals = request.FbInputParamMap.Values
                    .SelectMany(v => v.Split('|'))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => NormalizeRuntimeSymbolPath(s.TrimStart('~')))  // 剥除极性前缀再规范化
                    .Where(IsRuntimeReadableSymbol)
                    .Where(s => !requestedSymbols.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                requestedSymbols.AddRange(externalActuals);
            }

            var relatedAnchors = (request.RelatedAnchors ?? new List<string>())
                .Concat(string.IsNullOrWhiteSpace(rootNode.VariableName) ? Array.Empty<string>() : new[] { NormalizeRuntimeSymbolPath(rootNode.VariableName) })
                .Concat(string.IsNullOrWhiteSpace(evaluationRootNode.VariableName) ? Array.Empty<string>() : new[] { NormalizeRuntimeSymbolPath(evaluationRootNode.VariableName) })
                .Concat(string.IsNullOrWhiteSpace(request.VariableName) ? Array.Empty<string>() : new[] { NormalizeRuntimeSymbolPath(request.VariableName) })
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var snapshotResult = await _signalSnapshotService.AcquireAsync(new AcquireDiagnosticSnapshotRequestDto
            {
                ProjectId = request.ProjectId,
                StationCode = string.IsNullOrWhiteSpace(request.StationCode) ? astRow.StationCode : request.StationCode,
                StationMode = request.StationMode,
                IncludeStationAnchorVariables = request.IncludeStationAnchorVariables,
                SymbolPaths = requestedSymbols,
                RelatedAnchors = relatedAnchors,
                PreviousValues = request.PreviousValues,
                PreviousSignalQuality = request.PreviousSignalQuality,
                LastChangeTimes = request.LastChangeTimes,
                UseSimulation = request.UseSimulation,
                SimulatedCurrentValues = request.SimulatedCurrentValues,
            });

            // 预加载 GRAPH Transition AST：收集 rootNode 内所有 StepAction 的 OutgoingTransitionNetworkIds，
            // 批量查 logicunit_ast 表，填充执行上下文缓存，供 EvaluateOutputNode 同步求值。
            if (astRow.BlockId.HasValue)
            {
                await PreloadGraphTransitionAstCacheAsync(
                    evaluationRootNode,
                    astRow.BlockId.Value,
                    request.ProjectId ,
                    snapshotResult.ExecutionContext);
            }

            // FB 穿透：注入形参→实参信号覆盖值（使 #IHP 等 FB 内部形参可被正确求值）
            // 支持 | 分隔的多变量映射，以及 "~" 极性前缀：
            //   无前缀 → 正常触点：信号值直接注入
            //   "~" 前缀 → 常闭触点：信号值取反后注入（~VarName=True 等价于 #IWP=False）
            // 多变量 OR 合并语义：任一触点为激活（注入值为 True）则整体注入 True
            if (request.FbInputParamMap != null && request.FbInputParamMap.Count > 0)
            {
                foreach (var kv in request.FbInputParamMap)
                {
                    var formalName = kv.Key;
                    var externalEntries = kv.Value.Split('|')
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim())
                        .ToList();

                    object resolvedValue = null;
                    foreach (var entry in externalEntries)
                    {
                        // 解析极性前缀
                        var isNegContact = entry.StartsWith("~");
                        var actualVarName = isNegContact ? entry.TrimStart('~') : entry;

                        if (TryGetSignalValue(actualVarName, snapshotResult.ExecutionContext.CurrentValues, out var signalValue))
                        {
                            // 常闭触点：物理信号取反后才是形参的有效值
                            // 例：~Debug_Test.Debug[18]=True → #IWP 注入 False（触点断开）
                            var effectiveValue = isNegContact
                                ? (object)(!(CoerceBoolean(signalValue) ?? false))
                                : signalValue;

                            if (resolvedValue == null)
                            {
                                resolvedValue = effectiveValue;
                            }
                            else
                            {
                                // 多变量 OR 合并：任一触点激活（有效值 True）则结果为 True
                                var prev = CoerceBoolean(resolvedValue);
                                var curr = CoerceBoolean(effectiveValue);
                                if (curr == true) resolvedValue = effectiveValue;
                                else if (prev != true) resolvedValue = effectiveValue;
                            }
                        }
                    }

                    if (resolvedValue != null)
                    {
                        var hashName = formalName.StartsWith("#") ? formalName : "#" + formalName;
                        snapshotResult.ExecutionContext.CurrentValues[hashName] = resolvedValue;
                        var plainName = formalName.TrimStart('#');
                        snapshotResult.ExecutionContext.CurrentValues[plainName] = resolvedValue;

                        foreach (var aliasName in astReferencedSymbols.Where(symbol => SymbolTailEquals(symbol, plainName)))
                        {
                            snapshotResult.ExecutionContext.CurrentValues[aliasName] = resolvedValue;
                        }
                    }
                }
            }

            var rootTrace = EvaluateNode(evaluationRootNode, snapshotResult.ExecutionContext);
            await SupplementContextForDrillDownTransitionsAsync(
                request.ProjectId,
                snapshotResult.StationCode,
                rootTrace,
                snapshotResult,
                relatedAnchors,
                anchorTypeMap: null);
            rootTrace = EvaluateNode(evaluationRootNode, snapshotResult.ExecutionContext);
            var failurePath = BuildFailurePath(rootTrace);
                var messages = BuildMessages(astRow, rootTrace, snapshotResult.MissingSymbols);

            var unsupportedCount = CountNodesByStatus(rootTrace, StatusUnsupported);
            var unknownCount     = CountNodesByStatus(rootTrace, StatusUnknown);
            var diagQuality      = ComputeDiagnosisQuality(rootTrace.Status, unsupportedCount, unknownCount, failurePath);

            return new EvaluatePersistedAstResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = snapshotResult.StationCode,
                LogicUnitId = astRow.LogicUnitId ?? 0,
                BlockId = astRow.BlockId,
                BlockName = astRow.BlockName,
                VariableName = evaluationRootNode.VariableName,
                CompileStatus = astRow.CompileStatus,
                DeviceName = snapshotResult.DeviceName,
                FallbackDeviceName = snapshotResult.FallbackDeviceName,
                InputVariables = inputVariables,
                OutputVariables = outputVariables,
                RequestedSymbols = snapshotResult.RequestedSymbols ?? new List<string>(),
                MissingSymbols = snapshotResult.MissingSymbols ?? new List<string>(),
                EvaluatedValue = rootTrace.Result,
                EvaluationStatus = rootTrace.Status,
                DiagnosisQuality = diagQuality,
                UnsupportedNodeCount = unsupportedCount,
                UnknownNodeCount = unknownCount,
                ExecutionContext = snapshotResult.ExecutionContext,
                Snapshot = snapshotResult.Snapshot,
                RootEvaluation = rootTrace,
                FailurePath = failurePath,
                Messages = messages
            };
        }

        /// <summary>
        /// 批量求值：多个 AST 共享一次 PLC 快照读取。
        /// 
        /// 流程：
        ///   1. 加载所有 AST 行并反序列化
        ///   2. 汇总所有 AST 引用的符号
        ///   3. 一次 PLC 批量读取获取共享快照
        ///   4. 逐个 AST 使用共享的 ExecutionContext 求值
        /// 
        /// 相比逐个调 EvaluateAsync，PLC 请求从 N 次降为 1 次。
        /// </summary>
        public async Task<List<EvaluateBatchItemResult>> EvaluateBatchAsync(
            EvaluateBatchRequest batchRequest,
            StopCauseExecutionContext sharedContext = null)
        {
            if (batchRequest.Targets == null || batchRequest.Targets.Count == 0)
                return new List<EvaluateBatchItemResult>();

            // ── Phase 1: 加载所有 AST 行并反序列化 ──
            var preparedItems = new List<PreparedAstItem>();
            var allSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allRelatedAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allTransitionBlockIds = new HashSet<long>();

            foreach (var target in batchRequest.Targets)
            {
                try
                {
                    PLC_LogicUnitAstEntity astRow;
                    if (target.LogicUnitId > 0)
                    {
                        var targetLogicUnitId = target.LogicUnitId;
                        // 优先从调用方已提供的 astRows 查找
                        astRow = batchRequest.PreloadedAstRows?
                            .FirstOrDefault(a => a.LogicUnitId == targetLogicUnitId);
                        if (astRow == null)
                        {
                            var rows = await _logicUnitAstService.GetListAsync(it =>
                                it.ProjectId == batchRequest.ProjectId &&
                                it.IsDeleted == false &&
                                it.LogicUnitId == targetLogicUnitId);
                            astRow = rows.OrderByDescending(it => ScoreCompileStatus(it.CompileStatus)).FirstOrDefault();
                        }
                    }
                    else
                    {
                        astRow = null;
                    }

                    if (astRow == null || string.IsNullOrWhiteSpace(astRow.RootNodeJson))
                    {
                        preparedItems.Add(new PreparedAstItem { Target = target, Error = "未找到 AST 或 RootNodeJson 为空" });
                        continue;
                    }

                    var rootNode = JsonSerializer.Deserialize<StopCauseAstNode>(astRow.RootNodeJson, JsonOptions);
                    if (rootNode == null)
                    {
                        preparedItems.Add(new PreparedAstItem { Target = target, Error = "RootNodeJson 反序列化失败" });
                        continue;
                    }
                    rootNode = FocusRootForTargetVariable(rootNode, target.VariableName);

                    var inputVars = DeserializeStringList(astRow.InputVariablesJson)
                        .Select(NormalizeRuntimeSymbolPath).Where(IsRuntimeReadableSymbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var outputVars = DeserializeStringList(astRow.OutputVariablesJson)
                        .Select(NormalizeRuntimeSymbolPath).Where(IsRuntimeReadableSymbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var astSymbols = CollectRuntimeSymbols(rootNode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                    foreach (var s in astSymbols) allSymbols.Add(s);
                    if (IsRuntimeReadableSymbol(rootNode.VariableName))
                        allSymbols.Add(NormalizeRuntimeSymbolPath(rootNode.VariableName));
                    if (IsRuntimeReadableSymbol(target.VariableName))
                        allSymbols.Add(NormalizeRuntimeSymbolPath(target.VariableName));

                    if (!string.IsNullOrWhiteSpace(rootNode.VariableName))
                        allRelatedAnchors.Add(NormalizeRuntimeSymbolPath(rootNode.VariableName));
                    if (!string.IsNullOrWhiteSpace(target.VariableName))
                        allRelatedAnchors.Add(NormalizeRuntimeSymbolPath(target.VariableName));

                    if (astRow.BlockId.HasValue)
                        allTransitionBlockIds.Add(astRow.BlockId.Value);

                    preparedItems.Add(new PreparedAstItem
                    {
                        Target = target,
                        AstRow = astRow,
                        RootNode = rootNode,
                        InputVariables = inputVars,
                        OutputVariables = outputVars,
                    });
                }
                catch (Exception ex)
                {
                    preparedItems.Add(new PreparedAstItem { Target = target, Error = ex.Message });
                }
            }

            // 无可用 AST 时直接返回错误结果
            var validItems = preparedItems.Where(p => p.AstRow != null).ToList();
            if (validItems.Count == 0)
            {
                return preparedItems.Select(p => new EvaluateBatchItemResult
                {
                    VariableName = p.Target.VariableName,
                    LogicUnitId = p.Target.LogicUnitId,
                    Error = p.Error ?? "无可用 AST",
                }).ToList();
            }

            // ── Phase 2: PLC 快照获取（有共享上下文则仅增量补读缺失符号） ──
            AcquireDiagnosticSnapshotResultDto snapshotResult;
            if (sharedContext != null)
            {
                // 找出共享上下文中尚不存在的符号
                var missingSymbols = allSymbols
                    .Where(s => !sharedContext.CurrentValues.ContainsKey(NormalizeRuntimeSymbolPath(s)))
                    .ToList();

                if (missingSymbols.Count > 0)
                {
                    // 增量补读：仅读取新 AST 引用但顶层快照未覆盖的符号
                    var supplementary = await _signalSnapshotService.AcquireAsync(new AcquireDiagnosticSnapshotRequestDto
                    {
                        ProjectId = batchRequest.ProjectId,
                        StationCode = batchRequest.StationCode,
                        SymbolPaths = missingSymbols,
                        RelatedAnchors = allRelatedAnchors.ToList(),
                    });

                    // 合并到共享上下文（仅追加，不覆盖已有值）
                    foreach (var kv in supplementary.ExecutionContext.CurrentValues)
                    {
                        if (!sharedContext.CurrentValues.ContainsKey(kv.Key))
                            sharedContext.CurrentValues[kv.Key] = kv.Value;
                    }
                    foreach (var kv in supplementary.ExecutionContext.SignalQuality)
                    {
                        if (!sharedContext.SignalQuality.ContainsKey(kv.Key))
                            sharedContext.SignalQuality[kv.Key] = kv.Value;
                    }
                }

                snapshotResult = new AcquireDiagnosticSnapshotResultDto
                {
                    ProjectId = batchRequest.ProjectId,
                    StationCode = batchRequest.StationCode,
                    ExecutionContext = sharedContext,
                    RequestedSymbols = allSymbols.ToList(),
                    MissingSymbols = new List<string>(),
                };
            }
            else
            {
                snapshotResult = await _signalSnapshotService.AcquireAsync(new AcquireDiagnosticSnapshotRequestDto
                {
                    ProjectId = batchRequest.ProjectId,
                    StationCode = batchRequest.StationCode,
                    SymbolPaths = allSymbols.ToList(),
                    RelatedAnchors = allRelatedAnchors.ToList(),
                    AnchorTypeMap = batchRequest.AnchorTypeMap,
                });
            }

            // ── Phase 3: 预加载所有 GRAPH Transition AST（按 Block 批量，每个 Block 仅一次 DB 查询） ──
            foreach (var blockId in allTransitionBlockIds)
            {
                var blockRootNodes = validItems
                    .Where(p => p.AstRow.BlockId == blockId)
                    .Select(p => p.RootNode)
                    .ToList();
                await PreloadGraphTransitionAstCacheForBlockAsync(
                    blockRootNodes, blockId, batchRequest.ProjectId, snapshotResult.ExecutionContext);
            }

            // ── Phase 4: 逐个 AST 使用共享快照求值 ──
            var results = new List<EvaluateBatchItemResult>();
            foreach (var item in preparedItems)
            {
                if (item.AstRow == null)
                {
                    results.Add(new EvaluateBatchItemResult
                    {
                        VariableName = item.Target.VariableName,
                        LogicUnitId = item.Target.LogicUnitId,
                        Error = item.Error,
                    });
                    continue;
                }

                try
                {
                    var rootTrace = EvaluateNode(item.RootNode, snapshotResult.ExecutionContext);
                    await SupplementContextForDrillDownTransitionsAsync(
                        batchRequest.ProjectId,
                        snapshotResult.StationCode,
                        rootTrace,
                        snapshotResult,
                        allRelatedAnchors,
                        batchRequest.AnchorTypeMap);
                    rootTrace = EvaluateNode(item.RootNode, snapshotResult.ExecutionContext);
                    var failurePath = BuildFailurePath(rootTrace);
                    var unsupportedCount = CountNodesByStatus(rootTrace, StatusUnsupported);
                    var unknownCount = CountNodesByStatus(rootTrace, StatusUnknown);
                    var diagQuality = ComputeDiagnosisQuality(rootTrace.Status, unsupportedCount, unknownCount, failurePath);

                    results.Add(new EvaluateBatchItemResult
                    {
                        VariableName = item.RootNode.VariableName ?? item.Target.VariableName,
                        LogicUnitId = item.AstRow.LogicUnitId ?? item.Target.LogicUnitId,
                        BlockName = item.AstRow.BlockName,
                        EvalResult = new EvaluatePersistedAstResultDto
                        {
                            ProjectId = batchRequest.ProjectId,
                            StationCode = snapshotResult.StationCode,
                            LogicUnitId = item.AstRow.LogicUnitId ?? 0,
                            BlockId = item.AstRow.BlockId,
                            BlockName = item.AstRow.BlockName,
                            VariableName = item.RootNode.VariableName,
                            CompileStatus = item.AstRow.CompileStatus,
                            DeviceName = snapshotResult.DeviceName,
                            FallbackDeviceName = snapshotResult.FallbackDeviceName,
                            InputVariables = item.InputVariables,
                            OutputVariables = item.OutputVariables,
                            RequestedSymbols = snapshotResult.RequestedSymbols ?? new List<string>(),
                            MissingSymbols = snapshotResult.MissingSymbols ?? new List<string>(),
                            EvaluatedValue = rootTrace.Result,
                            EvaluationStatus = rootTrace.Status,
                            DiagnosisQuality = diagQuality,
                            UnsupportedNodeCount = unsupportedCount,
                            UnknownNodeCount = unknownCount,
                            ExecutionContext = snapshotResult.ExecutionContext,
                            Snapshot = snapshotResult.Snapshot,
                            RootEvaluation = rootTrace,
                            FailurePath = failurePath,
                            Messages = BuildMessages(item.AstRow, rootTrace, snapshotResult.MissingSymbols),
                        }
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new EvaluateBatchItemResult
                    {
                        VariableName = item.Target.VariableName,
                        LogicUnitId = item.Target.LogicUnitId,
                        Error = ex.Message,
                    });
                }
            }

            return results;
        }

        private async Task SupplementContextForDrillDownTransitionsAsync(
            long projectId,
            string stationCode,
            StopCauseAstTraceNode rootTrace,
            AcquireDiagnosticSnapshotResultDto snapshotResult,
            IEnumerable<string> relatedAnchors,
            Dictionary<string, string> anchorTypeMap)
        {
            var missingSymbols = CollectMissingDrillDownTransitionSymbols(rootTrace, snapshotResult?.ExecutionContext);
            if (missingSymbols.Count == 0)
            {
                return;
            }

            var supplementary = await _signalSnapshotService.AcquireAsync(new AcquireDiagnosticSnapshotRequestDto
            {
                ProjectId = projectId,
                StationCode = stationCode,
                SymbolPaths = missingSymbols,
                RelatedAnchors = (relatedAnchors ?? Array.Empty<string>()).Where(it => !string.IsNullOrWhiteSpace(it)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                AnchorTypeMap = anchorTypeMap,
            });

            MergeExecutionContext(snapshotResult?.ExecutionContext, supplementary?.ExecutionContext);
            MergeList(snapshotResult?.RequestedSymbols, supplementary?.RequestedSymbols);
            MergeList(snapshotResult?.MissingSymbols, supplementary?.MissingSymbols);
            MergeList(snapshotResult?.Snapshot?.SourceSymbols, supplementary?.Snapshot?.SourceSymbols);
            MergeList(snapshotResult?.Snapshot?.MissingSymbols, supplementary?.Snapshot?.MissingSymbols);
        }

        internal static List<string> CollectMissingDrillDownTransitionSymbols(
            StopCauseAstTraceNode rootTrace,
            StopCauseExecutionContext context)
        {
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rootTrace == null || context?.GraphTransitionAstCache == null || context.GraphTransitionAstCache.Count == 0)
            {
                return symbols.ToList();
            }

            CollectMissingDrillDownTransitionSymbolsRecursive(rootTrace, context, symbols);
            return symbols.ToList();
        }

        private static void CollectMissingDrillDownTransitionSymbolsRecursive(
            StopCauseAstTraceNode trace,
            StopCauseExecutionContext context,
            HashSet<string> symbols)
        {
            if (trace == null)
            {
                return;
            }

            if (string.Equals(trace.NodeType, StopCauseAstNodeType.GraphTransition, StringComparison.OrdinalIgnoreCase)
                && TryParseTransitionNetworkId(trace.NodeId, out var transitionId)
                && context.GraphTransitionAstCache.TryGetValue(transitionId, out var transitionAst))
            {
                foreach (var symbol in CollectRuntimeSymbols(transitionAst)
                    .Select(NormalizeRuntimeSymbolPath)
                    .Where(IsRuntimeReadableSymbol))
                {
                    if (context.CurrentValues == null || !context.CurrentValues.ContainsKey(symbol))
                    {
                        symbols.Add(symbol);
                    }
                }
            }

            foreach (var child in trace.Children ?? new List<StopCauseAstTraceNode>())
            {
                CollectMissingDrillDownTransitionSymbolsRecursive(child, context, symbols);
            }
        }

        private static bool TryParseTransitionNetworkId(string nodeId, out int transitionId)
        {
            transitionId = 0;
            var text = (nodeId ?? string.Empty).Trim();
            if (!text.StartsWith("transition:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return int.TryParse(text.Substring("transition:".Length), out transitionId);
        }

        private static void MergeExecutionContext(StopCauseExecutionContext target, StopCauseExecutionContext source)
        {
            if (target == null || source == null)
            {
                return;
            }

            foreach (var kv in source.CurrentValues ?? new Dictionary<string, object>())
            {
                target.CurrentValues[kv.Key] = kv.Value;
            }

            foreach (var kv in source.SignalQuality ?? new Dictionary<string, string>())
            {
                target.SignalQuality[kv.Key] = kv.Value;
            }

            MergeList(target.SourceSymbols, source.SourceSymbols);
        }

        private static void MergeList(List<string> target, IEnumerable<string> source)
        {
            if (target == null || source == null)
            {
                return;
            }

            foreach (var item in source.Where(it => !string.IsNullOrWhiteSpace(it)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!target.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    target.Add(item);
                }
            }
        }

        #region Batch 辅助类型

        /// <summary>批量求值请求</summary>
        public class EvaluateBatchRequest
        {
            public long ProjectId { get; set; }
            public string StationCode { get; set; }
            /// <summary>待求值目标列表</summary>
            public List<EvaluateBatchTarget> Targets { get; set; } = new();
            /// <summary>已预加载的 AST 行（可选，避免重复查 DB）</summary>
            public List<PLC_LogicUnitAstEntity> PreloadedAstRows { get; set; }
            /// <summary>锚点类型映射（可选，透传给快照服务）</summary>
            public Dictionary<string, string> AnchorTypeMap { get; set; }
        }

        public class EvaluateBatchTarget
        {
            public long LogicUnitId { get; set; }
            public string VariableName { get; set; }
            public string DisplayLabel { get; set; }
            /// <summary>携带调用方自定义数据（如 ChangedAt 等）</summary>
            public object Tag { get; set; }
        }

        public class EvaluateBatchItemResult
        {
            public string VariableName { get; set; }
            public long LogicUnitId { get; set; }
            public string BlockName { get; set; }
            /// <summary>求值成功时的完整结果</summary>
            public EvaluatePersistedAstResultDto EvalResult { get; set; }
            /// <summary>求值失败时的错误信息</summary>
            public string Error { get; set; }
            public bool Success => EvalResult != null;
        }

        private class PreparedAstItem
        {
            public EvaluateBatchTarget Target { get; set; }
            public PLC_LogicUnitAstEntity AstRow { get; set; }
            public StopCauseAstNode RootNode { get; set; }
            public List<string> InputVariables { get; set; }
            public List<string> OutputVariables { get; set; }
            public string Error { get; set; }
        }

        #endregion

        private async Task<PLC_LogicUnitAstEntity> LoadAstRowAsync(EvaluatePersistedAstRequestDto request)
        {
            if (request.LogicUnitId.HasValue)
            {
                var targetLogicUnitId = request.LogicUnitId.Value;
                var rows = await _logicUnitAstService.GetListAsync(it =>
                    it.ProjectId == request.ProjectId &&
                    it.IsDeleted == false &&
                    it.LogicUnitId == targetLogicUnitId);

                return rows
                    .OrderByDescending(it => ScoreCompileStatus(it.CompileStatus))
                    .FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(request.VariableName))
            {
                throw new InvalidOperationException("运行时求值至少需要提供 LogicUnitId 或 VariableName 其中之一。");
            }

            var normalizedVariableName = NormalizeRuntimeSymbolPath(request.VariableName);
            // 将闭包变量预求值，避免 SqlSugar 把 .HasValue / IsNullOrWhiteSpace 翻译成非法 PostgreSQL NOT 语法
            var filterBlockId = request.BlockId;
            var filterStationCode = string.IsNullOrWhiteSpace(request.StationCode) ? null : request.StationCode;
            var candidates = await _logicUnitAstService.GetListAsync(it =>
                it.ProjectId == request.ProjectId &&
                it.IsDeleted == false &&
                (filterBlockId == null || it.BlockId == filterBlockId) &&
                (filterStationCode == null || it.StationCode == filterStationCode));

            return candidates
                .Select(it => new
                {
                    Row = it,
                    MatchScore = ScoreAstRowMatch(it, normalizedVariableName, request.StationCode, request.BlockId)
                })
                .Where(it => it.MatchScore > 0)
                .OrderByDescending(it => it.MatchScore)
                .ThenByDescending(it => ScoreCompileStatus(it.Row.CompileStatus))
                .Select(it => it.Row)
                .FirstOrDefault();
        }

        /// <summary>
        /// 批量预加载整个 Block 的所有 GRAPH Transition AST（一次 DB 查询代替 N 次）。
        /// 先从所有 rootNode 的 Metadata 收集 TransitionIds，若全部为空则批量查 logicunit 表补丁，
        /// 最后一次性加载所有 Transition AST 行。
        /// </summary>
        private async Task PreloadGraphTransitionAstCacheForBlockAsync(
            List<StopCauseAstNode> rootNodes,
            long blockId,
            long projectId,
            StopCauseExecutionContext context)
        {
            var allTransitionNetworkIds = new HashSet<int>();

            // Step 1: 从所有 rootNode 的 Metadata 收集已编译的 TransitionIds
            foreach (var rootNode in rootNodes)
                CollectOutgoingTransitionNetworkIds(rootNode, allTransitionNetworkIds);

            // Step 2: Metadata 为空时，批量查 logicunit 表补丁所有 rootNode
            if (allTransitionNetworkIds.Count == 0)
            {
                // 收集所有 rootNode 中的 STEP_ACTION 节点及其 LogicUnitId
                var allStepActionNodes = new List<(StopCauseAstNode Node, long LogicUnitId)>();
                foreach (var rootNode in rootNodes)
                    CollectStepActionNodesWithLogicUnitId(rootNode, allStepActionNodes);

                if (allStepActionNodes.Count > 0)
                {
                    var logicUnitIds = allStepActionNodes.Select(x => x.LogicUnitId).Distinct().ToList();
                    var blockIdCopy = blockId;

                    var logicUnits = await _logicUnitService.GetListAsync(it =>
                        it.BlockId == blockIdCopy &&
                        it.IsDeleted == false &&
                        logicUnitIds.Contains(it.Id));

                    var transMap = new Dictionary<long, string>();
                    foreach (var lu in logicUnits)
                    {
                        if (!string.IsNullOrWhiteSpace(lu.GraphOutgoingTransitionsJson))
                            transMap[lu.Id] = lu.GraphOutgoingTransitionsJson;
                    }

                    foreach (var (node, luId) in allStepActionNodes)
                    {
                        if (!transMap.TryGetValue(luId, out var json)) continue;
                        try
                        {
                            var ids = JsonSerializer.Deserialize<List<int>>(json);
                            if (ids == null || ids.Count == 0) continue;
                            node.Metadata ??= new Dictionary<string, string>();
                            node.Metadata["OutgoingTransitionNetworkIds"] = json;
                            foreach (var id in ids)
                                allTransitionNetworkIds.Add(id);
                        }
                        catch { }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[PreloadGraphTransition] blockId={blockId} 全量收集 TransitionIds=[{string.Join(",", allTransitionNetworkIds)}] (from {rootNodes.Count} rootNodes)");

            if (allTransitionNetworkIds.Count == 0) return;

            // Step 3: 一次性加载所有 Transition AST
            var networkIdList = allTransitionNetworkIds.Where(id => !context.GraphTransitionAstCache.ContainsKey(id)).ToList();
            if (networkIdList.Count == 0) return;

            var blockIdCopy2 = blockId;
            var projectIdCopy = projectId;
            var transitionRows = await _logicUnitAstService.GetListAsync(it =>
                it.ProjectId == projectIdCopy &&
                it.BlockId == blockIdCopy2 &&
                it.IsDeleted == false &&
                networkIdList.Contains(it.NetworkId.Value));

            foreach (var row in transitionRows)
            {
                if (!row.NetworkId.HasValue || string.IsNullOrWhiteSpace(row.RootNodeJson))
                    continue;
                if (string.IsNullOrWhiteSpace(row.UnitType) || !row.UnitType.Contains("TRANSITION", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var node = JsonSerializer.Deserialize<StopCauseAstNode>(row.RootNodeJson, JsonOptions);
                    if (node != null && !context.GraphTransitionAstCache.ContainsKey(row.NetworkId.Value))
                    {
                        context.GraphTransitionAstCache[row.NetworkId.Value] = node;
                        System.Diagnostics.Debug.WriteLine(
                            $"[PreloadGraphTransition] 缓存 Transition NetworkId={row.NetworkId.Value} NodeType={node.NodeType} ChildCount={node.Children?.Count ?? 0}");
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 预加载 GRAPH Transition AST 到执行上下文缓存，并补丁 AST 节点的 Metadata。
        /// 
        /// 策略：
        ///   1. 先从 AST 节点 Metadata 收集已编译的 OutgoingTransitionNetworkIds
        ///   2. 如果 Metadata 为空（编译时未带入），则回退查 logicunit 表的 GraphOutgoingTransitionsJson
        ///   3. 将从 DB 获得的 TransitionIds 补丁到 AST 节点 Metadata，使 EvaluateOutgoingTransitionsIfApplicable 能工作
        ///   4. 批量加载 Transition AST 行填充缓存
        /// </summary>
        private async Task PreloadGraphTransitionAstCacheAsync(
            StopCauseAstNode rootNode,
            long blockId,
            long projectId,
            StopCauseExecutionContext context)
        {
            // 收集所有出口 Transition NetworkId（来自 AST Metadata）
            var allTransitionNetworkIds = new HashSet<int>();
            CollectOutgoingTransitionNetworkIds(rootNode, allTransitionNetworkIds);

            // Metadata 为空时，回退查 logicunit 表获取 GraphOutgoingTransitionsJson
            if (allTransitionNetworkIds.Count == 0)
            {
                await EnrichOutgoingTransitionIdsFromDbAsync(rootNode, blockId, projectId, allTransitionNetworkIds);
            }

            if (allTransitionNetworkIds.Count == 0)
                return;

            var networkIdList = allTransitionNetworkIds.ToList();
            var blockIdCopy = blockId;
            var projectIdCopy = projectId;

            // 批量加载 Transition AST 行（同一 Block 下，UnitType 包含 TRANSITION）
            var transitionRows = await _logicUnitAstService.GetListAsync(it =>
                it.ProjectId == projectIdCopy &&
                it.BlockId == blockIdCopy && 
                it.IsDeleted == false &&
                networkIdList.Contains(it.NetworkId.Value));

            foreach (var row in transitionRows)
            {
                if (!row.NetworkId.HasValue || string.IsNullOrWhiteSpace(row.RootNodeJson))
                    continue;
                if (string.IsNullOrWhiteSpace(row.UnitType) || !row.UnitType.Contains("TRANSITION", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var node = JsonSerializer.Deserialize<StopCauseAstNode>(row.RootNodeJson, JsonOptions);
                    if (node != null && !context.GraphTransitionAstCache.ContainsKey(row.NetworkId.Value))
                    {
                        context.GraphTransitionAstCache[row.NetworkId.Value] = node;
                    }
                }
                catch { /* 单条反序列化失败不影响其他Transition */ }
            }
        }

        /// <summary>
        /// 从 logicunit 表补取 GraphOutgoingTransitionsJson，补丁到 AST 节点 Metadata。
        /// 解决编译时 GraphOutgoingTransitionsJson 尚未填充导致 Metadata 为空的问题。
        /// </summary>
        private async Task EnrichOutgoingTransitionIdsFromDbAsync(
            StopCauseAstNode rootNode,
            long blockId,
            long projectId,
            HashSet<int> outTransitionIds)
        {
            // 收集 AST 中所有 STEP_*_ACTION 子节点及其 LogicUnitId
            var stepActionNodes = new List<(StopCauseAstNode Node, long LogicUnitId)>();
            CollectStepActionNodesWithLogicUnitId(rootNode, stepActionNodes);

            if (stepActionNodes.Count == 0) return;

            var logicUnitIds = stepActionNodes.Select(x => x.LogicUnitId).Distinct().ToList();
            var blockIdCopy = blockId;

            // 批量查 logicunit 表（无 ProjectId 字段，用 BlockId + Id 过滤）
            var logicUnits = await _logicUnitService.GetListAsync(it =>
                it.BlockId == blockIdCopy &&
                it.IsDeleted == false &&
                logicUnitIds.Contains(it.Id));

            // 建立 LogicUnitId → GraphOutgoingTransitionsJson 映射
            var transMap = new Dictionary<long, string>();
            foreach (var lu in logicUnits)
            {
                if (!string.IsNullOrWhiteSpace(lu.GraphOutgoingTransitionsJson))
                    transMap[lu.Id] = lu.GraphOutgoingTransitionsJson;
            }

            // 补丁 AST 节点 Metadata
            foreach (var (node, luId) in stepActionNodes)
            {
                if (!transMap.TryGetValue(luId, out var json)) continue;

                try
                {
                    var ids = JsonSerializer.Deserialize<List<int>>(json);
                    if (ids == null || ids.Count == 0) continue;

                    // 补丁 Metadata
                    node.Metadata ??= new Dictionary<string, string>();
                    node.Metadata["OutgoingTransitionNetworkIds"] = json;

                    foreach (var id in ids)
                        outTransitionIds.Add(id);
                }
                catch { }
            }
        }

        /// <summary>
        /// 从 AST 树中收集 UnitType 包含 STEP 和 ACTION 的节点及其 LogicUnitId。
        /// LogicUnitId 从 NodeId（格式 "logic-unit:{id}:action"）中解析。
        /// </summary>
        private static void CollectStepActionNodesWithLogicUnitId(
            StopCauseAstNode node,
            List<(StopCauseAstNode Node, long LogicUnitId)> result)
        {
            if (node == null) return;

            if (node.Metadata != null
                && node.Metadata.TryGetValue("UnitType", out var unitType)
                && !string.IsNullOrWhiteSpace(unitType)
                && unitType.Contains("STEP", StringComparison.OrdinalIgnoreCase)
                && unitType.Contains("ACTION", StringComparison.OrdinalIgnoreCase))
            {
                // 从 NodeId "logic-unit:12345:action" 解析 LogicUnitId
                var nodeId = node.NodeId;
                if (!string.IsNullOrWhiteSpace(nodeId) && nodeId.StartsWith("logic-unit:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = nodeId.Split(':');
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var luId))
                    {
                        result.Add((node, luId));
                    }
                }
            }

            if (node.Children != null)
                foreach (var child in node.Children)
                    CollectStepActionNodesWithLogicUnitId(child, result);
        }

        private static void CollectOutgoingTransitionNetworkIds(StopCauseAstNode node, HashSet<int> result)
        {
            if (node == null) return;

            if (node.Metadata != null &&
                node.Metadata.TryGetValue("OutgoingTransitionNetworkIds", out var meta) &&
                !string.IsNullOrWhiteSpace(meta))
            {
                try
                {
                    var ids = JsonSerializer.Deserialize<List<int>>(meta);
                    if (ids != null)
                        foreach (var id in ids) result.Add(id);
                }
                catch { }
            }

            if (node.Children != null)
                foreach (var child in node.Children)
                    CollectOutgoingTransitionNetworkIds(child, result);
        }

        internal static StopCauseAstNode FocusRootForTargetVariable(StopCauseAstNode root, string targetVariable)
        {
            if (root == null || string.IsNullOrWhiteSpace(targetVariable))
            {
                return root;
            }

            var aggregate = FindParallelOutputAggregate(root);
            if (aggregate == null)
            {
                return root;
            }

            var targetNodes = FindTargetOutputNodes(aggregate, targetVariable);
            if (targetNodes.Count == 0)
            {
                return root;
            }

            var focusedChildren = targetNodes
                .Select(node => CloneAstNode(node))
                .Where(node => node != null)
                .ToList();

            if (string.Equals(root.NodeType, StopCauseAstNodeType.Anchor, StringComparison.OrdinalIgnoreCase))
            {
                var focusedRoot = CloneAstNode(root, focusedChildren);
                focusedRoot.VariableName = targetVariable.Trim();
                focusedRoot.Metadata ??= new Dictionary<string, string>();
                focusedRoot.Metadata["FocusedTargetVariable"] = targetVariable.Trim();
                focusedRoot.Metadata["FocusSource"] = "ParallelOutputAggregate";
                return focusedRoot;
            }

            if (focusedChildren.Count == 1)
            {
                return focusedChildren[0];
            }

            var focusedAggregate = CloneAstNode(aggregate, focusedChildren);
            focusedAggregate.Metadata ??= new Dictionary<string, string>();
            focusedAggregate.Metadata["FocusedTargetVariable"] = targetVariable.Trim();
            focusedAggregate.Metadata["FocusSource"] = "ParallelOutputAggregate";
            return focusedAggregate;
        }

        private static StopCauseAstNode FindParallelOutputAggregate(StopCauseAstNode root)
        {
            if (IsParallelOutputAggregate(root))
            {
                return root;
            }

            return root.Children?
                .FirstOrDefault(IsParallelOutputAggregate);
        }

        private static bool IsParallelOutputAggregate(StopCauseAstNode node)
        {
            if (node == null
                || !string.Equals(node.NodeType, StopCauseAstNodeType.And, StringComparison.OrdinalIgnoreCase)
                || (node.Children?.Count ?? 0) <= 1)
            {
                return false;
            }

            var sourceType = GetMetadataValue(node.Metadata, "SourceType");
            var isLadLike = string.Equals(sourceType, "LAD", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(sourceType, "FBD", StringComparison.OrdinalIgnoreCase)
                            || (node.NodeId?.IndexOf(":lad-root", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                            || string.Equals(GetMetadataValue(node.Metadata, "Semantics"), "ParallelOutputs", StringComparison.OrdinalIgnoreCase);

            return isLadLike && node.Children.Any(IsTargetOutputNode);
        }

        private static List<StopCauseAstNode> FindTargetOutputNodes(StopCauseAstNode aggregate, string targetVariable)
        {
            return (aggregate.Children ?? new List<StopCauseAstNode>())
                .Where(IsTargetOutputNode)
                .Where(child => OutputNodeMatchesTarget(child, targetVariable))
                .ToList();
        }

        private static bool IsTargetOutputNode(StopCauseAstNode node)
        {
            if (node == null)
            {
                return false;
            }

            return string.Equals(node.NodeType, StopCauseAstNodeType.Coil, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(node.NodeType, StopCauseAstNodeType.Set, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(node.NodeType, StopCauseAstNodeType.Reset, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(node.NodeType, StopCauseAstNodeType.Move, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(node.NodeType, StopCauseAstNodeType.SetResetFlipFlop, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(node.NodeType, StopCauseAstNodeType.ExternalDependency, StringComparison.OrdinalIgnoreCase);
        }

        private static bool OutputNodeMatchesTarget(StopCauseAstNode node, string targetVariable)
        {
            var names = new[]
            {
                node.VariableName,
                GetMetadataValue(node.Metadata, "Target"),
                GetMetadataValue(node.Metadata, "Destination"),
                GetMetadataValue(node.Metadata, "Q"),
                GetMetadataValue(node.Metadata, "OutputParam")
            };

            return names.Any(name => SymbolMatchesTarget(name, targetVariable));
        }

        private static bool SymbolMatchesTarget(string candidate, string target)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            var candidateNorm = NormalizeForTargetMatch(candidate);
            var targetNorm = NormalizeForTargetMatch(target);
            if (string.Equals(candidateNorm, targetNorm, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var candidateTail = LastSymbolSegment(candidateNorm);
            var targetTail = LastSymbolSegment(targetNorm);
            if (string.IsNullOrWhiteSpace(candidateTail) || string.IsNullOrWhiteSpace(targetTail))
            {
                return false;
            }

            if (string.Equals(candidateTail, targetTail, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return candidateTail.Length >= 3
                   && targetTail.EndsWith("_" + candidateTail, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeForTargetMatch(string value)
        {
            return (value ?? string.Empty).Trim().Replace("\"", string.Empty);
        }

        private static string LastSymbolSegment(string value)
        {
            var normalized = NormalizeForTargetMatch(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var dotIndex = normalized.LastIndexOf('.');
            return dotIndex >= 0 && dotIndex + 1 < normalized.Length
                ? normalized.Substring(dotIndex + 1)
                : normalized;
        }

        private static StopCauseAstNode CloneAstNode(StopCauseAstNode node, List<StopCauseAstNode> children = null)
        {
            if (node == null)
            {
                return null;
            }

            return new StopCauseAstNode
            {
                NodeId = node.NodeId,
                NodeType = node.NodeType,
                VariableName = node.VariableName,
                DisplayName = node.DisplayName,
                Operator = node.Operator,
                ExpectedValue = node.ExpectedValue,
                Metadata = node.Metadata == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(node.Metadata, StringComparer.OrdinalIgnoreCase),
                Children = children ?? (node.Children ?? new List<StopCauseAstNode>())
                    .Select(child => CloneAstNode(child))
                    .Where(child => child != null)
                    .ToList()
            };
        }

        internal StopCauseAstTraceNode EvaluateNode(StopCauseAstNode node, StopCauseExecutionContext context)
        {
            if (node == null)
            {
                return new StopCauseAstTraceNode
                {
                    Status = StatusUnknown,
                    Message = "节点为空，无法继续求值。"
                };
            }

            var trace = CreateTraceShell(node);
            StopCauseAstTraceNode result;
            switch (node.NodeType)
            {
                case StopCauseAstNodeType.Anchor:
                    result = EvaluateAnchor(node, context, trace);
                    break;
                case StopCauseAstNodeType.And:
                    result = EvaluateAnd(node, context, trace);
                    break;
                case StopCauseAstNodeType.Or:
                    result = EvaluateOr(node, context, trace);
                    break;
                case StopCauseAstNodeType.Not:
                    result = EvaluateNot(node, context, trace);
                    break;
                case StopCauseAstNodeType.Contact:
                    result = EvaluateContact(node, context, trace);
                    break;
                case StopCauseAstNodeType.Compare:
                    result = EvaluateCompare(node, context, trace);
                    break;
                case StopCauseAstNodeType.Coil:
                case StopCauseAstNodeType.Set:
                case StopCauseAstNodeType.Reset:
                    result = EvaluateOutputNode(node, context, trace);
                    break;
                case StopCauseAstNodeType.GraphStepActive:
                    result = EvaluateGraphStepActive(node, context, trace);
                    break;
                case StopCauseAstNodeType.GraphTransition:
                    result = EvaluateGraphTransition(node, context, trace);
                    break;
                case StopCauseAstNodeType.Reference:
                    result = EvaluateReference(node, context, trace);
                    break;
                case StopCauseAstNodeType.ExternalDependency:
                    result = EvaluateExternalDependency(node, context, trace);
                    break;
                case StopCauseAstNodeType.Timer:
                    result = EvaluateTimer(node, context, trace);
                    break;
                case StopCauseAstNodeType.Move:
                    result = EvaluateMove(node, context, trace);
                    break;
                case StopCauseAstNodeType.SetResetFlipFlop:
                    result = EvaluateSetResetFlipFlop(node, context, trace);
                    break;
                case StopCauseAstNodeType.Opaque:
                    trace.Status = StatusUnsupported;
                    trace.Message = $"Opaque 节点（{node.DisplayName ?? "未知结构"}）无法进行运行时求值。";
                    result = trace;
                    break;
                default:
                    trace.Status = StatusUnsupported;
                    trace.Message = $"节点类型 {node.NodeType} 暂未进入运行时求值范围。";
                    result = trace;
                    break;
            }

            // 统一处理 GRAPH 出口转移条件：所有节点类型求值完毕后，
            // 如果 Metadata 中包含 OutgoingTransitionNetworkIds，都尝试评估。
            // 统一在此处调用，避免各子方法早期 return 跳过的死角。
            EvaluateOutgoingTransitionsIfApplicable(node, context, result);

            return result;
        }

        private StopCauseAstTraceNode EvaluateAnchor(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            trace.ActualValue = ResolveTokenValue(node.VariableName, context);
            trace.Children = EvaluateChildren(node, context);
            if (trace.Children.Count == 0)
            {
                var outputState = CoerceBoolean(trace.ActualValue);
                if (!outputState.HasValue)
                {
                    trace.Status = StatusUnknown;
                    trace.Message = "锚点没有可执行子树，且当前值无法转换为布尔态。";
                    return trace;
                }

                trace.Result = outputState.Value;
                trace.Status = outputState.Value ? StatusTrue : StatusFalse;
                trace.Message = "锚点直接使用当前在线值求值。";
                return trace;
            }

            if (trace.Children.Any(it => string.Equals(it.Status, StatusTrue, StringComparison.OrdinalIgnoreCase)))
            {
                trace.Result = true;
                trace.Status = StatusTrue;
            }
            else if (trace.Children.All(it => string.Equals(it.Status, StatusFalse, StringComparison.OrdinalIgnoreCase)))
            {
                trace.Result = false;
                trace.Status = StatusFalse;
            }
            else if (trace.Children.Any(it => string.Equals(it.Status, StatusUnknown, StringComparison.OrdinalIgnoreCase)))
            {
                trace.Status = StatusUnknown;
            }
            else
            {
                trace.Status = StatusUnsupported;
            }

            var actualOutput = CoerceBoolean(trace.ActualValue);
            if (actualOutput.HasValue && trace.Result.HasValue && actualOutput.Value != trace.Result.Value)
            {
                if (trace.Result.Value && !actualOutput.Value)
                {
                    // AST 条件全部满足（True），但锚点实际输出为 False —— 典型的瞬态信号场景：
                    // 某个信号曾短暂变化导致停机，但在诊断时已恢复正常，AST 无法捕捉。
                    trace.Status = StatusFalse;
                    trace.Result = false;
                    trace.Message = $"疑似瞬态停机：AST 所有条件当前均满足(True)，但锚点 {node.VariableName} 实际输出为 False。" +
                        "可能存在已恢复的瞬态信号（如光电开关误遮挡、气压波动等）导致停机后自动复位，建议查看 PLC 信号历史记录。";
                }
                else
                {
                    trace.Message = $"AST 条件结果为 {trace.Result.Value}，但锚点 {node.VariableName} 当前在线值为 {actualOutput.Value}，可能存在写入竞争或扫描周期延迟。";
                }
            }
            else if (trace.Result.HasValue)
            {
                trace.Message = trace.Result.Value
                    ? "至少存在一条写入链路满足当前锚点条件。"
                    : "当前没有任何已编译写入链路满足锚点条件。";
            }
            else
            {
                trace.Message = "锚点包含未覆盖分支，当前结果无法完全判定。";
            }

            return trace;
        }

        private StopCauseAstTraceNode EvaluateAnd(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            trace.Children = EvaluateChildren(node, context);
            if (trace.Children.Count == 0)
            {
                trace.Status = StatusUnknown;
                trace.Message = "And 节点没有子节点。";
                return trace;
            }

            if (trace.Children.Any(it => string.Equals(it.Status, StatusFalse, StringComparison.OrdinalIgnoreCase)))
            {
                trace.Result = false;
                trace.Status = StatusFalse;
                trace.Message = "And 节点存在为 False 的前置条件。";
                return trace;
            }

            if (trace.Children.All(it => string.Equals(it.Status, StatusTrue, StringComparison.OrdinalIgnoreCase)))
            {
                trace.Result = true;
                trace.Status = StatusTrue;
                trace.Message = "And 节点全部条件满足。";
                return trace;
            }

            trace.Status = trace.Children.Any(it => string.Equals(it.Status, StatusUnknown, StringComparison.OrdinalIgnoreCase))
                ? StatusUnknown
                : StatusUnsupported;
            trace.Message = "And 节点包含未解析或未覆盖条件。";
            return trace;
        }

        private StopCauseAstTraceNode EvaluateOr(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            trace.Children = EvaluateChildren(node, context);
            if (trace.Children.Count == 0)
            {
                trace.Status = StatusUnknown;
                trace.Message = "Or 节点没有子节点。";
                return trace;
            }

            if (trace.Children.Any(it => string.Equals(it.Status, StatusTrue, StringComparison.OrdinalIgnoreCase)))
            {
                trace.Result = true;
                trace.Status = StatusTrue;
                trace.Message = "Or 节点至少存在一条满足分支。";
                return trace;
            }

            if (trace.Children.All(it => string.Equals(it.Status, StatusFalse, StringComparison.OrdinalIgnoreCase)))
            {
                trace.Result = false;
                trace.Status = StatusFalse;
                trace.Message = "Or 节点所有分支均未满足。";
                return trace;
            }

            trace.Status = trace.Children.Any(it => string.Equals(it.Status, StatusUnknown, StringComparison.OrdinalIgnoreCase))
                ? StatusUnknown
                : StatusUnsupported;
            trace.Message = "Or 节点存在未覆盖分支，当前无法证明其整体为 False。";
            return trace;
        }

        private StopCauseAstTraceNode EvaluateNot(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            trace.Children = EvaluateChildren(node, context);
            var child = trace.Children.FirstOrDefault();
            if (child == null)
            {
                trace.Status = StatusUnknown;
                trace.Message = "Not 节点缺少子节点。";
                return trace;
            }

            if (!child.Result.HasValue)
            {
                trace.Status = child.Status;
                trace.Message = "Not 节点的子节点结果未决。";
                return trace;
            }

            trace.Result = !child.Result.Value;
            trace.Status = trace.Result.Value ? StatusTrue : StatusFalse;
            trace.Message = $"Not 节点对 {child.Result.Value} 取反得到 {trace.Result.Value}。";
            return trace;
        }

        private StopCauseAstTraceNode EvaluateContact(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            trace.ActualValue = ResolveTokenValue(node.VariableName, context);
            var actual = CoerceBoolean(trace.ActualValue);
            if (!actual.HasValue)
            {
                trace.Status = StatusUnknown;
                trace.Message = BuildSignalMessage(node.VariableName, context, "当前信号缺失或无法转换为布尔值。");
                return trace;
            }

            trace.Result = actual.Value;
            trace.Status = actual.Value ? StatusTrue : StatusFalse;
            trace.Message = BuildSignalMessage(node.VariableName, context, $"当前值为 {actual.Value}。");
            return trace;
        }

        private StopCauseAstTraceNode EvaluateCompare(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            var leftToken = !string.IsNullOrWhiteSpace(node.VariableName)
                ? node.VariableName
                : GetMetadataValue(node.Metadata, "Left");
            var rightToken = !string.IsNullOrWhiteSpace(GetMetadataValue(node.Metadata, "Right"))
                ? GetMetadataValue(node.Metadata, "Right")
                : node.ExpectedValue;

            var left = ResolveTokenValue(leftToken, context);
            // 比较节点的右值通常是常量（如 1, 6, 100）。
            // 通用 ResolveTokenValue 会将 "1"/"0" 解释为 bool true/false（适合 Contact），
            // 但在比较上下文中需要保持为数值。因此先尝试数值直解。
            var right = ResolveCompareOperand(rightToken, context);
            trace.ActualValue = left;
            trace.ExpectedValue = rightToken;

            var result = EvaluateComparison(left, right, node.Operator);
            if (!result.HasValue)
            {
                trace.Status = StatusUnknown;
                trace.Message = $"比较失败：{FormatValue(left)} {node.Operator} {FormatValue(right)}。";
                return trace;
            }

            trace.Result = result.Value;
            trace.Status = result.Value ? StatusTrue : StatusFalse;
            trace.Message = $"比较结果：{FormatValue(left)} {node.Operator} {FormatValue(right)} => {result.Value}。";
            return trace;
        }

        private StopCauseAstTraceNode EvaluateOutputNode(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            trace.ActualValue = ResolveTokenValue(node.VariableName, context);
            trace.Children = EvaluateChildren(node, context);
            var condition = trace.Children.FirstOrDefault();
            if (condition == null)
            {
                var actualOutput = CoerceBoolean(trace.ActualValue);
                if (!actualOutput.HasValue)
                {
                    trace.Status = StatusUnknown;
                    trace.Message = "输出节点没有前置条件，且当前输出值无法判定。";
                    return trace;
                }

                trace.Result = actualOutput.Value;
                trace.Status = actualOutput.Value ? StatusTrue : StatusFalse;
                trace.Message = $"输出节点直接使用当前值 {actualOutput.Value}。";
                return trace;
            }

            if (string.Equals(node.NodeType, StopCauseAstNodeType.Reset, StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateResetOutputNode(node, trace, condition);
            }

            trace.Result = condition.Result;
            trace.Status = condition.Status;
            var actual = CoerceBoolean(trace.ActualValue);
            if (actual.HasValue && condition.Result.HasValue && actual.Value != condition.Result.Value)
            {
                trace.Message = $"条件结果为 {condition.Result.Value}，但目标 {node.VariableName} 当前在线值为 {actual.Value}。";
            }
            else
            {
                trace.Message = $"输出节点继承前置条件结果：{condition.Status}。";
            }

            return trace;
        }

        private static StopCauseAstTraceNode EvaluateResetOutputNode(
            StopCauseAstNode node,
            StopCauseAstTraceNode trace,
            StopCauseAstTraceNode condition)
        {
            if (condition.Result == true)
            {
                trace.Result = false;
                trace.Status = StatusFalse;
                var actual = CoerceBoolean(trace.ActualValue);
                trace.Message = actual == true
                    ? $"复位条件已满足，但目标 {node.VariableName} 当前在线值仍为 True，可能存在后续写入竞争或采样时序差异。"
                    : "复位条件已满足，目标被复位为 False。";
                return trace;
            }

            if (condition.Result == false)
            {
                var actual = CoerceBoolean(trace.ActualValue);
                if (actual.HasValue)
                {
                    trace.Result = actual.Value;
                    trace.Status = actual.Value ? StatusTrue : StatusFalse;
                    trace.Message = $"复位条件未触发，目标 {node.VariableName} 沿用当前在线值 {actual.Value}。";
                    return trace;
                }

                trace.Status = StatusUnknown;
                trace.Message = "复位条件未触发，且目标当前在线值未知，无法判断保持状态。";
                return trace;
            }

            trace.Result = null;
            trace.Status = condition.Status;
            trace.Message = "复位条件结果未知，无法判断目标复位状态。";
            return trace;
        }

        /// <summary>
        /// 当 StepAction 的出口 Transition 缓存中存在数据时，逐一评估转移条件并附加到 trace.OutgoingTransitions。
        /// 仅在步骤当前处于激活状态（trace.Result == true）时执行，以还原"步骤卡住"场景。
        /// </summary>
        private void EvaluateOutgoingTransitionsIfApplicable(
            StopCauseAstNode node,
            StopCauseExecutionContext context,
            StopCauseAstTraceNode trace)
        {
            if (node.Metadata == null) return;
            if (!node.Metadata.TryGetValue("OutgoingTransitionNetworkIds", out var meta) || string.IsNullOrWhiteSpace(meta))
                return;

            List<int> transitionIds;
            try { transitionIds = JsonSerializer.Deserialize<List<int>>(meta); }
            catch { return; }

            if (transitionIds == null || transitionIds.Count == 0) return;

            // 只有步骤当前明确激活（true）时才评估出口转移，null/false 均跳过
            var stepActive = trace.Result ?? CoerceBoolean(trace.ActualValue);
            if (stepActive != true) return;

            System.Diagnostics.Debug.WriteLine(
                $"[EvalOutgoingTransitions] Variable={node.VariableName} TransIds=[{string.Join(",", transitionIds)}] StepActive=True");

            var transitionTraces = new List<StopCauseAstTraceNode>();
            foreach (var tid in transitionIds)
            {
                if (!context.GraphTransitionAstCache.TryGetValue(tid, out var transNode))
                {
                    // 未命中缓存（可能该 Transition 尚未编译 AST）
                    transitionTraces.Add(new StopCauseAstTraceNode
                    {
                        NodeId = $"transition:{tid}",
                        NodeType = StopCauseAstNodeType.GraphTransition,
                        DisplayName = $"Transition {tid}",
                        Status = StatusUnknown,
                        Message = $"Transition {tid} 尚未编译 AST，无法运行时求值。"
                    });
                    continue;
                }

                var tTrace = EvaluateNode(transNode, context);
                // 强制标记为 GraphTransition + transition:{tid} NodeId，
                // 使下游 ExtractTransitionFailureFindings 和 CollectMissingDrillDownTransitionSymbols 都能识别
                tTrace.NodeType = StopCauseAstNodeType.GraphTransition;
                tTrace.NodeId = $"transition:{tid}";
                if (string.IsNullOrWhiteSpace(tTrace.DisplayName))
                    tTrace.DisplayName = $"Transition {tid}";

                // 诊断：递归打印 Transition 内部完整求值结果树
                System.Diagnostics.Debug.WriteLine(
                    $"[EvalOutgoingTransitions] Transition {tid} 评估结果: Status={tTrace.Status} Result={tTrace.Result} Children={tTrace.Children?.Count ?? 0}");
                PrintTraceTree(tTrace.Children, "  ");

                transitionTraces.Add(tTrace);
            }

            if (transitionTraces.Count == 0) return;

            System.Diagnostics.Debug.WriteLine(
                $"[EvalOutgoingTransitions] 追加 {transitionTraces.Count} 个 Transition traces 到 {node.VariableName}, 当前 Children.Count={trace.Children?.Count ?? -1}");

            // 将出口 Transition 评估结果追加到 Children 后（不影响原始逻辑结果，仅作诊断信息）
            trace.Children ??= new List<StopCauseAstTraceNode>();
            trace.Children.AddRange(transitionTraces);

            // 若全部 Transition 条件均为 False → 步骤被卡，提示下探路径
            var allFalse = transitionTraces.All(t => string.Equals(t.Status, StatusFalse, StringComparison.OrdinalIgnoreCase));
            var anyFalse = transitionTraces.Any(t => string.Equals(t.Status, StatusFalse, StringComparison.OrdinalIgnoreCase));
            if (allFalse)
            {
                trace.Message += $" 步骤激活但全部出口转移条件（共 {transitionTraces.Count} 个）均未满足，步骤被阻塞。";
            }
            else if (anyFalse)
            {
                var falseCnt = transitionTraces.Count(t => string.Equals(t.Status, StatusFalse, StringComparison.OrdinalIgnoreCase));
                trace.Message += $" 步骤激活，{falseCnt}/{transitionTraces.Count} 个出口转移条件未满足。";
            }
        }

        /// <summary>
        /// 递归打印 trace 子树（诊断用）。最多递归 6 层。
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void PrintTraceTree(List<StopCauseAstTraceNode> children, string indent, int maxDepth = 6)
        {
            if (children == null || maxDepth <= 0) return;
            foreach (var c in children)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TransitionTree]{indent}{c.NodeType} Var={c.VariableName} Status={c.Status} Result={c.Result} Val={c.ActualValue}");
                PrintTraceTree(c.Children, indent + "  ", maxDepth - 1);
            }
        }

        /// <summary>
        /// GraphStepActive — 步骤激活状态，等同于 Contact 求值（读取在线信号布尔值）。
        /// </summary>
        private StopCauseAstTraceNode EvaluateGraphStepActive(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            trace.ActualValue = ResolveTokenValue(node.VariableName, context);
            var actual = CoerceBoolean(trace.ActualValue);
            if (!actual.HasValue)
            {
                trace.Status = StatusUnknown;
                trace.Message = BuildSignalMessage(node.VariableName, context, "GRAPH 步骤激活状态信号缺失或无法转换为布尔值。");
                return trace;
            }

            trace.Result = actual.Value;
            trace.Status = actual.Value ? StatusTrue : StatusFalse;
            trace.Message = BuildSignalMessage(node.VariableName, context, $"GRAPH 步骤激活状态当前值为 {actual.Value}。");
            return trace;
        }

        /// <summary>
        /// GraphTransition — 先尝试求值子节点，若无子节点则回退到信号读取。
        /// 编译器可能把转换条件表达式存为子节点（已解析）或 Metadata.ExpressionText（未解析）。
        /// </summary>
        private StopCauseAstTraceNode EvaluateGraphTransition(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            trace.Children = EvaluateChildren(node, context);
            if (trace.Children.Count > 0)
            {
                // 有可执行子树：沿用 And 语义（转换条件一般是多条件 AND）
                if (trace.Children.Any(it => string.Equals(it.Status, StatusFalse, StringComparison.OrdinalIgnoreCase)))
                {
                    trace.Result = false;
                    trace.Status = StatusFalse;
                    trace.Message = "GraphTransition 条件存在未满足的前置项。";
                    return trace;
                }

                if (trace.Children.All(it => string.Equals(it.Status, StatusTrue, StringComparison.OrdinalIgnoreCase)))
                {
                    trace.Result = true;
                    trace.Status = StatusTrue;
                    trace.Message = "GraphTransition 条件全部满足。";
                    return trace;
                }

                trace.Status = trace.Children.Any(it => string.Equals(it.Status, StatusUnknown, StringComparison.OrdinalIgnoreCase))
                    ? StatusUnknown
                    : StatusUnsupported;
                trace.Message = "GraphTransition 条件中存在未解析或未覆盖节点。";
                return trace;
            }

            // 无子节点 — 尝试通过变量名读取信号值
            if (!string.IsNullOrWhiteSpace(node.VariableName))
            {
                trace.ActualValue = ResolveTokenValue(node.VariableName, context);
                var actual = CoerceBoolean(trace.ActualValue);
                if (actual.HasValue)
                {
                    trace.Result = actual.Value;
                    trace.Status = actual.Value ? StatusTrue : StatusFalse;
                    trace.Message = $"GraphTransition 通过信号 {node.VariableName} 直接求值为 {actual.Value}。";
                    return trace;
                }
            }

            // 既无子节点也无信号 — 标记为 Unsupported 并提示表达式文本
            var exprText = GetMetadataValue(node.Metadata, "ExpressionText");
            trace.Status = StatusUnsupported;
            trace.Message = string.IsNullOrWhiteSpace(exprText)
                ? "GraphTransition 缺少可执行子树和信号映射，无法运行时求值。"
                : $"GraphTransition 表达式 [{exprText}] 尚未编译为可执行子树。";
            return trace;
        }

        /// <summary>
        /// Reference — 编译器识别但未能编译为可执行节点的结构。
        /// 如果有子节点，尝试求值子树；否则回退到信号读取；最终标记为 Unsupported。
        /// </summary>
        private StopCauseAstTraceNode EvaluateReference(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            trace.Children = EvaluateChildren(node, context);
            if (trace.Children.Count > 0)
            {
                // 有子节点时使用 And 语义合并
                if (trace.Children.Any(it => string.Equals(it.Status, StatusFalse, StringComparison.OrdinalIgnoreCase)))
                {
                    trace.Result = false;
                    trace.Status = StatusFalse;
                    trace.Message = $"Reference 节点 {node.VariableName ?? "(匿名)"} 子树中存在 False 条件。";
                    return trace;
                }

                if (trace.Children.All(it => string.Equals(it.Status, StatusTrue, StringComparison.OrdinalIgnoreCase)))
                {
                    trace.Result = true;
                    trace.Status = StatusTrue;
                    trace.Message = $"Reference 节点 {node.VariableName ?? "(匿名)"} 子树全部满足。";
                    return trace;
                }

                trace.Status = trace.Children.Any(it => string.Equals(it.Status, StatusUnknown, StringComparison.OrdinalIgnoreCase))
                    ? StatusUnknown
                    : StatusUnsupported;
                trace.Message = $"Reference 节点 {node.VariableName ?? "(匿名)"} 子树包含未覆盖分支。";
                return trace;
            }

            // 无子节点 — 尝试读取在线值做最佳努力判定
            if (!string.IsNullOrWhiteSpace(node.VariableName))
            {
                trace.ActualValue = ResolveTokenValue(node.VariableName, context);
                var actual = CoerceBoolean(trace.ActualValue);
                if (actual.HasValue)
                {
                    trace.Result = actual.Value;
                    trace.Status = actual.Value ? StatusTrue : StatusFalse;
                    trace.Message = $"Reference 节点 {node.VariableName} 通过信号直接求值为 {actual.Value}（无条件树，仅读取在线值）。";
                    return trace;
                }
            }

            var reason = GetMetadataValue(node.Metadata, "UnsupportedReason");
            trace.Status = StatusUnsupported;
            trace.Message = string.IsNullOrWhiteSpace(reason)
                ? $"Reference 节点 {node.VariableName ?? "(匿名)"} 无可执行子树且信号缺失。"
                : $"Reference 节点 {node.VariableName ?? "(匿名)"}：{reason}";
            return trace;
        }

        /// <summary>
        /// ExternalDependency — SCL 函数调用。
        /// 如果输出变量有在线信号则用信号值做最佳努力判定，否则标记 Unsupported。
        /// </summary>
        private StopCauseAstTraceNode EvaluateExternalDependency(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            var callee = GetMetadataValue(node.Metadata, "Callee");

            // 先尝试通过输出变量读取在线值
            if (!string.IsNullOrWhiteSpace(node.VariableName))
            {
                trace.ActualValue = ResolveTokenValue(node.VariableName, context);
                var actual = CoerceBoolean(trace.ActualValue);
                if (actual.HasValue)
                {
                    trace.Result = actual.Value;
                    trace.Status = actual.Value ? StatusTrue : StatusFalse;
                    trace.Message = $"ExternalDependency（{callee}）输出 {node.VariableName} 当前在线值为 {actual.Value}（函数逻辑未展开，仅读取输出信号）。";
                    return trace;
                }
            }

            trace.Status = StatusUnsupported;
            trace.Message = string.IsNullOrWhiteSpace(callee)
                ? "ExternalDependency 节点无法运行时求值（缺少函数信息和输出信号）。"
                : $"ExternalDependency 函数 {callee} 的内部逻辑未展开，且输出信号 {node.VariableName ?? "(无)"} 缺失。";
            return trace;
        }

        /// <summary>
        /// Timer (TON/TOF/TP) 求值。
        /// 策略：
        ///   1. 如果有子节点（IN 启动条件），先求值子节点
        ///   2. 优先读 Q 输出信号做快速判定
        ///   3. Q 不可读时，若 IN 条件为 False 则 TON.Q 必为 False
        ///   4. 其他情况标记 Unknown 并携带 ET/PT 元数据
        /// </summary>
        private StopCauseAstTraceNode EvaluateTimer(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            var timerType = GetMetadataValue(node.Metadata, "TimerType") ?? node.Operator ?? "TON";
            var qSymbol = node.VariableName ?? GetMetadataValue(node.Metadata, "Q");
            var etSymbol = GetMetadataValue(node.Metadata, "ET");

            trace.Children = EvaluateChildren(node, context);
            var inCondition = trace.Children.FirstOrDefault();

            // 优先读 Q 输出信号
            if (!string.IsNullOrWhiteSpace(qSymbol))
            {
                trace.ActualValue = ResolveTokenValue(qSymbol, context);
                var qValue = CoerceBoolean(trace.ActualValue);
                if (qValue.HasValue)
                {
                    trace.Result = qValue.Value;
                    trace.Status = qValue.Value ? StatusTrue : StatusFalse;
                    trace.Message = $"{timerType} 输出 {qSymbol} 当前值为 {qValue.Value}。";
                    return trace;
                }
            }

            // Q 不可读 — 根据 IN 条件推断
            if (inCondition != null && inCondition.Result.HasValue)
            {
                if (!inCondition.Result.Value && string.Equals(timerType, "TON", StringComparison.OrdinalIgnoreCase))
                {
                    // TON: IN=False → Q 必为 False，ET 已清零
                    trace.Result = false;
                    trace.Status = StatusFalse;
                    trace.Message = $"TON 启动条件为 False，定时器未运行，Q 必为 False。";
                    return trace;
                }
                if (!inCondition.Result.Value && string.Equals(timerType, "TOF", StringComparison.OrdinalIgnoreCase))
                {
                    // TOF: IN=False → Q 可能仍为 True（延迟关断），无法确定
                    trace.Status = StatusUnknown;
                    trace.Message = $"TOF 输入已断开，但已过时间未知，Q 状态无法判定。";
                    return trace;
                }
                if (inCondition.Result.Value && string.Equals(timerType, "TON", StringComparison.OrdinalIgnoreCase))
                {
                    // TON: IN=True → Q 取决于 ET >= PT，无法确定
                    trace.Status = StatusUnknown;
                    trace.Message = $"TON 启动条件为 True，但已过时间未知，Q 状态需读取信号。";
                    return trace;
                }
            }

            trace.Status = StatusUnknown;
            trace.Message = $"{timerType} 输出信号 {qSymbol ?? "(无)"} 缺失，无法判定定时器状态。";
            return trace;
        }

        /// <summary>
        /// Move（数据传送）求值。
        /// 策略：
        ///   1. 如果有子节点（EN 使能条件），先求值
        ///   2. 若 EN=False，MOVE 未执行 → 结果取决于 OUT 当前值
        ///   3. 若 EN=True，比较 IN 与 OUT1 值是否一致
        ///   4. 没有 EN 时，直接比较 IN 与 OUT1
        /// </summary>
        private StopCauseAstTraceNode EvaluateMove(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            var source = GetMetadataValue(node.Metadata, "Source");
            var destination = node.VariableName ?? GetMetadataValue(node.Metadata, "Destination");

            trace.Children = EvaluateChildren(node, context);
            var enCondition = trace.Children.FirstOrDefault();

            var sourceValue = ResolveTokenValue(source, context);
            var destValue = ResolveTokenValue(destination, context);
            trace.ActualValue = destValue;

            // EN 条件为 False → MOVE 未执行，用 OUT 当前值判定
            if (enCondition != null && enCondition.Result.HasValue && !enCondition.Result.Value)
            {
                var destBool = CoerceBoolean(destValue);
                if (destBool.HasValue)
                {
                    trace.Result = destBool.Value;
                    trace.Status = destBool.Value ? StatusTrue : StatusFalse;
                    trace.Message = $"MOVE 使能条件为 False（未执行），目标 {destination} 当前值为 {destBool.Value}。";
                    return trace;
                }
                trace.Status = StatusUnknown;
                trace.Message = $"MOVE 使能条件为 False，目标 {destination ?? "(无)"} 信号缺失。";
                return trace;
            }

            // EN=True 或没有 EN → 比较 IN 与 OUT 是否一致
            if (sourceValue != null && destValue != null)
            {
                var equal = EvaluateComparison(sourceValue, destValue, "Eq");
                if (equal.HasValue)
                {
                    trace.Result = equal.Value;
                    trace.Status = equal.Value ? StatusTrue : StatusFalse;
                    trace.Message = equal.Value
                        ? $"MOVE 传送一致：{source} = {destination}。"
                        : $"MOVE 传送不一致：{source}({FormatValue(sourceValue)}) ≠ {destination}({FormatValue(destValue)})。";
                    return trace;
                }
            }

            // 无法比较 — 回退到目标值布尔判定
            var fallbackBool = CoerceBoolean(destValue);
            if (fallbackBool.HasValue)
            {
                trace.Result = fallbackBool.Value;
                trace.Status = fallbackBool.Value ? StatusTrue : StatusFalse;
                trace.Message = $"MOVE 源值未知，目标 {destination} 当前布尔值为 {fallbackBool.Value}。";
                return trace;
            }

            trace.Status = StatusUnknown;
            trace.Message = $"MOVE 源 {source ?? "(无)"} 和目标 {destination ?? "(无)"} 信号均缺失。";
            return trace;
        }

        /// <summary>
        /// SR/RS 触发器求值。
        /// SR: Set 优先 — S=1 → Q=1 不管 R1
        /// RS: Reset 优先 — R=1 → Q=0 不管 S1
        /// 策略：
        ///   1. 求值子节点（第一个=Set条件, 第二个=Reset条件）
        ///   2. 根据 SR/RS 优先级规则推断 Q
        ///   3. 回退到读取 Q 输出信号
        /// </summary>
        private StopCauseAstTraceNode EvaluateSetResetFlipFlop(StopCauseAstNode node, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            var flipFlopType = GetMetadataValue(node.Metadata, "FlipFlopType") ?? node.Operator ?? "SR";
            var qSymbol = node.VariableName ?? GetMetadataValue(node.Metadata, "Q");
            var isSR = string.Equals(flipFlopType, "SR", StringComparison.OrdinalIgnoreCase);

            trace.Children = EvaluateChildren(node, context);
            var setChild = trace.Children.Count > 0 ? trace.Children[0] : null;
            var resetChild = trace.Children.Count > 1 ? trace.Children[1] : null;

            var setResult = setChild?.Result;
            var resetResult = resetChild?.Result;

            // 根据优先级规则推断 Q
            if (setResult.HasValue && resetResult.HasValue)
            {
                bool qResult;
                if (isSR)
                {
                    // SR: Set 优先 — 两者都为 True 时 Q=True
                    qResult = setResult.Value || (!resetResult.Value && ReadQFallback(qSymbol, context, trace));
                    if (setResult.Value)
                    {
                        qResult = true;
                    }
                    else if (resetResult.Value)
                    {
                        qResult = false;
                    }
                    else
                    {
                        // 两者都 False — Q 保持，读当前值
                        var current = ReadQFallback(qSymbol, context, trace);
                        trace.Result = CoerceBoolean(current ? (object)true : false);
                        trace.Status = current ? StatusTrue : StatusFalse;
                        trace.Message = $"{flipFlopType}: S=False, R=False，Q 保持当前值 {current}。";
                        return trace;
                    }
                }
                else
                {
                    // RS: Reset 优先 — 两者都为 True 时 Q=False
                    if (resetResult.Value)
                    {
                        qResult = false;
                    }
                    else if (setResult.Value)
                    {
                        qResult = true;
                    }
                    else
                    {
                        var current = ReadQFallback(qSymbol, context, trace);
                        trace.Result = CoerceBoolean(current ? (object)true : false);
                        trace.Status = current ? StatusTrue : StatusFalse;
                        trace.Message = $"{flipFlopType}: S=False, R=False，Q 保持当前值 {current}。";
                        return trace;
                    }
                }

                trace.Result = qResult;
                trace.Status = qResult ? StatusTrue : StatusFalse;
                trace.Message = $"{flipFlopType}: S={setResult.Value}, R={resetResult.Value} → Q={qResult}。";
                return trace;
            }

            // 子条件不完整 — 回退到读 Q 信号
            if (!string.IsNullOrWhiteSpace(qSymbol))
            {
                trace.ActualValue = ResolveTokenValue(qSymbol, context);
                var qValue = CoerceBoolean(trace.ActualValue);
                if (qValue.HasValue)
                {
                    trace.Result = qValue.Value;
                    trace.Status = qValue.Value ? StatusTrue : StatusFalse;
                    trace.Message = $"{flipFlopType} 条件链不完整，Q({qSymbol}) 当前在线值为 {qValue.Value}。";
                    return trace;
                }
            }

            trace.Status = StatusUnknown;
            trace.Message = $"{flipFlopType} 条件未完全求值且 Q 信号缺失。";
            return trace;
        }

        private bool ReadQFallback(string qSymbol, StopCauseExecutionContext context, StopCauseAstTraceNode trace)
        {
            if (string.IsNullOrWhiteSpace(qSymbol)) return false;
            trace.ActualValue = ResolveTokenValue(qSymbol, context);
            return CoerceBoolean(trace.ActualValue) ?? false;
        }

        private List<StopCauseAstTraceNode> EvaluateChildren(StopCauseAstNode node, StopCauseExecutionContext context)
        {
            return (node.Children ?? new List<StopCauseAstNode>())
                .Select(child => child == null ? null : EvaluateNode(child, context))
                .Where(it => it != null)
                .ToList();
        }

        private static StopCauseAstTraceNode CreateTraceShell(StopCauseAstNode node)
        {
            return new StopCauseAstTraceNode
            {
                NodeId = node.NodeId,
                NodeType = node.NodeType,
                VariableName = node.VariableName,
                DisplayName = node.DisplayName,
                Operator = node.Operator,
                ExpectedValue = node.ExpectedValue,
                Metadata = node.Metadata == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(node.Metadata, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static List<string> BuildMessages(PLC_LogicUnitAstEntity astRow, StopCauseAstTraceNode rootTrace, IReadOnlyCollection<string> missingSymbols)
        {
            var messages = new List<string>();
            if (!string.Equals(astRow?.CompileStatus, AstCompileStatus.Compiled, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"当前 AST 编译状态为 {astRow?.CompileStatus ?? AstCompileStatus.Pending}，求值结果可能受未覆盖分支影响。");
            }

            if (missingSymbols?.Count > 0)
            {
                messages.Add($"存在 {missingSymbols.Count} 个在线信号未命中：{string.Join(", ", missingSymbols.Take(10))}{(missingSymbols.Count > 10 ? " ..." : string.Empty)}");
            }

            if (string.Equals(rootTrace?.Status, StatusFalse, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add("目标 AST 当前求值为 False，可继续使用 FailurePath 做首条失败链路定位。");
            }
            else if (string.Equals(rootTrace?.Status, StatusUnknown, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(rootTrace?.Status, StatusUnsupported, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add("目标 AST 当前未能完全判定，需继续补齐运行时节点支持或补采缺失信号。");
            }
            else if (string.Equals(rootTrace?.Status, StatusTrue, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add("目标 AST 当前求值为 True，说明已编译链路上至少存在一条满足条件的写入路径。");
            }

            return messages;
        }

        internal static List<StopCauseAstTraceNode> BuildFailurePath(StopCauseAstTraceNode rootTrace)
        {
            var path = new List<StopCauseAstTraceNode>();
            var current = rootTrace;
            while (current != null && !string.Equals(current.Status, StatusTrue, StringComparison.OrdinalIgnoreCase))
            {
                path.Add(CloneFlatTraceNode(current));
                current = SelectFailureChild(current);
            }

            // Not 节点的失败原因是子节点为 True，需要把该 True 子节点也加入路径作为终端根因节点
            if (current != null
                && string.Equals(current.Status, StatusTrue, StringComparison.OrdinalIgnoreCase)
                && path.Count > 0
                && string.Equals(path[^1].NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase))
            {
                path.Add(CloneFlatTraceNode(current));
            }

            return path;
        }

        /// <summary>
        /// 根据根节点状态与失败路径计算诊断质量标签，与停机监控诊断流程的分类逻辑保持一致。
        /// Diagnosed       — 根节点已确定，且失败路径上无 Unknown/Unsupported。
        /// DiagnosedPartial — 根节点已确定，但失败路径含不可读节点。
        /// Undetermined    — 根节点为 Unknown/Unsupported，无法给出结论。
        /// </summary>
        internal static string ComputeDiagnosisQuality(
            string rootStatus,
            int unsupportedCount,
            int unknownCount,
            List<StopCauseAstTraceNode> failurePath)
        {
            if (!string.Equals(rootStatus, StatusTrue, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rootStatus, StatusFalse, StringComparison.OrdinalIgnoreCase))
            {
                return "Undetermined";
            }

            var hasAmbiguity = unsupportedCount > 0 || unknownCount > 0;
            if (!hasAmbiguity) return "Diagnosed";

            // 失败路径上所有节点均已确定 → 关键链路完整
            var failurePathClean = failurePath == null
                || failurePath.Count == 0
                || failurePath.All(n =>
                    !string.Equals(n.Status, StatusUnknown, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(n.Status, StatusUnsupported, StringComparison.OrdinalIgnoreCase));

            return failurePathClean ? "Diagnosed" : "DiagnosedPartial";
        }


        internal static StopCauseAstTraceNode SelectFailureChild(StopCauseAstTraceNode node)
        {
            var children = node.Children ?? new List<StopCauseAstTraceNode>();
            if (children.Count == 0)
            {
                return null;
            }

            if (string.Equals(node.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase))
            {
                return children.FirstOrDefault();
            }

            return children.FirstOrDefault(it => string.Equals(it.Status, StatusFalse, StringComparison.OrdinalIgnoreCase))
                ?? children.FirstOrDefault(it => string.Equals(it.Status, StatusUnknown, StringComparison.OrdinalIgnoreCase))
                ?? children.FirstOrDefault(it => string.Equals(it.Status, StatusUnsupported, StringComparison.OrdinalIgnoreCase))
                ?? children.FirstOrDefault();
        }

        private static StopCauseAstTraceNode CloneFlatTraceNode(StopCauseAstTraceNode node)
        {
            return new StopCauseAstTraceNode
            {
                NodeId = node.NodeId,
                NodeType = node.NodeType,
                VariableName = node.VariableName,
                DisplayName = node.DisplayName,
                Operator = node.Operator,
                ExpectedValue = node.ExpectedValue,
                ActualValue = node.ActualValue,
                Result = node.Result,
                Status = node.Status,
                Message = node.Message,
                Metadata = node.Metadata == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(node.Metadata, StringComparer.OrdinalIgnoreCase),
                Children = new List<StopCauseAstTraceNode>()
            };
        }

        private static int CountNodesByStatus(StopCauseAstTraceNode node, string targetStatus)
        {
            if (node == null)
            {
                return 0;
            }

            var current = string.Equals(node.Status, targetStatus, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            return current + (node.Children ?? new List<StopCauseAstTraceNode>()).Sum(child => CountNodesByStatus(child, targetStatus));
        }

        private static IEnumerable<string> CollectRuntimeSymbols(StopCauseAstNode node)
        {
            if (node == null)
            {
                yield break;
            }

            if (IsRuntimeReadableSymbol(node.VariableName))
            {
                yield return NormalizeRuntimeSymbolPath(node.VariableName);
            }

            var left = GetMetadataValue(node.Metadata, "Left");
            if (IsRuntimeReadableSymbol(left))
            {
                yield return NormalizeRuntimeSymbolPath(left);
            }

            var right = GetMetadataValue(node.Metadata, "Right");
            if (IsRuntimeReadableSymbol(right))
            {
                yield return NormalizeRuntimeSymbolPath(right);
            }

            if (IsRuntimeReadableSymbol(node.ExpectedValue))
            {
                yield return NormalizeRuntimeSymbolPath(node.ExpectedValue);
            }

            // Timer: Q, ET, PT 元数据里可能有可读符号
            foreach (var metaKey in new[] { "Q", "ET", "PT", "Source", "Destination" })
            {
                var metaVal = GetMetadataValue(node.Metadata, metaKey);
                if (IsRuntimeReadableSymbol(metaVal))
                {
                    yield return NormalizeRuntimeSymbolPath(metaVal);
                }
            }

            foreach (var child in node.Children ?? new List<StopCauseAstNode>())
            {
                foreach (var symbol in CollectRuntimeSymbols(child))
                {
                    yield return symbol;
                }
            }
        }

        private static int ScoreAstRowMatch(PLC_LogicUnitAstEntity row, string normalizedVariableName, string stationCode, long? blockId)
        {
            var score = 0;
            var outputVariables = DeserializeStringList(row.OutputVariablesJson)
                .Select(NormalizeRuntimeSymbolPath)
                .ToList();
            if (outputVariables.Any(it => string.Equals(it, normalizedVariableName, StringComparison.OrdinalIgnoreCase)))
            {
                score += 200;
            }

            if (!string.IsNullOrWhiteSpace(row.RootNodeJson))
            {
                try
                {
                    var root = JsonSerializer.Deserialize<StopCauseAstNode>(row.RootNodeJson, JsonOptions);
                    if (root != null && string.Equals(NormalizeRuntimeSymbolPath(root.VariableName), normalizedVariableName, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 180;
                    }
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(stationCode) && string.Equals(row.StationCode, stationCode, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }

            if (blockId.HasValue && row.BlockId == blockId.Value)
            {
                score += 30;
            }

            score += ScoreCompileStatus(row.CompileStatus);
            return score;
        }

        private static int ScoreCompileStatus(string compileStatus)
        {
            if (string.Equals(compileStatus, AstCompileStatus.Compiled, StringComparison.OrdinalIgnoreCase))
            {
                return 20;
            }

            if (string.Equals(compileStatus, AstCompileStatus.PartiallyCompiled, StringComparison.OrdinalIgnoreCase))
            {
                return 10;
            }

            return 0;
        }

        private static List<string> DeserializeStringList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        internal static string NormalizeRuntimeSymbolPath(string symbolPath)
        {
            return (symbolPath ?? string.Empty)
                .Trim()
                .Trim('"')
                .Replace("\"", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("IQ.", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SymbolTailEquals(string symbolPath, string tailName)
        {
            var normalized = NormalizeRuntimeSymbolPath(symbolPath);
            var tail = NormalizeRuntimeSymbolPath(tailName).TrimStart('#');
            if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(tail))
                return false;

            var actualTail = normalized.Contains('.')
                ? normalized.Substring(normalized.LastIndexOf('.') + 1)
                : normalized.TrimStart('#');

            return string.Equals(actualTail, tail, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsRuntimeReadableSymbol(string symbolPath)
        {
            var normalized = NormalizeRuntimeSymbolPath(symbolPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized.StartsWith("#", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (TryParseLiteral(normalized, out _))
            {
                return false;
            }

            if (ReservedSymbolKeywords.Contains(normalized))
            {
                return false;
            }

            return true;
        }

        private static object ResolveTokenValue(string token, StopCauseExecutionContext context)
        {
            if (TryGetSignalValue(token, context?.CurrentValues, out var signalValue))
            {
                return signalValue;
            }

            if (TryParseLiteral(token, out var literal))
            {
                return literal;
            }

            // 信号在 PLC 中读取不到（Missing/Bad）时，默认视为 true（正常/OK）。
            // 理由：读不到通常是变量未映射到 IoT 网关、地址偏移不对、或变量不存在于在线设备，
            // 而非设备故障。将其标记为 Unknown 会导致整条 AST 链路无法判定，
            // 默认为 true 可让求值继续聚焦在能读到实际异常值的节点上。
            if (!string.IsNullOrWhiteSpace(token))
            {
                var normalized = NormalizeRuntimeSymbolPath(token);
                if (!string.IsNullOrWhiteSpace(normalized)
                    && context?.SignalQuality != null
                    && context.SignalQuality.TryGetValue(normalized, out var quality)
                    && string.Equals(quality, "Missing", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // 信号质量表里找不到但 CurrentValues 也没有 → 同样视为读不到，默认 true
                if (context?.CurrentValues != null
                    && !context.CurrentValues.ContainsKey(normalized))
                {
                    return true;
                }
            }

            return null;
        }

        /// <summary>
        /// 解析比较操作数 —— 数值优先。
        /// 与 ResolveTokenValue 的区别：先尝试将 token 解析为数字（decimal），
        /// 避免 "1"/"0" 被 TryParseLiteral 当作 bool true/false，导致数值比较失败。
        /// 如果 token 不是纯数字，回退到 ResolveTokenValue（可能是变量名）。
        /// </summary>
        private static object ResolveCompareOperand(string token, StopCauseExecutionContext context)
        {
            var text = (token ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // 优先：是否能从信号表读到值（token 是变量名）
            if (TryGetSignalValue(text, context?.CurrentValues, out var signalValue))
            {
                return signalValue;
            }

            // 其次：纯数字字面量（包括 "0", "1", "6", "100", "3.14" 等）
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var numericValue))
            {
                return numericValue;
            }

            // 最终回退到通用解析
            return ResolveTokenValue(token, context);
        }

        private static bool TryGetSignalValue(string token, IDictionary<string, object> currentValues, out object value)
        {
            value = null;
            if (currentValues == null || currentValues.Count == 0)
            {
                return false;
            }

            var normalized = NormalizeRuntimeSymbolPath(token);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (currentValues.TryGetValue(normalized, out value))
            {
                return true;
            }

            var matched = currentValues.FirstOrDefault(it => string.Equals(NormalizeRuntimeSymbolPath(it.Key), normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(matched.Key))
            {
                value = matched.Value;
                return true;
            }

            // FB 内部变量尾段匹配：TIA Portal 将形参编译为 "实例名.形参名" 格式（如 CylinderCmd.IWP），
            // 但 FbInputParamMap 注入的 key 是纯形参名（如 "IWP"/"#IWP"）。
            // 精确匹配失败时，用尾段（最后一个 '.' 之后）在 CurrentValues 中再次查找。
            // 例：CylinderCmd.IWP → 尾段 IWP → 命中注入的 "IWP" → 得到实际信号值
            if (normalized.Contains('.'))
            {
                var tail = normalized.Substring(normalized.LastIndexOf('.') + 1);
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    if (currentValues.TryGetValue(tail, out value)) return true;
                    var hashTail = "#" + tail;
                    if (currentValues.TryGetValue(hashTail, out value)) return true;
                    var tailMatched = currentValues.FirstOrDefault(it =>
                        string.Equals(NormalizeRuntimeSymbolPath(it.Key), tail, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(tailMatched.Key))
                    {
                        value = tailMatched.Value;
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool TryParseLiteral(string raw, out object literal)
        {
            literal = null;
            var text = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if ((text.StartsWith("\"", StringComparison.Ordinal) && text.EndsWith("\"", StringComparison.Ordinal)) ||
                (text.StartsWith("'", StringComparison.Ordinal) && text.EndsWith("'", StringComparison.Ordinal)))
            {
                literal = text.Substring(1, text.Length - 2);
                return true;
            }

            if (bool.TryParse(text, out var boolValue))
            {
                literal = boolValue;
                return true;
            }

            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase))
            {
                literal = true;
                return true;
            }

            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase))
            {
                literal = false;
                return true;
            }

            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var decimalValue))
            {
                literal = decimalValue;
                return true;
            }

            return false;
        }

        internal static bool? CoerceBoolean(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is JsonElement element)
            {
                return element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue != 0m,
                    JsonValueKind.String => CoerceBoolean(element.GetString()),
                    _ => null
                };
            }

            if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0m;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (bool.TryParse(text, out var parsedBool))
            {
                return parsedBool;
            }

            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedNumber))
            {
                return parsedNumber != 0m;
            }

            if (string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return null;
        }

        internal static bool? EvaluateComparison(object left, object right, string op)
        {
            if (left == null || right == null)
            {
                return null;
            }

            var normalizedOp = (op ?? string.Empty).Trim();
            if (TryConvertToDecimal(left, out var leftNumber) && TryConvertToDecimal(right, out var rightNumber))
            {
                return normalizedOp switch
                {
                    "Eq" => leftNumber == rightNumber,
                    "Ne" => leftNumber != rightNumber,
                    "Gt" => leftNumber > rightNumber,
                    "Ge" => leftNumber >= rightNumber,
                    "Lt" => leftNumber < rightNumber,
                    "Le" => leftNumber <= rightNumber,
                    _ => null
                };
            }

            var leftBool = CoerceBoolean(left);
            var rightBool = CoerceBoolean(right);
            if (leftBool.HasValue && rightBool.HasValue)
            {
                return normalizedOp switch
                {
                    "Eq" => leftBool.Value == rightBool.Value,
                    "Ne" => leftBool.Value != rightBool.Value,
                    _ => null
                };
            }

            var leftText = Convert.ToString(left, CultureInfo.InvariantCulture) ?? string.Empty;
            var rightText = Convert.ToString(right, CultureInfo.InvariantCulture) ?? string.Empty;
            return normalizedOp switch
            {
                "Eq" => string.Equals(leftText, rightText, StringComparison.OrdinalIgnoreCase),
                "Ne" => !string.Equals(leftText, rightText, StringComparison.OrdinalIgnoreCase),
                _ => null
            };
        }

        private static bool TryConvertToDecimal(object raw, out decimal value)
        {
            value = 0m;
            if (raw == null)
            {
                return false;
            }

            if (raw is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
                {
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String)
                {
                    return decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
                }

                return false;
            }

            return decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static string FormatValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is JsonElement element)
            {
                return element.ToString();
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string BuildSignalMessage(string variableName, StopCauseExecutionContext context, string suffix)
        {
            if (context?.SignalQuality == null)
            {
                return suffix;
            }

            var normalized = NormalizeRuntimeSymbolPath(variableName);
            if (!context.SignalQuality.TryGetValue(normalized, out var quality))
            {
                return suffix;
            }

            return $"信号质量 {quality}。{suffix}";
        }

        private static string GetMetadataValue(IDictionary<string, string> metadata, string key)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            return metadata.TryGetValue(key, out var value)
                ? value
                : string.Empty;
        }
    }
}
