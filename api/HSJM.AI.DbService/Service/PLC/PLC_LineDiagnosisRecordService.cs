using System.Linq.Expressions;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;

namespace HSJM.AI.DbService
{
    public class PLC_LineDiagnosisRecordService : AbstractBaseService<PLC_LineDiagnosisRecordEntity>, IPLC_LineDiagnosisRecordService, IAutoInject
    {
        public async Task<bool> SoftDeleteAsync(Expression<Func<PLC_LineDiagnosisRecordEntity, bool>> whereExpression)
        {
            return await UpdateAsync(
                it => new PLC_LineDiagnosisRecordEntity
                {
                    IsDeleted = true,
                    ModifyTime = DateTime.Now
                },
                whereExpression);
        }

        public async Task<bool> SoftDeleteByIdAsync(object id)
        {
            var result = false;
            var delData = GetById(id);
            if (delData != null)
            {
                delData.IsDeleted = true;
                delData.ModifyTime = DateTime.Now;
                result = await UpdateAsync(delData);
            }
            return result;
        }

        public async Task<bool> SoftDeleteByIdsAsync(List<long> listId)
        {
            var result = false;
            var delDatas = await GetListByIdsAsync(listId);
            if (delDatas.IsListNullOrEmpty() == false)
            {
                delDatas.ForEach(x =>
                {
                    x.IsDeleted = true;
                    x.ModifyTime = DateTime.Now;
                });
                int rows = await UpdateRangeAsync(delDatas);
                result = rows > 0;
            }
            return result;
        }
    }
}
