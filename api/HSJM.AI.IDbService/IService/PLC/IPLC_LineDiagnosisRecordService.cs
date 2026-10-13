using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_LineDiagnosisRecordService : IBaseService<PLC_LineDiagnosisRecordEntity>
    {
        Task<bool> SoftDeleteByIdAsync(object id);

        Task<bool> SoftDeleteAsync(Expression<Func<PLC_LineDiagnosisRecordEntity, bool>> whereExpression);

        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}
