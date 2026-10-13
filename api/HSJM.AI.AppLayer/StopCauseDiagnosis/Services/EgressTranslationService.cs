using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.Hubs;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 出口翻译补丁（第 3 层 LLM 兜底翻译的产物）。
    /// Path 为相对于 LineDiagnosisResultDto（前端 diagnosisResult）的 camelCase 路径，
    /// 如 constraintReport / warnings[0] / stationSummaries[2].keyFindings[0].failurePathSummary。
    /// </summary>
    public class EgressTextPatch
    {
        public string Path { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// 翻译跟随事件：原文先推（LineDiagnosisCompleted），译文译好后补推本事件，前端原位替换。
    /// final=true 表示本任务翻译流结束（前端可安全断开 SignalR）。
    /// </summary>
    public class LineDiagnosisContentTranslatedDto
    {
        public string TaskId { get; set; }
        public string Lang { get; set; }
        public List<EgressTextPatch> Patches { get; set; } = new List<EgressTextPatch>();
        public bool Final { get; set; }
        public int SkippedCount { get; set; }
    }

    /// <summary>
    /// 第 3 层出口翻译管道：对 SignalR 推送前的结果做 LLM 兜底翻译。
    /// 覆盖第 1 层（LLM 直生）与第 2 层（固定字典）之外的自由文本：
    /// KeyFindings / TemplateMatchInfo / ConstraintReport / Warnings / DB 嵌入数据等。
    /// 设计约束：
    /// - 永不阻塞原始事件推送（原文先行，译文跟随）
    /// - 缓存命中零成本；未命中按预算限流（条数/字符/并行度）
    /// - 术语保护：PLC 信号名 / %IO 地址 / DB 名 / FB 块名 / 工位号原样保留
    /// - LLM 不可用时静默降级（原文已送达，仅少一份译文）
    /// </summary>
    public class EgressTranslationService : IAutoInject, ISelfScopedAutoInject
    {
        private const int MaxItems = 80;
        private const int MaxItemChars = 4000;
        private const int MaxTotalChars = 24000;
        private const int MaxParallelism = 3;
        private const int PerCallTimeoutMs = 60_000;

        private static readonly ConcurrentDictionary<string, string> Cache = new ConcurrentDictionary<string, string>();

        // 术语保护：命中即包进 ⟦⟧，LLM 指令要求原样保留
        private static readonly Regex[] ProtectPatterns =
        {
            new Regex(@"%[IQM]\d+(?:\.\d+)?", RegexOptions.Compiled),                      // %I1331.2 / %Q1067.5
            new Regex(@"\b(?:FB|FC|DB|OB)\d+[A-Za-z0-9_]*", RegexOptions.Compiled),         // FB460_MainGraph / DB420_OP100Manual
            new Regex(@"\bOP\d{3}[A-Za-z0-9-]*", RegexOptions.Compiled),                    // OP030 / OP010-SD01
            new Regex(@"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+|\.[A-Za-z0-9_.\[\]-]+)+", RegexOptions.Compiled), // 含 _ 或 . 的标识符
        };

        private static readonly Regex CjkRegex = new Regex("[\u4e00-\u9fff]", RegexOptions.Compiled);

        private readonly IHubContext<StationShadowMonitoringHub> _hubContext;
        private readonly AgentRuntimeService _agentRuntimeService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<EgressTranslationService> _logger;

        private ChatClientAgent _translateAgent;
        private readonly SemaphoreSlim _agentLock = new SemaphoreSlim(1, 1);

        public EgressTranslationService(
            IHubContext<StationShadowMonitoringHub> hubContext,
            AgentRuntimeService agentRuntimeService,
            IConfiguration configuration,
            ILogger<EgressTranslationService> logger)
        {
            _hubContext = hubContext;
            _agentRuntimeService = agentRuntimeService;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>配置开关 WebConfig:ResponseI18n:Enabled，缺省开启。</summary>
        public bool IsEnabled
        {
            get
            {
                if (_configuration == null) return true;
                var section = _configuration.GetSection("WebConfig:ResponseI18n");
                if (!section.Exists()) return true;
                return section.GetValue<bool?>("Enabled") != false;
            }
        }

        /// <summary>zh-CN 是规范存储语言，无需出口翻译。</summary>
        public bool NeedsTranslation(string outputLanguage) =>
            IsEnabled && !LineDiagnosisI18n.Normalize(outputLanguage).Equals(LineDiagnosisI18n.ZhCn, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Completed 事件的翻译跟随：收集可译字段 → 缓存命中直取 / 未命中调 LLM → 批量补推译文。
        /// 无论成败都会推一条 final 事件（前端依赖它安全断连）。
        /// </summary>
        public async Task FollowUpCompletedAsync(string taskId, LineDiagnosisResultDto result, string outputLanguage)
        {
            var lang = LineDiagnosisI18n.Normalize(outputLanguage);
            var @event = new LineDiagnosisContentTranslatedDto
            {
                TaskId = taskId,
                Lang = lang,
                Final = true,
            };

            try
            {
                if (result != null)
                {
                    var targets = CollectTargets(result, lang);
                    var patches = new List<EgressTextPatch>();
                    var skipped = 0;
                    var totalBudget = MaxTotalChars;

                    using var throttler = new SemaphoreSlim(MaxParallelism, MaxParallelism);
                    var tasks = targets.Take(MaxItems).Select(async t =>
                    {
                        if (t.Text == null || t.Text.Length > MaxItemChars || t.Text.Length > totalBudget)
                        {
                            Interlocked.Increment(ref skipped);
                            return;
                        }
                        Interlocked.Add(ref totalBudget, -t.Text.Length);

                        await throttler.WaitAsync();
                        try
                        {
                            var translated = await TranslateAsync(t.Text, lang);
                            if (!string.IsNullOrWhiteSpace(translated))
                            {
                                lock (patches)
                                {
                                    patches.Add(new EgressTextPatch { Path = t.Path, Value = translated });
                                }
                            }
                            else
                            {
                                Interlocked.Increment(ref skipped);
                            }
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    }).ToList();

                    await Task.WhenAll(tasks);
                    @event.Patches = patches;
                    @event.SkippedCount = skipped;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[出口翻译] Completed 跟随翻译异常 TaskId={TaskId} Lang={Lang}", taskId, lang);
            }

            try
            {
                var group = StationShadowMonitoringHub.BuildLineDiagnosisGroup(taskId);
                await _hubContext.Clients.Group(group).SendAsync("LineDiagnosisContentTranslated", @event);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[出口翻译] 推送译文事件失败 TaskId={TaskId}", taskId);
            }
        }

        /// <summary>
        /// 翻译单段文本（带缓存 + 术语保护 + LLM 调用）。失败返回 null（调用方跳过补丁）。
        /// </summary>
        public async Task<string> TranslateAsync(string text, string outputLanguage)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var lang = LineDiagnosisI18n.Normalize(outputLanguage);
            if (lang.Equals(LineDiagnosisI18n.ZhCn, StringComparison.OrdinalIgnoreCase)) return text;

            // 纯 ASCII（无中文）默认已是英文/中性文本，不浪费 LLM 调用
            if (!CjkRegex.IsMatch(text)) return text;

            var cacheKey = lang + "|" + text;
            if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

            var protectedText = ProtectTerms(text);
            var agent = await GetOrCreateAgentAsync(lang);
            if (agent == null) return null;

            using var cts = new CancellationTokenSource(PerCallTimeoutMs);
            var response = await _agentRuntimeService.RunAgentAsync(agent, protectedText, null, cts.Token);
            if (response == null || !response.Success || string.IsNullOrWhiteSpace(response.Content))
            {
                return null;
            }

            var result = UnprotectTerms(response.Content.Trim());
            Cache[cacheKey] = result;
            return result;
        }

        private async Task<ChatClientAgent> GetOrCreateAgentAsync(string lang)
        {
            if (_translateAgent != null) return _translateAgent;
            await _agentLock.WaitAsync();
            try
            {
                if (_translateAgent != null) return _translateAgent;
                var languageName = LineDiagnosisI18n.LanguageName(lang);
                var instructions =
                    "You are a professional translator for industrial PLC diagnosis software. " +
                    $"Translate the user text into {languageName}. " +
                    "Tokens wrapped in \u27e6 \u27e7 are PLC identifiers (signal names, IO addresses, DB/FB block names, station codes) and must be copied verbatim, unchanged. " +
                    "Keep numbers, units, punctuation structure and markdown lists. " +
                    "Output ONLY the translated text, with no explanations and no quotes.";
                _translateAgent = await _agentRuntimeService.CreateSimpleAgentAsync(instructions, "EgressTranslator");
                return _translateAgent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[出口翻译] 创建翻译 Agent 失败（LLM 不可用则降级原文）");
                return null;
            }
            finally
            {
                _agentLock.Release();
            }
        }

        internal static string ProtectTerms(string text)
        {
            foreach (var pattern in ProtectPatterns)
            {
                text = pattern.Replace(text, m => "\u27e6" + m.Value + "\u27e7");
            }
            return text;
        }

        internal static string UnprotectTerms(string text) =>
            text.Replace("\u27e6", "").Replace("\u27e7", "");

        /// <summary>
        /// 收集结果中需要出口翻译的字段（白名单，防止误伤数据契约字段）。
        /// llmReport 字段仅对非 en-US 的非中文语言收集（en-US 由直生覆盖、zh-CN 无需翻译）。
        /// </summary>
        internal static List<(string Path, string Text, Action<string> Set)> CollectTargets(
            LineDiagnosisResultDto result, string lang)
        {
            var targets = new List<(string, string, Action<string>)>();

            void Add(string path, string current, Action<string> set)
            {
                if (!string.IsNullOrWhiteSpace(current)) targets.Add((path, current, set));
            }

            Add("constraintReport", result.ConstraintReport, v => result.ConstraintReport = v);

            if (result.Warnings != null)
            {
                for (var i = 0; i < result.Warnings.Count; i++)
                {
                    var idx = i;
                    Add($"warnings[{idx}]", result.Warnings[idx], v => result.Warnings[idx] = v);
                }
            }

            var llm = result.LlmReport;
            var llmNeeds = !lang.Equals(LineDiagnosisI18n.EnUs, StringComparison.OrdinalIgnoreCase);
            if (llm != null && llmNeeds)
            {
                Add("llmReport.rootCauseDescription", llm.RootCauseDescription, v => llm.RootCauseDescription = v);
                Add("llmReport.impactChain", llm.ImpactChain, v => llm.ImpactChain = v);
                Add("llmReport.reasoning", llm.Reasoning, v => llm.Reasoning = v);
                Add("llmReport.toolCallSummary", llm.ToolCallSummary, v => llm.ToolCallSummary = v);
                if (llm.SuggestedActions != null)
                {
                    for (var i = 0; i < llm.SuggestedActions.Count; i++)
                    {
                        var idx = i;
                        Add($"llmReport.suggestedActions[{idx}]", llm.SuggestedActions[idx], v => llm.SuggestedActions[idx] = v);
                    }
                }
                if (llm.StationAnalysis != null)
                {
                    for (var i = 0; i < llm.StationAnalysis.Count; i++)
                    {
                        var idx = i;
                        Add($"llmReport.stationAnalysis[{idx}].summary", llm.StationAnalysis[idx].Summary, v => llm.StationAnalysis[idx].Summary = v);
                    }
                }
                if (llm.SecondaryCauses != null)
                {
                    for (var i = 0; i < llm.SecondaryCauses.Count; i++)
                    {
                        var idx = i;
                        Add($"llmReport.secondaryCauses[{idx}].description", llm.SecondaryCauses[idx].Description, v => llm.SecondaryCauses[idx].Description = v);
                    }
                }
            }

            if (result.StationSummaries != null)
            {
                for (var i = 0; i < result.StationSummaries.Count; i++)
                {
                    var summary = result.StationSummaries[i];
                    var sIdx = i;
                    Add($"stationSummaries[{sIdx}].templateMatchInfo", summary.TemplateMatchInfo, v => summary.TemplateMatchInfo = v);
                    Add($"stationSummaries[{sIdx}].errorMessage", summary.ErrorMessage, v => summary.ErrorMessage = v);

                    if (summary.ActiveAlarms != null)
                    {
                        for (var k = 0; k < summary.ActiveAlarms.Count; k++)
                        {
                            var alarm = summary.ActiveAlarms[k];
                            var aIdx = k;
                            Add($"stationSummaries[{sIdx}].activeAlarms[{aIdx}].text", alarm.Text, v => alarm.Text = v);
                        }
                    }

                    if (summary.KeyFindings == null) continue;
                    for (var j = 0; j < summary.KeyFindings.Count; j++)
                    {
                        var finding = summary.KeyFindings[j];
                        var fIdx = j;
                        Add($"stationSummaries[{sIdx}].keyFindings[{fIdx}].failurePathSummary", finding.FailurePathSummary, v => finding.FailurePathSummary = v);
                        Add($"stationSummaries[{sIdx}].keyFindings[{fIdx}].signalHistoryAnalysis", finding.SignalHistoryAnalysis, v => finding.SignalHistoryAnalysis = v);
                        Add($"stationSummaries[{sIdx}].keyFindings[{fIdx}].ioTagComment", finding.IoTagComment, v => finding.IoTagComment = v);
                    }
                }
            }

            return targets;
        }
    }
}
