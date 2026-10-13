using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_GraphBlock
{
    /// <summary>
    /// GRAPH 程序块解析信息应用服务：条件过滤查询 + 按块查询 source+block 信封数据
    /// </summary>
    public class PLC_GraphBlockApp : ISelfScopedAutoInject
    {
        private readonly ISqlSugarClient _db;
        private readonly ILogger<PLC_GraphBlockApp> _logger;

        public PLC_GraphBlockApp(ISqlSugarClient db, ILogger<PLC_GraphBlockApp> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// 条件过滤查询 GRAPH 程序块解析信息（source + block 信封列表，按块编号升序）。
        /// 过滤条件全部可选、AND 组合：
        /// - projectId：PLC 项目 id（ai_plc_block_new.projectid）
        /// - plcCode：PLC 设备编码（ai_plc_project_new.plccode，如 PLC1/PLC2）
        /// - stationCode：工站代码（ai_plc_block_new.stationcode，如 OP010）
        /// - blockName：块名模糊匹配（Contains）
        /// </summary>
        public async Task<HttpResponseResultModel<List<PLC_GraphBlockDTO>>> GetListAsync(
            long projectId, string plcCode, string stationCode, string blockName, int limit = 500)
        {
            var response = new HttpResponseResultModel<List<PLC_GraphBlockDTO>>();
            try
            {
                plcCode = plcCode?.Trim();
                stationCode = stationCode?.Trim();
                blockName = blockName?.Trim();
                if (limit <= 0) limit = 500;

                // PLC 设备编码（PLC1/PLC2）→ ai_plc_project_new.plccode 命中的项目 id 集合
                List<long> plcProjectIds = null;
                if (!string.IsNullOrEmpty(plcCode))
                {
                    plcProjectIds = await _db.Queryable<PLC_ProjectEntity>()
                        .Where(p => p.IsDeleted == false && p.PlcCode == plcCode)
                        .Select(p => p.Id)
                        .ToListAsync()
                        .ConfigureAwait(false);

                    // 编码未命中任何项目：直接返回空列表（合法结果，非错误）
                    if (plcProjectIds.Count == 0)
                    {
                        response.BackResult = new List<PLC_GraphBlockDTO>();
                        response.IsSuccess = true;
                        return response;
                    }
                }

                var rows = await _db.Queryable<PLC_BlockEntity>()
                    .Where(b => b.IsDeleted == false
                        && b.ProgrammingLanguage == "GRAPH")
                    .WhereIF(projectId > 0, b => b.ProjectId == projectId)
                    .WhereIF(!string.IsNullOrEmpty(stationCode), b => b.StationCode == stationCode)
                    .WhereIF(!string.IsNullOrEmpty(blockName), b => b.BlockName.Contains(blockName))
                    .WhereIF(plcProjectIds != null,
                        b => b.ProjectId.HasValue && plcProjectIds.Contains(b.ProjectId.Value))
                    .OrderBy(b => b.BlockNumber, OrderByType.Asc)
                    .Select(b => new
                    {
                        b.Id,
                        b.ProjectId,
                        b.ParseRunId,
                        b.SourceHash,
                        b.BlockName,
                        b.BlockType,
                        b.BlockNumber,
                        b.ProgrammingLanguage
                    })
                    .Take(limit)
                    .ToListAsync()
                    .ConfigureAwait(false);

                // 2026-09-09 修复：恢复真实 HasPreviewBatch（还原 8/31 实现，9/04「PLC 模块自包含」
                // 改造 5e55346 误砍成恒 false，前端 catalog 一直直接消费本字段 → 结构列表全部
                // 显示「生成预览」且「查看流程图」按钮(v-if)整体消失）。
                // 状态口径取 PreviewReady+Committed（与 LineUpGraphCatalogRepository / LatestPreview 一致：
                // 已保存结构的块同样可查看流程图）。
                var previewBlockNames = await LoadPreviewBlockNamesAsync(
                    rows.Select(r => r.BlockName).Where(name => !string.IsNullOrEmpty(name)).Distinct().ToList(),
                    500).ConfigureAwait(false);
                response.BackResult = rows
                    .Select(r => new PLC_GraphBlockDTO
                    {
                        Source = new PLC_GraphBlockSourceDTO
                        {
                            PlcProjectId = r.ProjectId ?? 0L,
                            PlcBlockId = r.Id,
                            ParseRunId = r.ParseRunId,
                            SourceHash = r.SourceHash
                        },
                        Block = new PLC_GraphBlockInfoDTO
                        {
                            BlockName = r.BlockName,
                            BlockType = r.BlockType,
                            BlockNumber = r.BlockNumber,
                            ProgrammingLanguage = r.ProgrammingLanguage
                        },
                        HasPreviewBatch = previewBlockNames.Contains(r.BlockName ?? string.Empty)
                    })
                    .ToList();
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[GRAPH程序块] 条件查询失败 ProjectId={ProjectId} PlcCode={PlcCode} Station={Station} BlockName={BlockName}",
                    projectId, plcCode, stationCode, blockName);
                response.IsSuccess = false;
                response.ErrorMessage = "查询失败: " + ex.Message;
            }
            return response;
        }

        /// <summary>
        /// 查询给定块名中存在可预览批次（PreviewReady/Committed）的集合。
        /// 还原 8/31 d9336c4 实现（9/04 自包含改造 5e55346 误删）；状态口径扩为双状态，
        /// 与 LineUpGraphCatalogRepository 的 HasPreviewBatch jsonb 匹配一致。
        /// 批次表是 PG 大小写保留表，SqlSugar Queryable 会炸，走手写带引号 SQL + 参数化 IN 分块。
        /// </summary>
        private async Task<HashSet<string>> LoadPreviewBlockNamesAsync(List<string> blockNames, int maxParams)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (blockNames == null || blockNames.Count == 0)
            {
                return result;
            }

            foreach (var chunk in blockNames.Chunk(Math.Max(1, maxParams)))
            {
                var keyParams = chunk
                    .Select((name, index) => new SugarParameter($"@N{index}", name))
                    .ToArray();
                var keyList = string.Join(", ", keyParams.Select(p => p.ParameterName));
                var rows = await _db.Ado.SqlQueryAsync<string>(
                        "SELECT DISTINCT \"PreviewJson\" #>> '{Documents,0,BlockName}' FROM \"AI_LineUp_ImportBatch\" " +
                        "WHERE \"IsDeleted\" = FALSE AND \"Status\" IN ('PreviewReady', 'Committed') " +
                        "AND \"PreviewJson\" #>> '{Documents,0,BlockName}' IN (" + keyList + ")",
                        keyParams)
                    .ConfigureAwait(false);
                foreach (var name in rows)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        result.Add(name);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 按块主键获取单个 GRAPH 程序块的解析信息（source + block 信封）
        /// </summary>
        public async Task<HttpResponseResultModel<PLC_GraphBlockDTO>> GetByBlockAsync(long blockId)
        {
            var response = new HttpResponseResultModel<PLC_GraphBlockDTO>();
            try
            {
                var row = await _db.Queryable<PLC_BlockEntity>()
                    .Where(b => b.IsDeleted == false
                        && b.Id == blockId
                        && b.ProgrammingLanguage == "GRAPH")
                    .Select(b => new
                    {
                        b.Id,
                        b.ProjectId,
                        b.ParseRunId,
                        b.SourceHash,
                        b.BlockName,
                        b.BlockType,
                        b.BlockNumber,
                        b.ProgrammingLanguage
                    })
                    .FirstAsync()
                    .ConfigureAwait(false);

                if (row == null)
                {
                    response.IsSuccess = false;
                    response.ErrorMessage = $"未找到 GRAPH 程序块（id={blockId} 不存在或编程语言不是 GRAPH）";
                    return response;
                }

                var previewBlockNames = await LoadPreviewBlockNamesAsync(
                    new List<string> { row.BlockName }, 1).ConfigureAwait(false);
                response.BackResult = new PLC_GraphBlockDTO
                {
                    Source = new PLC_GraphBlockSourceDTO
                    {
                        PlcProjectId = row.ProjectId ?? 0L,
                        PlcBlockId = row.Id,
                        ParseRunId = row.ParseRunId,
                        SourceHash = row.SourceHash
                    },
                    Block = new PLC_GraphBlockInfoDTO
                    {
                        BlockName = row.BlockName,
                        BlockType = row.BlockType,
                        BlockNumber = row.BlockNumber,
                        ProgrammingLanguage = row.ProgrammingLanguage
                    },
                    HasPreviewBatch = previewBlockNames.Contains(row.BlockName ?? string.Empty)
                };
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH程序块] 按块查询失败 BlockId={BlockId}", blockId);
                response.IsSuccess = false;
                response.ErrorMessage = "查询失败: " + ex.Message;
            }
            return response;
        }
    }
}
