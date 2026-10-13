using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationSignalHistoryService : IBaseService<PLC_StationSignalHistoryEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationSignalHistoryEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}