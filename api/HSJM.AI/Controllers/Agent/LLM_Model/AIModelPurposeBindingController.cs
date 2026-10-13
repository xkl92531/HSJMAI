using HSJM.AI.AppLayer.Agent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers;

[ApiController]
[Authorize]
[ApiExplorerSettings(GroupName = "Agent")]
[Route("AIModelPurposeBinding")]
public sealed class AIModelPurposeBindingController(AiModelPurposeBindingService bindings) : ControllerBase
{
    [HttpGet("List")]
    public async Task<ActionResult<AiModelPurposeListDto>> ListAsync(CancellationToken token)
        => Ok(await bindings.ListAsync(token));

    [HttpPut("Set")]
    public async Task<ActionResult<AiModelPurposeBindingDto>> SetAsync(
        [FromBody] SetAiModelPurposeBindingRequest request, CancellationToken token)
    {
        if (request is null)
            return BadRequest(new { errorCode = "InvalidRequest", message = "缺少用途绑定请求" });
        try
        {
            return Ok(await bindings.SetAsync(request.PurposeCode, request.ModelId,
                request.ExpectedRevision, token));
        }
        catch (AiModelPurposeException ex)
        {
            var error = new { errorCode = ex.Code, message = ex.Message };
            return ex.Code switch
            {
                "RevisionConflict" => Conflict(error),
                "UnknownPurpose" or "InvalidModelId" or "InvalidRevision" => BadRequest(error),
                "ModelUnavailable" or "ModelDisabled" or "ModelTypeMismatch" or "ModelIncomplete" =>
                    UnprocessableEntity(error),
                _ => StatusCode(StatusCodes.Status503ServiceUnavailable, error)
            };
        }
    }
}

public sealed record SetAiModelPurposeBindingRequest(
    string PurposeCode, string ModelId, long ExpectedRevision);
