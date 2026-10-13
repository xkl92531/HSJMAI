using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Runtime;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.LineUp;

/// <summary>
/// PLC 元数据变更 → LineUp 监控缓存联动（2026-09-14）：
/// PLC 工程 zip 重传后块/接口/iotag 被重写，但绑定行（RuntimeBindingJson/ValidationRevision）不变，
/// 绑定缓存版本比对发现不了；此处主动按 PlcProjectId 逐出绑定缓存与采集规则缓存，
/// 下一轮采样即走全量重建（含 IsCurrentAsync 元数据时效校验）。
/// 与 LineUpProjectCascadeApp（项目删除级联，IPlcProjectRemovedHandler）对称的依赖倒置实现。
/// </summary>
public class LineUpMonitorCacheMaintenanceApp : IPlcProjectMetadataChangedHandler, IAutoInject, ISelfScopedAutoInject
{
    private readonly LineUpMonitorBindingCache _bindingCache;
    private readonly LineUpTagCollectionRuleCache _ruleCache;
    private readonly ILogger<LineUpMonitorCacheMaintenanceApp> _logger;

    public LineUpMonitorCacheMaintenanceApp(
        LineUpMonitorBindingCache bindingCache,
        LineUpTagCollectionRuleCache ruleCache,
        ILogger<LineUpMonitorCacheMaintenanceApp> logger)
    {
        _bindingCache = bindingCache;
        _ruleCache = ruleCache;
        _logger = logger;
    }

    public Task HandleProjectsMetadataChangedAsync(
        IReadOnlyCollection<long> plcProjectIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plcProjectIds);
        foreach (var plcProjectId in plcProjectIds)
        {
            try
            {
                _bindingCache.InvalidateByPlcProject(plcProjectId);
                _ruleCache.Invalidate(plcProjectId);
            }
            catch (Exception error)
            {
                _logger.LogWarning(error,
                    "[LineUpMonitor] PLC 元数据变更缓存联动失败（不影响上传，30s 复查兜底） project={PlcProjectId}",
                    plcProjectId);
            }
        }

        return Task.CompletedTask;
    }
}
