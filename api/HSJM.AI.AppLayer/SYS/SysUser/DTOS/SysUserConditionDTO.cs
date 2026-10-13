using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.SYS.SysUser.DTOS
{
    /// <summary>
    /// 用户账号查询条件
    /// </summary>
    public class SysUserConditionDTO
    {
        /// <summary>
        ///  账号
        /// </summary> 
        public string Account { get; set; }
        /// <summary>
        ///  姓名
        /// </summary> 
        public string Name { get; set; } 

    }
}
