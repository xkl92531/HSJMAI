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
[Route("LineUp/Import")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp GRAPH 导入")]
public sealed class LineUpImportController(
    LineUpImportApp app,
    ILogger<LineUpImportController> logger) : ControllerBase
{
    [HttpGet("LatestPreview")]
    [DisplayName("查找最新可预览批次")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpLatestPreviewDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> FindLatestPreview(
        [FromQuery] long lineId,
        [FromQuery] long deviceId,
        [FromQuery] string? blockName,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await app.FindLatestPreviewBatchAsync(lineId, deviceId, blockName ?? string.Empty, cancellationToken);
            return LineUpControllerResults.Success(result);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp latest preview lookup failed for block {BlockName}.", blockName);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpPost("Preview")]
    [DisplayName("上传 GRAPH 并创建预览")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpImportStartedDto>), StatusCodes.Status202Accepted)]
    public async Task<ActionResult> StartPreview(
        [FromForm] IFormFile file,
        [FromForm] long lineId,
        [FromForm] long deviceId,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0 || lineId <= 0 || deviceId <= 0)
        {
            return LineUpControllerResults.Failure("InvalidSelection", HttpStatusCode.BadRequest);
        }

        try
        {
            var result = await app.StartPreviewAsync(file, lineId, deviceId, cancellationToken);
            return LineUpControllerResults.Success(result, HttpStatusCode.Accepted);
        }
        catch (LineUpImportException error)
        {
            logger.LogWarning("LineUp preview upload rejected with code {Code}.", error.Code);
            return LineUpControllerResults.BusinessFailure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp preview upload failed.");
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpPost("PreviewFromBlock")]
    [DisplayName("从 PLC GRAPH 块创建预览（免上传）")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpImportStartedDto>), StatusCodes.Status202Accepted)]
    public async Task<ActionResult> StartPreviewFromBlock(
        [FromBody] LineUpImportFromBlockRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.BlockId <= 0 || request.LineId <= 0 || request.DeviceId <= 0)
        {
            return LineUpControllerResults.Failure("InvalidSelection", HttpStatusCode.BadRequest);
        }

        try
        {
            var result = await app.StartPreviewFromBlockAsync(
                request.BlockId, request.LineId, request.DeviceId, cancellationToken);
            return LineUpControllerResults.Success(result, HttpStatusCode.Accepted);
        }
        catch (LineUpImportException error)
        {
            logger.LogWarning(
                "LineUp preview-from-block {BlockId} rejected with code {Code}.",
                request.BlockId, error.Code);
            return LineUpControllerResults.BusinessFailure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp preview-from-block {BlockId} failed.", request.BlockId);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpGet("{batchId:long}/Progress")]
    [DisplayName("查询 GRAPH 导入进度")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpImportProgressDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> GetProgress(long batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(() => app.GetProgressAsync(batchId, cancellationToken), batchId, cancellationToken);

    [HttpGet("{batchId:long}/Preview")]
    [DisplayName("读取 GRAPH 导入预览")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpImportPreviewDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> GetPreview(long batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(() => app.GetPreviewAsync(batchId, cancellationToken), batchId, cancellationToken);

    [HttpPost("{batchId:long}/Commit")]
    [DisplayName("提交 GRAPH 结构")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpImportCommitReceiptDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> Commit(
        long batchId,
        [FromBody] LineUpImportCommitRequest request,
        CancellationToken cancellationToken)
    {
        if (batchId <= 0 || request is null)
        {
            return LineUpControllerResults.Failure("InvalidRequest", HttpStatusCode.BadRequest);
        }

        return await ExecuteAsync(
            () => app.CommitAsync(batchId, request, cancellationToken),
            batchId,
            cancellationToken);
    }

    [HttpPost("HierarchyReassign")]
    [DisplayName("改派结构绑定的工序/工站/子工站")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpHierarchyReassignReceipt>), StatusCodes.Status200OK)]
    public async Task<ActionResult> ReassignHierarchy(
        [FromBody] LineUpHierarchyReassignRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.LineId <= 0 || request.DeviceId <= 0)
        {
            return LineUpControllerResults.Failure("InvalidRequest", HttpStatusCode.BadRequest);
        }

        try
        {
            return LineUpControllerResults.Success(
                await app.ReassignHierarchyBindingsAsync(request, cancellationToken));
        }
        catch (LineUpImportException error)
        {
            Console.Error.WriteLine($"[LineUp] hierarchy reassign rejected {error.Code}: {error.Message}");
            return LineUpControllerResults.BusinessFailure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[LineUp] hierarchy reassign failed: {error.GetType().Name}: {error.Message}");
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    private async Task<ActionResult> ExecuteAsync<T>(
        Func<Task<T>> action,
        long batchId,
        CancellationToken cancellationToken)
    {
        if (batchId <= 0)
        {
            return LineUpControllerResults.Failure("InvalidBatch", HttpStatusCode.BadRequest);
        }

        try
        {
            return LineUpControllerResults.Success(await action());
        }
        catch (LineUpImportException error)
        {
            // EventLog logger 无权限时 Warning/Error 级都会炸（AggregateException 掩盖真实响应），改 Console 直写
            Console.Error.WriteLine($"[LineUp] batch {batchId} rejected {error.Code}: {error.Message}");
            return LineUpControllerResults.BusinessFailure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[LineUp] batch {batchId} request failed: {error.GetType().Name}: {error.Message}");
            Console.Error.WriteLine(error.StackTrace);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }
}
