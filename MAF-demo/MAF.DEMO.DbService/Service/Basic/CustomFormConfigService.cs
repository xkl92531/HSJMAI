
using System.Linq.Expressions;
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/3/7 11:29:06
using MAF.DEMO.IDbService;
using MAF.DEMO.Core;
using MAF.DEMO.Utility;

namespace MAF.DEMO.DbService
{
    public class CustomFormConfigService : AbstractBaseService<CustomFormConfigEntity>, ICustomFormConfigService,IAutoInject
    {
       /// <summary>
        /// 异步根据条件软删
        /// </summary>
        /// <param name="whereExpression"></param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteAsync(Expression<Func<CustomFormConfigEntity, bool>> whereExpression)
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

        /// <summary>
        /// 异步软删根据id删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteByIdAsync(object id)
        {
            var result = false;
            var delData = GetById(id);
            if (delData!=null)
            {
                delData.IsDeleted = true;
                result = await UpdateAsync(delData);
            }
            return result;
        }

        /// <summary>
        /// 异步软删根据ids删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteByIdsAsync(IList<long> listId)
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