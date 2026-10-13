using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 使用 LLM 对虚拟 Q 点候选进行主分类和语义标注。
    /// 分为两种使用场景：
    ///   1. ClassifyVirtualCandidatesAsync —— LLM 作为主分类引擎（不依赖关键字预筛）
    ///   2. AnnotateAcceptedAsync / ReviewQueueAsync —— 规则路径事后补充标注（兜底）
    /// LLM 不参与布尔真值判定，只负责语义分类和标签生成。
    /// </summary>
    public class LlmAnchorAnnotatorService : ISelfScopedAutoInject
    {
        private const int BatchSize = 20;
        private const int StationBatchSize = 50;

        /// <summary>
        /// 主分类 prompt：接受富结构上下文（写入指令类型、网络标题、块名、同网络变量），
        /// 输出四分类结果，不依赖变量命名规范。
        /// </summary>
        private const string ClassifySystemPrompt = @"
你是一名工业自动化 PLC 代码分析专家，专注于西门子 TIA Portal。
你的任务是根据提供的结构性上下文，对每个变量进行语义分类。

分类规则：
- VirtualQPoint：虽无物理 %Q 地址，但在控制语义上承担""动作命令发出""或""动作触发""角色。
  典型特征：由 GRAPH Step Action 或 LAD Coil/SCoil/RCoil 写入；写入后作为下游设备驱动、FB调用或工位触发信号的输入；
  网络标题或所在块包含动作相关描述（气缸、夹爪、电机、启动、运行、伸出、缩回等）。
- AuxiliaryOutput：指示灯、蜂鸣器、HMI 显示位等辅助输出，不直接驱动机械动作。
- HandshakeSignal：用于 PLC 与外部系统（MES、机器人、视觉、AGV、条码扫描器）之间状态协商的变量。
- StateFlag：内部状态标志、中间辅助位，仅在 PLC 程序内部流转，不驱动外部动作。
- Unknown：信息不足，无法判断。

重要说明：
- 不要依赖变量名中是否包含关键字，而是结合写入指令类型、网络标题、同网络变量和控制逻辑综合判断。
- ""控制逻辑""字段描述了该变量在什么条件下被写入（从 AST 解析而来），这是判断变量语义的最强证据。
  例如""当 SafetyOK=TRUE 且 StartCmd=TRUE 时，输出 CylinderExtend""表明这是一个实际驱动气缸的动作命令。
- 如果网络标题明确描述了动作（如""气缸1伸出控制""），即使变量名为 DB10.x1 也应判定为 VirtualQPoint。
- 写入指令类型 STEP_ACTION/GRAPH 是强证据，COIL/SET/RESET 是中强证据。

每个变量输出 JSON 对象，字段：
- varName: 原始变量名（原样返回）
- category: VirtualQPoint / AuxiliaryOutput / HandshakeSignal / StateFlag / Unknown
- semanticLabel: 简洁中文动作语义描述（不超过 20 字），Unknown 时可返回空字符串
- reason: 一句话判断依据（不超过 60 字）

以 JSON 数组回复，不要有任何额外文字。示例：
[
  {""varName"":""DB10.x1"",""category"":""VirtualQPoint"",""semanticLabel"":""气缸1伸出命令"",""reason"":""写入指令为 STEP_ACTION，网络标题为气缸1伸出控制，同网络含传感器反馈变量""},
  {""varName"":""DB20.Robot_Ready"",""category"":""HandshakeSignal"",""semanticLabel"":"""",""reason"":""网络标题含机器人握手，同网络包含 MES 协商变量，读多写少""},
  {""varName"":""DB5.StepActive"",""category"":""StateFlag"",""semanticLabel"":"""",""reason"":""仅在 GRAPH 内部读取，无外部设备驱动证据""}
]";

        /// <summary>
        /// 兜底标注 prompt：规则路径运行后，对已识别候选补充语义标签（仅二值判断）。
        /// </summary>
        private const string AnnotateSystemPrompt = @"
你是一名工业自动化 PLC 代码分析专家，专注于西门子 TIA Portal 项目。
你的任务是判断给定的变量列表中，哪些是""虚拟 Q 点""——即没有物理 %Q 地址，但在控制逻辑上承担""动作命令发出""或""动作触发""角色的内部 DB 变量。

判断依据：
1. 变量被写入（赋值），且写入动作来自 GRAPH Step Action 或 LAD Coil/Set/Reset 指令。
2. 变量名或注释中含有动作类语义：气缸、夹爪、电机、启动、运行、伸出、缩回、下降、上升、Cmd、Req、Start、Run、Allow、Permit、Open、Close、Extend、Retract 等。
3. 写入后该变量作为下游动作块、设备驱动或其他工位触发信号的输入条件被读取。
4. 变量不属于外部握手状态（MES、机器人、视觉、条码等交互协商信号）。

对每个变量，你必须输出 JSON 对象，包含：
- varName: 原始变量名（原样返回）
- isVirtualQPoint: true / false
- semanticLabel: 简洁中文动作语义描述（不超过 20 字），如无法确定则返回空字符串
- reason: 一句话判断依据（不超过 60 字）

整体以 JSON 数组回复，不要有额外文字。示例：
[
  {""varName"":""DB10.Cyl1_Extend_Cmd"",""isVirtualQPoint"":true,""semanticLabel"":""气缸1伸出命令"",""reason"":""变量名含 Cyl + Extend + Cmd，写入后驱动气缸动作""},
  {""varName"":""DB20.Robot_Ready"",""isVirtualQPoint"":false,""semanticLabel"":"""",""reason"":""Robot_Ready 属于外部机器人握手状态，非本侧动作命令""}
]";

                private const string StationInferenceSystemPrompt = @"
你是一名工业自动化 PLC 代码分析专家，专注于西门子 TIA Portal 项目。
你的任务是根据变量上下文，推断其最可能归属的工位编码。

推断要求：
- 工位编码不固定为 OP 开头，可以是 OP02、OP020、ST12、LINE03、CELL4、UNIT02、工位12 等现场常见写法。
- 中文工厂中常用 2#、3#、10# 这种「序号+井号」形式代表工位/设备编号，如「2#三色灯」归属工位 2#，需正确识别。
- 只有出现明确工位级标识时才允许输出工位；像 CYL02、YV01、VALVE02、MOTOR01、夹爪1 这类部件/执行元件编号不是工位，禁止输出。
- 重点参考网络标题、块名、变量注释、同网络变量、已有规则证据。
- 严禁把 DB110、FB20、FC12、OB1、Q0.0 这类地址/块编号误判为工位。
- 如果信息不足，请返回空字符串，并把 confidence 设为 0。
- 若已有规则工位且证据明显，不要为了变化而变化；仅在你有更强把握时才覆盖。

请输出 JSON 数组，每项字段：
- targetKey: 原样返回
- stationCode: 你判断的工位编码；无法判断则返回空字符串
- confidence: 0 到 1 之间的小数
- reason: 一句话理由，不超过 60 字

不要输出任何额外文字。示例：
[
    {""targetKey"":""var:DB10.CmdStart"",""stationCode"":""ST12"",""confidence"":0.82,""reason"":""网络标题和块名均指向 ST12 上料位""},
    {""targetKey"":""var:DB20.StepFlag"",""stationCode"":"""",""confidence"":0.00,""reason"":""上下文未出现稳定工位标识""}
]";

        private const string StationGroupInferenceSystemPrompt = @"
你是一名工业自动化 PLC 代码分析专家，专注于西门子 TIA Portal 项目。
你的任务是根据 IOTag 的 path 分组或 table 分组上下文，为整个分组判断最可能的工位编码。

规则：
- 你处理的是一个分组，不是单个点位。应综合该组的路径、标签表名、样本标签名、样本注释、设备编号来判断。
- 工位编码不固定为 OP 开头，可以是 OP02、OP020、ST12、LINE03、CELL4、UNIT02、工位12 等。
- 中文工厂中常用 2#、3#、10# 这种「序号+井号」形式代表工位/设备编号，如多个样本标签名前缀均含 2# 则该组归属工位 2#。
- 如果样本里出现的是 CYL02、YV01、VALVE02、MOTOR01 这类气缸/阀/电机/部件编号，而没有清晰工位级标识，则必须返回空字符串。
- 严禁把 DB110、FB20、FC12、OB1、Q0.0 这类地址/块编号误判为工位。
- 如果整个分组都看不出稳定工位，请返回空字符串和 0 置信度。

请输出 JSON 数组，每项字段：
- targetKey: 原样返回
- stationCode: 你判断的工位编码；无法判断则返回空字符串
- confidence: 0 到 1 之间的小数
- reason: 一句话理由，不超过 60 字

不要输出任何额外文字。";

        /// <summary>并发 LLM 批次数，默认 4，可在 DI 注册后调整以适应速率限制</summary>
        public int MaxConcurrency { get; set; } = 4;

        private readonly LLMModelService _llmModelService;
        private readonly AstMarkdownRendererService _astMarkdownRenderer;
        private readonly ILogger<LlmAnchorAnnotatorService> _logger;

        public LlmAnchorAnnotatorService(
            LLMModelService llmModelService,
            AstMarkdownRendererService astMarkdownRenderer,
            ILogger<LlmAnchorAnnotatorService> logger)
        {
            _llmModelService = llmModelService;
            _astMarkdownRenderer = astMarkdownRenderer;
            _logger = logger;
        }

        private async Task<ChatResponse?> CallLlmDirectAsync(
            string systemPrompt,
            string userPrompt,
            string sceneName)
        {
            try
            {
                var recommendedModel = await _llmModelService.RecommendModelAsync();
                if (recommendedModel == null)
                {
                    _logger.LogWarning("[LlmAnchorAnnotator] {Scene} 无可用 LLM 模型", sceneName);
                    return null;
                }

                var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(recommendedModel.Id);
                return await _llmModelService.ChatAsync(chatClient, userPrompt, systemPrompt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LlmAnchorAnnotator] {Scene} 直连 LLM 失败", sceneName);
                return null;
            }
        }

        /// <summary>
        /// 对 accepted（已确认）锚点批量生成语义标签。
        /// 只覆盖 SemanticLabel 为空或仅为变量名的条目。
        /// </summary>
        public async Task AnnotateAcceptedAsync(IReadOnlyList<AnchorCandidate> anchors)
        {
            var targets = anchors
                .Where(it => string.IsNullOrWhiteSpace(it.SemanticLabel) ||
                             string.Equals(it.SemanticLabel, it.VariableName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (targets.Count == 0)
            {
                return;
            }

            await RunAnnotationBatchesAsync(targets, applyFn: (candidate, result) =>
            {
                if (!string.IsNullOrWhiteSpace(result.SemanticLabel))
                {
                    candidate.SemanticLabel = result.SemanticLabel;
                }

                if (!string.IsNullOrWhiteSpace(result.Reason))
                {
                    candidate.Evidence.Add($"[LLM] {result.Reason}");
                }
            });
        }

        /// <summary>
        /// 对 ReviewQueue 做 LLM 二次复核，把 LLM 判断为虚拟 Q 点的条目升级到 accepted，
        /// 其余保留在 reviewQueue 并附上 LLM 理由。
        /// </summary>
        public async Task<(List<AnchorCandidate> Promoted, List<AnchorCandidate> Remaining)> ReviewQueueAsync(
            IReadOnlyList<AnchorCandidate> reviewQueue)
        {
            if (reviewQueue.Count == 0)
            {
                return (new List<AnchorCandidate>(), new List<AnchorCandidate>());
            }

            var resultMap = new ConcurrentDictionary<string, LlmAnnotationResult>(StringComparer.OrdinalIgnoreCase);

            await RunAnnotationBatchesAsync(reviewQueue, applyFn: (candidate, result) =>
            {
                resultMap[candidate.VariableName] = result;
            });

            var promoted = new List<AnchorCandidate>();
            var remaining = new List<AnchorCandidate>();

            foreach (var candidate in reviewQueue)
            {
                if (resultMap.TryGetValue(candidate.VariableName, out var result))
                {
                    if (!string.IsNullOrWhiteSpace(result.SemanticLabel))
                    {
                        candidate.SemanticLabel = result.SemanticLabel;
                    }

                    if (!string.IsNullOrWhiteSpace(result.Reason))
                    {
                        candidate.Evidence.Add($"[LLM] {result.Reason}");
                    }

                    if (result.IsVirtualQPoint)
                    {
                        candidate.DiscoveryStatus = AnchorDiscoveryStatus.Confirmed;
                        candidate.PrimaryReason = "LLM 语义复核后确认为虚拟动作命令输出";
                        promoted.Add(candidate);
                    }
                    else
                    {
                        remaining.Add(candidate);
                    }
                }
                else
                {
                    remaining.Add(candidate);
                }
            }

            return (promoted, remaining);
        }

        private async Task RunAnnotationBatchesAsync(
            IReadOnlyList<AnchorCandidate> candidates,
            Action<AnchorCandidate, LlmAnnotationResult> applyFn)
        {
            var batches = candidates
                .Select((it, idx) => (it, idx))
                .GroupBy(it => it.idx / BatchSize)
                .Select(g => g.Select(x => x.it).ToList())
                .ToList();

            var sem = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = batches.Select(async batch =>
            {
                await sem.WaitAsync();
                try
                {
                    var results = await CallLlmBatchAsync(batch);
                    // 每个候选只属于一个批次，无跨批次竞争，回调可安全执行
                    foreach (var candidate in batch)
                    {
                        if (results.TryGetValue(candidate.VariableName, out var res))
                        {
                            applyFn(candidate, res);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LlmAnchorAnnotator] 批次标注失败，跳过本批次，不影响规则结果");
                }
                finally
                {
                    sem.Release();
                }
            });
            await Task.WhenAll(tasks);
        }

        private async Task<Dictionary<string, LlmAnnotationResult>> CallLlmBatchAsync(
            IReadOnlyList<AnchorCandidate> batch)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请对下列变量逐一判断，按要求格式回复：");
            sb.AppendLine();

            foreach (var c in batch)
            {
                sb.AppendLine($"变量名: {c.VariableName}");
                if (!string.IsNullOrWhiteSpace(c.SemanticLabel) &&
                    !string.Equals(c.SemanticLabel, c.VariableName, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"  注释/标签: {c.SemanticLabel}");
                }

                sb.AppendLine($"  逻辑来源: {c.LogicSourceType}");
                if (c.Evidence?.Count > 0)
                {
                    sb.AppendLine($"  已有证据: {string.Join("；", c.Evidence.Take(3))}");
                }

                sb.AppendLine();
            }

            var response = await CallLlmDirectAsync(AnnotateSystemPrompt, sb.ToString(), "VirtualQPointAnnotator");

            if (response == null || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("[LlmAnchorAnnotator] LLM 返回为空或失败");
                return new Dictionary<string, LlmAnnotationResult>();
            }

            return ParseLlmResponse(response.Content);
        }

        private static Dictionary<string, LlmAnnotationResult> ParseLlmResponse(string content)
        {
            var result = new Dictionary<string, LlmAnnotationResult>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var start = content.IndexOf('[');
                var end = content.LastIndexOf(']');
                if (start < 0 || end <= start)
                {
                    return result;
                }

                var json = content.Substring(start, end - start + 1);
                var items = JsonSerializer.Deserialize<List<LlmAnnotationResult>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.VarName))
                        {
                            result[item.VarName] = item;
                        }
                    }
                }
            }
            catch
            {
                // 解析失败不影响规则结果
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // LLM 主分类路径（不依赖关键字，面向不规范命名场景）
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// LLM 作为主分类引擎，对结构性候选（来自写入指令预筛的变量）进行四分类。
        /// 返回 dict key=VariableName, value=分类结果。
        /// </summary>
        public async Task<Dictionary<string, LlmClassificationResult>> ClassifyVirtualCandidatesAsync(
            IReadOnlyList<VirtualCandidateContext> candidates)
        {
            var result = new Dictionary<string, LlmClassificationResult>(StringComparer.OrdinalIgnoreCase);
            if (candidates == null || candidates.Count == 0)
            {
                return result;
            }

            var batches = candidates
                .Select((it, idx) => (it, idx))
                .GroupBy(it => it.idx / BatchSize)
                .Select(g => g.Select(x => x.it).ToList())
                .ToList();

            var sem = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = batches.Select(async batch =>
            {
                await sem.WaitAsync();
                try
                {
                    return await CallLlmClassificationAsync(batch);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LlmAnchorAnnotator] 分类批次失败，跳过本批次，不影响规则兜底");
                    return new Dictionary<string, LlmClassificationResult>();
                }
                finally
                {
                    sem.Release();
                }
            });

            var batchResults = await Task.WhenAll(tasks);
            foreach (var batchResult in batchResults)
            {
                foreach (var kvp in batchResult)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }

            return result;
        }

        public async Task<Dictionary<string, StationInferenceResult>> InferStationsForVirtualCandidatesAsync(
            IReadOnlyList<VirtualCandidateContext> candidates)
        {
            var result = new Dictionary<string, StationInferenceResult>(StringComparer.OrdinalIgnoreCase);
            if (candidates == null || candidates.Count == 0)
            {
                return result;
            }

            var batches = candidates
                .Select((it, idx) => (it, idx))
                .GroupBy(it => it.idx / BatchSize)
                .Select(g => g.Select(x => x.it).ToList())
                .ToList();

            var sem = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = batches.Select(async batch =>
            {
                await sem.WaitAsync();
                try
                {
                    return await CallLlmStationInferenceAsync(batch);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LlmAnchorAnnotator] 工位推断批次失败，跳过本批次，不影响规则工位结果");
                    return new Dictionary<string, StationInferenceResult>();
                }
                finally
                {
                    sem.Release();
                }
            });

            var batchResults = await Task.WhenAll(tasks);
            foreach (var batchResult in batchResults)
            {
                foreach (var kvp in batchResult)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }

            return result;
        }

        public async Task<Dictionary<string, StationInferenceResult>> InferStationsForPhysicalTagsAsync(
            IReadOnlyList<PLC_IOTagEntity> tags)
        {
            var result = new Dictionary<string, StationInferenceResult>(StringComparer.OrdinalIgnoreCase);
            if (tags == null || tags.Count == 0)
            {
                return result;
            }

            var batches = tags
                .Select((it, idx) => (it, idx))
                .GroupBy(it => it.idx / StationBatchSize)
                .Select(g => g.Select(x => x.it).ToList())
                .ToList();

            var sem = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = batches.Select(async batch =>
            {
                await sem.WaitAsync();
                try
                {
                    return await CallLlmPhysicalStationInferenceAsync(batch);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LlmAnchorAnnotator] 物理点工位推断批次失败，跳过本批次，不影响规则工位结果");
                    return new Dictionary<string, StationInferenceResult>();
                }
                finally
                {
                    sem.Release();
                }
            });

            var batchResults = await Task.WhenAll(tasks);
            foreach (var batchResult in batchResults)
            {
                foreach (var kvp in batchResult)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }

            return result;
        }

        public async Task<Dictionary<string, StationInferenceResult>> InferStationsForPhysicalTagGroupsAsync(
            IReadOnlyList<PhysicalTagGroupContext> groups)
        {
            var result = new Dictionary<string, StationInferenceResult>(StringComparer.OrdinalIgnoreCase);
            if (groups == null || groups.Count == 0)
            {
                return result;
            }

            var batches = groups
                .Select((it, idx) => (it, idx))
                .GroupBy(it => it.idx / StationBatchSize)
                .Select(g => g.Select(x => x.it).ToList())
                .ToList();

            var sem = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
            var tasks = batches.Select(async batch =>
            {
                await sem.WaitAsync();
                try
                {
                    return await CallLlmPhysicalGroupStationInferenceAsync(batch);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LlmAnchorAnnotator] 分组工位推断批次失败，跳过本批次，不影响剩余点位推断");
                    return new Dictionary<string, StationInferenceResult>();
                }
                finally
                {
                    sem.Release();
                }
            });

            var batchResults = await Task.WhenAll(tasks);
            foreach (var batchResult in batchResults)
            {
                foreach (var kvp in batchResult)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }

            return result;
        }

        private async Task<Dictionary<string, LlmClassificationResult>> CallLlmClassificationAsync(
            IReadOnlyList<VirtualCandidateContext> batch)
        {
            // 批量预取 AST 控制逻辑表达式（一次 DB 查询覆盖整个 batch）
            var projectId = batch.FirstOrDefault()?.ProjectId ?? 0;
            Dictionary<string, string> astExpressions = null;
            if (projectId > 0)
            {
                try
                {
                    var varNames = batch.Select(c => c.VariableName).Where(n => n != null);
                    astExpressions = await _astMarkdownRenderer.BatchRenderVariableExpressionsAsync(projectId, varNames);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[LlmAnchorAnnotator] 批量获取 AST 表达式失败，继续无 AST 上下文分类");
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("请对下列变量逐一进行分类，按要求格式回复：");
            sb.AppendLine();

            foreach (var c in batch)
            {
                sb.AppendLine($"变量名: {c.VariableName}");

                if (!string.IsNullOrWhiteSpace(c.Comment))
                {
                    sb.AppendLine($"  注释: {c.Comment}");
                }

                if (!string.IsNullOrWhiteSpace(c.StationCode))
                {
                    sb.AppendLine($"  工位推断: {c.StationCode} (confidence={c.StationConfidence:0.00})");
                }

                if (c.StationEvidence?.Count > 0)
                {
                    sb.AppendLine($"  工位证据: {string.Join(" | ", c.StationEvidence.Take(4))}");
                }

                if (c.WriteInstructionTypes?.Count > 0)
                {
                    sb.AppendLine($"  写入指令类型: {string.Join(", ", c.WriteInstructionTypes)}");
                }

                if (c.NetworkTitles?.Count > 0)
                {
                    sb.AppendLine($"  所在网络标题: {string.Join(" | ", c.NetworkTitles)}");
                }

                if (c.BlockNames?.Count > 0)
                {
                    sb.AppendLine($"  所在块: {string.Join(", ", c.BlockNames)}");
                }

                if (c.CoLocatedWriteVariables?.Count > 0)
                {
                    sb.AppendLine($"  同网络写入变量（Co-location）: {string.Join(", ", c.CoLocatedWriteVariables.Take(6))}");
                }

                sb.AppendLine($"  写入次数: {c.WriteCount}, 读取次数: {c.ReadCount}");

                // AST 控制逻辑：该变量在什么条件下被写入
                if (astExpressions != null
                    && astExpressions.TryGetValue(c.VariableName, out var astExpr)
                    && !string.IsNullOrWhiteSpace(astExpr))
                {
                    sb.AppendLine($"  控制逻辑: {astExpr}");
                }

                sb.AppendLine();
            }

            var userPrompt = sb.ToString();

            // 记录投入 LLM 的完整上下文（System Prompt + User Prompt）
            var logContent = $"===== Q点分类 LLM 上下文 ({batch.Count}个变量, AST命中{astExpressions?.Count ?? 0}个) =====\n" +
                $"--- System Prompt ---\n{ClassifySystemPrompt}\n" +
                $"--- User Prompt ({userPrompt.Length} chars) ---\n{userPrompt}";
            await WriteLogHelper.WriteLogsAsync(logContent, "QPointClassify_LLM_Input");

            var response = await CallLlmDirectAsync(ClassifySystemPrompt, userPrompt, "VirtualQPointClassifier");

            // 记录 LLM 返回
            await WriteLogHelper.WriteLogsAsync(
                $"===== Q点分类 LLM 返回 (Success={response != null}, {response?.Content?.Length ?? 0} chars) =====\n{response?.Content ?? string.Empty}",
                "QPointClassify_LLM_Output");

            if (response == null || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("[LlmAnchorAnnotator] LLM 分类返回为空或失败");
                return new Dictionary<string, LlmClassificationResult>();
            }

            return ParseClassificationResponse(response.Content);
        }

        private async Task<Dictionary<string, StationInferenceResult>> CallLlmStationInferenceAsync(
            IReadOnlyList<VirtualCandidateContext> batch)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请对下列变量逐一推断工位归属，按要求格式回复：");
            sb.AppendLine();

            foreach (var c in batch)
            {
                sb.AppendLine($"targetKey: var:{c.VariableName}");
                sb.AppendLine($"变量名: {c.VariableName}");

                if (!string.IsNullOrWhiteSpace(c.Comment))
                {
                    sb.AppendLine($"  注释: {c.Comment}");
                }

                if (!string.IsNullOrWhiteSpace(c.StationCode))
                {
                    sb.AppendLine($"  规则工位候选: {c.StationCode} (confidence={c.StationConfidence:0.00})");
                }

                if (c.StationEvidence?.Count > 0)
                {
                    sb.AppendLine($"  规则证据: {string.Join(" | ", c.StationEvidence.Take(5))}");
                }

                if (c.NetworkTitles?.Count > 0)
                {
                    sb.AppendLine($"  网络标题: {string.Join(" | ", c.NetworkTitles.Take(5))}");
                }

                if (c.BlockNames?.Count > 0)
                {
                    sb.AppendLine($"  块名: {string.Join(", ", c.BlockNames.Take(5))}");
                }

                if (c.CoLocatedWriteVariables?.Count > 0)
                {
                    sb.AppendLine($"  同网络变量: {string.Join(", ", c.CoLocatedWriteVariables.Take(6))}");
                }

                if (c.WriteInstructionTypes?.Count > 0)
                {
                    sb.AppendLine($"  写入指令: {string.Join(", ", c.WriteInstructionTypes.Take(6))}");
                }

                sb.AppendLine($"  写入次数: {c.WriteCount}, 读取次数: {c.ReadCount}");
                sb.AppendLine();
            }

            var response = await CallLlmDirectAsync(StationInferenceSystemPrompt, sb.ToString(), "StationInferenceClassifier");

            if (response == null || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("[LlmAnchorAnnotator] LLM 工位推断返回为空或失败");
                return new Dictionary<string, StationInferenceResult>();
            }

            return ParseStationInferenceResponse(response.Content);
        }

        private async Task<Dictionary<string, StationInferenceResult>> CallLlmPhysicalStationInferenceAsync(
            IReadOnlyList<PLC_IOTagEntity> batch)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请对下列物理 IOTag 逐一推断工位归属，按要求格式回复：");
            sb.AppendLine();

            foreach (var tag in batch)
            {
                sb.AppendLine($"targetKey: tag:{tag.Id}");
                sb.AppendLine($"标签名: {tag.TagName}");

                if (!string.IsNullOrWhiteSpace(tag.Address))
                {
                    sb.AppendLine($"  物理地址: {tag.Address}");
                }

                if (!string.IsNullOrWhiteSpace(tag.TableName))
                {
                    sb.AppendLine($"  标签表: {tag.TableName}");
                }

                if (!string.IsNullOrWhiteSpace(tag.Path))
                {
                    sb.AppendLine($"  路径: {tag.Path}");
                }

                if (!string.IsNullOrWhiteSpace(tag.Comment))
                {
                    sb.AppendLine($"  注释: {tag.Comment}");
                }

                if (!string.IsNullOrWhiteSpace(tag.EquipmentId))
                {
                    sb.AppendLine($"  设备编号: {tag.EquipmentId}");
                }

                if (!string.IsNullOrWhiteSpace(tag.TagCategory))
                {
                    sb.AppendLine($"  业务分类: {tag.TagCategory}");
                }

                if (!string.IsNullOrWhiteSpace(tag.TagType))
                {
                    sb.AppendLine($"  控制大类: {tag.TagType}");
                }

                if (!string.IsNullOrWhiteSpace(tag.TagEffect))
                {
                    sb.AppendLine($"  标签作用: {tag.TagEffect}");
                }

                sb.AppendLine();
            }

            var response = await CallLlmDirectAsync(StationInferenceSystemPrompt, sb.ToString(), "PhysicalTagStationInference");

            if (response == null || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("[LlmAnchorAnnotator] 物理点 LLM 工位推断返回为空或失败");
                return new Dictionary<string, StationInferenceResult>();
            }

            return ParseStationInferenceResponse(response.Content);
        }

        private async Task<Dictionary<string, StationInferenceResult>> CallLlmPhysicalGroupStationInferenceAsync(
            IReadOnlyList<PhysicalTagGroupContext> batch)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请对下列 IOTag 分组逐一推断工位归属，按要求格式回复：");
            sb.AppendLine();

            foreach (var group in batch)
            {
                sb.AppendLine($"targetKey: group:{group.GroupType}:{group.GroupKey}");
                sb.AppendLine($"分组类型: {group.GroupType}");
                sb.AppendLine($"分组键: {group.GroupKey}");

                if (!string.IsNullOrWhiteSpace(group.GroupPath))
                {
                    sb.AppendLine($"  路径: {group.GroupPath}");
                }

                if (!string.IsNullOrWhiteSpace(group.GroupTableName))
                {
                    sb.AppendLine($"  标签表: {group.GroupTableName}");
                }

                sb.AppendLine($"  点位数量: {group.ItemCount}");

                if (group.SampleTagNames.Count > 0)
                {
                    sb.AppendLine($"  样本标签名: {string.Join(", ", group.SampleTagNames.Take(8))}");
                }

                if (group.SampleComments.Count > 0)
                {
                    sb.AppendLine($"  样本注释: {string.Join(" | ", group.SampleComments.Take(6))}");
                }

                if (group.SampleEquipmentIds.Count > 0)
                {
                    sb.AppendLine($"  样本设备编号: {string.Join(", ", group.SampleEquipmentIds.Take(6))}");
                }

                sb.AppendLine();
            }

            var response = await CallLlmDirectAsync(StationGroupInferenceSystemPrompt, sb.ToString(), "PhysicalTagGroupStationInference");

            if (response == null || string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("[LlmAnchorAnnotator] 分组 LLM 工位推断返回为空或失败");
                return new Dictionary<string, StationInferenceResult>();
            }

            return ParseStationInferenceResponse(response.Content);
        }

        private static Dictionary<string, LlmClassificationResult> ParseClassificationResponse(string content)
        {
            var result = new Dictionary<string, LlmClassificationResult>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var start = content.IndexOf('[');
                var end = content.LastIndexOf(']');
                if (start < 0 || end <= start)
                {
                    return result;
                }

                var json = content.Substring(start, end - start + 1);
                var items = JsonSerializer.Deserialize<List<LlmClassificationResult>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.VarName))
                        {
                            result[item.VarName] = item;
                        }
                    }
                }
            }
            catch
            {
                // 解析失败不影响规则兜底
            }

            return result;
        }

        private static Dictionary<string, StationInferenceResult> ParseStationInferenceResponse(string content)
        {
            var result = new Dictionary<string, StationInferenceResult>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var start = content.IndexOf('[');
                var end = content.LastIndexOf(']');
                if (start < 0 || end <= start)
                {
                    return result;
                }

                var json = content.Substring(start, end - start + 1);
                var items = JsonSerializer.Deserialize<List<LlmStationInferenceResult>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item.TargetKey))
                        {
                            continue;
                        }

                        var key = item.TargetKey.StartsWith("var:", StringComparison.OrdinalIgnoreCase)
                            ? item.TargetKey.Substring(4)
                            : item.TargetKey;

                        result[key] = new StationInferenceResult
                        {
                            StationCode = NormalizeStationCode(item.StationCode),
                            Confidence = item.Confidence < 0 ? 0m : item.Confidence > 0.98m ? 0.98m : item.Confidence,
                            InferenceSource = "LLM",
                            InferenceDetail = item.TargetKey?.StartsWith("group:", StringComparison.OrdinalIgnoreCase) == true
                                ? item.TargetKey.Substring(6)
                                : "SingleItem",
                            Evidence = string.IsNullOrWhiteSpace(item.Reason)
                                ? new List<string>()
                                : new List<string> { $"[LLM] {item.Reason}" }
                        };
                    }
                }
            }
            catch
            {
                // 解析失败不影响规则兜底
            }

            return result;
        }

        private static string NormalizeStationCode(string stationCode)
        {
            var normalized = StationCodeHeuristics.NormalizeStationCode(stationCode);
            return StationCodeHeuristics.IsLikelyStationCode(normalized)
                ? normalized
                : string.Empty;
        }

        private class LlmAnnotationResult
        {
            public string VarName { get; set; }
            public bool IsVirtualQPoint { get; set; }
            public string SemanticLabel { get; set; }
            public string Reason { get; set; }
        }

        public class LlmClassificationResult
        {
            public string VarName { get; set; }

            /// <summary>VirtualQPoint / AuxiliaryOutput / HandshakeSignal / StateFlag / Unknown</summary>
            public string Category { get; set; }

            public string SemanticLabel { get; set; }
            public string Reason { get; set; }
        }

        private class LlmStationInferenceResult
        {
            public string TargetKey { get; set; }
            public string StationCode { get; set; }
            public decimal Confidence { get; set; }
            public string Reason { get; set; }
        }
    }
}
