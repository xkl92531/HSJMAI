using HSJM.AI.AppLayer;
using HSJM.AI.AppLayer.PLC.PLC_Upload;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// 报警变量台账接口
    /// </summary>
    [ApiExplorerSettings(GroupName = "PLC")]
    [Route("PLC_AlarmVariable")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("报警变量台账")]
    public class PLC_AlarmVariableController : ControllerBase
    {
        private readonly PLC_AlarmVariableApp _app;

        public PLC_AlarmVariableController(PLC_AlarmVariableApp app)
        {
            _app = app;
        }

        /// <summary>
        /// 分页查询报警变量台账
        /// </summary>
        [Route("GetListPaged")]
        [HttpGet]
        [DisplayName("分页查询报警变量台账")]
        public async Task<ActionResult> GetListPagedAsync(
            int page = 1, int limit = 20, long? projectId = null, string keyword = null,
            string category = null, string level = null,
            bool? monitorEnabled = null, bool? manuallyConfirmed = null, bool? collectable = null)
        {
            var result = await _app.GetListPagedAsync(page, limit, projectId, keyword, category, level,
                monitorEnabled, manuallyConfirmed, collectable).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 手工新增报警变量（自动人工确认锁 + 可采集性判定）
        /// </summary>
        [Route("Insert")]
        [HttpPost]
        [DisplayName("手工新增报警变量")]
        [ProducesResponseType(typeof(HttpResponseResultModel<long>), 200)]
        public async Task<ActionResult> InsertAsync([FromBody] PLC_AlarmVariableEntity entity)
        {
            if (entity == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await _app.InsertAsync(entity).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 编辑台账行（业务字段；编辑后自动加人工确认锁）
        /// </summary>
        [Route("UpdateById/{id}")]
        [HttpPost]
        [DisplayName("编辑报警变量")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> UpdateByIdAsync(long id, [FromBody] PLC_AlarmVariableEntity dto)
        {
            var result = await _app.UpdateAsync(id, dto).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 人工确认锁开关（confirmed=true 后 LLM 重跑永不覆盖）
        /// </summary>
        [Route("Confirm/{id}")]
        [HttpPost]
        [DisplayName("人工确认锁开关")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> ConfirmAsync(long id, [FromQuery] bool confirmed = true)
        {
            var result = await _app.ConfirmAsync(id, confirmed).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 批量启停监控（collectable=false 的行拒绝开启）
        /// </summary>
        [Route("SetMonitor")]
        [HttpPost]
        [DisplayName("批量启停监控")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> SetMonitorAsync([FromBody] SetEnabledRequest request)
        {
            if (request == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await _app.SetMonitorAsync(request.Ids, request.Enabled).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 批量启停小锦推送
        /// </summary>
        [Route("SetPush")]
        [HttpPost]
        [DisplayName("批量启停小锦推送")]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> SetPushAsync([FromBody] SetEnabledRequest request)
        {
            if (request == null)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            var result = await _app.SetPushAsync(request.Ids, request.Enabled).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 软删除
        /// </summary>
        [Route("Delete/{id}")]
        [HttpDelete]
        [DisplayName("删除报警变量")]
        public async Task<ActionResult> DeleteAsync(long id)
        {
            var result = await _app.DeleteAsync(id).ConfigureAwait(false);
            return Ok(new HttpResponseResultModel<bool> { IsSuccess = result, BackResult = result });
        }

        /// <summary>
        /// 批量软删除
        /// </summary>
        [Route("DeleteBatch")]
        [HttpDelete]
        [DisplayName("批量删除报警变量")]
        public async Task<ActionResult> DeleteBatchAsync([FromBody] List<long> ids)
        {
            var result = await _app.DeleteBatchAsync(ids).ConfigureAwait(false);
            return Ok(new HttpResponseResultModel<bool> { IsSuccess = result, BackResult = result });
        }

        /// <summary>
        /// 手动重跑三通道识别（返回 taskId，进度轮询 PLC_Upload/GetProgress/{taskId}）
        /// </summary>
        [Route("RerunRecognition/{projectId}")]
        [HttpPost]
        [DisplayName("手动重跑识别")]
        [ProducesResponseType(typeof(HttpResponseResultModel<UploadTaskStartResult>), 200)]
        public async Task<ActionResult> RerunRecognitionAsync(long projectId)
        {
            var result = await _app.RerunRecognitionAsync(projectId).ConfigureAwait(false);
            return Ok(result);
        }
    }

    /// <summary>批量启停请求体</summary>
    public class SetEnabledRequest
    {
        /// <summary>台账行 id 集合</summary>
        public List<long> Ids { get; set; }

        /// <summary>目标状态</summary>
        public bool Enabled { get; set; }
    }
}
