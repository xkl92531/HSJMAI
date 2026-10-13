using System.ComponentModel;
using System.Net;
using HSJM.AI.AppLayer.LineUp.Compatibility;
using HSJM.AI.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/BusinessConfig")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp 业务配置")]
public sealed class LineUpBusinessConfigController(
    LineUpBusinessConfigApp app,
    ILogger<LineUpBusinessConfigController> logger) : ControllerBase
{
    [HttpGet("{kind}")]
    public Task<ActionResult> Query(
        string kind,
        [FromQuery] long parentId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? keyword = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => app.QueryAsync(kind, parentId, page, pageSize, keyword, cancellationToken), cancellationToken);

    [HttpPost("{kind}")]
    public Task<ActionResult> Create(
        string kind,
        [FromBody] LineUpBusinessConfigWriteDto request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => app.CreateAsync(kind, request, cancellationToken), cancellationToken);

    [HttpPut("{kind}/{id:long}")]
    public Task<ActionResult> Update(
        string kind,
        long id,
        [FromBody] LineUpBusinessConfigWriteDto request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => app.UpdateAsync(kind, id, request, cancellationToken), cancellationToken);

    [HttpDelete("{kind}/{id:long}")]
    public Task<ActionResult> Delete(string kind, long id, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await app.DeleteAsync(kind, id, cancellationToken);
            return true;
        }, cancellationToken);

    private async Task<ActionResult> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return LineUpControllerResults.Success(await action());
        }
        catch (LineUpCompatibilityException error)
        {
            logger.LogWarning("LineUp business configuration rejected with code {Code}.", error.Code);
            return LineUpCompatibilityControllerResults.Failure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp business configuration request failed.");
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }
}
