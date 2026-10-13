using Microsoft.Extensions.DependencyInjection;
using Scrutor;
using System.Reflection;
using System.Collections.Generic;
using MAF.DEMO.Core;
using System.Linq;
using MAF.DEMO.AppLayer;
using MAF.DEMO.AppLayer.AI;
using MAF.DEMO.Utility;
using MAF.DEMO.MediatR;
using Microsoft.AspNetCore.Http;
using Polly;
using System.Net.Http;
using System;
using MAF.DEMO.DbService;
using MAF.DEMO.IDbService;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using Microsoft.Extensions.Configuration;

namespace MAF.DEMO
{
    public static class RegisterService
    {
        public static void RegisterComponents(IServiceCollection services, IConfiguration configuration)
        {
            RegisterHttpClientWithPolly(services);
            RegisterAgentServices(services, configuration);
            //services.AddHttpClient();
            services.AddScoped<RequestToHttpHelper>();
            services.AddSingleton<TPLLogger>();
           // services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddHttpContextAccessor();//单例
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
        /// 注册Microsoft Agent Framework相关服务
        /// 优先使用appsettings.json的LLMConfig配置
        /// </summary>
        public static void RegisterAgentServices(IServiceCollection services, IConfiguration configuration)
        {
            // 从配置文件读取
            var llmConfig = configuration.GetSection("WebConfig:LLMConfig");
            var baseUrl = llmConfig["BaseUrl"];
            var modelName = llmConfig["ModelName"];
            var apiKey = llmConfig["ApiKey"];
            
            // 环境变量配置（优先级更高）
            var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            var azureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

            if (!string.IsNullOrEmpty(githubToken))
            {
                // 使用GitHub Models
                services.AddSingleton<IChatClient>(sp =>
                    new OpenAI.Chat.ChatClient(
                        "gpt-4o-mini",
                        new ApiKeyCredential(githubToken),
                        new OpenAIClientOptions { Endpoint = new Uri("https://models.github.ai/inference") })
                    .AsIChatClient());
            }
            else if (!string.IsNullOrEmpty(openaiKey))
            {
                // 使用OpenAI
                services.AddSingleton<IChatClient>(sp =>
                    new OpenAI.Chat.ChatClient("gpt-4o-mini", new ApiKeyCredential(openaiKey))
                    .AsIChatClient());
            }
            else if (!string.IsNullOrEmpty(azureEndpoint) && !string.IsNullOrEmpty(azureKey))
            {
                // 使用Azure OpenAI
                var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini";
                services.AddSingleton<IChatClient>(sp =>
                    new OpenAI.Chat.ChatClient(
                        deploymentName,
                        new ApiKeyCredential(azureKey),
                        new OpenAIClientOptions { Endpoint = new Uri(azureEndpoint) })
                    .AsIChatClient());
            }
            else if (!string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(modelName))
            {
                // 使用本地vLLM服务（从配置文件读取）
                services.AddSingleton<IChatClient>(sp =>
                    new OpenAI.Chat.ChatClient(
                        modelName,
                        string.IsNullOrEmpty(apiKey) ? new ApiKeyCredential("dummy") : new ApiKeyCredential(apiKey),
                        new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
                    .AsIChatClient());
            }
            else
            {
                // 无配置，启动时会报错
                services.AddSingleton<IChatClient>(sp =>
                {
                    throw new InvalidOperationException(
                        "未配置AI服务。请在appsettings.json的WebConfig.LLMConfig中配置BaseUrl和ModelName，" +
                        "或设置以下环境变量之一:\n" +
                        "- GITHUB_TOKEN (使用GitHub Models)\n" +
                        "- OPENAI_API_KEY (使用OpenAI)\n" +
                        "- AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY (使用Azure OpenAI)");
                });
            }

            // 注册AgentDemoApp
            services.AddScoped<AgentDemoApp>();
        }
    }
}