using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationShadowEventService : IBaseService<PLC_StationShadowEventEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationShadowEventEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}