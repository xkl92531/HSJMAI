using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer;

/// <summary>
/// PLC 项目元数据变更联动钩子（2026-09-14，依赖倒置）：
/// PLC 工程 zip 重新上传解析完成（块/接口/IO Tag 等元数据表被重写）后调用，
/// 让各域清理依赖这些元数据的进程内缓存/派生结构。
/// 接口定义在公共层——PLC 模块只依赖抽象，不触碰任何其它域；
/// 实现应幂等（重复调用无副作用）、失败不应阻断上传（调用方 catch 记日志）。
/// 与 IPlcProjectRemovedHandler（项目删除级联）对称。
/// </summary>
public interface IPlcProjectMetadataChangedHandler
{
    /// <param name="plcProjectIds">元数据被重写的 PLC 项目 Id 集合</param>
    Task HandleProjectsMetadataChangedAsync(IReadOnlyCollection<long> plcProjectIds, CancellationToken cancellationToken = default);
}
