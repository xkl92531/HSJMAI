using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_GraphStepTag
{
    /// <summary>
    /// GRAPH 步位注册台账应用服务：汇总查询 + 手动重试入队
    /// </summary>
    public class PLC_GraphStepTagApp : ISelfScopedAutoInject
    {
        private readonly ISqlSugarClient _db;
        private readonly ILogger<PLC_GraphStepTagApp> _logger;

        public PLC_GraphStepTagApp(ISqlSugarClient db, ILogger<PLC_GraphStepTagApp> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// 按项目汇总 GRAPH 步位注册台账：总体统计 + 按 DB 分组明细
        /// </summary>
        public async Task<HttpResponseResultModel<PLC_GraphStepTagSummaryDTO>> GetRegistrationSummaryAsync(long projectId)
        {
            var response = new HttpResponseResultModel<PLC_GraphStepTagSummaryDTO>();
            try
            {
                var rows = await _db.Queryable<PLC_GraphStepTagEntity>()
                    .Where(x => x.IsDeleted == false && x.ProjectId == projectId)
                    .Select(x => new
                    {
                        x.DbName,
                        x.FbName,
                        x.TagCategory,
                        x.RegStatus,
                        x.StepNumber,
                        x.Symbol,
                        x.VerifyDetail,
                        x.StationCode,
                        x.StationName
                    })
                    .ToListAsync()
                    .ConfigureAwait(false);

                var summary = new PLC_GraphStepTagSummaryDTO
                {
                    ProjectId = projectId,
                    TotalSymbols = rows.Count,
                    DbCount = rows.Select(r => r.DbName).Distinct().Count(),
                    VerifiedCount = rows.Count(r => r.RegStatus == "Verified"),
                    RegisteredCount = rows.Count(r => r.RegStatus == "Registered"),
                    PendingCount = rows.Count(r => r.RegStatus == "Pending"),
                    RegFailedCount = rows.Count(r => r.RegStatus == "RegFailed"),
                    VerifyFailedCount = rows.Count(r => r.RegStatus == "VerifyFailed"),
                    LastRegTime = await _db.Queryable<PLC_GraphStepTagEntity>()
                        .Where(x => x.IsDeleted == false && x.ProjectId == projectId && x.RegTime != null)
                        .MaxAsync(x => x.RegTime)
                        .ConfigureAwait(false),
                    Databases = rows
                        .GroupBy(r => new { r.DbName, r.FbName })
                        .Select(g => new PLC_GraphStepTagDbSummaryDTO
                        {
                            DbName = g.Key.DbName,
                            FbName = g.Key.FbName,
                            StationCode = g.Select(x => x.StationCode).FirstOrDefault(),
                            StationName = g.Select(x => x.StationName).FirstOrDefault(),
                            StepCount = g.Count(x => x.TagCategory == "EXEC_BIT"),
                            SymbolCount = g.Count(),
                            VerifiedCount = g.Count(x => x.RegStatus == "Verified"),
                            RegisteredCount = g.Count(x => x.RegStatus == "Registered"),
                            PendingCount = g.Count(x => x.RegStatus == "Pending"),
                            RegFailedCount = g.Count(x => x.RegStatus == "RegFailed"),
                            VerifyFailedCount = g.Count(x => x.RegStatus == "VerifyFailed"),
                            // 失败原因样本（前端展示用）。2026-09-22 修复：只从失败行取样——
                            // 此前任意取样会把通过行的「L1/L2通过」也当失败原因展示，
                            // 混合状态 DB（部分行早期已 Verified）出现“失败原因: L1/L2通过”的矛盾文案。
                            FailureSample = g.Where(x => (x.RegStatus == "VerifyFailed" || x.RegStatus == "RegFailed")
                                    && !string.IsNullOrWhiteSpace(x.VerifyDetail))
                                .Select(x => x.VerifyDetail)
                                .FirstOrDefault()
                            ?? g.Where(x => x.RegStatus != "Verified" && !string.IsNullOrWhiteSpace(x.VerifyDetail))
                                .Select(x => x.VerifyDetail)
                                .FirstOrDefault()
                            ?? string.Empty
                        })
                        .OrderBy(d => d.DbName)
                        .ToList()
                };

                response.BackResult = summary;
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH步位台账] 汇总查询失败 ProjectId={ProjectId}", projectId);
                response.IsSuccess = false;
                response.ErrorMessage = "汇总查询失败: " + ex.Message;
            }
            return response;
        }

        /// <summary>
        /// 按 DB 取台账明细行（分页，给前端展开明细表格用）
        /// </summary>
        public async Task<HttpResponseResultModel<List<PLC_GraphStepTagRowDTO>>> GetRowsByDbAsync(long projectId, string dbName, int limit = 500)
        {
            var response = new HttpResponseResultModel<List<PLC_GraphStepTagRowDTO>>();
            try
            {
                var rows = await _db.Queryable<PLC_GraphStepTagEntity>()
                    .Where(x => x.IsDeleted == false && x.ProjectId == projectId && x.DbName == dbName)
                    .OrderBy(x => x.StepNumber, OrderByType.Asc)
                    .Select(x => new PLC_GraphStepTagRowDTO
                    {
                        Id = x.Id,
                        Symbol = x.Symbol,
                        DbAddress = x.DbAddress,
                        StationCode = x.StationCode,
                        StationName = x.StationName,
                        TagCategory = x.TagCategory,
                        StepNumber = x.StepNumber,
                        BitIndex = x.BitIndex,
                        RegStatus = x.RegStatus,
                        VerifyDetail = x.VerifyDetail,
                        RegTime = x.RegTime,
                        VerifyTime = x.VerifyTime
                    })
                    .Take(limit)
                    .ToListAsync()
                    .ConfigureAwait(false);

                response.BackResult = rows;
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH步位台账] 明细查询失败 ProjectId={ProjectId} DbName={DbName}", projectId, dbName);
                response.IsSuccess = false;
                response.ErrorMessage = "明细查询失败: " + ex.Message;
            }
            return response;
        }

        /// <summary>
        /// 按项目 + 步号取台账行（预览弹框工步详情用）；fbName 非空时只取该 GRAPH FB 类型绑定的实例 DB，否则返回全部实例
        /// </summary>
        public async Task<HttpResponseResultModel<List<PLC_GraphStepTagRowDTO>>> GetRowsByStepAsync(long projectId, int stepNumber, string? fbName = null, int limit = 50)
        {
            var response = new HttpResponseResultModel<List<PLC_GraphStepTagRowDTO>>();
            try
            {
                var query = _db.Queryable<PLC_GraphStepTagEntity>()
                    .Where(x => x.IsDeleted == false && x.ProjectId == projectId && x.StepNumber == stepNumber);
                if (!string.IsNullOrWhiteSpace(fbName))
                {
                    query = query.Where(x => x.FbName == fbName);
                }
                var rows = await query
                    .OrderBy(x => x.Symbol, OrderByType.Asc)
                    .Select(x => new PLC_GraphStepTagRowDTO
                    {
                        Id = x.Id,
                        Symbol = x.Symbol,
                        DbAddress = x.DbAddress,
                        StationCode = x.StationCode,
                        StationName = x.StationName,
                        TagCategory = x.TagCategory,
                        StepNumber = x.StepNumber,
                        BitIndex = x.BitIndex,
                        RegStatus = x.RegStatus,
                        VerifyDetail = x.VerifyDetail,
                        RegTime = x.RegTime,
                        VerifyTime = x.VerifyTime
                    })
                    .Take(limit)
                    .ToListAsync()
                    .ConfigureAwait(false);

                response.BackResult = rows;
                response.IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH步位台账] 步号明细查询失败 ProjectId={ProjectId} Step={Step}", projectId, stepNumber);
                response.IsSuccess = false;
                response.ErrorMessage = "步号明细查询失败: " + ex.Message;
            }
            return response;
        }

        /// <summary>
        /// 手动触发重新注册（重新入队后台任务）。返回是否成功入队。
        /// </summary>
        public Task<HttpResponseResultModel<string>> RetryRegistrationAsync(long projectId)
        {
            var response = new HttpResponseResultModel<string>();
            try
            {
                var enqueued = Agent.Services.Plc_diagnosis.GraphStepTagRegistrationJob.EnqueueIfNotRunning(projectId);
                response.IsSuccess = true;
                response.BackResult = enqueued
                    ? "已入队重新注册（后台任务将按最新绑定重新生成台账并注册网关）"
                    : "任务已在队列中或正在运行，无需重复入队";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GRAPH步位台账] 手动重试入队失败 ProjectId={ProjectId}", projectId);
                response.IsSuccess = false;
                response.ErrorMessage = "入队失败: " + ex.Message;
            }
            return Task.FromResult(response);
        }
    }
}
