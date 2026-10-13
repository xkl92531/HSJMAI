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
/// GRAPH 节点（工步/转换）→ BOM → 点位图 关联快照：
/// 生成/查询"当前工步的 BOM 图片信息"，外部系统可直接查库或走本接口。
/// </summary>
[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/GraphNodeBom")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp GRAPH 节点 BOM 点位图关联")]
public sealed class LineUpGraphNodeBomController(
    LineUpGraphNodeBomApp app,
    ILogger<LineUpGraphNodeBomController> logger) : ControllerBase
{
    [HttpPost("{batchId:long}/Rebuild")]
    [DisplayName("按预览批次重建节点 BOM 关联")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpGraphNodeBomRebuildDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> Rebuild(long batchId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.RebuildAsync(batchId, cancellationToken),
            $"rebuild graph node bom snapshot for batch {batchId}",
            cancellationToken);

    [HttpGet("{batchId:long}")]
    [DisplayName("按批次查节点 BOM 关联")]
    [ProducesResponseType(typeof(HttpResponseResultModel<IReadOnlyList<LineUpGraphNodeBomDto>>), StatusCodes.Status200OK)]
    public Task<ActionResult> ListByBatch(
        long batchId,
        [FromQuery] string? nodeSourceKey,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.ListByBatchAsync(batchId, nodeSourceKey, cancellationToken),
            $"list graph node bom rows for batch {batchId}",
            cancellationToken);

    [HttpGet("ByNode")]
    [DisplayName("按产线+块名+节点查最近一次生成的 BOM 关联")]
    [ProducesResponseType(typeof(HttpResponseResultModel<IReadOnlyList<LineUpGraphNodeBomDto>>), StatusCodes.Status200OK)]
    public Task<ActionResult> ListByNode(
        [FromQuery] long lineId,
        [FromQuery] string blockName,
        [FromQuery] string nodeSourceKey,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.ListByNodeAsync(lineId, blockName, nodeSourceKey, cancellationToken),
            "list graph node bom rows by node",
            cancellationToken);

    private async Task<ActionResult> ExecuteAsync<T>(
        Func<Task<T>> action,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return LineUpControllerResults.Success(await action());
        }
        catch (LineUpGraphNodeBomException error)
        {
            logger.LogWarning("LineUp graph node bom request rejected with code {Code}.", error.Code);
            var status = error.Code switch
            {
                "LineUpBatchNotFound" or "LineUpBatchPreviewNotReady" => HttpStatusCode.NotFound,
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
            logger.LogError(error, "LineUp graph node bom operation {Operation} failed.", operation);
            try
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"LineUpGraphNodeBom {operation} 异常: {error}", "LineUpGraphNodeBom");
            }
            catch
            {
                // 日志失败不影响错误返回
            }

            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }
}
