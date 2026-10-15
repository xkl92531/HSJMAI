using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding
{
    /// <summary>
    /// GRAPH 程序块与工站批量绑定应用服务（批量绑定弹框）。
    /// 绑定落库到 ai_plc_stationblockbinding（ProjectId + StationCode + BlockId/BlockName），
    /// 与 GRAPH 步位注册的工站分组消费方（GraphStepTagRegistrationJob 按 BlockName+ProjectId 匹配）同源；
    /// 绑定/解绑同步维护 ai_plc_block_new.stationcode，保证 GRAPH 结构管理页按工站过滤一致。
    /// </summary>
    public class PLC_GraphStationBindingApp : ISelfScopedAutoInject
    {
        /// <summary>手动绑定行的 Reason 标记（工位提取持久化据此保留人工绑定，勿改文案）</summary>
        public const string ManualReason = StationBlockBindingRules.ManualReason;

        /// <summary>工站绑定写入咨询锁盐值（BindAsync 与 StationMapPersistenceService.PersistAsync 共用同口径）</summary>
        public const long StationBindingLockSalt = 0x535442314C4F434BL;

        /// <summary>按项目生成工站绑定写入的咨询锁键（升序加锁防死锁）</summary>
        public static long StationBindingLockKey(long projectId) => StationBindingLockSalt ^ projectId;

        /// <summary>单批块数量上限（防御异常大请求）</summary>
        private const int MaxBlockIdsPerRequest = 500;

        /// <summary>绑定名 IN 查询分块大小（与 PLC_GraphBlockApp 一致的量级）</summary>
        private const int NameChunkSize = 400;

        private readonly ISqlSugarClient _db;
        private readonly ILogger<PLC_GraphStationBindingApp> _logger;
        private readonly StationPlcScopeService _stationPlcScopeService;
        private readonly GraphStepRegistrationTrigger _registrationTrigger;

        public PLC_GraphStationBindingApp(
            ISqlSugarClient db,
            ILogger<PLC_GraphStationBindingApp> logger,
            StationPlcScopeService stationPlcScopeService,
            GraphStepRegistrationTrigger registrationTrigger)
        {
            _db = db;
            _logger = logger;
            _stationPlcScopeService = stationPlcScopeService;
            _registrationTrigger = registrationTrigger;
        }

        /// <summary>
        /// 查询 GRAPH 程序块及其全部有效工站绑定（按块编号升序）。
        /// projectId &gt; 0 时仅返回该项目；绑定匹配口径与步位注册消费方一致：ProjectId + BlockName（大小写不敏感）。
        /// </summary>
        public async Task<HttpResponseResultModel<List<PLC_GraphStationBindingBlockDTO>>> GetBlocksAsync(long projectId = 0)
        {
            var response = new HttpResponseResultModel<List<PLC_GraphStationBindingBlockDTO>>();
            try
            {
                var blocks = await _db.Queryable<PLC_BlockEntity>()
                    .Where(b => b.IsDeleted == false && b.ProgrammingLanguage == "GRAPH")
                    .WhereIF(projectId > 0, b => b.ProjectId == projectId)
                    .OrderBy(b => b.BlockNumber, OrderByType.Asc)
                    .Select(b => new { b.Id, b.ProjectId, b.BlockName, b.BlockType, b.BlockNumber })
                    .Take(2000)
                    .ToListAsync()
                    .ConfigureAwait(false);

                var result = blocks
                    .Select(b => new PLC_GraphStationBindingBlockDTO
                    {
                        BlockId = b.Id,
                        BlockName = b.BlockName,
                        BlockType = b.BlockType,
                        BlockNumber = b.BlockNumber,
                        PlcProjectId = b.ProjectId ?? 0L
                    })
                    .ToList();

                await AttachBindingsAsync(result).ConfigureAwait(false);

                response.BackResult = result;
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH工站绑定] 查询块绑定失败 ProjectId={ProjectId}", projectId);
                response.IsSuccess = false;
                response.ErrorMessage = "查询失败: " + ex.Message;
            }
            return response;
        }

        /// <summary>
        /// 批量绑定：把一批 GRAPH 块绑到目标工站。
        /// 逐块按其自身项目做工位白名单校验（StationPlcScopeService：防跨 PLC 误绑）；
        /// 块已绑其它工站时换绑（软删旧绑定 + 新增目标绑定）；
        /// 已绑目标工站的块跳过；同步把块 stationcode 更新为目标工站。
        /// 有改动的项目在提交后排队一次 GRAPH 步位重新注册（RegistrationMessage 说明结果）。
        /// </summary>
        public async Task<HttpResponseResultModel<PLC_GraphStationBindingBindResultDTO>> BindAsync(PLC_GraphStationBindingBindRequestDTO request)
        {
            var (response, changedProjectIds) = await BindCoreAsync(request).ConfigureAwait(false);
            if (response.IsSuccess && response.BackResult != null)
            {
                response.BackResult.RegistrationMessage = RequestReregistration(changedProjectIds);
            }
            return response;
        }

        private async Task<(HttpResponseResultModel<PLC_GraphStationBindingBindResultDTO> Response, List<long> ChangedProjectIds)> BindCoreAsync(
            PLC_GraphStationBindingBindRequestDTO request)
        {
            var response = new HttpResponseResultModel<PLC_GraphStationBindingBindResultDTO>();
            var summary = new PLC_GraphStationBindingBindResultDTO();
            var changedProjectIds = new List<long>();
            try
            {
                request ??= new PLC_GraphStationBindingBindRequestDTO();
                var stationCode = request.StationCode?.Trim();
                var blockIds = (request.BlockIds ?? new List<long>())
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                if (string.IsNullOrEmpty(stationCode) || blockIds.Count == 0)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = "stationCode 与 blockIds 均不能为空";
                    return (response, changedProjectIds);
                }
                if (blockIds.Count > MaxBlockIdsPerRequest)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = $"单次最多绑定 {MaxBlockIdsPerRequest} 个块";
                    return (response, changedProjectIds);
                }

                // 工站必须存在于基础资料（Basic_WorkStation 是带引号 PascalCase 表，走原生 SQL），
                // 以库内 Code 大小写为准回写绑定
                const string stationSql = @"SELECT ""Id"" AS ""Id"", ""Code"" AS ""Code"", ""Name"" AS ""Name""
FROM ""Basic_WorkStation""
WHERE ""IsDeleted"" = false AND LOWER(""Code"") = LOWER(@code) LIMIT 1";
                var stationRows = await _db.Ado.SqlQueryAsync<dynamic>(stationSql,
                    new SugarParameter("@code", stationCode)).ConfigureAwait(false);
                if (stationRows == null || stationRows.Count == 0)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = $"工站 {stationCode} 不存在（基础资料 Basic_WorkStation）";
                    return (response, changedProjectIds);
                }
                var stationDict = (IDictionary<string, object>)stationRows[0];
                if (stationDict.TryGetValue("Code", out var canonicalCode)
                    && !string.IsNullOrWhiteSpace(Convert.ToString(canonicalCode)))
                {
                    stationCode = Convert.ToString(canonicalCode).Trim();
                }

                var blocks = await _db.Queryable<PLC_BlockEntity>()
                    .Where(b => b.IsDeleted == false
                        && b.ProgrammingLanguage == "GRAPH"
                        && blockIds.Contains(b.Id))
                    .OrderBy(b => b.BlockNumber, OrderByType.Asc)
                    .ToListAsync()
                    .ConfigureAwait(false);
                var blocksById = blocks.ToDictionary(b => b.Id);

                var operatorId = CurrentUserInfo.Instance.UserID;
                var operatorName = ResolveOperatorName();
                var now = DateTime.Now;
                var whitelistCache = new Dictionary<long, HashSet<string>>();
                var toInsert = new List<PLC_StationBlockBindingEntity>();
                var oldBindingIds = new List<long>();
                var blockIdsToStamp = new List<long>();

                // 2026-10-08 A：existing 检查到落库全程包事务，并按项目拿咨询锁——
                // 与工位提取（StationMapPersistenceService.PersistAsync）互斥，防止读到
                // 提取「块工位码已盖、绑定行未落库」的中间窗口而产生重复绑定（多项目按 id 升序加锁防死锁）
                await _db.Ado.BeginTranAsync().ConfigureAwait(false);
                try
                {
                    foreach (var lockProjectId in blocks.Where(b => b.ProjectId.HasValue)
                        .Select(b => b.ProjectId.Value).Distinct().OrderBy(x => x))
                    {
                        await _db.Ado.ExecuteCommandAsync("SELECT pg_advisory_xact_lock(@key)",
                            new SugarParameter("@key", StationBindingLockKey(lockProjectId))).ConfigureAwait(false);
                    }

                    var validBlocks = new List<GraphBlockRef>();
                    foreach (var blockId in blockIds)
                    {
                        if (!blocksById.TryGetValue(blockId, out var block))
                        {
                            AddSkip(summary, $"#{blockId}", "块不存在或不是 GRAPH 块");
                            continue;
                        }
                        if (!block.ProjectId.HasValue)
                        {
                            AddSkip(summary, block.BlockName, "块缺少项目归属");
                            continue;
                        }

                        var allowed = await GetAllowedStationsCachedAsync(whitelistCache, block.ProjectId.Value).ConfigureAwait(false);
                        if (allowed != null && !StationPlcScopeService.IsAllowed(stationCode, allowed))
                        {
                            AddSkip(summary, block.BlockName, "不在该 PLC 的工位白名单（Basic_SubStation.PlcName）");
                            continue;
                        }

                        validBlocks.Add(new GraphBlockRef(block.Id, block.ProjectId.Value, block.BlockName ?? string.Empty, block.StationCode));
                    }

                    // 2026-10-14：按「项目 + 块名」一次性规划——以前逐块查库判断「已绑定」，同名块一起勾选时
                    // 两块都判为未绑定，插出多条手动行。现在同名只插一条，同名下其它工站行、自动建议、
                    // 已拒绝行和以前重复插入的手动行一并软删（StationBlockBindingRules.PlanManualBind）
                    var existing = await LoadLiveBindingsAsync(validBlocks).ConfigureAwait(false);
                    var plan = StationBlockBindingRules.PlanManualBind(stationCode, validBlocks, existing, operatorId, operatorName, now);
                    foreach (var (skippedName, skipReason) in plan.Skipped)
                    {
                        AddSkip(summary, skippedName, skipReason);
                    }
                    toInsert.AddRange(plan.ToInsert);
                    oldBindingIds.AddRange(plan.SoftDeleteIds.Distinct());
                    blockIdsToStamp.AddRange(plan.BlocksToStamp.Select(b => b.BlockId).Distinct());
                    changedProjectIds.AddRange(plan.ChangedProjectIds);

                    if (oldBindingIds.Count > 0)
                    {
                        await SoftDeleteBindingsAsync(oldBindingIds, operatorId, operatorName).ConfigureAwait(false);
                    }

                    if (toInsert.Count > 0)
                    {
                        await _db.Insertable(toInsert).ExecuteCommandAsync().ConfigureAwait(false);
                    }

                    if (blockIdsToStamp.Count > 0)
                    {
                        await _db.Updateable<PLC_BlockEntity>()
                            .Where(b => blockIdsToStamp.Contains(b.Id) && b.IsDeleted == false)
                            .SetColumns(b => new PLC_BlockEntity
                            {
                                StationCode = stationCode,
                                ModifierId = operatorId,
                                ModifierName = operatorName,
                                ModifyTime = DateTime.Now
                            })
                            .ExecuteCommandAsync()
                            .ConfigureAwait(false);
                    }

                    await _db.Ado.CommitTranAsync().ConfigureAwait(false);
                }
                catch
                {
                    await _db.Ado.RollbackTranAsync().ConfigureAwait(false);
                    throw;
                }

                summary.BoundCount = toInsert.Count;
                summary.MovedCount = oldBindingIds.Count;
                summary.UpdatedBlockCount = blockIdsToStamp.Count;
                summary.SkippedCount = summary.Skipped.Count;

                _logger.LogInformation(
                    "[GRAPH工站绑定] 批量绑定完成 Station={Station} 新增={Bound} 换绑移除={Moved} 块stationcode更新={Stamped} 跳过={Skipped}",
                    stationCode, summary.BoundCount, summary.MovedCount, summary.UpdatedBlockCount, summary.SkippedCount);

                response.BackResult = summary;
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH工站绑定] 批量绑定失败 Station={Station}", request?.StationCode);
                response.IsSuccess = false;
                response.ErrorMessage = "绑定失败: " + ex.Message;
            }
            return (response, changedProjectIds);
        }

        /// <summary>
        /// 解绑：手动行按 Id 软删除，自动建议行改记「已拒绝」；块 stationcode 回退到剩余有效绑定
        /// 或逻辑单元染色结果，都没有才清空（回到右侧未绑定区）。
        /// </summary>
        public async Task<HttpResponseResultModel<PLC_GraphStationBindingUnbindResultDTO>> UnbindAsync(PLC_GraphStationBindingUnbindRequestDTO request)
        {
            var response = new HttpResponseResultModel<PLC_GraphStationBindingUnbindResultDTO>();
            var summary = new PLC_GraphStationBindingUnbindResultDTO();
            try
            {
                request ??= new PLC_GraphStationBindingUnbindRequestDTO();
                var bindingIds = (request.BindingIds ?? new List<long>())
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
                if (bindingIds.Count == 0)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = "bindingIds 不能为空";
                    return response;
                }

                var rows = await _db.Queryable<PLC_StationBlockBindingEntity>()
                    .Where(x => x.IsDeleted == false && bindingIds.Contains(x.Id))
                    .ToListAsync()
                    .ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    response.BackResult = summary;
                    response.IsSuccess = true;
                    return response;
                }

                var operatorId = CurrentUserInfo.Instance.UserID;
                var operatorName = ResolveOperatorName();

                // 2026-10-14：解绑自动建议 = 拒绝它（行保留、改记「已拒绝」，下次提取不再建议同一工站）；
                // 手动行和已拒绝行照旧软删
                var suggestionIds = rows
                    .Where(x => StationBlockBindingRules.Classify(x.Reason) == StationBindingStatus.Suggested)
                    .Select(x => x.Id)
                    .ToList();
                var deleteIds = rows.Select(x => x.Id).Except(suggestionIds).ToList();
                if (deleteIds.Count > 0)
                {
                    await SoftDeleteBindingsAsync(deleteIds, operatorId, operatorName).ConfigureAwait(false);
                }
                if (suggestionIds.Count > 0)
                {
                    await MarkRejectedAsync(suggestionIds, operatorId, operatorName).ConfigureAwait(false);
                }

                // 块工位不再直接清空：回退到剩余有效绑定 → 该块逻辑单元染色最多的工位，都没有才清空
                var affectedKeys = rows
                    .Where(x => x.ProjectId.HasValue && !string.IsNullOrWhiteSpace(x.BlockName))
                    .Select(x => new { ProjectId = x.ProjectId.Value, Name = x.BlockName.Trim().ToLowerInvariant() })
                    .Distinct()
                    .ToList();
                var whitelistCache = new Dictionary<long, HashSet<string>>();
                foreach (var key in affectedKeys)
                {
                    var (restored, cleared) = await RestoreBlockStationAsync(key.ProjectId, key.Name, whitelistCache, operatorId, operatorName).ConfigureAwait(false);
                    summary.RestoredBlockCount += restored;
                    summary.ClearedBlockCount += cleared;
                }

                summary.UnboundCount = rows.Count;
                summary.RejectedCount = suggestionIds.Count;
                summary.RegistrationMessage = RequestReregistration(affectedKeys.Select(k => k.ProjectId));
                _logger.LogInformation("[GRAPH工站绑定] 解绑完成 解绑={Unbound} 其中拒绝建议={Rejected} 块工位回退={Restored} 清空块工位={Cleared}",
                    rows.Count, summary.RejectedCount, summary.RestoredBlockCount, summary.ClearedBlockCount);

                response.BackResult = summary;
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH工站绑定] 解绑失败");
                response.IsSuccess = false;
                response.ErrorMessage = "解绑失败: " + ex.Message;
            }
            return response;
        }

        /// <summary>
        /// 接受自动建议：把选中的建议行按其工站转成手动绑定（走与批量绑定相同的白名单校验与同名去重）。
        /// </summary>
        public async Task<HttpResponseResultModel<PLC_GraphStationBindingBindResultDTO>> AcceptSuggestionsAsync(PLC_GraphStationBindingUnbindRequestDTO request)
        {
            var response = new HttpResponseResultModel<PLC_GraphStationBindingBindResultDTO>();
            var total = new PLC_GraphStationBindingBindResultDTO();
            var acceptedChangedProjectIds = new List<long>();
            try
            {
                var ids = (request?.BindingIds ?? new List<long>()).Where(id => id > 0).Distinct().ToList();
                if (ids.Count == 0)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = "bindingIds 不能为空";
                    return response;
                }

                var suggestions = (await _db.Queryable<PLC_StationBlockBindingEntity>()
                        .Where(x => x.IsDeleted == false && ids.Contains(x.Id))
                        .ToListAsync()
                        .ConfigureAwait(false))
                    .Where(x => StationBlockBindingRules.Classify(x.Reason) == StationBindingStatus.Suggested
                        && x.ProjectId.HasValue
                        && !string.IsNullOrWhiteSpace(x.StationCode)
                        && !string.IsNullOrWhiteSpace(x.BlockName))
                    .ToList();

                foreach (var group in suggestions.GroupBy(x => x.StationCode.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    var projectIds = group.Select(x => x.ProjectId.Value).Distinct().ToList();
                    var lowerNames = group.Select(x => x.BlockName.Trim().ToLowerInvariant()).Distinct().ToList();
                    var blockIds = await _db.Queryable<PLC_BlockEntity>()
                        .Where(b => b.IsDeleted == false
                            && b.ProgrammingLanguage == "GRAPH"
                            && b.ProjectId != null
                            && projectIds.Contains(b.ProjectId.Value)
                            && lowerNames.Contains(SqlFunc.ToLower(b.BlockName)))
                        .Select(b => b.Id)
                        .ToListAsync()
                        .ConfigureAwait(false);
                    if (blockIds.Count == 0)
                    {
                        foreach (var name in group.Select(x => x.BlockName.Trim()).Distinct())
                        {
                            AddSkip(total, name, "找不到对应的 GRAPH 块");
                        }
                        continue;
                    }

                    // 分批按工站绑定，重新注册在全部批次结束后统一排一次
                    var (one, changed) = await BindCoreAsync(new PLC_GraphStationBindingBindRequestDTO { StationCode = group.Key, BlockIds = blockIds }).ConfigureAwait(false);
                    if (one.IsSuccess)
                    {
                        acceptedChangedProjectIds.AddRange(changed);
                    }
                    if (!one.IsSuccess || one.BackResult == null)
                    {
                        foreach (var name in group.Select(x => x.BlockName.Trim()).Distinct())
                        {
                            AddSkip(total, name, one.ErrorMessage ?? "接受建议失败");
                        }
                        continue;
                    }
                    total.BoundCount += one.BackResult.BoundCount;
                    total.MovedCount += one.BackResult.MovedCount;
                    total.UpdatedBlockCount += one.BackResult.UpdatedBlockCount;
                    foreach (var skip in one.BackResult.Skipped)
                    {
                        AddSkip(total, skip.BlockName, skip.Reason);
                    }
                }

                total.SkippedCount = total.Skipped.Count;
                total.RegistrationMessage = RequestReregistration(acceptedChangedProjectIds);
                response.BackResult = total;
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH工站绑定] 接受建议失败");
                response.IsSuccess = false;
                response.ErrorMessage = "接受建议失败: " + ex.Message;
            }
            return response;
        }

        /// <summary>拒绝自动建议：行保留并改记「已拒绝」，以后提取不再把同一块建议到同一工站。</summary>
        public async Task<HttpResponseResultModel<PLC_GraphStationBindingUnbindResultDTO>> RejectSuggestionsAsync(PLC_GraphStationBindingUnbindRequestDTO request)
        {
            var response = new HttpResponseResultModel<PLC_GraphStationBindingUnbindResultDTO>();
            var summary = new PLC_GraphStationBindingUnbindResultDTO();
            try
            {
                var ids = (request?.BindingIds ?? new List<long>()).Where(id => id > 0).Distinct().ToList();
                if (ids.Count == 0)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = "bindingIds 不能为空";
                    return response;
                }

                var suggestionRows = (await _db.Queryable<PLC_StationBlockBindingEntity>()
                        .Where(x => x.IsDeleted == false && ids.Contains(x.Id))
                        .ToListAsync()
                        .ConfigureAwait(false))
                    .Where(x => StationBlockBindingRules.Classify(x.Reason) == StationBindingStatus.Suggested)
                    .ToList();
                var suggestionIds = suggestionRows.Select(x => x.Id).ToList();
                if (suggestionIds.Count > 0)
                {
                    await MarkRejectedAsync(suggestionIds, CurrentUserInfo.Instance.UserID, ResolveOperatorName()).ConfigureAwait(false);
                }

                summary.RejectedCount = suggestionIds.Count;
                // 被拒绝的建议可能正是块当前的有效工站，IGW 分组也要跟着更新
                summary.RegistrationMessage = RequestReregistration(
                    suggestionRows.Where(x => x.ProjectId.HasValue).Select(x => x.ProjectId.Value));
                response.BackResult = summary;
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH工站绑定] 拒绝建议失败");
                response.IsSuccess = false;
                response.ErrorMessage = "拒绝建议失败: " + ex.Message;
            }
            return response;
        }

        /// <summary>按「项目 + 块名」读出这批块现有的全部有效绑定行（块名不区分大小写）</summary>
        private async Task<List<PLC_StationBlockBindingEntity>> LoadLiveBindingsAsync(IReadOnlyCollection<GraphBlockRef> blocks)
        {
            var result = new List<PLC_StationBlockBindingEntity>();
            foreach (var projectGroup in blocks.GroupBy(b => b.ProjectId))
            {
                var projectId = projectGroup.Key;
                var lowerNames = projectGroup
                    .Select(b => (b.BlockName ?? string.Empty).Trim().ToLowerInvariant())
                    .Where(n => n.Length > 0)
                    .Distinct()
                    .ToList();
                foreach (var chunk in lowerNames.Chunk(NameChunkSize))
                {
                    var names = chunk.ToList();
                    result.AddRange(await _db.Queryable<PLC_StationBlockBindingEntity>()
                        .Where(x => x.IsDeleted == false
                            && x.ProjectId == projectId
                            && names.Contains(SqlFunc.ToLower(x.BlockName)))
                        .ToListAsync()
                        .ConfigureAwait(false));
                }
            }
            return result;
        }

        /// <summary>解绑后按剩余绑定 / 逻辑单元染色回填同名块的工位；返回（回填成其它工位的块数，清空的块数）</summary>
        private async Task<(int Restored, int Cleared)> RestoreBlockStationAsync(
            long projectId,
            string lowerBlockName,
            Dictionary<long, HashSet<string>> whitelistCache,
            long operatorId,
            string operatorName)
        {
            var remaining = await _db.Queryable<PLC_StationBlockBindingEntity>()
                .Where(x => x.IsDeleted == false && x.ProjectId == projectId && SqlFunc.ToLower(x.BlockName) == lowerBlockName)
                .ToListAsync()
                .ConfigureAwait(false);
            var blocks = await _db.Queryable<PLC_BlockEntity>()
                .Where(b => b.IsDeleted == false && b.ProjectId == projectId && SqlFunc.ToLower(b.BlockName) == lowerBlockName)
                .ToListAsync()
                .ConfigureAwait(false);
            if (blocks.Count == 0)
            {
                return (0, 0);
            }

            var blockIds = blocks.Select(b => b.Id).ToList();
            var luStations = await _db.Queryable<PLC_StationLogicUnitBindingEntity>()
                .Where(x => x.IsDeleted == false && x.ProjectId == projectId && x.BlockId != null && blockIds.Contains(x.BlockId.Value))
                .Select(x => new { x.StationCode, x.Confidence })
                .ToListAsync()
                .ConfigureAwait(false);
            var allowed = await GetAllowedStationsCachedAsync(whitelistCache, projectId).ConfigureAwait(false);
            var station = StationBlockBindingRules.ResolveStationAfterUnbind(
                remaining,
                luStations.Select(x => (x.StationCode, x.Confidence)),
                allowed) ?? string.Empty;

            var changedIds = blocks
                .Where(b => !string.Equals((b.StationCode ?? string.Empty).Trim(), station, StringComparison.OrdinalIgnoreCase))
                .Select(b => b.Id)
                .ToList();
            if (changedIds.Count == 0)
            {
                return (0, 0);
            }

            await _db.Updateable<PLC_BlockEntity>()
                .Where(b => changedIds.Contains(b.Id) && b.IsDeleted == false)
                .SetColumns(b => new PLC_BlockEntity
                {
                    StationCode = station,
                    ModifierId = operatorId,
                    ModifierName = operatorName,
                    ModifyTime = DateTime.Now
                })
                .ExecuteCommandAsync()
                .ConfigureAwait(false);
            return station.Length == 0 ? (0, changedIds.Count) : (changedIds.Count, 0);
        }

        /// <summary>对有改动的项目排队 GRAPH 步位重新注册，返回给前端的一句话说明（排队失败不影响绑定结果）</summary>
        private string RequestReregistration(IEnumerable<long> projectIds)
        {
            var results = _registrationTrigger.Request(projectIds);
            foreach (var failed in results.Where(r => r.Error != null))
            {
                _logger.LogWarning("[GRAPH工站绑定] 项目 {ProjectId} 重新注册排队失败: {Error}", failed.ProjectId, failed.Error);
            }
            return GraphStepRegistrationTrigger.Describe(results);
        }

        /// <summary>按 Id 把自动建议行改记为「已拒绝」（行保留，供以后提取跳过）</summary>
        private async Task MarkRejectedAsync(List<long> ids, long operatorId, string operatorName)
        {
            await _db.Updateable<PLC_StationBlockBindingEntity>()
                .Where(x => ids.Contains(x.Id) && x.IsDeleted == false)
                .SetColumns(x => new PLC_StationBlockBindingEntity
                {
                    Reason = StationBlockBindingRules.RejectedReason,
                    ModifierId = operatorId,
                    ModifierName = operatorName,
                    ModifyTime = DateTime.Now
                })
                .ExecuteCommandAsync()
                .ConfigureAwait(false);
        }

        /// <summary>按 Id 批量软删绑定行</summary>
        private async Task SoftDeleteBindingsAsync(List<long> ids, long operatorId, string operatorName)
        {
            await _db.Updateable<PLC_StationBlockBindingEntity>()
                .Where(x => ids.Contains(x.Id) && x.IsDeleted == false)
                .SetColumns(x => new PLC_StationBlockBindingEntity
                {
                    IsDeleted = true,
                    ModifierId = operatorId,
                    ModifierName = operatorName,
                    ModifyTime = DateTime.Now
                })
                .ExecuteCommandAsync()
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 为块列表附加有效绑定（ProjectId + BlockName 匹配，与 GraphStepTagRegistrationJob 消费口径一致）。
        /// IN 查询按块名分块，避免超长参数列表。
        /// </summary>
        private async Task AttachBindingsAsync(List<PLC_GraphStationBindingBlockDTO> blocks)
        {
            var projectIds = blocks.Select(b => b.PlcProjectId).Where(id => id > 0).Distinct().ToList();
            var names = blocks.Select(b => b.BlockName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            if (projectIds.Count == 0 || names.Count == 0)
            {
                return;
            }

            var bindingRows = new List<PLC_StationBlockBindingEntity>();
            foreach (var nameChunk in names.Chunk(NameChunkSize))
            {
                var chunk = nameChunk.ToList();
                bindingRows.AddRange(await _db.Queryable<PLC_StationBlockBindingEntity>()
                    .Where(x => x.IsDeleted == false
                        && x.ProjectId != null
                        && projectIds.Contains(x.ProjectId.Value)
                        && chunk.Contains(x.BlockName))
                    .Select(x => new PLC_StationBlockBindingEntity
                    {
                        Id = x.Id,
                        ProjectId = x.ProjectId,
                        BlockId = x.BlockId,
                        BlockName = x.BlockName,
                        StationCode = x.StationCode,
                        Reason = x.Reason,
                        Confidence = x.Confidence,
                        IsShared = x.IsShared,
                        CreateTime = x.CreateTime
                    })
                    .ToListAsync()
                    .ConfigureAwait(false));
            }

            // 已拒绝的建议不算绑定，单独列在 RejectedStations
            var rejectedByProjectAndName = bindingRows
                .Where(x => x.ProjectId.HasValue && !string.IsNullOrWhiteSpace(x.StationCode)
                    && StationBlockBindingRules.Classify(x.Reason) == StationBindingStatus.Rejected)
                .GroupBy(x => (ProjectId: x.ProjectId.Value, Name: (x.BlockName ?? string.Empty).Trim().ToLowerInvariant()))
                .ToDictionary(g => g.Key, g => g.Select(x => x.StationCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

            // 键：ProjectId + 小写块名（绑定匹配大小写不敏感）；组内排序：手动绑定优先，再按绑定时间倒序
            var byProjectAndName = bindingRows
                .Where(x => x.ProjectId.HasValue && !string.IsNullOrWhiteSpace(x.StationCode)
                    && StationBlockBindingRules.Classify(x.Reason) != StationBindingStatus.Rejected)
                .GroupBy(x => (ProjectId: x.ProjectId.Value, Name: (x.BlockName ?? string.Empty).Trim().ToLowerInvariant()))
                .ToDictionary(g => g.Key, g => g
                    .OrderByDescending(x => string.Equals(x.Reason, ManualReason, StringComparison.Ordinal))
                    .ThenByDescending(x => x.CreateTime ?? DateTime.MinValue)
                    .ToList());

            foreach (var block in blocks)
            {
                if (block.PlcProjectId <= 0 || string.IsNullOrEmpty(block.BlockName))
                {
                    continue;
                }
                var key = (ProjectId: block.PlcProjectId, Name: block.BlockName.Trim().ToLowerInvariant());
                if (rejectedByProjectAndName.TryGetValue(key, out var rejectedStations))
                {
                    block.RejectedStations = rejectedStations;
                }
                if (!byProjectAndName.TryGetValue(key, out var bindings))
                {
                    continue;
                }
                // 同一工站的重复行（以前同名块重复绑定留下的）只显示一条，手动行优先
                block.Bindings = bindings
                    .GroupBy(x => x.StationCode.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Select(x => new PLC_GraphStationBindingItemDTO
                    {
                        BindingId = x.Id,
                        StationCode = x.StationCode?.Trim(),
                        Reason = x.Reason,
                        Status = StationBlockBindingRules.Classify(x.Reason).ToString(),
                        Confidence = x.Confidence,
                        IsShared = x.IsShared,
                        BoundAt = x.CreateTime
                    })
                    .ToList();
            }
        }

        private async Task<HashSet<string>> GetAllowedStationsCachedAsync(
            Dictionary<long, HashSet<string>> cache, long projectId)
        {
            if (cache.TryGetValue(projectId, out var cached))
            {
                return cached;
            }
            var allowed = await _stationPlcScopeService.GetAllowedStationsAsync(projectId).ConfigureAwait(false);
            cache[projectId] = allowed;
            return allowed;
        }

        private static void AddSkip(PLC_GraphStationBindingBindResultDTO summary, string blockName, string reason)
        {
            if (summary.Skipped.Count < 50)
            {
                summary.Skipped.Add(new PLC_GraphStationBindingSkipDTO { BlockName = blockName, Reason = reason });
            }
        }

        private static string ResolveOperatorName()
        {
            var name = CurrentUserInfo.Instance.UserName;
            return string.IsNullOrWhiteSpace(name) ? ManualReason : name;
        }
    }
}
