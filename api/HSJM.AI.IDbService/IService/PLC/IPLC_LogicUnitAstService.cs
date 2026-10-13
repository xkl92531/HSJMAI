using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_LogicUnitAstService : IBaseService<PLC_LogicUnitAstEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_LogicUnitAstEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}
