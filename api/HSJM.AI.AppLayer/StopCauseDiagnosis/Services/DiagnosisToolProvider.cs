using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 停机诊断 Function Call 工具提供者。
    /// 
    /// 职责：将底层诊断服务（信号缓存、AST 求值、工位拓扑、记忆库…）
    ///       封装为 AIFunction 列表，供 ReAct 循环中的 LLM Agent 按需调用。
    /// 
    /// 设计原则：
    /// 1. 每个工具返回精简文本（非 JSON），LLM 直接阅读无需反序列化
    /// 2. 单次返回限制 ≤ 4000 字符，超长自动截断
    /// 3. 工具之间无依赖，LLM 可任意顺序调用
    /// 4. 所有查询只读，不产生副作用
    /// </summary>
    public class DiagnosisToolProvider : ISelfScopedAutoInject
    {
        private readonly StationShadowStateCacheService _cacheService;
        private readonly StopCauseAstRuntimeEvaluationService _runtimeEvalService;
        private readonly AstMarkdownRendererService _astRenderer;
        private readonly KnowledgeMemoryService _knowledgeMemory;
        private readonly IPLC_LogicUnitAstService _logicUnitAstService;
        private readonly IPLC_StationAnchorBindingService _anchorBindingService;
        private readonly IPLC_StationShadowConfigService _shadowConfigService;
        private readonly IPLC_BlockService _blockService;
        private readonly ILogger<DiagnosisToolProvider> _logger;

        /// <summary>工具返回文本最大字符数</summary>
        private const int MaxOutputChars = 4000;

        public DiagnosisToolProvider(
            StationShadowStateCacheService cacheService,
            StopCauseAstRuntimeEvaluationService runtimeEvalService,
            AstMarkdownRendererService astRenderer,
            KnowledgeMemoryService knowledgeMemory,
            IPLC_LogicUnitAstService logicUnitAstService,
            IPLC_StationAnchorBindingService anchorBindingService,
            IPLC_StationShadowConfigService shadowConfigService,
            IPLC_BlockService blockService,
            ILogger<DiagnosisToolProvider> logger)
        {
            _cacheService = cacheService;
            _runtimeEvalService = runtimeEvalService;
            _astRenderer = astRenderer;
            _knowledgeMemory = knowledgeMemory;
            _logicUnitAstService = logicUnitAstService;
            _anchorBindingService = anchorBindingService;
            _shadowConfigService = shadowConfigService;
            _blockService = blockService;
            _logger = logger;
        }

        #region 工具注册

        /// <summary>
        /// 构建诊断工具列表，绑定到指定项目的上下文。
        /// projectId 在闭包中捕获，LLM 无需感知。
        /// </summary>
        public List<AIFunction> BuildTools(long projectId)
        {
            return new List<AIFunction>
            {
                // 1. 工位级摘要
                AIFunctionFactory.Create(
                    ([Description("工位编号，如 OP010")] string stationCode) =>
                        GetStationSummaryAsync(projectId, stationCode),
                    name: "GetStationSummary",
                    description: "获取指定工位的实时状态摘要：当前信号值统计、最近变化、锚点绑定。用于快速了解一个工位的整体状况。"),

                // 2. 单信号实时值
                AIFunctionFactory.Create(
                    ([Description("信号/变量名，如 DB110.DBX0.0 或 Tag_Motor1")] string signalName,
                     [Description("工位编号")] string stationCode) =>
                        GetSignalValueAsync(projectId, stationCode, signalName),
                    name: "GetSignalValue",
                    description: "查询单个信号的当前值和最近更新时间。"),

                // 3. 信号变化历史
                AIFunctionFactory.Create(
                    ([Description("工位编号")] string stationCode,
                     [Description("最近N分钟，默认5")] int minutes) =>
                        GetSignalHistoryAsync(projectId, stationCode, minutes),
                    name: "GetSignalHistory",
                    description: "获取工位最近N分钟的信号变化记录（按时间倒序）。帮助判断哪些信号发生了变化、变化方向和时间。"),

                // 4. AST 求值
                AIFunctionFactory.Create(
                    ([Description("要求值的变量名")] string variableName,
                     [Description("工位编号")] string stationCode) =>
                        EvaluateVariableAsync(projectId, stationCode, variableName),
                    name: "EvaluateVariable",
                    description: "对指定变量触发 AST 运行时求值，返回当前值（TRUE/FALSE）和完整失败路径（哪些叶子条件为 FALSE）。这是诊断的核心工具。"),

                // 5. 工位拓扑
                AIFunctionFactory.Create(
                    () => GetStationTopologyAsync(projectId),
                    name: "GetStationTopology",
                    description: "获取全线工位列表及上下游拓扑关系（工位编号、启用状态、锚点数量）。用于理解产线布局和选择下一步要检查的工位。"),

                // 6. AST 控制路径
                AIFunctionFactory.Create(
                    ([Description("变量名")] string variableName) =>
                        GetAstExpressionAsync(projectId, variableName),
                    name: "GetAstExpression",
                    description: "获取变量的 AST 控制逻辑表达式（人类可读的自然语言格式），展示完整的条件→输出关系。用于理解某个信号是被什么条件控制的。"),

                // 7. 搜索信号
                AIFunctionFactory.Create(
                    ([Description("搜索关键词（信号名、注释、地址的一部分）")] string keyword,
                     [Description("可选：限定工位编号，为空则搜索全项目")] string stationCode) =>
                        SearchSignalsAsync(projectId, keyword, stationCode),
                    name: "SearchSignals",
                    description: "按关键词搜索信号（模糊匹配信号名、注释、地址）。当你不确定精确信号名时使用。"),

                // 8. 知识记忆
                AIFunctionFactory.Create(
                    ([Description("可选：限定工位编号")] string stationCode) =>
                        GetKnowledgeMemoryAsync(projectId, stationCode),
                    name: "GetKnowledgeMemory",
                    description: "查询该工位/项目的历史诊断记忆：过去的故障模式、已确认的根因、维护记录。帮助避免重复诊断已知问题。"),
            };
        }

        #endregion

        #region 工具实现

        /// <summary>工具1: 工位实时状态摘要</summary>
        private async Task<string> GetStationSummaryAsync(long projectId, string stationCode)
        {
            _logger.LogInformation("[DiagTool] GetStationSummary: {Station}", stationCode);
            var sb = new StringBuilder();

            try
            {
                // 当前信号镜像
                var signalDict = await _cacheService.GetSignalMirrorsAsync(projectId, stationCode);
                if (signalDict == null || signalDict.Count == 0)
                {
                    sb.AppendLine($"工位 {stationCode}: 无信号缓存数据（监控可能未启动）");
                }
                else
                {
                    var signalValues = signalDict.Values;
                    var trueCount = signalValues.Count(s => IsTrueValue(s.CurrentValue));
                    var falseCount = signalValues.Count(s => IsFalseValue(s.CurrentValue));
                    var otherCount = signalDict.Count - trueCount - falseCount;
                    sb.AppendLine($"## 工位 {stationCode} 状态摘要");
                    sb.AppendLine($"- 信号总数: {signalDict.Count}（TRUE={trueCount}, FALSE={falseCount}, 其它={otherCount}）");

                    // 最近变化的信号（前5个）
                    var recentChanged = signalValues
                        .Where(s => s.LastChangeTime.HasValue)
                        .OrderByDescending(s => s.LastChangeTime.Value)
                        .Take(5)
                        .ToList();

                    if (recentChanged.Count > 0)
                    {
                        sb.AppendLine("- 最近变化:");
                        foreach (var s in recentChanged)
                        {
                            var ago = (DateTime.Now - s.LastChangeTime.Value).TotalSeconds;
                            sb.AppendLine($"  - {s.VariableName ?? s.SignalKey} = {s.CurrentValue} ({ago:F0}秒前)");
                        }
                    }
                }

                // 锚点绑定
                var anchors = await _anchorBindingService.GetListAsync(it =>
                    it.IsDeleted == false && it.ProjectId == projectId && it.StationCode == stationCode);
                if (anchors.Count > 0)
                {
                    sb.AppendLine($"- Q点锚点: {anchors.Count}个");
                    foreach (var a in anchors.Take(10))
                    {
                        sb.AppendLine($"  - {a.AnchorKey}: {a.VariableName} ({a.AnchorType})");
                    }
                }

                // 最近变化记录数量
                var changes = await _cacheService.GetRecentChangesAsync(projectId, stationCode, 10);
                if (changes != null && changes.Count > 0)
                {
                    var oldest = changes.LastOrDefault()?.ChangedAt;
                    var newest = changes.FirstOrDefault()?.ChangedAt;
                    sb.AppendLine($"- 最近变化: {changes.Count}条记录，时间跨度 {oldest?.ToString("HH:mm:ss")} ~ {newest?.ToString("HH:mm:ss")}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"查询失败: {ex.Message}");
            }

            return Truncate(sb.ToString());
        }

        /// <summary>工具2: 单信号实时值</summary>
        private async Task<string> GetSignalValueAsync(long projectId, string stationCode, string signalName)
        {
            _logger.LogInformation("[DiagTool] GetSignalValue: {Station}/{Signal}", stationCode, signalName);

            try
            {
                var signalDict = await _cacheService.GetSignalMirrorsAsync(projectId, stationCode);
                if (signalDict == null || signalDict.Count == 0)
                    return $"工位 {stationCode} 无信号缓存数据";

                var signalValues = signalDict.Values;
                // 精确匹配优先，再模糊匹配
                var match = signalValues.FirstOrDefault(s =>
                    string.Equals(s.VariableName, signalName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.SignalKey, signalName, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                {
                    // 模糊匹配
                    match = signalValues.FirstOrDefault(s =>
                        (s.VariableName ?? "").Contains(signalName, StringComparison.OrdinalIgnoreCase)
                        || (s.SignalKey ?? "").Contains(signalName, StringComparison.OrdinalIgnoreCase));
                }

                if (match == null)
                    return $"未在工位 {stationCode} 中找到信号 '{signalName}'。" +
                           $"\n提示: 可使用 SearchSignals 工具按关键词搜索。";

                var sb = new StringBuilder();
                sb.AppendLine($"信号: {match.VariableName ?? match.SignalKey}");
                sb.AppendLine($"当前值: {match.CurrentValue}");
                if (match.LastChangeTime.HasValue)
                {
                    var ago = (DateTime.Now - match.LastChangeTime.Value).TotalSeconds;
                    sb.AppendLine($"最近更新: {match.LastChangeTime:HH:mm:ss}（{ago:F0}秒前）");
                }
                if (!string.IsNullOrEmpty(match.SemanticLabel))
                    sb.AppendLine($"语义标签: {match.SemanticLabel}");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"查询失败: {ex.Message}";
            }
        }

        /// <summary>工具3: 信号变化历史</summary>
        private async Task<string> GetSignalHistoryAsync(long projectId, string stationCode, int minutes)
        {
            _logger.LogInformation("[DiagTool] GetSignalHistory: {Station}, {Minutes}min", stationCode, minutes);
            minutes = Math.Clamp(minutes, 1, 30);

            try
            {
                var changes = await _cacheService.GetRecentChangesAsync(projectId, stationCode, 500);
                if (changes == null || changes.Count == 0)
                    return $"工位 {stationCode} 暂无信号变化记录（监控可能未启动）";

                // 策略：先尝试指定时间窗口，若无数据则自动回退到“最近变化”模式（避免停机很久后查不到任何数据）
                var windowStart = DateTime.Now.AddMinutes(-minutes);
                var filtered = changes
                    .Where(c => c.ChangedAt.HasValue && c.ChangedAt.Value >= windowStart)
                    .OrderByDescending(c => c.ChangedAt)
                    .ToList();

                string timeHint;
                if (filtered.Count == 0)
                {
                    // 时间窗口内无数据 → 回退到最近 100 条变化记录
                    filtered = changes
                        .Where(c => c.ChangedAt.HasValue)
                        .OrderByDescending(c => c.ChangedAt)
                        .Take(100)
                        .ToList();
                    if (filtered.Count == 0)
                        return $"工位 {stationCode} 无有效的信号变化记录";

                    var latestTime = filtered.First().ChangedAt.Value;
                    var elapsed = DateTime.Now - latestTime;
                    string elapsedStr;
                    if (elapsed.TotalSeconds < 60) elapsedStr = $"{elapsed.TotalSeconds:F0}秒前";
                    else if (elapsed.TotalMinutes < 60) elapsedStr = $"{elapsed.TotalMinutes:F0}分钟前";
                    else elapsedStr = $"{elapsed.TotalHours:F1}小时前";
                    timeHint = $"最近 {minutes} 分钟内无变化，以下是最近的历史变化记录（最后变化在 {elapsedStr}）";
                }
                else
                {
                    timeHint = $"最近 {minutes} 分钟变化记录";
                }

                var sb = new StringBuilder();
                sb.AppendLine($"## 工位 {stationCode} {timeHint}（{filtered.Count}条）");

                // 按变量分组统计
                var grouped = filtered.GroupBy(c =>
                    !string.IsNullOrWhiteSpace(c.VariableName) ? c.VariableName : c.SignalKey,
                    StringComparer.OrdinalIgnoreCase);

                sb.AppendLine($"涉及 {grouped.Count()} 个信号:");
                foreach (var grp in grouped.OrderByDescending(g => g.Count()).Take(20))
                {
                    var latest = grp.First();
                    sb.AppendLine($"- {grp.Key}: {grp.Count()}次变化，最新值={latest.NewValue}（{latest.ChangedAt:HH:mm:ss}）");

                    // 展示该变量最近3次变化
                    foreach (var c in grp.Take(3))
                    {
                        sb.AppendLine($"    {c.ChangedAt:HH:mm:ss} {c.OldValue} → {c.NewValue}");
                    }
                }

                return Truncate(sb.ToString());
            }
            catch (Exception ex)
            {
                return $"查询失败: {ex.Message}";
            }
        }

        /// <summary>工具4: AST 运行时求值</summary>
        private async Task<string> EvaluateVariableAsync(long projectId, string stationCode, string variableName)
        {
            _logger.LogInformation("[DiagTool] EvaluateVariable: {Station}/{Var}", stationCode, variableName);

            try
            {
                // 查找该变量的 AST
                var astRows = await _logicUnitAstService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == projectId
                    && it.StationCode == stationCode
                    && it.CompileStatus == "Compiled");

                // 找到输出该变量的 LogicUnit
                var targetAst = astRows?.FirstOrDefault(a =>
                    a.OutputVariablesJson != null
                    && a.OutputVariablesJson.Contains(variableName, StringComparison.OrdinalIgnoreCase));

                if (targetAst == null)
                    return $"未在工位 {stationCode} 中找到输出变量 '{variableName}' 的 AST。" +
                           $"\n提示: 变量名需精确匹配 AST 输出列表中的名称。";

                var evalResult = await _runtimeEvalService.EvaluateAsync(
                    new EvaluatePersistedAstRequestDto
                    {
                        ProjectId = projectId,
                        StationCode = stationCode,
                        LogicUnitId = targetAst.LogicUnitId,
                        VariableName = variableName,
                    });

                var sb = new StringBuilder();
                sb.AppendLine($"## 变量 {variableName} 求值结果");
                sb.AppendLine($"- 当前值: **{(evalResult.EvaluatedValue == true ? "TRUE" : evalResult.EvaluatedValue == false ? "FALSE" : "未知")}**");
                sb.AppendLine($"- 求值状态: {evalResult.EvaluationStatus}");
                sb.AppendLine($"- 诊断质量: {evalResult.DiagnosisQuality}");
                sb.AppendLine($"- 所属块: {evalResult.BlockName ?? targetAst.BlockName}");

                // 失败路径
                if (evalResult.FailurePath != null && evalResult.FailurePath.Count > 0)
                {
                    sb.AppendLine("- 失败路径（FALSE的条件链）:");
                    foreach (var node in evalResult.FailurePath.Take(10))
                    {
                        var valueStr = node.ActualValue != null ? $" = {node.ActualValue}" : "";
                        sb.AppendLine($"  - {node.VariableName ?? node.NodeType}: {node.Status}{valueStr}");
                    }
                }

                // GRAPH Transition 信息（如果有）
                if (evalResult.RootEvaluation?.Children != null)
                {
                    var transitions = evalResult.RootEvaluation.Children
                        .Where(c => string.Equals(c.NodeType, "GraphTransition", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (transitions.Count > 0)
                    {
                        var falseTrans = transitions.Count(t => string.Equals(t.Status, "False", StringComparison.OrdinalIgnoreCase));
                        sb.AppendLine($"- GRAPH 出口转移: {falseTrans}/{transitions.Count} 个未满足");
                        foreach (var t in transitions.Take(5))
                        {
                            sb.AppendLine($"  - {t.DisplayName ?? t.NodeId}: {t.Status}");
                        }
                    }
                }

                return Truncate(sb.ToString());
            }
            catch (Exception ex)
            {
                return $"求值失败: {ex.Message}";
            }
        }

        /// <summary>工具5: 工位拓扑</summary>
        private async Task<string> GetStationTopologyAsync(long projectId)
        {
            _logger.LogInformation("[DiagTool] GetStationTopology: Project={ProjectId}", projectId);

            try
            {
                var configs = await _shadowConfigService.GetListAsync(it =>
                    it.IsDeleted == false && it.ProjectId == projectId);

                if (configs == null || configs.Count == 0)
                    return "未找到任何监控工位配置";

                var sb = new StringBuilder();
                sb.AppendLine($"## 产线拓扑（{configs.Count}个工位）");

                var ordered = configs.OrderBy(c => c.StationCode, StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var cfg in ordered)
                {
                    var status = cfg.Enabled == true ? "✅启用" : "❌禁用";

                    // 锚点数
                    var anchors = await _anchorBindingService.GetListAsync(it =>
                        it.IsDeleted == false && it.ProjectId == projectId
                        && it.StationCode == cfg.StationCode);

                    sb.AppendLine($"- **{cfg.StationCode}** [{status}] 锚点={anchors.Count}");
                }

                return Truncate(sb.ToString());
            }
            catch (Exception ex)
            {
                return $"查询失败: {ex.Message}";
            }
        }

        /// <summary>工具6: AST 控制路径</summary>
        private async Task<string> GetAstExpressionAsync(long projectId, string variableName)
        {
            _logger.LogInformation("[DiagTool] GetAstExpression: {Var}", variableName);

            try
            {
                var markdown = await _astRenderer.RenderVariableContextMarkdownAsync(projectId, variableName);
                return Truncate(markdown);
            }
            catch (Exception ex)
            {
                return $"查询失败: {ex.Message}";
            }
        }

        /// <summary>工具7: 搜索信号</summary>
        private async Task<string> SearchSignalsAsync(long projectId, string keyword, string stationCode)
        {
            _logger.LogInformation("[DiagTool] SearchSignals: keyword={Keyword}, station={Station}", keyword, stationCode);

            try
            {
                if (string.IsNullOrWhiteSpace(keyword))
                    return "请提供搜索关键词";

                var sb = new StringBuilder();
                var keyword_lower = keyword.Trim().ToLowerInvariant();

                // 方案: 从 AST 的输出变量列表中搜索
                var query = await _logicUnitAstService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == projectId
                    && it.CompileStatus == "Compiled"
                    && it.OutputVariablesJson != null);

                if (!string.IsNullOrWhiteSpace(stationCode))
                {
                    query = query.Where(a =>
                        string.Equals(a.StationCode, stationCode.Trim(), StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                // 提取匹配的变量
                var matches = new List<(string Variable, string Block, string Station)>();
                foreach (var ast in query)
                {
                    try
                    {
                        var outputs = JsonSerializer.Deserialize<List<string>>(ast.OutputVariablesJson);
                        if (outputs == null) continue;

                        foreach (var output in outputs)
                        {
                            if (output.ToLowerInvariant().Contains(keyword_lower))
                            {
                                matches.Add((output, ast.BlockName, ast.StationCode));
                            }
                        }
                    }
                    catch { /* skip parse errors */ }
                }

                if (matches.Count == 0)
                {
                    // 尝试从信号缓存中搜索
                    if (!string.IsNullOrWhiteSpace(stationCode))
                    {
                        var signalDict = await _cacheService.GetSignalMirrorsAsync(projectId, stationCode);
                        if (signalDict != null)
                        {
                            var cacheMatches = signalDict.Values.Where(s =>
                                (s.VariableName ?? "").ToLowerInvariant().Contains(keyword_lower)
                                || (s.SignalKey ?? "").ToLowerInvariant().Contains(keyword_lower)
                                || (s.SemanticLabel ?? "").ToLowerInvariant().Contains(keyword_lower))
                                .Take(20)
                                .ToList();

                            if (cacheMatches.Count > 0)
                            {
                                sb.AppendLine($"从信号缓存中找到 {cacheMatches.Count} 个匹配:");
                                foreach (var m in cacheMatches)
                                {
                                    sb.AppendLine($"- {m.VariableName ?? m.SignalKey} = {m.CurrentValue}" +
                                        (!string.IsNullOrEmpty(m.SemanticLabel) ? $"（{m.SemanticLabel}）" : ""));
                                }
                                return sb.ToString();
                            }
                        }
                    }

                    return $"未找到匹配 '{keyword}' 的信号。请尝试更短的关键词或不同的工位。";
                }

                // 去重并展示
                var distinct = matches
                    .GroupBy(m => m.Variable, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Take(30)
                    .ToList();

                sb.AppendLine($"找到 {distinct.Count} 个匹配 '{keyword}' 的变量:");
                foreach (var m in distinct)
                {
                    sb.AppendLine($"- {m.Variable}（块={m.Block}, 工位={m.Station}）");
                }

                return Truncate(sb.ToString());
            }
            catch (Exception ex)
            {
                return $"搜索失败: {ex.Message}";
            }
        }

        /// <summary>工具8: 知识记忆</summary>
        private async Task<string> GetKnowledgeMemoryAsync(long projectId, string stationCode)
        {
            _logger.LogInformation("[DiagTool] GetKnowledgeMemory: station={Station}", stationCode);

            try
            {
                var stationCodes = string.IsNullOrWhiteSpace(stationCode)
                    ? null
                    : new List<string> { stationCode.Trim() };

                var memory = await _knowledgeMemory.LoadDiagnosisContextAsync(
                    projectId, stationCodes, maxChars: MaxOutputChars);

                if (string.IsNullOrWhiteSpace(memory))
                    return string.IsNullOrWhiteSpace(stationCode)
                        ? "该项目暂无诊断历史记忆"
                        : $"工位 {stationCode} 暂无诊断历史记忆";

                return Truncate(memory);
            }
            catch (Exception ex)
            {
                return $"查询失败: {ex.Message}";
            }
        }

        #endregion

        #region 辅助方法

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length <= MaxOutputChars) return text;
            return text.Substring(0, MaxOutputChars - 20) + "\n...[结果截断]";
        }

        private static bool IsTrueValue(object value)
        {
            if (value == null) return false;
            var s = value.ToString()?.Trim();
            return string.Equals(s, "True", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "1", StringComparison.Ordinal);
        }

        private static bool IsFalseValue(object value)
        {
            if (value == null) return false;
            var s = value.ToString()?.Trim();
            return string.Equals(s, "False", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "0", StringComparison.Ordinal);
        }

        #endregion
    }
}
