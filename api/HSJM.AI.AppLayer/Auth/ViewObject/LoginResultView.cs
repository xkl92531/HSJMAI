using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Auth.ViewObject
{
    public class RefreshTokenRspView
    {
        public string Token { set; get; }
        public long ExpiredTime { get; set; } = 3600;
    }

    public class RefreshTokenView
    {
        public string RefreshToken { set; get; }
        public string Token { set; get; }
    }

    /// <summary>
    /// 返回结果
    /// </summary>
    public class LoginResultView
    {
        /// <summary>
        /// 刷新TOKEN
        /// </summary>
        public string RefreshToken { set; get; }
        /// <summary>
        /// 令牌
        /// </summary>
        public string Token { get; set; }
        /// <summary>
        /// 头像
        /// </summary>
        public string HeadUrl { get; set; }
        /// <summary>
        /// 姓名
        /// </summary>
        public string EmpName { get; set; }
        /// <summary>
        /// 昵称
        /// </summary>
        public string Nickname { get; set; }
        /// <summary>
        /// 账号
        /// </summary>
        public string Account { get; set; }
        /// <summary>
        /// 有效时间（秒）
        /// </summary>
        public int ExpiredTime { get; set; } = 3600;
    }
}
