using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using SqlSugar;

namespace MAF.DEMO.IDbService
{
    /// <summary>
    /// 数据库上下文扩展
    /// </summary>
    public class DbSet<T> : SimpleClient<T> where T : class, new()
    {
        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="context">数据库上下文</param>
        public DbSet(ISqlSugarClient context) : base(context)
        {
        }

        /// <summary>
        /// 根据实体更新（主键要有值，主键是更新条件），返回是否成功
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <param name="setColumns">指定更新列</param>
        /// <returns></returns>
        public bool Saveable(T model, Expression<Func<T, object>> setColumns)
        {
            return Context.Updateable(model).UpdateColumns(setColumns).ExecuteCommand() > 0;
        }

        /// <summary>
        /// 根据实体更新（主键要有值，主键是更新条件），返回影响行数
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <param name="setColumns">指定更新列</param>
        /// <returns></returns>
        public int SaveableReturnCommand(T model, Expression<Func<T, object>> setColumns)
        {
            return Context.Updateable(model).UpdateColumns(setColumns).ExecuteCommand();
        }

        /// <summary>
        /// 根据实体更新（主键要有值，主键是更新条件），返回影响行数
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <param name="setColumns">指定更新列</param>
        /// <returns></returns>
        public int SaveableReturnCommand(IEnumerable<T> model, Expression<Func<T, object>> setColumns)
        {
            return Context.Updateable(model.ToList()).UpdateColumns(setColumns).ExecuteCommand();
        }

        /// <summary>
        /// 插入 设置NULL列不插入和强制插入自增列
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public bool InsertWithOutNull(T model)
        {
            return Context.Insertable<T>(model).ExecuteCommand() > 0;
        }

        /// <summary>
        /// 插入 设置NULL列不插入和强制插入自增列
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public int InsertWithOutNullReturnCommand(T model)
        {
            return Context.Insertable<T>(model).ExecuteCommand();
        }

        /// <summary>
        /// 批量插入 设置NULL列不插入和强制插入自增列
        /// </summary>
        /// <param name="models">Db模型集合</param>
        /// <returns></returns>
        public bool InsertRangeWithOutNull(IEnumerable<T> models)
        {
            return Context.Insertable<T>(models.ToList()).ExecuteCommand() > 0;
        }

        /// <summary>
        /// 批量插入 设置NULL列不插入和强制插入自增列
        /// </summary>
        /// <param name="models">Db模型集合</param>
        /// <returns></returns>
        public int InsertRangeWithOutNullReturnCommand(IEnumerable<T> models)
        {
            return Context.Insertable<T>(models.ToList()).ExecuteCommand();
        }

        /// <summary>
        /// 异步插入
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public override async Task<bool> InsertAsync(T model)
        {
            return await Context.Insertable<T>(model).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 异步插入
        /// </summary>
        /// <param name="models">Db模型集合</param>
        /// <returns></returns>
        public async Task<bool> InsertAsync(T[] models)
        {
            return await Context.Insertable<T>(models).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 异步插入
        /// </summary>
        /// <param name="models">Db模型集合</param>
        /// <returns></returns>
        public async Task<bool> InsertAsync(List<T> models)
        {
            return await Context.Insertable<T>(models).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 插入返回自增id 设置NULL列不插入和强制插入自增列
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public int InsertWithOutNullReturnIdentity(T model)
        {
            return Context.Insertable<T>(model).IgnoreColumns(true, true).ExecuteReturnIdentity();
        }


        /// <summary>
        /// 插入
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public override T InsertReturnEntity(T model)
        {
            return Context.Insertable<T>(model).IgnoreColumns(true, true).ExecuteReturnEntity();
        }

        /// <summary>
        /// 异步插入
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public override async Task<T> InsertReturnEntityAsync(T model)
        {
            return await Context.Insertable<T>(model).IgnoreColumns(true, true).ExecuteReturnEntityAsync();
        }

        /// <summary>
        /// 异步插入
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public override async Task<int> InsertReturnIdentityAsync(T model)
        {
            return await Context.Insertable<T>(model).IgnoreColumns(true, true).ExecuteReturnIdentityAsync();
        }

        /// <summary>
        /// 异步删除
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public override async Task<bool> DeleteAsync(T model)
        {
            return await Context.Deleteable<T>(model).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 异步删除
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public override async Task<bool> DeleteAsync(List<T> model)
        {
            return await Context.Deleteable<T>(model).ExecuteCommandHasChangeAsync();
        }

        /// <summary>
        /// 异步删除
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public override async Task<bool> DeleteAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await Context.Deleteable<T>(whereExpression).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 异步根据主键删除
        /// </summary>
        /// <param name="id">主键</param>
        public override async Task<bool> DeleteByIdAsync(dynamic id)
        {
            return await Context.Deleteable<T>(id).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 异步根据主键集合删除
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public override async Task<bool> DeleteByIdsAsync(dynamic[] ids)
        {
            return await Context.Deleteable<T>(ids).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 根据主键集合删除
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public bool DeleteByIds(List<dynamic> ids)
        {
            return Context.Deleteable<T>(ids).ExecuteCommand() > 0;
        }

        /// <summary>
        /// 异步根据主键集合删除
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public async Task<bool> DeleteByIdsAsync(List<dynamic> ids)
        {
            return await Context.Deleteable<T>(ids).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 根据主键集合查询
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public List<T> GetByIds(IEnumerable<dynamic> ids)
        {
            return Context.Queryable<T>().In(ids).ToList();
        }
        
        /// <summary>
        /// 异步根据主键集合查询
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public async Task<List<T>> GetByIdsAsync(IEnumerable<dynamic> ids)
        {
            return await Context.Queryable<T>().In(ids).ToListAsync();
        }

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="ids">ID集合</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public List<TResult> GetByIds<TResult>(IEnumerable<dynamic> ids, Expression<Func<T, TResult>> expression)
        {
            return Context.Queryable<T>().In(ids).Select<TResult>(expression).ToList();
        }
        
        /// <summary>
        /// 异步根据主键删除
        /// </summary>
        /// <param name="id">主键</param>
        public async Task<bool> DeleteByIdAsync(long id)
        {
            return await Context.Deleteable<T>(id).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 异步根据主键集合删除
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public async Task<bool> DeleteByIdsAsync(long[] ids)
        {
            return await Context.Deleteable<T>(ids).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 根据主键集合删除
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public bool DeleteByIds(List<long> ids)
        {
            return Context.Deleteable<T>(ids).ExecuteCommand() > 0;
        }

        /// <summary>
        /// 异步根据主键集合删除
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public async Task<bool> DeleteByIdsAsync(List<long> ids)
        {
            return await Context.Deleteable<T>(ids).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 根据主键集合查询
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public List<T> GetByIds(IEnumerable<long> ids)
        {
            return Context.Queryable<T>().In(ids).ToList();
        }
        
        /// <summary>
        /// 异步根据主键集合查询
        /// </summary>
        /// <param name="ids">主键集合</param>
        /// <returns></returns>
        public async Task<List<T>> GetByIdsAsync(IEnumerable<long> ids)
        {
            return await Context.Queryable<T>().In(ids).ToListAsync();
        }

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="ids">ID集合</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public List<TResult> GetByIds<TResult>(IEnumerable<long> ids, Expression<Func<T, TResult>> expression)
        {
            return Context.Queryable<T>().In(ids).Select<TResult>(expression).ToList();
        }

        /// <summary>
        /// 求和
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="sumExpression">指定列求和</param>
        /// <returns></returns>
        public TResult Sum<TResult>(Expression<Func<T, bool>> whereExpression, Expression<Func<T, TResult>> sumExpression)
        {
            return Context.Queryable<T>().Where(whereExpression).Sum(sumExpression);
        }

        /// <summary>
        /// 求和
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="sumExpression">指定列求和</param>
        /// <returns></returns>
        public async Task<TResult> SumAsync<TResult>(Expression<Func<T, bool>> whereExpression, Expression<Func<T, TResult>> sumExpression)
        {
            return await Context.Queryable<T>().Where(whereExpression).SumAsync(sumExpression);
        }


        /// <summary>
        /// 最大
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="maxExpression">指定列最大值</param>
        /// <returns></returns>
        public TResult Max<TResult>(Expression<Func<T, bool>> whereExpression, Expression<Func<T, TResult>> maxExpression)
        {
            return Context.Queryable<T>().Where(whereExpression).Max(maxExpression);
        }

        /// <summary>
        /// 最大
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="maxExpression">指定列最大值</param>
        /// <returns></returns>
        public async Task<TResult> MaxAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> maxExpression)
        {
            return await Context.Queryable<T>().Where(whereExpression).MaxAsync(maxExpression);
        }

        /// <summary>
        /// 最小
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="minExpression">指定列最小值</param>
        /// <returns></returns>
        public TResult Min<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> minExpression)
        {
            return Context.Queryable<T>().Where(whereExpression).Min(minExpression);
        }

        /// <summary>
        /// 最小
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="minExpression">指定列最小值</param>
        /// <returns></returns>
        public async Task<TResult> MinAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> minExpression)
        {
            return await Context.Queryable<T>().Where(whereExpression).MinAsync(minExpression);
        }


        /// <summary>
        /// 平均
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="avgExpression">指定列平均</param>
        /// <returns></returns>
        public TResult Avg<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> avgExpression)
        {
            return Context.Queryable<T>().Where(whereExpression).Avg(avgExpression);
        }

        /// <summary>
        /// 平均
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="avgExpression">指定列平均</param>
        /// <returns></returns>
        public async Task<TResult> AvgAsync<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> avgExpression)
        {
            return await Context.Queryable<T>().Where(whereExpression).AvgAsync(avgExpression);
        }
        

        /// <summary>
        /// 异步查询全部
        /// </summary>
        /// <returns></returns>
        public async Task<List<T>> GetListAsync()
        {
            return await Context.Queryable<T>().ToListAsync();
        }

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public List<TResult> GetList<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> expression)
        {
            return Context.Queryable<T>().Where(whereExpression).Select<TResult>(expression).ToList();
        }

        /// <summary>
        /// 查询指定（自定义排序）
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="expression">指定列</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <returns></returns>
        public List<TResult> GetList<TResult>(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, TResult>> expression, Expression<Func<T, object>> orderByExpression, OrderByType orderByType)
        {
            return Context.Queryable<T>().Where(whereExpression).OrderBy(orderByExpression, orderByType).Select<TResult>(expression).ToList();
        }


        /// <summary>
        /// 查询全部
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <returns></returns>
        public List<T> GetList(Expression<Func<T, bool>> whereExpression, Expression<Func<T, object>> orderByExpression,
            OrderByType orderByType)
        {
            return Context.Queryable<T>().Where(whereExpression).OrderBy(orderByExpression, orderByType).Select<T>()
                .ToList();
        }

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public List<TResult> GetList<TResult>(Expression<Func<T, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T, object>> orderByExpression, Expression<Func<T, TResult>> expression)
        {
            return Context.Queryable<T>().Where(whereExpression).OrderBy(orderByExpression, orderByType)
                .Select<TResult>(expression).ToList();
        }

        /// <summary>
        /// 查询指定列
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="expression">指定列</param>
        /// <returns></returns>
        public async Task<List<TResult>> GetListAsync<TResult>(Expression<Func<T, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T, object>> orderByExpression, Expression<Func<T, TResult>> expression)
        {
            return await Context.Queryable<T>().Where(whereExpression).OrderBy(orderByExpression, orderByType)
                .Select<TResult>(expression).ToListAsync();
        }

        /// <summary>
        /// 异步查询全部数据
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <returns></returns>
        public async Task<List<T>> GetListAsync(Expression<Func<T, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T, object>> orderByExpression)
        {
            return await Context.Queryable<T>().Where(whereExpression).OrderBy(orderByExpression, orderByType).ToListAsync();
        }

        /// <summary>
        /// 异步查询全部
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public async Task<List<T>> GetListNoLockAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await Context.Queryable<T>().With(SqlWith.NoLock).Where(whereExpression).ToListAsync();
        }

        /// <summary>
        /// 异步查询全部数据
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <returns></returns>
        public async Task<List<T>> GetListNoLockAsync(Expression<Func<T, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T, object>> orderByExpression)
        {
            return await Context.Queryable<T>().With(SqlWith.NoLock).Where(whereExpression).OrderBy(orderByExpression, orderByType).ToListAsync();
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
        /// <param name="selectexpression">指定列</param>
        /// <returns></returns>
        public List<TResult> GetListPage<TResult>(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, OrderByType orderByType,
            Expression<Func<T, object>>? orderByExpression, Expression<Func<T, TResult>> selectexpression, ref int totalCount)
        {
            List<TResult> result = Context.Queryable<T>()
                .OrderByIF(orderByExpression != null, orderByExpression, orderByType)
                .Where(whereExpression)
                .Select<TResult>(selectexpression)
                .ToPageList(intPageIndex, intPageSize, ref totalCount);
            return result;
        }

        /// <summary>
        /// 分页查询查询（自定义排序）
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <returns></returns>
        public List<T> GetListPage(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, OrderByType orderByType, Expression<Func<T, object>>? orderByExpression, ref int totalCount)
        {
            return Context.Queryable<T>()
                .OrderByIF(orderByExpression != null, orderByExpression, orderByType)
                .Where(whereExpression)
                .ToPageList(intPageIndex, intPageSize, ref totalCount);
        }

        /// <summary>
        /// 分页查询查询（自定义排序）
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <returns></returns>
        public List<T> GetListPageNoLock(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, OrderByType orderByType, Expression<Func<T, object>>? orderByExpression, ref int totalCount)
        {
            return Context.Queryable<T>()
                .With(SqlWith.NoLock)
                .OrderByIF(orderByExpression != null, orderByExpression, orderByType)
                .Where(whereExpression)
                .ToPageList(intPageIndex, intPageSize, ref totalCount);
        }


        /// <summary>
        /// 分页查询查询（自定义排序）
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="orderByExpression">排序表达式</param>
        /// <param name="orderByType">排序枚举</param>
        /// <returns></returns>
        public async Task<List<T>> GetListPageAsync(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, OrderByType orderByType, Expression<Func<T, object>>? orderByExpression, RefAsync<int> totalCount)
        {
            return await Context.Queryable<T>()
                .OrderByIF(orderByExpression != null, orderByExpression, orderByType)
                .Where(whereExpression)
                .ToPageListAsync(intPageIndex, intPageSize, totalCount);
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
        /// <param name="selectexpression">指定列</param>
        /// <returns></returns>
        public List<TResult> GetListPage<TResult>(List<IConditionalModel> conditionalList, int intPageIndex,
            int intPageSize, OrderByType orderByType,
            Expression<Func<T, object>>? orderByExpression, Expression<Func<T, TResult>> selectexpression, ref int totalCount)
        {
            List<TResult> result = Context.Queryable<T>()
                .OrderByIF(orderByExpression != null, orderByExpression, orderByType)
                .Where(conditionalList)
                .Select<TResult>(selectexpression)
                .ToPageList(intPageIndex, intPageSize, ref totalCount);
            return result;
        }

        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="intPageIndex">页码</param>
        /// <param name="intPageSize">页大小</param>
        /// <param name="totalCount">总数</param>
        /// <param name="selectexpression">指定列</param>
        /// <returns></returns>
        public List<TResult> GetListPage<TResult>(Expression<Func<T, bool>> whereExpression, int intPageIndex,
            int intPageSize, Expression<Func<T, TResult>> selectexpression, ref int totalCount)
        {
            List<TResult> result = Context.Queryable<T>().Where(whereExpression)
                .Select<TResult>(selectexpression)
                .ToPageList(intPageIndex, intPageSize, ref totalCount);

            return result;
        }


        /// <summary>
        /// 异步查询全部
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public async Task<List<T>> GetListAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await Context.Queryable<T>().Where(whereExpression).ToListAsync();
        }

        /// <summary>
        /// 异步查询全部(高并发使用)
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public async Task<List<T>> GetListConcurrencyAsync(Expression<Func<T, bool>> whereExpression)
        {
            var newDb = Context.CopyNew();
            return await newDb.Queryable<T>().Where(whereExpression).ToListAsync();
        }


        /// <summary>
        /// 通过开始时间和结束时间找到对应时间的表
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="beginDate">分表开始时间</param>
        /// <param name="endDate">分表结束时间</param>
        /// <param name="pageIndex">页码</param>
        /// <param name="pageSize">条数</param>
        /// <returns></returns>
        public List<T> GetSplitTablePageList(Expression<Func<T, bool>> whereExpression, DateTime beginDate, DateTime endDate,
            int pageIndex, int pageSize)
        {
            return Context.Queryable<T>().Where(whereExpression)
                .SplitTable(beginDate, endDate).ToPageList(pageIndex, pageSize);
        }

        /// <summary>
        /// 通过开始时间和结束时间找到对应时间的表
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="beginDate">分表开始时间</param>
        /// <param name="endDate">分表结束时间</param>
        /// <returns></returns>
        public List<T> GetSplitTableList(Expression<Func<T, bool>> whereExpression, DateTime beginDate, DateTime endDate)
        {
            return Context.Queryable<T>().Where(whereExpression)
                .SplitTable(beginDate, endDate).ToList();
        }

        /// <summary>
        /// 所有分表检索找到对应表
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="pageIndex">页码</param>
        /// <param name="pageSize">条数</param>
        /// <returns></returns>
        public List<T> GetSplitTablePageList(Expression<Func<T, bool>> whereExpression, int pageIndex, int pageSize)
        {
            return Context.Queryable<T>().Where(whereExpression)
                .SplitTable(tabs => tabs).ToPageList(pageIndex, pageSize);
        }

        /// <summary>
        /// 所有分表检索找到对应表
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public List<T> GetSplitTableList(Expression<Func<T, bool>> whereExpression)
        {
            return Context.Queryable<T>().Where(whereExpression)
                .SplitTable(tabs => tabs).ToList();
        }

        /// <summary>
        /// 查询全部
        /// </summary>
        /// <param name="strWhere">where条件</param>
        /// <returns></returns>
        public List<T> GetList(string strWhere)
        {
            return Context.Queryable<T>().WhereIF(!string.IsNullOrEmpty(strWhere), strWhere).ToList();
        }

        /// <summary>
        /// 异步查询全部
        /// </summary>
        /// <param name="strWhere">where条件</param>
        /// <returns></returns>
        public async Task<List<T>> GetListAsync(string strWhere)
        {
            return await Context.Queryable<T>().WhereIF(!string.IsNullOrEmpty(strWhere), strWhere).ToListAsync();
        }

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
            return await Context.Queryable(joinExpression).Where(whereExpression).Select(resultExpression).ToListAsync();
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
            return await Context.Queryable(joinExpression).OrderBy(orderByExpression, orderByType).Where(whereExpression).Select(resultExpression).ToListAsync();
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
            return Context.Queryable(joinExpression).OrderBy(orderByExpression, orderByType)
                .Where(whereExpression)
                .Select(resultExpression)
                .ToPageList(pageIndex, pageSize, ref totalCount);
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
            return Context.Queryable(joinExpression).With(SqlWith.NoLock).OrderBy(orderByExpression, orderByType)
                .Where(whereExpression)
                .Select(resultExpression)
                .ToPageList(pageIndex, pageSize, ref totalCount);
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
            return await Context.Queryable(joinExpression).Where(whereExpression).Select(resultExpression).ToListAsync();
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
            return await Context.Queryable(joinExpression).OrderBy(orderByExpression, orderByType).Where(whereExpression).Select(resultExpression).ToListAsync();
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
            return Context.Queryable(joinExpression).OrderBy(orderByExpression, orderByType)
                .Where(whereExpression)
                .Select(resultExpression)
                .ToPageList(pageIndex, pageSize, ref totalCount);
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
            return await Context.Queryable(joinExpression).Where(whereExpression).Select(resultExpression).ToListAsync();
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
            return await Context.Queryable(joinExpression).OrderBy(orderByExpression, orderByType).Where(whereExpression).Select(resultExpression).ToListAsync();
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
            return Context.Queryable(joinExpression).OrderBy(orderByExpression, orderByType)
                .Where(whereExpression)
                .Select(resultExpression)
                .ToPageList(pageIndex, pageSize, ref totalCount);
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
            return await Context.Queryable(joinExpression).Where(whereExpression).CountAsync();
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
            return await Context.Queryable(joinExpression).OrderBy(orderByExpression, orderByType).Where(whereExpression).CountAsync();
        }

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <returns>一个任务，完成时返回符合条件的结果列表。</returns>
        public async Task<int> GetList<T1, T2, T3>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression)
        {
            // 使用EF Core的Queryable扩展方法进行异步查询并转换为列表
            return await Context.Queryable(joinExpression).Where(whereExpression).CountAsync();
        }

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="orderByType">排序方式。</param>
        /// <param name="orderByExpression">排序表达式，用于定义排序规则。</param>
        /// <returns>一个任务，完成时返回符合条件并按指定排序条件排序的结果列表。</returns>
        public async Task<int> GetList<T1, T2, T3>(Expression<Func<T1, T2, T3, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, object>> orderByExpression)
        {
            // 基于排序条件进行异步查询并转换为列表
            return await Context.Queryable(joinExpression).OrderBy(orderByExpression, orderByType).Where(whereExpression).CountAsync();;
        }

        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="T4">第四个泛型类型参数。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <returns>一个任务，完成时返回符合条件的结果列表。</returns>
        public async Task<int> GetList<T1, T2, T3, T4>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression)
        {
            // 使用EF Core的Queryable扩展方法进行异步查询并转换为列表
            return await Context.Queryable(joinExpression).Where(whereExpression).CountAsync();;
        }


        /// <summary>
        /// 联表查询
        /// </summary>
        /// <typeparam name="T1">第一个泛型类型参数。</typeparam>
        /// <typeparam name="T2">第二个泛型类型参数。</typeparam>
        /// <typeparam name="T3">第三个泛型类型参数。</typeparam>
        /// <typeparam name="T4">第四个泛型类型参数。</typeparam>
        /// <param name="joinExpression">连接表达式，用于定义数据的连接方式。</param>
        /// <param name="whereExpression">筛选表达式，用于过滤数据。</param>
        /// <param name="orderByType">排序方式。</param>
        /// <param name="orderByExpression">排序表达式，用于定义排序规则。</param>
        /// <returns>一个任务，完成时返回符合条件并按指定排序条件排序的结果列表。</returns>
        public async Task<int> GetList<T1, T2, T3, T4>(Expression<Func<T1, T2, T3, T4, object[]>> joinExpression,
            Expression<Func<T1, T2, T3, T4, bool>> whereExpression, OrderByType orderByType,
            Expression<Func<T1, T2, T3, T4, object>> orderByExpression)
        {
            // 基于排序条件进行异步查询并转换为列表
            return await Context.Queryable(joinExpression).OrderBy(orderByExpression, orderByType).Where(whereExpression).CountAsync();;
        }

        #endregion

        /// <summary>
        /// 根据主键查询
        /// </summary>
        /// <param name="objId">主键</param>
        /// <param name="blnUseCache">是否使用缓存</param>
        /// <returns></returns>
        public T GetById(dynamic objId, bool blnUseCache)
        {
            return Context.Queryable<T>().WithCacheIF(blnUseCache).InSingle(objId);
        }

        /// <summary>
        /// 异步根据主键查询
        /// </summary>
        /// <param name="objId">主键</param>
        /// <param name="blnUseCache">是否使用缓存</param>
        /// <returns></returns>
        public async Task<T> GetByIdAsync(dynamic objId, bool blnUseCache)
        {
            return await Task.Run(Context.Queryable<T>().WithCacheIF(blnUseCache).InSingle(objId));
        }

        /// <summary>
        /// 异步根据主键查询
        /// </summary>
        /// <param name="objId">主键</param>
        /// <returns></returns>
        public override async Task<T> GetByIdAsync(dynamic objId)
        {
            return await Task.Run(() => Context.Queryable<T>().InSingle(objId));
        }

        /// <summary>
        /// 根据where查询单条
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public override async Task<T> GetSingleAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await Context.Queryable<T>().SingleAsync(whereExpression);
        }

        /// <summary>
        /// 根据where条件查询单条，多个返回第一条
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <param name="orderExpression">orderBy表达式</param>
        /// <param name="orderByType">排序类型</param>
        /// <returns></returns>
        public T GetFirst(Expression<Func<T, bool>> whereExpression,
            Expression<Func<T, object>> orderExpression, OrderByType orderByType)
        {
            return Context.Queryable<T>().Where(whereExpression).OrderBy(orderExpression, orderByType).First();
        }

        /// <summary>
        /// 更新，返回影响行数
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public int UpdateReturnCommand(T model)
        {
            return Context.Updateable(model).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommand();
        }

        /// <summary>
        /// 更新，返回影响行数
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public int UpdateRangeReturnCommand(IEnumerable<T> model)
        {
            return Context.Updateable(model.ToList()).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommand();
        }

        /// <summary>
        /// 更新，返回影响行数
        /// </summary>
        /// <param name="columns">指定列</param>
        /// <param name="whereExpression">where条件</param>
        /// <returns></returns>
        public int UpdateRangeReturnCommand(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression)
        {
            return Context.Updateable<T>().SetColumns(columns).Where(whereExpression).ExecuteCommand();
        }

        /// <summary>
        /// 异步更新
        /// </summary>
        /// <param name="model">Db模型</param>
        /// <returns></returns>
        public override async Task<bool> UpdateAsync(T model)
        {
            return await Context.Updateable<T>(model).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommandAsync() >
                   0;
        }

        /// <summary>
        /// 异步更新
        /// </summary>
        /// <param name="columns">更新列表达式</param>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public override async Task<bool> UpdateAsync(Expression<Func<T, T>> columns, Expression<Func<T, bool>> whereExpression)
        {
            return await Context.Updateable<T>().SetColumns(columns).Where(whereExpression).ExecuteCommandAsync() > 0;
        }

        /// <summary>
        /// 异步批量更新
        /// </summary>
        /// <param name="updateObjs">Db模型集合</param>
        /// <returns></returns>
        public async Task<int> UpdateRangeAsync(IEnumerable<T> updateObjs)
        {
            return await Context.Updateable(updateObjs.ToList()).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommandAsync();
        }

        /// <summary>
        /// 异步是否存在
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public override async Task<bool> IsAnyAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await Context.Queryable<T>().AnyAsync(whereExpression);
        }

        /// <summary>
        /// 异步合计
        /// </summary>
        /// <param name="whereExpression">where表达式</param>
        /// <returns></returns>
        public override async Task<int> CountAsync(Expression<Func<T, bool>> whereExpression)
        {
            return await Context.Queryable<T>().CountAsync(whereExpression);
        }

        /// <summary>
        /// 更新忽略NUll列
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public bool UpdateWithOutNull(T model)
        {
            return Context.Updateable(model).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommand() > 0;
        }

        /// <summary>
        /// 更新指定列
        /// </summary>
        /// <param name="model"></param>
        /// <param name="setColumns"></param>
        /// <returns></returns>
        public bool Update(T model, Expression<Func<T, T>> setColumns)
        {
            return Context.Updateable(model).SetColumns(setColumns).ExecuteCommand() > 0;
        }

        /// <summary>
        /// 更新指定列
        /// </summary>
        /// <param name="model"></param>
        /// <param name="setColumns"></param>
        /// <returns></returns>
        public int UpdateReturnCommand(T model, Expression<Func<T, T>> setColumns)
        {
            return Context.Updateable(model).SetColumns(setColumns).ExecuteCommand();
        }

        /// <summary>
        /// 更新忽略NUll列，返回影响行数
        /// </summary>
        /// <param name="models"></param>
        /// <returns></returns>
        public int UpdateRangeWithOutNullReturnCommand(IEnumerable<T> models)
        {
            return Context.Updateable(models.ToList()).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommand();
        }

        /// <summary>
        /// 更新忽略NUll列
        /// </summary>
        /// <param name="models"></param>
        /// <returns></returns>
        public bool UpdateRangeWithOutNull(IEnumerable<T> models)
        {
            return Context.Updateable(models.ToList()).ExecuteCommand() > 0;
        }

        /// <summary>
        /// 批量插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public int BulkCopy(List<T> t)
        {
            return Context.Fastest<T>().BulkCopy(t);
        }

        /// <summary>
        /// 批量更新
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public int BulkUpdate(List<T> t)
        {
            return Context.Fastest<T>().BulkUpdate(t);
        }

        /// <summary>
        /// 批量有则更新无则插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public int BulkMerge(List<T> t)
        {
            return Context.Fastest<T>().BulkMerge(t);
        }

        /// <summary>
        /// 批量插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public async Task<int> BulkCopyAsync(List<T> t)
        {
            return await Context.Fastest<T>().BulkCopyAsync(t);
        }

        /// <summary>
        /// 批量更新
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public async Task<int> BulkUpdateAsync(List<T> t)
        {
            return await Context.Fastest<T>().BulkUpdateAsync(t);
        }

        /// <summary>
        /// 批量有则更新无则插入
        /// </summary>
        /// <param name="t">数据表</param>
        /// <returns></returns>
        public async Task<int> BulkMergeAsync(List<T> t)
        {
            return await Context.Fastest<T>().BulkMergeAsync(t);
        }

        /// <summary>
        /// 有则更新无则插入
        /// </summary>
        /// <param name="model"></param>
        /// <returns>返回插入数量和更新属性</returns>
        public int AddOrUpdate(T model)
        {
            var storageableResult = Context.Storageable(model).ExecuteCommand();
            return storageableResult;
        }

        /// <summary>
        /// 有则更新无则插入
        /// </summary>
        /// <param name="model"></param>
        /// <returns>返回插入数量和更新属性</returns>
        public int AddOrUpdate(List<T> model)
        {
            var storageableResult = Context.Storageable(model).ExecuteCommand();
            return storageableResult;
        }

        /// <summary>
        /// 更新指定列
        /// </summary>
        /// <param name="models"></param>
        /// <param name="setColumns"></param>
        /// <returns></returns>
        public bool UpdateRange(IEnumerable<T> models, Expression<Func<T, T>> setColumns)
        {
            return Context.Updateable(models.ToList()).SetColumns(setColumns).ExecuteCommand() > 0;
        }

        /// <summary>
        /// 更新指定列
        /// </summary>
        /// <param name="models"></param>
        /// <param name="setColumns"></param>
        /// <returns></returns>
        public int UpdateRangeReturnCommand(IEnumerable<T> models, Expression<Func<T, T>> setColumns)
        {
            return Context.Updateable(models.ToList()).SetColumns(setColumns).ExecuteCommand();
        }

        /// <summary>
        /// 更新忽略NUll列，返回影响行数
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        public int UpdateWithOutNullReturnCommand(T model)
        {
            return Context.Updateable(model).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommand();
        }

        /// <summary>
        /// 根据条件加载树形结构
        /// </summary>
        /// <param name="whereExpression">where条件</param>
        /// <param name="childListExpression">子节点结构</param>
        /// <param name="parentIdExpression">父节点字段</param>
        /// <param name="rootValue">父节点条件</param>
        /// <returns></returns>
        public async Task<List<T>> GetListTreeAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, IEnumerable<object>>> childListExpression,
            Expression<Func<T, object>> parentIdExpression, object rootValue)
        {
            return await Context.Queryable<T>().Where(whereExpression).ToTreeAsync(childListExpression, parentIdExpression, rootValue);
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
        public async Task<List<T>> GetListTreeAsync(Expression<Func<T, bool>> whereExpression, Expression<Func<T, IEnumerable<object>>> childListExpression,
            Expression<Func<T, object>> parentIdExpression, Expression<Func<T, object>> orderExpression, OrderByType orderByType, object rootValue)
        {
            return await Context.Queryable<T>().Where(whereExpression).OrderBy(orderExpression, orderByType).ToTreeAsync(childListExpression, parentIdExpression, rootValue);
        }


        /// <summary>
        /// 加载树形结构
        /// </summary>
        /// <param name="childListExpression">子节点结构</param>
        /// <param name="parentIdExpression">父节点字段</param>
        /// <param name="rootValue">父节点条件</param>
        /// <returns></returns>
        public async Task<List<T>> GetListTreeAsync(Expression<Func<T, IEnumerable<object>>> childListExpression, Expression<Func<T, object>> parentIdExpression, object rootValue)
        {
            return await Context.Queryable<T>().ToTreeAsync(childListExpression, parentIdExpression, rootValue);
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
            return await Context.Queryable<T>().Where(whereExpression).ToChildListAsync(parentIdExpression, primaryKeyValue, isContainOneself);
        }

        /// <summary>
        /// 根据父节点ID获取所有子节点列表
        /// </summary>
        /// <param name="parentIdExpression"></param>
        /// <param name="primaryKeyValue"></param>
        /// <returns></returns>
        public async Task<List<T>> GetListChildAsync(Expression<Func<T, object>> parentIdExpression, object primaryKeyValue)
        {
            return await Context.Queryable<T>().ToChildListAsync(parentIdExpression, primaryKeyValue);
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
            return await Context.Queryable<T>().Where(whereExpression).ToParentListAsync(parentIdExpression, primaryKeyValue);
        }

        /// <summary>
        /// 根据父子点ID获取所有父节点列表
        /// </summary>
        /// <param name="parentIdExpression"></param>
        /// <param name="primaryKeyValue"></param>
        /// <returns></returns>
        public async Task<List<T>> GetParentListAsync(Expression<Func<T, object>> parentIdExpression, object primaryKeyValue)
        {
            return await Context.Queryable<T>().ToParentListAsync(parentIdExpression, primaryKeyValue);
        }
    }
}