using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.Core
{
    /// <summary>
    /// 
    /// </summary>
    public interface IQueryBaseRepository
    {
        /// <summary>
        /// 获得单条数据
        /// </summary>
        /// <typeparam name="TPrimaryKeyType"></typeparam>
        /// <param name="id"></param>
        /// <returns></returns>
        Task<T> GetAsync<T, TPrimaryKeyType>(TPrimaryKeyType id);

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TPrimaryKeyType"></typeparam>
        /// <param name="ids"></param>
        /// <returns></returns>
        Task<List<T>> GetListByIdsAsync<T, TPrimaryKeyType>(List<TPrimaryKeyType> ids);

        /// <summary>
        /// 根据某个唯一字段列获取单条数据(唯一值)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="TColunmValue"></typeparam>
        /// <param name="column"></param>
        /// <returns></returns>
        Task<T> GetSingleAsync<T, TColunmValue>(KeyValuePair<string, TColunmValue> column);

        

        /// <summary>
        /// 根据主键是否存在记录
        /// </summary>
        Task<bool> ExistsAsync<TPrimaryKeyType>(TPrimaryKeyType id);

       






        /// <summary>
        ///  某个字段是否唯一
        /// </summary>
        /// <typeparam name="TColunmValue"></typeparam>
        /// <param name="column"></param>
        /// <returns>true  唯一  false 不唯一</returns>
        Task<bool> IsUniqueAsync<TColunmValue>(KeyValuePair<string, TColunmValue> column);
       
    }
}
