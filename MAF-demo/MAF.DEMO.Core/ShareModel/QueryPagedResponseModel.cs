using System.Collections.Generic;

namespace MAF.DEMO.Core
{
    /// <summary>
    /// 分页查询结果类
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class QueryPagedResponseModel<T>
    {
        /// <summary>
        /// 数据总数
        /// </summary>
        public int Total { get; set; }
        /// <summary>
        /// 当前分页数据
        /// </summary>
        public List<T> Data { get; set; }
    }
}
