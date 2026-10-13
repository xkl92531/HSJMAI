using HSJM.AI.AppLayer;
using HSJM.AI.Utility;
using HSJM.AI.Filters;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.PLC.PLC_GraphBlock;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.ComponentModel;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// GRAPH 程序块解析信息接口（source 溯源段 + block 块信息段）
    /// </summary>
    [ApiExplorerSettings(GroupName = "PLC")]
    [Route("PLC_GraphBlock")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("GRAPH程序块解析信息")]
    public class PLC_GraphBlockController : ControllerBase
    {
        private readonly PLC_GraphBlockApp _app;

        public PLC_GraphBlockController(PLC_GraphBlockApp app)
        {
            _app = app;
        }

        /// <summary>
        /// 条件过滤查询 GRAPH 程序块解析信息（按块编号升序，条件全部可选、AND 组合）
        /// </summary>
        /// <param name="projectId">PLC 项目 id（可选，>0 生效）</param>
        /// <param name="plcCode">PLC 设备编码（可选，ai_plc_project_new.plccode，如 PLC1/PLC2）</param>
        /// <param name="stationCode">工站代码（可选，如 OP010）</param>
        /// <param name="blockName">块名模糊匹配（可选，如 FB260_CacheGraph 或 Cache）</param>
        /// <param name="limit">单次返回上限（默认 500）</param>
        /// <returns>source + block 信封列表</returns>
        [Route("GetList")]
        [DisplayName("条件过滤查询GRAPH程序块解析信息")]
        [ProducesResponseType(typeof(HttpResponseResultModel<List<PLC_GraphBlockDTO>>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetListAsync(
            long projectId = 0,
            string plcCode = null,
            string stationCode = null,
            string blockName = null,
            int limit = 500)
        {
            var result = await _app.GetListAsync(projectId, plcCode, stationCode, blockName, limit).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 按块主键获取单个 GRAPH 程序块解析信息
        /// </summary>
        /// <param name="blockId">程序块 id（ai_plc_block_new.id）</param>
        /// <returns>source + block 信封</returns>
        [Route("GetByBlock/{blockId}")]
        [DisplayName("按块主键获取GRAPH程序块解析信息")]
        [ProducesResponseType(typeof(HttpResponseResultModel<PLC_GraphBlockDTO>), 200)]
        [HttpGet]
        public async Task<ActionResult> GetByBlockAsync(long blockId)
        {
            if (blockId <= 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await _app.GetByBlockAsync(blockId).ConfigureAwait(false);
            return Ok(result);
        }
    }
}
