using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationAnchorBindingService : IBaseService<PLC_StationAnchorBindingEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationAnchorBindingEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}