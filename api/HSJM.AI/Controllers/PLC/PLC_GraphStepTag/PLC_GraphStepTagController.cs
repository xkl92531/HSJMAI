using HSJM.AI.AppLayer;
using HSJM.AI.Utility;
using HSJM.AI.Filters;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.PLC.PLC_GraphStepTag;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.ComponentModel;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// GRAPH 步位注册台账接口
    /// </summary>
    [ApiExplorerSettings(GroupName = "PLC")]
    [Route("PLC_GraphStepTag")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("GRAPH步位注册台账")]
    public class PLC_GraphStepTagController : ControllerBase
    {
        private readonly PLC_GraphStepTagApp _app;

        public PLC_GraphStepTagController(PLC_GraphStepTagApp app)
        {
            _app = app;
        }

        /// <summary>
        /// 按项目获取注册汇总（总体统计 + 按 DB 分组）
        /// </summary>
        /// <param name="projectId">项目 id</param>
        /// <returns></returns>
        [Route("GetRegistrationSummary/{projectId}")]
        [DisplayName("按项目获取注册汇总")]
        [ProducesResponseType(typeof(HttpResponseResultModel<PLC_GraphStepTagSummaryDTO>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetRegistrationSummaryAsync(long projectId)
        {
            if (projectId <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await _app.GetRegistrationSummaryAsync(projectId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 按 DB 获取台账明细行（前端展开表格）
        /// </summary>
        /// <param name="projectId">项目 id</param>
        /// <param name="dbName">实例 DB 名</param>
        /// <param name="limit">单次返回上限（默认 500）</param>
        /// <returns></returns>
        [Route("GetRowsByDb")]
        [DisplayName("按DB获取台账明细")]
        [ProducesResponseType(typeof(HttpResponseResultModel<List<PLC_GraphStepTagRowDTO>>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetRowsByDbAsync(long projectId, string dbName, int limit = 500)
        {
            if (projectId <= 0 || string.IsNullOrWhiteSpace(dbName))
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await _app.GetRowsByDbAsync(projectId, dbName, limit).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 按项目 + 步号取台账行（预览弹框工步详情）；fbName 非空时只取该 GRAPH FB 对应的实例 DB
        /// </summary>
        /// <param name="projectId">项目 id</param>
        /// <param name="stepNumber">GRAPH 步号（1 起）</param>
        /// <param name="fbName">GRAPH FB 类型名（如 FB160_MainGraph，可选过滤）</param>
        /// <param name="limit">单次返回上限（默认 50）</param>
        /// <returns></returns>
        [Route("GetRowsByStep")]
        [DisplayName("按步号获取台账明细")]
        [ProducesResponseType(typeof(HttpResponseResultModel<List<PLC_GraphStepTagRowDTO>>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetRowsByStepAsync(long projectId, int stepNumber, string? fbName = null, int limit = 50)
        {
            if (projectId <= 0 || stepNumber <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await _app.GetRowsByStepAsync(projectId, stepNumber, fbName, limit).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 手动触发重新注册（重新入队后台任务）
        /// </summary>
        /// <param name="projectId">项目 id</param>
        /// <returns></returns>
        [Route("RetryRegistration/{projectId}")]
        [DisplayName("手动触发重新注册")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        [HttpPost]
        public async Task<ActionResult> RetryRegistrationAsync(long projectId)
        {
            if (projectId <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await _app.RetryRegistrationAsync(projectId).ConfigureAwait(false);
            return Ok(result);
        }
    }
}
