using HSJM.AI.IDbService.DbBase;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 基础接口
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IBaseService<T> where T : class, new()
    {
        DbSet<T> GetDb();
        DbTransaction BeginTran(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted);
        void CommitTran();
        void RollbackTran();

        #region Insert

        /// <summary>
        /// 插入(NUll不插入值)，返回是否成功
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        bool InsertSplitTable(T model);

        /// <summary>
        /// 插入(NUll不插入值)，返回是否成功
        /// </summary>
        /// <param name="models"></param>
        /// <returns></returns>
        bool InsertRangeSplitTable(IEnumerable<T> models);

        bool DeleteSplitTable(T model);

        bool DeleteRangeSplitTable(IEnumerable<T> models);


        /// <summary>
        /// 插入(NUll不插入值)，返回是否成功
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        bool Insert(T model);

        /// <summary>
        /// 同步插入，返回雪花Id
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        long InsertSnowflakeId(T t);

        /// <summary>
        /// 插入，返回自增id
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        List<long> InsertSnowflakeId(List<T> model);

        /// <summary>
        /// 异步插入，返回是否成功
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        Task<bool> InsertAsync(T model);

        /// <summary>
        /// 异步插入，返回雪花Id
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        Task<long> InsertSnowflakeIdAsync(T t);

        /// <summary>
        /// 异步插入，返回自增id
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        Task<List<long>> InsertSnowflakeIdAsync(List<T> model);

        /// <summary>
        /// 异步批量插入，返回是否成功
        /// </summary>
        /// <param name="dbModels">主键集合</param>
        /// <returns></returns>
        bool InsertRange(T[] dbModels);

        /// <summary>
        /// 异步批量插入，返回是否成功
        /// </summary>
        /// <param name="dbModels">Db模型集合</param>
        /// <returns></returns>
        Task<bool> InsertRangeAsync(T[] dbModels);

        /// <summary>
        /// 异步批量插入，返回是否成功
        /// </summary>
        /// <param name="lisT">Db模型集合</param>
        /// <returns></returns>
        bool InsertRange(List<T> lisT);

        /// <summary>
        /// 异步批量插入，返回是否成功
        /// </summary>
        /// <param name="lisT">Db模型集合</param>
        /// <returns></returns>
        Task<bool> InsertRangeAsync(List<T> lisT);

        /// <summary>
        /// 插入，返回自增id
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        int InsertReturnIdentity(T model);

        /// <summary>
        /// 异步插入，返回自增id
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        Task<int> InsertReturnIdentityAsync(T model);

        /// <summary>
        /// 插入，返回model
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        T InsertReturnEntity(T model);

        /// <summary>
        /// 异步插入，返回model
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        Task<T> InsertReturnEntityAsync(T model);

        /// <summary>
        /// 插入，返回影响行数
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        int InsertReturnCommand(T model);

        /// <summary>
        /// 批量插入，返回影响行数
        /// </summary>
        /// <param name="models"></param>
        /// <returns></returns>
        int InsertRangeReturnCommand(IEnumerable<T> models);

        #endregion

        #region Delete

        /// <summary>
        /// 删除
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        bool Delete(T model);

        /// <summary>
        /// 异步删除
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        Task<bool> DeleteAsync(T model);

        /// <summary>
        /// 异步批量删除
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        Task<bool> DeleteRangeAsync(List<T> model);

        /// <summary>
        /// 根据where条件删除
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        bool Delete(Expression<Func<T, bool>> whereExpression);


        /// <summary>
        /// 根据where条件删除
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        Task<bool> DeleteAsync(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 根据主键删除
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        bool DeleteById(object id);

        /// <summary>
        /// 异步根据主键删除
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        Task<bool> DeleteByIdAsync(object id);

        /// <summary>
        /// 根据主键批量删除
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        bool DeleteByIds(object[] listId);

        /// <summary>
        /// 异步根据主键批量删除
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        Task<bool> DeleteByIdsAsync(object[] listId);

        /// <summary>
        /// 根据主键批量删除
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        bool DeleteByIds(List<object> listId);

        /// <summary>
        /// 异步根据主键批量删除
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        Task<bool> DeleteByIdsAsync(List<object> listId);

        #endregion

        #region Get

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        List<TResult> GetList<TResult>(Expression<Func<T, bool>> whereExpression,
            OrderByType orderByType, Expression<Func<T, object>> orderByExpression,
            Expression<Func<T, TResult>> expression);

        /// <summary>
        /// 查询指定列（升序）
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        List<TResult> GetListOrderByAsc<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderByExpression, Expression<Func<T, TResult>> expression);

        /// <summary>
        /// 查询指定列（降序）
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        List<TResult> GetListOrderByDesc<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderByExpression, Expression<Func<T, TResult>> expression);

        /// <summary>
        /// 查询全部数据，不建议使用
        /// </summary>
        /// <returns></returns>
        List<T> GetList();

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="ids">ID集合</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        List<TResult> GetByIds<TResult>(IEnumerable<object> ids, Expression<Func<T, TResult>> expression);


        /// <summary>
        /// 根据where条件查询全部数据
        /// </summary>
        /// <returns></returns>
        List<T> GetList(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 根据where条件查询全部数据排序
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <returns></returns>
        List<T> GetList(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderByExpression, OrderByType orderByType);

        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <returns></returns>
        List<T> GetListPage(Expression<Func<T, bool>> whereExpression, int intPageIndex, int intPageSize,
            ref int totalCount);

        /// <summary>
        /// 升序分页查询
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序</param>
        /// <returns></returns>
        List<T> GetListPageOrderByAsc(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, Expression<Func<T, object>> orderByExpression, ref int totalCount);

        /// <summary>
        /// 分页查询（自定义排序）
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序</param>
        /// <param name="orderByType">排序枚举</param>
        /// <returns></returns>
        List<T> GetListPageOrderBy(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, Expression<Func<T, object>> orderByExpression, OrderByType orderByType, ref int totalCount);

        /// <summary>
        /// 分页查询（自定义排序）
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序</param>
        /// <param name="orderByType">排序枚举</param>
        /// <returns></returns>
        List<T> GetListPageOrderByNoLock(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, Expression<Func<T, object>> orderByExpression, OrderByType orderByType, ref int totalCount);

        /// <summary>
        /// 多条件分页查询
        /// </summary>
        /// <param name="conditionalList">where条件集合</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <returns></returns>
        List<T> GetListPage(List<IConditionalModel> conditionalList, int intPageIndex, int intPageSize,
            ref int totalCount);

        /// <summary>
        /// 多条件升序分页查询
        /// </summary>
        /// <param name="conditionalList">where条件集合</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序</param>
        /// <returns></returns>
        List<T> GetListPageOrderByAsc(List<IConditionalModel> conditionalList, int intPageIndex,
            int intPageSize, ref int totalCount,
            Expression<Func<T, object>> orderByExpression);

        /// <summary>
        /// 多条件降序分页查询
        /// </summary>
        /// <param name="conditionalList">where条件集合</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序</param>
        /// <returns></returns>
        List<T> GetListPageOrderByDesc(List<IConditionalModel> conditionalList, int intPageIndex,
            int intPageSize, ref int totalCount,
            Expression<Func<T, object>> orderByExpression);

        /// <summary>
        /// 异步查询全部数据，不建议使用
        /// </summary>
        /// <returns></returns>
        Task<List<T>> GetListAsync();

        /// <summary>
        /// 异步查询全部数据
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <returns></returns>
        Task<List<T>> GetListAsync(Expression<Func<T, bool>> whereExpression,
            OrderByType orderByType, Expression<Func<T, object>> orderByExpression);
        
        
        /// <summary>
        /// 根据where条件查询全部数据
        /// </summary>
        /// <returns></returns>
        Task<List<T>> GetListNoLockAsync(Expression<Func<T, bool>> whereExpression);
        
        /// <summary>
        /// 异步查询全部数据
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <returns></returns>
        Task<List<T>> GetListNoLockAsync(Expression<Func<T, bool>> whereExpression,
            OrderByType orderByType, Expression<Func<T, object>> orderByExpression);

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        Task<List<TResult>> GetListAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            OrderByType orderByType, Expression<Func<T, object>> orderByExpression,
            Expression<Func<T, TResult>> expression);

        /// <summary>
        /// 异步根据where条件查询全部数据
        /// </summary>
        /// <returns></returns>
        Task<List<T>> GetListAsync(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 异步查询全部(高并发使用)
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        Task<List<T>> GetListConcurrencyAsync(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 异步根据where条件查询全部数据
        /// </summary>
        /// <param name="strWhere"></param>
        /// <returns></returns>
        Task<List<T>> GetListAsync(string strWhere);

        /// <summary>
        /// 根据主键查询
        /// </summary>
        /// <param name="objId"></param>
        /// <returns></returns>
        T GetById(object objId);

        /// <summary>
        /// 根据主键查询，可从缓存中查，不建议使用
        /// </summary>
        /// <param name="objId"></param>
        /// <param name="blnUseCache"></param>
        /// <returns></returns>
        T GetById(object objId, bool blnUseCache);

        /// <summary>
        /// 根据where条件查询单条，超过一条会报错!!!
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        T GetSingle(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 根据where条件查询单条，多个返回第一条
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderExpression">orderBy表达式</param>
        /// <param name="orderByType">排序类型</param>
        /// <returns></returns>
        T GetFirst(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderExpression, OrderByType orderByType);

        /// <summary>
        /// 根据where条件查询单条，多个返回第一条
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderExpression">orderBy表达式</param>
        /// <param name="orderByType">排序类型</param>
        /// <returns></returns>
        Task<T> GetFirstAsync(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderExpression, OrderByType orderByType);

        /// <summary>
        /// 根据where条件查询单条，多个返回第一条
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        Task<T> GetFirstAsync(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 根据where条件查询单条，超过一条会报错!!!
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        Task<T> GetSingleAsync(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 异步根据主键查询
        /// </summary>
        /// <param name="objId"></param>
        /// <returns></returns>
        Task<T> GetByIdAsync(object objId);

        /// <summary>
        /// 根据主键查询，可从缓存中查，不建议使用
        /// </summary>
        /// <param name="objId"></param>
        /// <param name="blnUseCache"></param>
        /// <returns></returns>
        Task<T> GetByIdAsync(object objId, bool blnUseCache);

        /// <summary>
        /// 根据主键集合查询
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        List<T> GetListByIds(IEnumerable<object> listId);

        /// <summary>
        /// 异步根据主键集合查询
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        Task<List<T>> GetListByIdsAsync(List<long> listId);

        #endregion

        #region Update

        /// <summary>
        /// 根据实体更新（主键要有值，主键是更新条件）
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        bool Update(T model);

        bool UpdateSplitTable(T model);

        /// <summary>
        /// 批量更新
        /// </summary>
        /// <param name="updateObjs">Db模型集合</param>
        /// <returns></returns>
        bool UpdateRangeSplitTable(IEnumerable<T> updateObjs);


        /// <summary>
        /// 异步根据实体更新（主键要有值，主键是更新条件）
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        Task<bool> UpdateAsync(T model);

        /// <summary>
        /// 根据表达式中的列更新
        /// </summary>
        /// <param name="columns">指定列表达式</param>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        bool Update(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 异步根据表达式中的列更新
        /// </summary>
        /// <param name="columns">指定列表达式</param>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        Task<bool> UpdateAsync(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 根据实体更新（主键要有值，主键是更新条件）
        /// </summary>
        /// <param name="updateObjs">Db模型集合</param>
        /// <returns></returns>
        bool UpdateRange(IEnumerable<T> updateObjs);

        /// <summary>
        /// 异步根据实体更新（主键要有值，主键是更新条件）
        /// </summary>
        /// <param name="updateObjs">Db模型集合</param>
        /// <returns></returns>
        Task<int> UpdateRangeAsync(IEnumerable<T> updateObjs);

        /// <summary>
        /// 更新，返回影响行数
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        int UpdateReturnCommand(T model);

        /// <summary>
        /// 批量更新，返回影响行数
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        int UpdateRangeReturnCommand(IEnumerable<T> model);

        /// <summary>
        /// 更新，返回影响行数
        /// </summary>
        /// <param name="columns">指定列</param>
        /// <param name="whereExpression">where条件</param>
        /// <returns></returns>
        int UpdateRangeReturnCommand(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 更新指定列
        /// </summary>
        /// <param name="model"></param>
        /// <param name="columns"></param>
        /// <returns></returns>
        bool Update(T model, Expression<Func<T, T>> columns);

        /// <summary>
        /// 批量更新指定列
        /// </summary>
        /// <param name="updateObjs"></param>
        /// <param name="columns"></param>+
        /// 
        /// <returns></returns>
        bool UpdateRange(IEnumerable<T> updateObjs, Expression<Func<T, T>> columns);

        /// <summary>
        /// 更新指定列
        /// </summary>
        /// <param name="model"></param>
        /// <param name="columns"></param>
        /// <returns></returns>
        int UpdateReturnCommand(T model, Expression<Func<T, T>> columns);

        /// <summary>
        /// 批量更新指定列
        /// </summary>
        /// <param name="model"></param>
        /// <param name="columns"></param>
        /// <returns></returns>
        int UpdateRangeReturnCommand(IEnumerable<T> model, Expression<Func<T, T>> columns);

        /// <summary>
        /// 大数据批量修改
        /// </summary>
        int BulkUpdate(List<T> entity);

        #endregion

        #region 分页查询

        /// <summary>
        /// 列表查询
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="startTime">分表查询开始时间</param>
        /// <param name="endTime">分表查询结束时间</param>
        /// <returns></returns>
        List<T> GetList_SplitTable(Expression<Func<T, bool>> whereExpression, DateTime startTime, DateTime endTime
        );

        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="startTime">分表查询开始时间</param>
        /// <param name="endTime">分表查询结束时间</param>
        /// <returns></returns>
        List<T> GetListPage_SplitTable(Expression<Func<T, bool>> whereExpression, DateTime startTime, DateTime endTime, int intPageIndex, int intPageSize,
            ref int totalCount);


        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <returns></returns>
        List<T> GetListPage(Expression<Func<T, bool>> whereExpression, int intPageIndex, int intPageSize,
            ref int totalCount, OrderByType orderByType,
            Expression<Func<T, object>> orderByExpression);

        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="conditionalList">where表达式集合</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <returns></returns>
        List<T> GetListPage(List<IConditionalModel> conditionalList, int intPageIndex, int intPageSize,
            ref int totalCount, OrderByType orderByType,
            Expression<Func<T, object>> orderByExpression);

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        List<TResult> GetList<TResult>(Expression<Func<T, bool>> whereExpression, Expression<Func<T, TResult>> expression);

        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="selectexpression">指定列</param>
        /// <returns></returns>
        List<TResult> GetListPage<TResult>(Expression<Func<T, bool>> whereExpression, int intPageIndex, int intPageSize, ref int totalCount,
            Expression<Func<T, TResult>> selectexpression);

        /// <summary>
        /// 分页查询返回指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="selectexpression">指定列</param>
        /// <returns></returns>
        List<TResult> GetListPage<TResult>(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, ref int totalCount, OrderByType orderByType,
            Expression<Func<T, object>>? orderByExpression,
            Expression<Func<T, TResult>> selectexpression);

        /// <summary>
        /// 分页查询指定列
        /// </summary>
        /// <param name="conditionalList">where表达式集合</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="selectexpression">指定列</param>
        /// <returns></returns>
        List<TResult> GetListPage<TResult>(List<IConditionalModel> conditionalList, int intPageIndex,
            int intPageSize, ref int totalCount, OrderByType orderByType,
            Expression<Func<T, object>>? orderByExpression,
            Expression<Func<T, TResult>> selectexpression);

        /// <summary>
        /// 分页倒序查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="selectexpression">指定列</param>
        /// <returns></returns>
        List<TResult> GetListPageOrderByAsc<TResult>(Expression<Func<T, bool>> whereExpression,
            int intPageIndex, int intPageSize, ref int totalCount, Expression<Func<T, object>>? orderByExpression,
            Expression<Func<T, TResult>> selectexpression);

        /// <summary>
        /// 分页正序查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="selectexpression">指定列</param>
        /// <returns></returns>
        List<TResult> GetListPageOrderByDesc<TResult>(Expression<Func<T, bool>> whereExpression,
            int intPageIndex, int intPageSize, ref int totalCount, Expression<Func<T, object>>? orderByExpression,
            Expression<Func<T, TResult>> selectexpression);

        #endregion

        #region 是否存在

        /// <summary>
        /// 是否存在
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        bool IsAny(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 异步是否存在
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        Task<bool> IsAnyAsync(Expression<Func<T, bool>> whereExpression);

        #endregion

        #region 其他

        /// <summary>
        /// 合计
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        int Count(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 异步合计
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        Task<int> CountAsync(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 求和
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="sumExpression">指定列求和</param>
        /// <returns></returns>
        TResult Sum<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> sumExpression);

        /// <summary>
        /// 异步求和
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="sumExpression">指定列求和</param>
        /// <returns></returns>
        Task<TResult> SumAsync<TResult>(Expression<Func<T, bool>> whereExpression, Expression<Func<T, TResult>> sumExpression);

        /// <summary>
        /// 最大
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="maxExpression">指定列最大值</param>
        /// <returns></returns>
        TResult Max<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> maxExpression);

        /// <summary>
        /// 异步最大
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="maxExpression">指定列最大值</param>
        /// <returns></returns>
        Task<TResult> MaxAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> maxExpression);

        /// <summary>
        /// 最小
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="minExpression">指定列最小值</param>
        /// <returns></returns>
        TResult Min<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> minExpression);

        /// <summary>
        /// 异步最小
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="minExpression">指定列最小值</param>
        /// <returns></returns>
        Task<TResult> MinAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> minExpression);

        /// <summary>
        /// 平均
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="avgExpression">指定列平均</param>
        /// <returns></returns>
        TResult Avg<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> avgExpression);

        /// <summary>
        /// 平均
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="avgExpression">指定列平均</param>
        /// <returns></returns>
        Task<TResult> AvgAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> avgExpression);

        /// <summary>
        /// 根据条件加载树形结构
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="childListExpression">子节点结构</param>
        /// <param name="parentIdExpression">父节点字段</param>
        /// <param name="rootValue">父节点条件</param>
        /// <returns></returns>
        Task<List<T>> GetListTreeAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, IEnumerable<object>>> childListExpression,
            Expression<Func<T, object>> parentIdExpression,
            object rootValue);

        /// <summary>
        /// 根据条件加载树形结构
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="childListExpression">子节点结构</param>
        /// <param name="parentIdExpression">父节点字段</param>
        /// <param name="rootValue">父节点条件</param>
        /// <param name="orderExpression">排序条件</param>
        /// <param name="orderByType">排序类型</param>
        /// <returns></returns>
        Task<List<T>> GetListTreeAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, IEnumerable<object>>> childListExpression,
            Expression<Func<T, object>> parentIdExpression, Expression<Func<T, object>> orderExpression, OrderByType orderByType,
            object rootValue);

        /// <summary>
        /// 加载树形结构
        /// </summary>
        /// <param name="childListExpression">子节点结构</param>
        /// <param name="parentIdExpression">父节点字段</param>
        /// <param name="rootValue">父节点条件</param>
        /// <returns></returns>
        Task<List<T>> GetListTreeAsync(Expression<Func<T, IEnumerable<object>>> childListExpression,
            Expression<Func<T, object>> parentIdExpression,
            object rootValue);

        /// <summary>
        /// 根据父节点ID获取所有子节点列表
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="parentIdExpression"></param>
        /// <param name="primaryKeyValue"></param>
        /// <param name="isContainOneself">是否包含自己</param>
        /// <returns></returns>
        Task<List<T>> GetListChildAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, object>> parentIdExpression, object primaryKeyValue, bool isContainOneself = true);

        /// <summary>
        /// 根据父节点ID获取所有子节点列表
        /// </summary>
        /// <param name="parentIdExpression">父节点字段</param>
        /// <param name="primaryKeyValue">父节点</param>
        /// <returns></returns>
        Task<List<T>> GetListChildAsync(Expression<Func<T, object>> parentIdExpression, object primaryKeyValue);

        /// <summary>
        /// 根据父子点ID获取所有父节点列表
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="parentIdExpression"></param>
        /// <param name="primaryKeyValue"></param>
        /// <returns></returns>
        Task<List<T>> GetParentListAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, object>> parentIdExpression, object primaryKeyValue);

        /// <summary>
        /// 根据父子点ID获取所有父节点列表
        /// </summary>
        /// <param name="parentIdExpression"></param>
        /// <param name="primaryKeyValue"></param>
        /// <returns></returns>
        Task<List<T>> GetParentListAsync(Expression<Func<T, object>> parentIdExpression, object primaryKeyValue);

        #endregion

        #region 联表查询

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <param name="resultExpression">结果表达式，用于定义从连接和筛选后的结果中提取的具体结果字段。</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
        Task<List<TResult>> GetList<T1, T2, TResult>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression,
            Expression<Func<T1, T2, TResult>> resultExpression);

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <param name="resultExpression">结果表达式，用于定义从连接和筛选后的结果中提取的具体结果字段。</param>
        /// <param name="orderByType">排序</param>
        /// <param name="orderByExpression">排序字段</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
        Task<List<TResult>> GetList<T1, T2, TResult>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression,
            Expression<Func<T1, T2, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, object>> orderByExpression);

        /// <summary>
        /// 联表分页查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <param name="resultExpression">结果表达式，用于定义从连接和筛选后的结果中提取的具体结果字段。</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="pageIndex">页码</param>
        /// <param name="pageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
        List<TResult> GetListPage<T1, T2, TResult>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression,
            Expression<Func<T1, T2, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, object>> orderByExpression,
            int pageIndex, int pageSize, ref int totalCount);

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="resultExpression">结果表达式，用于映射查询结果。</param>
        /// <returns>一个任务，完成时返回符合条件的结果列表。</returns>
        Task<List<TResult>> GetList<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression,
            Expression<Func<T1, T2, T3, TResult>> resultExpression);

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="resultExpression">结果表达式，用于映射查询结果。</param>
        /// <param name="orderByType">排序方式。</param>
        /// <param name="orderByExpression">排序表达式，用于定义排序规则。</param>
        /// <returns>一个任务，完成时返回符合条件并按指定排序条件排序的结果列表。</returns>
        Task<List<TResult>> GetList<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression,
            Expression<Func<T1, T2, T3, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, object>> orderByExpression);

        /// <summary>
        /// 联表分页查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="resultExpression">结果表达式，用于映射查询结果。</param>
        /// <param name="orderByType">排序方式。</param>
        /// <param name="orderByExpression">排序表达式，用于定义排序规则。</param>
        /// <param name="pageIndex">页码索引。</param>
        /// <param name="pageSize">页大小。</param>
        /// <param name="totalCount">总记录数的引用，用于返回查询到的总记录数。</param>
        /// <returns>符合条件的分页结果列表。</returns>
        List<TResult> GetListPage<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression,
            Expression<Func<T1, T2, T3, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, object>> orderByExpression,
            int pageIndex, int pageSize, ref int totalCount);

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="T4">第四个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="resultExpression">结果表达式，用于映射查询结果。</param>
        /// <returns>一个任务，完成时返回符合条件的结果列表。</returns>
        Task<List<TResult>> GetList<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression,
            Expression<Func<T1, T2, T3, T4, TResult>> resultExpression);


        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="T4">第四个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="resultExpression">结果表达式，用于映射查询结果。</param>
        /// <param name="orderByType">排序方式。</param>
        /// <param name="orderByExpression">排序表达式，用于定义排序规则。</param>
        /// <returns>一个任务，完成时返回符合条件并按指定排序条件排序的结果列表。</returns>
        Task<List<TResult>> GetList<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression,
            Expression<Func<T1, T2, T3, T4, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, T4, object>> orderByExpression);

        /// <summary>
        /// 联表分页查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="T4">第四个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="resultExpression">结果表达式，用于映射查询结果。</param>
        /// <param name="orderByType">排序方式。</param>
        /// <param name="orderByExpression">排序表达式，用于定义排序规则。</param>
        /// <param name="pageIndex">页码索引。</param>
        /// <param name="pageSize">页大小。</param>
        /// <param name="totalCount">总记录数的引用，用于返回查询到的总记录数。</param>
        /// <returns>符合条件的分页结果列表。</returns>
        List<TResult> GetListPage<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression,
            Expression<Func<T1, T2, T3, T4, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, T4, object>> orderByExpression,
            int pageIndex, int pageSize, ref int totalCount);

        /// <summary>
        /// 联表分页查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <param name="resultExpression">结果表达式，用于定义从连接和筛选后的结果中提取的具体结果字段。</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="pageIndex">页码</param>
        /// <param name="pageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
        List<TResult> GetListPageNoLock<T1, T2, TResult>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression,
            Expression<Func<T1, T2, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, object>> orderByExpression,
            int pageIndex, int pageSize, ref int totalCount);


        /// <summary>
        /// 联表查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
       Task<int> GetList<T1, T2>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression);

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <param name="orderByType">排序</param>
        /// <param name="orderByExpression">排序字段</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
        Task<int> GetList<T1, T2>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T1, T2, object>> orderByExpression);
        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <returns>一个任务，完成时返回符合条件的结果列表。</returns>
        Task<int> GetList<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression);

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="orderByType">排序方式。</param>
        /// <param name="orderByExpression">排序表达式，用于定义排序规则。</param>
        /// <returns>一个任务，完成时返回符合条件并按指定排序条件排序的结果列表。</returns>
        Task<int> GetList<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, object>> orderByExpression);

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="T4">第四个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <returns>一个任务，完成时返回符合条件的结果列表。</returns>
        Task<int> GetList<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression);


        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="T4">第四个泛型类型参数。</typeparam>
        /// <typeparam name="TResult">查询结果的泛型类型。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="orderByType">排序方式。</param>
        /// <param name="orderByExpression">排序表达式，用于定义排序规则。</param>
        /// <returns>一个任务，完成时返回符合条件并按指定排序条件排序的结果列表。</returns>
        Task<int> GetList<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, T4, object>> orderByExpression);

        #endregion

        /// <summary>
        /// 异步根据主键集合查询
        /// </summary>
        /// <param name="whereExpression"></param>
        /// <returns></returns>
        Task<List<T>> GetListByWhereAsync(Expression<Func<T, bool>> whereExpression);

        /// <summary>
        /// 异步执行sql语句查询
        /// </summary>
        /// <param name="sql"></param>
        /// <returns></returns>
        Task<List<T>> GetListBySqlAsync(string sql);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sql"></param>
        /// <returns></returns>
        List<T> GetListBySql(string sql);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sql"></param>
        /// <returns></returns>
        Task<DataTable> GetDataTableBySqlAsync(string sql);

        /// <summary>
        /// 切换数据库
        /// </summary>
        void ChangeDataBase(string baseKey);

        /// <summary>
        /// 有则更新无则插入
        /// </summary>
        /// <param name="model"></param>
        /// <returns>返回插入数量和更新属性</returns>
        int AddOrUpdate(T model);

        /// <summary>
        /// 有则更新无则插入
        /// </summary>
        /// <param name="model"></param>
        /// <returns>返回插入数量和更新属性</returns>
        int AddOrUpdate(List<T> model);

        /// <summary>
        /// 批量插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        int BulkCopy(List<T> t);

        /// <summary>
        /// 批量有则更新无则插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        int BulkMerge(List<T> t);

        /// <summary>
        /// 批量插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        Task<int> BulkCopyAsync(List<T> t);

        /// <summary>
        /// 批量更新
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        Task<int> BulkUpdateAsync(List<T> t);

        /// <summary>
        /// 批量有则更新无则插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        Task<int> BulkMergeAsync(List<T> t);
        
    }

    /// <summary>
    /// 业务抽象
    /// </summary>
    public interface IBaseService
    {
    }
}