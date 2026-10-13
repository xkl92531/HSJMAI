
using System.Linq.Expressions;
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:49
namespace HSJM.AI.IDbService
{
    public interface ISysRoleUsersService : IBaseService<SysRoleUsersEntity>
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
        Task<bool> SoftDeleteAsync(Expression<Func<SysRoleUsersEntity, bool>> whereExpression);

        /// <summary>
        /// 异步软删根据ids删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        Task<bool> SoftDeleteByIdsAsync(IList<long> listId);
    }
}