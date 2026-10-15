using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 全线停机 LLM 跨工位根因分析服务。
    /// 将各工位 AST 诊断摘要汇总后交给 LLM 做根因归纳。
    /// </summary>
    public class LineDiagnosisLlmService : ISelfScopedAutoInject
    {
        private readonly ILogger<LineDiagnosisLlmService> _logger;
        private readonly AgentRuntimeService _agentRuntimeService;
        private readonly KnowledgeMemoryService _knowledgeMemoryService;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        internal static string NormalizeOutputLanguage(string outputLanguage) =>
            LineDiagnosisI18n.Normalize(outputLanguage);

        internal static bool IsEnglishOutput(string outputLanguage) =>
            string.Equals(NormalizeOutputLanguage(outputLanguage), "en-US", StringComparison.OrdinalIgnoreCase);

        public LineDiagnosisLlmService(
            ILogger<LineDiagnosisLlmService> logger,
            AgentRuntimeService agentRuntimeService,
            KnowledgeMemoryService knowledgeMemoryService)
        {
            _logger = logger;
            _agentRuntimeService = agentRuntimeService;
            _knowledgeMemoryService = knowledgeMemoryService;
        }

        /// <summary>
        /// 将各工位诊断摘要交给 LLM 做跨工位根因分析。
        /// </summary>
        /// <param name="summaries">工位诊断摘要列表</param>
        /// <param name="constraintReport">可选：IO诊断报告（DiagnoseEnergyFlowAsync 返回的字符串），为 null 时表示未开启约束分析</param>
        public async Task<LineDiagnosisLlmReport> AnalyzeAsync(
            List<StationDiagnosisSummaryDto> summaries,
            string constraintReport = null,
            string outputLanguage = "zh-CN",
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputLanguage = NormalizeOutputLanguage(outputLanguage);
            summaries = (summaries ?? new()).Where(s => s != null).ToList();
            if (summaries.Count == 0)
                return BuildEvidenceOnlyReport(summaries, constraintReport, outputLanguage);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string modelContent = null;

            try
            {
                _logger.LogInformation(
                    "[全线诊断LLM] 开始跨工位根因分析: 工位数={Count}, 异常工位={Abnormal}",
                    summaries.Count,
                    summaries.Count(s => s.OverallStatus == "SomeFalse"));

                // 加载项目知识记忆作为 LLM 上下文
                var projectId = summaries.FirstOrDefault()?.ProjectId ?? 0;
                var stationCodes = summaries.Select(s => s.StationCode).Where(s => s != null).ToList();
                string knowledgeContext = "";
                if (projectId > 0)
                {
                    try
                    {
                        knowledgeContext = await _knowledgeMemoryService.LoadDiagnosisContextAsync(
                            projectId, stationCodes, maxChars: 8000);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[全线诊断LLM] 加载知识记忆失败，继续无记忆诊断");
                    }
                }

                var evidenceProfiles = BuildEvidenceProfiles(summaries, constraintReport);
                var prompt = BuildPrompt(summaries, knowledgeContext, constraintReport, evidenceProfiles, outputLanguage);
                var agent = await _agentRuntimeService.CreateAgentByRoleAsync(
                    AppsettingsConfig.GetDiagnosticAgentRoleId());
                cancellationToken.ThrowIfCancellationRequested();
                var response = await _agentRuntimeService.RunAgentAsync(
                    agent,
                    prompt,
                    null,
                    cancellationToken);

                sw.Stop();

                cancellationToken.ThrowIfCancellationRequested();
                modelContent = response.Content;
                if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
                    _logger.LogWarning("[全线诊断LLM] 模型不可用，原始结果仅作审计: Error={Error}, Response={Response}",
                        response.ErrorMessage, modelContent);
                var report = CompleteModelResponse(response.Success, response.Content, evidenceProfiles, outputLanguage,
                    response.Success ? (long)response.ExecutionTimeMs : sw.ElapsedMilliseconds);

                _logger.LogInformation(
                    "[全线诊断LLM] 完成: RootCause={Station}, Confidence={Conf}, Time={Time}ms",
                    report.RootCauseStation, report.Confidence, report.ExecutionTimeMs);

                // 仅当置信度 ≥ 0.7 时保存到记忆库（低置信度结论不可靠，不作为后续参考）
                // 保存时标记 IsVerified=false，需人工确认后才提升为可信记忆
                if (report.Success && projectId > 0
                    && !string.IsNullOrEmpty(report.RootCauseStation)
                    && report.Confidence >= 0.7)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var memoryContent = $"- 根因工位: {report.RootCauseStation}\n" +
                                $"- 置信度: {report.Confidence:F2}\n" +
                                $"- 描述: {report.RootCauseDescription}\n" +
                                $"- 影响链: {report.ImpactChain}\n" +
                                $"- 时间: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";

                            // 先检查是否已有相同工位的近期诊断记忆（2小时内），有则跳过避免堆积
                            var recentCutoff = DateTime.UtcNow.AddHours(-2);
                            var existing = await _knowledgeMemoryService.FindRecentDiagnosisAsync(
                                projectId, report.RootCauseStation, recentCutoff);
                            if (existing != null)
                            {
                                _logger.LogDebug("[全线诊断LLM] 工位 {Station} 2小时内已有记忆，跳过保存",
                                    report.RootCauseStation);
                                return;
                            }

                            await _knowledgeMemoryService.SaveDiagnosisMemoryAsync(
                                projectId, report.RootCauseStation,
                                $"诊断结论 - {report.RootCauseStation} - {DateTime.UtcNow:MMdd HHmm}",
                                memoryContent,
                                (float)report.Confidence);
                        }
                        catch (Exception memEx)
                        {
                            _logger.LogWarning(memEx, "[全线诊断LLM] 保存诊断记忆失败");
                        }
                    });
                }

                return report;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogError(ex, "[全线诊断LLM] 跨工位根因分析异常，原始结果仅作审计: {Response}", modelContent);
                return BuildEvidenceOnlyReport(summaries, constraintReport, outputLanguage, sw.ElapsedMilliseconds);
            }
        }

        internal static LineDiagnosisLlmReport BuildEvidenceOnlyReport(
            List<StationDiagnosisSummaryDto> summaries, string constraintReport = null,
            string outputLanguage = "zh-CN", long executionTimeMs = 0) =>
            CompleteModelResponse(false, null, BuildEvidenceProfiles(summaries, constraintReport), outputLanguage, executionTimeMs);

        internal static LineDiagnosisLlmReport CompleteModelResponse(bool success, string content,
            IReadOnlyList<StationRootCauseEvidenceProfile> evidenceProfiles, string outputLanguage = "zh-CN",
            long executionTimeMs = 0)
        {
            var report = success && !string.IsNullOrWhiteSpace(content)
                ? ParseResponse(content)
                : new LineDiagnosisLlmReport { Success = false, EvidenceOnly = true };
            report.ExecutionTimeMs = executionTimeMs;
            ApplyEvidenceArbitration(report, evidenceProfiles, outputLanguage);
            return report;
        }

        internal static string BuildPrompt(
            List<StationDiagnosisSummaryDto> summaries,
            string knowledgeContext = null,
            string constraintReport = null,
            IReadOnlyList<StationRootCauseEvidenceProfile> evidenceProfiles = null,
            string outputLanguage = "zh-CN")
        {
            outputLanguage = NormalizeOutputLanguage(outputLanguage);
            var isEnglishOutput = IsEnglishOutput(outputLanguage);
            var sb = new StringBuilder();

            sb.AppendLine("# 任务：产线全线停机根因分析");
            sb.AppendLine();
            sb.AppendLine("你是一名资深西门子 PLC 产线工程师。以下是产线各工位的实时 AST 诊断摘要。");
            sb.AppendLine("请分析哪个工位最可能是停机根因，哪些工位是被上游阻塞连带影响的。");
            AppendOutputLanguageInstruction(sb, outputLanguage);
            sb.AppendLine();

            // 注入知识记忆上下文（如果有）
            if (!string.IsNullOrWhiteSpace(knowledgeContext))
            {
                sb.AppendLine(knowledgeContext);
                sb.AppendLine();
            }

            // 工位拓扑（按编码排序即顺序）
            var ordered = summaries.OrderBy(s => s.StationOrder).ToList();
            sb.AppendLine("## 工位拓扑（按物料流向排序）");
            sb.AppendLine(string.Join(" → ", ordered.Select(s =>
            {
                var statusIcon = s.OverallStatus switch
                {
                    "AllTrue" => "✅",
                    "Running" => "🟢",
                    "SomeFalse" => "❌",
                    _ => "⚪"
                };
                return $"{statusIcon}{s.StationCode}";
            })));
            sb.AppendLine();

            // 全线概览：先给 LLM 一个快速总览
            var abnormalStations = ordered.Where(s => s.OverallStatus == "SomeFalse").ToList();
            var normalStations = ordered.Where(s => s.OverallStatus == "AllTrue" || s.OverallStatus == "Running").ToList();
            sb.AppendLine("## 全线概览");
            sb.AppendLine($"- 总工位: {ordered.Count}, 异常: {abnormalStations.Count}, 正常: {normalStations.Count}");

            // 构建跨工位等待关系图
            var waitingRelations = new List<string>();
            foreach (var station in abnormalStations)
            {
                if (station.KeyFindings == null) continue;
                var waitTargets = station.KeyFindings
                    .Select(GetConfirmedWaitingTarget)
                    .Where(target => target != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var target in waitTargets)
                {
                    waitingRelations.Add($"  {station.StationCode} 等待 → {target}");
                }
            }
            if (waitingRelations.Count > 0)
            {
                sb.AppendLine("- 工位间等待关系（代码层面已确认）:");
                foreach (var rel in waitingRelations)
                    sb.AppendLine(rel);
            }
            sb.AppendLine();

            AppendEvidenceProfileSection(sb, evidenceProfiles);
            ValveAlarmPromptFormatter.AppendSection(sb, summaries);

            // 各工位摘要 — 只展开异常工位，正常工位一行带过
            sb.AppendLine("## 正常工位（无需分析）");
            foreach (var station in normalStations)
            {
                var statusLabel = station.OverallStatus == "Running" ? "运行中" : "全部正常";
                sb.AppendLine($"- {station.StationCode}: {statusLabel}");
            }
            sb.AppendLine();

            sb.AppendLine("## 异常工位详情");
            foreach (var station in abnormalStations)
            {
                sb.AppendLine();
                sb.AppendLine($"### {station.StationCode} (序号{station.StationOrder})");
                sb.AppendLine($"- 状态: {station.OverallStatus}");
                sb.AppendLine($"- 诊断策略: {station.DiagnosisStrategy ?? "N/A"}");
                sb.AppendLine($"- 锚点总数: {station.AnchorCount}, 求值数: {station.EvaluatedCount}, False数: {station.FalseAnchorCount}");

                // 模板匹配信息（如果有）
                if (!string.IsNullOrWhiteSpace(station.TemplateMatchInfo))
                {
                    sb.AppendLine($"- 📋 {station.TemplateMatchInfo}");
                }

                if (!string.IsNullOrWhiteSpace(station.ErrorMessage))
                {
                    sb.AppendLine($"- 错误: {station.ErrorMessage}");
                }

                if (station.KeyFindings?.Count > 0)
                {
                    // ── 过滤：转移条件已满足（EvaluatedValue=true）的 GraphTransitionTrace findings
                    // 对 AI 分析无意义——机器已通过这些步骤，不是当前卡点，排除可减少噪声 ──
                    var llmFindings = station.KeyFindings
                        .Where(f => !(f.DiagnosisPhase == "GraphTransitionTrace" && f.EvaluatedValue == true))
                        .ToList();

                    // ── 按故障归属分组 ──
                    var allInternal = llmFindings
                        .Where(f => f.FaultOwnership == "InternalFault" || string.IsNullOrEmpty(f.FaultOwnership))
                        .ToList();
                    var allWaiting = llmFindings
                        .Where(f => !string.IsNullOrEmpty(f.FaultOwnership) && f.FaultOwnership.StartsWith("WaitingFor:"))
                        .ToList();
                    var allExternal = llmFindings
                        .Where(f => f.FaultOwnership == "ExternalInput")
                        .ToList();

                    // ── 功能块分布统计（让LLM看到"全景"而非随机5个）──
                    if (llmFindings.Count > 10)
                    {
                        sb.AppendLine($"- 📊 发现总数: {llmFindings.Count} (🔴内部:{allInternal.Count} 🟡等待:{allWaiting.Count} ⚪外部:{allExternal.Count})");
                        var blockGroups = llmFindings
                            .Where(f => !string.IsNullOrWhiteSpace(f.BlockName))
                            .GroupBy(f => f.BlockName, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(g => g.Count())
                            .ToList();
                        if (blockGroups.Count > 0)
                        {
                            sb.AppendLine("- 📊 按功能块分布:");
                            foreach (var bg in blockGroups.Take(8))
                            {
                                var falseInBlock = bg.Count(f => f.EvaluatedValue == false);
                                var trueInBlock = bg.Count(f => f.EvaluatedValue == true);
                                sb.AppendLine($"    {bg.Key}: {bg.Count()}个发现(FALSE:{falseInBlock} TRUE:{trueInBlock})");
                            }
                            if (blockGroups.Count > 8)
                                sb.AppendLine($"    ...及其他 {blockGroups.Count - 8} 个功能块");
                        }
                    }

                    // ── 按归属展示代表性findings（优先选有失败路径、有历史分析的）──
                    if (allInternal.Count > 0)
                    {
                        var representative = SelectRepresentativeFindings(allInternal, 5);
                        sb.AppendLine($"- 🔴 本工位条件信号（归属标签不证明故障）({allInternal.Count}个，展示{representative.Count}个代表):");
                        foreach (var finding in representative)
                            AppendFinding(sb, finding);
                        if (allInternal.Count > representative.Count)
                            sb.AppendLine($"    ...及其他 {allInternal.Count - representative.Count} 个本工位条件信号（归属标签不证明故障）");
                    }

                    if (allWaiting.Count > 0)
                    {
                        var representative = SelectRepresentativeFindings(allWaiting, 5);
                        sb.AppendLine($"- 🟡 等待上游信号({allWaiting.Count}个，展示{representative.Count}个代表):");
                        foreach (var finding in representative)
                        {
                            var waitTarget = finding.FaultOwnership.Substring("WaitingFor:".Length);
                            AppendFinding(sb, finding, $"等待{waitTarget}");
                        }
                        // 展示等待目标汇总
                        var allWaitTargets = allWaiting
                            .Where(f => f.FaultOwnership.Length > "WaitingFor:".Length)
                            .Select(f => f.FaultOwnership.Substring("WaitingFor:".Length))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        if (allWaitTargets.Count > 0)
                            sb.AppendLine($"    等待目标汇总: {string.Join(", ", allWaitTargets)}");
                    }

                    if (allExternal.Count > 0)
                    {
                        var representative = SelectRepresentativeFindings(allExternal, 3);
                        sb.AppendLine($"- ⚪ 外部输入信号({allExternal.Count}个，展示{representative.Count}个代表):");
                        foreach (var finding in representative)
                            AppendFinding(sb, finding);
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 分析要点");
            sb.AppendLine("1. 产线中**任意一个工位故障都会导致整线停机**，故障传播是辐射式的（不是线性的从前到后）。不能仅凭\"多个工位异常\"就认为有多个故障。");
            sb.AppendLine("2. InternalFault、WaitingFor、ExternalInput 是条件归属标签，不是故障证明；失败路径、L1/L2 追踪和物理地址只能帮助核对信号。");
            sb.AppendLine("3. False、Unknown、GRAPH execbits 和完成/允许/握手状态不能独立证明故障，也不能证明通信中断。");
            sb.AppendLine("4. 仅 Good 且激活的 PLC 报警，或标有‘判定: 确认违规’的 IO 条目，支持故障候选。报警可能锁存，不能由此断言当前机械部件损坏。");
            sb.AppendLine("5. AllTrue/Running 只描述活动状态，不得排除同工位的独立 PLC 报警。");
            sb.AppendLine("6. 没有独立证据时 rootCauseStation=null，role=Unknown，明确证据不足；不得反向断言产线正常。");
            sb.AppendLine("7. 只有条件确实为 False 的 WaitingFor 边可以建立等待链。链直接或多跳到有独立报警/确认违规的候选工位时，沿链的纯等待工位可标记 Impacted。多个独立候选时 rootCauseStation=null，并保留每个候选的已证明等待链；同一等待站通向多个候选必须明确无法唯一归属。未知条件、循环、跨项目同名歧义及自身独立故障不得简单归为连带，禁止按工位编号或模型猜测建立关系。");
            sb.AppendLine("8. 所有公开字段遵守同一证据门槛，包括根因、工位摘要、次因、建议动作和推理。现场建议用于核实信号及命令，不得把猜测当已发生故障。");
            sb.AppendLine();

            // 注入 IO 约束诊断报告（如果有）
            if (!string.IsNullOrWhiteSpace(constraintReport))
            {
                sb.AppendLine("## IO 约束证据报告（内部参考）");
                sb.AppendLine("以下是本次停机诊断中来自能量流/IO诊断服务的分析报告，请将这部分作为根因分析的重要参考依据：");
                sb.AppendLine("若报告中的约束问题包含 I点地址，请在最终 JSON 的 rootCauseDescription、stationAnalysis.summary、suggestedActions 中明确输出对应信号名和地址，便于现场按点位排查。");
                sb.AppendLine("注意：最终面向用户的 rootCauseDescription、impactChain、stationAnalysis、secondaryCauses、suggestedActions 中不要使用“IO约束诊断确认”“约束诊断服务确认”“IO约束故障”等内部来源表述。只描述已确认的信号状态和报警/违规证据，标明根因仍待现场确认；不要把报警直接解释为机械损坏。");
                sb.AppendLine("注意：报告中标注“无法判定”“采集失败”“未采到”的条目，是该信号没有采集到实时值（通讯故障、点名不存在等），不是已确认的 IO 故障，不能作为故障点输出，也不能替换成 true/false 之类的猜测值。");
                sb.AppendLine();
                sb.AppendLine(constraintReport);
                sb.AppendLine();
            }

            AppendOutputSchema(sb, isEnglishOutput);

            return sb.ToString();
        }

        private static void AppendOutputLanguageInstruction(StringBuilder sb, string outputLanguage)
        {
            var lang = LineDiagnosisI18n.Normalize(outputLanguage);
            var languageName = LineDiagnosisI18n.LanguageName(lang);
            sb.AppendLine();
            sb.AppendLine("## 输出语言 / Output language");
            if (lang == LineDiagnosisI18n.EnUs)
            {
                sb.AppendLine("- The final JSON field names and enum values must stay exactly the same.");
                sb.AppendLine("- All user-facing string values must be written in English: rootCauseDescription, impactChain, stationAnalysis.summary, secondaryCauses.description, suggestedActions, reasoning.");
                sb.AppendLine("- Keep PLC station codes, variable names, DB names, IO addresses, block names and enum values unchanged.");
                sb.AppendLine("- Chinese signal comments may be kept only when they are the original PLC comments; explain the conclusion itself in English.");
            }
            else if (lang == LineDiagnosisI18n.ZhCn)
            {
                sb.AppendLine("- 最终 JSON 字段名和枚举值保持不变。");
                sb.AppendLine("- 面向用户的结论、工位摘要、次因、建议动作和推理过程使用中文。");
                sb.AppendLine("- PLC 工位号、变量名、DB 名、IO 地址、逻辑块名和枚举值保持原样。");
            }
            else
            {
                // zh-TW / ja-JP：结论性文字用目标语言，技术标识保持原样
                sb.AppendLine($"- The final JSON field names and enum values must stay exactly the same.");
                sb.AppendLine($"- All user-facing string values (rootCauseDescription, impactChain, stationAnalysis.summary, secondaryCauses.description, suggestedActions, reasoning) must be written in {languageName}.");
                sb.AppendLine("- Keep PLC station codes, variable names, DB names, IO addresses, block names and enum values unchanged.");
                sb.AppendLine("- Original Chinese PLC signal comments may be quoted as-is; explain the conclusion itself in the target language.");
            }
        }

        private static void AppendOutputSchema(StringBuilder sb, bool isEnglishOutput)
        {
            sb.AppendLine("## 输出要求");
            sb.AppendLine(isEnglishOutput
                ? "Return JSON only. Use null for rootCauseStation unless independent fault evidence supports a station candidate. A candidate is not a confirmed physical root cause."
                : "只返回 JSON。缺少独立故障证据时 rootCauseStation 为 null；有证据时也只能描述待确认候选，不能宣称已确认物理根因。");
            sb.AppendLine("{\"rootCauseStation\":null,\"rootCauseDescription\":\"\",\"confidence\":0,\"impactChain\":\"\",\"stationAnalysis\":[],\"secondaryCauses\":[],\"suggestedActions\":[],\"reasoning\":\"\"}");
            sb.AppendLine("stationAnalysis: [{stationCode, role: RootCause|Impacted|Normal|Unknown, summary}]; secondaryCauses: [{stationCode, description}]");
        }

        internal sealed class StationRootCauseEvidenceProfile
        {
            public string StationCode { get; set; }
            public long ProjectId { get; set; }
            public bool AmbiguousStation { get; set; }
            public List<string> ConfirmedEvidence { get; set; } = new();
            public HashSet<string> ConfirmedWaitingTargets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public bool HasConfirmedFaultEvidence => HasConstraintPhysicalIoFault || HasExplicitFaultEvidence;
            public int StationOrder { get; set; }
            public int Score { get; set; }
            public string EvidenceLevel { get; set; }
            public int DirectActuatorFeedbackCount { get; set; }
            public int GraphTransitionBlockCount { get; set; }
            public int ExplicitAlarmOrFaultCount { get; set; }
            public int ClearNearTraceCount { get; set; }
            public int ProcessHandshakeCount { get; set; }
            public int WaitingCount { get; set; }
            public int ExternalInputCount { get; set; }
            public int InternalFaultCount { get; set; }
            public int ConstraintPhysicalIoFaultCount { get; set; }
            public List<string> Reasons { get; set; } = new List<string>();

            public bool HasStrongPhysicalEvidence => HasConfirmedFaultEvidence;

            public bool HasConstraintPhysicalIoFault => ConstraintPhysicalIoFaultCount > 0;

            public bool HasExplicitFaultEvidence => ExplicitAlarmOrFaultCount > 0;

            public bool IsMostlyProcessHandshake =>
                ProcessHandshakeCount > 0
                && DirectActuatorFeedbackCount == 0
                && ExplicitAlarmOrFaultCount == 0;

            public bool IsExternalOrHandshakeDominant =>
                DirectActuatorFeedbackCount == 0
                && ExplicitAlarmOrFaultCount == 0
                && (ProcessHandshakeCount > 0 || ExternalInputCount > 0 || WaitingCount > 0);
        }

        internal static IReadOnlyList<StationRootCauseEvidenceProfile> BuildEvidenceProfiles(
            List<StationDiagnosisSummaryDto> summaries,
            string constraintReport = null)
        {
            var profiles = summaries == null || summaries.Count == 0
                ? new List<StationRootCauseEvidenceProfile>()
                : summaries
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(s.StationCode))
                    .Select(BuildEvidenceProfile)
                    .ToList();

            MergeConstraintEvidenceProfiles(
                profiles,
                BuildConstraintEvidenceProfiles(constraintReport, summaries));

            // Public report DTOs identify stations by code only. Never merge evidence across PLC projects.
            var ambiguousCodes = (summaries ?? new())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.StationCode))
                .GroupBy(s => s.StationCode, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(s => s.ProjectId).Distinct().Count() > 1)
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in profiles)
                profile.AmbiguousStation = ambiguousCodes.Contains(profile.StationCode);

            return profiles
                .OrderByDescending(p => p.HasConstraintPhysicalIoFault)
                .ThenByDescending(p => p.Score)
                .ThenByDescending(p => p.DirectActuatorFeedbackCount)
                .ThenByDescending(p => p.GraphTransitionBlockCount)
                .ThenBy(p => p.StationOrder)
                .ToList();
        }

        private static IReadOnlyList<StationRootCauseEvidenceProfile> BuildConstraintEvidenceProfiles(
            string constraintReport,
            List<StationDiagnosisSummaryDto> summaries)
        {
            if (string.IsNullOrWhiteSpace(constraintReport))
                return Array.Empty<StationRootCauseEvidenceProfile>();

            var stationOrder = summaries?
                .Where(s => !string.IsNullOrWhiteSpace(s.StationCode))
                .GroupBy(s => s.StationCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Min(s => s.StationOrder), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var profiles = new Dictionary<string, StationRootCauseEvidenceProfile>(StringComparer.OrdinalIgnoreCase);
            // The report producer labels only confirmed constraint issues. Values/words alone are not proof.
            // Each issue belongs to its header station; referenced signals do not establish faults at other stations.
            var entries = Regex.Split(constraintReport, @"(?m)(?=^\s*\d+\.\s*工位\s*[:：])");
            foreach (var entry in entries)
            {
                if (!Regex.IsMatch(entry, @"(?m)^\s*判定\s*[:：]\s*确认违规\s*$")
                    || Regex.IsMatch(entry, @"(?m)^\s*无法判定规则\s*[:：]"))
                    continue;
                var header = Regex.Match(entry, @"(?m)^\s*\d+\.\s*工位\s*[:：]\s*(OP\d{2,4})\b", RegexOptions.IgnoreCase);
                if (!header.Success) continue;
                var stationCode = header.Groups[1].Value.ToUpperInvariant();
                var evidence = Regex.Split(entry, @"\r?\n")
                    .Select(NormalizeEvidenceLine)
                    .Where(line => IsPhysicalIoConstraintFaultLine(line)
                        || Regex.IsMatch(line, @"^(当前值|原因)\s*[:：]"))
                    .Select(StripUnreadSignalText)
                    .Select(BuildConstraintEvidenceText)
                    .Where(line => !string.IsNullOrWhiteSpace(line)).Distinct().ToList();
                if (evidence.Count == 0) continue;
                if (!profiles.TryGetValue(stationCode, out var profile))
                {
                    profile = new StationRootCauseEvidenceProfile
                    {
                        StationCode = stationCode,
                        StationOrder = stationOrder.TryGetValue(stationCode, out var order) ? order : int.MaxValue,
                        EvidenceLevel = "ConstraintPhysicalIoFault",
                    };
                    profiles[stationCode] = profile;
                }
                profile.ConstraintPhysicalIoFaultCount++;
                profile.InternalFaultCount++;
                profile.Score += 120;
                profile.ConfirmedEvidence.AddRange(evidence);
                foreach (var evidenceText in evidence.Take(6 - profile.Reasons.Count))
                    profile.Reasons.Add($"IO物理约束强证据: {evidenceText}");
            }

            foreach (var profile in profiles.Values)
            {
                profile.Score = Math.Max(90, Math.Min(180, profile.Score));
                profile.EvidenceLevel = "ConstraintPhysicalIoFault";
            }

            return profiles.Values.ToList();
        }

        private static void MergeConstraintEvidenceProfiles(
            List<StationRootCauseEvidenceProfile> profiles,
            IReadOnlyList<StationRootCauseEvidenceProfile> constraintProfiles)
        {
            if (constraintProfiles == null || constraintProfiles.Count == 0)
                return;

            foreach (var constraint in constraintProfiles)
            {
                var existing = profiles.FirstOrDefault(p =>
                    string.Equals(p.StationCode, constraint.StationCode, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    profiles.Add(constraint);
                    continue;
                }

                // The text report has no project key; a duplicate code cannot safely receive its evidence.
                if (profiles.Where(p => string.Equals(p.StationCode, constraint.StationCode, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.ProjectId).Distinct().Count() > 1)
                {
                    // Keep the verified violation visible while refusing an unsupported project attribution.
                    constraint.AmbiguousStation = true;
                    profiles.Add(constraint);
                    continue;
                }
                existing.ConfirmedEvidence.AddRange(constraint.ConfirmedEvidence);
                existing.Score = Math.Max(existing.Score + constraint.Score, constraint.Score);
                existing.ConstraintPhysicalIoFaultCount += constraint.ConstraintPhysicalIoFaultCount;
                existing.InternalFaultCount += constraint.InternalFaultCount;
                existing.EvidenceLevel = "ConstraintPhysicalIoFault";
                foreach (var reason in constraint.Reasons)
                {
                    if (existing.Reasons.Count >= 6)
                        break;
                    if (!existing.Reasons.Contains(reason))
                        existing.Reasons.Insert(0, reason);
                }
            }
        }

        internal static bool IsPhysicalIoConstraintFaultLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;
            // D-1：无法判定 / 采集失败 / 「未采到信号:」清单行 是采集问题，不是 IO 物理故障证据
            if (Regex.IsMatch(line, @"(未发现|无异常|无问题|0\s*个问题|0\s*条问题|no\s+issues?|无法判定|采集失败|^未采到信号\s*[:：])", RegexOptions.IgnoreCase))
                return false;
            // D-1：违规条目里夹带的未采到信号（「X(%I1.1)=未采到(原因)」、「未采到信号（其取值不影响本结论）：…」）
            // 不能当证据，剔除后只看已采到的部分；剩下没有物理证据则不算
            line = StripUnreadSignalText(line);
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var hasPhysicalAddress = Regex.IsMatch(line, @"%[IQ]\d+(?:\.\d+)?", RegexOptions.IgnoreCase);
            var hasPhysicalSignal = Regex.IsMatch(line,
                @"\bOP\d{2,4}[_-][A-Za-z0-9_\-\[\].]*(CYL|YV|Sensor|Servo|Motor|Axis|Door|Safe)[A-Za-z0-9_\-\[\].]*\b",
                RegexOptions.IgnoreCase)
                || Regex.IsMatch(line, @"(I点|Q点|物理|到位反馈|反馈信号|传感器|气缸|电磁阀|伺服|电机)", RegexOptions.IgnoreCase);
            if (!hasPhysicalAddress && !hasPhysicalSignal)
                return false;

            return Regex.IsMatch(line,
                @"(故障|异常|违反|缺失|未满足|必须为|触发|False|True|false|true|constraint|violation|fault|abnormal|missing|not\s+satisfied)",
                RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// D-1：去掉约束报告行里未采到信号的文字（诊断报告 FormatSignalValue 的「X(地址)=未采到(原因)」
        /// 与违规原因末尾的「未采到信号（其取值不影响本结论）：…」），它们不是实测值，不能作为 IO 物理故障证据。
        /// </summary>
        internal static string StripUnreadSignalText(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;
            var stripped = Regex.Replace(line, @"未采到信号（其取值不影响本结论）[:：].*$", string.Empty);
            stripped = Regex.Replace(stripped, @"[^\s,，:：]+=未采到\([^)]*\)", string.Empty);
            // D-1 fix round 1：只收拾因移除未采到片段而产生的“悬空”逗号（相邻/首尾多出来的逗号），
            // 不能再像原来那样把整行所有逗号都规整一遍——那会把普通中文叙述里的单个「，」
            // （如“当前为 true，应永为 false”）也误判成需要规范化的列表分隔符改写掉。
            stripped = Regex.Replace(stripped, @"(?:[,，]\s*){2,}", ", ");
            stripped = Regex.Replace(stripped, @"^\s*[,，]\s*", string.Empty);
            stripped = Regex.Replace(stripped, @"[,，]\s*$", string.Empty);
            return stripped.Trim();
        }

        private static string BuildConstraintEvidenceText(string line)
        {
            var addresses = Regex.Matches(line, @"%[IQ]\d+(?:\.\d+)?", RegexOptions.IgnoreCase)
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();
            var signals = Regex.Matches(line, @"\b(?:DB\d+_[A-Za-z0-9_\.\[\]]+|OP\d{2,4}[_-][A-Za-z0-9_\-\[\].]+)\b", RegexOptions.IgnoreCase)
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            var facts = new List<string>();
            if (signals.Count > 0)
                facts.Add($"信号={string.Join(",", signals)}");
            if (addresses.Count > 0)
                facts.Add($"地址={string.Join(",", addresses)}");
            facts.Add(line);
            // This feeds the public evidence report, not just a prompt preview. The decisive
            // signal/address can be at the end of a long full-logic rule; never silently drop it.
            return string.Join("；", facts);
        }

        private static string NormalizeEvidenceLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;
            return Regex.Replace(line, @"\s+", " ")
                .Trim(' ', '-', '*', '•', '●', '\t');
        }

        private static string TrimEvidenceText(string text, int maxLength = 220)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "...";
        }

        private static StationRootCauseEvidenceProfile BuildEvidenceProfile(StationDiagnosisSummaryDto station)
        {
            var profile = new StationRootCauseEvidenceProfile
            {
                StationCode = station.StationCode,
                ProjectId = station.ProjectId,
                StationOrder = station.StationOrder,
            };

            // ActiveAlarms are produced only by ValveAlarmEvaluator after a Good + true read.
            // AST findings carry no read-quality/alarm contract, so their labels cannot establish an alarm.
            foreach (var alarm in station.ActiveAlarms ?? new())
            {
                if (!IsNativeFault(alarm)) continue;
                profile.ExplicitAlarmOrFaultCount++;
                var alarmEvidence = new List<string> { $"{alarm.AlarmVariable}=true (Good)" };
                if (!string.IsNullOrWhiteSpace(alarm.Text)) alarmEvidence.Add($"PLC alarm: {alarm.Text}");
                if (!string.IsNullOrWhiteSpace(alarm.ValveInstance)) alarmEvidence.Add($"Valve={alarm.ValveInstance}");
                foreach (var signal in new[] { alarm.OutputSignal }.Concat(alarm.Feedbacks ?? new()))
                {
                    if (signal == null || string.IsNullOrWhiteSpace(signal.Name)) continue;
                    var address = string.IsNullOrWhiteSpace(signal.Address) ? "" : $"({signal.Address})";
                    var value = signal.Value switch { true => "true", false => "false", _ => "Unknown" };
                    alarmEvidence.Add($"{signal.Name}{address}={value}");
                }
                profile.ConfirmedEvidence.Add(string.Join("; ", alarmEvidence.Distinct()));
            }

            foreach (var finding in station.KeyFindings ?? new List<StationAnchorFinding>())
            {
                if (finding == null)
                    continue;
                var waitingTarget = GetConfirmedWaitingTarget(finding);
                if (waitingTarget != null && station.OverallStatus == "SomeFalse")
                    profile.ConfirmedWaitingTargets.Add(waitingTarget);
                if (finding.DiagnosisPhase == "GraphTransitionTrace" && finding.EvaluatedValue == true)
                    continue;

                var score = ScoreFindingForRootCause(finding, profile);
                profile.Score += score;
                AddEvidenceReason(profile, finding, score);
            }

            profile.Score = Math.Max(-50, Math.Min(120, profile.Score));
            profile.EvidenceLevel =
                profile.HasExplicitFaultEvidence ? "ExplicitAlarmOrFault" :
                profile.DirectActuatorFeedbackCount > 0 ? "FeedbackConditionUnmet" :
                profile.IsMostlyProcessHandshake ? "ProcessHandshakeOrRobotHold" :
                profile.Score > 20 ? "Mixed" : "Weak";

            return profile;
        }

        internal static bool IsNativeFault(ActiveValveAlarmDto alarm) =>
            alarm != null && !string.IsNullOrWhiteSpace(alarm.AlarmVariable)
            && !string.Equals(alarm.Kind, "FwdLock", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(alarm.Kind, "BkdLock", StringComparison.OrdinalIgnoreCase)
            && !alarm.AlarmVariable.Split('.').Any(segment =>
                segment.Equals("CylinderLockErrors", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Warnings", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Messages", StringComparison.OrdinalIgnoreCase));

        private static string GetConfirmedWaitingTarget(StationAnchorFinding finding)
        {
            // Do not turn Unknown into an edge, even if another DTO field contains the word False.
            if (finding?.EvaluatedValue != false
                || (!string.IsNullOrWhiteSpace(finding.EvaluationStatus)
                    && !string.Equals(finding.EvaluationStatus, "False", StringComparison.OrdinalIgnoreCase))
                || finding.FaultOwnership?.StartsWith("WaitingFor:", StringComparison.OrdinalIgnoreCase) != true)
                return null;
            var target = finding.FaultOwnership["WaitingFor:".Length..].Trim();
            return target.Length == 0 ? null : target;
        }

        private static int ScoreFindingForRootCause(
            StationAnchorFinding finding,
            StationRootCauseEvidenceProfile profile)
        {
            var score = 0;
            var isFalse = IsFalseFinding(finding);
            var isProcessHandshake = IsProcessHandshakeFinding(finding);
            const bool isExplicitAlarm = false; // Only quality-checked ActiveAlarms establish active PLC faults.
            var isDirectActuator = IsDirectActuatorFeedbackFinding(finding);
            var isGraphBlock = IsGraphTransitionBlockFinding(finding);
            var hasNearTrace = HasClearNearTrace(finding);

            if (finding.FaultOwnership == "InternalFault" || string.IsNullOrWhiteSpace(finding.FaultOwnership))
            {
                profile.InternalFaultCount++;
                score += isProcessHandshake && !isDirectActuator && !isExplicitAlarm ? 1 : 6;
            }
            else if (finding.FaultOwnership == "ExternalInput")
            {
                profile.ExternalInputCount++;
                score += isDirectActuator ? 4 : isProcessHandshake ? -8 : -3;
            }
            else if (finding.FaultOwnership.StartsWith("WaitingFor:", StringComparison.OrdinalIgnoreCase))
            {
                profile.WaitingCount++;
                score -= 10;
            }

            if (isDirectActuator)
            {
                profile.DirectActuatorFeedbackCount++;
                score += 28;
            }

            if (isGraphBlock)
            {
                profile.GraphTransitionBlockCount++;
                score += isDirectActuator ? 14 : isProcessHandshake ? -2 : 8;
            }

            if (hasNearTrace)
            {
                profile.ClearNearTraceCount++;
                score += isProcessHandshake || finding.FaultOwnership == "ExternalInput"
                    ? 2
                    : finding.TraceDepth == 2 ? 12 : 8;
            }

            if (isExplicitAlarm)
            {
                profile.ExplicitAlarmOrFaultCount++;
                score += isDirectActuator ? 12 : 22;
            }

            if (isProcessHandshake)
            {
                profile.ProcessHandshakeCount++;
                score -= hasNearTrace || isExplicitAlarm ? 3 : 8;
            }

            if (!string.IsNullOrWhiteSpace(finding.IoTagAddress))
                score += isDirectActuator ? 8 : 3;
            if (!string.IsNullOrWhiteSpace(finding.FailurePathSummary))
                score += 3;
            if (!string.IsNullOrWhiteSpace(finding.SignalHistoryAnalysis))
                score += 2;
            if (finding.DiagnosisQuality == "Diagnosed")
                score += 1;
            if (!isFalse && !isExplicitAlarm)
                score -= 4;

            return Math.Max(-25, Math.Min(45, score));
        }

        private static void AddEvidenceReason(
            StationRootCauseEvidenceProfile profile,
            StationAnchorFinding finding,
            int score)
        {
            if (profile.Reasons.Count >= 6)
                return;

            var label = !string.IsNullOrWhiteSpace(finding.DisplayName)
                ? finding.DisplayName
                : finding.VariableName;
            if (string.IsNullOrWhiteSpace(label))
                return;

            if (IsDirectActuatorFeedbackFinding(finding))
            {
                profile.Reasons.Add($"执行机构/到位条件未满足（非独立故障证明）: {label}");
                return;
            }

            if (IsProcessHandshakeFinding(finding) && score < 5)
            {
                profile.Reasons.Add($"流程状态/握手条件未满足: {label}");
                return;
            }

            if (score > 12)
                profile.Reasons.Add($"可疑信号: {label}");
        }

        internal static void AppendEvidenceProfileSection(
            StringBuilder sb,
            IReadOnlyList<StationRootCauseEvidenceProfile> evidenceProfiles)
        {
            if (evidenceProfiles == null || evidenceProfiles.Count == 0)
                return;

            sb.AppendLine("## 结构化根因证据画像");
            sb.AppendLine("只有 Good 且激活的 PLC 报警或标有‘判定: 确认违规’的 IO 规则支持故障候选。False、Unknown、execbits、InternalFault 标签及 FailurePathSummary 仅说明条件或流程状态，不能单独证明故障或通信中断。证据不足时 rootCauseStation 必须为 null，所有输出字段都只能描述待确认状态。评分仅用于排查排序，不能替代故障证据。报警可能锁存，故障候选仍需现场确认，不得直接描述为当前机械部件损坏。");
            foreach (var profile in evidenceProfiles.Take(8))
            {
                sb.AppendLine(
                    $"- {profile.StationCode}: Score={profile.Score}, Level={profile.EvidenceLevel}, " +
                    $"ConstraintIoFault={profile.ConstraintPhysicalIoFaultCount}, " +
                    $"DirectActuator={profile.DirectActuatorFeedbackCount}, GraphBlock={profile.GraphTransitionBlockCount}, " +
                    $"NearTrace={profile.ClearNearTraceCount}, " +
                    $"AlarmFault={profile.ExplicitAlarmOrFaultCount}, ProcessHandshake={profile.ProcessHandshakeCount}, " +
                    $"ExternalInput={profile.ExternalInputCount}, Waiting={profile.WaitingCount}");
                if (profile.Reasons.Count > 0)
                    sb.AppendLine($"  证据: {string.Join("；", profile.Reasons.Take(3))}");
                if (profile.ConfirmedEvidence.Count > 0)
                    sb.AppendLine($"  确认信号/违规: {string.Join("；", profile.ConfirmedEvidence.Take(6))}");
                if (profile.ConfirmedWaitingTargets.Count > 0)
                    sb.AppendLine($"  已读 False 的等待目标（仍需核对目标独立故障证据及歧义/循环）: {string.Join(", ", profile.ConfirmedWaitingTargets)}");
            }
            sb.AppendLine();
        }

        internal static void ApplyEvidenceArbitration(
            LineDiagnosisLlmReport report,
            IReadOnlyList<StationRootCauseEvidenceProfile> evidenceProfiles,
            string outputLanguage = "zh-CN") =>
            DiagnosisEvidenceGuard.Apply(report, evidenceProfiles, outputLanguage);

        private static bool IsFalseFinding(StationAnchorFinding f) =>
            f?.EvaluatedValue == false
            || string.Equals(f?.EvaluationStatus, "False", StringComparison.OrdinalIgnoreCase);

        private static bool IsGraphTransitionBlockFinding(StationAnchorFinding f)
        {
            var text = BuildFindingText(f);
            return IsFalseFinding(f)
                   && (string.Equals(f?.DiagnosisPhase, "GraphTransitionTrace", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("[Transition]", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("Transition", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("转移条件", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDirectActuatorFeedbackFinding(StationAnchorFinding f)
        {
            if (!IsFalseFinding(f))
                return false;

            var text = BuildFindingText(f);
            var isProcessHandshake = IsProcessHandshakeFinding(f);
            var hasPhysicalIoAddress = HasPhysicalIoAddress(f);
            var isExternalInput = string.Equals(f.FaultOwnership, "ExternalInput", StringComparison.OrdinalIgnoreCase);

            if (isExternalInput && !hasPhysicalIoAddress)
                return false;

            if (isProcessHandshake && !hasPhysicalIoAddress)
                return false;

            var hasCylinderOrValve = Regex.IsMatch(text,
                @"(CYL|Cylinder|YV|Valve|FwdPos|BkdPos|IWP|IHP|气缸|电磁阀|阀|伸出|缩回)",
                RegexOptions.IgnoreCase);
            var hasFeedback = Regex.IsMatch(text,
                @"(FwdPos|BkdPos|IWP|IHP|Pos|Feedback|Sensor|到位|反馈|传感器)",
                RegexOptions.IgnoreCase);
            var hasDriveDevice = Regex.IsMatch(text,
                @"(Motor|Drive|Axis|Servo|电机|驱动|轴|伺服)",
                RegexOptions.IgnoreCase);
            var hasDriveFault = Regex.IsMatch(text,
                @"(Alarm|Fault|Error|Err|Warn|Warning|EStop|E_Stop|CommLost|报警|故障|错误|异常|急停|通信|通讯|断开)",
                RegexOptions.IgnoreCase);

            return (hasCylinderOrValve && hasFeedback) || (hasDriveDevice && hasDriveFault);
        }

        private static bool HasPhysicalIoAddress(StationAnchorFinding f)
        {
            return !string.IsNullOrWhiteSpace(f?.IoTagAddress)
                   && (f.IoTagAddress.StartsWith("%I", StringComparison.OrdinalIgnoreCase)
                       || f.IoTagAddress.StartsWith("%Q", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsProcessHandshakeFinding(StationAnchorFinding f)
        {
            var text = BuildFindingText(f);
            if (Regex.IsMatch(text,
                    @"(KUKA|Fool|FoolNumber|WorkDone|Cache|OK|Robot|Reach|Drop|Pick|Allow|Ready|Done|Complete|Finish|LineOK|Handshake|Sync|机器人|完成|允许|到达|抓取|放置|握手|同步|就绪|准备|防呆|缓存)",
                    RegexOptions.IgnoreCase))
            {
                return true;
            }

            return Regex.IsMatch(text,
                @"(Robot|Reach|Drop|Pick|Allow|Ready|Done|Complete|Finish|LineOK|Handshake|Sync|机器人|完成|允许|到达|抓取|放置|握手|同步|就绪|准备)",
                RegexOptions.IgnoreCase);
        }

        private static bool HasClearNearTrace(StationAnchorFinding f)
        {
            if (f == null)
                return false;

            return f.TraceDepth is 1 or 2
                && !string.IsNullOrWhiteSpace(f.FailurePathSummary)
                && (string.Equals(f.DiagnosisQuality, "Diagnosed", StringComparison.OrdinalIgnoreCase)
                    || IsFalseFinding(f));
        }

        private static string BuildFindingText(StationAnchorFinding f)
        {
            if (f == null)
                return string.Empty;

            return string.Join(" ", new[]
            {
                f.DisplayName,
                f.VariableName,
                f.FailurePathSummary,
                f.IoTagComment,
                f.BlockName,
                f.DiagnosisPhase,
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static void AppendFinding(StringBuilder sb, StationAnchorFinding finding, string extra = null)
        {
            var displayLabel = !string.IsNullOrWhiteSpace(finding.DisplayName)
                ? $"{finding.DisplayName}({finding.VariableName})"
                : finding.VariableName;
            var extraTag = !string.IsNullOrEmpty(extra) ? $" [{extra}]" : "";
            sb.AppendLine($"  - {displayLabel} = {finding.EvaluatedValue} [{finding.EvaluationStatus}]{extraTag}");
            if (!string.IsNullOrWhiteSpace(finding.FailurePathSummary))
            {
                sb.AppendLine($"    失败路径: {finding.FailurePathSummary}");
            }
            // 物理地址对操作工定位接线有直接帮助，优先输出
            if (!string.IsNullOrWhiteSpace(finding.IoTagAddress))
            {
                var commentPart = !string.IsNullOrWhiteSpace(finding.IoTagComment)
                    ? $" ({finding.IoTagComment})"
                    : "";
                sb.AppendLine($"    📍 物理地址: {finding.IoTagAddress}{commentPart}");
            }
            if (!string.IsNullOrWhiteSpace(finding.SignalHistoryAnalysis))
            {
                sb.AppendLine($"    信号历史: {finding.SignalHistoryAnalysis}");
            }
        }

        /// <summary>
        /// 判断 Finding 是否属于气缸/阀门/电机类执行机构（优先级最高，这类设备物理故障是最常见停机根因）。
        /// </summary>
        private static bool IsMechanicalActuatorFinding(StationAnchorFinding f)
        {
            var combined = $"{f.DisplayName} {f.VariableName}";
            return System.Text.RegularExpressions.Regex.IsMatch(combined,
                @"(CYL|YV|Motor|Drive|Servo|Pump|Fan|Conveyor|气缸|电机|阀门|电磁阀|驱动|泵|风机|传送)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 判断 Finding 是否属于安全/手自动模式类（第二优先级）。
        /// </summary>
        private static bool IsSafetyOrModeFinding(StationAnchorFinding f)
        {
            var combined = $"{f.DisplayName} {f.VariableName}";
            return System.Text.RegularExpressions.Regex.IsMatch(combined,
                @"(Safe|Safety|Door|Guard|Fence|Manual|Auto|Mode|Enable|安全门|防护|手动|自动|模式|使能)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 判断 Finding 是否属于外部机器人/设备反馈类（第三优先级）。
        /// </summary>
        private static bool IsExternalRobotFinding(StationAnchorFinding f)
        {
            var combined = $"{f.DisplayName} {f.VariableName}";
            return System.Text.RegularExpressions.Regex.IsMatch(combined,
                @"(Robot|AGV|Vision|Camera|Scanner|RFID|机器人|视觉|扫码|条码)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 从 findings 列表中选出最具代表性的 N 个。
        /// 优先级：① 气缸/电机/阀门类执行机构 → ② 安全门/手自动模式 → ③ 外部机器人/设备反馈 → ④ 其他。
        /// 同层内按信息丰富度（有失败路径、有历史分析、FB深追踪、Diagnosed）打分排序。
        /// 同时确保不同功能块(BlockName)都有代表，避免全部来自同一个块。
        /// </summary>
        private static List<StationAnchorFinding> SelectRepresentativeFindings(
            List<StationAnchorFinding> findings, int maxCount)
        {
            if (findings.Count <= maxCount)
                return findings;

            // 打分：物理设备类型优先级（最重要），再按信息丰富度
            var scored = findings.Select(f => new
            {
                Finding = f,
                Score =
                    // 层级1：气缸/电机/阀门类执行机构（最有诊断价值，最常见停机根因）
                    (!IsProcessHandshakeFinding(f) && IsMechanicalActuatorFinding(f) ? 20 : 0)
                    // 层级2：安全门/手自动模式类
                  + (IsSafetyOrModeFinding(f) ? 10 : 0)
                    // 层级3：外部机器人/设备反馈
                  + (IsExternalRobotFinding(f) ? 5 : 0)
                    // 信息丰富度加分（有失败路径、历史分析、深追踪的更有说服力）
                  + (string.IsNullOrWhiteSpace(f.FailurePathSummary) ? 0 : 3)
                  + (string.IsNullOrWhiteSpace(f.SignalHistoryAnalysis) ? 0 : 2)
                  + (f.TraceDepth > 0 ? 2 : 0)
                  + (f.DiagnosisQuality == "Diagnosed" ? 1 : 0)
                    // TRUE（Transition未满足/报警激活）比大量False更有诊断价值
                  + (f.EvaluatedValue == true ? 1 : 0)
            }).ToList();

            // 先按功能块分散选取（每块至少1个代表），再按得分补全
            var result = new List<StationAnchorFinding>();
            var blockGroups = scored
                .Where(s => !string.IsNullOrWhiteSpace(s.Finding.BlockName))
                .GroupBy(s => s.Finding.BlockName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Max(s => s.Score))
                .ToList();

            // 每个块取最高分的1个
            foreach (var bg in blockGroups)
            {
                if (result.Count >= maxCount) break;
                var best = bg.OrderByDescending(s => s.Score).First().Finding;
                if (!result.Contains(best))
                    result.Add(best);
            }

            // 补全到 maxCount
            foreach (var s in scored.OrderByDescending(s => s.Score))
            {
                if (result.Count >= maxCount) break;
                if (!result.Contains(s.Finding))
                    result.Add(s.Finding);
            }

            return result;
        }

        private static LineDiagnosisLlmReport ParseResponse(string content)
        {
            try
            {
                var json = ExtractJsonFromResponse(content);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var report = new LineDiagnosisLlmReport
                {
                    Success = true,
                    RootCauseStation = root.TryGetProperty("rootCauseStation", out var rcs) ? rcs.GetString() : null,
                    RootCauseDescription = root.TryGetProperty("rootCauseDescription", out var rcd) ? rcd.GetString() : null,
                    Confidence = root.TryGetProperty("confidence", out var conf) && conf.TryGetDouble(out var confVal) ? confVal : 0.5,
                    ImpactChain = root.TryGetProperty("impactChain", out var ic) ? ic.GetString() : null,
                    Reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() : content,
                    RawResponse = content,
                };

                if (root.TryGetProperty("stationAnalysis", out var sa) && sa.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in sa.EnumerateArray())
                    {
                        report.StationAnalysis.Add(new LineDiagnosisStationAnalysis
                        {
                            StationCode = item.TryGetProperty("stationCode", out var sc) ? sc.GetString() : null,
                            Role = item.TryGetProperty("role", out var role) ? role.GetString() : "Unknown",
                            Summary = item.TryGetProperty("summary", out var sum) ? sum.GetString() : null,
                        });
                    }
                }

                if (root.TryGetProperty("secondaryCauses", out var sc2) && sc2.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in sc2.EnumerateArray())
                    {
                        var stationCode = item.TryGetProperty("stationCode", out var sec) ? sec.GetString() : null;
                        var desc = item.TryGetProperty("description", out var d) ? d.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(stationCode))
                        {
                            report.SecondaryCauses.Add(new SecondaryCauseItem
                            {
                                StationCode = stationCode,
                                Description = desc,
                            });
                        }
                    }
                }

                if (root.TryGetProperty("suggestedActions", out var actions) && actions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var action in actions.EnumerateArray())
                    {
                        var text = action.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            report.SuggestedActions.Add(text);
                        }
                    }
                }

                NormalizePublicEvidenceSourceLanguage(report);
                return report;
            }
            catch (JsonException)
            {
                return new LineDiagnosisLlmReport
                {
                    Success = true,
                    Confidence = 0.3,
                    Reasoning = content,
                    RawResponse = content,
                    RootCauseDescription = NormalizePublicEvidenceSourceLanguage(content),
                };
            }
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

        internal static string NormalizePublicEvidenceSourceLanguage(string text)
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

        private static string ExtractJsonFromResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            var trimmed = text.Trim();

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

            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            return trimmed;
        }

        /// <summary>
        /// 工位级轻量 LLM 诊断：单轮 prompt，不使用工具，返回简短文本摘要。
        /// </summary>
        public async Task<string> AnalyzeStationAsync(
            StationDiagnosisSummaryDto summary,
            string outputLanguage = "zh-CN",
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputLanguage = NormalizeOutputLanguage(outputLanguage);
            var isEnglishOutput = IsEnglishOutput(outputLanguage);
            if (summary == null || ((summary.KeyFindings?.Count ?? 0) == 0 && (summary.ActiveAlarms?.Count ?? 0) == 0))
                return null;

            var stationEvidence = BuildEvidenceProfiles(new() { summary });
            if (!stationEvidence.Any(p => p.HasConfirmedFaultEvidence))
            {
                var insufficientReport = new LineDiagnosisLlmReport { Success = true };
                ApplyEvidenceArbitration(insufficientReport, stationEvidence, outputLanguage);
                return insufficientReport.RootCauseDescription;
            }

            var sb = new StringBuilder();
            if (isEnglishOutput)
            {
                sb.AppendLine($"You are a Siemens PLC production-line engineer. Analyze station {summary.StationCode} and summarize the likely fault cause in 2-3 concise English sentences.");
                sb.AppendLine("Keep PLC variable names, station codes and IO addresses unchanged.");
            }
            else
            {
                sb.AppendLine($"你是一名西门子 PLC 产线工程师。请分析工位 {summary.StationCode} 的诊断数据，用 2-3 句话总结可能的故障原因。");
            }
            sb.AppendLine();
            sb.AppendLine(isEnglishOutput
                ? $"Strategy: {summary.DiagnosisStrategy}"
                : $"策略: {summary.DiagnosisStrategy}");
            sb.AppendLine(isEnglishOutput
                ? $"Anchors: total {summary.AnchorCount}, evaluated {summary.EvaluatedCount}, FALSE {summary.FalseAnchorCount}"
                : $"锚点: 共 {summary.AnchorCount} 个, 求值 {summary.EvaluatedCount} 个, FALSE {summary.FalseAnchorCount} 个");
            sb.AppendLine();
            sb.AppendLine(isEnglishOutput ? "Abnormal anchors:" : "异常锚点:");
            // 过滤已满足的 Transition 条件（对单站点 AI 分析同样无意义）
            var stationLlmFindings = (summary.KeyFindings ?? new())
                .Where(f => f != null && !(f.DiagnosisPhase == "GraphTransitionTrace" && f.EvaluatedValue == true))
                .ToList();
            foreach (var f in stationLlmFindings.Take(10)) // 最多 10 个，控制 token
            {
                sb.AppendLine($"- {f.DisplayName ?? f.VariableName}: {f.EvaluationStatus}");
                if (!string.IsNullOrWhiteSpace(f.DiagnosisQuality))
                    sb.AppendLine(isEnglishOutput
                        ? $"  Diagnosis: {f.DiagnosisQuality}"
                        : $"  诊断: {f.DiagnosisQuality}");
            }
            if (stationLlmFindings.Count > 10)
                sb.AppendLine(isEnglishOutput
                    ? $"  ...({stationLlmFindings.Count - 10} more abnormal anchors omitted)"
                    : $"  ...（还有 {stationLlmFindings.Count - 10} 个异常锚点省略）");

            sb.AppendLine();
            sb.AppendLine(isEnglishOutput
                ? "Return only the short fault analysis in English. Do not output JSON or a title."
                : "请直接输出简短的故障分析（2-3句话），不要 JSON，不要标题。");

            string modelContent = null;
            try
            {
                var agent = await _agentRuntimeService.CreateAgentByRoleAsync(AppsettingsConfig.GetDiagnosticAgentRoleId());
                cancellationToken.ThrowIfCancellationRequested();
                var response = await _agentRuntimeService.RunAgentAsync(agent, sb.ToString(), null, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                modelContent = response.Content;
                if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
                    _logger?.LogWarning("[单工位LLM] 模型不可用，原始结果仅作审计: {Response}", modelContent);
                return CompleteModelResponse(response.Success, response.Content, stationEvidence, outputLanguage).RootCauseDescription;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger?.LogWarning(ex, "[单工位LLM] 模型异常，原始结果仅作审计: {Response}", modelContent);
                return BuildEvidenceOnlyReport(new() { summary }, outputLanguage: outputLanguage).RootCauseDescription;
            }
        }
    }
}
