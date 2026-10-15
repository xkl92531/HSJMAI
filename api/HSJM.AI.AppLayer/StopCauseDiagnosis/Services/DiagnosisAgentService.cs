using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// Agent 驱动的停机诊断服务。
    /// 
    /// 取代固定扫 10/20 个锚点 → LLM 汇总的旧模式。
    /// 新模式：把 8 个诊断工具交给 LLM，让 LLM 通过 ReAct 循环自主探查：
    ///   1. 先看拓扑，了解产线布局
    ///   2. 逐工位查看状态摘要
    ///   3. 对异常信号做 AST 求值
    ///   4. 跨工位追溯根因
    ///   5. 给出结构化诊断报告
    /// </summary>
    public class DiagnosisAgentService : ISelfScopedAutoInject
    {
        private readonly ILogger<DiagnosisAgentService> _logger;
        private readonly LLMModelService _llmModelService;
        private readonly ReActExecutionEngine _reActEngine;
        private readonly DiagnosisToolProvider _toolProvider;
        private readonly KnowledgeMemoryService _knowledgeMemoryService;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public DiagnosisAgentService(
            ILogger<DiagnosisAgentService> logger,
            LLMModelService llmModelService,
            ReActExecutionEngine reActEngine,
            DiagnosisToolProvider toolProvider,
            KnowledgeMemoryService knowledgeMemoryService)
        {
            _logger = logger;
            _llmModelService = llmModelService;
            _reActEngine = reActEngine;
            _toolProvider = toolProvider;
            _knowledgeMemoryService = knowledgeMemoryService;
        }

        /// <summary>
        /// Agent 驱动的全线停机诊断。
        /// 
        /// 输入：已完成的各工位 AST 诊断摘要（代码层面已做完）。
        /// 改进：LLM 不再只读静态摘要，而是可以主动调用工具追查任何可疑信号。
        /// </summary>
        /// <param name="projectId">项目 ID</param>
        /// <param name="summaries">各工位 AST 诊断摘要（由 DiagnoseStationForLineAsync 生成）</param>
        /// <param name="maxIterations">ReAct 最大迭代次数</param>
        /// <param name="onStep">步骤回调（可选，用于 SignalR 推送）</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<LineDiagnosisLlmReport> AnalyzeWithAgentAsync(
            long projectId,
            List<StationDiagnosisSummaryDto> summaries,
            string constraintReport = null,
            int maxIterations = 12,
            Func<ReActStep, Task> onStep = null,
            CancellationToken cancellationToken = default,
            string outputLanguage = "zh-CN")
        {
            outputLanguage = LineDiagnosisLlmService.NormalizeOutputLanguage(outputLanguage);
            var isEnglishOutput = LineDiagnosisLlmService.IsEnglishOutput(outputLanguage);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation(
                "[DiagAgent] 开始 Agent 诊断: ProjectId={ProjectId}, 工位数={Count}, 异常={Abnormal}",
                projectId, summaries.Count,
                summaries.Count(s => s.OverallStatus == "SomeFalse"));

            try
            {
                // 1. 构建诊断 Prompt（将摘要喂给 LLM 作为起始上下文）
                var systemPrompt = BuildDiagnosisSystemPrompt(outputLanguage);
                var taskDescription = BuildTaskDescription(summaries, constraintReport, outputLanguage);

                // 2. 获取 LLM ChatClient（使用推荐模型）
                var roleId = AppsettingsConfig.GetDiagnosticAgentRoleId();
                var recommendedModel = await _llmModelService.RecommendModelAsync();
                if (recommendedModel == null)
                    throw new InvalidOperationException("无可用的 LLM 模型");
                var chatClient = (await _llmModelService.CreateChatClientByModelIdAsync(recommendedModel.Id)).AsIChatClient();

                // 3. 构建工具列表
                var tools = _toolProvider.BuildTools(projectId);

                // 4. 执行 ReAct 循环
                var config = new ReActExecutionConfig
                {
                    SessionId = $"diag-{projectId}-{DateTime.UtcNow.Ticks}",
                    RoleId = roleId,
                    TaskDescription = taskDescription,
                    SystemPrompt = systemPrompt,
                    Tools = tools,
                    MaxIterations = maxIterations,
                    EnableReflection = true,
                    OnStepComplete = onStep,
                    CallLimits = new ToolCallLimits
                    {
                        MaxCallsPerTool = 8,
                        MaxTotalCalls = 30,
                        MaxDuplicateCalls = 2,
                        DuplicateWarningThreshold = 1
                    }
                };

                var result = await _reActEngine.ExecuteAsync(chatClient, config, cancellationToken);

                sw.Stop();

                _logger.LogInformation(
                    "[DiagAgent] ReAct 完成: Success={Success}, Iterations={Iter}, FinalAnswer长度={Len}",
                    result.Success, result.TotalIterations,
                    result.FinalAnswer?.Length ?? 0);
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    _logger.LogWarning("[DiagAgent] ReAct ErrorMessage: {Err}", result.ErrorMessage);

                // 5. 解析 LLM 输出
                var report = ParseAgentResult(result, outputLanguage);
                report.ExecutionTimeMs = sw.ElapsedMilliseconds;
                report.AgentIterations = result.TotalIterations;
                report.ToolCallSummary = FormatToolCallSummary(result.ToolCallSummary, outputLanguage);
                LineDiagnosisLlmService.ApplyEvidenceArbitration(
                    report,
                    LineDiagnosisLlmService.BuildEvidenceProfiles(summaries, constraintReport),
                    outputLanguage);

                _logger.LogInformation(
                    "[DiagAgent] 完成: RootCause={Station}, Confidence={Conf}, Iterations={Iter}, Time={Time}ms",
                    report.RootCauseStation, report.Confidence,
                    result.TotalIterations, sw.ElapsedMilliseconds);

                // 6. 保存诊断记忆（高置信度时）
                if (report.Success && report.Confidence >= 0.7)
                {
                    _ = SaveDiagnosisMemoryAsync(projectId, report);
                }

                return report;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _logger.LogWarning("[DiagAgent] 被取消");
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "[DiagAgent] 失败");
                return new LineDiagnosisLlmReport
                {
                    Success = false,
                    RootCauseDescription = isEnglishOutput
                        ? $"Agent diagnosis failed: {ex.Message}"
                        : $"Agent 诊断失败: {ex.Message}",
                    ExecutionTimeMs = sw.ElapsedMilliseconds,
                };
            }
        }

        #region Prompt 构建

        private static string BuildDiagnosisSystemPrompt(string outputLanguage = "zh-CN")
        {
            var normalizedLang = LineDiagnosisLlmService.NormalizeOutputLanguage(outputLanguage);
            var isEnglishOutput = LineDiagnosisLlmService.IsEnglishOutput(outputLanguage);
            var languageInstruction = isEnglishOutput
                ? @"
## Output language

- The final JSON field names and enum values must stay exactly the same.
- Write all user-facing JSON values in English: rootCauseDescription, impactChain, stationAnalysis.summary, secondaryCauses.description, suggestedActions and reasoning.
- Keep PLC station codes, variable names, DB names, IO addresses, block names and enum values unchanged.
- Chinese PLC comments may be quoted only as original signal comments; explain the diagnosis itself in English.

English JSON style example:
{
  ""rootCauseStation"": ""OP030"",
  ""rootCauseDescription"": ""AI diagnosis confirms a real fault at OP030: cylinder CYL08 extended-position feedback OP030_CYL08_W did not turn TRUE after the output command. Check the sensor, wiring, IO module and cylinder mechanism."",
  ""confidence"": 0.85,
  ""impactChain"": ""Root cause: OP030 -> Impacted: [OP010,OP020,OP040,OP050]"",
  ""stationAnalysis"": [
    { ""stationCode"": ""OP030"", ""role"": ""RootCause"", ""summary"": ""Cylinder CYL08 did not reach the extended position; this is the strongest local physical evidence."" },
    { ""stationCode"": ""OP040"", ""role"": ""Impacted"", ""summary"": ""Stopped because it is waiting for OP030 to recover."" }
  ],
  ""suggestedActions"": [""Inspect OP030 CYL08 sensor, wiring and IO module.""],
  ""reasoning"": ""Prioritized direct actuator feedback conflicts over process handshake waiting signals, then used cross-station waiting evidence to determine the impact range.""
}
"
                : normalizedLang == LineDiagnosisI18n.ZhCn
                ? @"
## 输出语言

- 最终 JSON 字段名和枚举值保持不变。
- 面向用户的结论、工位摘要、次因、建议动作和推理过程使用中文。
- PLC 工位号、变量名、DB 名、IO 地址、逻辑块名和枚举值保持原样。
"
                : $@"
## Output language

- The final JSON field names and enum values must stay exactly the same.
- All user-facing values (rootCauseDescription, impactChain, stationAnalysis.summary, secondaryCauses.description, suggestedActions, reasoning) must be written in {LineDiagnosisI18n.LanguageName(normalizedLang)}.
- Keep PLC station codes, variable names, DB names, IO addresses, block names and enum values unchanged.
- Original Chinese PLC signal comments may be quoted as-is; explain the conclusion itself in {LineDiagnosisI18n.LanguageName(normalizedLang)}.
";

            return @"你是一个工业产线停机诊断 AI 专家。你的任务是找到停机的根本原因。

## 你的工作方法

1. **先看全局**：使用 GetStationTopology 了解产线布局
2. **定位异常**：从下面提供的 AST 诊断摘要中识别异常工位
3. **深入调查**：对可疑工位使用 GetStationSummary 查看详细状态
4. **追根溯源**：
   - 使用 EvaluateVariable 对关键信号做 AST 求值，找到条件链中哪个环节是 FALSE
   - 使用 GetSignalHistory 查看信号变化时序，判断因果关系
   - 使用 GetAstExpression 查看信号的控制逻辑，理解谁在控制谁
5. **跨工位追溯**：如果故障标记为 'WaitingFor:XXX'，说明本工位在等待其它工位，要跨过去查。注意：**故障传播方向不固定**，根因可能在产线中间、末端甚至辅助工位，不要假设故障一定从前往后传播。
6. **查阅记忆**：使用 GetKnowledgeMemory 查看是否有已知的故障模式

## 输出格式

最终结论**必须**输出一个 JSON 对象，结构如下：
{
  ""rootCauseStation"": ""OP030"",
  ""rootCauseDescription"": ""具体是什么信号/设备导致停机"",
  ""confidence"": 0.85,
  ""impactChain"": ""根因:OP030 → 受影响:[OP010,OP020,OP040,OP050]"",
  ""stationAnalysis"": [
    { ""stationCode"": ""OP030"", ""role"": ""RootCause"", ""summary"": ""该工位内部故障描述"" },
    { ""stationCode"": ""OP010"", ""role"": ""Impacted"", ""summary"": ""因OP030故障停机，等待OP030恢复"" },
    { ""stationCode"": ""OP050"", ""role"": ""Impacted"", ""summary"": ""因OP030故障停机，等待OP030恢复"" }
  ],
  ""suggestedActions"": [""检查..."", ""确认...""],
  ""reasoning"": ""完整推理过程""
}

字段说明：
- rootCauseStation：根因工位编号
- rootCauseDescription：具体是什么信号/设备导致停机
- confidence：0.0~1.0，基于证据充分程度
- impactChain：辐射式故障传播描述，格式为 ""根因:XXX → 受影响:[YYY,ZZZ,...]""。产线中**任意一个工位故障都可导致整线停机**，传播是辐射式的而非线性的
- stationAnalysis：每个相关工位的角色（RootCause/Impacted/Normal）和摘要
- suggestedActions：维修人员应该检查的具体动作列表
- reasoning：完整推理过程

## 注意事项

- 不要猜测，每个结论都要有工具查询的证据支撑
- FALSE 的信号不一定是故障，可能是正常等待（按需工位、等待上游物料）
- 注意区分 InternalFault（内部故障）和 WaitingFor（等待其它工位）——WaitingFor 只说明该工位在等别人，不代表一定是""上游""故障
- 变化最近的信号更可能是故障触发点
- GRAPH 块中 Transition 未满足表示该步骤在等待条件，需查看条件叶子
- 流程/握手类状态信号（Reach/Pick/Drop/完成/允许/就绪/同步等）为 False 时，不要直接当根因；先看它是否有明确 L1/L2 近端追踪、明确报警、通信中断或物理反馈矛盾。
- 若结构化证据画像显示某工位有直接执行机构卡滞（气缸/阀/电机输出与到位反馈矛盾，或 GRAPH 转换卡在物理反馈叶子），而另一工位主要是流程状态未满足，要优先解释二者近端追踪和证据强弱，再判定根因。
- IO 约束检测命中的互斥/蕴含/全满足异常通常是真实故障候选；如果约束报告包含 I点地址（%Ixx.x），最终 rootCauseDescription、stationAnalysis.summary、suggestedActions 必须输出对应信号名和地址。
- IO 约束报告只是内部证据来源。最终面向用户的 rootCauseDescription、impactChain、stationAnalysis、secondaryCauses、suggestedActions 中不要使用“IO约束诊断确认”“约束诊断服务确认”“IO约束故障”等内部来源表述。若该证据支持故障结论，请写成“AI诊断确认存在故障”，再描述具体信号、I点地址和物理故障现象。

## 迭代预算管理

- 你的迭代次数是**有限的**（通常 12 轮），必须高效利用每一轮
- 前 3 轮：快速定位异常工位和关键信号，不要在正常工位上浪费轮次
- 中间轮次：聚焦最可疑的 1-2 个工位深入追查
- 最后 2 轮（系统会提醒你）：**必须停止工具调用，直接输出最终结论**
- 如果已收集足够证据，随时可以提前输出结论，不需要用完全部轮次" + languageInstruction;
        }

        /// <summary>
        /// 将 AST 诊断摘要转为任务描述。
        /// 这是 LLM 的起始上下文，不需要太详细（LLM 可自己查询补充）。
        /// </summary>
        private static string BuildTaskDescription(
            List<StationDiagnosisSummaryDto> summaries,
            string constraintReport = null,
            string outputLanguage = "zh-CN")
        {
            var isEnglishOutput = LineDiagnosisLlmService.IsEnglishOutput(outputLanguage);
            var sb = new StringBuilder();
            sb.AppendLine("# 停机诊断任务");
            if (isEnglishOutput)
            {
                sb.AppendLine();
                sb.AppendLine("Language requirement: final user-facing JSON values must be English. Keep station codes, variable names and IO addresses unchanged.");
            }
            sb.AppendLine();

            var abnormal = summaries.Where(s => s.OverallStatus == "SomeFalse").ToList();
            var running = summaries.Where(s => s.OverallStatus == "Running").ToList();
            var allTrue = summaries.Where(s => s.OverallStatus == "AllTrue").ToList();

            sb.AppendLine($"全线 {summaries.Count} 个工位，其中 {abnormal.Count} 个异常, {running.Count} 个运转中, {allTrue.Count} 个就绪。");
            sb.AppendLine();

            LineDiagnosisLlmService.AppendEvidenceProfileSection(
                sb,
                LineDiagnosisLlmService.BuildEvidenceProfiles(summaries, constraintReport));
            ValveAlarmPromptFormatter.AppendSection(sb, summaries);

            if (!string.IsNullOrWhiteSpace(constraintReport))
            {
                sb.AppendLine("## IO 约束证据报告（内部参考）");
                sb.AppendLine("以下约束结果来自实时 IO 约束检测。若包含 I点地址，请在最终结论中明确输出信号名和地址。");
                sb.AppendLine("约束违反说明该工位确有异常，但不等于它是停机源头：先按工位间等待关系确定阻塞源头工位，等待其它工位的工位上的约束违反按连带结果处理。");
                sb.AppendLine("注意：不要把“IO约束诊断确认”作为最终结论开头；面向用户请统一写成“AI诊断确认存在故障”，再说明具体信号、点位和故障现象。");
                sb.AppendLine(constraintReport);
                sb.AppendLine();
            }

            // 异常工位 — 给出关键发现摘要（不给完整数据，让 LLM 自己查）
            if (abnormal.Count > 0)
            {
                sb.AppendLine("## 异常工位摘要");
                foreach (var s in abnormal.OrderBy(x => x.StationOrder))
                {
                    sb.AppendLine($"### {s.StationCode}（策略: {s.DiagnosisStrategy}, 耗时: {s.ElapsedMs}ms）");
                    sb.AppendLine($"- 求值 {s.EvaluatedCount} 个锚点，{s.FalseAnchorCount} 个为 FALSE");

                    // 模板匹配信息
                    if (!string.IsNullOrWhiteSpace(s.TemplateMatchInfo))
                        sb.AppendLine($"- 📋 {s.TemplateMatchInfo}");

                    if (s.KeyFindings.Count > 0)
                    {
                        // 故障归属统计概览
                        var internalCount = s.KeyFindings.Count(f => f.FaultOwnership == "InternalFault" || string.IsNullOrEmpty(f.FaultOwnership));
                        var waitingCount = s.KeyFindings.Count(f => !string.IsNullOrEmpty(f.FaultOwnership) && f.FaultOwnership.StartsWith("WaitingFor:"));
                        var externalCount = s.KeyFindings.Count(f => f.FaultOwnership == "ExternalInput");
                        sb.AppendLine($"- 发现分布: 🔴内部:{internalCount} 🟡等待:{waitingCount} ⚪外部:{externalCount}");

                        // 功能块分布（findings>10时显示，帮助Agent理解多通道场景）
                        if (s.KeyFindings.Count > 10)
                        {
                            var blockGroups = s.KeyFindings
                                .Where(f => !string.IsNullOrWhiteSpace(f.BlockName))
                                .GroupBy(f => f.BlockName, StringComparer.OrdinalIgnoreCase)
                                .OrderByDescending(g => g.Count())
                                .Take(5)
                                .Select(g => $"{g.Key}({g.Count()})");
                            sb.AppendLine($"- 功能块分布: {string.Join(", ", blockGroups)}");
                        }

                        // 只给前 3 个关键发现的简要信息
                        var topFindings = s.KeyFindings
                            .Where(f => f.EvaluatedValue == false || f.FaultOwnership != null)
                            .Take(3);
                        foreach (var f in topFindings)
                        {
                            var ownership = !string.IsNullOrEmpty(f.FaultOwnership)
                                ? $"[{f.FaultOwnership}]" : "";
                            sb.AppendLine($"  - {f.VariableName} = {(f.EvaluatedValue == false ? "FALSE" : "TRUE")} {ownership}");
                            if (!string.IsNullOrEmpty(f.FailurePathSummary))
                                sb.AppendLine($"    路径: {Truncate(f.FailurePathSummary, 120)}");
                        }

                        if (s.KeyFindings.Count > 3)
                        {
                            sb.AppendLine($"  - ...还有 {s.KeyFindings.Count - 3} 个发现，可使用 GetStationSummary(\"{s.StationCode}\") 查看详情");
                        }
                    }
                    sb.AppendLine();
                }
            }

            // 正常/就绪工位只列名
            if (running.Count > 0)
                sb.AppendLine($"运转中工位: {string.Join(", ", running.Select(s => s.StationCode))}");
            if (allTrue.Count > 0)
                sb.AppendLine($"就绪工位: {string.Join(", ", allTrue.Select(s => s.StationCode))}");

            sb.AppendLine();
            sb.AppendLine(isEnglishOutput
                ? "Use the tools to investigate abnormal stations and identify the root cause. Pay special attention to signals marked with FaultOwnership=InternalFault."
                : "请使用工具深入调查异常工位，找到根因。尤其关注 FaultOwnership 标记为 InternalFault 的信号。");

            return sb.ToString();
        }

        #endregion

        #region 结果解析

        private static LineDiagnosisLlmReport ParseAgentResult(
            ReActExecutionResult result,
            string outputLanguage = "zh-CN")
        {
            var isEnglishOutput = LineDiagnosisLlmService.IsEnglishOutput(outputLanguage);
            if (!result.Success || string.IsNullOrWhiteSpace(result.FinalAnswer))
            {
                return new LineDiagnosisLlmReport
                {
                    Success = false,
                    RootCauseDescription = result.ErrorMessage
                        ?? (isEnglishOutput ? "The agent did not produce a valid conclusion." : "Agent 未输出有效结论"),
                    RawResponse = result.FinalAnswer,
                };
            }

            var report = new LineDiagnosisLlmReport
            {
                Success = true,
                RawResponse = result.FinalAnswer,
            };

            // 尝试 JSON 解析
            try
            {
                var text = result.FinalAnswer;
                // 去掉 ```json ... ``` 包裹
                text = StripMarkdownCodeFence(text);
                // 尝试提取 JSON 块
                var jsonStart = text.IndexOf('{');
                var jsonEnd = text.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonStr = text.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var parsed = JsonSerializer.Deserialize<AgentDiagnosisOutput>(jsonStr, JsonOptions);
                    if (parsed != null)
                    {
                        report.RootCauseStation = parsed.RootCauseStation;
                        report.RootCauseDescription = parsed.RootCauseDescription;
                        report.Confidence = Math.Clamp(parsed.Confidence, 0, 1);
                        report.ImpactChain = parsed.ImpactChain;
                        report.SuggestedActions = parsed.SuggestedActions ?? new List<string>();
                        report.Reasoning = parsed.Reasoning;
                        if (parsed.StationAnalysis?.Count > 0)
                        {
                            report.StationAnalysis = parsed.StationAnalysis.Select(sa => new LineDiagnosisStationAnalysis
                            {
                                StationCode = sa.StationCode,
                                Role = sa.Role,
                                Summary = sa.Summary,
                            }).ToList();
                        }
                        NormalizePublicEvidenceSourceLanguage(report);
                        return report;
                    }
                }
            }
            catch { /* JSON解析失败，降级为文本提取 */ }

            // 降级：从文本中提取
            report.RootCauseDescription = NormalizePublicEvidenceSourceLanguage(result.FinalAnswer);
            report.Confidence = 0.5; // 无结构化输出时置信度降级
            return report;
        }

        private static void NormalizePublicEvidenceSourceLanguage(LineDiagnosisLlmReport report)
        {
            if (report == null)
                return;

            report.RootCauseDescription = NormalizePublicEvidenceSourceLanguage(report.RootCauseDescription);
            report.ImpactChain = NormalizePublicEvidenceSourceLanguage(report.ImpactChain);
            report.Reasoning = NormalizePublicEvidenceSourceLanguage(report.Reasoning);

            foreach (var station in report.StationAnalysis)
                station.Summary = NormalizePublicEvidenceSourceLanguage(station.Summary);

            foreach (var cause in report.SecondaryCauses)
                cause.Description = NormalizePublicEvidenceSourceLanguage(cause.Description);

            for (var i = 0; i < report.SuggestedActions.Count; i++)
                report.SuggestedActions[i] = NormalizePublicEvidenceSourceLanguage(report.SuggestedActions[i]);
        }

        private static string NormalizePublicEvidenceSourceLanguage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            return text
                .Replace("IO约束诊断确认存在严重故障", "AI诊断确认存在故障", StringComparison.OrdinalIgnoreCase)
                .Replace("IO约束诊断确认存在故障", "AI诊断确认存在故障", StringComparison.OrdinalIgnoreCase)
                .Replace("IO约束诊断确认存在", "AI诊断确认存在", StringComparison.OrdinalIgnoreCase)
                .Replace("IO 约束诊断确认存在严重故障", "AI诊断确认存在故障", StringComparison.OrdinalIgnoreCase)
                .Replace("IO 约束诊断确认存在故障", "AI诊断确认存在故障", StringComparison.OrdinalIgnoreCase)
                .Replace("IO 约束诊断确认存在", "AI诊断确认存在", StringComparison.OrdinalIgnoreCase)
                .Replace("约束诊断服务确认", "AI诊断确认", StringComparison.OrdinalIgnoreCase)
                .Replace("能量流/约束诊断服务确认", "AI诊断确认", StringComparison.OrdinalIgnoreCase)
                .Replace("(IO约束故障)", "(AI诊断IO故障)", StringComparison.OrdinalIgnoreCase)
                .Replace("（IO约束故障）", "（AI诊断IO故障）", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatToolCallSummary(
            ToolCallTrackerSummary summary,
            string outputLanguage = "zh-CN")
        {
            if (summary == null) return null;
            var isEnglishOutput = LineDiagnosisLlmService.IsEnglishOutput(outputLanguage);
            var sb = new StringBuilder();
            sb.AppendLine(isEnglishOutput
                ? $"Tool calls: {summary.TotalCalls}, duration: {summary.Duration.TotalSeconds:F1}s"
                : $"工具调用总计: {summary.TotalCalls}次, 耗时: {summary.Duration.TotalSeconds:F1}s");
            if (summary.ToolCallCounts != null)
            {
                foreach (var kv in summary.ToolCallCounts.OrderByDescending(x => x.Value))
                {
                    sb.AppendLine(isEnglishOutput
                        ? $"  - {kv.Key}: {kv.Value} calls"
                        : $"  - {kv.Key}: {kv.Value}次");
                }
            }
            if (summary.DuplicateCallCounts != null && summary.DuplicateCallCounts.Count > 0)
            {
                sb.AppendLine(isEnglishOutput
                    ? $"Duplicate calls: {string.Join(", ", summary.DuplicateCallCounts.Select(kv => $"{kv.Key}={kv.Value}"))}"
                    : $"重复调用: {string.Join(", ", summary.DuplicateCallCounts.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
            return sb.ToString();
        }

        #endregion

        #region 记忆保存

        private async Task SaveDiagnosisMemoryAsync(long projectId, LineDiagnosisLlmReport report)
        {
            try
            {
                var stationCodes = !string.IsNullOrEmpty(report.RootCauseStation)
                    ? new List<string> { report.RootCauseStation }
                    : null;

                var content = $"[Agent诊断] 根因工位: {report.RootCauseStation}, " +
                    $"置信度: {report.Confidence:F2}, " +
                    $"描述: {report.RootCauseDescription}, " +
                    $"影响链: {report.ImpactChain}";

                await _knowledgeMemoryService.SaveDiagnosisMemoryAsync(
                    projectId, report.RootCauseStation,
                    $"Agent诊断: {report.RootCauseStation}", content);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DiagAgent] 保存记忆失败");
            }
        }

        #endregion

        #region 辅助

        /// <summary>
        /// 去除 LLM 输出中的 Markdown 代码围栏（```json ... ```），
        /// 保留纯 JSON 文本供反序列化。
        /// </summary>
        private static string StripMarkdownCodeFence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            // 匹配 ```json 或 ``` 开头
            var idx = text.IndexOf("```", StringComparison.Ordinal);
            if (idx < 0) return text;
            // 找到开头 ``` 后的换行
            var lineEnd = text.IndexOf('\n', idx);
            if (lineEnd < 0) return text;
            var inner = text.Substring(lineEnd + 1);
            // 找到结尾 ```
            var closeIdx = inner.LastIndexOf("```", StringComparison.Ordinal);
            if (closeIdx >= 0)
                inner = inner.Substring(0, closeIdx);
            return inner.Trim();
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen) return text;
            return text.Substring(0, maxLen - 3) + "...";
        }

        /// <summary>Agent 诊断输出的 JSON 结构</summary>
        private class AgentDiagnosisOutput
        {
            [JsonPropertyName("rootCauseStation")]
            public string RootCauseStation { get; set; }

            [JsonPropertyName("rootCauseDescription")]
            public string RootCauseDescription { get; set; }

            [JsonPropertyName("confidence")]
            public double Confidence { get; set; }

            [JsonPropertyName("impactChain")]
            public string ImpactChain { get; set; }

            [JsonPropertyName("stationAnalysis")]
            public List<AgentStationAnalysis> StationAnalysis { get; set; }

            [JsonPropertyName("suggestedActions")]
            public List<string> SuggestedActions { get; set; }

            [JsonPropertyName("reasoning")]
            public string Reasoning { get; set; }
        }

        private class AgentStationAnalysis
        {
            [JsonPropertyName("stationCode")]
            public string StationCode { get; set; }

            [JsonPropertyName("role")]
            public string Role { get; set; }

            [JsonPropertyName("summary")]
            public string Summary { get; set; }
        }

        #endregion
    }
}
