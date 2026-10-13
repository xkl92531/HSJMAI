using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.CylinderSignalEvent;
using HSJM.AI.AppLayer.PLC.CylinderSignalEvent.DTOS;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers
{
    [ApiExplorerSettings(GroupName = "PLC")]
    [Route("CylinderSignalEvent")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    [DisplayName("Cylinder signal event collection")]
    public class CylinderSignalEventController : ControllerBase
    {
        private readonly CylinderSignalEventApp _app;

        public CylinderSignalEventController(CylinderSignalEventApp app)
        {
            _app = app;
        }

        [Route("CollectOnce")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<CylinderSignalCollectResultDto>), 200)]
        public async Task<ActionResult> CollectOnceAsync([FromBody] CylinderSignalCollectRequestDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest("ProjectId is required.");
            }

            var result = await _app.CollectOnceAsync(request).ConfigureAwait(false);
            return Ok(result);
        }

        [Route("GenerateSimulationValues")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<CylinderSignalSimulationGenerateResultDto>), 200)]
        public async Task<ActionResult> GenerateSimulationValuesAsync([FromBody] CylinderSignalSimulationGenerateRequestDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest("ProjectId is required.");
            }

            var result = await _app.GenerateSimulationValuesAsync(request).ConfigureAwait(false);
            return Ok(result);
        }

        [Route("CollectAllOnce")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<CylinderSignalCollectAllResultDto>), 200)]
        public async Task<ActionResult> CollectAllOnceAsync([FromBody] CylinderSignalCollectAllRequestDto request)
        {
            var result = await _app.CollectAllProjectsOnceAsync(request).ConfigureAwait(false);
            return Ok(result);
        }

        [Route("GetJobConfig")]
        [HttpGet]
        [ProducesResponseType(typeof(CylinderSignalCollectorJobConfigDto), 200)]
        public async Task<ActionResult> GetJobConfigAsync()
        {
            var result = await _app.GetJobConfigAsync().ConfigureAwait(false);
            return Ok(result);
        }

        [Route("SaveJobConfig")]
        [HttpPost]
        [ProducesResponseType(typeof(HttpResponseResultModel<CylinderSignalCollectorJobConfigDto>), 200)]
        public async Task<ActionResult> SaveJobConfigAsync([FromBody] CylinderSignalCollectorJobConfigDto request)
        {
            if (request == null)
            {
                return BadRequest("Request is required.");
            }

            var result = await _app.SaveJobConfigAsync(request).ConfigureAwait(false);
            return Ok(result);
        }

        [Route("GetListPaged")]
        [HttpGet]
        [ProducesResponseType(typeof(QueryPagedResponseModel<CylinderSignalEventItemDto>), 200)]
        public async Task<ActionResult> GetListPagedAsync(
            int page = 1,
            int limit = 20,
            [FromQuery] CylinderSignalEventQueryDto query = null)
        {
            var result = await _app.GetListPagedAsync(page, limit, query).ConfigureAwait(false);
            return Ok(result);
        }
    }
}
