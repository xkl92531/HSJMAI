using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 第五阶段：基于 AST + 物理Q点 + IOTag 上下文，交由 LLM 统一识别虚拟Q点、握手位和 IoMapping。
    /// </summary>
    public class AstSemanticInferenceService : ISelfScopedAutoInject
    {
        private const int BatchSize = 12;
        private const int MaxPromptSnippetLength = 1200;
        private const int MaxLogContentLength = 6000;
        private static readonly Regex XmlTagRegex = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex AssignmentRegex = new Regex(@"(?<left>[^:=;\r\n]+):=(?<right>[^;\r\n]+);", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex QuotedTokenRegex = new Regex("\"(?<value>[^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex ReferenceTokenRegex = new Regex(@"(?<value>[A-Za-z_][A-Za-z0-9_\-]*(?:\.[A-Za-z_][A-Za-z0-9_\-]*|\[[0-9]+\])*)", RegexOptions.Compiled);
        private static readonly Regex StationCodeRegex = new Regex(@"OP\d{2,3}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const string AstSemanticSystemPrompt = @"
你是一名工业自动化 PLC 代码分析专家，专注于西门子 TIA Portal。
你会收到一组已经完成 AST 转换的 PLC 写入逻辑片段，每个片段都带有：
- 工位
- 块名 / 逻辑单元类型 / 编程语言
- AST 摘要 / AST 白话解释
- 输入变量
- 输出变量
- AST 控制逻辑表达式（自然语言，描述完整控制条件与输出动作）
- 同工位物理 Q 点 IOTag 清单

> 重要：AST 控制逻辑表达式是判断变量用途的最核心依据。请优先依据表达式中的输出变量、动作类型（置位/复位/写入/赋值）和条件链路来分类。

你的任务：
1. 判断每个输出变量是否属于 VirtualQPoint、HandshakeSignal 或 Ignore。
2. 若能判断，请给出简洁中文 semanticLabel。
3. 若该输出变量与某个 IOTag 存在明显语义映射关系，请返回 mappedIoTags 和 ioMapping。

严格规则：
- VirtualQPoint：内部变量，无物理 %Q 地址，但承担动作命令、动作触发、使能、启动、置位等控制输出角色。
  典型命名特征：*Cmd, *Enable, *Start, *Run, *Open, *Close, *Forward, *Backward, *Up, *Down, *Clamp, *Unclamp, *Push, *Pull, *Lift, *Lower, *Activate 等。
  典型逻辑特征：在满足一组安全条件/步骤条件判断后被置位(Set)或写入 TRUE，代表执行某个动作。
- HandshakeSignal：与机器人、MES、视觉、扫码、AGV、上位机等系统协商的请求/允许/完成/应答类信号。
  典型命名特征：*Req, *Ack, *Done, *Allow, *Busy, *Ready, *Complete, *Request, *Response, *Handshake 等。
- Ignore：普通状态位、中间标记、流程步号、纯计数器/定时器中间输出、温度/速度等模拟量中间值。
  只有当变量明确不承担动作控制或系统协商功能时才归为 Ignore，不要轻易将可能的控制信号归类为 Ignore。
- mappedIoTags 必须只返回当前提供的物理 IOTag 标签名。
- 如果看不出稳定映射，就返回空数组和空 ioMapping。

返回 JSON 数组，不要输出任何额外文字。每项字段：
- logicUnitId: 原样返回
- variableName: 原样返回
- category: VirtualQPoint / HandshakeSignal / Ignore
- semanticLabel: 不超过20字，可为空
- reason: 不超过80字
- confidence: 0 到 1
- mappedIoTags: 字符串数组
- ioMapping: 简洁映射描述，可为空
";

        public int MaxConcurrency { get; set; } = 4;

        private readonly AgentRuntimeService _agentRuntimeService;
        private readonly ILogger<AstSemanticInferenceService> _logger;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly AstSemanticProgressService _progressService;
        private readonly AstMarkdownRendererService _astMarkdownRenderer;

        public AstSemanticInferenceService(
            AgentRuntimeService agentRuntimeService,
            ILogger<AstSemanticInferenceService> logger,
            IPLC_IOTagService ioTagService,
            AstSemanticProgressService progressService,
            AstMarkdownRendererService astMarkdownRenderer)
        {
            _agentRuntimeService = agentRuntimeService;
            _logger = logger;
            _ioTagService = ioTagService;
            _progressService = progressService;
            _astMarkdownRenderer = astMarkdownRenderer;
        }

        public async Task<List<AstSemanticFinding>> InferAsync(
            AnchorDiscoveryRequestDto request,
            IReadOnlyList<StopCauseAstCompileResult> compileResults,
            IReadOnlyList<AnchorCandidate> physicalAnchors)
        {
            var hardwareIoTagsByStation = await LoadHardwareIoTagsByStationAsync(request.ProjectId);
            var candidates = BuildCandidates(compileResults, physicalAnchors, hardwareIoTagsByStation);

            // 批量获取 AST 控制逻辑表达式（单次DB查询），替代原始XML提供给LLM
            var allOutputNames = candidates.Select(c => c.VariableName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var astExpressions = await _astMarkdownRenderer.BatchRenderVariableExpressionsAsync(request.ProjectId, allOutputNames);
            foreach (var candidate in candidates)
            {
                if (astExpressions.TryGetValue(candidate.VariableName, out var expr))
                {
                    candidate.AstControlLogicExpression = expr;
                }
            }

            // 过滤掉上下文不足的候选：没有AST表达式、没有有效叙述、AST只是Reference占位的候选
            // 对LLM没有判断价值，只浪费token
            var preFilterCount = candidates.Count;
            candidates = candidates.Where(c => !IsLowContextCandidate(c)).ToList();
            if (candidates.Count < preFilterCount)
            {
                _logger.LogInformation(
                    "[AstSemanticInference] 候选上下文过滤: {Before} -> {After}, 移除 {Removed} 个低上下文候选",
                    preFilterCount, candidates.Count, preFilterCount - candidates.Count);
            }

            var progressKey = request?.SemanticProgressKey;
            if (candidates.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(progressKey))
                {
                    _progressService.Start(progressKey, 0, 0);
                    _progressService.Complete(progressKey, 0, "没有命中可用于 Q 点语义分析的 AST 片段");
                }
                return new List<AstSemanticFinding>();
            }

            var batches = candidates
                .Select((item, index) => new { item, index })
                .GroupBy(it => it.index / BatchSize)
                .Select(g => g.Select(x => x.item).ToList())
                .ToList();

            if (!string.IsNullOrWhiteSpace(progressKey))
            {
                _progressService.Start(progressKey, candidates.Count, batches.Count);
            }

            var sem = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var findings = new ConcurrentBag<AstSemanticFinding>();
            var completedCandidates = 0;
            var completedBatches = 0;
            var foundCount = 0;

            var tasks = batches.Select(async batch =>
            {
                await sem.WaitAsync();
                try
                {
                    var batchFindings = await CallBatchAsync(batch);
                    foreach (var finding in batchFindings)
                    {
                        findings.Add(finding);
                    }

                    if (!string.IsNullOrWhiteSpace(progressKey))
                    {
                        var currentCandidates = Interlocked.Add(ref completedCandidates, batch.Count);
                        var currentBatches = Interlocked.Increment(ref completedBatches);
                        var currentFound = Interlocked.Add(ref foundCount, batchFindings.Count);
                        _progressService.Advance(
                            progressKey,
                            currentCandidates,
                            currentBatches,
                            currentFound,
                            $"正在分析，已完成 {Math.Min(currentCandidates, candidates.Count)}/{candidates.Count} 个候选输出变量");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AstSemanticInference] LLM AST 语义批次失败，跳过本批次");

                    if (!string.IsNullOrWhiteSpace(progressKey))
                    {
                        var currentCandidates = Interlocked.Add(ref completedCandidates, batch.Count);
                        var currentBatches = Interlocked.Increment(ref completedBatches);
                        _progressService.Advance(
                            progressKey,
                            currentCandidates,
                            currentBatches,
                            foundCount,
                            $"已跳过一个失败批次，当前完成 {Math.Min(currentCandidates, candidates.Count)}/{candidates.Count} 个候选输出变量");
                    }
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);

            var result = findings
                .GroupBy(it => $"{it.LogicUnitId}:{it.VariableName}:{it.Category}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(it => it.Confidence)
                    .ThenByDescending(it => it.RelatedIOTags.Count)
                    .First())
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .ToList();

            await PersistIoMappingsAsync(request.ProjectId, result);

            if (!string.IsNullOrWhiteSpace(progressKey))
            {
                _progressService.Complete(progressKey, result.Count);
            }

            return result;
        }

        public LlmDiscoveryDiagnostics BuildDiagnostics(IReadOnlyList<AstSemanticFinding> findings, int candidateCount)
        {
            var result = findings ?? new List<AstSemanticFinding>();
            return new LlmDiscoveryDiagnostics
            {
                TotalContextCount = candidateCount,
                StationFilteredCount = candidateCount,
                SentToLlmCount = candidateCount,
                LlmReturnedCount = result.Count,
                ConfirmedByLlmCount = result.Count(it => string.Equals(it.Category, "VirtualQPoint", StringComparison.OrdinalIgnoreCase)),
                HandshakeByLlmCount = result.Count(it => string.Equals(it.Category, "HandshakeSignal", StringComparison.OrdinalIgnoreCase)),
                ReviewByLlmCount = 0,
                UnknownDroppedCount = result.Count(it => string.Equals(it.Category, "Ignore", StringComparison.OrdinalIgnoreCase))
            };
        }

        public int CountCandidates(IReadOnlyList<StopCauseAstCompileResult> compileResults, IReadOnlyList<AnchorCandidate> physicalAnchors)
        {
            return BuildCandidates(
                compileResults,
                physicalAnchors,
                new Dictionary<string, List<PLC_IOTagEntity>>(StringComparer.OrdinalIgnoreCase)).Count;
        }

        private async Task<List<AstSemanticFinding>> CallBatchAsync(IReadOnlyList<AstSemanticCandidate> batch)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请对下列 AST 输出变量逐一完成语义分类与 IoMapping 识别：");
            sb.AppendLine();

            foreach (var item in batch)
            {
                sb.AppendLine($"logicUnitId: {item.LogicUnitId}");
                sb.AppendLine($"variableName: {item.VariableName}");
                if (!string.IsNullOrWhiteSpace(item.StationCode))
                {
                    sb.AppendLine($"工位: {item.StationCode}");
                }

                sb.AppendLine($"块名: {item.BlockName}");
                if (!string.IsNullOrWhiteSpace(item.Title))
                {
                    sb.AppendLine($"标题: {item.Title}");
                }
                sb.AppendLine($"语言: {item.ProgrammingLanguage}");
                sb.AppendLine($"单元类型: {item.UnitType}");

                if (item.InputVariables.Count > 0)
                {
                    sb.AppendLine($"输入变量: {string.Join(", ", item.InputVariables.Take(12))}");
                }

                sb.AppendLine($"输出变量: {string.Join(", ", item.OutputVariables.Take(8))}");

                if (!string.IsNullOrWhiteSpace(item.AstSummary))
                {
                    sb.AppendLine($"AST摘要: {item.AstSummary}");
                }

                if (!string.IsNullOrWhiteSpace(item.AstNarrative))
                {
                    sb.AppendLine($"AST白话解释: {item.AstNarrative}");
                }

                if (!string.IsNullOrWhiteSpace(item.AstControlLogicExpression))
                {
                    sb.AppendLine($"AST控制逻辑表达式: {item.AstControlLogicExpression}");
                }
                else if (!string.IsNullOrWhiteSpace(item.RawContentExcerpt))
                {
                    // AST 表达式不可用时，保留可读文本作为兜底
                    var readableSnippet = ExtractReadableSnippet(item.RawContentExcerpt);
                    if (!string.IsNullOrWhiteSpace(readableSnippet))
                    {
                        sb.AppendLine($"可读文本片段: {TruncateForPrompt(readableSnippet)}");
                    }
                }

                if (item.PhysicalTagHints.Count > 0)
                {
                    sb.AppendLine("同工位物理Q点:");
                    foreach (var hint in item.PhysicalTagHints.Take(8))
                    {
                        sb.AppendLine($"- {hint}");
                    }
                }

                if (item.DeterministicMappedIoTags.Count > 0)
                {
                    sb.AppendLine($"确定性IoTag映射候选: {string.Join(", ", item.DeterministicMappedIoTags.Take(8))}");
                }

                if (!string.IsNullOrWhiteSpace(item.DeterministicIoMapping))
                {
                    sb.AppendLine($"确定性IoMapping: {item.DeterministicIoMapping}");
                }

                sb.AppendLine();
            }

            var agent = await _agentRuntimeService.CreateSimpleAgentAsync(
                instructions: AstSemanticSystemPrompt,
                agentName: "AstSemanticClassifier");

            _logger.LogInformation(
                "[AstSemanticInference] Batch prompt ready. CandidateCount={CandidateCount} LogicUnitIds={LogicUnitIds} PromptSample={PromptSample}",
                batch.Count,
                string.Join(",", batch.Select(it => it.LogicUnitId).Distinct().Take(20)),
                TruncateForLog(sb.ToString()));
            await WriteTraceLogAsync(
                $"[AstSemanticInference] Batch prompt ready. CandidateCount={batch.Count}; LogicUnitIds={string.Join(",", batch.Select(it => it.LogicUnitId).Distinct().Take(20))}; PromptSample={TruncateForLog(sb.ToString())}");

            try { await WriteLogHelper.WriteLogsAsync($"=== System Prompt ===\n{AstSemanticSystemPrompt}\n\n=== User Prompt ({batch.Count} candidates) ===\n{sb}", "QPointSemantic_LLM_Input"); } catch { }

            var response = await _agentRuntimeService.RunAgentAsync(agent, sb.ToString());
            _logger.LogInformation(
                "[AstSemanticInference] Batch response received. Success={Success} CandidateCount={CandidateCount} Error={Error} ResponseSample={ResponseSample}",
                response.Success,
                batch.Count,
                response.ErrorMessage ?? string.Empty,
                TruncateForLog(response.Content));
            await WriteTraceLogAsync(
                $"[AstSemanticInference] Batch response received. Success={response.Success}; CandidateCount={batch.Count}; Error={response.ErrorMessage ?? string.Empty}; ResponseSample={TruncateForLog(response.Content)}");

            try { await WriteLogHelper.WriteLogsAsync($"=== Response (Success={response.Success}, Error={response.ErrorMessage ?? ""}) ===\n{response.Content}", "QPointSemantic_LLM_Output"); } catch { }

            if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("[AstSemanticInference] LLM 返回为空或失败: {Error}", response.ErrorMessage);
                await WriteTraceLogAsync($"[AstSemanticInference] LLM 返回为空或失败. Error={response.ErrorMessage ?? string.Empty}");
                return new List<AstSemanticFinding>();
            }

            var parsed = ParseResponse(response.Content);
            if (parsed.Count == 0)
            {
                _logger.LogWarning(
                    "[AstSemanticInference] LLM 有返回但未解析出有效语义结果。CandidateCount={CandidateCount} ResponseSample={ResponseSample}",
                    batch.Count,
                    TruncateForLog(response.Content));
                await WriteTraceLogAsync(
                    $"[AstSemanticInference] LLM 有返回但未解析出有效语义结果. CandidateCount={batch.Count}; ResponseSample={TruncateForLog(response.Content)}");
            }
            else
            {
                _logger.LogInformation(
                    "[AstSemanticInference] Parsed semantic findings. CandidateCount={CandidateCount} ParsedCount={ParsedCount} Categories={Categories}",
                    batch.Count,
                    parsed.Count,
                    string.Join(", ", parsed.GroupBy(it => it.Category ?? string.Empty).Select(g => $"{g.Key}:{g.Count()}").Take(8)));
                await WriteTraceLogAsync(
                    $"[AstSemanticInference] Parsed semantic findings. CandidateCount={batch.Count}; ParsedCount={parsed.Count}; Categories={string.Join(", ", parsed.GroupBy(it => it.Category ?? string.Empty).Select(g => $"{g.Key}:{g.Count()}").Take(8))}");
            }

            var candidateMap = batch.ToDictionary(
                it => $"{it.LogicUnitId}:{it.VariableName}",
                it => it,
                StringComparer.OrdinalIgnoreCase);

            foreach (var finding in parsed)
            {
                if (!candidateMap.TryGetValue($"{finding.LogicUnitId}:{finding.VariableName}", out var candidate))
                {
                    continue;
                }

                finding.BlockName = candidate.BlockName;
                finding.StationCode = candidate.StationCode;

                if (candidate.DeterministicMappedIoTags.Count > 0)
                {
                    finding.RelatedIOTags = candidate.DeterministicMappedIoTags
                        .Concat(finding.RelatedIOTags ?? new List<string>())
                        .Where(it => !string.IsNullOrWhiteSpace(it))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                if (!string.IsNullOrWhiteSpace(candidate.DeterministicIoMapping))
                {
                    finding.IoMapping = candidate.DeterministicIoMapping;
                }

                if (!string.IsNullOrWhiteSpace(candidate.DeterministicCategoryOverride))
                {
                    finding.Category = candidate.DeterministicCategoryOverride;
                }

                if (!string.IsNullOrWhiteSpace(candidate.DeterministicSemanticLabelOverride))
                {
                    finding.SemanticLabel = candidate.DeterministicSemanticLabelOverride;
                }

                if (!string.IsNullOrWhiteSpace(candidate.DeterministicReasonOverride))
                {
                    finding.Reason = candidate.DeterministicReasonOverride;
                }

                ApplyIgnoreCategoryIfMappedToInputTag(finding, candidate);
            }

            var existingKeys = parsed
                .Select(it => $"{it.LogicUnitId}:{it.VariableName}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in batch)
            {
                var key = $"{candidate.LogicUnitId}:{candidate.VariableName}";
                if (existingKeys.Contains(key))
                {
                    continue;
                }

                if (candidate.DeterministicMappedIoTags == null || candidate.DeterministicMappedIoTags.Count == 0)
                {
                    continue;
                }

                var syntheticFinding = new AstSemanticFinding
                {
                    LogicUnitId = candidate.LogicUnitId,
                    BlockName = candidate.BlockName,
                    StationCode = candidate.StationCode,
                    VariableName = candidate.VariableName,
                    Category = candidate.DeterministicCategoryOverride ?? "Ignore",
                    SemanticLabel = candidate.DeterministicSemanticLabelOverride ?? "显式IO映射",
                    Reason = candidate.DeterministicReasonOverride ?? "代码中存在明确赋值映射，已按确定性规则补回",
                    Confidence = 0.99m,
                    RelatedIOTags = candidate.DeterministicMappedIoTags
                        .Where(it => !string.IsNullOrWhiteSpace(it))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    IoMapping = candidate.DeterministicIoMapping
                };

                ApplyIgnoreCategoryIfMappedToInputTag(syntheticFinding, candidate);
                parsed.Add(syntheticFinding);
            }

            return parsed;
        }

        private static List<AstSemanticCandidate> BuildCandidates(
            IReadOnlyList<StopCauseAstCompileResult> compileResults,
            IReadOnlyList<AnchorCandidate> physicalAnchors,
            IReadOnlyDictionary<string, List<PLC_IOTagEntity>> hardwareIoTagsByStation)
        {
            var physicalByStation = (physicalAnchors ?? new List<AnchorCandidate>())
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode) && !string.IsNullOrWhiteSpace(it.VariableName))
                .GroupBy(it => it.StationCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(it => $"{it.VariableName} ({it.Address}) {it.SemanticLabel}".Trim()).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var candidates = new List<AstSemanticCandidate>();
            foreach (var compileResult in compileResults ?? new List<StopCauseAstCompileResult>())
            {
                foreach (var report in compileResult.CoverageReports ?? new List<AstCoverageReport>())
                {
                    var outputs = (report.OutputVariables ?? new List<string>())
                        .Where(it => !string.IsNullOrWhiteSpace(it))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (outputs.Count == 0 && !string.IsNullOrWhiteSpace(compileResult.Anchor?.VariableName))
                    {
                        outputs.Add(compileResult.Anchor.VariableName);
                    }

                    if (outputs.Count == 0)
                    {
                        continue;
                    }

                    if (!report.LogicUnitId.HasValue)
                    {
                        continue;
                    }

                    var stationCode = compileResult.Anchor?.StationCode ?? string.Empty;
                    physicalByStation.TryGetValue(stationCode ?? string.Empty, out var physicalHints);
                    hardwareIoTagsByStation ??= new Dictionary<string, List<PLC_IOTagEntity>>(StringComparer.OrdinalIgnoreCase);
                    hardwareIoTagsByStation.TryGetValue(stationCode ?? string.Empty, out var hardwareTags);

                    foreach (var output in outputs)
                    {
                        var relevantNode = BuildRelevantSlice(compileResult.RootNode, report.LogicUnitId.Value, output);
                        var directIoMapping = TryBuildDeterministicIoMapping(report.RawContentExcerpt, output, hardwareTags);
                        candidates.Add(new AstSemanticCandidate
                        {
                            LogicUnitId = report.LogicUnitId.Value,
                            BlockName = report.BlockName,
                            Title = report.Title,
                            StationCode = stationCode,
                            VariableName = output,
                            InputVariables = (report.InputVariables ?? new List<string>())
                                .Where(it => !string.IsNullOrWhiteSpace(it))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList(),
                            OutputVariables = outputs,
                            ProgrammingLanguage = report.SourceType,
                            UnitType = report.UnitType,
                            RawContentExcerpt = report.RawContentExcerpt ?? string.Empty,
                            AstSummary = BuildAstSummary(relevantNode ?? compileResult.RootNode),
                            AstNarrative = BuildAstNarrative(relevantNode ?? compileResult.RootNode),
                            PhysicalTagHints = physicalHints ?? new List<string>(),
                            HardwareIoTags = hardwareTags ?? new List<PLC_IOTagEntity>(),
                            DeterministicMappedIoTags = directIoMapping.MappedIoTags,
                            DeterministicIoMapping = directIoMapping.IoMapping,
                            DeterministicCategoryOverride = directIoMapping.CategoryOverride,
                            DeterministicSemanticLabelOverride = directIoMapping.SemanticLabelOverride,
                            DeterministicReasonOverride = directIoMapping.ReasonOverride
                        });
                    }
                }
            }

            return candidates;
        }

        /// <summary>
        /// 判断候选是否上下文不足，无法为LLM提供有效判断依据。
        /// 只在候选同时满足以下条件时才过滤：
        /// 1. 没有 AST 控制逻辑表达式
        /// 2. AST 叙述为空或仅为 Reference/Opaque 占位描述
        /// 3. 没有确定性 IO 映射
        /// 有任何一项有效上下文的候选都会保留。
        /// </summary>
        private static bool IsLowContextCandidate(AstSemanticCandidate candidate)
        {
            // 有 AST 控制逻辑表达式 → 保留
            if (!string.IsNullOrWhiteSpace(candidate.AstControlLogicExpression))
            {
                return false;
            }

            // 有确定性 IO 映射 → 保留
            if (candidate.DeterministicMappedIoTags != null && candidate.DeterministicMappedIoTags.Count > 0)
            {
                return false;
            }

            // 有有效的 AST 叙述（不是纯占位描述）→ 保留
            if (!string.IsNullOrWhiteSpace(candidate.AstNarrative) &&
                candidate.AstNarrative.IndexOf("未完全编译", StringComparison.Ordinal) < 0 &&
                candidate.AstNarrative.IndexOf("暂不可解析", StringComparison.Ordinal) < 0 &&
                candidate.AstNarrative.IndexOf("未展开", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            // 有有效的 AST 摘要（不是纯 Reference/Opaque）→ 保留
            if (!string.IsNullOrWhiteSpace(candidate.AstSummary) &&
                !string.Equals(candidate.AstSummary, "Reference", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate.AstSummary, "Opaque", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 所有上下文都是空或占位 → 过滤
            return true;
        }

        private async Task<Dictionary<string, List<PLC_IOTagEntity>>> LoadHardwareIoTagsByStationAsync(long projectId)
        {
            var tags = await _ioTagService.GetListAsync(it =>
                it.ProjectId == projectId &&
                it.IsDeleted == false &&
                !string.IsNullOrWhiteSpace(it.TagName));

            return tags
                .Where(IsHardwareIoTag)
                .Select(it => new
                {
                    Tag = it,
                    StationCode = ExtractStationCode(it.TagName, it.TableName, it.Path)
                })
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode))
                .GroupBy(it => it.StationCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(it => it.Tag)
                        .GroupBy(it => it.TagName, StringComparer.OrdinalIgnoreCase)
                        .Select(it => it.First())
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsHardwareIoTag(PLC_IOTagEntity tag)
        {
            return tag != null &&
                   !string.IsNullOrWhiteSpace(tag.TagName) &&
                   !string.IsNullOrWhiteSpace(tag.Address) &&
                   (tag.Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase) ||
                    tag.Address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase));
        }

        private static string ExtractStationCode(params string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var match = StationCodeRegex.Match(value);
                if (match.Success)
                {
                    return match.Value.ToUpperInvariant();
                }
            }

            return string.Empty;
        }

        private static DeterministicIoMappingResult TryBuildDeterministicIoMapping(
            string rawContentExcerpt,
            string outputVariable,
            IReadOnlyList<PLC_IOTagEntity> hardwareTags)
        {
            if (string.IsNullOrWhiteSpace(rawContentExcerpt) || string.IsNullOrWhiteSpace(outputVariable))
            {
                return DeterministicIoMappingResult.Empty;
            }

            var normalizedOutput = NormalizeVariableToken(outputVariable);
            if (string.IsNullOrWhiteSpace(normalizedOutput))
            {
                return DeterministicIoMappingResult.Empty;
            }

            var hardwareTagMap = (hardwareTags ?? new List<PLC_IOTagEntity>())
                .Where(it => !string.IsNullOrWhiteSpace(it.TagName))
                .GroupBy(it => it.TagName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var readableContent = ExtractReadableSnippet(rawContentExcerpt);
            if (string.IsNullOrWhiteSpace(readableContent))
            {
                return DeterministicIoMappingResult.Empty;
            }

            foreach (Match assignment in AssignmentRegex.Matches(readableContent))
            {
                var left = assignment.Groups["left"].Value;
                if (!string.Equals(NormalizeVariableToken(left), normalizedOutput, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var right = assignment.Groups["right"].Value;
                var referenceTokens = ExtractReferenceTokens(right);

                if (referenceTokens.Count == 0)
                {
                    continue;
                }

                var mappedTags = referenceTokens
                    .Where(it => hardwareTagMap.ContainsKey(it) || LooksLikeHardwareTag(it))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (mappedTags.Count == 0)
                {
                    continue;
                }

                var bypassTerms = referenceTokens
                    .Where(it => !mappedTags.Contains(it, StringComparer.OrdinalIgnoreCase))
                    .Where(it => it.IndexOf("bypass", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var mappingText = $"{outputVariable} <= {string.Join(" OR ", mappedTags)}";
                if (bypassTerms.Count > 0)
                {
                    mappingText += $" | bypass: {string.Join(", ", bypassTerms)}";
                }

                return new DeterministicIoMappingResult
                {
                    MappedIoTags = mappedTags,
                    IoMapping = mappingText
                };
            }

            return DeterministicIoMappingResult.Empty;
        }

        private static List<string> ExtractReferenceTokens(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return new List<string>();
            }

            var tokens = QuotedTokenRegex.Matches(expression)
                .Select(it => NormalizeVariableToken(it.Groups["value"].Value))
                .Concat(ReferenceTokenRegex.Matches(expression)
                    .Select(it => NormalizeVariableToken(it.Groups["value"].Value)))
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Where(it => !IsExpressionKeyword(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return tokens;
        }

        private static bool IsExpressionKeyword(string token)
        {
            return token.Equals("AND", StringComparison.OrdinalIgnoreCase)
                || token.Equals("OR", StringComparison.OrdinalIgnoreCase)
                || token.Equals("NOT", StringComparison.OrdinalIgnoreCase)
                || token.Equals("XOR", StringComparison.OrdinalIgnoreCase)
                || token.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || token.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeHardwareTag(string token)
        {
            return !string.IsNullOrWhiteSpace(token) &&
                   StationCodeRegex.IsMatch(token) &&
                   token.IndexOf('.') < 0;
        }

        private static void ApplyIgnoreCategoryIfMappedToInputTag(AstSemanticFinding finding, AstSemanticCandidate candidate)
        {
            if (finding == null || candidate == null)
            {
                return;
            }

            if (finding.RelatedIOTags == null || finding.RelatedIOTags.Count == 0)
            {
                return;
            }

            var hardwareTagMap = (candidate.HardwareIoTags ?? new List<PLC_IOTagEntity>())
                .Where(it => !string.IsNullOrWhiteSpace(it.TagName))
                .GroupBy(it => it.TagName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var mappedInputTags = finding.RelatedIOTags
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Select(tagName => hardwareTagMap.TryGetValue(tagName, out var tag) ? tag : null)
                .Where(tag => tag != null)
                .ToList();

            if (!IsPhysicalInputTagMapping(mappedInputTags))
            {
                return;
            }

            finding.Category = "Ignore";
            if (string.IsNullOrWhiteSpace(finding.SemanticLabel) || string.Equals(finding.SemanticLabel, "显式IO映射", StringComparison.OrdinalIgnoreCase))
            {
                finding.SemanticLabel = "输入点映射";
            }

            finding.Reason = "结果已关联到物理I点，归类为输入点映射并忽略虚拟Q点判定";
        }

        private static bool IsPhysicalInputTagMapping(IReadOnlyList<PLC_IOTagEntity> mappedTags)
        {
            if (mappedTags == null || mappedTags.Count == 0)
            {
                return false;
            }

            return mappedTags.Any(tag =>
                !string.IsNullOrWhiteSpace(tag.Address) &&
                tag.Address.StartsWith("%I", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeVariableToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\"", string.Empty)
                .Replace(" ", string.Empty)
                .Trim();
        }

        private static StopCauseAstNode BuildRelevantSlice(StopCauseAstNode rootNode, long logicUnitId, string outputVariable)
        {
            if (rootNode == null)
            {
                return null;
            }

            var logicUnitToken = $"logic-unit:{logicUnitId}";
            var logicUnitPath = FindNodePath(
                rootNode,
                node => !string.IsNullOrWhiteSpace(node.NodeId) && node.NodeId.IndexOf(logicUnitToken, StringComparison.OrdinalIgnoreCase) >= 0);
            var logicUnitRoot = logicUnitPath?.LastOrDefault();

            if (logicUnitRoot == null)
            {
                logicUnitPath = FindNodePath(
                    rootNode,
                    node => node.Metadata != null &&
                            node.Metadata.TryGetValue("Target", out var target) &&
                            !string.IsNullOrWhiteSpace(outputVariable) &&
                            string.Equals(target, outputVariable, StringComparison.OrdinalIgnoreCase));
                logicUnitRoot = logicUnitPath?.LastOrDefault();
            }

            if (logicUnitRoot == null)
            {
                return CloneNode(rootNode);
            }

            if (string.IsNullOrWhiteSpace(outputVariable))
            {
                return CloneNode(logicUnitRoot);
            }

            var outputPath = FindNodePath(
                logicUnitRoot,
                node => string.Equals(node.VariableName, outputVariable, StringComparison.OrdinalIgnoreCase) ||
                        (node.Metadata != null && node.Metadata.TryGetValue("Target", out var target) && string.Equals(target, outputVariable, StringComparison.OrdinalIgnoreCase)));

            if (outputPath == null || outputPath.Count == 0)
            {
                return CloneNode(logicUnitRoot);
            }

            return BuildSliceFromPath(outputPath, 0);
        }

        private static StopCauseAstNode FindNode(StopCauseAstNode node, Func<StopCauseAstNode, bool> predicate)
        {
            if (node == null)
            {
                return null;
            }

            if (predicate(node))
            {
                return node;
            }

            foreach (var child in node.Children ?? new List<StopCauseAstNode>())
            {
                var match = FindNode(child, predicate);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static List<StopCauseAstNode> FindNodePath(StopCauseAstNode node, Func<StopCauseAstNode, bool> predicate)
        {
            var path = new List<StopCauseAstNode>();
            return FindNodePathInternal(node, predicate, path) ? path : null;
        }

        private static bool FindNodePathInternal(StopCauseAstNode node, Func<StopCauseAstNode, bool> predicate, List<StopCauseAstNode> path)
        {
            if (node == null)
            {
                return false;
            }

            path.Add(node);
            if (predicate(node))
            {
                return true;
            }

            foreach (var child in node.Children ?? new List<StopCauseAstNode>())
            {
                if (FindNodePathInternal(child, predicate, path))
                {
                    return true;
                }
            }

            path.RemoveAt(path.Count - 1);
            return false;
        }

        private static StopCauseAstNode BuildSliceFromPath(IReadOnlyList<StopCauseAstNode> path, int index)
        {
            if (path == null || index < 0 || index >= path.Count)
            {
                return null;
            }

            var current = path[index];
            if (index == path.Count - 1)
            {
                return CloneNode(current);
            }

            var clone = CloneNodeShallow(current);
            var childSlice = BuildSliceFromPath(path, index + 1);
            if (childSlice != null)
            {
                clone.Children.Add(childSlice);
            }

            return clone;
        }

        private static StopCauseAstNode CloneNode(StopCauseAstNode node)
        {
            if (node == null)
            {
                return null;
            }

            var clone = CloneNodeShallow(node);
            clone.Children = (node.Children ?? new List<StopCauseAstNode>())
                .Select(CloneNode)
                .Where(child => child != null)
                .ToList();
            return clone;
        }

        private static StopCauseAstNode CloneNodeShallow(StopCauseAstNode node)
        {
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
                Children = new List<StopCauseAstNode>()
            };
        }

        private static string BuildAstSummary(StopCauseAstNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            Walk(node, 0, parts);
            return string.Join(" | ", parts.Take(12));

            static void Walk(StopCauseAstNode current, int depth, List<string> sink)
            {
                if (current == null || depth > 3 || sink.Count >= 20)
                {
                    return;
                }

                var label = string.IsNullOrWhiteSpace(current.VariableName)
                    ? current.NodeType
                    : $"{current.NodeType}:{current.VariableName}";
                sink.Add(label);

                foreach (var child in current.Children ?? new List<StopCauseAstNode>())
                {
                    Walk(child, depth + 1, sink);
                }
            }
        }

        private static string BuildAstNarrative(StopCauseAstNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var lines = new List<string>();
            var anchorName = FirstNonEmpty(node.DisplayName, node.VariableName);
            if (!string.IsNullOrWhiteSpace(anchorName))
            {
                lines.Add($"当前分析锚点是 {anchorName}");
            }

            var children = node.Children ?? new List<StopCauseAstNode>();
            foreach (var child in children.Take(6))
            {
                var line = DescribeFlow(child, 0);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                }
            }

            if (lines.Count == 0)
            {
                var fallback = DescribeNodeFallback(node);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    lines.Add(fallback);
                }
            }

            return string.Join("；", lines.Where(it => !string.IsNullOrWhiteSpace(it)).Take(8));

            static string DescribeFlow(StopCauseAstNode current, int depth)
            {
                if (current == null || depth > 6)
                {
                    return string.Empty;
                }

                switch (current.NodeType)
                {
                    case StopCauseAstNodeType.Coil:
                    case StopCauseAstNodeType.Set:
                    case StopCauseAstNodeType.Reset:
                    {
                        var action = DescribeActionVerb(current.NodeType);
                        var target = FirstNonEmpty(current.VariableName, current.DisplayName);
                        var condition = current.Children?.Count > 0 ? DescribeCondition(current.Children[0], depth + 1) : string.Empty;
                        return string.IsNullOrWhiteSpace(condition)
                            ? $"执行 {action}{target}"
                            : $"当 {condition} 时，执行 {action}{target}";
                    }

                    case StopCauseAstNodeType.GraphTransition:
                    {
                        var target = FirstNonEmpty(current.DisplayName, current.VariableName);
                        var condition = current.Children?.Count > 0 ? DescribeCondition(current.Children[0], depth + 1) : string.Empty;
                        return string.IsNullOrWhiteSpace(condition)
                            ? $"GRAPH 转换 {target} 被触发"
                            : $"当 {condition} 时，触发 GRAPH 转换 {target}";
                    }

                    case StopCauseAstNodeType.And:
                    case StopCauseAstNodeType.Or:
                    {
                        var childFlows = (current.Children ?? new List<StopCauseAstNode>())
                            .Select(child => DescribeFlow(child, depth + 1))
                            .Where(text => !string.IsNullOrWhiteSpace(text))
                            .Take(4)
                            .ToList();
                        if (childFlows.Count > 0)
                        {
                            return string.Join("；", childFlows);
                        }

                        return DescribeNodeFallback(current);
                    }

                    default:
                        return DescribeNodeFallback(current);
                }
            }

            static string DescribeCondition(StopCauseAstNode current, int depth)
            {
                if (current == null || depth > 6)
                {
                    return string.Empty;
                }

                switch (current.NodeType)
                {
                    case StopCauseAstNodeType.Contact:
                        return $"{FirstNonEmpty(current.VariableName, current.DisplayName)} 为真";
                    case StopCauseAstNodeType.Not:
                    {
                        var inner = current.Children?.Count > 0 ? DescribeCondition(current.Children[0], depth + 1) : string.Empty;
                        return string.IsNullOrWhiteSpace(inner) ? "条件为假" : $"非({inner})";
                    }
                    case StopCauseAstNodeType.And:
                    {
                        var parts = (current.Children ?? new List<StopCauseAstNode>())
                            .Select(child => DescribeCondition(child, depth + 1))
                            .Where(text => !string.IsNullOrWhiteSpace(text))
                            .Take(6)
                            .ToList();
                        return parts.Count == 0 ? string.Empty : string.Join(" 且 ", parts);
                    }
                    case StopCauseAstNodeType.Or:
                    {
                        var parts = (current.Children ?? new List<StopCauseAstNode>())
                            .Select(child => DescribeCondition(child, depth + 1))
                            .Where(text => !string.IsNullOrWhiteSpace(text))
                            .Take(6)
                            .ToList();
                        return parts.Count == 0 ? string.Empty : string.Join(" 或 ", parts);
                    }
                    case StopCauseAstNodeType.Compare:
                    {
                        var left = FirstNonEmpty(current.VariableName, TryGetMetadata(current, "Left"));
                        var right = FirstNonEmpty(TryGetMetadata(current, "Right"), current.ExpectedValue);
                        var op = FirstNonEmpty(current.Operator, "Eq");
                        return $"{left} {NormalizeOperator(op)} {right}";
                    }
                    case StopCauseAstNodeType.GraphTransition:
                        return $"GRAPH 转换 {FirstNonEmpty(current.DisplayName, current.VariableName)} 成立";
                    case StopCauseAstNodeType.ExternalDependency:
                        return $"调用外部逻辑 {FirstNonEmpty(current.DisplayName, current.VariableName)}";
                    case StopCauseAstNodeType.Reference:
                        return $"引用片段 {FirstNonEmpty(current.DisplayName, current.VariableName)} 参与判断";
                    case StopCauseAstNodeType.Opaque:
                        return $"存在未展开片段 {FirstNonEmpty(current.DisplayName, current.VariableName)}";
                    case StopCauseAstNodeType.Coil:
                    case StopCauseAstNodeType.Set:
                    case StopCauseAstNodeType.Reset:
                        return $"输出 {FirstNonEmpty(current.VariableName, current.DisplayName)} 被写入";
                    default:
                        return DescribeNodeFallback(current);
                }
            }

            static string DescribeNodeFallback(StopCauseAstNode current)
            {
                if (current == null)
                {
                    return string.Empty;
                }

                return current.NodeType switch
                {
                    StopCauseAstNodeType.Anchor => $"根锚点变量 {FirstNonEmpty(current.DisplayName, current.VariableName)}",
                    StopCauseAstNodeType.ExternalDependency => $"调用外部依赖 {FirstNonEmpty(current.DisplayName, current.VariableName)}",
                    StopCauseAstNodeType.Reference => $"存在未完全编译片段 {FirstNonEmpty(current.DisplayName, current.VariableName)}",
                    StopCauseAstNodeType.Opaque => $"存在暂不可解析片段 {FirstNonEmpty(current.DisplayName, current.VariableName)}",
                    _ => $"节点 {current.NodeType} {FirstNonEmpty(current.VariableName, current.DisplayName)}"
                };
            }

            static string DescribeActionVerb(string nodeType)
            {
                return nodeType switch
                {
                    StopCauseAstNodeType.Set => "置位 ",
                    StopCauseAstNodeType.Reset => "复位 ",
                    _ => "写入 "
                };
            }

            static string TryGetMetadata(StopCauseAstNode current, string key)
            {
                return current.Metadata != null && current.Metadata.TryGetValue(key, out var value)
                    ? value
                    : string.Empty;
            }

            static string NormalizeOperator(string op)
            {
                return op switch
                {
                    "Eq" => "==",
                    "Ne" => "!=",
                    "Gt" => ">",
                    "Ge" => ">=",
                    "Lt" => "<",
                    "Le" => "<=",
                    _ => op
                };
            }

            static string FirstNonEmpty(params string[] values)
            {
                return values?.FirstOrDefault(it => !string.IsNullOrWhiteSpace(it)) ?? string.Empty;
            }
        }

        private static string ExtractReadableSnippet(string rawContent)
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

        private static string RenderStructuredText(XElement structuredText)
        {
            if (structuredText == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
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

        private static string TruncateForPrompt(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= MaxPromptSnippetLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, MaxPromptSnippetLength) + " ...(truncated)";
        }

        private static string TruncateForLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= MaxLogContentLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, MaxLogContentLength) + " ...(truncated)";
        }

        private static async Task WriteTraceLogAsync(string message)
        {
            try
            {
                await WriteLogHelper.WriteLogsAsync(message, "StopCauseDiagnosis/AstSemanticInference");
            }
            catch
            {
            }
        }

        private async Task PersistIoMappingsAsync(long projectId, IReadOnlyList<AstSemanticFinding> findings)
        {
            var mappingGroups = (findings ?? new List<AstSemanticFinding>())
                .Where(it => it.RelatedIOTags?.Count > 0)
                .SelectMany(it => it.RelatedIOTags.Select(tag => new { TagName = tag, Finding = it }))
                .Where(it => !string.IsNullOrWhiteSpace(it.TagName))
                .GroupBy(it => it.TagName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (mappingGroups.Count == 0)
            {
                return;
            }

            var tagNames = mappingGroups.Select(it => it.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var tags = await _ioTagService.GetListAsync(it =>
                it.ProjectId == projectId &&
                it.IsDeleted == false &&
                tagNames.Contains(it.TagName));

            if (tags.Count == 0)
            {
                return;
            }

            foreach (var tag in tags)
            {
                var group = mappingGroups.FirstOrDefault(it => string.Equals(it.Key, tag.TagName, StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    continue;
                }

                var mappings = group
                    .Select(it => BuildIoMappingText(it.Finding, tag.TagName))
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (mappings.Count == 0)
                {
                    continue;
                }

                tag.IoMapping = string.Join(" | ", mappings.Take(8));
                tag.ModifyTime = DateTime.Now;
            }

            await _ioTagService.UpdateRangeAsync(tags);
        }

        private static string BuildIoMappingText(AstSemanticFinding finding, string tagName)
        {
            if (finding == null || string.IsNullOrWhiteSpace(finding.VariableName))
            {
                return string.Empty;
            }

            var segments = new List<string>
            {
                $"tag={NormalizeMappingValue(tagName)}",
                $"sourceVar={NormalizeMappingValue(finding.VariableName)}"
            };

            if (!string.IsNullOrWhiteSpace(finding.StationCode))
            {
                segments.Add($"station={NormalizeMappingValue(finding.StationCode)}");
            }

            if (!string.IsNullOrWhiteSpace(finding.BlockName))
            {
                segments.Add($"block={NormalizeMappingValue(finding.BlockName)}");
            }

            if (finding.LogicUnitId > 0)
            {
                segments.Add($"logicUnitId={finding.LogicUnitId}");
            }

            if (!string.IsNullOrWhiteSpace(finding.Category))
            {
                segments.Add($"category={NormalizeMappingValue(finding.Category)}");
            }

            if (!string.IsNullOrWhiteSpace(finding.SemanticLabel))
            {
                segments.Add($"label={NormalizeMappingValue(finding.SemanticLabel)}");
            }

            if (!string.IsNullOrWhiteSpace(finding.IoMapping))
            {
                segments.Add($"mapping={NormalizeMappingValue(finding.IoMapping)}");
            }

            if (!string.IsNullOrWhiteSpace(finding.Reason))
            {
                segments.Add($"reason={NormalizeMappingValue(finding.Reason)}");
            }

            return string.Join("; ", segments);
        }

        private static string NormalizeMappingValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value
                .Replace(";", ",")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }

        private static List<AstSemanticFinding> ParseResponse(string content)
        {
            var result = new List<AstSemanticFinding>();
            try
            {
                var start = content.IndexOf('[');
                var end = content.LastIndexOf(']');
                if (start < 0 || end <= start)
                {
                    return result;
                }

                var json = content.Substring(start, end - start + 1);
                var items = JsonSerializer.Deserialize<List<LlmAstSemanticResult>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                });

                if (items == null)
                {
                    return result;
                }

                foreach (var item in items)
                {
                    if (item == null ||
                        string.IsNullOrWhiteSpace(item.VariableName) ||
                        string.Equals(item.VariableName, "Null", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new AstSemanticFinding
                    {
                        LogicUnitId = item.LogicUnitId,
                        VariableName = item.VariableName,
                        Category = item.Category ?? "Ignore",
                        SemanticLabel = item.SemanticLabel ?? string.Empty,
                        Reason = item.Reason ?? string.Empty,
                        Confidence = item.Confidence,
                        RelatedIOTags = item.MappedIoTags?.Where(it => !string.IsNullOrWhiteSpace(it)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                            ?? new List<string>(),
                        IoMapping = item.IoMapping ?? string.Empty
                    });
                }
            }
            catch
            {
            }

            return result;
        }

        private class AstSemanticCandidate
        {
            public long LogicUnitId { get; set; }
            public string BlockName { get; set; }
            public string Title { get; set; }
            public string StationCode { get; set; }
            public string VariableName { get; set; }
            public List<string> InputVariables { get; set; } = new List<string>();
            public List<string> OutputVariables { get; set; } = new List<string>();
            public string ProgrammingLanguage { get; set; }
            public string UnitType { get; set; }
            public string RawContentExcerpt { get; set; }
            public string AstSummary { get; set; }
            public string AstNarrative { get; set; }
            public string AstControlLogicExpression { get; set; }
            public List<string> PhysicalTagHints { get; set; } = new List<string>();
            public List<PLC_IOTagEntity> HardwareIoTags { get; set; } = new List<PLC_IOTagEntity>();
            public List<string> DeterministicMappedIoTags { get; set; } = new List<string>();
            public string DeterministicIoMapping { get; set; }
            public string DeterministicCategoryOverride { get; set; }
            public string DeterministicSemanticLabelOverride { get; set; }
            public string DeterministicReasonOverride { get; set; }
        }

        private class DeterministicIoMappingResult
        {
            public static readonly DeterministicIoMappingResult Empty = new DeterministicIoMappingResult();

            public List<string> MappedIoTags { get; set; } = new List<string>();

            public string IoMapping { get; set; } = string.Empty;

            public string CategoryOverride { get; set; } = string.Empty;

            public string SemanticLabelOverride { get; set; } = string.Empty;

            public string ReasonOverride { get; set; } = string.Empty;
        }

        private class LlmAstSemanticResult
        {
            public long LogicUnitId { get; set; }
            public string VariableName { get; set; }
            public string Category { get; set; }
            public string SemanticLabel { get; set; }
            public string Reason { get; set; }
            public decimal Confidence { get; set; }
            public List<string> MappedIoTags { get; set; } = new List<string>();
            public string IoMapping { get; set; }
        }
    }
}