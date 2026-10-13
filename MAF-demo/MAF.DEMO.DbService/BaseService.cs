using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using SqlSugar;
using MAF.DEMO.Core;
using MAF.DEMO.Utility;
using MAF.DEMO.IDbService;
using System.Data;

namespace MAF.DEMO.DbService
{
    /// <summary>
    /// 抽象基类实现
    /// </summary>
    public abstract class AbstractBaseService<T> : DbContext, IBaseService<T> where T : class, new()
    {
        /// <summary>
        /// 构造函数
        /// </summary>
        protected AbstractBaseService()
        {
        }

        #region Insert

        /// <summary>
        /// 插入(NUll不插入值)，返回是否成功
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual bool InsertSplitTable(T model)
        {
            return Db.Insertable<T>(model).SplitTable().ExecuteCommand() > 0;
        }

        /// <summary>
        /// 插入(NUll不插入值)，返回是否成功
        /// </summary>
        /// <param name="models"></param>
        /// <returns></returns>
        public virtual bool InsertRangeSplitTable(IEnumerable<T> models)
        {
            return Db.Insertable<T>(models).SplitTable().ExecuteCommand() > 0;
        }

        public virtual bool DeleteSplitTable(T model)
        {
            //var tableName = Db.SplitHelper<SplitTestTable>().GetTableName(data.CreateTime);//根据时间获取表名
            return Db.Deleteable<T>(model).SplitTable().ExecuteCommand() > 0; //,SplitTable不能少
        }

        public virtual bool DeleteRangeSplitTable(IEnumerable<T> models)
        {
            //var tableName = Db.SplitHelper<SplitTestTable>().GetTableName(data.CreateTime);//根据时间获取表名
            return Db.Deleteable<T>(models).SplitTable().ExecuteCommand() > 0; //,SplitTable不能少
        }


        /// <summary>
        /// 插入(NUll不插入值)，返回是否成功
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual bool Insert(T model)
        {
            return GetDbSet<T>().InsertWithOutNull(model);
        }

        /// <summary>
        /// 同步插入，返回雪花Id
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        public virtual long InsertSnowflakeId(T t)
        {
            return GetDbSet<T>().InsertReturnSnowflakeId(t);
        }

        /// <summary>
        /// 插入，返回自增id
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual List<long> InsertSnowflakeId(List<T> model)
        {
            return GetDbSet<T>().InsertReturnSnowflakeId(model);
        }

        /// <summary>
        /// 异步插入，返回是否成功
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual async Task<bool> InsertAsync(T model)
        {
            return await GetDbSet<T>().InsertAsync(model);
        }

        /// <summary>
        /// 异步插入，返回雪花Id
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        public virtual async Task<long> InsertSnowflakeIdAsync(T t)
        {
            return await GetDbSet<T>().InsertReturnSnowflakeIdAsync(t);
        }

        /// <summary>
        /// 异步插入，返回自增id
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual async Task<List<long>> InsertSnowflakeIdAsync(List<T> model)
        {
            return await GetDbSet<T>().InsertReturnSnowflakeIdAsync(model);
        }

        /// <summary>
        /// 异步批量插入，返回是否成功
        /// </summary>
        /// <param name="dbModels">主键集合</param>
        /// <returns></returns>
        public virtual bool InsertRange(T[] dbModels)
        {
            return GetDbSet<T>().InsertRangeWithOutNull(dbModels);
        }

        /// <summary>
        /// 异步批量插入，返回是否成功
        /// </summary>
        /// <param name="dbModels">Db模型集合</param>
        /// <returns></returns>
        public virtual async Task<bool> InsertRangeAsync(T[] dbModels)
        {
            return await GetDbSet<T>().InsertAsync(dbModels);
        }

        /// <summary>
        /// 异步批量插入，返回是否成功
        /// </summary>
        /// <param name="lisT">Db模型集合</param>
        /// <returns></returns>
        public virtual bool InsertRange(List<T> lisT)
        {
            return GetDbSet<T>().InsertRangeWithOutNull(lisT);
        }

        /// <summary>
        /// 异步批量插入，返回是否成功
        /// </summary>
        /// <param name="lisT">Db模型集合</param>
        /// <returns></returns>
        public virtual async Task<bool> InsertRangeAsync(List<T> lisT)
        {
            return await GetDbSet<T>().InsertAsync(lisT);
        }

        /// <summary>
        /// 插入，返回自增id
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual int InsertReturnIdentity(T model)
        {
            return GetDbSet<T>().InsertWithOutNullReturnIdentity(model);
        }

        /// <summary>
        /// 异步插入，返回自增id
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual async Task<int> InsertReturnIdentityAsync(T model)
        {
            return await GetDbSet<T>().InsertReturnIdentityAsync(model);
        }

        /// <summary>
        /// 插入，返回model
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual T InsertReturnEntity(T model)
        {
            return GetDbSet<T>().InsertReturnEntity(model);
        }

        /// <summary>
        /// 异步插入，返回model
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual async Task<T> InsertReturnEntityAsync(T model)
        {
            return await GetDbSet<T>().InsertReturnEntityAsync(model);
        }

        /// <summary>
        /// 插入，返回影响行数
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual int InsertReturnCommand(T model)
        {
            return GetDbSet<T>().InsertWithOutNullReturnCommand(model);
        }

        /// <summary>
        /// 批量插入，返回影响行数
        /// </summary>
        /// <param name="models"></param>
        /// <returns></returns>
        public virtual int InsertRangeReturnCommand(IEnumerable<T> models)
        {
            return GetDbSet<T>().InsertRangeWithOutNullReturnCommand(models);
        }

        #endregion

        #region Delete

        /// <summary>
        /// 删除
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual bool Delete(T model)
        {
            return GetDbSet<T>().Delete(model);
        }

        /// <summary>
        /// 异步删除
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual async Task<bool> DeleteAsync(T model)
        {
            return await GetDbSet<T>().DeleteAsync(model);
        }

        /// <summary>
        /// 异步批量删除
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual async Task<bool> DeleteRangeAsync(List<T> model)
        {
            return await GetDbSet<T>().DeleteAsync(model);
        }

        /// <summary>
        /// 根据where条件删除
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual bool Delete(Expression<Func<T, bool>> whereExpression)
        {
            return GetDbSet<T>().Delete(whereExpression);
        }


        /// <summary>
        /// 根据where条件删除
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual async Task<bool> DeleteAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().DeleteAsync(whereExpression);
        }

        /// <summary>
        /// 根据主键删除
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public virtual bool DeleteById(object id)
        {
            return GetDbSet<T>().DeleteById(id);
        }

        /// <summary>
        /// 异步根据主键删除
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public virtual async Task<bool> DeleteByIdAsync(object id)
        {
            return await GetDbSet<T>().DeleteByIdAsync(id);
        }

        /// <summary>
        /// 根据主键批量删除
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        public virtual bool DeleteByIds(object[] listId)
        {
            return GetDbSet<T>().DeleteByIds(listId);
        }

        /// <summary>
        /// 异步根据主键批量删除
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        public virtual async Task<bool> DeleteByIdsAsync(object[] listId)
        {
            return await GetDbSet<T>().DeleteByIdsAsync(listId);
        }

        /// <summary>
        /// 根据主键批量删除
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        public virtual bool DeleteByIds(List<object> listId)
        {
            return GetDbSet<T>().DeleteByIds(listId);
        }

        /// <summary>
        /// 异步根据主键批量删除
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        public virtual async Task<bool> DeleteByIdsAsync(List<object> listId)
        {
            return await GetDbSet<T>().DeleteByIdsAsync(listId);
        }

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
        public virtual List<TResult> GetList<TResult>(Expression<Func<T, bool>> whereExpression,
            OrderByType orderByType, Expression<Func<T, object>> orderByExpression,
            Expression<Func<T, TResult>> expression)
        {
            return GetDbSet<T>().GetList(whereExpression, orderByType, orderByExpression, expression);
        }

        /// <summary>
        /// 异步查询全部数据
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListAsync(Expression<Func<T, bool>> whereExpression,
            OrderByType orderByType, Expression<Func<T, object>> orderByExpression)
        {
            return await GetDbSet<T>().GetListAsync(whereExpression, orderByType, orderByExpression);
        }

        /// <summary>
        /// 异步根据where条件查询全部数据
        /// </summary>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListNoLockAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().GetListNoLockAsync(whereExpression);
        }

        /// <summary>
        /// 异步查询全部数据
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListNoLockAsync(Expression<Func<T, bool>> whereExpression,
            OrderByType orderByType, Expression<Func<T, object>> orderByExpression)
        {
            return await GetDbSet<T>().GetListNoLockAsync(whereExpression, orderByType, orderByExpression);
        }

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public virtual async Task<List<TResult>> GetListAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            OrderByType orderByType, Expression<Func<T, object>> orderByExpression,
            Expression<Func<T, TResult>> expression)
        {
            return await GetDbSet<T>().GetListAsync(whereExpression, orderByType, orderByExpression, expression);
        }

        /// <summary>
        /// 查询指定列（升序）
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public virtual List<TResult> GetListOrderByAsc<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderByExpression, Expression<Func<T, TResult>> expression)
        {
            return GetDbSet<T>().GetList(whereExpression, OrderByType.Asc, orderByExpression, expression);
        }

        /// <summary>
        /// 查询指定列（降序）
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public virtual List<TResult> GetListOrderByDesc<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderByExpression, Expression<Func<T, TResult>> expression)
        {
            return GetDbSet<T>().GetList(whereExpression, OrderByType.Desc, orderByExpression, expression);
        }

        /// <summary>
        /// 查询全部数据，不建议使用
        /// </summary>
        /// <returns></returns>
        public virtual List<T> GetList()
        {
            return GetDbSet<T>().GetList();
        }

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="ids">ID集合</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public virtual List<TResult> GetByIds<TResult>(IEnumerable<object> ids, Expression<Func<T, TResult>> expression)
        {
            return GetDbSet<T>().GetByIds(ids, expression);
        }


        /// <summary>
        /// 根据where条件查询全部数据
        /// </summary>
        /// <returns></returns>
        public virtual List<T> GetList(Expression<Func<T, bool>> whereExpression)
        {
            return GetDbSet<T>().GetList(whereExpression);
        }

        /// <summary>
        /// 异步查询全部(高并发使用)
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListConcurrencyAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().GetListConcurrencyAsync(whereExpression);
        }

        /// <summary>
        /// 根据where条件查询全部数据排序
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <returns></returns>
        public virtual List<T> GetList(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderByExpression, OrderByType orderByType)
        {
            return GetDbSet<T>().GetList(whereExpression, orderByExpression, orderByType);
        }

        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <returns></returns>
        public virtual List<T> GetListPage(Expression<Func<T, bool>> whereExpression, int intPageIndex, int intPageSize,
            ref int totalCount)
        {
            PageModel pageModel = new PageModel {PageIndex = intPageIndex, PageSize = intPageSize, TotalCount = totalCount};

            List<T> result = GetDbSet<T>().GetPageList(whereExpression, pageModel);

            totalCount = pageModel.TotalCount;
            return result;
        }

        /// <summary>
        /// 升序分页查询
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序</param>
        /// <returns></returns>
        public virtual List<T> GetListPageOrderByAsc(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, Expression<Func<T, object>> orderByExpression, ref int totalCount)
        {
            PageModel pageModel = new PageModel {PageIndex = intPageIndex, PageSize = intPageSize, TotalCount = totalCount};

            List<T> result = GetDbSet<T>().GetPageList(whereExpression, pageModel,
                orderByExpression);

            totalCount = pageModel.TotalCount;
            return result;
        }

        /// <summary>
        /// 分页查询（自定义排序）
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序</param>
        /// <param name="totalCount">总数</param>
        /// <returns></returns>
        public virtual List<T> GetListPageOrderBy(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, Expression<Func<T, object>> orderByExpression, OrderByType orderByType, ref int totalCount)
        {
            return GetDbSet<T>().GetListPage(whereExpression, intPageIndex, intPageSize, orderByType, orderByExpression, ref totalCount);
        }

        /// <summary>
        /// 分页查询（自定义排序）
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序</param>
        /// <param name="totalCount">总数</param>
        /// <returns></returns>
        public virtual List<T> GetListPageOrderByNoLock(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, Expression<Func<T, object>> orderByExpression, OrderByType orderByType, ref int totalCount)
        {
            return GetDbSet<T>().GetListPageNoLock(whereExpression, intPageIndex, intPageSize, orderByType, orderByExpression, ref totalCount);
        }


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
        public virtual async Task<List<T>> GetListPageOrderByAsync(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, int totalCount,
            Expression<Func<T, object>> orderByExpression, OrderByType orderByType)
        {
            //PageModel pageModel = new PageModel {PageIndex = intPageIndex, PageSize = intPageSize, TotalCount = totalCount};

            List<T> result = await GetDbSet<T>().GetListPageAsync(whereExpression, intPageIndex, intPageSize, orderByType, orderByExpression, totalCount);
            return result;
        }

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
        public virtual List<T> GetListPageOrderByNoLockAsync(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, ref int totalCount,
            Expression<Func<T, object>> orderByExpression, OrderByType orderByType)
        {
            List<T> result = GetDbSet<T>().GetListPageNoLock(whereExpression, intPageIndex, intPageSize, orderByType, orderByExpression, ref totalCount);
            return result;
        }


        /// <summary>
        /// 多条件分页查询
        /// </summary>
        /// <param name="conditionalList">where条件集合</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <returns></returns>
        public virtual List<T> GetListPage(List<IConditionalModel> conditionalList, int intPageIndex, int intPageSize,
            ref int totalCount)
        {
            PageModel pageModel = new PageModel {PageIndex = intPageIndex, PageSize = intPageSize, TotalCount = totalCount};

            List<T> result = GetDbSet<T>().GetPageList(conditionalList, pageModel);

            totalCount = pageModel.TotalCount;
            return result;
        }

        /// <summary>
        /// 多条件升序分页查询
        /// </summary>
        /// <param name="conditionalList">where条件集合</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序</param>
        /// <returns></returns>
        public virtual List<T> GetListPageOrderByAsc(List<IConditionalModel> conditionalList, int intPageIndex,
            int intPageSize, ref int totalCount,
            Expression<Func<T, object>> orderByExpression)
        {
            PageModel pageModel = new PageModel {PageIndex = intPageIndex, PageSize = intPageSize, TotalCount = totalCount};

            List<T> result = GetDbSet<T>().GetPageList(conditionalList, pageModel,
                orderByExpression);

            totalCount = pageModel.TotalCount;
            return result;
        }

        /// <summary>
        /// 多条件降序分页查询
        /// </summary>
        /// <param name="conditionalList">where条件集合</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序</param>
        /// <returns></returns>
        public virtual List<T> GetListPageOrderByDesc(List<IConditionalModel> conditionalList, int intPageIndex,
            int intPageSize, ref int totalCount,
            Expression<Func<T, object>> orderByExpression)
        {
            PageModel pageModel = new PageModel {PageIndex = intPageIndex, PageSize = intPageSize, TotalCount = totalCount};

            List<T> result = GetDbSet<T>().GetPageList(conditionalList, pageModel,
                orderByExpression, OrderByType.Desc);

            totalCount = pageModel.TotalCount;
            return result;
        }

        /// <summary>
        /// 异步查询全部数据，不建议使用
        /// </summary>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListAsync()
        {
            return await GetDbSet<T>().GetListAsync();
        }

        /// <summary>
        /// 异步根据where条件查询全部数据
        /// </summary>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().GetListAsync(whereExpression);
        }

        /// <summary>
        /// 异步根据where条件查询全部数据
        /// </summary>
        /// <param name="strWhere"></param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListAsync(string strWhere)
        {
            return await GetDbSet<T>().GetListAsync(strWhere);
        }

        /// <summary>
        /// 根据主键查询
        /// </summary>
        /// <param name="objId"></param>
        /// <returns></returns>
        public virtual T GetById(object objId)
        {
            return GetDbSet<T>().GetById(objId);
        }

        /// <summary>
        /// 根据主键查询，可从缓存中查，不建议使用
        /// </summary>
        /// <param name="objId"></param>
        /// <param name="blnUseCache"></param>
        /// <returns></returns>
        public virtual T GetById(object objId, bool blnUseCache)
        {
            return GetDbSet<T>().GetById(objId, blnUseCache);
        }

        /// <summary>
        /// 根据where条件查询单条，超过一条会报错!!!
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual T GetSingle(Expression<Func<T, bool>> whereExpression)
        {
            return GetDbSet<T>().GetSingle(whereExpression);
        }

        /// <summary>
        /// 根据where条件查询单条，多个返回第一条
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderExpression">orderBy表达式</param>
        /// <param name="orderByType">排序类型</param>
        /// <returns></returns>
        public virtual T GetFirst(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderExpression, OrderByType orderByType)
        {
            return GetDbSet<T>().GetFirst(whereExpression, orderExpression, orderByType);
        }

        /// <summary>
        /// 根据where条件查询单条，多个返回第一条
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderExpression">orderBy表达式</param>
        /// <param name="orderByType">排序类型</param>
        /// <returns></returns>
        public virtual async Task<T> GetFirstAsync(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderExpression, OrderByType orderByType)
        {
            return await Db.Queryable<T>().OrderBy(orderExpression, orderByType).Where(whereExpression).FirstAsync();
        }

        /// <summary>
        /// 根据where条件查询单条，多个返回第一条
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual async Task<T> GetFirstAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().GetFirstAsync(whereExpression);
        }

        /// <summary>
        /// 根据where条件查询单条，超过一条会报错!!!
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual async Task<T> GetSingleAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().GetSingleAsync(whereExpression);
        }

        /// <summary>
        /// 异步根据主键查询
        /// </summary>
        /// <param name="objId"></param>
        /// <returns></returns>
        public virtual async Task<T> GetByIdAsync(object objId)
        {
            return await GetDbSet<T>().GetByIdAsync(objId);
        }

        /// <summary>
        /// 根据主键查询，可从缓存中查，不建议使用
        /// </summary>
        /// <param name="objId"></param>
        /// <param name="blnUseCache"></param>
        /// <returns></returns>
        public virtual async Task<T> GetByIdAsync(object objId, bool blnUseCache)
        {
            return await GetDbSet<T>().GetByIdAsync(objId, blnUseCache);
        }

        /// <summary>
        /// 根据主键集合查询
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        public virtual List<T> GetListByIds(IEnumerable<object> listId)
        {
            return GetDbSet<T>().GetByIds(listId);
        }

        /// <summary>
        /// 异步根据主键集合查询
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListByIdsAsync(List<long> listId)
        {
            return await GetDbSet<T>().GetByIdsAsync(listId);
        }
        
        /// <summary>
        /// 异步根据主键集合查询
        /// </summary>
        /// <param name="listId"></param>
        /// <returns></returns>
        public async Task<List<T>> GetListByIdsAsync(IList<long> listId)
        {
            return await GetDbSet<T>().GetByIdsAsync(listId);
        }
        
        /// <summary>
        /// 异步根据主键集合查询
        /// </summary>
        /// <param name="whereExpression"></param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListByWhereAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().AsQueryable().Where(whereExpression).ToListAsync();
        }

        /// <summary>
        /// 执行sql语句查询
        /// </summary>
        /// <param name="sql"></param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListBySqlAsync(string sql)
        {
            return await Db.Ado.SqlQueryAsync<T>(sql);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sql"></param>
        /// <returns></returns>
        public virtual List<T> GetListBySql(string sql)
        {
            return Db.Ado.SqlQuery<T>(sql);
        }

        /// <summary>
        /// 执行sql语句查询
        /// </summary>
        /// <param name="sql"></param>
        /// <returns></returns>
        public virtual async Task<DataTable> GetDataTableBySqlAsync(string sql)
        {
            return await Db.Ado.GetDataTableAsync(sql);
        }

        #endregion

        #region Update

        /// <summary>
        /// 根据实体更新（主键要有值，主键是更新条件）
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual bool Update(T model)
        {
            return GetDbSet<T>().UpdateWithOutNull(model);
        }

        public virtual bool UpdateSplitTable(T model)
        {
            return Db.Updateable(model).IgnoreColumns(ignoreAllNullColumns: true).SplitTable().ExecuteCommand() > 0;
        }

        /// <summary>
        /// 批量更新
        /// </summary>
        /// <param name="updateObjs">Db模型集合</param>
        /// <returns></returns>
        public virtual bool UpdateRangeSplitTable(IEnumerable<T> updateObjs)
        {
            return Db.Updateable(updateObjs.ToList()).IgnoreColumns(ignoreAllNullColumns: true).SplitTable().ExecuteCommand() > 0;
        }

        /// <summary>
        /// 异步根据实体更新（主键要有值，主键是更新条件）
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual async Task<bool> UpdateAsync(T model)
        {
            return await GetDbSet<T>().UpdateAsync(model);
        }

        /// <summary>
        /// 根据表达式中的列更新
        /// </summary>
        /// <param name="columns">指定列表达式</param>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual bool Update(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression)
        {
            return GetDbSet<T>().Update(columns, whereExpression);
        }

        /// <summary>
        /// 异步根据表达式中的列更新
        /// </summary>
        /// <param name="columns">指定列表达式</param>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual async Task<bool> UpdateAsync(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().UpdateAsync(columns, whereExpression);
        }

        /// <summary>
        /// 根据实体更新（主键要有值，主键是更新条件）
        /// </summary>
        /// <param name="updateObjs">Db模型集合</param>
        /// <returns></returns>
        public virtual bool UpdateRange(IEnumerable<T> updateObjs)
        {
            return GetDbSet<T>().UpdateRangeWithOutNull(updateObjs.ToList());
        }

        /// <summary>
        /// 异步根据实体更新（主键要有值，主键是更新条件）
        /// </summary>
        /// <param name="updateObjs">Db模型集合</param>
        /// <returns></returns>
        public virtual async Task<int> UpdateRangeAsync(IEnumerable<T> updateObjs)
        {
            return await GetDbSet<T>().UpdateRangeAsync(updateObjs);
        }

        /// <summary>
        /// 更新，返回影响行数
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual int UpdateReturnCommand(T model)
        {
            return GetDbSet<T>().UpdateReturnCommand(model);
        }

        /// <summary>
        /// 批量更新，返回影响行数
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public virtual int UpdateRangeReturnCommand(IEnumerable<T> model)
        {
            return GetDbSet<T>().UpdateRangeReturnCommand(model);
        }

        /// <summary>
        /// 更新，返回影响行数
        /// </summary>
        /// <param name="columns">指定列</param>
        /// <param name="whereExpression">where条件</param>
        /// <returns></returns>
        public virtual int UpdateRangeReturnCommand(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression)
        {
            return GetDbSet<T>().UpdateRangeReturnCommand(columns, whereExpression);
        }

        /// <summary>
        /// 更新指定列
        /// </summary>
        /// <param name="model"></param>
        /// <param name="columns"></param>
        /// <returns></returns>
        public virtual bool Update(T model, Expression<Func<T, T>> columns)
        {
            return GetDbSet<T>().Update(model, columns);
        }

        /// <summary>
        /// 批量更新指定列
        /// </summary>
        /// <param name="updateObjs"></param>
        /// <param name="columns"></param>+
        /// 
        /// <returns></returns>
        public virtual bool UpdateRange(IEnumerable<T> updateObjs, Expression<Func<T, T>> columns)
        {
            return GetDbSet<T>().UpdateRange(updateObjs, columns);
        }

        /// <summary>
        /// 更新指定列
        /// </summary>
        /// <param name="model"></param>
        /// <param name="columns"></param>
        /// <returns></returns>
        public int UpdateReturnCommand(T model, Expression<Func<T, T>> columns)
        {
            return GetDbSet<T>().UpdateReturnCommand(model, columns);
        }

        /// <summary>
        /// 批量更新指定列
        /// </summary>
        /// <param name="model"></param>
        /// <param name="columns"></param>
        /// <returns></returns>
        public int UpdateRangeReturnCommand(IEnumerable<T> model, Expression<Func<T, T>> columns)
        {
            return GetDbSet<T>().UpdateRangeReturnCommand(model, columns);
        }

        /// <summary>
        /// 大数据批量修改
        /// </summary>
        public int BulkUpdate(List<T> entity)
        {
            return GetDbSet<T>().BulkUpdate(entity);
        }

        #endregion

        #region 分页查询

        /// <summary>
        /// 列表查询
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="startTime">分表查询开始时间</param>
        /// <param name="endTime">分表查询结束时间</param>
        /// <returns></returns>
        public virtual List<T> GetList_SplitTable(Expression<Func<T, bool>> whereExpression, DateTime startTime, DateTime endTime
        )
        {
            List<T> result = Db.Queryable<T>().Where(whereExpression)
                .SplitTable(startTime, endTime).ToList();
            return result;
        }

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
        public virtual List<T> GetListPage_SplitTable(Expression<Func<T, bool>> whereExpression, DateTime startTime, DateTime endTime, int intPageIndex, int intPageSize,
            ref int totalCount)
        {
            PageModel pageModel = new PageModel {PageIndex = intPageIndex, PageSize = intPageSize, TotalCount = totalCount};

            List<T> result = Db.Queryable<T>().Where(whereExpression).SplitTable(startTime, endTime).ToPageList(intPageIndex, intPageSize);

            totalCount = pageModel.TotalCount;
            return result;
        }


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
        public virtual List<T> GetListPage(Expression<Func<T, bool>> whereExpression, int intPageIndex, int intPageSize,
            ref int totalCount, OrderByType orderByType,
            Expression<Func<T, object>> orderByExpression)
        {
            PageModel pageModel = new PageModel {PageIndex = intPageIndex, PageSize = intPageSize, TotalCount = totalCount};

            List<T> result = GetDbSet<T>().GetPageList(whereExpression, pageModel,
                orderByExpression, orderByType);

            totalCount = pageModel.TotalCount;
            return result;
        }

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
        public virtual List<T> GetListPage(List<IConditionalModel> conditionalList, int intPageIndex, int intPageSize,
            ref int totalCount, OrderByType orderByType,
            Expression<Func<T, object>> orderByExpression)
        {
            PageModel pageModel = new PageModel {PageIndex = intPageIndex, PageSize = intPageSize, TotalCount = totalCount};

            List<T> result = GetDbSet<T>().GetPageList(conditionalList, pageModel,
                orderByExpression, orderByType);

            totalCount = pageModel.TotalCount;
            return result;
        }

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public virtual List<TResult> GetList<TResult>(Expression<Func<T, bool>> whereExpression, Expression<Func<T, TResult>> expression)
        {
            return GetDbSet<T>().GetList(whereExpression, expression);
        }

        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="selectexpression">指定列</param>
        /// <returns></returns>
        public virtual List<TResult> GetListPage<TResult>(Expression<Func<T, bool>> whereExpression, int intPageIndex, int intPageSize, ref int totalCount,
            Expression<Func<T, TResult>> selectexpression)
        {
            return GetDbSet<T>().GetListPage(whereExpression, intPageIndex, intPageSize, selectexpression, ref totalCount);
        }

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
        public virtual List<TResult> GetListPage<TResult>(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, ref int totalCount, OrderByType orderByType,
            Expression<Func<T, object>>? orderByExpression,
            Expression<Func<T, TResult>> selectexpression)
        {
            return GetDbSet<T>().GetListPage(whereExpression, intPageIndex, intPageSize, orderByType,
                orderByExpression, selectexpression, ref totalCount);
        }

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
        public virtual List<TResult> GetListPage<TResult>(List<IConditionalModel> conditionalList, int intPageIndex,
            int intPageSize, ref int totalCount, OrderByType orderByType,
            Expression<Func<T, object>>? orderByExpression,
            Expression<Func<T, TResult>> selectexpression)
        {
            return GetDbSet<T>().GetListPage(conditionalList, intPageIndex, intPageSize, orderByType,
                orderByExpression, selectexpression, ref totalCount);
        }

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
        public virtual List<TResult> GetListPageOrderByAsc<TResult>(Expression<Func<T, bool>> whereExpression,
            int intPageIndex, int intPageSize, ref int totalCount, Expression<Func<T, object>>? orderByExpression,
            Expression<Func<T, TResult>> selectexpression)
        {
            return GetDbSet<T>().GetListPage(whereExpression, intPageIndex, intPageSize,
                OrderByType.Asc, orderByExpression, selectexpression, ref totalCount);
        }

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
        public virtual List<TResult> GetListPageOrderByDesc<TResult>(Expression<Func<T, bool>> whereExpression,
            int intPageIndex, int intPageSize, ref int totalCount, Expression<Func<T, object>>? orderByExpression,
            Expression<Func<T, TResult>> selectexpression)
        {
            return GetDbSet<T>().GetListPage(whereExpression, intPageIndex, intPageSize,
                OrderByType.Desc, orderByExpression, selectexpression, ref totalCount);
        }

        #endregion

        #region 是否存在

        /// <summary>
        /// 是否存在
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual bool IsAny(Expression<Func<T, bool>> whereExpression)
        {
            return GetDbSet<T>().IsAny(whereExpression);
        }

        /// <summary>
        /// 异步是否存在
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual async Task<bool> IsAnyAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().IsAnyAsync(whereExpression);
        }

        #endregion

        #region 其他

        /// <summary>
        /// 合计
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual int Count(Expression<Func<T, bool>> whereExpression)
        {
            return GetDbSet<T>().Count(whereExpression);
        }

        /// <summary>
        /// 异步合计
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public virtual async Task<int> CountAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await GetDbSet<T>().CountAsync(whereExpression);
        }

        /// <summary>
        /// 求和
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="sumExpression">指定列求和</param>
        /// <returns></returns>
        public virtual TResult Sum<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> sumExpression)
        {
            return GetDbSet<T>().Sum(whereExpression, sumExpression);
        }

        /// <summary>
        /// 异步求和
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="sumExpression">指定列求和</param>
        /// <returns></returns>
        public virtual async Task<TResult> SumAsync<TResult>(Expression<Func<T, bool>> whereExpression, Expression<Func<T, TResult>> sumExpression)
        {
            return await GetDbSet<T>().SumAsync(whereExpression, sumExpression);
        }

        /// <summary>
        /// 最大
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="maxExpression">指定列最大值</param>
        /// <returns></returns>
        public virtual TResult Max<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> maxExpression)
        {
            return GetDbSet<T>().Max(whereExpression, maxExpression);
        }

        /// <summary>
        /// 异步最大
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="maxExpression">指定列最大值</param>
        /// <returns></returns>
        public virtual async Task<TResult> MaxAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> maxExpression)
        {
            return await GetDbSet<T>().MaxAsync(whereExpression, maxExpression);
        }

        /// <summary>
        /// 最小
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="minExpression">指定列最小值</param>
        /// <returns></returns>
        public virtual TResult Min<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> minExpression)
        {
            return GetDbSet<T>().Min(whereExpression, minExpression);
        }

        /// <summary>
        /// 异步最小
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="minExpression">指定列最小值</param>
        /// <returns></returns>
        public virtual async Task<TResult> MinAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> minExpression)
        {
            return await GetDbSet<T>().MinAsync(whereExpression, minExpression);
        }

        /// <summary>
        /// 平均
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="avgExpression">指定列平均</param>
        /// <returns></returns>
        public virtual TResult Avg<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> avgExpression)
        {
            return GetDbSet<T>().Avg(whereExpression, avgExpression);
        }

        /// <summary>
        /// 平均
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="avgExpression">指定列平均</param>
        /// <returns></returns>
        public virtual async Task<TResult> AvgAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> avgExpression)
        {
            return await GetDbSet<T>().AvgAsync(whereExpression, avgExpression);
        }

        /// <summary>
        /// 根据条件加载树形结构
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="childListExpression">子节点结构</param>
        /// <param name="parentIdExpression">父节点字段</param>
        /// <param name="rootValue">父节点条件</param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListTreeAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, IEnumerable<object>>> childListExpression,
            Expression<Func<T, object>> parentIdExpression,
            object rootValue)
        {
            return await GetDbSet<T>().GetListTreeAsync(whereExpression, childListExpression, parentIdExpression, rootValue);
        }

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
        public virtual async Task<List<T>> GetListTreeAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, IEnumerable<object>>> childListExpression,
            Expression<Func<T, object>> parentIdExpression, Expression<Func<T, object>> orderExpression, OrderByType orderByType,
            object rootValue)
        {
            return await GetDbSet<T>().GetListTreeAsync(whereExpression, childListExpression, parentIdExpression, orderExpression, orderByType, rootValue);
        }


        /// <summary>
        /// 加载树形结构
        /// </summary>
        /// <param name="childListExpression">子节点结构</param>
        /// <param name="parentIdExpression">父节点字段</param>
        /// <param name="rootValue">父节点条件</param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListTreeAsync(Expression<Func<T, IEnumerable<object>>> childListExpression,
            Expression<Func<T, object>> parentIdExpression,
            object rootValue)
        {
            return await GetDbSet<T>().GetListTreeAsync(childListExpression, parentIdExpression, rootValue);
        }

        /// <summary>
        /// 根据父节点ID获取所有子节点列表
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="parentIdExpression"></param>
        /// <param name="primaryKeyValue"></param>
        /// <param name="isContainOneself">是否包含自己</param>
        /// <returns></returns>
        public async Task<List<T>> GetListChildAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, object>> parentIdExpression, object primaryKeyValue, bool isContainOneself = true)
        {
            return await GetDbSet<T>().GetListChildAsync(whereExpression, parentIdExpression, primaryKeyValue, isContainOneself);
        }

        /// <summary>
        /// 根据父节点ID获取所有子节点列表
        /// </summary>
        /// <param name="parentIdExpression">父节点字段</param>
        /// <param name="primaryKeyValue">父节点</param>
        /// <returns></returns>
        public virtual async Task<List<T>> GetListChildAsync(Expression<Func<T, object>> parentIdExpression, object primaryKeyValue)
        {
            return await GetDbSet<T>().GetListChildAsync(parentIdExpression, primaryKeyValue);
        }

        /// <summary>
        /// 根据父子点ID获取所有父节点列表
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="parentIdExpression"></param>
        /// <param name="primaryKeyValue"></param>
        /// <returns></returns>
        public async Task<List<T>> GetParentListAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, object>> parentIdExpression, object primaryKeyValue)
        {
            return await GetDbSet<T>().GetParentListAsync(whereExpression, parentIdExpression, primaryKeyValue);
        }

        /// <summary>
        /// 根据父子点ID获取所有父节点列表
        /// </summary>
        /// <param name="parentIdExpression"></param>
        /// <param name="primaryKeyValue"></param>
        /// <returns></returns>
        public async Task<List<T>> GetParentListAsync(Expression<Func<T, object>> parentIdExpression, object primaryKeyValue)
        {
            return await GetDbSet<T>().GetParentListAsync(parentIdExpression, primaryKeyValue);
        }

        #endregion

        #region 联表查询

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <param name="resultExpression">结果表达式，用于定义从连接和筛选后的结果中提取的具体结果字段。</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
        public async Task<List<TResult>> GetList<T1, T2, TResult>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression,
            Expression<Func<T1, T2, TResult>> resultExpression)
        {
            // 使用EF Core的Queryable扩展方法，根据连接表达式和筛选表达式查询数据，并通过结果表达式映射结果，最后异步转换为列表形式返回。
            return await GetDbSet<T>().GetList(joinExpression, whereExpression, resultExpression);
        }

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <param name="resultExpression">结果表达式，用于定义从连接和筛选后的结果中提取的具体结果字段。</param>
        /// <param name="orderByType">排序</param>
        /// <param name="orderByExpression">排序字段</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
        public async Task<List<TResult>> GetList<T1, T2, TResult>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression,
            Expression<Func<T1, T2, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, object>> orderByExpression)
        {
            return await GetDbSet<T>().GetList(joinExpression, whereExpression, resultExpression, orderByType, orderByExpression);
        }

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
        public List<TResult> GetListPage<T1, T2, TResult>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression,
            Expression<Func<T1, T2, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, object>> orderByExpression,
            int pageIndex, int pageSize, ref int totalCount)
        {
            return GetDbSet<T>().GetListPage(joinExpression, whereExpression, resultExpression, orderByType, orderByExpression, pageIndex, pageSize, ref totalCount);
        }

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
        public async Task<List<TResult>> GetList<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression,
            Expression<Func<T1, T2, T3, TResult>> resultExpression)
        {
            // 使用EF Core的Queryable扩展方法进行异步查询并转换为列表
            return await GetDbSet<T>().GetList(joinExpression, whereExpression, resultExpression);
        }

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
        public async Task<List<TResult>> GetList<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression,
            Expression<Func<T1, T2, T3, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, object>> orderByExpression)
        {
            // 基于排序条件进行异步查询并转换为列表
            return await GetDbSet<T>().GetList(joinExpression, whereExpression, resultExpression, orderByType, orderByExpression);
        }

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
        public List<TResult> GetListPage<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression,
            Expression<Func<T1, T2, T3, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, object>> orderByExpression,
            int pageIndex, int pageSize, ref int totalCount)
        {
            // 执行分页查询
            return GetDbSet<T>().GetListPage(joinExpression, whereExpression, resultExpression, orderByType, orderByExpression, pageIndex, pageSize, ref totalCount);
        }

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
        public async Task<List<TResult>> GetList<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression,
            Expression<Func<T1, T2, T3, T4, TResult>> resultExpression)
        {
            // 使用EF Core的Queryable扩展方法进行异步查询并转换为列表
            return await GetDbSet<T>().GetList(joinExpression, whereExpression, resultExpression);
        }


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
        public async Task<List<TResult>> GetList<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression,
            Expression<Func<T1, T2, T3, T4, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, T4, object>> orderByExpression)
        {
            // 基于排序条件进行异步查询并转换为列表
            return await GetDbSet<T>().GetList(joinExpression, whereExpression, resultExpression, orderByType, orderByExpression);
        }

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
        public List<TResult> GetListPage<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression,
            Expression<Func<T1, T2, T3, T4, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, T4, object>> orderByExpression,
            int pageIndex, int pageSize, ref int totalCount)
        {
            // 执行分页查询
            return GetDbSet<T>().GetListPage(joinExpression, whereExpression, resultExpression, orderByType, orderByExpression, pageIndex, pageSize, ref totalCount);
        }

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
        public List<TResult> GetListPageNoLock<T1, T2, TResult>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression,
            Expression<Func<T1, T2, TResult>> resultExpression, OrderByType orderByType,
            Expression<Func<T1, T2, object>> orderByExpression,
            int pageIndex, int pageSize, ref int totalCount)
        {
            return GetDbSet<T>().GetListPageNoLock(joinExpression, whereExpression, resultExpression, orderByType, orderByExpression, pageIndex, pageSize, ref totalCount);
        }

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
        public async Task<int> GetList<T1, T2>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression)
        {
            // 使用EF Core的Queryable扩展方法，根据连接表达式和筛选表达式查询数据，并通过结果表达式映射结果，最后异步转换为列表形式返回。
            return await GetDbSet<T>().GetList(joinExpression, whereExpression);
        }

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <param name="joinExpression">连接表达式，用于定义两个类型T1和T2之间的连接关系。</param>
        /// <param name="whereExpression">筛选表达式，用于对类型T1的实体进行筛选。</param>
        /// <param name="orderByType">排序</param>
        /// <param name="orderByExpression">排序字段</param>
        /// <returns>返回一个包含结果的列表，列表的元素类型为TResult。</returns>
        public async Task<int> GetList<T1, T2>(Expression<Func<T1, T2, object[]>> joinExpression,
            Expression<Func<T1, T2, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T1, T2, object>> orderByExpression)
        {
            return await GetDbSet<T>().GetList(joinExpression,whereExpression, orderByType, orderByExpression);
        }

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
        public async Task<int> GetList<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression)
        {
            // 使用EF Core的Queryable扩展方法进行异步查询并转换为列表
            return await GetDbSet<T>().GetList(joinExpression,whereExpression);
        }

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
        public async Task<int> GetList<T1, T2, T3, TResult>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, object>> orderByExpression)
        {
            // 基于排序条件进行异步查询并转换为列表
            return await GetDbSet<T>().GetList(joinExpression,whereExpression, orderByType,orderByExpression);
            ;
        }

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
        public async Task<int> GetList<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression)
        {
            // 使用EF Core的Queryable扩展方法进行异步查询并转换为列表
            return await GetDbSet<T>().GetList(joinExpression,whereExpression);
        }


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
        public async Task<int> GetList<T1, T2, T3, T4, TResult>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, T4, object>> orderByExpression)
        {
            // 基于排序条件进行异步查询并转换为列表
            return await GetDbSet<T>().GetList(joinExpression,whereExpression, orderByType,orderByExpression);
            ;
        }

        #endregion

        /// <summary>
        /// 切换数据库
        /// </summary>
        public void ChangeDataBase(string baseKey)
        {
            if (!string.IsNullOrWhiteSpace(baseKey))
            {
                Db = new SqlSugarClient(DatabaseList.DbList.First(a => a.ConfigId == baseKey)
                    .ConnectionConfig);
            }
        }

        /// <summary>
        /// 有则更新无则插入
        /// </summary>
        /// <param name="model"></param>
        /// <returns>返回插入数量和更新属性</returns>
        public int AddOrUpdate(T model)
        {
            return GetDbSet<T>().AddOrUpdate(model);
        }

        /// <summary>
        /// 有则更新无则插入
        /// </summary>
        /// <param name="model"></param>
        /// <returns>返回插入数量和更新属性</returns>
        public int AddOrUpdate(List<T> model)
        {
            return GetDbSet<T>().AddOrUpdate(model);
        }

        /// <summary>
        /// 批量插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public int BulkCopy(List<T> t)
        {
            return GetDbSet<T>().BulkCopy(t);
        }

        /// <summary>
        /// 批量有则更新无则插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public int BulkMerge(List<T> t)
        {
            return GetDbSet<T>().BulkMerge(t);
        }

        /// <summary>
        /// 批量插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public async Task<int> BulkCopyAsync(List<T> t)
        {
            return await GetDbSet<T>().BulkCopyAsync(t);
        }

        /// <summary>
        /// 批量更新
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public async Task<int> BulkUpdateAsync(List<T> t)
        {
            return await GetDbSet<T>().BulkUpdateAsync(t);
        }

        /// <summary>
        /// 批量有则更新无则插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public async Task<int> BulkMergeAsync(List<T> t)
        {
            return await GetDbSet<T>().BulkMergeAsync(t);
        }

        public DbSet<T> GetDb()
        {
            return GetDbSet<T>();
        }
    }

    /// <summary>
    /// 业务抽象
    /// </summary>
    public abstract class AbstractBaseService : IBaseService
    {
    }
}