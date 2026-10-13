using HSJM.AI.AppLayer.SYS.SysUser.DTOS;
using HSJM.AI.AppLayer.SYS.SysUser;
using HSJM.AI.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Auth;
using HSJM.AI.AppLayer.Auth.ViewObject;
using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;

namespace HSJM.AI.Controllers
{
    /// <summary>
    /// 授权
    /// </summary>
    [Route("Auth")]
    [ApiController]
    [DisplayName("授权")]
    public class AuthController : ControllerBase
    {
        AuthApp authApp;
        public AuthController(AuthApp authApp)
        {
            this.authApp = authApp;
        }

        /// <summary>
        /// 登录
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [Route("Login")]
        [ProducesResponseType(typeof(LoginResultView), 200)]
        [HttpPost]
        [DisplayName("登录")]
        [AllowAnonymous]
        public async Task<ActionResult> Login(LoginInfoView dto)
        {
            var result = await authApp.Login(dto).ConfigureAwait(false);
            return Ok(result);
        }


        /// <summary>
        /// 刷新TOKEN
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [Route("RefreshToken")]
        [ProducesResponseType(typeof(LoginResultView), 200)]
        [HttpPost]
        [DisplayName("刷新TOKEN")]
        [AllowAnonymous]
        public async Task<ActionResult> Login(RefreshTokenView dto)
        {
            var result = await authApp.RefreshTokenAsync(dto).ConfigureAwait(false);
            return Ok(result);
        }
    }
}
