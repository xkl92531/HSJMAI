using System.Linq.Expressions;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;

namespace HSJM.AI.DbService
{
    public class PLC_StationAnchorBindingService : AbstractBaseService<PLC_StationAnchorBindingEntity>, IPLC_StationAnchorBindingService, IAutoInject
    {
        public async Task<bool> SoftDeleteAsync(Expression<Func<PLC_StationAnchorBindingEntity, bool>> whereExpression)
        {
            var result = false;
            var delDatas = await GetListAsync(whereExpression);
            if (delDatas.IsListNullOrEmpty() == false)
            {
                delDatas.ForEach(x => x.IsDeleted = true);
                int rows = await UpdateRangeAsync(delDatas);
                result = rows > 0;
            }
            return result;
        }

        public async Task<bool> SoftDeleteByIdAsync(object id)
        {
            var result = false;
            var delData = GetById(id);
            if (delData != null)
            {
                delData.IsDeleted = true;
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
                delDatas.ForEach(x => x.IsDeleted = true);
                int rows = await UpdateRangeAsync(delDatas);
                result = rows > 0;
            }
            return result;
        }
    }
}