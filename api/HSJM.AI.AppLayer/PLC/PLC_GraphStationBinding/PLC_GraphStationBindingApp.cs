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
        public const string ManualReason = "手动绑定";

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

        public PLC_GraphStationBindingApp(
            ISqlSugarClient db,
            ILogger<PLC_GraphStationBindingApp> logger,
            StationPlcScopeService stationPlcScopeService)
        {
            _db = db;
            _logger = logger;
            _stationPlcScopeService = stationPlcScopeService;
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
        /// </summary>
        public async Task<HttpResponseResultModel<PLC_GraphStationBindingBindResultDTO>> BindAsync(PLC_GraphStationBindingBindRequestDTO request)
        {
            var response = new HttpResponseResultModel<PLC_GraphStationBindingBindResultDTO>();
            var summary = new PLC_GraphStationBindingBindResultDTO();
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
                    return response;
                }
                if (blockIds.Count > MaxBlockIdsPerRequest)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = $"单次最多绑定 {MaxBlockIdsPerRequest} 个块";
                    return response;
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
                    return response;
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

                        var existing = await _db.Queryable<PLC_StationBlockBindingEntity>()
                            .Where(x => x.IsDeleted == false
                                && x.ProjectId == block.ProjectId
                                && x.BlockName == block.BlockName)
                            .ToListAsync()
                            .ConfigureAwait(false);
                        if (existing.Any(x => string.Equals(x.StationCode?.Trim(), stationCode, StringComparison.OrdinalIgnoreCase)))
                        {
                            AddSkip(summary, block.BlockName, "已绑定该工站");
                            continue;
                        }

                        // 换绑：软删同项目同名块的其它工站绑定（含自动提取行），再新增目标工站绑定
                        oldBindingIds.AddRange(existing.Select(x => x.Id));
                        toInsert.Add(new PLC_StationBlockBindingEntity
                        {
                            ProjectId = block.ProjectId,
                            StationCode = stationCode,
                            BlockId = block.Id,
                            BlockName = block.BlockName,
                            Confidence = 1.0m,
                            Reason = ManualReason,
                            IsShared = false,
                            CreaterId = operatorId,
                            CreaterName = operatorName,
                            CreateTime = now,
                            ModifierId = operatorId,
                            ModifierName = operatorName,
                            ModifyTime = now
                        });
                        if (!string.Equals(block.StationCode, stationCode, StringComparison.OrdinalIgnoreCase))
                        {
                            blockIdsToStamp.Add(block.Id);
                        }
                    }

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
            return response;
        }

        /// <summary>
        /// 解绑：按绑定行 Id 软删除；块在该项目下再无任何有效绑定时清空其 stationcode（回到右侧未绑定区）。
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
                await SoftDeleteBindingsAsync(rows.Select(x => x.Id).ToList(), operatorId, operatorName).ConfigureAwait(false);

                // 完全解绑（该项目同名块无剩余有效绑定）的块清空 stationcode
                var cleared = 0;
                var affectedKeys = rows
                    .Where(x => x.ProjectId.HasValue && !string.IsNullOrWhiteSpace(x.BlockName))
                    .Select(x => new { ProjectId = x.ProjectId.Value, Name = x.BlockName })
                    .Distinct()
                    .ToList();
                foreach (var key in affectedKeys)
                {
                    var remaining = await _db.Queryable<PLC_StationBlockBindingEntity>()
                        .Where(x => x.IsDeleted == false
                            && x.ProjectId == key.ProjectId
                            && x.BlockName == key.Name)
                        .CountAsync()
                        .ConfigureAwait(false);
                    if (remaining > 0)
                    {
                        continue;
                    }

                    cleared += await _db.Updateable<PLC_BlockEntity>()
                        .Where(b => b.IsDeleted == false
                            && b.ProjectId == key.ProjectId
                            && b.BlockName == key.Name
                            && b.StationCode != null && b.StationCode != "")
                        .SetColumns(b => new PLC_BlockEntity
                        {
                            StationCode = "",
                            ModifierId = operatorId,
                            ModifierName = operatorName,
                            ModifyTime = DateTime.Now
                        })
                        .ExecuteCommandAsync()
                        .ConfigureAwait(false);
                }

                summary.UnboundCount = rows.Count;
                summary.ClearedBlockCount = cleared;
                _logger.LogInformation("[GRAPH工站绑定] 解绑完成 删除绑定={Unbound} 清空块工位={Cleared}", rows.Count, cleared);

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

            // 键：ProjectId + 小写块名（绑定匹配大小写不敏感）；组内排序：手动绑定优先，再按绑定时间倒序
            var byProjectAndName = bindingRows
                .Where(x => x.ProjectId.HasValue && !string.IsNullOrWhiteSpace(x.StationCode))
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
                if (!byProjectAndName.TryGetValue(key, out var bindings))
                {
                    continue;
                }
                block.Bindings = bindings
                    .Select(x => new PLC_GraphStationBindingItemDTO
                    {
                        BindingId = x.Id,
                        StationCode = x.StationCode?.Trim(),
                        Reason = x.Reason,
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
