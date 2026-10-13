using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_VariablePropagationService : IBaseService<PLC_VariablePropagationEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_VariablePropagationEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}