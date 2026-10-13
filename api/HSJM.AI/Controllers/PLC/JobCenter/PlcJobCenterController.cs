using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.JobCenter;
using HSJM.AI.AppLayer.PLC.JobCenter.DTOS;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers
{
    [ApiExplorerSettings(GroupName = "PLC")]
    [Route("PlcJobCenter")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("PLC任务调度中心")]
    public class PlcJobCenterController : ControllerBase
    {
        private readonly PlcJobCenterApp _app;

        public PlcJobCenterController(PlcJobCenterApp app)
        {
            _app = app;
        }

        [Route("GetTasks")]
        [HttpGet]
        [ProducesResponseType(typeof(List<PlcJobCenterTaskItemDto>), 200)]
        public async Task<ActionResult> GetTasksAsync()
        {
            return Ok(await _app.GetTasksAsync().ConfigureAwait(false));
        }

        [Route("GetTaskTemplates")]
        [HttpGet]
        [ProducesResponseType(typeof(List<PlcJobCenterTaskTemplateDto>), 200)]
        public async Task<ActionResult> GetTaskTemplatesAsync()
        {
            return Ok(await _app.GetTaskTemplatesAsync().ConfigureAwait(false));
        }

        [Route("GetTaskDetail")]
        [HttpGet]
        [ProducesResponseType(typeof(PlcJobCenterTaskDetailDto), 200)]
        public async Task<ActionResult> GetTaskDetailAsync(string taskKey)
        {
            return Ok(await _app.GetTaskDetailAsync(taskKey).ConfigureAwait(false));
        }

        [Route("GetStationGroups")]
        [HttpGet]
        [ProducesResponseType(typeof(List<PlcJobCenterStationGroupDto>), 200)]
        public async Task<ActionResult> GetStationGroupsAsync(string projectIdsText)
        {
            return Ok(await _app.GetStationGroupsAsync(projectIdsText).ConfigureAwait(false));
        }

        [Route("CreateTask")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<PlcJobCenterTaskDetailDto>), 200)]
        public async Task<ActionResult> CreateTaskAsync([FromBody] PlcJobCenterTaskCreateDto request)
        {
            return Ok(await _app.CreateTaskAsync(request).ConfigureAwait(false));
        }

        [Route("SaveTaskConfig")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<PlcJobCenterTaskDetailDto>), 200)]
        public async Task<ActionResult> SaveTaskConfigAsync([FromBody] PlcJobCenterTaskConfigSaveDto request)
        {
            return Ok(await _app.SaveTaskConfigAsync(request).ConfigureAwait(false));
        }

        [Route("ChangeTaskState")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> ChangeTaskStateAsync([FromBody] PlcJobCenterTaskStateChangeDto request)
        {
            return Ok(await _app.ChangeTaskStateAsync(request).ConfigureAwait(false));
        }

        [Route("DeleteTask")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<bool>), 200)]
        public async Task<ActionResult> DeleteTaskAsync([FromBody] PlcJobCenterTaskDeleteDto request)
        {
            return Ok(await _app.DeleteTaskAsync(request).ConfigureAwait(false));
        }

        [Route("RunTaskNow")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<PlcJobCenterRunTaskResultDto>), 200)]
        public async Task<ActionResult> RunTaskNowAsync([FromBody] PlcJobCenterRunTaskRequestDto request)
        {
            return Ok(await _app.RunTaskNowAsync(request).ConfigureAwait(false));
        }

        [Route("ClearTaskCache")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<PlcJobCenterClearCacheResultDto>), 200)]
        public async Task<ActionResult> ClearTaskCacheAsync([FromBody] PlcJobCenterClearCacheRequestDto request)
        {
            return Ok(await _app.ClearTaskCacheAsync(request).ConfigureAwait(false));
        }
    }
}
