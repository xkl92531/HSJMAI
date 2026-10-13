using HSJM.AI.AppLayer.PLC.PLC_Upload;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel;
using System.Threading.Tasks;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// PLC文件上传与解析接口
    /// </summary>
    [ApiExplorerSettings(GroupName = "PLC")]
    [Route("PLC_Upload")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("PLC文件上传与解析")]
    public class PLC_UploadController : ControllerBase
    {
        private readonly PLC_UploadApp _plcUploadApp;

        public PLC_UploadController(PLC_UploadApp plcUploadApp)
        {
            _plcUploadApp = plcUploadApp;
        }

        /// <summary>
        /// 上传并解析PLC项目文件 (Zip) - 同步方式（保留兼容）
        /// </summary>
        /// <param name="file">Zip文件</param>
        /// <returns></returns>
        [Route("UploadAndParse")]
        [HttpPost]
        [DisplayName("上传并解析PLC项目文件")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        public async Task<ActionResult> UploadAndParseAsync([FromForm] IFormFile file, [FromForm] long projectId)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            if (projectId <= 0)
            {
                return BadRequest("ProjectId is required");
            }

            var result = await _plcUploadApp.UploadAndParseAsync(file, projectId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 启动后台异步上传解析任务
        /// 立即返回任务ID，前端通过轮询进度接口获取处理状态
        /// </summary>
        /// <param name="file">Zip文件</param>
        /// <param name="projectId">项目ID</param>
        /// <returns>返回任务ID和状态</returns>
        [Route("StartBackgroundUpload")]
        [HttpPost]
        [DisplayName("启动后台异步上传解析")]
        [ProducesResponseType(typeof(HttpResponseResultModel<UploadTaskStartResult>), 200)]
        public async Task<ActionResult> StartBackgroundUploadAsync([FromForm] IFormFile file, [FromForm] long projectId, [FromForm] bool fullProjectUpdate = false)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            if (projectId <= 0)
            {
                return BadRequest("ProjectId is required");
            }

            var result = await _plcUploadApp.StartBackgroundUploadAsync(file, projectId, fullProjectUpdate).ConfigureAwait(false);
            return Ok(result);
        }




        /// <summary>
        /// 查询上传任务进度
        /// </summary>
        /// <param name="taskId">任务ID（由 StartBackgroundUpload 返回）</param>
        /// <returns>上传进度信息</returns>
        [Route("GetProgress")]
        [HttpGet]
        [DisplayName("查询上传任务进度")]
        [ProducesResponseType(typeof(HttpResponseResultModel<UploadProgress>), 200)]
        public ActionResult GetProgress([FromQuery] string taskId)
        {
            if (string.IsNullOrEmpty(taskId))
            {
                return BadRequest("TaskId is required");
            }

            var progress = _plcUploadApp.GetUploadProgress(taskId);

            var result = new HttpResponseResultModel<UploadProgress>
            {
                IsSuccess = progress != null,
                BackResult = progress,
                ErrorMessage = progress == null ? "任务不存在或已过期" : null
            };

            return Ok(result);
        }

        /// <summary>
        /// 上传IO标签CSV文件
        /// </summary>
        /// <param name="file">CSV文件</param>
        /// <param name="projectId">项目ID</param>
        /// <returns></returns>
        [Route("UploadIOTagsCsv")]
        [HttpPost]
        [DisplayName("上传IO标签CSV文件")]
        [ProducesResponseType(typeof(HttpResponseResultModel<int>), 200)]
        public async Task<ActionResult> UploadIOTagsCsvAsync([FromForm] IFormFile file, [FromForm] long projectId)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(MessageFactory.CreateParamsIsNullMessage());
            }
            if (projectId <= 0)
            {
                return BadRequest("ProjectId is required");
            }
            if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Only CSV files are allowed");
            }

            var result = await _plcUploadApp.UploadIOTagsCsvAsync(file, projectId).ConfigureAwait(false);
            return Ok(result);
        }


        /// <summary>
        /// 批量注册标签名，I点，Q点
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        [Route("ExecBatchRegistrationTag/{projectId}")]
        [HttpPost]
        [DisplayName("上传IO标签CSV文件")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        public async Task<ActionResult> ExecuteBatchRegistrationTag(long projectId)
        {
            var result = await _plcUploadApp.ExecuteBatchRegistrationTag(projectId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// V2（2026-09-12；2026-09-19 sub-project B 改走批量注册）：按项目 ioturl 自动分派网关类型的批量注册。
        /// 非老网关 URL → 入队 PLC_IgwRegistrationJob 全量注册（IGW 批量接口 POST /api/v1/project/bulk/tags，本项目通道下 S7/S7Plus 设备，DB 变量 + IO 标签；后台执行，接口秒回）；
        /// 老网关 URL（端口 2500）→ 委托原 ExecuteBatchRegistrationTag（行为零改动）。
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        [Route("ExecBatchRegistrationTagV2/{projectId}")]
        [HttpPost]
        [DisplayName("批量注册PLC标签V2(自动分派网关)")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        public async Task<ActionResult> ExecuteBatchRegistrationTagV2(long projectId)
        {
            var result = await _plcUploadApp.ExecuteBatchRegistrationTagV2(projectId).ConfigureAwait(false);
            return Ok(result);
        }


        /// <summary>
        /// 重置约束数据
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        [Route("ResetRule/{projectId}")]
        [HttpPost]
        [DisplayName("重置约束数据")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        [AllowAnonymous]
        public async Task<ActionResult> ResetRule(long projectId)
        {
            var result = await _plcUploadApp.ResetRule(projectId).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 重置所有未删除项目的约束并触发全量重新生成（不传具体 projectId 时使用）。
        /// 内部调 ResetRule(0)，走 ai_plc_project_new 所有未删除项目。
        /// </summary>
        [Route("ResetRule")]
        [HttpPost]
        [DisplayName("重置所有项目约束")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        [AllowAnonymous]
        public async Task<ActionResult> ResetRuleAll()
        {
            var result = await _plcUploadApp.ResetRule(0).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 只重跑指定 LU 的约束（2026-10-02 起走 XMLV2）：用项目保存的上传包全项目生成，只替换该 LU 所在程序段的约束与报警清单行。
        /// refreshAi=true 时该范围的普通网络 AI 与规则措辞不走缓存（FB 契约缓存不清）。没有保存包 / 映射不上 → 失败，不改任何东西。
        /// </summary>
        [Route("RerunSingleLu/{luId}")]
        [HttpPost]
        [DisplayName("重跑单个LU的约束生成")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        [AllowAnonymous]
        public async Task<ActionResult> RerunSingleLu(long luId, [FromQuery] bool refreshAi = false)
        {
            var result = await _plcUploadApp.RerunSingleLuAsync(luId, refreshAi).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 只重跑指定程序块的约束（2026-10-02 起走 XMLV2）：用项目保存的上传包全项目生成，只替换该块范围的约束与报警清单行。
        /// refreshAi 同 RerunSingleLu。没有保存包 / 块不在包里 / 块没有程序体 → 失败，不改任何东西。
        /// </summary>
        [Route("RerunBlock/{blockId}")]
        [HttpPost]
        [DisplayName("重跑单个程序块的约束生成")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        [AllowAnonymous]
        public async Task<ActionResult> RerunBlock(long blockId, [FromQuery] bool refreshAi = false)
        {
            var result = await _plcUploadApp.RerunBlockAsync(blockId, refreshAi).ConfigureAwait(false);
            return Ok(result);
        }

        /// <summary>
        /// 手动重跑 GRAPH 步位注册（判定 → 台账 → 网关注册 → 读值验证）。
        /// 适合修好项目 IotUrl / 网关恢复后补注册，无需重新上传 zip。
        /// </summary>
        [Route("RerunGraphStepRegistration/{projectId}")]
        [HttpPost]
        [DisplayName("重跑GRAPH步位网关注册")]
        [ProducesResponseType(typeof(HttpResponseResultModel<string>), 200)]
        [AllowAnonymous]
        public async Task<ActionResult> RerunGraphStepRegistration(long projectId)
        {
            var result = await _plcUploadApp.RerunGraphStepRegistrationAsync(projectId).ConfigureAwait(false);
            return Ok(result);
        }
    }
}

