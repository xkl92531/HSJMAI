using System.ComponentModel;
using System.Net;
using HSJM.AI.AppLayer.LineUp;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/GraphCatalog")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp GRAPH 结构")]
public sealed class LineUpGraphCatalogController(
    LineUpGraphCatalogApp app,
    ILogger<LineUpGraphCatalogController> logger) : ControllerBase
{
    [HttpGet("Lookups")]
    [DisplayName("读取 GRAPH 查询条件")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpGraphCatalogLookupDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> GetLookups(
        [FromQuery] long? plcProjectId,
        [FromQuery] long? lineId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "load lookups",
            () => app.GetLookupsAsync(plcProjectId, lineId, cancellationToken),
            cancellationToken);

    [HttpGet]
    [DisplayName("查询 GRAPH 结构目录")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpGraphCatalogPageDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> Query(
        [FromQuery] LineUpGraphCatalogQueryDto query,
        CancellationToken cancellationToken) =>
        ExecuteAsync("query catalog", () => app.QueryAsync(query, cancellationToken), cancellationToken);

    [HttpGet("{assetId:long}")]
    [DisplayName("读取 GRAPH 结构详情")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpGraphCatalogDetailDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> GetDetail(
        long assetId,
        [FromQuery] long? bindingId,
        CancellationToken cancellationToken) =>
        ExecuteAsync("load asset", () => app.GetDetailAsync(assetId, bindingId, cancellationToken), cancellationToken);

    [HttpPost("{assetId:long}/Bindings")]
    [DisplayName("绑定 GRAPH 到工位和 PLC 设备")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpGraphBindingDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> SaveBinding(
        long assetId,
        [FromBody] LineUpGraphBindingRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync("save binding", () => app.SaveBindingAsync(assetId, request, cancellationToken), cancellationToken);

    [HttpPut("Bindings/{bindingId:long}/Aliases")]
    [DisplayName("保存 GRAPH 元素别名")]
    [ProducesResponseType(typeof(HttpResponseResultModel<IReadOnlyList<LineUpGraphAliasDto>>), StatusCodes.Status200OK)]
    public Task<ActionResult> SaveAliases(
        long bindingId,
        [FromBody] LineUpGraphAliasSaveRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync("save aliases", () => app.SaveAliasesAsync(bindingId, request, cancellationToken), cancellationToken);

    [HttpGet("Bindings/{bindingId:long}/PublishPreview")]
    [DisplayName("预览 GRAPH 发布差异")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpGraphPublishPreviewDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> PreviewPublish(
        long bindingId,
        [FromQuery] long targetRevisionId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "preview publication",
            () => app.PreviewPublishAsync(bindingId, targetRevisionId, cancellationToken),
            cancellationToken);

    [HttpPost("Bindings/{bindingId:long}/Publish")]
    [DisplayName("发布或回退 GRAPH 结构版本")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpGraphPublishReceiptDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> Publish(
        long bindingId,
        [FromBody] LineUpGraphPublishRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            "publish revision",
            () => app.PublishAsync(bindingId, request, CurrentUserInfo.Instance.UserID, cancellationToken),
            cancellationToken);

    private async Task<ActionResult> ExecuteAsync<T>(
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
            logger.LogWarning("LineUp GRAPH catalog {Operation} rejected with code {Code}.", operation, error.Code);
            return LineUpControllerResults.BusinessFailure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp GRAPH catalog {Operation} failed.", operation);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }
}
