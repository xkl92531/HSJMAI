using HSJM.AI.AppLayer;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// 故障事件接口（页面查询 + 小锦 Pull 补拉）
    /// </summary>
    [ApiExplorerSettings(GroupName = "PLC")]
    [Route("PLC_FaultEvent")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("故障事件")]
    public class PLC_FaultEventController : ControllerBase
    {
        private readonly PLC_FaultEventApp _app;

        public PLC_FaultEventController(PLC_FaultEventApp app)
        {
            _app = app;
        }

        /// <summary>
        /// 分页查询故障事件（一行 = 一次完整故障，含起止时间与持续时长）
        /// </summary>
        [Route("GetListPaged")]
        [HttpGet]
        [DisplayName("分页查询故障事件")]
        public async Task<ActionResult> GetListPagedAsync(
            int page = 1, int limit = 20, long? projectId = null, string variableName = null,
            bool onlyOpen = false, DateTime? startTime = null, DateTime? endTime = null)
        {
            var result = await _app.GetListPagedAsync(page, limit, projectId, variableName, onlyOpen, startTime, endTime)
                .ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 小锦掉线补拉：按 modifytime 游标拉取变化过的故障事件。
        /// 匿名开放（内网边界；与 FaultEventHub 一致）。
        /// 契约：afterTime 不带 = 拉存量；返回 cursor 作为下次 afterTime；消息按 (eventId, eventType) 去重。
        /// </summary>
        [Route("Pull")]
        [HttpGet]
        [AllowAnonymous]
        [DisplayName("故障事件补拉")]
        public async Task<ActionResult> PullAsync(
            [FromQuery] long projectId = 0, [FromQuery] DateTime? afterTime = null, [FromQuery] int limit = 100)
        {
            if (projectId <= 0)
            {
                return Ok(new HttpResponseResultModel<object>
                {
                    IsSuccess = false,
                    ErrorMessage = "projectId 不能为空"
                });
            }
            var result = await _app.PullAsync(projectId, afterTime, limit).ConfigureAwait(false);
            return Ok(result);
        }
    }
}
