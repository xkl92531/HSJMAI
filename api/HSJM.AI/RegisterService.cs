using Microsoft.Extensions.DependencyInjection;
using Scrutor;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;
using System.Collections.Generic;
using HSJM.AI.Core;
using System.Linq;
using HSJM.AI.AppLayer;
using HSJM.AI.Utility;
using HSJM.AI.MediatR;
using Microsoft.AspNetCore.Http;
using Polly;
using System.Net.Http;
using System;
using HSJM.AI.DbService;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.Agent.Services;
using Microsoft.Extensions.AI;

namespace HSJM.AI
{
    public static class RegisterService
    {
        public static void RegisterComponents(IServiceCollection services)
        {
            // 注册IChatClient
            RegisterIChatClient(services);
            services.TryAddScoped<IAiModelPurposeResolver, AiModelPurposeBindingService>();
            
            RegisterHttpClientWithPolly(services);
            //services.AddHttpClient();
            services.AddScoped<RequestToHttpHelper>();
            services.AddSingleton<TPLLogger>();
           // services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddHttpContextAccessor();//单例
            
            // 注册PLC解析工厂工具
            services.AddScoped<HSJM.AI.AppLayer.Agent.Tools.FileOperationTools>();
            services.AddScoped<HSJM.AI.AppLayer.Agent.Tools.RegexGeneratorTool>();
            services.AddScoped<HSJM.AI.AppLayer.Agent.Tools.PLCCodeAnalysisTool>();
            services.AddScoped<HSJM.AI.AppLayer.Agent.Tools.ParserGenerationTools>(); // ✅ 新增解析器生成工具
            services.AddScoped<HSJM.AI.AppLayer.Agent.Tools.PLCFileDetector>(); // ✅ PLC文件检测工具
            services.AddScoped<HSJM.AI.AppLayer.Agent.Tools.GeneratedParserTool>(); // ✅ 动态解析器执行工具
            services.AddScoped<HSJM.AI.AppLayer.Agent.Tools.FileContentAnalysisTool>(); // ✅ 文件内容解读工具
            services.AddScoped<HSJM.AI.AppLayer.Agent.Tools.ParserTemplateTools>(); // ✅ 解析器模版管理工具
            
            RegisterApp.RegisterComponents(services);
            RegisterMediatR.RegisterComponents(services);
            DbRegisterService.Register(services);
            IDbRegisterService.RegisterService(services);
            //自动注入IAutoInject
            services.Scan(x =>
            {
                var entryAssembly = Assembly.GetEntryAssembly();
                var referencedAssemblies = entryAssembly.GetReferencedAssemblies().Select(Assembly.Load);
                var assemblies = new List<Assembly> { entryAssembly }.Concat(referencedAssemblies);

                x.FromAssemblies(assemblies)
                    .AddClasses(classes => classes.AssignableTo(typeof(IAutoInject)))
                        .AsImplementedInterfaces()
                        .WithScopedLifetime()
                        //接口注册Scoped
                    .AddClasses(classes => classes.AssignableTo(typeof(IScopedAutoInject)))
                        .AsImplementedInterfaces()
                        .WithScopedLifetime()
                    //接口注册Singleton
                    .AddClasses(classes => classes.AssignableTo(typeof(ISingletonAutoInject)))
                          .AsImplementedInterfaces()
                          .WithSingletonLifetime()
                   //接口注册Transient
                    .AddClasses(classes => classes.AssignableTo(typeof(ITransientAutoInject)))
                          .AsImplementedInterfaces()
                          .WithTransientLifetime()
                    //具体类注册Scoped
                    .AddClasses(classes => classes.AssignableTo(typeof(ISelfScopedAutoInject)))
                          .AsSelf()
                          .WithScopedLifetime()
                    //具体类注册Singleton
                    .AddClasses(classes => classes.AssignableTo(typeof(ISelfSingletonAutoInject)))
                          .AsSelf()
                          .WithSingletonLifetime()
                    //具体类注册Transient
                    .AddClasses(classes => classes.AssignableTo(typeof(ISelfTransientAutoInject)))
                          .AsSelf()
                          .WithTransientLifetime();
            });
        }
        /// <summary>
        ///  http 请求注入polly，失败了，自动重试
        /// </summary>
        /// <param name="services"></param>
        public static void RegisterHttpClientWithPolly(IServiceCollection services)
        {
            // 相关文档地址
            //https://docs.microsoft.com/zh-cn/aspnet/core/fundamentals/http-requests?view=aspnetcore-5.0

            int retryTimes = GlobalConstant.HttpRequestRetryTimes;//重试2次
            int index = 1;
            int retryIntervalMilliseconds = GlobalConstant.PollyRetryIntervalMilliseconds;//重试间隔
            services.AddHttpClient(GlobalConstant.HttpClientName)
               .AddTransientHttpErrorPolicy(p => p.WaitAndRetryAsync(retryTimes, retryAttempt => TimeSpan.FromMilliseconds(retryIntervalMilliseconds), (response, timeSpan) =>
               {
                   WriteLogHelper.WriteLogsAsync($"{timeSpan.ToString()}重试第{index}次：" + response.Exception?.ToString(), "Error");
                   index++;
                   if (index > 2)
                   {
                       index = 1;
                   }
               }));
        }

        /// <summary>
        /// 每个作用域从网站用途绑定解析默认模型，新任务可读取最新绑定。
        /// </summary>
        private static void RegisterIChatClient(IServiceCollection services)
        {
            services.TryAddScoped<IChatClient>(sp =>
            {
                var binding = sp.GetRequiredService<IAiModelPurposeResolver>()
                    .ResolveAsync(AiModelPurposes.DefaultChat).GetAwaiter().GetResult();
                return sp.GetRequiredService<LLMModelService>()
                    .CreateChatClientByModelIdAsync(binding.Model.Id).GetAwaiter().GetResult()
                    .AsIChatClient();
            });
        }
    }
}
