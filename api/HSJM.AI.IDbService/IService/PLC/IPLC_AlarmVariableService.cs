using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_AlarmVariableService : IBaseService<PLC_AlarmVariableEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_AlarmVariableEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}
