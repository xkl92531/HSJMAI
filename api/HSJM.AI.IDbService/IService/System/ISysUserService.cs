using HSJM.AI.IDbService.Models.System;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.IDbService.IService.System
{
    /// <summary>
    /// 用户账号接口
    /// </summary>
    public interface ISysUserService: IBaseService<SysUserEntity>
    {
            /// <summary>
        /// 异步软删根据id删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        Task<bool> SoftDeleteByIdAsync(object id);


        /// <summary>
        /// 异步根据条件软删
        /// </summary>
        /// <param name="whereExpression"></param>
        /// <returns></returns>
        Task<bool> SoftDeleteAsync(Expression<Func<SysUserEntity, bool>> whereExpression);

        /// <summary>
        /// 异步软删根据ids删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        Task<bool> SoftDeleteByIdsAsync(IList<long> listId);
    }
}
