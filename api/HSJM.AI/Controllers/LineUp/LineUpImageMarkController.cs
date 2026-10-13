using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using HSJM.AI.AppLayer.LineUp.ImageMarks;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/ImageMarks")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp BOM 图片点位")]
public sealed class LineUpImageMarkController(
    LineUpImageMarkApp app,
    LineUpBomImportApp importApp,
    ILogger<LineUpImageMarkController> logger) : ControllerBase
{
    private const int MaxMarkerJsonCharacters = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    [HttpGet("Stations/{subStationId:long}")]
    [DisplayName("读取子工站图片点位")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpImageWorkspaceDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> GetStation(
        long subStationId,
        [FromQuery] string? bomKeyword,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.GetStationAsync(subStationId, bomKeyword, cancellationToken),
            "read station image workspace",
            cancellationToken);

    [HttpGet("Plcs")]
    [DisplayName("按产线列出可选 PLC 设备")]
    [ProducesResponseType(typeof(HttpResponseResultModel<IReadOnlyList<LineUpPlcDeviceDto>>), StatusCodes.Status200OK)]
    public Task<ActionResult> GetPlcs(
        [FromQuery] long? productionLineId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.GetPlcDevicesAsync(productionLineId, cancellationToken),
            "list plc devices",
            cancellationToken);

    [HttpPost("Boms/{subStationId:long}")]
    [DisplayName("新增子工站点位(BOM)并绑定标签编码")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpBomMutationDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> CreateBom(
        long subStationId,
        [FromBody] LineUpBomWriteRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.CreateBomAsync(subStationId, request, cancellationToken),
            $"create bom for station {subStationId}",
            cancellationToken);

    [HttpPut("Boms/{bomId:long}")]
    [DisplayName("更新点位(BOM)位号/标签编码")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpBomMutationDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> UpdateBom(
        long bomId,
        [FromBody] LineUpBomWriteRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.UpdateBomAsync(bomId, request, cancellationToken),
            $"update bom {bomId}",
            cancellationToken);

    [HttpDelete("Boms/{bomId:long}")]
    [DisplayName("删除点位(BOM)")]
    public Task<ActionResult> DeleteBom(long bomId, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await app.DeleteBomAsync(bomId, cancellationToken);
            return true;
        }, $"delete bom {bomId}", cancellationToken);

    [HttpPut("Boms/{bomId:long}/Association")]
    [DisplayName("设置点位(BOM)关联状态")]
    public Task<ActionResult> SetBomAssociation(
        long bomId,
        [FromQuery] bool associated,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.SetBomAssociationAsync(bomId, associated, cancellationToken),
            $"set bom {bomId} association to {associated}",
            cancellationToken);

    [HttpPost("Boms/Import/Preview")]
    [Consumes("multipart/form-data")]
    [DisplayName("解析点位清单导入模板(预览)")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpBomImportPreviewDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> ImportBomPreview(
        [FromForm] LineUpBomImportPreviewForm form,
        CancellationToken cancellationToken)
    {
        if (form is null || form.ProductionLineId <= 0 || form.File is null || form.File.Length <= 0)
            return LineUpControllerResults.Failure("LineUpImportFileInvalid", HttpStatusCode.BadRequest);
        try
        {
            await using var stream = form.File.OpenReadStream();
            return LineUpControllerResults.Success(await importApp.PreviewAsync(
                form.ProductionLineId, form.File.FileName, stream, cancellationToken));
        }
        catch (LineUpImageMarkException error)
        {
            return BusinessFailure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp BOM import preview failed.");
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpPost("Boms/Import/Commit")]
    [DisplayName("确认导入点位清单")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpBomImportCommitDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> ImportBomCommit(
        [FromBody] LineUpBomImportCommitRequest? request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => importApp.CommitAsync(request, cancellationToken),
            "import boms from template",
            cancellationToken);

    [HttpPost("Images")]
    [Consumes("multipart/form-data")]
    [DisplayName("上传子工站图片")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpImageDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> UploadImage(
        [FromForm] LineUpImageUploadForm form,
        CancellationToken cancellationToken)
    {
        if (form?.File is null || form.File.Length <= 0)
            return LineUpControllerResults.Failure("LineUpInvalidImage", HttpStatusCode.BadRequest);
        try
        {
            await using var stream = form.File.OpenReadStream();
            return LineUpControllerResults.Success(await app.UploadImageAsync(
                form.SubStationId, form.File.FileName, stream, cancellationToken));
        }
        catch (LineUpImageMarkException error)
        {
            return BusinessFailure(error);
        }
        catch (LineUpImportException error)
        {
            logger.LogWarning("LineUp image upload rejected with code {Code}.", error.Code);
            return LineUpControllerResults.Failure(error.Code, HttpStatusCode.UnprocessableEntity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp image upload failed.");
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpPut("Images/{imageId:long}")]
    [Consumes("multipart/form-data")]
    [DisplayName("保存图片点位")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpImageSaveDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> SaveImage(
        long imageId,
        [FromForm] LineUpImageSaveForm form,
        CancellationToken cancellationToken)
    {
        if (form is null || string.IsNullOrWhiteSpace(form.MarkersJson) ||
            form.MarkersJson.Length > MaxMarkerJsonCharacters)
            return LineUpControllerResults.Failure("LineUpInvalidMarkers", HttpStatusCode.BadRequest);

        IReadOnlyList<LineUpImageMarkerWriteDto>? markers;
        try
        {
            markers = JsonSerializer.Deserialize<IReadOnlyList<LineUpImageMarkerWriteDto>>(
                form.MarkersJson, JsonOptions);
        }
        catch (JsonException)
        {
            return LineUpControllerResults.Failure("LineUpInvalidMarkers", HttpStatusCode.BadRequest);
        }

        if (markers is null)
            return LineUpControllerResults.Failure("LineUpInvalidMarkers", HttpStatusCode.BadRequest);

        try
        {
            await using var rendered = form.RenderedImage?.OpenReadStream();
            return LineUpControllerResults.Success(await app.SaveImageAsync(
                imageId,
                new(form.RowVersion, markers),
                rendered,
                form.RenderedImage?.FileName,
                cancellationToken));
        }
        catch (LineUpImageMarkException error)
        {
            return BusinessFailure(error);
        }
        catch (LineUpImportException error)
        {
            logger.LogWarning("LineUp rendered image rejected with code {Code}.", error.Code);
            return LineUpControllerResults.Failure(error.Code, HttpStatusCode.UnprocessableEntity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp image {ImageId} save failed.", imageId);
            try
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"LineUpImageMark save image={imageId} 异常: {error}\r\n", "LineUpImageMark");
            }
            catch
            {
                // 日志失败不影响错误返回
            }
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpDelete("Images/{imageId:long}")]
    [DisplayName("删除图片及点位")]
    public Task<ActionResult> DeleteImage(long imageId, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await app.DeleteImageAsync(imageId, cancellationToken);
            return true;
        }, "delete image", cancellationToken);

    [HttpGet("Images/{imageId:long}/Content")]
    [DisplayName("读取受控图片内容")]
    public async Task<ActionResult> GetContent(
        long imageId,
        [FromQuery] bool current,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await app.OpenContentAsync(imageId, current, cancellationToken);
            return File(content.Stream, content.ContentType, content.DownloadName, enableRangeProcessing: true);
        }
        catch (LineUpImageMarkException error)
        {
            return BusinessFailure(error);
        }
        catch (LineUpImportException error)
        {
            logger.LogWarning("LineUp image {ImageId} content rejected with code {Code}.", imageId, error.Code);
            return LineUpControllerResults.Failure(error.Code,
                error.Code == "ImageContentMissing" ? HttpStatusCode.NotFound : HttpStatusCode.UnprocessableEntity,
                error.SafeSummary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp image {ImageId} content read failed.", imageId);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpGet("FaultLocation")]
    [DisplayName("按 PLC 标签或 BOM 精确定位图片点位")]
    public Task<ActionResult> GetFaultLocation(
        [FromQuery] string? tagCode,
        [FromQuery] long? bomId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.GetFaultLocationAsync(tagCode, bomId, cancellationToken),
            "locate fault image marker",
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
        catch (LineUpImageMarkException error)
        {
            return BusinessFailure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp image mark operation {Operation} failed.", operation);
            try
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"LineUpImageMark {operation} 异常: {error}\r\n", "LineUpImageMark");
            }
            catch
            {
                // 日志失败不影响错误返回
            }
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    private ObjectResult BusinessFailure(LineUpImageMarkException error)
    {
        logger.LogWarning("LineUp image mark request rejected with code {Code}.", error.Code);
        var status = error.Code switch
        {
            "LineUpSubStationNotFound" or "LineUpImageNotFound" or "LineUpImageContentMissing" or
                "LineUpBomNotFound" =>
                HttpStatusCode.NotFound,
            "LineUpImageVersionConflict" or "LineUpMarkerOwnershipMismatch" or
                "LineUpBomTagCodeDuplicated" or "LineUpBomInUse" =>
                HttpStatusCode.Conflict,
            "LineUpInvalidSubStation" or "LineUpInvalidImage" or "LineUpInvalidImageFileName" or
                "LineUpInvalidRowVersion" or "LineUpInvalidMarkers" or "LineUpFaultLookupRequired" or
                "LineUpInvalidBom" or "LineUpInvalidPlcDevice" =>
                HttpStatusCode.BadRequest,
            _ => HttpStatusCode.UnprocessableEntity
        };
        return LineUpControllerResults.Failure(error.Code, status, error.Message);
    }
}

public sealed class LineUpImageUploadForm
{
    public long SubStationId { get; set; }
    public IFormFile? File { get; set; }
}

public sealed class LineUpImageSaveForm
{
    public long RowVersion { get; set; }
    public string? MarkersJson { get; set; }
    public IFormFile? RenderedImage { get; set; }
}

public sealed class LineUpBomImportPreviewForm
{
    public long ProductionLineId { get; set; }
    public IFormFile? File { get; set; }
}
