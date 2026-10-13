using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_LogicUnitSemanticFindingService : IBaseService<PLC_LogicUnitSemanticFindingEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_LogicUnitSemanticFindingEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}
