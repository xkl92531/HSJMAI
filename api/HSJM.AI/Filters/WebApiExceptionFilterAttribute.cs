
using HSJM.AI.Core;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using HSJM.AI.Utility.LoggerHelper;
using NPOI.SS.Formula.Functions;
using HSJM.AI.IDbService;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using HSJM.AI.DbService;
using Magicodes.ExporterAndImporter.Core.Extension;
using Newtonsoft.Json;

namespace HSJM.AI.Filters
{
    /// <summary>
    /// api请求异常日志类
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
    public class WebApiExceptionFilterAttribute : ExceptionFilterAttribute, IActionFilter
    {
        public WebApiExceptionFilterAttribute()
        {
        }


        /// <summary>
        /// 控制器中的操作执行之前调用此方法
        /// </summary>
        /// <param name="context"></param>
        public void OnActionExecuting(ActionExecutingContext context)
        {
            context.HttpContext.Items.Add(
                "params",
                SensitiveLogDataSanitizer.SanitizeActionArguments(context.ActionArguments));
        }

        /// <summary>
        /// 控制器中的操作执行之后调用此方法
        /// </summary>
        /// <param name="context"></param>
        public void OnActionExecuted(ActionExecutedContext context)
        {
            // do nothing
        }
        /// <summary>
        /// 控制器中的操作异常调用此方法
        /// </summary>
        /// <param name="actionExecutedContext"></param>
        /// <returns></returns>
        public override Task OnExceptionAsync(ExceptionContext actionExecutedContext)
        {

            if (actionExecutedContext.Exception != null)
            {
                var service = CommonServiceProvider.ServiceProvider.GetService<IExceptionService>();
                ControllerActionDescriptor action = (ControllerActionDescriptor)actionExecutedContext.ActionDescriptor;
                Exception exception = actionExecutedContext.Exception;
                ExceptionEntity exceptionEntity = new ExceptionEntity();
                exceptionEntity.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                exceptionEntity.ExceptionSource = exception.Source;
                exceptionEntity.ExceptionMsg = exception.Message;
                exceptionEntity.ExceptionName = exception.GetType().Name;
                exceptionEntity.ExceptionTime = DateTime.Now;
                exceptionEntity.Account = CurrentUserInfo.Instance.Account;
                exceptionEntity.ParamsObj = JsonConvert.SerializeObject(actionExecutedContext.HttpContext.Items["params"]);
                exceptionEntity.ClassName = action.ControllerName;
                exceptionEntity.MethodName = action.ActionName;
                exceptionEntity.Name = action.ControllerTypeInfo.GetDisplayName();
                exceptionEntity.StackTrace = exception.StackTrace;
                service.Insert(exceptionEntity);
            }
            HttpResponseResultModel<string> result = new HttpResponseResultModel<string>();
            result.HttpStatusCode = (HttpStatusCode)actionExecutedContext.HttpContext.Response.StatusCode;
            result.IsSuccess = false;
            result.ErrorMessage = "出现异常,请稍后重试";
            result.ExceptionMessage = actionExecutedContext.Exception.ToString();
            actionExecutedContext.Result = new ObjectResult(result);
            return base.OnExceptionAsync(actionExecutedContext);
        }

        /// <summary>
        /// 写异常日志
        /// </summary>
        /// <param name="exceptionContext"></param>
        /// <returns></returns>
        private async Task WriteErrorAsync(ExceptionContext exceptionContext)
        {
            WebApiExceptionLogModel logModel = new WebApiExceptionLogModel();
            //获取Action 参数

            var items = exceptionContext.HttpContext.Items;
            logModel.ExecuteStartTime = (DateTime)items["executeStartTime"];
            logModel.ExecuteEndTime = DateTime.Now;
            IDictionary<string, object> actionArguments = null;
            if (items.ContainsKey("params"))
            {
                actionArguments = (IDictionary<string, object>)items["params"];
            }
            logModel.ActionParams = new Dictionary<string, object>(actionArguments);
            logModel.HttpRequestHeaders = SensitiveLogDataSanitizer.SanitizeHeaders(
                exceptionContext.HttpContext.Request.Headers.ToString());
            logModel.HttpRequestPath = exceptionContext.HttpContext.Request.Path;
            logModel.HttpMethod = exceptionContext.HttpContext.Request.Method;
            logModel.ActionName = ((ControllerActionDescriptor)exceptionContext.ActionDescriptor).ActionName;
            logModel.ControllerName = ((ControllerActionDescriptor)exceptionContext.ActionDescriptor).ControllerName;
            logModel.TotalSeconds = (logModel.ExecuteEndTime - logModel.ExecuteStartTime).TotalSeconds;
            logModel.ExceptionMessage = exceptionContext.Exception.ToString();
            logModel.IP = CommonHttpContext.Current.Connection.RemoteIpAddress.ToString();
            logModel.StatusCode = exceptionContext.HttpContext.Response.StatusCode;
            //// 使用MongoDB的话，WebApiExceptionLogModel 需要继承下MongoDBBaseField
            // MongoDbHelper<WebApiExceptionLogModel> helper = new MongoDbHelper<WebApiExceptionLogModel>();
            // helper.Insert(logModel);
            var json = JsonHelper.SerializeObject(logModel);
            //   WriteLogHelper.WriteLogsAsync(json, logModel.ControllerName);
            MyLogger.Logger.Error("{" + MyLogger.LogFilePath + "}_" + json, "ApiException");
        }
    }
}
