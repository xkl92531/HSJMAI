using MAF.DEMO.AppLayer.Auth.ViewObject;
using MAF.DEMO.Core;
using MAF.DEMO.Core.Const;
using MAF.DEMO.IDbService;
using MAF.DEMO.IDbService.IService.System;
using MAF.DEMO.Utility;
using MAF.DEMO.Utility.JWTHelper;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.AppLayer.Auth
{
    /// <summary>
    /// 授权
    /// </summary>
    public class AuthApp : ISelfScopedAutoInject
    {
        private readonly ISysUserService userService;
        private readonly ISysRoleUsersService roleUsersService;
        private readonly ILoginLogService loginLogService;
        private readonly IHttpContextAccessor httpContext;

        public AuthApp(ISysUserService userService, ISysRoleUsersService roleUsersService, ILoginLogService loginLogService, IHttpContextAccessor httpContext)
        {
            this.userService = userService;
            this.roleUsersService = roleUsersService;
            this.loginLogService = loginLogService;
            this.httpContext = httpContext;
        }

        /// <summary>
        /// 刷新TOKEN
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<RefreshTokenRspView>> RefreshTokenAsync(RefreshTokenView dto)
        {
            // 通过刷新token 和老token来进行刷新
            // 刷新TOKEN验证是否有效，
            // 老TOKEN中获取账户信息，
            // 创建新TOKEN返回
            HttpResponseResultModel<RefreshTokenRspView> rsp = new HttpResponseResultModel<RefreshTokenRspView>();

            // 校验
            var cacheRefreshToken = CommonRedisHelper.GetString($"refreshToken:{dto.RefreshToken}");
            if (cacheRefreshToken != dto.RefreshToken)
            {
                rsp.HttpStatusCode = HttpStatusCode.Unauthorized;
                rsp.ErrorMessage = "刷新TOKEN过期，请重新登录";
                return rsp;
            }
            var payload = JWTEncryption.Decrypt(dto.Token);
            Dictionary<string, object> newPayload = new Dictionary<string, object>();
            foreach (var item in payload)
            {
                if (item.Key.StartsWith("CLAINM"))
                {
                    newPayload.Add(item.Key, item.Value);
                }
            }
            var result = new RefreshTokenRspView();
            var newToken = JWTEncryption.Encrypt(newPayload);
            rsp.IsSuccess = true;
            result.Token = newToken;
            result.ExpiredTime = AppsettingsConfig.JWTConfig.ExpiresMinutes * 60;
            rsp.BackResult = result;
            return rsp;

        }


        /// <summary>
        /// 登录
        /// </summary>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<LoginResultView>> Login(LoginInfoView dto)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            HttpResponseResultModel<LoginResultView> result = new HttpResponseResultModel<LoginResultView>() { IsSuccess = false };
            if (dto == null)
            {
                result.ErrorMessage = "参数不能为空！";
                return result;
            }

            if (string.IsNullOrWhiteSpace(dto.Account))
            {
                result.ErrorMessage = "用户名不能为空！";
                return result;
            }
            if (string.IsNullOrWhiteSpace(dto.Password))
            {
                result.ErrorMessage = "密码不能为空！";
                return result;
            }
            var md5Password = EncryptDecryptHelper.Md5(dto.Password);
            var userEntity = await userService.GetSingleAsync(it => it.Account == dto.Account.Trim() && it.Password == md5Password);
            if (userEntity == null)
            {
                result.ErrorMessage = "用户名或密码不正确！";
                return result;
            }
            stopwatch.Stop();
            Console.WriteLine("登录耗时1：" + stopwatch.ElapsedMilliseconds);
            stopwatch.Restart();
            if (userEntity.Status == DataDictionary.Code.Status.停用)
            {
                result.ErrorMessage = "当前用户已停用！";
                return result;
            }

            if (userEntity.AdminType != (int)AccountTypeEnum.SuperAdministrator)
            {
                //验证是否有权限登录系统
                var ls = await roleUsersService.GetDb().AsQueryable().InnerJoin<SysRoleEntity>((user, role) => user.IsDeleted == false && role.IsDeleted == false && role.Id == user.RoleID && role.Status == DataDictionary.Code.Status.正常)
                      .Where(user => user.UserID == userEntity.Id).ToListAsync();
                if (ls.Count <= 0)
                {
                    result.ErrorMessage = "您没有权限登录此系统，请联系管理员！";
                    return result;
                }
            }
            stopwatch.Stop();
            Console.WriteLine("登录耗时2：" + stopwatch.ElapsedMilliseconds);
            stopwatch.Restart();
            userEntity.LastLoginIp = CommonHttpContext.Current.Connection.RemoteIpAddress.MapToIPv4().ToString();
            userEntity.LastLoginTime = DateTime.Now;
            await userService.UpdateAsync(userEntity);

            LoginLogEntity loginLogEntity = new LoginLogEntity();
            loginLogEntity.Account = userEntity.Account;
            loginLogEntity.LoginDate = DateTime.Now;
            var ip = httpContext.HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (string.IsNullOrEmpty(ip))
            {
                ip = httpContext.HttpContext.Connection.RemoteIpAddress.ToString();
            }
            if (httpContext.HttpContext.Request.Headers.ContainsKey("User-Agent"))
            {
                loginLogEntity.OSVersion = httpContext.HttpContext.Request.Headers["User-Agent"].ToString();
            }

            loginLogEntity.LoginIP = ip;
            loginLogEntity.UserID = userEntity.Id;
            loginLogEntity.CreaterName = userEntity.Name;
            loginLogEntity.CreaterId = userEntity.Id;
            loginLogEntity.CreateTime = DateTime.Now;
            loginLogEntity.ModifierId = userEntity.Id;
            loginLogEntity.ModifierName = userEntity.Name;
            loginLogEntity.ModifyTime = DateTime.Now;
            await loginLogService.InsertAsync(loginLogEntity);
            stopwatch.Stop();
            Console.WriteLine("登录耗时3：" + stopwatch.ElapsedMilliseconds);
            stopwatch.Restart();
            var payload = new Dictionary<string, object>
            {
                {ClaimConst.CLAINM_USERID, userEntity.Id},
                {ClaimConst.CLAINM_ACCOUNT, userEntity.Account},
                {ClaimConst.CLAINM_NAME, userEntity.Name},
                {ClaimConst.CLAINM_SUPERADMIN, userEntity.AdminType},
            };
            var token = JWTEncryption.Encrypt(payload);
            // 生成刷新TOKEN 保留缓冲区7天
            var refreshToken = Guid.NewGuid().ToString();

            CommonRedisHelper.StringSet("refreshToken:" + refreshToken, refreshToken, 86400 * 30);
            LoginResultView view = new LoginResultView();
            view.RefreshToken = refreshToken;
            view.Account = userEntity.Account;
            view.EmpName = userEntity.Name;
            view.ExpiredTime = AppsettingsConfig.JWTConfig.ExpiresMinutes * 60;
            view.HeadUrl = userEntity.HeadUrl;
            view.Token = token;
            result.BackResult = view;
            result.IsSuccess = true;
            return result;
        }
    }
}
