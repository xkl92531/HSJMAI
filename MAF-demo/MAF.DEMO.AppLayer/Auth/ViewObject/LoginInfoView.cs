using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.AppLayer.Auth.ViewObject
{
    public class LoginInfoView
    {
        /// <summary>
        ///  账号
        /// </summary> 
        public string Account { get; set; } = "admin";

        /// <summary>
        ///  密码
        /// </summary> 
        public string Password { get; set; } = "1";
    }
}
