using System.Linq.Expressions;

namespace HSJM.AI.IDbService
{
    public interface IChat_MessageService : IBaseService<Chat_MessageEntity>
    {
        /// <summary>
        /// 异步软删根据id删除
        /// </summary>
        Task<bool> SoftDeleteByIdAsync(object id);

        /// <summary>
        /// 异步根据条件软删
        /// </summary>
        Task<bool> SoftDeleteAsync(Expression<Func<Chat_MessageEntity, bool>> whereExpression);

        /// <summary>
        /// 异步软删根据ids删除
        /// </summary>
        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
    }
}
