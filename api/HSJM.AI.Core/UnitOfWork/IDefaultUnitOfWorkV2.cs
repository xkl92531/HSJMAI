using HSJM.AI.Core;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace HSJM.AI.Core
{
    public interface IDefaultUnitOfWorkV2<TPrimaryKeyType, TEntity, TCommandRepository> where TEntity : IEntity<TPrimaryKeyType> where TCommandRepository : ICommandBaseRepository<TPrimaryKeyType>
    {
        void RegisterInsert(TEntity entity);

        void RegisterUpdate(TEntity entity);


        /// <summary>
        ///  有则更新（增加），无则删除
        /// 1.entities中有， oldIdList没有的数据插入
        /// 2.oldIdList 和entities中有 都有的数据更新
        /// 3.oldIdList中有，entities中没有的数据删除
        /// </summary>
        /// <param name="newEntityList"></param>
        /// <param name="oldIdList"></param>
        /// <param name="unitofWorkRepository"></param>
        void RegisterUpsertDelete(List<TEntity> newEntityList, List<TPrimaryKeyType> oldIdList);





        void RegisterDelete(TPrimaryKeyType id);

        void RegisterDeleteBatch(IList<TPrimaryKeyType> idList);

        Task CommitAsync();
    }
}
