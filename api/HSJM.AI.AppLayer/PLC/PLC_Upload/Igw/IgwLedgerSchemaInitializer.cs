using System;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.IDbService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// 启动阶段一次性确保 IGW 注册台账表 + 项目通道名列存在，并回收软删台账行（spec §4.5，task-5 controller 决策；M-5 2026-09-20）。
    /// 两步 DDL 都是幂等的（<see cref="PLC_IgwRegistrationSchemaDdl"/> 的 CREATE TABLE/INDEX IF NOT EXISTS +
    /// ALTER TABLE ADD COLUMN IF NOT EXISTS），失败只记日志、不抛出、不阻断宿主启动——
    /// <see cref="IPLC_IgwRegistrationService"/> 自身在 ReplaceForProjectAsync/GetLedgerAsync 路径上也有懒加载
    /// 兜底（EnsureLedgerSchemaAsync，带 5 分钟重试冷却），GetChannelNameAsync/SetChannelNameAsync 遇列缺失直接
    /// 优雅降级，因此本初始化器失败不会让应用不可用，只是台账/通道名功能在修复前暂不可用。
    /// <para>
    /// task-5 fix round 2：<see cref="StartAsync"/> **不 await** 实际 DDL 检查——数据库不可达/慢时不能拖慢宿主
    /// 启动（其它接口、健康检查都等着宿主起来），改为 fire-and-forget 到后台任务，仍完整 try-catch 记日志；
    /// <c>EnsureLedgerSchemaAsync</c> 的 5 分钟冷却懒加载兜底覆盖"本次启动检查还没跑完/失败"的情况。
    /// </para>
    /// 写法照抄 <c>StationShadowRecurringJobBootstrapService</c>（同项目里另一个 IHostedService 启动初始化器）：
    /// 构造函数注入 IServiceScopeFactory，后台任务内 CreateScope 解析 scoped 服务。
    /// </summary>
    public class IgwLedgerSchemaInitializer : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<IgwLedgerSchemaInitializer> _logger;

        public IgwLedgerSchemaInitializer(IServiceScopeFactory scopeFactory, ILogger<IgwLedgerSchemaInitializer> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // 不 await：DB 不可达（网络分区/库还没起来）时不能让宿主启动一直卡在这里。fire-and-forget，
            // 内部完整 try-catch 记日志；本方法本身立即返回 CompletedTask。
            _ = RunSchemaCheckAsync();
            return Task.CompletedTask;
        }

        private async Task RunSchemaCheckAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var ledgerService = scope.ServiceProvider.GetRequiredService<IPLC_IgwRegistrationService>();

                // 两个方法内部各自 try-catch 记日志、不抛出；这里仍按顺序 await，保证补列不会抢在建表前面
                // （虽然两者操作的是不同表，互不依赖，但顺序执行更利于日志按时间顺序排查）。
                await ledgerService.EnsureLedgerSchemaAsync().ConfigureAwait(false);
                await ledgerService.EnsureProjectChannelColumnAsync().ConfigureAwait(false);

                // M-5：回收历史软删台账行（替换已改硬删；删项目级联软删的行也在这里回收）。失败只记日志
                try
                {
                    var purged = await ledgerService.PurgeSoftDeletedAsync().ConfigureAwait(false);
                    if (purged > 0)
                    {
                        _logger.LogInformation("[IGW台账] 启动清理软删台账行 {Count} 行", purged);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IGW台账] 启动清理软删台账行失败（不影响注册，下次启动重试）");
                }

                // 2026-09-20 hardening：项目级注册状态表（注册身份 / GRAPH 失败 / 待补跑），内部 try-catch 不抛出
                var stateService = scope.ServiceProvider.GetRequiredService<IPLC_IgwRegistrationStateService>();
                await stateService.EnsureSchemaAsync().ConfigureAwait(false);

                _logger.LogInformation("[IGW台账] 启动阶段建表/补列检查完成");
            }
            catch (Exception ex)
            {
                // 双重保险：EnsureLedgerSchemaAsync/EnsureProjectChannelColumnAsync 内部已各自 try-catch 记日志，
                // 这里兜住 DI 解析失败等意外，避免异常变成未观测的 Task 异常。
                _logger.LogWarning(ex, "[IGW台账] 启动阶段建表/补列检查异常（不阻断启动）");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
