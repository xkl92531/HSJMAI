using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.AppLayer
{
    /// <summary>
    /// View共用字段
    /// </summary>
    public class ViewBaseField
    {
        /// <summary>
        /// 添加人
        /// </summary>

        public long? CreaterId { get; set; }

        /// <summary>
        /// 添加时间
        /// </summary>      
        public DateTime? CreateTime { get; set; }

        /// <summary>
        /// 修改人
        /// </summary>
        public long? ModifierId { get; set; }

        /// <summary>
        /// 修改时间
        /// </summary>
        public DateTime? ModifyTime { get; set; }

        /// <summary>
        /// 修改人
        /// </summary>
        public string ModifierName { get; set; }

        /// <summary>
        /// 添加人
        /// </summary>
        public string CreaterName { get; set; }
    }
}
