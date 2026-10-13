using HSJM.AI.AppLayer;
using HSJM.AI.Utility;
using HSJM.AI.Filters;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.ComponentModel;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// GRAPH 程序块与工站批量绑定接口（批量绑定弹框）
    /// </summary>
    [ApiExplorerSettings(GroupName = "PLC")]
    [Route("PLC_GraphStationBinding")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("GRAPH工站批量绑定")]
    public class PLC_GraphStationBindingController : ControllerBase
    {
        private readonly PLC_GraphStationBindingApp _app;

        public PLC_GraphStationBindingController(PLC_GraphStationBindingApp app)
        {
            _app = app;
        }

        /// <summary>
        /// 查询 GRAPH 程序块及其工站绑定（Bindings 为空 = 未绑定，右侧候选区；非空 = 已绑定，左侧可解绑）
        /// </summary>
        /// <param name="projectId">PLC 项目 id（可选，>0 时仅该项目）</param>
        [Route("GetBlocks")]
        [DisplayName("查询GRAPH块工站绑定")]
        [ProducesResponseType(typeof(HttpResponseResultModel<List<PLC_GraphStationBindingBlockDTO>>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetBlocksAsync(long projectId = 0)
        {
            var result = await _app.GetBlocksAsync(projectId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 批量绑定：把多个 GRAPH 块绑到指定工站（逐块按项目做工位白名单校验，已绑其它工站的块换绑）
        /// </summary>
        [Route("Bind")]
        [DisplayName("批量绑定GRAPH块到工站")]
        [ProducesResponseType(typeof(HttpResponseResultModel<PLC_GraphStationBindingBindResultDTO>), 200)]
        [HttpPost]
        public async Task<ActionResult> BindAsync([FromBody] PLC_GraphStationBindingBindRequestDTO request)
        {
            var result = await _app.BindAsync(request).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 解绑：按绑定行 Id 软删除，块完全解绑后回到未绑定区
        /// </summary>
        [Route("Unbind")]
        [DisplayName("解绑GRAPH块工站绑定")]
        [ProducesResponseType(typeof(HttpResponseResultModel<PLC_GraphStationBindingUnbindResultDTO>), 200)]
        [HttpPost]
        public async Task<ActionResult> UnbindAsync([FromBody] PLC_GraphStationBindingUnbindRequestDTO request)
        {
            var result = await _app.UnbindAsync(request).ConfigureAwait(false);
            return Ok(result);
        }
    }
}
