using System.ComponentModel;
using System.Net;
using HSJM.AI.AppLayer.LineUp.GraphNodeBom;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

/// <summary>
/// GRAPH 节点成品图快照：保存 graph 结构时前端上传"成品图+匹配 BOM 红点"物化 PNG，
/// 查看流程图按节点直接读取，不再运行时匹配。
/// </summary>
[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/GraphNodeImage")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp GRAPH 节点成品图快照")]
public sealed class LineUpGraphNodeImageController(
    LineUpGraphNodeImageApp app,
    ILogger<LineUpGraphNodeImageController> logger) : ControllerBase
{
    [HttpPost("Save")]
    [DisplayName("保存节点成品图快照（同批次同节点整组替换）")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpGraphNodeImageSaveDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> Save(
        LineUpGraphNodeImageSaveRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.SaveNodeImagesAsync(request, cancellationToken),
            $"save graph node image snapshot for node {request.NodeSourceKey}",
            cancellationToken);

    [HttpGet("ByNode")]
    [DisplayName("按产线+块名+节点查最近一次保存的成品图快照")]
    [ProducesResponseType(typeof(HttpResponseResultModel<IReadOnlyList<LineUpGraphNodeImageDto>>), StatusCodes.Status200OK)]
    public Task<ActionResult> ListByNode(
        [FromQuery] long lineId,
        [FromQuery] string blockName,
        [FromQuery] string nodeSourceKey,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.ListByNodeAsync(lineId, blockName, nodeSourceKey, cancellationToken),
            "list graph node image snapshots by node",
            cancellationToken);

    [HttpGet("Content")]
    [DisplayName("读取节点成品图内容")]
    public async Task<ActionResult> GetContent(
        [FromQuery] string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await app.ResolveContentAsync(key, cancellationToken);
            return PhysicalFile(content.Path, content.ContentType, content.DownloadName, enableRangeProcessing: true);
        }
        catch (LineUpGraphNodeImageException error)
        {
            logger.LogWarning("LineUp graph node image content rejected with code {Code}.", error.Code);
            return LineUpControllerResults.Failure(
                error.Code,
                error.Code == "GraphNodeImageContentMissing" ? HttpStatusCode.NotFound : HttpStatusCode.UnprocessableEntity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp graph node image content read failed for key {Key}.", key);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    private async Task<ActionResult> ExecuteAsync<T>(
        Func<Task<T>> action,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return LineUpControllerResults.Success(await action());
        }
        catch (LineUpGraphNodeImageException error)
        {
            logger.LogWarning("LineUp graph node image request rejected with code {Code}.", error.Code);
            var status = error.Code switch
            {
                "GraphNodeImagePayloadInvalid" or "GraphNodeImageIdentityInvalid" or "GraphNodeImageContentInvalid"
                    => HttpStatusCode.BadRequest,
                _ => HttpStatusCode.BadRequest,
            };
            return LineUpControllerResults.Failure(error.Code, status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp graph node image operation {Operation} failed.", operation);
            try
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"LineUpGraphNodeImage {operation} 异常: {error}", "LineUpGraphNodeImage");
            }
            catch
            {
                // 日志失败不影响错误返回
            }

            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }
}