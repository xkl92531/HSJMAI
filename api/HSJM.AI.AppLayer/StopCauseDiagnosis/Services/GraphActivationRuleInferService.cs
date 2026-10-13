using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.Basic;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 离线推导 GraphAction 信号激活条件规则（activation_rule）。
    /// 以 LogicUnitId 为收敛维度，将同一逻辑片段的多个 GraphAction 变量合并为一次 LLM 调用，
    /// 最大程度减少 Token 消耗。
    /// 规则码格式：BOOL_TRUE / INT_EQ:N / INT_GT:0 / WORD_GT:0 / BITMASK:N / UNKNOWN
    /// </summary>
    public class GraphActivationRuleInferService : ISelfScopedAutoInject
    {
        private readonly LLMModelService _llmModelService;
        private readonly IPLC_LogicUnitAstService _astService;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_BlockInterfaceService _blockInterfaceService;
        private readonly ILogger<GraphActivationRuleInferService> _logger;

        /// <summary>每次 LLM 调用最多携带多少个逻辑片段组（控制 Prompt 长度）</summary>
        private const int LogicUnitBatchSize = 5;

        /// <summary>最大并发批次数</summary>
        private const int MaxConcurrency = 2;

        /// <summary>代码片段最大字符数（超出截断）</summary>
        private const int MaxExcerptLen = 600;

        private const string SystemPrompt =
            """
            你是西门子 TIA Portal / STEP 7 PLC 程序分析专家。
            任务：分析给定的 PLC 逻辑片段（SCL/STL/梯形图文本），对其中的 GRAPH 步骤激活变量推导"激活状态"的值条件。

            输出格式（严格 JSON 数组，不要任何额外说明）：
            [
              { "var": "<变量名>", "rule": "<规则码>", "reason": "<推理说明，30字内>" },
              ...
            ]

            规则码取值（必须精确使用以下之一）：
            - BOOL_TRUE    变量为 Bool，激活 = True
            - INT_EQ:N     变量为 Int/DInt，激活 = 等于整数 N（如 INT_EQ:5）
            - INT_GT:0     变量为 Int/DInt，激活 = 任意非零正值（无法确定具体步骤号时用此）
            - WORD_GT:0    变量为 Word/DWord，激活 = 任意非零值
            - BITMASK:N    变量为 Word/DWord，激活 = 按位与 N 不为零（如 BITMASK:1）
            - UNKNOWN      无法从代码片段推断，需人工确认

            判断规则：
            - 变量名含 .Active / .S_Active / _Act / _En 等布尔激活语义 → BOOL_TRUE
            - 代码中有 StepVar := N 类整数赋值 → INT_EQ:N
            - 代码中有 StepNumber/StepNo 类变量赋值且无固定值 → INT_GT:0
            - 代码中有位操作赋值（Word 类型）→ BITMASK:N 或 WORD_GT:0
            - DataType=BOOL 时默认 BOOL_TRUE
            """;

        private const string StepRoleSystemPrompt =
            """
            你是西门子 GRAPH 顺控程序分析专家。
            任务：根据 GRAPH StepAction 片段、步骤标题、输出变量、出口 Transition 条件，给每个 StepAction 标注一个语义角色。

            输出格式（严格 JSON 数组，不要任何额外说明）：
            [
              { "logicUnitId": 123, "role": "WorkStep", "confidence": 0.82, "reason": "30字内说明" }
            ]

            role 必须精确使用以下之一：
            - InitStep       初始化/复位/默认待机准备步
            - IdleStep       空闲/待命/等待启动步
            - LongWaitStep   允许长时间等待的人工、物料、外部系统等待步
            - WorkStep       明确驱动执行机构、伺服、气缸、阀、夹爪、工艺动作的工作步
            - WaitUpstream   等待上游交接、来料、允许信号的步骤
            - WaitDownstream 等待下游放行、接料、允许信号的步骤
            - AlarmHandling  报警、异常处理、故障复位相关步骤
            - Unknown        证据不足，无法确定

            判断要求：
            - 不要只靠关键字，必须综合 Action 文本、标题、出口 Transition 条件、输入/输出变量语义。
            - 只有看到明确的动作命令或工艺执行意图，才标 WorkStep。
            - 只有看到等待/空闲/初始化语义且没有动作命令，才标 InitStep/IdleStep/LongWaitStep。
            - 如果只是字符串提示或 HMI 文本，同时出口条件才是真正推进条件，优先根据出口条件判断等待对象。
            - 这些角色只是后续诊断排序证据，不是硬过滤规则；不确定时返回 Unknown，confidence <= 0.5。
            """;

        public GraphActivationRuleInferService(
            LLMModelService llmModelService,
            IPLC_LogicUnitAstService astService,
            IPLC_IOTagService ioTagService,
            IPLC_BlockInterfaceService blockInterfaceService,
            ILogger<GraphActivationRuleInferService> logger)
        {
            _llmModelService = llmModelService;
            _astService = astService;
            _ioTagService = ioTagService;
            _blockInterfaceService = blockInterfaceService;
            _logger = logger;
        }

        /// <summary>
        /// 为指定工位的 GraphAction 信号批量推导激活规则。
        /// 返回 dict&lt;variableName, activationRule&gt;（key 不区分大小写）。
        /// </summary>
        public async Task<Dictionary<string, string>> InferRulesAsync(
            long projectId,
            string stationCode,
            IReadOnlyList<PLC_StationShadowSignalEntity> graphSignals)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (graphSignals == null || graphSignals.Count == 0)
                return result;

            var allVarNames = graphSignals
                .Where(s => !string.IsNullOrWhiteSpace(s.VariableName))
                .Select(s => s.VariableName!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 1. 加载所有 AST 行（含 OutputVariablesJson，用来反查变量属于哪个 LogicUnit）
            var allAstRows = await _astService.GetListAsync(it =>
                it.IsDeleted == false && it.ProjectId == projectId);

            // 2. 加载 IOTag DataType（尝试按 TagName 和 SymbolPath/VariableName 双路匹配）
            List<PLC_IOTagEntity> ioTags = new();
            if (allVarNames.Count > 0)
            {
                ioTags = await _ioTagService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == projectId &&
                    allVarNames.Contains(it.TagName));
            }

            var dataTypeByVar = ioTags
                .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                .GroupBy(t => t.TagName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().DataType ?? "", StringComparer.OrdinalIgnoreCase);

            // 2b. IOTag 未匹配到 DataType 的变量 → 用路径末段反查 BlockInterface（referencedtypename 为空才是叶子标量）
            var unmatchedVars = allVarNames
                .Where(v => !dataTypeByVar.TryGetValue(v, out var existDt) || string.IsNullOrWhiteSpace(existDt))
                .ToList();
            if (unmatchedVars.Count > 0)
            {
                var lastSegments = unmatchedVars
                    .Select(v => v.Contains('.') ? v[(v.LastIndexOf('.') + 1)..] : v)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (lastSegments.Count > 0)
                {
                    _logger.LogWarning("[GraphActivationRuleInfer] BlockInterface查询开始 unmatchedVars={Count} segments=[{Segs}]",
                        unmatchedVars.Count, string.Join(",", lastSegments.Take(10)));

                    // 先不过滤 ReferencedTypeName，查回所有匹配的行再在内存里过滤叶子标量
                    var blockIfRows = await _blockInterfaceService.GetListAsync(it =>
                        it.IsDeleted == false &&
                        lastSegments.Contains(it.VariableName));

                    _logger.LogWarning("[GraphActivationRuleInfer] BlockInterface查询结果 totalRows={Total} 示例=[{Sample}]",
                        blockIfRows.Count,
                        string.Join("|", blockIfRows.Take(5).Select(r =>
                            $"{r.VariableName}:{r.DataType}:ref={r.ReferencedTypeName ?? "null"}")));

                    // 内存里过滤：ReferencedTypeName 为 null 或空字符串 → 叶子标量类型
                    var leafRows = blockIfRows
                        .Where(r => string.IsNullOrWhiteSpace(r.ReferencedTypeName))
                        .ToList();

                    _logger.LogWarning("[GraphActivationRuleInfer] BlockInterface叶子行数={LeafCount}",
                        leafRows.Count);

                    var blockIfByLastSeg = leafRows
                        .Where(r => !string.IsNullOrWhiteSpace(r.VariableName) && !string.IsNullOrWhiteSpace(r.DataType))
                        .GroupBy(r => r.VariableName!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().DataType!, StringComparer.OrdinalIgnoreCase);

                    var fillCount = 0;
                    foreach (var v in unmatchedVars)
                    {
                        var seg = v.Contains('.') ? v[(v.LastIndexOf('.') + 1)..] : v;
                        if (blockIfByLastSeg.TryGetValue(seg, out var bdt) && !string.IsNullOrWhiteSpace(bdt))
                        {
                            dataTypeByVar[v] = bdt;
                            fillCount++;
                        }
                    }

                    _logger.LogWarning("[GraphActivationRuleInfer] BlockInterface补充完成 unmatchedVars={Count} leafRows={Leaf} filledCount={Filled}",
                        unmatchedVars.Count, leafRows.Count, fillCount);

                    // 记录仍然找不到类型的变量，供排查
                    var stillMissing = unmatchedVars
                        .Where(v => !dataTypeByVar.TryGetValue(v, out var dt2) || string.IsNullOrWhiteSpace(dt2))
                        .ToList();
                    if (stillMissing.Count > 0)
                    {
                        _logger.LogWarning("[GraphActivationRuleInfer] 仍未找到DataType的变量 count={Count} vars=[{Vars}]",
                            stillMissing.Count, string.Join("|", stillMissing));
                    }
                }
            }

            // 2c. IOTag + BlockInterface 均未匹配的变量全部列出（兜底日志）
            {
                var noType = allVarNames
                    .Where(v => !dataTypeByVar.TryGetValue(v, out var dt3) || string.IsNullOrWhiteSpace(dt3))
                    .ToList();
                if (noType.Count > 0)
                {
                    _logger.LogWarning("[GraphActivationRuleInfer] 最终无DataType变量 count={Count} vars=[{Vars}]",
                        noType.Count, string.Join("|", noType));
                }
            }

            // 3. 通过 OutputVariablesJson 反查：变量名 → AST 行（LogicUnit 上下文）
            //    优先精确匹配，多条时取 UnitType 含 STEP 的优先
            var astByVar = BuildAstLookupByOutputVar(allAstRows);

            // 4. 将影子信号按"找到的 LogicUnitId"分组，找不到的归入 noContextGroup
            var byLogicUnit = new Dictionary<long, List<(PLC_StationShadowSignalEntity sig, PLC_LogicUnitAstEntity ast)>>();
            var noContextSignals = new List<PLC_StationShadowSignalEntity>();

            foreach (var sig in graphSignals)
            {
                if (string.IsNullOrWhiteSpace(sig.VariableName)) continue;
                var varName = sig.VariableName.Trim();

                // 优先用信号自身的 LogicUnitId，否则通过 AST OutputVariables 反查
                long? resolvedUnit = sig.LogicUnitId;
                PLC_LogicUnitAstEntity? matchedAst = null;

                if (!resolvedUnit.HasValue && astByVar.TryGetValue(varName, out var candidates))
                {
                    matchedAst = candidates.FirstOrDefault();
                    resolvedUnit = matchedAst?.LogicUnitId;
                }
                else if (resolvedUnit.HasValue)
                {
                    matchedAst = allAstRows.FirstOrDefault(a => a.LogicUnitId == resolvedUnit);
                }

                if (resolvedUnit.HasValue && matchedAst != null)
                {
                    if (!byLogicUnit.ContainsKey(resolvedUnit.Value))
                        byLogicUnit[resolvedUnit.Value] = new();
                    byLogicUnit[resolvedUnit.Value].Add((sig, matchedAst));
                }
                else
                {
                    noContextSignals.Add(sig);
                }
            }

            // 4b. 记录没有找到 AST 上下文的信号（可能是播种时 LogicUnitId 未写入 + OutputVariables 未命中）
            if (noContextSignals.Count > 0)
            {
                _logger.LogWarning("[GraphActivationRuleInfer] 无AST上下文信号 count={Count} vars=[{Vars}]",
                    noContextSignals.Count,
                    string.Join("|", noContextSignals
                        .Where(s => !string.IsNullOrWhiteSpace(s.VariableName))
                        .Select(s => s.VariableName!)));
            }

            // 5. 有代码上下文的按 LogicUnit 分批调 LLM
            if (byLogicUnit.Count > 0)
            {
                // 将 (sig, ast) 组转换为 InferRulesAsync 内部格式：每组一个 ast + 多个 sig
                var logicUnitGroups = byLogicUnit.Values
                    .Select(group =>
                    {
                        var ast = group.First().ast;
                        var sigs = group.Select(x => x.sig).ToList();
                        return (ast, sigs);
                    })
                    .ToList();

                var batches = logicUnitGroups
                    .Select((g, i) => (g, i))
                    .GroupBy(x => x.i / LogicUnitBatchSize)
                    .Select(batch => batch.Select(x => x.g).ToList())
                    .ToList();

                var sem = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
                var tasks = batches.Select(async batch =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        return await CallLlmForBatchGroupsAsync(batch, dataTypeByVar);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[GraphActivationRuleInfer] 批次失败，跳过");
                        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                    finally
                    {
                        sem.Release();
                    }
                });

                var batchResults = await Task.WhenAll(tasks);
                foreach (var br in batchResults)
                    foreach (var kv in br)
                        result[kv.Key] = kv.Value;

                // LLM 返回 UNKNOWN 但已知 DataType → 用 DataType 兜底覆盖（避免 WString 等类型被误标 UNKNOWN）
                foreach (var key in result.Keys.ToList())
                {
                    if (result[key] == "UNKNOWN" &&
                        dataTypeByVar.TryGetValue(key, out var fallbackDt) &&
                        !string.IsNullOrWhiteSpace(fallbackDt))
                    {
                        var fallback = InferRuleFromDataType(fallbackDt);
                        if (fallback != "UNKNOWN")
                            result[key] = fallback;
                    }
                }
            }

            // 6. 无代码上下文的信号 → 按 DataType 直接推断（不调 LLM）
            foreach (var sig in noContextSignals)
            {
                if (string.IsNullOrWhiteSpace(sig.VariableName)) continue;
                var varName = sig.VariableName.Trim();
                if (result.ContainsKey(varName)) continue;
                dataTypeByVar.TryGetValue(varName, out var dt);
                result[varName] = InferRuleFromDataType(dt);
            }

            // 汇总 UNKNOWN 变量（=没有 DataType 或 LLM 无法推断）
            var unknownVars = result
                .Where(kv => kv.Value == "UNKNOWN")
                .Select(kv => kv.Key)
                .ToList();
            // 其中：无 DataType 导致 UNKNOWN 的（noContext 且 dataType 为空）
            var noDataTypeVars = noContextSignals
                .Where(s => !string.IsNullOrWhiteSpace(s.VariableName))
                .Select(s => s.VariableName!.Trim())
                .Where(v => !dataTypeByVar.TryGetValue(v, out var dt4) || string.IsNullOrWhiteSpace(dt4))
                .ToList();

            await WriteLogHelper.WriteLogsAsync(
                $"[GraphActivationRuleInfer] {stationCode} 推导完成 signalCount={graphSignals.Count} " +
                $"withAst={byLogicUnit.Sum(g => g.Value.Count)} noAst={noContextSignals.Count} ruleCount={result.Count} " +
                $"unknownCount={unknownVars.Count} noDataTypeCount={noDataTypeVars.Count} " +
                (noDataTypeVars.Count > 0 ? $"noDataType=[{string.Join("|", noDataTypeVars)}] " : "") +
                $"rules=[{string.Join(",", result.Take(5).Select(kv => $"{kv.Key}={kv.Value}"))}]",
                "GraphActivationRule");

            return result;
        }

        /// <summary>
        /// Infer semantic roles for GRAPH StepAction logic units.
        /// The result is keyed by LogicUnitId and is stored as diagnosis evidence.
        /// </summary>
        public async Task<Dictionary<long, GraphStepRoleInferenceResult>> InferStepRolesAsync(
            long projectId,
            string stationCode,
            IReadOnlyList<PLC_StationShadowSignalEntity> graphSignals)
        {
            var result = new Dictionary<long, GraphStepRoleInferenceResult>();
            if (graphSignals == null || graphSignals.Count == 0)
                return result;

            var allAstRows = await _astService.GetListAsync(it =>
                it.IsDeleted == false && it.ProjectId == projectId);
            var astByVar = BuildAstLookupByOutputVar(allAstRows);

            var byLogicUnit = new Dictionary<long, List<(PLC_StationShadowSignalEntity sig, PLC_LogicUnitAstEntity ast)>>();
            var noContextSignals = new List<PLC_StationShadowSignalEntity>();

            foreach (var sig in graphSignals)
            {
                if (string.IsNullOrWhiteSpace(sig.VariableName)) continue;
                var varName = sig.VariableName.Trim();

                long? resolvedUnit = sig.LogicUnitId;
                PLC_LogicUnitAstEntity? matchedAst = null;

                if (!resolvedUnit.HasValue && astByVar.TryGetValue(varName, out var candidates))
                {
                    matchedAst = candidates.FirstOrDefault();
                    resolvedUnit = matchedAst?.LogicUnitId;
                }
                else if (resolvedUnit.HasValue)
                {
                    matchedAst = allAstRows.FirstOrDefault(a => a.LogicUnitId == resolvedUnit);
                }

                if (resolvedUnit.HasValue && matchedAst != null)
                {
                    if (!byLogicUnit.ContainsKey(resolvedUnit.Value))
                        byLogicUnit[resolvedUnit.Value] = new();
                    byLogicUnit[resolvedUnit.Value].Add((sig, matchedAst));
                }
                else
                {
                    noContextSignals.Add(sig);
                }
            }

            if (noContextSignals.Count > 0)
            {
                _logger.LogWarning("[GraphStepRoleInfer] No AST context signals count={Count} vars=[{Vars}]",
                    noContextSignals.Count,
                    string.Join("|", noContextSignals
                        .Where(s => !string.IsNullOrWhiteSpace(s.VariableName))
                        .Select(s => s.VariableName!)));
            }

            if (byLogicUnit.Count == 0)
                return result;

            var logicUnitGroups = byLogicUnit.Values
                .Select(group =>
                {
                    var ast = group.First().ast;
                    var sigs = group.Select(x => x.sig).ToList();
                    return (ast, sigs);
                })
                .ToList();

            var expectedLogicUnitIds = logicUnitGroups
                .Select(g => g.ast.LogicUnitId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            var batches = logicUnitGroups
                .Select((g, i) => (g, i))
                .GroupBy(x => x.i / LogicUnitBatchSize)
                .Select(batch => batch.Select(x => x.g).ToList())
                .ToList();

            var sem = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = batches.Select(async batch =>
            {
                await sem.WaitAsync();
                try
                {
                    return await CallLlmForStepRoleBatchGroupsAsync(batch);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GraphStepRoleInfer] Batch failed, skipped");
                    return BuildUnknownRoleResult(batch
                        .Select(g => g.ast.LogicUnitId)
                        .Where(id => id.HasValue)
                        .Select(id => id!.Value)
                        .ToList());
                }
                finally
                {
                    sem.Release();
                }
            });

            var batchResults = await Task.WhenAll(tasks);
            foreach (var br in batchResults)
            {
                foreach (var kv in br)
                    result[kv.Key] = kv.Value;
            }

            foreach (var id in expectedLogicUnitIds)
            {
                if (!result.ContainsKey(id))
                    result[id] = GraphStepRoleInferenceResult.Unknown(id);
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[GraphStepRoleInfer] {stationCode} completed signalCount={graphSignals.Count} " +
                $"withAst={byLogicUnit.Sum(g => g.Value.Count)} noAst={noContextSignals.Count} roleCount={result.Count} " +
                $"roles=[{string.Join(",", result.Take(5).Select(kv => $"{kv.Key}={kv.Value.Role}/{kv.Value.Confidence}"))}]",
                "GraphStepRole");

            return result;
        }

        /// <summary>
        /// 通过 OutputVariablesJson 反查：变量名 → AST 行列表（一个变量可能出现在多个 AST 行）。
        /// </summary>
        private static Dictionary<string, List<PLC_LogicUnitAstEntity>> BuildAstLookupByOutputVar(
            List<PLC_LogicUnitAstEntity> astRows)
        {
            var lookup = new Dictionary<string, List<PLC_LogicUnitAstEntity>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ast in astRows)
            {
                if (string.IsNullOrWhiteSpace(ast.OutputVariablesJson)) continue;
                try
                {
                    var vars = System.Text.Json.JsonSerializer.Deserialize<List<string>>(ast.OutputVariablesJson);
                    if (vars == null) continue;
                    foreach (var v in vars)
                    {
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        var key = v.Trim();
                        if (!lookup.ContainsKey(key))
                            lookup[key] = new List<PLC_LogicUnitAstEntity>();
                        // STEP_X_ACTION 类型优先插到最前
                        if (!string.IsNullOrWhiteSpace(ast.UnitType) &&
                            ast.UnitType.StartsWith("STEP_", StringComparison.OrdinalIgnoreCase))
                            lookup[key].Insert(0, ast);
                        else
                            lookup[key].Add(ast);
                    }
                }
                catch { }
            }
            return lookup;
        }

        private async Task<Dictionary<string, string>> CallLlmForBatchGroupsAsync(
            IReadOnlyList<(PLC_LogicUnitAstEntity ast, List<PLC_StationShadowSignalEntity> sigs)> groups,
            IReadOnlyDictionary<string, string> dataTypeByVar)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请分析以下 PLC 逻辑片段，为每个 GraphAction 变量推导激活规则：");
            sb.AppendLine();

            var allVarNames = new List<string>();

            foreach (var (ast, sigs) in groups)
            {
                sb.AppendLine($"--- 逻辑片段 LogicUnitId={ast.LogicUnitId} ---");
                sb.AppendLine($"Block: {ast.BlockName}  Language: {ast.ProgrammingLanguage}");
                if (!string.IsNullOrWhiteSpace(ast.Title))
                    sb.AppendLine($"标题: {ast.Title}");

                if (!string.IsNullOrWhiteSpace(ast.RawContentExcerpt))
                {
                    var excerpt = TruncateExcerpt(ast.RawContentExcerpt, MaxExcerptLen);
                    sb.AppendLine($"代码片段:\n{excerpt}");
                }

                sb.AppendLine("需推导规则的变量：");
                foreach (var sig in sigs)
                {
                    if (string.IsNullOrWhiteSpace(sig.VariableName)) continue;
                    var varName = sig.VariableName.Trim();
                    dataTypeByVar.TryGetValue(varName, out var dt);
                    var label = string.IsNullOrWhiteSpace(sig.SemanticLabel) ? "" : $" [{sig.SemanticLabel}]";
                    sb.AppendLine($"  - {varName}{label}  DataType={dt ?? "未知"}");
                    allVarNames.Add(varName);
                }
                sb.AppendLine();
            }

            var recommendedModel = await _llmModelService.RecommendModelAsync();
            if (recommendedModel == null)
            {
                _logger.LogWarning("[GraphActivationRuleInfer] 无可用 LLM 模型");
                return BuildUnknownResult(allVarNames);
            }

            var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(recommendedModel.Id);
            var response = await _llmModelService.ChatAsync(chatClient, sb.ToString(), SystemPrompt);

            if (response == null || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("[GraphActivationRuleInfer] LLM 返回为空");
                return BuildUnknownResult(allVarNames);
            }

            try
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[GraphActivationRuleInfer] LLM输入({allVarNames.Count}个变量): {sb.ToString()[..Math.Min(400, sb.Length)]}\n" +
                    $"LLM输出: {response.Content[..Math.Min(500, response.Content.Length)]}",
                    "GraphActivationRule_LLM");
            }
            catch { }

            return ParseRuleResponse(response.Content, allVarNames);
        }

        private async Task<Dictionary<long, GraphStepRoleInferenceResult>> CallLlmForStepRoleBatchGroupsAsync(
            IReadOnlyList<(PLC_LogicUnitAstEntity ast, List<PLC_StationShadowSignalEntity> sigs)> groups)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请为以下 GRAPH StepAction 标注语义角色：");
            sb.AppendLine();

            var expectedLogicUnitIds = groups
                .Select(g => g.ast.LogicUnitId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            foreach (var (ast, sigs) in groups)
            {
                var metadata = ExtractGraphActionMetadata(ast);
                var actionText = GetMetadataValue(metadata, "ActionText");
                var actionRaw = GetMetadataValue(metadata, "ActionRawContentExcerpt");
                var stepNumber = GetMetadataValue(metadata, "GraphStepNumber");
                var outgoingTransitions = GetMetadataValue(metadata, "OutgoingTransitionNetworkIds");
                var transitionConditions = GetMetadataValue(metadata, "GraphTransitionConditions");

                sb.AppendLine($"--- StepAction LogicUnitId={ast.LogicUnitId} ---");
                sb.AppendLine($"Block: {ast.BlockName}  Language: {ast.ProgrammingLanguage}  UnitType: {ast.UnitType}");
                if (!string.IsNullOrWhiteSpace(ast.Title))
                    sb.AppendLine($"标题: {ast.Title}");
                if (!string.IsNullOrWhiteSpace(ast.Comment))
                    sb.AppendLine($"注释: {TruncateExcerpt(ast.Comment, 160)}");
                if (!string.IsNullOrWhiteSpace(stepNumber))
                    sb.AppendLine($"GraphStepNumber: {stepNumber}");

                if (!string.IsNullOrWhiteSpace(actionText))
                    sb.AppendLine($"ActionText:\n{TruncateExcerpt(actionText, 500)}");
                if (!string.IsNullOrWhiteSpace(actionRaw) &&
                    !string.Equals(actionRaw.Trim(), actionText?.Trim(), StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"ActionXmlExcerpt:\n{TruncateExcerpt(actionRaw, 800)}");
                if (string.IsNullOrWhiteSpace(actionText) &&
                    string.IsNullOrWhiteSpace(actionRaw) &&
                    !string.IsNullOrWhiteSpace(ast.RawContentExcerpt))
                    sb.AppendLine($"RawContent:\n{TruncateExcerpt(ast.RawContentExcerpt, 800)}");

                if (!string.IsNullOrWhiteSpace(outgoingTransitions))
                    sb.AppendLine($"OutgoingTransitionNetworkIds: {TruncateExcerpt(outgoingTransitions, 160)}");
                if (!string.IsNullOrWhiteSpace(transitionConditions))
                    sb.AppendLine($"TransitionConditions:\n{BuildTransitionConditionPrompt(transitionConditions)}");

                if (!string.IsNullOrWhiteSpace(ast.InputVariablesJson))
                    sb.AppendLine($"InputVariables: {TruncateExcerpt(ast.InputVariablesJson, 500)}");
                if (!string.IsNullOrWhiteSpace(ast.OutputVariablesJson))
                    sb.AppendLine($"OutputVariables: {TruncateExcerpt(ast.OutputVariablesJson, 500)}");

                sb.AppendLine("监控变量:");
                foreach (var sig in sigs)
                {
                    if (string.IsNullOrWhiteSpace(sig.VariableName)) continue;
                    var label = string.IsNullOrWhiteSpace(sig.SemanticLabel) ? "" : $" [{sig.SemanticLabel}]";
                    sb.AppendLine($"  - {sig.VariableName}{label}");
                }

                sb.AppendLine();
            }

            var recommendedModel = await _llmModelService.RecommendModelAsync();
            if (recommendedModel == null)
            {
                _logger.LogWarning("[GraphStepRoleInfer] No available LLM model");
                return BuildUnknownRoleResult(expectedLogicUnitIds);
            }

            var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(recommendedModel.Id);
            var response = await _llmModelService.ChatAsync(chatClient, sb.ToString(), StepRoleSystemPrompt);

            if (response == null || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("[GraphStepRoleInfer] LLM returned empty content");
                return BuildUnknownRoleResult(expectedLogicUnitIds);
            }

            try
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[GraphStepRoleInfer] LLM input({expectedLogicUnitIds.Count} units): {sb.ToString()[..Math.Min(500, sb.Length)]}\n" +
                    $"LLM output: {response.Content[..Math.Min(500, response.Content.Length)]}",
                    "GraphStepRole_LLM");
            }
            catch { }

            return ParseStepRoleResponse(response.Content, expectedLogicUnitIds);
        }

        private static string BuildTransitionConditionPrompt(string transitionConditionsJson)
        {
            if (string.IsNullOrWhiteSpace(transitionConditionsJson))
                return string.Empty;

            try
            {
                var items = JsonSerializer.Deserialize<List<GraphTransitionConditionSummary>>(transitionConditionsJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items == null || items.Count == 0)
                    return TruncateExcerpt(transitionConditionsJson, 1200);

                var sb = new StringBuilder();
                foreach (var item in items.Take(6))
                {
                    sb.AppendLine($"- TransitionNetworkId={item.TransitionNetworkId}, LogicUnitId={item.LogicUnitId}, UnitType={item.UnitType}, Block={item.BlockName}");
                    if (!string.IsNullOrWhiteSpace(item.Title))
                        sb.AppendLine($"  Title: {item.Title}");
                    if (item.InputVariables?.Count > 0)
                        sb.AppendLine($"  InputVariables: {string.Join(", ", item.InputVariables.Take(12))}");
                    if (item.OutputVariables?.Count > 0)
                        sb.AppendLine($"  OutputVariables: {string.Join(", ", item.OutputVariables.Take(8))}");
                    if (!string.IsNullOrWhiteSpace(item.RawContentExcerpt))
                        sb.AppendLine($"  ConditionXmlExcerpt: {TruncateExcerpt(item.RawContentExcerpt, 700)}");
                }

                if (items.Count > 6)
                    sb.AppendLine($"- ... {items.Count - 6} more transition conditions omitted");

                return sb.ToString().TrimEnd();
            }
            catch
            {
                return TruncateExcerpt(transitionConditionsJson, 1200);
            }
        }

        private static Dictionary<string, string> ParseRuleResponse(
            string content,
            IReadOnlyList<string> expectedVarNames)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var start = content.IndexOf('[');
                var end = content.LastIndexOf(']');
                if (start < 0 || end <= start) return BuildUnknownResult(expectedVarNames);

                var json = content[start..(end + 1)];
                var items = JsonSerializer.Deserialize<List<RuleResultItem>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items != null)
                    foreach (var item in items)
                        if (!string.IsNullOrWhiteSpace(item.Var) && !string.IsNullOrWhiteSpace(item.Rule))
                            result[item.Var] = NormalizeRule(item.Rule);
            }
            catch { }

            // 未解析到的变量标记 UNKNOWN
            // 先尝试用末段短名去 LLM 结果里二次匹配（LLM 可能把完整路径简写为末段）
            foreach (var varName in expectedVarNames)
            {
                if (result.ContainsKey(varName)) continue;

                // 尝试末段短名匹配
                var lastSeg = varName.Contains('.') ? varName[(varName.LastIndexOf('.') + 1)..] : null;
                if (lastSeg != null && result.TryGetValue(lastSeg, out var shortRule))
                {
                    result[varName] = shortRule;
                }
                else
                {
                    result[varName] = "UNKNOWN";
                }
            }

            return result;
        }

        private static Dictionary<string, string> BuildUnknownResult(IReadOnlyList<string> varNames)
        {
            return varNames.ToDictionary(v => v, _ => "UNKNOWN", StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<long, GraphStepRoleInferenceResult> ParseStepRoleResponse(
            string content,
            IReadOnlyList<long> expectedLogicUnitIds)
        {
            var result = new Dictionary<long, GraphStepRoleInferenceResult>();
            try
            {
                var start = content.IndexOf('[');
                var end = content.LastIndexOf(']');
                if (start >= 0 && end > start)
                {
                    var json = content[start..(end + 1)];
                    var items = JsonSerializer.Deserialize<List<StepRoleResultItem>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (items != null)
                    {
                        foreach (var item in items)
                        {
                            if (!item.LogicUnitId.HasValue || item.LogicUnitId.Value <= 0) continue;
                            result[item.LogicUnitId.Value] = new GraphStepRoleInferenceResult
                            {
                                LogicUnitId = item.LogicUnitId.Value,
                                Role = NormalizeStepRole(item.Role),
                                Confidence = ClampConfidence(item.Confidence),
                                Reason = TruncateExcerpt(item.Reason ?? string.Empty, 480)
                            };
                        }
                    }
                }
            }
            catch { }

            foreach (var id in expectedLogicUnitIds.Distinct())
            {
                if (!result.ContainsKey(id))
                    result[id] = GraphStepRoleInferenceResult.Unknown(id);
            }

            return result;
        }

        private static Dictionary<long, GraphStepRoleInferenceResult> BuildUnknownRoleResult(IReadOnlyList<long> logicUnitIds)
        {
            return logicUnitIds
                .Distinct()
                .ToDictionary(id => id, GraphStepRoleInferenceResult.Unknown);
        }

        private static Dictionary<string, string> ExtractGraphActionMetadata(PLC_LogicUnitAstEntity ast)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (ast == null || string.IsNullOrWhiteSpace(ast.RootNodeJson))
                return result;

            try
            {
                var node = JsonSerializer.Deserialize<StopCauseAstNode>(ast.RootNodeJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var actionNode = FindGraphActionNode(node, ast.LogicUnitId) ?? node;
                if (actionNode?.Metadata == null)
                    return result;

                foreach (var kv in actionNode.Metadata)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                        result[kv.Key] = kv.Value ?? string.Empty;
                }
            }
            catch { }

            return result;
        }

        private static StopCauseAstNode? FindGraphActionNode(StopCauseAstNode? node, long? logicUnitId)
        {
            if (node == null) return null;

            if (logicUnitId.HasValue &&
                !string.IsNullOrWhiteSpace(node.NodeId) &&
                node.NodeId.IndexOf($"logic-unit:{logicUnitId.Value}:action", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return node;
            }

            if (node.Metadata != null &&
                node.Metadata.TryGetValue("UnitType", out var unitType) &&
                unitType.IndexOf("STEP_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                unitType.IndexOf("ACTION", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return node;
            }

            foreach (var child in node.Children ?? new List<StopCauseAstNode>())
            {
                var found = FindGraphActionNode(child, logicUnitId);
                if (found != null) return found;
            }

            return null;
        }

        private static string GetMetadataValue(Dictionary<string, string> metadata, string key)
        {
            return metadata != null && metadata.TryGetValue(key, out var value)
                ? value ?? string.Empty
                : string.Empty;
        }

        /// <summary>
        /// 仅根据 PLC DataType 字符串推断规则（无代码上下文时的兜底，不调 LLM）。
        /// </summary>
        public static string InferRuleFromDataType(string? dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return "BOOL_TRUE";
            var dt = dataType.Trim().ToUpperInvariant();
            if (dt == "BOOL") return "BOOL_TRUE";
            if (dt is "INT" or "DINT" or "UINT" or "UDINT" or "SINT" or "USINT" or "LINT" or "ULINT")
                return "INT_GT:0";
            if (dt is "WORD" or "DWORD" or "BYTE" or "LWORD")
                return "WORD_GT:0";
            // WString/String/Char 类型：写入非空字符串视为激活
            if (dt.StartsWith("WSTRING") || dt.StartsWith("STRING") || dt == "CHAR" || dt == "WCHAR")
                return "WSTRING_NEQ:";
            return "BOOL_TRUE"; // 未知类型默认 bool 安全兜底
        }

        /// <summary>
        /// 根据 activation_rule 和当前值判断信号是否处于激活状态。
        /// rule 为 null/空 时默认 BOOL_TRUE 语义。
        /// </summary>
        public static bool EvaluateRule(string? rule, object? value)
        {
            if (value == null) return false;

            var effectiveRule = string.IsNullOrWhiteSpace(rule)
                ? "BOOL_TRUE"
                : rule.Trim().ToUpperInvariant();

            switch (effectiveRule)
            {
                case "BOOL_TRUE":
                {
                    var s = value.ToString()?.Trim().ToUpperInvariant();
                    return s == "TRUE" || s == "1";
                }
                case "BOOL_FALSE":
                {
                    var s = value.ToString()?.Trim().ToUpperInvariant();
                    return s == "FALSE" || s == "0";
                }
                case "INT_GT:0":
                case "WORD_GT:0":
                {
                    if (decimal.TryParse(value.ToString(), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out var d))
                        return d > 0;
                    return false;
                }
                case "UNKNOWN":
                    // 无法确认 → 保守策略：只认 bool=True
                    goto case "BOOL_TRUE";
            }

            // WSTRING_NEQ:（字符串非空视为激活）
            if (effectiveRule.StartsWith("WSTRING_NEQ:"))
            {
                var s2 = value.ToString();
                return !string.IsNullOrEmpty(s2);
            }

            // INT_EQ:N
            if (effectiveRule.StartsWith("INT_EQ:"))
            {
                if (decimal.TryParse(effectiveRule[7..], NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var target) &&
                    decimal.TryParse(value.ToString(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var actual))
                    return actual == target;
                return false;
            }

            // BITMASK:N
            if (effectiveRule.StartsWith("BITMASK:"))
            {
                if (long.TryParse(effectiveRule[8..], out var mask) &&
                    long.TryParse(value.ToString(), out var val))
                    return (val & mask) != 0;
                return false;
            }

            // 未识别规则 → 保守 bool
            var sv = value.ToString()?.Trim().ToUpperInvariant();
            return sv == "TRUE" || sv == "1";
        }

        private static string NormalizeRule(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "UNKNOWN";
            var r = raw.Trim().ToUpperInvariant();
            // 允许 LLM 输出 INT_EQ:5、BITMASK:1 等带数字的规则
            if (r == "BOOL_TRUE" || r == "INT_GT:0" || r == "WORD_GT:0" || r == "UNKNOWN")
                return r;
            if (r.StartsWith("INT_EQ:") || r.StartsWith("BITMASK:") || r.StartsWith("WSTRING_NEQ:"))
                return r;
            return "UNKNOWN";
        }

        private static string NormalizeStepRole(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
            var role = raw.Trim();
            return role.ToUpperInvariant() switch
            {
                "INITSTEP" or "INIT" or "INITIAL" => "InitStep",
                "IDLESTEP" or "IDLE" => "IdleStep",
                "LONGWAITSTEP" or "LONGWAIT" or "LONG_WAIT" => "LongWaitStep",
                "WORKSTEP" or "WORK" or "ACTIONSTEP" => "WorkStep",
                "WAITUPSTREAM" or "WAIT_UPSTREAM" => "WaitUpstream",
                "WAITDOWNSTREAM" or "WAIT_DOWNSTREAM" => "WaitDownstream",
                "ALARMHANDLING" or "ALARM" or "FAULT" => "AlarmHandling",
                "UNKNOWN" => "Unknown",
                _ => "Unknown"
            };
        }

        private static decimal ClampConfidence(decimal? confidence)
        {
            if (!confidence.HasValue) return 0m;
            if (confidence.Value < 0m) return 0m;
            if (confidence.Value > 1m) return 1m;
            return decimal.Round(confidence.Value, 4);
        }

        private static string TruncateExcerpt(string excerpt, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(excerpt) || excerpt.Length <= maxLen) return excerpt;
            return excerpt[..maxLen] + "...[截断]";
        }

        private class RuleResultItem
        {
            public string? Var { get; set; }
            public string? Rule { get; set; }
            public string? Reason { get; set; }
        }

        private class StepRoleResultItem
        {
            public long? LogicUnitId { get; set; }
            public string? Role { get; set; }
            public decimal? Confidence { get; set; }
            public string? Reason { get; set; }
        }
    }

    public class GraphStepRoleInferenceResult
    {
        public long LogicUnitId { get; set; }

        public string Role { get; set; } = "Unknown";

        public decimal Confidence { get; set; }

        public string Reason { get; set; } = string.Empty;

        public static GraphStepRoleInferenceResult Unknown(long logicUnitId)
        {
            return new GraphStepRoleInferenceResult
            {
                LogicUnitId = logicUnitId,
                Role = "Unknown",
                Confidence = 0m,
                Reason = "No reliable role inference."
            };
        }
    }
}
