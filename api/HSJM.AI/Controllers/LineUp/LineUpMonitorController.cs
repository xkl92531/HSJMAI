using System.ComponentModel;
using System.Net;
using HSJM.AI.AppLayer.LineUp.Runtime;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.IDbService;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

[ApiExplorerSettings(GroupName = "LineUp")]
[Route("LineUp/Monitor")]
[ApiController]
[ModelValidation]
[Authorize]
[DisplayName("LineUp 设备运行监控")]
public sealed class LineUpMonitorController(
    LineUpMonitorCoordinator coordinator,
    LineUpMonitorNavigationApp navigationApp,
    ILineUpTagCollectionRuleRepository collectionRuleRepository,
    LineUpTagCollectionRuleCache collectionRuleCache,
    ILogger<LineUpMonitorController> logger) : ControllerBase
{
    [HttpGet("Navigation")]
    [DisplayName("读取运行监控层级导航")]
    [ProducesResponseType(typeof(HttpResponseResultModel<IReadOnlyList<LineUpMonitorNavigationNodeDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult> GetNavigation(
        [FromQuery] bool includeUnconfigured = false,
        [FromQuery] string? keyword = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return LineUpControllerResults.Success(
                await navigationApp.GetNavigationAsync(includeUnconfigured, keyword, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp monitor navigation request failed.");
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpGet("Bindings")]
    [DisplayName("读取运行监控绑定目录")]
    [ProducesResponseType(typeof(HttpResponseResultModel<IReadOnlyList<LineUpMonitorBindingSummaryDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult> GetBindings(
        [FromQuery] long? subStationId,
        [FromQuery] long? lineId,
        [FromQuery] long? deviceId,
        [FromQuery] long? stationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedSubStationId = subStationId is > 0
                ? subStationId
                : lineId is > 0 && deviceId is > 0 && stationId is > 0
                    ? await coordinator.ResolveSubStationAsync(
                        lineId.Value, deviceId.Value, stationId.Value, cancellationToken)
                    : null;
            if (resolvedSubStationId is not > 0)
            {
                // 查无已发布绑定的 (line, device, station) 组合不是坏请求：返回空目录。
                // 原先回 400 会让前端把“尚未发布”当错误无限重试并触发全局错误提示。
                return LineUpControllerResults.Success(
                    Array.Empty<LineUpMonitorBindingSummaryDto>());
            }
            return LineUpControllerResults.Success(
                await coordinator.GetBindingsAsync(resolvedSubStationId.Value, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error,
                "LineUp monitor binding catalog request failed for line {LineId}, device {DeviceId}, station {StationId}.",
                lineId,
                deviceId,
                stationId);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpGet("{bindingId:long}/Definition")]
    [DisplayName("读取运行监控定义")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpMonitorDefinitionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> GetDefinition(
        long bindingId,
        [FromQuery] string sequenceSourceKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var definition = await coordinator.GetDefinitionAsync(Key(bindingId, sequenceSourceKey), cancellationToken);
            return definition is null
                ? LineUpControllerResults.Failure("LineUpMonitorUnavailable", HttpStatusCode.NotFound)
                : LineUpControllerResults.Success(LineUpMonitorWireProjection.Definition(definition));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error,
                "LineUp monitor definition request failed for binding {BindingId} sequence {SequenceSourceKey}.",
                bindingId,
                sequenceSourceKey);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpGet("{bindingId:long}/Snapshot")]
    [DisplayName("读取运行监控快照")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpMonitorSnapshotDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> GetSnapshot(
        long bindingId,
        [FromQuery] string sequenceSourceKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await coordinator.GetSnapshotAsync(Key(bindingId, sequenceSourceKey), cancellationToken);
            return snapshot is null
                ? LineUpControllerResults.Failure("LineUpMonitorUnavailable", HttpStatusCode.NotFound)
                : LineUpControllerResults.Success(LineUpMonitorWireProjection.Snapshot(snapshot));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error,
                "LineUp monitor snapshot request failed for binding {BindingId} sequence {SequenceSourceKey}.",
                bindingId,
                sequenceSourceKey);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpGet("{bindingId:long}/Events")]
    [DisplayName("读取运行监控事件")]
    [ProducesResponseType(typeof(HttpResponseResultModel<LineUpMonitorEventPageDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult> GetEvents(
        long bindingId,
        [FromQuery] string sequenceSourceKey,
        [FromQuery] long? beforeEventId,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return LineUpControllerResults.Success(
                await coordinator.GetEventsAsync(
                    Key(bindingId, sequenceSourceKey),
                    beforeEventId,
                    limit,
                    cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error,
                "LineUp monitor event request failed for binding {BindingId} sequence {SequenceSourceKey}.",
                bindingId,
                sequenceSourceKey);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    // ===== 变量采集开关（2026-09-12）=====
    // 屏蔽 = 变量不进运行时 ReadPlan：引用它的转移条件求值落 Unknown（不降级连接状态）。
    // 仅允许条件类变量；GRAPH 工步执行位（RT_DATA.EXEC_BITS）禁止屏蔽。

    public sealed record TagCollectionRuleRequest(long PlcProjectId, string VariablePath, string? Reason = null);

    [HttpGet("TagCollection/Rules")]
    [DisplayName("读取变量采集屏蔽规则")]
    [ProducesResponseType(typeof(HttpResponseResultModel<IReadOnlyList<string>>), StatusCodes.Status200OK)]
    public async Task<ActionResult> GetTagCollectionRules(
        [FromQuery] long plcProjectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (plcProjectId <= 0)
            {
                return LineUpControllerResults.Failure("PlcProjectIdInvalid", HttpStatusCode.BadRequest);
            }
            collectionRuleCache.Invalidate(plcProjectId);
            return LineUpControllerResults.Success(
                await collectionRuleRepository.ListDisabledAsync(plcProjectId, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error, "LineUp tag collection rule list failed for project {PlcProjectId}.", plcProjectId);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpPost("TagCollection/Disable")]
    [DisplayName("屏蔽变量采集")]
    public async Task<ActionResult> DisableTagCollection(
        [FromBody] TagCollectionRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = request.VariablePath?.Trim() ?? string.Empty;
            if (request.PlcProjectId <= 0 || normalized.Length == 0)
            {
                return LineUpControllerResults.Failure("TagCollectionRuleRequestInvalid", HttpStatusCode.BadRequest);
            }
            if (normalized.Contains("RT_DATA.EXEC_BITS.", StringComparison.OrdinalIgnoreCase))
            {
                return LineUpControllerResults.Failure(
                    "StepExecutionCannotBeDisabled", HttpStatusCode.BadRequest);
            }
            await collectionRuleRepository.DisableAsync(
                request.PlcProjectId, normalized, request.Reason, cancellationToken);
            collectionRuleCache.Invalidate(request.PlcProjectId);
            return LineUpControllerResults.Success(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error,
                "LineUp tag collection disable failed for project {PlcProjectId} path {VariablePath}.",
                request.PlcProjectId, request.VariablePath);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    [HttpPost("TagCollection/Enable")]
    [DisplayName("恢复变量采集")]
    public async Task<ActionResult> EnableTagCollection(
        [FromBody] TagCollectionRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = request.VariablePath?.Trim() ?? string.Empty;
            if (request.PlcProjectId <= 0 || normalized.Length == 0)
            {
                return LineUpControllerResults.Failure("TagCollectionRuleRequestInvalid", HttpStatusCode.BadRequest);
            }
            await collectionRuleRepository.EnableAsync(request.PlcProjectId, normalized, cancellationToken);
            collectionRuleCache.Invalidate(request.PlcProjectId);
            return LineUpControllerResults.Success(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LineUpControllerResults.Failure("RequestCancelled", (HttpStatusCode)499);
        }
        catch (Exception error)
        {
            logger.LogError(error,
                "LineUp tag collection enable failed for project {PlcProjectId} path {VariablePath}.",
                request.PlcProjectId, request.VariablePath);
            return LineUpControllerResults.Failure("InternalError", HttpStatusCode.InternalServerError);
        }
    }

    private static LineUpMonitorKey Key(long bindingId, string sequenceSourceKey) =>
        new(bindingId, sequenceSourceKey?.Trim() ?? string.Empty);
}
