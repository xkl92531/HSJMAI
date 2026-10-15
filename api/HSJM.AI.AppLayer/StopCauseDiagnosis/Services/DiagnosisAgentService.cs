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
            cancellationToken.ThrowIfCancellationRequested();
            summaries = (summaries ?? new()).Where(s => s != null).ToList();
            outputLanguage = LineDiagnosisLlmService.NormalizeOutputLanguage(outputLanguage);
            var isEnglishOutput = LineDiagnosisLlmService.IsEnglishOutput(outputLanguage);
            if (summaries.Count == 0)
                return LineDiagnosisLlmService.BuildEvidenceOnlyReport(summaries, constraintReport, outputLanguage);
            string modelContent = null;
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
                    OnStepComplete = CreatePublicProgressCallback(onStep, outputLanguage),
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
                cancellationToken.ThrowIfCancellationRequested();
                modelContent = result.FinalAnswer;
                if (!result.Success || string.IsNullOrWhiteSpace(modelContent))
                    _logger.LogWarning("[DiagAgent] 模型不可用，原始结果仅作审计: {Response}", modelContent);

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
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogError(ex, "[DiagAgent] 失败，原始结果仅作审计: {Response}", modelContent);
                return LineDiagnosisLlmService.BuildEvidenceOnlyReport(summaries, constraintReport, outputLanguage, sw.ElapsedMilliseconds);
            }
        }

        internal static Func<ReActStep, Task> CreatePublicProgressCallback(
            Func<ReActStep, Task> onStep, string outputLanguage = "zh-CN")
        {
            if (onStep == null) return null;
            var language = LineDiagnosisI18n.Normalize(outputLanguage);
            string T(string cn, string en, string tw, string ja) => language switch
            {
                "en-US" => en, "zh-TW" => tw, "ja-JP" => ja, _ => cn
            };
            return step =>
            {
                // The engine retains this original object in ExecutionHistory and reads Complete.Content
                // AFTER the callback. Publish a detached projection, never mutate the engine/audit input.
                var status = !step.Success
                    ? T("诊断步骤执行失败，详情见服务端日志。", "Diagnosis step failed; see server logs for details.",
                        "診斷步驟執行失敗，詳情見伺服器記錄。", "診断ステップに失敗しました。詳細はサーバーログを確認してください。")
                    : step.Decision == ReActStepDecision.Complete
                        ? T("分析步骤已完成，正在核验结论证据。", "Analysis step completed; diagnosis evidence is being verified.",
                            "分析步驟已完成，正在核驗結論證據。", "分析ステップが完了しました。診断の証拠を検証しています。")
                        : step.WasIntercepted
                            ? T("工具调用受到执行限制，正在调整检查。", "Tool call was limited; checks are being adjusted.",
                                "工具呼叫受到執行限制，正在調整檢查。", "ツール呼び出しが制限されました。確認手順を調整しています。")
                            : step.Phase switch
                            {
                                ReActPhase.Action or ReActPhase.Observation => T("工具检查步骤已完成，结果待证据核验。",
                                    "Tool check completed; results await evidence verification.", "工具檢查步驟已完成，結果待證據核驗。", "ツール確認が完了しました。結果の証拠を検証します。"),
                                ReActPhase.Reflection => T("正在复核诊断证据。", "Reviewing diagnosis evidence.",
                                    "正在複核診斷證據。", "診断の証拠を再確認しています。"),
                                _ => T("正在分析诊断证据。", "Analyzing diagnosis evidence.",
                                    "正在分析診斷證據。", "診断の証拠を分析しています。")
                            };
                return onStep(new ReActStep
                {
                    Phase = step.Phase, Decision = step.Decision, Iteration = step.Iteration,
                    Timestamp = step.Timestamp, DurationMs = step.DurationMs, Success = step.Success,
                    WasIntercepted = step.WasIntercepted, Content = status,
                    ErrorMessage = string.IsNullOrWhiteSpace(step.ErrorMessage) ? null
                        : T("步骤错误详情见服务端日志。", "See server logs for step error details.",
                            "步驟錯誤詳情見伺服器記錄。", "ステップのエラー詳細はサーバーログを確認してください。"),
                    InterceptReason = string.IsNullOrWhiteSpace(step.InterceptReason) ? null
                        : T("执行限制，详情见服务端日志。", "Execution limited; see server logs.",
                            "執行限制，詳情見伺服器記錄。", "実行が制限されました。サーバーログを確認してください。"),
                    ToolCall = step.ToolCall == null ? null : new ReActToolCall
                    {
                        ToolName = step.ToolCall.ToolName, CallId = step.ToolCall.CallId,
                        // Arguments and tool outputs can contain model-written fault prose too.
                        Arguments = null
                    }
                });
            };
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
  ""rootCauseStation"": null,
  ""rootCauseDescription"": ""Insufficient independent fault evidence. A command with unmet feedback is a condition to investigate; it does not by itself confirm a fault or communication loss."",
  ""confidence"": 0,
  ""impactChain"": ""Causal relationships between stations are unconfirmed."",
  ""stationAnalysis"": [
    { ""stationCode"": ""OP030"", ""role"": ""Unknown"", ""summary"": ""Feedback condition is unmet; root cause is unconfirmed."" }
  ],
  ""secondaryCauses"": [],
  ""suggestedActions"": [""Verify current PLC alarms, commands and complete feedback conditions with read quality and timestamps.""],
  ""reasoning"": ""Only a Good active PLC alarm or a confirmed constraint violation supports a fault candidate. A latched alarm does not establish current mechanical damage.""
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
- InternalFault 仅为归属标签，不证明故障；WaitingFor 只说明工位在等待，不证明另一工位发生故障。
- False、Unknown、GRAPH execbits 活动位及 FailurePathSummary 都不能独立证明故障。仅使用 Good 且激活的 PLC 报警或标有“判定: 确认违规”的 IO 规则作为故障候选；报警可能锁存，不等于机械部件已损坏。
- 缺少独立证据时 rootCauseStation 必须为 null。rootCauseDescription、reasoning、stationAnalysis、secondaryCauses、suggestedActions 都不得重新引入未经证实的故障或通信中断断言。
- 只有实际为 False 的 WaitingFor 边直接或多跳到独立故障候选，才能将沿链的纯等待工位标为 Impacted；多候选时 rootCauseStation=null，仍保留每个候选的已证明等待链，同一等待站通向多个候选必须明确无法唯一归属。Unknown、循环、跨项目同名歧义或自身独立故障不得简单标为连带，禁止按工位编号猜传播。
- 变化最近的信号更可能是故障触发点
- GRAPH 块中 Transition 未满足表示该步骤在等待条件，需查看条件叶子
- Reach/Pick/Drop/完成/允许/就绪/同步等状态为 False，及 L1/L2 近端追踪、物理反馈等待都只帮助定位待核实条件，不独立支持根因。
- GRAPH 转换等待物理反馈不能直接叫作执行机构卡滞。仅在独立报警/确认违规存在时列候选，并区分当前状态、锁存报警和待确认根因。
- IO 条目必须标有“判定: 确认违规”才能作为候选；保留已读取的信号名及地址，无法判定/未采到不能作为故障点。
- IO 约束报告只是内部证据来源。最终面向用户的 rootCauseDescription、impactChain、stationAnalysis、secondaryCauses、suggestedActions 中不要使用“IO约束诊断确认”“约束诊断服务确认”“IO约束故障”等内部来源表述。只描述已确认信号和报警/违规事实，明确根因待确认，不写“AI诊断确认存在故障”或推断机械损坏。

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
                sb.AppendLine("仅标有‘判定: 确认违规’的条目支持独立故障候选。保留具体信号和值，注明报警可能锁存；未采到的信号不能补成 true/false，也不能直接推断机械损坏。");
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
                            var value = f.EvaluatedValue switch { true => "TRUE", false => "FALSE", _ => "UNKNOWN" };
                            sb.AppendLine($"  - {f.VariableName} = {value} {ownership}");
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
                ? "Prioritize independently verified active PLC alarms and confirmed constraint violations. Other findings describe conditions to investigate. Trace only explicit FALSE WaitingFor edges to the selected fault station; InternalFault alone is not fault evidence."
                : "优先核查独立的激活 PLC 报警和确认违规，其余发现仅作为待核实条件。只沿实际为 False 的 WaitingFor 边追踪到故障工位；InternalFault 标签本身不证明故障。");

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
