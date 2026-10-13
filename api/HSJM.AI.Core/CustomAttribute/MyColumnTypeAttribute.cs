using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.Core
{
    /// <summary>
    /// 
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MyColumnTypeAttribute : Attribute
    {
        public Type Type { get; set; }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="type"></param>
        public MyColumnTypeAttribute(Type type)
        {
            Type = type;
        }
    }
}
