using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.Core.CustomAttribute
{
    /// <summary>
    /// 中英文特性
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MyLanguageAttribute : Attribute
    {
        /// <summary>
        /// 中文名称
        /// </summary>
        public string CName { get; set; }
        /// <summary>
        /// 英文名称
        /// </summary>
        public string EName { get; set; }
        /// <summary>
        /// 繁体名称
        /// </summary>
        public string TCName { get; set; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cName"></param>
        /// <param name="eName"></param>
        /// <param name="tCName"></param>
        public MyLanguageAttribute(string cName, string eName, string tCName)
        {
            this.CName = cName;
            this.EName = eName;
            this.TCName = tCName;
        }
    }
}
