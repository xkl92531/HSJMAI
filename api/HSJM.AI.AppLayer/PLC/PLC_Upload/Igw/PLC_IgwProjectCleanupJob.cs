using Hangfire;
using Hangfire.States;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// 删除项目后的 IGW 通道清理任务（M-4，Hangfire 本机队列，不自动重试）。编排见 <see cref="IgwProjectRemovalCleaner"/>；
    /// Failed → 抛异常（面板重试）；Skipped / Succeeded 不抛。
    /// <para>同项目注册任务排队 / 执行中时本任务推迟重排（<see cref="ShouldDefer"/>），避免「清理删掉设备 → 在飞注册又建回来」
    /// 留下无人认领的孤儿设备；注册侧同时在写台账 / 登记身份前复查项目是否已删除（见 IgwRegistrationRunner）。</para>
    /// </summary>
    public class PLC_IgwProjectCleanupJob : ISelfScopedAutoInject
    {
        private readonly ISqlSugarClient _db;
        private readonly IPLC_IgwRegistrationService _registrationService;
        private readonly IPLC_IgwRegistrationStateService _stateService;
        private readonly IgwBulkClient _bulkClient;
        private readonly ILogger<PLC_IgwProjectCleanupJob> _logger;

        public PLC_IgwProjectCleanupJob(ISqlSugarClient db, IPLC_IgwRegistrationService registrationService,
            IPLC_IgwRegistrationStateService stateService, IgwBulkClient bulkClient, ILogger<PLC_IgwProjectCleanupJob> logger)
        {
            _db = db;
            _registrationService = registrationService;
            _stateService = stateService;
            _bulkClient = bulkClient;
            _logger = logger;
        }

        /// <summary>每个被删除的项目入队一次清理（本机队列 local_&lt;机器名&gt;）</summary>
        public static void EnqueueForRemovedProjects(IEnumerable<long> projectIds)
        {
            var client = new BackgroundJobClient();
            foreach (var projectId in (projectIds ?? Array.Empty<long>()).Distinct())
            {
                client.Create<PLC_IgwProjectCleanupJob>(job => job.ExecuteAsync(projectId), new EnqueuedState(MachineQueue.BuildCurrent()));
            }
        }

        /// <summary>注册在飞时推迟清理的次数上限（× <see cref="DeferDelay"/> = 闸门占位失效时长，之后照常清理）</summary>
        internal const int MaxDeferrals = 30;

        /// <summary>每次推迟的间隔</summary>
        internal static readonly TimeSpan DeferDelay = TimeSpan.FromMinutes(1);

        /// <summary>是否推迟本次清理：同项目注册排队 / 执行中，且推迟次数未用完</summary>
        internal static bool ShouldDefer(bool registrationInFlight, int deferrals) =>
            registrationInFlight && deferrals < MaxDeferrals;

        /// <summary>兼容入口（重启前已入队的旧任务 / 面板重试）：等价于第 0 次尝试</summary>
        [AutomaticRetry(Attempts = 0)]
        [JobDisplayName("IGW 通道清理 · 已删除项目{0}")]
        public Task ExecuteAsync(long projectId) => ExecuteAsync(projectId, 0);

        /// <param name="projectId">已删除的项目ID</param>
        /// <param name="deferrals">已因注册在飞推迟过的次数（<see cref="MaxDeferrals"/> 次后不再推迟，照常清理）</param>
        [AutomaticRetry(Attempts = 0)]
        [JobDisplayName("IGW 通道清理 · 已删除项目{0}")]
        public async Task ExecuteAsync(long projectId, int deferrals)
        {
            var registrationInFlight = PLC_IgwRegistrationJob.IsRegistrationInFlight(projectId);
            if (ShouldDefer(registrationInFlight, deferrals))
            {
                // 本机队列（与注册任务同队列）：延迟重排，不占用 worker 空转
                new BackgroundJobClient().Schedule<PLC_IgwProjectCleanupJob>(MachineQueue.BuildCurrent(),
                    job => job.ExecuteAsync(projectId, deferrals + 1), DeferDelay);
                _logger.LogInformation("[IGW清理] 项目 {ProjectId} 有注册任务在执行，清理推迟 {Delay} 后重试（第 {Count} 次）",
                    projectId, DeferDelay, deferrals + 1);
                return;
            }
            if (registrationInFlight)
            {
                _logger.LogWarning("[IGW清理] 项目 {ProjectId} 已推迟 {Count} 次、注册仍在执行，本次照常清理"
                    + "（注册侧不会再写台账 / 登记身份；若它此后仍向 IGW 写入设备，需人工核对或重新入队本清理任务）",
                    projectId, deferrals);
            }
            var cleaner = new IgwProjectRemovalCleaner(_bulkClient, new DbStore(this),
                text => WriteLogHelper.WriteLogsAsync(text, "PLC上传"), _logger);
            var outcome = await cleaner.RunAsync(projectId, CancellationToken.None).ConfigureAwait(false);
            PLC_IgwRegistrationJob.EnsureNotFailed(outcome);
        }

        private sealed class DbStore : IIgwProjectRemovalStore
        {
            private readonly PLC_IgwProjectCleanupJob _job;

            public DbStore(PLC_IgwProjectCleanupJob job) => _job = job;

            /// <summary>项目行已软删：按 Id 直查（不加 IsDeleted 条件）；通道名走台账服务的裸列读取</summary>
            public async Task<IgwRemovedProjectInfo> GetProjectAsync(long projectId)
            {
                var project = await _job._db.Queryable<PLC_ProjectEntity>()
                    .Where(p => p.Id == projectId)
                    .Select(p => new PLC_ProjectEntity { Id = p.Id, ProjectName = p.ProjectName, IotUrl = p.IotUrl, PlcIp = p.PlcIp })
                    .FirstAsync()
                    .ConfigureAwait(false);
                if (project == null)
                {
                    return null;
                }
                var channel = await _job._registrationService.GetChannelNameAsync(projectId).ConfigureAwait(false);
                var state = await _job._stateService.GetAsync(projectId).ConfigureAwait(false);
                return new IgwRemovedProjectInfo(project.Id, project.ProjectName, project.IotUrl, project.PlcIp, channel,
                    state?.RegisteredIdentity);
            }

            public Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId) =>
                _job._stateService.ListPeerProjectsAsync(projectId);

            public async Task DeleteRegistrationRecordsAsync(long projectId)
            {
                await _job._registrationService.DeleteProjectLedgerAsync(projectId).ConfigureAwait(false);
                await _job._stateService.DeleteAsync(projectId).ConfigureAwait(false);
            }
        }
    }
}
