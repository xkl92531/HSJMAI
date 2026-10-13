using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace HSJM.AI.Core
{
    public interface IUnitOfWork<TPrimaryKeyType>
    {
        /// <summary>
        /// 插入
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="unitofWorkRepository"></param>
        void RegisterInsert(IEntity<TPrimaryKeyType> entity, ICommandBaseRepository<TPrimaryKeyType> unitofWorkRepository);

        /// <summary>
        /// 保存，不支持多个同一类实体（同一个类型实体只能添加一个，否则会异常）
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="unitofWorkRepository"></param>
        /// 
        [Obsolete("V2版本已弃用")]
        void RegisterSave(IEntity<TPrimaryKeyType> entity, ICommandBaseRepository<TPrimaryKeyType> unitofWorkRepository)
        {

        }

        /// <summary>
        /// 保存， 有则更新 无则新增 entities不存在的删除
        /// </summary>
        /// <param name="entity"></param>
        /// 
        [Obsolete("V2版本已弃用")]
        void RegisterSaveBatch(List<IEntity<TPrimaryKeyType>> entities, ICommandBaseRepository<TPrimaryKeyType> unitofWorkRepository)
        {

        }

        /// <summary>
        /// 更新
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="unitofWorkRepository"></param>
        void RegisterUpdate(IEntity<TPrimaryKeyType> entity, ICommandBaseRepository<TPrimaryKeyType> unitofWorkRepository);

        [Obsolete("V2版本已弃用")]
        void RegisterUpdateBatch(List<IEntity<TPrimaryKeyType>> entityList, ICommandBaseRepository<TPrimaryKeyType> unitofWorkRepository)
        {

        }

        /// <summary>
        ///  有则更新（增加），无则删除
        /// 1.entities中有， oldIdList没有的数据插入
        /// 2.oldIdList 和entities中有 都有的数据更新
        /// 3.oldIdList中有，entities中没有的数据删除
        /// </summary>
        /// <param name="newEntityList"></param>
        /// <param name="oldIdList"></param>
        /// <param name="unitofWorkRepository"></param>
        void RegisterUpsertDelete(List<IEntity<TPrimaryKeyType>> newEntityList, List<TPrimaryKeyType> oldIdList, ICommandBaseRepository<TPrimaryKeyType> unitofWorkRepository);


        /// <summary>
        /// 删除
        /// </summary>
        /// <param name="id"></param>
        /// <param name="unitofWorkRepository"></param>
        void RegisterDelete(TPrimaryKeyType id, ICommandBaseRepository<TPrimaryKeyType> unitofWorkRepository);

        /// <summary>
        /// 根据id更新字段
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="fields"></param>
        /// <param name="unitofWorkRepository"></param>
        /// 
        [Obsolete("V2版本已弃用")]
        void RegisterUpdateById(IEntity<TPrimaryKeyType> entity, IList<string> fields, ICommandBaseRepository<TPrimaryKeyType> unitofWorkRepository)
        {

        }




        /// <summary>
        /// 根据id集合更新单个字段
        /// </summary>
        /// <param name="id"></param>
        /// <param name="column"></param>
        /// <param name="unitofWorkRepository"></param>
        /// 
        [Obsolete("V2版本已弃用")]
        void RegisterUpdateSingleFieldByIds(IList<TPrimaryKeyType> id, KeyValuePair<string, object> column, ICommandBaseRepository<TPrimaryKeyType> unitofWorkRepository)
        {

        }

        Task CommitAsync();
    }
}
