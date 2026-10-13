using System.ComponentModel;
using System.Net;
using HSJM.AI.AppLayer.LineUp;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/Workspace")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp 工作台")]
public sealed class LineUpWorkspaceController(
    LineUpWorkspaceApp app,
    ILogger<LineUpWorkspaceController> logger) : ControllerBase
{
    [HttpGet]
    [DisplayName("读取 LineUp 工作台")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpWorkspaceDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> Get(
        [FromQuery] long? lineId,
        CancellationToken cancellationToken)
    {
        try
        {
            return LineUpControllerResults.Success(await app.GetAsync(lineId, cancellationToken));
        }
        catch (LineUpImportException error)
        {
            logger.LogWarning("LineUp workspace request rejected with code {Code}.", error.Code);
            return LineUpControllerResults.BusinessFailure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp workspace request failed.");
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }
}
