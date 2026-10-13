using System.ComponentModel;
using System.Net;
using HSJM.AI.AppLayer.LineUp.Compatibility;
using HSJM.AI.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/MasterData")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp 基础资料")]
public sealed class LineUpMasterDataController(
    LineUpMasterDataApp app,
    ILogger<LineUpMasterDataController> logger) : ControllerBase
{
    [HttpGet("{level}")]
    public Task<ActionResult> Query(
        string level,
        [FromQuery] long? parentId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? keyword = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => app.QueryAsync(level, parentId, page, pageSize, keyword, cancellationToken), cancellationToken);

    [HttpPost("{level}")]
    public Task<ActionResult> Create(
        string level,
        [FromBody] LineUpMasterDataWriteDto request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => app.CreateAsync(level, request, cancellationToken), cancellationToken);

    [HttpPut("{level}/{id:long}")]
    public Task<ActionResult> Update(
        string level,
        long id,
        [FromBody] LineUpMasterDataWriteDto request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => app.UpdateAsync(level, id, request, cancellationToken), cancellationToken);

    [HttpDelete("{level}/{id:long}")]
    public Task<ActionResult> Delete(string level, long id, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await app.DeleteAsync(level, id, cancellationToken);
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
            SafeLog(() => logger.LogWarning("LineUp master-data request rejected with code {Code}.", error.Code));
            return LineUpCompatibilityControllerResults.Failure(error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            SafeLog(() => logger.LogError(error, "LineUp master-data request failed."));
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    /// <summary>
    /// 日志兜底：非管理员运行时 EventLog provider 会抛「拒绝访问」，不隔离会把本应
    /// 404/409 的业务响应包装成全局「出现异常」。日志失败绝不能改变业务响应。
    /// </summary>
    private static void SafeLog(Action log)
    {
        try
        {
            log();
        }
        catch
        {
            // 日志基础设施故障（如 EventLog 无权限）时忽略，保证业务响应不受影响
        }
    }
}

internal static class LineUpCompatibilityControllerResults
{
    internal static ActionResult Failure(LineUpCompatibilityException error) =>
        LineUpControllerResults.Failure(error.Code, error.Code switch
        {
            "LineUpDuplicateCode" or "LineUpMasterHasChildren" => HttpStatusCode.Conflict,
            "LineUpRecordNotFound" => HttpStatusCode.NotFound,
            "LineUpInvalidId" or "LineUpInvalidLevel" or "LineUpInvalidKind" or "LineUpInvalidPage" or
                "LineUpInvalidRequest" or "LineUpCodeRequired" or "LineUpNameRequired" or "LineUpValueTooLong" => HttpStatusCode.BadRequest,
            _ => HttpStatusCode.UnprocessableEntity,
        });
}
