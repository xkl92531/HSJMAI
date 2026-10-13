using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 第一阶段编译服务，当前负责为候选锚点提取 AST 构建种子。
    /// </summary>
    public class StopCausePhaseOneCompilerService : ISelfScopedAutoInject
    {
        private readonly IPLC_VariableAccessService _variableAccessService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_BlockCallService _blockCallService;

        public StopCausePhaseOneCompilerService(
            IPLC_VariableAccessService variableAccessService,
            IPLC_LogicUnitService logicUnitService,
            IPLC_BlockService blockService,
            IPLC_BlockCallService blockCallService)
        {
            _variableAccessService = variableAccessService;
            _logicUnitService = logicUnitService;
            _blockService = blockService;
            _blockCallService = blockCallService;
        }

        public async Task<List<StopCauseAstSeed>> BuildSeedsAsync(
            AnchorDiscoveryRequestDto request,
            IEnumerable<AnchorCandidate> anchors)
        {
            var anchorList = anchors
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .GroupBy(it => it.VariableName, StringComparer.OrdinalIgnoreCase)
                .Select(it => it.First())
                .ToList();

            if (anchorList.Count == 0)
            {
                return new List<StopCauseAstSeed>();
            }

            var variableNames = anchorList.Select(it => it.VariableName).ToList();
            var blockId = request.BlockId;
            var variableAccesses = blockId.HasValue
                ? await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.BlockId == blockId.Value &&
                    variableNames.Contains(it.VariableName))
                : await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    variableNames.Contains(it.VariableName));

            var logicUnitIds = variableAccesses
                .Where(IsWriteAccess)
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            var logicUnits = logicUnitIds.Count == 0
                ? new List<PLC_LogicUnitEntity>()
                : await _logicUnitService.GetListAsync(it => it.IsDeleted == false && logicUnitIds.Contains(it.Id));

            logicUnits = logicUnits
                .Where(LogicUnitAnalysisPolicy.IsEligible)
                .ToList();

            var eligibleLogicUnitIds = logicUnits
                .Select(it => it.Id)
                .ToHashSet();

            variableAccesses = LogicUnitAnalysisPolicy.FilterAccessesByEligibleLogicUnits(variableAccesses, eligibleLogicUnitIds);

            var blockIds = logicUnits
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();

            var blocks = blockIds.Count == 0
                ? new List<PLC_BlockEntity>()
                : await _blockService.GetListAsync(it => it.IsDeleted == false && blockIds.Contains(it.Id));

            var logicUnitMap = logicUnits.ToDictionary(it => it.Id, it => it);
            var blockMap = blocks.ToDictionary(it => it.Id, it => it);

            var seeds = new List<StopCauseAstSeed>();
            foreach (var anchor in anchorList)
            {
                var writeAccesses = variableAccesses
                    .Where(it => string.Equals(it.VariableName, anchor.VariableName, StringComparison.OrdinalIgnoreCase))
                    .Where(IsWriteAccess)
                    .ToList();

                var writerLogicUnits = writeAccesses
                    .Where(it => it.LogicUnitId.HasValue)
                    .Select(it => it.LogicUnitId.Value)
                    .Distinct()
                    .Where(logicUnitMap.ContainsKey)
                    .Select(id => BuildSummary(logicUnitMap[id], blockMap, request.RawContentMaxLength))
                    .ToList();

                var relatedVariables = variableAccesses
                    .Where(it => writeAccesses.Any(w => w.LogicUnitId == it.LogicUnitId))
                    .Select(it => it.VariableName)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(30)
                    .ToList();

                // 同 LogicUnit 的所有 WRITE 变量（用于 OutputVariables 补全）
                var writerLogicUnitIds = writeAccesses
                    .Where(it => it.LogicUnitId.HasValue)
                    .Select(it => it.LogicUnitId.Value)
                    .Distinct()
                    .ToHashSet();

                var siblingWriteVariables = variableAccesses
                    .Where(it => it.LogicUnitId.HasValue
                        && writerLogicUnitIds.Contains(it.LogicUnitId.Value)
                        && IsWriteAccess(it))
                    .Select(it => it.VariableName)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(50)
                    .ToList();

                seeds.Add(new StopCauseAstSeed
                {
                    Anchor = anchor,
                    CompileStatus = AstCompileStatus.Pending,
                    WriterLogicUnits = writerLogicUnits,
                    RelatedVariables = relatedVariables,
                    CoverageReports = writerLogicUnits
                        .Select(summary => BuildCoverageReport(summary, anchor, relatedVariables, siblingWriteVariables))
                        .ToList()
                });
            }

            return seeds;
        }

        public async Task<List<StopCauseAstSeed>> BuildWriterSeedsAsync(AnchorDiscoveryRequestDto request)
        {
            var blockId = request.BlockId;
            var allAccesses = blockId.HasValue
                ? await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.BlockId == blockId.Value &&
                    it.LogicUnitId.HasValue &&
                    !string.IsNullOrEmpty(it.VariableName))
                : await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.LogicUnitId.HasValue &&
                    !string.IsNullOrEmpty(it.VariableName));

            await WriteLogHelper.WriteLogsAsync(
                $"[AST种子诊断] ProjectId={request.ProjectId}, BlockId={blockId}, StationCode={request.StationCode ?? "ALL"}, " +
                $"VariableAccesses(有LogicUnitId)={allAccesses.Count}, " +
                $"AccessTypes分布=[{string.Join(", ", allAccesses.GroupBy(a => a.AccessType ?? "NULL").Select(g => $"{g.Key}:{g.Count()}"))}]",
                "AST种子");

            // ── 通过 VariableAccess 收集 LogicUnit ──
            var logicUnitIds = allAccesses
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            var allLogicUnits = logicUnitIds.Count == 0
                ? new List<PLC_LogicUnitEntity>()
                : await _logicUnitService.GetListAsync(it => it.IsDeleted == false && logicUnitIds.Contains(it.Id));

            // ── 补充加载：同 Block 下所有 eligible LogicUnit（覆盖无 VariableAccess 的单元） ──
            var accessBlockIds = allLogicUnits
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();
            if (blockId.HasValue && !accessBlockIds.Contains(blockId.Value))
            {
                accessBlockIds.Add(blockId.Value);
            }

            if (accessBlockIds.Count > 0)
            {
                var supplementUnits = await _logicUnitService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.BlockId.HasValue &&
                    accessBlockIds.Contains(it.BlockId.Value));

                var existingIds = allLogicUnits.Select(it => it.Id).ToHashSet();
                var newUnits = supplementUnits
                    .Where(it => !existingIds.Contains(it.Id))
                    .ToList();

                if (newUnits.Count > 0)
                {
                    allLogicUnits = allLogicUnits.Concat(newUnits).ToList();
                    await WriteLogHelper.WriteLogsAsync(
                        $"[AST种子诊断] 补充加载同Block下无VariableAccess的LogicUnit: +{newUnits.Count}, " +
                        $"补充UnitType分布=[{string.Join(", ", newUnits.GroupBy(u => u.UnitType ?? "NULL").Select(g => $"{g.Key}:{g.Count()}"))}]",
                        "AST种子");
                }
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[AST种子诊断] LogicUnit总数={allLogicUnits.Count}, " +
                $"UnitType分布=[{string.Join(", ", allLogicUnits.GroupBy(u => u.UnitType ?? "NULL").Select(g => $"{g.Key}:{g.Count()}"))}]",
                "AST种子");

            var logicUnits = allLogicUnits
                .Where(LogicUnitAnalysisPolicy.IsEligible)
                .ToList();

            await WriteLogHelper.WriteLogsAsync(
                $"[AST种子诊断] Eligible(SCL_BODY/NETWORK/TRANSITION/STEP_ACTION)={logicUnits.Count}, 被过滤={allLogicUnits.Count - logicUnits.Count}",
                "AST种子");

            var eligibleLogicUnitIds = logicUnits.Select(it => it.Id).ToHashSet();
            allAccesses = LogicUnitAnalysisPolicy.FilterAccessesByEligibleLogicUnits(allAccesses, eligibleLogicUnitIds);

            var blockIds = logicUnits
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();

            var blocks = blockIds.Count == 0
                ? new List<PLC_BlockEntity>()
                : await _blockService.GetListAsync(it => it.IsDeleted == false && blockIds.Contains(it.Id));

            var blockMap = blocks.ToDictionary(it => it.Id, it => it);

            // 构建调用链工位继承映射：被调用块 → 调用者块的工位
            var callerStationMap = await BuildCallerStationMapAsync(request.ProjectId, blockMap);

            var accessesByLogicUnit = allAccesses
                .GroupBy(it => it.LogicUnitId.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            var seeds = new List<StopCauseAstSeed>();
            foreach (var logicUnit in logicUnits)
            {
                // 优先使用 VariableAccess 数据，补充加载的单元可能没有 Access 记录
                accessesByLogicUnit.TryGetValue(logicUnit.Id, out var accesses);
                accesses ??= new List<PLC_VariableAccessEntity>();

                var writes = accesses
                    .Where(IsWriteAccess)
                    .Select(it => it.VariableName)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var reads = accesses
                    .Where(IsReadAccess)
                    .Select(it => it.VariableName)
                    .Where(it => !string.IsNullOrWhiteSpace(it))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(30)
                    .ToList();

                // 有写变量 → 输出 = writes，否则以读变量作为锚点（TRANSITION / 纯条件 NETWORK 等）
                var effectiveOutputs = writes.Count > 0 ? writes : reads;

                var block = logicUnit.BlockId.HasValue && blockMap.TryGetValue(logicUnit.BlockId.Value, out var currentBlock)
                    ? currentBlock
                    : null;

                // 对于既无 write 也无 read 的单元，尝试用块名/标题作为标识
                var fallbackName = logicUnit.Title ?? block?.BlockName ?? $"logicunit:{logicUnit.Id}";
                var anchorVariable = effectiveOutputs.FirstOrDefault() ?? fallbackName;

                var stationCode = ResolveSeedStationCode(request, block, effectiveOutputs.Count > 0 ? effectiveOutputs : new List<string> { fallbackName }, callerStationMap);
                if (!string.IsNullOrWhiteSpace(request.StationCode) &&
                    !string.Equals(request.StationCode, stationCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var summary = BuildSummary(logicUnit, blockMap, request.RawContentMaxLength);
                summary.GraphTransitionConditionsJson = BuildGraphTransitionConditionsJson(
                    logicUnit,
                    logicUnits,
                    accessesByLogicUnit,
                    blockMap,
                    request.RawContentMaxLength);

                var anchorLabel = FirstNonEmpty(logicUnit.Title, block?.BlockName, anchorVariable);
                var hasWrites = writes.Count > 0;
                var anchorType = hasWrites ? "WriterLogicUnit" : "ReaderLogicUnit";
                var anchorReason = hasWrites
                    ? "按写入逻辑单元直接构建 AST Seed"
                    : "无写入变量的逻辑单元（条件网络/TRANSITION），按读取变量构建 AST Seed";

                seeds.Add(new StopCauseAstSeed
                {
                    Anchor = new AnchorCandidate
                    {
                        AnchorKey = $"logicunit:{logicUnit.Id}",
                        AnchorType = anchorType,
                        VariableName = anchorVariable,
                        StationCode = stationCode,
                        SemanticLabel = anchorLabel,
                        Source = anchorType,
                        Confidence = hasWrites ? 0.92m : 0.85m,
                        DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                        PrimaryReason = anchorReason,
                        LogicSourceType = logicUnit.ProgrammingLanguage,
                        Evidence = new List<string>
                        {
                            $"LogicUnitId: {logicUnit.Id}",
                            $"WriterCount: {writes.Count}",
                            $"ReaderCount: {reads.Count}",
                            $"UnitType: {logicUnit.UnitType}"
                        }
                    },
                    CompileStatus = AstCompileStatus.Pending,
                    WriterLogicUnits = new List<WriterLogicUnitSummary> { summary },
                    RelatedVariables = reads.Concat(writes).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList(),
                    CoverageReports = new List<AstCoverageReport>
                    {
                        BuildWriterCoverageReport(summary, reads, effectiveOutputs.Count > 0 ? effectiveOutputs : new List<string>())
                    }
                });
            }

            await WriteLogHelper.WriteLogsAsync(
                $"[AST种子诊断] 最终种子数={seeds.Count}, EligibleLogicUnits遍历={logicUnits.Count}",
                "AST种子");

            return seeds
                .OrderBy(it => it.Anchor?.StationCode)
                .ThenBy(it => it.WriterLogicUnits.FirstOrDefault()?.BlockName)
                .ThenBy(it => it.WriterLogicUnits.FirstOrDefault()?.NetworkId)
                .ToList();
        }

        public async Task<StopCauseAstSeed> BuildWriterSeedForLogicUnitAsync(RecompileLogicUnitAstRequestDto request)
        {
            if (request == null || request.ProjectId <= 0 || request.LogicUnitId <= 0)
            {
                return null;
            }

            var logicUnit = await _logicUnitService.GetFirstAsync(it =>
                it.IsDeleted == false &&
                it.Id == request.LogicUnitId);

            if (logicUnit == null || !LogicUnitAnalysisPolicy.IsEligible(logicUnit))
            {
                return null;
            }

            if (logicUnit.BlockId.HasValue)
            {
                var owningBlock = await _blockService.GetFirstAsync(it =>
                    it.IsDeleted == false &&
                    it.Id == logicUnit.BlockId.Value);
                if (owningBlock?.ProjectId.HasValue == true &&
                    owningBlock.ProjectId.Value != request.ProjectId)
                {
                    return null;
                }
            }

            var logicUnits = logicUnit.BlockId.HasValue
                ? await _logicUnitService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.BlockId == logicUnit.BlockId.Value)
                : new List<PLC_LogicUnitEntity> { logicUnit };

            logicUnits = logicUnits
                .Where(LogicUnitAnalysisPolicy.IsEligible)
                .ToList();

            var blockIds = logicUnits
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();

            var blocks = blockIds.Count == 0
                ? new List<PLC_BlockEntity>()
                : await _blockService.GetListAsync(it => it.IsDeleted == false && blockIds.Contains(it.Id));
            var blockMap = blocks.ToDictionary(it => it.Id, it => it);

            var allAccesses = logicUnit.BlockId.HasValue
                ? await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.BlockId == logicUnit.BlockId.Value &&
                    it.LogicUnitId.HasValue &&
                    !string.IsNullOrEmpty(it.VariableName))
                : await _variableAccessService.GetListAsync(it =>
                    it.IsDeleted == false &&
                    it.ProjectId == request.ProjectId &&
                    it.LogicUnitId == logicUnit.Id &&
                    !string.IsNullOrEmpty(it.VariableName));

            var eligibleLogicUnitIds = logicUnits.Select(it => it.Id).ToHashSet();
            allAccesses = LogicUnitAnalysisPolicy.FilterAccessesByEligibleLogicUnits(allAccesses, eligibleLogicUnitIds);
            var accessesByLogicUnit = allAccesses
                .GroupBy(it => it.LogicUnitId.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            accessesByLogicUnit.TryGetValue(logicUnit.Id, out var accesses);
            accesses ??= new List<PLC_VariableAccessEntity>();

            var writes = accesses
                .Where(IsWriteAccess)
                .Select(it => it.VariableName)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var reads = accesses
                .Where(IsReadAccess)
                .Select(it => it.VariableName)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();

            var effectiveOutputs = writes.Count > 0 ? writes : reads;
            var block = logicUnit.BlockId.HasValue && blockMap.TryGetValue(logicUnit.BlockId.Value, out var currentBlock)
                ? currentBlock
                : null;

            var fallbackName = logicUnit.Title ?? block?.BlockName ?? $"logicunit:{logicUnit.Id}";
            var anchorVariable = effectiveOutputs.FirstOrDefault() ?? fallbackName;
            var seedRequest = new AnchorDiscoveryRequestDto
            {
                ProjectId = request.ProjectId,
                StationCode = request.StationCode,
                RawContentMaxLength = request.RawContentMaxLength
            };
            var callerStationMap = await BuildCallerStationMapAsync(request.ProjectId, blockMap);
            var stationCode = ResolveSeedStationCode(
                seedRequest,
                block,
                effectiveOutputs.Count > 0 ? effectiveOutputs : new List<string> { fallbackName },
                callerStationMap);

            if (string.IsNullOrWhiteSpace(stationCode) && !string.IsNullOrWhiteSpace(request.StationCode))
            {
                stationCode = request.StationCode.Trim();
            }

            var summary = BuildSummary(logicUnit, blockMap, request.RawContentMaxLength);
            summary.GraphTransitionConditionsJson = BuildGraphTransitionConditionsJson(
                logicUnit,
                logicUnits,
                accessesByLogicUnit,
                blockMap,
                request.RawContentMaxLength);

            var hasWrites = writes.Count > 0;
            var anchorType = hasWrites ? "WriterLogicUnit" : "ReaderLogicUnit";
            var anchorReason = hasWrites
                ? "Recompiled a single writer logic unit by LogicUnitId"
                : "Recompiled a single read-only logic unit by LogicUnitId";
            var anchorLabel = FirstNonEmpty(logicUnit.Title, block?.BlockName, anchorVariable);

            return new StopCauseAstSeed
            {
                Anchor = new AnchorCandidate
                {
                    AnchorKey = $"logicunit:{logicUnit.Id}",
                    AnchorType = anchorType,
                    VariableName = anchorVariable,
                    StationCode = stationCode,
                    SemanticLabel = anchorLabel,
                    Source = anchorType,
                    Confidence = hasWrites ? 0.92m : 0.85m,
                    DiscoveryStatus = AnchorDiscoveryStatus.Confirmed,
                    PrimaryReason = anchorReason,
                    LogicSourceType = logicUnit.ProgrammingLanguage,
                    Evidence = new List<string>
                    {
                        $"LogicUnitId: {logicUnit.Id}",
                        $"WriterCount: {writes.Count}",
                        $"ReaderCount: {reads.Count}",
                        $"UnitType: {logicUnit.UnitType}"
                    }
                },
                CompileStatus = AstCompileStatus.Pending,
                WriterLogicUnits = new List<WriterLogicUnitSummary> { summary },
                RelatedVariables = reads.Concat(writes).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList(),
                CoverageReports = new List<AstCoverageReport>
                {
                    BuildWriterCoverageReport(summary, reads, effectiveOutputs.Count > 0 ? effectiveOutputs : new List<string>())
                }
            };
        }

        private static WriterLogicUnitSummary BuildSummary(
            PLC_LogicUnitEntity logicUnit,
            IReadOnlyDictionary<long, PLC_BlockEntity> blockMap,
            int rawContentMaxLength)
        {
            PLC_BlockEntity block = null;
            if (logicUnit.BlockId.HasValue)
            {
                blockMap.TryGetValue(logicUnit.BlockId.Value, out block);
            }

            var rawContent = logicUnit.RawContent ?? string.Empty;
            if (rawContentMaxLength > 0 && rawContent.Length > rawContentMaxLength)
            {
                rawContent = rawContent.Substring(0, rawContentMaxLength);
            }

            return new WriterLogicUnitSummary
            {
                LogicUnitId = logicUnit.Id,
                BlockId = logicUnit.BlockId,
                BlockName = block?.BlockName ?? string.Empty,
                ProgrammingLanguage = logicUnit.ProgrammingLanguage,
                UnitType = logicUnit.UnitType,
                NetworkId = logicUnit.NetworkId,
                Title = logicUnit.Title,
                RawContent = logicUnit.RawContent ?? string.Empty,
                RawContentExcerpt = rawContent,
                CallPinBindingsJson = logicUnit.CallPinBindingsJson,
                GraphOutgoingTransitionsJson = logicUnit.GraphOutgoingTransitionsJson,
            };
        }

        private static bool IsWriteAccess(PLC_VariableAccessEntity entity)
        {
            return string.Equals(entity.AccessType, "WRITE", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entity.AccessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReadAccess(PLC_VariableAccessEntity entity)
        {
            return string.Equals(entity.AccessType, "READ", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entity.AccessType, "READWRITE", StringComparison.OrdinalIgnoreCase);
        }

        private static AstCoverageReport BuildCoverageReport(
            WriterLogicUnitSummary summary,
            AnchorCandidate anchor,
            List<string> relatedVariables,
            List<string> siblingWriteVariables = null)
        {
            // OutputVariables: 以锚点变量为首，补全同 LogicUnit 的其他 WRITE 变量
            // 这样诊断阶段 FindBestRuntimeAstOutputMatch 能正确匹配到 GRAPH StepAction 行
            var outputVars = new List<string> { anchor.VariableName };
            if (siblingWriteVariables != null)
            {
                foreach (var v in siblingWriteVariables)
                {
                    if (!string.Equals(v, anchor.VariableName, StringComparison.OrdinalIgnoreCase))
                        outputVars.Add(v);
                    if (outputVars.Count >= 50) break;
                }
            }

            return new AstCoverageReport
            {
                SourceType = BuildSourceType(summary),
                UnitType = summary.UnitType,
                Title = summary.Title,
                BlockName = summary.BlockName,
                LogicUnitId = summary.LogicUnitId,
                NetworkId = summary.NetworkId,
                CompileStatus = AstCompileStatus.Pending,
                UnsupportedReason = string.Empty,
                RawContent = summary.RawContent,
                RawContentExcerpt = summary.RawContentExcerpt,
                InputVariables = relatedVariables
                    .Where(it => !string.Equals(it, anchor.VariableName, StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .ToList(),
                OutputVariables = outputVars,
                SuggestedUpgradeCategory = BuildSuggestedUpgradeCategory(summary),
                CallPinBindingsJson = summary.CallPinBindingsJson,
                GraphOutgoingTransitionsJson = summary.GraphOutgoingTransitionsJson,
                GraphTransitionConditionsJson = summary.GraphTransitionConditionsJson,
            };
        }

        private static AstCoverageReport BuildWriterCoverageReport(
            WriterLogicUnitSummary summary,
            List<string> inputVariables,
            List<string> outputVariables)
        {
            return new AstCoverageReport
            {
                SourceType = BuildSourceType(summary),
                UnitType = summary.UnitType,
                Title = summary.Title,
                BlockName = summary.BlockName,
                LogicUnitId = summary.LogicUnitId,
                NetworkId = summary.NetworkId,
                CompileStatus = AstCompileStatus.Pending,
                UnsupportedReason = string.Empty,
                RawContent = summary.RawContent,
                RawContentExcerpt = summary.RawContentExcerpt,
                InputVariables = inputVariables?.Take(20).ToList() ?? new List<string>(),
                OutputVariables = outputVariables?.Take(20).ToList() ?? new List<string>(),
                SuggestedUpgradeCategory = BuildSuggestedUpgradeCategory(summary),
                CallPinBindingsJson = summary.CallPinBindingsJson,
                GraphOutgoingTransitionsJson = summary.GraphOutgoingTransitionsJson,
                GraphTransitionConditionsJson = summary.GraphTransitionConditionsJson,
            };
        }

        private static string BuildGraphTransitionConditionsJson(
            PLC_LogicUnitEntity stepActionUnit,
            IReadOnlyList<PLC_LogicUnitEntity> logicUnits,
            IReadOnlyDictionary<long, List<PLC_VariableAccessEntity>> accessesByLogicUnit,
            IReadOnlyDictionary<long, PLC_BlockEntity> blockMap,
            int rawContentMaxLength)
        {
            if (stepActionUnit == null ||
                !IsGraphStepActionUnit(stepActionUnit.UnitType) ||
                !stepActionUnit.BlockId.HasValue ||
                string.IsNullOrWhiteSpace(stepActionUnit.GraphOutgoingTransitionsJson))
            {
                return null;
            }

            var transitionNumbers = ParseTransitionNumbers(stepActionUnit.GraphOutgoingTransitionsJson);
            if (transitionNumbers.Count == 0)
            {
                return null;
            }

            var summaries = logicUnits
                .Where(unit => unit != null
                    && unit.BlockId == stepActionUnit.BlockId
                    && unit.NetworkId.HasValue
                    && transitionNumbers.Contains(unit.NetworkId.Value)
                    && IsGraphTransitionUnit(unit.UnitType))
                .OrderBy(unit => unit.NetworkId)
                .Select(unit =>
                {
                    accessesByLogicUnit.TryGetValue(unit.Id, out var accesses);
                    accesses ??= new List<PLC_VariableAccessEntity>();

                    PLC_BlockEntity block = null;
                    if (unit.BlockId.HasValue)
                    {
                        blockMap.TryGetValue(unit.BlockId.Value, out block);
                    }

                    return new GraphTransitionConditionSummary
                    {
                        TransitionNetworkId = unit.NetworkId,
                        LogicUnitId = unit.Id,
                        UnitType = unit.UnitType,
                        Title = unit.Title,
                        BlockName = block?.BlockName,
                        RawContentExcerpt = BuildRawContentExcerpt(unit.RawContent, rawContentMaxLength),
                        InputVariables = accesses
                            .Where(IsReadAccess)
                            .Select(it => it.VariableName)
                            .Where(it => !string.IsNullOrWhiteSpace(it))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(30)
                            .ToList(),
                        OutputVariables = accesses
                            .Where(IsWriteAccess)
                            .Select(it => it.VariableName)
                            .Where(it => !string.IsNullOrWhiteSpace(it))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(20)
                            .ToList()
                    };
                })
                .ToList();

            return summaries.Count > 0
                ? JsonSerializer.Serialize(summaries)
                : null;
        }

        private static HashSet<int> ParseTransitionNumbers(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<int>>(json)?
                    .Where(it => it > 0)
                    .ToHashSet()
                    ?? new HashSet<int>();
            }
            catch
            {
                return new HashSet<int>();
            }
        }

        private static bool IsGraphStepActionUnit(string unitType)
            => !string.IsNullOrWhiteSpace(unitType)
               && unitType.IndexOf("STEP_", StringComparison.OrdinalIgnoreCase) >= 0
               && unitType.IndexOf("_ACTION", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsGraphTransitionUnit(string unitType)
            => !string.IsNullOrWhiteSpace(unitType)
               && unitType.IndexOf("TRANSITION", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string BuildRawContentExcerpt(string rawContent, int rawContentMaxLength)
        {
            rawContent ??= string.Empty;
            return rawContentMaxLength > 0 && rawContent.Length > rawContentMaxLength
                ? rawContent.Substring(0, rawContentMaxLength)
                : rawContent;
        }

        private static string ResolveSeedStationCode(
            AnchorDiscoveryRequestDto request,
            PLC_BlockEntity block,
            IReadOnlyList<string> writes,
            IReadOnlyDictionary<long, string> callerStationMap = null)
        {
            // 优先级 1：写入变量→工位映射（最可靠，变量名前缀直接反映实际工位）
            // 原因：GRAPH FB 块（如 FB360_MainGraph）可能被多个工位调用，
            // 块级 stationcode 只记录了一个（可能还是错的），
            // 但变量名（如 OP030_CYL01）能准确反映真实工位归属。
            if (request?.PrecomputedVariableStations != null)
            {
                foreach (var write in writes ?? Array.Empty<string>())
                {
                    if (request.PrecomputedVariableStations.TryGetValue(write, out var station) &&
                        !string.IsNullOrWhiteSpace(station?.StationCode))
                    {
                        return station.StationCode;
                    }
                }
            }

            // 优先级 2：块直属工位（仅当变量无法推断时才使用）
            if (!string.IsNullOrWhiteSpace(block?.StationCode))
            {
                return block.StationCode;
            }

            // 优先级 3：调用链继承 — 从调用当前块的父块继承工位
            if (callerStationMap != null && block != null &&
                callerStationMap.TryGetValue(block.Id, out var callerStation) &&
                !string.IsNullOrWhiteSpace(callerStation))
            {
                return callerStation;
            }

            return string.Empty;
        }

        /// <summary>
        /// 构建被调用块→调用者工位映射。
        /// 当一个块被有工位的块调用时，继承调用者的工位。
        /// 如果有多个调用者且工位不一致，则不继承（避免歧义）。
        /// </summary>
        private async Task<Dictionary<long, string>> BuildCallerStationMapAsync(
            long projectId,
            IReadOnlyDictionary<long, PLC_BlockEntity> blockMap)
        {
            var result = new Dictionary<long, string>();
            if (blockMap.Count == 0) return result;

            // 查出项目内所有调用关系
            var calls = await _blockCallService.GetListAsync(it =>
                it.IsDeleted == false &&
                it.ProjectId == projectId &&
                it.CallerBlockId.HasValue &&
                !string.IsNullOrWhiteSpace(it.CalleeBlockName));

            if (calls.Count == 0) return result;

            // 构建 calleeBlockName → callerBlocks 映射
            var blockByName = blockMap.Values
                .Where(b => !string.IsNullOrWhiteSpace(b.BlockName))
                .GroupBy(b => b.BlockName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // 按被调用块名聚合调用者
            var calleeToCallerStations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var call in calls)
            {
                if (!call.CallerBlockId.HasValue) continue;
                if (!blockMap.TryGetValue(call.CallerBlockId.Value, out var callerBlock)) continue;
                if (string.IsNullOrWhiteSpace(callerBlock.StationCode)) continue;

                if (!calleeToCallerStations.TryGetValue(call.CalleeBlockName, out var stations))
                {
                    stations = new List<string>();
                    calleeToCallerStations[call.CalleeBlockName] = stations;
                }
                stations.Add(callerBlock.StationCode);
            }

            // 只有当所有调用者工位一致时才继承
            foreach (var (calleeName, callerStations) in calleeToCallerStations)
            {
                var distinct = callerStations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (distinct.Count != 1) continue; // 多工位冲突，不继承

                if (blockByName.TryGetValue(calleeName, out var calleeBlock) &&
                    string.IsNullOrWhiteSpace(calleeBlock.StationCode))
                {
                    result[calleeBlock.Id] = distinct[0];
                }
            }

            return result;
        }

        private static string FirstNonEmpty(params string[] candidates)
        {
            return candidates?.FirstOrDefault(it => !string.IsNullOrWhiteSpace(it)) ?? string.Empty;
        }

        private static string BuildSourceType(WriterLogicUnitSummary summary)
        {
            if (string.Equals(summary.ProgrammingLanguage, "GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                return "GRAPH";
            }

            if (string.Equals(summary.ProgrammingLanguage, "LAD", StringComparison.OrdinalIgnoreCase))
            {
                return "LAD";
            }

            if (string.Equals(summary.ProgrammingLanguage, "FBD", StringComparison.OrdinalIgnoreCase))
            {
                return "FBD";
            }

            if (string.Equals(summary.ProgrammingLanguage, "STL", StringComparison.OrdinalIgnoreCase))
            {
                return "STL";
            }

            if (string.Equals(summary.ProgrammingLanguage, "SCL", StringComparison.OrdinalIgnoreCase))
            {
                return "SCL";
            }

            return summary.ProgrammingLanguage ?? string.Empty;
        }

        private static string BuildSuggestedUpgradeCategory(WriterLogicUnitSummary summary)
        {
            if (string.Equals(summary.ProgrammingLanguage, "GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                return "GraphAstCompiler";
            }

            if (string.Equals(summary.ProgrammingLanguage, "LAD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(summary.ProgrammingLanguage, "FBD", StringComparison.OrdinalIgnoreCase))
            {
                return "LadAstCompiler";
            }

            if (string.Equals(summary.ProgrammingLanguage, "STL", StringComparison.OrdinalIgnoreCase))
            {
                return "StlAstCompiler";
            }

            return "OtherLanguageCompiler";
        }
    }
}
