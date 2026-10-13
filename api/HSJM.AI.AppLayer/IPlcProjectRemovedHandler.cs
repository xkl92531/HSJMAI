using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer;

/// <summary>
/// PLC 项目删除联动钩子（2026-09-04，依赖倒置）：
/// PLC 模块删除 ai_plc_project_new 项目后调用，让各域自行清理从该项目派生的数据。
/// 接口定义在公共层——PLC 模块只依赖抽象，不触碰任何其它域的表；
/// 实现方（如 LineUp 域的 LineUpProjectCascadeApp）在自己的域内软删派生数据。
/// 实现应幂等（重复调用无副作用）、失败不应阻断项目删除（调用方 catch 记日志）。
/// </summary>
public interface IPlcProjectRemovedHandler
{
    /// <param name="plcProjectIds">被删除的 PLC 项目 Id 集合</param>
    Task HandleProjectsRemovedAsync(IReadOnlyCollection<long> plcProjectIds, CancellationToken cancellationToken = default);
}
