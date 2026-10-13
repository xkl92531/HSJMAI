using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// Q点和虚拟锚点发现服务
    /// </summary>
    public class QPointAnchorDiscoveryService : ISelfScopedAutoInject
    {
        private static readonly string[] AuxiliaryKeywords = new[]
        {
            "lamp", "light", "led", "buzzer", "horn", "beacon", "indicator",
            "指示灯", "蜂鸣器", "报警灯", "照明"
        };

        private static readonly string[] ActionKeywords = new[]
        {
            "cyl", "cylinder", "gripper", "clamp", "motor", "run", "start", "fwd", "rev",
            "open", "close", "lift", "down", "up", "lock", "unlock", "气缸", "夹爪", "电机",
            "输送", "启动", "伸出", "缩回", "下降", "上升"
        };

        private static readonly string[] VirtualKeywords = new[]
        {
            "cmd", "req", "request", "run", "start", "allow", "permit", "release",
            "工单", "允许", "放行", "请求", "命令"
        };

        private static readonly string[] StatusKeywords = new[]
        {
            "busy", "done", "complete", "finish", "error", "status", "state", "valid",
            "fault", "alarm", "warn", "warning", "ready", "active", "feedback", "fbk",
            "actual", "ack", "endofprofile", "inposition", "home", "homed", "eno",
            "ret_val", "et", "errorid", "诊断", "故障", "报警", "状态", "反馈", "完成"
        };

        private static readonly string[] HandshakeKeywords = new[]
        {
            "mes", "robot", "vision", "barcode", "scanner", "agv", "external", "handshake",
            "ack", "busy", "state", "status", "机器人", "视觉", "条码", "扫码", "外部",
            "状态", "应答", "握手"
        };

        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_BlockService _blockService;
        private readonly StationInferenceService _stationInferenceService;
        private readonly LlmAnchorAnnotatorService _llmAnnotator;
        private readonly IPointReverseIndexService _iPointReverseIndexService;

        public QPointAnchorDiscoveryService(
            IPLC_IOTagService ioTagService,
            IPLC_VariableAccessService variableAccessService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_BlockService blockService,
            StationInferenceService stationInferenceService,
            LlmAnchorAnnotatorService llmAnnotator = null,
            IPointReverseIndexService iPointReverseIndexService = null)
        {
            _ioTagService = ioTagService;
            _variableAccessService = variableAccessService;
            _logicUnitService = logicUnitService;
            _blockService = blockService;
            _stationInferenceService = stationInferenceService;
            _llmAnnotator = llmAnnotator;
            _iPointReverseIndexService = iPointReverseIndexService;
        }

        public async Task<AnchorDiscoveryResultDto> DiscoverAsync(AnchorDiscoveryRequestDto request)
        {
            var physicalAnchors = request.IncludePhysicalAnchors
                ? await DiscoverPhysicalAnchorsAsync(request)
                : new List<AnchorCandidate>();

            var nonPhysicalDiscovery = request.IncludeVirtualAnchors || request.IncludeHandshakeAnchors
                ? await DiscoverNonPhysicalAnchorsAsync(request)
                : new NonPhysicalDiscoveryResult();

            var acceptedVirtual = request.IncludeVirtualAnchors
                ? nonPhysicalDiscovery.VirtualAnchors
                : new List<AnchorCandidate>();
            var reviewQueue = nonPhysicalDiscovery.ReviewQueue;
            var handshakeAnchors = request.IncludeHandshakeAnchors
                ? nonPhysicalDiscovery.HandshakeAnchors
                : new List<AnchorCandidate>();

            var inputAnchors = request.IncludeInputAnchors
                ? await DiscoverInputAnchorsAsync(request)
                : new List<AnchorCandidate>();

            var corePhysicalAnchors = SelectCorePhysicalAnchors(physicalAnchors);
            var coreVirtualAnchors = SelectCoreVirtualAnchors(acceptedVirtual);

            return new AnchorDiscoveryResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode,
                PhysicalAnchors = physicalAnchors,
                ConfirmedPhysicalAnchors = physicalAnchors.Where(it => it.AnchorType == "PhysicalAction").ToList(),
                CorePhysicalAnchors = corePhysicalAnchors,
                VirtualAnchors = acceptedVirtual,
                CoreVirtualAnchors = coreVirtualAnchors,
                VirtualAnchorReviewQueue = reviewQueue,
                HandshakeAnchors = handshakeAnchors,
                InputAnchors = inputAnchors,
                LlmDiagnostics = nonPhysicalDiscovery.LlmDiagnostics,
                Warnings = BuildWarnings(request, physicalAnchors, acceptedVirtual, handshakeAnchors)
            };
        }

        private async Task<NonPhysicalDiscoveryResult> DiscoverNonPhysicalAnchorsAsync(AnchorDiscoveryRequestDto request)
        {
            if (_llmAnnotator != null)
            {
                var llmResult = await DiscoverNonPhysicalAnchorsWithLlmAsync(request);
                if (llmResult.VirtualAnchors.Count > 0 || llmResult.HandshakeAnchors.Count > 0 || llmResult.ReviewQueue.Count > 0)
                {
                    return llmResult;
                }

                var ruleResult = await DiscoverNonPhysicalAnchorsWithRulesAsync(request);
                ruleResult.LlmDiagnostics = llmResult.LlmDiagnostics ?? new LlmDiscoveryDiagnostics();
                return ruleResult;
            }

            return await DiscoverNonPhysicalAnchorsWithRulesAsync(request);
        }

        private async Task<List<AnchorCandidate>> DiscoverPhysicalAnchorsAsync(AnchorDiscoveryRequestDto request)
        {
            HashSet<string> scopedVariableNames = null;
            if (request.BlockId.HasValue)
            {
                var blockId = request.BlockId.Value;
                scopedVariableNames = (await _variableAccessService.GetListAsync(it =>
                        it.IsDeleted == false &&
                        it.ProjectId == request.ProjectId &&
                        it.BlockId == blockId &&
                        !string.IsNullOrEmpty(it.VariableName)))
                    .Select(it => it.VariableName)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (scopedVariableNames.Count == 0)
                {
                    return new List<AnchorCandidate>();
                }
            }

            var tags = await _ioTagService.GetListAsync(it =>
                it.IsDeleted == false &&
                it.ProjectId == request.ProjectId &&
                !string.IsNullOrEmpty(it.Address) &&
                it.Address.Contains("%Q"));

            if (scopedVariableNames != null)
            {
                tags = tags
                    .Where(it => !string.IsNullOrWhiteSpace(it.TagName) && scopedVariableNames.Contains(it.TagName))
                    .ToList();
            }

            var stationInferenceMap = request.PrecomputedPhysicalTagStations;
            if (stationInferenceMap == null)
            {
                stationInferenceMap = await _stationInferenceService.InferForPhysicalTagsAsync(
                    tags,
                    request.UseLlmAnnotation);
            }

            var result = new List<AnchorCandidate>();
            foreach (var tag in tags)
            {
                stationInferenceMap.TryGetValue(tag.Id, out var stationInference);
                stationInference ??= _stationInferenceService.InferForPhysicalTag(tag);
                var stationCode = stationInference.StationCode;
                if (!string.IsNullOrEmpty(request.StationCode) &&
                    !string.Equals(stationCode, request.StationCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var combined = CombineText(tag.TagName, tag.Comment, tag.TagEffect, tag.TagType, tag.TagCategory);
                var isAuxiliary = ContainsAny(combined, AuxiliaryKeywords);
                if (isAuxiliary && !request.IncludeAuxiliary)
                {
                    continue;
                }

                var confidence = ContainsAny(combined, ActionKeywords) ? 0.95m : 0.72m;
                var evidence = new List<string>
                {
                    $"物理地址: {tag.Address}",
                    isAuxiliary ? "命中辅助输出关键词" : "命中物理Q点规则"
                };

                if (ContainsAny(combined, ActionKeywords))
                {
                    evidence.Add("命中动作输出关键词");
                }

                result.Add(new AnchorCandidate
                {
                    AnchorKey = $"physical:{tag.TagName}",
                    AnchorType = isAuxiliary ? "Auxiliary" : "PhysicalAction",
                    VariableName = tag.TagName,
                    Address = tag.Address,
                    StationCode = stationCode,
                    StationConfidence = stationInference.Confidence,
                    StationEvidence = stationInference.Evidence,
                    EquipmentId = tag.EquipmentId,
                    SemanticLabel = FirstNonEmpty(tag.Comment, tag.TagEffect, tag.TagCategory, tag.TagName),
                    Source = "PLC_IOTag",
                    Confidence = confidence,
                    DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                    PrimaryReason = isAuxiliary ? "命中辅助输出规则" : "命中物理Q点清单",
                    LogicSourceType = "PhysicalOutput",
                    ScoreBreakdown = BuildPhysicalScoreBreakdown(isAuxiliary, combined),
                    IOTagId = tag.Id,
                    Evidence = evidence.Concat(stationInference.Evidence.Select(it => $"工位证据: {it}")).ToList()
                });
            }

            return result
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // I 点输入锚点发现：扫描 %I 地址，通过反向索引关联受影响的 Q 点/AST
        // ─────────────────────────────────────────────────────────────────────

        private static readonly string[] SensorKeywords = new[]
        {
            "sensor", "proximity", "photoelectric", "limit", "switch", "encoder",
            "传感器", "接近", "光电", "限位", "开关", "编码器", "到位", "检测"
        };

        private static readonly string[] SafetyKeywords = new[]
        {
            "safety", "estop", "e-stop", "emergency", "guard", "door", "curtain", "interlock",
            "安全", "急停", "光栅", "门", "联锁", "防护"
        };

        private async Task<List<AnchorCandidate>> DiscoverInputAnchorsAsync(AnchorDiscoveryRequestDto request)
        {
            if (_iPointReverseIndexService == null)
            {
                return new List<AnchorCandidate>();
            }

            // 1. 查询所有 %I 地址的 IOTag
            var iTags = await _ioTagService.GetListAsync(it =>
                it.IsDeleted == false &&
                it.ProjectId == request.ProjectId &&
                !string.IsNullOrEmpty(it.Address) &&
                it.Address.Contains("%I"));

            if (iTags.Count == 0)
            {
                return new List<AnchorCandidate>();
            }

            // 2. 构建反向索引
            var reverseIndex = await _iPointReverseIndexService.BuildReverseIndexAsync(
                request.ProjectId, request.StationCode);

            // 3. 工位推断
            var stationInferenceMap = await _stationInferenceService.InferForPhysicalTagsAsync(
                iTags, request.UseLlmAnnotation);

            var result = new List<AnchorCandidate>();
            foreach (var tag in iTags)
            {
                stationInferenceMap.TryGetValue(tag.Id, out var stationInference);
                stationInference ??= _stationInferenceService.InferForPhysicalTag(tag);
                var stationCode = stationInference.StationCode;
                if (!string.IsNullOrEmpty(request.StationCode) &&
                    !string.Equals(stationCode, request.StationCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var combined = CombineText(tag.TagName, tag.Comment, tag.TagEffect, tag.TagType, tag.TagCategory);
                var isSafety = ContainsAny(combined, SafetyKeywords);
                var isSensor = ContainsAny(combined, SensorKeywords);

                // 查反向索引获取受影响的输出
                var affectedOutputs = new List<string>();
                var affectedBlocks = new List<string>();
                var tagName = tag.TagName?.Trim() ?? "";
                if (reverseIndex.TryGetValue(tagName, out var deps))
                {
                    affectedOutputs = deps
                        .SelectMany(d => d.OutputVariables ?? new List<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    affectedBlocks = deps
                        .Select(d => d.AstBlockName)
                        .Where(b => !string.IsNullOrWhiteSpace(b))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                var confidence = isSafety ? 0.90m : isSensor ? 0.80m : 0.65m;
                var subType = isSafety ? "Safety" : isSensor ? "Sensor" : "GeneralInput";

                var evidence = new List<string>
                {
                    $"物理地址: {tag.Address}",
                    isSafety ? "命中安全输入关键词" : isSensor ? "命中传感器关键词" : "通用 I 点输入"
                };
                if (affectedOutputs.Count > 0)
                {
                    evidence.Add($"影响 Q 点: {string.Join(", ", affectedOutputs.Take(5))}" +
                                 (affectedOutputs.Count > 5 ? $" 等共{affectedOutputs.Count}个" : ""));
                }
                if (affectedBlocks.Count > 0)
                {
                    evidence.Add($"关联逻辑块: {string.Join(", ", affectedBlocks.Take(3))}");
                }
                evidence.AddRange(stationInference.Evidence.Select(e => $"工位证据: {e}"));

                result.Add(new AnchorCandidate
                {
                    AnchorKey = $"input:{tagName}",
                    AnchorType = "PhysicalInput",
                    VariableName = tagName,
                    Address = tag.Address,
                    StationCode = stationCode,
                    StationConfidence = stationInference.Confidence,
                    StationEvidence = stationInference.Evidence,
                    EquipmentId = tag.EquipmentId,
                    SemanticLabel = FirstNonEmpty(tag.Comment, tag.TagEffect, tag.TagCategory, tag.TagName),
                    Source = "PLC_IOTag",
                    Confidence = confidence,
                    DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                    PrimaryReason = $"命中物理I点({subType})",
                    LogicSourceType = "PhysicalInput",
                    ScoreBreakdown = new AnchorScoreBreakdown
                    {
                        NameScore = isSafety ? 0.28m : isSensor ? 0.24m : 0.18m,
                        SourceScore = 0.25m,
                        WriteScore = !string.IsNullOrWhiteSpace(tag.Comment) ? 0.15m : 0m,
                        FlowScore = affectedOutputs.Count > 0 ? 0.20m : 0.05m,
                    },
                    IOTagId = tag.Id,
                    Evidence = evidence,
                });
            }

            return result
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // LLM 主分类路径：结构性预筛 + 富上下文 + LLM 四分类
        // 不依赖变量命名关键字，最大化覆盖不规范命名工程
        // ─────────────────────────────────────────────────────────────────────

        private async Task<NonPhysicalDiscoveryResult> DiscoverNonPhysicalAnchorsWithLlmAsync(AnchorDiscoveryRequestDto request)
        {
            var blockId = request.BlockId;
            // 1. 结构性预筛：找所有有写入记录的内部变量（不过滤 IOTag，因为 %Q 已由物理路径处理）
            var writeAccesses = blockId.HasValue
                ? await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.BlockId == blockId.Value &&
                    (it.AccessType == "WRITE" || it.AccessType == "READWRITE") &&
                    it.IsIOTag != true &&
                    it.IsStepVariable != true &&
                    it.IsAlarmVariable != true)
                : await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    (it.AccessType == "WRITE" || it.AccessType == "READWRITE") &&
                    it.IsIOTag != true &&
                    it.IsStepVariable != true &&
                    it.IsAlarmVariable != true);

            if (writeAccesses.Count == 0)
            {
                return new NonPhysicalDiscoveryResult();
            }

            // 1b. 类型过滤：只保留 DB 块中的标量类型变量，排除结构体和非 DB 块变量
            {
                var candidateVarNames = writeAccesses
                    .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                    .Select(it => it.VariableName)
                    .Distinct()
                    .ToList();
                var scalarDbVarNames = await _variableAccessService.GetScalarDbVarNamesAsync(
                    request.ProjectId, candidateVarNames);
                writeAccesses = writeAccesses
                    .Where(it => scalarDbVarNames.Contains(it.VariableName))
                    .ToList();
                if (writeAccesses.Count == 0)
                {
                    return new NonPhysicalDiscoveryResult();
                }
            }

            // 2. 读取次数（用于写读比证据）
            var allAccesses = blockId.HasValue
                ? await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.BlockId == blockId.Value)
                : await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId);

            var readCountByVar = allAccesses
                .Where(it => it.AccessType == "READ" || it.AccessType == "READWRITE")
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            // 3. 批量拉取 LogicUnit 和 Block 元数据（用于 网络标题 + 块名 上下文）
            var logicUnitIds = writeAccesses
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            var blockIds = writeAccesses
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();

            var logicUnitMap = logicUnitIds.Count > 0
                ? LogicUnitAnalysisPolicy.BuildEligibleMap(
                    await _logicUnitService.GetListAsync(it => logicUnitIds.Contains(it.Id) && it.IsDeleted == false))
                : new Dictionary<long, PLC_LogicUnitEntity>();

            var eligibleLogicUnitIds = LogicUnitAnalysisPolicy.BuildEligibleIdSet(logicUnitMap);
            writeAccesses = LogicUnitAnalysisPolicy.FilterAccessesByEligibleLogicUnits(writeAccesses, eligibleLogicUnitIds);
            allAccesses = LogicUnitAnalysisPolicy.FilterAccessesByEligibleLogicUnits(allAccesses, eligibleLogicUnitIds);

            var blockMap = blockIds.Count > 0
                ? (await _blockService.GetListAsync(it => blockIds.Contains(it.Id) && it.IsDeleted == false))
                    .ToDictionary(it => it.Id, it => it)
                : new Dictionary<long, PLC_BlockEntity>();

            var logicUnitAccessMap = LogicUnitAnalysisPolicy.BuildEligibleAccessMap(allAccesses, eligibleLogicUnitIds);

            // 4. Co-location map：同一 LogicUnit 内写入的其他变量
            var coLocationByUnit = writeAccesses
                .Where(it => it.LogicUnitId.HasValue && eligibleLogicUnitIds.Contains(it.LogicUnitId.Value))
                .GroupBy(it => it.LogicUnitId.Value)
                .ToDictionary(g => g.Key, g => g.Select(it => it.VariableName).Distinct().ToList());

            // 5. 构建富上下文 VirtualCandidateContext
            var contexts = new List<VirtualCandidateContext>();

            foreach (var group in writeAccesses
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase))
            {
                var variableName = group.Key;
                var writeItems = group.ToList();

                if (string.IsNullOrWhiteSpace(variableName))
                {
                    continue;
                }

                StationInferenceResult stationInference = null;
                request.PrecomputedVariableStations?.TryGetValue(variableName, out stationInference);
                stationInference ??= _stationInferenceService.InferForVariableGroup(
                    variableName,
                    writeItems,
                    logicUnitMap,
                    blockMap,
                    logicUnitAccessMap);

                readCountByVar.TryGetValue(variableName, out var readCount);

                var instructionTypes = writeItems
                    .Where(it => !string.IsNullOrWhiteSpace(it.Instruction))
                    .Select(it => it.Instruction.Trim())
                    .Distinct()
                    .Take(6)
                    .ToList();

                var networkTitles = writeItems
                    .Where(it => it.LogicUnitId.HasValue && logicUnitMap.ContainsKey(it.LogicUnitId.Value))
                    .Select(it => logicUnitMap[it.LogicUnitId.Value].Title)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Distinct()
                    .Take(5)
                    .ToList();

                var blockNames = writeItems
                    .Where(it => it.BlockId.HasValue && blockMap.ContainsKey(it.BlockId.Value))
                    .Select(it => blockMap[it.BlockId.Value].BlockName)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Distinct()
                    .Take(5)
                    .ToList();

                var coLocated = writeItems
                    .Where(it => it.LogicUnitId.HasValue && coLocationByUnit.ContainsKey(it.LogicUnitId.Value))
                    .SelectMany(it => coLocationByUnit[it.LogicUnitId.Value])
                    .Where(it => !string.Equals(it, variableName, StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .Take(8)
                    .ToList();

                var mergedInstructions = string.Join(" ", instructionTypes);
                var hasGraphWrite = ContainsAny(mergedInstructions, new[] { "GRAPH", "STEP_ACTION", "STEP_" });
                var hasLadCoilWrite = ContainsAny(mergedInstructions, new[] { "COIL", "OUT", "SET", "RESET" });

                contexts.Add(new VirtualCandidateContext
                {
                    ProjectId = request.ProjectId,
                    VariableName = variableName,
                    Comment = writeItems.Select(it => it.Comment).FirstOrDefault(it => !string.IsNullOrWhiteSpace(it)) ?? string.Empty,
                    WriteInstructionTypes = instructionTypes,
                    BlockNames = blockNames,
                    NetworkTitles = networkTitles,
                    CoLocatedWriteVariables = coLocated,
                    HasGraphWrite = hasGraphWrite,
                    HasLadCoilWrite = hasLadCoilWrite,
                    WriteCount = writeItems.Count,
                    ReadCount = readCount,
                    StationCode = stationInference.StationCode,
                    StationConfidence = stationInference.Confidence,
                    StationEvidence = stationInference.Evidence,
                    LogicSourceType = hasGraphWrite ? "GraphAction" : hasLadCoilWrite ? "LadCoil" : "VariableWrite"
                });
            }

            var diagnostics = new LlmDiscoveryDiagnostics
            {
                TotalContextCount = contexts.Count
            };

            if (contexts.Count == 0)
            {
                return new NonPhysicalDiscoveryResult
                {
                    LlmDiagnostics = diagnostics
                };
            }

            if (request.UseLlmAnnotation && _llmAnnotator != null)
            {
                await EnrichVirtualCandidateStationsAsync(contexts);
            }

            if (!string.IsNullOrWhiteSpace(request.StationCode))
            {
                contexts = contexts
                    .Where(it => string.Equals(it.StationCode, request.StationCode, StringComparison.OrdinalIgnoreCase))
                    .Where(it => !HasConflictingExplicitStationReference(
                        request.StationCode,
                        it.VariableName,
                        it.Comment,
                        string.Join(" ", it.BlockNames ?? new List<string>()),
                        string.Join(" ", it.NetworkTitles ?? new List<string>())))
                    .ToList();
            }

            diagnostics.StationFilteredCount = contexts.Count;

            if (contexts.Count == 0)
            {
                return new NonPhysicalDiscoveryResult
                {
                    LlmDiagnostics = diagnostics
                };
            }

            // 6. 结构性预筛：减少进 LLM 的候选数量（约 60-70%）
            // 规则 A：有 GRAPH/LAD Coil 写入 → 强证据，直接保留
            // 规则 B：读次数 > 5×写次数 且无任何指令记录 → 典型状态反馈/传感器变量，排除
            // 规则 C：只有1次写入且无指令记录 → 证据太弱，排除
            var filteredContexts = new List<VirtualCandidateContext>();
            var suppressedSamples = new List<LlmCandidateDiagnosticItem>();
            foreach (var ctx in contexts)
            {
                if (ctx.HasGraphWrite || ctx.HasLadCoilWrite)
                {
                    filteredContexts.Add(ctx);
                    continue;
                }

                if (IsLikelyStatusLikeVirtualCandidate(
                    ctx.VariableName,
                    CombineText(ctx.VariableName, ctx.Comment, string.Join(" ", ctx.NetworkTitles ?? new List<string>()), string.Join(" ", ctx.BlockNames ?? new List<string>())),
                    ctx.LogicSourceType,
                    ctx.WriteCount,
                    ctx.ReadCount))
                {
                    suppressedSamples.Add(BuildDiagnosticItem(ctx, "Suppressed", "命中状态/反馈关键词，缺少动作命令证据"));
                    continue;
                }

                if (ShouldSuppressNonPhysicalCandidate(ctx))
                {
                    suppressedSamples.Add(BuildDiagnosticItem(ctx, "Suppressed", "变量名过短、近纯数字或缺少可解释标识"));
                    continue;
                }

                if (ctx.ReadCount > ctx.WriteCount * 5 && ctx.WriteInstructionTypes.Count == 0)
                {
                    suppressedSamples.Add(BuildDiagnosticItem(ctx, "Suppressed", "读远多于写且无指令记录，像状态反馈/传感器"));
                    continue;
                }

                if (ctx.WriteCount == 1 && ctx.WriteInstructionTypes.Count == 0)
                {
                    suppressedSamples.Add(BuildDiagnosticItem(ctx, "Suppressed", "仅 1 次写入且无指令记录，证据过弱"));
                    continue;
                }

                filteredContexts.Add(ctx);
            }

            diagnostics.SentToLlmCount = filteredContexts.Count;
            diagnostics.SuppressedCount = suppressedSamples.Count;
            diagnostics.SuppressedSamples = suppressedSamples.Take(12).ToList();

            // 6.5. GRAPH Action 写入信号自动确认为 Q 点锚点（不进 LLM）
            // GRAPH Step Action 的写入信号本质上就是该步骤要执行的 Q 点输出，
            // 不需要 LLM 判断，直接提升为 Confirmed VirtualAction。
            var graphAutoConfirmed = new List<AnchorCandidate>();
            var nonGraphCandidates = new List<VirtualCandidateContext>();
            foreach (var ctx in filteredContexts)
            {
                if (ctx.HasGraphWrite)
                {
                    graphAutoConfirmed.Add(new AnchorCandidate
                    {
                        AnchorKey = $"virtual:{ctx.VariableName}:GraphAction",
                        AnchorType = "VirtualAction",
                        VariableName = ctx.VariableName,
                        StationCode = ctx.StationCode,
                        StationConfidence = ctx.StationConfidence,
                        StationEvidence = ctx.StationEvidence,
                        SemanticLabel = !string.IsNullOrWhiteSpace(ctx.Comment) ? ctx.Comment : ctx.VariableName,
                        Source = "PLC_VariableAccess+GraphAction",
                        Confidence = 0.95m, // GRAPH action 写入 = 确定性极高
                        DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                        PrimaryReason = "GRAPH Step Action 写入信号 → 自动确认为 Q 点锚点",
                        LogicSourceType = "GraphAction",
                        IsCoreAnchor = true,
                        ScoreBreakdown = BuildVirtualScoreBreakdown(
                            ctx.VariableName, ctx.Comment, ctx.WriteCount, ctx.ReadCount, ctx.StationCode, "GraphAction"),
                        Evidence = new List<string>
                        {
                            "GRAPH Step Action 写入 → 必然为 Q 点",
                            $"写入次数: {ctx.WriteCount}",
                            $"读取次数: {ctx.ReadCount}",
                        }.Concat(ctx.StationEvidence.Select(it => $"工位证据: {it}")).ToList()
                    });
                }
                else
                {
                    nonGraphCandidates.Add(ctx);
                }
            }

            // 7. LLM 主分类（仅对非 GRAPH 的预筛后候选）
            diagnostics.SentToLlmCount = nonGraphCandidates.Count;
            var classifications = await _llmAnnotator.ClassifyVirtualCandidatesAsync(nonGraphCandidates);
            diagnostics.LlmReturnedCount = classifications.Count;

            // 7.1 映射分类结果到 AnchorCandidate
            var accepted = new List<AnchorCandidate>(graphAutoConfirmed); // GRAPH 自动确认的先放进去
            var handshakeAccepted = new List<AnchorCandidate>();
            var reviewQueue = new List<AnchorCandidate>();
            var classifiedSamples = new List<LlmCandidateDiagnosticItem>();
            var unknownDroppedCount = 0;
            var stateFlagDroppedCount = 0;

            foreach (var ctx in nonGraphCandidates)
            {
                classifications.TryGetValue(ctx.VariableName, out var cls);
                var category = cls?.Category ?? "Unknown";
                var semanticLabel = !string.IsNullOrWhiteSpace(cls?.SemanticLabel) ? cls.SemanticLabel : ctx.VariableName;
                var reason = cls?.Reason ?? string.Empty;

                if (string.Equals(category, "Unknown", StringComparison.OrdinalIgnoreCase) && ShouldSuppressUnknownReview(ctx))
                {
                    unknownDroppedCount++;
                    classifiedSamples.Add(BuildDiagnosticItem(ctx, "Dropped", string.IsNullOrWhiteSpace(reason) ? "LLM 返回 Unknown 且命中弱候选抑制规则" : reason, category));
                    continue;
                }

                // StateFlag 直接排除
                if (string.Equals(category, "StateFlag", StringComparison.OrdinalIgnoreCase))
                {
                    stateFlagDroppedCount++;
                    classifiedSamples.Add(BuildDiagnosticItem(ctx, "Dropped", string.IsNullOrWhiteSpace(reason) ? "LLM 判定为状态位，非动作锚点" : reason, category));
                    continue;
                }

                var isHandshake = string.Equals(category, "HandshakeSignal", StringComparison.OrdinalIgnoreCase);
                var anchorType = isHandshake
                    ? "HandshakeState"
                    : string.Equals(category, "AuxiliaryOutput", StringComparison.OrdinalIgnoreCase)
                        ? "Auxiliary"
                        : "VirtualAction";

                if (anchorType == "Auxiliary" && !request.IncludeAuxiliary)
                {
                    continue;
                }

                var isConfirmed = string.Equals(category, "VirtualQPoint", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(category, "AuxiliaryOutput", StringComparison.OrdinalIgnoreCase) ||
                                  isHandshake;

                var status = isConfirmed ? AnchorDiscoveryStatus.Confirmed : AnchorDiscoveryStatus.Review;
                var evidenceText = CombineText(
                    ctx.Comment,
                    string.Join(" ", ctx.NetworkTitles ?? new List<string>()),
                    string.Join(" ", ctx.BlockNames ?? new List<string>()));

                var candidate = new AnchorCandidate
                {
                    AnchorKey = isHandshake
                        ? $"handshake:{ctx.VariableName}"
                        : $"virtual:{ctx.VariableName}:{ctx.LogicSourceType}",
                    AnchorType = anchorType,
                    VariableName = ctx.VariableName,
                    StationCode = ctx.StationCode,
                    StationConfidence = ctx.StationConfidence,
                    StationEvidence = ctx.StationEvidence,
                    SemanticLabel = semanticLabel,
                    Source = "PLC_VariableAccess+LLM",
                    Confidence = isHandshake
                        ? Math.Max(0.76m, CalculateHandshakeConfidence(ctx.ReadCount, ctx.WriteCount, evidenceText))
                        : isConfirmed ? 0.82m : 0.50m,
                    DiscoveryStatus = status,
                    PrimaryReason = string.IsNullOrWhiteSpace(reason) ? $"LLM 分类为 {category}" : $"[LLM] {reason}",
                    LogicSourceType = isHandshake ? "HandshakeFlow" : ctx.LogicSourceType,
                    ScoreBreakdown = isHandshake
                        ? BuildHandshakeScoreBreakdown(ctx.ReadCount, ctx.WriteCount, evidenceText, ctx.StationCode)
                        : BuildVirtualScoreBreakdown(
                            ctx.VariableName, ctx.Comment, ctx.WriteCount, ctx.ReadCount, ctx.StationCode, ctx.LogicSourceType),
                    Evidence = new List<string>
                    {
                        $"写入次数: {ctx.WriteCount}",
                        $"读取次数: {ctx.ReadCount}",
                        $"逻辑来源: {(isHandshake ? "HandshakeFlow" : ctx.LogicSourceType)}",
                        string.IsNullOrWhiteSpace(reason) ? $"LLM 分类: {category}" : $"[LLM] {reason}"
                    }.Concat(ctx.StationEvidence.Select(it => $"工位证据: {it}")).ToList()
                };

                classifiedSamples.Add(BuildDiagnosticItem(
                    ctx,
                    isHandshake ? "Handshake" : isConfirmed ? "Confirmed" : "Review",
                    string.IsNullOrWhiteSpace(reason) ? $"LLM 分类为 {category}" : reason,
                    category));

                if (isHandshake)
                {
                    handshakeAccepted.Add(candidate);
                }
                else if (isConfirmed)
                {
                    accepted.Add(candidate);
                }
                else
                {
                    reviewQueue.Add(candidate);
                }
            }

            return new NonPhysicalDiscoveryResult
            {
                VirtualAnchors = accepted.OrderByDescending(it => it.Confidence).ThenBy(it => it.VariableName).ToList(),
                HandshakeAnchors = handshakeAccepted.OrderByDescending(it => it.Confidence).ThenBy(it => it.VariableName).ToList(),
                ReviewQueue = reviewQueue.OrderByDescending(it => it.Confidence).ThenBy(it => it.VariableName).ToList(),
                LlmDiagnostics = new LlmDiscoveryDiagnostics
                {
                    TotalContextCount = diagnostics.TotalContextCount,
                    StationFilteredCount = diagnostics.StationFilteredCount,
                    SentToLlmCount = diagnostics.SentToLlmCount,
                    SuppressedCount = diagnostics.SuppressedCount,
                    LlmReturnedCount = diagnostics.LlmReturnedCount,
                    ConfirmedByLlmCount = accepted.Count,
                    HandshakeByLlmCount = handshakeAccepted.Count,
                    ReviewByLlmCount = reviewQueue.Count,
                    UnknownDroppedCount = unknownDroppedCount,
                    StateFlagDroppedCount = stateFlagDroppedCount,
                    SuppressedSamples = diagnostics.SuppressedSamples,
                    ClassifiedSamples = classifiedSamples.Take(16).ToList()
                }
            };
        }

        private static LlmCandidateDiagnosticItem BuildDiagnosticItem(
            VirtualCandidateContext context,
            string decision,
            string reason,
            string category = "")
        {
            return new LlmCandidateDiagnosticItem
            {
                VariableName = context?.VariableName ?? string.Empty,
                Decision = decision,
                Reason = reason ?? string.Empty,
                Category = category ?? string.Empty,
                StationCode = context?.StationCode ?? string.Empty,
                LogicSourceType = context?.LogicSourceType ?? string.Empty
            };
        }

        private static bool ShouldSuppressNonPhysicalCandidate(VirtualCandidateContext context)
        {
            var variableName = context?.VariableName?.Trim();
            if (string.IsNullOrWhiteSpace(variableName))
            {
                return true;
            }

            if (IsNumericLikeToken(variableName))
            {
                return true;
            }

            if (variableName.Length <= 2 && !ContainsAsciiLetter(variableName) && !ContainsCjk(variableName))
            {
                return true;
            }

            return false;
        }

        private static bool ShouldSuppressUnknownReview(VirtualCandidateContext context)
        {
            var variableName = context?.VariableName?.Trim();
            if (string.IsNullOrWhiteSpace(variableName))
            {
                return true;
            }

            if (ShouldSuppressNonPhysicalCandidate(context))
            {
                return true;
            }

            var normalized = variableName.Replace("[", string.Empty).Replace("]", string.Empty);
            if (normalized.StartsWith("Aux.", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Temp.", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Tmp.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool IsNumericLikeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.All(ch => char.IsDigit(ch) || ch == '.' || ch == '_' || ch == '-');
        }

        private static bool ContainsAsciiLetter(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Any(ch =>
                (ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z'));
        }

        private static bool ContainsCjk(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
        }

        private static bool HasConflictingExplicitStationReference(string requestedStationCode, params string[] texts)
        {
            if (string.IsNullOrWhiteSpace(requestedStationCode) || texts == null || texts.Length == 0)
            {
                return false;
            }

            var explicitStations = texts
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .SelectMany(StationCodeHeuristics.ExtractStationCodes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (explicitStations.Count == 0)
            {
                return false;
            }

            return explicitStations.Any(it => !IsSameStationAlias(it, requestedStationCode))
                && !explicitStations.Any(it => IsSameStationAlias(it, requestedStationCode));
        }

        private static bool IsSameStationAlias(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            var leftAlias = StationCodeHeuristics.GetAliasFamilyKey(left);
            var rightAlias = StationCodeHeuristics.GetAliasFamilyKey(right);

            if (!string.IsNullOrWhiteSpace(leftAlias) && !string.IsNullOrWhiteSpace(rightAlias))
            {
                return string.Equals(leftAlias, rightAlias, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(
                StationCodeHeuristics.NormalizeStationCode(left),
                StationCodeHeuristics.NormalizeStationCode(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private async Task<NonPhysicalDiscoveryResult> DiscoverNonPhysicalAnchorsWithRulesAsync(AnchorDiscoveryRequestDto request)
        {
            var virtualResult = request.IncludeVirtualAnchors
                ? await DiscoverVirtualAnchorsWithKeywordsAsync(request)
                : (Accepted: new List<AnchorCandidate>(), ReviewQueue: new List<AnchorCandidate>());

            var handshakeResult = request.IncludeHandshakeAnchors
                ? await DiscoverHandshakeAnchorsAsync(request)
                : new List<AnchorCandidate>();

            return new NonPhysicalDiscoveryResult
            {
                VirtualAnchors = virtualResult.Accepted,
                HandshakeAnchors = handshakeResult,
                ReviewQueue = virtualResult.ReviewQueue
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // 关键字兜底路径：命名规范时的快速识别，不依赖 LLM
        // ─────────────────────────────────────────────────────────────────────

        private async Task<(List<AnchorCandidate> Accepted, List<AnchorCandidate> ReviewQueue)> DiscoverVirtualAnchorsWithKeywordsAsync(AnchorDiscoveryRequestDto request)
        {
            var blockId = request.BlockId;
            var variableAccesses = blockId.HasValue
                ? await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.BlockId == blockId.Value &&
                    !string.IsNullOrEmpty(it.VariableName))
                : await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    !string.IsNullOrEmpty(it.VariableName));

            // 类型过滤：只保留 DB 块中的标量类型变量，排除结构体和非 DB 块变量
            {
                var candidateVarNames = variableAccesses
                    .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                    .Select(it => it.VariableName)
                    .Distinct()
                    .ToList();
                var scalarDbVarNames = await _variableAccessService.GetScalarDbVarNamesAsync(
                    request.ProjectId, candidateVarNames);
                variableAccesses = variableAccesses
                    .Where(it => scalarDbVarNames.Contains(it.VariableName))
                    .ToList();
            }

            var logicUnitIds = variableAccesses
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            var blockIds = variableAccesses
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();

            var logicUnitMap = logicUnitIds.Count > 0
                ? LogicUnitAnalysisPolicy.BuildEligibleMap(
                    await _logicUnitService.GetListAsync(it => logicUnitIds.Contains(it.Id) && it.IsDeleted == false))
                : new Dictionary<long, PLC_LogicUnitEntity>();

            var eligibleLogicUnitIds = LogicUnitAnalysisPolicy.BuildEligibleIdSet(logicUnitMap);
            variableAccesses = LogicUnitAnalysisPolicy.FilterAccessesByEligibleLogicUnits(variableAccesses, eligibleLogicUnitIds);

            var blockMap = blockIds.Count > 0
                ? (await _blockService.GetListAsync(it => blockIds.Contains(it.Id) && it.IsDeleted == false))
                    .ToDictionary(it => it.Id, it => it)
                : new Dictionary<long, PLC_BlockEntity>();

            var logicUnitAccessMap = LogicUnitAnalysisPolicy.BuildEligibleAccessMap(variableAccesses, eligibleLogicUnitIds);

            var accepted = new List<AnchorCandidate>();
            var reviewQueue = new List<AnchorCandidate>();
            foreach (var group in variableAccesses
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase))
            {
                var variableName = group.Key;
                if (!IsLikelyVirtualVariable(variableName))
                {
                    continue;
                }

                var readCount = group.Count(IsReadAccess);
                var writeCount = group.Count(IsWriteAccess);
                if (writeCount == 0)
                {
                    continue;
                }

                var mergedText = CombineText(new[] { variableName }.Concat(group.Select(it => it.Comment ?? string.Empty)).ToArray());
                if (ContainsAny(mergedText, HandshakeKeywords))
                {
                    continue;
                }

                var groupedAccesses = group.ToList();
                var logicSourceType = ResolveVirtualLogicSourceType(groupedAccesses);

                // GRAPH Action 写入信号：直接确认为 Q 点，跳过状态变量/分数过滤
                if (logicSourceType == "GraphAction")
                {
                    StationInferenceResult graphStationInf = null;
                    request.PrecomputedVariableStations?.TryGetValue(variableName, out graphStationInf);
                    graphStationInf ??= _stationInferenceService.InferForVariableGroup(
                        variableName, groupedAccesses, logicUnitMap, blockMap, logicUnitAccessMap);
                    if (!string.IsNullOrEmpty(request.StationCode) &&
                        !string.Equals(graphStationInf.StationCode, request.StationCode, StringComparison.OrdinalIgnoreCase))
                        continue;

                    accepted.Add(new AnchorCandidate
                    {
                        AnchorKey = $"virtual:{variableName}:GraphAction",
                        AnchorType = "VirtualAction",
                        VariableName = variableName,
                        StationCode = graphStationInf.StationCode,
                        StationConfidence = graphStationInf.Confidence,
                        StationEvidence = graphStationInf.Evidence,
                        SemanticLabel = FirstNonEmpty(group.Select(it => it.Comment).FirstOrDefault(), variableName),
                        Source = "PLC_VariableAccess+GraphAction",
                        Confidence = 0.95m,
                        DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                        PrimaryReason = "GRAPH Step Action 写入信号 → 自动确认为 Q 点锚点",
                        LogicSourceType = "GraphAction",
                        IsCoreAnchor = true,
                        ScoreBreakdown = BuildVirtualScoreBreakdown(variableName, mergedText, writeCount, readCount,
                            graphStationInf.StationCode, "GraphAction"),
                        Evidence = new List<string>
                        {
                            "GRAPH Step Action 写入 → 必然为 Q 点",
                            $"写入次数: {writeCount}", $"读取次数: {readCount}",
                        }.Concat(graphStationInf.Evidence.Select(it => $"工位证据: {it}")).ToList()
                    });
                    continue;
                }

                if (IsLikelyStatusLikeVirtualCandidate(variableName, mergedText, logicSourceType, writeCount, readCount))
                {
                    continue;
                }

                StationInferenceResult stationInference = null;
                request.PrecomputedVariableStations?.TryGetValue(variableName, out stationInference);
                stationInference ??= _stationInferenceService.InferForVariableGroup(
                    variableName,
                    groupedAccesses,
                    logicUnitMap,
                    blockMap,
                    logicUnitAccessMap);
                var stationCode = stationInference.StationCode;
                if (!string.IsNullOrEmpty(request.StationCode) &&
                    !string.Equals(stationCode, request.StationCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (HasConflictingExplicitStationReference(
                    request.StationCode,
                    variableName,
                    mergedText,
                    string.Join(" ", groupedAccesses.Select(it => it.Comment).Where(it => !string.IsNullOrWhiteSpace(it)).Distinct().Take(4))))
                {
                    continue;
                }
                var scoreBreakdown = BuildVirtualScoreBreakdown(variableName, mergedText, writeCount, readCount, stationCode, logicSourceType);
                var confidence = scoreBreakdown.TotalScore;
                if (confidence < 0.58m)
                {
                    continue;
                }

                var evidence = new List<string>
                {
                    $"写入次数: {writeCount}",
                    $"读取次数: {readCount}",
                    readCount == 0 ? "满足只写不读规则" : "满足写主导规则",
                    $"逻辑来源: {logicSourceType}"
                };

                if (ContainsAny(mergedText, VirtualKeywords))
                {
                    evidence.Add("命中动作命令关键词");
                }

                var candidate = new AnchorCandidate
                {
                    AnchorKey = $"virtual:{variableName}:{logicSourceType}",
                    AnchorType = "VirtualAction",
                    VariableName = variableName,
                    StationCode = stationCode,
                    StationConfidence = stationInference.Confidence,
                    StationEvidence = stationInference.Evidence,
                    SemanticLabel = FirstNonEmpty(group.Select(it => it.Comment).FirstOrDefault(), variableName),
                    Source = "PLC_VariableAccess",
                    Confidence = confidence,
                    DiscoveryStatus = confidence >= 0.70m ? AnchorDiscoveryStatus.Confirmed : AnchorDiscoveryStatus.Review,
                    PrimaryReason = confidence >= 0.70m ? "满足虚拟动作输出候选规则" : "满足部分虚拟动作证据，待人工复核",
                    LogicSourceType = logicSourceType,
                    ScoreBreakdown = scoreBreakdown,
                    Evidence = evidence.Concat(stationInference.Evidence.Select(it => $"工位证据: {it}")).ToList()
                };

                if (candidate.DiscoveryStatus == AnchorDiscoveryStatus.Confirmed)
                {
                    accepted.Add(candidate);
                }
                else
                {
                    reviewQueue.Add(candidate);
                }
            }

            return (
                accepted.OrderByDescending(it => it.Confidence).ThenBy(it => it.VariableName).ToList(),
                reviewQueue.OrderByDescending(it => it.Confidence).ThenBy(it => it.VariableName).ToList());
        }

        private async Task<List<AnchorCandidate>> DiscoverHandshakeAnchorsAsync(AnchorDiscoveryRequestDto request)
        {
            var blockId = request.BlockId;
            var variableAccesses = blockId.HasValue
                ? await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.BlockId == blockId.Value &&
                    !string.IsNullOrEmpty(it.VariableName))
                : await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    !string.IsNullOrEmpty(it.VariableName));

            // 类型过滤：只保留 DB 块中的标量类型变量，排除结构体和非 DB 块变量
            {
                var candidateVarNames = variableAccesses
                    .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                    .Select(it => it.VariableName)
                    .Distinct()
                    .ToList();
                var scalarDbVarNames = await _variableAccessService.GetScalarDbVarNamesAsync(
                    request.ProjectId, candidateVarNames);
                variableAccesses = variableAccesses
                    .Where(it => scalarDbVarNames.Contains(it.VariableName))
                    .ToList();
            }

            var logicUnitIds = variableAccesses
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            var blockIds = variableAccesses
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();

            var logicUnitMap = logicUnitIds.Count > 0
                ? LogicUnitAnalysisPolicy.BuildEligibleMap(
                    await _logicUnitService.GetListAsync(it => logicUnitIds.Contains(it.Id) && it.IsDeleted == false))
                : new Dictionary<long, PLC_LogicUnitEntity>();

            var eligibleLogicUnitIds = LogicUnitAnalysisPolicy.BuildEligibleIdSet(logicUnitMap);
            variableAccesses = LogicUnitAnalysisPolicy.FilterAccessesByEligibleLogicUnits(variableAccesses, eligibleLogicUnitIds);

            var blockMap = blockIds.Count > 0
                ? (await _blockService.GetListAsync(it => blockIds.Contains(it.Id) && it.IsDeleted == false))
                    .ToDictionary(it => it.Id, it => it)
                : new Dictionary<long, PLC_BlockEntity>();

            var logicUnitAccessMap = LogicUnitAnalysisPolicy.BuildEligibleAccessMap(variableAccesses, eligibleLogicUnitIds);

            var result = new List<AnchorCandidate>();
            foreach (var group in variableAccesses
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase))
            {
                var variableName = group.Key;
                var mergedText = CombineText(new[] { variableName }.Concat(group.Select(it => it.Comment ?? string.Empty)).ToArray());
                if (!ContainsAny(mergedText, HandshakeKeywords))
                {
                    continue;
                }

                var readCount = group.Count(IsReadAccess);
                var writeCount = group.Count(IsWriteAccess);
                if (readCount < 2 && writeCount == 0)
                {
                    continue;
                }

                var groupedAccesses = group.ToList();
                StationInferenceResult stationInference = null;
                request.PrecomputedVariableStations?.TryGetValue(variableName, out stationInference);
                stationInference ??= _stationInferenceService.InferForVariableGroup(
                    variableName,
                    groupedAccesses,
                    logicUnitMap,
                    blockMap,
                    logicUnitAccessMap);
                var stationCode = stationInference.StationCode;
                if (!string.IsNullOrEmpty(request.StationCode) &&
                    !string.Equals(stationCode, request.StationCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (HasConflictingExplicitStationReference(
                    request.StationCode,
                    variableName,
                    mergedText,
                    string.Join(" ", groupedAccesses.Select(it => it.Comment).Where(it => !string.IsNullOrWhiteSpace(it)).Distinct().Take(4))))
                {
                    continue;
                }

                result.Add(new AnchorCandidate
                {
                    AnchorKey = $"handshake:{variableName}",
                    AnchorType = "HandshakeState",
                    VariableName = variableName,
                    StationCode = stationCode,
                    StationConfidence = stationInference.Confidence,
                    StationEvidence = stationInference.Evidence,
                    SemanticLabel = FirstNonEmpty(group.Select(it => it.Comment).FirstOrDefault(), variableName),
                    Source = "PLC_VariableAccess",
                    Confidence = CalculateHandshakeConfidence(readCount, writeCount, mergedText),
                    DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                    PrimaryReason = "命中握手状态候选规则",
                    LogicSourceType = "HandshakeFlow",
                    ScoreBreakdown = BuildHandshakeScoreBreakdown(readCount, writeCount, mergedText, stationCode),
                    Evidence = new List<string>
                    {
                        $"读取次数: {readCount}",
                        $"写入次数: {writeCount}",
                        "命中握手/外部系统关键词"
                    }.Concat(stationInference.Evidence.Select(it => $"工位证据: {it}")).ToList()
                });
            }

            return result
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .ToList();
        }

        private static List<string> BuildWarnings(
            AnchorDiscoveryRequestDto request,
            List<AnchorCandidate> physicalAnchors,
            List<AnchorCandidate> virtualAnchors,
            List<AnchorCandidate> handshakeAnchors)
        {
            var warnings = new List<string>();
            if (request.IncludePhysicalAnchors && physicalAnchors.Count == 0)
            {
                warnings.Add("未发现物理 Q 点候选，请检查 IOTag 是否已完整入库。");
            }

            if (request.IncludeVirtualAnchors && virtualAnchors.Count == 0)
            {
                warnings.Add("未发现虚拟 Q 点候选，可能是项目暂未包含单向暴露变量，或变量访问记录不完整。");
            }

            if (request.IncludeHandshakeAnchors && handshakeAnchors.Count == 0)
            {
                warnings.Add("未发现握手位候选，后续可在接入 MES/机器人/视觉工位样本后进一步扩展规则。");
            }

            return warnings;
        }

        private static List<AnchorCandidate> SelectCorePhysicalAnchors(IEnumerable<AnchorCandidate> anchors)
        {
            return SelectCoreAnchors(
                anchors,
                minimumScore: 0.72m,
                calculateScore: CalculatePhysicalCoreScore,
                calculateReason: BuildPhysicalCoreReason);
        }

        private static List<AnchorCandidate> SelectCoreVirtualAnchors(IEnumerable<AnchorCandidate> anchors)
        {
            return SelectCoreAnchors(
                anchors,
                minimumScore: 0.74m,
                calculateScore: CalculateVirtualCoreScore,
                calculateReason: BuildVirtualCoreReason);
        }

        private static List<AnchorCandidate> SelectCoreAnchors(
            IEnumerable<AnchorCandidate> anchors,
            decimal minimumScore,
            Func<AnchorCandidate, decimal> calculateScore,
            Func<AnchorCandidate, string> calculateReason)
        {
            var results = new List<AnchorCandidate>();
            foreach (var anchor in anchors ?? Enumerable.Empty<AnchorCandidate>())
            {
                var score = calculateScore(anchor);
                anchor.CoreScore = score;
                anchor.CoreReason = calculateReason(anchor);
                anchor.IsCoreAnchor = score >= minimumScore;
                if (anchor.IsCoreAnchor)
                {
                    results.Add(anchor);
                }
            }

            return results
                .OrderByDescending(it => it.CoreScore)
                .ThenByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .ToList();
        }

        private static decimal CalculatePhysicalCoreScore(AnchorCandidate anchor)
        {
            if (anchor == null || !string.Equals(anchor.AnchorType, "PhysicalAction", StringComparison.OrdinalIgnoreCase))
            {
                return 0m;
            }

            var score = 0.52m;
            if (anchor.Confidence >= 0.90m)
            {
                score += 0.18m;
            }
            else if (anchor.Confidence >= 0.72m)
            {
                score += 0.12m;
            }

            if (!string.IsNullOrWhiteSpace(anchor.StationCode))
            {
                score += 0.12m;
            }

            if (anchor.StationConfidence >= 0.60m)
            {
                score += 0.08m;
            }

            if (anchor.ScoreBreakdown?.NameScore >= 0.25m)
            {
                score += 0.10m;
            }

            if (anchor.Evidence.Any(it => it.Contains("动作输出关键词", StringComparison.OrdinalIgnoreCase)))
            {
                score += 0.08m;
            }

            return Math.Min(score, 0.98m);
        }

        private static decimal CalculateVirtualCoreScore(AnchorCandidate anchor)
        {
            if (anchor == null ||
                !string.Equals(anchor.AnchorType, "VirtualAction", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(anchor.DiscoveryStatus, AnchorDiscoveryStatus.Confirmed, StringComparison.OrdinalIgnoreCase))
            {
                return 0m;
            }

            var score = 0.32m;
            if (string.Equals(anchor.LogicSourceType, "GraphAction", StringComparison.OrdinalIgnoreCase))
            {
                score += 0.18m;
            }
            else if (string.Equals(anchor.LogicSourceType, "LadCoil", StringComparison.OrdinalIgnoreCase))
            {
                score += 0.12m;
            }
            else if (string.Equals(anchor.LogicSourceType, "VariableWrite", StringComparison.OrdinalIgnoreCase))
            {
                score += 0.06m;
            }

            if (!string.IsNullOrWhiteSpace(anchor.StationCode))
            {
                score += 0.14m;
            }

            if (anchor.StationConfidence >= 0.55m)
            {
                score += 0.06m;
            }

            if (anchor.Confidence >= 0.82m)
            {
                score += 0.12m;
            }
            else if (anchor.Confidence >= 0.70m)
            {
                score += 0.06m;
            }

            if (anchor.ScoreBreakdown?.NameScore >= 0.16m)
            {
                score += 0.08m;
            }

            if (anchor.Evidence.Any(it => it.Contains("LLM", StringComparison.OrdinalIgnoreCase) || it.Contains("写入次数", StringComparison.OrdinalIgnoreCase)))
            {
                score += 0.06m;
            }

            return Math.Min(score, 0.98m);
        }

        private static string BuildPhysicalCoreReason(AnchorCandidate anchor)
        {
            if (anchor == null || !string.Equals(anchor.AnchorType, "PhysicalAction", StringComparison.OrdinalIgnoreCase))
            {
                return "非物理动作输出";
            }

            var reasons = new List<string> { "硬件Q点动作输出" };
            if (!string.IsNullOrWhiteSpace(anchor.StationCode))
            {
                reasons.Add($"已归属工位 {anchor.StationCode}");
            }

            if (anchor.ScoreBreakdown?.NameScore >= 0.25m)
            {
                reasons.Add("动作语义强");
            }

            if (anchor.Confidence >= 0.90m)
            {
                reasons.Add("候选置信度高");
            }

            return string.Join(" + ", reasons);
        }

        private static string BuildVirtualCoreReason(AnchorCandidate anchor)
        {
            if (anchor == null || !string.Equals(anchor.AnchorType, "VirtualAction", StringComparison.OrdinalIgnoreCase))
            {
                return "非虚拟动作输出";
            }

            var reasons = new List<string> { "虚拟动作命令" };
            if (!string.IsNullOrWhiteSpace(anchor.LogicSourceType))
            {
                reasons.Add(anchor.LogicSourceType);
            }

            if (!string.IsNullOrWhiteSpace(anchor.StationCode))
            {
                reasons.Add($"已归属工位 {anchor.StationCode}");
            }

            if (anchor.Confidence >= 0.82m)
            {
                reasons.Add("候选置信度高");
            }

            return string.Join(" + ", reasons);
        }

        private static bool IsLikelyVirtualVariable(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName))
            {
                return false;
            }

            if (ContainsAny(variableName, StatusKeywords))
            {
                return false;
            }

            return ContainsAny(variableName, VirtualKeywords) ||
                   ContainsAny(variableName, ActionKeywords) ||
                   variableName.Contains(".Cmd", StringComparison.OrdinalIgnoreCase) ||
                   variableName.Contains("Command", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyStatusLikeVirtualCandidate(
            string variableName,
            string mergedText,
            string logicSourceType,
            int writeCount,
            int readCount)
        {
            if (!ContainsAny(mergedText, StatusKeywords))
            {
                return false;
            }

            var commandLike = ContainsAny(mergedText, VirtualKeywords) ||
                              ContainsAny(mergedText, ActionKeywords) ||
                              (!string.IsNullOrWhiteSpace(variableName) &&
                               (variableName.Contains(".Cmd", StringComparison.OrdinalIgnoreCase) ||
                                variableName.Contains("Command", StringComparison.OrdinalIgnoreCase)));

            if (commandLike && (string.Equals(logicSourceType, "GraphAction", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(logicSourceType, "LadCoil", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return readCount >= writeCount || string.Equals(logicSourceType, "VariableWrite", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReadAccess(PLC_VariableAccessEntity entity)
        {
            return string.Equals(entity.AccessType, "READ", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entity.AccessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }

        private async Task EnrichVirtualCandidateStationsAsync(List<VirtualCandidateContext> contexts)
        {
            var pending = contexts
                .Where(NeedsLlmStationInference)
                .ToList();

            if (pending.Count == 0)
            {
                return;
            }

            var llmResults = await _llmAnnotator.InferStationsForVirtualCandidatesAsync(pending);
            foreach (var context in pending)
            {
                if (!llmResults.TryGetValue(context.VariableName, out var llmInference))
                {
                    continue;
                }

                if (!ShouldApplyLlmStationInference(context, llmInference))
                {
                    continue;
                }

                context.StationCode = llmInference.StationCode;
                context.StationConfidence = llmInference.Confidence;
                context.StationEvidence = llmInference.Evidence;
            }
        }

        private static bool NeedsLlmStationInference(VirtualCandidateContext context)
        {
            return context != null &&
                   (string.IsNullOrWhiteSpace(context.StationCode) || context.StationConfidence < 0.40m);
        }

        private static bool ShouldApplyLlmStationInference(VirtualCandidateContext context, StationInferenceResult llmInference)
        {
            if (context == null || llmInference == null || string.IsNullOrWhiteSpace(llmInference.StationCode))
            {
                return false;
            }

            if (llmInference.Confidence < 0.58m)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(context.StationCode))
            {
                return true;
            }

            if (string.Equals(context.StationCode, llmInference.StationCode, StringComparison.OrdinalIgnoreCase))
            {
                return llmInference.Confidence > context.StationConfidence;
            }

            return llmInference.Confidence >= context.StationConfidence + 0.18m;
        }

        private static bool IsWriteAccess(PLC_VariableAccessEntity entity)
        {
            return string.Equals(entity.AccessType, "WRITE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entity.AccessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }

        private static decimal CalculateVirtualConfidence(int writeCount, int readCount, string variableName)
        {
            var confidence = 0.70m;
            if (writeCount >= 2)
            {
                confidence += 0.10m;
            }

            if (readCount == 0)
            {
                confidence += 0.10m;
            }

            if (ContainsAny(variableName, VirtualKeywords))
            {
                confidence += 0.10m;
            }

            return Math.Min(confidence, 0.96m);
        }

        private static AnchorScoreBreakdown BuildPhysicalScoreBreakdown(bool isAuxiliary, string combinedText)
        {
            return new AnchorScoreBreakdown
            {
                NameScore = ContainsAny(combinedText, ActionKeywords) ? 0.30m : 0.18m,
                WriteScore = 0.20m,
                SourceScore = isAuxiliary ? 0.08m : 0.22m,
                FlowScore = 0.08m,
                StationScore = 0.08m,
                ExposureScore = 0.05m
            };
        }

        private static AnchorScoreBreakdown BuildVirtualScoreBreakdown(
            string variableName,
            string mergedText,
            int writeCount,
            int readCount,
            string stationCode,
            string logicSourceType)
        {
            var nameScore = ContainsAny(mergedText, VirtualKeywords) ? 0.20m : 0.08m;
            var writeScore = readCount == 0 ? 0.20m : (writeCount > readCount ? 0.14m : 0.05m);
            var sourceScore = logicSourceType == "GraphAction" ? 0.20m : logicSourceType == "LadCoil" ? 0.16m : 0.08m;
            var flowScore = writeCount >= 2 ? 0.14m : 0.08m;
            var stationScore = string.IsNullOrWhiteSpace(stationCode) ? 0.04m : 0.10m;
            var exposureScore = variableName.Contains("DB", StringComparison.OrdinalIgnoreCase) ? 0.08m : 0.04m;

            return new AnchorScoreBreakdown
            {
                NameScore = nameScore,
                WriteScore = writeScore,
                SourceScore = sourceScore,
                FlowScore = flowScore,
                StationScore = stationScore,
                ExposureScore = exposureScore
            };
        }

        private static AnchorScoreBreakdown BuildHandshakeScoreBreakdown(int readCount, int writeCount, string text, string stationCode)
        {
            return new AnchorScoreBreakdown
            {
                NameScore = ContainsAny(text, HandshakeKeywords) ? 0.20m : 0.10m,
                WriteScore = readCount >= 3 ? 0.18m : 0.10m,
                SourceScore = 0.18m,
                FlowScore = writeCount >= 1 ? 0.12m : 0.08m,
                StationScore = string.IsNullOrWhiteSpace(stationCode) ? 0.04m : 0.08m,
                ExposureScore = 0.10m
            };
        }

        private static string ResolveVirtualLogicSourceType(IEnumerable<PLC_VariableAccessEntity> accesses)
        {
            var mergedText = CombineText(
                accesses.Select(it => it.Instruction).ToArray());

            if (ContainsAny(mergedText, new[] { "GRAPH", "STEP_ACTION", "STEP_", "TRANSITION" }))
            {
                return "GraphAction";
            }

            if (ContainsAny(mergedText, new[] { "COIL", "OUT", "SET", "RESET", "S", "R" }))
            {
                return "LadCoil";
            }

            return "VariableWrite";
        }

        private static decimal CalculateHandshakeConfidence(int readCount, int writeCount, string text)
        {
            var confidence = 0.68m;
            if (readCount >= 3)
            {
                confidence += 0.10m;
            }

            if (writeCount >= 1)
            {
                confidence += 0.08m;
            }

            if (ContainsAny(text, HandshakeKeywords))
            {
                confidence += 0.10m;
            }

            return Math.Min(confidence, 0.95m);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(it => !string.IsNullOrWhiteSpace(it)) ?? string.Empty;
        }

        private static string CombineText(params string[] values)
        {
            return string.Join(" ", values.Where(it => !string.IsNullOrWhiteSpace(it)));
        }

        private static bool ContainsAny(string value, IEnumerable<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return keywords.Any(keyword => value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private sealed class NonPhysicalDiscoveryResult
        {
            public List<AnchorCandidate> VirtualAnchors { get; set; } = new List<AnchorCandidate>();

            public List<AnchorCandidate> HandshakeAnchors { get; set; } = new List<AnchorCandidate>();

            public List<AnchorCandidate> ReviewQueue { get; set; } = new List<AnchorCandidate>();

            public LlmDiscoveryDiagnostics LlmDiagnostics { get; set; } = new LlmDiscoveryDiagnostics();
        }
    }
}
