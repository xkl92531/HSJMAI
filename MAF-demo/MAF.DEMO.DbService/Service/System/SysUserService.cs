using MAF.DEMO.Core;
using MAF.DEMO.IDbService;
using MAF.DEMO.IDbService.IService.System;
using MAF.DEMO.IDbService.Models.System;
using MAF.DEMO.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.DbService.Service.System
{
    /// <summary>
    /// 用户账号
    /// </summary>
    public class SysUserService : AbstractBaseService<SysUserEntity>, ISysUserService, IAutoInject
    {
        /// <summary>
        /// 异步根据条件软删
        /// </summary>
        /// <param name="whereExpression"></param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteAsync(Expression<Func<SysUserEntity, bool>> whereExpression)
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
            if (delData != null)
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
