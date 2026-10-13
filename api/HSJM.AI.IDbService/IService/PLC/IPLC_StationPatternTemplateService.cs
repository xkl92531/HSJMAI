using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationPatternTemplateService : IBaseService<PLC_StationPatternTemplateEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationPatternTemplateEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}