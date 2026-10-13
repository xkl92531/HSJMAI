using System.ComponentModel;
using System.Net;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.AppLayer.LineUp.Runtime;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/RuntimeBindings")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp 运行绑定")]
public sealed class LineUpRuntimeBindingController(
    LineUpRuntimeBindingApp app,
    ILogger<LineUpRuntimeBindingController> logger) : ControllerBase
{
    [HttpPost("{bindingId:long}/Prepare")]
    [DisplayName("生成 LineUp 运行绑定")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpRuntimeBindingPreparationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> Prepare(
        long bindingId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            bindingId,
            "prepare",
            () => app.PrepareAsync(bindingId, cancellationToken),
            cancellationToken);

    [HttpPost("{bindingId:long}/Validate")]
    [DisplayName("在线验证 LineUp 运行绑定")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpRuntimeBindingValidationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> Validate(
        long bindingId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            bindingId,
            "validate",
            () => app.ValidateAsync(bindingId, cancellationToken),
            cancellationToken);

    [HttpPut("{bindingId:long}/Monitoring")]
    [DisplayName("启用或停用 LineUp 监控")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpMonitoringStatusDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> SetMonitoring(
        long bindingId,
        [FromBody] LineUpMonitoringCommand command,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            bindingId,
            command?.Enabled == true ? "enable" : "disable",
            () => app.SetMonitoringAsync(bindingId, command?.Enabled == true, cancellationToken),
            cancellationToken);

    private async Task<ActionResult> ExecuteAsync<T>(
        long bindingId,
        string operation,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return LineUpControllerResults.Success(await action());
        }
        catch (LineUpImportException error)
        {
            logger.LogWarning(
                "LineUp runtime binding {Operation} rejected for binding {BindingId} with code {Code}.",
                operation,
                bindingId,
                error.Code);
            return LineUpControllerResults.BusinessFailure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(
                error,
                "LineUp runtime binding {Operation} failed for binding {BindingId}.",
                operation,
                bindingId);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }
}
