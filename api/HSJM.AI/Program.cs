using Hangfire;
using Hangfire.PostgreSql;
using HSJM.AI.AppLayer.Hubs;
using HSJM.AI.AppLayer.LineUp.Runtime;
using HSJM.AI.AppLayer.PLC.AlarmMonitor;
using HSJM.AI.AppLayer.PLC.CylinderSignalEvent;
using HSJM.AI.AppLayer.PLC.DwEtl;
using HSJM.AI.AppLayer.PLC.JobCenter;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using HSJM.AI.Health;
using HSJM.AI.Mcp;
using HSJM.AI.Utility;
using HSJM.AI.Utility.LoggerHelper;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.PlatformAbstractions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using ModelContextProtocol.Protocol;
using MongoDB.Driver.Core.Configuration;
using Npgsql;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
namespace HSJM.AI
{
    public class Program
    {
        static bool canCreateNew; //用来承接是否已经有一个实例在运行的布尔变量
        //限制单例运行
        static Mutex m = new Mutex(true, "HSJM.AI", out canCreateNew);

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
            RegisterService.RegisterComponents(builder.Services);

            // IGW 注册台账建表/补列（spec §4.5，task-5 fix round 1）：幂等 DDL，失败只记日志不阻断启动，
            // 不依赖 BackgroundJobs 开关（纯本地 DDL 检查，不产生 Hangfire 任务/外部 I/O）。
            builder.Services.AddHostedService<IgwLedgerSchemaInitializer>();

            // 2026-09-23 工位信号历史表查询索引（停机诊断 GRAPH 兜底路径全表扫描 34~44 秒）：
            // 后台 CREATE INDEX CONCURRENTLY，失败只记日志不阻断启动
            builder.Services.AddHostedService<HSJM.AI.AppLayer.StopCauseDiagnosis.Services.StationSignalHistoryIndexInitializer>();

            // Add MediatR
            builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetEntryAssembly()));

            builder.Services.AddSingleton<IMcpInvocationLogSink, McpInvocationFileLogSink>();
            builder.Services.AddSingleton<McpInvocationLogger>();

            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation
                    {
                        Name = "hsjm-diagnosis",
                        Title = "设备诊断平台",
                        Version = "2.0.0",
                    };
                })
                .WithHttpTransport(options =>
                {
                    options.Stateless = true;
                })
                .WithTools<McpDiagnosisTools>();

            if (builder.Configuration.GetValue("McpDiagnosisRegistration:Enabled", false))
            {
                builder.Services.AddHostedService<McpDiagnosisRegistrationHostedService>();
            }

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
                //雪花 ID（超过 JS Number 安全整数）序列化为字符串，防止前端 JSON.parse 丢精度后回传失真
                options.SerializerSettings.Converters.Add(new HSJM.AI.Core.LongSnowflakeSafeJsonConverter());
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
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"].ToString();
                        if (!string.IsNullOrWhiteSpace(accessToken) &&
                            context.HttpContext.Request.Path.StartsWithSegments("/hubs/lineup-monitor"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("McpAccess", policy => policy.RequireAssertion(context =>
                    context.User.Identity?.IsAuthenticated == true
                    || (context.Resource is HttpContext httpContext
                        && httpContext.Connection.RemoteIpAddress is { } remoteAddress
                        && IPAddress.IsLoopback(remoteAddress))));
            });

            // Add Swagger
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("Basic", new OpenApiInfo { Title = "基础模块API", Version = "Basic" });
                options.SwaggerDoc("AgentTest", new OpenApiInfo { Title = "智能体测试", Version = "AgentTest" });
                options.SwaggerDoc("AI_PLC", new OpenApiInfo { Title = "PLC解析", Version = "AI_PLC" });
                options.SwaggerDoc("System", new OpenApiInfo { Title = "系统管理", Version = "System" });
                options.SwaggerDoc("DiagnosticStrategyFactory", new OpenApiInfo { Title = "策略诊断工厂", Version = "DiagnosticStrategyFactory" });
                options.SwaggerDoc("Log", new OpenApiInfo { Title = "日志管理", Version = "Log" });
                options.SwaggerDoc("PLC", new OpenApiInfo { Title = "AI PLC解析", Version = "PLC" });
                options.SwaggerDoc("LineUp", new OpenApiInfo { Title = "设备运行监控与 GRAPH 导入", Version = "LineUp" });
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

            var backgroundJobsEnabled = builder.Configuration.GetValue(
                "WebConfig:BackgroundJobs:Enabled", true);
            if (backgroundJobsEnabled)
            {
                builder.Services.AddHostedService<StationShadowRecurringJobBootstrapService>();
                // M-1（2026-09-20）：进程重启后恢复本机队列的 IGW 注册待补跑范围（只在后台任务开启时，避免开发机入队无人消费的任务）
                builder.Services.AddHostedService<IgwPendingRegistrationRecoveryService>();
                // 2026-09-14: 屏蔽自研 JobCenter 调度 worker（用户拍板：确认不再启用）。
                // 依据：两库（hsjm_ai_new / hsjm_ai_new_test）唯一任务 cylinder_signal_collector_main
                // 已于 2026-07-21 软删 + Enabled=false（末次运行 2026-06-01），零活跃任务，
                // worker 每 100ms 空轮询 basic_systemparameter（每进程每天 ~86 万次空查询）。
                // PlcJobCenterController 管理 API 保留（Authorize 保护，不自动执行）；
                // 若将来需要恢复自动调度，取消下面这行注释即可（任务定义存 basic_systemparameter，
                // ParamCode 前缀 PLC_JOB_CENTER_TASK:，复查脚本 tools/check_jobcenter_state.py）。
                // builder.Services.AddHostedService<PlcJobCenterBackgroundWorker>();
                // dw ETL 每日调度（dw-etl-daily，01:30，必须早于 02:00 的保留清理任务——详见该类注释）。
                // 队列锁：入本机 local_<机器名> 队列（HangfireQueueNames 在主项目，AppLayer 不可见，故工厂注入）
                builder.Services.AddHostedService(_ => new DwEtlRecurringJobBootstrapService(HangfireQueueNames.CurrentServiceQueue));
            }
            builder.Services.AddSingleton<LineUpMonitorBroadcastService>();
            builder.Services.AddSingleton<ILineUpMonitorDeltaPublisher>(serviceProvider =>
                serviceProvider.GetRequiredService<LineUpMonitorBroadcastService>());
            builder.Services.AddSingleton<ILineUpMonitorBroadcastPump>(serviceProvider =>
                serviceProvider.GetRequiredService<LineUpMonitorBroadcastService>());
            builder.Services.AddHostedService<LineUpMonitorBackgroundService>();

            // ==================== 高频影子监控 Worker ====================
            var workerSection = builder.Configuration.GetSection("StationShadowWorker");
            var workerConfig = new StationShadowWorkerConfig();
            workerSection.Bind(workerConfig);
            StationShadowWorkerConfig.Current = workerConfig;
            builder.Services.AddHostedService<StationShadowBackgroundWorker>();

            // ==================== 报警监测 Worker（报警变量台账 → 故障起止事件 → FaultEventHub 推送小锦） ====================
            var alarmMonitorSection = builder.Configuration.GetSection("AlarmMonitor");
            var alarmMonitorConfig = new AlarmMonitorWorkerConfig();
            alarmMonitorSection.Bind(alarmMonitorConfig);
            AlarmMonitorWorkerConfig.Current = alarmMonitorConfig;
            builder.Services.AddHostedService<AlarmMonitorWorker>();

            builder.Services.AddSingleton<IRedisHealthProbe, RedisHealthProbe>();
            builder.Services.AddHealthChecks()
                .AddCheck<ExternalPostgresHealthCheck>("postgresql")
                .AddCheck<ExternalRedisHealthCheck>("redis");

            // ==================== HttpClient 配置 ====================
            // LLM 健康检查专用 HttpClient（超时5秒）
            builder.Services.AddHttpClient("LLMHealthCheck", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Add("User-Agent", "HSJM-AI-HealthCheck/1.0");
            });


            // ==================== Hangfire 后台任务框架 ====================

            // 2. 配置 PostgreSQL 存储选项（核心：直接用 PostgreSqlStorageOptions）
            var postgreSqlOptions = new PostgreSqlStorageOptions
            {
                // 核心配置项（所有版本都支持）
                SchemaName = "hangfire", // 自定义架构名
                QueuePollInterval = TimeSpan.FromSeconds(5), // 队列轮询间隔
                PrepareSchemaIfNecessary = builder.Configuration.GetValue("WebConfig:Hangfire:PrepareSchemaIfNecessary", false),
                UseNativeDatabaseTransactions = true, // 启用原生事务（替代 DisableGlobalLocks）
                // PLC 文件上传解析任务可能耗时数十分钟，必须大于最长任务执行时间
                // 否则 Hangfire 会认为 worker 挂死而重新入队导致重复执行
                InvisibilityTimeout = TimeSpan.FromHours(2),
                // 允许 Hangfire 等待最多 30 秒来获取分布式锁，避免多工位并发注册时锁超时报错
                DistributedLockTimeout = TimeSpan.FromSeconds(30),
            };

            builder.Services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(options =>
                {
                    options.UseNpgsqlConnection(AppsettingsConfig.DBList.First(x => x.ConfigId == "DefultDataBase").ConnectionString);
                }, postgreSqlOptions));
            //.UseSqlServerStorage(AppsettingsConfig.DBList.First().ConnectionString, new Hangfire.SqlServer.SqlServerStorageOptions
            //{
            //    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            //    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            //    QueuePollInterval = TimeSpan.Zero,
            //    UseRecommendedIsolationLevel = true,
            //    DisableGlobalLocks = true
            //}));

            // 启动 Hangfire Server
            if (backgroundJobsEnabled)
            {
                // LocalOnlyQueues（WebConfig:Hangfire:LocalOnlyQueues=true，2026-09-07 新增）：
                // 本机开发后端与其他部署共用同一 Hangfire 库（如 hsjm_ai_new_test）时，
                // 只监听本机 local_<machine> 队列——既保证本机自己入队的任务（PLC上传等）有人执行，
                // 又不会抢共享 default/critical/low 队列里其他机器/部署（如 6.75 测试容器）的任务。
                var localOnlyQueues = builder.Configuration.GetValue("WebConfig:Hangfire:LocalOnlyQueues", false);
                builder.Services.AddHangfireServer(options =>
                {
                    options.WorkerCount = 20; // 并发 Worker 数量
                    options.Queues = localOnlyQueues
                        ? new[] { HangfireQueueNames.CurrentServiceQueue }
                        : HangfireQueueNames.BuildServerQueues(); // 队列优先级 + 当前服务本机队列
                    options.ServerName = $"HSJM-AI-Worker-{Environment.MachineName}";
                });
            }

            // ==================== SignalR 实时通信 ====================
            builder.Services.AddSignalR(options =>
            {
                options.EnableDetailedErrors = builder.Environment.IsDevelopment(); // 开发环境启用详细错误
                options.MaximumReceiveMessageSize = 102400; // 100KB
                options.StreamBufferCapacity = 10;
            })
            .AddJsonProtocol(options =>
            {
                // 解决中文被转义为 \uXXXX 的问题
                options.PayloadSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
                // 确保 SignalR 消息属性名使用 camelCase（与前端 JS 约定一致）
                options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                //雪花 ID（超过 JS Number 安全整数）输出字符串，防止前端丢精度
                options.PayloadSerializerOptions.Converters.Add(new HSJM.AI.Core.LongSnowflakeSafeSystemTextJsonConverter());
            });

            // 注册服务
            RegisterService.RegisterComponents(builder.Services);
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
            //Npgsql
            if (StaticConfig.AppContext_ConvertInfinityDateTime == false)
            {
                AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
                AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);
            }
            //如果不想用默认的可以把
            //StaticConfig.AppContext_ConvertInfinityDateTime = true;//这样就不走ORM默认

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
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseStaticHttpContext();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();
            app.MapMcp("/mcp").RequireAuthorization("McpAccess");

            // ==================== Hangfire Dashboard ====================
            // 2026-09-14: 由 MapHangfireDashboard（endpoint 风格）改为 UseHangfireDashboard（中间件风格）。
            // 原因：.NET 10 下 endpoint 风格的所有子路径（/hangfire/succeeded、/hangfire/failed 等）抛
            // "The request reached the end of the pipeline without executing the endpoint"
            // （AspNetCoreDashboardMiddleware 内部子管道在 net10 ApplicationBuilder 下无终结点，预存 bug，
            // 首页 /hangfire 因直接命中不触发）。中间件风格自管路径匹配短路，不受影响。
            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                Authorization = new[] { new HangfireAuthorizationFilter() },
                DashboardTitle = "设备诊断平台 - 任务监控",
                StatsPollingInterval = 2000, // 2秒刷新一次统计
                DisplayStorageConnectionString = false
            });

            // ==================== SignalR Hubs ====================
            app.MapHub<AgentStreamingHub>("/hubs/agent-streaming");
            app.MapHub<WorkflowHub>("/hubs/workflow");
            app.MapHub<StationShadowMonitoringHub>("/hubs/station-shadow-monitoring");
            app.MapHub<FaultEventHub>("/hubs/fault-event");
            app.MapHub<LineUpMonitorHub>("/hubs/lineup-monitor").RequireAuthorization();

            CommonServiceProvider.ServiceProvider = app.Services.CreateScope().ServiceProvider;
     

            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/Basic/swagger.json", "基础模块");
                options.SwaggerEndpoint("/swagger/AgentTest/swagger.json", "智能体测试");
                options.SwaggerEndpoint("/swagger/AI_PLC/swagger.json", "PLC解析");
                options.SwaggerEndpoint("/swagger/System/swagger.json", "系统管理");
                options.SwaggerEndpoint("/swagger/DiagnosticStrategyFactory/swagger.json", "策略诊断工厂");
                options.SwaggerEndpoint("/swagger/Log/swagger.json", "日志管理");
                options.SwaggerEndpoint("/swagger/PLC/swagger.json", "AI PLC");
                options.SwaggerEndpoint("/swagger/LineUp/swagger.json", "设备运行监控");

            });

            app.MapHealthChecks("/live", new HealthCheckOptions
            {
                Predicate = _ => false,
            });
            app.MapHealthChecks("/health");
            app.MapFallbackToFile("index.html");

            // 启动应用
            try
            {
                Console.WriteLine("系统提示:系统运行中。。。");
                app.Run();
               // app.RunAsync();
               // Console.ReadKey();
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
            System.Diagnostics.StackTrace ss = new System.Diagnostics.StackTrace(true);
            System.Reflection.MethodBase mb = ss.GetFrame(1).GetMethod();

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
            var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                                  ?? Environments.Production;
            var configuration = new ConfigurationBuilder()
                     .SetBasePath(Directory.GetCurrentDirectory())
                     .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                     .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true)
                     .AddJsonFile("appsettings.AIAgent.json", optional: true, reloadOnChange: true)
                     .AddEnvironmentVariables()
                     .Build();
            AppsettingsConfig config = new AppsettingsConfig();
            configuration.Bind("WebConfig", config);

            // 绑定 AIAgent 配置
            var aiAgentConfig = new AIAgentConfig();
            configuration.GetSection("WebConfig:AIAgent").Bind(aiAgentConfig);
            AppsettingsConfig.AIAgent = aiAgentConfig;

            Console.WriteLine($"✅ AI Agent 非模型配置已加载 - 最大迭代: {aiAgentConfig.MaxIterations}, 超时: {aiAgentConfig.TimeoutSeconds}s");
        }
    }
}
