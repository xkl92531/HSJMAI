using MAF.DEMO.Core;
using MAF.DEMO.Utility;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace MAF.DEMO.Filters
{
    /// <summary>
    /// 请求过滤器，以确保请求IP在信任列表中
    /// </summary>
    public class IPFilter : ActionFilterAttribute
    {
        /// <summary>
        /// 控制器中的操作执行之前调用此方法
        /// </summary>
        /// <param name="actionContext"></param>
        public override void OnActionExecuting(ActionExecutingContext actionContext)
         {
            var ip = actionContext.HttpContext.Connection.RemoteIpAddress.ToString().Replace("::ffff:", "");
            if (!AppsettingsConfig.AllowedIpAddresses.IsListNullOrEmpty())
            {
                if (!AppsettingsConfig.AllowedIpAddresses.Contains(ip) && !AppsettingsConfig.AllowedIpAddresses.Contains("*"))
                {
                    var req = ((Microsoft.AspNetCore.Mvc.ControllerBase)actionContext.Controller).Request;
                    HttpResponseResultModel<string> response = new HttpResponseResultModel<string>()
                    {
                        IsSuccess = false,
                        ErrorMessage = "请求来源非法",
                        HttpStatusCode = HttpStatusCode.Forbidden
                    };
                    WriteLogHelper.WriteLogsAsync($"ip:{ip},path:{req.Path},param:{JsonHelper.SerializeObject(actionContext.ActionArguments)}", "非法请求");
                    actionContext.Result = new ObjectResult(response);

                }
            }
            base.OnActionExecuting(actionContext);
        }
    }
}
