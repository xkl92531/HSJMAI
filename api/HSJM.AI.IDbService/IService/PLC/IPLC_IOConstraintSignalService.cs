using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IPLC_IOConstraintSignalService : IBaseService<PLC_IOConstraintSignalEntity>
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
        Task<bool> SoftDeleteAsync(Expression<Func<PLC_IOConstraintSignalEntity, bool>> whereExpression);

        /// <summary>
        /// 异步软删根据ids删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}
