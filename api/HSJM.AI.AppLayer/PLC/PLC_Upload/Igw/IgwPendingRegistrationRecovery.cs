using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>M-1：把状态表里属于本机队列的待补跑范围重新入队（纯编排，依赖注入的入队委托，便于单测）</summary>
    public static class IgwPendingRegistrationRecovery
    {
        /// <param name="stateService">注册状态表服务</param>
        /// <param name="queue">本机队列名（只恢复该队列的记录）</param>
        /// <param name="enqueue">入队委托</param>
        /// <param name="logger">日志</param>
        /// <param name="hasQueuedJob">该项目在 Hangfire 里已有排队 / 执行中的注册任务（null = 不检查，一律入队）</param>
        /// <param name="handToQueuedJob">把恢复出的范围交给该已排队任务（闸门结转 / 待补跑；记录保留到它成功后清除）</param>
        /// <param name="persistExtras">
        /// 入队后更新记录（生产：PLC_IgwRegistrationJob.PersistExtrasAsync 在项目落库锁内写最新快照 / 条件清除，
        /// 避免与刚开始执行的任务并发落库时误清）；null → 直接条件清除本队列记录
        /// </param>
        /// <param name="cancellationToken">宿主停止时取消（逐项目检查，已开始的单个项目会做完）</param>
        /// <returns>成功入队（或并入执行中 / 交给已排队任务）的项目数</returns>
        public static async Task<int> RecoverAsync(IPLC_IgwRegistrationStateService stateService, string queue,
            Func<long, IgwRegistrationArgs, Task<IgwEnqueueResult>> enqueue, ILogger logger,
            Func<long, bool> hasQueuedJob = null, Action<long, IgwRegistrationArgs> handToQueuedJob = null,
            Func<long, Task> persistExtras = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await stateService.ListPendingAsync(queue).ConfigureAwait(false);
            var recovered = 0;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var args = IgwPendingArgsCodec.Deserialize(row.PendingArgsJson);
                    if (args == null)
                    {
                        logger.LogWarning("[IGW注册] 项目 {ProjectId} 待补跑范围无法解析，已清除：{Json}", row.ProjectId, row.PendingArgsJson);
                        await stateService.ClearPendingAsync(row.ProjectId, queue).ConfigureAwait(false);
                        continue;
                    }
                    if (hasQueuedJob != null && handToQueuedJob != null && hasQueuedJob(row.ProjectId))
                    {
                        // 重启前已入队的同项目任务会在本机重新执行：交给它（开始执行时并入），不另起一个重复任务
                        handToQueuedJob(row.ProjectId, args);
                        recovered++;
                        logger.LogInformation("[IGW注册] 项目 {ProjectId} 已有排队中的注册任务，待补跑范围交由其一并执行", row.ProjectId);
                        continue;
                    }
                    var result = await enqueue(row.ProjectId, args).ConfigureAwait(false);
                    if (result == IgwEnqueueResult.Enqueued)
                    {
                        // 新任务的 Hangfire 参数已持久化该范围；并入执行中任务时由其自行落库，此处不清
                        if (persistExtras != null)
                        {
                            await persistExtras(row.ProjectId).ConfigureAwait(false);
                        }
                        else
                        {
                            await stateService.ClearPendingAsync(row.ProjectId, queue).ConfigureAwait(false);
                        }
                    }
                    recovered++;
                    logger.LogInformation("[IGW注册] 项目 {ProjectId} 已恢复进程重启前的待补跑注册（{Result}）", row.ProjectId, result);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[IGW注册] 项目 {ProjectId} 恢复待补跑注册失败（保留记录，下次启动重试）", row.ProjectId);
                }
            }
            return recovered;
        }
    }

    /// <summary>
    /// 启动时恢复待补跑注册（仅 WebConfig:BackgroundJobs:Enabled=true 时注册，见 Program.cs）。后台执行、不阻塞启动，
    /// 延迟 15 秒等 Hangfire 存储就绪；只处理本机队列（local_&lt;机器名&gt;）的记录，其它机器的记录留给它们自己。
    /// </summary>
    public sealed class IgwPendingRegistrationRecoveryService : IHostedService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<IgwPendingRegistrationRecoveryService> _logger;
        private readonly TimeSpan _startupDelay;
        private readonly CancellationTokenSource _stopping = new();
        private Task _running = Task.CompletedTask;
        /// <summary>1 = _stopping 已释放（StopAsync 可重复调用：释放后再调直接返回）</summary>
        private int _disposed;

        /// <summary>构造</summary>
        public IgwPendingRegistrationRecoveryService(IServiceScopeFactory scopeFactory, ILogger<IgwPendingRegistrationRecoveryService> logger)
            : this(scopeFactory, logger, StartupDelay)
        {
        }

        /// <summary>单测用：可指定启动延迟</summary>
        internal IgwPendingRegistrationRecoveryService(IServiceScopeFactory scopeFactory, ILogger<IgwPendingRegistrationRecoveryService> logger,
            TimeSpan startupDelay)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _startupDelay = startupDelay;
        }

        /// <summary>后台启动恢复（不阻塞宿主启动）</summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _running = RunAsync(_stopping.Token);
            return Task.CompletedTask;
        }

        /// <summary>宿主停止：取消整个恢复（延迟中 / 逐项目之间），等待其结束（最长到宿主停止超时）后释放；可重复调用</summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            try
            {
                _stopping.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 并发的另一次 StopAsync 刚释放：恢复已结束
                return;
            }
            try
            {
                await Task.WhenAny(_running, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                if (_running.IsCompleted && Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _stopping.Dispose();
                }
            }
        }

        private async Task RunAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(_startupDelay, stoppingToken).ConfigureAwait(false);
                using var scope = _scopeFactory.CreateScope();
                var stateService = scope.ServiceProvider.GetRequiredService<IPLC_IgwRegistrationStateService>();
                await stateService.EnsureSchemaAsync().ConfigureAwait(false);
                var queue = MachineQueue.BuildCurrent();
                var queuedProjects = PLC_IgwRegistrationJob.FindQueuedProjects(queue, _logger);
                var count = await IgwPendingRegistrationRecovery.RecoverAsync(stateService, queue,
                    (projectId, args) => PLC_IgwRegistrationJob.EnqueueAsync(projectId, args.ChangedBlocks.ToArray(),
                        args.DeletedBlocks.ToArray(), args.IoRefreshed, args.Full, null, stateService, _logger),
                    _logger,
                    queuedProjects == null ? null : queuedProjects.Contains,
                    PLC_IgwRegistrationJob.HandToQueuedJob,
                    projectId => PLC_IgwRegistrationJob.PersistExtrasAsync(projectId, stateService, _logger, markPersisted: true),
                    stoppingToken).ConfigureAwait(false);
                if (count > 0)
                {
                    _logger.LogInformation("[IGW注册] 启动恢复待补跑注册 {Count} 个项目", count);
                }
            }
            catch (OperationCanceledException)
            {
                // 宿主停止
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IGW注册] 启动恢复待补跑注册失败（不影响启动；可用「注册PLC标签」按钮全量兜底）");
            }
        }
    }
}
