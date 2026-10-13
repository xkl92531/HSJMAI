using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpCompatibilityRepository
{
    Task<LineUpMasterPageData> QueryMasterAsync(LineUpMasterQuery query, CancellationToken cancellationToken);
    Task<LineUpMasterParentLookup?> GetMasterParentAsync(string level, long parentId, CancellationToken cancellationToken);
    Task<bool> MasterCodeExistsAsync(string level, long parentId, string code, long? excludeId, CancellationToken cancellationToken);
    Task<long> CreateMasterAsync(LineUpMasterWrite write, CancellationToken cancellationToken);
    Task<bool> UpdateMasterAsync(LineUpMasterWrite write, CancellationToken cancellationToken);
    Task<bool> SoftDeleteMasterAsync(string level, long id, CancellationToken cancellationToken);
    Task<LineUpMasterDeleteCheck> GetMasterDeleteCheckAsync(string level, long id, CancellationToken cancellationToken);
    /// <summary>
    /// 级联软删层级基础信息：自身 + 全部下级（工序/工站/子工站）+ 动作及其执行条件/异常解决方案/动作步骤。
    /// 仅用于层级级别（ProductionLine/Process/WorkStation/SubStation）；Device 不支持级联（保持引用保护）。
    /// 返回受影响（软删）记录总数。
    /// </summary>
    Task<int> CascadeSoftDeleteMasterAsync(string level, long id, CancellationToken cancellationToken);

    Task<LineUpBusinessPageData> QueryBusinessAsync(LineUpBusinessQuery query, CancellationToken cancellationToken);
    Task<LineUpBusinessParentLookup?> GetBusinessParentAsync(string kind, long parentId, CancellationToken cancellationToken);
    Task<bool> BusinessCodeExistsAsync(string kind, long parentId, string code, long? excludeId, CancellationToken cancellationToken);
    Task<long> CreateBusinessAsync(LineUpBusinessWrite write, CancellationToken cancellationToken);
    Task<bool> UpdateBusinessAsync(LineUpBusinessWrite write, CancellationToken cancellationToken);
    Task<bool> SoftDeleteBusinessAsync(string kind, long id, CancellationToken cancellationToken);
}
