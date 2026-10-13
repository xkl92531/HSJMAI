using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.Core
{
    /// <summary>
    /// JWT 配置
    /// </summary>
    public class JWTConfig
    {
        /// <summary>
        /// 密钥
        /// </summary>
        public string SecurityKey { get; set; }
        /// <summary>
        /// 发行人
        /// </summary>
        public string Issuer { get; set; }
        /// <summary>
        /// 订阅人
        /// </summary>
        public string Audience { get; set; }
        /// <summary>
        /// 超时时间（分钟）
        /// </summary>
        public int ExpiresMinutes { get; set; } = 30;
    }
}
