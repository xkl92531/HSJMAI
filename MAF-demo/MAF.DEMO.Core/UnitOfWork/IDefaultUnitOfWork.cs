using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace MAF.DEMO.Core
{
    public interface IDefaultUnitOfWork<TPrimaryKeyType, TEntity, TCommandRepository> where TEntity : IEntity<TPrimaryKeyType> where TCommandRepository : ICommandBaseRepository<TPrimaryKeyType>
    {
        void RegisterInsert(TEntity entity);


        [Obsolete("V2版本已弃用")]
        void RegisterInsertBatch(IList<TEntity> entities);

        void RegisterUpdate(TEntity entity);


        [Obsolete("V2版本已弃用")]
        void RegisterUpdateBatch(IList<TEntity> entities);

        /// <summary>
        /// 保存， 有则更新 无则新增 entities不存在的删除
        /// </summary>
        /// <param name="entity"></param>
        /// 
        [Obsolete("V2版本已弃用")]
        void RegisterSaveBatch(IList<TEntity> entities);

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



        [Obsolete("已弃用")]
        void RegisterUpdateByFields(TEntity entity, List<string> fields);

        [Obsolete("V2版本已弃用")]
        void RegisterUpdateById(TEntity entity, List<string> fields);



        [Obsolete("V2版本已弃用")]
        void RegisterUpdateSingleFieldByIds(IList<TPrimaryKeyType> idList, KeyValuePair<string, object> column);


        void RegisterDelete(TPrimaryKeyType id);

        void RegisterDeleteBatch(IList<TPrimaryKeyType> idList);

        Task CommitAsync();
    }
}
