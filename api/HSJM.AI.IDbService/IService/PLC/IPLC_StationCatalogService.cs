using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationCatalogService : IBaseService<PLC_StationCatalogEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationCatalogEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}