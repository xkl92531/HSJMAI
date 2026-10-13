using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationShadowSignalService : IBaseService<PLC_StationShadowSignalEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationShadowSignalEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}