using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.AppLayer.Hubs;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using SqlSugar;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis
{
    /// <summary>
    /// Q点停机推导独立模块入口
    /// 当前实现第一阶段：锚点发现和 AST 构建种子提取。
    /// </summary>
    public class StopCauseDiagnosisApp : ISelfScopedAutoInject
    {
        private readonly StationExtractionService _stationExtractionService;
        private readonly StationPlcScopeService _stationPlcScopeService;
        private readonly IPLC_StationBlockBindingService _stationBlockBindingService;
        private readonly ValveAlarmDiagnosisService _valveAlarmDiagnosisService;
        private readonly StationBackfillService _stationBackfillService;
        private readonly QPointAnchorDiscoveryService _anchorDiscoveryService;
        private readonly StopCausePhaseOneCompilerService _phaseOneCompilerService;
        private readonly StopCauseAstCompilerService _astCompilerService;
        private readonly AstSemanticInferenceService _astSemanticInferenceService;
        private readonly AstPersistenceService _astPersistenceService;
        private readonly AstSemanticProgressService _astSemanticProgressService;
        private readonly StationMapBuilderService _stationMapBuilderService;
        private readonly VariablePropagationBuilderService _variablePropagationBuilderService;
        private readonly StationColoringService _stationColoringService;
        private readonly StationMapPersistenceService _stationMapPersistenceService;
        private readonly IPLC_StationAnchorBindingService _stationAnchorBindingService;
        private readonly StopCauseSignalSnapshotService _signalSnapshotService;
        private readonly StopCauseAstRuntimeEvaluationService _runtimeEvaluationService;
        private readonly StationShadowStateCacheService _stationShadowStateCacheService;
        private readonly IPLC_StationShadowConfigService _stationShadowConfigService;
        private readonly IPLC_StationShadowEventService _stationShadowEventService;
        private readonly IPLC_StationShadowSignalService _stationShadowSignalService;
        private readonly IPLC_StationSignalHistoryService _stationSignalHistoryService;
        private readonly IPLC_StationPatternTemplateService _stationPatternTemplateService;
        private readonly IPLC_LogicUnitAstService _logicUnitAstService;
        private readonly StationShadowRuntimeService _stationShadowRuntimeService;
        private readonly AstLlmFallbackService _astLlmFallbackService;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_BlockInterfaceService _blockInterfaceService;
        private readonly FullAnalysisProgressService _fullAnalysisProgressService;
        private readonly LineDiagnosisLlmService _lineDiagnosisLlmService;
        private readonly DiagnosisAgentService _diagnosisAgentService;
        private readonly IPLC_ProjectService _projectService;
        private readonly StationShadowMonitoringBroadcastService _broadcastService;
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_LineDiagnosisRecordService _lineDiagnosisRecordService;
        private readonly IPointReverseIndexService _iPointReverseIndexService;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_DbFbBindingService _dbFbBindingService;
        private readonly ExecutionSuccessorChainService _executionSuccessorChainService;
        private readonly DiagnosticStrategyFactoryApp _diagnosticStrategyFactoryApp;
        private readonly GraphActivationRuleInferService _graphActivationRuleInferService;
        private readonly DeviceHealthSemanticClassifierService _deviceHealthSemanticClassifierService;
        private readonly FbLocalVariableConcretizer _fbLocalVariableConcretizer;
        private const string DeviceFocusGroupName = "设备关注信号";
        private const string GraphActionAnchorType = "GraphAction";
        private const string GraphExecBitAnchorType = "GraphExecBit";
        private const string GraphExecBitGroupName = "GRAPH执行位候补";
        private const int GraphExecBitMaxCount = 250;
        private const int DeviceFocusMaxFbPenetrationDepth = 1;
        private static readonly HashSet<string> DeviceFocusAnchorTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DeviceAlarmState",
            "DeviceHoldState",
            "DevicePermitState",
            "DeviceMotionFeedback",
            "DeviceAnalogContext"
        };
        private static readonly HashSet<string> DeviceFocusExpandedDefaultExcludedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DeviceAlarmState",
            "DeviceAnalogContext"
        };

        public StopCauseDiagnosisApp(
            StationExtractionService stationExtractionService,
            StationBackfillService stationBackfillService,
            QPointAnchorDiscoveryService anchorDiscoveryService,
            StopCausePhaseOneCompilerService phaseOneCompilerService,
            StopCauseAstCompilerService astCompilerService,
            AstSemanticInferenceService astSemanticInferenceService,
            AstPersistenceService astPersistenceService,
            AstSemanticProgressService astSemanticProgressService,
            StationMapBuilderService stationMapBuilderService,
            VariablePropagationBuilderService variablePropagationBuilderService,
            StationColoringService stationColoringService,
            StationMapPersistenceService stationMapPersistenceService,
            IPLC_StationAnchorBindingService stationAnchorBindingService,
            StopCauseSignalSnapshotService signalSnapshotService,
            StopCauseAstRuntimeEvaluationService runtimeEvaluationService,
            StationShadowStateCacheService stationShadowStateCacheService,
            IPLC_StationShadowConfigService stationShadowConfigService,
            IPLC_StationShadowEventService stationShadowEventService,
            IPLC_StationShadowSignalService stationShadowSignalService,
            IPLC_StationSignalHistoryService stationSignalHistoryService,
            IPLC_StationPatternTemplateService stationPatternTemplateService,
            IPLC_LogicUnitAstService logicUnitAstService,
            StationShadowRuntimeService stationShadowRuntimeService,
            AstLlmFallbackService astLlmFallbackService,
            IPLC_BlockService blockService,
            IPLC_BlockInterfaceService blockInterfaceService,
            FullAnalysisProgressService fullAnalysisProgressService,
            LineDiagnosisLlmService lineDiagnosisLlmService,
            DiagnosisAgentService diagnosisAgentService,
            IPLC_ProjectService projectService,
            StationShadowMonitoringBroadcastService broadcastService,
            IPLC_VariableAccessService variableAccessService,
            IPLC_LineDiagnosisRecordService lineDiagnosisRecordService,
            IPointReverseIndexService iPointReverseIndexService,
            IPLC_IOTagService ioTagService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_DbFbBindingService dbFbBindingService,
            ExecutionSuccessorChainService executionSuccessorChainService,
            DiagnosticStrategyFactoryApp diagnosticStrategyFactoryApp,
            GraphActivationRuleInferService graphActivationRuleInferService,
            DeviceHealthSemanticClassifierService deviceHealthSemanticClassifierService,
            StationPlcScopeService stationPlcScopeService,
            FbLocalVariableConcretizer fbLocalVariableConcretizer,
            ValveAlarmDiagnosisService valveAlarmDiagnosisService,
            IPLC_StationBlockBindingService stationBlockBindingService)
        {
            _stationBlockBindingService = stationBlockBindingService;
            _stationPlcScopeService = stationPlcScopeService;
            _fbLocalVariableConcretizer = fbLocalVariableConcretizer;
            _valveAlarmDiagnosisService = valveAlarmDiagnosisService;
            _stationExtractionService = stationExtractionService;
            _stationBackfillService = stationBackfillService;
            _anchorDiscoveryService = anchorDiscoveryService;
            _phaseOneCompilerService = phaseOneCompilerService;
            _astCompilerService = astCompilerService;
            _astSemanticInferenceService = astSemanticInferenceService;
            _astPersistenceService = astPersistenceService;
            _astSemanticProgressService = astSemanticProgressService;
            _stationMapBuilderService = stationMapBuilderService;
            _variablePropagationBuilderService = variablePropagationBuilderService;
            _stationColoringService = stationColoringService;
            _stationMapPersistenceService = stationMapPersistenceService;
            _stationAnchorBindingService = stationAnchorBindingService;
            _signalSnapshotService = signalSnapshotService;
            _runtimeEvaluationService = runtimeEvaluationService;
            _stationShadowStateCacheService = stationShadowStateCacheService;
            _stationShadowConfigService = stationShadowConfigService;
            _stationShadowEventService = stationShadowEventService;
            _stationShadowSignalService = stationShadowSignalService;
            _stationSignalHistoryService = stationSignalHistoryService;
            _stationPatternTemplateService = stationPatternTemplateService;
            _logicUnitAstService = logicUnitAstService;
            _stationShadowRuntimeService = stationShadowRuntimeService;
            _astLlmFallbackService = astLlmFallbackService;
            _blockService = blockService;
            _blockInterfaceService = blockInterfaceService;
            _fullAnalysisProgressService = fullAnalysisProgressService;
            _lineDiagnosisLlmService = lineDiagnosisLlmService;
            _diagnosisAgentService = diagnosisAgentService;
            _projectService = projectService;
            _broadcastService = broadcastService;
            _variableAccessService = variableAccessService;
            _lineDiagnosisRecordService = lineDiagnosisRecordService;
            _iPointReverseIndexService = iPointReverseIndexService;
            _ioTagService = ioTagService;
            _logicUnitService = logicUnitService;
            _dbFbBindingService = dbFbBindingService;
            _executionSuccessorChainService = executionSuccessorChainService;
            _diagnosticStrategyFactoryApp = diagnosticStrategyFactoryApp;
            _graphActivationRuleInferService = graphActivationRuleInferService;
            _deviceHealthSemanticClassifierService = deviceHealthSemanticClassifierService;
        }

        /// <summary>
        /// 独立执行工位提取与标记。
        /// 适合作为导入解析完成后的前置阶段，再进入 Q 点识别、AST 建模和监控阶段。
        /// </summary>
        public async Task<HttpResponseResultModel<StationExtractionResultDto>> ExtractStationsAsync(StationExtractionRequestDto request)
        {
            var response = new HttpResponseResultModel<StationExtractionResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }

            var dto = await _stationExtractionService.BuildAsync(request);

            // 工位白名单过滤（2026-08-26）：对齐工位基础信息（Basic_SubStation.PlcName 绑定 PLC1/PLC2），
            // 项目 plccode 之外的工位线索（跨 PLC 通信变量带入）一律清空，防止 PLC1 的块被染到 PLC2 的工位（OP080~OP100）
            var allowedStations = await _stationPlcScopeService.GetAllowedStationsAsync(request.ProjectId);
            if (allowedStations != null)
            {
                foreach (var item in dto.PhysicalTagItems)
                {
                    if (!StationPlcScopeService.IsAllowed(item.StationCode, allowedStations)) item.StationCode = null;
                }
                foreach (var item in dto.VariableGroupItems)
                {
                    if (!StationPlcScopeService.IsAllowed(item.StationCode, allowedStations)) item.StationCode = null;
                }
                dto.StationBuckets.RemoveAll(b => !StationPlcScopeService.IsAllowed(b.StationCode, allowedStations));
            }

            if (request.IncludeLogicUnitMarking)
            {
                dto.StationColoringResult = await _stationColoringService.ColorAsync(
                    request.ProjectId,
                    BuildAssignmentsFromStationItems(dto));
            }

            if (request.PersistStationMarking || request.UpdateBlockStationCode)
            {
                dto.BackfillSummary = await _stationBackfillService.ApplyAsync(
                    request.ProjectId,
                    dto,
                    request.PersistStationMarking,
                    request.UpdateBlockStationCode);
            }

            // 站码回填后自动增量补播 GRAPH EXEC_BITS 候补信号：
            // 回填前 block.stationcode 为空、按工位查不到块 —— 此处是播种最早可行的时机。
            // 幂等（SignalKey/VariableName 双去重，ReplaceExisting=false），只补增量、不触碰
            // 已有锚点信号与监控历史；锚点信号仍由一键分析末尾的自动重播或手动播种负责。
            // 覆盖两个入口：手动 extract-stations 与一键分析（其 Step 1 内部也调用本方法）。
            if (request.UpdateBlockStationCode)
            {
                var seededStations = new List<string>();
                try
                {
                    var graphStations = (await _blockService.GetListAsync(block =>
                            block.IsDeleted == false
                            && block.ProjectId == request.ProjectId
                            && !string.IsNullOrWhiteSpace(block.StationCode)))
                        .Select(block => block.StationCode.Trim())
                        .Where(code => string.IsNullOrWhiteSpace(request.StationCode)
                            || string.Equals(code, request.StationCode.Trim(), StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var station in graphStations)
                    {
                        var seedResult = await SeedMonitoringSignalsAsync(new StationShadowSeedSignalsRequestDto
                        {
                            ProjectId = request.ProjectId,
                            StationCode = station,
                            ReplaceExisting = false
                        });
                        if (seedResult?.BackResult?.InsertedCount > 0)
                        {
                            seededStations.Add($"{station}:{seedResult.BackResult.InsertedCount}");
                        }
                    }
                }
                catch (Exception seedEx)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[ExtractStations] {request.ProjectId} EXEC_BITS 自动补播异常（不影响工位提取结果）: {seedEx.Message}",
                        "StopCauseDiagnosis/ExtractStations");
                }

                if (seededStations.Count > 0)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[ExtractStations] {request.ProjectId} 站码回填后自动补播 EXEC_BITS 候补: {string.Join(", ", seededStations)}",
                        "StopCauseDiagnosis/ExtractStations");
                }
            }

            dto.Summary = BuildStationExtractionSummary(dto);

            if (!request.IncludeSourceItems)
            {
                dto.PhysicalTagItems.Clear();
                dto.VariableGroupItems.Clear();
            }
            else
            {
                dto.PhysicalTagItems = dto.PhysicalTagItems.Take(request.MaxDetailCountPerCategory).ToList();
                dto.VariableGroupItems = dto.VariableGroupItems.Take(request.MaxDetailCountPerCategory).ToList();
            }

            response.BackResult = dto;
            response.IsSuccess = true;
            return response;
        }

        public async Task<HttpResponseResultModel<DeviceHealthPreExtractResultDto>> PreExtractDeviceHealthAnchorsAsync(
            DeviceHealthPreExtractRequestDto request)
        {
            var response = new HttpResponseResultModel<DeviceHealthPreExtractResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId cannot be empty";
                return response;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var stationCode = request.StationCode?.Trim();
            var result = new DeviceHealthPreExtractResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = stationCode,
                BlockId = request.BlockId
            };

            var astRows = await _logicUnitAstService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == request.ProjectId
                && (string.IsNullOrEmpty(stationCode) || it.StationCode == stationCode)
                && (request.BlockId == null || it.BlockId == request.BlockId)
                && (it.CompileStatus == AstCompileStatus.Compiled || it.CompileStatus == AstCompileStatus.PartiallyCompiled));

            astRows = astRows
                .Where(RuntimeAnchorSelectionPolicy.IsRuntimeEligibleAst)
                .Where(it => !IsGraphStepAction(it.UnitType))
                .Where(it => string.IsNullOrWhiteSpace(it.UnitType)
                    || it.UnitType.IndexOf("TRANSITION", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();

            result.ScannedAstRowCount = astRows.Count;
            if (astRows.Count == 0)
            {
                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.Warnings.Add("No compiled AST rows found for the requested scope.");
                response.IsSuccess = true;
                response.BackResult = result;
                return response;
            }

            var logicUnitIds = astRows
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            var writeAccesses = logicUnitIds.Count == 0
                ? new List<PLC_VariableAccessEntity>()
                : await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == request.ProjectId
                    && it.LogicUnitId.HasValue
                    && logicUnitIds.Contains(it.LogicUnitId.Value)
                    && !string.IsNullOrEmpty(it.VariableName)
                    && (it.AccessType == "WRITE" || it.AccessType == "READWRITE"));

            var accessByLogicAndVariable = writeAccesses
                .GroupBy(it => $"{it.LogicUnitId}:{it.VariableName?.Trim()}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var ioTags = await _ioTagService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == request.ProjectId
                && !string.IsNullOrEmpty(it.TagName));

            var ioTagByName = ioTags
                .GroupBy(it => it.TagName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var existingSignals = await _stationShadowSignalService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == request.ProjectId
                && (string.IsNullOrEmpty(stationCode) || it.StationCode == stationCode));

            var existingBindings = await _stationAnchorBindingService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == request.ProjectId
                && (string.IsNullOrEmpty(stationCode) || it.StationCode == stationCode));

            var existingSignalVars = existingSignals
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .Select(it => BuildStationVariableKey(it.StationCode, it.VariableName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var existingBindingVars = existingBindings
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .Select(it => BuildStationVariableKey(it.StationCode, it.VariableName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = new List<DeviceHealthPreExtractCandidateDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ast in astRows)
            {
                var outputs = DeserializeStringListSafe(ast.OutputVariablesJson)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Select(it => it.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var inputs = DeserializeStringListSafe(ast.InputVariablesJson)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Select(it => it.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(30)
                    .ToList();

                result.ScannedOutputCount += outputs.Count;

                foreach (var output in outputs)
                {
                    if (!IsDeviceHealthReadableVariable(output))
                    {
                        continue;
                    }

                    if (ioTagByName.TryGetValue(output, out var tag)
                        && !string.IsNullOrWhiteSpace(tag.Address)
                        && tag.Address.Contains("%Q", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsDeviceMessageVariable(output) && !request.IncludeMessageSignals)
                    {
                        continue;
                    }

                    var classification = ClassifyDeviceHealthOutput(output, ast.BlockName, ast.Title, inputs);
                    if (classification == null)
                    {
                        if (!request.UseLlmSemanticClassification)
                        {
                            continue;
                        }

                        classification = BuildDeviceHealthReviewClassification(output);
                    }

                    if (!request.IncludeFeedbackSignals
                        && string.Equals(classification.AnchorType, "DeviceMotionFeedback", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!request.IncludeAnalogContextSignals
                        && string.Equals(classification.AnchorType, "DeviceAnalogContext", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    accessByLogicAndVariable.TryGetValue($"{ast.LogicUnitId}:{output}", out var access);
                    var scope = access?.VariableScope;
                    var monitorability = ResolveDeviceHealthMonitorability(output, scope);
                    var isLocalOnly = string.Equals(monitorability, "LocalOnly", StringComparison.OrdinalIgnoreCase);
                    if (isLocalOnly && !request.IncludeLocalVariables)
                    {
                        continue;
                    }

                    var resolvedStation = FirstNonEmpty(
                        ast.StationCode,
                        stationCode,
                        ExtractStationCodeFromText(output),
                        ExtractStationCodeFromText(ast.BlockName));

                    var score = classification.Score;
                    if (!string.IsNullOrWhiteSpace(resolvedStation)) score += 5;
                    if (!string.IsNullOrWhiteSpace(ast.BlockName) && ContainsAny(ast.BlockName, DeviceContextKeywords)) score += 5;
                    if (string.Equals(scope, "GlobalVariable", StringComparison.OrdinalIgnoreCase)) score += 8;
                    if (isLocalOnly) score -= 20;
                    if (existingSignalVars.Contains(BuildStationVariableKey(resolvedStation, output))) score -= 10;
                    if (score < Math.Max(0, request.MinScore) && !request.UseLlmSemanticClassification)
                    {
                        continue;
                    }

                    var dedupKey = $"{resolvedStation}:{output}:{ast.LogicUnitId}";
                    if (!seen.Add(dedupKey))
                    {
                        continue;
                    }

                    var alreadyInMonitoring = existingSignalVars.Contains(BuildStationVariableKey(resolvedStation, output));
                    var alreadyInBinding = existingBindingVars.Contains(BuildStationVariableKey(resolvedStation, output));
                    var recommended = !alreadyInMonitoring
                                      && !isLocalOnly
                                      && !string.Equals(classification.AnchorType, "DeviceAnalogContext", StringComparison.OrdinalIgnoreCase)
                                      && !string.Equals(classification.AnchorType, "DeviceMessageContext", StringComparison.OrdinalIgnoreCase)
                                      && !string.Equals(classification.AnchorType, "InternalDerivedState", StringComparison.OrdinalIgnoreCase)
                                      && !string.Equals(classification.AnchorType, "Ignore", StringComparison.OrdinalIgnoreCase)
                                      && !string.Equals(classification.AnchorType, "Unknown", StringComparison.OrdinalIgnoreCase);

                    candidates.Add(new DeviceHealthPreExtractCandidateDto
                    {
                        VariableName = output,
                        StationCode = resolvedStation,
                        AnchorType = classification.AnchorType,
                        SemanticCategory = classification.SemanticCategory,
                        SemanticLabel = classification.SemanticLabel,
                        ActivationRule = classification.ActivationRule,
                        AbnormalWhen = classification.AbnormalWhen,
                        MonitoringRole = classification.MonitoringRole,
                        RootPriority = classification.RootPriority,
                        SemanticSource = classification.SemanticSource,
                        SourceHash = ast.SourceHash,
                        Score = score,
                        Confidence = Math.Min(0.99m, Math.Max(0.50m, score / 100m)),
                        RecommendedForMonitoring = recommended,
                        Monitorability = monitorability,
                        AlreadyInMonitoring = alreadyInMonitoring,
                        AlreadyInAnchorBinding = alreadyInBinding,
                        LogicUnitId = ast.LogicUnitId,
                        BlockId = ast.BlockId,
                        BlockName = ast.BlockName,
                        NetworkId = ast.NetworkId,
                        NetworkTitle = ast.Title,
                        UnitType = ast.UnitType,
                        VariableScope = scope,
                        InputVariables = inputs,
                        OutputVariables = outputs,
                        Evidence = new List<string>
                        {
                            classification.Reason,
                            $"AST: {ast.BlockName}/LU={ast.LogicUnitId}/Network={ast.NetworkId}",
                            string.IsNullOrWhiteSpace(scope) ? "VariableScope: unknown" : $"VariableScope: {scope}",
                            recommended ? "Recommended for monitoring seed." : "Preview only or already covered."
                        }
                    });
                }
            }

            if (request.UseLlmSemanticClassification && _deviceHealthSemanticClassifierService != null)
            {
                result.LlmSemanticClassificationUsed = true;
                var semanticResult = await _deviceHealthSemanticClassifierService.EnrichAsync(request, candidates);
                result.SemanticClassifierVersion = semanticResult.ClassifierVersion;
                result.LlmCacheHitCount = semanticResult.CacheHitCount;
                result.LlmSentCount = semanticResult.SentCount;
                result.LlmReturnedCount = semanticResult.ReturnedCount;
                result.LlmPersistedCount = semanticResult.PersistedCount;
                result.LlmLimitSkippedCount = semanticResult.LimitSkippedCount;
                result.LlmElapsedMs = semanticResult.ElapsedMs;
                result.LlmErrorMessage = semanticResult.ErrorMessage;
                if (semanticResult.LimitSkippedCount > 0)
                {
                    result.Warnings.Add($"LLM semantic classification limit reached; {semanticResult.LimitSkippedCount} candidates were not sent. Set maxLlmCandidates to 0 for full initialization within the service safety cap.");
                }

                if (!string.IsNullOrWhiteSpace(semanticResult.ErrorMessage))
                {
                    result.Warnings.Add($"LLM semantic classification failed or skipped: {semanticResult.ErrorMessage}");
                }
            }

            var finalCandidates = candidates.AsEnumerable();
            if (!request.IncludeLlmRejectedCandidates)
            {
                finalCandidates = finalCandidates.Where(it => !IsRejectedDeviceHealthCandidate(it));
            }

            finalCandidates = finalCandidates
                .Where(it => it.Score >= Math.Max(0, request.MinScore));

            var orderedCandidates = finalCandidates
                .OrderByDescending(it => it.RecommendedForMonitoring)
                .ThenByDescending(it => it.Score)
                .ThenBy(it => it.StationCode)
                .ThenBy(it => it.VariableName)
                .ToList();
            var outputLimit = ResolveDeviceHealthCandidateLimit(request.MaxCandidates, 300, 5000);
            if (orderedCandidates.Count > outputLimit)
            {
                result.Warnings.Add($"Device health candidate output limit reached; returned {outputLimit} of {orderedCandidates.Count} candidates. Set maxCandidates to 0 for full initialization within the service safety cap.");
            }

            result.Candidates = orderedCandidates
                .Take(outputLimit)
                .ToList();
            result.CandidateCount = result.Candidates.Count;
            result.ExistingMonitoringCount = result.Candidates.Count(it => it.AlreadyInMonitoring);
            result.ExistingAnchorBindingCount = result.Candidates.Count(it => it.AlreadyInAnchorBinding);
            result.CategoryCounts = result.Candidates
                .GroupBy(it => it.AnchorType ?? "Unknown", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            result.StationCounts = result.Candidates
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode))
                .GroupBy(it => it.StationCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            result.ElapsedMs = sw.ElapsedMilliseconds;

            response.IsSuccess = true;
            response.BackResult = result;
            return response;
        }

        public async Task<HttpResponseResultModel<DeviceFocusSignalsResultDto>> GetDeviceFocusSignalsAsync(
            DeviceFocusSignalQueryDto request)
        {
            var response = new HttpResponseResultModel<DeviceFocusSignalsResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId cannot be empty";
                return response;
            }

            var stationCode = request.StationCode?.Trim();
            var query = _stationShadowSignalService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId)
                .Where(it =>
                    it.GroupName == DeviceFocusGroupName
                    || it.AnchorType == "DeviceAlarmState"
                    || it.AnchorType == "DeviceHoldState"
                    || it.AnchorType == "DevicePermitState"
                    || it.AnchorType == "DeviceMotionFeedback"
                    || it.AnchorType == "DeviceAnalogContext");

            if (!string.IsNullOrWhiteSpace(stationCode))
            {
                query = query.Where(it => it.StationCode == stationCode);
            }

            if (!string.IsNullOrWhiteSpace(request.AnchorType))
            {
                var anchorType = request.AnchorType.Trim();
                query = query.Where(it => it.AnchorType == anchorType);
            }

            var entities = await query
                .OrderBy(it => it.StationCode, OrderByType.Asc)
                .OrderBy(it => it.Priority, OrderByType.Desc)
                .OrderBy(it => it.SortOrder, OrderByType.Asc)
                .ToListAsync();

            if (!string.IsNullOrWhiteSpace(request.Keyword))
            {
                var keyword = request.Keyword.Trim();
                entities = entities
                    .Where(it =>
                        ContainsIgnoreCase(it.VariableName, keyword)
                        || ContainsIgnoreCase(it.SignalKey, keyword)
                        || ContainsIgnoreCase(it.SemanticLabel, keyword)
                        || ContainsIgnoreCase(it.SemanticCategory, keyword)
                        || ContainsIgnoreCase(it.Remark, keyword))
                    .ToList();
            }

            var limit = ResolveDeviceHealthCandidateLimit(request.Limit, 2000, 5000);
            var signals = entities
                .Take(limit)
                .Select(MapDeviceFocusSignal)
                .ToList();

            var result = new DeviceFocusSignalsResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = stationCode,
                TotalCount = signals.Count,
                StationCount = signals
                    .Where(it => !string.IsNullOrWhiteSpace(it.StationCode))
                    .Select(it => it.StationCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                Signals = signals,
                CategoryCounts = signals
                    .GroupBy(it => it.AnchorType ?? "Unknown", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
                Stations = signals
                    .GroupBy(it => string.IsNullOrWhiteSpace(it.StationCode) ? "未识别工位" : it.StationCode.Trim(), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key)
                    .Select(g => new DeviceFocusStationGroupDto
                    {
                        StationCode = g.Key,
                        TotalCount = g.Count(),
                        CategoryCounts = g
                            .GroupBy(it => it.AnchorType ?? "Unknown", StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase),
                        Signals = g
                            .OrderByDescending(it => it.Priority ?? 0)
                            .ThenBy(it => it.SortOrder ?? 9999)
                            .ThenBy(it => it.VariableName)
                            .ToList()
                    })
                    .ToList()
            };

            response.IsSuccess = true;
            response.BackResult = result;
            return response;
        }

        public async Task<HttpResponseResultModel<DeviceFocusSignalSeedResultDto>> SeedDeviceFocusSignalsAsync(
            DeviceFocusSignalSeedRequestDto request)
        {
            var response = new HttpResponseResultModel<DeviceFocusSignalSeedResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId cannot be empty";
                return response;
            }

            request.MaxCandidates = 0;
            request.MinScore = Math.Max(0, request.MinScore);
            request.IncludeLocalVariables = true;
            request.IncludeFeedbackSignals = true;
            request.IncludeAnalogContextSignals = true;
            request.UseLlmSemanticClassification = true;
            request.UseSemanticCache = true;
            request.PersistSemanticCache = true;
            request.MaxLlmCandidates = request.MaxLlmCandidates == 0 ? 0 : request.MaxLlmCandidates;

            var preExtract = await PreExtractDeviceHealthAnchorsAsync(request);
            if (!preExtract.IsSuccess || preExtract.BackResult == null)
            {
                response.IsSuccess = false;
                response.ErrorMessage = preExtract.ErrorMessage ?? "设备关注信号分析失败";
                return response;
            }

            var stationCode = request.StationCode?.Trim();
            var result = new DeviceFocusSignalSeedResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = stationCode,
                AnalyzedCandidateCount = preExtract.BackResult.CandidateCount
            };
            result.Warnings.AddRange(preExtract.BackResult.Warnings ?? new List<string>());

            var candidates = preExtract.BackResult.Candidates
                .Where(IsSeedableDeviceFocusCandidate)
                .Where(it => !request.SeedRecommendedOnly || it.RecommendedForMonitoring)
                .OrderBy(it => it.StationCode)
                .ThenByDescending(it => it.RecommendedForMonitoring)
                .ThenByDescending(it => it.Score)
                .ThenBy(it => it.VariableName)
                .ToList();

            var candidateGroups = candidates
                .GroupBy(BuildDeviceFocusSignalKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var duplicateCandidateCount = candidateGroups.Sum(g => g.Count() - 1);
            if (duplicateCandidateCount > 0)
            {
                candidates = candidateGroups.Select(g => g.First()).ToList();
                result.DuplicateCandidateCount = duplicateCandidateCount;
                result.Warnings.Add($"已合并 {duplicateCandidateCount} 条重复设备关注信号候选，按 ProjectId + StationCode + SignalKey 保持唯一。");
            }

            result.SeedCandidateCount = candidates.Count;
            if (candidates.Count == 0)
            {
                response.IsSuccess = true;
                response.BackResult = result;
                result.Warnings.Add("没有可同步到工位监控配置的设备关注信号。");
                return response;
            }

            if (request.ReplaceExistingDeviceSignals)
            {
                List<PLC_StationShadowSignalEntity> deleteTargets;
                if (string.IsNullOrWhiteSpace(stationCode))
                {
                    deleteTargets = await _stationShadowSignalService.GetListAsync(it =>
                        it.IsDeleted == false
                        && it.ProjectId == request.ProjectId
                        && it.GroupName == DeviceFocusGroupName);
                    if (deleteTargets.Count > 0)
                    {
                        await _stationShadowSignalService.SoftDeleteAsync(it =>
                            it.IsDeleted == false
                            && it.ProjectId == request.ProjectId
                            && it.GroupName == DeviceFocusGroupName);
                    }
                }
                else
                {
                    deleteTargets = await _stationShadowSignalService.GetListAsync(it =>
                        it.IsDeleted == false
                        && it.ProjectId == request.ProjectId
                        && it.StationCode == stationCode
                        && it.GroupName == DeviceFocusGroupName);
                    if (deleteTargets.Count > 0)
                    {
                        await _stationShadowSignalService.SoftDeleteAsync(it =>
                            it.IsDeleted == false
                            && it.ProjectId == request.ProjectId
                            && it.StationCode == stationCode
                            && it.GroupName == DeviceFocusGroupName);
                    }
                }

                result.DeletedCount = deleteTargets.Count;
            }

            var existing = await _stationShadowSignalService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == request.ProjectId
                && !string.IsNullOrEmpty(it.SignalKey)
                && (string.IsNullOrEmpty(stationCode) || it.StationCode == stationCode));
            var existingByKey = existing
                .Where(it => !string.IsNullOrWhiteSpace(it.SignalKey))
                .GroupBy(it => it.SignalKey.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var now = DateTime.Now;
            var toInsert = new List<PLC_StationShadowSignalEntity>();
            var toUpdate = new List<PLC_StationShadowSignalEntity>();
            var pendingByKey = new Dictionary<string, (DeviceHealthPreExtractCandidateDto Candidate, int SortOrder)>(
                StringComparer.OrdinalIgnoreCase);
            var sortOrder = 0;
            foreach (var candidate in candidates)
            {
                sortOrder++;
                var signalKey = BuildDeviceFocusSignalKey(candidate);
                pendingByKey[signalKey] = (candidate, sortOrder);
                if (existingByKey.TryGetValue(signalKey, out var existingEntity))
                {
                    ApplyDeviceFocusSignal(existingEntity, candidate, signalKey, sortOrder, now, updateCreateTime: false);
                    toUpdate.Add(existingEntity);
                    continue;
                }

                var entity = new PLC_StationShadowSignalEntity();
                ApplyDeviceFocusSignal(entity, candidate, signalKey, sortOrder, now, updateCreateTime: true);
                entity.ProjectId = request.ProjectId;
                toInsert.Add(entity);
            }

            if (toInsert.Count > 0)
            {
                var insertKeys = toInsert
                    .Select(it => it.SignalKey?.Trim())
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var latestExisting = await _stationShadowSignalService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == request.ProjectId
                    && !string.IsNullOrEmpty(it.SignalKey)
                    && insertKeys.Contains(it.SignalKey)
                    && (string.IsNullOrEmpty(stationCode) || it.StationCode == stationCode));
                if (latestExisting.Count > 0)
                {
                    var recoveredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var existingEntity in latestExisting
                                 .Where(it => !string.IsNullOrWhiteSpace(it.SignalKey))
                                 .GroupBy(it => it.SignalKey.Trim(), StringComparer.OrdinalIgnoreCase)
                                 .Select(g => g.First()))
                    {
                        var signalKey = existingEntity.SignalKey.Trim();
                        if (!pendingByKey.TryGetValue(signalKey, out var pending))
                        {
                            continue;
                        }

                        ApplyDeviceFocusSignal(
                            existingEntity,
                            pending.Candidate,
                            signalKey,
                            pending.SortOrder,
                            now,
                            updateCreateTime: false);
                        toUpdate.Add(existingEntity);
                        recoveredKeys.Add(signalKey);
                    }

                    if (recoveredKeys.Count > 0)
                    {
                        toInsert.RemoveAll(it => recoveredKeys.Contains(it.SignalKey?.Trim() ?? string.Empty));
                        result.Warnings.Add($"刷新写入前发现 {recoveredKeys.Count} 条信号已存在，已自动改为更新，避免重复插入。");
                    }
                }
            }

            if (toInsert.Count > 0)
            {
                await _stationShadowSignalService.BulkCopyAsync(toInsert);
            }

            if (toUpdate.Count > 0)
            {
                await _stationShadowSignalService.BulkUpdateAsync(toUpdate);
            }

            result.InsertedCount = toInsert.Count;
            result.UpdatedCount = toUpdate.Count;
            result.StationCounts = candidates
                .GroupBy(it => it.StationCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => string.IsNullOrWhiteSpace(g.Key) ? "未识别工位" : g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            response.IsSuccess = true;
            response.BackResult = result;
            return response;
        }

        private sealed class DeviceHealthClassification
        {
            public string AnchorType { get; set; }
            public string SemanticCategory { get; set; }
            public string SemanticLabel { get; set; }
            public string ActivationRule { get; set; }
            public string AbnormalWhen { get; set; }
            public string MonitoringRole { get; set; }
            public int? RootPriority { get; set; }
            public string SemanticSource { get; set; } = "Rule";
            public int Score { get; set; }
            public string Reason { get; set; }
        }

        private static readonly string[] DeviceAlarmKeywords =
        {
            "alarm", "warning", "warn", "fault", "error", "err", "alm", "flt", "trip", "abnormal"
        };

        private static readonly string[] DeviceHoldKeywords =
        {
            "hold", "interlock", "inhibit", "blocked", "block", "stop", "stopped", "disable", "disabled"
        };

        private static readonly string[] DevicePermitKeywords =
        {
            "ready", "permit", "permission", "allow", "enable", "enabled", "ok", "available"
        };

        private static readonly string[] DeviceFeedbackKeywords =
        {
            "done", "complete", "completed", "finish", "finished", "inposition", "in_pos", "inpos",
            "positionok", "workpositionok", "positonok", "workpositonok", "taskdone", "home", "homed", "in_home"
        };

        private static readonly string[] DeviceAnalogKeywords =
        {
            "position", "actual", "speed", "velocity", "torque", "current", "force", "pressure"
        };

        private static readonly string[] DeviceContextKeywords =
        {
            "axis", "servo", "motor", "drive", "robot", "cam", "sync", "vision", "camera", "barcode", "scanner", "agv"
        };

        private static DeviceHealthClassification ClassifyDeviceHealthOutput(
            string variableName,
            string blockName,
            string title,
            IReadOnlyCollection<string> inputVariables)
        {
            var variableText = variableName ?? string.Empty;
            var leafText = GetVariableLeaf(variableText);
            var contextText = $"{blockName} {title}";
            var text = $"{leafText} {contextText}".ToLowerInvariant();

            if (IsDeviceMessageVariable(variableText))
            {
                return new DeviceHealthClassification
                {
                    AnchorType = "DeviceMessageContext",
                    SemanticCategory = "MessageContext",
                    SemanticLabel = "设备消息/提示上下文",
                    ActivationRule = "UNKNOWN",
                    AbnormalWhen = "ContextOnly",
                    MonitoringRole = "Context",
                    RootPriority = 20,
                    Score = 30,
                    Reason = "Output is a display/message bit; keep as context instead of root-cause monitoring seed."
                };
            }

            if (IsAlarmLikeDeviceHealthVariable(variableText, leafText))
            {
                return new DeviceHealthClassification
                {
                    AnchorType = "DeviceAlarmState",
                    SemanticCategory = "AlarmFaultWarning",
                    SemanticLabel = "设备报警/故障状态",
                    ActivationRule = "BOOL_TRUE",
                    AbnormalWhen = "True",
                    MonitoringRole = "RootCause",
                    RootPriority = 90,
                    Score = 92,
                    Reason = "Output leaf/path matches alarm/fault/warning semantics."
                };
            }

            if (ContainsAny(text, DeviceHoldKeywords))
            {
                return new DeviceHealthClassification
                {
                    AnchorType = "DeviceHoldState",
                    SemanticCategory = "HoldInterlock",
                    SemanticLabel = "设备保持/互锁状态",
                    ActivationRule = "BOOL_TRUE",
                    AbnormalWhen = "True",
                    MonitoringRole = "RootCause",
                    RootPriority = 82,
                    Score = 82,
                    Reason = "Output name or block context matches hold/interlock/stop semantics."
                };
            }

            if (ContainsAny(text, DeviceFeedbackKeywords))
            {
                return new DeviceHealthClassification
                {
                    AnchorType = "DeviceMotionFeedback",
                    SemanticCategory = "MotionFeedback",
                    SemanticLabel = "动作完成/位置反馈状态",
                    ActivationRule = "BOOL_FALSE",
                    AbnormalWhen = "False",
                    MonitoringRole = "RootCause",
                    RootPriority = 72,
                    Score = 72,
                    Reason = "Output name matches done/in-position/home feedback semantics."
                };
            }

            if (ContainsAny(text, DevicePermitKeywords))
            {
                return new DeviceHealthClassification
                {
                    AnchorType = "DevicePermitState",
                    SemanticCategory = "PermitReady",
                    SemanticLabel = "设备许可/就绪状态",
                    ActivationRule = "BOOL_FALSE",
                    AbnormalWhen = "False",
                    MonitoringRole = "RootCause",
                    RootPriority = 72,
                    Score = 72,
                    Reason = "Output name or block context matches ready/permit/enable/ok semantics."
                };
            }

            if (ContainsAny(text, DeviceAnalogKeywords) && ContainsAny($"{blockName} {title} {string.Join(" ", inputVariables ?? Array.Empty<string>())}", DeviceContextKeywords))
            {
                return new DeviceHealthClassification
                {
                    AnchorType = "DeviceAnalogContext",
                    SemanticCategory = "AnalogContext",
                    SemanticLabel = "设备模拟量上下文",
                    ActivationRule = "UNKNOWN",
                    AbnormalWhen = "ContextOnly",
                    MonitoringRole = "Context",
                    RootPriority = 45,
                    Score = 48,
                    Reason = "Output appears to be an axis/device analog context signal."
                };
            }

            return null;
        }

        private static DeviceHealthClassification BuildDeviceHealthReviewClassification(string variableName)
        {
            return new DeviceHealthClassification
            {
                AnchorType = "Unknown",
                SemanticCategory = "DeviceHealthReview",
                SemanticLabel = string.IsNullOrWhiteSpace(variableName) ? "设备健康候选" : variableName,
                ActivationRule = "UNKNOWN",
                AbnormalWhen = "Unknown",
                MonitoringRole = "Review",
                RootPriority = 50,
                SemanticSource = "StructuralPrefilter",
                Score = 50,
                Reason = "Runtime-eligible write output; queued for LLM semantic classification."
            };
        }

        private static bool IsDeviceMessageVariable(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName)) return false;
            if (variableName.IndexOf(".Messages.", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            var leaf = GetVariableLeaf(variableName);
            return Regex.IsMatch(leaf, @"^Message\d*$", RegexOptions.IgnoreCase);
        }

        private static bool IsAlarmLikeDeviceHealthVariable(string variableName, string leafText)
        {
            if (string.IsNullOrWhiteSpace(variableName)) return false;
            if (IsDeviceMessageVariable(variableName)) return false;

            var path = variableName.ToLowerInvariant();
            if (path.Contains(".warnings.")
                || path.Contains(".warning.")
                || path.Contains(".faults.")
                || path.Contains(".fault.")
                || path.Contains(".errors.")
                || path.Contains(".error.")
                || path.Contains(".alarms.")
                || path.Contains(".alarm."))
            {
                return true;
            }

            return ContainsAny(leafText, DeviceAlarmKeywords);
        }

        private static string GetVariableLeaf(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName)) return string.Empty;
            var value = variableName.Trim();
            var index = value.LastIndexOf('.');
            return index >= 0 && index < value.Length - 1
                ? value[(index + 1)..]
                : value;
        }

        private static bool IsDeviceHealthReadableVariable(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName)) return false;
            var v = variableName.Trim();
            if (v.StartsWith("#", StringComparison.Ordinal)) return false;
            if (decimal.TryParse(v, out _)) return false;
            if (v.Equals("AlwaysTRUE", StringComparison.OrdinalIgnoreCase)) return false;
            if (v.StartsWith("Clock_", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static string ResolveDeviceHealthMonitorability(string variableName, string variableScope)
        {
            if (string.Equals(variableScope, "LocalVariable", StringComparison.OrdinalIgnoreCase))
            {
                return "LocalOnly";
            }

            if (!string.IsNullOrWhiteSpace(variableName)
                && (variableName.StartsWith("#", StringComparison.Ordinal)
                    || (!variableName.Contains('.') && !variableName.Contains('['))))
            {
                return "LocalOnly";
            }

            return "RuntimeReadable";
        }

        private static bool IsRejectedDeviceHealthCandidate(DeviceHealthPreExtractCandidateDto candidate)
        {
            if (candidate == null) return true;
            if (string.Equals(candidate.AnchorType, "Ignore", StringComparison.OrdinalIgnoreCase)) return true;
            if (IsDeviceAlarmFalseAbnormalRule(
                    candidate.AnchorType,
                    candidate.ActivationRule,
                    candidate.AbnormalWhen))
            {
                return true;
            }

            if (string.Equals(candidate.AnchorType, "Unknown", StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.SemanticSource, "LLM", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(candidate.MonitoringRole, "Ignore", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool IsDeviceAlarmFalseAbnormalRule(
            string anchorType,
            string activationRule,
            string abnormalWhen = null)
        {
            if (!string.Equals(anchorType, "DeviceAlarmState", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(activationRule, "BOOL_FALSE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(abnormalWhen, "False", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static bool IsSeedableDeviceFocusCandidate(DeviceHealthPreExtractCandidateDto candidate)
        {
            if (candidate == null) return false;
            if (string.IsNullOrWhiteSpace(candidate.VariableName)) return false;
            if (string.IsNullOrWhiteSpace(candidate.StationCode)) return false;
            if (!DeviceFocusAnchorTypes.Contains(candidate.AnchorType ?? string.Empty)) return false;
            if (string.Equals(candidate.Monitorability, "LocalOnly", StringComparison.OrdinalIgnoreCase)) return false;
            if (IsRejectedDeviceHealthCandidate(candidate)) return false;
            return true;
        }

        private static DeviceFocusSignalDto MapDeviceFocusSignal(PLC_StationShadowSignalEntity entity)
        {
            return new DeviceFocusSignalDto
            {
                Id = entity.Id,
                ProjectId = entity.ProjectId,
                StationCode = entity.StationCode,
                SignalKey = entity.SignalKey,
                VariableName = entity.VariableName,
                AnchorType = entity.AnchorType,
                SemanticCategory = entity.SemanticCategory,
                SemanticLabel = entity.SemanticLabel,
                ActivationRule = entity.ActivationRule,
                GroupName = entity.GroupName,
                Priority = entity.Priority,
                IsCore = entity.IsCore == true,
                IsContext = entity.IsContext == true,
                IsWatchOnly = entity.IsWatchOnly == true,
                ParticipateInPattern = entity.ParticipateInPattern == true,
                Enabled = entity.Enabled != false,
                LogicUnitId = entity.LogicUnitId,
                SortOrder = entity.SortOrder,
                Remark = entity.Remark,
                CreateTime = entity.CreateTime,
                ModifyTime = entity.ModifyTime
            };
        }

        private static void ApplyDeviceFocusSignal(
            PLC_StationShadowSignalEntity entity,
            DeviceHealthPreExtractCandidateDto candidate,
            string signalKey,
            int sortOrder,
            DateTime now,
            bool updateCreateTime)
        {
            var isContext = string.Equals(candidate.MonitoringRole, "Context", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.AnchorType, "DeviceAnalogContext", StringComparison.OrdinalIgnoreCase);
            var isRootCause = string.Equals(candidate.MonitoringRole, "RootCause", StringComparison.OrdinalIgnoreCase);
            var isCore = !isContext && (isRootCause || candidate.RecommendedForMonitoring);
            var priority = candidate.RootPriority ?? candidate.Score;

            entity.StationCode = candidate.StationCode?.Trim();
            entity.SignalKey = signalKey;
            entity.VariableName = candidate.VariableName;
            entity.SignalType = candidate.AnchorType;
            entity.AnchorType = candidate.AnchorType;
            entity.GroupName = DeviceFocusGroupName;
            entity.Priority = priority <= 0 ? 80 : priority;
            entity.IsCore = isCore;
            entity.IsContext = isContext;
            entity.IsWatchOnly = isContext || !candidate.RecommendedForMonitoring;
            entity.ParticipateInPattern = !isContext && candidate.RecommendedForMonitoring;
            entity.LogicUnitId = candidate.LogicUnitId;
            entity.SemanticLabel = candidate.SemanticLabel;
            entity.SemanticCategory = candidate.SemanticCategory;
            entity.ActivationRule = string.IsNullOrWhiteSpace(candidate.ActivationRule) ? "UNKNOWN" : candidate.ActivationRule;
            entity.SortOrder = sortOrder;
            entity.Enabled = true;
            entity.IsDeleted = false;
            entity.Remark = BuildDeviceFocusRemark(candidate);
            entity.ModifyTime = now;
            if (updateCreateTime)
            {
                entity.CreateTime = now;
            }
        }

        private static string BuildDeviceFocusSignalKey(DeviceHealthPreExtractCandidateDto candidate)
        {
            var station = string.IsNullOrWhiteSpace(candidate.StationCode) ? "UNKNOWN" : candidate.StationCode.Trim();
            var anchorType = string.IsNullOrWhiteSpace(candidate.AnchorType) ? "DeviceFocus" : candidate.AnchorType.Trim();
            var variable = Regex.Replace(candidate.VariableName?.Trim() ?? string.Empty, @"\s+", string.Empty);
            return $"device-focus:{station}:{anchorType}:{variable}";
        }

        private static string BuildDeviceFocusRemark(DeviceHealthPreExtractCandidateDto candidate)
        {
            var evidence = candidate.Evidence?.FirstOrDefault(it => !string.IsNullOrWhiteSpace(it));
            var parts = new List<string>
            {
                $"Role={candidate.MonitoringRole ?? "Unknown"}",
                $"AbnormalWhen={candidate.AbnormalWhen ?? "Unknown"}",
                $"Source={candidate.SemanticSource ?? "Unknown"}",
                $"Score={candidate.Score}",
                $"Confidence={candidate.Confidence:0.##}"
            };
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                parts.Add(evidence);
            }

            return string.Join("; ", parts);
        }

        private static string BuildStationVariableKey(string stationCode, string variableName)
            => $"{stationCode?.Trim() ?? string.Empty}:{variableName?.Trim() ?? string.Empty}";

        private static string ExtractStationCodeFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = Regex.Match(text, @"OP\d{2,3}", RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToUpperInvariant() : null;
        }

        private static bool ContainsAny(string value, IEnumerable<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(value) || keywords == null)
            {
                return false;
            }

            return keywords.Any(keyword =>
                !string.IsNullOrWhiteSpace(keyword)
                && value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(it => !string.IsNullOrWhiteSpace(it))?.Trim();
        }

        /// <summary>
        /// 发现物理Q点、虚拟Q点和握手位候选。
        /// </summary>
        public async Task<HttpResponseResultModel<AnchorDiscoveryResultDto>> DiscoverAnchorsAsync(AnchorDiscoveryRequestDto request)
        {
            var response = new HttpResponseResultModel<AnchorDiscoveryResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }

            await PreparePrecomputedStationsAsync(request);

            response.BackResult = await _anchorDiscoveryService.DiscoverAsync(request);
            response.BackResult.StationMap = await _stationMapBuilderService.BuildAsync(
                request.ProjectId,
                CollectStationMapInputAnchors(response.BackResult));
            response.BackResult.StationMap.VariablePropagations = await _variablePropagationBuilderService.BuildAsync(
                request.ProjectId,
                CollectStationMapInputAnchors(response.BackResult));
            response.BackResult.Summary = BuildSummary(
                response.BackResult,
                CollectStationMapInputAnchors(response.BackResult));
            response.IsSuccess = true;
            return response;
        }

        /// <summary>
        /// 仅返回工位地图与传播链预览，适合测试工位识别与虚拟Q点归属。
        /// 不返回 AST、染色结果等较重内容。
        /// </summary>
        public async Task<HttpResponseResultModel<object>> PreviewStationMapAsync(AnchorDiscoveryRequestDto request)
        {
            var response = new HttpResponseResultModel<object>();
            var discover = await DiscoverAnchorsAsync(request);
            if (!discover.IsSuccess || discover.BackResult == null)
            {
                response.IsSuccess = false;
                response.ErrorMessage = discover.ErrorMessage;
                return response;
            }

            var dto = discover.BackResult;
            response.BackResult = new
            {
                dto.ProjectId,
                dto.StationCode,
                dto.GeneratedAt,
                dto.Summary,
                AnchorCounts = new
                {
                    Physical = dto.PhysicalAnchors.Count,
                    CorePhysical = dto.CorePhysicalAnchors.Count,
                    Virtual = dto.VirtualAnchors.Count,
                    CoreVirtual = dto.CoreVirtualAnchors.Count,
                    Review = dto.VirtualAnchorReviewQueue.Count,
                    Handshake = dto.HandshakeAnchors.Count
                },
                dto.StationMap,
                dto.Warnings
            };
            response.IsSuccess = true;
            return response;
        }

        /// <summary>
        /// Recompile and optionally persist the AST row for one LogicUnit.
        /// </summary>
        public async Task<HttpResponseResultModel<RecompileLogicUnitAstResultDto>> RecompileLogicUnitAstAsync(
            RecompileLogicUnitAstRequestDto request)
        {
            var response = new HttpResponseResultModel<RecompileLogicUnitAstResultDto>();
            if (request == null || request.ProjectId <= 0 || request.LogicUnitId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 和 LogicUnitId 不能为空";
                return response;
            }

            var seed = await _phaseOneCompilerService.BuildWriterSeedForLogicUnitAsync(request);
            if (seed == null)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "未找到可编译的 LogicUnit，或该 LogicUnit 类型不在 AST 编译范围内";
                return response;
            }

            var compileResults = await _astCompilerService.CompileSeedsAsync(new List<StopCauseAstSeed> { seed });
            var compileResult = compileResults.FirstOrDefault();
            var coverage = compileResult?.CoverageReports?
                .FirstOrDefault(it => it.LogicUnitId == request.LogicUnitId)
                ?? seed.CoverageReports.FirstOrDefault();

            var persistedRows = 0;
            if (request.Persist)
            {
                persistedRows = await _astPersistenceService.PersistLogicUnitAsync(
                    request.ProjectId,
                    request.LogicUnitId,
                    compileResults,
                    Array.Empty<AstSemanticFinding>());
            }

            var rootNode = compileResult?.RootNode;
            var writerSummary = seed.WriterLogicUnits.FirstOrDefault();
            response.BackResult = new RecompileLogicUnitAstResultDto
            {
                ProjectId = request.ProjectId,
                LogicUnitId = request.LogicUnitId,
                StationCode = compileResult?.Anchor?.StationCode,
                BlockId = writerSummary?.BlockId,
                BlockName = coverage?.BlockName,
                UnitType = coverage?.UnitType,
                NetworkId = coverage?.NetworkId,
                Title = coverage?.Title,
                ProgrammingLanguage = writerSummary?.ProgrammingLanguage,
                AnchorVariable = compileResult?.Anchor?.VariableName ?? seed.Anchor?.VariableName,
                CompileStatus = compileResult?.CompileStatus ?? coverage?.CompileStatus,
                CompilerName = compileResult?.CompilerName,
                InputVariables = coverage?.InputVariables ?? new List<string>(),
                OutputVariables = coverage?.OutputVariables ?? new List<string>(),
                NodeCount = CountStopCauseAstNodes(rootNode),
                MaxDepth = MeasureStopCauseAstDepth(rootNode),
                Persisted = request.Persist,
                PersistedAstRows = persistedRows,
                RootNode = request.IncludeRootNode ? rootNode : null,
                RootNodeJson = request.IncludeRootNodeJson && rootNode != null
                    ? JsonSerializer.Serialize(rootNode)
                    : null,
                Messages = compileResult?.Messages ?? new List<string>()
            };
            response.IsSuccess = true;
            return response;
        }

        /// <summary>
        /// 构建第一阶段锚点目录和 AST 种子，并可选执行工位染色。
        /// 无论是否包含 AST，汇总统计（Summary）始终填充。
        /// </summary>
        public async Task<HttpResponseResultModel<AnchorDiscoveryResultDto>> BuildPhaseOneCatalogAsync(AnchorDiscoveryRequestDto request)
        {
            var response = new HttpResponseResultModel<AnchorDiscoveryResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }

            await PreparePrecomputedStationsAsync(request);

            var physicalRequest = ClonePhysicalOnlyRequest(request);
            var dto = await _anchorDiscoveryService.DiscoverAsync(physicalRequest);

            if (request.IncludeAstSeeds)
            {
                dto.AstSeeds = await _phaseOneCompilerService.BuildWriterSeedsAsync(request);

                if (request.IncludeAstCompileResults)
                {
                    dto.AstCompileResults = await _astCompilerService.CompileSeedsAsync(dto.AstSeeds);
                }
            }

            if (request.IncludeAstCompileResults && (request.IncludeVirtualAnchors || request.IncludeHandshakeAnchors))
            {
                dto.AstSemanticFindings = await _astSemanticInferenceService.InferAsync(
                    request,
                    dto.AstCompileResults,
                    dto.PhysicalAnchors);

                // 后置工位补全：利用 PrecomputedVariableStations + 调用链为空工位 findings 回填
                BackfillFindingStationCodes(dto.AstSemanticFindings, request);

                dto.AstSemanticFindings = await ResolveAndExpandSemanticFindingsAsync(
                    dto.AstSemanticFindings, request.ProjectId);

                ApplyAstSemanticFindings(dto, request);
                dto.LlmDiagnostics = _astSemanticInferenceService.BuildDiagnostics(
                    dto.AstSemanticFindings,
                    _astSemanticInferenceService.CountCandidates(dto.AstCompileResults, dto.PhysicalAnchors));
            }

            // ── GRAPH StepAction 输出变量补充为锚点 ──
            // 物理 Q 点来自 IOTag 表（%Q 地址），但 GRAPH StepAction 里的赋值变量
            // （如 OP030_YV01_H）是 DB 变量，不在 IOTag 表中。
            // 将它们从 AST 编译结果中提取出来，作为 GraphAction 类型锚点补充，
            // 确保它们进入锚点绑定表、监控信号播种和诊断候选。
            if (dto.AstCompileResults?.Count > 0)
            {
                // 物理锚点已覆盖的变量名：GraphAction 不重复创建
                var existingPhysicalVarNames = new HashSet<string>(
                    dto.PhysicalAnchors.Select(a => a.VariableName).Where(v => !string.IsNullOrWhiteSpace(v)),
                    StringComparer.OrdinalIgnoreCase);

                var graphAnchors = new List<AnchorCandidate>();
                // 用 "varName|logicUnitId" 去重，允许同一变量出现在多个 Step（不同 LogicUnitId）
                var graphAnchorDedupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var compileResult in dto.AstCompileResults)
                {
                    if (compileResult.CoverageReports == null) continue;
                    foreach (var report in compileResult.CoverageReports)
                    {
                        if (!IsGraphStepAction(report.UnitType)) continue;
                        if (report.CompileStatus != AstCompileStatus.Compiled
                            && report.CompileStatus != AstCompileStatus.PartiallyCompiled) continue;

                        foreach (var outputVar in report.OutputVariables ?? new List<string>())
                        {
                            if (string.IsNullOrWhiteSpace(outputVar)) continue;
                            var varName = outputVar.Trim();
                            // 物理锚点已有该变量，跳过
                            if (existingPhysicalVarNames.Contains(varName)) continue;
                            // 同一 (varName, logicUnitId) 只创建一次
                            var dedupKey = $"{varName}|{report.LogicUnitId}";
                            if (!graphAnchorDedupKeys.Add(dedupKey)) continue;

                            // 工位推断：从 anchor 继承，或从变量名前缀推断
                            var stationCode = compileResult.Anchor?.StationCode;

                            graphAnchors.Add(new AnchorCandidate
                            {
                                // AnchorKey 内嵌 LogicUnitId，播种时直接解析，无需反查 AST
                                AnchorKey = $"graph:{varName}:lu:{report.LogicUnitId}",
                                AnchorType = "GraphAction",
                                VariableName = varName,
                                LogicUnitId = report.LogicUnitId,
                                StationCode = stationCode,
                                StationConfidence = compileResult.Anchor?.StationConfidence ?? 0.8m,
                                StationEvidence = new List<string> { $"继承自GRAPH所属锚点 {compileResult.Anchor?.AnchorKey}" },
                                Source = "GraphStepAction",
                                Confidence = 0.90m,
                                DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                                PrimaryReason = $"GRAPH StepAction 赋值输出（{report.BlockName}/{report.UnitType}）",
                                LogicSourceType = "GraphStepOutput",
                                Evidence = new List<string>
                                {
                                    $"来源: GRAPH StepAction (LogicUnit={report.LogicUnitId}, Block={report.BlockName})",
                                    $"UnitType: {report.UnitType}",
                                    $"Action片段: {BuildGraphActionEvidence(report.RawContentExcerpt)}",
                                    $"出口Transition: {BuildGraphTransitionEvidence(report.GraphOutgoingTransitionsJson, report.GraphTransitionConditionsJson)}"
                                }
                            });
                        }
                    }
                }

                if (graphAnchors.Count > 0)
                {
                    dto.PhysicalAnchors.AddRange(graphAnchors);
                }
            }

            var allAnchors = CollectStationMapInputAnchors(dto);
            dto.StationMap = await _stationMapBuilderService.BuildAsync(request.ProjectId, allAnchors);
            dto.StationMap.VariablePropagations = await _variablePropagationBuilderService.BuildAsync(request.ProjectId, allAnchors);

            if (request.IncludeStationColoring)
            {
                dto.StationColoringResult = await _stationColoringService.ColorAsync(
                    request.ProjectId,
                    dto.StationMap.AnchorAssignments);
            }

            var shouldInferGraphActivationRules = false;
            string graphActivationRuleStationCode = null;

            if (request.PersistStationMap)
            {
                await _stationMapPersistenceService.PersistAsync(
                    request.ProjectId,
                    dto.StationMap,
                    dto.StationColoringResult,
                    request.StationCode);

                // 物理删除下游监控数据，避免残留旧记录导致重复
                var sc = request.StationCode?.Trim();
                if (!string.IsNullOrWhiteSpace(sc))
                {
                    await _stationShadowSignalService.DeleteAsync(
                        it => it.ProjectId == request.ProjectId && it.StationCode == sc);
                    await _stationPatternTemplateService.DeleteAsync(
                        it => it.ProjectId == request.ProjectId && it.StationCode == sc);
                    await _stationShadowConfigService.DeleteAsync(
                        it => it.ProjectId == request.ProjectId && it.StationCode == sc);
                }
                else
                {
                    await _stationShadowSignalService.DeleteAsync(
                        it => it.ProjectId == request.ProjectId);
                    await _stationPatternTemplateService.DeleteAsync(
                        it => it.ProjectId == request.ProjectId);
                    await _stationShadowConfigService.DeleteAsync(
                        it => it.ProjectId == request.ProjectId);
                }

                // 自动重新播种 shadow signal
                var stationCodes = !string.IsNullOrWhiteSpace(sc)
                    ? new List<string> { sc }
                    : dto.StationMap.AnchorAssignments
                        .Select(a => a.StationCode)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                foreach (var station in stationCodes)
                {
                    await SeedMonitoringSignalsAsync(new StationShadowSeedSignalsRequestDto
                    {
                        ProjectId = request.ProjectId,
                        StationCode = station,
                        ReplaceExisting = false   // 已物理删除，无需再清理
                    });
                }
            }

            if (request.PersistStationMap)
            {
                shouldInferGraphActivationRules = true;
                graphActivationRuleStationCode = request.StationCode?.Trim();
            }

            if (request.PersistAstResults && request.IncludeAstCompileResults)
            {
                await _astPersistenceService.PersistAsync(
                    request.ProjectId,
                    request.StationCode,
                    request.BlockId,
                    dto.AstCompileResults,
                    dto.AstSemanticFindings);
            }

            if (shouldInferGraphActivationRules)
            {
                try
                {
                    await InferAndPersistGraphActivationRulesAsync(
                        request.ProjectId,
                        graphActivationRuleStationCode);
                }
                catch (Exception ex)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GraphActivationRule] PersistStationMap后推导失败，不影响一键分析入库: {ex.Message}",
                        "GraphActivationRule");
                }
            }

            dto.Summary = BuildSummary(dto, allAnchors);
            response.BackResult = dto;
            response.IsSuccess = true;
            return response;
        }

        /// <summary>
        /// 后台执行一键分析入库全流程（供 Hangfire 调用）。
        /// 合并了 extract-stations → station-map → compile-preview 三步为单一后台任务，
        /// 并通过 FullAnalysisProgressService 实时上报进度。
        /// 该方法也可直接被 PLC 上传完成后的回调触发。
        /// </summary>
        [Hangfire.AutomaticRetry(Attempts = 0)]
        [Hangfire.Queue("default")]
        /// <summary>
        /// 上传后自动跑的一条龙：全量分析（产出 AST + 锚点绑定）→ 顺带把缺的监控信号补上。
        ///
        /// 为什么要串起来（2026-09-22）：上传解析只产出 LogicUnit，AST 是另一条独立链路，
        /// 不跑就没有；而没有 AST 就没有锚点绑定，没有锚点绑定就播不出监控信号。结果是
        /// 停机诊断整片「无AST」、信号心跳每个工位 0核心/0上下文——两个症状同一个根因，
        /// 以前都要人去工作台手点两次才能补齐。
        ///
        /// 播种用 replaceExisting: false：只补该工位还没有的信号，**不动**已有的。
        /// 上传是高频动作，若每次都整片重建，现场手工调过的监控信号会被反复冲掉。
        ///
        /// 播种失败不改变分析本身的成败——AST 已经落库，诊断就已经能用了。
        /// </summary>
        public async Task ExecuteFullAnalysisAndSeedSignalsAsync(
            string taskId, long projectId, bool useLlmAnnotation, int rawContentMaxLength)
        {
            await ExecuteFullAnalysisAsync(taskId, projectId, null, null, useLlmAnnotation, rawContentMaxLength);

            try
            {
                var seed = await BatchSeedMonitoringSignalsAsync(projectId, replaceExisting: false);
                await WriteLogHelper.WriteLogsAsync(
                    $"[上传自动分析] 监控信号补播完成 Project={projectId} "
                    + $"成功={seed?.IsSuccess} 说明={seed?.ErrorMessage}",
                    "停机诊断");
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[上传自动分析] 监控信号补播失败 Project={projectId}：{ex.Message}"
                    + "（不影响已落库的 AST，可到监控页手动重建）",
                    "停机诊断");
            }
        }

        public async Task ExecuteFullAnalysisAsync(string taskId, long projectId, string stationCode, long? blockId, bool useLlmAnnotation, int rawContentMaxLength)
        {
            var progress = _fullAnalysisProgressService.Get(taskId);
            if (progress == null) return;

            var isBlockScoped = blockId.HasValue;
            var summary = new FullAnalysisSummaryDto();

            try
            {
                // ── Step 1: 工位提取与块工位回填 ──
                _fullAnalysisProgressService.Advance(taskId, 1, "工位提取", "正在提取工位信息并回填块工位...", 5);

                var extractRequest = new StationExtractionRequestDto
                {
                    ProjectId = projectId,
                    StationCode = stationCode,
                    BlockId = blockId,
                    UseLlmAnnotation = useLlmAnnotation,
                    IncludePhysicalTags = true,
                    IncludeVariableGroups = true,
                    IncludeLogicUnitMarking = true,
                    IncludeSourceItems = true, // 需要 VariableGroupItems 用于写入变量投票回填
                    PersistStationMarking = !isBlockScoped,
                    UpdateBlockStationCode = !isBlockScoped,
                    MaxDetailCountPerCategory = 200
                };
                var extractResult = await ExtractStationsAsync(extractRequest);
                if (!extractResult.IsSuccess)
                {
                    _fullAnalysisProgressService.Fail(taskId, 1, "工位提取", extractResult.ErrorMessage ?? "工位提取失败");
                    return;
                }
                summary.StationCount = extractResult.BackResult?.StationBuckets?.Count ?? 0;

                // 从 VariableGroupItems 构建变量→工位映射，供写入变量投票回填使用
                var variableStations = (extractResult.BackResult?.VariableGroupItems ?? new List<StationMarkedSourceItem>())
                    .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                    .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                        {
                            var best = g.OrderByDescending(it => it.Confidence).First();
                            return new StationInferenceResult
                            {
                                StationCode = best.StationCode ?? string.Empty,
                                Confidence = best.Confidence,
                                InferenceSource = best.StationSource,
                                InferenceDetail = best.StationSourceDetail,
                                Evidence = best.Evidence?.ToList() ?? new List<string>()
                            };
                        },
                        StringComparer.OrdinalIgnoreCase);

                // 释放源数据，后续不再需要
                extractResult.BackResult?.PhysicalTagItems?.Clear();
                extractResult.BackResult?.VariableGroupItems?.Clear();

                _fullAnalysisProgressService.Advance(taskId, 1, "工位提取", $"工位提取完成，发现 {summary.StationCount} 个工位", 15);

                // ── Step 2: 写入变量投票回填 + 工位地图构建 ──
                _fullAnalysisProgressService.Advance(taskId, 2, "工位地图", "正在执行写入变量投票回填...", 16);
                if (!isBlockScoped)
                {
                    var votingCount = await _stationBackfillService.ApplyWriteVariableVotingAsync(projectId, variableStations);
                    if (votingCount > 0)
                    {
                        summary.StationCount += 0; // 投票不一定产生新工位，只是补充块的工位归属
                    }
                }
                _fullAnalysisProgressService.Advance(taskId, 2, "工位地图", "正在构建工位地图与变量传播链...", 18);

                var stationMapRequest = new AnchorDiscoveryRequestDto
                {
                    ProjectId = projectId,
                    StationCode = stationCode,
                    BlockId = blockId,
                    UseLlmAnnotation = useLlmAnnotation,
                    IncludePhysicalAnchors = true,
                    IncludeVirtualAnchors = false,
                    IncludeHandshakeAnchors = false,
                    IncludeAstSeeds = false,
                    IncludeAstCompileResults = false,
                    RawContentMaxLength = rawContentMaxLength
                };
                var mapResult = await DiscoverAnchorsAsync(stationMapRequest);
                _fullAnalysisProgressService.Advance(taskId, 2, "工位地图", "工位地图构建完成", 28);

                // ── Step 3: AST 种子构建 ──
                _fullAnalysisProgressService.Advance(taskId, 3, "AST 种子", "正在构建 AST 写入逻辑种子...", 30);

                var compileRequest = new AnchorDiscoveryRequestDto
                {
                    ProjectId = projectId,
                    StationCode = stationCode,
                    BlockId = blockId,
                    UseLlmAnnotation = useLlmAnnotation,
                    IncludePhysicalAnchors = true,
                    IncludeVirtualAnchors = false,
                    IncludeHandshakeAnchors = false,
                    IncludeAstSeeds = true,
                    IncludeAstCompileResults = true,
                    IncludeStationColoring = !isBlockScoped,
                    PersistStationMap = !isBlockScoped,
                    PersistAstResults = true,
                    RawContentMaxLength = rawContentMaxLength
                };

                // ── Step 4: AST 编译 + 落库（在 BuildPhaseOneCatalogAsync 内完成） ──
                _fullAnalysisProgressService.Advance(taskId, 4, "AST 编译", "正在编译 AST 并落库...", 40);

                var compileResult = await BuildPhaseOneCatalogAsync(compileRequest);
                if (!compileResult.IsSuccess)
                {
                    _fullAnalysisProgressService.Fail(taskId, 4, "AST 编译", compileResult.ErrorMessage ?? "AST 编译失败");
                    return;
                }

                var compileDto = compileResult.BackResult;
                summary.AstSeedCount = compileDto?.AstSeeds?.Count ?? 0;
                summary.AstCompileResultCount = compileDto?.AstCompileResults?.Count ?? 0;
                summary.AnchorBindingCount = compileDto?.StationMap?.AnchorAssignments?.Count ?? 0;
                _fullAnalysisProgressService.Advance(taskId, 4, "AST 编译",
                    $"AST 编译完成: 种子 {summary.AstSeedCount}, 编译结果 {summary.AstCompileResultCount}", 85);

                // ── Step 5: 收尾 ──
                _fullAnalysisProgressService.Advance(taskId, 5, "收尾", "正在整理结果...", 90);
                summary.MonitoringSignalCount = 0; // 监控信号已在 BuildPhaseOneCatalogAsync 内播种

                _fullAnalysisProgressService.Complete(taskId, summary);

                await WriteLogHelper.WriteLogsAsync(
                    $"[一键分析完成] TaskId={taskId}, ProjectId={projectId}, StationCode={stationCode ?? "ALL"}, " +
                    $"Stations={summary.StationCount}, Seeds={summary.AstSeedCount}, Compiled={summary.AstCompileResultCount}, Bindings={summary.AnchorBindingCount}",
                    "一键分析");
            }
            catch (Exception ex)
            {
                _fullAnalysisProgressService.Fail(taskId, progress.CurrentStep, progress.StepName, ex.Message);
                await WriteLogHelper.WriteLogsAsync(
                    $"[一键分析失败] TaskId={taskId}, Step={progress.CurrentStep} {progress.StepName}, Error={ex.Message}\n{ex.StackTrace}",
                    "一键分析");
            }
        }

        /// <summary>
        /// 获取一键分析进度
        /// </summary>
        public FullAnalysisProgressDto GetFullAnalysisProgress(string taskId)
        {
            return _fullAnalysisProgressService.Get(taskId);
        }

        /// <summary>
        /// 后台执行批量 Q 点提取（供 Hangfire 调用）。
        /// 枚举项目所有工位，逐一 BuildPhaseOneCatalogAsync，通过进度服务实时上报。
        /// </summary>
        [Hangfire.AutomaticRetry(Attempts = 0)]
        [Hangfire.Queue("default")]
        public async Task ExecuteBatchQPointDiscoveryAsync(
            string taskId,
            long projectId,
            bool includePhysicalAnchors,
            bool includeVirtualAnchors,
            bool includeHandshakeAnchors,
            bool includeAstSeeds,
            bool includeAstCompileResults,
            bool useLlmAnnotation,
            int rawContentMaxLength)
        {
            var progress = _fullAnalysisProgressService.Get(taskId);
            if (progress == null) return;

            try
            {
                // Step 1: 枚举工位
                _fullAnalysisProgressService.Advance(taskId, 1, "枚举工位", "正在探测项目内所有工位...", 5);

                var probe = new StationExtractionRequestDto
                {
                    ProjectId = projectId,
                    IncludePhysicalTags = includePhysicalAnchors,
                    IncludeVariableGroups = includeVirtualAnchors || includeHandshakeAnchors,
                    IncludeLogicUnitMarking = false,
                    IncludeSourceItems = false,
                    UseLlmAnnotation = false
                };
                var probeResult = await ExtractStationsAsync(probe);
                var stations = new List<string>();
                if (probeResult.IsSuccess && probeResult.BackResult != null)
                {
                    stations = probeResult.BackResult.StationBuckets
                        .Select(it => it.StationCode)
                        .Where(it => !string.IsNullOrWhiteSpace(it))
                        .Distinct()
                        .OrderBy(s => s)
                        .ToList();
                }

                if (stations.Count == 0)
                {
                    _fullAnalysisProgressService.Fail(taskId, 1, "枚举工位", "未发现任何工位，请先执行一键分析并入库");
                    return;
                }

                _fullAnalysisProgressService.Advance(taskId, 1, "枚举工位",
                    $"发现 {stations.Count} 个工位，开始逐一提取 Q 点...", 10);

                // Step 2: 逐工位提取
                int completed = 0;
                int totalAnchors = 0;
                var stationResults = new List<object>();

                foreach (var station in stations)
                {
                    completed++;
                    int pct = 10 + (int)(80.0 * completed / stations.Count);
                    _fullAnalysisProgressService.Advance(taskId, 2, $"提取 Q 点 ({completed}/{stations.Count})",
                        $"正在处理工位 {station}...", Math.Min(pct, 95));

                    try
                    {
                        var req = new AnchorDiscoveryRequestDto
                        {
                            ProjectId = projectId,
                            StationCode = station,
                            IncludePhysicalAnchors = includePhysicalAnchors,
                            IncludeVirtualAnchors = includeVirtualAnchors,
                            IncludeHandshakeAnchors = includeHandshakeAnchors,
                            IncludeAstSeeds = includeAstSeeds,
                            IncludeAstCompileResults = includeAstCompileResults,
                            UseLlmAnnotation = useLlmAnnotation,
                            PersistStationMap = true,
                            PersistAstResults = true,
                            RawContentMaxLength = rawContentMaxLength
                        };
                        var r = await BuildPhaseOneCatalogAsync(req);
                        if (r.IsSuccess && r.BackResult != null)
                        {
                            var dto = r.BackResult;
                            var count = dto.PhysicalAnchors.Count + dto.VirtualAnchors.Count + dto.HandshakeAnchors.Count;
                            totalAnchors += count;
                            stationResults.Add(new { station, count, success = true });
                        }
                        else
                        {
                            stationResults.Add(new { station, count = 0, success = false, error = r.ErrorMessage });
                        }
                    }
                    catch (Exception ex)
                    {
                        stationResults.Add(new { station, count = 0, success = false, error = ex.Message });
                    }
                }

                // Complete
                var summary = new FullAnalysisSummaryDto
                {
                    StationCount = stations.Count,
                    AnchorBindingCount = totalAnchors
                };
                _fullAnalysisProgressService.Complete(taskId, summary);

                await WriteLogHelper.WriteLogsAsync(
                    $"[批量Q点提取完成] TaskId={taskId}, ProjectId={projectId}, Stations={stations.Count}, TotalAnchors={totalAnchors}",
                    "批量Q点提取");
            }
            catch (Exception ex)
            {
                _fullAnalysisProgressService.Fail(taskId, progress.CurrentStep, progress.StepName, ex.Message);
                await WriteLogHelper.WriteLogsAsync(
                    $"[批量Q点提取失败] TaskId={taskId}, Error={ex.Message}\n{ex.StackTrace}",
                    "批量Q点提取");
            }
        }

        /// <summary>
        /// 为项目内所有 GraphAction 信号推导并持久化 activation_rule。
        /// 以 LogicUnitId 为收敛维度，相同逻辑片段的多个变量合并为一次 LLM 调用。
        /// 推导结果批量写入 ai_plc_station_shadow_signal.activation_rule。
        /// </summary>
        private async Task InferAndPersistGraphActivationRulesAsync(long projectId, string stationCodeFilter = null)
        {
            var normalizedStationCode = stationCodeFilter?.Trim();
            var allGraphSignals = string.IsNullOrEmpty(normalizedStationCode)
                ? await _stationShadowSignalService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == projectId &&
                    it.AnchorType == "GraphAction" &&
                    !string.IsNullOrEmpty(it.VariableName))
                : await _stationShadowSignalService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == projectId &&
                    it.StationCode == normalizedStationCode &&
                    it.AnchorType == "GraphAction" &&
                    !string.IsNullOrEmpty(it.VariableName));

            if (allGraphSignals == null || allGraphSignals.Count == 0)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[GraphActivationRule] ProjectId={projectId} 无 GraphAction 信号，跳过",
                    "GraphActivationRule");
                return;
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[GraphActivationRule] ProjectId={projectId} 开始推导，GraphAction 信号数={allGraphSignals.Count}",
                "GraphActivationRule");

            // 按工位分组，逐工位推导（保持 LLM prompt 语境聚焦）
            var byStation = allGraphSignals
                .GroupBy(s => s.StationCode ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList());

            var now = DateTime.Now;
            foreach (var (stationCode, signals) in byStation)
            {
                var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var stepRoles = new Dictionary<long, GraphStepRoleInferenceResult>();
                try
                {
                    rules = await _graphActivationRuleInferService.InferRulesAsync(projectId, stationCode, signals);
                }
                catch (Exception ex)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GraphActivationRule] {stationCode} 推导失败: {ex.Message}",
                        "GraphActivationRule");
                }

                try
                {
                    stepRoles = await _graphActivationRuleInferService.InferStepRolesAsync(projectId, stationCode, signals);
                }
                catch (Exception ex)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GraphStepRole] {stationCode} 推导失败: {ex.Message}",
                        "GraphStepRole");
                }

                // 批量更新 activation_rule + graph step role
                var toUpdate = signals
                    .Where(s =>
                    {
                        var hasRule = !string.IsNullOrWhiteSpace(s.VariableName)
                                      && rules.TryGetValue(s.VariableName.Trim(), out var r)
                                      && !string.IsNullOrWhiteSpace(r);
                        var hasRole = s.LogicUnitId.HasValue && stepRoles.ContainsKey(s.LogicUnitId.Value);
                        return hasRule || hasRole;
                    })
                    .Select(s =>
                    {
                        if (!string.IsNullOrWhiteSpace(s.VariableName) &&
                            rules.TryGetValue(s.VariableName.Trim(), out var rule) &&
                            !string.IsNullOrWhiteSpace(rule))
                        {
                            s.ActivationRule = rule;
                        }

                        if (s.LogicUnitId.HasValue && stepRoles.TryGetValue(s.LogicUnitId.Value, out var role))
                        {
                            s.GraphStepRole = role.Role;
                            s.GraphStepRoleConfidence = role.Confidence;
                            s.GraphStepRoleReason = role.Reason;
                        }

                        s.ModifyTime = now;
                        return s;
                    })
                    .ToList();

                if (toUpdate.Count > 0)
                    await _stationShadowSignalService.BulkUpdateAsync(toUpdate);

                await WriteLogHelper.WriteLogsAsync(
                    $"[GraphActivationRule] {stationCode} 更新={toUpdate.Count}/{signals.Count}, roleCount={stepRoles.Count}",
                    "GraphActivationRule");
            }
        }

        /// <summary>
        /// 后台执行批量监控信号播种（供 Hangfire 调用）。
        /// 枚举项目所有工位锚点绑定，逐一 SeedMonitoringSignalsAsync，通过进度服务实时上报。
        /// </summary>
        [Hangfire.AutomaticRetry(Attempts = 0)]
        [Hangfire.Queue("default")]
        public async Task ExecuteBatchSeedSignalsAsync(string taskId, long projectId, bool replaceExisting)
        {
            var progress = _fullAnalysisProgressService.Get(taskId);
            if (progress == null) return;

            try
            {
                // Step 1: 枚举工位
                _fullAnalysisProgressService.Advance(taskId, 1, "枚举工位", "正在查询锚点绑定中的工位列表...", 5);

                var stations = await _stationAnchorBindingService.GetDb().AsQueryable()
                    .Where(it => it.IsDeleted == false && it.ProjectId == projectId && !string.IsNullOrEmpty(it.StationCode))
                    .Select(it => it.StationCode)
                    .Distinct()
                    .ToListAsync();

                stations = stations.OrderBy(s => s).ToList();

                if (stations.Count == 0)
                {
                    _fullAnalysisProgressService.Fail(taskId, 1, "枚举工位", "该项目没有任何锚点绑定数据，请先执行 Q 点提取");
                    return;
                }

                _fullAnalysisProgressService.Advance(taskId, 1, "枚举工位",
                    $"发现 {stations.Count} 个工位，开始逐一重建监控信号...", 10);

                // Step 2: 逐工位播种
                int completed = 0;
                int totalInserted = 0;
                int totalDeleted = 0;

                foreach (var station in stations)
                {
                    completed++;
                    int pct = 10 + (int)(85.0 * completed / stations.Count);
                    _fullAnalysisProgressService.Advance(taskId, 2, $"播种信号 ({completed}/{stations.Count})",
                        $"正在重建工位 {station} 的监控信号...", Math.Min(pct, 97));

                    var singleResult = await SeedMonitoringSignalsAsync(new StationShadowSeedSignalsRequestDto
                    {
                        ProjectId = projectId,
                        StationCode = station,
                        ReplaceExisting = replaceExisting
                    });

                    if (singleResult.IsSuccess && singleResult.BackResult != null)
                    {
                        totalInserted += singleResult.BackResult.InsertedCount;
                        totalDeleted += singleResult.BackResult.DeletedCount;
                    }
                }

                // Complete
                var summary = new FullAnalysisSummaryDto
                {
                    StationCount = stations.Count,
                    MonitoringSignalCount = totalInserted
                };

                // Step 3: LLM 推导 GraphAction 激活规则（按 LogicUnitId 收敛调用，跨工位批量入库）
                _fullAnalysisProgressService.Advance(taskId, 3, "推导激活规则",
                    "正在通过 LLM 分析 GRAPH 步骤激活条件...", 98);
                try
                {
                    await InferAndPersistGraphActivationRulesAsync(projectId);
                }
                catch (Exception ex)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GraphActivationRule] 推导失败（不影响信号入库）: {ex.Message}",
                        "批量信号播种");
                }

                _fullAnalysisProgressService.Complete(taskId, summary);

                await WriteLogHelper.WriteLogsAsync(
                    $"[批量信号播种完成] TaskId={taskId}, ProjectId={projectId}, Stations={stations.Count}, Inserted={totalInserted}, Deleted={totalDeleted}",
                    "批量信号播种");
            }
            catch (Exception ex)
            {
                _fullAnalysisProgressService.Fail(taskId, progress.CurrentStep, progress.StepName, ex.Message);
                await WriteLogHelper.WriteLogsAsync(
                    $"[批量信号播种失败] TaskId={taskId}, Error={ex.Message}\n{ex.StackTrace}",
                    "批量信号播种");
            }
        }

        /// <summary>
        /// 根据符号全路径读取在线值并组装诊断快照。
        /// 当前先完成采样和上下文装配，后续在此基础上接入 AST 求值与停机分类。
        /// </summary>
        public async Task<HttpResponseResultModel<AcquireDiagnosticSnapshotResultDto>> AcquireDiagnosticSnapshotAsync(AcquireDiagnosticSnapshotRequestDto request)
        {
            var response = new HttpResponseResultModel<AcquireDiagnosticSnapshotResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }

            try
            {
                response.BackResult = await _signalSnapshotService.AcquireAsync(request);
                response.IsSuccess = true;
                return response;
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                return response;
            }
        }

        /// <summary>
        /// 读取已持久化 AST，并代入在线快照执行首版布尔求值。
        /// 当前用于单锚点/单 LogicUnit 在线验证与失败路径查看。
        /// </summary>
        public async Task<HttpResponseResultModel<EvaluatePersistedAstResultDto>> EvaluatePersistedAstAsync(EvaluatePersistedAstRequestDto request)
        {
            var response = new HttpResponseResultModel<EvaluatePersistedAstResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }

            try
            {
                response.BackResult = await _runtimeEvaluationService.EvaluateAsync(request);
                response.IsSuccess = true;
                return response;
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                return response;
            }
        }

        /// <summary>
        /// 单工位 GRAPH 诊断调试接口 — 走完整的 DiagnoseStationForLineAsync 流程，
        /// 适合打断点逐步跟踪 LocateStallCandidates → EvaluateStallCandidates → TryCompensateTransitionEvaluation 等关键逻辑。
        /// </summary>
        public async Task<HttpResponseResultModel<object>> DebugDiagnoseStationAsync(
            long projectId,
            string stationCode,
            int maxAnchors = 200,
            bool enableDeviceFocusCheck = false,
            List<string> deviceFocusAnchorTypes = null)
        {
            var response = new HttpResponseResultModel<object>();
            if (projectId <= 0 || string.IsNullOrWhiteSpace(stationCode))
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 和 StationCode 不能为空";
                return response;
            }

            try
            {
                using var signalReadScope = StopCauseSignalReadScope.Begin();
                // 直接调用核心诊断方法 — 可在 DiagnoseStationForLineAsync 内任意位置打断点
                var summary = await DiagnoseStationForLineAsync(
                    projectId, stationCode.Trim(), stationOrder: 0,
                    maxAnchors: maxAnchors,
                    enablePerStationLlm: false,
                    enableDeviceFocusCheck: enableDeviceFocusCheck,
                    deviceFocusAnchorTypes: deviceFocusAnchorTypes);

                response.IsSuccess = true;
                response.BackResult = new
                {
                    summary.ProjectId,
                    summary.StationCode,
                    summary.OverallStatus,
                    summary.DiagnosisStrategy,
                    summary.AnchorCount,
                    summary.EvaluatedCount,
                    summary.FalseAnchorCount,
                    summary.ElapsedMs,
                    summary.TimingBreakdown,
                    EnableDeviceFocusCheck = enableDeviceFocusCheck,
                    DeviceFocusAnchorTypes = enableDeviceFocusCheck
                        ? ResolveLineDeviceFocusAnchorTypes(deviceFocusAnchorTypes).ToList()
                        : new List<string>(),
                    summary.TemplateMatchScore,
                    summary.TemplateMissReason,
                    summary.TemplateMatchInfo,
                    FindingsCount = summary.KeyFindings?.Count ?? 0,
                    GraphFindings = summary.KeyFindings?
                        .Where(f => f.DiagnosisPhase == "GraphTransitionTrace")
                        .Select(f => new
                        {
                            f.VariableName,
                            f.DisplayName,
                            f.EvaluatedValue,
                            f.EvaluationStatus,
                            f.DiagnosisQuality,
                            f.BlockName,
                            f.FailurePathSummary,
                            f.FaultOwnership,
                            f.TraceDepth,
                            f.SignalHistoryAnalysis,
                        }).ToList(),
                    NonGraphFindings = summary.KeyFindings?
                        .Where(f => f.DiagnosisPhase != "GraphTransitionTrace")
                        .Select(f => new
                        {
                            f.VariableName,
                            f.DisplayName,
                            f.EvaluatedValue,
                            f.EvaluationStatus,
                            f.FailurePathSummary,
                            f.FaultOwnership,
                            f.TraceDepth,
                            f.DiagnosisPhase,
                        }).ToList(),
                };
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = $"{ex.Message}\n{ex.StackTrace}";
            }

            return response;
        }

        /// <summary>
        /// AST 深度追踪测试接口 — 验证单个逻辑单元的 AST 求值 + 完整失败路径追踪能力。
        /// 将诊断管线中的 AST 求值 → GRAPH 转移条件检查 → 跨 LogicUnit 写入源追踪 → 信号历史分析
        /// 完整串联起来，用于独立验证追踪逻辑是否正确。
        /// </summary>
        /// <summary>
        /// 独立执行单工位设备关注信号诊断，不依赖 GRAPH 是否激活。
        /// </summary>
        public async Task<HttpResponseResultModel<StationDiagnosisSummaryDto>> DiagnoseDeviceFocusSignalsAsync(DeviceFocusDiagnosisRequestDto request)
        {
            var response = new HttpResponseResultModel<StationDiagnosisSummaryDto>();
            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.StationCode))
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 和 StationCode 不能为空";
                return response;
            }

            try
            {
                var maxCandidates = Math.Clamp(request.MaxCandidates <= 0 ? 20 : request.MaxCandidates, 1, 200);
                var maxFbDepth = Math.Min(
                    Math.Clamp(request.MaxFbPenetrationDepth <= 0 ? 4 : request.MaxFbPenetrationDepth, 1, 12),
                    DeviceFocusMaxFbPenetrationDepth);
                var maxFindings = Math.Clamp(request.MaxFindings <= 0 ? 50 : request.MaxFindings, 1, 200);
                var anchorTypeFilter = ResolveDeviceFocusDiagnosisAnchorTypes(request);
                response.BackResult = await DiagnoseDeviceFocusForStationAsync(
                    request.ProjectId,
                    request.StationCode.Trim(),
                    stationOrder: 0,
                    maxCandidates,
                    maxFbDepth,
                    anchorTypeFilter,
                    maxFindings);
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                response.ExceptionMessage = ex.ToString();
            }

            return response;
        }

        private static HashSet<string> ResolveDeviceFocusDiagnosisAnchorTypes(DeviceFocusDiagnosisRequestDto request)
        {
            var explicitTypes = NormalizeDeviceFocusAnchorTypes(request?.AnchorTypes);

            if (explicitTypes.Count > 0)
                return explicitTypes;

            if (request?.AlarmFaultOnly != false)
                return ResolveDefaultDeviceFocusDiagnosisAnchorTypes();

            return ResolveExpandedDeviceFocusDiagnosisAnchorTypes();
        }

        private static HashSet<string> ResolveLineDeviceFocusAnchorTypes(IEnumerable<string> anchorTypes)
        {
            var explicitTypes = NormalizeDeviceFocusAnchorTypes(anchorTypes);
            return explicitTypes.Count > 0
                ? explicitTypes
                : ResolveDefaultDeviceFocusDiagnosisAnchorTypes();
        }

        private static HashSet<string> NormalizeDeviceFocusAnchorTypes(IEnumerable<string> anchorTypes)
        {
            return (anchorTypes ?? Enumerable.Empty<string>())
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Select(type => type.Trim())
                .Where(type => DeviceFocusAnchorTypes.Contains(type))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> ResolveDefaultDeviceFocusDiagnosisAnchorTypes()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "DeviceHoldState"
            };
        }

        private static HashSet<string> ResolveExpandedDeviceFocusDiagnosisAnchorTypes()
        {
            return new HashSet<string>(
                DeviceFocusAnchorTypes.Where(type => !DeviceFocusExpandedDefaultExcludedTypes.Contains(type)),
                StringComparer.OrdinalIgnoreCase);
        }

        public async Task<HttpResponseResultModel<AstDeepTraceTestResultDto>> AstDeepTraceTestAsync(AstDeepTraceTestRequestDto request)
        {
            var response = new HttpResponseResultModel<AstDeepTraceTestResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }
            if (!request.LogicUnitId.HasValue && string.IsNullOrWhiteSpace(request.VariableName))
            {
                response.IsSuccess = false;
                response.ErrorMessage = "请提供 LogicUnitId 或 VariableName 之一";
                return response;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new AstDeepTraceTestResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode,
            };
            var timings = new Dictionary<string, long>();
            var maxTraceDepth = Math.Clamp(request.MaxTraceDepth, 1, 20);

            try
            {
                // ── 阶段 1: 解析输入，定位目标 AST ──
                var phaseStart = sw.ElapsedMilliseconds;
                PLC_LogicUnitAstEntity targetAst = null;

                if (request.LogicUnitId.HasValue && request.LogicUnitId.Value > 0)
                {
                    // 方式 A: 直接按 LogicUnitId 查找
                    var astList = await _logicUnitAstService.GetListAsync(it =>
                        it.IsDeleted == false
                        && it.ProjectId == request.ProjectId
                        && it.LogicUnitId == request.LogicUnitId.Value);
                    targetAst = astList
                        .OrderByDescending(it => ScoreCompileStatus(it.CompileStatus))
                        .FirstOrDefault();
                    result.InputResolution = targetAst != null
                        ? $"通过 LogicUnitId={request.LogicUnitId} 直接定位，CompileStatus={targetAst.CompileStatus}"
                        : $"LogicUnitId={request.LogicUnitId} 未找到已编译 AST";
                }
                else
                {
                    // 方式 B: 按 VariableName 搜索匹配的 AST
                    var filter = SqlSugar.Expressionable.Create<PLC_LogicUnitAstEntity>()
                        .And(it => it.IsDeleted == false && it.ProjectId == request.ProjectId);
                    if (!string.IsNullOrWhiteSpace(request.StationCode))
                        filter.And(it => it.StationCode == request.StationCode);
                    filter.And(it => it.CompileStatus == "Compiled" || it.CompileStatus == "PartiallyCompiled");

                    var allAstRows = await _logicUnitAstService.GetListAsync(filter.ToExpression());
                    targetAst = FindBestAstOutputMatch(request.VariableName.Trim(), allAstRows);

                    if (targetAst != null)
                    {
                        result.InputResolution = $"通过 VariableName='{request.VariableName}' 匹配到 AST: " +
                            $"LogicUnitId={targetAst.LogicUnitId}, Block={targetAst.BlockName}, " +
                            $"StationCode={targetAst.StationCode}, CompileStatus={targetAst.CompileStatus}";
                    }
                    else
                    {
                        // 尝试模糊匹配（输出变量 JSON 中包含该名称）
                        var fuzzyMatch = allAstRows
                            .Where(it => !string.IsNullOrWhiteSpace(it.OutputVariablesJson)
                                && it.OutputVariablesJson.Contains(request.VariableName.Trim(), StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(it => ScoreCompileStatus(it.CompileStatus))
                            .FirstOrDefault();
                        if (fuzzyMatch != null)
                        {
                            targetAst = fuzzyMatch;
                            result.InputResolution = $"通过 VariableName='{request.VariableName}' 模糊匹配到 AST: " +
                                $"LogicUnitId={targetAst.LogicUnitId}, Block={targetAst.BlockName}";
                        }
                        else
                        {
                            result.InputResolution = $"VariableName='{request.VariableName}' 未匹配到任何 AST";
                        }
                    }
                }
                timings["InputResolve"] = sw.ElapsedMilliseconds - phaseStart;

                if (targetAst == null)
                {
                    result.Messages.Add("未找到目标 AST，流程终止");
                    result.ElapsedMs = sw.ElapsedMilliseconds;
                    result.TimingBreakdown = timings;
                    response.BackResult = result;
                    response.IsSuccess = true;
                    return response;
                }

                // 填充元信息
                result.LogicUnitId = targetAst.LogicUnitId ?? 0;
                result.BlockName = targetAst.BlockName;
                result.StationCode = targetAst.StationCode ?? request.StationCode;
                result.UnitType = targetAst.UnitType;
                result.CompileStatus = targetAst.CompileStatus;
                result.VariableName = request.VariableName ?? targetAst.BlockName;
                result.IsGraphAction = IsGraphRelated(targetAst);

                // ── 阶段 2: AST 求值 ──
                phaseStart = sw.ElapsedMilliseconds;
                var evalRequest = new EvaluatePersistedAstRequestDto
                {
                    ProjectId = request.ProjectId,
                    StationCode = result.StationCode,
                    LogicUnitId = targetAst.LogicUnitId,
                    VariableName = request.VariableName,
                    UseSimulation = request.UseSimulation,
                    SimulatedCurrentValues = request.SimulatedCurrentValues,
                };
                var evalResult = await _runtimeEvaluationService.EvaluateAsync(evalRequest);
                timings["AstEvaluate"] = sw.ElapsedMilliseconds - phaseStart;

                // 填充求值结果
                result.EvaluatedValue = evalResult.EvaluatedValue;
                result.EvaluationStatus = evalResult.EvaluationStatus;
                result.DiagnosisQuality = evalResult.DiagnosisQuality;
                result.UnsupportedNodeCount = evalResult.UnsupportedNodeCount;
                result.UnknownNodeCount = evalResult.UnknownNodeCount;
                result.InputVariables = evalResult.InputVariables;
                result.OutputVariables = evalResult.OutputVariables;
                result.MissingSymbols = evalResult.MissingSymbols;
                result.RootEvaluation = evalResult.RootEvaluation;
                result.FailurePath = evalResult.FailurePath;
                result.Messages.AddRange(evalResult.Messages);

                // ── 阶段 3: GRAPH 转移条件检查（如果是 GRAPH StepAction 且当前值=TRUE）──
                if (result.IsGraphAction && evalResult.EvaluatedValue == true && evalResult.RootEvaluation != null)
                {
                    phaseStart = sw.ElapsedMilliseconds;
                    result.Messages.Add("[GRAPH] 步骤处于激活状态(TRUE)，检查出口转移条件...");

                    var transFindings = ExtractTransitionFailureFindings(
                        evalResult.RootEvaluation, targetAst.BlockName, null, result.StationCode);
                    result.TransitionFindings = transFindings;

                    var falseTransCount = transFindings.Count(f => f.EvaluatedValue == false);
                    result.Messages.Add($"[GRAPH] 转移条件: {transFindings.Count} 个，其中 {falseTransCount} 个为 FALSE");

                    // 对所有转移条件内部的 FALSE 叶子信号做深度追踪
                    var allTransTraces = GetAllTransitionTraces(evalResult.RootEvaluation);
                    if (allTransTraces.Count > 0)
                    {
                        result.Messages.Add($"[GRAPH] 对 {allTransTraces.Count} 个转移条件进行深度追踪...");
                        var allAstRows = await LoadStationAstRowsAsync(request.ProjectId, result.StationCode);
                        var traceSummary = new StationDiagnosisSummaryDto { KeyFindings = new List<StationAnchorFinding>() };
                        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var transTrace in allTransTraces)
                        {
                            await TraceFailureWriteSourcesAsync(
                                request.ProjectId, result.StationCode, allAstRows,
                                transTrace, traceSummary, 200, depth: 1,
                                visitedVariables: visited,
                                diagnosisPhase: "DeepTraceTest-GraphTransition",
                                sharedContext: evalResult.ExecutionContext);
                        }

                        result.DeepTraceFindings.AddRange(traceSummary.KeyFindings);
                        result.VisitedVariables = visited.ToList();
                        result.ActualMaxDepth = traceSummary.KeyFindings.Count > 0
                            ? traceSummary.KeyFindings.Max(f => f.TraceDepth)
                            : 0;
                    }
                    timings["GraphTransitionTrace"] = sw.ElapsedMilliseconds - phaseStart;
                }
                // ── 阶段 3b: 非 GRAPH 或 GRAPH FALSE → 直接追踪失败路径 ──
                else if (evalResult.RootEvaluation != null && evalResult.EvaluatedValue == false)
                {
                    phaseStart = sw.ElapsedMilliseconds;
                    result.Messages.Add("[Trace] 求值结果为 FALSE，开始跨 LogicUnit 深度追踪...");

                    var allAstRows = await LoadStationAstRowsAsync(request.ProjectId, result.StationCode);
                    var traceSummary = new StationDiagnosisSummaryDto { KeyFindings = new List<StationAnchorFinding>() };
                    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    await TraceFailureWriteSourcesAsync(
                        request.ProjectId, result.StationCode, allAstRows,
                        evalResult.RootEvaluation, traceSummary, 200, depth: 1,
                        visitedVariables: visited,
                        diagnosisPhase: "DeepTraceTest-FailurePath",
                        sharedContext: evalResult.ExecutionContext);

                    result.DeepTraceFindings = traceSummary.KeyFindings;
                    result.VisitedVariables = visited.ToList();
                    result.ActualMaxDepth = traceSummary.KeyFindings.Count > 0
                        ? traceSummary.KeyFindings.Max(f => f.TraceDepth)
                        : 0;

                    timings["FailurePathTrace"] = sw.ElapsedMilliseconds - phaseStart;
                }
                else if (evalResult.EvaluatedValue == true)
                {
                    result.Messages.Add("[Info] 求值结果为 TRUE，无失败路径可追踪");
                }
                else
                {
                    result.Messages.Add($"[Info] 求值结果为 {evalResult.EvaluationStatus}，无法进行深度追踪");
                }

                // ── 阶段 4: 信号变化历史分析 ──
                if (request.IncludeSignalHistory && evalResult.FailurePath?.Count > 0)
                {
                    phaseStart = sw.ElapsedMilliseconds;
                    result.SignalHistoryAnalysis = await AnalyzeFailurePathHistoryAsync(
                        request.ProjectId, result.StationCode, evalResult.FailurePath);
                    timings["SignalHistory"] = sw.ElapsedMilliseconds - phaseStart;
                }

                // ── 阶段 5: 生成诊断总结 ──
                result.DiagnosisSummary = BuildDeepTraceTestSummary(result);

                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.TimingBreakdown = timings;
                response.BackResult = result;
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                result.Messages.Add($"[Error] 异常: {ex.Message}");
                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.TimingBreakdown = timings;
                response.BackResult = result;
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
            }

            return response;
        }

        /// <summary>
        /// 加载指定工位的所有已编译 AST 行（用于深度追踪时跨 LogicUnit 查找）。
        /// </summary>
        private async Task<List<PLC_LogicUnitAstEntity>> LoadStationAstRowsAsync(long projectId, string stationCode)
        {
            var filter = SqlSugar.Expressionable.Create<PLC_LogicUnitAstEntity>()
                .And(it => it.IsDeleted == false && it.ProjectId == projectId)
                .And(it => it.CompileStatus == "Compiled" || it.CompileStatus == "PartiallyCompiled");
            if (!string.IsNullOrWhiteSpace(stationCode))
                filter.And(it => it.StationCode == stationCode);

            return await _logicUnitAstService.GetListAsync(filter.ToExpression());
        }

        /// <summary>
        /// 生成深度追踪测试的人可读诊断总结。
        /// </summary>
        private static string BuildDeepTraceTestSummary(AstDeepTraceTestResultDto result)
        {
            var lines = new List<string>();

            lines.Add($"=== AST 深度追踪测试报告 ===");
            lines.Add($"目标: {result.BlockName} (LogicUnitId={result.LogicUnitId})");
            lines.Add($"工位: {result.StationCode ?? "未知"}, 类型: {result.UnitType ?? "未知"}");
            lines.Add($"编译状态: {result.CompileStatus}");
            lines.Add($"输入解析: {result.InputResolution}");
            lines.Add("");

            // 求值结果
            lines.Add($"[求值] 结果={result.EvaluatedValue}, 状态={result.EvaluationStatus}, 质量={result.DiagnosisQuality}");
            if (result.MissingSymbols.Count > 0)
                lines.Add($"[求值] ⚠ 缺失符号({result.MissingSymbols.Count}): {string.Join(", ", result.MissingSymbols.Take(10))}");
            if (result.UnsupportedNodeCount > 0 || result.UnknownNodeCount > 0)
                lines.Add($"[求值] 不支持节点={result.UnsupportedNodeCount}, 未知节点={result.UnknownNodeCount}");

            // 失败路径
            if (result.FailurePath?.Count > 0)
            {
                lines.Add("");
                lines.Add($"[失败路径] 首条失败路径（{result.FailurePath.Count} 个节点）:");
                foreach (var node in result.FailurePath)
                {
                    var display = !string.IsNullOrWhiteSpace(node.DisplayName) ? node.DisplayName : node.VariableName ?? node.NodeId;
                    lines.Add($"  {node.NodeType}: {display} = {node.ActualValue} → {node.Status}");
                }
            }

            // GRAPH 转移条件
            if (result.TransitionFindings.Count > 0)
            {
                lines.Add("");
                lines.Add($"[GRAPH 转移条件] {result.TransitionFindings.Count} 个:");
                foreach (var tf in result.TransitionFindings)
                {
                    lines.Add($"  {tf.DisplayName}: {tf.EvaluationStatus} (Value={tf.EvaluatedValue})");
                    if (!string.IsNullOrWhiteSpace(tf.FailurePathSummary))
                        lines.Add($"    {tf.FailurePathSummary}");
                }
            }

            // 深度追踪
            if (result.DeepTraceFindings.Count > 0)
            {
                lines.Add("");
                lines.Add($"[深度追踪] {result.DeepTraceFindings.Count} 个 Findings, 最大深度={result.ActualMaxDepth}:");
                foreach (var f in result.DeepTraceFindings)
                {
                    var depthPrefix = new string(' ', Math.Min(f.TraceDepth * 2, 20));
                    lines.Add($"  {depthPrefix}[L{f.TraceDepth}] {f.DisplayName}: {f.EvaluationStatus}" +
                        $" (Value={f.EvaluatedValue}, Phase={f.DiagnosisPhase}, Ownership={f.FaultOwnership})");
                    if (!string.IsNullOrWhiteSpace(f.FailurePathSummary))
                        lines.Add($"  {depthPrefix}  → {f.FailurePathSummary}");
                }
            }

            // 信号历史
            if (!string.IsNullOrWhiteSpace(result.SignalHistoryAnalysis))
            {
                lines.Add("");
                lines.Add($"[信号历史] {result.SignalHistoryAnalysis}");
            }

            lines.Add("");
            lines.Add($"耗时: {result.ElapsedMs}ms, 访问变量: {result.VisitedVariables.Count} 个");

            return string.Join("\n", lines);
        }

        public async Task<List<PersistedWriterAstDetailDto>> GetPersistedWriterAstDetailsAsync(PersistedWriterAstQueryDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return new List<PersistedWriterAstDetailDto>();
            }

            return await _astPersistenceService.QueryAsync(request);
        }

        public async Task<QueryPagedResponseModel<PersistedWriterAstDetailDto>> GetPersistedWriterAstDetailsPagedAsync(PersistedWriterAstPagedQueryDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return new QueryPagedResponseModel<PersistedWriterAstDetailDto>
                {
                    Total = 0,
                    Data = new List<PersistedWriterAstDetailDto>()
                };
            }

            return await _astPersistenceService.QueryPagedAsync(request);
        }

        public async Task<StationMapResult> GetPersistedStationMapAsync(PersistedStationMapQueryDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return new StationMapResult();
            }

            return await _stationMapPersistenceService.QueryAsync(request);
        }

        public async Task<QueryPagedResponseModel<PersistedSemanticFindingListItemDto>> GetPersistedSemanticFindingsPagedAsync(PersistedSemanticFindingPagedQueryDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return new QueryPagedResponseModel<PersistedSemanticFindingListItemDto>
                {
                    Total = 0,
                    Data = new List<PersistedSemanticFindingListItemDto>()
                };
            }

            return await _astPersistenceService.QuerySemanticFindingsPagedAsync(request);
        }

        /// <summary>
        /// 查询未分配工位的块（覆盖度诊断）
        /// </summary>
        public async Task<object> GetUnassignedBlocksAsync(DTOS.UnassignedBlocksQueryDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return new { Total = 0, Assigned = 0, Unassigned = 0, Rate = "0%", Data = new List<object>() };
            }

            var allBlocks = await _blockService.GetListAsync(it =>
                it.ProjectId == request.ProjectId && it.IsDeleted == false);

            var assigned = allBlocks.Count(b => !string.IsNullOrWhiteSpace(b.StationCode));
            var unassigned = allBlocks.Count - assigned;
            var rate = allBlocks.Count > 0
                ? $"{(double)assigned / allBlocks.Count * 100:F1}%"
                : "0%";

            // Apply filters on unassigned blocks
            var query = allBlocks.Where(b => string.IsNullOrWhiteSpace(b.StationCode));

            if (!string.IsNullOrWhiteSpace(request.BlockName))
                query = query.Where(b => b.BlockName != null && b.BlockName.Contains(request.BlockName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(request.BlockType))
                query = query.Where(b => b.BlockType != null && b.BlockType.Equals(request.BlockType, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(request.ProgrammingLanguage))
                query = query.Where(b => b.ProgrammingLanguage != null && b.ProgrammingLanguage.Equals(request.ProgrammingLanguage, StringComparison.OrdinalIgnoreCase));

            var filtered = query.ToList();
            var page = Math.Max(1, request.Page);
            var limit = Math.Clamp(request.Limit, 1, 200);

            var pagedData = filtered
                .OrderBy(b => b.BlockType)
                .ThenBy(b => b.BlockName)
                .Skip((page - 1) * limit)
                .Take(limit)
                .Select(b => new
                {
                    b.Id,
                    b.BlockName,
                    b.BlockType,
                    b.BlockNumber,
                    b.ProgrammingLanguage,
                    b.Description,
                    b.IsPotentialEntry,
                    b.CallsOutCount,
                    b.CalledByCount,
                    b.NetworksCount,
                })
                .ToList();

            // Summary by type
            var byType = allBlocks
                .GroupBy(b => b.BlockType ?? "Unknown")
                .Select(g => new
                {
                    BlockType = g.Key,
                    Total = g.Count(),
                    Assigned = g.Count(b => !string.IsNullOrWhiteSpace(b.StationCode)),
                    Unassigned = g.Count(b => string.IsNullOrWhiteSpace(b.StationCode)),
                })
                .OrderByDescending(x => x.Unassigned)
                .ToList();

            return new
            {
                Total = allBlocks.Count,
                Assigned = assigned,
                Unassigned = unassigned,
                Rate = rate,
                FilteredTotal = filtered.Count,
                ByType = byType,
                Data = pagedData,
            };
        }

        public AstSemanticProgressDto GetAstSemanticProgress(string progressKey)
        {
            return _astSemanticProgressService.Get(progressKey) ?? new AstSemanticProgressDto
            {
                Key = progressKey,
                Status = "Idle",
                Message = "未找到语义分析进度"
            };
        }

        // ==================== 工位监控配置 CRUD ====================

        /// <summary>
        /// 直接读取工位监控信号的 IoT 实时值（调试用）
        /// </summary>
        public async Task<HttpResponseResultModel<StationShadowLiveReadResultDto>> LiveReadMonitoringSignalsAsync(StationShadowLiveReadRequestDto request)
        {
            var response = new HttpResponseResultModel<StationShadowLiveReadResultDto>();
            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.StationCode))
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 和 StationCode 不能为空";
                return response;
            }

            var stationCode = request.StationCode.Trim();
            var result = new StationShadowLiveReadResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = stationCode,
                ReadTime = DateTime.Now
            };

            try
            {
                var signalEntities = await _stationShadowSignalService.GetDb().AsQueryable()
                    .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId && it.StationCode == stationCode && it.Enabled == true)
                    .OrderBy(it => it.SortOrder, SqlSugar.OrderByType.Asc)
                    .ToListAsync();

                if (signalEntities.Count == 0)
                {
                    result.Warnings.Add("当前工位没有启用的监控信号");
                    response.IsSuccess = true;
                    response.BackResult = result;
                    return response;
                }

                var symbolPaths = signalEntities
                    .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                    .Select(it => it.VariableName.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var snapshotResult = await _signalSnapshotService.AcquireAsync(new AcquireDiagnosticSnapshotRequestDto
                {
                    ProjectId = request.ProjectId,
                    StationCode = stationCode,
                    SymbolPaths = symbolPaths,
                    IncludeStationAnchorVariables = false
                });

                result.DeviceName = snapshotResult.DeviceName;
                result.FallbackDeviceName = snapshotResult.FallbackDeviceName;

                var currentValues = snapshotResult.ExecutionContext?.CurrentValues ?? new Dictionary<string, object>();
                var signalQuality = snapshotResult.ExecutionContext?.SignalQuality ?? new Dictionary<string, string>();

                foreach (var entity in signalEntities)
                {
                    var varName = (entity.VariableName ?? "").Trim();
                    var normalizedKey = varName.Trim('"').Replace("\"", "").Replace("IQ.", "", StringComparison.OrdinalIgnoreCase);

                    currentValues.TryGetValue(normalizedKey, out var value);
                    signalQuality.TryGetValue(normalizedKey, out var quality);

                    result.Signals.Add(new StationShadowLiveReadItemDto
                    {
                        SignalKey = entity.SignalKey?.Trim(),
                        VariableName = varName,
                        AnchorType = entity.AnchorType?.Trim(),
                        Value = value,
                        IsGood = string.Equals(quality, "Good", StringComparison.OrdinalIgnoreCase),
                        Quality = quality ?? "Missing"
                    });
                }

                result.TotalCount = result.Signals.Count;
                result.GoodCount = result.Signals.Count(s => s.IsGood);
                result.BadCount = result.Signals.Count(s => !s.IsGood);

                if (snapshotResult.MissingSymbols?.Count > 0)
                {
                    result.Warnings.Add($"有 {snapshotResult.MissingSymbols.Count} 个变量未能从 IoT 读取: {string.Join(", ", snapshotResult.MissingSymbols.Take(10))}");
                }

                response.IsSuccess = true;
                response.BackResult = result;
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                response.ExceptionMessage = ex.ToString();
            }

            return response;
        }

        /// <summary>
        /// 批量播种：枚举项目内所有工位，逐一执行 SeedMonitoringSignalsAsync。
        /// </summary>
        public async Task<HttpResponseResultModel<BatchSeedSignalsResultDto>> BatchSeedMonitoringSignalsAsync(long projectId, bool replaceExisting)
        {
            var response = new HttpResponseResultModel<BatchSeedSignalsResultDto>();
            if (projectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }

            // 从锚点绑定表获取所有工位
            var stations = await _stationAnchorBindingService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == projectId && !string.IsNullOrEmpty(it.StationCode))
                .Select(it => it.StationCode)
                .Distinct()
                .ToListAsync();

            if (stations.Count == 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "该项目没有任何锚点绑定数据，请先执行 Q 点提取";
                return response;
            }

            var result = new BatchSeedSignalsResultDto { ProjectId = projectId };
            foreach (var station in stations.OrderBy(s => s))
            {
                var singleResult = await SeedMonitoringSignalsAsync(new StationShadowSeedSignalsRequestDto
                {
                    ProjectId = projectId,
                    StationCode = station,
                    ReplaceExisting = replaceExisting
                });

                var detail = new BatchSeedStationDetail { StationCode = station };
                if (singleResult.IsSuccess && singleResult.BackResult != null)
                {
                    detail.InsertedCount = singleResult.BackResult.InsertedCount;
                    detail.DeletedCount = singleResult.BackResult.DeletedCount;
                    detail.AnchorBindingCount = singleResult.BackResult.AnchorBindingCount;
                    detail.Success = true;
                }
                else
                {
                    detail.Success = false;
                    detail.ErrorMessage = singleResult.ErrorMessage;
                }
                result.StationDetails.Add(detail);
            }

            result.TotalStations = result.StationDetails.Count;
            result.SuccessStations = result.StationDetails.Count(d => d.Success);
            result.TotalInserted = result.StationDetails.Sum(d => d.InsertedCount);
            result.TotalDeleted = result.StationDetails.Sum(d => d.DeletedCount);

            // 播种完成后，离线推导 GraphAction 激活规则并持久化到 activation_rule 字段
            // 失败不影响主流程返回
            try
            {
                await InferAndPersistGraphActivationRulesAsync(projectId);
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[GraphActivationRule] 批量播种后推导失败（不影响信号入库）: {ex.Message}",
                    "批量信号播种");
            }

            response.IsSuccess = true;
            response.BackResult = result;
            return response;
        }

        /// <summary>
        /// 从锚点绑定表自动播种监控信号到 ai_plc_station_shadow_signal
        /// </summary>
        public async Task<HttpResponseResultModel<StationShadowSeedSignalsResultDto>> SeedMonitoringSignalsAsync(StationShadowSeedSignalsRequestDto request)
        {
            var response = new HttpResponseResultModel<StationShadowSeedSignalsResultDto>();
            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.StationCode))
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 和 StationCode 不能为空";
                return response;
            }

            var stationCode = request.StationCode.Trim();
            var result = new StationShadowSeedSignalsResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = stationCode
            };

            // 1. 加载锚点绑定
            var bindings = await _stationAnchorBindingService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == request.ProjectId
                && it.StationCode == stationCode
                && !string.IsNullOrEmpty(it.AnchorKey));
            result.AnchorBindingCount = bindings.Count;

            if (bindings.Count == 0)
            {
                // 锚点绑定缺失只影响锚点信号段；EXEC_BITS 候补段自成一体（依赖 block.stationcode + STEP_x_ACTION LU），
                // 不应被此前置挡住 —— 继续走 GRAPH EXEC_BITS 追加播种。
                result.Warnings.Add($"工位 {stationCode} 没有锚点绑定数据（跳过锚点信号，仅播种 GRAPH EXEC_BITS 候补）；如需完整信号请先执行一键分析并入库");
            }

            // 2. 查已有信号
            var existingSignals = await _stationShadowSignalService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId && it.StationCode == stationCode)
                .ToListAsync();
            result.ExistingSignalCount = existingSignals.Count;
            var existingKeys = new HashSet<string>(existingSignals.Select(s => s.SignalKey?.Trim() ?? ""), StringComparer.OrdinalIgnoreCase);
            // 同时按 variable_name 去重，避免同一变量被多个 AnchorKey 重复监控
            var existingVarNames = new HashSet<string>(
                existingSignals.Where(s => !string.IsNullOrWhiteSpace(s.VariableName)).Select(s => s.VariableName.Trim()),
                StringComparer.OrdinalIgnoreCase);

            // 3. 若 ReplaceExisting，先软删旧信号
            if (request.ReplaceExisting && existingSignals.Count > 0)
            {
                await _stationShadowSignalService.UpdateAsync(
                    it => new PLC_StationShadowSignalEntity { IsDeleted = true, ModifyTime = DateTime.Now },
                    it => it.IsDeleted == false && it.ProjectId == request.ProjectId && it.StationCode == stationCode);
                result.DeletedCount = existingSignals.Count;
                existingKeys.Clear();
                existingVarNames.Clear();
            }

            // 4. 按锚点绑定生成信号
            var now = DateTime.Now;
            int sortOrder = 0;
            var toInsert = new List<PLC_StationShadowSignalEntity>();

            // 预加载工位 AST 行，用于反查 LogicUnitId
            var stationAstRowsForSeed = await LoadStationAstRowsAsync(request.ProjectId, stationCode);

            // 按 AnchorType 分组排序：GraphAction → PhysicalAction → VirtualAction → HandshakeState → 其它
            var anchorTypePriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [GraphActionAnchorType] = 1,
                ["PhysicalAction"] = 2,
                ["VirtualAction"] = 3,
                ["HandshakeState"] = 4
            };

            var orderedBindings = bindings
                .OrderBy(b => anchorTypePriority.TryGetValue(b.AnchorType ?? "", out var p) ? p : 9)
                .ThenByDescending(b => b.Confidence ?? 0m)
                .ThenBy(b => b.AnchorKey)
                .ToList();

            foreach (var binding in orderedBindings)
            {
                var signalKey = binding.AnchorKey.Trim();
                if (existingKeys.Contains(signalKey))
                    continue;

                var variableName = binding.VariableName?.Trim();
                var anchorType = binding.AnchorType?.Trim() ?? "";
                var isGraph = anchorType.Equals(GraphActionAnchorType, StringComparison.OrdinalIgnoreCase);
                var isPhysical = anchorType.Equals("PhysicalAction", StringComparison.OrdinalIgnoreCase);
                var isVirtual = anchorType.Equals("VirtualAction", StringComparison.OrdinalIgnoreCase);
                var isHandshake = anchorType.Equals("HandshakeState", StringComparison.OrdinalIgnoreCase);

                // GraphAction 允许同一变量出现多次（不同 Step 对应不同 LogicUnitId），
                // 其他类型按变量名去重，避免重复监控同一变量
                if (!isGraph && !string.IsNullOrWhiteSpace(variableName) && existingVarNames.Contains(variableName))
                    continue;

                existingKeys.Add(signalKey);
                if (!string.IsNullOrWhiteSpace(variableName))
                    existingVarNames.Add(variableName);
                sortOrder++;

                // LogicUnitId 获取策略：
                // 1. GraphAction：优先从 AnchorKey 内嵌解析（格式 "graph:{varName}:lu:{id}"）
                // 2. 解析失败或非 GraphAction：fallback 到 FindBestAstOutputMatch 反查
                long? resolvedLogicUnitId = null;
                if (isGraph)
                {
                    const string luMarker = ":lu:";
                    var luIdx = signalKey.LastIndexOf(luMarker, StringComparison.Ordinal);
                    if (luIdx >= 0 && long.TryParse(signalKey[(luIdx + luMarker.Length)..], out var parsedLu))
                        resolvedLogicUnitId = parsedLu;
                }
                if (!resolvedLogicUnitId.HasValue && !string.IsNullOrWhiteSpace(variableName))
                {
                    var matchedAst = FindBestAstOutputMatch(variableName, stationAstRowsForSeed);
                    resolvedLogicUnitId = matchedAst?.LogicUnitId;
                }

                toInsert.Add(new PLC_StationShadowSignalEntity
                {
                    ProjectId = request.ProjectId,
                    StationCode = stationCode,
                    SignalKey = signalKey,
                    VariableName = variableName,
                    SignalType = anchorType,
                    AnchorType = anchorType,
                    IsCore = isGraph || isPhysical || isVirtual,
                    IsContext = isHandshake,
                    IsWatchOnly = false,
                    ParticipateInPattern = true,
                    Priority = isGraph ? 1 : isPhysical ? 2 : isVirtual ? 3 : isHandshake ? 4 : 6,
                    SortOrder = sortOrder,
                    LogicUnitId = resolvedLogicUnitId,
                    Enabled = true,
                    IsDeleted = false,
                    CreateTime = now,
                    ModifyTime = now
                });
            }

            sortOrder = await AppendGraphExecBitShadowSignalsAsync(
                request.ProjectId,
                stationCode,
                stationAstRowsForSeed,
                existingKeys,
                existingVarNames,
                toInsert,
                request.ReplaceExisting ? new List<PLC_StationShadowSignalEntity>() : existingSignals,
                now,
                sortOrder);

            if (toInsert.Count > 0)
            {
                await _stationShadowSignalService.BulkCopyAsync(toInsert);
            }
            result.InsertedCount = toInsert.Count;

            if (toInsert.Count == 0 && !request.ReplaceExisting && bindings.Count > 0)
            {
                result.Warnings.Add("所有锚点绑定已存在对应监控信号，无新增");
            }

            // 5. 自动创建监控配置（若不存在）
            var existingConfig = await _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId && it.StationCode == stationCode)
                .FirstAsync();

            if (existingConfig == null)
            {
                await _stationShadowConfigService.InsertAsync(new PLC_StationShadowConfigEntity
                {
                    ProjectId = request.ProjectId,
                    StationCode = stationCode,
                    Enabled = true,
                    CoreIntervalMs = 1000,
                    ContextIntervalMs = 3000,
                    BundleWindowMs = 5000,
                    SilenceThresholdSeconds = 30,
                    SnapshotTtlSeconds = 86400,
                    HistoryRetentionDays = 3,
                    Remark = "由 seed-signals 自动创建",
                    IsDeleted = false,
                    CreateTime = now,
                    ModifyTime = now
                });
                result.ConfigCreated = true;
                result.Warnings.Add("已自动创建工位监控配置（Enabled = true）");
            }
            else if (existingConfig.Enabled != true)
            {
                result.Warnings.Add("工位监控配置存在但已禁用，请在配置面板中启用");
            }

            response.IsSuccess = true;
            response.BackResult = result;
            return response;
        }

        private async Task<int> AppendGraphExecBitShadowSignalsAsync(
            long projectId,
            string stationCode,
            List<PLC_LogicUnitAstEntity> stationAstRowsForSeed,
            HashSet<string> existingKeys,
            HashSet<string> existingVarNames,
            List<PLC_StationShadowSignalEntity> toInsert,
            List<PLC_StationShadowSignalEntity> existingSignalsForRole,
            DateTime now,
            int sortOrder)
        {
            var blockIds = new HashSet<long>(
                (stationAstRowsForSeed ?? new List<PLC_LogicUnitAstEntity>())
                    .Where(row => row.BlockId.HasValue)
                    .Select(row => row.BlockId!.Value));

            var stationBlocks = await _blockService.GetListAsync(block =>
                block.IsDeleted == false
                && block.ProjectId == projectId
                && block.StationCode == stationCode);
            foreach (var block in stationBlocks.Where(block => block?.Id > 0))
            {
                blockIds.Add(block.Id);
            }

            // FB→实例DB 关系取自 db 解析的权威绑定表（ai_plc_dbfbinding，源自博图 InstanceDB XML 的 InstanceOfName，
            // 上传解析时随 DB 块写入），与 GraphStepTagRegistrationJob 注册台账同源。
            // 不做 FB→DB 命名推断：同 FB 多实例（如 FB6405_AxisCombo → DB115/116/.../716_AxisCombo 共 8 个实例）
            // 或异名实例 DB 场景下命名推断会错；绑定表无记录则跳过该块（宁缺毋错，需重传含 InstanceDB 的导出）。
            var graphDbBindings = await _dbFbBindingService.GetListAsync(binding =>
                binding.IsDeleted == false
                && binding.ProjectId == projectId
                && !string.IsNullOrWhiteSpace(binding.InstanceOfName)
                && !string.IsNullOrWhiteSpace(binding.DbName));
            var allowedStations = await _stationPlcScopeService.GetAllowedStationsAsync(projectId);

            // 2026-10-14：实例 DB 名写着本工位的（如 DB310_OP030_Graph），即使其模板 FB 的工位是别的工位，也算本工位
            var fbNamesOwnedByInstanceName = graphDbBindings
                .Where(binding => StationBlockBindingRules.IsSameStation(
                    StationBlockBindingRules.ResolveInstanceStation(binding.DbName, null, allowedStations), stationCode))
                .Select(binding => binding.InstanceOfName!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (fbNamesOwnedByInstanceName.Count > 0)
            {
                var ownedFbBlocks = await _blockService.GetListAsync(block =>
                    block.IsDeleted == false
                    && block.ProjectId == projectId
                    && fbNamesOwnedByInstanceName.Contains(block.BlockName));
                foreach (var block in ownedFbBlocks.Where(block => block?.Id > 0))
                {
                    blockIds.Add(block.Id);
                }
            }

            if (blockIds.Count == 0)
                return sortOrder;

            var blockIdList = blockIds.ToList();
            var allBlocks = await _blockService.GetListAsync(block =>
                block.IsDeleted == false
                && block.ProjectId == projectId
                && blockIdList.Contains(block.Id));
            var blockById = allBlocks
                .GroupBy(block => block.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var allLogicUnits = await _logicUnitService.GetListAsync(unit =>
                unit.IsDeleted == false
                && unit.BlockId.HasValue
                && blockIdList.Contains(unit.BlockId.Value));

            var stepUnits = allLogicUnits
                .Where(unit => IsGraphStepAction(unit.UnitType))
                .Where(unit => unit.BlockId.HasValue)
                .ToList();
            if (stepUnits.Count == 0)
                return sortOrder;

            var dbNamesByFbName = graphDbBindings
                .GroupBy(binding => binding.InstanceOfName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(binding => binding.DbName!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var roleByLogicUnitId = (existingSignalsForRole ?? new List<PLC_StationShadowSignalEntity>())
                .Concat(toInsert)
                .Where(signal => signal != null
                    && signal.LogicUnitId.HasValue
                    && string.Equals(signal.AnchorType?.Trim(), GraphActionAnchorType, StringComparison.OrdinalIgnoreCase))
                .GroupBy(signal => signal.LogicUnitId!.Value)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var blockGroup in stepUnits
                .GroupBy(unit => unit.BlockId!.Value)
                .OrderBy(group => blockById.TryGetValue(group.Key, out var block) ? block.BlockName : string.Empty))
            {
                if (!blockById.TryGetValue(blockGroup.Key, out var block)
                    || string.IsNullOrWhiteSpace(block.BlockName))
                    continue;

                if (!dbNamesByFbName.TryGetValue(block.BlockName.Trim(), out var instanceDbNames)
                    || instanceDbNames.Count == 0)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[SeedMonitoringSignals] {stationCode} GRAPH EXEC_BITS 播种跳过：Block={block.BlockName} 在绑定表(ai_plc_dbfbinding)无实例DB记录，不做命名推断（需重传含 InstanceDB 的导出）",
                        "StopCauseDiagnosis/SeedSignals");
                    continue;
                }

                // 2026-10-14：同一模板 FB 的多个实例 DB 按各自名字归工位，只播属于本工位的实例；
                // 名字里没有工位号的沿用 FB 的工位，FB 也没有工位时保持原样（都播）
                var ownInstanceDbNames = instanceDbNames
                    .Where(dbName =>
                    {
                        var owner = StationBlockBindingRules.ResolveInstanceStation(dbName, block.StationCode, allowedStations);
                        return owner == null || StationBlockBindingRules.IsSameStation(owner, stationCode);
                    })
                    .ToList();
                if (ownInstanceDbNames.Count < instanceDbNames.Count)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[SeedMonitoringSignals] {stationCode} GRAPH EXEC_BITS 按实例归属跳过：Block={block.BlockName} " +
                        $"跳过=[{string.Join(",", instanceDbNames.Except(ownInstanceDbNames, StringComparer.OrdinalIgnoreCase))}]",
                        "StopCauseDiagnosis/SeedSignals");
                }
                if (ownInstanceDbNames.Count == 0)
                {
                    continue;
                }
                instanceDbNames = ownInstanceDbNames;

                // 位下标严格用「步号-1」（与 GraphStepTagRegistrationJob 注册台账 EXEC_BITS[n-1] 一致），
                // 缺号/跳号不会移位；同一 DB 内重复步号按首次出现去重（existingVarNames 兜底）。
                // 同 FB 多实例 DB（如 AxisCombo）逐实例各播一组，signalKey/variableName 天然按实例 DB 隔离。
                var orderedSteps = blockGroup
                    .Select(unit => new { Unit = unit, StepNo = ResolveGraphStepNumber(unit.UnitType) })
                    .Where(item => item.StepNo.HasValue)
                    .OrderBy(item => item.StepNo!.Value)
                    .ThenBy(item => item.Unit.NetworkId ?? int.MaxValue)
                    .ThenBy(item => item.Unit.Id)
                    .ToList();

                foreach (var runtimeDbName in instanceDbNames)
                {
                foreach (var stepItem in orderedSteps)
                {
                    var stepUnit = stepItem.Unit;
                    var stepNo = stepItem.StepNo!.Value;
                    if (stepNo < 1 || stepNo > GraphExecBitMaxCount)
                    {
                        await WriteLogHelper.WriteLogsAsync(
                            $"[SeedMonitoringSignals] {stationCode} GRAPH EXEC_BITS 播种跳过：Block={block.BlockName} S{stepNo} 超出数组容量（1..{GraphExecBitMaxCount}）",
                            "StopCauseDiagnosis/SeedSignals");
                        continue;
                    }

                    var bitIndex = stepNo - 1;
                    var variableName = $"{runtimeDbName}.RT_DATA.EXEC_BITS.EXEC_BITS[{bitIndex}]";
                    var signalKey = $"graph-execbits:{runtimeDbName}:step:{stepNo}:lu:{stepUnit.Id}";
                    if (existingKeys.Contains(signalKey)
                        || existingVarNames.Contains(variableName)
                        || toInsert.Any(signal => string.Equals(signal.VariableName, variableName, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    existingKeys.Add(signalKey);
                    existingVarNames.Add(variableName);
                    sortOrder++;

                    roleByLogicUnitId.TryGetValue(stepUnit.Id, out var roleSignal);
                    var title = string.IsNullOrWhiteSpace(stepUnit.Title)
                        ? stepUnit.UnitType
                        : stepUnit.Title.Trim();
                    var semanticLabel = $"[EXEC_BITS候补] {block.BlockName}@{runtimeDbName} S{stepNo}: {title}";

                    toInsert.Add(new PLC_StationShadowSignalEntity
                    {
                        ProjectId = projectId,
                        StationCode = stationCode,
                        SignalKey = signalKey,
                        VariableName = variableName,
                        SignalType = GraphExecBitAnchorType,
                        AnchorType = GraphExecBitAnchorType,
                        GroupName = GraphExecBitGroupName,
                        IsCore = true,
                        IsContext = false,
                        IsWatchOnly = false,
                        ParticipateInPattern = true,
                        Priority = 5,
                        SortOrder = sortOrder,
                        LogicUnitId = stepUnit.Id,
                        SemanticLabel = semanticLabel,
                        SemanticCategory = "GraphRuntimeStep",
                        ActivationRule = "BOOL_TRUE",
                        GraphStepRole = roleSignal?.GraphStepRole,
                        GraphStepRoleConfidence = roleSignal?.GraphStepRoleConfidence,
                        GraphStepRoleReason = roleSignal?.GraphStepRoleReason,
                        Remark = $"GRAPH EXEC_BITS fallback. RuntimeDb={runtimeDbName}, StepNo={stepNo}, ExecBitIndex={bitIndex}, StepUnitType={stepUnit.UnitType}",
                        Enabled = true,
                        IsDeleted = false,
                        CreateTime = now,
                        ModifyTime = now
                    });
                }
                }
            }

            return sortOrder;
        }

        public async Task<List<StationShadowConfigItemDto>> GetMonitoringConfigsAsync(StationShadowConfigQueryDto query)
        {
            var q = _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false);

            if (query.ProjectId.HasValue && query.ProjectId.Value > 0)
                q = q.Where(it => it.ProjectId == query.ProjectId.Value);

            if (!string.IsNullOrWhiteSpace(query.StationCode))
                q = q.Where(it => it.StationCode == query.StationCode.Trim());

            if (query.Enabled.HasValue)
                q = q.Where(it => it.Enabled == query.Enabled.Value);

            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                var kw = query.Keyword.Trim();
                q = q.Where(it => it.StationCode.Contains(kw) || it.Remark.Contains(kw));
            }

            var limit = query.Limit > 0 ? Math.Min(query.Limit, 500) : 100;
            var entities = await q
                .OrderBy(it => it.ProjectId, OrderByType.Asc)
                .OrderBy(it => it.StationCode, OrderByType.Asc)
                .Take(limit)
                .ToListAsync();

            return entities.Select(MapConfigEntityToDto).ToList();
        }

        public async Task<HttpResponseResultModel<StationShadowConfigItemDto>> CreateMonitoringConfigAsync(StationShadowConfigCreateDto dto)
        {
            var response = new HttpResponseResultModel<StationShadowConfigItemDto>();
            if (dto == null || dto.ProjectId <= 0 || string.IsNullOrWhiteSpace(dto.StationCode))
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 和 StationCode 不能为空";
                return response;
            }

            var stationCode = dto.StationCode.Trim();
            var exists = await _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == dto.ProjectId && it.StationCode == stationCode)
                .AnyAsync();
            if (exists)
            {
                response.IsSuccess = false;
                response.ErrorMessage = $"工位 {stationCode} 已存在监控配置";
                return response;
            }

            var entity = new PLC_StationShadowConfigEntity
            {
                ProjectId = dto.ProjectId,
                StationCode = stationCode,
                Enabled = dto.Enabled,
                StationModeFilterJson = dto.StationModeFilterJson,
                CoreIntervalMs = dto.CoreIntervalMs,
                ContextIntervalMs = dto.ContextIntervalMs,
                BundleWindowMs = dto.BundleWindowMs,
                SilenceThresholdSeconds = dto.SilenceThresholdSeconds,
                SnapshotTtlSeconds = dto.SnapshotTtlSeconds,
                HistoryRetentionDays = dto.HistoryRetentionDays,
                PatternRebuildCron = dto.PatternRebuildCron,
                Remark = dto.Remark,
                IsDeleted = false,
                CreateTime = DateTime.Now,
                ModifyTime = DateTime.Now
            };

            var inserted = await _stationShadowConfigService.InsertAsync(entity);
            if (!inserted)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "写入数据库失败";
                return response;
            }

            response.IsSuccess = true;
            response.BackResult = MapConfigEntityToDto(entity);
            return response;
        }

        public async Task<HttpResponseResultModel<StationShadowConfigItemDto>> UpdateMonitoringConfigAsync(StationShadowConfigUpdateDto dto)
        {
            var response = new HttpResponseResultModel<StationShadowConfigItemDto>();
            if (dto == null || dto.Id <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "配置 Id 不能为空";
                return response;
            }

            var entity = await _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.Id == dto.Id)
                .FirstAsync();
            if (entity == null)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "未找到对应配置";
                return response;
            }

            entity.Enabled = dto.Enabled;
            entity.StationModeFilterJson = dto.StationModeFilterJson;
            entity.CoreIntervalMs = dto.CoreIntervalMs;
            entity.ContextIntervalMs = dto.ContextIntervalMs;
            entity.BundleWindowMs = dto.BundleWindowMs;
            entity.SilenceThresholdSeconds = dto.SilenceThresholdSeconds;
            entity.SnapshotTtlSeconds = dto.SnapshotTtlSeconds;
            entity.HistoryRetentionDays = dto.HistoryRetentionDays;
            entity.PatternRebuildCron = dto.PatternRebuildCron;
            entity.Remark = dto.Remark;
            entity.ModifyTime = DateTime.Now;

            await _stationShadowConfigService.UpdateAsync(entity);
            response.IsSuccess = true;
            response.BackResult = MapConfigEntityToDto(entity);
            return response;
        }

        public async Task<HttpResponseResultModel<bool>> DeleteMonitoringConfigAsync(long id)
        {
            var response = new HttpResponseResultModel<bool>();
            if (id <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "Id 不能为空";
                return response;
            }

            var deleted = await _stationShadowConfigService.SoftDeleteByIdAsync(id);
            response.IsSuccess = deleted;
            response.BackResult = deleted;
            if (!deleted)
                response.ErrorMessage = "未找到对应配置或已删除";
            return response;
        }

        public async Task<HttpResponseResultModel<StationShadowConfigItemDto>> ToggleMonitoringConfigAsync(long id, bool enabled)
        {
            var response = new HttpResponseResultModel<StationShadowConfigItemDto>();
            var entity = await _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.Id == id)
                .FirstAsync();
            if (entity == null)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "未找到对应配置";
                return response;
            }

            entity.Enabled = enabled;
            entity.ModifyTime = DateTime.Now;
            await _stationShadowConfigService.UpdateAsync(entity);
            response.IsSuccess = true;
            response.BackResult = MapConfigEntityToDto(entity);
            return response;
        }

        private static StationShadowConfigItemDto MapConfigEntityToDto(PLC_StationShadowConfigEntity entity)
        {
            return new StationShadowConfigItemDto
            {
                Id = entity.Id,
                ProjectId = entity.ProjectId,
                StationCode = entity.StationCode,
                Enabled = entity.Enabled ?? false,
                StationModeFilterJson = entity.StationModeFilterJson,
                CoreIntervalMs = entity.CoreIntervalMs,
                ContextIntervalMs = entity.ContextIntervalMs,
                BundleWindowMs = entity.BundleWindowMs,
                SilenceThresholdSeconds = entity.SilenceThresholdSeconds,
                SnapshotTtlSeconds = entity.SnapshotTtlSeconds,
                HistoryRetentionDays = entity.HistoryRetentionDays,
                PatternRebuildCron = entity.PatternRebuildCron,
                LastPatternRebuildTime = entity.LastPatternRebuildTime,
                Remark = entity.Remark,
                CreateTime = entity.CreateTime,
                ModifyTime = entity.ModifyTime
            };
        }

        public async Task<List<StationShadowStationListItemDto>> GetMonitoringStationsAsync(long projectId)
        {
            if (projectId <= 0)
            {
                return new List<StationShadowStationListItemDto>();
            }

            // 从三个来源合并工位列表（Redis、配置表、锚点绑定），而非严格降级
            var stationCodeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var redisStations = await _stationShadowStateCacheService.GetProjectStationsAsync(projectId);
            foreach (var s in redisStations.Where(it => !string.IsNullOrWhiteSpace(it)))
                stationCodeSet.Add(s.Trim());

            var configEntities = await _stationShadowConfigService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == projectId)
                .ToListAsync();
            foreach (var c in configEntities.Where(it => !string.IsNullOrWhiteSpace(it.StationCode)))
                stationCodeSet.Add(c.StationCode.Trim());

            var anchorBindingStations = (await _stationAnchorBindingService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == projectId &&
                    !string.IsNullOrWhiteSpace(it.StationCode)))
                .Select(it => it.StationCode.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var a in anchorBindingStations)
                stationCodeSet.Add(a);

            var stationCodes = stationCodeSet.OrderBy(it => it).ToList();

            var configMap = configEntities
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode))
                .GroupBy(it => it.StationCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(it => it.Key, it => it.OrderByDescending(x => x.ModifyTime ?? x.CreateTime).First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<StationShadowStationListItemDto>();
            foreach (var stationCode in stationCodes.Where(it => !string.IsNullOrWhiteSpace(it)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(it => it))
            {
                var normalizedStationCode = stationCode.Trim();
                var state = await _stationShadowStateCacheService.GetStationStateAsync(projectId, normalizedStationCode);
                var runtimeLease = await _stationShadowStateCacheService.GetRuntimeLeaseAsync(projectId, normalizedStationCode);
                configMap.TryGetValue(normalizedStationCode, out var config);

                result.Add(new StationShadowStationListItemDto
                {
                    ProjectId = projectId,
                    StationCode = normalizedStationCode,
                    Enabled = config?.Enabled ?? false,
                    Status = state?.Status,
                    StationMode = state?.StationMode,
                    IsSilent = state?.IsSilent ?? false,
                    SilentSeconds = state?.SilentSeconds ?? 0,
                    LastSampleTime = state?.LastSampleTime,
                    LastBundleTime = state?.LastBundleTime,
                    LastEventId = state?.LastEventId,
                    LastSnapshotId = state?.LastSnapshotId,
                    CoreSignalCount = state?.CoreSignalCount ?? 0,
                    ContextSignalCount = state?.ContextSignalCount ?? 0,
                    RuntimeStatus = runtimeLease?.Status,
                    RuntimeNodeName = runtimeLease?.NodeName,
                    RuntimeHeartbeat = runtimeLease?.LastHeartbeat,
                    CoreIntervalMs = config?.CoreIntervalMs,
                    ContextIntervalMs = config?.ContextIntervalMs,
                    BundleWindowMs = config?.BundleWindowMs,
                    SilenceThresholdSeconds = config?.SilenceThresholdSeconds
                });
            }

            return result;
        }

        /// <summary>
        /// 获取所有工位的最近信号变化（用于产线信号总览看板）
        /// </summary>
        public async Task<List<StationChangeSummaryDto>> GetAllStationRecentChangesAsync(long projectId, int changesPerStation = 20)
        {
            if (projectId <= 0) return new List<StationChangeSummaryDto>();

            changesPerStation = Math.Clamp(changesPerStation, 5, 100);
            var stations = await GetMonitoringStationsAsync(projectId);
            var result = new List<StationChangeSummaryDto>();

            foreach (var station in stations)
            {
                var changes = await _stationShadowStateCacheService.GetRecentChangesAsync(
                    projectId, station.StationCode, changesPerStation);

                result.Add(new StationChangeSummaryDto
                {
                    StationCode = station.StationCode,
                    Enabled = station.Enabled,
                    Status = station.Status,
                    StationMode = station.StationMode,
                    IsSilent = station.IsSilent,
                    LastSampleTime = station.LastSampleTime,
                    CoreSignalCount = station.CoreSignalCount,
                    ContextSignalCount = station.ContextSignalCount,
                    RecentChanges = changes
                });
            }

            return result;
        }

        public async Task<StationShadowOverviewDto> GetMonitoringStationOverviewAsync(StationShadowQueryDto request)
        {
            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.StationCode))
            {
                return new StationShadowOverviewDto();
            }

            var stationCode = request.StationCode.Trim();
            var state = await _stationShadowStateCacheService.GetStationStateAsync(request.ProjectId, stationCode);
            var runtimeLease = await _stationShadowStateCacheService.GetRuntimeLeaseAsync(request.ProjectId, stationCode);
            var signals = await _stationShadowStateCacheService.GetSignalMirrorsAsync(request.ProjectId, stationCode);
            var latestEvent = await GetLatestMonitoringEventAsync(request.ProjectId, stationCode);

            StationShadowSnapshotCacheModel snapshot = null;
            if (!string.IsNullOrWhiteSpace(state?.LastSnapshotId))
            {
                snapshot = await _stationShadowStateCacheService.GetSnapshotAsync(request.ProjectId, stationCode, state.LastSnapshotId);
            }

            return new StationShadowOverviewDto
            {
                ProjectId = request.ProjectId,
                StationCode = stationCode,
                HasState = state != null,
                HasRuntimeLease = runtimeLease != null,
                HasSnapshot = snapshot != null,
                SignalMirrorCount = signals.Count,
                GeneratedAt = DateTime.Now,
                State = state,
                RuntimeLease = runtimeLease,
                LatestEvent = latestEvent,
                Snapshot = snapshot
            };
        }

        public async Task<List<StationShadowSignalCacheModel>> GetMonitoringSignalsAsync(StationShadowSignalQueryDto request)
        {
            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.StationCode))
            {
                return new List<StationShadowSignalCacheModel>();
            }

            var stationCode = request.StationCode.Trim();
            var limit = NormalizeLimit(request.Limit, 500, 2000);
            var query = (await _stationShadowStateCacheService.GetSignalMirrorsAsync(request.ProjectId, stationCode)).Values.AsEnumerable();

            if (request.OnlyCore.HasValue)
            {
                query = query.Where(it => it.IsCore == request.OnlyCore.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.GroupName))
            {
                query = query.Where(it => string.Equals(it.GroupName, request.GroupName.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(request.SignalType))
            {
                query = query.Where(it => string.Equals(it.SignalType, request.SignalType.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(request.AnchorType))
            {
                query = query.Where(it => string.Equals(it.AnchorType, request.AnchorType.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(request.Keyword))
            {
                var keyword = request.Keyword.Trim();
                query = query.Where(it =>
                    ContainsIgnoreCase(it.SignalKey, keyword) ||
                    ContainsIgnoreCase(it.VariableName, keyword) ||
                    ContainsIgnoreCase(it.SemanticLabel, keyword));
            }

            return query
                .OrderByDescending(it => it.LastChangeTime ?? DateTime.MinValue)
                .ThenBy(it => it.SignalKey)
                .Take(limit)
                .ToList();
        }

        public async Task<List<StationShadowChangeCacheModel>> GetMonitoringChangesAsync(StationShadowTimelineQueryDto request)
        {
            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.StationCode))
            {
                return new List<StationShadowChangeCacheModel>();
            }

            return await _stationShadowStateCacheService.GetRecentChangesAsync(
                request.ProjectId,
                request.StationCode.Trim(),
                NormalizeLimit(request.Limit, 50, 1000));
        }

        public async Task<List<StationShadowBundleCacheModel>> GetMonitoringBundlesAsync(StationShadowTimelineQueryDto request)
        {
            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.StationCode))
            {
                return new List<StationShadowBundleCacheModel>();
            }

            return await _stationShadowStateCacheService.GetRecentBundlesAsync(
                request.ProjectId,
                request.StationCode.Trim(),
                NormalizeLimit(request.Limit, 50, 500));
        }

        public async Task<List<StationShadowEventDto>> GetMonitoringEventsAsync(StationShadowTimelineQueryDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return new List<StationShadowEventDto>();
            }

            var limit = NormalizeLimit(request.Limit, 50, 500);
            var query = _stationShadowEventService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId);

            if (!string.IsNullOrWhiteSpace(request.StationCode))
            {
                var stationCode = request.StationCode.Trim();
                query = query.Where(it => it.StationCode == stationCode);
            }

            var entities = await query
                .OrderBy(it => it.TriggeredAt, OrderByType.Desc)
                .OrderBy(it => it.Id, OrderByType.Desc)
                .Take(limit)
                .ToListAsync();

            return entities.Select(MapShadowEvent).ToList();
        }

        public async Task<(List<StationShadowHistoryItemDto> Items, int Total)> GetMonitoringHistoryAsync(StationShadowHistoryQueryDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return (new List<StationShadowHistoryItemDto>(), 0);
            }

            var page = Math.Max(1, request.Page);
            var pageSize = Math.Clamp(request.PageSize, 10, 200);

            var query = _stationSignalHistoryService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId);

            if (!string.IsNullOrWhiteSpace(request.StationCode))
            {
                var stationCode = request.StationCode.Trim();
                query = query.Where(it => it.StationCode == stationCode);
            }

            if (!string.IsNullOrWhiteSpace(request.BundleId))
            {
                var bundleId = request.BundleId.Trim();
                query = query.Where(it => it.BundleId == bundleId);
            }

            if (!string.IsNullOrWhiteSpace(request.Keyword))
            {
                var keyword = request.Keyword.Trim();
                query = query.Where(it =>
                    it.SignalKey.Contains(keyword) ||
                    it.VariableName.Contains(keyword) ||
                    it.GroupName.Contains(keyword) ||
                    it.SignalType.Contains(keyword));
            }

            RefAsync<int> total = 0;
            var entities = await query
                .OrderBy(it => it.ChangedAt, OrderByType.Desc)
                .OrderBy(it => it.Id, OrderByType.Desc)
                .ToPageListAsync(page, pageSize,  total);

            return (entities.Select(MapSignalHistory).ToList(), total);
        }

        public async Task<List<StationShadowSampleLibraryItemDto>> GetMonitoringSampleLibraryAsync(StationShadowSampleLibraryQueryDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return new List<StationShadowSampleLibraryItemDto>();
            }

            var limit = NormalizeLimit(request.Limit, 30, 200);
            var historyQuery = _stationSignalHistoryService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId);

            if (!string.IsNullOrWhiteSpace(request.StationCode))
            {
                var stationCode = request.StationCode.Trim();
                historyQuery = historyQuery.Where(it => it.StationCode == stationCode);
            }

            if (!string.IsNullOrWhiteSpace(request.Keyword))
            {
                var keyword = request.Keyword.Trim();
                historyQuery = historyQuery.Where(it =>
                    it.SignalKey.Contains(keyword) ||
                    it.VariableName.Contains(keyword) ||
                    it.GroupName.Contains(keyword) ||
                    it.SignalType.Contains(keyword) ||
                    it.BundleId.Contains(keyword));
            }

            var historyRows = await historyQuery
                .OrderBy(it => it.ChangedAt, OrderByType.Desc)
                .OrderBy(it => it.Id, OrderByType.Desc)
                .Take(limit * 40)
                .ToListAsync();

            if (historyRows.Count == 0)
            {
                return new List<StationShadowSampleLibraryItemDto>();
            }

            var bundleIds = historyRows
                .Select(it => it.BundleId)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var snapshotIds = historyRows
                .Select(it => it.SnapshotId)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var eventQuery = _stationShadowEventService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId);
            if (!string.IsNullOrWhiteSpace(request.StationCode))
            {
                var stationCode = request.StationCode.Trim();
                eventQuery = eventQuery.Where(it => it.StationCode == stationCode);
            }

            var relatedEvents = await eventQuery
                .Where(it => bundleIds.Contains(it.LastBundleId) || snapshotIds.Contains(it.SnapshotId))
                .OrderBy(it => it.TriggeredAt, OrderByType.Desc)
                .OrderBy(it => it.Id, OrderByType.Desc)
                .ToListAsync();

            var eventByBundleId = relatedEvents
                .Where(it => !string.IsNullOrWhiteSpace(it.LastBundleId))
                .GroupBy(it => it.LastBundleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(it => it.Key, it => MapShadowEvent(it.First()), StringComparer.OrdinalIgnoreCase);
            var eventBySnapshotId = relatedEvents
                .Where(it => !string.IsNullOrWhiteSpace(it.SnapshotId))
                .GroupBy(it => it.SnapshotId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(it => it.Key, it => MapShadowEvent(it.First()), StringComparer.OrdinalIgnoreCase);

            return historyRows
                .GroupBy(BuildMonitoringSampleKey)
                .Select(group => BuildMonitoringSampleLibraryItem(group.ToList(), eventByBundleId, eventBySnapshotId))
                .Where(it => it != null)
                .OrderByDescending(it => it.EndTime ?? DateTime.MinValue)
                .ThenByDescending(it => it.ChangeCount)
                .Take(limit)
                .ToList();
        }

        public async Task<List<StationShadowPatternTemplateDto>> GetMonitoringPatternTemplatesAsync(StationShadowPatternTemplateQueryDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return new List<StationShadowPatternTemplateDto>();
            }

            var limit = NormalizeLimit(request.Limit, 30, 200);
            var stationCode = request.StationCode?.Trim();
            var effectiveStationMode = request.StationMode?.Trim();
            if (string.IsNullOrWhiteSpace(effectiveStationMode) && !string.IsNullOrWhiteSpace(stationCode))
            {
                var state = await _stationShadowStateCacheService.GetStationStateAsync(request.ProjectId, stationCode);
                effectiveStationMode = state?.StationMode?.Trim();
            }

            var query = _stationPatternTemplateService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId);

            if (!string.IsNullOrWhiteSpace(stationCode))
            {
                query = query.Where(it => it.StationCode == stationCode);
            }

            if (request.OnlyActive)
            {
                query = query.Where(it => it.Active == true);
            }

            if (!string.IsNullOrWhiteSpace(effectiveStationMode))
            {
                query = query.Where(it => it.StationMode == effectiveStationMode || it.StationMode == null || it.StationMode == string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(request.PatternType))
            {
                var patternType = request.PatternType.Trim();
                query = query.Where(it => it.PatternType == patternType);
            }

            if (!string.IsNullOrWhiteSpace(request.LastBundleSignature))
            {
                var lastBundleSignature = request.LastBundleSignature.Trim();
                query = query.Where(it => it.LastBundleSignature == lastBundleSignature);
            }

            if (!string.IsNullOrWhiteSpace(request.Keyword))
            {
                var keyword = request.Keyword.Trim();
                query = query.Where(it =>
                    it.TemplateName.Contains(keyword) ||
                    it.PatternType.Contains(keyword) ||
                    it.LastBundleSignature.Contains(keyword));
            }

            var entities = await query
                .OrderBy(it => it.Confidence, OrderByType.Desc)
                .OrderBy(it => it.SupportCount, OrderByType.Desc)
                .OrderBy(it => it.TemplateVersion, OrderByType.Desc)
                .OrderBy(it => it.LastRebuildTime, OrderByType.Desc)
                .OrderBy(it => it.Id, OrderByType.Desc)
                .Take(limit)
                .ToListAsync();

            IEnumerable<PLC_StationPatternTemplateEntity> orderedEntities = entities;
            if (!string.IsNullOrWhiteSpace(effectiveStationMode))
            {
                orderedEntities = orderedEntities
                    .OrderByDescending(it => string.Equals(it.StationMode, effectiveStationMode, StringComparison.OrdinalIgnoreCase));
            }

            return orderedEntities
                .Select(MapPatternTemplate)
                .ToList();
        }

        public async Task<HttpResponseResultModel<StationShadowPatternTemplateRebuildResultDto>> RebuildMonitoringPatternTemplatesAsync(StationShadowPatternTemplateRebuildRequestDto request)
        {
            var response = new HttpResponseResultModel<StationShadowPatternTemplateRebuildResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }

            if (string.IsNullOrWhiteSpace(request.StationCode))
            {
                response.IsSuccess = false;
                response.ErrorMessage = "StationCode 不能为空";
                return response;
            }

            var result = new StationShadowPatternTemplateRebuildResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode?.Trim(),
                StationMode = request.StationMode?.Trim(),
                PatternType = string.IsNullOrWhiteSpace(request.PatternType) ? "ExpectedNextBundle" : request.PatternType.Trim()
            };

            var sampleLibrary = await GetMonitoringSampleLibraryAsync(new StationShadowSampleLibraryQueryDto
            {
                ProjectId = request.ProjectId,
                StationCode = result.StationCode,
                Keyword = request.Keyword,
                Limit = NormalizeLimit(request.Limit, 60, 300)
            });
            result.SourceSampleCount = sampleLibrary.Count;

            var selectedSampleKeys = (request.SampleKeys ?? new List<string>())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Select(it => it.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var acceptedSamples = sampleLibrary
                .Where(it => !string.IsNullOrWhiteSpace(it.BundleId) || string.Equals(it.SampleType, "Bundle", StringComparison.OrdinalIgnoreCase))
                .Where(it => selectedSampleKeys.Count == 0 || selectedSampleKeys.Contains(it.SampleKey ?? string.Empty))
                .Where(it => string.IsNullOrWhiteSpace(result.StationMode) || string.Equals(it.StationMode, result.StationMode, StringComparison.OrdinalIgnoreCase))
                .Select(it => new
                {
                    Sample = it,
                    Signature = ResolveSampleBundleSignature(it)
                })
                .Where(it => !string.IsNullOrWhiteSpace(it.Signature))
                .OrderBy(it => it.Sample.StartTime ?? DateTime.MinValue)
                .ThenBy(it => it.Sample.EndTime ?? DateTime.MinValue)
                .ThenBy(it => it.Sample.BundleId ?? string.Empty)
                .ToList();

            result.AcceptedSampleCount = acceptedSamples.Count;
            if (acceptedSamples.Count == 0)
            {
                result.Warnings.Add("当前工位缺少可用于模板重建的 bundle 样本，需要先积累多轮监控采样数据");
                response.BackResult = result;
                return response;
            }

            foreach (var modeGroup in acceptedSamples.GroupBy(
                it => string.IsNullOrWhiteSpace(it.Sample.StationMode) ? string.Empty : it.Sample.StationMode.Trim(),
                StringComparer.OrdinalIgnoreCase))
            {
                var stationMode = modeGroup.Key;
                var orderedGroup = modeGroup
                    .OrderBy(it => it.Sample.StartTime ?? DateTime.MinValue)
                    .ThenBy(it => it.Sample.EndTime ?? DateTime.MinValue)
                    .ThenBy(it => it.Sample.BundleId ?? string.Empty)
                    .ToList();
                if (orderedGroup.Count == 0)
                {
                    continue;
                }

                var signatureSamples = orderedGroup
                    .GroupBy(it => it.Signature, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(it => it.Key, it => it.Select(x => x.Sample).ToList(), StringComparer.OrdinalIgnoreCase);
                var transitionGaps = new Dictionary<string, Dictionary<string, List<double>>>(StringComparer.OrdinalIgnoreCase);

                for (var index = 0; index < orderedGroup.Count - 1; index++)
                {
                    var current = orderedGroup[index];
                    var next = orderedGroup[index + 1];
                    if (string.IsNullOrWhiteSpace(current.Signature) || string.IsNullOrWhiteSpace(next.Signature))
                    {
                        continue;
                    }

                    if (!transitionGaps.TryGetValue(current.Signature, out var nextMap))
                    {
                        nextMap = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
                        transitionGaps[current.Signature] = nextMap;
                    }

                    if (!nextMap.TryGetValue(next.Signature, out var gaps))
                    {
                        gaps = new List<double>();
                        nextMap[next.Signature] = gaps;
                    }

                    gaps.Add(CalculateGapMilliseconds(current.Sample.EndTime, next.Sample.StartTime));
                }

                var existingTemplates = await _stationPatternTemplateService.GetDb().AsQueryable()
                    .Where(it => it.IsDeleted == false
                        && it.ProjectId == request.ProjectId
                        && it.StationCode == result.StationCode
                        && it.PatternType == result.PatternType
                        && it.StationMode == stationMode)
                    .ToListAsync();

                // ── ReplaceExisting 时先删再算版本号，避免唯一索引冲突 ──
                if (request.ReplaceExisting && existingTemplates.Count > 0)
                {
                    await _stationPatternTemplateService.DeleteAsync(it =>
                        it.ProjectId == request.ProjectId
                        && it.StationCode == result.StationCode
                        && it.PatternType == result.PatternType
                        && it.StationMode == stationMode);
                    result.ReplacedTemplateCount += existingTemplates.Count(it => it.Active);
                }

                // 查询含已删除在内的最大版本号，确保全局递增不重复
                var globalMaxVersion = await _stationPatternTemplateService.GetDb().AsQueryable()
                    .Where(it => it.ProjectId == request.ProjectId
                        && it.StationCode == result.StationCode
                        && it.PatternType == result.PatternType
                        && it.StationMode == stationMode)
                    .MaxAsync(it => it.TemplateVersion) ?? 0;

                var generatedEntities = new List<PLC_StationPatternTemplateEntity>();
                var versionCounter = globalMaxVersion;
                foreach (var signatureEntry in signatureSamples.OrderByDescending(it => it.Value.Count))
                {
                    var signature = signatureEntry.Key;
                    var samples = signatureEntry.Value
                        .OrderBy(it => it.StartTime ?? DateTime.MinValue)
                        .ThenBy(it => it.EndTime ?? DateTime.MinValue)
                        .ThenBy(it => it.BundleId ?? string.Empty)
                        .ToList();
                    if (samples.Count < Math.Max(1, request.MinSupportCount))
                    {
                        continue;
                    }

                    transitionGaps.TryGetValue(signature, out var nextMap);
                    var nextStats = (nextMap ?? new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase))
                        .Select(it => new
                        {
                            BundleSignature = it.Key,
                            Count = it.Value.Count,
                            Probability = samples.Count == 0 ? 0m : Math.Round((decimal)it.Value.Count / samples.Count, 4),
                            AvgGapMs = it.Value.Count == 0 ? 0d : Math.Round(it.Value.Average(), 2),
                            MinGapMs = it.Value.Count == 0 ? 0d : Math.Round(it.Value.Min(), 2),
                            MaxGapMs = it.Value.Count == 0 ? 0d : Math.Round(it.Value.Max(), 2)
                        })
                        .OrderByDescending(it => it.Count)
                        .ThenBy(it => it.BundleSignature)
                        .ToList();

                    var topNext = nextStats.FirstOrDefault();
                    var allGapValues = nextMap?.SelectMany(it => it.Value).ToList() ?? new List<double>();
                    var referenceSample = samples.Last();
                    versionCounter++;
                    var templateVersion = versionCounter;

                    generatedEntities.Add(new PLC_StationPatternTemplateEntity
                    {
                        ProjectId = request.ProjectId,
                        StationCode = result.StationCode,
                        StationMode = stationMode,
                        PatternType = result.PatternType,
                        TemplateName = BuildPatternTemplateName(result.StationCode, stationMode, signature),
                        TemplateVersion = templateVersion,
                        SupportCount = samples.Count,
                        Confidence = topNext == null ? 0m : topNext.Probability,
                        LastBundleSignature = signature,
                        BundleSequenceJson = JsonSerializer.Serialize(new
                        {
                            currentBundleSignature = signature,
                            signalKeys = referenceSample.SignalKeys ?? new List<string>(),
                            recentSamples = samples
                                .TakeLast(5)
                                .Select(it => new
                                {
                                    it.SampleKey,
                                    it.BundleId,
                                    it.StartTime,
                                    it.EndTime,
                                    it.SignalKeys
                                })
                                .ToList()
                        }),
                        NextExpectedMapJson = JsonSerializer.Serialize(nextStats),
                        TimeWindowStatsJson = JsonSerializer.Serialize(new
                        {
                            transitionCount = allGapValues.Count,
                            avgGapMs = allGapValues.Count == 0 ? 0d : Math.Round(allGapValues.Average(), 2),
                            minGapMs = allGapValues.Count == 0 ? 0d : Math.Round(allGapValues.Min(), 2),
                            maxGapMs = allGapValues.Count == 0 ? 0d : Math.Round(allGapValues.Max(), 2),
                            generatedAt = DateTime.Now
                        }),
                        Active = true,
                        LastRebuildTime = DateTime.Now,
                        SourceSampleCount = orderedGroup.Count,
                        Remark = BuildPatternTemplateRemark(samples.Count, nextStats.Count, stationMode),
                        IsDeleted = false,
                        CreateTime = DateTime.Now,
                        ModifyTime = DateTime.Now
                    });
                }

                if (generatedEntities.Count == 0)
                {
                    result.Warnings.Add($"工位 {result.StationCode} 模式 {(string.IsNullOrWhiteSpace(stationMode) ? "Default" : stationMode)} 没有达到最小支持度的模板样本。");
                    continue;
                }

                await _stationPatternTemplateService.BulkCopyAsync(generatedEntities);
                result.GeneratedTemplateCount += generatedEntities.Count;
                result.GeneratedTemplateIds.AddRange(generatedEntities.Select(it => it.Id));
                result.Templates.AddRange(generatedEntities.Select(MapPatternTemplate));
            }

            response.BackResult = result;
            response.IsSuccess = result.GeneratedTemplateCount > 0;
            response.ErrorMessage = result.GeneratedTemplateCount > 0 ? null : "未生成任何规律模板";
            return response;
        }

        public async Task<StationShadowSnapshotCacheModel> GetMonitoringSnapshotAsync(StationShadowSnapshotQueryDto request)
        {
            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.StationCode))
            {
                return null;
            }

            var stationCode = request.StationCode.Trim();
            var snapshotId = request.SnapshotId;
            if (string.IsNullOrWhiteSpace(snapshotId))
            {
                var state = await _stationShadowStateCacheService.GetStationStateAsync(request.ProjectId, stationCode);
                snapshotId = state?.LastSnapshotId;
            }

            if (string.IsNullOrWhiteSpace(snapshotId))
            {
                return null;
            }

            return await _stationShadowStateCacheService.GetSnapshotAsync(request.ProjectId, stationCode, snapshotId.Trim());
        }

        public async Task<HttpResponseResultModel<RunStationShadowCycleResultDto>> RunMonitoringCycleAsync(RunStationShadowCycleRequestDto request)
        {
            var response = new HttpResponseResultModel<RunStationShadowCycleResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }

            if (string.IsNullOrWhiteSpace(request.StationCode))
            {
                response.IsSuccess = false;
                response.ErrorMessage = "StationCode 不能为空";
                return response;
            }

            try
            {
                response.BackResult = await _stationShadowRuntimeService.RunCycleAsync(request);
                if (request.RebuildPatternsAfterCycle)
                {
                    var rebuildResult = await RebuildMonitoringPatternTemplatesAsync(new StationShadowPatternTemplateRebuildRequestDto
                    {
                        ProjectId = request.ProjectId,
                        StationCode = request.StationCode,
                        StationMode = request.StationMode,
                        Limit = NormalizeLimit(request.PatternSampleLimit, 60, 300),
                        PatternType = "ExpectedNextBundle",
                        ReplaceExisting = true,
                        MinSupportCount = 1
                    });

                    if (!rebuildResult.IsSuccess)
                    {
                        response.BackResult?.Warnings.Add($"模板重建未完成: {rebuildResult.ErrorMessage}");
                    }
                    else
                    {
                        response.BackResult?.Warnings.Add($"模板重建完成，生成 {rebuildResult.BackResult?.GeneratedTemplateCount ?? 0} 条规律模板");
                    }
                }

                if (request.DiagnosePendingAfterCycle)
                {
                    var diagnoseResult = await DiagnosePendingMonitoringEventsAsync(new StationShadowDiagnosisRunRequestDto
                    {
                        ProjectId = request.ProjectId,
                        StationCode = request.StationCode,
                        Limit = Math.Max(1, request.DiagnosisEventLimit),
                    });

                    if (!diagnoseResult.IsSuccess)
                    {
                        response.BackResult?.Warnings.Add($"自动诊断未完成: {diagnoseResult.ErrorMessage}");
                    }
                    else if (diagnoseResult.BackResult != null)
                    {
                        response.BackResult?.Warnings.Add($"自动诊断完成，处理 {diagnoseResult.BackResult.ProcessedCount} 条事件，成功 {diagnoseResult.BackResult.SuccessCount} 条");
                        foreach (var warning in diagnoseResult.BackResult.Warnings.Take(3))
                        {
                            response.BackResult?.Warnings.Add(warning);
                        }
                    }
                }

                response.IsSuccess = true;
                return response;
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                response.ExceptionMessage = ex.ToString();
                return response;
            }
        }

        public async Task<HttpResponseResultModel<StationShadowDiagnosisRunResultDto>> DiagnosePendingMonitoringEventsAsync(StationShadowDiagnosisRunRequestDto request)
        {
            var response = new HttpResponseResultModel<StationShadowDiagnosisRunResultDto>();
            if (request == null || request.ProjectId <= 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId 不能为空";
                return response;
            }

            var result = new StationShadowDiagnosisRunResultDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode?.Trim(),
            };

            try
            {
                var limit = NormalizeLimit(request.Limit, 5, 50);
                var stationCode = request.StationCode?.Trim();
                var query = _stationShadowEventService.GetDb().AsQueryable()
                    .Where(it => it.IsDeleted == false && it.ProjectId == request.ProjectId);

                if (request.EventId.HasValue)
                {
                    query = query.Where(it => it.Id == request.EventId.Value);
                }

                if (!string.IsNullOrWhiteSpace(stationCode))
                {
                    query = query.Where(it => it.StationCode == stationCode);
                }

                if (!request.ForceReprocess)
                {
                    query = query.Where(it => it.DiagnosisStatus == "Pending");
                }

                var pendingEvents = await query
                    .OrderBy(it => it.TriggeredAt, OrderByType.Asc)
                    .OrderBy(it => it.Id, OrderByType.Asc)
                    .Take(limit)
                    .ToListAsync();

                result.RequestedCount = pendingEvents.Count;
                if (pendingEvents.Count == 0)
                {
                    response.IsSuccess = true;
                    response.BackResult = result;
                    return response;
                }

                foreach (var stationGroup in pendingEvents.GroupBy(it => it.StationCode?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    var stationSignals = await _stationShadowSignalService.GetDb().AsQueryable()
                        .Where(it => it.IsDeleted == false
                            && it.ProjectId == request.ProjectId
                            && it.StationCode == stationGroup.Key)
                        .ToListAsync();
                    var stationBindings = await _stationAnchorBindingService.GetListAsync(it =>
                        it.IsDeleted == false
                        && it.ProjectId == request.ProjectId
                        && it.StationCode == stationGroup.Key);
                    var stationAstRows = await _logicUnitAstService.GetListAsync(it =>
                        it.IsDeleted == false
                        && it.ProjectId == request.ProjectId
                        && it.StationCode == stationGroup.Key);

                    foreach (var eventEntity in stationGroup)
                    {
                        var item = await DiagnoseMonitoringEventAsync(request.ProjectId, eventEntity, stationSignals, stationBindings, stationAstRows);
                        result.Items.Add(item);
                        result.ProcessedCount += 1;
                        if (item.Success)
                        {
                            result.SuccessCount += 1;
                        }
                        else
                        {
                            result.FailedCount += 1;
                            if (item.Warnings.Count > 0)
                            {
                                result.Warnings.Add($"事件 {item.EventId}: {item.Warnings[0]}");
                            }
                        }
                    }
                }

                response.BackResult = result;
                response.IsSuccess = true;
                return response;
            }
            catch (Exception ex)
            {
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                response.ExceptionMessage = ex.ToString();
                response.BackResult = result;
                return response;
            }
        }

        /// <summary>
        /// 按工位分段批量执行锚点发现 + AST 编译预览。
        /// 适合单个 PLC 含多工位时的离线全量分析场景。
        /// 每个工位独立执行，MaxParallelStations 控制工位级并发数。
        /// </summary>
        /// <param name="baseRequest">公共参数，StationCode 会被每个工位覆盖</param>
        /// <param name="stationCodes">需要分析的工位列表，传空则自动枚举项目内所有工位</param>
        /// <param name="maxParallelStations">工位级并发数，建议 2-3</param>
        public async Task<List<AnchorDiscoveryResultDto>> BatchDiscoverByStationsAsync(
            AnchorDiscoveryRequestDto baseRequest,
            IEnumerable<string> stationCodes,
            int maxParallelStations = 2)
        {
            var stations = stationCodes
                ?.Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList()
                ?? new List<string>();

            // 没传工位列表时，先用关键字路径快速探测全部工位
            if (stations.Count == 0)
            {
                var probe = new StationExtractionRequestDto
                {
                    ProjectId = baseRequest.ProjectId,
                    IncludePhysicalTags = baseRequest.IncludePhysicalAnchors,
                    IncludeVariableGroups = baseRequest.IncludeVirtualAnchors || baseRequest.IncludeHandshakeAnchors,
                    IncludeLogicUnitMarking = false,
                    IncludeSourceItems = false,
                    UseLlmAnnotation = false
                };
                var probeResult = await ExtractStationsAsync(probe);
                if (probeResult.IsSuccess && probeResult.BackResult != null)
                {
                    stations = probeResult.BackResult.StationBuckets
                        .Select(it => it.StationCode)
                        .Where(it => !string.IsNullOrWhiteSpace(it))
                        .Distinct()
                        .ToList();
                }

                // 如果仍然为空（所有工位均未肝心化），就跑一次不过滤
                if (stations.Count == 0)
                {
                    stations.Add(string.Empty);
                }
            }

            var results = new ConcurrentBag<AnchorDiscoveryResultDto>();
            var sem = new SemaphoreSlim(maxParallelStations, maxParallelStations);

            var tasks = stations.Select(async station =>
            {
                await sem.WaitAsync();
                try
                {
                    var req = new AnchorDiscoveryRequestDto
                    {
                        ProjectId = baseRequest.ProjectId,
                        StationCode = station,
                        BlockId = baseRequest.BlockId,
                        IncludeAuxiliary = baseRequest.IncludeAuxiliary,
                        IncludePhysicalAnchors = baseRequest.IncludePhysicalAnchors,
                        IncludeVirtualAnchors = baseRequest.IncludeVirtualAnchors,
                        IncludeHandshakeAnchors = baseRequest.IncludeHandshakeAnchors,
                        IncludeAstSeeds = baseRequest.IncludeAstSeeds,
                        IncludeAstCompileResults = baseRequest.IncludeAstCompileResults,
                        UseLlmAnnotation = baseRequest.UseLlmAnnotation,
                        IncludeStationColoring = baseRequest.IncludeStationColoring,
                        PersistStationMap = baseRequest.PersistStationMap,
                        PersistAstResults = baseRequest.PersistAstResults,
                        RawContentMaxLength = baseRequest.RawContentMaxLength
                    };
                    var r = await BuildPhaseOneCatalogAsync(req);
                    if (r.IsSuccess && r.BackResult != null)
                    {
                        results.Add(r.BackResult);
                    }
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);
            return results.OrderBy(it => it.StationCode).ToList();
        }

        // ── 内部辅助 ──────────────────────────────────────────────────────

        private async Task PreparePrecomputedStationsAsync(AnchorDiscoveryRequestDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return;
            }

            await TryLoadPersistedStationsAsync(request);

            var needsPhysical = request.IncludePhysicalAnchors && request.PrecomputedPhysicalTagStations == null;
            var needsVariables =
                (request.IncludeVirtualAnchors || request.IncludeHandshakeAnchors || request.IncludeAstSeeds || request.IncludeAstCompileResults)
                && request.PrecomputedVariableStations == null;
            if (!needsPhysical && !needsVariables)
            {
                return;
            }

            var extraction = await _stationExtractionService.BuildAsync(new StationExtractionRequestDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode,
                BlockId = request.BlockId,
                UseLlmAnnotation = request.UseLlmAnnotation,
                IncludePhysicalTags = request.IncludePhysicalAnchors,
                IncludeVariableGroups = request.IncludeVirtualAnchors || request.IncludeHandshakeAnchors || request.IncludeAstSeeds || request.IncludeAstCompileResults,
                IncludeLogicUnitMarking = false,
                IncludeSourceItems = true,
                PersistStationMarking = false,
                UpdateBlockStationCode = false
            });

            if (needsPhysical)
            {
                request.PrecomputedPhysicalTagStations = extraction.PhysicalTagItems
                    .Select(it => new { Item = it, TagId = ParsePhysicalTagId(it.SourceKey) })
                    .Where(it => it.TagId.HasValue)
                    .ToDictionary(
                        it => it.TagId.Value,
                        it => new StationInferenceResult
                        {
                            StationCode = it.Item.StationCode ?? string.Empty,
                            Confidence = it.Item.Confidence,
                            InferenceSource = it.Item.StationSource,
                            InferenceDetail = it.Item.StationSourceDetail,
                            Evidence = it.Item.Evidence?.ToList() ?? new List<string>()
                        });
            }

            if (needsVariables)
            {
                request.PrecomputedVariableStations = extraction.VariableGroupItems
                    .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                    .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                        {
                            var best = g
                                .OrderByDescending(it => it.Confidence)
                                .ThenBy(it => it.VariableName)
                                .First();

                            return new StationInferenceResult
                            {
                                StationCode = best.StationCode ?? string.Empty,
                                Confidence = best.Confidence,
                                InferenceSource = best.StationSource,
                                InferenceDetail = best.StationSourceDetail,
                                Evidence = best.Evidence?.ToList() ?? new List<string>()
                            };
                        },
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        private async Task<StationShadowEventDto> GetLatestMonitoringEventAsync(long projectId, string stationCode)
        {
            var entity = await _stationShadowEventService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == projectId && it.StationCode == stationCode)
                .OrderBy(it => it.TriggeredAt, OrderByType.Desc)
                .OrderBy(it => it.Id, OrderByType.Desc)
                .FirstAsync();

            return MapShadowEvent(entity);
        }

        private static StationShadowEventDto MapShadowEvent(PLC_StationShadowEventEntity entity)
        {
            if (entity == null)
            {
                return null;
            }

            return new StationShadowEventDto
            {
                Id = entity.Id,
                ProjectId = entity.ProjectId,
                StationCode = entity.StationCode,
                EventType = entity.EventType,
                EventLevel = entity.EventLevel,
                TriggeredAt = entity.TriggeredAt,
                StationMode = entity.StationMode,
                LastBundleId = entity.LastBundleId,
                LastBundleSignature = entity.LastBundleSignature,
                LastChangedSignal = entity.LastChangedSignal,
                SnapshotId = entity.SnapshotId,
                TargetAnchorKey = entity.TargetAnchorKey,
                DiagnosisStatus = entity.DiagnosisStatus,
                SignalCount = entity.SignalCount,
                BundleCount = entity.BundleCount,
                SourceRuntimeNode = entity.SourceRuntimeNode,
                IsArchived = entity.IsArchived ?? false,
                ExpectedNextBundleJson = entity.ExpectedNextBundleJson,
                ExpectedNextBundle = DeserializeMonitoringJson<StationShadowExpectedNextBundleDto>(entity.ExpectedNextBundleJson),
                MissingBundleJson = entity.MissingBundleJson,
                MissingBundle = DeserializeMonitoringJson<StationShadowMissingBundleDto>(entity.MissingBundleJson),
                DiagnosisResultJson = entity.DiagnosisResultJson,
                DiagnosisResult = DeserializeMonitoringJson<StationShadowDiagnosisResultDto>(entity.DiagnosisResultJson)
            };
        }

        private static StationShadowHistoryItemDto MapSignalHistory(PLC_StationSignalHistoryEntity entity)
        {
            if (entity == null)
            {
                return null;
            }

            return new StationShadowHistoryItemDto
            {
                Id = entity.Id,
                ProjectId = entity.ProjectId,
                StationCode = entity.StationCode,
                SignalKey = entity.SignalKey,
                VariableName = entity.VariableName,
                OldValue = entity.OldValue,
                NewValue = entity.NewValue,
                ChangedAt = entity.ChangedAt,
                StationMode = entity.StationMode,
                Quality = entity.Quality,
                BundleId = entity.BundleId,
                SnapshotId = entity.SnapshotId,
                EventId = entity.EventId,
                SignalType = entity.SignalType,
                GroupName = entity.GroupName,
                LogicUnitId = entity.LogicUnitId,
            };
        }

        private static StationShadowPatternTemplateDto MapPatternTemplate(PLC_StationPatternTemplateEntity entity)
        {
            if (entity == null)
            {
                return null;
            }

            return new StationShadowPatternTemplateDto
            {
                Id = entity.Id,
                ProjectId = entity.ProjectId,
                StationCode = entity.StationCode,
                StationMode = entity.StationMode,
                PatternType = entity.PatternType,
                TemplateName = entity.TemplateName,
                TemplateVersion = entity.TemplateVersion,
                SupportCount = entity.SupportCount,
                Confidence = entity.Confidence,
                LastBundleSignature = entity.LastBundleSignature,
                BundleSequenceJson = entity.BundleSequenceJson,
                NextExpectedMapJson = entity.NextExpectedMapJson,
                TimeWindowStatsJson = entity.TimeWindowStatsJson,
                Active = entity.Active,
                LastRebuildTime = entity.LastRebuildTime,
                SourceSampleCount = entity.SourceSampleCount,
                Remark = entity.Remark,
            };
        }

        private async Task<StationShadowDiagnosisRunItemDto> DiagnoseMonitoringEventAsync(
            long projectId,
            PLC_StationShadowEventEntity eventEntity,
            List<PLC_StationShadowSignalEntity> stationSignals,
            List<PLC_StationAnchorBindingEntity> stationBindings,
            List<PLC_LogicUnitAstEntity> stationAstRows)
        {
            var item = new StationShadowDiagnosisRunItemDto
            {
                EventId = eventEntity.Id,
                StationCode = eventEntity.StationCode,
                DiagnosisStatus = eventEntity.DiagnosisStatus,
                LastBundleSignature = eventEntity.LastBundleSignature,
            };

            string finalStatus;
            string expectedNextBundleJson = null;
            string missingBundleJson = null;
            string diagnosisResultJson;
            string targetAnchorKey = null;

            try
            {
                if (!string.Equals(eventEntity.EventType, "Silent", StringComparison.OrdinalIgnoreCase))
                {
                    finalStatus = string.IsNullOrWhiteSpace(eventEntity.DiagnosisStatus) ? "Skipped" : eventEntity.DiagnosisStatus;
                    item.Warnings.Add("当前事件不是 Silent 事件，跳过自动诊断。");
                    item.Success = string.Equals(finalStatus, "Recovered", StringComparison.OrdinalIgnoreCase);
                    diagnosisResultJson = SerializeMonitoringJson(new StationShadowDiagnosisResultDto
                    {
                        Status = finalStatus,
                        Reason = "UnsupportedEventType",
                        EventType = eventEntity.EventType,
                        EventId = eventEntity.Id,
                        LastBundleSignature = eventEntity.LastBundleSignature,
                        EventContext = BuildDiagnosisEventContext(eventEntity),
                        DiagnosedAt = DateTime.Now,
                    });
                    await UpdateMonitoringEventDiagnosisAsync(eventEntity, finalStatus, expectedNextBundleJson, missingBundleJson, targetAnchorKey, diagnosisResultJson);
                    item.DiagnosisStatus = finalStatus;
                    return item;
                }

                if (string.IsNullOrWhiteSpace(eventEntity.LastBundleSignature))
                {
                    finalStatus = "NoSignature";
                    item.Warnings.Add("事件缺少 LastBundleSignature，无法匹配规律模板。");
                    diagnosisResultJson = SerializeMonitoringJson(new StationShadowDiagnosisResultDto
                    {
                        Status = finalStatus,
                        Reason = "LastBundleSignatureMissing",
                        EventId = eventEntity.Id,
                        EventType = eventEntity.EventType,
                        EventContext = BuildDiagnosisEventContext(eventEntity),
                        DiagnosedAt = DateTime.Now,
                    });
                    await UpdateMonitoringEventDiagnosisAsync(eventEntity, finalStatus, expectedNextBundleJson, missingBundleJson, targetAnchorKey, diagnosisResultJson);
                    item.DiagnosisStatus = finalStatus;
                    return item;
                }

                var templates = await GetMonitoringPatternTemplatesAsync(new StationShadowPatternTemplateQueryDto
                {
                    ProjectId = projectId,
                    StationCode = eventEntity.StationCode,
                    StationMode = eventEntity.StationMode,
                    PatternType = "ExpectedNextBundle",
                    LastBundleSignature = eventEntity.LastBundleSignature,
                    OnlyActive = true,
                    Limit = 10,
                });

                var template = templates.FirstOrDefault();
                if (template == null)
                {
                    finalStatus = "NoTemplate";
                    item.Warnings.Add("未找到匹配当前 LastBundleSignature 的规律模板。");
                    diagnosisResultJson = SerializeMonitoringJson(new StationShadowDiagnosisResultDto
                    {
                        Status = finalStatus,
                        Reason = "PatternTemplateNotFound",
                        EventId = eventEntity.Id,
                        EventType = eventEntity.EventType,
                        LastBundleSignature = eventEntity.LastBundleSignature,
                        EventContext = BuildDiagnosisEventContext(eventEntity),
                        DiagnosedAt = DateTime.Now,
                    });
                    await UpdateMonitoringEventDiagnosisAsync(eventEntity, finalStatus, expectedNextBundleJson, missingBundleJson, targetAnchorKey, diagnosisResultJson);
                    item.DiagnosisStatus = finalStatus;
                    return item;
                }

                item.MatchedTemplateId = template.Id;
                item.TemplateConfidence = template.Confidence;

                var expectedNext = ResolveExpectedNextBundle(template.NextExpectedMapJson);
                if (expectedNext == null || string.IsNullOrWhiteSpace(expectedNext.BundleSignature))
                {
                    finalStatus = "NoExpectedBundle";
                    item.Warnings.Add("模板未给出有效的 ExpectedNextBundle。");
                    diagnosisResultJson = SerializeMonitoringJson(new StationShadowDiagnosisResultDto
                    {
                        Status = finalStatus,
                        Reason = "ExpectedNextBundleMissing",
                        EventId = eventEntity.Id,
                        EventType = eventEntity.EventType,
                        LastBundleSignature = eventEntity.LastBundleSignature,
                        Template = BuildDiagnosisTemplate(template, null),
                        EventContext = BuildDiagnosisEventContext(eventEntity),
                        DiagnosedAt = DateTime.Now,
                    });
                    await UpdateMonitoringEventDiagnosisAsync(eventEntity, finalStatus, expectedNextBundleJson, missingBundleJson, targetAnchorKey, diagnosisResultJson);
                    item.DiagnosisStatus = finalStatus;
                    return item;
                }

                var expectedSignalKeys = ParseBundleSignature(expectedNext.BundleSignature);
                item.ExpectedBundleSignature = expectedNext.BundleSignature;
                item.MissingSignalKeys = expectedSignalKeys;
                expectedNextBundleJson = SerializeMonitoringJson(MapExpectedNextBundle(expectedNext));
                missingBundleJson = SerializeMonitoringJson(new StationShadowMissingBundleDto
                {
                    BundleSignature = expectedNext.BundleSignature,
                    SignalKeys = expectedSignalKeys,
                    InferredFromTemplateId = template.Id,
                    InferredAt = DateTime.Now,
                });

                var signalLookup = ResolveDiagnosisSignalTarget(expectedSignalKeys, stationSignals, stationBindings, stationAstRows, eventEntity.LastChangedSignal);
                if (signalLookup == null)
                {
                    finalStatus = "NoSignalMapping";
                    item.Warnings.Add("预期缺失 bundle 未能映射到有效的监控信号配置。");
                    diagnosisResultJson = SerializeMonitoringJson(new StationShadowDiagnosisResultDto
                    {
                        Status = finalStatus,
                        Reason = "SignalMappingNotFound",
                        EventId = eventEntity.Id,
                        EventType = eventEntity.EventType,
                        LastBundleSignature = eventEntity.LastBundleSignature,
                        ExpectedSignalKeys = expectedSignalKeys,
                        FallbackSignalKey = eventEntity.LastChangedSignal,
                        AvailableBindings = (stationBindings ?? new List<PLC_StationAnchorBindingEntity>()).Count,
                        Template = BuildDiagnosisTemplate(template, expectedNext),
                        EventContext = BuildDiagnosisEventContext(eventEntity),
                        DiagnosedAt = DateTime.Now,
                    });
                    await UpdateMonitoringEventDiagnosisAsync(eventEntity, finalStatus, expectedNextBundleJson, missingBundleJson, targetAnchorKey, diagnosisResultJson);
                    item.DiagnosisStatus = finalStatus;
                    return item;
                }

                targetAnchorKey = signalLookup.AnchorKey;
                item.TargetAnchorKey = signalLookup.AnchorKey;
                item.TargetVariableName = signalLookup.VariableName;
                item.TargetLogicUnitId = signalLookup.LogicUnitId;
                item.ResolutionSource = signalLookup.ResolutionSource;

                var additionalSymbols = (stationSignals ?? new List<PLC_StationShadowSignalEntity>())
                    .Where(it => expectedSignalKeys.Contains(it.SignalKey ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    .Select(it => it.VariableName)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Select(it => it.Trim())
                    .Concat((stationBindings ?? new List<PLC_StationAnchorBindingEntity>())
                        .Where(it => string.Equals(it.AnchorKey, signalLookup.AnchorKey, StringComparison.OrdinalIgnoreCase))
                        .Select(it => it.VariableName)
                        .Where(it => !string.IsNullOrWhiteSpace(it))
                        .Select(it => it.Trim()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                EvaluatePersistedAstResultDto astResult;
                try
                {
                    astResult = await _runtimeEvaluationService.EvaluateAsync(new EvaluatePersistedAstRequestDto
                    {
                        ProjectId = projectId,
                        StationCode = eventEntity.StationCode,
                        StationMode = eventEntity.StationMode,
                        LogicUnitId = signalLookup.LogicUnitId,
                        VariableName = signalLookup.VariableName,
                        IncludeStationAnchorVariables = true,
                        AdditionalSymbolPaths = additionalSymbols,
                        RelatedAnchors = additionalSymbols,
                    });
                }
                catch (Exception ex)
                {
                    finalStatus = "NoAst";
                    item.Warnings.Add($"AST 下钻失败: {ex.Message}");
                    diagnosisResultJson = SerializeMonitoringJson(new StationShadowDiagnosisResultDto
                    {
                        Status = finalStatus,
                        Reason = "AstEvaluationFailed",
                        EventId = eventEntity.Id,
                        EventType = eventEntity.EventType,
                        LastBundleSignature = eventEntity.LastBundleSignature,
                        ExpectedSignalKeys = expectedSignalKeys,
                        Template = BuildDiagnosisTemplate(template, expectedNext),
                        Target = BuildDiagnosisTarget(signalLookup),
                        EventContext = BuildDiagnosisEventContext(eventEntity),
                        DiagnosedAt = DateTime.Now,
                        Error = ex.Message,
                    });
                    await UpdateMonitoringEventDiagnosisAsync(eventEntity, finalStatus, expectedNextBundleJson, missingBundleJson, targetAnchorKey, diagnosisResultJson);
                    item.DiagnosisStatus = finalStatus;
                    return item;
                }

                item.AstEvaluated = true;
                item.AstEvaluatedValue = astResult.EvaluatedValue;
                item.AstEvaluationStatus = astResult.EvaluationStatus;
                item.Messages = astResult.Messages ?? new List<string>();

                // 区分完全诊断与部分诊断
                var rootStatus = astResult.EvaluationStatus ?? string.Empty;
                if (string.Equals(rootStatus, "True", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rootStatus, "False", StringComparison.OrdinalIgnoreCase))
                {
                    // 若树中存在不可读变量，但失败路径上的每个节点均已确定（无 Unknown/Unsupported），
                    // 则关键链路完整，仍视为完全诊断，不因旁支的不可读信号降级为部分诊断。
                    var hasAmbiguity = astResult.UnsupportedNodeCount > 0 || astResult.UnknownNodeCount > 0;
                    var failurePathClean = !hasAmbiguity || IsFailurePathAllDetermined(astResult.FailurePath);
                    finalStatus = failurePathClean ? "Diagnosed" : "DiagnosedPartial";
                }
                else
                {
                    // 根节点 Unknown / Unsupported — 标记为部分诊断
                    finalStatus = "DiagnosedPartial";
                }

                item.Success = true;

                // DiagnosedPartial → 尝试 LLM 辅助推理
                AstLlmFallbackResultDto llmFallback = null;
                if (string.Equals(finalStatus, "DiagnosedPartial", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        llmFallback = await _astLlmFallbackService.TryFallbackAsync(astResult);
                    }
                    catch
                    {
                        // LLM 失败不影响主流程
                    }

                    // LLM 给出明确结论（非 Uncertain）且置信度足够时，升级为 LlmDiagnosed
                    if (llmFallback != null
                        && llmFallback.Success
                        && llmFallback.Confidence >= 0.75
                        && !string.Equals(llmFallback.Conclusion, "Uncertain", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(llmFallback.Conclusion))
                    {
                        finalStatus = "LlmDiagnosed";
                    }
                }

                diagnosisResultJson = SerializeMonitoringJson(new StationShadowDiagnosisResultDto
                {
                    Status = finalStatus,
                    EventId = eventEntity.Id,
                    EventType = eventEntity.EventType,
                    LastBundleSignature = eventEntity.LastBundleSignature,
                    ExpectedSignalKeys = expectedSignalKeys,
                    Template = BuildDiagnosisTemplate(template, expectedNext),
                    Target = BuildDiagnosisTarget(signalLookup),
                    Ast = BuildDiagnosisAst(astResult),
                    LlmFallback = llmFallback,
                    EventContext = BuildDiagnosisEventContext(eventEntity),
                    DiagnosedAt = DateTime.Now,
                });

                await UpdateMonitoringEventDiagnosisAsync(eventEntity, finalStatus, expectedNextBundleJson, missingBundleJson, targetAnchorKey, diagnosisResultJson);
                item.DiagnosisStatus = finalStatus;
                return item;
            }
            catch (Exception ex)
            {
                finalStatus = "Error";
                item.Warnings.Add(ex.Message);
                diagnosisResultJson = SerializeMonitoringJson(new StationShadowDiagnosisResultDto
                {
                    Status = finalStatus,
                    Reason = "UnhandledException",
                    EventId = eventEntity.Id,
                    EventType = eventEntity.EventType,
                    LastBundleSignature = eventEntity.LastBundleSignature,
                    EventContext = BuildDiagnosisEventContext(eventEntity),
                    DiagnosedAt = DateTime.Now,
                    Error = ex.ToString(),
                });
                await UpdateMonitoringEventDiagnosisAsync(eventEntity, finalStatus, expectedNextBundleJson, missingBundleJson, targetAnchorKey, diagnosisResultJson);
                item.DiagnosisStatus = finalStatus;
                return item;
            }
        }

        /// <summary>
        /// 判断失败路径上的所有节点是否均为确定值（非 Unknown / Unsupported）。
        /// 用于在树中存在不可读信号时，仍对失败链路完整的情况升级为 Diagnosed。
        /// </summary>
        private static bool IsFailurePathAllDetermined(List<StopCauseAstTraceNode> failurePath)
        {
            if (failurePath == null || failurePath.Count == 0)
            {
                return false; // 没有失败路径则不升级
            }

            return failurePath.All(node =>
                !string.Equals(node.Status, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(node.Status, "Unsupported", StringComparison.OrdinalIgnoreCase));
        }

        private static string SerializeMonitoringJson<T>(T value)
        {
            return JsonSerializer.Serialize(value);
        }

        private static T DeserializeMonitoringJson<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            }
            catch
            {
                return null;
            }
        }

        private static StationShadowExpectedNextBundleDto MapExpectedNextBundle(MonitoringExpectedNextBundle bundle)
        {
            if (bundle == null)
            {
                return null;
            }

            return new StationShadowExpectedNextBundleDto
            {
                BundleSignature = bundle.BundleSignature,
                Count = bundle.Count,
                Probability = bundle.Probability,
                AvgGapMs = bundle.AvgGapMs,
                MinGapMs = bundle.MinGapMs,
                MaxGapMs = bundle.MaxGapMs,
            };
        }

        private static StationShadowDiagnosisTemplateDto BuildDiagnosisTemplate(StationShadowPatternTemplateDto template, MonitoringExpectedNextBundle expectedNext)
        {
            if (template == null)
            {
                return null;
            }

            return new StationShadowDiagnosisTemplateDto
            {
                Id = template.Id,
                TemplateName = template.TemplateName,
                TemplateVersion = template.TemplateVersion,
                Confidence = template.Confidence,
                ExpectedBundleSignature = expectedNext?.BundleSignature,
                Probability = expectedNext?.Probability,
                Count = expectedNext?.Count,
            };
        }

        private static StationShadowDiagnosisTargetDto BuildDiagnosisTarget(MonitoringDiagnosisSignalTarget target)
        {
            if (target == null)
            {
                return null;
            }

            return new StationShadowDiagnosisTargetDto
            {
                SignalKey = target.SignalKey,
                AnchorKey = target.AnchorKey,
                VariableName = target.VariableName,
                LogicUnitId = target.LogicUnitId,
                ResolutionSource = target.ResolutionSource,
            };
        }

        private static StationShadowDiagnosisAstDto BuildDiagnosisAst(EvaluatePersistedAstResultDto astResult)
        {
            if (astResult == null)
            {
                return null;
            }

            return new StationShadowDiagnosisAstDto
            {
                LogicUnitId = astResult.LogicUnitId,
                BlockId = astResult.BlockId,
                BlockName = astResult.BlockName,
                VariableName = astResult.VariableName,
                EvaluatedValue = astResult.EvaluatedValue,
                EvaluationStatus = astResult.EvaluationStatus,
                MissingSymbols = astResult.MissingSymbols ?? new List<string>(),
                Messages = astResult.Messages ?? new List<string>(),
                FailurePath = astResult.FailurePath,
            };
        }

        private static StationShadowDiagnosisEventContextDto BuildDiagnosisEventContext(PLC_StationShadowEventEntity eventEntity)
        {
            if (eventEntity == null)
            {
                return null;
            }

            return new StationShadowDiagnosisEventContextDto
            {
                EventId = eventEntity.Id,
                EventType = eventEntity.EventType,
                TriggeredAt = eventEntity.TriggeredAt,
                LastBundleSignature = eventEntity.LastBundleSignature,
                LastChangedSignal = eventEntity.LastChangedSignal,
                SnapshotId = eventEntity.SnapshotId,
            };
        }

        private async Task UpdateMonitoringEventDiagnosisAsync(
            PLC_StationShadowEventEntity eventEntity,
            string diagnosisStatus,
            string expectedNextBundleJson,
            string missingBundleJson,
            string targetAnchorKey,
            string diagnosisResultJson)
        {
            eventEntity.DiagnosisStatus = diagnosisStatus;
            eventEntity.ExpectedNextBundleJson = expectedNextBundleJson;
            eventEntity.MissingBundleJson = missingBundleJson;
            eventEntity.TargetAnchorKey = targetAnchorKey;
            eventEntity.DiagnosisResultJson = diagnosisResultJson;
            eventEntity.ModifyTime = DateTime.Now;
            await _stationShadowEventService.UpdateAsync(eventEntity);
        }

        private static MonitoringExpectedNextBundle ResolveExpectedNextBundle(string nextExpectedMapJson)
        {
            if (string.IsNullOrWhiteSpace(nextExpectedMapJson))
            {
                return null;
            }

            var items = JsonSerializer.Deserialize<List<MonitoringExpectedNextBundle>>(nextExpectedMapJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            return items?
                .Where(it => !string.IsNullOrWhiteSpace(it.BundleSignature))
                .OrderByDescending(it => it.Count)
                .ThenByDescending(it => it.Probability)
                .FirstOrDefault();
        }

        private static List<string> ParseBundleSignature(string signature)
        {
            return (signature ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(it => it.Trim())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static MonitoringDiagnosisSignalTarget ResolveDiagnosisSignalTarget(
            List<string> expectedSignalKeys,
            List<PLC_StationShadowSignalEntity> stationSignals,
            List<PLC_StationAnchorBindingEntity> stationBindings,
            List<PLC_LogicUnitAstEntity> stationAstRows,
            string fallbackSignalKey)
        {
            stationSignals ??= new List<PLC_StationShadowSignalEntity>();
            stationBindings ??= new List<PLC_StationAnchorBindingEntity>();
            stationAstRows ??= new List<PLC_LogicUnitAstEntity>();

            foreach (var expectedSignalKey in expectedSignalKeys ?? new List<string>())
            {
                var signalCandidates = stationSignals
                    .Where(it => string.Equals(it.SignalKey, expectedSignalKey, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(it => it.Enabled == true)
                    .ThenByDescending(it => it.IsCore == true)
                    .ThenByDescending(it => it.ParticipateInPattern == true)
                    .ThenBy(it => it.SortOrder ?? int.MaxValue)
                    .ThenBy(it => it.Priority ?? int.MaxValue)
                    .ToList();

                foreach (var signal in signalCandidates)
                {
                    var bindingMatches = stationBindings
                        .Where(it =>
                            string.Equals(it.AnchorKey, signal.SignalKey, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrWhiteSpace(signal.VariableName) && string.Equals(it.VariableName, signal.VariableName, StringComparison.OrdinalIgnoreCase)))
                        .OrderByDescending(it => !string.IsNullOrWhiteSpace(it.AnchorKey) && string.Equals(it.AnchorKey, signal.SignalKey, StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(it => it.HasConflict != true)
                        .ThenByDescending(it => it.Confidence ?? 0m)
                        .ToList();

                    foreach (var binding in bindingMatches)
                    {
                        var bindingVariableName = string.IsNullOrWhiteSpace(binding.VariableName) ? null : binding.VariableName.Trim();
                        var astMatch = FindBestRuntimeAstOutputMatch(bindingVariableName, stationAstRows);
                        if (!string.IsNullOrWhiteSpace(bindingVariableName) || astMatch?.LogicUnitId.HasValue == true || signal.LogicUnitId.HasValue)
                        {
                            return new MonitoringDiagnosisSignalTarget
                            {
                                SignalKey = signal.SignalKey?.Trim(),
                                AnchorKey = string.IsNullOrWhiteSpace(binding.AnchorKey) ? signal.SignalKey?.Trim() : binding.AnchorKey.Trim(),
                                VariableName = bindingVariableName ?? NormalizeNullable(signal.VariableName),
                                LogicUnitId = astMatch?.LogicUnitId ?? signal.LogicUnitId,
                                ResolutionSource = astMatch != null ? "AnchorBinding+AstOutput" : "AnchorBinding",
                            };
                        }
                    }

                    var directVariableName = NormalizeNullable(signal.VariableName);
                    var directAstMatch = FindBestRuntimeAstOutputMatch(directVariableName, stationAstRows);
                    if (!string.IsNullOrWhiteSpace(directVariableName) || directAstMatch?.LogicUnitId.HasValue == true || signal.LogicUnitId.HasValue)
                    {
                        return new MonitoringDiagnosisSignalTarget
                        {
                            SignalKey = signal.SignalKey?.Trim(),
                            AnchorKey = signal.SignalKey?.Trim(),
                            VariableName = directVariableName,
                            LogicUnitId = directAstMatch?.LogicUnitId ?? signal.LogicUnitId,
                            ResolutionSource = directAstMatch != null ? "SignalVariable+AstOutput" : "SignalConfig",
                        };
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(fallbackSignalKey))
            {
                var fallbackSignal = stationSignals
                    .Where(it => string.Equals(it.SignalKey, fallbackSignalKey, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(it => !string.IsNullOrWhiteSpace(it.VariableName))
                    .ThenByDescending(it => it.LogicUnitId.HasValue)
                    .FirstOrDefault();
                if (fallbackSignal != null && (!string.IsNullOrWhiteSpace(fallbackSignal.VariableName) || fallbackSignal.LogicUnitId.HasValue))
                {
                    var fallbackAstMatch = FindBestRuntimeAstOutputMatch(NormalizeNullable(fallbackSignal.VariableName), stationAstRows);
                    return new MonitoringDiagnosisSignalTarget
                    {
                        SignalKey = fallbackSignal.SignalKey?.Trim(),
                        AnchorKey = fallbackSignal.SignalKey?.Trim(),
                        VariableName = NormalizeNullable(fallbackSignal.VariableName),
                        LogicUnitId = fallbackAstMatch?.LogicUnitId ?? fallbackSignal.LogicUnitId,
                        ResolutionSource = fallbackAstMatch != null ? "FallbackSignal+AstOutput" : "FallbackSignal",
                    };
                }
            }

            return null;
        }

        private static PLC_LogicUnitAstEntity FindBestRuntimeAstOutputMatch(string variableName, List<PLC_LogicUnitAstEntity> stationAstRows)
        {
            if (stationAstRows == null || stationAstRows.Count == 0)
            {
                return null;
            }

            var runtimeEligibleRows = stationAstRows
                .Where(RuntimeAnchorSelectionPolicy.IsRuntimeEligibleAst)
                .ToList();

            return FindBestAstOutputMatch(variableName, runtimeEligibleRows);
        }

        private static PLC_LogicUnitAstEntity FindBestAstOutputMatch(string variableName, List<PLC_LogicUnitAstEntity> stationAstRows)
        {
            if (string.IsNullOrWhiteSpace(variableName) || stationAstRows == null || stationAstRows.Count == 0)
            {
                return null;
            }

            var normalizedVariableName = variableName.Trim();
            return stationAstRows
                .Select(it => new
                {
                    Row = it,
                    Score = ScoreAstOutputMatch(it, normalizedVariableName)
                })
                .Where(it => it.Score > 0)
                .OrderByDescending(it => it.Score)
                .ThenByDescending(it => ScoreCompileStatus(it.Row.CompileStatus))
                .Select(it => it.Row)
                .FirstOrDefault();
        }

        private static int ScoreAstOutputMatch(PLC_LogicUnitAstEntity row, string variableName)
        {
            if (row == null || string.IsNullOrWhiteSpace(variableName))
            {
                return 0;
            }

            var score = 0;
            var outputVariables = DeserializeStringListSafe(row.OutputVariablesJson);
            if (outputVariables.Any(it => string.Equals(it, variableName, StringComparison.OrdinalIgnoreCase)))
            {
                score += 100;
            }
            // 后缀模糊匹配：应对 OutputVariablesJson 存完整路径 vs 查找用短名（或反之）
            // 例如 OutputVariablesJson=["DB310_OP030_User.Status.Control.OP030_CYL08_FwdAuto"]
            //       variableName="OP030_CYL08_FwdAuto" → 后缀命中，加60分
            else if (outputVariables.Any(it =>
                it.EndsWith("." + variableName, StringComparison.OrdinalIgnoreCase) ||
                variableName.EndsWith("." + it, StringComparison.OrdinalIgnoreCase)))
            {
                score += 60;
            }

            var rootVariableName = ParseRootVariableName(row.RootNodeJson);
            if (!string.IsNullOrWhiteSpace(rootVariableName) && string.Equals(rootVariableName, variableName, StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }

            // 仅当变量名有匹配时才加编译状态权重（作为同分 tiebreaker），
            // 否则任何 Compiled 行都会误匹配为 score>0
            if (score > 0)
            {
                score += ScoreCompileStatus(row.CompileStatus);

                // GRAPH StepAction 优先于同块内其他类型（如 NETWORK），
                // 因为 StepAction 行才是 GRAPH 诊断的正确入口
                if (IsGraphStepAction(row.UnitType))
                    score += 50;
            }
            return score;
        }

        private static string ParseRootVariableName(string rootNodeJson)
        {
            if (string.IsNullOrWhiteSpace(rootNodeJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(rootNodeJson);
                if (document.RootElement.TryGetProperty("VariableName", out var variableElement)
                    && variableElement.ValueKind == JsonValueKind.String)
                {
                    return NormalizeNullable(variableElement.GetString());
                }
            }
            catch
            {
            }

            return null;
        }

        private static List<string> DeserializeStringListSafe(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static int ScoreCompileStatus(string compileStatus)
        {
            if (string.Equals(compileStatus, AstCompileStatus.Compiled, StringComparison.OrdinalIgnoreCase))
            {
                return 30;
            }

            if (string.Equals(compileStatus, AstCompileStatus.PartiallyCompiled, StringComparison.OrdinalIgnoreCase))
            {
                return 20;
            }

            if (string.Equals(compileStatus, AstCompileStatus.Unsupported, StringComparison.OrdinalIgnoreCase))
            {
                return 5;
            }

            return 0;
        }

        private static string NormalizeNullable(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private sealed class MonitoringExpectedNextBundle
        {
            public string BundleSignature { get; set; }

            public int Count { get; set; }

            public decimal Probability { get; set; }

            public double AvgGapMs { get; set; }

            public double MinGapMs { get; set; }

            public double MaxGapMs { get; set; }
        }

        private sealed class MonitoringDiagnosisSignalTarget
        {
            public string SignalKey { get; set; }

            public string AnchorKey { get; set; }

            public string VariableName { get; set; }

            public long? LogicUnitId { get; set; }

            public string ResolutionSource { get; set; }
        }

        private static string BuildMonitoringSampleKey(PLC_StationSignalHistoryEntity entity)
        {
            if (!string.IsNullOrWhiteSpace(entity?.BundleId))
            {
                return $"bundle:{entity.BundleId.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(entity?.SnapshotId))
            {
                return $"snapshot:{entity.SnapshotId.Trim()}";
            }

            var changedAt = entity?.ChangedAt ?? entity?.CreateTime ?? DateTime.MinValue;
            return $"series:{entity?.StationCode}:{changedAt:yyyyMMddHHmm}:{entity?.SignalKey}";
        }

        private static StationShadowSampleLibraryItemDto BuildMonitoringSampleLibraryItem(
            List<PLC_StationSignalHistoryEntity> rows,
            Dictionary<string, StationShadowEventDto> eventByBundleId,
            Dictionary<string, StationShadowEventDto> eventBySnapshotId)
        {
            if (rows == null || rows.Count == 0)
            {
                return null;
            }

            var orderedRows = rows
                .OrderBy(it => it.ChangedAt ?? DateTime.MinValue)
                .ThenBy(it => it.Id)
                .ToList();
            var first = orderedRows.First();
            var last = orderedRows.Last();
            var signalKeys = orderedRows
                .Select(it => it.SignalKey)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(it => it)
                .ToList();
            var groupNames = orderedRows
                .Select(it => it.GroupName)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(it => it)
                .ToList();
            var signalTypes = orderedRows
                .Select(it => it.SignalType)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(it => it)
                .ToList();

            StationShadowEventDto relatedEvent = null;
            if (!string.IsNullOrWhiteSpace(first.BundleId) && eventByBundleId.TryGetValue(first.BundleId.Trim(), out var bundleEvent))
            {
                relatedEvent = bundleEvent;
            }
            else if (!string.IsNullOrWhiteSpace(first.SnapshotId) && eventBySnapshotId.TryGetValue(first.SnapshotId.Trim(), out var snapshotEvent))
            {
                relatedEvent = snapshotEvent;
            }

            var sampleType = !string.IsNullOrWhiteSpace(first.BundleId)
                ? "Bundle"
                : (!string.IsNullOrWhiteSpace(first.SnapshotId) ? "Snapshot" : "ChangeSeries");

            var sampleLabel = !string.IsNullOrWhiteSpace(first.BundleId)
                ? $"{first.StationCode} 动作样本 {first.BundleId}"
                : (!string.IsNullOrWhiteSpace(first.SnapshotId)
                    ? $"{first.StationCode} 快照样本 {first.SnapshotId}"
                    : $"{first.StationCode} 变化串 {signalKeys.FirstOrDefault() ?? "Unknown"}");

            var summaryParts = new List<string>
            {
                $"{orderedRows.Count} 条持久化变化",
                $"{signalKeys.Count} 个信号",
            };
            if (groupNames.Count > 0)
            {
                summaryParts.Add($"{groupNames.Count} 个分组");
            }
            if (relatedEvent != null && !string.IsNullOrWhiteSpace(relatedEvent.EventType))
            {
                summaryParts.Add($"关联事件 {relatedEvent.EventType}");
            }

            return new StationShadowSampleLibraryItemDto
            {
                SampleKey = BuildMonitoringSampleKey(first),
                SampleType = sampleType,
                ProjectId = first.ProjectId,
                StationCode = first.StationCode,
                BundleId = first.BundleId,
                SnapshotId = first.SnapshotId,
                EventId = relatedEvent?.Id ?? first.EventId,
                StationMode = last.StationMode ?? first.StationMode,
                StartTime = first.ChangedAt ?? first.CreateTime,
                EndTime = last.ChangedAt ?? last.CreateTime,
                ChangeCount = orderedRows.Count,
                SignalCount = signalKeys.Count,
                SignalKeys = signalKeys,
                GroupNames = groupNames,
                SignalTypes = signalTypes,
                SampleLabel = sampleLabel,
                SampleSummary = string.Join(" / ", summaryParts),
                EventType = relatedEvent?.EventType,
                EventLevel = relatedEvent?.EventLevel,
                LatestTriggeredAt = relatedEvent?.TriggeredAt,
                DiagnosisStatus = relatedEvent?.DiagnosisStatus,
                LastBundleSignature = !string.IsNullOrWhiteSpace(relatedEvent?.LastBundleSignature)
                    ? relatedEvent.LastBundleSignature
                    : (string.Equals(sampleType, "Bundle", StringComparison.OrdinalIgnoreCase)
                        ? string.Join("|", signalKeys)
                        : null),
            };
        }

        private static string ResolveSampleBundleSignature(StationShadowSampleLibraryItemDto sample)
        {
            if (!string.IsNullOrWhiteSpace(sample?.LastBundleSignature))
            {
                return sample.LastBundleSignature.Trim();
            }

            var signalKeys = (sample?.SignalKeys ?? new List<string>())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Select(it => it.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(it => it)
                .ToList();

            return signalKeys.Count == 0 ? null : string.Join("|", signalKeys);
        }

        private static double CalculateGapMilliseconds(DateTime? currentEndTime, DateTime? nextStartTime)
        {
            if (!currentEndTime.HasValue || !nextStartTime.HasValue)
            {
                return 0;
            }

            return Math.Max(0, Math.Round((nextStartTime.Value - currentEndTime.Value).TotalMilliseconds, 2));
        }

        private static string BuildPatternTemplateName(string stationCode, string stationMode, string signature)
        {
            var normalizedStation = string.IsNullOrWhiteSpace(stationCode) ? "Station" : stationCode.Trim();
            var normalizedMode = string.IsNullOrWhiteSpace(stationMode) ? "Default" : stationMode.Trim();
            var normalizedSignature = string.IsNullOrWhiteSpace(signature)
                ? "Unknown"
                : signature.Replace("|", " -> ");

            return $"{normalizedStation}/{normalizedMode}/{normalizedSignature}";
        }

        private static string BuildPatternTemplateRemark(int supportCount, int nextPatternCount, string stationMode)
        {
            var modeText = string.IsNullOrWhiteSpace(stationMode) ? "默认模式" : stationMode.Trim();
            return $"由影子监控样本重建，模式={modeText}，支持样本={supportCount}，候选后继={nextPatternCount}";
        }

        private static int NormalizeLimit(int value, int defaultValue, int maxValue)
        {
            if (value <= 0)
            {
                return defaultValue;
            }

            return Math.Min(value, maxValue);
        }

        private static int ResolveDeviceHealthCandidateLimit(int value, int defaultValue, int maxValue)
        {
            if (value == 0)
            {
                return maxValue;
            }

            if (value < 0)
            {
                return defaultValue;
            }

            return Math.Min(value, maxValue);
        }

        private static bool ContainsIgnoreCase(string source, string keyword)
        {
            return !string.IsNullOrWhiteSpace(source)
                && !string.IsNullOrWhiteSpace(keyword)
                && source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        private async Task TryLoadPersistedStationsAsync(AnchorDiscoveryRequestDto request)
        {
            var needsPhysical = request.IncludePhysicalAnchors && request.PrecomputedPhysicalTagStations == null;
            var needsVariables =
                (request.IncludeVirtualAnchors || request.IncludeHandshakeAnchors || request.IncludeAstSeeds || request.IncludeAstCompileResults)
                && request.PrecomputedVariableStations == null;
            if (!needsPhysical && !needsVariables)
            {
                return;
            }

            var bindings = string.IsNullOrWhiteSpace(request.StationCode)
                ? await _stationAnchorBindingService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId)
                : await _stationAnchorBindingService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.StationCode == request.StationCode);

            if (bindings.Count == 0)
            {
                return;
            }

            if (needsPhysical)
            {
                var physical = bindings
                    .Select(it => new
                    {
                        Binding = it,
                        TagId = ParsePhysicalTagId(it.AnchorKey)
                    })
                    .Where(it => it.TagId.HasValue)
                    .GroupBy(it => it.TagId.Value)
                    .ToDictionary(
                        g => g.Key,
                        g => BuildInferenceFromBinding(g
                            .OrderByDescending(it => it.Binding.Confidence ?? 0m)
                            .ThenByDescending(it => it.Binding.Id)
                            .First()
                            .Binding));

                if (physical.Count > 0)
                {
                    request.PrecomputedPhysicalTagStations = physical;
                }
            }

            if (needsVariables)
            {
                var variables = bindings
                    .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                    .Where(it => !it.AnchorKey.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => BuildInferenceFromBinding(g
                            .OrderByDescending(it => it.Confidence ?? 0m)
                            .ThenByDescending(it => it.Id)
                            .First()),
                        StringComparer.OrdinalIgnoreCase);

                if (variables.Count > 0)
                {
                    request.PrecomputedVariableStations = variables;
                }
            }
        }

        private static StationInferenceResult BuildInferenceFromBinding(PLC_StationAnchorBindingEntity binding)
        {
            return new StationInferenceResult
            {
                StationCode = binding?.StationCode ?? string.Empty,
                Confidence = binding?.Confidence ?? 0m,
                InferenceSource = "PersistedBinding",
                InferenceDetail = binding?.AnchorType ?? string.Empty,
                Evidence = DeserializeEvidence(binding?.EvidenceJson)
            };
        }

        private static List<string> DeserializeEvidence(string evidenceJson)
        {
            if (string.IsNullOrWhiteSpace(evidenceJson))
            {
                return new List<string>();
            }

            try
            {
                return JsonHelper.DeserializeObject<List<string>>(evidenceJson) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static long? ParsePhysicalTagId(string sourceKey)
        {
            if (string.IsNullOrWhiteSpace(sourceKey) || !sourceKey.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return long.TryParse(sourceKey.Substring(4), out var tagId)
                ? tagId
                : null;
        }

        private static AnchorDiscoverySummary BuildSummary(
            AnchorDiscoveryResultDto dto,
            IEnumerable<AnchorCandidate> allAnchors)
        {
            var anchors = allAnchors.ToList();
            var confirmed = anchors.Where(a => a.DiscoveryStatus == AnchorDiscoveryStatus.Confirmed).ToList();

            var stationCount = dto.StationMap?.StationBuckets?.Count > 0
                ? dto.StationMap.StationBuckets
                    .GroupBy(g => StationCodeHeuristics.NormalizeStationCode(g.StationCode), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Sum(x => x.AnchorCount), StringComparer.OrdinalIgnoreCase)
                : confirmed
                    .Where(a => !string.IsNullOrWhiteSpace(a.StationCode))
                    .GroupBy(a => StationCodeHeuristics.NormalizeStationCode(a.StationCode), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            decimal avgConf = confirmed.Count > 0
                ? Math.Round(confirmed.Average(a => a.Confidence), 3)
                : 0m;

            var diagnostics = dto.LlmDiagnostics ?? new LlmDiscoveryDiagnostics();
            var llmClassified = Math.Max(
                diagnostics.LlmReturnedCount,
                diagnostics.ConfirmedByLlmCount + diagnostics.HandshakeByLlmCount + diagnostics.ReviewByLlmCount);

            if (llmClassified <= 0)
            {
                llmClassified = confirmed.Count(a => !string.IsNullOrWhiteSpace(a.SemanticLabel)
                    && a.Source != null
                    && a.Source.Contains("LLM", StringComparison.OrdinalIgnoreCase));
            }

            return new AnchorDiscoverySummary
            {
                TotalPhysical = dto.PhysicalAnchors.Count,
                TotalVirtual = dto.VirtualAnchors.Count + dto.VirtualAnchorReviewQueue.Count,
                TotalHandshake = dto.HandshakeAnchors.Count,
                CorePhysicalCount = dto.CorePhysicalAnchors.Count,
                CoreVirtualCount = dto.CoreVirtualAnchors.Count,
                ConfirmedCount = confirmed.Count,
                ReviewQueueSize = dto.VirtualAnchorReviewQueue.Count,
                RejectedCount = anchors.Count(a => a.DiscoveryStatus == AnchorDiscoveryStatus.Rejected),
                LlmClassifiedCount = llmClassified,
                StationAnchorCount = stationCount,
                StationBucketCount = dto.StationMap?.StationBuckets?.Count ?? 0,
                StationConflictCount = dto.StationMap?.StationConflicts?.Count ?? 0,
                OrphanAnchorCount = dto.StationMap?.OrphanAnchors?.Count ?? 0,
                UnstationedCount = confirmed.Count(a => string.IsNullOrWhiteSpace(a.StationCode)),
                AverageConfidence = avgConf,
                ColoredLogicUnitCount = dto.StationColoringResult.Count,
                ConflictLogicUnitCount = dto.StationColoringResult.Count(l => l.HasConflict)
            };
        }

        private static List<AnchorCandidate> CollectStationMapInputAnchors(AnchorDiscoveryResultDto dto)
        {
            var anchors = new List<AnchorCandidate>();
            anchors.AddRange(dto.PhysicalAnchors);
            anchors.AddRange(dto.VirtualAnchors);
            anchors.AddRange(dto.VirtualAnchorReviewQueue);
            anchors.AddRange(dto.HandshakeAnchors);
            return anchors;
        }

        private static List<AnchorStationAssignment> BuildAssignmentsFromStationItems(StationExtractionResultDto dto)
        {
            return dto.PhysicalTagItems
                .Concat(dto.VariableGroupItems)
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode) && !string.IsNullOrWhiteSpace(it.VariableName))
                .Select(it => new AnchorStationAssignment
                {
                    AnchorKey = it.SourceKey,
                    VariableName = it.VariableName,
                    AnchorType = it.SourceType,
                    StationCode = it.StationCode,
                    Confidence = it.Confidence,
                    Evidence = it.Evidence.Take(6).ToList()
                })
                .ToList();
        }

        private static StationExtractionSummary BuildStationExtractionSummary(StationExtractionResultDto dto)
        {
            var allItems = dto.PhysicalTagItems.Concat(dto.VariableGroupItems).ToList();
            var taggedPhysical = dto.PhysicalTagItems.Count(it => !string.IsNullOrWhiteSpace(it.StationCode));
            var taggedVariables = dto.VariableGroupItems.Count(it => !string.IsNullOrWhiteSpace(it.StationCode));

            return new StationExtractionSummary
            {
                StationCount = dto.StationBuckets.Count,
                PhysicalTagCount = dto.PhysicalTagItems.Count,
                VariableGroupCount = dto.VariableGroupItems.Count,
                TaggedPhysicalTagCount = taggedPhysical,
                TaggedVariableGroupCount = taggedVariables,
                UnassignedPhysicalTagCount = dto.PhysicalTagItems.Count - taggedPhysical,
                UnassignedVariableGroupCount = dto.VariableGroupItems.Count - taggedVariables,
                PathGroupLlmCount = allItems.Count(it => string.Equals(it.StationSource, "LLM", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(it.StationSourceDetail)
                    && it.StationSourceDetail.StartsWith("PathGroup:", StringComparison.OrdinalIgnoreCase)),
                TableGroupLlmCount = allItems.Count(it => string.Equals(it.StationSource, "LLM", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(it.StationSourceDetail)
                    && it.StationSourceDetail.StartsWith("TableGroup:", StringComparison.OrdinalIgnoreCase)),
                SingleItemLlmCount = allItems.Count(it => string.Equals(it.StationSource, "LLM", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(it.StationSourceDetail, "SingleItem", StringComparison.OrdinalIgnoreCase)),
                RuleInferenceCount = allItems.Count(it => string.Equals(it.StationSource, "Rule", StringComparison.OrdinalIgnoreCase)),
                ColoredLogicUnitCount = dto.StationColoringResult.Count,
                ConflictLogicUnitCount = dto.StationColoringResult.Count(it => it.HasConflict)
            };
        }

        private static AnchorDiscoveryRequestDto ClonePhysicalOnlyRequest(AnchorDiscoveryRequestDto request)
        {
            return new AnchorDiscoveryRequestDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode,
                BlockId = request.BlockId,
                IncludeAuxiliary = request.IncludeAuxiliary,
                IncludePhysicalAnchors = request.IncludePhysicalAnchors,
                IncludeVirtualAnchors = false,
                IncludeHandshakeAnchors = false,
                IncludeAstSeeds = false,
                IncludeAstCompileResults = false,
                RawContentMaxLength = request.RawContentMaxLength,
                UseLlmAnnotation = request.UseLlmAnnotation,
                IncludeStationColoring = false,
                PersistStationMap = false,
                PersistAstResults = false,
                PrecomputedPhysicalTagStations = request.PrecomputedPhysicalTagStations,
                PrecomputedVariableStations = request.PrecomputedVariableStations
            };
        }

        /// <summary>
        /// 后置工位补全：为 StationCode 为空的语义 findings 回填工位。
        /// 策略1: 通过变量名在 PrecomputedVariableStations 中查找
        /// 策略2: 通过 BlockName 在同批 findings 中找同块已有工位的多数投票
        /// </summary>
        private static void BackfillFindingStationCodes(
            List<AstSemanticFinding> findings,
            AnchorDiscoveryRequestDto request)
        {
            if (findings == null || findings.Count == 0) return;

            var variableStations = request?.PrecomputedVariableStations;
            var backfilledCount = 0;

            // 先收集同块已有工位的 findings，供策略2使用
            var stationByBlock = findings
                .Where(f => !string.IsNullOrWhiteSpace(f.BlockName) && !string.IsNullOrWhiteSpace(f.StationCode))
                .GroupBy(f => f.BlockName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(f => f.StationCode, StringComparer.OrdinalIgnoreCase)
                          .OrderByDescending(sg => sg.Count())
                          .First().Key,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var finding in findings)
            {
                if (!string.IsNullOrWhiteSpace(finding.StationCode)) continue;

                // 策略1: 变量→工位映射
                if (variableStations != null &&
                    !string.IsNullOrWhiteSpace(finding.VariableName) &&
                    variableStations.TryGetValue(finding.VariableName, out var station) &&
                    !string.IsNullOrWhiteSpace(station?.StationCode))
                {
                    finding.StationCode = station.StationCode;
                    backfilledCount++;
                    continue;
                }

                // 策略2: 同块多数投票
                if (!string.IsNullOrWhiteSpace(finding.BlockName) &&
                    stationByBlock.TryGetValue(finding.BlockName, out var blockStation))
                {
                    finding.StationCode = blockStation;
                    backfilledCount++;
                }
            }
        }

        private static void ApplyAstSemanticFindings(AnchorDiscoveryResultDto dto, AnchorDiscoveryRequestDto request)
        {
            var findings = dto.AstSemanticFindings ?? new List<AstSemanticFinding>();

            dto.VirtualAnchors = request.IncludeVirtualAnchors
                ? findings
                    .Where(it => string.Equals(it.Category, "VirtualQPoint", StringComparison.OrdinalIgnoreCase))
                    .Select(BuildSemanticAnchor)
                    .OrderByDescending(it => it.Confidence)
                    .ThenBy(it => it.VariableName)
                    .ToList()
                : new List<AnchorCandidate>();

            dto.HandshakeAnchors = request.IncludeHandshakeAnchors
                ? findings
                    .Where(it => string.Equals(it.Category, "HandshakeSignal", StringComparison.OrdinalIgnoreCase))
                    .Select(BuildSemanticAnchor)
                    .OrderByDescending(it => it.Confidence)
                    .ThenBy(it => it.VariableName)
                    .ToList()
                : new List<AnchorCandidate>();

            dto.VirtualAnchorReviewQueue = new List<AnchorCandidate>();
            dto.CorePhysicalAnchors = SelectTopAnchors(dto.PhysicalAnchors, 12);
            dto.CoreVirtualAnchors = SelectTopAnchors(dto.VirtualAnchors, 16);
            dto.Warnings = dto.Warnings
                .Concat(findings.Count == 0 && request.IncludeAstCompileResults
                    ? new[] { "AST 语义阶段未返回虚拟Q/握手结果，请检查编译覆盖率或 LLM 响应。" }
                    : Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static AnchorCandidate BuildSemanticAnchor(AstSemanticFinding finding)
        {
            return new AnchorCandidate
            {
                AnchorKey = $"ast-semantic:{finding.LogicUnitId}:{finding.VariableName}:{finding.Category}",
                AnchorType = string.Equals(finding.Category, "HandshakeSignal", StringComparison.OrdinalIgnoreCase)
                    ? "HandshakeState"
                    : "VirtualAction",
                VariableName = finding.VariableName,
                StationCode = finding.StationCode,
                SemanticLabel = string.IsNullOrWhiteSpace(finding.SemanticLabel) ? finding.VariableName : finding.SemanticLabel,
                Source = "AST+LLM",
                Confidence = finding.Confidence,
                DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                PrimaryReason = finding.Reason,
                LogicSourceType = "WriterLogicUnit",
                Evidence = BuildSemanticEvidence(finding)
            };
        }

        private static List<string> BuildSemanticEvidence(AstSemanticFinding finding)
        {
            var evidence = new List<string>();
            if (!string.IsNullOrWhiteSpace(finding.BlockName))
            {
                evidence.Add($"块: {finding.BlockName}");
            }

            if (!string.IsNullOrWhiteSpace(finding.Reason))
            {
                evidence.Add($"LLM: {finding.Reason}");
            }

            if (finding.RelatedIOTags?.Count > 0)
            {
                evidence.Add($"关联物理点: {string.Join(", ", finding.RelatedIOTags.Take(6))}");
            }

            if (!string.IsNullOrWhiteSpace(finding.IoMapping))
            {
                evidence.Add($"IoMapping: {finding.IoMapping}");
            }

            return evidence;
        }

        #region BlockInterface 变量路径补全与结构体展开

        /// <summary>
        /// 所有虚拟Q点/握手信号都必须去 BlockInterface 表里匹配一次：
        /// 用 BlockName + StripScopePrefix(VariablePath) 作为权威全路径。
        /// 若变量为结构体(Struct/UDT)，递归展开为叶子标量变量。
        /// </summary>
        private async Task<List<AstSemanticFinding>> ResolveAndExpandSemanticFindingsAsync(
            List<AstSemanticFinding> findings, long projectId)
        {
            if (findings == null || findings.Count == 0)
                return findings ?? new List<AstSemanticFinding>();

            var toResolve = findings
                .Where(f => string.Equals(f.Category, "VirtualQPoint", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(f.Category, "HandshakeSignal", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var passThrough = findings
                .Where(f => !string.Equals(f.Category, "VirtualQPoint", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(f.Category, "HandshakeSignal", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (toResolve.Count == 0)
                return findings;

            // 只加载 InstanceDB / GlobalDB（ProgrammingLanguage=DB）
            var allBlocks = await _blockService.GetListAsync(b =>
                b.IsDeleted == false && b.ProjectId == projectId
                && !string.IsNullOrWhiteSpace(b.BlockName)
                && b.ProgrammingLanguage != null
                && b.ProgrammingLanguage.ToUpper() == "DB"
                && b.BlockType != null
                && (b.BlockType == "InstanceDB" || b.BlockType == "GlobalDB"));
            if (allBlocks.Count == 0)
                return findings;

            var blockIds = allBlocks.Select(b => b.Id).ToList();
            var allInterfaces = await _blockInterfaceService.GetListAsync(i =>
                i.IsDeleted == false && i.BlockId.HasValue && blockIds.Contains(i.BlockId.Value)
                && !string.IsNullOrWhiteSpace(i.VariablePath));

            if (allInterfaces.Count == 0)
                return findings;

            var blockById = allBlocks.ToDictionary(b => b.Id, b => b);
            var blockByName = new Dictionary<string, PLC_BlockEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in allBlocks)
            {
                if (!blockByName.ContainsKey(b.BlockName))
                    blockByName[b.BlockName] = b;
            }

            var ifacesByBlockId = allInterfaces
                .GroupBy(i => i.BlockId.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            // 全量索引：BlockName.StripScopePrefix(VariablePath) → (block, iface)
            var canonicalIndex = new Dictionary<string, (PLC_BlockEntity block, PLC_BlockInterfaceEntity iface)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var iface in allInterfaces)
            {
                if (!iface.BlockId.HasValue || !blockById.TryGetValue(iface.BlockId.Value, out var block))
                    continue;
                var stripped = StripScopePrefix(iface.VariablePath);
                var key = $"{block.BlockName}.{stripped}";
                if (!canonicalIndex.ContainsKey(key))
                    canonicalIndex[key] = (block, iface);

                // 额外注册数组折叠版本：V90.V90[1].X → V90[1].X 以便匹配 PLC 代码中的短写法
                var collapsed = CollapseArrayPath(stripped);
                if (collapsed != stripped)
                {
                    var collapsedKey = $"{block.BlockName}.{collapsed}";
                    if (!canonicalIndex.ContainsKey(collapsedKey))
                        canonicalIndex[collapsedKey] = (block, iface);
                }
            }

            var resolved = new List<AstSemanticFinding>(passThrough);
            foreach (var finding in toResolve)
            {
                var varName = (finding.VariableName ?? "").Trim().Trim('"').Replace("\"", "");
                if (string.IsNullOrWhiteSpace(varName))
                {
                    resolved.Add(finding);
                    continue;
                }

                // 策略1/2 合并成本地函数（2026-10-07：具体化成功后可复用重跑同一套匹配）
                // 策略1：直接查全量索引（变量名本身就是 BlockName.Path 格式）
                // 策略2：解析 BlockName 前缀，在该 Block 的接口中按路径匹配
                PLC_BlockEntity matchedBlock = null;
                PLC_BlockInterfaceEntity matchedIface = null;

                (PLC_BlockEntity, PLC_BlockInterfaceEntity)? TryMatchCanonical(string name)
                {
                    if (canonicalIndex.TryGetValue(name, out var directHit))
                    {
                        return (directHit.block, directHit.iface);
                    }

                    var dotIdx = name.IndexOf('.');
                    if (dotIdx > 0 && dotIdx < name.Length - 1)
                    {
                        var prefix = name.Substring(0, dotIdx);
                        if (blockByName.TryGetValue(prefix, out var prefixBlock)
                            && ifacesByBlockId.TryGetValue(prefixBlock.Id, out var blockIfaces))
                        {
                            var ifaceMatch = MatchInterfaceByPath(name.Substring(dotIdx + 1), blockIfaces);
                            if (ifaceMatch != null)
                            {
                                return (prefixBlock, ifaceMatch);
                            }
                        }
                    }

                    return null;
                }

                var canonicalMatch = TryMatchCanonical(varName);
                if (canonicalMatch != null)
                {
                    (matchedBlock, matchedIface) = canonicalMatch.Value;
                }

                // 策略3（NEW 2026-10-07）：LocalVariable 形参链/实例 DB 具体化（FbLocalVariableConcretizer）。
                // 必须排在盲搜（下方策略4）之前：S_AutoRunMode 这类 GRAPH 模板 STAT 在本项目十几个
                // 实例 DB 接口里同名存在，盲搜会错配到错误的 DB。具体化以 finding.BlockName 为锚点：
                //   形参链：#I_Mode.DryRunMode → 沿调用绑定向上 → DB10050_Mode.M010_Mode.DryRunMode
                //   STAT 镜像：#S_AutoRunMode → ai_plc_dbfbinding 唯一实例 DB → IDB150_xxx.S_AutoRunMode
                // 不可唯一具体化时返回 null，后续策略行为与改造前完全一致（零回归）。
                if (matchedIface == null)
                {
                    var fbName = (finding.BlockName ?? "").Trim();
                    var localPath = varName.StartsWith("#") ? varName.Substring(1) : varName;
                    if (!string.IsNullOrWhiteSpace(fbName) && !string.IsNullOrWhiteSpace(localPath))
                    {
                        var concretized = await _fbLocalVariableConcretizer
                            .TryConcretizeAsync(projectId, fbName, localPath).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(concretized))
                        {
                            var concretizedMatch = TryMatchCanonical(concretized);
                            if (concretizedMatch != null)
                            {
                                (matchedBlock, matchedIface) = concretizedMatch.Value;
                            }
                        }
                    }
                }

                // 策略4：无 Block 前缀，在所有 Block 中搜索路径匹配
                if (matchedIface == null)
                {
                    foreach (var block in allBlocks)
                    {
                        if (!ifacesByBlockId.TryGetValue(block.Id, out var blockIfaces))
                            continue;
                        var match = MatchInterfaceByPath(varName, blockIfaces);
                        if (match != null)
                        {
                            matchedBlock = block;
                            matchedIface = match;
                            break;
                        }
                    }
                }

                // 策略5：LocalVariable → 对应 InstanceDB（旧名字推导兜底，仅当上方具体化未能解析时）
                // FB 的本地变量在对应的 InstanceDB 中结构完全一致，名称规则 FB→DB 直接替换。
                // 例如：变量 Valve[7].FwdPmt 在 FB2001BB 内 → DB2001BB.Valve[7].FwdPmt
                // 注意：按 "DB"+FB号 推导，对本项目 IDB150_xxx 命名不生效——权威路径是上方策略3
                // （查 ai_plc_dbfbinding 绑定），本兜底仅覆盖老项目 FB/DB 同号命名习惯。
                if (matchedIface == null)
                {
                    var fbName = (finding.BlockName ?? "").Trim();
                    var localPath = varName.StartsWith("#") ? varName.Substring(1) : varName;

                    if (!string.IsNullOrWhiteSpace(fbName) && fbName.StartsWith("FB", StringComparison.OrdinalIgnoreCase))
                    {
                        var dbName = "DB" + fbName.Substring(2);
                        if (blockByName.TryGetValue(dbName, out var dbBlock)
                            && ifacesByBlockId.TryGetValue(dbBlock.Id, out var dbIfaces))
                        {
                            var match = MatchInterfaceByPath(localPath, dbIfaces);
                            if (match != null)
                            {
                                matchedBlock = dbBlock;
                                matchedIface = match;
                            }
                        }
                    }
                }

                // 未匹配到：保留原始 finding
                if (matchedIface == null || matchedBlock == null)
                {
                    resolved.Add(finding);
                    continue;
                }

                // 结构体/UDT → 展开为叶子标量变量
                if (IsStructOrUdtType(matchedIface.DataType))
                {
                    var leaves = FindLeafDescendants(matchedIface.Id,
                        ifacesByBlockId.GetValueOrDefault(matchedBlock.Id, new List<PLC_BlockInterfaceEntity>()));
                    if (leaves.Count == 0)
                    {
                        resolved.Add(finding);
                        continue;
                    }

                    foreach (var leaf in leaves.Take(30))
                    {
                        var fullPath = BuildFullVariablePath(matchedBlock.BlockName, leaf.VariablePath);
                        resolved.Add(CloneSemanticFinding(finding, fullPath,
                            $"{finding.SemanticLabel}·{leaf.VariableName}"));
                    }
                }
                else
                {
                    // 标量 → 使用 BlockInterface 权威全路径
                    var fullPath = BuildFullVariablePath(matchedBlock.BlockName, matchedIface.VariablePath);
                    finding.VariableName = fullPath;
                    resolved.Add(finding);
                }
            }

            return resolved;
        }

        /// <summary>
        /// 在指定 Block 的接口列表中匹配路径。
        /// 尝试多种方式：直接匹配、去Scope前缀、加Static.前缀、数组路径展开/折叠。
        /// </summary>
        private static PLC_BlockInterfaceEntity MatchInterfaceByPath(
            string path, List<PLC_BlockInterfaceEntity> interfaces)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // 直接匹配 + 去 Scope 匹配
            foreach (var iface in interfaces)
            {
                if (string.IsNullOrWhiteSpace(iface.VariablePath)) continue;
                if (string.Equals(iface.VariablePath, path, StringComparison.OrdinalIgnoreCase))
                    return iface;
                if (string.Equals(StripScopePrefix(iface.VariablePath), path, StringComparison.OrdinalIgnoreCase))
                    return iface;
            }

            // 加 Static. 前缀
            var staticMatch = interfaces.FirstOrDefault(i =>
                string.Equals(i.VariablePath, "Static." + path, StringComparison.OrdinalIgnoreCase));
            if (staticMatch != null) return staticMatch;

            // 数组路径展开匹配：V90[1].X → V90.V90[1].X（BlockInterface 层级格式）
            var expanded = ExpandArrayPath(path);
            if (expanded != path)
            {
                foreach (var iface in interfaces)
                {
                    if (string.IsNullOrWhiteSpace(iface.VariablePath)) continue;
                    var stripped = StripScopePrefix(iface.VariablePath);
                    if (string.Equals(stripped, expanded, StringComparison.OrdinalIgnoreCase))
                        return iface;
                    if (string.Equals(iface.VariablePath, "Static." + expanded, StringComparison.OrdinalIgnoreCase))
                        return iface;
                }
            }

            return null;
        }

        private static readonly string[] ScopePrefixes =
            { "Static.", "Input.", "Output.", "InOut.", "Temp.", "Constant." };

        private static string StripScopePrefix(string variablePath)
        {
            if (string.IsNullOrWhiteSpace(variablePath)) return variablePath ?? string.Empty;
            foreach (var prefix in ScopePrefixes)
            {
                if (variablePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return variablePath.Substring(prefix.Length);
            }
            return variablePath;
        }

        /// <summary>
        /// 数组路径展开：PLC代码短写法 → BlockInterface层级格式。
        /// 将 Name[i] 展开为 Name.Name[i]，支持多级数组。
        /// 例如：V90[1].CTRL_IN.X → V90.V90[1].CTRL_IN.X
        ///       A[2].B[3].C       → A.A[2].B.B[3].C
        /// </summary>
        private static string ExpandArrayPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.Contains('['))
                return path;

            var parts = path.Split('.');
            var result = new List<string>();
            foreach (var part in parts)
            {
                var bracketIdx = part.IndexOf('[');
                if (bracketIdx > 0)
                {
                    // Name[i] → Name.Name[i]
                    var arrayName = part.Substring(0, bracketIdx);
                    result.Add(arrayName);
                    result.Add(part);
                }
                else
                {
                    result.Add(part);
                }
            }
            var expanded = string.Join(".", result);
            return expanded;
        }

        /// <summary>
        /// 数组路径折叠：BlockInterface层级格式 → PLC代码短写法。
        /// 将 Name.Name[i] 折叠为 Name[i]，支持多级数组。
        /// 例如：V90.V90[1].CTRL_IN.X → V90[1].CTRL_IN.X
        /// </summary>
        private static string CollapseArrayPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.Contains('['))
                return path;

            var parts = path.Split('.');
            var result = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                // 如果下一个 part 是 当前part名[索引] 的格式，则跳过当前（折叠）
                if (i + 1 < parts.Length)
                {
                    var next = parts[i + 1];
                    var bracketIdx = next.IndexOf('[');
                    if (bracketIdx > 0
                        && string.Equals(next.Substring(0, bracketIdx), parts[i], StringComparison.OrdinalIgnoreCase))
                    {
                        // 跳过当前 part，让下一轮直接加 Name[i]
                        continue;
                    }
                }
                result.Add(parts[i]);
            }
            return string.Join(".", result);
        }

        private static bool IsStructOrUdtType(string dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return false;
            var dt = dataType.Trim();
            return string.Equals(dt, "Struct", StringComparison.OrdinalIgnoreCase)
                || dt.StartsWith("UDT_", StringComparison.OrdinalIgnoreCase);
        }

        private static List<PLC_BlockInterfaceEntity> FindLeafDescendants(
            long parentId, List<PLC_BlockInterfaceEntity> allInterfaces)
        {
            var leaves = new List<PLC_BlockInterfaceEntity>();
            var queue = new Queue<long>();
            queue.Enqueue(parentId);

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                var children = allInterfaces.Where(i => i.ParentId == currentId).ToList();
                foreach (var child in children)
                {
                    if (child.OffsetByte == null) continue;
                    if (IsStructOrUdtType(child.DataType))
                    {
                        queue.Enqueue(child.Id);
                    }
                    else
                    {
                        leaves.Add(child);
                    }
                }
            }

            return leaves;
        }

        private static string BuildFullVariablePath(string blockName, string variablePath)
        {
            var stripped = StripScopePrefix(variablePath);
            if (string.IsNullOrWhiteSpace(blockName) || string.IsNullOrWhiteSpace(stripped))
                return stripped ?? string.Empty;
            return $"{blockName}.{stripped}";
        }

        private static AstSemanticFinding CloneSemanticFinding(
            AstSemanticFinding original, string newVariableName, string newSemanticLabel)
        {
            return new AstSemanticFinding
            {
                LogicUnitId = original.LogicUnitId,
                BlockId = original.BlockId,
                BlockName = original.BlockName,
                StationCode = original.StationCode,
                VariableName = newVariableName,
                Category = original.Category,
                SemanticLabel = newSemanticLabel,
                Reason = original.Reason,
                Confidence = original.Confidence,
                RelatedIOTags = original.RelatedIOTags?.ToList() ?? new List<string>(),
                IoMapping = original.IoMapping
            };
        }

        #endregion

        #region 全线停机诊断

        /// <summary>
        /// 全线停机诊断：跨所有 PLC Project 并行排查各工位，汇总到 LLM 做跨工位根因分析。
        /// 支持 Hangfire 后台调用 + SignalR 逐工位推送进度。
        /// </summary>
        public async Task<HttpResponseResultModel<LineDiagnosisResultDto>> DiagnoseLineStoppageAsync(
            LineDiagnosisRequestDto request,
            string taskId = null)
        {
            var businessProgress = new LineDiagnosisBusinessProgress(
                new DiagnosisProgressScope(
                    NullDiagnosisProgressReporter.Instance,
                    0,
                    100,
                    "line_stoppage"));
            return await businessProgress.RunToTerminalAsync(
                _ => DiagnoseLineStoppageCoreAsync(
                    request,
                    taskId,
                    enableSignalR: true,
                    businessProgress: businessProgress),
                response => response?.IsSuccess == true);
        }

        /// <summary>
        /// MCP 产线诊断入口：执行与页面相同的核心流程，并把页面过程事件交给调用方实时传输。
        /// </summary>
        public async Task<HttpResponseResultModel<LineDiagnosisResultDto>> DiagnoseLineStoppageForMcpAsync(
            LineDiagnosisRequestDto request,
            string taskId = null,
            Func<LineDiagnosisProgressEventDto, Task> onEvent = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request ??= new LineDiagnosisRequestDto();
            taskId = string.IsNullOrWhiteSpace(taskId)
                ? (string.IsNullOrWhiteSpace(request.TaskId)
                    ? $"line_mcp_{DateTime.Now:yyyyMMddHHmmssfff}"
                    : request.TaskId.Trim())
                : taskId.Trim();
            request.TaskId = taskId;

            var businessProgress = new LineDiagnosisBusinessProgress(
                new DiagnosisProgressScope(
                    NullDiagnosisProgressReporter.Instance,
                    0,
                    100,
                    "line_stoppage"),
                onEvent);

            return await businessProgress.RunToTerminalAsync(
                operationCancellationToken => DiagnoseLineStoppageCoreAsync(
                    request,
                    taskId,
                    enableSignalR: false,
                    cancellationToken: operationCancellationToken,
                    businessProgress: businessProgress),
                response => response?.IsSuccess == true,
                cancellationToken);
        }

        public async Task<HttpResponseResultModel<AgentLineDiagnosisResponseDto>> DiagnoseLineStoppageForAgentAsync(
            AgentLineDiagnosisRequestDto request,
            string taskId = null,
            Func<LineDiagnosisProgressEventDto, Task> onEvent = null,
            CancellationToken cancellationToken = default,
            IDiagnosisProgressReporter progressReporter = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request ??= new AgentLineDiagnosisRequestDto();
            taskId = string.IsNullOrWhiteSpace(taskId)
                ? (string.IsNullOrWhiteSpace(request.TaskId)
                    ? $"line_agent_{DateTime.Now:yyyyMMddHHmmssfff}"
                    : request.TaskId.Trim())
                : taskId.Trim();
            var processEvents = new List<LineDiagnosisProgressEventDto>();
            var collectProcess = false;

            async Task ForwardEvent(LineDiagnosisProgressEventDto evt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evt == null) return;
                evt.TaskId = string.IsNullOrWhiteSpace(evt.TaskId) ? taskId : evt.TaskId;
                if (collectProcess)
                {
                    processEvents.Add(evt);
                }

                if (onEvent != null)
                {
                    await onEvent(evt);
                }
            }

            var lineProgress = new DiagnosisProgressScope(
                progressReporter ?? NullDiagnosisProgressReporter.Instance,
                0,
                100,
                "line_stoppage");
            var businessProgress = new LineDiagnosisBusinessProgress(lineProgress, ForwardEvent);

            return await businessProgress.RunToTerminalAsync(
                async operationCancellationToken =>
                {
                    var outputMode = ResolveAgentOutputMode(string.IsNullOrWhiteSpace(request.Mode)
                        ? request.OutputMode
                        : request.Mode);
                    var hasExplicitLlmMode = !string.IsNullOrWhiteSpace(request.LlmMode);
                    var llmMode = ResolveAgentLlmMode(request.LlmMode);
                    if (string.Equals(outputMode, "FinalNodesOnly", StringComparison.OrdinalIgnoreCase)
                        && !hasExplicitLlmMode)
                    {
                        llmMode = "None";
                    }

                    var lineRequest = BuildAgentLineDiagnosisCoreRequest(request, llmMode, outputMode);
                    lineRequest.TaskId = taskId;
                    collectProcess = request.IncludeProcess
                        || string.Equals(outputMode, "ProcessAndSummary", StringComparison.OrdinalIgnoreCase);

                    var diagnosisResponse = await DiagnoseLineStoppageCoreAsync(
                        lineRequest,
                        taskId,
                        enableSignalR: false,
                        cancellationToken: operationCancellationToken,
                        businessProgress: businessProgress);

                    var result = diagnosisResponse.BackResult;
                    var agentResult = BuildAgentLineDiagnosisResponse(
                        request,
                        result,
                        taskId,
                        outputMode,
                        llmMode,
                        processEvents);

                    return new HttpResponseResultModel<AgentLineDiagnosisResponseDto>
                    {
                        IsSuccess = diagnosisResponse.IsSuccess,
                        ErrorMessage = diagnosisResponse.ErrorMessage,
                        ExceptionMessage = diagnosisResponse.ExceptionMessage,
                        BackResult = agentResult,
                    };
                },
                response => response?.IsSuccess == true,
                cancellationToken);
        }

        /// <summary>
        /// 排查前的工站绑定提示：只报自动规则拿不准的 GRAPH 块（没有任何工站、或工站证据冲突），自动判断明确的不报
        /// （规则见 StationBlockBindingRules.BuildCoverageWarnings）。
        /// </summary>
        private async Task<List<string>> BuildBindingCoverageWarningsAsync(List<long> projectIds, IEnumerable<string> stationCodes)
        {
            if (projectIds == null || projectIds.Count == 0)
                return new List<string>();

            var graphBlocks = await _blockService.GetListAsync(block =>
                block.IsDeleted == false
                && block.ProgrammingLanguage == "GRAPH"
                && block.ProjectId != null
                && projectIds.Contains(block.ProjectId.Value));
            if (graphBlocks.Count == 0)
                return new List<string>();

            var bindings = await _stationBlockBindingService.GetListAsync(binding =>
                binding.IsDeleted == false
                && binding.ProjectId != null
                && projectIds.Contains(binding.ProjectId.Value));

            var scope = new HashSet<string>(
                (stationCodes ?? Enumerable.Empty<string>()).Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim()),
                StringComparer.OrdinalIgnoreCase);

            return StationBlockBindingRules.BuildCoverageWarnings(
                graphBlocks
                    .Where(block => block.ProjectId.HasValue && !string.IsNullOrWhiteSpace(block.BlockName))
                    .Select(block => new GraphBlockRef(block.Id, block.ProjectId!.Value, block.BlockName, block.StationCode)),
                bindings,
                scope);
        }

        private async Task<HttpResponseResultModel<LineDiagnosisResultDto>> DiagnoseLineStoppageCoreAsync(
            LineDiagnosisRequestDto request,
            string taskId = null,
            bool enableSignalR = true,
            CancellationToken cancellationToken = default,
            LineDiagnosisBusinessProgress businessProgress = null)
        {
            var response = new HttpResponseResultModel<LineDiagnosisResultDto>();
            var hasSignalR = enableSignalR && !string.IsNullOrWhiteSpace(taskId);
            businessProgress ??= new LineDiagnosisBusinessProgress(
                new DiagnosisProgressScope(
                    NullDiagnosisProgressReporter.Instance,
                    0,
                    100,
                    "line_stoppage"));

            if (request == null)
            {
                response.IsSuccess = false;
                response.ErrorMessage = LineDiagnosisI18n.T("err.requestNull", LineDiagnosisI18n.ZhCn);
                if (hasSignalR) await _broadcastService.BroadcastLineDiagnosisFailedAsync(taskId, response.ErrorMessage);
                return response;
            }

            request.OutputLanguage = LineDiagnosisLlmService.NormalizeOutputLanguage(request.OutputLanguage);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new LineDiagnosisResultDto
            {
                DiagnosedAt = DateTime.Now,
                OutputLanguage = request.OutputLanguage,
            };
            // 2026-09-23 整次排查的分阶段耗时，随结果回前端（标题栏显示），不必再去服务器翻日志定位慢在哪
            var phaseTimings = new List<string>();
            long lastPhaseMark = 0;
            void MarkPhase(string name)
            {
                var now = sw.ElapsedMilliseconds;
                phaseTimings.Add($"{name}:{now - lastPhaseMark}ms");
                lastPhaseMark = now;
                result.TimingBreakdown = string.Join(" ", phaseTimings);
            }

            // ── 全局超时保护 ──
            var timeoutSec = request.TimeoutSeconds > 0
                ? request.TimeoutSeconds
                : (request.UseAgentDiagnosis ? 600 : 300); // Agent 默认 10 分钟，传统 5 分钟
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            // 2026-09-23 本次排查内同一批点位只读一次（各工位并行排查、同一工位的多轮取值共用）
            using var signalReadScope = StopCauseSignalReadScope.Begin();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var operationCancellationToken = linkedCts.Token;
            ConcurrentBag<StationDiagnosisSummaryDto> summaries = null;

            try
            {
                // 1. 确定要排查的 Project 列表
                var projectIds = request.ProjectIds?
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                if (projectIds == null || projectIds.Count == 0)
                {
                    var allProjects = await _projectService.GetListAsync(it => it.IsDeleted == false);
                    projectIds = allProjects.Select(p => p.Id).ToList();
                }

                if (projectIds.Count == 0)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = LineDiagnosisI18n.T("err.noProjects", request.OutputLanguage);
                    response.BackResult = result;
                    if (hasSignalR) await _broadcastService.BroadcastLineDiagnosisFailedAsync(taskId, response.ErrorMessage);
                    return response;
                }

                result.ProjectIds = projectIds;

                // Read operating mode before station/constraint diagnosis, including when constraints are unchecked.
                IReadOnlyList<Original353ManualModeRead> operatingModes = [];
                async Task RefreshOperatingModesAsync()
                {
                    if (_diagnosticStrategyFactoryApp == null) return;
                    try
                    {
                        operatingModes = await _diagnosticStrategyFactoryApp.ReadOperatingModesAsync(projectIds, operationCancellationToken);
                        result.OperatingModeReport = Original353OperatingModeSummary.Report(operatingModes);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception)
                    {
                        operatingModes = [];
                        result.OperatingModeReport = "当前未能确认运行模式，请检查模式信号。";
                    }
                }
                await RefreshOperatingModesAsync();
                await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto {
                    Type = "operating_mode_checked", TaskId = taskId,
                    Message = string.IsNullOrWhiteSpace(result.OperatingModeReport) ? "运行模式检查完成。" : result.OperatingModeReport,
                }, operationCancellationToken);

                // 2. 跨所有 Project 收集工位
                var stationProjectPairs = new List<(long ProjectId, string StationCode)>();

                var filterStationCodes = request.StationCodes?
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var projectId in projectIds)
                {
                    var stations = await GetMonitoringStationsAsync(projectId);
                    foreach (var s in stations)
                    {
                        if (string.IsNullOrWhiteSpace(s.StationCode)) continue;
                        // 未在 plc_station_shadow_config 中启用的工位跳过（Enabled=false 或无配置记录）
                        if (!s.Enabled) continue;
                        if (filterStationCodes != null && filterStationCodes.Count > 0
                            && !filterStationCodes.Contains(s.StationCode.Trim())) continue;

                        stationProjectPairs.Add((projectId, s.StationCode.Trim()));
                    }
                }

                stationProjectPairs = stationProjectPairs
                    .Distinct()
                    .OrderBy(p => p.StationCode, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.ProjectId)
                    .ToList();

                if (stationProjectPairs.Count == 0)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = LineDiagnosisI18n.T("err.noStations", request.OutputLanguage);
                    response.BackResult = result;
                    if (hasSignalR) await _broadcastService.BroadcastLineDiagnosisFailedAsync(taskId, response.ErrorMessage);
                    return response;
                }

                result.TotalStations = stationProjectPairs.Count;

                // 2026-10-15 排查前提示（自动为主）：只在有 GRAPH 块没有工站、或工站证据冲突时写进 Warnings，
                // 随结果返回（只提示，不影响排查）
                try
                {
                    result.Warnings.AddRange(await BuildBindingCoverageWarningsAsync(
                        projectIds,
                        stationProjectPairs.Select(p => p.StationCode)));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception coverageEx)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[诊断] 工站绑定覆盖检查失败（不影响排查）: {coverageEx.Message}",
                        "LineDiagnosis/BindingCoverage");
                }

                // 推送：告知前端工位列表（用于渲染占位）
                if (hasSignalR)
                {
                    await _broadcastService.BroadcastLineDiagnosisStartedAsync(taskId,
                        stationProjectPairs.Select(p => p.StationCode).ToList());
                }

                await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto
                {
                    Type = "start",
                    TaskId = taskId,
                    StationCodes = stationProjectPairs.Select(p => p.StationCode).ToList(),
                    TotalCount = stationProjectPairs.Count,
                    Message = "Line diagnosis started.",
                }, operationCancellationToken);

                MarkPhase("Prepare");

                // 3. 并行排查各工位（逐个完成后推送进度）
                var maxParallel = Math.Clamp(request.MaxParallelStations, 1, 6);
                var maxAnchors = Math.Clamp(request.MaxAnchorsPerStation, 1, 500);
                var activeGraphGraceSeconds = Math.Clamp(request.ActiveGraphGraceSeconds, 0, 60);
                summaries = new ConcurrentBag<StationDiagnosisSummaryDto>();
                var completedCount = 0;
                var totalCount = stationProjectPairs.Count;
                var sem = new SemaphoreSlim(maxParallel, maxParallel);

                var tasks = stationProjectPairs.Select(async (pair, index) =>
                {
                    await sem.WaitAsync(operationCancellationToken);
                    try
                                 {
                                     await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto
                        {
                            Type = "station_start",
                            TaskId = taskId,
                            StationCode = pair.StationCode,
                            CompletedCount = completedCount,
                            TotalCount = totalCount,
                            Payload = new
                            {
                                pair.ProjectId,
                                StationCode = pair.StationCode,
                                StationOrder = index + 1,
                            },
                            Message = $"{pair.StationCode} diagnosis started.",
                        }, operationCancellationToken);

                        var summary = await DiagnoseStationForLineAsync(
                            pair.ProjectId,
                            pair.StationCode,
                            index + 1,
                            maxAnchors,
                            request.EnablePerStationLlm,
                            request.GraphOnlyMode,
                            request.MaxFbPenetrationDepth,
                            activeGraphGraceSeconds,
                            request.EnableDeviceFocusCheck,
                            request.DeviceFocusAnchorTypes,
                            request.OutputLanguage,
                            operationCancellationToken,
                            postDiagnosisRecheck: false);
                        summaries.Add(summary);

                        var done = Interlocked.Increment(ref completedCount);
                        if (hasSignalR)
                        {
                            await _broadcastService.BroadcastLineDiagnosisStationDoneAsync(
                                taskId, summary, done, totalCount);
                        }

                        await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto
                        {
                            Type = "station_done",
                            TaskId = taskId,
                            StationCode = summary.StationCode,
                            CompletedCount = done,
                            TotalCount = totalCount,
                            Station = summary,
                            Payload = new
                            {
                                summary.ProjectId,
                                summary.StationCode,
                                summary.StationOrder,
                                summary.OverallStatus,
                                summary.DiagnosisStrategy,
                                summary.AnchorCount,
                                summary.EvaluatedCount,
                                summary.FalseAnchorCount,
                                FindingCount = summary.KeyFindings?.Count ?? 0,
                                summary.ElapsedMs,
                                summary.TimingBreakdown,
                                summary.TemplateMatchInfo,
                                summary.ErrorMessage,
                            },
                            Message = $"{summary.StationCode} diagnosis completed.",
                        }, operationCancellationToken);
                    }
                    finally
                    {
                        sem.Release();
                    }
                });

                await Task.WhenAll(tasks);
                MarkPhase("Stations");

                // ── 诊断后全线二次活跃度检测 ──
                // 诊断期间瞬时信号可能恢复，对仍标记为 SomeFalse 的工位延迟重检。
                // 策略：等待 5 秒后看是否有持续的不同信号变化（排除报警循环等单信号抖动）。
                // 2026-09-23 工位内不再各自等 5 秒复检（占着并行名额白等），这里是唯一一次复检，
                // 等待从 3 秒延长到 5 秒，与原工位内复检一致（让 PLC 走完多个扫描周期，避免慢节拍误判）。
                var suspectSummaries = summaries
                    .Where(s => s.OverallStatus == "SomeFalse")
                    .ToList();
                if (suspectSummaries.Count > 0)
                {
                    await Task.Delay(5000, operationCancellationToken); // 等 5 秒让瞬时信号稳定
                    foreach (var suspect in suspectSummaries)
                    {
                        operationCancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var freshChanges = await _stationShadowStateCacheService
                                .GetRecentChangesAsync(suspect.ProjectId, suspect.StationCode, 30);
                            var recheckGraphVariableNames = request.GraphOnlyMode
                                ? await GetGraphActionVariableNameSetAsync(suspect.ProjectId, suspect.StationCode)
                                : null;
                            var freshBool = freshChanges?
                                .Where(c => !IsStringSignalChange(c)
                                    && (!request.GraphOnlyMode || IsConfiguredVariableChange(c, recheckGraphVariableNames)))
                                .ToList();
                            if (freshBool != null && freshBool.Count > 0)
                            {
                                var now2 = DateTime.Now;
                                var freshWindow = freshBool
                                    .Where(c => c.ChangedAt.HasValue && c.ChangedAt.Value >= now2.AddSeconds(-4))
                                    .ToList();
                                // 关键改进：看不同信号数而非总变化数，避免单个报警信号循环抖动被误判为活跃
                                var distinctFreshSignals = freshWindow
                                    .Select(c => c.VariableName ?? c.SignalKey)
                                    .Where(v => !string.IsNullOrWhiteSpace(v))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .Count();
                                if (distinctFreshSignals >= 3)
                                {
                                    suspect.OverallStatus = "Running";
                                    suspect.DiagnosisStrategy += "+RecheckOverride";
                                    await WriteLogHelper.WriteLogsAsync(
                                        $"[诊断] {suspect.StationCode} 二次活跃度检测：{distinctFreshSignals}个不同信号活跃 → Running " +
                                        $"(原Findings={suspect.KeyFindings.Count})",
                                        "LineDiagnosis/ActivityRecheck");
                                }
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { /* 二次检测失败不影响原诊断 */ }
                    }
                }

                var orderedSummaries = summaries
                    .OrderBy(s => s.StationOrder)
                    .ToList();

                result.StationSummaries = orderedSummaries;
                result.DiagnosedStations = orderedSummaries.Count;
                result.AbnormalStations = orderedSummaries.Count(s => s.OverallStatus == "SomeFalse");

                // 汇总工位诊断日志
                var stationPhaseMs = sw.ElapsedMilliseconds;
                await WriteLogHelper.WriteLogsAsync(
                    $"[诊断] 全部工位完成 耗时={stationPhaseMs}ms 工位数={orderedSummaries.Count} " +
                    $"异常={result.AbnormalStations} 策略分布=" +
                    string.Join(",", orderedSummaries.GroupBy(s => s.DiagnosisStrategy ?? "N/A")
                        .Select(g => $"{g.Key}:{g.Count()}")),
                    "LineDiagnosis/Summary");
                MarkPhase("Recheck");

                // 4-. 阀门报警（2026-09-30）：阀门 FB 的 FwdErr/BkdErr/FwdLockErr/BkdLockErr 所连报警位，
                //     按工位挂到结果上并交给 AI 汇总。只作证据，不改工位状态；失败不影响其余诊断。
                try
                {
                    result.AlarmReadError = await _valveAlarmDiagnosisService.AttachAsync(orderedSummaries, operationCancellationToken);
                }
                catch (OperationCanceledException) when (operationCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception alarmEx)
                {
                    result.AlarmReadError = "报警读取失败：" + alarmEx.Message;
                }
                if (!string.IsNullOrWhiteSpace(result.AlarmReadError))
                {
                    await WriteLogHelper.WriteLogsAsync($"[诊断] {result.AlarmReadError}", "LineDiagnosis/Summary");
                }
                MarkPhase("Alarms");

                // 4a. IO 约束检查（可选，前端勾选 IncludeConstraintAnalysis=true 时运行）
                await RefreshOperatingModesAsync();
                var allManual = Original353OperatingModeSummary.AllManual(projectIds, operatingModes);
                var skipOrdinaryConstraints = Original353OperatingModeSummary.AllSelectedHaveManual(projectIds, operatingModes);
                if (request.IncludeConstraintAnalysis && skipOrdinaryConstraints)
                {
                    result.ConstraintReport = allManual
                        ? "当前所选设备均处于手动模式，已跳过普通动作约束判断。"
                        : "当前所选设备有工位处于手动模式，已跳过普通动作约束判断。";
                    await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto {
                        Type = "constraint_done", TaskId = taskId,
                        Message = result.ConstraintReport, Payload = new { Skipped = true, Reason = "ManualMode", Judged = 0 }
                    }, operationCancellationToken);
                }
                if (request.IncludeConstraintAnalysis && _diagnosticStrategyFactoryApp != null && !skipOrdinaryConstraints)
                {
                    try
                    {
                        await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto
                        {
                            Type = "constraint_start",
                            TaskId = taskId,
                            Message = "IO constraint analysis started.",
                        }, operationCancellationToken);

                        var energyReq = new EnergyFlowDiagnosticRequestDto
                        {
                            ProductionLineId = 1,
                            ProjectIds = projectIds,
                            ForceDeepAnalysis = false
                        };
                        var constraintProgress = businessProgress.CreateEnergyFlowReporter(taskId);
                        var constraintResult = await _diagnosticStrategyFactoryApp.DiagnoseEnergyFlowAsync(
                            energyReq,
                            constraintProgress,
                            operationCancellationToken);
                        if (constraintResult.IsSuccess && !string.IsNullOrWhiteSpace(constraintResult.BackResult))
                        {
                            result.ConstraintReport = constraintResult.BackResult;
                            await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto
                            {
                                Type = "constraint_done",
                                TaskId = taskId,
                                Payload = new
                                {
                                    ReportLength = result.ConstraintReport.Length,
                                },
                                Message = "IO constraint analysis completed.",
                            }, operationCancellationToken);
                            await WriteLogHelper.WriteLogsAsync(
                                $"[诊断] IO诊断完成 报告长度={result.ConstraintReport.Length}",
                                "LineDiagnosis/ConstraintCheck");
                        }

                        if (string.IsNullOrWhiteSpace(result.ConstraintReport))
                        {
                            await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto
                            {
                                Type = "constraint_done",
                                TaskId = taskId,
                                Payload = new
                                {
                                    constraintResult.IsSuccess,
                                    constraintResult.ErrorMessage,
                                },
                                Message = "IO constraint analysis completed without a report.",
                            }, operationCancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto
                        {
                            Type = "constraint_error",
                            TaskId = taskId,
                            Payload = new { ex.Message },
                            Message = ex.Message,
                        }, operationCancellationToken);

                        await WriteLogHelper.WriteLogsAsync(
                            $"[诊断] IO诊断异常（不影响主流程）: {ex.Message}",
                            "LineDiagnosis/ConstraintCheck");
                    }
                }

                if (request.IncludeConstraintAnalysis && _diagnosticStrategyFactoryApp != null)
                {
                    MarkPhase("Constraint");
                }

                // 4. LLM 跨工位根因分析
                if (!string.IsNullOrWhiteSpace(result.OperatingModeReport))
                    result.ConstraintReport = result.OperatingModeReport + "\n" + result.ConstraintReport;
                var llmSw = System.Diagnostics.Stopwatch.StartNew();
                var hasConstraintReport = !string.IsNullOrWhiteSpace(result.ConstraintReport);
                var llmConstraintContext = Original353OperatingModeSummary.ForModel(result.ConstraintReport, operatingModes);
                if (request.UseLlmAnalysis
                    && (result.AbnormalStations > 0
                        || request.ForceLlmSummaryWhenNoAbnormal
                        || hasConstraintReport))
                {
                    if (hasSignalR) await _broadcastService.BroadcastLineDiagnosisLlmStartAsync(taskId);
                    await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto
                    {
                        Type = "llm_start",
                        TaskId = taskId,
                        Message = request.UseAgentDiagnosis
                            ? "Agent diagnosis started."
                            : "LLM summary started.",
                    }, operationCancellationToken);

                    if (request.UseAgentDiagnosis)
                    {
                        // Agent 模式：LLM 通过 ReAct 循环自主调用工具探查
                        var projectId = result.ProjectIds?.FirstOrDefault() ?? 0;
                        result.LlmReport = await _diagnosisAgentService.AnalyzeWithAgentAsync(
                            projectId, orderedSummaries, llmConstraintContext, request.AgentMaxIterations,
                            onStep: async step =>
                            {
                                // 将每一步推理过程写入日志
                                var toolInfo = step.ToolCall != null ? $" Tool={step.ToolCall.ToolName}" : "";
                                await WriteLogHelper.WriteLogsAsync(
                                    $"[Agent] Iter={step.Iteration} {step.Phase}{toolInfo} " +
                                    $"Decision={step.Decision} Duration={step.DurationMs}ms " +
                                    $"Content={(step.Content?.Length > 200 ? step.Content.Substring(0, 200) + "..." : step.Content)}",
                                    "LineDiagnosis/AgentStep");

                                var agentStepPayload = new
                                {
                                    TaskId = taskId,
                                    Iteration = step.Iteration,
                                    Phase = step.Phase.ToString(),
                                    Decision = step.Decision.ToString(),
                                    Content = step.Content?.Length > 500 ? step.Content.Substring(0, 500) + "..." : step.Content,
                                    ToolName = step.ToolCall?.ToolName,
                                    ToolArgs = step.ToolCall?.Arguments?.Length > 200 ? step.ToolCall.Arguments.Substring(0, 200) + "..." : step.ToolCall?.Arguments,
                                    DurationMs = step.DurationMs,
                                    Success = step.Success,
                                    ErrorMessage = step.ErrorMessage,
                                };

                                // 推送 Agent 推理步骤到前端
                                if (hasSignalR)
                                {
                                    try
                                    {
                                        await _broadcastService.BroadcastLineDiagnosisAgentStepAsync(taskId, agentStepPayload);
                                    }
                                    catch { /* 广播失败不影响诊断主流程 */ }
                                }

                                await businessProgress.EmitAsync(new LineDiagnosisProgressEventDto
                                {
                                    Type = "agent_step",
                                    TaskId = taskId,
                                    ResultFragment = new LineDiagnosisResultFragmentDto
                                    {
                                        Iteration = step.Iteration,
                                        Phase = step.Phase,
                                        Decision = step.Decision,
                                    },
                                    Payload = agentStepPayload,
                                    Message = $"Agent step {step.Iteration} completed.",
                                }, operationCancellationToken);
                            },
                            cancellationToken: operationCancellationToken,
                            outputLanguage: request.OutputLanguage);
                    }
                    else
                    {
                        // 传统模式：直接喂摘要给 LLM 生成报告（如有约束诊断报告一并传入）
                        result.LlmReport = await _lineDiagnosisLlmService.AnalyzeAsync(
                            orderedSummaries,
                            llmConstraintContext,
                            request.OutputLanguage,
                            operationCancellationToken);
                    }
                }

                Original353OperatingModeSummary.Apply(result.LlmReport, operatingModes);
                llmSw.Stop();
                MarkPhase("Llm");
                if (result.LlmReport != null)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[诊断] LLM阶段完成 耗时={llmSw.ElapsedMilliseconds}ms " +
                        $"模式={(request.UseAgentDiagnosis ? "Agent" : "传统")} " +
                        $"成功={result.LlmReport.Success} 根因={result.LlmReport.RootCauseStation ?? "N/A"} " +
                        $"置信度={result.LlmReport.Confidence}",
                        "LineDiagnosis/LlmPhase");

                    await businessProgress.EmitAsync(
                        LineDiagnosisBusinessProgress.CreateLlmDoneEvent(
                            taskId,
                            result.LlmReport,
                            orderedSummaries.Select(summary => summary.StationCode)),
                        operationCancellationToken);
                }

                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                response.IsSuccess = true;
                response.BackResult = result;

                // ── 5. 持久化诊断记录 ──
                _ = PersistDiagnosisRecordAsync(taskId, request, result);

                if (hasSignalR) await _broadcastService.BroadcastLineDiagnosisCompletedAsync(taskId, result, request.OutputLanguage);
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // ── 全局超时 ──
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.TimedOut = true;
                result.Warnings.Add(LineDiagnosisI18n.T("warn.timeout", request.OutputLanguage, timeoutSec));

                // 尽量收集已完成的工位摘要
                if (result.StationSummaries == null || result.StationSummaries.Count == 0)
                {
                    var partialSummaries = summaries?.ToList();
                    if (partialSummaries != null && partialSummaries.Count > 0)
                    {
                        result.StationSummaries = partialSummaries.OrderBy(s => s.StationOrder).ToList();
                        result.DiagnosedStations = partialSummaries.Count;
                        result.AbnormalStations = partialSummaries.Count(s => s.OverallStatus == "SomeFalse");
                    }
                }

                response.IsSuccess = true; // 部分结果仍可用
                response.ErrorMessage = LineDiagnosisI18n.T("err.timeoutPartial", request.OutputLanguage, timeoutSec);
                response.BackResult = result;
                _ = PersistDiagnosisRecordAsync(taskId, request, result);
                if (hasSignalR) await _broadcastService.BroadcastLineDiagnosisCompletedAsync(taskId, result, request.OutputLanguage);
                return response;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                response.IsSuccess = false;
                response.ErrorMessage = ex.Message;
                response.ExceptionMessage = ex.ToString();
                response.BackResult = result;
                if (hasSignalR) await _broadcastService.BroadcastLineDiagnosisFailedAsync(taskId, ex.Message);
                return response;
            }
        }

        /// <summary>
        /// 异步持久化诊断记录（fire-and-forget，不阻塞主流程）。
        /// </summary>
        private static string ResolveAgentOutputMode(string outputMode)
        {
            var value = (outputMode ?? string.Empty).Trim();
            if (value.Equals("ProcessAndSummary", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Process", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Full", StringComparison.OrdinalIgnoreCase)
                || value.Equals("process", StringComparison.OrdinalIgnoreCase)
                || value.Equals("full", StringComparison.OrdinalIgnoreCase))
            {
                return "ProcessAndSummary";
            }

            if (value.Equals("SummaryOnly", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Summary", StringComparison.OrdinalIgnoreCase)
                || value.Equals("summary", StringComparison.OrdinalIgnoreCase)
                || value.Equals("llm", StringComparison.OrdinalIgnoreCase))
            {
                return "SummaryOnly";
            }

            return "FinalNodesOnly";
        }

        private static List<long> ResolveAgentProjectIds(AgentLineDiagnosisRequestDto request)
        {
            var ids = request.ProjectIds?
                .Where(id => id > 0)
                .Distinct()
                .ToList() ?? new List<long>();

            if (ids.Count == 0 && request.ProjectId.GetValueOrDefault() > 0)
            {
                ids.Add(request.ProjectId.Value);
            }

            return ids;
        }

        private static List<string> ResolveAgentStationCodes(AgentLineDiagnosisRequestDto request)
        {
            var stationCodes = request.StationCodes?
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (stationCodes.Count == 0 && !string.IsNullOrWhiteSpace(request.StationCode))
            {
                stationCodes.Add(request.StationCode.Trim());
            }

            return stationCodes;
        }

        private static string ResolveAgentLlmMode(string llmMode)
        {
            var value = (llmMode ?? string.Empty).Trim();
            if (value.Equals("None", StringComparison.OrdinalIgnoreCase)
                || value.Equals("NoLlm", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                return "None";
            }

            if (value.Equals("Agent", StringComparison.OrdinalIgnoreCase))
            {
                return "Agent";
            }

            return "Traditional";
        }

        private static LineDiagnosisRequestDto BuildAgentLineDiagnosisCoreRequest(
            AgentLineDiagnosisRequestDto request,
            string llmMode,
            string outputMode)
        {
            var useLlm = !string.Equals(llmMode, "None", StringComparison.OrdinalIgnoreCase);
            var isFinalNodesOnly = string.Equals(outputMode, "FinalNodesOnly", StringComparison.OrdinalIgnoreCase);
            var hasExplicitLlmMode = !string.IsNullOrWhiteSpace(request.LlmMode);
            return new LineDiagnosisRequestDto
            {
                ProjectIds = ResolveAgentProjectIds(request),
                StationCodes = ResolveAgentStationCodes(request),
                MaxParallelStations = request.MaxParallelStations,
                MaxAnchorsPerStation = request.MaxAnchorsPerStation,
                UseLlmAnalysis = useLlm,
                ForceLlmSummaryWhenNoAbnormal = useLlm
                    && (!isFinalNodesOnly || request.IncludeConstraintAnalysis || hasExplicitLlmMode),
                UseAgentDiagnosis = useLlm && string.Equals(llmMode, "Agent", StringComparison.OrdinalIgnoreCase),
                AgentMaxIterations = request.AgentMaxIterations,
                EnablePerStationLlm = useLlm && request.EnablePerStationLlm,
                TimeoutSeconds = request.TimeoutSeconds,
                TaskId = request.TaskId,
                GraphOnlyMode = request.GraphOnlyMode,
                EnableDeviceFocusCheck = request.EnableDeviceFocusCheck,
                DeviceFocusAnchorTypes = request.DeviceFocusAnchorTypes?.ToList() ?? new List<string>(),
                ActiveGraphGraceSeconds = request.ActiveGraphGraceSeconds,
                IncludeConstraintAnalysis = request.IncludeConstraintAnalysis,
                MaxFbPenetrationDepth = request.MaxFbPenetrationDepth,
                OutputLanguage = request.OutputLanguage,
            };
        }

        private static AgentLineDiagnosisResponseDto BuildAgentLineDiagnosisResponse(
            AgentLineDiagnosisRequestDto request,
            LineDiagnosisResultDto result,
            string taskId,
            string outputMode,
            string llmMode,
            List<LineDiagnosisProgressEventDto> events)
        {
            var maxFinalNodes = Math.Clamp(request.MaxFinalNodes, 1, 200);
            var includeFinalNodes = !string.Equals(outputMode, "SummaryOnly", StringComparison.OrdinalIgnoreCase)
                || string.Equals(llmMode, "None", StringComparison.OrdinalIgnoreCase);

            var response = new AgentLineDiagnosisResponseDto
            {
                TaskId = taskId,
                OutputMode = outputMode,
                LlmMode = llmMode,
                OutputLanguage = result?.OutputLanguage ?? LineDiagnosisLlmService.NormalizeOutputLanguage(request.OutputLanguage),
                DiagnosedAt = result?.DiagnosedAt ?? DateTime.Now,
                ElapsedMs = result?.ElapsedMs ?? 0,
                TotalStations = result?.TotalStations ?? 0,
                DiagnosedStations = result?.DiagnosedStations ?? 0,
                AbnormalStations = result?.AbnormalStations ?? 0,
                TimedOut = result?.TimedOut ?? false,
                Summary = string.Equals(llmMode, "None", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : result?.LlmReport,
                FinalNodes = includeFinalNodes
                    ? BuildAgentFinalNodes(result, maxFinalNodes)
                    : new List<AgentLineDiagnosisFinalNodeDto>(),
                StationStatuses = BuildAgentStationStatuses(result),
                Events = (request.IncludeProcess
                    || string.Equals(outputMode, "ProcessAndSummary", StringComparison.OrdinalIgnoreCase))
                    ? events ?? new List<LineDiagnosisProgressEventDto>()
                    : new List<LineDiagnosisProgressEventDto>(),
                RawResult = request.IncludeRawResult ? result : null,
                Warnings = result?.Warnings?.ToList() ?? new List<string>(),
            };

            if (string.Equals(llmMode, "None", StringComparison.OrdinalIgnoreCase)
                && string.Equals(outputMode, "SummaryOnly", StringComparison.OrdinalIgnoreCase))
            {
                response.Warnings.Add("SummaryOnly with LlmMode=None has no LLM summary; FinalNodes are returned instead.");
            }

            if (includeFinalNodes && response.FinalNodes.Count == 0)
            {
                response.Warnings.Add(response.Summary != null
                    ? "No final nodes were returned because no station was diagnosed as SomeFalse with key findings. A summary was still generated from StationStatuses."
                    : "No final nodes were returned because no station was diagnosed as SomeFalse with key findings. Check StationStatuses for Running/Skipped/AllTrue details, or enable device focus if needed.");
            }

            return response;
        }

        private static List<AgentLineDiagnosisStationStatusDto> BuildAgentStationStatuses(LineDiagnosisResultDto result)
        {
            if (result?.StationSummaries == null || result.StationSummaries.Count == 0)
            {
                return new List<AgentLineDiagnosisStationStatusDto>();
            }

            return result.StationSummaries
                .OrderBy(s => s.StationOrder)
                .Select(s => new AgentLineDiagnosisStationStatusDto
                {
                    ProjectId = s.ProjectId,
                    StationCode = s.StationCode,
                    StationOrder = s.StationOrder,
                    OverallStatus = s.OverallStatus,
                    DiagnosisStrategy = s.DiagnosisStrategy,
                    AnchorCount = s.AnchorCount,
                    EvaluatedCount = s.EvaluatedCount,
                    FalseAnchorCount = s.FalseAnchorCount,
                    FindingCount = s.KeyFindings?.Count ?? 0,
                    ElapsedMs = s.ElapsedMs,
                    TimingBreakdown = s.TimingBreakdown,
                    TemplateMatchInfo = s.TemplateMatchInfo,
                    ErrorMessage = s.ErrorMessage,
                })
                .ToList();
        }

        private static List<AgentLineDiagnosisFinalNodeDto> BuildAgentFinalNodes(
            LineDiagnosisResultDto result,
            int maxFinalNodes)
        {
            var nodes = new List<AgentLineDiagnosisFinalNodeDto>();
            if (result?.StationSummaries == null || result.StationSummaries.Count == 0)
            {
                return nodes;
            }

            foreach (var station in result.StationSummaries
                .Where(s => s?.KeyFindings != null && s.KeyFindings.Count > 0)
                .OrderByDescending(s => string.Equals(s.OverallStatus, "SomeFalse", StringComparison.OrdinalIgnoreCase))
                .ThenBy(s => s.StationOrder))
            {
                var findings = station.KeyFindings
                    .Where(f => f != null)
                    .ToList();
                if (findings.Count == 0) continue;

                var maxDepth = findings.Max(f => f.TraceDepth);
                var selected = findings
                    .Where(f => f.TraceDepth == maxDepth
                        || string.Equals(f.DiagnosisPhase, "DeviceFocusDirect", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(f => f.VariableName ?? f.DisplayName ?? f.FailurePathSummary ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(f => f.TraceDepth).First())
                    .ToList();

                foreach (var finding in selected)
                {
                    nodes.Add(new AgentLineDiagnosisFinalNodeDto
                    {
                        ProjectId = station.ProjectId,
                        StationCode = station.StationCode,
                        StationOrder = station.StationOrder,
                        OverallStatus = station.OverallStatus,
                        DiagnosisStrategy = station.DiagnosisStrategy,
                        VariableName = finding.VariableName,
                        DisplayName = finding.DisplayName,
                        EvaluationStatus = finding.EvaluationStatus,
                        EvaluatedValue = finding.EvaluatedValue,
                        DiagnosisQuality = finding.DiagnosisQuality,
                        FailurePathSummary = finding.FailurePathSummary,
                        BlockName = finding.BlockName,
                        TraceDepth = finding.TraceDepth,
                        FaultOwnership = finding.FaultOwnership,
                        DiagnosisPhase = finding.DiagnosisPhase,
                        IoTagAddress = finding.IoTagAddress,
                        IoTagComment = finding.IoTagComment,
                        LastTrueAt = finding.LastTrueAt,
                        SignalHistoryAnalysis = finding.SignalHistoryAnalysis,
                    });

                    if (nodes.Count >= maxFinalNodes)
                    {
                        return nodes;
                    }
                }
            }

            return nodes;
        }

        /// <summary>
        /// Async fire-and-forget persistence for line diagnosis records.
        /// </summary>
        private async Task PersistDiagnosisRecordAsync(string taskId, LineDiagnosisRequestDto request, LineDiagnosisResultDto result)
        {
            try
            {
                var llm = result.LlmReport;
                var strategyDist = result.StationSummaries?
                    .GroupBy(s => s.DiagnosisStrategy ?? "N/A")
                    .Select(g => $"{g.Key}:{g.Count()}")
                    .ToList();

                var record = new PLC_LineDiagnosisRecordEntity
                {
                    TaskId = taskId,
                    ProjectIds = result.ProjectIds != null ? string.Join(",", result.ProjectIds) : null,
                    DiagnosedAt = result.DiagnosedAt,
                    TotalStations = result.TotalStations,
                    DiagnosedStations = result.DiagnosedStations,
                    AbnormalStations = result.AbnormalStations,
                    ElapsedMs = result.ElapsedMs,
                    TimedOut = result.TimedOut,
                    DiagnosisMode = request.UseAgentDiagnosis ? "Agent" : "Traditional",
                    RootCauseStation = llm?.RootCauseStation,
                    RootCauseDescription = llm?.RootCauseDescription,
                    Confidence = llm?.Confidence,
                    ImpactChain = llm?.ImpactChain,
                    SuggestedActionsJson = llm?.SuggestedActions != null
                        ? System.Text.Json.JsonSerializer.Serialize(llm.SuggestedActions)
                        : null,
                    Reasoning = llm?.Reasoning,
                    AgentIterations = llm?.AgentIterations,
                    ToolCallSummary = llm?.ToolCallSummary,
                    ResultJson = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = false,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    }),
                    StrategyDistribution = strategyDist != null ? string.Join(",", strategyDist) : null,
                    CreateTime = DateTime.Now,
                };

                await _lineDiagnosisRecordService.InsertAsync(record);
                result.RecordId = record.Id;
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[诊断] 持久化诊断记录失败: {ex.Message}",
                    "LineDiagnosis/PersistError");
            }
        }

        /// <summary>
        /// 单工位停机诊断（用于全线排查）。
        /// 当前先屏蔽动作簇模板主链；只有存在激活的 GRAPH 步骤时才继续做事件/后继链诊断，
        /// 没有激活 GRAPH 时直接跳过，等待后续 LAD 执行链接入。
        /// </summary>
        private async Task<StationDiagnosisSummaryDto> DiagnoseDeviceFocusForStationAsync(
            long projectId,
            string stationCode,
            int stationOrder,
            int maxCandidates,
            int maxFbDepth,
            HashSet<string> anchorTypeFilter = null,
            int maxFindings = 50)
        {
            var stationSw = System.Diagnostics.Stopwatch.StartNew();
            var timings = new Dictionary<string, long>();
            var summary = new StationDiagnosisSummaryDto
            {
                ProjectId = projectId,
                StationCode = stationCode,
                StationOrder = stationOrder,
                OverallStatus = "NoAst",
                DiagnosisStrategy = "DeviceFocusDirectEval",
            };

            try
            {
                var effectiveAnchorTypeFilter = anchorTypeFilter ?? ResolveDefaultDeviceFocusDiagnosisAnchorTypes();
                var phaseStart = stationSw.ElapsedMilliseconds;
                var astRows = await _logicUnitAstService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == projectId
                    && it.StationCode == stationCode
                    && it.CompileStatus == "Compiled");
                timings["AstLoad"] = stationSw.ElapsedMilliseconds - phaseStart;

                if (astRows == null || astRows.Count == 0)
                {
                    summary.TemplateMatchInfo = "No compiled AST rows found for the requested scope.";
                    return summary;
                }

                phaseStart = stationSw.ElapsedMilliseconds;
                var activeDeviceFocusCandidates = await GetActiveDeviceFocusCandidatesAsync(
                    projectId,
                    stationCode,
                    astRows,
                    Math.Max(maxCandidates, 1),
                    effectiveAnchorTypeFilter);
                timings["DeviceFocusCheck"] = stationSw.ElapsedMilliseconds - phaseStart;

                if (activeDeviceFocusCandidates.Count == 0)
                {
                    summary.OverallStatus = "Skipped";
                    summary.AnchorCount = 0;
                    summary.EvaluatedCount = 0;
                    summary.FalseAnchorCount = 0;
                    var scope = effectiveAnchorTypeFilter.Count > 0
                        ? string.Join(",", effectiveAnchorTypeFilter.OrderBy(type => type))
                        : "All diagnostic device focus signals";
                    summary.TemplateMatchInfo = $"No active device focus signal found. Scope={scope}.";
                    return summary;
                }

                phaseStart = stationSw.ElapsedMilliseconds;
                await EvaluateStallCandidatesAsync(
                    projectId,
                    stationCode,
                    activeDeviceFocusCandidates,
                    astRows,
                    summary,
                    maxCandidates,
                    maxFbDepth);
                if (maxFindings > 0 && summary.KeyFindings.Count > maxFindings)
                {
                    var originalCount = summary.KeyFindings.Count;
                    summary.KeyFindings = summary.KeyFindings.Take(maxFindings).ToList();
                    var note = $"Returned first {maxFindings} findings, original findings={originalCount}.";
                    summary.TemplateMatchInfo = string.IsNullOrWhiteSpace(summary.TemplateMatchInfo)
                        ? note
                        : $"{summary.TemplateMatchInfo} {note}";
                }
                timings["Eval"] = stationSw.ElapsedMilliseconds - phaseStart;
            }
            catch (Exception ex)
            {
                summary.OverallStatus = "Error";
                summary.ErrorMessage = ex.Message;
            }
            finally
            {
                stationSw.Stop();
                summary.ElapsedMs = stationSw.ElapsedMilliseconds;
                summary.TimingBreakdown = FormatTimings(timings);
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[设备关注信号诊断] {stationCode} 耗时={summary.ElapsedMs}ms " +
                $"候选={summary.AnchorCount} 求值={summary.EvaluatedCount} False={summary.FalseAnchorCount} " +
                $"Findings={summary.KeyFindings.Count} 明细=[{summary.TimingBreakdown}]",
                "LineDiagnosis/DeviceFocusStandalone");

            return summary;
        }

        private async Task<StationDiagnosisSummaryDto> DiagnoseStationForLineAsync(
            long projectId, string stationCode, int stationOrder, int maxAnchors, bool enablePerStationLlm = false, bool graphOnlyMode = true, int maxFbDepth = 4, int activeGraphGraceSeconds = 8, bool enableDeviceFocusCheck = true, List<string> deviceFocusAnchorTypes = null, string outputLanguage = "zh-CN", CancellationToken cancellationToken = default,
            bool postDiagnosisRecheck = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stationSw = System.Diagnostics.Stopwatch.StartNew();
            var timings = new Dictionary<string, long>();
            // 2026-09-23 本工位深层步骤累计耗时（写源追溯 / 失败路径历史），由 FormatTimings 并入明细
            using var subTimings = StationSubTimings.Begin();
            bool preCheckHighActivity = false;
            var summary = new StationDiagnosisSummaryDto
            {
                ProjectId = projectId,
                StationCode = stationCode,
                StationOrder = stationOrder,
                OverallStatus = "NoAst",
            };

            try
            {
                // ── Phase 1: 活跃度前置检测 ──
                // 仅做"极高活跃度"判断（短窗口内密集变化），避免在明显运行中浪费 AST 求值资源。
                // 边界场景（报警循环、慢节拍等）不在此判断：先诊断，诊断后二次验证。
                var phaseStart = stationSw.ElapsedMilliseconds;
                var recentChanges = await _stationShadowStateCacheService
                    .GetRecentChangesAsync(projectId, stationCode, 50);
                cancellationToken.ThrowIfCancellationRequested();
                var activityGraphVariableNames = graphOnlyMode
                    ? await GetGraphActionVariableNameSetAsync(projectId, stationCode)
                    : null;
                // 仅用布尔/数值信号判断活跃度（STRING 变化不算）
                var boolChanges = recentChanges?
                    .Where(c => !IsStringSignalChange(c)
                        && (!graphOnlyMode || IsConfiguredVariableChange(c, activityGraphVariableNames)))
                    .ToList();

                if (boolChanges != null && boolChanges.Count > 0)
                {
                    var now = DateTime.Now;
                    // 极高活跃度：3 秒内有 ≥5 个不同信号在变化 → 几乎确定在运行
                    var veryRecentChanges = boolChanges
                        .Where(c => c.ChangedAt.HasValue && c.ChangedAt.Value >= now.AddSeconds(-3))
                        .ToList();
                    var distinctSignals = veryRecentChanges
                        .Select(c => c.VariableName ?? c.SignalKey)
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    if (distinctSignals >= 5)
                    {
                        stationSw.Stop();
                        summary.OverallStatus = "Running";
                        summary.DiagnosisStrategy = "ActivityDetected";
                        summary.AnchorCount = 0;
                        summary.EvaluatedCount = 0;
                        summary.FalseAnchorCount = 0;
                        summary.ElapsedMs = stationSw.ElapsedMilliseconds;
                        summary.TimingBreakdown = $"ActivityCheck:{stationSw.ElapsedMilliseconds}ms";
                        return summary;
                    }

                    // 不满足极高活跃度条件 → 标记为"可能有活跃"，诊断后做二次验证
                    preCheckHighActivity = veryRecentChanges.Count >= 2;
                }
                timings["ActivityCheck"] = stationSw.ElapsedMilliseconds - phaseStart;

                // ── Phase 2: 加载 AST ──
                phaseStart = stationSw.ElapsedMilliseconds;
                var astRows = await _logicUnitAstService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == projectId
                    && it.StationCode == stationCode
                    && it.CompileStatus == "Compiled");
                cancellationToken.ThrowIfCancellationRequested();
                timings["AstLoad"] = stationSw.ElapsedMilliseconds - phaseStart;

                if (astRows == null || astRows.Count == 0)
                {
                    // 2026-09-22：这条 return 会让 OverallStatus 停在初始值 "NoAst"，页面上只剩一个灰方块，
                    // 既不说是"没跑过锚点发现"还是"编译失败"还是"工位号对不上"。现场连不上库时这行原因
                    // 是唯一线索，所以把它同时写进日志和工位卡片（前端 station.errorMessage 直接显示）。
                    // 只在缺 AST 这条失败路径上多查一次，正常路径零开销。
                    try
                    {
                        // 列名写死在 SQL 里，不用 .Select(new 实体{...}) 投影：SqlSugar 在那种投影里
                        // 不套用 SugarColumn 映射，会把属性名当列名发出去（实测 42703
                        // column "stationcode" does not exist）。这张表的列是 station_code /
                        // compile_status / project_id 下划线式，但 isdeleted 偏偏不带下划线。
                        // projectId 是 long，内插无注入风险（GetListBySqlAsync 不收参数）。
                        var probe = await _logicUnitAstService.GetListBySqlAsync(
                            "SELECT station_code, compile_status FROM ai_plc_logicunit_ast "
                            + $"WHERE isdeleted = false AND project_id = {projectId}");
                        // 守卫：万一 SqlQueryAsync 按属性名而不是 SugarColumn 映射回来，两列会全是 null，
                        // 而全 null 会被下面解读成"这些行的工位为空"——一个看着像结论的错误结论。
                        // 这功能的全部意义就是别再给人假信息，所以宁可说"查不出来"。
                        if (probe.Count > 0 && probe.All(it =>
                                it.StationCode is null && it.CompileStatus is null))
                        {
                            await WriteLogHelper.WriteLogsAsync(
                                $"[LineDiagnosis] 工位 {stationCode} 状态 NoAst（项目 {projectId}）："
                                + $"项目下有 {probe.Count} 行 AST，但探查查询的两列都没映射回来"
                                + "（station_code/compile_status 全 null），无法判断原因；"
                                + "这通常是 SqlSugar 列映射问题，不是数据问题，请直接查库。",
                                "停机诊断");
                            stationSw.Stop();
                            summary.ElapsedMs = stationSw.ElapsedMilliseconds;
                            summary.TimingBreakdown = FormatTimings(timings);
                            return summary;
                        }

                        var reason = MissingAstDiagnosis.Describe(
                            stationCode,
                            probe.Select(it => (it.StationCode, it.CompileStatus)).ToList());
                        if (!string.IsNullOrEmpty(reason))
                        {
                            summary.ErrorMessage = reason;
                            await WriteLogHelper.WriteLogsAsync(
                                $"[LineDiagnosis] 工位 {stationCode} 状态 NoAst（项目 {projectId}）：{reason}",
                                "停机诊断");
                        }
                    }
                    catch (Exception probeEx)
                    {
                        await WriteLogHelper.WriteLogsAsync(
                            $"[LineDiagnosis] 工位 {stationCode} 状态 NoAst（项目 {projectId}），"
                            + $"但追查原因时查库失败：{probeEx.Message}",
                            "停机诊断");
                    }

                    stationSw.Stop();
                    summary.ElapsedMs = stationSw.ElapsedMilliseconds;
                    summary.TimingBreakdown = FormatTimings(timings);
                    return summary;
                }

                // ── Phase 3: 运行时 GRAPH 锚点激活检测 ──
                // graphOnlyMode=true（默认）：无激活 GRAPH 步骤则直接跳过，不进 IO 信号后继推导。
                // graphOnlyMode=false：无 GRAPH 时回退到 RecentIoTagBased。
                phaseStart = stationSw.ElapsedMilliseconds;
                var activeGraphCandidates = await GetActiveGraphCandidatesAsync(projectId, stationCode, astRows);
                timings["GraphActiveCheck"] = stationSw.ElapsedMilliseconds - phaseStart;
                var hasActiveGraphAnchors = activeGraphCandidates.Count > 0;
                var filteredFreshGraphCandidates = false;

                var activeDeviceFocusCandidates = new List<StallCandidateInfo>();
                if (enableDeviceFocusCheck)
                {
                    phaseStart = stationSw.ElapsedMilliseconds;
                    var deviceFocusAnchorTypeFilter = ResolveLineDeviceFocusAnchorTypes(deviceFocusAnchorTypes);
                    activeDeviceFocusCandidates = await GetActiveDeviceFocusCandidatesAsync(
                        projectId,
                        stationCode,
                        astRows,
                        Math.Max(maxAnchors, 1),
                        deviceFocusAnchorTypeFilter);
                    timings["DeviceFocusCheck"] = stationSw.ElapsedMilliseconds - phaseStart;
                }
                else
                {
                    timings["DeviceFocusCheck"] = 0;
                }
                var hasDeviceFocusCandidates = activeDeviceFocusCandidates.Count > 0;

                if (hasActiveGraphAnchors)
                {
                    phaseStart = stationSw.ElapsedMilliseconds;
                    var graphFreshness = await FilterFreshActiveGraphCandidatesAsync(
                        projectId,
                        stationCode,
                        activeGraphCandidates,
                        activeGraphGraceSeconds);
                    timings["FreshGraphCheck"] = stationSw.ElapsedMilliseconds - phaseStart;

                    if (graphFreshness.FreshCandidates.Count > 0)
                    {
                        var freshSummary = BuildFreshActiveGraphSummary(graphFreshness, activeGraphGraceSeconds);
                        if (graphFreshness.SettledCandidates.Count == 0 && !hasDeviceFocusCandidates)
                        {
                            stationSw.Stop();
                            summary.OverallStatus = "Running";
                            summary.DiagnosisStrategy = "SkippedFreshActiveGraph";
                            summary.AnchorCount = activeGraphCandidates.Count;
                            summary.EvaluatedCount = 0;
                            summary.FalseAnchorCount = 0;
                            summary.TemplateMatchInfo = $"激活 GRAPH 仍在 {activeGraphGraceSeconds}s 宽限期内：{freshSummary}，视为工位正在推进，跳过本轮故障判定。";
                            summary.ElapsedMs = stationSw.ElapsedMilliseconds;
                            summary.TimingBreakdown = FormatTimings(timings);
                            await WriteLogHelper.WriteLogsAsync(
                                $"[诊断] {stationCode} 激活 GRAPH 刚切步，宽限期内跳过：{freshSummary}",
                                "LineDiagnosis/FreshGraphCheck");
                            return summary;
                        }

                        activeGraphCandidates = graphFreshness.SettledCandidates;
                        hasActiveGraphAnchors = activeGraphCandidates.Count > 0;
                        filteredFreshGraphCandidates = true;
                        summary.TemplateMatchInfo = $"已忽略刚激活 GRAPH 宽限期候选：{freshSummary}，继续诊断其余已稳定激活步骤。";
                        await WriteLogHelper.WriteLogsAsync(
                            $"[诊断] {stationCode} 已过滤刚激活 GRAPH 候选：{freshSummary}，剩余={activeGraphCandidates.Count}",
                            "LineDiagnosis/FreshGraphCheck");
                    }
                }

                if (!hasActiveGraphAnchors && graphOnlyMode && !hasDeviceFocusCandidates)
                {
                    stationSw.Stop();
                    summary.OverallStatus = "Skipped";
                    summary.DiagnosisStrategy = "SkippedNoActiveGraph";
                    summary.AnchorCount = 0;
                    summary.EvaluatedCount = 0;
                    summary.FalseAnchorCount = 0;
                    summary.TemplateMatchInfo = "无激活 GRAPH 步骤，已跳过诊断（GraphOnlyMode）。";
                    summary.ElapsedMs = stationSw.ElapsedMilliseconds;
                    summary.TimingBreakdown = FormatTimings(timings);
                    return summary;
                }

                // ── Phase 4: 策略选择 ──
                // 有激活 GRAPH 步骤：直接用激活的 GraphAction 信号作为候选，评估其转移条件（GraphDirectEval）
                // 无激活 GRAPH（且非 graphOnlyMode）：StallPoint → RecentIoTag → 跳过
                phaseStart = stationSw.ElapsedMilliseconds;

                var hasDirectDiagnosticCandidates = hasActiveGraphAnchors || hasDeviceFocusCandidates;
                if (hasDirectDiagnosticCandidates)
                {
                    // 有激活 GRAPH 步骤：直接将当前 TRUE 的 GraphAction 变量作为候选，
                    // 进入 EvaluateStallCandidatesAsync Phase 1 评估其转移条件，精准定位卡滞原因。
                    var directCandidates = activeGraphCandidates
                        .Concat(activeDeviceFocusCandidates)
                        .Where(item => item != null && !string.IsNullOrWhiteSpace(item.VariableName))
                        .GroupBy(item => item.VariableName.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Select(group => group
                            .OrderByDescending(ResolveDirectCandidatePriority)
                            .ThenByDescending(item => item.ActiveSignalCount)
                            .First())
                        .OrderByDescending(ResolveDirectCandidatePriority)
                        .ThenByDescending(item => item.MatchRatio)
                        .ThenByDescending(item => item.ActiveSignalCount)
                        .Take(Math.Max(maxAnchors, 1))
                        .ToList();
                    var usedGraphExecBitFallback = directCandidates.Any(item => item.IsGraphExecBitFallback);

                    if (hasActiveGraphAnchors && hasDeviceFocusCandidates)
                    {
                        summary.DiagnosisStrategy = filteredFreshGraphCandidates
                            ? "GraphDirectEval+DeviceFocusDirectEval+FreshGraphFilter"
                            : "GraphDirectEval+DeviceFocusDirectEval";
                    }
                    else if (hasActiveGraphAnchors)
                    {
                        summary.DiagnosisStrategy = filteredFreshGraphCandidates ? "GraphDirectEval+FreshGraphFilter" : "GraphDirectEval";
                    }
                    else
                    {
                        summary.DiagnosisStrategy = "DeviceFocusDirectEval";
                    }
                    if (usedGraphExecBitFallback)
                    {
                        summary.DiagnosisStrategy += "+ExecBitsFallback";
                    }

                    await EvaluateStallCandidatesAsync(projectId, stationCode, directCandidates, astRows, summary, maxAnchors, maxFbDepth, cancellationToken);
                    timings["Eval"] = stationSw.ElapsedMilliseconds - phaseStart;
                }
                else
                {
                    // 无激活GRAPH：StallPoint → RecentIoTag → 跳过
                    var stallCandidates = await LocateStallCandidatesAsync(projectId, stationCode, astRows);
                    timings["StallLocate"] = stationSw.ElapsedMilliseconds - phaseStart;

                    if (stallCandidates.Count > 0)
                    {
                        summary.DiagnosisStrategy = "StallPointBased";
                        phaseStart = stationSw.ElapsedMilliseconds;
                        await EvaluateStallCandidatesAsync(projectId, stationCode, stallCandidates, astRows, summary, maxAnchors, maxFbDepth, cancellationToken);
                        timings["Eval"] = stationSw.ElapsedMilliseconds - phaseStart;
                    }
                    else
                    {
                        // 无 GRAPH、无 StallPoint：扫描当前镜像中最近激活的 IO 信号作为诊断起点
                        phaseStart = stationSw.ElapsedMilliseconds;
                        var ioTagCandidates = await LocateRecentlyActivatedIoTagCandidatesAsync(projectId, stationCode, astRows);
                        timings["IoTagLocate"] = stationSw.ElapsedMilliseconds - phaseStart;

                        if (ioTagCandidates.Count > 0)
                        {
                            summary.DiagnosisStrategy = "RecentIoTagBased";
                            phaseStart = stationSw.ElapsedMilliseconds;
                            await EvaluateStallCandidatesAsync(projectId, stationCode, ioTagCandidates, astRows, summary, maxAnchors, maxFbDepth, cancellationToken);
                            timings["Eval"] = stationSw.ElapsedMilliseconds - phaseStart;
                        }
                        else
                        {
                            stationSw.Stop();
                            summary.OverallStatus = "Skipped";
                            summary.DiagnosisStrategy = "SkippedNoActiveSignal";
                            summary.AnchorCount = 0;
                            summary.EvaluatedCount = 0;
                            summary.FalseAnchorCount = 0;
                            summary.TemplateMatchInfo = "无激活GRAPH步骤，且未检测到当前活跃的IO信号，已跳过诊断。";
                            summary.ElapsedMs = stationSw.ElapsedMilliseconds;
                            summary.TimingBreakdown = FormatTimings(timings);
                            return summary;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                summary.OverallStatus = "Error";
                summary.ErrorMessage = ex.Message;
            }

            // ── 诊断后活跃度校验 ──
            // 所有 SomeFalse 结果一律做延迟重检，避免慢节拍设备或单工位调用漏判。
            // 核心思路：诊断完成后等几秒，如果有新的不同信号在持续变化 → 说明实际在运行。
            // 2026-09-23 一键排查（postDiagnosisRecheck=false）不在这里等：这 5 秒占着并行名额白等，
            // 所有异常工位在全线收尾时统一复检一次（等待同样 5 秒）。单工位调试没有全线复检，仍在这里做。
            if (postDiagnosisRecheck && summary.OverallStatus == "SomeFalse")
            {
                // 2026-09-23 计入工位耗时明细：这一步固定等 5 秒，此前不在 TimingBreakdown 里
                var postRecheckStart = stationSw.ElapsedMilliseconds;
                try
                {
                    await Task.Delay(5000, cancellationToken); // 等 5 秒让 PLC 完成多个扫描周期，避免慢节拍误判
                    var recheckChanges = await _stationShadowStateCacheService
                        .GetRecentChangesAsync(projectId, stationCode, 30);
                    var recheckGraphVariableNames = graphOnlyMode
                        ? await GetGraphActionVariableNameSetAsync(projectId, stationCode)
                        : null;
                    var recheckBool = recheckChanges?
                        .Where(c => !IsStringSignalChange(c)
                            && (!graphOnlyMode || IsConfiguredVariableChange(c, recheckGraphVariableNames)))
                        .ToList();
                    if (recheckBool != null && recheckBool.Count > 0)
                    {
                        var now3 = DateTime.Now;
                        // 2 秒窗口内有 ≥3 个不同信号在变化 → 确认在运行
                        var freshChanges = recheckBool
                            .Where(c => c.ChangedAt.HasValue && c.ChangedAt.Value >= now3.AddSeconds(-3))
                            .ToList();
                        var freshDistinctSignals = freshChanges
                            .Select(c => c.VariableName ?? c.SignalKey)
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count();

                        if (freshDistinctSignals >= 3)
                        {
                            summary.OverallStatus = "Running";
                            summary.DiagnosisStrategy += "+PostDiagnosisRecheck";
                            await WriteLogHelper.WriteLogsAsync(
                                $"[诊断] {stationCode} 诊断后活跃度校验：{freshDistinctSignals}个不同信号在变化 → Running " +
                                $"(原诊断Findings={summary.KeyFindings.Count})",
                                "LineDiagnosis/PostDiagnosisRecheck");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 后校验失败不影响原诊断 */ }
                timings["PostRecheck"] = stationSw.ElapsedMilliseconds - postRecheckStart;
            }

            // ── 工位独立 LLM 诊断（轻量级单轮） ──
            if (enablePerStationLlm && summary.OverallStatus == "SomeFalse" && summary.KeyFindings.Count > 0)
            {
                var stationLlmStart = stationSw.ElapsedMilliseconds;
                try
                {
                    summary.LlmSummary = await GeneratePerStationLlmSummaryAsync(
                        summary,
                        outputLanguage,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception llmEx)
                {
                    summary.LlmSummary = $"[LLM 分析失败: {llmEx.Message}]";
                }
                timings["StationLlm"] = stationSw.ElapsedMilliseconds - stationLlmStart;
            }

            stationSw.Stop();
            summary.ElapsedMs = stationSw.ElapsedMilliseconds;
            summary.TimingBreakdown = FormatTimings(timings);

            await WriteLogHelper.WriteLogsAsync(
                $"[诊断] {stationCode} 策略={summary.DiagnosisStrategy} 耗时={summary.ElapsedMs}ms " +
                $"锚点={summary.AnchorCount} 求值={summary.EvaluatedCount} False={summary.FalseAnchorCount} " +
                $"Findings={summary.KeyFindings.Count} 明细=[{summary.TimingBreakdown}]",
                "LineDiagnosis/StationTiming");

            return summary;
        }

        private async Task<FreshActiveGraphFilterResult> FilterFreshActiveGraphCandidatesAsync(
            long projectId,
            string stationCode,
            List<StallCandidateInfo> activeGraphCandidates,
            int graceSeconds)
        {
            var result = new FreshActiveGraphFilterResult();
            if (activeGraphCandidates == null || activeGraphCandidates.Count == 0)
            {
                return result;
            }

            result.SettledCandidates.AddRange(activeGraphCandidates.Where(item => item != null));
            if (graceSeconds <= 0)
            {
                return result;
            }

            var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, stationCode);
            if (mirrors == null || mirrors.Count == 0)
            {
                return result;
            }

            var mirrorByVariable = mirrors.Values
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.VariableName))
                .GroupBy(item => item.VariableName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(item => IsCurrentlyTrue(item.CurrentValue) ? 1 : 0)
                        .ThenByDescending(item => item.LastChangeTime ?? DateTime.MinValue)
                        .First(),
                    StringComparer.OrdinalIgnoreCase);

            var now = DateTime.Now;
            result.SettledCandidates.Clear();

            foreach (var candidate in activeGraphCandidates.Where(item => item != null))
            {
                if (!string.IsNullOrWhiteSpace(candidate.VariableName)
                    && mirrorByVariable.TryGetValue(candidate.VariableName.Trim(), out var mirror)
                    && ExecutionSuccessorSelectionPolicy.IsFreshlyActivatedGraphAction(mirror, graceSeconds, now))
                {
                    result.FreshCandidates.Add(new FreshActiveGraphCandidateInfo
                    {
                        Candidate = candidate,
                        LastChangeTime = mirror.LastChangeTime,
                        AgeSeconds = Math.Max(0, (now - mirror.LastChangeTime!.Value).TotalSeconds)
                    });
                    continue;
                }

                result.SettledCandidates.Add(candidate);
            }

            return result;
        }

        private static string BuildFreshActiveGraphSummary(FreshActiveGraphFilterResult result, int graceSeconds)
        {
            if (result?.FreshCandidates == null || result.FreshCandidates.Count == 0)
            {
                return $"无宽限期候选({graceSeconds}s)";
            }

            var labels = result.FreshCandidates
                .OrderBy(item => item.AgeSeconds)
                .Take(5)
                .Select(item =>
                {
                    var name = item.Candidate?.VariableName ?? item.Candidate?.DisplayLabel ?? "GRAPH";
                    return $"{name}({item.AgeSeconds:0.0}s)";
                });

            var suffix = result.FreshCandidates.Count > 5 ? $" 等{result.FreshCandidates.Count}个" : string.Empty;
            return $"{string.Join(",", labels)}{suffix}";
        }

        /// <summary>
        /// 格式化阶段耗时字典为紧凑字符串。
        /// </summary>
        private static string FormatTimings(Dictionary<string, long> timings)
        {
            // 2026-09-23 附上深层步骤累计耗时（写源追溯 / 失败路径历史，见 StationSubTimings）
            var sub = StationSubTimings.Current?.Snapshot() ?? Array.Empty<KeyValuePair<string, long>>();
            return string.Join("|", timings.Select(kv => $"{kv.Key}:{kv.Value}ms")
                .Concat(sub.Select(kv => $"{kv.Key}:{kv.Value}ms")));
        }

        private async Task<HashSet<string>> GetGraphActionVariableNameSetAsync(long projectId, string stationCode)
        {
            var allShadowSignals = await _stationShadowSignalService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == projectId
                && it.StationCode == stationCode);

            return allShadowSignals?
                .Where(s => string.Equals(s.AnchorType?.Trim(), "GraphAction", StringComparison.OrdinalIgnoreCase)
                    && s.DiagnosisExcluded != true
                    && !string.IsNullOrWhiteSpace(s.VariableName))
                .Select(s => s.VariableName.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsConfiguredVariableChange(
            StationShadowChangeCacheModel change,
            HashSet<string> variableNames)
        {
            if (change == null || variableNames == null || variableNames.Count == 0)
                return false;

            var variableName = !string.IsNullOrWhiteSpace(change.VariableName)
                ? change.VariableName.Trim()
                : change.SignalKey?.Trim();

            return !string.IsNullOrWhiteSpace(variableName) && variableNames.Contains(variableName);
        }

        /// <summary>
        /// 工位独立 LLM 诊断摘要（轻量级单轮调用）。
        /// </summary>
        private async Task<string> GeneratePerStationLlmSummaryAsync(
            StationDiagnosisSummaryDto summary,
            string outputLanguage = "zh-CN",
            CancellationToken cancellationToken = default)
        {
            return await _lineDiagnosisLlmService.AnalyzeStationAsync(
                summary,
                outputLanguage,
                cancellationToken);
        }

        /// <summary>
        /// 判断信号变化是否为 STRING 类型（字符串值不参与活跃度/停机判断）。
        /// 三级判定：
        ///   1. SignalType 元数据含 "String" → 直接排除
        ///   2. OldValue 或 NewValue 任一为非数值非布尔 → 排除
        ///   3. 值为空/null → 不排除（保守）
        /// </summary>
        private static bool IsStringSignalChange(StationShadowChangeCacheModel change)
        {
            // Level 1: 元数据判定 — 优先使用 SignalType（如果监控配置中标注了类型）
            if (!string.IsNullOrWhiteSpace(change.SignalType)
                && change.SignalType.IndexOf("String", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Level 2: 新值判定
            if (IsNonNumericString(change.NewValue?.ToString()))
                return true;

            // Level 3: 旧值判定 — 如果旧值是字符串也排除（该信号本质是 STRING 类型）
            if (IsNonNumericString(change.OldValue?.ToString()))
                return true;

            return false;
        }

        /// <summary>
        /// 判断字符串值是否为非数值/非布尔（即 STRING 类型信号的值）。
        /// </summary>
        private static bool IsNonNumericString(string val)
        {
            if (string.IsNullOrEmpty(val)) return false;

            // 布尔值
            if (val == "0" || val == "1"
                || val.Equals("true", StringComparison.OrdinalIgnoreCase)
                || val.Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;

            // 纯数值（整数/浮点）
            if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
                return false;

            // 其余视为 STRING
            return true;
        }

        /// <summary>
        /// 异步触发模板自动重建（fire-and-forget，不阻塞诊断主流程）。
        /// 连续 N 次出现未知 Bundle 签名时调用，使模板覆盖新通道/新产品模式。
        /// </summary>
        private async Task TryAutoRebuildTemplatesAsync(long projectId, string stationCode)
        {
            try
            {
                var result = await RebuildMonitoringPatternTemplatesAsync(
                    new DTOS.StationShadowPatternTemplateRebuildRequestDto
                    {
                        ProjectId = projectId,
                        StationCode = stationCode,
                        PatternType = "ExpectedNextBundle",
                        ReplaceExisting = true,
                        MinSupportCount = 2, // 至少2次样本支持才生成模板
                    });

                var generated = result?.BackResult?.GeneratedTemplateCount ?? 0;
                await WriteLogHelper.WriteLogsAsync(
                    $"[自动重建] {stationCode} 完成: 生成{generated}个模板 " +
                    $"源样本={result?.BackResult?.SourceSampleCount ?? 0} " +
                    $"有效={result?.BackResult?.AcceptedSampleCount ?? 0}",
                    "LineDiagnosis/AutoRebuild");

                // 重建成功后清计数，避免重复触发
                if (generated > 0)
                    _stationShadowStateCacheService.ResetUnknownSignatureCount(projectId, stationCode);
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[自动重建] {stationCode} 失败: {ex.Message}",
                    "LineDiagnosis/AutoRebuild");
            }
        }

        /// <summary>
        /// 模板驱动策略：从 PatternTemplate 匹配最近一个 Bundle，找出"预期发生但没有发生"的信号。
        /// 返回结构化匹配结果，包含失败原因分类、匹配度评分。
        /// </summary>
        private async Task<TemplateMatchResult> LocateTemplateTargetsAsync(
            long projectId, string stationCode,
            List<PLC_LogicUnitAstEntity> astRows,
            StationDiagnosisSummaryDto summary)
        {
            try
            {
                // 1. 获取最近 Bundle
                var recentBundles = await _stationShadowStateCacheService
                    .GetRecentBundlesAsync(projectId, stationCode, 5);
                if (recentBundles == null || recentBundles.Count == 0)
                    return new TemplateMatchResult { Outcome = TemplateMatchOutcome.NoBundles };

                var lastBundle = recentBundles.First();
                if (string.IsNullOrWhiteSpace(lastBundle.BundleSignature))
                    return new TemplateMatchResult { Outcome = TemplateMatchOutcome.NoBundles };

                // 2. 查询匹配的模板
                var templates = await _stationPatternTemplateService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.Active == true
                    && it.ProjectId == projectId
                    && it.StationCode == stationCode
                    && it.LastBundleSignature == lastBundle.BundleSignature);

                if (templates == null || templates.Count == 0)
                    return new TemplateMatchResult
                    {
                        Outcome = TemplateMatchOutcome.UnknownSignature,
                        CurrentBundleSignature = lastBundle.BundleSignature,
                    };

                // 取置信度最高的模板
                var bestTemplate = templates
                    .Where(t => !string.IsNullOrWhiteSpace(t.NextExpectedMapJson))
                    .OrderByDescending(t => t.Confidence ?? 0)
                    .ThenByDescending(t => t.SupportCount ?? 0)
                    .FirstOrDefault();

                if (bestTemplate == null)
                    return new TemplateMatchResult
                    {
                        Outcome = TemplateMatchOutcome.NoExpectedData,
                        CurrentBundleSignature = lastBundle.BundleSignature,
                    };

                // 3. 解析 NextExpectedMapJson → 预期下一步的信号列表
                var nextExpectedItems = ParseNextExpectedMap(bestTemplate.NextExpectedMapJson);
                if (nextExpectedItems == null || nextExpectedItems.Count == 0)
                    return new TemplateMatchResult
                    {
                        Outcome = TemplateMatchOutcome.NoExpectedData,
                        CurrentBundleSignature = lastBundle.BundleSignature,
                    };

                // 收集预期的所有信号键（前3个最可能的Bundle拆解为单个信号键）
                var expectedSignalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in nextExpectedItems.OrderByDescending(e => e.Probability).Take(3))
                {
                    if (!string.IsNullOrWhiteSpace(item.BundleSignature))
                    {
                        foreach (var key in item.BundleSignature.Split('|', StringSplitOptions.RemoveEmptyEntries))
                            expectedSignalKeys.Add(key.Trim());
                    }
                }

                if (expectedSignalKeys.Count == 0)
                    return new TemplateMatchResult
                    {
                        Outcome = TemplateMatchOutcome.NoExpectedData,
                        CurrentBundleSignature = lastBundle.BundleSignature,
                    };

                // 4. 获取最近变化中已经出现的信号（排除已完成的）
                var recentChanges = await _stationShadowStateCacheService
                    .GetRecentChangesAsync(projectId, stationCode, 200);
                var recentSignalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (recentChanges != null)
                {
                    // 只看最后一个 Bundle 之后的变化
                    foreach (var change in recentChanges)
                    {
                        if (change.ChangedAt.HasValue && lastBundle.EndTime.HasValue
                            && change.ChangedAt.Value > lastBundle.EndTime.Value)
                        {
                            if (!string.IsNullOrWhiteSpace(change.SignalKey))
                                recentSignalKeys.Add(change.SignalKey.Trim());
                        }
                    }
                }

                // 5. 找出"预期会变但没变"的信号 = 诊断目标
                var missingSignalKeys = expectedSignalKeys
                    .Where(k => !recentSignalKeys.Contains(k))
                    .ToList();

                if (missingSignalKeys.Count == 0)
                    return new TemplateMatchResult
                    {
                        Outcome = TemplateMatchOutcome.AllExpectedOccurred,
                        CurrentBundleSignature = lastBundle.BundleSignature,
                    };

                // 6. 将信号键映射到 AST 锚点
                var candidates = new List<StallCandidateInfo>();
                var bindings = await _stationAnchorBindingService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == projectId
                    && it.StationCode == stationCode);

                foreach (var signalKey in missingSignalKeys)
                {
                    var binding = bindings?.FirstOrDefault(b =>
                        string.Equals(b.AnchorKey, signalKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(b.VariableName, signalKey, StringComparison.OrdinalIgnoreCase));

                    var variableName = binding?.VariableName ?? signalKey;
                    var astMatch = FindBestRuntimeAstOutputMatch(variableName, astRows);
                    if (astMatch == null) continue;

                    if (candidates.Any(c => string.Equals(c.VariableName, variableName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    candidates.Add(new StallCandidateInfo
                    {
                        VariableName = variableName,
                        SignalKey = signalKey,
                        ChangedAt = lastBundle.EndTime ?? DateTime.Now,
                        LogicUnitId = astMatch.LogicUnitId,
                        BlockName = astMatch.BlockName,
                        DisplayLabel = signalKey,
                        UnitType = astMatch.UnitType,
                        IsGraphAction = IsGraphRelated(astMatch),
                    });
                }

                // 7. 计算匹配度评分（T4.2）
                var topExpected = nextExpectedItems.OrderByDescending(e => e.Probability).First();
                var confidenceScore = (int)Math.Round(Math.Min((double)(bestTemplate.Confidence ?? 0), 1.0) * 40); // 0-40
                var supportScore = Math.Min((bestTemplate.SupportCount ?? 0) * 4, 30); // 0-30（≥8次满分）
                var coverageScore = expectedSignalKeys.Count > 0
                    ? (int)Math.Round((double)missingSignalKeys.Count / expectedSignalKeys.Count * 30) // 0-30
                    : 0;
                var matchScore = confidenceScore + supportScore + coverageScore;

                var scoreDetail = $"置信度:{confidenceScore}/40 支持数:{supportScore}/30({bestTemplate.SupportCount ?? 0}次) " +
                    $"缺失覆盖:{coverageScore}/30({missingSignalKeys.Count}/{expectedSignalKeys.Count})";

                if (candidates.Count > 0)
                {
                    summary.TemplateMatchInfo = $"模板命中: 最后Bundle=[{lastBundle.BundleSignature}] → " +
                        $"预期下一步=[{topExpected.BundleSignature}](概率{topExpected.Probability:P0},支持{bestTemplate.SupportCount ?? 0}次) → " +
                        $"缺失{missingSignalKeys.Count}个信号 → 聚焦{candidates.Count}个锚点 | 评分={matchScore}/100({scoreDetail})";

                    await WriteLogHelper.WriteLogsAsync(
                        $"[诊断] {stationCode} 模板驱动: {summary.TemplateMatchInfo}",
                        "LineDiagnosis/TemplateMatch");

                    return new TemplateMatchResult
                    {
                        Outcome = TemplateMatchOutcome.Matched,
                        Candidates = candidates,
                        CurrentBundleSignature = lastBundle.BundleSignature,
                        MatchScore = matchScore,
                        MatchScoreDetail = scoreDetail,
                    };
                }
                else
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[诊断] {stationCode} 模板未匹配到锚点: 预期信号={string.Join(",", missingSignalKeys)} 但无对应AST",
                        "LineDiagnosis/TemplateMatch");

                    return new TemplateMatchResult
                    {
                        Outcome = TemplateMatchOutcome.NoAstMapping,
                        CurrentBundleSignature = lastBundle.BundleSignature,
                        MatchScore = matchScore,
                        MatchScoreDetail = scoreDetail,
                    };
                }
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[诊断] {stationCode} 模板匹配异常: {ex.Message}",
                    "LineDiagnosis/TemplateMatch");
                return new TemplateMatchResult { Outcome = TemplateMatchOutcome.Error };
            }
        }

        /// <summary>
        /// 解析 NextExpectedMapJson 为结构化列表。
        /// </summary>
        private static List<NextExpectedItem> ParseNextExpectedMap(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<List<NextExpectedItem>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
            }
            catch
            {
                return null;
            }
        }

        private class NextExpectedItem
        {
            public string BundleSignature { get; set; }
            public int Count { get; set; }
            public double Probability { get; set; }
            public double AvgGapMs { get; set; }
            public double MinGapMs { get; set; }
            public double MaxGapMs { get; set; }
        }

        /// <summary>
        /// 模板匹配结果分类
        /// </summary>
        private enum TemplateMatchOutcome
        {
            /// <summary>Redis 无 Bundle（监控未启动或冷启动）</summary>
            NoBundles,
            /// <summary>有 Bundle 但模板表无匹配签名（新通道/新产品模式）</summary>
            UnknownSignature,
            /// <summary>模板命中但 NextExpectedMap 无数据</summary>
            NoExpectedData,
            /// <summary>所有预期信号都已发生（模式正常推进中）</summary>
            AllExpectedOccurred,
            /// <summary>预期信号未映射到 AST 锚点</summary>
            NoAstMapping,
            /// <summary>模板命中且找到诊断目标</summary>
            Matched,
            /// <summary>匹配过程异常</summary>
            Error,
        }

        private class TemplateMatchResult
        {
            public TemplateMatchOutcome Outcome { get; set; }
            public List<StallCandidateInfo> Candidates { get; set; }
            /// <summary>当前 Bundle 签名（用于未知签名追踪）</summary>
            public string CurrentBundleSignature { get; set; }
            /// <summary>匹配度评分 0-100（T4.2：综合置信度、支持数、缺失比例）</summary>
            public int MatchScore { get; set; }
            /// <summary>评分明细</summary>
            public string MatchScoreDetail { get; set; }
        }

        /// <summary>
        /// 从 Redis 滑动窗口定位卡滞候选 Q 点（零 DB 查询）。
        /// Redis List (changes) 保留最近 2000 条变化 / TTL 6h，天然是近期滑动历史。
        /// 逻辑：取最近 5 分钟内变为 TRUE 的信号，按时间倒序逐一排查。
        /// </summary>
        private async Task<List<StallCandidateInfo>> LocateStallCandidatesAsync(
            long projectId, string stationCode, List<PLC_LogicUnitAstEntity> astRows)
        {
            // 1. 从 Redis 读取最近变化（滑动窗口，最多取 500 条）
            var recentChanges = await _stationShadowStateCacheService.GetRecentChangesAsync(projectId, stationCode, 500);
            if (recentChanges == null || recentChanges.Count == 0)
            {
                return new List<StallCandidateInfo>();
            }

            // 2. 时间窗口：优先取最近 5 分钟，若窗口内无数据则自动回退到“最近变化”模式
            //    避免停机超过 5 分钟后找不到任何候选，导致降级为全扫描把所有 FALSE 都报为异常
            var windowStart = DateTime.Now.AddMinutes(-5);
            var windowedChanges = recentChanges
                .Where(c => c.ChangedAt.HasValue && c.ChangedAt.Value >= windowStart)
                .ToList();

            // 若 5 分钟窗口内无数据，回退到最近 200 条变化记录（覆盖停机时刻前后的变化）
            List<StopCauseDiagnosis.Models.StationShadowChangeCacheModel> effectiveChanges;
            if (windowedChanges.Count > 0)
            {
                effectiveChanges = windowedChanges;
            }
            else
            {
                effectiveChanges = recentChanges
                    .Where(c => c.ChangedAt.HasValue)
                    .Take(200)
                    .ToList();
            }

            // 3. 筛选：有效变化记录中值变为 TRUE 的信号
            var candidates = new List<StallCandidateInfo>();
            foreach (var change in effectiveChanges)
            {
                // 判断是否为有效变化（新值非空即纳入候选，由后续AST求值判断当前状态）
                if (!IsActivationChange(change.OldValue, change.NewValue)) continue;

                // 排除 STRING 信号：字符串信号不构成停机候选
                if (IsStringSignalChange(change)) continue;

                // 优先用 VariableName，降级用 SignalKey 匹配 AST
                var variableName = !string.IsNullOrWhiteSpace(change.VariableName)
                    ? change.VariableName.Trim()
                    : change.SignalKey?.Trim();
                if (string.IsNullOrWhiteSpace(variableName)) continue;

                // 去重：同一变量只取最近一次
                if (candidates.Any(c => string.Equals(c.VariableName, variableName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // 匹配已编译 AST
                var astMatch = FindBestRuntimeAstOutputMatch(variableName, astRows);
                if (astMatch == null) continue;

                var isGraph = IsGraphRelated(astMatch);
                candidates.Add(new StallCandidateInfo
                {
                    VariableName = variableName,
                    SignalKey = change.SignalKey,
                    ChangedAt = change.ChangedAt ?? DateTime.Now,
                    LogicUnitId = astMatch.LogicUnitId,
                    BlockName = astMatch.BlockName,
                    DisplayLabel = change.SignalKey ?? variableName,
                    UnitType = astMatch.UnitType,
                    IsGraphAction = isGraph,
                    ActivationDirection = isGraph ? ClassifyActivationDirection(change.OldValue, change.NewValue) : "Unknown",
                });
            }

            // 4. I 点增强：扫描最近变化的 I 点信号，通过反向索引关联到受影响的 AST/Q 点
            try
            {
                var iPointReverseIndex = await _iPointReverseIndexService.BuildReverseIndexAsync(projectId, stationCode);
                if (iPointReverseIndex.Count > 0)
                {
                    var existingVars = new HashSet<string>(
                        candidates.Select(c => c.VariableName), StringComparer.OrdinalIgnoreCase);

                    foreach (var change in effectiveChanges)
                    {
                        var varName = !string.IsNullOrWhiteSpace(change.VariableName)
                            ? change.VariableName.Trim()
                            : change.SignalKey?.Trim();
                        if (string.IsNullOrWhiteSpace(varName)) continue;

                        // 仅处理在反向索引里的 I 点
                        if (!iPointReverseIndex.TryGetValue(varName, out var deps)) continue;

                        // 该 I 点影响的 AST 输出，如果还没在候选列表，则追加
                        foreach (var dep in deps)
                        {
                            foreach (var outVar in dep.OutputVariables)
                            {
                                if (existingVars.Contains(outVar)) continue;

                                var astMatch = FindBestRuntimeAstOutputMatch(outVar, astRows);
                                if (astMatch == null) continue;

                                existingVars.Add(outVar);
                                candidates.Add(new StallCandidateInfo
                                {
                                    VariableName = outVar,
                                    SignalKey = $"[I点触发:{varName}] {outVar}",
                                    ChangedAt = change.ChangedAt ?? DateTime.Now,
                                    LogicUnitId = astMatch.LogicUnitId,
                                    BlockName = astMatch.BlockName,
                                    DisplayLabel = $"[I点:{varName}→]{outVar}",
                                    UnitType = astMatch.UnitType,
                                    IsGraphAction = IsGraphRelated(astMatch),
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // I 点增强失败不阻断主流程
                await WriteLogHelper.WriteLogsAsync(
                    $"[诊断] I点反向索引增强失败（不影响主流程）: {ex.Message}",
                    "LineDiagnosis/IPointIndex");
            }

            // 5. STRING 降级候选补充：当 GRAPH 候选为 0 但窗口内有 STRING 变化对应 GRAPH 块时，
            //    将这些 GRAPH 块的 StepAction 作为降级候选（STRING 变化可能是步骤内的信息更新）
            var graphCandidateCount = candidates.Count(c => c.IsGraphAction);
            if (graphCandidateCount == 0)
            {
                try
                {
                    var existingVarNames = new HashSet<string>(
                        candidates.Select(c => c.VariableName), StringComparer.OrdinalIgnoreCase);

                    foreach (var change in effectiveChanges)
                    {
                        // 只处理被 STRING 过滤排除的信号
                        if (!IsStringSignalChange(change)) continue;

                        var varName = !string.IsNullOrWhiteSpace(change.VariableName)
                            ? change.VariableName.Trim()
                            : change.SignalKey?.Trim();
                        if (string.IsNullOrWhiteSpace(varName)) continue;

                        // 尝试找到对应的 GRAPH StepAction AST
                        var astMatch = FindBestRuntimeAstOutputMatch(varName, astRows);
                        if (astMatch == null) continue;
                        if (!IsGraphRelated(astMatch)) continue;
                        if (existingVarNames.Contains(varName)) continue;

                        existingVarNames.Add(varName);
                        candidates.Add(new StallCandidateInfo
                        {
                            VariableName = varName,
                            SignalKey = $"[STRING降级] {change.SignalKey ?? varName}",
                            ChangedAt = change.ChangedAt ?? DateTime.Now,
                            LogicUnitId = astMatch.LogicUnitId,
                            BlockName = astMatch.BlockName,
                            DisplayLabel = $"[STRING降级→GRAPH]{varName}",
                            UnitType = astMatch.UnitType,
                            IsGraphAction = true,
                            ActivationDirection = ClassifyActivationDirection(change.OldValue, change.NewValue),
                        });
                    }
                }
                catch (Exception strEx)
                {
                    // STRING 降级失败不阻断主流程
                    await WriteLogHelper.WriteLogsAsync(
                        $"[诊断] STRING降级候选补充失败: {strEx.Message}",
                        "LineDiagnosis/StringDegradedCandidates");
                }
            }

            // 6. GRAPH 候选排序：SetTrue(当前激活步骤) 优先，ResetFalse(刚完成步骤) 次之
            // 这样 EvaluateStallCandidatesAsync 会先处理当前卡住的步骤
            candidates = candidates
                .OrderByDescending(c => c.IsGraphAction ? 1 : 0) // GRAPH 优先
                .ThenBy(c => c.ActivationDirection == "SetTrue" ? 0 : c.ActivationDirection == "ResetFalse" ? 1 : 2)
                .ThenByDescending(c => c.ChangedAt) // 时间倒序
                .ToList();

            return candidates;
        }

        /// <summary>
        /// 两阶段 GRAPH 优先诊断：
        /// Phase 1（GraphTransitionTrace）：先评估 GRAPH StepAction 内的 Q 点，
        ///   如果活跃(TRUE) → 提取出口 Transition 条件 → 找到未满足的转移条件 → AST 下探到根因。
        /// Phase 2（EnableMissing）：如果 Phase 1 未定位到根因 → 评估剩余非 GRAPH Q 点，
        ///   FALSE 的即"该使能但未使能" → AST 下探到根因。
        /// </summary>
        private async Task EvaluateStallCandidatesAsync(
            long projectId, string stationCode,
            List<StallCandidateInfo> candidates,
            List<PLC_LogicUnitAstEntity> astRows,
            StationDiagnosisSummaryDto summary,
            int maxAnchors, int maxFbDepth = 4,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary.AnchorCount = candidates.Count;
            const int maxKeyFindings = 200;

            // ── 拆分候选：GRAPH StepAction vs 非 GRAPH ──
            var graphCandidates = candidates.Where(c => c.IsGraphAction).ToList();
            var nonGraphCandidates = candidates.Where(c => !c.IsGraphAction).ToList();

            int totalEvaluatedCount = 0;
            int totalFalseCount = 0;
            bool graphPhaseFoundRootCause = false;

            // 跨所有 anchor 共享的已访问变量集，避免不同 anchor 追踪到同一根因时重复输出
            var sharedVisitedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 已评估过的 anchor 变量名去重（同一变量可能出现在多个 stall candidate 中）
            var evaluatedAnchorVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ═══════════════════════════════════════════════════════════
            // Phase 1：GRAPH 优先 — 活跃的 StepAction 查出口转移条件
            // ═══════════════════════════════════════════════════════════
            if (graphCandidates.Count > 0)
            {
                // 对 LogicUnitId 为 null 的候选，通过 AST 反查补上，确保不漏评
                var graphTargets = new List<StopCauseAstRuntimeEvaluationService.EvaluateBatchTarget>();
                foreach (var c in graphCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var luId = c.LogicUnitId;
                    if (!luId.HasValue && !string.IsNullOrWhiteSpace(c.VariableName))
                    {
                        var fallbackAst = FindBestRuntimeAstOutputMatch(c.VariableName, astRows);
                        if (fallbackAst?.LogicUnitId != null)
                            luId = fallbackAst.LogicUnitId;
                    }
                    if (!luId.HasValue) continue;
                    graphTargets.Add(new StopCauseAstRuntimeEvaluationService.EvaluateBatchTarget
                    {
                        LogicUnitId = luId.Value,
                        VariableName = c.VariableName,
                        DisplayLabel = c.DisplayLabel,
                        Tag = c,
                    });
                }

                var graphBatchRequest = new StopCauseAstRuntimeEvaluationService.EvaluateBatchRequest
                {
                    ProjectId = projectId,
                    StationCode = stationCode,
                    PreloadedAstRows = astRows,
                    Targets = graphTargets,
                };

                var graphResults = await _runtimeEvaluationService.EvaluateBatchAsync(graphBatchRequest);
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var item in graphResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = graphCandidates.FirstOrDefault(c =>
                        c.LogicUnitId == item.LogicUnitId &&
                        string.Equals(c.VariableName, item.VariableName, StringComparison.OrdinalIgnoreCase))
                        ?? graphCandidates.FirstOrDefault(c => c.LogicUnitId == item.LogicUnitId);

                    // 同一变量已通过其他 candidate 评估过 → 跳过避免重复 findings
                    var anchorKey = (item.VariableName ?? candidate?.VariableName ?? "").Trim();
                    if (!string.IsNullOrEmpty(anchorKey) && !evaluatedAnchorVariables.Add(anchorKey))
                        continue;

                    if (!item.Success)
                    {
                        if (summary.KeyFindings.Count < maxKeyFindings)
                        {
                            summary.KeyFindings.Add(new StationAnchorFinding
                            {
                                VariableName = item.VariableName,
                                DisplayName = candidate?.DisplayLabel ?? item.VariableName,
                                EvaluationStatus = "Error",
                                BlockName = candidate?.BlockName,
                                FailurePathSummary = item.Error,
                                LastTrueAt = ResolveFindingLastTrueAt(candidate),
                                DiagnosisPhase = "GraphTransitionTrace",
                            });
                        }
                        continue;
                    }

                    var evalResult = item.EvalResult;
                    totalEvaluatedCount++;

                    // 将外部可配置的 FB 穿透深度写入求值上下文，供 PenetrateFbCallOutputAsync 使用
                    if (evalResult?.ExecutionContext != null)
                        evalResult.ExecutionContext.MaxFbPenetrationDepth =
                            ResolveCandidateMaxFbPenetrationDepth(candidate, maxFbDepth);

                    if (candidate?.IsGraphExecBitFallback == true)
                    {
                        var compensatedFindings = await TryCompensateTransitionEvaluationAsync(
                            projectId, stationCode, evalResult, candidate, astRows);
                        cancellationToken.ThrowIfCancellationRequested();

                        var falseTransCount = compensatedFindings.Count(f => f.EvaluatedValue == false);
                        var totalTransCount = compensatedFindings.Count;
                        if (falseTransCount > 0)
                            graphPhaseFoundRootCause = true;

                        if (summary.KeyFindings.Count < maxKeyFindings)
                        {
                            summary.KeyFindings.Add(new StationAnchorFinding
                            {
                                VariableName = candidate.VariableName,
                                DisplayName = candidate.DisplayLabel ?? candidate.VariableName,
                                EvaluationStatus = "True",
                                EvaluatedValue = true,
                                DiagnosisQuality = "Diagnosed",
                                BlockName = candidate.BlockName ?? evalResult.BlockName,
                                FailurePathSummary = totalTransCount > 0
                                    ? $"[GRAPH EXEC_BITS候补] 步骤位激活(TRUE)，{falseTransCount}/{totalTransCount} 个出口转移条件未满足"
                                    : "[GRAPH EXEC_BITS候补] 步骤位激活(TRUE)，但未找到出口 Transition 条件，无法继续定位转移阻塞点",
                                LastTrueAt = ResolveFindingLastTrueAt(candidate),
                                TraceDepth = 0,
                                FaultOwnership = "InternalFault",
                                DiagnosisPhase = "GraphTransitionTrace",
                            });
                        }

                        foreach (var tf in compensatedFindings)
                        {
                            if (summary.KeyFindings.Count >= maxKeyFindings) break;
                            tf.DiagnosisPhase = "GraphTransitionTrace";
                            summary.KeyFindings.Add(tf);
                        }

                        foreach (var tf in compensatedFindings.Where(f => f.EvaluatedValue == false))
                        {
                            if (summary.KeyFindings.Count >= maxKeyFindings) break;
                            if (string.IsNullOrWhiteSpace(tf.VariableName))
                                continue;

                            var writerAst = astRows.FirstOrDefault(a =>
                            {
                                var outputs = DeserializeStringListSafe(a.OutputVariablesJson);
                                return outputs.Any(o => string.Equals(o, tf.VariableName, StringComparison.OrdinalIgnoreCase));
                            });
                            if (writerAst == null)
                                continue;

                            var singleEvalResult = await _runtimeEvaluationService.EvaluateAsync(
                                new EvaluatePersistedAstRequestDto
                                {
                                    ProjectId = projectId,
                                    StationCode = stationCode,
                                    LogicUnitId = writerAst.LogicUnitId ?? 0,
                                });
                            if (singleEvalResult?.RootEvaluation != null)
                            {
                                await TraceFailureWriteSourcesAsync(
                                    projectId, stationCode, astRows,
                                    singleEvalResult.RootEvaluation, summary, maxKeyFindings / 2, depth: 1,
                                    visitedVariables: sharedVisitedVariables,
                                    diagnosisPhase: "GraphTransitionTrace",
                                    sharedContext: evalResult.ExecutionContext);
                            }
                        }

                        continue;
                    }

                    if (evalResult.EvaluatedValue == true)
                    {
                        // GRAPH action 活跃(TRUE) → 检查出口 Transition 条件
                        var transitionFindings = ExtractTransitionFailureFindings(
                            evalResult.RootEvaluation, candidate, stationCode);

                        if (transitionFindings.Count > 0)
                        {
                            var falseTransCount = transitionFindings.Count(f => f.EvaluatedValue == false);
                            var totalTransCount = transitionFindings.Count;

                            if (falseTransCount > 0)
                            {
                                // 有 FALSE 的转移条件 → 定位到根因
                                graphPhaseFoundRootCause = true;
                            }

                            var finding = new StationAnchorFinding
                            {
                                VariableName = candidate?.VariableName ?? item.VariableName,
                                DisplayName = candidate?.DisplayLabel ?? item.VariableName,
                                EvaluationStatus = evalResult.EvaluationStatus,
                                EvaluatedValue = evalResult.EvaluatedValue,
                                DiagnosisQuality = evalResult.DiagnosisQuality,
                                BlockName = candidate?.BlockName ?? evalResult.BlockName,
                                FailurePathSummary = $"[GRAPH优先] 步骤激活(TRUE)，{falseTransCount}/{totalTransCount} 个出口转移条件未满足",
                                LastTrueAt = ResolveFindingLastTrueAt(candidate),
                                TraceDepth = 0,
                                FaultOwnership = "InternalFault",
                                DiagnosisPhase = "GraphTransitionTrace",
                                SignalHistoryAnalysis = await AnalyzeFailurePathHistoryAsync(
                                    projectId, stationCode, evalResult.FailurePath),
                            };
                            if (summary.KeyFindings.Count < maxKeyFindings)
                                summary.KeyFindings.Add(finding);

                            // 将每个 Transition 的详细信息也加入 Findings
                            foreach (var tf in transitionFindings)
                            {
                                if (summary.KeyFindings.Count >= maxKeyFindings) break;
                                tf.DiagnosisPhase = "GraphTransitionTrace";
                                summary.KeyFindings.Add(tf);
                            }

                            // 对所有 Transition（不限于 FALSE）内部的 FALSE 叶子信号做跨 LogicUnit 深度追溯
                            // 原因：即使 Transition 整体为 TRUE，其内部仍可能有 FALSE 的子信号需要追因
                            var allTransTraces = GetAllTransitionTraces(evalResult.RootEvaluation);
                            await WriteLogHelper.WriteLogsAsync(
                                $"[诊断·深追踪] {stationCode} 候选={candidate?.VariableName} " +
                                $"GetAllTransitionTraces 找到 {allTransTraces.Count} 个 Transition trace " +
                                $"(rootEval.NodeType={evalResult.RootEvaluation?.NodeType} Children={evalResult.RootEvaluation?.Children?.Count ?? -1})" +
                                (allTransTraces.Count > 0
                                    ? $" traceTypes=[{string.Join(",", allTransTraces.Select(t => $"{t.NodeType}:{t.NodeId}:{t.Status}"))}]"
                                    : " → 未找到任何 Transition，深追踪将跳过"),
                                "LineDiagnosis/DeepTrace");

                            foreach (var transTrace in allTransTraces)
                            {
                                if (summary.KeyFindings.Count >= maxKeyFindings) break;
                                var beforeCount = summary.KeyFindings.Count;
                                try
                                {
                                    await TraceFailureWriteSourcesAsync(
                                        projectId, stationCode, astRows,
                                        transTrace, summary, maxKeyFindings / 2, depth: 1,
                                        visitedVariables: sharedVisitedVariables,
                                        diagnosisPhase: "GraphTransitionTrace",
                                        sharedContext: evalResult.ExecutionContext);
                                }
                                catch (Exception traceEx)
                                {
                                    await WriteLogHelper.WriteLogsAsync(
                                        $"[诊断·深追踪] {stationCode} TraceFailureWriteSourcesAsync 异常: {traceEx.Message}\n{traceEx.StackTrace}",
                                        "LineDiagnosis/DeepTrace");
                                    summary.KeyFindings.Add(new StationAnchorFinding
                                    {
                                        VariableName = transTrace.VariableName ?? transTrace.NodeId,
                                        DisplayName = $"[深追踪异常] {transTrace.VariableName ?? transTrace.NodeId}",
                                        EvaluationStatus = "Error",
                                        FailurePathSummary = $"深追踪异常: {traceEx.Message}",
                                        TraceDepth = 1,
                                        DiagnosisPhase = "GraphTransitionTrace",
                                    });
                                }
                                var afterCount = summary.KeyFindings.Count;
                                await WriteLogHelper.WriteLogsAsync(
                                    $"[诊断·深追踪] {stationCode} Transition={transTrace.NodeId} Status={transTrace.Status} " +
                                    $"Children={transTrace.Children?.Count ?? -1} " +
                                    $"新增Findings={afterCount - beforeCount} visitedVars=[{string.Join(",", sharedVisitedVariables.Take(20))}]",
                                    "LineDiagnosis/DeepTrace");
                            }
                        }
                        else
                        {
                            // Transition 未通过求值树注入（可能 NodeType 非 Output 导致 EvaluateOutgoingTransitionsIfApplicable 未执行）
                            // → 补偿：搜索同 Block 内所有输出该变量的 StepAction，查找 OutgoingTransitionNetworkIds 并求值
                            var compensatedFindings = await TryCompensateTransitionEvaluationAsync(
                                projectId, stationCode, evalResult, candidate, astRows);

                            if (compensatedFindings.Count > 0)
                            {
                                var falseTransCount = compensatedFindings.Count(f => f.EvaluatedValue == false);
                                var totalTransCount = compensatedFindings.Count;

                                if (falseTransCount > 0)
                                    graphPhaseFoundRootCause = true;

                                var finding = new StationAnchorFinding
                                {
                                    VariableName = candidate?.VariableName ?? item.VariableName,
                                    DisplayName = candidate?.DisplayLabel ?? item.VariableName,
                                    EvaluationStatus = evalResult.EvaluationStatus,
                                    EvaluatedValue = evalResult.EvaluatedValue,
                                    DiagnosisQuality = evalResult.DiagnosisQuality,
                                    BlockName = candidate?.BlockName ?? evalResult.BlockName,
                                    FailurePathSummary = $"[GRAPH优先·补偿] 步骤激活(TRUE)，{falseTransCount}/{totalTransCount} 个出口转移条件未满足",
                                    LastTrueAt = ResolveFindingLastTrueAt(candidate),
                                    TraceDepth = 0,
                                    FaultOwnership = "InternalFault",
                                    DiagnosisPhase = "GraphTransitionTrace",
                                    SignalHistoryAnalysis = await AnalyzeFailurePathHistoryAsync(
                                        projectId, stationCode, evalResult.FailurePath),
                                };
                                if (summary.KeyFindings.Count < maxKeyFindings)
                                    summary.KeyFindings.Add(finding);

                                foreach (var tf in compensatedFindings)
                                {
                                    if (summary.KeyFindings.Count >= maxKeyFindings) break;
                                    tf.DiagnosisPhase = "GraphTransitionTrace";
                                    summary.KeyFindings.Add(tf);
                                }

                                // 对 FALSE 的 Transition trace 做深度追溯
                                foreach (var tf in compensatedFindings.Where(f => f.EvaluatedValue == false))
                                {
                                    if (summary.KeyFindings.Count >= maxKeyFindings) break;
                                    // 通过变量名从 astRows 查找写入源进行追溯
                                    if (!string.IsNullOrWhiteSpace(tf.VariableName))
                                    {
                                        var writerAst = astRows.FirstOrDefault(a =>
                                        {
                                            var outputs = DeserializeStringListSafe(a.OutputVariablesJson);
                                            return outputs.Any(o => string.Equals(o, tf.VariableName, StringComparison.OrdinalIgnoreCase));
                                        });
                                        if (writerAst != null)
                                        {
                                            var singleEvalResult = await _runtimeEvaluationService.EvaluateAsync(
                                                new EvaluatePersistedAstRequestDto
                                                {
                                                    ProjectId = projectId,
                                                    StationCode = stationCode,
                                                    LogicUnitId = writerAst.LogicUnitId ?? 0,
                                                });
                                            if (singleEvalResult?.RootEvaluation != null)
                                            {
                                                await TraceFailureWriteSourcesAsync(
                                                    projectId, stationCode, astRows,
                                                    singleEvalResult.RootEvaluation, summary, maxKeyFindings / 2, depth: 1,
                                                    visitedVariables: sharedVisitedVariables,
                                                    diagnosisPhase: "GraphTransitionTrace",
                                                    sharedContext: evalResult.ExecutionContext);
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // 确实没有出口转移条件 → 执行器可能未响应
                                if (summary.KeyFindings.Count < maxKeyFindings)
                                {
                                    summary.KeyFindings.Add(new StationAnchorFinding
                                    {
                                        VariableName = candidate?.VariableName ?? item.VariableName,
                                        DisplayName = candidate?.DisplayLabel ?? item.VariableName,
                                        EvaluationStatus = evalResult.EvaluationStatus,
                                        EvaluatedValue = evalResult.EvaluatedValue,
                                        DiagnosisQuality = evalResult.DiagnosisQuality,
                                        BlockName = candidate?.BlockName ?? evalResult.BlockName,
                                        FailurePathSummary = "[GRAPH优先] 条件满足(TRUE)但动作未产生后续变化，可能是执行器未响应或等待外部信号",
                                        LastTrueAt = ResolveFindingLastTrueAt(candidate),
                                        TraceDepth = 0,
                                        FaultOwnership = "InternalFault",
                                        DiagnosisPhase = "GraphTransitionTrace",
                                        SignalHistoryAnalysis = await AnalyzeFailurePathHistoryAsync(
                                            projectId, stationCode, evalResult.FailurePath),
                                    });
                                }
                            }
                        }
                    }
                    else if (evalResult.EvaluatedValue == false)
                    {
                        // GRAPH action 未激活(FALSE) → 步骤当前未执行，无诊断价值，仅计数
                        totalFalseCount++;
                    }
                }

                await WriteLogHelper.WriteLogsAsync(
                    $"[诊断] {stationCode} Phase1(GRAPH优先) 候选={graphCandidates.Count} " +
                    $"已评估={totalEvaluatedCount} 找到根因={graphPhaseFoundRootCause} " +
                    $"Findings={summary.KeyFindings.Count}",
                    "LineDiagnosis/GraphPriority");
            }

            // ═══════════════════════════════════════════════════════════
            // Phase 2：EnableMissing — 该使能但未使能的非 GRAPH Q 点
            // 仅在 Phase 1 未通过转移条件定位到根因时执行
            // ═══════════════════════════════════════════════════════════
            var nonGraphCandidatesToEvaluate = graphPhaseFoundRootCause
                ? nonGraphCandidates.Where(c => c.IsDeviceFocus).ToList()
                : nonGraphCandidates;
            var skippedNonDeviceNonGraphCount = graphPhaseFoundRootCause
                ? nonGraphCandidates.Count - nonGraphCandidatesToEvaluate.Count
                : 0;

            var directDeviceFocusCandidates = nonGraphCandidatesToEvaluate
                .Where(ShouldReportDeviceFocusDirectly)
                .ToList();
            foreach (var candidate in directDeviceFocusCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var anchorKey = candidate.VariableName?.Trim();
                if (!string.IsNullOrEmpty(anchorKey) && !evaluatedAnchorVariables.Add(anchorKey))
                    continue;

                totalEvaluatedCount++;
                totalFalseCount++;
                if (summary.KeyFindings.Count < maxKeyFindings)
                {
                    summary.KeyFindings.Add(BuildDirectDeviceFocusFinding(candidate));
                }
            }

            if (directDeviceFocusCandidates.Count > 0)
            {
                nonGraphCandidatesToEvaluate = nonGraphCandidatesToEvaluate
                    .Except(directDeviceFocusCandidates)
                    .ToList();
            }

            if (nonGraphCandidatesToEvaluate.Count > 0)
            {
                var nonGraphBatchRequest = new StopCauseAstRuntimeEvaluationService.EvaluateBatchRequest
                {
                    ProjectId = projectId,
                    StationCode = stationCode,
                    PreloadedAstRows = astRows,
                    Targets = nonGraphCandidatesToEvaluate
                        .Where(c => c.LogicUnitId.HasValue)
                        .Select(c => new StopCauseAstRuntimeEvaluationService.EvaluateBatchTarget
                        {
                            LogicUnitId = c.LogicUnitId.Value,
                            VariableName = c.VariableName,
                            DisplayLabel = c.DisplayLabel,
                            Tag = c,
                        }).ToList(),
                };

                if (nonGraphBatchRequest.Targets.Count == 0)
                {
                    summary.EvaluatedCount = totalEvaluatedCount;
                    summary.FalseAnchorCount = totalFalseCount;
                    summary.OverallStatus = summary.KeyFindings.Count > 0 ? "SomeFalse" :
                        totalEvaluatedCount > 0 ? "AllTrue" : "NoAst";
                    return;
                }

                var nonGraphResults = await _runtimeEvaluationService.EvaluateBatchAsync(nonGraphBatchRequest);
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var item in nonGraphResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = nonGraphCandidatesToEvaluate.FirstOrDefault(c =>
                        c.LogicUnitId == item.LogicUnitId &&
                        string.Equals(c.VariableName, item.VariableName, StringComparison.OrdinalIgnoreCase))
                        ?? nonGraphCandidatesToEvaluate.FirstOrDefault(c => c.LogicUnitId == item.LogicUnitId);

                    // 同一变量已通过其他 candidate 评估过 → 跳过避免重复 findings
                    var anchorKey = (item.VariableName ?? candidate?.VariableName ?? "").Trim();
                    if (!string.IsNullOrEmpty(anchorKey) && !evaluatedAnchorVariables.Add(anchorKey))
                        continue;

                    if (!item.Success)
                    {
                        if (summary.KeyFindings.Count < maxKeyFindings)
                        {
                            summary.KeyFindings.Add(new StationAnchorFinding
                            {
                                VariableName = item.VariableName,
                                DisplayName = candidate?.DisplayLabel ?? item.VariableName,
                                EvaluationStatus = "Error",
                                BlockName = candidate?.BlockName,
                                FailurePathSummary = item.Error,
                                LastTrueAt = ResolveFindingLastTrueAt(candidate),
                                DiagnosisPhase = candidate?.IsDeviceFocus == true ? "DeviceFocusAbnormal" : "EnableMissing",
                            });
                        }
                        continue;
                    }

                    var evalResult = item.EvalResult;
                    totalEvaluatedCount++;

                    // 将外部可配置的 FB 穿透深度写入求值上下文，供 PenetrateFbCallOutputAsync 使用
                    if (evalResult?.ExecutionContext != null)
                        evalResult.ExecutionContext.MaxFbPenetrationDepth =
                            ResolveCandidateMaxFbPenetrationDepth(candidate, maxFbDepth);

                    var finding = new StationAnchorFinding
                    {
                        VariableName = candidate?.VariableName ?? item.VariableName,
                        DisplayName = candidate?.DisplayLabel ?? item.VariableName,
                        EvaluationStatus = evalResult.EvaluationStatus,
                        EvaluatedValue = evalResult.EvaluatedValue,
                        DiagnosisQuality = evalResult.DiagnosisQuality,
                        BlockName = candidate?.BlockName ?? evalResult.BlockName,
                        FailurePathSummary = BuildFailurePathSummary(evalResult.FailurePath),
                        LastTrueAt = ResolveFindingLastTrueAt(candidate),
                        TraceDepth = 0,
                        FaultOwnership = "InternalFault",
                        DiagnosisPhase = "EnableMissing",
                    };

                    var nonGraphDiagnosisPhase = candidate?.IsDeviceFocus == true ? "DeviceFocusAbnormal" : "EnableMissing";
                    finding.DiagnosisPhase = nonGraphDiagnosisPhase;

                    if (evalResult.EvaluatedValue == false)
                    {
                        totalFalseCount++;
                        if (candidate?.IsDeviceFocus == true)
                        {
                            finding.FailurePathSummary = BuildDeviceFocusAbnormalFindingSummary(candidate, evalResult.EvaluatedValue);
                        }

                        finding.SignalHistoryAnalysis = await AnalyzeFailurePathHistoryAsync(
                            projectId, stationCode, evalResult.FailurePath);

                        if (summary.KeyFindings.Count < maxKeyFindings)
                            summary.KeyFindings.Add(finding);

                        await TraceFailureWriteSourcesAsync(
                            projectId, stationCode, astRows,
                            evalResult.RootEvaluation, summary, maxKeyFindings / 2, depth: 1,
                            visitedVariables: sharedVisitedVariables,
                            diagnosisPhase: nonGraphDiagnosisPhase,
                            sharedContext: evalResult.ExecutionContext);
                    }
                    else if (evalResult.EvaluatedValue == true)
                    {
                        if (IsDeviceFocusAbnormalTrueCandidate(candidate))
                        {
                            finding.FailurePathSummary = BuildDeviceFocusAbnormalFindingSummary(candidate, evalResult.EvaluatedValue);
                            finding.SignalHistoryAnalysis = await AnalyzeFailurePathHistoryAsync(
                                projectId, stationCode, evalResult.FailurePath);
                            if (summary.KeyFindings.Count < maxKeyFindings)
                                summary.KeyFindings.Add(finding);
                            continue;
                        }
                        // 非 GRAPH 的 TRUE Q 点也检查 Transition（兼容非标 GRAPH 结构）
                        var transitionFindings = ExtractTransitionFailureFindings(
                            evalResult.RootEvaluation, candidate, stationCode);

                        if (transitionFindings.Count > 0)
                        {
                            var falseTransCount = transitionFindings.Count(f => f.EvaluatedValue == false);
                            var totalTransCount = transitionFindings.Count;
                            finding.FailurePathSummary = $"步骤激活(TRUE)，{falseTransCount}/{totalTransCount} 个出口转移条件未满足";
                            finding.SignalHistoryAnalysis = await AnalyzeFailurePathHistoryAsync(
                                projectId, stationCode, evalResult.FailurePath);
                            finding.DiagnosisPhase = nonGraphDiagnosisPhase;
                            if (summary.KeyFindings.Count < maxKeyFindings)
                                summary.KeyFindings.Add(finding);

                            foreach (var tf in transitionFindings)
                            {
                                if (summary.KeyFindings.Count >= maxKeyFindings) break;
                                tf.DiagnosisPhase = nonGraphDiagnosisPhase;
                                summary.KeyFindings.Add(tf);
                            }

                            foreach (var transTrace in GetAllTransitionTraces(evalResult.RootEvaluation))
                            {
                                if (summary.KeyFindings.Count >= maxKeyFindings) break;
                                await TraceFailureWriteSourcesAsync(
                                    projectId, stationCode, astRows,
                                    transTrace, summary, maxKeyFindings / 2, depth: 1,
                                    visitedVariables: sharedVisitedVariables,
                                    diagnosisPhase: nonGraphDiagnosisPhase,
                                    sharedContext: evalResult.ExecutionContext);
                            }
                        }
                        else
                        {
                            finding.FailurePathSummary = "条件满足(TRUE)但动作未产生后续变化，可能是执行器未响应或等待外部信号";
                            finding.SignalHistoryAnalysis = await AnalyzeFailurePathHistoryAsync(
                                projectId, stationCode, evalResult.FailurePath);
                            if (summary.KeyFindings.Count < maxKeyFindings)
                                summary.KeyFindings.Add(finding);
                        }
                    }
                }

                await WriteLogHelper.WriteLogsAsync(
                    $"[诊断] {stationCode} Phase2(EnableMissing) 候选={nonGraphCandidates.Count} " +
                    $"累计评估={totalEvaluatedCount} False={totalFalseCount} " +
                    $"Findings={summary.KeyFindings.Count}",
                    "LineDiagnosis/EnableMissing");
            }
            else if (graphPhaseFoundRootCause && nonGraphCandidates.Count > 0)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[诊断] {stationCode} Phase1 已定位根因(GRAPH转移条件)，跳过 Phase2 的 {nonGraphCandidates.Count} 个非GRAPH候选",
                    "LineDiagnosis/GraphPriority");
            }

            summary.EvaluatedCount = totalEvaluatedCount;
            summary.FalseAnchorCount = totalFalseCount;
            // OverallStatus 只由有意义的 KeyFindings 决定，不能仅凭 totalFalseCount。
            // 原因：SuccessorChain/StallPoint 选出的候选本来就是"预期激活但尚未激活"的节点，
            // 瞬时求值绝大多数为 False 是选取逻辑决定的，不代表真正的故障。
            // 只有 AST 追溯出有明确失败路径的 KeyFindings 才算根因依据。
            summary.OverallStatus = summary.KeyFindings.Count > 0 ? "SomeFalse" :
                totalEvaluatedCount > 0 ? "AllTrue" : "NoAst";
        }

        /// <summary>
        /// 从 TRUE 求值的 StepAction trace 中提取 FALSE 的出口 Transition 子节点，
        /// 为每个 FALSE Transition 生成 StationAnchorFinding。
        /// 
        /// 注意：AST 根节点通常是 Anchor，StepAction 是其子节点，
        /// 而 Transition traces 被追加到 StepAction 子节点的 Children 中，
        /// 因此需要搜索两层深度才能找到 GraphTransition 节点。
        /// </summary>
        private static List<StationAnchorFinding> ExtractTransitionFailureFindings(
            StopCauseAstTraceNode rootEvaluation,
            string blockName,
            DateTime? changedAt,
            string stationCode)
        {
            var findings = new List<StationAnchorFinding>();
            if (rootEvaluation?.Children == null) return findings;

            // 递归收集所有 GraphTransition 节点（不限深度）
            var transitionNodes = new List<StopCauseAstTraceNode>();
            CollectTransitionNodesForExtraction(rootEvaluation, transitionNodes);

            System.Diagnostics.Debug.WriteLine(
                $"[ExtractTransFindings] rootEvaluation.NodeType={rootEvaluation.NodeType} 总计找到 {transitionNodes.Count} 个 GraphTransition 节点");

            foreach (var child in transitionNodes)
            {
                // 收集 Transition 中 FALSE 叶子信号的摘要
                var falseLeaves = new List<string>();
                CollectFalseLeafNames(child, falseLeaves);

                // 收集 Transition 内所有叶子信号的值摘要（用于展示根因细节）
                var allLeafDetails = new List<string>();
                CollectLeafSignalDetails(child, allLeafDetails);
                var detailSummary = allLeafDetails.Count > 0
                    ? string.Join("; ", allLeafDetails.Take(8))
                    : null;

                string failureSummary;
                if (child.Result == false)
                {
                    var falseLeafSummary = falseLeaves.Count > 0
                        ? string.Join(", ", falseLeaves.Take(5))
                        : null;
                    failureSummary = $"转移条件未满足{(falseLeafSummary != null ? $"，卡在: {falseLeafSummary}" : "")}";
                }
                else
                {
                    failureSummary = $"转移条件已满足({child.Status})";
                }
                if (detailSummary != null)
                    failureSummary += $" | 明细: {detailSummary}";

                findings.Add(new StationAnchorFinding
                {
                    VariableName = child.VariableName ?? child.DisplayName ?? child.NodeId,
                    DisplayName = $"[Transition] {child.DisplayName ?? child.NodeId}",
                    EvaluationStatus = child.Status,
                    EvaluatedValue = child.Result,
                    BlockName = blockName,
                    FailurePathSummary = failureSummary,
                    TraceDepth = 0,
                    FaultOwnership = "InternalFault",
                    LastTrueAt = changedAt,
                });
            }

            return findings;
        }

        /// <summary>
        /// 递归收集 trace 树中所有 GraphTransition 节点（不进入 Transition 内部子树）。
        /// </summary>
        private static void CollectTransitionNodesForExtraction(StopCauseAstTraceNode node, List<StopCauseAstTraceNode> result)
        {
            if (node?.Children == null) return;
            foreach (var child in node.Children)
            {
                if (string.Equals(child.NodeType, StopCauseDiagnosis.Models.StopCauseAstNodeType.GraphTransition, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(child);
                }
                else
                {
                    CollectTransitionNodesForExtraction(child, result);
                }
            }
        }

        /// <summary>
        /// ExtractTransitionFailureFindings 的 StallCandidateInfo 适配重载。
        /// </summary>
        private static List<StationAnchorFinding> ExtractTransitionFailureFindings(
            StopCauseAstTraceNode rootEvaluation,
            StallCandidateInfo candidate,
            string stationCode)
            => ExtractTransitionFailureFindings(rootEvaluation, candidate.BlockName, ResolveFindingLastTrueAt(candidate), stationCode);

        /// <summary>
        /// 补偿路径：当 ExtractTransitionFailureFindings 返回空（求值树中无 GraphTransition 子节点）时，
        /// 从 AST 元数据或同 Block 的兄弟 StepAction 中查找 OutgoingTransitionNetworkIds，
        /// 利用 ExecutionContext.GraphTransitionAstCache 求值 Transition。
        ///
        /// 查找策略（按优先级）：
        ///   1. 当前 trace 元数据中的 OutgoingTransitionNetworkIds（直接命中）
        ///   2. 同 Block 内所有输出同一变量的 GRAPH StepAction AST 行（变量可能出现在多个 Action 中）
        /// </summary>
        private async Task<List<StationAnchorFinding>> TryCompensateTransitionEvaluationAsync(
            long projectId,
            string stationCode,
            EvaluatePersistedAstResultDto evalResult,
            StallCandidateInfo candidate,
            List<PLC_LogicUnitAstEntity> astRows)
        {
            var findings = new List<StationAnchorFinding>();
            if (evalResult?.ExecutionContext == null)
                return findings;

            var variableName = candidate?.VariableName?.Trim();
            var blockId = evalResult.BlockId;

            // ── 策略1: 从当前 trace 元数据中提取 OutgoingTransitionNetworkIds ──
            var transitionIds = ExtractTransitionIdsFromMetadata(evalResult.RootEvaluation?.Metadata);

            // ── 策略2: 当前 trace 没有 → 搜索同 Block 内所有输出该变量的 GRAPH StepAction ──
            if (transitionIds.Count == 0 && !string.IsNullOrWhiteSpace(variableName) && blockId.HasValue)
            {
                var siblingAstRows = astRows
                    .Where(a => a.BlockId == blockId.Value
                        && !string.IsNullOrWhiteSpace(a.UnitType)
                        && IsGraphStepAction(a.UnitType)
                        && (a.LogicUnitId ?? 0) != (evalResult.LogicUnitId))
                    .ToList();

                foreach (var siblingRow in siblingAstRows)
                {
                    // 检查输出变量是否包含目标变量
                    var outputs = DeserializeStringListSafe(siblingRow.OutputVariablesJson);
                    if (!outputs.Any(o => string.Equals(o, variableName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // 从 RootNodeJson 中提取 OutgoingTransitionNetworkIds
                    var siblingTransIds = ExtractTransitionIdsFromRootNodeJson(siblingRow.RootNodeJson);
                    if (siblingTransIds.Count > 0)
                    {
                        // 确保这些 Transition 的 AST 也被加载到缓存中
                        await EnsureTransitionAstCachedAsync(
                            siblingTransIds, blockId.Value, projectId, evalResult.ExecutionContext);

                        foreach (var tid in siblingTransIds)
                            transitionIds.Add(tid);
                    }
                }
            }

            if (transitionIds.Count == 0)
                return findings;

            // ── 求值所有找到的 Transition ──
            var cache = evalResult.ExecutionContext.GraphTransitionAstCache;
            foreach (var tid in transitionIds.Distinct())
            {
                if (!cache.TryGetValue(tid, out var transNode))
                {
                    findings.Add(new StationAnchorFinding
                    {
                        VariableName = $"transition:{tid}",
                        DisplayName = $"[Transition·补偿] {tid}",
                        EvaluationStatus = "Unknown",
                        BlockName = candidate?.BlockName,
                        FailurePathSummary = $"Transition {tid} 尚未编译 AST，无法运行时求值",
                        TraceDepth = 0,
                        FaultOwnership = "InternalFault",
                        LastTrueAt = ResolveFindingLastTrueAt(candidate),
                    });
                    continue;
                }

                var tTrace = _runtimeEvaluationService.EvaluateNode(transNode, evalResult.ExecutionContext);

                var falseLeaves = new List<string>();
                CollectFalseLeafNames(tTrace, falseLeaves);
                var leafSummary = falseLeaves.Count > 0
                    ? string.Join(", ", falseLeaves.Take(5))
                    : null;

                findings.Add(new StationAnchorFinding
                {
                    VariableName = tTrace.VariableName ?? tTrace.DisplayName ?? $"transition:{tid}",
                    DisplayName = $"[Transition·补偿] {tTrace.DisplayName ?? tid.ToString()}",
                    EvaluationStatus = tTrace.Status,
                    EvaluatedValue = tTrace.Result,
                    BlockName = candidate?.BlockName,
                    FailurePathSummary = tTrace.Result == false
                        ? $"转移条件未满足{(leafSummary != null ? $"，卡在: {leafSummary}" : "")}"
                        : $"转移条件已满足({tTrace.Status})",
                    TraceDepth = 0,
                    FaultOwnership = "InternalFault",
                    LastTrueAt = ResolveFindingLastTrueAt(candidate),
                });
            }

            return findings;
        }

        /// <summary>从 trace 元数据字典中提取 OutgoingTransitionNetworkIds。</summary>
        private static List<int> ExtractTransitionIdsFromMetadata(Dictionary<string, string> metadata)
        {
            if (metadata == null) return new List<int>();
            if (!metadata.TryGetValue("OutgoingTransitionNetworkIds", out var idsJson)
                || string.IsNullOrWhiteSpace(idsJson))
                return new List<int>();
            try { return System.Text.Json.JsonSerializer.Deserialize<List<int>>(idsJson) ?? new List<int>(); }
            catch { return new List<int>(); }
        }

        /// <summary>从 RootNodeJson 字符串中提取 Metadata.OutgoingTransitionNetworkIds。</summary>
        private static List<int> ExtractTransitionIdsFromRootNodeJson(string rootNodeJson)
        {
            if (string.IsNullOrWhiteSpace(rootNodeJson)) return new List<int>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(rootNodeJson);
                if (doc.RootElement.TryGetProperty("Metadata", out var metaEl)
                    && metaEl.TryGetProperty("OutgoingTransitionNetworkIds", out var idsEl))
                {
                    var idsStr = idsEl.GetString();
                    if (!string.IsNullOrWhiteSpace(idsStr))
                        return System.Text.Json.JsonSerializer.Deserialize<List<int>>(idsStr) ?? new List<int>();
                }
            }
            catch { }
            return new List<int>();
        }

        /// <summary>
        /// 确保指定的 Transition NetworkIds 已被加载到 ExecutionContext.GraphTransitionAstCache 中。
        /// 对缓存中缺失的 ID，从数据库查询 TRANSITION 类型的 AST 行。
        /// </summary>
        private async Task EnsureTransitionAstCachedAsync(
            List<int> transitionIds, long blockId, long projectId,
            StopCauseExecutionContext context)
        {
            var missingIds = transitionIds.Where(id => !context.GraphTransitionAstCache.ContainsKey(id)).ToList();
            if (missingIds.Count == 0) return;

            var blockIdCopy = blockId;
            var projectIdCopy = projectId;
            var rows = await _logicUnitAstService.GetListAsync(it =>
                it.ProjectId == projectIdCopy
                && it.BlockId == blockIdCopy
                && it.IsDeleted == false
                && missingIds.Contains(it.NetworkId.Value));

            foreach (var row in rows)
            {
                if (!row.NetworkId.HasValue || string.IsNullOrWhiteSpace(row.RootNodeJson)) continue;
                if (string.IsNullOrWhiteSpace(row.UnitType)
                    || !row.UnitType.Contains("TRANSITION", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var node = System.Text.Json.JsonSerializer.Deserialize<StopCauseAstNode>(
                        row.RootNodeJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (node != null && !context.GraphTransitionAstCache.ContainsKey(row.NetworkId.Value))
                        context.GraphTransitionAstCache[row.NetworkId.Value] = node;
                }
                catch { }
            }
        }

        /// <summary>
        /// 递归收集 TRUE 求值的 StepAction trace 中所有 FALSE 的 GraphTransition 子树（不限深度），
        /// 用于后续 TraceFailureWriteSourcesAsync 下探。
        /// </summary>
        private static List<StopCauseAstTraceNode> GetFalseTransitionTraces(StopCauseAstTraceNode rootEvaluation)
        {
            var result = new List<StopCauseAstTraceNode>();
            if (rootEvaluation?.Children == null) return result;
            CollectFalseTransitionTracesRecursive(rootEvaluation, result);
            return result;
        }

        private static void CollectFalseTransitionTracesRecursive(StopCauseAstTraceNode node, List<StopCauseAstTraceNode> result)
        {
            if (node?.Children == null) return;
            foreach (var child in node.Children)
            {
                if (string.Equals(child.NodeType, StopCauseDiagnosis.Models.StopCauseAstNodeType.GraphTransition, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(child.Status, "False", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(child);
                }
                else
                {
                    CollectFalseTransitionTracesRecursive(child, result);
                }
            }
        }

        /// <summary>
        /// 递归收集求值树中所有 GraphTransition trace 节点（不限 Status、不限深度），
        /// 用于对 Transition 内部的 FALSE 叶子信号做深度追溯。
        /// 即使 Transition 整体为 TRUE，其内部子条件可能有 FALSE 信号需要追因。
        /// </summary>
        private static List<StopCauseAstTraceNode> GetAllTransitionTraces(StopCauseAstTraceNode rootEvaluation)
        {
            var result = new List<StopCauseAstTraceNode>();
            if (rootEvaluation?.Children == null) return result;
            CollectAllTransitionTracesRecursive(rootEvaluation, result);
            return result;
        }

        private static void CollectAllTransitionTracesRecursive(StopCauseAstTraceNode node, List<StopCauseAstTraceNode> result)
        {
            if (node?.Children == null) return;
            foreach (var child in node.Children)
            {
                if (string.Equals(child.NodeType, StopCauseDiagnosis.Models.StopCauseAstNodeType.GraphTransition, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(child);
                    // 不再深入 Transition 内部搜索（Transition 内部是条件子树，不是嵌套 Transition）
                }
                else
                {
                    CollectAllTransitionTracesRecursive(child, result);
                }
            }
        }

        /// <summary>
        /// 收集 trace 子树中 FALSE 叶子节点的变量名（用于摘要展示）。
        /// </summary>
        private static void CollectFalseLeafNames(StopCauseAstTraceNode node, List<string> result)
        {
            if (node == null) return;
            var isFalse = string.Equals(node.Status, "False", StringComparison.OrdinalIgnoreCase);
            var hasChildren = node.Children != null && node.Children.Count > 0;

            if (isFalse && !hasChildren && !string.IsNullOrWhiteSpace(node.VariableName))
            {
                result.Add(node.VariableName);
                return;
            }

            if (hasChildren)
            {
                foreach (var child in node.Children)
                    CollectFalseLeafNames(child, result);
            }
        }

        /// <summary>
        /// 收集 Transition 内所有叶子信号的名称 + 当前值摘要（不区分 True/False）。
        /// 格式: "变量名=True" 或 "变量名=False" 或 "变量名=Unknown"。
        /// </summary>
        private static void CollectLeafSignalDetails(StopCauseAstTraceNode node, List<string> result)
        {
            if (node == null) return;
            var hasChildren = node.Children != null && node.Children.Count > 0;

            // 跳过 GraphTransition 根节点本身，只收集其子级
            if (!string.Equals(node.NodeType, StopCauseDiagnosis.Models.StopCauseAstNodeType.GraphTransition, StringComparison.OrdinalIgnoreCase)
                && !hasChildren && !string.IsNullOrWhiteSpace(node.VariableName))
            {
                result.Add($"{node.VariableName}={node.Status ?? "Unknown"}");
                return;
            }

            if (hasChildren)
            {
                foreach (var child in node.Children)
                    CollectLeafSignalDetails(child, result);
            }
        }

        /// <summary>
        /// 跨 LogicUnit 失败路径下探：从完整求值树中提取所有 False 叶子节点的变量，
        /// 反查哪些 LogicUnit WRITE 了该变量，加载其 AST 继续递归求值。
        /// 对于无写入源的叶子变量（如 I 点、HMI 变量），直接作为追踪终点输出。
        /// </summary>
        private async Task TraceFailureWriteSourcesAsync(
            long projectId, string stationCode,
            List<PLC_LogicUnitAstEntity> astRows,
            StopCauseAstTraceNode rootEvaluation,
            StationDiagnosisSummaryDto summary,
            int maxAnchors,
            int depth,
            HashSet<string> visitedVariables = null,
            string diagnosisPhase = null,
            StopCauseExecutionContext sharedContext = null)
        {
            // 只在最外层计时：递归调用的耗时已包含在外层里
            using var measure = depth == 0 ? StationSubTimings.Measure("TraceSum") : null;
            await TraceFailureWriteSourcesCoreAsync(projectId, stationCode, astRows, rootEvaluation, summary,
                maxAnchors, depth, visitedVariables, diagnosisPhase, sharedContext);
        }

        private async Task TraceFailureWriteSourcesCoreAsync(
            long projectId, string stationCode,
            List<PLC_LogicUnitAstEntity> astRows,
            StopCauseAstTraceNode rootEvaluation,
            StationDiagnosisSummaryDto summary,
            int maxAnchors,
            int depth,
            HashSet<string> visitedVariables = null,
            string diagnosisPhase = null,
            StopCauseExecutionContext sharedContext = null)
        {
            const int MaxTraceDepth = 10;
            if (depth > MaxTraceDepth || rootEvaluation == null)
                return;

            if (summary.KeyFindings.Count >= maxAnchors * 2)
                return;

            visitedVariables ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 从完整求值树中递归收集所有 False 状态的叶子变量
            // GraphTransition 内部是转移条件子树，Not/Reset 节点内的 False 叶子同样是真实故障，
            // 不能被 CollectFalseLeaves 的守卫逻辑提前截断。
            var falseLeaves = new List<StopCauseAstTraceNode>();
            if (string.Equals(rootEvaluation.NodeType, StopCauseAstNodeType.GraphTransition, StringComparison.OrdinalIgnoreCase))
                CollectFalseLeavesUnguarded(rootEvaluation, falseLeaves);
            else
                CollectFalseLeaves(rootEvaluation, falseLeaves);

            // 按变量名去重，并过滤 STRING 类型叶子（STRING 值不参与根因追踪）
            falseLeaves = falseLeaves
                .Where(n => !string.IsNullOrWhiteSpace(n.VariableName))
                .Where(n => !IsNonNumericString(n.ActualValue?.ToString()))
                .GroupBy(n => n.VariableName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            await WriteLogHelper.WriteLogsAsync(
                $"[DeepTrace] depth={depth} rootType={rootEvaluation.NodeType} rootVar={rootEvaluation.VariableName} " +
                $"rootStatus={rootEvaluation.Status} rootChildren={rootEvaluation.Children?.Count ?? -1} " +
                $"falseLeaves={falseLeaves.Count}: [{string.Join(", ", falseLeaves.Take(10).Select(l => $"{l.VariableName}={l.ActualValue}({l.Status})"))}] " +
                $"visitedBefore=[{string.Join(",", visitedVariables.Take(20))}]",
                "LineDiagnosis/DeepTrace");

            // ── Phase A: 批量收集所有叶子的写入源，区分终端变量和有写入源的变量 ──
            // 结构: variableName → (leaf, writeLogicUnitIds, writerAstMap)
            var terminalLeaves = new List<(StopCauseAstTraceNode Leaf, string Variable)>();
            var writerGroups = new List<(StopCauseAstTraceNode Leaf, string Variable, List<long> WriteUnitIds)>();

            foreach (var leaf in falseLeaves)
            {
                if (summary.KeyFindings.Count >= maxAnchors * 2) break;

                var traceVariable = leaf.VariableName?.Trim();
                if (string.IsNullOrWhiteSpace(traceVariable)) continue;
                if (visitedVariables.Contains(traceVariable)) continue;
                visitedVariables.Add(traceVariable);

                // 查 variable_access 表：哪些 LogicUnit WRITE 了这个变量
                var writeAccesses = await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == projectId
                    && it.VariableName == traceVariable
                    && (it.AccessType == "WRITE" || it.AccessType == "READWRITE")
                    && it.LogicUnitId.HasValue);

                await WriteLogHelper.WriteLogsAsync(
                    $"[DeepTrace] depth={depth} PhaseA 查变量 '{traceVariable}' 写入源: writeAccesses={writeAccesses?.Count ?? -1}",
                    "LineDiagnosis/DeepTrace");

                if (writeAccesses == null || writeAccesses.Count == 0)
                {
                    terminalLeaves.Add((leaf, traceVariable));
                }
                else
                {
                    var writeLogicUnitIds = writeAccesses
                        .Select(it => it.LogicUnitId.Value)
                        .Distinct()
                        .ToList();
                    writerGroups.Add((leaf, traceVariable, writeLogicUnitIds));
                    await WriteLogHelper.WriteLogsAsync(
                        $"[DeepTrace] depth={depth} 变量 {traceVariable} 有 {writeLogicUnitIds.Count} 个写入源: [{string.Join(",", writeLogicUnitIds.Take(5))}]",
                        "LineDiagnosis/DeepTrace");
                }
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[DeepTrace] depth={depth} PhaseA汇总: 终端变量={terminalLeaves.Count} 有写入源变量={writerGroups.Count} 已跳过(visited)={falseLeaves.Count - terminalLeaves.Count - writerGroups.Count}",
                "LineDiagnosis/DeepTrace");

            // ── Phase B: 处理终端变量（无写入源 = I点/HMI/外部信号等） ──
            foreach (var (leaf, traceVariable) in terminalLeaves)
            {
                if (summary.KeyFindings.Count >= maxAnchors * 2) break;
                // 2026-10-14 跨 PLC：本工程找不到写入源的，可能是对端 PLC 工程里的通信块（如 DB182_PLC1<->PLC2），
                // 到块所在工程找写入它的工位——找到了就记为「在等那个工位」，不再当成本工位的根因终点
                var (isCrossPlc, crossPlcWriterStation) = await ResolveCrossPlcWriterAsync(projectId, traceVariable);
                await WriteLogHelper.WriteLogsAsync(
                    $"[DeepTrace] depth={depth} PhaseB 终端信号: {traceVariable}={leaf.ActualValue} (无写入源) → " +
                    (isCrossPlc ? $"跨PLC信号，对端写入工位={crossPlcWriterStation ?? "未知"}" : "输出为根因终点"),
                    "LineDiagnosis/DeepTrace");
                await EmitTerminalFinding(projectId, stationCode, leaf, traceVariable, depth, diagnosisPhase, summary,
                    isCrossPlc, crossPlcWriterStation);
            }

            // ── Phase C: 预加载所有写入源 AST（批量 DB 查询） ──
            var allWriteUnitIds = writerGroups
                .SelectMany(g => g.WriteUnitIds)
                .Distinct()
                .ToList();

            // 先从已有 astRows 查，缺失的批量查 DB
            var writerAstMap = new Dictionary<long, PLC_LogicUnitAstEntity>();
            var missingUnitIds = new List<long>();
            foreach (var unitId in allWriteUnitIds)
            {
                var existing = astRows.FirstOrDefault(a => a.LogicUnitId == unitId);
                if (existing != null)
                    writerAstMap[unitId] = existing;
                else
                    missingUnitIds.Add(unitId);
            }

            if (missingUnitIds.Count > 0)
            {
                var missingAstRows = await _logicUnitAstService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == projectId
                    && missingUnitIds.Contains(it.LogicUnitId.Value)
                    && (it.CompileStatus == "Compiled" || it.CompileStatus == "PartiallyCompiled"));

                foreach (var row in missingAstRows)
                {
                    if (row.LogicUnitId.HasValue && !writerAstMap.ContainsKey(row.LogicUnitId.Value))
                        writerAstMap[row.LogicUnitId.Value] = row;
                }
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[DeepTrace] depth={depth} PhaseC: allWriteUnitIds={allWriteUnitIds.Count} writerAstMap={writerAstMap.Count} missingIds=[{string.Join(",", missingUnitIds.Take(10))}]",
                "LineDiagnosis/DeepTrace");

            // ── Phase D: 批量求值 —— 所有写入源共享一次 PLC 快照 ──
            var batchTargets = new List<(long WriteUnitId, string TraceVariable, StopCauseAstTraceNode Leaf)>();
            foreach (var (leaf, traceVariable, writeUnitIds) in writerGroups)
            {
                foreach (var writeLogicUnitId in writeUnitIds)
                {
                    if (summary.KeyFindings.Count + batchTargets.Count >= maxAnchors * 2) break;

                    if (!writerAstMap.ContainsKey(writeLogicUnitId))
                    {
                        summary.KeyFindings.Add(new StationAnchorFinding
                        {
                            VariableName = traceVariable,
                            DisplayName = $"[AST缺失·L{depth}] {traceVariable}",
                            EvaluationStatus = "Unavailable",
                            EvaluatedValue = null,
                            BlockName = null,
                            FailurePathSummary = $"⚠ 写入源 LogicUnit({writeLogicUnitId}) AST 未编译，追踪中断于 {traceVariable}",
                            TraceDepth = depth,
                            FaultOwnership = "Unknown",
                            DiagnosisPhase = diagnosisPhase,
                        });
                        continue;
                    }

                    batchTargets.Add((writeLogicUnitId, traceVariable, leaf));
                }
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[DeepTrace] depth={depth} PhaseD: batchTargets={batchTargets.Count} maxAnchors={maxAnchors} currentFindings={summary.KeyFindings.Count}",
                "LineDiagnosis/DeepTrace");

            // 批量求值：复用共享上下文跳过 PLC 读取（深度追踪优化）
            var batchEvalResults = new Dictionary<long, EvaluatePersistedAstResultDto>();
            if (batchTargets.Count > 0)
            {
                var distinctUnitIds = batchTargets.Select(t => t.WriteUnitId).Distinct().ToList();
                try
                {
                    var batchResult = await _runtimeEvaluationService.EvaluateBatchAsync(
                        new StopCauseAstRuntimeEvaluationService.EvaluateBatchRequest
                        {
                            ProjectId = projectId,
                            StationCode = stationCode,
                            Targets = distinctUnitIds.Select(uid => new StopCauseAstRuntimeEvaluationService.EvaluateBatchTarget
                            {
                                LogicUnitId = uid,
                                VariableName = batchTargets.First(t => t.WriteUnitId == uid).TraceVariable,
                            }).ToList(),
                            PreloadedAstRows = writerAstMap.Values.ToList(),
                        },
                        sharedContext);

                    foreach (var item in batchResult)
                    {
                        if (item.Success && item.EvalResult != null)
                            batchEvalResults[item.LogicUnitId] = item.EvalResult;
                    }
                }
                catch
                {
                    // 批量求值整体失败时，降级为逐个求值（保持原有行为）
                    foreach (var (writeUnitId, traceVariable, _) in batchTargets)
                    {
                        try
                        {
                            var evalResult = await _runtimeEvaluationService.EvaluateAsync(
                                new EvaluatePersistedAstRequestDto
                                {
                                    ProjectId = projectId,
                                    StationCode = stationCode,
                                    LogicUnitId = writeUnitId,
                                    VariableName = traceVariable,
                                });
                            batchEvalResults[writeUnitId] = evalResult;
                        }
                        catch { /* 单个失败跳过 */ }
                    }
                }
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[DeepTrace] depth={depth} PhaseD求值完成: batchEvalResults={batchEvalResults.Count} keys=[{string.Join(",", batchEvalResults.Keys.Take(10))}]",
                "LineDiagnosis/DeepTrace");

            // ── Phase E: 处理求值结果 + 递归下探 ──
            // 收集本层所有需要递归的 (rootEvaluation, writerAst)，统一递归
            var recursionQueue = new List<(StopCauseAstTraceNode EvalRoot, PLC_LogicUnitAstEntity WriterAst)>();

            foreach (var (writeUnitId, traceVariable, leaf) in batchTargets)
            {
                if (summary.KeyFindings.Count >= maxAnchors * 2) break;

                if (!batchEvalResults.TryGetValue(writeUnitId, out var evalResult))
                {
                    summary.KeyFindings.Add(new StationAnchorFinding
                    {
                        VariableName = traceVariable,
                        DisplayName = $"[求值异常·L{depth}] {traceVariable}",
                        EvaluationStatus = "Error",
                        EvaluatedValue = null,
                        BlockName = null,
                        FailurePathSummary = $"⚠ 写入源 LogicUnit({writeUnitId}) 批量求值未返回结果",
                        TraceDepth = depth,
                        FaultOwnership = "Unknown",
                        DiagnosisPhase = diagnosisPhase,
                    });
                    continue;
                }

                await WriteLogHelper.WriteLogsAsync(
                    $"[DeepTrace] depth={depth} PhaseE: writeUnitId={writeUnitId} var={traceVariable} EvalStatus={evalResult.EvaluationStatus} EvalValue={evalResult.EvaluatedValue} DiagQuality={evalResult.DiagnosisQuality}",
                    "LineDiagnosis/DeepTrace");

                // 统一获取写入源 AST 信息 & IOTag
                var writerAst = writerAstMap.GetValueOrDefault(writeUnitId);
                var writerStation = writerAst?.StationCode?.Trim();
                string ownership;
                if (string.IsNullOrEmpty(writerStation) || string.Equals(writerStation, stationCode, StringComparison.OrdinalIgnoreCase))
                    ownership = "InternalFault";
                else
                    ownership = $"WaitingFor:{writerStation}";

                var (ioComment, semLabel, _, ioAddr) = await LookupIoTagInfoAsync(projectId, stationCode, traceVariable);
                var tagSuffix = BuildIoTagDisplaySuffix(semLabel, ioComment);

                if (evalResult.EvaluatedValue == false)
                {
                    // ── 先缩窄，再构建 FailurePath（避免使用完整 lad-root 中首个无关 false 分支） ──
                    StopCauseAstTraceNode narrowed = null;
                    string failurePathSummary;

                    if (evalResult.RootEvaluation != null)
                    {
                        narrowed = NarrowEvalToTargetVariable(evalResult.RootEvaluation, traceVariable);

                        // ── 二次缩窄：Anchor 节点命中时，进一步搜索 ExternalDependency 叶子 ──
                        // 典型场景：FB Call 网络的 Anchor(VariableName=FwdPos) 包含
                        //   And → [ExternalDep(QBkd→OP030_YV01_H), ExternalDep(FwdPos→DB345...FwdPos), ...]
                        // NarrowEvalToTargetVariable 因 Anchor.VariableName 匹配直接返回根节点，
                        // 导致 ReferenceEquals=true → 走 BuildFailurePathSummary → 选中 QBkd（And块首个False）
                        // 此二次缩窄从子树中精确找到 VariableName==traceVariable 的 ExternalDependency
                        if (narrowed != null
                            && !string.Equals(narrowed.NodeType, "ExternalDependency", StringComparison.OrdinalIgnoreCase))
                        {
                            var extDepMatch = FindExternalDependencyByVariable(narrowed, traceVariable);
                            if (extDepMatch != null)
                                narrowed = extDepMatch;
                        }

                        // 缩窄成功到 ExternalDependency → 构建针对目标变量的失败路径
                        if (narrowed != null
                            && !ReferenceEquals(narrowed, evalResult.RootEvaluation)
                            && string.Equals(narrowed.NodeType, "ExternalDependency", StringComparison.OrdinalIgnoreCase)
                            && narrowed.Metadata != null)
                        {
                            var callee = narrowed.Metadata.GetValueOrDefault("Callee") ?? "";
                            var outputParam = narrowed.Metadata.GetValueOrDefault("OutputParam") ?? "";
                            failurePathSummary = $"{writerAst?.BlockName ?? evalResult.BlockName}=False → {callee}.{outputParam} → {traceVariable}={narrowed.Status}";
                        }
                        else
                        {
                            failurePathSummary = BuildFailurePathSummary(evalResult.FailurePath);
                        }
                    }
                    else
                    {
                        failurePathSummary = BuildFailurePathSummary(evalResult.FailurePath);
                    }

                    summary.KeyFindings.Add(new StationAnchorFinding
                    {
                        VariableName = traceVariable,
                        DisplayName = $"[写入源L{depth}] {traceVariable}{tagSuffix}",
                        EvaluationStatus = evalResult.EvaluationStatus,
                        EvaluatedValue = evalResult.EvaluatedValue,
                        DiagnosisQuality = evalResult.DiagnosisQuality,
                        BlockName = writerAst?.BlockName ?? evalResult.BlockName,
                        FailurePathSummary = failurePathSummary,
                        TraceDepth = depth,
                        FaultOwnership = ownership,
                        DiagnosisPhase = diagnosisPhase,
                        IoTagAddress = ioAddr,
                        IoTagComment = ioComment,
                    });

                    if (narrowed != null)
                    {
                        // ExternalDependency 节点优先穿透处理：
                        // 运行时求值树中 ExternalDependency 节点可能含 InputParam 子节点，
                        // 若先判断 Children>0 会错误进入 recursionQueue 而跳过 FB 穿透。
                        // 因此 ExternalDependency 检查必须在子节点检查之前。
                        if (string.Equals(narrowed.NodeType, "ExternalDependency", StringComparison.OrdinalIgnoreCase)
                            && narrowed.Metadata != null
                            && narrowed.Metadata.ContainsKey("Callee"))
                        {
                            // ── FB 穿透追踪 ──
                            // ExternalDependency 节点来自 Call (FB/FC)，需穿透到 FB 内部
                            // 查找写入该输出参数的网络，继续追踪到物理 I 点（L2/L3...）
                            await PenetrateFbCallOutputAsync(
                                projectId, stationCode, astRows,
                                narrowed, traceVariable, summary,
                                maxAnchors, depth + 1, visitedVariables,
                                diagnosisPhase, sharedContext);
                        }
                        else if ((narrowed.Children?.Count ?? 0) > 0)
                        {
                            // 对非 FB-Call 网络：缩窄递归范围，只递归与当前追踪变量匹配的子树
                            // 避免把所有无关 false 输出都当作根因追踪
                            recursionQueue.Add((narrowed, writerAst));
                        }
                    }
                }
                else if (evalResult.EvaluatedValue == null
                         || string.Equals(evalResult.EvaluationStatus, "Unsupported", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(evalResult.EvaluationStatus, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    // 写入源 AST 无法求值（Unsupported / Error / null）——输出中断 Finding
                    summary.KeyFindings.Add(new StationAnchorFinding
                    {
                        VariableName = traceVariable,
                        DisplayName = $"[写入源L{depth}·{evalResult.EvaluationStatus}] {traceVariable}{tagSuffix}",
                        EvaluationStatus = evalResult.EvaluationStatus ?? "Unsupported",
                        EvaluatedValue = null,
                        DiagnosisQuality = "Undetermined",
                        BlockName = writerAst?.BlockName ?? evalResult.BlockName,
                        FailurePathSummary = $"⚠ 写入源 LogicUnit({writeUnitId}) 求值状态={evalResult.EvaluationStatus}，追踪中断于 {traceVariable}（信号实际值=False）",
                        TraceDepth = depth,
                        FaultOwnership = ownership,
                        DiagnosisPhase = diagnosisPhase,
                    });

                    await WriteLogHelper.WriteLogsAsync(
                        $"[DeepTrace] depth={depth} PhaseE: writeUnitId={writeUnitId} var={traceVariable} EvalStatus={evalResult.EvaluationStatus} → 输出中断Finding",
                        "LineDiagnosis/DeepTrace");
                }
                else
                {
                    // EvaluatedValue == true: 写入命令已发但信号反馈仍为 false → 命令/反馈不一致
                    await WriteLogHelper.WriteLogsAsync(
                        $"[DeepTrace] depth={depth} PhaseE: writeUnitId={writeUnitId} var={traceVariable} EvalValue={evalResult.EvaluatedValue} ≠ false → 跳过(不追踪)",
                        "LineDiagnosis/DeepTrace");
                }
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[DeepTrace] depth={depth} PhaseE完成: 递归队列={recursionQueue.Count} 当前Findings={summary.KeyFindings.Count}",
                "LineDiagnosis/DeepTrace");

            // ── Phase F: 递归下探（本层结果已全部输出，再进入下一层） ──
            foreach (var (evalRoot, writerAstInfo) in recursionQueue)
            {
                if (summary.KeyFindings.Count >= maxAnchors * 2) break;

                System.Diagnostics.Debug.WriteLine(
                    $"[DeepTrace] ▶ 进入 depth={depth + 1} 递归下探 rootVar={evalRoot?.VariableName}");

                await TraceFailureWriteSourcesAsync(
                    projectId, stationCode, astRows,
                    evalRoot, summary,
                    maxAnchors, depth + 1, visitedVariables,
                    diagnosisPhase, sharedContext);
            }
        }

        /// <summary>
        /// 终端变量处理（无写入源）：分类、IOTag 反查、语义标签、输出 Finding。
        /// </summary>
        /// <summary>
        /// 跨 PLC 终端信号：变量所在块不在本工程、只在另一个工程里时，返回 (true, 对端写入工位)；
        /// 对端工程里找不到写入源或写入工位说不清时工位为 null。规则见 CrossPlcRules。
        /// </summary>
        private async Task<(bool IsCrossPlc, string WriterStation)> ResolveCrossPlcWriterAsync(long projectId, string traceVariable)
        {
            try
            {
                var blockName = CrossPlcRules.BlockNameOf(traceVariable);
                if (blockName == null)
                    return (false, null);

                var blockProjects = (await _blockService.GetListAsync(block =>
                        block.IsDeleted == false
                        && block.ProjectId != null
                        && block.BlockName == blockName))
                    .Select(block => block.ProjectId!.Value)
                    .Distinct()
                    .ToList();
                var owners = new Dictionary<string, IReadOnlyCollection<long>>(StringComparer.OrdinalIgnoreCase)
                {
                    [blockName] = blockProjects
                };
                var ownerProjectId = CrossPlcRules.ResolveOwnerProject(traceVariable, projectId, owners);
                if (ownerProjectId == null)
                    return (false, null);

                var writeAccesses = await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == ownerProjectId.Value
                    && it.VariableName == traceVariable
                    && (it.AccessType == "WRITE" || it.AccessType == "READWRITE")
                    && it.LogicUnitId.HasValue);
                var writerUnitIds = writeAccesses
                    .Select(it => it.LogicUnitId!.Value)
                    .Distinct()
                    .ToList();
                if (writerUnitIds.Count == 0)
                    return (true, null);

                var writerAstRows = await _logicUnitAstService.GetListAsync(it =>
                    it.IsDeleted == false
                    && it.ProjectId == ownerProjectId.Value
                    && it.LogicUnitId.HasValue
                    && writerUnitIds.Contains(it.LogicUnitId.Value));
                return (true, CrossPlcRules.PickWriterStation(writerAstRows.Select(row => row.StationCode)));
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[DeepTrace] 跨PLC写入工位查找失败（按普通终端信号处理）: {traceVariable} {ex.Message}",
                    "LineDiagnosis/DeepTrace");
                return (false, null);
            }
        }

        private async Task EmitTerminalFinding(
            long projectId, string stationCode,
            StopCauseAstTraceNode leaf, string traceVariable,
            int depth, string diagnosisPhase,
            StationDiagnosisSummaryDto summary,
            bool isCrossPlc = false,
            string crossPlcWriterStation = null)
        {
            var (ioTagComment, semanticLabel, terminalCategory, ioTagAddress) =
                await LookupIoTagInfoAsync(projectId, stationCode, traceVariable);

            var displaySuffix = BuildIoTagDisplaySuffix(semanticLabel, ioTagComment);
            if (isCrossPlc)
            {
                terminalCategory = "跨PLC信号";
            }
            var failurePathSummary = isCrossPlc
                ? $"🔗 跨 PLC 信号：{traceVariable}{displaySuffix}，当前值={leaf.ActualValue}，" +
                  (string.IsNullOrWhiteSpace(crossPlcWriterStation)
                      ? "由对端 PLC 写入（未找到写入工位）"
                      : $"由对端 PLC 的 {crossPlcWriterStation} 写入，本工位在等它")
                : $"🔍 根因终点（{terminalCategory}）：{traceVariable}{displaySuffix}，当前值={leaf.ActualValue}";

            summary.KeyFindings.Add(new StationAnchorFinding
            {
                VariableName = traceVariable,
                DisplayName = $"[{terminalCategory}·L{depth}] {traceVariable}{displaySuffix}",
                EvaluationStatus = leaf.Status,
                EvaluatedValue = leaf.Result,
                BlockName = null,
                FailurePathSummary = failurePathSummary,
                TraceDepth = depth,
                FaultOwnership = CrossPlcRules.TerminalOwnership(isCrossPlc, crossPlcWriterStation),
                DiagnosisPhase = diagnosisPhase,
                IoTagAddress = ioTagAddress,
                IoTagComment = ioTagComment,
            });
        }

        /// <summary>
        /// 查找变量对应的 IOTag 注释和语义标签。
        /// 返回: (ioTagComment, semanticLabel, terminalCategory)
        /// </summary>
        private async Task<(string IoTagComment, string SemanticLabel, string TerminalCategory, string IoTagAddress)> LookupIoTagInfoAsync(
            long projectId, string stationCode, string variableName)
        {
            string ioTagComment = null;
            string semanticLabel = null;
            string ioTagAddress = null;
            var terminalCategory = ClassifyTerminalVariable(variableName);

            if (terminalCategory == "DB变量" || terminalCategory == "其他信号")
            {
                try
                {
                    var ioTag = await _ioTagService.GetDb().AsQueryable()
                        .Where(t => t.IsDeleted == false && t.ProjectId == projectId
                            && (t.TagName == variableName || t.Address == variableName))
                        .Select(t => new { t.Address, t.Comment, t.TagCategory, t.TagName })
                        .FirstAsync();

                    if (ioTag != null && !string.IsNullOrWhiteSpace(ioTag.Address))
                    {
                        var addr = ioTag.Address.Trim();
                        ioTagAddress = addr;
                        if (Regex.IsMatch(addr, @"^%?[IE]\d|^%?[IE][BWD]\d", RegexOptions.IgnoreCase))
                            terminalCategory = "物理输入(I点)";
                        else if (Regex.IsMatch(addr, @"^%?[QA]\d|^%?[QA][BWD]\d", RegexOptions.IgnoreCase))
                            terminalCategory = "物理输出(Q点)";

                        if (!string.IsNullOrWhiteSpace(ioTag.Comment))
                            ioTagComment = ioTag.Comment.Trim();
                    }
                }
                catch { }
            }

            try
            {
                var sigEntity = await _stationShadowSignalService.GetDb().AsQueryable()
                    .Where(s => s.IsDeleted == false && s.ProjectId == projectId
                        && s.StationCode == stationCode && s.VariableName == variableName)
                    .Select(s => new { s.SemanticLabel, s.SignalKey })
                    .FirstAsync();
                if (sigEntity != null && !string.IsNullOrWhiteSpace(sigEntity.SemanticLabel))
                    semanticLabel = sigEntity.SemanticLabel;
            }
            catch { }

            return (ioTagComment, semanticLabel, terminalCategory, ioTagAddress);
        }

        /// <summary>
        /// 根据 IOTag/SemanticLabel 构建显示后缀，如 " (到位信号)"。
        /// </summary>
        private static string BuildIoTagDisplaySuffix(string semanticLabel, string ioTagComment)
        {
            if (!string.IsNullOrWhiteSpace(semanticLabel))
                return $" ({semanticLabel})";
            if (!string.IsNullOrWhiteSpace(ioTagComment))
                return $" ({ioTagComment})";
            return "";
        }

        /// <summary>
        /// FB/FC 穿透追踪：当深追踪到达一个 ExternalDependency (Call 输出) 且值为 false 时，
        /// 穿透到被调用 FB 内部，查找写入该输出参数的网络，求值并报告终端 I 点根因。
        /// 同时将 Call 输入形参（如 #IHP）映射到调用处外部实参（如 %I1273.2）的信号值，
        /// 使 FB 内部 AST 正确求值，最终定位到物理 I 点。
        /// </summary>
        private async Task PenetrateFbCallOutputAsync(
            long projectId, string stationCode,
            List<PLC_LogicUnitAstEntity> astRows,
            StopCauseAstTraceNode callOutputNode,
            string targetVariable,
            StationDiagnosisSummaryDto summary,
            int maxAnchors, int depth,
            HashSet<string> visitedVariables,
            string diagnosisPhase,
            StopCauseExecutionContext sharedContext)
        {
            if (depth > (sharedContext?.MaxFbPenetrationDepth ?? 4)) return; // FB 嵌套防止无限递归

            var callee = callOutputNode.Metadata?.GetValueOrDefault("Callee");
            var outputParam = callOutputNode.Metadata?.GetValueOrDefault("OutputParam");
            if (string.IsNullOrWhiteSpace(callee) || string.IsNullOrWhiteSpace(outputParam))
                return;

            // ── Step 0: 从 Metadata 提取 FB 输入参数映射（编译时存入的 InputParam: 前缀） ──
            var inputParamMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (callOutputNode.Metadata != null)
            {
                foreach (var kv in callOutputNode.Metadata)
                {
                    if (kv.Key.StartsWith("InputParam:", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        var formalName = kv.Key.Substring("InputParam:".Length);
                        inputParamMap[formalName] = kv.Value;
                    }
                }
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[DeepTrace] depth={depth} FB穿透: callee={callee} outputParam={outputParam} targetVar={targetVariable} inputParams={inputParamMap.Count}:[{string.Join(",", inputParamMap.Select(kv => $"{kv.Key}→{kv.Value}").Take(8))}]",
                "LineDiagnosis/DeepTrace");

            try
            {
                // Step 1: 查找被调用 FB 的块
                var calleeBlocks = await _blockService.GetListAsync(b =>
                    b.ProjectId == projectId &&
                    b.BlockName == callee &&
                    !b.IsDeleted);
                var calleeBlock = calleeBlocks?.FirstOrDefault();
                if (calleeBlock == null)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[DeepTrace] depth={depth} FB穿透: 未找到块 {callee}，穿透中断",
                        "LineDiagnosis/DeepTrace");
                    return;
                }

                // ── 构建 FB 输入形参 → 外部实参映射（供求值器注入信号值） ──
                var fbInputParamMap = inputParamMap.Count > 0 ? inputParamMap : null;

                // ── FB 内部递归追踪 ──
                // 从输出参数开始，沿局部变量依赖链向上追踪，直到到达输入参数或全局变量。
                // 典型链路: FwdPos → CylinderCmd.FwdPos(Static) → IWP(Input) → 外部传感器
                var fbInternalVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var traceQueue = new Queue<string>();
                traceQueue.Enqueue(outputParam);
                const int maxFbInternalHops = 5;
                var hopCount = 0;

                while (traceQueue.Count > 0 && hopCount < maxFbInternalHops)
                {
                    if (summary.KeyFindings.Count >= maxAnchors * 2) break;

                    var currentParam = traceQueue.Dequeue();
                    if (!fbInternalVisited.Add(currentParam)) continue;
                    hopCount++;

                    // 查找 FB 内写入该变量的 variable_access
                    var formalParam = currentParam;
                    var formalParamHash = formalParam.StartsWith("#") ? formalParam : "#" + formalParam;

                    var calleeWriteAccesses = await _variableAccessService.GetListAsync(va =>
                        va.BlockId == calleeBlock.Id &&
                        (va.VariableName == formalParam || va.VariableName == formalParamHash) &&
                        (va.AccessType == "WRITE" || va.AccessType == "READWRITE") &&
                        !va.IsDeleted);

                    // 形参名 "FwdPos" 在 FB 内部通常以 "CylinderCmd.FwdPos" 格式存储（TIA 编译器加上实例前缀）。
                    // 精确匹配失败时，加载该块全部写入记录，按变量名尾段做二次匹配。
                    if (!calleeWriteAccesses.Any())
                    {
                        var tailParam = formalParam.TrimStart('#');
                        if (!string.IsNullOrEmpty(tailParam))
                        {
                            var allBlockWrites = await _variableAccessService.GetListAsync(va =>
                                va.BlockId == calleeBlock.Id &&
                                (va.AccessType == "WRITE" || va.AccessType == "READWRITE") &&
                                !va.IsDeleted);
                            calleeWriteAccesses = allBlockWrites
                                .Where(va =>
                                {
                                    var vn = va.VariableName?.Trim();
                                    if (string.IsNullOrWhiteSpace(vn)) return false;
                                    var vnTail = vn.Contains('.')
                                        ? vn.Substring(vn.LastIndexOf('.') + 1)
                                        : vn.TrimStart('#');
                                    return string.Equals(vnTail, tailParam, StringComparison.OrdinalIgnoreCase);
                                })
                                .ToList();
                        }
                    }

                    if (!calleeWriteAccesses.Any())
                    {
                        await WriteLogHelper.WriteLogsAsync(
                            $"[DeepTrace] depth={depth} FB穿透hop{hopCount}: {callee} 内未找到写入 {formalParam}/{formalParamHash} 的变量访问记录（含尾段匹配）",
                            "LineDiagnosis/DeepTrace");
                        continue;
                    }

                    var calleeLogicUnitIds = calleeWriteAccesses
                        .Where(va => va.LogicUnitId.HasValue)
                        .Select(va => va.LogicUnitId.Value)
                        .Distinct()
                        .ToList();

                    await WriteLogHelper.WriteLogsAsync(
                        $"[DeepTrace] depth={depth} FB穿透hop{hopCount}: {callee} 内找到 {calleeLogicUnitIds.Count} 个写入 {currentParam} 的LogicUnit: [{string.Join(",", calleeLogicUnitIds.Take(5))}]",
                        "LineDiagnosis/DeepTrace");

                    // 加载 FB 内部的 AST
                    var calleeAstRows = await _logicUnitAstService.GetListAsync(it =>
                        it.IsDeleted == false
                        && it.ProjectId == projectId
                        && calleeLogicUnitIds.Contains(it.LogicUnitId.Value)
                        && (it.CompileStatus == "Compiled" || it.CompileStatus == "PartiallyCompiled"));

                    if (!calleeAstRows.Any())
                    {
                        await WriteLogHelper.WriteLogsAsync(
                            $"[DeepTrace] depth={depth} FB穿透hop{hopCount}: {callee} 内部 {currentParam} 的 AST 未编译",
                            "LineDiagnosis/DeepTrace");
                        continue;
                    }

                    // 求值 FB 内部 AST
                    foreach (var fbAst in calleeAstRows)
                    {
                        if (summary.KeyFindings.Count >= maxAnchors * 2) break;

                        try
                        {
                            var fbEvalResult = await _runtimeEvaluationService.EvaluateAsync(
                                new EvaluatePersistedAstRequestDto
                                {
                                    ProjectId = projectId,
                                    StationCode = stationCode,
                                    LogicUnitId = fbAst.LogicUnitId ?? 0,
                                    VariableName = currentParam,
                                    FbInputParamMap = fbInputParamMap,
                                });

                            if (fbEvalResult == null) continue;

                            await WriteLogHelper.WriteLogsAsync(
                                $"[DeepTrace] depth={depth} FB穿透hop{hopCount}求值: {callee} LogicUnit={fbAst.LogicUnitId} param={currentParam} EvalStatus={fbEvalResult.EvaluationStatus} EvalValue={fbEvalResult.EvaluatedValue}",
                                "LineDiagnosis/DeepTrace");

                            if (fbEvalResult.RootEvaluation == null) continue;

                            // 缩窄到当前追踪参数对应的子树
                            var fbEvalRoot = NarrowToFbOutputParam(
                                fbEvalResult.RootEvaluation, currentParam);

                            // 收集 False 叶子
                            var fbFalseLeaves = new List<StopCauseAstTraceNode>();
                            CollectFalseLeaves(fbEvalRoot, fbFalseLeaves);

                            var fbTrueBlockingLeaves = new List<StopCauseAstTraceNode>();
                            CollectFbTrueBlockingLeaves(fbEvalRoot, fbTrueBlockingLeaves);

                            // 同时收集 Unsupported 叶子（FB 内部局部变量无法从 PLC 读取）
                            var fbUnsupportedLeaves = new List<StopCauseAstTraceNode>();
                            CollectLeavesByStatus(fbEvalRoot, "Unsupported", fbUnsupportedLeaves);

                            // 合并：False 叶子直接输出 finding，Unsupported 叶子需继续追踪
                            var allLeaves = fbFalseLeaves
                                .Concat(fbTrueBlockingLeaves)
                                .Concat(fbUnsupportedLeaves)
                                .Where(n => !string.IsNullOrWhiteSpace(n.VariableName))
                                .Where(n => !IsNonNumericString(n.ActualValue?.ToString()))
                                .GroupBy(n =>
                                {
                                    var polarity = n.Metadata != null && n.Metadata.TryGetValue("FailurePolarity", out var p) ? p : "";
                                    return $"{n.VariableName.Trim()}|{n.Status}|{polarity}";
                                }, StringComparer.OrdinalIgnoreCase)
                                .Select(g => g.First())
                                .ToList();

                            var fbInputFallbackLeaves = new List<StopCauseAstTraceNode>();
                            if (allLeaves.Count == 0)
                            {
                                fbInputFallbackLeaves = CollectFbInputFallbackLeaves(
                                    fbEvalRoot,
                                    fbEvalResult.ExecutionContext,
                                    inputParamMap,
                                    fbAst,
                                    currentParam);

                                if (fbInputFallbackLeaves.Count > 0)
                                {
                                    allLeaves = fbInputFallbackLeaves
                                        .Where(n => !string.IsNullOrWhiteSpace(n.VariableName))
                                        .Where(n => !IsNonNumericString(n.ActualValue?.ToString()))
                                        .GroupBy(n => n.VariableName.Trim(), StringComparer.OrdinalIgnoreCase)
                                        .Select(g => g.First())
                                        .ToList();
                                }
                            }

                            await WriteLogHelper.WriteLogsAsync(
                                $"[DeepTrace] depth={depth} FB穿透hop{hopCount}: {callee} falseLeaves={fbFalseLeaves.Count} trueBlockingLeaves={fbTrueBlockingLeaves.Count} unsupportedLeaves={fbUnsupportedLeaves.Count} combined={allLeaves.Count}: [{string.Join(", ", allLeaves.Take(5).Select(l => $"{l.VariableName}={l.Status}"))}]",
                                "LineDiagnosis/DeepTrace");

                            if (fbInputFallbackLeaves.Count > 0)
                            {
                                await WriteLogHelper.WriteLogsAsync(
                                    $"[DeepTrace] depth={depth} FB input fallback: {callee} inputFallbackLeaves={fbInputFallbackLeaves.Count}: [{string.Join(", ", fbInputFallbackLeaves.Take(5).Select(l => $"{l.VariableName}={l.Status}"))}]",
                                    "LineDiagnosis/DeepTrace");
                            }

                            foreach (var fbLeaf in allLeaves)
                            {
                                if (summary.KeyFindings.Count >= maxAnchors * 2) break;

                                var fbLeafVar = fbLeaf.VariableName?.Trim();
                                if (string.IsNullOrWhiteSpace(fbLeafVar)) continue;

                                var isUnsupported = string.Equals(fbLeaf.Status, "Unsupported", StringComparison.OrdinalIgnoreCase);
                                var isTrueBlockingLeaf = fbLeaf.Metadata != null
                                    && fbLeaf.Metadata.TryGetValue("FailurePolarity", out var failurePolarity)
                                    && string.Equals(failurePolarity, "TrueBlocks", StringComparison.OrdinalIgnoreCase);

                                // ── 1. 尝试解析为 FB 输入参数（#形参 → 外部实参） ──
                                var resolvedExternalVar = (string)null;
                                var resolvedAllExternalVars = (string)null;
                                var plainFormalCheck = fbLeafVar.StartsWith("#")
                                    ? fbLeafVar.TrimStart('#')
                                    : fbLeafVar;
                                // 先精确匹配（如 "IWP"），再尝试尾段匹配（如 "CylinderCmd.IWP" → 尾段 "IWP"）。
                                // TIA 编译器在 FB 内部使用 "结构体.形参" 格式，但 inputParamMap 的 key 是纯形参名。
                                var resolvedFormalForDisplay = plainFormalCheck;
                                if (!inputParamMap.TryGetValue(plainFormalCheck, out var externalActual))
                                {
                                    var tailFormal = plainFormalCheck.Contains('.')
                                        ? plainFormalCheck.Substring(plainFormalCheck.LastIndexOf('.') + 1)
                                        : null;
                                    if (!string.IsNullOrEmpty(tailFormal))
                                    {
                                        if (inputParamMap.TryGetValue(tailFormal, out externalActual))
                                            resolvedFormalForDisplay = tailFormal;
                                    }
                                }
                                if (externalActual != null)
                                {
                                    resolvedAllExternalVars = externalActual;
                                    // "~" 前缀表示常闭触点（NegContact）极性：
                                    //   ~VarName → 该信号 True 时触点断开（是根因）
                                    //   VarName  → 该信号 False 时触点断开（是根因）
                                    // 取第一个非空候选作为主显示变量，剥除极性前缀后使用
                                    resolvedExternalVar = externalActual.Contains('|')
                                        ? externalActual.Split('|').Select(s => s.Trim()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.TrimStart('~')
                                        : externalActual.TrimStart('~');
                                }

                                // ── 2. 若为 Unsupported 且未映射到输入参数 → 是 FB 内部局部变量，加入追踪队列 ──
                                if (isUnsupported && resolvedExternalVar == null)
                                {
                                    if (!fbInternalVisited.Contains(fbLeafVar))
                                    {
                                        await WriteLogHelper.WriteLogsAsync(
                                            $"[DeepTrace] depth={depth} FB穿透hop{hopCount}: {fbLeafVar} 为 Unsupported 局部变量，加入 FB 内部追踪队列",
                                            "LineDiagnosis/DeepTrace");
                                        traceQueue.Enqueue(fbLeafVar);
                                    }
                                    continue; // 不输出 Finding，等递归追踪到终端再输出
                                }

                                // ── 3. 极性分析：判断常闭触点场景 ──
                                // 从 resolvedAllExternalVars 中找与 resolvedExternalVar 匹配的原始条目，
                                // 检查是否带 "~" 前缀（NegContact，信号 True 时触点断开）
                                var rawEntry = resolvedAllExternalVars != null
                                    ? resolvedAllExternalVars.Split('|')
                                        .Select(s => s.Trim())
                                        .FirstOrDefault(s => string.Equals(s.TrimStart('~'), resolvedExternalVar, StringComparison.OrdinalIgnoreCase))
                                    : null;
                                var isNegContactEntry = rawEntry != null && rawEntry.StartsWith("~");

                                // ── 4. 输出 Finding ──
                                var displayVar = resolvedExternalVar ?? fbLeafVar;
                                if (visitedVariables.Contains(displayVar)) continue;
                                visitedVariables.Add(displayVar);

                                if (resolvedAllExternalVars != null && resolvedAllExternalVars.Contains('|'))
                                {
                                    foreach (var ev in resolvedAllExternalVars.Split('|'))
                                    {
                                        if (!string.IsNullOrWhiteSpace(ev))
                                            visitedVariables.Add(ev.Trim().TrimStart('~'));
                                    }
                                }

                                var (ioComment, semLabel, terminalCategory, ioAddr) =
                                    await LookupIoTagInfoAsync(projectId, stationCode, displayVar);
                                var tagSuffix = BuildIoTagDisplaySuffix(semLabel, ioComment);

                                var fbOwnership = "InternalFault";
                                string displayPrefix;
                                if (resolvedExternalVar != null)
                                {
                                    // 常闭触点场景：信号 True 时触点断开，是根因
                                    displayPrefix = isNegContactEntry
                                        ? (!string.IsNullOrWhiteSpace(terminalCategory)
                                            ? $"[{terminalCategory}·常闭·L{depth}]"
                                            : $"[FB输入·常闭·L{depth}]")
                                        : isTrueBlockingLeaf
                                            ? (!string.IsNullOrWhiteSpace(terminalCategory)
                                                ? $"[{terminalCategory}·取反·L{depth}]"
                                                : $"[FB输入·取反·L{depth}]")
                                            : (!string.IsNullOrWhiteSpace(terminalCategory)
                                                ? $"[{terminalCategory}·L{depth}]"
                                                : $"[FB输入·L{depth}]");
                                }
                                else if (fbLeafVar.StartsWith("#"))
                                {
                                    displayPrefix = $"[{callee}·L{depth}]";
                                }
                                else
                                {
                                    displayPrefix = $"[写入源L{depth}]";
                                }

                                // 极性说明：常闭触点的信号值是 True（激活），而非 False
                                var leafValueNote = isNegContactEntry
                                    ? "=True（常闭触点被断开）"
                                    : isTrueBlockingLeaf
                                        ? "=True（取反条件被触发）"
                                        : "=False";
                                var evalStatusForFinding = (isNegContactEntry || isTrueBlockingLeaf) ? "True" : (fbLeaf.Status ?? "False");
                                var evalValueForFinding = (isNegContactEntry || isTrueBlockingLeaf) ? (bool?)true : false;

                                summary.KeyFindings.Add(new StationAnchorFinding
                                {
                                    VariableName = displayVar,
                                    DisplayName = $"{displayPrefix} {displayVar}{tagSuffix}",
                                    EvaluationStatus = evalStatusForFinding,
                                    EvaluatedValue = evalValueForFinding,
                                    DiagnosisQuality = "Diagnosed",
                                    BlockName = callee,
                                    FailurePathSummary = resolvedExternalVar != null
                                        ? $"{callee}.{outputParam} → #{resolvedFormalForDisplay}({resolvedExternalVar}{leafValueNote}){tagSuffix}（穿透 FB 输入形参定位到外部信号）"
                                        : $"{callee}.{outputParam} → {fbLeafVar}{leafValueNote}{tagSuffix}（穿透 FB 定位到终端信号）",
                                    TraceDepth = depth,
                                    FaultOwnership = fbOwnership,
                                    DiagnosisPhase = diagnosisPhase,
                                    IoTagAddress = ioAddr,
                                    IoTagComment = ioComment,
                                });

                                // 两种情况下继续往下深追踪：
                                // Case A: resolvedExternalVar == null → FB 内部局部变量，尝试继续溯源写入方
                                // Case B: resolvedExternalVar != null 且非常闭触点 → 外部 DB 变量，继续追踪这个 DB 变量的写入方（L3+）
                                //         常闭触点（isNegContactEntry）信号本身为 True 是根因，暂不支持 L3 溢源
                                if (!displayVar.StartsWith("#") && resolvedExternalVar == null)
                                {
                                    // Case A: FB 内部变量
                                    if (!isTrueBlockingLeaf)
                                    {
                                        await TraceFailureWriteSourcesAsync(
                                            projectId, stationCode, astRows,
                                            fbLeaf, summary,
                                            maxAnchors, depth + 1, visitedVariables,
                                            diagnosisPhase, sharedContext);
                                    }
                                }
                                else if (resolvedExternalVar != null && !isNegContactEntry && !isTrueBlockingLeaf && !resolvedExternalVar.StartsWith("#"))
                                {
                                    // Case B: 外部 DB 变量 → 查询其 PLC 写入方，有则继续展开（L3+）
                                    // visitedVariables 已含 resolvedExternalVar，需先移除以允许 TraceFailureWriteSourcesAsync 进入 PhaseA 查询写入方
                                    var externalWriteAccesses = await _variableAccessService.GetListAsync(va =>
                                        !va.IsDeleted && va.ProjectId == projectId &&
                                        va.VariableName == resolvedExternalVar &&
                                        (va.AccessType == "WRITE" || va.AccessType == "READWRITE") &&
                                        va.LogicUnitId.HasValue);
                                    if (externalWriteAccesses?.Count > 0)
                                    {
                                        await WriteLogHelper.WriteLogsAsync(
                                            $"[DeepTrace] depth={depth} L3+ 外部变量 {resolvedExternalVar} 有 {externalWriteAccesses.Count} 个写入方，继续递归",
                                            "LineDiagnosis/DeepTrace");
                                        visitedVariables.Remove(resolvedExternalVar); // 允许 TraceFailureWriteSourcesAsync 重新处理
                                        var externalLeafNode = new StopCauseAstTraceNode
                                        {
                                            NodeType = "Variable",
                                            VariableName = resolvedExternalVar,
                                            Status = "False",
                                            Result = false,
                                        };
                                        await TraceFailureWriteSourcesAsync(
                                            projectId, stationCode, astRows,
                                            externalLeafNode, summary,
                                            maxAnchors, depth + 1, visitedVariables,
                                            diagnosisPhase, sharedContext);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            await WriteLogHelper.WriteLogsAsync(
                                $"[DeepTrace] depth={depth} FB穿透异常: {callee} LogicUnit={fbAst.LogicUnitId} error={ex.Message}",
                                "LineDiagnosis/DeepTrace");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[DeepTrace] depth={depth} FB穿透整体异常: callee={callee} error={ex.Message}",
                    "LineDiagnosis/DeepTrace");
            }
        }

        /// <summary>
        /// 将递归根节点缩窄到与目标变量匹配的子树。
        /// 防止 FB Call 网络中无关输出参数被当作根因追踪（over-expansion）。
        /// </summary>
        private static StopCauseAstTraceNode NarrowEvalToTargetVariable(StopCauseAstTraceNode root, string targetVariable)
        {
            if (root == null || string.IsNullOrWhiteSpace(targetVariable))
                return root;

            var target = targetVariable.Trim();
            // 准备去引号版本用于容错匹配（Python解析器可能带引号: "DB645_xxx".INP.xxx ↔ DB645_xxx.INP.xxx）
            var targetNormalized = target.Replace("\"", "");

            // 根节点本身就是目标变量 → 直接返回
            if (NarrowVariableNameEquals(root.VariableName, target, targetNormalized))
                return root;

            // 根有多个子节点（典型的 FB Call AND root）→ 找到匹配目标变量的子节点
            if (root.Children != null && root.Children.Count > 1)
            {
                // 策略1: 精确匹配 VariableName（含去引号容错）
                var match = root.Children.FirstOrDefault(c =>
                    NarrowVariableNameEquals(c.VariableName, target, targetNormalized));
                if (match != null)
                    return match;

                // 策略2: 匹配 ExternalDependency 节点的最后一段路径（应对 DB 前缀格式差异）
                // 例如 target="DB645_OP60Cylinder.INP.OP060_CYL05_BkdPos" 的尾段 "OP060_CYL05_BkdPos"
                var targetLastSegment = target.Contains('.') ? target.Substring(target.LastIndexOf('.') + 1) : null;
                if (!string.IsNullOrWhiteSpace(targetLastSegment))
                {
                    var tailMatches = root.Children.Where(c =>
                    {
                        var childVar = c.VariableName?.Trim()?.Replace("\"", "");
                        if (string.IsNullOrWhiteSpace(childVar)) return false;
                        var childLast = childVar.Contains('.') ? childVar.Substring(childVar.LastIndexOf('.') + 1) : childVar;
                        return string.Equals(childLast, targetLastSegment, StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                    if (tailMatches.Count == 1)
                        return tailMatches[0]; // 唯一尾段匹配 → 可靠
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[NarrowEval] 未能缩窄: target={target} childCount={root.Children.Count} " +
                    $"childVars=[{string.Join(", ", root.Children.Take(8).Select(c => c.VariableName ?? "(null)"))}]");
            }

            // 无法缩窄（标准 Coil 网络等）→ 返回完整树
            return root;
        }

        /// <summary>
        /// 容错变量名匹配：先精确比较，再去引号比较。
        /// </summary>
        private static bool NarrowVariableNameEquals(string candidateVar, string target, string targetNormalized)
        {
            var candidate = candidateVar?.Trim();
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)) return true;
            // 去引号容错: "DB645_xxx".INP.xxx ↔ DB645_xxx.INP.xxx
            var candidateNormalized = candidate.Replace("\"", "");
            return string.Equals(candidateNormalized, targetNormalized, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 在求值树中深度优先搜索 VariableName==targetVariable 的 ExternalDependency 节点。
        /// 用于 Anchor 节点命中后的二次缩窄：从 FB Call And 块中精确找到对应输出的 ExternalDependency。
        /// </summary>
        private static StopCauseAstTraceNode FindExternalDependencyByVariable(StopCauseAstTraceNode root, string targetVariable)
        {
            if (root == null || string.IsNullOrWhiteSpace(targetVariable))
                return null;

            var target = targetVariable.Trim();
            var targetNormalized = target.Replace("\"", "");

            // 自身是 ExternalDependency 且 VariableName 匹配
            if (string.Equals(root.NodeType, "ExternalDependency", StringComparison.OrdinalIgnoreCase)
                && NarrowVariableNameEquals(root.VariableName, target, targetNormalized))
                return root;

            // 递归子节点（广度受限：FB Call And 块层数很浅，DFS 不会深递归）
            if (root.Children != null)
            {
                foreach (var child in root.Children)
                {
                    var found = FindExternalDependencyByVariable(child, targetVariable);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        /// <summary>
        /// 在 FB 内部评估树中，缩窄到写入指定输出参数的子树。
        /// 解决的问题：FB 内部一个 Network 可能有多个 Coil 写入不同输出
        /// （如 #FwdPos、#BkdPos、#QBkd 等共存于同一 lad-root），
        /// 如果不缩窄，CollectFalseLeaves 会把其他输出分支的 false 叶子也收集，
        /// 导致根因关联错误。
        /// </summary>
        private static StopCauseAstTraceNode NarrowToFbOutputParam(
            StopCauseAstTraceNode evalRoot, string outputParam)
        {
            if (evalRoot == null || string.IsNullOrWhiteSpace(outputParam))
                return evalRoot;

            // 编译后的 WriterLogicUnit 常以 Anchor 作为外层包装，真正的 lad-root 在其唯一子节点中。
            // 若这里直接因 Children.Count == 1 返回，会跳过 FwdPos/BkdPos 等并列 Coil 的缩窄，
            // 导致整张网络被扫描，把无关分支（如 BkdPos 子树）的叶子误收进来。
            if (string.Equals(evalRoot.NodeType, StopCauseAstNodeType.Anchor, StringComparison.OrdinalIgnoreCase)
                && evalRoot.Children != null
                && evalRoot.Children.Count == 1)
            {
                return NarrowToFbOutputParam(evalRoot.Children[0], outputParam);
            }

            var children = evalRoot.Children;
            if (children == null || children.Count <= 1)
                return evalRoot; // 只有一个子节点或无子节点，无需缩窄

            var formalWithHash = "#" + outputParam;

            // 策略 1: 直接子节点的 VariableName 精确匹配 #outputParam 或 outputParam
            var match = children.FirstOrDefault(c =>
            {
                var v = c.VariableName?.Trim();
                if (string.IsNullOrWhiteSpace(v)) return false;
                return string.Equals(v, formalWithHash, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v, outputParam, StringComparison.OrdinalIgnoreCase);
            });
            if (match != null) return match;

            // 策略 2: 递归查找包含 #outputParam 的子树（Coil 可能不在直接子节点上）
            // 支持精确匹配（#FwdPos / FwdPos）和尾段匹配（CylinderCmd.FwdPos → tail=FwdPos）
            var containingChildren = children
                .Where(c => ContainsVariableNode(c, formalWithHash)
                         || ContainsVariableNode(c, outputParam)
                         || ContainsVariableTailNode(c, outputParam))
                .ToList();
            if (containingChildren.Count == 1)
                return containingChildren[0]; // 唯一匹配 → 可靠

            // 策略 2b: 多个子树都含目标变量 → 优先选目标变量为 Coil（写入节点，有子节点）的子树
            // 原因：FB内同一 lad-root 可有多个 Network，FwdPos Coil 节点有子节点（其条件），
            // 而其他 Network 中读取 #FwdPos 作为 Contact 时是叶子节点（无子节点）。
            // 优先选 Coil 所在子树可避免把 BkdPos 等无关网络的 false 叶子也收集进来。
            if (containingChildren.Count > 1)
            {
                var coilContainers = containingChildren.Where(c =>
                {
                    var found = FindFirstVariableNode(c, formalWithHash)
                             ?? FindFirstVariableNode(c, outputParam)
                             ?? FindFirstVariableTailNode(c, outputParam); // 尾段 fallback: 匹配 CylinderCmd.FwdPos
                    return found != null && (found.Children?.Count ?? 0) > 0;
                }).ToList();
                // 有且仅有一个 Coil 容器 → 精确返回
                if (coilContainers.Count == 1) return coilContainers[0];
                // 多个 Coil 容器（少见）→ 取第一个，比返回全树更准确
                if (coilContainers.Count > 0) return coilContainers[0];
                // 都是 Contact 容器（极少见）→ 取第一个
                return containingChildren[0];
            }

            // 策略 3: 尾段匹配（应对 CylinderCmd.FwdPos 等复合路径）
            var tailMatches = children.Where(c =>
            {
                var v = c.VariableName?.Trim();
                if (string.IsNullOrWhiteSpace(v)) return false;
                var tail = v.Contains('.') ? v.Substring(v.LastIndexOf('.') + 1) : v;
                return string.Equals(tail, outputParam, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            if (tailMatches.Count == 1)
                return tailMatches[0];

            // 无法缩窄 → 返回完整树（兜底）
            return evalRoot;
        }

        /// <summary>
        /// 在树中找到第一个 VariableName 匹配的节点（DFS）。
        /// 与 ContainsVariableNode 不同，此方法返回节点本身，用于判断是 Coil（有子节点）还是 Contact（叶子）。
        /// </summary>
        private static StopCauseAstTraceNode FindFirstVariableNode(StopCauseAstTraceNode root, string varName)
        {
            if (root == null) return null;
            if (string.Equals(root.VariableName?.Trim(), varName, StringComparison.OrdinalIgnoreCase))
                return root;
            if (root.Children != null)
            {
                foreach (var child in root.Children)
                {
                    var found = FindFirstVariableNode(child, varName);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// 判断节点或其后代中是否包含指定变量名的节点（精确匹配）。
        /// </summary>
        private static bool ContainsVariableNode(StopCauseAstTraceNode node, string varName)
        {
            if (node == null) return false;
            if (string.Equals(node.VariableName?.Trim(), varName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (ContainsVariableNode(child, varName))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 判断节点或其后代中是否包含 VariableName 尾段（最后一个 '.' 之后）等于 tailName 的节点。
        /// 用于匹配 FB 内部复合路径，如 "CylinderCmd.FwdPos" 的尾段 "FwdPos"。
        /// </summary>
        private static bool ContainsVariableTailNode(StopCauseAstTraceNode node, string tailName)
        {
            if (node == null || string.IsNullOrWhiteSpace(tailName)) return false;
            var v = node.VariableName?.Trim();
            if (!string.IsNullOrWhiteSpace(v))
            {
                var tail = v.Contains('.') ? v.Substring(v.LastIndexOf('.') + 1) : v;
                if (string.Equals(tail, tailName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (ContainsVariableTailNode(child, tailName))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 深度优先搜索，返回 VariableName 尾段等于 tailName 的第一个节点。
        /// 用于策略 2b：识别 Coil（有子节点）vs Contact（叶子）以精确选择包含目标变量的子树。
        /// </summary>
        private static StopCauseAstTraceNode FindFirstVariableTailNode(StopCauseAstTraceNode root, string tailName)
        {
            if (root == null || string.IsNullOrWhiteSpace(tailName)) return null;
            var v = root.VariableName?.Trim();
            if (!string.IsNullOrWhiteSpace(v))
            {
                var tail = v.Contains('.') ? v.Substring(v.LastIndexOf('.') + 1) : v;
                if (string.Equals(tail, tailName, StringComparison.OrdinalIgnoreCase)) return root;
            }
            if (root.Children != null)
            {
                foreach (var child in root.Children)
                {
                    var found = FindFirstVariableTailNode(child, tailName);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static void CollectFalseLeaves(StopCauseAstTraceNode node, List<StopCauseAstTraceNode> result)
        {
            if (node == null) return;

            var isFalse = string.Equals(node.Status, "False", StringComparison.OrdinalIgnoreCase);
            var hasChildren = node.Children != null && node.Children.Count > 0;

            if (isFalse && !hasChildren && !string.IsNullOrWhiteSpace(node.VariableName))
            {
                // 叶子False节点——这才是真正导致失败的信号
                result.Add(node);
                return;
            }

            if (hasChildren)
            {
                // Not（negated-contact）节点的子节点为 False，表示 NOT 条件满足（NOT False = True），
                // 这是正常状态，不是故障原因。不递归进 Not 节点的子节点，避免误报互锁信号。
                // 例：NOT IHP（缩回到位传感器）在伸出网络里 IHP=False 是正常的，不应报告为 InternalFault。
                if (string.Equals(node.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase))
                    return;

                // Reset（R 线圈）节点的子节点 False 表示"复位条件未触发"，
                // 即输出没有被主动复位——这是正常状态，不是导致输出为 False 的根因。
                // 例：[IHP] → R(FwdPos)，IHP=False 表示"不在缩回位"，与伸出方向无关。
                if (string.Equals(node.NodeType, StopCauseAstNodeType.Reset, StringComparison.OrdinalIgnoreCase))
                    return;

                // SetResetFlipFlop（SR/RS 指令）：Children[0]=S 分支（置位条件），Children[1]=R 分支（复位条件）。
                // 只收集 S 分支的 False 叶子（置位条件不满足 = 故障）；
                // R 分支的 False 叶子（复位条件不触发 = 正常，不是故障）直接跳过。
                if (string.Equals(node.NodeType, StopCauseAstNodeType.SetResetFlipFlop, StringComparison.OrdinalIgnoreCase))
                {
                    if (node.Children.Count > 0)
                        CollectFalseLeaves(node.Children[0], result); // 只递归 S 分支
                    return;
                }

                foreach (var child in node.Children)
                {
                    CollectFalseLeaves(child, result);
                }
            }
        }

        private static void CollectFbTrueBlockingLeaves(StopCauseAstTraceNode node, List<StopCauseAstTraceNode> result)
        {
            if (node == null) return;

            if (IsDisabledAlwaysTrueGuard(node))
                return;

            var isFalse = string.Equals(node.Status, "False", StringComparison.OrdinalIgnoreCase);
            var isNot = string.Equals(node.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase);
            if (isFalse && isNot && node.Children?.Count > 0)
            {
                var child = node.Children[0];
                if (!string.IsNullOrWhiteSpace(child.VariableName)
                    && string.Equals(child.Status, "True", StringComparison.OrdinalIgnoreCase)
                    && !IsSyntheticAlwaysTrue(child.VariableName))
                {
                    result.Add(new StopCauseAstTraceNode
                    {
                        NodeId = child.NodeId,
                        NodeType = child.NodeType,
                        VariableName = child.VariableName,
                        DisplayName = child.DisplayName,
                        Operator = child.Operator,
                        ExpectedValue = child.ExpectedValue,
                        ActualValue = child.ActualValue,
                        Result = true,
                        Status = "True",
                        Message = "取反条件被触发，导致 FB 输出条件不成立。",
                        Metadata = new Dictionary<string, string>(child.Metadata ?? new Dictionary<string, string>())
                        {
                            ["FailurePolarity"] = "TrueBlocks"
                        },
                        Children = child.Children
                    });
                }
                return;
            }

            if (node.Children == null || node.Children.Count == 0)
                return;

            foreach (var child in node.Children)
            {
                if (string.Equals(node.NodeType, StopCauseAstNodeType.Or, StringComparison.OrdinalIgnoreCase)
                    && ContainsDisabledAlwaysTrueBranch(child))
                {
                    continue;
                }

                CollectFbTrueBlockingLeaves(child, result);
            }
        }

        private static List<StopCauseAstTraceNode> CollectFbInputFallbackLeaves(
            StopCauseAstTraceNode root,
            StopCauseExecutionContext context,
            IReadOnlyDictionary<string, string> inputParamMap,
            PLC_LogicUnitAstEntity astRow,
            string outputParam)
        {
            var result = new List<StopCauseAstTraceNode>();
            if (root == null || context == null || inputParamMap == null || inputParamMap.Count == 0)
                return result;

            var positiveTails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rawOutputRoot = TryNarrowRawAstToFbOutputParam(astRow, outputParam);
            if (rawOutputRoot != null)
            {
                CollectPositiveVariableTails(rawOutputRoot, positiveTails, underNot: false);
            }

            if (positiveTails.Count == 0)
            {
                CollectPositiveVariableTails(root, positiveTails, underNot: false);
            }

            var rawPositiveDependencies = new List<StopCauseAstNode>();
            if (rawOutputRoot != null)
            {
                CollectPositiveDependencyNodes(rawOutputRoot, rawPositiveDependencies, underNot: false);
            }

            foreach (var kv in inputParamMap)
            {
                var formal = kv.Key?.Trim();
                if (string.IsNullOrWhiteSpace(formal))
                    continue;

                var plainFormal = formal.TrimStart('#');
                var formalTail = GetVariableTail(plainFormal);
                var appearsInPositivePath = positiveTails.Contains(plainFormal) || positiveTails.Contains(formalTail);

                if (!appearsInPositivePath)
                    continue;

                if (!TryResolveFbInputEffectiveBool(context, plainFormal, kv.Value, out var effectiveValue))
                    continue;

                if (effectiveValue != false)
                    continue;

                var matchedNode = FindFirstPositiveVariableTailNode(root, formalTail)
                    ?? FindFirstVariableTailNode(root, formalTail);

                result.Add(new StopCauseAstTraceNode
                {
                    NodeId = matchedNode?.NodeId ?? $"fb-input:{plainFormal}",
                    NodeType = matchedNode?.NodeType ?? "Contact",
                    VariableName = matchedNode?.VariableName ?? plainFormal,
                    DisplayName = matchedNode?.DisplayName ?? plainFormal,
                    ExpectedValue = matchedNode?.ExpectedValue ?? "True",
                    ActualValue = false,
                    Result = false,
                    Status = "False",
                    Message = "FB input effective value is False while normal trace leaves are empty.",
                    Metadata = new Dictionary<string, string>(matchedNode?.Metadata ?? new Dictionary<string, string>())
                    {
                        ["FbInputFallback"] = "True"
                    },
                    Children = matchedNode?.Children ?? new List<StopCauseAstTraceNode>()
                });
            }

            foreach (var dependency in rawPositiveDependencies)
            {
                var dependencyVar = dependency.VariableName?.Trim();
                if (string.IsNullOrWhiteSpace(dependencyVar) || IsSyntheticAlwaysTrue(dependencyVar))
                    continue;

                var dependencyTail = GetVariableTail(dependencyVar);
                if (inputParamMap.ContainsKey(dependencyVar) || inputParamMap.ContainsKey(dependencyTail))
                    continue;

                if (TryGetContextValueByName(context, dependencyVar, out var currentValue)
                    && CoerceTraceBoolean(currentValue) == true)
                {
                    continue;
                }

                result.Add(new StopCauseAstTraceNode
                {
                    NodeId = dependency.NodeId ?? $"fb-internal:{dependencyVar}",
                    NodeType = dependency.NodeType ?? "Contact",
                    VariableName = dependencyVar,
                    DisplayName = dependency.DisplayName ?? dependencyVar,
                    ExpectedValue = dependency.ExpectedValue ?? "True",
                    ActualValue = currentValue,
                    Result = false,
                    Status = "Unsupported",
                    Message = "FB positive dependency requires internal write-source tracing.",
                    Metadata = new Dictionary<string, string>(dependency.Metadata ?? new Dictionary<string, string>())
                    {
                        ["FbInternalDependencyFallback"] = "True"
                    },
                    Children = new List<StopCauseAstTraceNode>()
                });
            }

            return result
                .GroupBy(n => n.VariableName?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .Select(g => g.First())
                .ToList();
        }

        private static StopCauseAstNode TryNarrowRawAstToFbOutputParam(
            PLC_LogicUnitAstEntity astRow,
            string outputParam)
        {
            if (astRow == null || string.IsNullOrWhiteSpace(astRow.RootNodeJson) || string.IsNullOrWhiteSpace(outputParam))
                return null;

            try
            {
                var root = JsonSerializer.Deserialize<StopCauseAstNode>(astRow.RootNodeJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return NarrowRawAstToFbOutputParam(root, outputParam);
            }
            catch
            {
                return null;
            }
        }

        private static StopCauseAstNode NarrowRawAstToFbOutputParam(
            StopCauseAstNode root,
            string outputParam)
        {
            if (root == null || string.IsNullOrWhiteSpace(outputParam))
                return root;

            if (string.Equals(root.NodeType, StopCauseAstNodeType.Anchor, StringComparison.OrdinalIgnoreCase)
                && root.Children != null
                && root.Children.Count == 1)
            {
                return NarrowRawAstToFbOutputParam(root.Children[0], outputParam);
            }

            var children = root.Children;
            if (children == null || children.Count == 0)
                return root;

            var directOutput = children.FirstOrDefault(child =>
                IsVariableTail(child.VariableName, outputParam)
                && (child.Children?.Count ?? 0) > 0);
            if (directOutput != null)
                return directOutput;

            var containingChildren = children
                .Where(child => ContainsVariableTailNode(child, outputParam))
                .ToList();
            if (containingChildren.Count == 1)
                return containingChildren[0];

            if (containingChildren.Count > 1)
            {
                var coilContainers = containingChildren.Where(child =>
                {
                    var found = FindFirstVariableTailNode(child, outputParam);
                    return found != null && (found.Children?.Count ?? 0) > 0;
                }).ToList();

                if (coilContainers.Count > 0)
                    return coilContainers[0];
            }

            if (IsVariableTail(root.VariableName, outputParam)
                && (root.Children?.Count ?? 0) > 0
                && !string.Equals(root.NodeType, StopCauseAstNodeType.Anchor, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return root;
        }

        private static void CollectPositiveVariableTails(
            StopCauseAstTraceNode node,
            HashSet<string> tails,
            bool underNot)
        {
            if (node == null) return;

            if (IsDisabledAlwaysTrueGuard(node))
                return;

            var nodeIsNot = string.Equals(node.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase);
            if (!underNot && !nodeIsNot && !string.IsNullOrWhiteSpace(node.VariableName) && !IsSyntheticAlwaysTrue(node.VariableName))
            {
                tails.Add(GetVariableTail(node.VariableName));
            }

            if (node.Children == null || node.Children.Count == 0)
                return;

            foreach (var child in node.Children)
            {
                if (string.Equals(node.NodeType, StopCauseAstNodeType.Or, StringComparison.OrdinalIgnoreCase)
                    && ContainsDisabledAlwaysTrueBranch(child))
                {
                    continue;
                }

                CollectPositiveVariableTails(child, tails, nodeIsNot ? !underNot : underNot);
            }
        }

        private static void CollectPositiveVariableTails(
            StopCauseAstNode node,
            HashSet<string> tails,
            bool underNot)
        {
            if (node == null) return;

            if (IsDisabledAlwaysTrueGuard(node))
                return;

            var nodeIsNot = string.Equals(node.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase);
            if (!underNot && !nodeIsNot && !string.IsNullOrWhiteSpace(node.VariableName) && !IsSyntheticAlwaysTrue(node.VariableName))
            {
                tails.Add(GetVariableTail(node.VariableName));
            }

            if (node.Children == null || node.Children.Count == 0)
                return;

            foreach (var child in node.Children)
            {
                if (string.Equals(node.NodeType, StopCauseAstNodeType.Or, StringComparison.OrdinalIgnoreCase)
                    && ContainsDisabledAlwaysTrueBranch(child))
                {
                    continue;
                }

                CollectPositiveVariableTails(child, tails, nodeIsNot ? !underNot : underNot);
            }
        }

        private static void CollectPositiveDependencyNodes(
            StopCauseAstNode node,
            List<StopCauseAstNode> result,
            bool underNot)
        {
            if (node == null) return;

            if (IsDisabledAlwaysTrueGuard(node))
                return;

            var nodeIsNot = string.Equals(node.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase);
            var hasChildren = node.Children != null && node.Children.Count > 0;
            if (!underNot
                && !nodeIsNot
                && !hasChildren
                && !string.IsNullOrWhiteSpace(node.VariableName)
                && !IsSyntheticAlwaysTrue(node.VariableName))
            {
                result.Add(node);
                return;
            }

            if (!hasChildren)
                return;

            foreach (var child in node.Children)
            {
                if (string.Equals(node.NodeType, StopCauseAstNodeType.Or, StringComparison.OrdinalIgnoreCase)
                    && ContainsDisabledAlwaysTrueBranch(child))
                {
                    continue;
                }

                CollectPositiveDependencyNodes(child, result, nodeIsNot ? !underNot : underNot);
            }
        }

        private static StopCauseAstTraceNode FindFirstPositiveVariableTailNode(
            StopCauseAstTraceNode node,
            string tailName,
            bool underNot = false)
        {
            if (node == null || string.IsNullOrWhiteSpace(tailName))
                return null;

            if (IsDisabledAlwaysTrueGuard(node))
                return null;

            var nodeIsNot = string.Equals(node.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase);
            if (!underNot && !nodeIsNot && !string.IsNullOrWhiteSpace(node.VariableName))
            {
                var tail = GetVariableTail(node.VariableName);
                if (string.Equals(tail, tailName, StringComparison.OrdinalIgnoreCase))
                    return node;
            }

            if (node.Children == null || node.Children.Count == 0)
                return null;

            foreach (var child in node.Children)
            {
                if (string.Equals(node.NodeType, StopCauseAstNodeType.Or, StringComparison.OrdinalIgnoreCase)
                    && ContainsDisabledAlwaysTrueBranch(child))
                {
                    continue;
                }

                var found = FindFirstPositiveVariableTailNode(child, tailName, nodeIsNot ? !underNot : underNot);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static bool TryResolveFbInputEffectiveBool(
            StopCauseExecutionContext context,
            string plainFormal,
            string externalSpec,
            out bool effectiveValue)
        {
            effectiveValue = false;
            if (context == null || string.IsNullOrWhiteSpace(plainFormal))
                return false;

            if (TryGetContextValueByName(context, plainFormal, out var formalValue)
                || TryGetContextValueByName(context, "#" + plainFormal, out formalValue))
            {
                var formalBool = CoerceTraceBoolean(formalValue);
                if (formalBool.HasValue)
                {
                    effectiveValue = formalBool.Value;
                    return true;
                }
            }

            return TryResolveExternalSpecEffectiveBool(context, externalSpec, out effectiveValue);
        }

        private static bool TryResolveExternalSpecEffectiveBool(
            StopCauseExecutionContext context,
            string externalSpec,
            out bool effectiveValue)
        {
            effectiveValue = false;
            if (context == null || string.IsNullOrWhiteSpace(externalSpec))
                return false;

            bool? resolved = null;
            foreach (var rawEntry in externalSpec.Split('|'))
            {
                var entry = rawEntry?.Trim();
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                var isNegated = entry.StartsWith("~");
                var actualName = isNegated ? entry.TrimStart('~') : entry;
                if (!TryGetContextValueByName(context, actualName, out var rawValue))
                    continue;

                var rawBool = CoerceTraceBoolean(rawValue);
                if (!rawBool.HasValue)
                    continue;

                var entryEffective = isNegated ? !rawBool.Value : rawBool.Value;
                if (!resolved.HasValue)
                    resolved = entryEffective;
                else if (entryEffective)
                    resolved = true;
                else if (resolved != true)
                    resolved = false;
            }

            if (!resolved.HasValue)
                return false;

            effectiveValue = resolved.Value;
            return true;
        }

        private static bool TryGetContextValueByName(
            StopCauseExecutionContext context,
            string variableName,
            out object value)
        {
            value = null;
            var values = context?.CurrentValues;
            if (values == null || values.Count == 0 || string.IsNullOrWhiteSpace(variableName))
                return false;

            var normalized = NormalizeTraceSymbol(variableName);
            if (values.TryGetValue(variableName, out value)
                || values.TryGetValue(normalized, out value))
            {
                return true;
            }

            var exact = values.FirstOrDefault(kv =>
                string.Equals(NormalizeTraceSymbol(kv.Key), normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact.Key))
            {
                value = exact.Value;
                return true;
            }

            var tail = GetVariableTail(normalized);
            if (!string.IsNullOrWhiteSpace(tail))
            {
                if (values.TryGetValue(tail, out value) || values.TryGetValue("#" + tail, out value))
                    return true;

                var tailMatch = values.FirstOrDefault(kv =>
                    string.Equals(GetVariableTail(kv.Key), tail, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(tailMatch.Key))
                {
                    value = tailMatch.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsVariableTailNode(StopCauseAstNode node, string tailName)
        {
            if (node == null || string.IsNullOrWhiteSpace(tailName)) return false;

            if (IsVariableTail(node.VariableName, tailName))
                return true;

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (ContainsVariableTailNode(child, tailName))
                        return true;
                }
            }

            return false;
        }

        private static StopCauseAstNode FindFirstVariableTailNode(StopCauseAstNode root, string tailName)
        {
            if (root == null || string.IsNullOrWhiteSpace(tailName)) return null;

            if (IsVariableTail(root.VariableName, tailName))
                return root;

            if (root.Children != null)
            {
                foreach (var child in root.Children)
                {
                    var found = FindFirstVariableTailNode(child, tailName);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private static bool IsVariableTail(string variableName, string tailName)
        {
            return !string.IsNullOrWhiteSpace(variableName)
                && !string.IsNullOrWhiteSpace(tailName)
                && string.Equals(GetVariableTail(variableName), GetVariableTail(tailName), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeTraceSymbol(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? ""
                : value.Trim().Trim('"').Replace("\"", "");
        }

        private static string GetVariableTail(string variableName)
        {
            var normalized = NormalizeTraceSymbol(variableName).TrimStart('#');
            if (string.IsNullOrWhiteSpace(normalized))
                return "";

            var dotIndex = normalized.LastIndexOf('.');
            return dotIndex >= 0 && dotIndex < normalized.Length - 1
                ? normalized.Substring(dotIndex + 1)
                : normalized;
        }

        private static bool? CoerceTraceBoolean(object value)
        {
            if (value == null) return null;
            if (value is bool b) return b;

            var text = value.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (bool.TryParse(text, out var boolResult)) return boolResult;
            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase)) return false;

            return null;
        }

        private static bool ContainsDisabledAlwaysTrueBranch(StopCauseAstTraceNode node)
        {
            if (node == null) return false;

            if (IsDisabledAlwaysTrueGuard(node))
                return true;

            return node.Children != null && node.Children.Any(ContainsDisabledAlwaysTrueBranch);
        }

        private static bool ContainsDisabledAlwaysTrueBranch(StopCauseAstNode node)
        {
            if (node == null) return false;

            if (IsDisabledAlwaysTrueGuard(node))
                return true;

            return node.Children != null && node.Children.Any(ContainsDisabledAlwaysTrueBranch);
        }

        private static bool IsDisabledAlwaysTrueGuard(StopCauseAstTraceNode node)
        {
            return string.Equals(node?.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase)
                && string.Equals(node.Status, "False", StringComparison.OrdinalIgnoreCase)
                && node.Children?.Count > 0
                && IsSyntheticAlwaysTrue(node.Children[0].VariableName);
        }

        private static bool IsDisabledAlwaysTrueGuard(StopCauseAstNode node)
        {
            return string.Equals(node?.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase)
                && node.Children?.Count > 0
                && IsSyntheticAlwaysTrue(node.Children[0].VariableName);
        }

        private static bool IsSyntheticAlwaysTrue(string variableName)
        {
            return string.Equals(variableName?.Trim(), "AlwaysTRUE", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 无守卫版 False 叶子收集：不跳过 Not / Reset / SetResetFlipFlop 节点。
        /// 专用于 GRAPH 转移条件子树——转移条件中所有 False 叶子均为真实故障，
        /// 不存在"NOT 节点内部 False 是正常状态"的语义。
        /// </summary>
        private static void CollectFalseLeavesUnguarded(StopCauseAstTraceNode node, List<StopCauseAstTraceNode> result)
        {
            if (node == null) return;
            var isFalse = string.Equals(node.Status, "False", StringComparison.OrdinalIgnoreCase);
            var hasChildren = node.Children != null && node.Children.Count > 0;
            if (isFalse && !hasChildren && !string.IsNullOrWhiteSpace(node.VariableName))
            {
                result.Add(node);
                return;
            }
            if (hasChildren)
            {
                foreach (var child in node.Children)
                    CollectFalseLeavesUnguarded(child, result);
            }
        }

        /// <summary>
        /// 按指定 Status 收集叶子节点（通用版本，用于收集 Unsupported 等状态）。
        /// 与 CollectFalseLeaves 逻辑相同，仅匹配状态不同。
        /// </summary>
        private static void CollectLeavesByStatus(StopCauseAstTraceNode node, string status, List<StopCauseAstTraceNode> result)
        {
            if (node == null) return;

            var isMatch = string.Equals(node.Status, status, StringComparison.OrdinalIgnoreCase);
            var hasChildren = node.Children != null && node.Children.Count > 0;

            if (isMatch && !hasChildren && !string.IsNullOrWhiteSpace(node.VariableName))
            {
                result.Add(node);
                return;
            }

            if (hasChildren)
            {
                // Not 节点内的 Unsupported/False 叶子均不收集：
                // NOT 内的叶子值为 False → NOT=True（满足条件），是正常状态，非故障根因。
                if (string.Equals(node.NodeType, StopCauseAstNodeType.Not, StringComparison.OrdinalIgnoreCase))
                    return;

                // Reset 线圈的子条件不满足（Unsupported/False）= 复位条件未触发，非故障根因。
                if (string.Equals(node.NodeType, StopCauseAstNodeType.Reset, StringComparison.OrdinalIgnoreCase))
                    return;

                // SetResetFlipFlop：只关心 S 分支（置位条件），跳过 R 分支（复位条件）。
                if (string.Equals(node.NodeType, StopCauseAstNodeType.SetResetFlipFlop, StringComparison.OrdinalIgnoreCase))
                {
                    if (node.Children.Count > 0)
                        CollectLeavesByStatus(node.Children[0], status, result);
                    return;
                }

                foreach (var child in node.Children)
                {
                    CollectLeavesByStatus(child, status, result);
                }
            }
        }

        /// <summary>
        /// 实时读取 IoT 判断当前是否有处于激活态的 GRAPH 步骤。
        /// 绕过 Redis 镜像（镜像在程序重启后可能尚未刷新，且 IsStableAlwaysOnGraphAction 过滤器
        /// 会误判长时间卡住的步骤），直接向 PLC 读取当前值。
        /// IoT 读取失败时降级到镜像检查。
        /// </summary>
        /// <summary>
        /// 读取 IoT 实时信号，返回当前激活（TRUE）的 GraphAction 影子信号所对应的 StallCandidateInfo 列表。
        /// 列表非空表示存在激活的 GRAPH 步骤，可直接作为 EvaluateStallCandidatesAsync 的候选。
        /// 读取失败时降级到 Redis 镜像（仅返回 bool 兜底，候选列表为空）。
        /// </summary>
        private async Task<List<StallCandidateInfo>> GetActiveDeviceFocusCandidatesAsync(
            long projectId,
            string stationCode,
            List<PLC_LogicUnitAstEntity> astRows,
            int maxCandidates,
            HashSet<string> anchorTypeFilter = null)
        {
            var allShadowSignals = await _stationShadowSignalService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == projectId
                && it.StationCode == stationCode
                && it.Enabled == true);

            var effectiveAnchorTypeFilter = anchorTypeFilter ?? ResolveDefaultDeviceFocusDiagnosisAnchorTypes();
            var focusSignals = (allShadowSignals ?? new List<PLC_StationShadowSignalEntity>())
                .Where(IsDeviceFocusDiagnosticSignal)
                .Where(signal => !IsDeviceAlarmFalseAbnormalRule(signal.AnchorType, signal.ActivationRule))
                .Where(signal => effectiveAnchorTypeFilter.Count == 0
                    || effectiveAnchorTypeFilter.Contains(signal.AnchorType?.Trim() ?? string.Empty))
                .GroupBy(signal => signal.VariableName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(signal => signal.Priority ?? 0)
                    .ThenBy(signal => signal.SortOrder ?? int.MaxValue)
                    .First())
                .OrderByDescending(signal => signal.Priority ?? 0)
                .ThenBy(signal => signal.SortOrder ?? int.MaxValue)
                .Take(Math.Max(maxCandidates * 2, 20))
                .ToList();

            if (focusSignals.Count == 0)
                return new List<StallCandidateInfo>();

            var symbolPaths = focusSignals
                .Select(signal => signal.VariableName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var valueByVariable = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var changeTimeByVariable = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var snapshot = await _signalSnapshotService.AcquireAsync(new AcquireDiagnosticSnapshotRequestDto
                {
                    ProjectId = projectId,
                    StationCode = stationCode,
                    SymbolPaths = symbolPaths,
                    IncludeStationAnchorVariables = false,
                });

                foreach (var kv in snapshot?.ExecutionContext?.CurrentValues ?? new Dictionary<string, object>())
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                    {
                        valueByVariable[kv.Key.Trim()] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[DeviceFocusCheck] {stationCode} realtime read failed, fallback to signal mirror: {ex.Message}",
                    "LineDiagnosis/DeviceFocusCheck");
            }

            if (valueByVariable.Count == 0)
            {
                var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, stationCode);
                foreach (var mirror in mirrors?.Values ?? Enumerable.Empty<StationShadowSignalCacheModel>())
                {
                    if (string.IsNullOrWhiteSpace(mirror?.VariableName))
                        continue;

                    var variableName = mirror.VariableName.Trim();
                    valueByVariable[variableName] = mirror.CurrentValue;
                    changeTimeByVariable[variableName] = mirror.LastChangeTime;
                }
            }
            else
            {
                var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, stationCode);
                foreach (var mirror in mirrors?.Values ?? Enumerable.Empty<StationShadowSignalCacheModel>())
                {
                    if (!string.IsNullOrWhiteSpace(mirror?.VariableName))
                    {
                        changeTimeByVariable[mirror.VariableName.Trim()] = mirror.LastChangeTime;
                    }
                }
            }

            if (valueByVariable.Count == 0)
                return new List<StallCandidateInfo>();

            var candidates = new List<StallCandidateInfo>();
            foreach (var signal in focusSignals)
            {
                var variableName = signal.VariableName?.Trim();
                if (string.IsNullOrWhiteSpace(variableName))
                    continue;

                if (!valueByVariable.TryGetValue(variableName, out var currentValue)
                    || !GraphActivationRuleInferService.EvaluateRule(signal.ActivationRule, currentValue))
                {
                    continue;
                }

                var astMatch = signal.LogicUnitId.HasValue
                    ? astRows.FirstOrDefault(row => row.LogicUnitId == signal.LogicUnitId)
                    : null;
                astMatch ??= FindBestRuntimeAstOutputMatch(variableName, astRows);
                if (astMatch?.LogicUnitId == null)
                    continue;

                changeTimeByVariable.TryGetValue(variableName, out var changedAt);
                var abnormalOnTrue = IsDeviceFocusAbnormalOnTrue(signal);
                var label = string.IsNullOrWhiteSpace(signal.SemanticLabel)
                    ? variableName
                    : signal.SemanticLabel.Trim();

                candidates.Add(new StallCandidateInfo
                {
                    VariableName = variableName,
                    SignalKey = $"[DeviceFocus] {label}",
                    ChangedAt = changedAt ?? DateTime.Now,
                    LastTrueAt = abnormalOnTrue ? changedAt : null,
                    LogicUnitId = astMatch.LogicUnitId,
                    BlockName = astMatch.BlockName,
                    DisplayLabel = $"[DeviceFocus] {label}",
                    UnitType = astMatch.UnitType,
                    IsGraphAction = false,
                    IsDeviceFocus = true,
                    AnchorType = signal.AnchorType,
                    ActivationRule = signal.ActivationRule,
                    AbnormalWhen = abnormalOnTrue ? "True" : "False",
                    CurrentValueText = currentValue?.ToString(),
                    ActivationDirection = abnormalOnTrue ? "DeviceFocusAbnormalTrue" : "DeviceFocusAbnormalFalse",
                    MatchRatio = 1.0,
                    ActiveSignalCount = Math.Max(signal.Priority ?? 0, 1),
                    TotalSignalCount = 1,
                });
            }

            if (candidates.Count > 0)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[DeviceFocusCheck] {stationCode} abnormal={candidates.Count} vars=[{string.Join(",", candidates.Take(8).Select(item => item.VariableName))}]",
                    "LineDiagnosis/DeviceFocusCheck");
            }

            return candidates
                .OrderByDescending(item => item.ActiveSignalCount)
                .Take(Math.Max(maxCandidates, 1))
                .ToList();
        }

        private static bool IsDeviceFocusDiagnosticSignal(PLC_StationShadowSignalEntity signal)
        {
            return signal != null
                && signal.DiagnosisExcluded != true
                && signal.IsContext != true
                && !string.Equals(signal.AnchorType, "DeviceAnalogContext", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(signal.VariableName)
                && (string.Equals(signal.GroupName, DeviceFocusGroupName, StringComparison.OrdinalIgnoreCase)
                    || DeviceFocusAnchorTypes.Contains(signal.AnchorType ?? string.Empty));
        }

        private static bool IsDeviceFocusAbnormalOnTrue(PLC_StationShadowSignalEntity signal)
        {
            if (string.Equals(signal?.ActivationRule, "BOOL_FALSE", StringComparison.OrdinalIgnoreCase))
                return false;

            var anchorType = signal?.AnchorType?.Trim();
            if (string.Equals(anchorType, "DeviceMotionFeedback", StringComparison.OrdinalIgnoreCase)
                || string.Equals(anchorType, "DevicePermitState", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool IsDeviceFocusAbnormalTrueCandidate(StallCandidateInfo candidate)
        {
            if (candidate?.IsDeviceFocus != true)
                return false;

            if (string.Equals(candidate.AbnormalWhen, "True", StringComparison.OrdinalIgnoreCase))
                return true;

            return string.Equals(candidate.ActivationRule, "BOOL_TRUE", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.AnchorType, "DeviceMotionFeedback", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.AnchorType, "DevicePermitState", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDeviceFocusAbnormalFindingSummary(StallCandidateInfo candidate, bool? evaluatedValue)
        {
            var abnormalWhen = string.IsNullOrWhiteSpace(candidate?.AbnormalWhen)
                ? "Unknown"
                : candidate.AbnormalWhen.Trim();
            var anchorType = string.IsNullOrWhiteSpace(candidate?.AnchorType)
                ? "DeviceFocus"
                : candidate.AnchorType.Trim();
            var currentValue = string.IsNullOrWhiteSpace(candidate?.CurrentValueText)
                ? evaluatedValue?.ToString() ?? "Unknown"
                : candidate.CurrentValueText.Trim();
            return $"Device focus signal abnormal: {anchorType}, abnormalWhen={abnormalWhen}, currentValue={currentValue}";
        }

        private static bool ShouldReportDeviceFocusDirectly(StallCandidateInfo candidate)
        {
            return candidate?.IsDeviceFocus == true
                && string.Equals(candidate.AnchorType, "DeviceHoldState", StringComparison.OrdinalIgnoreCase);
        }

        private static int ResolveCandidateMaxFbPenetrationDepth(StallCandidateInfo candidate, int requestedMaxDepth)
        {
            var normalized = Math.Clamp(requestedMaxDepth <= 0 ? 4 : requestedMaxDepth, 1, 12);
            return candidate?.IsDeviceFocus == true
                ? Math.Min(normalized, DeviceFocusMaxFbPenetrationDepth)
                : normalized;
        }

        private static StationAnchorFinding BuildDirectDeviceFocusFinding(StallCandidateInfo candidate)
        {
            var currentValue = CoerceTraceBoolean(candidate?.CurrentValueText);
            var currentValueText = string.IsNullOrWhiteSpace(candidate?.CurrentValueText)
                ? currentValue?.ToString() ?? "Unknown"
                : candidate.CurrentValueText.Trim();
            var abnormalWhen = string.IsNullOrWhiteSpace(candidate?.AbnormalWhen)
                ? "Unknown"
                : candidate.AbnormalWhen.Trim();
            var anchorType = string.IsNullOrWhiteSpace(candidate?.AnchorType)
                ? "DeviceFocus"
                : candidate.AnchorType.Trim();

            return new StationAnchorFinding
            {
                VariableName = candidate?.VariableName,
                DisplayName = candidate?.DisplayLabel ?? candidate?.VariableName,
                EvaluationStatus = currentValue?.ToString() ?? "Triggered",
                EvaluatedValue = currentValue,
                DiagnosisQuality = "Direct",
                BlockName = candidate?.BlockName,
                FailurePathSummary =
                    $"设备关注信号命中异常：{anchorType}，异常条件={abnormalWhen}，当前值={currentValueText}；保持/互锁类状态已直接作为诊断结论，不继续追踪写入源。",
                LastTrueAt = ResolveFindingLastTrueAt(candidate),
                TraceDepth = 0,
                FaultOwnership = "LocalInterlock",
                DiagnosisPhase = "DeviceFocusDirect",
            };
        }

        private async Task<List<StallCandidateInfo>> GetActiveGraphCandidatesAsync(
            long projectId,
            string stationCode,
            List<PLC_LogicUnitAstEntity> astRows)
        {
            // 取全部影子信号（不在 DB 层过滤 AnchorType，PostgreSQL 大小写敏感可能漏匹配）
            var allShadowSignals = await _stationShadowSignalService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == projectId
                && it.StationCode == stationCode);

            var graphSignals = allShadowSignals?
                .Where(s => string.Equals(s.AnchorType?.Trim(), GraphActionAnchorType, StringComparison.OrdinalIgnoreCase)
                    && s.DiagnosisExcluded != true
                    && !string.IsNullOrWhiteSpace(s.VariableName))
                .ToList();

            await WriteLogHelper.WriteLogsAsync(
                $"[GetActiveGraphCandidates] {stationCode} allSignals={allShadowSignals?.Count ?? 0} graphSignals={graphSignals?.Count ?? 0}",
                "LineDiagnosis/GraphCheck");

            if (graphSignals == null || graphSignals.Count == 0)
                return new List<StallCandidateInfo>();

            var symbolPaths = graphSignals
                .Select(s => s.VariableName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var graphVarNameSet = new HashSet<string>(symbolPaths, StringComparer.OrdinalIgnoreCase);
            var sampledGraphValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            List<string> trueVarNames;
            try
            {
                var swRead = System.Diagnostics.Stopwatch.StartNew();
                var snapshot = await _signalSnapshotService.AcquireAsync(new AcquireDiagnosticSnapshotRequestDto
                {
                    ProjectId = projectId,
                    StationCode = stationCode,
                    SymbolPaths = symbolPaths,
                    IncludeStationAnchorVariables = false,
                });
                swRead.Stop();

                var values = snapshot?.ExecutionContext?.CurrentValues;
                if (values != null)
                {
                    foreach (var kv in values)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key) && graphVarNameSet.Contains(kv.Key.Trim()))
                            sampledGraphValues[kv.Key.Trim()] = kv.Value;
                    }
                }

                // 构建变量名 → 激活规则的快查字典（key 不区分大小写）
                var ruleByVar = graphSignals
                    .Where(s => !string.IsNullOrWhiteSpace(s.VariableName))
                    .GroupBy(s => s.VariableName!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().ActivationRule, StringComparer.OrdinalIgnoreCase);

                // Phase 1: 使用 activation_rule 判断激活状态（无规则时等同 BOOL_TRUE）
                trueVarNames = values?
                    .Where(kv =>
                    {
                        ruleByVar.TryGetValue(kv.Key, out var rule);
                        return GraphActivationRuleInferService.EvaluateRule(rule, kv.Value);
                    })
                    .Select(kv => kv.Key)
                    .ToList()
                    ?? new List<string>();

                await WriteLogHelper.WriteLogsAsync(
                    $"[GetActiveGraphCandidates] {stationCode} IoT读取耗时{swRead.ElapsedMilliseconds}ms " +
                    $"symbolCount={symbolPaths.Count} valueCount={values?.Count ?? 0} " +
                    $"activatedCount={trueVarNames.Count} activatedVars=[{string.Join(",", trueVarNames.Take(5))}]",
                    "LineDiagnosis/GraphCheck");
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[GetActiveGraphCandidates] {stationCode} IoT读取异常，降级到镜像: {ex.Message}",
                    "LineDiagnosis/GraphCheck");
                // IoT 读取失败 → 降级到 Redis 镜像，只能判断有无（无法构建候选）
                var hasByMirror = await _executionSuccessorChainService.HasActiveGraphAnchorsAsync(projectId, stationCode);
                return hasByMirror
                    ? BuildGraphCandidatesFromSignals(graphSignals, graphSignals, astRows)  // 镜像降级：active=all（无法区分）
                    : new List<StallCandidateInfo>();
            }

            // Phase 2: 若实时 IoT 未能过滤出激活信号，回退到镜像快照。
            // 同样使用 activation_rule 判断（无规则时等同 BOOL_TRUE 保守策略）。
            // 镜像由监控服务持续刷新，IoT 短暂不可用时仍保留最后已知激活值。
            if (trueVarNames.Count == 0)
            {
                var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, stationCode);
                if (mirrors != null)
                {
                    var ruleByVarMirror = graphSignals
                        .Where(s => !string.IsNullOrWhiteSpace(s.VariableName))
                        .GroupBy(s => s.VariableName!.Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().ActivationRule, StringComparer.OrdinalIgnoreCase);

                    foreach (var mirror in mirrors.Values)
                    {
                        if (!string.IsNullOrWhiteSpace(mirror?.VariableName)
                            && graphVarNameSet.Contains(mirror.VariableName.Trim()))
                        {
                            sampledGraphValues[mirror.VariableName.Trim()] = mirror.CurrentValue;
                        }
                    }

                    var mirrorActivated = mirrors.Values
                        .Where(m => !string.IsNullOrWhiteSpace(m.VariableName)
                            && graphVarNameSet.Contains(m.VariableName.Trim()))
                        .Where(m =>
                        {
                            ruleByVarMirror.TryGetValue(m.VariableName!.Trim(), out var rule);
                            return GraphActivationRuleInferService.EvaluateRule(rule, m.CurrentValue);
                        })
                        .Select(m => m.VariableName!.Trim())
                        .ToList();

                    if (mirrorActivated.Count > 0)
                    {
                        trueVarNames = mirrorActivated;
                        await WriteLogHelper.WriteLogsAsync(
                            $"[GetActiveGraphCandidates] {stationCode} 实时无激活，镜像快照规则兜底: [{string.Join(",", mirrorActivated)}]",
                            "LineDiagnosis/GraphCheck");
                    }
                }
            }

            if (trueVarNames.Count == 0)
            {
                // 2026-09-23 先看 IGW 实时读的 EXEC_BITS（PLC 里的步激活位，最直接的"当前哪步在激活"），
                // 命中就直接用，不再去查信号历史表推断"最近激活过"。历史表查询曾让一个工位的
                // GraphActiveCheck 耗时 34~44 秒，而 EXEC_BITS 实时读取在 IGW 侧 p50 约 21 ms。
                // 实时 EXEC_BITS 也没有激活步时，才退回下面的历史推断（行为与原先一致）。
                var liveExecBitCandidates = await GetActiveGraphExecBitFallbackCandidatesAsync(
                    projectId, stationCode, astRows, allShadowSignals, allowHistoryFallback: false);
                if (liveExecBitCandidates.Count > 0)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GetActiveGraphCandidates] {stationCode} GRAPH 动作信号无激活，实时 EXEC_BITS 命中 {liveExecBitCandidates.Count} 个候选，跳过历史推断",
                        "LineDiagnosis/GraphCheck");
                    return liveExecBitCandidates;
                }

                trueVarNames = await ResolveRecentlyActivatedGraphVariablesAsync(projectId, stationCode, graphSignals);
                if (trueVarNames.Count > 0)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GetActiveGraphCandidates] {stationCode} realtime/mirror no active, recent GRAPH change fallback: [{string.Join(",", trueVarNames.Take(8))}]",
                        "LineDiagnosis/GraphCheck");
                }
            }

            if (trueVarNames.Count == 0)
            {
                await LogInactiveGraphEvaluationSamplesAsync(stationCode, graphSignals, sampledGraphValues);
                var execBitFallback = await GetActiveGraphExecBitFallbackCandidatesAsync(
                    projectId, stationCode, astRows, allShadowSignals);
                return execBitFallback;
            }

            var confirmedActivationTimeByVar = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            // ── 长时间激活过滤 ──
            // 目的：产线停机时，某些 GRAPH 步骤（如等待步骤、机器人原点步）可能在停机前很久
            // 就已激活，并非本次停机触发的故障原因。
            // 策略：只在存在“开机常 True / 缺少布尔变化证据”的强证据时过滤。
            // 不再按激活信号之间的相对 LastChangeTime 裁剪，避免停机很久后误删真实卡滞步骤。
            const int LongActiveEvidenceSampleCount = 1000;
            const int LongActiveHistorySampleCount = 1000;
            try
            {
                var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, stationCode);
                if (mirrors?.Count > 0)
                {
                    // 构建 VariableName → LastChangeTime 快查表（镜像 key 是 SignalKey，需遍历 Values）
                    var changeTimeByVar = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in mirrors.Values)
                    {
                        if (!string.IsNullOrWhiteSpace(m?.VariableName))
                            changeTimeByVar[m.VariableName.Trim()] = m.LastChangeTime;
                    }

                    var activeVarSet = new HashSet<string>(
                        trueVarNames.Select(v => v?.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)),
                        StringComparer.OrdinalIgnoreCase);
                    var activeVarList = activeVarSet.ToList();

                    var evidenceChanges = new List<StationShadowChangeCacheModel>();
                    var recentChangesForEvidence = await _stationShadowStateCacheService
                        .GetRecentChangesAsync(projectId, stationCode, LongActiveEvidenceSampleCount);
                    if (recentChangesForEvidence != null)
                    {
                        evidenceChanges.AddRange(recentChangesForEvidence.Where(c => !IsStringSignalChange(c)));
                    }

                    var historyQuerySw = System.Diagnostics.Stopwatch.StartNew();
                    var historyRows = activeVarList.Count == 0
                        ? new List<PLC_StationSignalHistoryEntity>()
                        : await _stationSignalHistoryService.GetDb().AsQueryable()
                            .Where(it => it.IsDeleted == false
                                && it.ProjectId == projectId
                                && it.StationCode == stationCode
                                && it.ChangedAt != null
                                && activeVarList.Contains(it.VariableName))
                            .OrderBy(it => it.ChangedAt, OrderByType.Desc)
                            .OrderBy(it => it.Id, OrderByType.Desc)
                            .Take(LongActiveHistorySampleCount)
                            .ToListAsync();
                    StationSubTimings.Add("HistoryQuerySum", historyQuerySw.ElapsedMilliseconds);

                    if (historyRows != null && historyRows.Count > 0)
                    {
                        evidenceChanges.AddRange(historyRows
                            .Where(h => !string.IsNullOrWhiteSpace(h.VariableName))
                            .Select(h => new StationShadowChangeCacheModel
                            {
                                SignalKey = h.SignalKey,
                                VariableName = h.VariableName,
                                OldValue = h.OldValue,
                                NewValue = h.NewValue,
                                ChangedAt = h.ChangedAt,
                                StationMode = h.StationMode,
                                GroupName = h.GroupName,
                                SignalType = h.SignalType,
                                Quality = h.Quality,
                                LogicUnitId = h.LogicUnitId
                            })
                            .Where(c => !IsStringSignalChange(c)));
                    }

                    var latestStationChange = evidenceChanges
                        .Where(c => c.ChangedAt.HasValue)
                        .Select(c => c.ChangedAt!.Value)
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max();

                    var changeEvidenceByVar = evidenceChanges
                        .Where(c =>
                        {
                            var varName = !string.IsNullOrWhiteSpace(c.VariableName)
                                ? c.VariableName.Trim()
                                : c.SignalKey?.Trim();
                            return !string.IsNullOrWhiteSpace(varName) && activeVarSet.Contains(varName);
                        })
                        .GroupBy(c => (!string.IsNullOrWhiteSpace(c.VariableName) ? c.VariableName.Trim() : c.SignalKey?.Trim())!,
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                    confirmedActivationTimeByVar = evidenceChanges
                        .Where(c => c.ChangedAt.HasValue && IsConfirmedSetTrueTransition(c))
                        .Select(c => new
                        {
                            VarName = !string.IsNullOrWhiteSpace(c.VariableName)
                                ? c.VariableName.Trim()
                                : c.SignalKey?.Trim(),
                            ChangedAt = c.ChangedAt!.Value
                        })
                        .Where(x => !string.IsNullOrWhiteSpace(x.VarName) && activeVarSet.Contains(x.VarName))
                        .GroupBy(x => x.VarName!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Max(x => x.ChangedAt), StringComparer.OrdinalIgnoreCase);

                    if (trueVarNames.Count > 0)
                    {
                        var originalTrueVarCount = trueVarNames.Count;
                        var filtered = new List<string>();
                        var suppressed = new List<string>();
                        foreach (var varName in trueVarNames.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            changeTimeByVar.TryGetValue(varName, out var lastChangeTime);
                            if (ShouldSuppressLongActiveGraphSignal(
                                varName,
                                lastChangeTime,
                                changeEvidenceByVar,
                                latestStationChange,
                                out var suppressReason))
                            {
                                suppressed.Add($"{varName}({suppressReason})");
                            }
                            else
                            {
                                filtered.Add(varName);
                            }
                        }

                        var filterDecision = GraphActiveCandidateRetentionPolicy.Apply(
                            trueVarNames,
                            filtered,
                            suppressed.Count);

                        if (filterDecision.FilterWouldClearAll)
                        {
                            await WriteLogHelper.WriteLogsAsync(
                                $"[GetActiveGraphCandidates] {stationCode} 长时间激活证据过滤未应用: " +
                                $"过滤将清空所有{trueVarNames.Count}个GRAPH候选，已保留原始激活候选。 " +
                                $"可疑=[{string.Join(",", suppressed.Take(8))}]，" +
                                $"工位最新变化={latestStationChange:HH:mm:ss}",
                                "LineDiagnosis/GraphCheck");
                        }
                        else if (filterDecision.FilterApplied)
                        {
                            await WriteLogHelper.WriteLogsAsync(
                                $"[GetActiveGraphCandidates] {stationCode} long-active filter applied: " +
                                $"original={originalTrueVarCount} kept={filterDecision.Candidates.Count} " +
                                $"suppressed=[{string.Join(",", suppressed.Take(8))}]",
                                "LineDiagnosis/GraphCheck");
                        }

                        if (suppressed.Count > 0)
                        {
                            trueVarNames = filterDecision.Candidates.ToList();
                            await WriteLogHelper.WriteLogsAsync(
                                $"[GetActiveGraphCandidates] {stationCode} long-active filter final: " +
                                $"original={originalTrueVarCount}, kept={trueVarNames.Count}, " +
                                $"suppressed=[{string.Join(",", suppressed.Take(8))}]",
                                "LineDiagnosis/GraphCheck");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[GetActiveGraphCandidates] {stationCode} 长时间激活过滤异常（不影响主流程）: {ex.Message}",
                    "LineDiagnosis/GraphCheck");
                // 异常时不过滤，直接使用原始 trueVarNames
            }

            // 只保留激活的 GraphAction 信号
            if (trueVarNames.Count == 0)
            {
                await LogInactiveGraphEvaluationSamplesAsync(stationCode, graphSignals, sampledGraphValues);
                return new List<StallCandidateInfo>();
            }

            var activeSignals = graphSignals
                .Where(s => trueVarNames.Any(v => string.Equals(v, s.VariableName?.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // 传入全量 graphSignals 作为分母，按 LogicUnitId 收敛计算 MatchRatio
            var filteredActiveSignals = FilterGraphSignalsForDiagnosis(activeSignals);
            if (filteredActiveSignals.Count < activeSignals.Count)
            {
                var suppressed = activeSignals
                    .Except(filteredActiveSignals)
                    .Select(s => $"{s.VariableName}({s.GraphStepRole ?? "Unknown"}/{s.GraphStepRoleConfidence?.ToString("0.00") ?? "-"}/{s.ActivationRule ?? "BOOL_TRUE"})")
                    .Take(10)
                    .ToList();

                await WriteLogHelper.WriteLogsAsync(
                    $"[GetActiveGraphCandidates] {stationCode} graph role filter: original={activeSignals.Count}, kept={filteredActiveSignals.Count}, suppressed=[{string.Join(",", suppressed)}]",
                    "LineDiagnosis/GraphCheck");
            }

            if (filteredActiveSignals.Count == 0)
            {
                await LogInactiveGraphEvaluationSamplesAsync(stationCode, graphSignals, sampledGraphValues);
                var execBitFallback = await GetActiveGraphExecBitFallbackCandidatesAsync(
                    projectId, stationCode, astRows, allShadowSignals);
                return execBitFallback;
            }

            return BuildGraphCandidatesFromSignals(filteredActiveSignals, graphSignals, astRows, confirmedActivationTimeByVar);
        }

        private async Task<List<StallCandidateInfo>> GetActiveGraphExecBitFallbackCandidatesAsync(
            long projectId,
            string stationCode,
            List<PLC_LogicUnitAstEntity> astRows,
            List<PLC_StationShadowSignalEntity> allShadowSignals,
            bool allowHistoryFallback = true)
        {
            // 2026-10-14：实例 DB 名明确写着别的工位（如 OP010 下挂了 DB310_OP030_Graph 的步位）的不算本工位——
            // 同一 GRAPH 模板 FB 的全部实例以前会被播进 FB 所在工位，读到别的工位卡住的步会把故障记到本工位
            var execBitSignals = allShadowSignals?
                .Where(signal => IsGraphExecBitSignal(signal)
                    && signal.DiagnosisExcluded != true
                    && !string.IsNullOrWhiteSpace(signal.VariableName)
                    && StationBlockBindingRules.ExecBitBelongsToStation(signal.VariableName, stationCode, null))
                .ToList() ?? new List<PLC_StationShadowSignalEntity>();

            if (execBitSignals.Count == 0)
                return new List<StallCandidateInfo>();

            var symbolPaths = execBitSignals
                .Select(signal => signal.VariableName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var symbolPathSet = new HashSet<string>(symbolPaths, StringComparer.OrdinalIgnoreCase);
            var sampledValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var confirmedActivationTimeByVar = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var trueVarNames = new List<string>();

            try
            {
                var snapshot = await _signalSnapshotService.AcquireAsync(new AcquireDiagnosticSnapshotRequestDto
                {
                    ProjectId = projectId,
                    StationCode = stationCode,
                    SymbolPaths = symbolPaths,
                    IncludeStationAnchorVariables = false,
                });

                var ruleByVar = execBitSignals
                    .GroupBy(signal => signal.VariableName!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().ActivationRule, StringComparer.OrdinalIgnoreCase);

                foreach (var kv in snapshot?.ExecutionContext?.CurrentValues ?? new Dictionary<string, object>())
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && symbolPathSet.Contains(kv.Key.Trim()))
                        sampledValues[kv.Key.Trim()] = kv.Value;
                }

                trueVarNames = snapshot?.ExecutionContext?.CurrentValues?
                    .Where(kv =>
                    {
                        ruleByVar.TryGetValue(kv.Key, out var rule);
                        return symbolPathSet.Contains(kv.Key) && GraphActivationRuleInferService.EvaluateRule(rule, kv.Value);
                    })
                    .Select(kv => kv.Key.Trim())
                    .ToList()
                    ?? new List<string>();
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[GetActiveGraphCandidates] {stationCode} EXEC_BITS候补实时读取异常，降级镜像: {ex.Message}",
                    "LineDiagnosis/GraphExecBits");
            }

            var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, stationCode);
            if (mirrors != null)
            {
                var ruleByVarMirror = execBitSignals
                    .GroupBy(signal => signal.VariableName!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().ActivationRule, StringComparer.OrdinalIgnoreCase);

                foreach (var mirror in mirrors.Values)
                {
                    if (string.IsNullOrWhiteSpace(mirror?.VariableName))
                        continue;

                    var varName = mirror.VariableName.Trim();
                    if (!symbolPathSet.Contains(varName))
                        continue;

                    sampledValues[varName] = mirror.CurrentValue;
                    if (mirror.LastChangeTime.HasValue)
                        confirmedActivationTimeByVar[varName] = mirror.LastChangeTime.Value;
                }

                if (trueVarNames.Count == 0)
                {
                    trueVarNames = mirrors.Values
                        .Where(mirror => !string.IsNullOrWhiteSpace(mirror?.VariableName)
                            && symbolPathSet.Contains(mirror.VariableName.Trim()))
                        .Where(mirror =>
                        {
                            var varName = mirror.VariableName!.Trim();
                            ruleByVarMirror.TryGetValue(varName, out var rule);
                            return GraphActivationRuleInferService.EvaluateRule(rule, mirror.CurrentValue);
                        })
                        .Select(mirror => mirror.VariableName!.Trim())
                        .ToList();
                }
            }

            if (trueVarNames.Count == 0 && !allowHistoryFallback)
            {
                // 只看实时值 / 镜像的调用：没有激活步就交给调用方决定下一步，不查历史表
                return new List<StallCandidateInfo>();
            }

            if (trueVarNames.Count == 0)
            {
                trueVarNames = await ResolveRecentlyActivatedGraphVariablesAsync(projectId, stationCode, execBitSignals);
            }

            if (trueVarNames.Count == 0)
            {
                await LogInactiveGraphEvaluationSamplesAsync(stationCode, execBitSignals, sampledValues);
                return new List<StallCandidateInfo>();
            }

            var activeSignals = execBitSignals
                .Where(signal => trueVarNames.Any(varName =>
                    string.Equals(varName, signal.VariableName?.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var filteredActiveSignals = FilterGraphSignalsForDiagnosis(activeSignals);

            if (filteredActiveSignals.Count < activeSignals.Count)
            {
                var suppressed = activeSignals
                    .Except(filteredActiveSignals)
                    .Select(signal => $"{signal.VariableName}({signal.GraphStepRole ?? "Unknown"}/{signal.GraphStepRoleConfidence?.ToString("0.00") ?? "-"})")
                    .Take(10)
                    .ToList();
                await WriteLogHelper.WriteLogsAsync(
                    $"[GetActiveGraphCandidates] {stationCode} EXEC_BITS候补角色过滤: original={activeSignals.Count}, kept={filteredActiveSignals.Count}, suppressed=[{string.Join(",", suppressed)}]",
                    "LineDiagnosis/GraphExecBits");
            }

            if (filteredActiveSignals.Count == 0)
                return new List<StallCandidateInfo>();

            var candidates = BuildGraphCandidatesFromSignals(
                    filteredActiveSignals,
                    execBitSignals,
                    astRows,
                    confirmedActivationTimeByVar)
                .OrderByDescending(candidate => candidate.GraphCandidateRank)
                .ThenByDescending(candidate => candidate.LastTrueAt ?? DateTime.MinValue)
                .ThenByDescending(candidate => candidate.MatchRatio)
                .ThenByDescending(candidate => candidate.ActiveSignalCount)
                .ToList();

            await WriteLogHelper.WriteLogsAsync(
                $"[GetActiveGraphCandidates] {stationCode} EXEC_BITS候补命中: active=[{string.Join(",", trueVarNames.Take(8))}], candidates={candidates.Count}",
                "LineDiagnosis/GraphExecBits");

            return candidates;
        }

        private async Task<List<string>> ResolveRecentlyActivatedGraphVariablesAsync(
            long projectId,
            string stationCode,
            List<PLC_StationShadowSignalEntity> graphSignals)
        {
            if (graphSignals == null || graphSignals.Count == 0)
                return new List<string>();

            var graphVarNameSet = new HashSet<string>(
                graphSignals
                    .Select(s => s.VariableName?.Trim())
                    .Where(v => !string.IsNullOrWhiteSpace(v)),
                StringComparer.OrdinalIgnoreCase);
            if (graphVarNameSet.Count == 0)
                return new List<string>();

            var ruleByVar = graphSignals
                .Where(s => !string.IsNullOrWhiteSpace(s.VariableName))
                .GroupBy(s => s.VariableName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().ActivationRule, StringComparer.OrdinalIgnoreCase);

            var evidenceChanges = new List<StationShadowChangeCacheModel>();
            var recentChanges = await _stationShadowStateCacheService.GetRecentChangesAsync(projectId, stationCode, 500);
            if (recentChanges != null && recentChanges.Count > 0)
                evidenceChanges.AddRange(recentChanges);

            try
            {
                var graphVarNames = graphVarNameSet.ToList();
                var historyQuerySw = System.Diagnostics.Stopwatch.StartNew();
                var historyRows = await _stationSignalHistoryService.GetDb().AsQueryable()
                    .Where(it => it.IsDeleted == false
                        && it.ProjectId == projectId
                        && it.StationCode == stationCode
                        && it.ChangedAt != null
                        && graphVarNames.Contains(it.VariableName))
                    .OrderBy(it => it.ChangedAt, OrderByType.Desc)
                    .OrderBy(it => it.Id, OrderByType.Desc)
                    .Take(500)
                    .ToListAsync();
                StationSubTimings.Add("HistoryQuerySum", historyQuerySw.ElapsedMilliseconds);

                if (historyRows != null && historyRows.Count > 0)
                {
                    evidenceChanges.AddRange(historyRows
                        .Where(h => !string.IsNullOrWhiteSpace(h.VariableName))
                        .Select(h => new StationShadowChangeCacheModel
                        {
                            SignalKey = h.SignalKey,
                            VariableName = h.VariableName,
                            OldValue = h.OldValue,
                            NewValue = h.NewValue,
                            ChangedAt = h.ChangedAt,
                            StationMode = h.StationMode,
                            GroupName = h.GroupName,
                            SignalType = h.SignalType,
                            Quality = h.Quality,
                            LogicUnitId = h.LogicUnitId
                        }));
                }
            }
            catch (Exception ex)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[GetActiveGraphCandidates] {stationCode} recent GRAPH history fallback query failed: {ex.Message}",
                    "LineDiagnosis/GraphCheck");
            }

            if (evidenceChanges.Count == 0)
                return new List<string>();

            var windowStart = DateTime.Now.AddMinutes(-30);
            var windowedChanges = evidenceChanges
                .Where(c => c.ChangedAt.HasValue && c.ChangedAt.Value >= windowStart)
                .ToList();
            var effectiveChanges = (windowedChanges.Count > 0
                    ? windowedChanges
                    : evidenceChanges.Where(c => c.ChangedAt.HasValue).Take(200))
                .OrderByDescending(c => c.ChangedAt ?? DateTime.MinValue)
                .ToList();

            var latestActiveByVar = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in effectiveChanges)
            {
                var varName = !string.IsNullOrWhiteSpace(change.VariableName)
                    ? change.VariableName.Trim()
                    : change.SignalKey?.Trim();
                if (string.IsNullOrWhiteSpace(varName) || !graphVarNameSet.Contains(varName))
                    continue;
                if (latestActiveByVar.ContainsKey(varName))
                    continue;

                ruleByVar.TryGetValue(varName, out var rule);
                if (IsStringSignalChange(change) && !IsStringActivationRule(rule))
                    continue;

                latestActiveByVar[varName] = GraphActivationRuleInferService.EvaluateRule(rule, change.NewValue);
            }

            var activated = latestActiveByVar
                .Where(kv => kv.Value)
                .Select(kv => kv.Key)
                .Take(50)
                .ToList();

            if (activated.Count > 0)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[GetActiveGraphCandidates] {stationCode} recent GRAPH fallback matched count={activated.Count} effectiveChanges={effectiveChanges.Count}",
                    "LineDiagnosis/GraphCheck");
            }

            return activated;
        }

        private async Task LogInactiveGraphEvaluationSamplesAsync(
            string stationCode,
            List<PLC_StationShadowSignalEntity> graphSignals,
            IReadOnlyDictionary<string, object> sampledGraphValues)
        {
            if (graphSignals == null || graphSignals.Count == 0)
                return;

            var sampleItems = graphSignals
                .Where(s => !string.IsNullOrWhiteSpace(s.VariableName))
                .GroupBy(s => s.VariableName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var signal = g.First();
                    var varName = g.Key;
                    object value = null;
                    sampledGraphValues?.TryGetValue(varName, out value);
                    return $"{varName}|rule={signal.ActivationRule ?? "BOOL_TRUE"}|value={FormatGraphLogValue(value)}";
                })
                .Take(12)
                .ToList();

            await WriteLogHelper.WriteLogsAsync(
                $"[GetActiveGraphCandidates] {stationCode} no active GRAPH after realtime/mirror/recent checks. samples=[{string.Join("; ", sampleItems)}]",
                "LineDiagnosis/GraphCheck");
        }

        private static bool IsStringActivationRule(string rule)
            => !string.IsNullOrWhiteSpace(rule)
                && (rule.Trim().StartsWith("STRING", StringComparison.OrdinalIgnoreCase)
                    || rule.Trim().StartsWith("WSTRING", StringComparison.OrdinalIgnoreCase));

        private static List<PLC_StationShadowSignalEntity> FilterGraphSignalsForDiagnosis(
            List<PLC_StationShadowSignalEntity> activeSignals)
        {
            if (activeSignals == null || activeSignals.Count == 0)
                return new List<PLC_StationShadowSignalEntity>();

            return activeSignals
                .Where(signal => !ShouldSuppressGraphSignalByRole(signal))
                .GroupBy(
                    signal => signal.LogicUnitId.HasValue
                        ? $"lu:{signal.LogicUnitId.Value}"
                        : $"var:{signal.VariableName?.Trim()}",
                    StringComparer.OrdinalIgnoreCase)
                .SelectMany(group =>
                {
                    var signals = group
                        .Where(signal => signal != null && !string.IsNullOrWhiteSpace(signal.VariableName))
                        .ToList();
                    var nonStringSignals = signals
                        .Where(signal => !IsStringActivationRule(signal.ActivationRule))
                        .ToList();

                    if (nonStringSignals.Count > 0)
                        return nonStringSignals;

                    return signals.Where(IsHighConfidenceWorkStep);
                })
                .ToList();
        }

        private static bool ShouldSuppressGraphSignalByRole(PLC_StationShadowSignalEntity signal)
        {
            var role = signal?.GraphStepRole?.Trim();
            if (string.IsNullOrWhiteSpace(role) || (signal.GraphStepRoleConfidence ?? 0m) < 0.80m)
                return false;

            return role.Equals("InitStep", StringComparison.OrdinalIgnoreCase)
                || role.Equals("IdleStep", StringComparison.OrdinalIgnoreCase)
                || role.Equals("LongWaitStep", StringComparison.OrdinalIgnoreCase)
                || role.Equals("AlarmHandling", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHighConfidenceWorkStep(PLC_StationShadowSignalEntity signal)
        {
            return string.Equals(signal?.GraphStepRole?.Trim(), "WorkStep", StringComparison.OrdinalIgnoreCase)
                && (signal.GraphStepRoleConfidence ?? 0m) >= 0.85m;
        }

        private static int ResolveGraphSignalCandidateRank(PLC_StationShadowSignalEntity signal)
        {
            var role = signal?.GraphStepRole?.Trim();
            var confidence = signal?.GraphStepRoleConfidence ?? 0m;
            if (string.IsNullOrWhiteSpace(role) || confidence < 0.80m)
                return 20;

            if (role.Equals("WorkStep", StringComparison.OrdinalIgnoreCase))
                return 30;
            if (role.Equals("WaitUpstream", StringComparison.OrdinalIgnoreCase)
                || role.Equals("WaitDownstream", StringComparison.OrdinalIgnoreCase))
                return 10;
            if (role.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return 20;

            return 0;
        }

        private static int ResolveDirectCandidatePriority(StallCandidateInfo candidate)
        {
            if (candidate?.IsDeviceFocus == true)
                return 40;
            if (candidate?.IsGraphAction == true)
                return candidate.GraphCandidateRank;
            return 0;
        }

        private static string FormatGraphLogValue(object value)
        {
            if (value == null)
                return "<null>";

            var text = value.ToString()?.Trim();
            if (string.IsNullOrEmpty(text))
                return "<empty>";

            return text.Length <= 48 ? text : text.Substring(0, 48) + "...";
        }

        /// <summary>
        /// 将 GraphAction 激活信号按 LogicUnitId（GRAPH 步骤块）收敛，计算每个步骤的信号匹配比例，
        /// 返回按 MatchRatio 降序排列的候选列表（一个步骤块一个候选）。
        ///
        /// MatchRatio = 该步骤当前激活信号数 / 该步骤全部注册信号数。
        /// 比例越高说明"停在这个步骤"的证据越充分，有效过滤掉跨步骤共用信号带来的误判。
        /// </summary>
        private List<StallCandidateInfo> BuildGraphCandidatesFromSignals(
            List<PLC_StationShadowSignalEntity> activeSignals,
            List<PLC_StationShadowSignalEntity> allGraphSignals,
            List<PLC_LogicUnitAstEntity> astRows,
            IReadOnlyDictionary<string, DateTime> confirmedActivationTimeByVar = null)
        {
            // ── 有 LogicUnitId 的：按步骤块收敛，计算 MatchRatio ──
            var diagnosticGraphSignals = FilterGraphSignalsForDiagnosis(allGraphSignals);

            var withUnit = activeSignals
                .Where(s => s.LogicUnitId.HasValue && !string.IsNullOrWhiteSpace(s.VariableName))
                .GroupBy(s => s.LogicUnitId!.Value)
                .Select(g =>
                {
                    // 该 LogicUnit 全部注册信号数（分母）
                    var totalInUnit = diagnosticGraphSignals.Count(s =>
                        s.LogicUnitId == g.Key && !string.IsNullOrWhiteSpace(s.VariableName));
                    var activeInUnit = g.Count();
                    var ratio = totalInUnit > 0 ? (double)activeInUnit / totalInUnit : 1.0;

                    // 代表信号选取优先级：
                    // 1. BOOL_TRUE 信号（最能代表步骤激活状态）
                    // 2. 非 STRING 类型的信号（INT/WORD/BOOL 等）
                    // 3. SemanticLabel 最长的（语义最丰富）
                    // 避免 STRING/WSTRING 类型信号被选为代表（其 AST 反查会找到上报 logic 而非 GRAPH Step）
                    var repSignal = g
                        .OrderByDescending(s => s.ActivationRule != null &&
                            s.ActivationRule.StartsWith("BOOL_TRUE", StringComparison.OrdinalIgnoreCase) ? 2 :
                            (s.ActivationRule == null || (!s.ActivationRule.StartsWith("WSTRING", StringComparison.OrdinalIgnoreCase)
                                && !s.ActivationRule.StartsWith("STRING", StringComparison.OrdinalIgnoreCase))) ? 1 : 0)
                        .ThenByDescending(s => s.SemanticLabel?.Length ?? 0)
                        .First();
                    var isExecBitFallback = IsGraphExecBitSignal(repSignal);
                    var varName = repSignal.VariableName!.Trim();
                    var astMatch = repSignal.LogicUnitId.HasValue
                        ? astRows.FirstOrDefault(row => row.LogicUnitId == repSignal.LogicUnitId.Value)
                        : null;
                    astMatch ??= FindBestRuntimeAstOutputMatch(varName, astRows);
                    var label = repSignal.SemanticLabel ?? varName;
                    var allActiveNames = string.Join(",", g.Take(5).Select(s => s.VariableName!.Trim()));
                    var lastTrueAt = ResolveLatestConfirmedGraphActivationAt(
                        g.Select(s => s.VariableName),
                        confirmedActivationTimeByVar);
                    var prefix = isExecBitFallback ? "GRAPH EXEC_BITS候补" : "GRAPH步骤";

                    return new StallCandidateInfo
                    {
                        VariableName = varName,
                        SignalKey = $"[{prefix} {activeInUnit}/{totalInUnit}] {label}",
                        ChangedAt = lastTrueAt ?? DateTime.MinValue,
                        LastTrueAt = lastTrueAt,
                        LogicUnitId = astMatch?.LogicUnitId ?? repSignal.LogicUnitId,
                        BlockName = astMatch?.BlockName,
                        DisplayLabel = $"[{prefix} {activeInUnit}/{totalInUnit}] {label}",
                        UnitType = astMatch?.UnitType,
                        IsGraphAction = true,
                        IsGraphExecBitFallback = isExecBitFallback,
                        AnchorType = repSignal.AnchorType,
                        ActivationDirection = "SetTrue",
                        MatchRatio = ratio,
                        ActiveSignalCount = activeInUnit,
                        TotalSignalCount = totalInUnit,
                        GraphStepRole = repSignal.GraphStepRole,
                        GraphStepRoleConfidence = repSignal.GraphStepRoleConfidence,
                        GraphStepRoleReason = repSignal.GraphStepRoleReason,
                        GraphCandidateRank = ResolveGraphSignalCandidateRank(repSignal),
                    };
                })
                .OrderByDescending(c => c.GraphCandidateRank)
                .ThenByDescending(c => c.MatchRatio)
                .ThenByDescending(c => c.ActiveSignalCount)
                .ToList();

            // ── 无 LogicUnitId 的：保持一变量一候选（无法按步骤收敛）──
            var withoutUnit = activeSignals
                .Where(s => !s.LogicUnitId.HasValue && !string.IsNullOrWhiteSpace(s.VariableName))
                .GroupBy(s => s.VariableName!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var signal = g.First();
                    var isExecBitFallback = IsGraphExecBitSignal(signal);
                    var varName = signal.VariableName!.Trim();
                    var astMatch = signal.LogicUnitId.HasValue
                        ? astRows.FirstOrDefault(row => row.LogicUnitId == signal.LogicUnitId.Value)
                        : null;
                    astMatch ??= FindBestRuntimeAstOutputMatch(varName, astRows);
                    var label = signal.SemanticLabel ?? varName;
                    var lastTrueAt = ResolveLatestConfirmedGraphActivationAt(
                        new[] { varName },
                        confirmedActivationTimeByVar);
                    var prefix = isExecBitFallback ? "GRAPH EXEC_BITS候补" : "GRAPH步骤";
                    return new StallCandidateInfo
                    {
                        VariableName = varName,
                        SignalKey = $"[{prefix}] {label}",
                        ChangedAt = lastTrueAt ?? DateTime.MinValue,
                        LastTrueAt = lastTrueAt,
                        LogicUnitId = astMatch?.LogicUnitId,
                        BlockName = astMatch?.BlockName,
                        DisplayLabel = $"[{prefix}] {label}",
                        UnitType = astMatch?.UnitType,
                        IsGraphAction = true,
                        IsGraphExecBitFallback = isExecBitFallback,
                        AnchorType = signal.AnchorType,
                        ActivationDirection = "SetTrue",
                        MatchRatio = 1.0,
                        ActiveSignalCount = 1,
                        TotalSignalCount = 1,
                        GraphStepRole = signal.GraphStepRole,
                        GraphStepRoleConfidence = signal.GraphStepRoleConfidence,
                        GraphStepRoleReason = signal.GraphStepRoleReason,
                        GraphCandidateRank = ResolveGraphSignalCandidateRank(signal),
                    };
                })
                .ToList();

            return withUnit.Concat(withoutUnit).ToList();
        }

        private static DateTime? ResolveLatestConfirmedGraphActivationAt(
            IEnumerable<string> variableNames,
            IReadOnlyDictionary<string, DateTime> confirmedActivationTimeByVar)
        {
            if (variableNames == null || confirmedActivationTimeByVar == null || confirmedActivationTimeByVar.Count == 0)
                return null;

            DateTime? latest = null;
            foreach (var variableName in variableNames)
            {
                var key = variableName?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!confirmedActivationTimeByVar.TryGetValue(key, out var changedAt) || changedAt <= DateTime.MinValue)
                    continue;

                if (!latest.HasValue || changedAt > latest.Value)
                    latest = changedAt;
            }

            return latest;
        }

        private static DateTime? ResolveFindingLastTrueAt(StallCandidateInfo candidate)
        {
            if (candidate == null)
                return null;

            if (candidate.LastTrueAt.HasValue)
                return candidate.LastTrueAt;

            return candidate.IsGraphAction ? null : candidate.ChangedAt;
        }

        private async Task<List<StallCandidateInfo>> LocateSuccessorChainCandidatesAsync(
            long projectId,
            string stationCode,
            List<PLC_LogicUnitAstEntity> astRows)
        {
            var successorCandidates = await _executionSuccessorChainService.QueryCandidatesAsync(
                projectId,
                stationCode,
                maxDepth: 3,
                maxCandidates: 12);

            if (successorCandidates == null || successorCandidates.Count == 0)
            {
                return new List<StallCandidateInfo>();
            }

            return successorCandidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    AstMatch = FindBestRuntimeAstOutputMatch(candidate.VariableName, astRows)
                })
                .Where(item => item.AstMatch != null)
                .GroupBy(item => item.Candidate.VariableName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(item => item.Candidate.Confidence)
                .ThenBy(item => item.Candidate.Depth)
                .Select(item =>
                {
                    var sourceSummary = string.Join(",", item.Candidate.SourceVariables.Take(3));
                    var displayLabel = string.IsNullOrWhiteSpace(item.Candidate.DisplayLabel)
                        ? item.Candidate.VariableName
                        : item.Candidate.DisplayLabel;
                    var prefix = string.IsNullOrWhiteSpace(sourceSummary)
                        ? "[后继链]"
                        : $"[后继链:{sourceSummary}]";

                    return new StallCandidateInfo
                    {
                        VariableName = item.Candidate.VariableName,
                        SignalKey = $"{prefix} {displayLabel}",
                        ChangedAt = DateTime.Now,
                        LogicUnitId = item.AstMatch.LogicUnitId,
                        BlockName = item.AstMatch.BlockName,
                        DisplayLabel = $"{prefix} {displayLabel}",
                        UnitType = item.AstMatch.UnitType,
                        IsGraphAction = IsGraphRelated(item.AstMatch),
                        ActivationDirection = "SuccessorExpected",
                    };
                })
                .ToList();
        }

        /// <summary>
        /// 非GRAPH场景后继链候选定位：从激活的 PhysicalAction/VirtualAction/HandshakeState 出发，
        /// 沿传播链寻找未激活的后继目标，转换为诊断候选。
        /// </summary>
        private async Task<List<StallCandidateInfo>> LocateNonGraphSuccessorChainCandidatesAsync(
            long projectId,
            string stationCode,
            List<PLC_LogicUnitAstEntity> astRows)
        {
            var successorCandidates = await _executionSuccessorChainService.QueryNonGraphCandidatesAsync(
                projectId,
                stationCode,
                maxDepth: 3,
                maxCandidates: 24);

            if (successorCandidates == null || successorCandidates.Count == 0)
            {
                return new List<StallCandidateInfo>();
            }

            return successorCandidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    AstMatch = FindBestRuntimeAstOutputMatch(candidate.VariableName, astRows)
                })
                .Where(item => item.AstMatch != null)
                .GroupBy(item => item.Candidate.VariableName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(item => item.Candidate.Confidence)
                .ThenBy(item => item.Candidate.Depth)
                .Select(item =>
                {
                    var sourceSummary = string.Join(",", item.Candidate.SourceVariables.Take(3));
                    var displayLabel = string.IsNullOrWhiteSpace(item.Candidate.DisplayLabel)
                        ? item.Candidate.VariableName
                        : item.Candidate.DisplayLabel;
                    var prefix = string.IsNullOrWhiteSpace(sourceSummary)
                        ? "[非GRAPH后继链]"
                        : $"[非GRAPH后继链:{sourceSummary}]";

                    return new StallCandidateInfo
                    {
                        VariableName = item.Candidate.VariableName,
                        SignalKey = $"{prefix} {displayLabel}",
                        ChangedAt = DateTime.Now,
                        LogicUnitId = item.AstMatch.LogicUnitId,
                        BlockName = item.AstMatch.BlockName,
                        DisplayLabel = $"{prefix} {displayLabel}",
                        UnitType = item.AstMatch.UnitType,
                        IsGraphAction = false,
                        ActivationDirection = "NonGraphSuccessorExpected",
                    };
                })
                .ToList();
        }

        /// <summary>
        /// 无 GRAPH 激活时的回退策略：从影子信号镜像中找到当前值为 True 且最近被激活的
        /// PhysicalAction / VirtualAction / HandshakeState 信号，以它们作为诊断起点。
        /// 相比非GRAPH后继链BFS（会扩散出大量False节点），此方法只聚焦"确实活跃"的信号，噪音极低。
        /// </summary>
        private async Task<List<StallCandidateInfo>> LocateRecentlyActivatedIoTagCandidatesAsync(
            long projectId, string stationCode, List<PLC_LogicUnitAstEntity> astRows)
        {
            // ── 策略：以"最近才变为True"的IO信号为种子，沿传播链找下游未激活的后继 ──
            // 不直接评估当前True的Q点（它们本身True，AST没问题），而是通过传播链推导
            // 它"应该带动哪个下游信号激活"，那个下游信号未激活才是卡滞点。
            // 深度限制2：只找直接后继，避免旧的BFS扩散噪音。

            // Step 1：从最近变化历史中取最近5分钟内变为True的IO信号作为种子
            var recentChanges = await _stationShadowStateCacheService.GetRecentChangesAsync(projectId, stationCode, 300);
            if (recentChanges == null || recentChanges.Count == 0)
                return new List<StallCandidateInfo>();

            var windowStart = DateTime.Now.AddMinutes(-5);
            var recentlyActivatedSeeds = recentChanges
                .Where(c => c.ChangedAt.HasValue && c.ChangedAt.Value >= windowStart
                    && !string.IsNullOrWhiteSpace(c.VariableName)
                    && IsSetTrueChange(c.OldValue, c.NewValue)
                    && IsNonGraphRuntimeAnchorChange(c))
                .GroupBy(c => c.VariableName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(c => c.ChangedAt).First())
                .OrderByDescending(c => c.ChangedAt)
                .Take(10)
                .Select(c => c.VariableName.Trim())
                .ToList();

            // Step 1b：若5分钟窗口无数据，回退到镜像中当前为True且最近变化时间最新的前5个IO信号
            if (recentlyActivatedSeeds.Count == 0)
            {
                var mirrors = await _stationShadowStateCacheService.GetSignalMirrorsAsync(projectId, stationCode);
                recentlyActivatedSeeds = (mirrors?.Values ?? Enumerable.Empty<StationShadowSignalCacheModel>())
                    .Where(m => IsNonGraphRuntimeAnchorType(m.AnchorType)
                        && IsCurrentlyTrue(m.CurrentValue)
                        && !string.IsNullOrWhiteSpace(m.VariableName))
                    .OrderByDescending(m => m.LastChangeTime ?? DateTime.MinValue)
                    .Take(5)
                    .Select(m => m.VariableName.Trim())
                    .ToList();
            }

            if (recentlyActivatedSeeds.Count == 0)
                return new List<StallCandidateInfo>();

            await WriteLogHelper.WriteLogsAsync(
                $"[诊断] RecentIoTagBased 种子={string.Join(",", recentlyActivatedSeeds)}",
                "LineDiagnosis/RecentIoTag");

            // Step 2：以种子变量沿传播链做深度2的BFS，找下游未激活后继
            var successorCandidates = await _executionSuccessorChainService.QueryFromRecentlyActivatedIoAsync(
                projectId, stationCode, recentlyActivatedSeeds, maxDepth: 2, maxCandidates: 12);

            if (successorCandidates == null || successorCandidates.Count == 0)
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"[诊断] RecentIoTagBased 传播链无下游未激活后继，回退直接评估种子信号",
                    "LineDiagnosis/RecentIoTag");

                // Step 2b：传播链找不到后继时，直接用种子信号本身作为候选（兜底）
                var candidates = new List<StallCandidateInfo>();
                foreach (var seed in recentlyActivatedSeeds)
                {
                    var astMatch = FindBestRuntimeAstOutputMatch(seed, astRows);
                    if (astMatch == null) continue;
                    candidates.Add(new StallCandidateInfo
                    {
                        VariableName = seed,
                        SignalKey = $"[活跃IO] {seed}",
                        ChangedAt = DateTime.Now,
                        LogicUnitId = astMatch.LogicUnitId,
                        BlockName = astMatch.BlockName,
                        DisplayLabel = $"[活跃IO] {seed}",
                        UnitType = astMatch.UnitType,
                        IsGraphAction = false,
                        ActivationDirection = "ActiveIoTag",
                    });
                }
                return candidates;
            }

            // Step 3：后继候选 → 匹配AST → 转为诊断候选
            var result = successorCandidates
                .Select(c => new { Candidate = c, AstMatch = FindBestRuntimeAstOutputMatch(c.VariableName, astRows) })
                .Where(x => x.AstMatch != null)
                .GroupBy(x => x.Candidate.VariableName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(x => x.Candidate.Confidence)
                .ThenBy(x => x.Candidate.Depth)
                .Select(x =>
                {
                    var sourceSummary = string.Join(",", x.Candidate.SourceVariables.Take(2));
                    var label = string.IsNullOrWhiteSpace(x.Candidate.DisplayLabel) ? x.Candidate.VariableName : x.Candidate.DisplayLabel;
                    var prefix = string.IsNullOrWhiteSpace(sourceSummary) ? "[IO后继链]" : $"[IO后继链:{sourceSummary}]";
                    return new StallCandidateInfo
                    {
                        VariableName = x.Candidate.VariableName,
                        SignalKey = $"{prefix} {label}",
                        ChangedAt = DateTime.Now,
                        LogicUnitId = x.AstMatch.LogicUnitId,
                        BlockName = x.AstMatch.BlockName,
                        DisplayLabel = $"{prefix} {label}",
                        UnitType = x.AstMatch.UnitType,
                        IsGraphAction = false,
                        ActivationDirection = "IoSuccessorExpected",
                    };
                })
                .ToList();

            await WriteLogHelper.WriteLogsAsync(
                $"[诊断] RecentIoTagBased 传播链后继候选={result.Count}",
                "LineDiagnosis/RecentIoTag");

            return result;
        }

        /// <summary>判断是否是 False→True 的激活变化</summary>
        private static bool IsSetTrueChange(object oldValue, object newValue)
        {
            var newStr = newValue?.ToString()?.Trim().ToUpperInvariant();
            var oldStr = oldValue?.ToString()?.Trim().ToUpperInvariant();
            if (newStr != "TRUE" && newStr != "1") return false;
            // 旧值为False/0或为空时认为是激活变化
            return string.IsNullOrWhiteSpace(oldStr) || oldStr == "FALSE" || oldStr == "0";
        }

        private static bool IsSetFalseChange(object oldValue, object newValue)
        {
            var newBool = CoerceTraceBoolean(newValue);
            var oldBool = CoerceTraceBoolean(oldValue);
            return newBool == false && (oldBool == true || !oldBool.HasValue);
        }

        private static bool IsConfirmedBooleanTransition(StationShadowChangeCacheModel change)
        {
            var oldBool = CoerceTraceBoolean(change?.OldValue);
            var newBool = CoerceTraceBoolean(change?.NewValue);
            return oldBool.HasValue && newBool.HasValue && oldBool.Value != newBool.Value;
        }

        private static bool IsConfirmedSetTrueTransition(StationShadowChangeCacheModel change)
        {
            var oldBool = CoerceTraceBoolean(change?.OldValue);
            var newBool = CoerceTraceBoolean(change?.NewValue);
            return oldBool == false && newBool == true;
        }

        private static bool IsBooleanSignalChange(StationShadowChangeCacheModel change)
        {
            if (change == null || IsStringSignalChange(change)) return false;
            return CoerceTraceBoolean(change.OldValue).HasValue
                || CoerceTraceBoolean(change.NewValue).HasValue;
        }

        private static bool ShouldSuppressLongActiveGraphSignal(
            string variableName,
            DateTime? lastChangeTime,
            IReadOnlyDictionary<string, List<StationShadowChangeCacheModel>> changeEvidenceByVar,
            DateTime? latestStationChangeTime,
            out string reason)
        {
            reason = null;
            var name = variableName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return false;

            List<StationShadowChangeCacheModel> changes = null;
            if (changeEvidenceByVar != null)
            {
                changeEvidenceByVar.TryGetValue(name, out changes);
            }

            var boolChanges = changes?
                .Where(IsBooleanSignalChange)
                .OrderByDescending(c => c.ChangedAt ?? DateTime.MinValue)
                .ToList() ?? new List<StationShadowChangeCacheModel>();

            var confirmedTransitions = boolChanges
                .Where(IsConfirmedBooleanTransition)
                .ToList();
            if (confirmedTransitions.Count == 0)
            {
                reason = boolChanges.Count == 0
                    ? "无真实False/True切换证据"
                    : $"仅有初始化/质量刷新记录{boolChanges.Count}条，无真实False/True切换";
                return true;
            }

            if (!latestStationChangeTime.HasValue || latestStationChangeTime.Value <= DateTime.MinValue)
                return false;

            var latestConfirmedTransitionTime = confirmedTransitions
                .Where(c => c.ChangedAt.HasValue)
                .Select(c => c.ChangedAt!.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            var effectiveLastChangeTime = latestConfirmedTransitionTime > DateTime.MinValue
                ? latestConfirmedTransitionTime
                : lastChangeTime;

            if (!effectiveLastChangeTime.HasValue)
            {
                reason = "无LastChange且无布尔变化证据";
                return true;
            }

            if (latestStationChangeTime.Value <= effectiveLastChangeTime.Value)
                return false;

            var staleSpan = latestStationChangeTime.Value - effectiveLastChangeTime.Value;
            if (staleSpan < TimeSpan.FromMinutes(30))
                return false;

            reason = $"早于工位最新变化{staleSpan.TotalMinutes:F0}分钟且无False/True变化证据";
            return true;
        }

        /// <summary>判断变化记录是否属于非GRAPH运行时锚点类型（通过 GroupName/SignalType 辅助判断）</summary>
        private static bool IsNonGraphRuntimeAnchorChange(StationShadowChangeCacheModel change)
        {
            // StationShadowChangeCacheModel 没有 AnchorType，只能通过 GroupName 粗判
            // GroupName 为空时也纳入候选（宁可多不可少，后续传播链会过滤）
            var group = change.GroupName?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(group)) return true;
            // 排除明显是 GRAPH 的 GroupName
            return !group.Contains("GRAPH");
        }

        private static bool IsNonGraphRuntimeAnchorType(string anchorType)
        {
            return string.Equals(anchorType, "PhysicalAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(anchorType, "VirtualAction", StringComparison.OrdinalIgnoreCase)
                || string.Equals(anchorType, "HandshakeState", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCurrentlyTrue(object value)
        {
            if (value == null) return false;
            var s = value.ToString()?.Trim().ToUpperInvariant();
            return s == "TRUE" || s == "1";
        }

        /// <summary>
        /// 从 Redis 信号变化历史中分析失败路径上各信号的最近变化，生成人类可读的结论。
        /// 自动检测瞬态抖动（短时间内 True→False→True）、连续变化等模式。
        /// </summary>
        private async Task<string> AnalyzeFailurePathHistoryAsync(
            long projectId, string stationCode,
            List<StopCauseAstTraceNode> failurePath)
        {
            using var measure = StationSubTimings.Measure("HistorySum");
            return await AnalyzeFailurePathHistoryCoreAsync(projectId, stationCode, failurePath);
        }

        private async Task<string> AnalyzeFailurePathHistoryCoreAsync(
            long projectId, string stationCode,
            List<StopCauseAstTraceNode> failurePath)
        {
            if (failurePath == null || failurePath.Count == 0)
                return null;

            // 收集失败路径上所有有变量名的节点
            var pathVariables = failurePath
                .Where(n => !string.IsNullOrWhiteSpace(n.VariableName))
                .Select(n => n.VariableName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pathVariables.Count == 0)
                return null;

            // 从 Redis 读取最近变化记录（最多 2000 条，覆盖 6 小时 TTL 内的所有变化）
            var recentChanges = await _stationShadowStateCacheService
                .GetRecentChangesAsync(projectId, stationCode, 500);
            if (recentChanges == null || recentChanges.Count == 0)
                return "暂无信号变化历史记录（监控服务可能未运行）";

            // 策略：不用固定时间窗口，而是找每个失败路径变量的最近变化记录。
            // 停机可能已发生很久，固定 10 分钟窗口会完全错过停机时刻的关键跳变。
            var pathVarSet = new HashSet<string>(pathVariables, StringComparer.OrdinalIgnoreCase);
            var relevantChanges = recentChanges
                .Where(c => c.ChangedAt.HasValue)
                .Where(c =>
                {
                    var varName = !string.IsNullOrWhiteSpace(c.VariableName) ? c.VariableName.Trim() : c.SignalKey?.Trim();
                    return !string.IsNullOrWhiteSpace(varName) && pathVarSet.Contains(varName);
                })
                .OrderByDescending(c => c.ChangedAt)
                .ToList();

            if (relevantChanges.Count == 0)
                return "失败路径上的信号无变化记录（可能早于监控窗口或未被监控覆盖）";

            // 计算距今多久，给用户时间参照
            var now = DateTime.Now;
            var latestChangeTime = relevantChanges.First().ChangedAt.Value;
            var elapsedSinceLastChange = now - latestChangeTime;
            string elapsedHint;
            if (elapsedSinceLastChange.TotalSeconds < 60)
                elapsedHint = $"{elapsedSinceLastChange.TotalSeconds:F0}秒前";
            else if (elapsedSinceLastChange.TotalMinutes < 60)
                elapsedHint = $"{elapsedSinceLastChange.TotalMinutes:F0}分钟前";
            else
                elapsedHint = $"{elapsedSinceLastChange.TotalHours:F1}小时前";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[最近一次相关信号变化在 {elapsedHint}]");

            // 按变量分组，每个变量取最近的几条变化记录进行分析
            var groupedByVar = relevantChanges
                .GroupBy(c => (!string.IsNullOrWhiteSpace(c.VariableName) ? c.VariableName.Trim() : c.SignalKey?.Trim()) ?? "",
                    StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key));

            foreach (var group in groupedByVar)
            {
                // 每个变量最多取最近 5 条变化记录
                var changes = group.OrderByDescending(c => c.ChangedAt).Take(5).OrderBy(c => c.ChangedAt).ToList();
                var varName = group.Key;
                var category = ClassifyTerminalVariable(varName);
                var lastChange = changes.Last();
                var lastChangeElapsed = now - lastChange.ChangedAt.Value;

                // 最近一次变化距今的人类可读描述
                string lastChangeAgo;
                if (lastChangeElapsed.TotalSeconds < 60)
                    lastChangeAgo = $"{lastChangeElapsed.TotalSeconds:F0}秒前";
                else if (lastChangeElapsed.TotalMinutes < 60)
                    lastChangeAgo = $"{lastChangeElapsed.TotalMinutes:F0}分钟前";
                else
                    lastChangeAgo = $"{lastChangeElapsed.TotalHours:F1}小时前";

                if (changes.Count == 1)
                {
                    var c = changes[0];
                    var timeStr = c.ChangedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "?";
                    sb.AppendLine($"● {varName}（{category}）：{timeStr}（{lastChangeAgo}）从 {FormatValue(c.OldValue)} 变为 {FormatValue(c.NewValue)}");
                }
                else
                {
                    // 多次变化 → 检测抖动模式
                    var first = changes.First();
                    var last = changes.Last();
                    var spanSeconds = (last.ChangedAt.Value - first.ChangedAt.Value).TotalSeconds;

                    if (spanSeconds <= 5 && changes.Count >= 2)
                    {
                        // 短时间多次变化 = 瞬态抖动
                        sb.AppendLine($"● {varName}（{category}）：检测到瞬态抖动！{spanSeconds:F1}秒内变化了{changes.Count}次");
                        foreach (var c in changes)
                        {
                            var timeStr = c.ChangedAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "?";
                            sb.AppendLine($"  {timeStr}: {FormatValue(c.OldValue)} → {FormatValue(c.NewValue)}");
                        }
                    }
                    else
                    {
                        // 正常的状态变化时间线
                        sb.AppendLine($"● {varName}（{category}）：最近{changes.Count}次变化（最后变化{lastChangeAgo}）");
                        foreach (var c in changes.TakeLast(3))
                        {
                            var timeStr = c.ChangedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "?";
                            sb.AppendLine($"  {timeStr}: {FormatValue(c.OldValue)} → {FormatValue(c.NewValue)}");
                        }
                    }
                }
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "(空)";
            var s = value.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return "(空)";
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return "True(通)";
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return "False(断)";
            return s;
        }

        /// <summary>
        /// 根据变量名特征判断追踪终端变量的类别。
        /// </summary>
        private static string ClassifyTerminalVariable(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName))
                return "未知";

            var v = variableName.Trim();

            // I 点 — 物理输入（传感器、按钮、开关）
            // 匹配: I0.0, IW100, %I0.5, %IB10, E0.0, EW100 (德语)
            if (System.Text.RegularExpressions.Regex.IsMatch(v,
                @"^(%?[IE]\d|%?[IE][BWD]\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "物理输入(I点)";

            // HMI / 上位机写入 — 通常在 DB 块中带 HMI/Panel/Screen/Remote/Cmd 关键词
            if (System.Text.RegularExpressions.Regex.IsMatch(v,
                @"(HMI|Panel|Screen|Remote|Cmd|Command|Recipe|Manual|手动|远程|界面|操作)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "HMI/上位机写入";

            // 报警 / 故障变量
            if (System.Text.RegularExpressions.Regex.IsMatch(v,
                @"(Alarm|Fault|Error|Warning|报警|故障|异常|Err_|Alm_|Flt_)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "报警/故障信号";

            // 外部设备反馈信号（机器人、AGV、视觉、夹爪等外部设备的完成/就绪反馈）
            if (System.Text.RegularExpressions.Regex.IsMatch(v,
                @"(Robot|AGV|Vision|Gripper|Conveyor|Servo|Camera|Barcode|RFID|Scanner|Done|Ready|Complete|Finish|Response|Feedback|Ack|到位|完成|就绪|响应|确认)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "外部设备反馈";

            // Q 点 — 物理输出（执行器、电磁阀）
            if (System.Text.RegularExpressions.Regex.IsMatch(v,
                @"^(%?[QA]\d|%?[QA][BWD]\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "物理输出(Q点)";

            // M 点 — 中间标志位
            if (System.Text.RegularExpressions.Regex.IsMatch(v,
                @"^(%?M\d|%?M[BWD]\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "中间标志(M点)";

            // DB 块变量（无法进一步分类）
            if (v.StartsWith("DB", StringComparison.OrdinalIgnoreCase) || v.Contains("."))
                return "DB变量";

            return "其他信号";
        }

        /// <summary>
        /// 判断一次值变化是否为有效的信号变化（用于卡滞候选筛选）。
        /// 只要新值非空就视为有效变化，不再区分 true/false 方向。
        /// 停机场景下 Q 点可能已从 true 复位回 false，只看 true 会漏掉关键信号，
        /// 应由后续 AST 求值来判断当前状态。
        /// </summary>
        private static bool IsActivationChange(object oldValue, object newValue)
        {
            var newStr = newValue?.ToString()?.Trim();
            // 只过滤无效值（null/空白），任何有效值变化都纳入候选
            return !string.IsNullOrWhiteSpace(newStr);
        }

        /// <summary>
        /// 判断 GRAPH 步骤的激活方向：
        /// - SetTrue：步骤从 FALSE→TRUE（刚激活，正在执行，可能卡住）
        /// - ResetFalse：步骤从 TRUE→FALSE（刚完成/复位，后继步骤可能卡住）
        /// - Unknown：无法判断
        /// </summary>
        private static string ClassifyActivationDirection(object oldValue, object newValue)
        {
            var newStr = newValue?.ToString()?.Trim().ToUpperInvariant();
            var oldStr = oldValue?.ToString()?.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(newStr)) return "Unknown";

            // TRUE / 1 → 步骤刚被激活
            if (newStr == "TRUE" || newStr == "1")
                return "SetTrue";

            // FALSE / 0 且 oldValue 为 TRUE/1 → 步骤刚完成(复位)
            if ((newStr == "FALSE" || newStr == "0") &&
                (oldStr == "TRUE" || oldStr == "1"))
                return "ResetFalse";

            return "Unknown";
        }

        /// <summary>
        /// 卡滞候选 Q 点信息（内部使用）。
        /// </summary>
        private class StallCandidateInfo
        {
            public string VariableName { get; set; }
            public string SignalKey { get; set; }
            public DateTime ChangedAt { get; set; }
            public DateTime? LastTrueAt { get; set; }
            public long? LogicUnitId { get; set; }
            public string BlockName { get; set; }
            public string DisplayLabel { get; set; }
            /// <summary>
            /// AST UnitType（如 STEP_1_ACTION / NETWORK / SCL_BODY 等）
            /// </summary>
            public string UnitType { get; set; }
            /// <summary>
            /// 是否为 GRAPH StepAction 块（UnitType 匹配 STEP_\d+_ACTION）
            /// </summary>
            public bool IsGraphAction { get; set; }
            public bool IsGraphExecBitFallback { get; set; }
            public bool IsDeviceFocus { get; set; }
            public string AnchorType { get; set; }
            public string ActivationRule { get; set; }
            public string AbnormalWhen { get; set; }
            public string CurrentValueText { get; set; }
            /// <summary>
            /// GRAPH 步骤激活方向：SetTrue=步骤刚激活, ResetFalse=步骤刚完成(复位), Unknown=无法判断
            /// </summary>
            public string ActivationDirection { get; set; } = "Unknown";

            /// <summary>
            /// 按 LogicUnitId 收敛后的信号匹配比例（激活信号数 / 该步骤总信号数）。
            /// 值越高说明该步骤当前激活的证据越充分。
            /// </summary>
            public double MatchRatio { get; set; } = 1.0;

            /// <summary>当前步骤激活信号数</summary>
            public int ActiveSignalCount { get; set; } = 1;

            /// <summary>当前步骤总注册信号数</summary>
            public int TotalSignalCount { get; set; } = 1;

            public string GraphStepRole { get; set; }

            public decimal? GraphStepRoleConfidence { get; set; }

            public string GraphStepRoleReason { get; set; }

            public int GraphCandidateRank { get; set; } = 20;
        }

        private sealed class FreshActiveGraphFilterResult
        {
            public List<StallCandidateInfo> SettledCandidates { get; } = new List<StallCandidateInfo>();

            public List<FreshActiveGraphCandidateInfo> FreshCandidates { get; } = new List<FreshActiveGraphCandidateInfo>();
        }

        private sealed class FreshActiveGraphCandidateInfo
        {
            public StallCandidateInfo Candidate { get; set; }

            public DateTime? LastChangeTime { get; set; }

            public double AgeSeconds { get; set; }
        }

        private static readonly Regex GraphStepActionRegex = new Regex(
            @"^STEP_\d+_ACTION$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsGraphStepAction(string unitType)
            => !string.IsNullOrWhiteSpace(unitType) && GraphStepActionRegex.IsMatch(unitType.Trim());

        private static int? ResolveGraphStepNumber(string unitType)
        {
            if (string.IsNullOrWhiteSpace(unitType))
                return null;

            var match = GraphStepActionRegex.Match(unitType.Trim());
            if (!match.Success)
                return null;

            var raw = Regex.Match(unitType.Trim(), @"STEP_(\d+)_ACTION", RegexOptions.IgnoreCase).Groups[1].Value;
            return int.TryParse(raw, out var stepNo) ? stepNo : null;
        }

        private static bool IsGraphExecBitSignal(PLC_StationShadowSignalEntity signal)
            => string.Equals(signal?.AnchorType?.Trim(), GraphExecBitAnchorType, StringComparison.OrdinalIgnoreCase);

        private static string BuildGraphActionEvidence(string rawContentExcerpt)
        {
            var text = StripXmlForEvidence(rawContentExcerpt);
            return string.IsNullOrWhiteSpace(text)
                ? "无显式Action赋值片段"
                : TruncateEvidence(text, 180);
        }

        private static string BuildGraphTransitionEvidence(string outgoingTransitionIdsJson, string transitionConditionsJson)
        {
            var ids = ParseTransitionIdsForEvidence(outgoingTransitionIdsJson);
            var idPart = ids.Count > 0
                ? string.Join(",", ids)
                : "未识别";

            var summaries = ParseGraphTransitionConditionSummaries(transitionConditionsJson);
            if (summaries.Count == 0)
            {
                return $"T[{idPart}]";
            }

            var parts = summaries
                .Take(4)
                .Select(summary =>
                {
                    var title = string.IsNullOrWhiteSpace(summary.Title) ? "未命名" : summary.Title.Trim();
                    var vars = summary.InputVariables?.Where(v => !string.IsNullOrWhiteSpace(v)).Take(4).ToList() ?? new List<string>();
                    var varPart = vars.Count > 0 ? $" 输入:{string.Join(",", vars)}" : string.Empty;
                    return $"T{summary.TransitionNetworkId}:{title}{varPart}";
                })
                .ToList();

            return string.Join(" | ", parts);
        }

        private static List<int> ParseTransitionIdsForEvidence(string outgoingTransitionIdsJson)
        {
            try
            {
                return JsonSerializer.Deserialize<List<int>>(outgoingTransitionIdsJson ?? string.Empty)?
                    .Where(it => it > 0)
                    .Distinct()
                    .ToList()
                    ?? new List<int>();
            }
            catch
            {
                return new List<int>();
            }
        }

        private static List<GraphTransitionConditionSummary> ParseGraphTransitionConditionSummaries(string transitionConditionsJson)
        {
            try
            {
                return JsonSerializer.Deserialize<List<GraphTransitionConditionSummary>>(transitionConditionsJson ?? string.Empty)
                    ?? new List<GraphTransitionConditionSummary>();
            }
            catch
            {
                return new List<GraphTransitionConditionSummary>();
            }
        }

        private static string StripXmlForEvidence(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var withoutTags = Regex.Replace(text, "<[^>]+>", " ");
            return Regex.Replace(withoutTags, "\\s+", " ").Trim();
        }

        private static string TruncateEvidence(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// 判断 AST 行是否属于 GRAPH 块（包括 STEP_X_ACTION、TRANSITION、NETWORK 等）。
        /// 优先看 UnitType，兜底看 ProgrammingLanguage。
        /// 用途：当 ScoreAstOutputMatch 未能优先选到 STEP_X_ACTION 行时，
        /// 通过 ProgrammingLanguage 字段兜底判断。
        /// </summary>
        private static bool IsGraphRelated(PLC_LogicUnitAstEntity ast)
        {
            if (ast == null) return false;
            if (IsGraphStepAction(ast.UnitType)) return true;
            if (!string.IsNullOrWhiteSpace(ast.UnitType)
                && ast.UnitType.IndexOf("TRANSITION", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return !string.IsNullOrWhiteSpace(ast.ProgrammingLanguage)
                && ast.ProgrammingLanguage.IndexOf("GRAPH", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 将失败路径压缩为单行摘要文本。
        /// </summary>
        private static string BuildFailurePathSummary(List<StopCauseAstTraceNode> failurePath)
        {
            if (failurePath == null || failurePath.Count == 0)
                return null;

            var parts = failurePath
                .Where(n => n != null)
                .Take(4)
                .Select(n =>
                {
                    var label = !string.IsNullOrWhiteSpace(n.DisplayName) ? n.DisplayName : n.VariableName ?? n.NodeId;
                    return $"{label}={n.Status}";
                });

            return string.Join(" → ", parts);
        }

        #endregion

        private static int CountStopCauseAstNodes(StopCauseAstNode node)
        {
            if (node == null)
            {
                return 0;
            }

            return 1 + (node.Children?.Sum(CountStopCauseAstNodes) ?? 0);
        }

        private static int MeasureStopCauseAstDepth(StopCauseAstNode node)
        {
            if (node == null)
            {
                return 0;
            }

            var childDepth = node.Children == null || node.Children.Count == 0
                ? 0
                : node.Children.Max(MeasureStopCauseAstDepth);
            return childDepth + 1;
        }

        private static List<AnchorCandidate> SelectTopAnchors(IEnumerable<AnchorCandidate> anchors, int limit)
        {
            return (anchors ?? Enumerable.Empty<AnchorCandidate>())
                .OrderByDescending(it => it.Confidence)
                .ThenBy(it => it.VariableName)
                .Take(limit)
                .ToList();
        }
    }
}
