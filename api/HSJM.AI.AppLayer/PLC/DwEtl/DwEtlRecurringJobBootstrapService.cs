using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Hosting;

namespace HSJM.AI.AppLayer.PLC.DwEtl
{
    /// <summary>
    /// dw ETL 每日调度的 Hangfire 注册（宿主启动时执行一次，AddOrUpdate 幂等）。
    /// 注册位置：Program.cs 的 backgroundJobsEnabled 分支（队列名由主项目 HangfireQueueNames.CurrentServiceQueue
    /// 构造后传入——AppLayer 不引用主项目，队列锁语义与 HangfireQueueNames 完全一致）。
    ///
    /// ⚠️ 调度时序铁律：dw-etl-daily（01:30）必须早于 station-shadow-data-retention-cleanup（每日 02:00，
    /// StationShadowRecurringJobBootstrapService 注册，按 HistoryRetentionDays 默认 3 天物理删除
    /// ai_plc_station_shadow_event / ai_plc_station_signal_history 过期行）——ETL 晚于清理会导致窗口内源行
    /// 被物理删除而永久丢失。当前 01:30 提前 30 分钟；若将来调整清理 cron，必须同步复核本任务时刻在其之前。
    /// </summary>
    public class DwEtlRecurringJobBootstrapService : IHostedService
    {
        public const string JobId = "dw-etl-daily";

        /// <summary>每日 01:30（30 1 * * *），早于 02:00 的保留清理任务（见类注释时序铁律）。</summary>
        public const string CronExpression = "30 1 * * *";

        private readonly string _queue;

        public DwEtlRecurringJobBootstrapService(string queue)
        {
            _queue = queue;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // 入本机专用队列 local_<机器名>（调用方传入 HangfireQueueNames.CurrentServiceQueue）：
            // 与 PLC 上传/GRAPH 注册等任务同锁——多机共享 Hangfire 库时不会被其它机器（可能旧 dll）的 worker 抢走。
            // 泛型 AddOrUpdate 的队列重载形参序为 (id, queue, call, cron)。
            RecurringJob.AddOrUpdate<DwEtlOrchestratorService>(
                JobId,
                _queue,
                service => service.RunAllAsync(),
                CronExpression);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
