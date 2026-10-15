using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using SqlSugar;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 工位地图持久化服务。
    /// 将 StationMapResult 与 LogicUnit 染色结果写入工位目录及绑定表。
    /// </summary>
    public class StationMapPersistenceService : ISelfScopedAutoInject
    {
        private readonly IPLC_StationCatalogService _stationCatalogService;
        private readonly IPLC_StationAnchorBindingService _stationAnchorBindingService;
        private readonly IPLC_StationBlockBindingService _stationBlockBindingService;
        private readonly IPLC_StationLogicUnitBindingService _stationLogicUnitBindingService;
        private readonly IPLC_VariablePropagationService _variablePropagationService;
        private readonly IPLC_BlockCallService _blockCallService;
        private readonly IPLC_BlockService _blockService;
        private readonly ISqlSugarClient _db;
        private readonly StationPlcScopeService _stationPlcScopeService;

        public StationMapPersistenceService(
            IPLC_StationCatalogService stationCatalogService,
            IPLC_StationAnchorBindingService stationAnchorBindingService,
            IPLC_StationBlockBindingService stationBlockBindingService,
            IPLC_StationLogicUnitBindingService stationLogicUnitBindingService,
            IPLC_VariablePropagationService variablePropagationService,
            IPLC_BlockCallService blockCallService,
            IPLC_BlockService blockService,
            ISqlSugarClient db,
            StationPlcScopeService stationPlcScopeService)
        {
            _stationPlcScopeService = stationPlcScopeService;
            _stationCatalogService = stationCatalogService;
            _stationAnchorBindingService = stationAnchorBindingService;
            _stationBlockBindingService = stationBlockBindingService;
            _stationLogicUnitBindingService = stationLogicUnitBindingService;
            _variablePropagationService = variablePropagationService;
            _blockCallService = blockCallService;
            _blockService = blockService;
            _db = db;
        }

        public async Task PersistAsync(
            long projectId,
            StationMapResult stationMap,
            IReadOnlyCollection<LogicUnitStationLabel> stationColoring,
            string stationCode = null)
        {
            stationMap ??= new StationMapResult();
            stationColoring ??= Array.Empty<LogicUnitStationLabel>();
            var sc = stationCode?.Trim();
            var hasStation = !string.IsNullOrWhiteSpace(sc);

            // 2026-10-14「自动建议 + 人工确认」：提取重新生成自动建议行（只读，事务外算好），
            // 手动绑定与已拒绝行原样保留（StationBlockBindingRules）
            var scoredBindings = await BuildBlockBindingsAsync(projectId, stationColoring);
            var projectBlocks = (await _blockService.GetListAsync(it => it.ProjectId == projectId && it.IsDeleted == false))
                .Where(it => !string.IsNullOrWhiteSpace(it.BlockName))
                .Select(it => new GraphBlockRef(it.Id, projectId, it.BlockName, it.StationCode))
                .ToList();
            var allowedStations = await _stationPlcScopeService.GetAllowedStationsAsync(projectId);

            // 2026-10-08 A：删除+批量写入包进事务，并拿工站绑定项目锁（与 PLC_GraphStationBindingApp.BindAsync 互斥）
            await _db.Ado.BeginTranAsync();
            try
            {
                await _db.Ado.ExecuteCommandAsync("SELECT pg_advisory_xact_lock(@key)",
                    new SugarParameter("@key", PLC_GraphStationBindingApp.StationBindingLockKey(projectId)));

                // 按工位范围硬删除旧数据，再用 BulkCopy 批量写入。
                // 块绑定表只删自动建议行（含 10-09 之前 reason 为空或打分原因的自动行）；手动 / 已拒绝行保留
                const string manualReason = StationBlockBindingRules.ManualReason;
                const string rejectedReason = StationBlockBindingRules.RejectedReason;
                if (hasStation)
                {
                    await _stationCatalogService.DeleteAsync(it => it.ProjectId == projectId && it.StationCode == sc);
                    await _stationAnchorBindingService.DeleteAsync(it => it.ProjectId == projectId && it.StationCode == sc);
                    await _stationBlockBindingService.DeleteAsync(it => it.ProjectId == projectId && it.StationCode == sc
                        && (it.Reason == null || (it.Reason != manualReason && it.Reason != rejectedReason)));
                    await _stationLogicUnitBindingService.DeleteAsync(it => it.ProjectId == projectId && it.StationCode == sc);
                    // VariablePropagation 无 StationCode 字段，指定工位时不做全量删除
                }
                else
                {
                    await _stationCatalogService.DeleteAsync(it => it.ProjectId == projectId);
                    await _stationAnchorBindingService.DeleteAsync(it => it.ProjectId == projectId);
                    await _stationBlockBindingService.DeleteAsync(it => it.ProjectId == projectId
                        && (it.Reason == null || (it.Reason != manualReason && it.Reason != rejectedReason)));
                    await _stationLogicUnitBindingService.DeleteAsync(it => it.ProjectId == projectId);
                    await _variablePropagationService.DeleteAsync(it => it.ProjectId == projectId);
                }

                var stationCatalogs = BuildStationCatalogs(projectId, stationMap);
                var anchorBindings = BuildAnchorBindings(projectId, stationMap);
                // 自动建议：名字里只有一个工位号的按名字；否则只采用有直接染色证据、无多工位冲突的打分结果。
                // 已手动绑定的块不建议、被拒绝过的「块+工站」不再建议、白名单外不建议、每个块名最多一条
                var keptBindings = await _stationBlockBindingService.GetListAsync(it => it.ProjectId == projectId && it.IsDeleted == false);
                var suggestions = StationBlockBindingRules.BuildSuggestions(
                    projectId, scoredBindings, projectBlocks, keptBindings, allowedStations);
                if (hasStation)
                {
                    // 只重提一个工位时只补该工位的建议，且不给其它工位已有建议的块再加一条
                    var namesWithRows = keptBindings
                        .Where(it => !string.IsNullOrWhiteSpace(it.BlockName))
                        .Select(it => it.BlockName.Trim())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    suggestions = suggestions
                        .Where(it => string.Equals(it.StationCode, sc, StringComparison.OrdinalIgnoreCase)
                            && !namesWithRows.Contains(it.BlockName))
                        .ToList();
                }
                var logicUnitBindings = BuildLogicUnitBindings(projectId, stationColoring);
                var variablePropagations = BuildVariablePropagations(projectId, stationMap);

                if (stationCatalogs.Count > 0)
                {
                    await _stationCatalogService.BulkCopyAsync(stationCatalogs);
                }

                if (anchorBindings.Count > 0)
                {
                    await _stationAnchorBindingService.BulkCopyAsync(anchorBindings);
                }

                if (suggestions.Count > 0)
                {
                    await _stationBlockBindingService.BulkCopyAsync(suggestions);
                }

                if (logicUnitBindings.Count > 0)
                {
                    await _stationLogicUnitBindingService.BulkCopyAsync(logicUnitBindings);
                }

                if (variablePropagations.Count > 0)
                {
                    await _variablePropagationService.BulkCopyAsync(variablePropagations);
                }

                await _db.Ado.CommitTranAsync();
            }
            catch
            {
                await _db.Ado.RollbackTranAsync();
                throw;
            }
        }

        public async Task<StationMapResult> QueryAsync(PersistedStationMapQueryDto query)
        {
            query ??= new PersistedStationMapQueryDto();
            if (query.ProjectId <= 0)
            {
                return new StationMapResult();
            }

            var stationCode = query.StationCode?.Trim();

            var stationCatalogs = string.IsNullOrWhiteSpace(stationCode)
                ? await _stationCatalogService.GetListAsync(it => it.ProjectId == query.ProjectId && it.IsDeleted == false)
                : await _stationCatalogService.GetListAsync(it => it.ProjectId == query.ProjectId && it.IsDeleted == false && it.StationCode == stationCode);

            var stationCodes = stationCatalogs
                .Select(it => it.StationCode)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(stationCode) && stationCodes.Count == 0)
            {
                stationCodes.Add(stationCode);
            }

            var anchorBindings = string.IsNullOrWhiteSpace(stationCode)
                ? await _stationAnchorBindingService.GetListAsync(it => it.ProjectId == query.ProjectId && it.IsDeleted == false)
                : await _stationAnchorBindingService.GetListAsync(it =>
                    it.ProjectId == query.ProjectId &&
                    it.IsDeleted == false &&
                    it.StationCode == stationCode);

            var logicUnitBindings = stationCodes.Count == 0
                ? new List<PLC_StationLogicUnitBindingEntity>()
                : await _stationLogicUnitBindingService.GetListAsync(it =>
                    it.ProjectId == query.ProjectId &&
                    it.IsDeleted == false &&
                    stationCodes.Contains(it.StationCode));

            var blockBindings = stationCodes.Count == 0
                ? new List<PLC_StationBlockBindingEntity>()
                : await _stationBlockBindingService.GetListAsync(it =>
                    it.ProjectId == query.ProjectId &&
                    it.IsDeleted == false &&
                    stationCodes.Contains(it.StationCode));
            // 被拒绝的自动建议不算绑定
            blockBindings = blockBindings
                .Where(it => StationBlockBindingRules.Classify(it.Reason) != StationBindingStatus.Rejected)
                .ToList();

            var variablePropagations = await _variablePropagationService.GetListAsync(it =>
                it.ProjectId == query.ProjectId &&
                it.IsDeleted == false);

            // BulkCopy 底层走 PostgreSQL COPY 命令，会绕过 partial unique index，
            // 可能写入同一 (ProjectId, StationCode) 的多条 is_deleted=false 记录。
            // 这里按 StationCode 去重，取 Confidence 最高的那条作为代表，防止 StationBucket 出现重复工位。
            var deduplicatedCatalogs = stationCatalogs
                .GroupBy(it => it.StationCode?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .Select(g => g.OrderByDescending(x => x.Confidence ?? 0).ThenByDescending(x => x.ModifyTime ?? x.CreateTime).First())
                .ToList();

            var result = new StationMapResult
            {
                StationBuckets = deduplicatedCatalogs
                    .Select(it => BuildStationBucket(
                        it,
                        anchorBindings.Where(binding => string.Equals(binding.StationCode, it.StationCode, StringComparison.OrdinalIgnoreCase)).ToList(),
                        logicUnitBindings.Where(binding => string.Equals(binding.StationCode, it.StationCode, StringComparison.OrdinalIgnoreCase)).ToList(),
                        blockBindings.Where(binding => string.Equals(binding.StationCode, it.StationCode, StringComparison.OrdinalIgnoreCase)).ToList()))
                    .Where(it => it != null)
                    .ToList(),
                AnchorAssignments = anchorBindings
                    .Where(it => !string.IsNullOrWhiteSpace(it.StationCode))
                    .Select(BuildAnchorAssignment)
                    .ToList(),
                StationConflicts = BuildStationConflicts(anchorBindings),
                OrphanAnchors = BuildOrphanAnchors(anchorBindings),
                VariablePropagations = BuildVariablePropagations(variablePropagations, stationCatalogs, stationCode)
            };

            return result;
        }

        public async Task<List<VariablePropagationLink>> QueryVariablePropagationsAsync(long projectId, string stationCode = null)
        {
            if (projectId <= 0)
            {
                return new List<VariablePropagationLink>();
            }

            var normalizedStationCode = stationCode?.Trim();
            var stationCatalogs = string.IsNullOrWhiteSpace(normalizedStationCode)
                ? await _stationCatalogService.GetListAsync(it => it.ProjectId == projectId && it.IsDeleted == false)
                : await _stationCatalogService.GetListAsync(it =>
                    it.ProjectId == projectId
                    && it.IsDeleted == false
                    && it.StationCode == normalizedStationCode);

            List<PLC_VariablePropagationEntity> variablePropagations;
            if (string.IsNullOrWhiteSpace(normalizedStationCode))
            {
                variablePropagations = await _variablePropagationService.GetListAsync(it =>
                    it.ProjectId == projectId
                    && it.IsDeleted == false);
            }
            else
            {
                var variableScope = BuildVariableScope(stationCatalogs, normalizedStationCode);
                variablePropagations = variableScope.Count == 0
                    ? await _variablePropagationService.GetListAsync(it =>
                        it.ProjectId == projectId
                        && it.IsDeleted == false)
                    : await _variablePropagationService.GetListAsync(it =>
                        it.ProjectId == projectId
                        && it.IsDeleted == false
                        && (variableScope.Contains(it.FromVariable) || variableScope.Contains(it.ToVariable)));
            }

            return BuildVariablePropagations(variablePropagations, stationCatalogs, normalizedStationCode);
        }

        private static StationBucket BuildStationBucket(
            PLC_StationCatalogEntity entity,
            IReadOnlyList<PLC_StationAnchorBindingEntity> anchorBindings,
            IReadOnlyList<PLC_StationLogicUnitBindingEntity> logicUnitBindings,
            IReadOnlyList<PLC_StationBlockBindingEntity> blockBindings)
        {
            if (entity == null || string.IsNullOrWhiteSpace(entity.StationCode))
            {
                return null;
            }

            var summary = JsonHelper.DeserializeObject<dynamic>(entity.SummaryJson ?? string.Empty);
            var evidence = DeserializeStringList(entity.EvidenceJson);
            var anchorKeys = anchorBindings?
                .Where(it => !string.IsNullOrWhiteSpace(it.AnchorKey))
                .Select(it => it.AnchorKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            var variableNames = anchorBindings?
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .Select(it => it.VariableName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (anchorKeys.Count == 0)
            {
                anchorKeys = DeserializeStringList(summary?.AnchorKeys?.ToString());
            }

            if (variableNames.Count == 0)
            {
                variableNames = DeserializeStringList(summary?.VariableNames?.ToString());
            }

            var anchorCount = anchorKeys.Count > 0
                ? anchorKeys.Count
                : TryGetInt(summary?.AnchorCount);

            var logicUnitCount = logicUnitBindings?
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .Count() ?? 0;

            if (logicUnitCount == 0)
            {
                logicUnitCount = TryGetInt(summary?.LogicUnitCount);
            }

            var blockCount = blockBindings?
                .Select(it => it.BlockId?.ToString() ?? it.BlockName)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() ?? 0;

            if (blockCount == 0)
            {
                blockCount = TryGetInt(summary?.BlockCount);
            }

            return new StationBucket
            {
                StationCode = entity.StationCode,
                Confidence = entity.Confidence ?? 0,
                AnchorCount = anchorCount,
                LogicUnitCount = logicUnitCount,
                BlockCount = blockCount,
                AnchorKeys = anchorKeys,
                VariableNames = variableNames,
                Evidence = evidence
            };
        }

        private static AnchorStationAssignment BuildAnchorAssignment(PLC_StationAnchorBindingEntity entity)
        {
            return new AnchorStationAssignment
            {
                AnchorKey = entity.AnchorKey,
                VariableName = entity.VariableName,
                AnchorType = entity.AnchorType,
                StationCode = entity.StationCode,
                Confidence = entity.Confidence ?? 0,
                HasConflict = entity.HasConflict ?? false,
                ConflictingStations = DeserializeStringList(entity.ConflictingStationsJson),
                Evidence = DeserializeStringList(entity.EvidenceJson)
            };
        }

        private static List<StationConflict> BuildStationConflicts(IReadOnlyList<PLC_StationAnchorBindingEntity> anchorBindings)
        {
            return (anchorBindings ?? new List<PLC_StationAnchorBindingEntity>())
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode) && (it.HasConflict ?? false))
                .GroupBy(
                    it => !string.IsNullOrWhiteSpace(it.VariableName) ? it.VariableName : it.AnchorKey,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var preferredStation = group
                        .GroupBy(it => it.StationCode, StringComparer.OrdinalIgnoreCase)
                        .Select(it => new
                        {
                            StationCode = it.Key,
                            Confidence = it.Average(x => x.Confidence ?? 0m)
                        })
                        .OrderByDescending(it => it.Confidence)
                        .ThenBy(it => it.StationCode)
                        .FirstOrDefault();

                    return new StationConflict
                    {
                        VariableName = group.FirstOrDefault()?.VariableName,
                        AnchorKeys = group
                            .Where(it => !string.IsNullOrWhiteSpace(it.AnchorKey))
                            .Select(it => it.AnchorKey)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        RecommendedStation = preferredStation?.StationCode,
                        RecommendedConfidence = preferredStation?.Confidence ?? 0m,
                        CandidateStations = group
                            .Select(it => it.StationCode)
                            .Concat(group.SelectMany(it => DeserializeStringList(it.ConflictingStationsJson)))
                            .Where(it => !string.IsNullOrWhiteSpace(it))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        Reason = "存在多个高分工位候选",
                        Evidence = group
                            .SelectMany(it => DeserializeStringList(it.EvidenceJson))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(8)
                            .ToList()
                    };
                })
                .Where(it => it.CandidateStations.Count > 1)
                .ToList();
        }

        private static List<OrphanAnchor> BuildOrphanAnchors(IReadOnlyList<PLC_StationAnchorBindingEntity> anchorBindings)
        {
            return (anchorBindings ?? new List<PLC_StationAnchorBindingEntity>())
                .Where(it => string.IsNullOrWhiteSpace(it.StationCode))
                .Select(it =>
                {
                    var evidence = DeserializeStringList(it.EvidenceJson);
                    var reasonEntry = evidence.FirstOrDefault(item => item.StartsWith(OrphanReasonPrefix, StringComparison.OrdinalIgnoreCase));
                    var reason = !string.IsNullOrWhiteSpace(reasonEntry)
                        ? reasonEntry.Substring(OrphanReasonPrefix.Length)
                        : "未形成稳定工位证据";

                    return new OrphanAnchor
                    {
                        AnchorKey = it.AnchorKey,
                        VariableName = it.VariableName,
                        AnchorType = it.AnchorType,
                        Reason = reason,
                        Evidence = evidence
                            .Where(item => !item.StartsWith(OrphanReasonPrefix, StringComparison.OrdinalIgnoreCase))
                            .ToList()
                    };
                })
                .ToList();
        }

        private static List<VariablePropagationLink> BuildVariablePropagations(
            IReadOnlyList<PLC_VariablePropagationEntity> entities,
            IReadOnlyList<PLC_StationCatalogEntity> stationCatalogs,
            string stationCode)
        {
            var variableScope = new HashSet<string>(BuildVariableScope(stationCatalogs, stationCode), StringComparer.OrdinalIgnoreCase);

            return (entities ?? new List<PLC_VariablePropagationEntity>())
                .Where(it =>
                    string.IsNullOrWhiteSpace(stationCode) ||
                    variableScope.Count == 0 ||
                    variableScope.Contains(it.FromVariable ?? string.Empty) ||
                    variableScope.Contains(it.ToVariable ?? string.Empty))
                .Select(it => new VariablePropagationLink
                {
                    FromVariable = it.FromVariable,
                    ToVariable = it.ToVariable,
                    LogicUnitId = it.LogicUnitId,
                    BlockId = it.BlockId,
                    PropagationType = it.PropagationType,
                    Confidence = it.Confidence ?? 0,
                    Evidence = DeserializeStringList(it.EvidenceJson)
                })
                .ToList();
        }

        private static List<string> BuildVariableScope(IReadOnlyList<PLC_StationCatalogEntity> stationCatalogs, string stationCode)
        {
            if (string.IsNullOrWhiteSpace(stationCode))
            {
                return new List<string>();
            }

            var targetStation = stationCatalogs.FirstOrDefault(it => string.Equals(it.StationCode, stationCode, StringComparison.OrdinalIgnoreCase));
            var variableNamesJson = string.Empty;

            if (targetStation != null)
            {
                try
                {
                    var summary = JsonHelper.DeserializeObject<Dictionary<string, object>>(targetStation.SummaryJson ?? string.Empty);
                    if (summary != null && summary.TryGetValue("VariableNames", out var rawVariableNames))
                    {
                        variableNamesJson = rawVariableNames?.ToString() ?? string.Empty;
                    }
                }
                catch
                {
                    variableNamesJson = string.Empty;
                }
            }

            return DeserializeStringList(variableNamesJson)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Select(it => it.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> DeserializeStringList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                return JsonHelper.DeserializeObject<List<string>>(json) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static int TryGetInt(object value)
        {
            if (value == null)
            {
                return 0;
            }

            return int.TryParse(value.ToString(), out var result) ? result : 0;
        }

        private static List<PLC_StationCatalogEntity> BuildStationCatalogs(long projectId, StationMapResult stationMap)
        {
            var conflictByStation = stationMap.StationConflicts
                .SelectMany(it => it.CandidateStations)
                .GroupBy(it => it, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            return stationMap.StationBuckets
                .Where(bucket => !string.IsNullOrWhiteSpace(bucket.StationCode))
                .GroupBy(bucket => bucket.StationCode, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var bucket = group
                        .OrderByDescending(it => it.AnchorCount)
                        .ThenByDescending(it => it.Confidence)
                        .First();

                    return new PLC_StationCatalogEntity
                    {
                    ProjectId = projectId,
                    StationCode = bucket.StationCode,
                    StationName = bucket.StationCode,
                    Confidence = bucket.Confidence,
                    SourceType = "StationMap",
                    Status = conflictByStation.ContainsKey(bucket.StationCode) ? "Conflict" : "Active",
                    EvidenceJson = JsonHelper.SerializeObject(bucket.Evidence),
                    SummaryJson = JsonHelper.SerializeObject(new
                    {
                        bucket.AnchorCount,
                        bucket.LogicUnitCount,
                        bucket.BlockCount,
                        bucket.AnchorKeys,
                        bucket.VariableNames
                    })
                    };
                })
                .ToList();
        }

        private static List<PLC_StationAnchorBindingEntity> BuildAnchorBindings(long projectId, StationMapResult stationMap)
        {
            var rows = stationMap.AnchorAssignments
                .Select(it => new PLC_StationAnchorBindingEntity
                {
                    ProjectId = projectId,
                    StationCode = it.StationCode,
                    AnchorKey = it.AnchorKey,
                    VariableName = it.VariableName,
                    AnchorType = it.AnchorType,
                    Confidence = it.Confidence,
                    HasConflict = it.HasConflict,
                    ConflictingStationsJson = JsonHelper.SerializeObject(it.ConflictingStations),
                    EvidenceJson = JsonHelper.SerializeObject(it.Evidence)
                })
                .ToList();

            rows.AddRange(stationMap.OrphanAnchors.Select(it => new PLC_StationAnchorBindingEntity
            {
                ProjectId = projectId,
                StationCode = null,
                AnchorKey = it.AnchorKey,
                VariableName = it.VariableName,
                AnchorType = it.AnchorType,
                Confidence = 0m,
                HasConflict = false,
                ConflictingStationsJson = JsonHelper.SerializeObject(new List<string>()),
                EvidenceJson = JsonHelper.SerializeObject(new[] { $"{OrphanReasonPrefix}{it.Reason}" }.Concat(it.Evidence ?? new List<string>()).ToList())
            }));

            return rows;
        }

        private async Task<List<PLC_StationBlockBindingEntity>> BuildBlockBindingsAsync(long projectId, IReadOnlyCollection<LogicUnitStationLabel> stationColoring)
        {
            var directLabels = stationColoring
                .Where(it => it.BlockId.HasValue && !string.IsNullOrWhiteSpace(it.StationCode))
                .ToList();

            var directScores = directLabels
                .GroupBy(it => it.BlockId.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(x => x.StationCode, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            x => x.Key,
                            x => new BlockStationAggregate
                            {
                                DirectScore = x.Sum(y => y.Confidence),
                                Evidence = x.Select(y => new
                                {
                                    y.LogicUnitId,
                                    y.AnchorKey,
                                    y.AnchorVariable,
                                    y.ColoringReason,
                                    y.ConflictingStations
                                }).Cast<object>().ToList(),
                                HasDirectSupport = true,
                                BlockName = x.Select(y => y.BlockName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                            },
                            StringComparer.OrdinalIgnoreCase),
                    EqualityComparer<long>.Default);

            var allBlockIds = directLabels
                .Where(it => it.BlockId.HasValue)
                .Select(it => it.BlockId.Value)
                .Distinct()
                .ToList();

            var blockCalls = await _blockCallService.GetListAsync(it =>
                it.ProjectId == projectId &&
                it.IsDeleted == false);

            var callerBlockIds = blockCalls
                .Where(it => it.CallerBlockId.HasValue)
                .Select(it => it.CallerBlockId.Value);

            allBlockIds = allBlockIds
                .Concat(callerBlockIds)
                .Distinct()
                .ToList();

            var blocks = allBlockIds.Count > 0
                ? await _blockService.GetListAsync(it => it.ProjectId == projectId && it.IsDeleted == false)
                : new List<PLC_BlockEntity>();

            var blockMapById = blocks.ToDictionary(it => it.Id, it => it);
            var blockMapByName = blocks
                .Where(it => !string.IsNullOrWhiteSpace(it.BlockName))
                .GroupBy(it => it.BlockName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            for (var iteration = 0; iteration < 2; iteration++)
            {
                var changed = false;
                foreach (var call in blockCalls)
                {
                    if (!call.CallerBlockId.HasValue || string.IsNullOrWhiteSpace(call.CalleeBlockName))
                    {
                        continue;
                    }

                    if (!directScores.TryGetValue(call.CallerBlockId.Value, out var callerStations) || callerStations.Count == 0)
                    {
                        continue;
                    }

                    if (!blockMapByName.TryGetValue(call.CalleeBlockName, out var calleeBlock))
                    {
                        continue;
                    }

                    if (!directScores.TryGetValue(calleeBlock.Id, out var calleeStations))
                    {
                        calleeStations = new Dictionary<string, BlockStationAggregate>(StringComparer.OrdinalIgnoreCase);
                        directScores[calleeBlock.Id] = calleeStations;
                    }

                    foreach (var station in callerStations)
                    {
                        if (!station.Value.CanPropagate)
                        {
                            continue;
                        }

                        var propagatedScore = Math.Min(station.Value.EffectiveScore * 0.45m, 0.55m);
                        if (propagatedScore < 0.25m)
                        {
                            continue;
                        }

                        if (!calleeStations.TryGetValue(station.Key, out var aggregate))
                        {
                            aggregate = new BlockStationAggregate
                            {
                                BlockName = calleeBlock.BlockName
                            };
                            calleeStations[station.Key] = aggregate;
                        }

                        if (propagatedScore > aggregate.PropagatedScore)
                        {
                            aggregate.PropagatedScore = propagatedScore;
                            changed = true;
                        }

                        aggregate.Evidence.Add(new
                        {
                            call.CallerBlockId,
                            call.CallerBlockName,
                            call.CalleeBlockName,
                            call.LogicUnitId,
                            Reason = "BlockCallPropagation",
                            SourceStation = station.Key,
                            PropagatedScore = Math.Round(propagatedScore, 3)
                        });
                    }
                }

                if (!changed)
                {
                    break;
                }
            }

            var blockStationCount = directScores
                .ToDictionary(
                    g => g.Key,
                    g => g.Value.Values.Count(it => it.EffectiveScore >= 0.25m),
                    EqualityComparer<long>.Default);

            return directScores
                .SelectMany(block =>
                {
                    var stationCandidates = block.Value
                        .Where(it => it.Value.EffectiveScore >= 0.25m)
                        .Select(it => new
                        {
                            BlockId = block.Key,
                            StationCode = it.Key,
                            Aggregate = it.Value
                        })
                        .OrderByDescending(it => it.Aggregate.EffectiveScore)
                        .ToList();

                    if (stationCandidates.Count == 0)
                    {
                        return Enumerable.Empty<PLC_StationBlockBindingEntity>();
                    }

                    var winner = stationCandidates[0];
                    var runnerUp = stationCandidates.Skip(1).FirstOrDefault();
                    var hasConflict = runnerUp != null &&
                        runnerUp.Aggregate.EffectiveScore >= Math.Max(0.30m, winner.Aggregate.EffectiveScore - 0.12m);

                    var onlyPropagation = !stationCandidates.Any(it => it.Aggregate.HasDirectSupport);
                    if (onlyPropagation && winner.Aggregate.EffectiveScore < 0.60m)
                    {
                        return Enumerable.Empty<PLC_StationBlockBindingEntity>();
                    }

                    if (onlyPropagation && !hasConflict)
                    {
                        return new[]
                        {
                            CreateBlockBinding(projectId, winner.BlockId, winner.StationCode, winner.Aggregate,
                                blockMapById, false, "ReviewRequired:BlockCallOnly")
                        };
                    }

                    if (!hasConflict)
                    {
                        var reason = winner.Aggregate.HasDirectSupport
                            ? "DerivedFromStationColoring+BlockCall"
                            : "ReviewRequired:BlockCallOnly";

                        return new[]
                        {
                            CreateBlockBinding(projectId, winner.BlockId, winner.StationCode, winner.Aggregate,
                                blockMapById, false, reason)
                        };
                    }

                    return stationCandidates
                        .Where(it => it.Aggregate.EffectiveScore >= Math.Max(0.30m, winner.Aggregate.EffectiveScore - 0.12m))
                        .Select(it => CreateBlockBinding(projectId, it.BlockId, it.StationCode, it.Aggregate,
                            blockMapById, true, onlyPropagation ? "ReviewRequired:ConflictingPropagation" : "SharedBlock:ConflictingStations"));
                })
                .ToList();
        }

        private static PLC_StationBlockBindingEntity CreateBlockBinding(
            long projectId,
            long blockId,
            string stationCode,
            BlockStationAggregate aggregate,
            IReadOnlyDictionary<long, PLC_BlockEntity> blockMapById,
            bool isShared,
            string reason)
        {
            return new PLC_StationBlockBindingEntity
            {
                ProjectId = projectId,
                StationCode = stationCode,
                BlockId = blockId,
                BlockName = aggregate.BlockName ?? (blockMapById.TryGetValue(blockId, out var block) ? block.BlockName : null),
                Confidence = Math.Round(aggregate.EffectiveScore, 3),
                Reason = reason,
                IsShared = isShared,
                EvidenceJson = JsonHelper.SerializeObject(aggregate.Evidence.Take(12).ToList())
            };
        }

        private sealed class BlockStationAggregate
        {
            public decimal DirectScore { get; set; }

            public decimal PropagatedScore { get; set; }

            public bool HasDirectSupport { get; set; }

            public string BlockName { get; set; }

            public List<object> Evidence { get; set; } = new List<object>();

            public decimal EffectiveScore => Math.Min(DirectScore + (PropagatedScore * 0.65m), 0.98m);

            public bool CanPropagate => DirectScore >= 0.30m || (HasDirectSupport && EffectiveScore >= 0.45m);
        }

        private static List<PLC_StationLogicUnitBindingEntity> BuildLogicUnitBindings(long projectId, IReadOnlyCollection<LogicUnitStationLabel> stationColoring)
        {
            return stationColoring
                .Where(it => !string.IsNullOrWhiteSpace(it.StationCode))
                .Select(it => new PLC_StationLogicUnitBindingEntity
                {
                    ProjectId = projectId,
                    StationCode = it.StationCode,
                    LogicUnitId = it.LogicUnitId,
                    BlockId = it.BlockId,
                    Confidence = it.Confidence,
                    Reason = it.ColoringReason,
                    ConflictingStationsJson = JsonHelper.SerializeObject(it.ConflictingStations),
                    EvidenceJson = JsonHelper.SerializeObject(new
                    {
                        it.BlockName,
                        it.NetworkTitle,
                        it.AnchorKey,
                        it.AnchorVariable
                    })
                })
                .ToList();
        }

            private const string OrphanReasonPrefix = "ORPHAN_REASON:";

        private static List<PLC_VariablePropagationEntity> BuildVariablePropagations(long projectId, StationMapResult stationMap)
        {
            return stationMap.VariablePropagations
                .Select(it => new PLC_VariablePropagationEntity
                {
                    ProjectId = projectId,
                    FromVariable = it.FromVariable,
                    ToVariable = it.ToVariable,
                    LogicUnitId = it.LogicUnitId,
                    BlockId = it.BlockId,
                    PropagationType = it.PropagationType,
                    Confidence = it.Confidence,
                    EvidenceJson = JsonHelper.SerializeObject(it.Evidence)
                })
                .ToList();
        }
    }
}