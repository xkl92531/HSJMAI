using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MAF.DEMO.Core;
using MAF.DEMO.Utility;
using MAF.DEMO.Filters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.PlatformAbstractions;
using Microsoft.OpenApi.Models;
using MediatR;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.Json;
using MAF.DEMO.Utility.LoggerHelper;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using SqlSugar;
using System.Diagnostics;

namespace MAF.DEMO
{
    public class Program
    {
        static bool canCreateNew; //用来承接是否已经有一个实例在运行的布尔变量
        //限制单例运行
        static Mutex m = new Mutex(true, "MAF.DEMO", out canCreateNew);

        public static void Main(string[] args)
        {
            //if (IsExistProcess())
            //{
            //    Console.WriteLine("系统提示:对不起,该系统不允许同时运行多个实例!");
            //    return;
            //}
            InitConfig();
            //多线程异常
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            var builder = WebApplication.CreateBuilder(args);

            // 添加服务注册
            RegisterService.RegisterComponents(builder.Services, builder.Configuration);

            // Add MediatR
            builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetEntryAssembly()));

            //禁用默认ModelState行为
            builder.Services.Configure<ApiBehaviorOptions>(options =>
            {
                options.SuppressModelStateInvalidFilter = true;
            });

            //全局配置Json序列化处理
            builder.Services.AddControllers(ops =>
            {
                ops.Filters.Add(new IPFilter());
                ops.Filters.Add(new WebApiExceptionFilterAttribute());
                ops.Filters.Add(new WebApiTrackerAttribute());
            })
            .AddNewtonsoftJson(options =>
            {
                //设置时间格式
                options.SerializerSettings.DateFormatString = "yyyy'-'MM'-'dd' 'HH':'mm':'ss";
                options.SerializerSettings.ContractResolver = new CustomContractResolver();
            });

            // Add JWT Authentication
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters()
                {
                    ValidateIssuer = true,
                    ValidIssuer = AppsettingsConfig.JWTConfig.Issuer, //发行人
                    ValidateAudience = true,
                    ValidAudience = AppsettingsConfig.JWTConfig.Audience,//订阅人
                    ValidateIssuerSigningKey = true,
                    //对称加密密钥
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AppsettingsConfig.JWTConfig.SecurityKey!)),
                    ValidateLifetime = true, //验证失效时间
                    ClockSkew = TimeSpan.FromMinutes(AppsettingsConfig.JWTConfig.ExpiresMinutes), //过期时间容错值
                    RequireExpirationTime = true,
                    AudienceValidator = (audiences, securityToken, validationParameters) =>
                    {
                        return true;
                    },
                    LifetimeValidator = (notBefore, expires, securityToken, validationParameters) =>
                    {
                        //return true;
                        if (validationParameters.ValidateLifetime)
                        {
                            return expires > DateTime.UtcNow;
                        }
                        return false;
                    }
                };
            });

            // Add Swagger
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("Basic", new OpenApiInfo { Title = "基础模块API", Version = "Basic" });
                options.SwaggerDoc("System", new OpenApiInfo { Title = "系统管理", Version = "System" });
                options.SwaggerDoc("Log", new OpenApiInfo { Title = "日志管理", Version = "Log" });
                options.SwaggerDoc("AI", new OpenApiInfo { Title = "AI智能体", Version = "AI", Description = "基于Microsoft Agent Framework的多智能体示例" });
              
                var basePath = AppContext.BaseDirectory;

                //自动加载XML
                IFileProvider fileProvider = new PhysicalFileProvider(basePath);
                var contents = fileProvider.GetDirectoryContents("");
                foreach (var content in contents)
                {
                    if (content.Name.EndsWith(".xml"))
                    {
                        options.IncludeXmlComments(Path.Combine(basePath, content.Name), true);
                    }
                }

                options.AddSecurityRequirement(new OpenApiSecurityRequirement {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Id = "Bearer",
                                Type = ReferenceType.SecurityScheme
                            }
                        },
                        new List<string>()
                    }
                });

                //放置接口Auth授权按钮
                options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Description = "Value Bearer {token}",
                    Name = "Authorization",//jwt默认的参数名称
                    In = ParameterLocation.Header,//jwt默认存放Authorization信息的位置(请求头中)
                    Type = SecuritySchemeType.ApiKey
                });
            });

            builder.Services.AddHealthChecks();

            // SqlSugar配置
            StaticConfig.CompleteUpdateableFunc =
           StaticConfig.CompleteDeleteableFunc = it => //it是具体的对象Updateable<T>等是个object
           {

               //反射的方法可能多个就需要用GetMethods().Where
               var method = it.GetType().GetMethod("EnableDiffLogEvent");
               method.Invoke(it, new object[] { null });

               //技巧：
               //可以定义一个接口只要是这个接口的才走这个逻辑
               //if(db.GetType().GenericTypeArguments[0].GetInterfaces().Any(it=>it==typeof(IDiff))
               //可以根据类型写if
               //if(x.GetType().GenericTypeArguments[0] = typeof(Order)) {   }
           };


            MyLogger.AddMyLogger();

            if (!AppsettingsConfig.RedisServerUrl.IsListNullOrEmpty())
            {
                CommonRedisHelper.InitRedis(AppsettingsConfig.RedisServerUrl);
            }
            //程序启动时注册 


            // Build the app
            var app = builder.Build();

            // Configure the HTTP request pipeline
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseCors(builder =>
            {
                builder.AllowAnyOrigin();
                builder.AllowAnyHeader();
                builder.AllowAnyMethod();
            });

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            CommonServiceProvider.ServiceProvider = app.Services.CreateScope().ServiceProvider;
            app.UseStaticHttpContext();
            app.UseStaticFiles();

            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/Basic/swagger.json", "基础模块");
                options.SwaggerEndpoint("/swagger/System/swagger.json", "系统管理");
                options.SwaggerEndpoint("/swagger/Log/swagger.json", "日志管理");
                options.SwaggerEndpoint("/swagger/AI/swagger.json", "AI智能体");
            });

            app.MapHealthChecks("/health");

            // 启动应用
            try
            {
                app.RunAsync();
                Console.WriteLine("系统提示:系统运行中。。。");
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"打开REST SERVER失败:{ex.ToString()}");
            }
        }

        /// <summary>
        /// 判断当前实例是否已经运行
        /// </summary>
        /// <returns></returns>
        static bool IsExistProcess()
        {
            if (canCreateNew)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        #region 多线程异常
        /// <summary>
        /// 多线程异常
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            string str = "";
            //父方法
            StackTrace ss = new StackTrace(true);
            MethodBase mb = ss.GetFrame(1).GetMethod();

            //取得父方法命名空间    
            str += mb.DeclaringType.Namespace + ".";

            //取得父方法类名    
            str += mb.DeclaringType.Name + ".";

            //取得父方法类全名    
            str += mb.DeclaringType.FullName + ".";

            //取得父方法名    
            str += mb.Name;

            Exception ex = e.ExceptionObject as Exception;

            string exMessage = Newtonsoft.Json.JsonConvert.SerializeObject(ex, Newtonsoft.Json.Formatting.Indented);

            //记录异常日志
            WriteLogHelper.WriteLogsAsync("子线程异常[" + str + "]\r\n" + ex.Message + "\r\n INFO:" + exMessage + "\r\n\r\n", "UnhandledException");
        }
        #endregion

        private static void InitConfig()
        {
            var configuration = new ConfigurationBuilder()
                     .SetBasePath(Directory.GetCurrentDirectory())
                     .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                     .Build();
            AppsettingsConfig config = new AppsettingsConfig();
            configuration.Bind("WebConfig", config);
        }
    }
}
