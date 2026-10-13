using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.IDbService
{

    /// <summary>
    /// I点Q点Item
    /// </summary>
    public class IQTagItem
    {
        /// <summary>
        /// 地址
        /// </summary>
        public string Address { get; set; }
        /// <summary>
        /// 大小
        /// </summary>
        public decimal ByteSize { get; set; } = 1;
        /// <summary>
        /// 地址
        /// </summary>
        public string DataType { get; set; }
        /// <summary>
        /// 路径名称
        /// </summary>
        public List<string> TagPathNames { get; set; }
    }


    public class ManualAddTagItemsResult
    {
        /// <summary>
        /// 地址
        /// </summary>
        public string DataType { get; set; }
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool IsGood { get; set; }

        /// <summary>
        /// 是否成功
        /// </summary>
        public string ErrorMessage { get; set; }
    }
}
