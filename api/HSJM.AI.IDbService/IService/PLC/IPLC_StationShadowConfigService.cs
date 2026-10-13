using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationShadowConfigService : IBaseService<PLC_StationShadowConfigEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationShadowConfigEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}