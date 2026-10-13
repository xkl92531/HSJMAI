using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationBlockBindingService : IBaseService<PLC_StationBlockBindingEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationBlockBindingEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}