using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.Core
{
    /// <summary>
    /// 
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MyComputedColumnAttribute : MyColumnAttribute
    {
        public MyComputedColumnTypeEnum ComputedColumnType = MyComputedColumnTypeEnum.Always;

        /// <summary>
        /// 
        /// </summary>
        public MyComputedColumnAttribute() { }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="name"></param>
        public MyComputedColumnAttribute(string name) : base(name) { }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="computedColumnType"></param>
        public MyComputedColumnAttribute(MyComputedColumnTypeEnum computedColumnType)
        {
            ComputedColumnType = computedColumnType;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="name"></param>
        /// <param name="computedColumnType"></param>
        public MyComputedColumnAttribute(string name, MyComputedColumnTypeEnum computedColumnType) : base(name)
        {
            ComputedColumnType = computedColumnType;
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public enum MyComputedColumnTypeEnum
    {
        /// <summary>
        /// Always considered as a computed column
        /// </summary>
        Always,
        /// <summary>
        /// Only considered a Computed column for inserts, Updates will not consider this column to be computed
        /// </summary>
        ComputedOnInsert,
        /// <summary>
        /// Only considered a Computed column for updates, Inserts will not consider this column to be computed
        /// </summary>
        ComputedOnUpdate
    }
}
