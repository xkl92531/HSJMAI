using HSJM.AI.Core;
using HSJM.AI.Core.Const;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HSJM.AI.Utility
{
    /// <summary>
    /// 当前登录用户
    /// </summary>
    public class CurrentUserInfo
    {
        /// <summary>
        /// 用户ID
        /// </summary>
        public long UserID
        {
            get
            {
                if (CommonHttpContext.Current!=null&&CommonHttpContext.Current.User?.FindFirst(ClaimConst.CLAINM_USERID) != null)
                {
                    return CommonHttpContext.Current.User.FindFirst(ClaimConst.CLAINM_USERID).Value.ToLong();
                }
                return 0;
            }
        }

        /// <summary>
        /// 用户账号
        /// </summary>
        public string Account
        {
            get
            {
                if (CommonHttpContext.Current != null && CommonHttpContext.Current.User?.FindFirst(ClaimConst.CLAINM_ACCOUNT) != null)
                {
                    return CommonHttpContext.Current.User.FindFirst(ClaimConst.CLAINM_ACCOUNT).Value.ToString();
                }
                return "";
            }
        }

        /// <summary>
        /// 当前系统ID
        /// </summary>
        public long SystemID
        {
            get
            {
                var dic = CommonHttpContext.Current.Request.Headers.FirstOrDefault(it => it.Key.Equals(ClaimConst.CLAINM_SYSTEMID)); 
                long id = 0;
                long.TryParse(dic.Value, out id);
                return id;
            }
        }

        /// <summary>
        /// 用户名称
        /// </summary>
        public string UserName
        {
            get
            {
                if (CommonHttpContext.Current != null && CommonHttpContext.Current.User?.FindFirst(ClaimConst.CLAINM_NAME) != null)
                {
                    return CommonHttpContext.Current.User.FindFirst(ClaimConst.CLAINM_NAME).Value.ToString();
                }
                return "";
            }
        }

        /// <summary>
        /// 用户名称
        /// </summary>
        public string Language
        {
            get
            {
                var dic = CommonHttpContext.Current.Request.Headers.FirstOrDefault(it => it.Key.Equals(ClaimConst.CLAINM_LANGUAGE));
                return dic.Value;
            }
        }



        /// <summary>
        /// 账号类型
        /// </summary>
        public AccountTypeEnum AccountType
        {
            get
            {
                if (CommonHttpContext.Current != null && CommonHttpContext.Current.User?.FindFirst(ClaimConst.CLAINM_SUPERADMIN) != null)
                {
                    return (AccountTypeEnum)CommonHttpContext.Current.User?.FindFirst(ClaimConst.CLAINM_SUPERADMIN).Value.ToInt();
                }
                return 0;
            }
        }

        /// <summary>
        /// 对象初始化
        /// </summary>
        public static readonly CurrentUserInfo Instance = new CurrentUserInfo();

    }
}
