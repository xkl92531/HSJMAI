using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationLogicUnitBindingService : IBaseService<PLC_StationLogicUnitBindingEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationLogicUnitBindingEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}