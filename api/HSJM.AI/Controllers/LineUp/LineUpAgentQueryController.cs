using System.ComponentModel;
using System.Net;
using HSJM.AI.AppLayer.LineUp.Agent;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

/// <summary>
/// 供外部 Agent 只读调用的两个查询接口：
/// 1. GET LineUp/Agent/BomPointImage —— 按工站编码+子工站编码+（工步编号|BOM编码）获取 BOM 点位图片与标记；
/// 2. GET LineUp/Agent/PointValues  —— 按产线+工站编码解析 projectId 并读实时点位值。
/// </summary>
[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/Agent")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp Agent 只读查询")]
public sealed class LineUpAgentQueryController(
    LineUpAgentQueryApp app,
    ILogger<LineUpAgentQueryController> logger) : ControllerBase
{
    /// <summary>按工站编码获取 BOM 点位图片（点位标记几何 + 图片内容地址）。subStationCode 可选：传 bomCode 时可不填（后端在工站全部子工站内自动定位）。</summary>
    [HttpGet("BomPointImage")]
    [DisplayName("Agent-按编码获取BOM点位图片")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpAgentBomImageResultDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> GetBomPointImage(
        [FromQuery] string workStationCode,
        [FromQuery] string? subStationCode,
        [FromQuery] int? stepNumber,
        [FromQuery] string? bomCode,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.GetBomPointImageAsync(workStationCode, subStationCode, stepNumber, bomCode, cancellationToken),
            $"agent bom point image ws={workStationCode} sub={subStationCode} step={stepNumber} bom={bomCode}",
            cancellationToken);

    /// <summary>按产线+工站编码解析 PLC 项目并读取实时点位值。tagKeyword 支持标签名或 BOM 编码（BOM 清单命中时按其 TagCode 精确过滤，未命中回退标签名包含匹配）。</summary>
    [HttpGet("PointValues")]
    [DisplayName("Agent-按产线工站获取实时点位值")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpAgentPointValuesResultDto>), StatusCodes.Status200OK)]
    public Task<ActionResult> GetPointValues(
        [FromQuery] string? line,
        [FromQuery] string workStationCode,
        [FromQuery] string? tagKeyword,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => app.GetPointValuesAsync(line, workStationCode, tagKeyword, cancellationToken),
            $"agent point values line={line} ws={workStationCode} keyword={tagKeyword}",
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
        catch (LineUpAgentQueryException error)
        {
            logger.LogWarning("LineUp agent query rejected with code {Code}: {Message}", error.Code, error.Message);
            var status = error.Code switch
            {
                "AgentSubStationNotFound" or "AgentStepNotFound" or "AgentBomNotFound" or
                    "AgentLineNotFound" or "AgentWorkStationNotFound" or "AgentPlcProjectNotFound" =>
                    HttpStatusCode.NotFound,
                "AgentCodesRequired" or "AgentFilterRequired" =>
                    HttpStatusCode.BadRequest,
                _ => HttpStatusCode.UnprocessableEntity
            };
            return LineUpControllerResults.Failure(error.Code, status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp agent query {Operation} failed.", operation);
            try
            {
                await WriteLogHelper.WriteLogsAsync(
                    $"LineUpAgentQuery {operation} 异常: {error}" + Environment.NewLine, "LineUpAgentQuery");
            }
            catch
            {
                // 日志失败不影响错误返回
            }
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }
}
