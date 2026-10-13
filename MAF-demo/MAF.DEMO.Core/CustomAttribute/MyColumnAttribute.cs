using System;
using System.Collections.Generic;
using System.Text;

namespace MAF.DEMO.Core
{
    /// <summary>
    ///  用于NPOCO 映射，屏蔽对NPOCO的引用
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MyColumnAttribute : Attribute
    {
        /// <summary>
        ///  构造函数
        /// </summary>
        public MyColumnAttribute() { }
        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="name">列名</param>
        public MyColumnAttribute(string name) { Name = name; }
        /// <summary>
        /// 
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// 
        /// </summary>
        public bool ForceToUtc { get; set; } = true;
    }
}
