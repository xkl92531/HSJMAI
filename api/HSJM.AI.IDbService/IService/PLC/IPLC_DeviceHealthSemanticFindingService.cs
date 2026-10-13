using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_DeviceHealthSemanticFindingService : IBaseService<PLC_DeviceHealthSemanticFindingEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_DeviceHealthSemanticFindingEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}
