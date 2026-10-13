using Hangfire;
using Hangfire.States;
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// PLC 上传后 IGW 自动注册任务（sub-project B spec §4.4，Hangfire 本机队列）：
    /// 本任务只负责取数（项目、DB 块、叶子接口行、IO 标签、台账、通道名）与进度上报，编排全部在
    /// <see cref="IgwRegistrationRunner"/>。IGW 项目的上传后注册与「注册PLC标签」按钮都入队本任务；
    /// 老网关项目仍走 <see cref="PLC_TagRegistrationJob"/>（行为不变）。
    /// 失败语义：任一请求失败 / 旧设备清理失败 / 台账写入失败 → 抛出异常，Hangfire 标记 Failed（不自动重试，
    /// 面板手工 Retry 同参重跑或按钮全量兜底）；配置缺失 → Skipped（不抛）。
    /// 结果 Failed 的最后一轮范围（含合并进来的其它上传）结转并落库，下一次同项目执行自动并入（见 <see cref="RunRoundsAsync"/>）。
    /// </summary>
    public class PLC_IgwRegistrationJob : ISelfScopedAutoInject
    {
        /// <summary>上传进度后处理槽 key / 展示名（PLC_UploadProgressService.SetPostProcess）</summary>
        public const string PostProcessKey = "igw-registration";
        public const string PostProcessTitle = "IGW 注册";

        /// <summary>同项目防重闸门（入队即占位；执行中再来的请求并入补跑）</summary>
        internal static readonly IgwRegistrationQueueGate Gate = new();

        private readonly IPLC_ProjectService _proj;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_BlockInterfaceService _blockInterfaceService;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IPLC_IgwRegistrationService _registrationService;
        private readonly ISqlSugarClient _db;
        private readonly IgwBulkClient _bulkClient;
        private readonly PLC_UploadProgressService _progressService;
        private readonly IPLC_IgwRegistrationStateService _stateService;
        private readonly ILogger<PLC_IgwRegistrationJob> _logger;

        public PLC_IgwRegistrationJob(
            IPLC_ProjectService proj,
            IPLC_BlockService blockService,
            IPLC_BlockInterfaceService blockInterfaceService,
            IPLC_IOTagService ioTagService,
            IPLC_IgwRegistrationService registrationService,
            ISqlSugarClient db,
            IgwBulkClient bulkClient,
            PLC_UploadProgressService progressService,
            IPLC_IgwRegistrationStateService stateService,
            ILogger<PLC_IgwRegistrationJob> logger)
        {
            _proj = proj;
            _blockService = blockService;
            _blockInterfaceService = blockInterfaceService;
            _ioTagService = ioTagService;
            _registrationService = registrationService;
            _db = db;
            _bulkClient = bulkClient;
            _progressService = progressService;
            _stateService = stateService;
            _logger = logger;
        }

        /// <summary>
        /// 该项目是否有注册任务排队 / 执行中（本进程内，跨进程由本机队列隔离）。
        /// 删项目清理任务（<see cref="PLC_IgwProjectCleanupJob"/>）据此推迟，避免与在飞注册互相打架。
        /// </summary>
        internal static bool IsRegistrationInFlight(long projectId) => Gate.IsOccupied(projectId);

        /// <summary>
        /// 入队（同项目防重，入队即占位）：无同项目任务 → 入本机队列 local_&lt;机器名&gt; 并返回 Enqueued；
        /// 同项目任务排队/执行中 → 本次范围并入其「待补跑」（该任务结束后以并集自动补跑一次），返回 MergedIntoPending。
        /// <para>私有：外部一律走 <see cref="EnqueueAsync"/>，否则并入待补跑的范围不会落库（进程重启即丢失）。</para>
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="changedBlockNames">本次变更（清理+重插）的块名</param>
        /// <param name="deletedBlockNames">本次已从数据库删除的块名（从 IGW 清除）</param>
        /// <param name="ioTagsRefreshed">本次上传刷新了 IO 标签表</param>
        /// <param name="full">全量注册（按钮）</param>
        /// <param name="taskId">上传任务ID（进度上报；null = 不上报）</param>
        private static IgwEnqueueResult EnqueueIfNotRunning(long projectId, string[] changedBlockNames, string[] deletedBlockNames,
            bool ioTagsRefreshed, bool full, string taskId = null)
        {
            var args = new IgwRegistrationArgs(changedBlockNames ?? Array.Empty<string>(), deletedBlockNames ?? Array.Empty<string>(),
                ioTagsRefreshed, full, taskId);
            var result = Gate.Reserve(projectId, args);
            if (result != IgwEnqueueResult.Enqueued)
            {
                return result;
            }
            try
            {
                // 本机专用队列：任务语义与本机 dll / 网关连通性强相关（见 MachineQueue 说明）
                var client = new BackgroundJobClient();
                client.Create<PLC_IgwRegistrationJob>(
                    svc => svc.ExecuteAsync(projectId, changedBlockNames, deletedBlockNames, ioTagsRefreshed, full, taskId),
                    new EnqueuedState(MachineQueue.BuildCurrent()));
                return result;
            }
            catch
            {
                Gate.Release(projectId);
                throw;
            }
        }

        /// <summary>M-1：待补跑落库的项目级串行锁 / 曾落库标记（只有落过库才需要写库清除）</summary>
        private static readonly ConcurrentDictionary<long, SemaphoreSlim> PendingPersistLocks = new();
        private static readonly ConcurrentDictionary<long, byte> PendingPersisted = new();

        /// <summary>
        /// 入队（同 <see cref="EnqueueIfNotRunning"/>），并入执行中任务的待补跑时把闸门里未完成的额外范围落库（M-1），
        /// 进程重启后由启动恢复重新入队。所有调用方（上传链路 / 按钮 / CSV 导入 / 启动恢复）都应走本方法。
        /// </summary>
        public static async Task<IgwEnqueueResult> EnqueueAsync(long projectId, string[] changedBlockNames, string[] deletedBlockNames,
            bool ioTagsRefreshed, bool full, string taskId, IPLC_IgwRegistrationStateService stateService, ILogger logger = null)
        {
            var result = EnqueueIfNotRunning(projectId, changedBlockNames, deletedBlockNames, ioTagsRefreshed, full, taskId);
            if (result == IgwEnqueueResult.MergedIntoPending)
            {
                await PersistExtrasAsync(projectId, stateService, logger).ConfigureAwait(false);
            }
            return result;
        }

        /// <summary>
        /// 把闸门里该项目未完成的额外范围写入状态表（无 → 若此前落过库则清除）。项目内串行，写入的总是调用时刻的最新快照，
        /// 因此并发调用最终落库的就是最终状态。失败只记日志（重启会丢这部分范围，按钮全量兜底）。
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="stateService">注册状态表服务</param>
        /// <param name="logger">日志</param>
        /// <param name="markPersisted">先标记「曾落库」（启动恢复入队后清除用：记录来自上次进程，本进程未写过）</param>
        internal static async Task PersistExtrasAsync(long projectId, IPLC_IgwRegistrationStateService stateService, ILogger logger,
            bool markPersisted = false)
        {
            if (stateService == null)
            {
                return;
            }
            var gate = PendingPersistLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (markPersisted)
                {
                    PendingPersisted[projectId] = 0;
                }
                var extras = Gate.GetUnfinishedExtras(projectId);
                if (extras == null)
                {
                    if (PendingPersisted.TryRemove(projectId, out _))
                    {
                        // 条件清除：只清本机队列的记录（其它机器的记录由其自己恢复 / 清除）
                        await stateService.ClearPendingAsync(projectId, MachineQueue.BuildCurrent()).ConfigureAwait(false);
                    }
                    return;
                }
                // 其它机器所属的记录只并入（保留其所属队列），不覆盖
                await stateService.SavePendingAsync(projectId, IgwPendingArgsCodec.Serialize(extras), MachineQueue.BuildCurrent(),
                    IgwPendingArgsCodec.Merge).ConfigureAwait(false);
                PendingPersisted[projectId] = 0;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[IGW注册] 项目 {ProjectId} 待补跑范围落库失败（进程重启会丢失这部分范围，可用按钮全量兜底）", projectId);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 启动恢复用（M-1 fix round 1）：把恢复出的范围交给重启前已入队、将在本机执行的同项目任务——
        /// 闸门结转（或已占位时并入待补跑），并标记已落库，使该任务成功后清除记录。
        /// </summary>
        internal static void HandToQueuedJob(long projectId, IgwRegistrationArgs args)
        {
            Gate.CarryOrMerge(projectId, args);
            PendingPersisted[projectId] = 0;
        }

        /// <summary>
        /// 启动恢复用：Hangfire 本机队列里排队中 / 已被取走（含重启前执行中、超时后会被重新取走）的本任务所属项目（Hangfire 监控 API，同 BackgroundOpsController 用法）；
        /// 查询失败返回 null（调用方按「一律入队」处理，最坏多跑一次，不会并发）。
        /// </summary>
        internal static HashSet<long> FindQueuedProjects(string queue, ILogger logger)
        {
            try
            {
                var api = JobStorage.Current.GetMonitoringApi();
                // 队列里残留的已删除 / 已完成任务（队列项未及时移除）不算：只看仍处于 Enqueued / Processing 的
                var jobs = api.EnqueuedJobs(queue, 0, 5000).Select(x => (x.Value?.Job, x.Value?.InEnqueuedState == true))
                    .Concat(api.FetchedJobs(queue, 0, 5000).Select(x => (x.Value?.Job, IsActiveState(x.Value?.State))));
                return CollectQueuedProjects(jobs);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[IGW注册] 查询 Hangfire 排队中的注册任务失败，启动恢复按一律入队处理");
                return null;
            }
        }

        /// <summary>Hangfire 任务状态是否仍会（或正在）执行：Enqueued / Processing</summary>
        internal static bool IsActiveState(string state) =>
            string.Equals(state, EnqueuedState.StateName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, ProcessingState.StateName, StringComparison.OrdinalIgnoreCase);

        /// <summary>活动（Active=true）的本任务所属项目ID（任务第一个参数）</summary>
        internal static HashSet<long> CollectQueuedProjects(IEnumerable<(Hangfire.Common.Job Job, bool Active)> jobs)
        {
            var result = new HashSet<long>();
            foreach (var (job, active) in jobs)
            {
                if (active && job?.Type == typeof(PLC_IgwRegistrationJob) && job.Args?.Count > 0 && job.Args[0] is long projectId)
                {
                    result.Add(projectId);
                }
            }
            return result;
        }

        /// <summary>Hangfire 执行入口：执行一轮，结束时若有并入的待补跑则以并集再跑，直至无待补跑。</summary>
        [AutomaticRetry(Attempts = 0)]
        [JobDisplayName("IGW 注册 · 项目{0}")]
        public Task ExecuteAsync(long projectId, string[] changedBlockNames, string[] deletedBlockNames,
            bool ioTagsRefreshed, bool full, string taskId = null)
        {
            var args = new IgwRegistrationArgs(changedBlockNames ?? Array.Empty<string>(), deletedBlockNames ?? Array.Empty<string>(),
                ioTagsRefreshed, full, taskId);
            return RunRoundsAsync(projectId, args, current => RunOnceAsync(projectId, current), _stateService, _logger, Report,
                extras => RequeueExtrasAsync(projectId, extras,
                    a => EnqueueAsync(projectId, a.ChangedBlocks.ToArray(), a.DeletedBlocks.ToArray(), a.IoRefreshed, a.Full, a.TaskId,
                        _stateService, _logger),
                    _stateService, _logger),
                text => WriteLogHelper.WriteLogsAsync(text, "PLC上传"));
        }

        /// <summary>
        /// 执行编排（静态、依赖以委托注入，便于单测）：
        /// <list type="bullet">
        /// <item>开始时并入闸门里的结转范围（上次 Failed 轮次留下的，M-1 fix round 1）；</item>
        /// <item>每轮结束取走待补跑再跑，直至无待补跑；每次都把未完成的额外范围落库；</item>
        /// <item>最后一轮结果 Failed（未抛异常，如批量探测失败 / 版本过低 / 旧设备清理失败 / 身份冲突）→ 该轮完整范围结转并保留落库记录，
        /// <b>不立即重新入队</b>（持续失败时不空转），由下一次同项目执行（上传 / 按钮 / 面板重试 / 启动恢复）并入；成功后清除；</item>
        /// <item>执行异常 → 释放占位，额外范围（含结转）另入队一次（入队失败保留落库记录，重启时恢复）。</item>
        /// </list>
        /// </summary>
        internal static async Task RunRoundsAsync(long projectId, IgwRegistrationArgs args,
            Func<IgwRegistrationArgs, Task<IgwRegistrationOutcome>> runOnce,
            IPLC_IgwRegistrationStateService stateService, ILogger logger,
            Action<IEnumerable<string>, string, string> report,
            Func<IgwRegistrationArgs, Task> requeueExtras,
            Func<string, Task> writeLog)
        {
            if (!Gate.TryStart(projectId, args))
            {
                logger.LogInformation("[IGW注册] 项目 {ProjectId} 已有注册任务在执行，本次范围并入其补跑", projectId);
                report(new[] { args.TaskId }, "Running", "已并入同项目正在执行的 IGW 注册任务，完成后自动补跑本次变更");
                await PersistExtrasAsync(projectId, stateService, logger).ConfigureAwait(false);
                return;
            }

            IgwRegistrationOutcome outcome = null;
            var current = args;
            try
            {
                var carried = Gate.AdoptCarried(projectId);
                if (carried != null)
                {
                    logger.LogInformation("[IGW注册] 项目 {ProjectId} 并入上次失败未完成的注册范围一并执行", projectId);
                    current = IgwRegistrationArgs.Union(current, carried);
                }
                while (current != null)
                {
                    report(current.AllTaskIds, "Running", current.Full ? "后台全量注册中" : "后台增量注册中");
                    outcome = await runOnce(current).ConfigureAwait(false);
                    var failed = outcome?.Status == IgwRegistrationStatus.Failed;
                    report(current.AllTaskIds, outcome?.Status.ToString() ?? "Failed", failed
                        ? $"{outcome.Detail}（下次上传 / 「注册PLC标签」按钮 / Hangfire 面板重试会自动带上本次范围）"
                        : outcome?.Detail ?? "注册未返回结果");

                    // Failed 且无待补跑 → 本轮范围结转（不丢、不立即重入队）；有待补跑 → 下一轮并集已覆盖本轮
                    var next = Gate.CompleteOrTakePending(projectId, current, failed);
                    // M-1：补跑轮次开始前把在飞的额外范围落库；无补跑时写入结转范围或清除此前落库的记录
                    await PersistExtrasAsync(projectId, stateService, logger).ConfigureAwait(false);
                    if (next != null)
                    {
                        logger.LogInformation("[IGW注册] 项目 {ProjectId} 执行期间有新的注册请求，按并集补跑一次", projectId);
                    }
                    else if (failed)
                    {
                        logger.LogWarning("[IGW注册] 项目 {ProjectId} 注册失败，本次范围已结转，下次同项目注册时一并重做", projectId);
                    }
                    current = next;
                }
            }
            catch (Exception ex)
            {
                var extras = Gate.ReleaseAndTakeExtras(projectId);
                logger.LogError(ex, "[IGW注册] 项目 {ProjectId} 执行异常（任务将标记 Failed）", projectId);
                report((current ?? args).AllTaskIds, "Failed", $"{ex.GetBaseException().Message}（可在 Hangfire 面板重试，或用「注册PLC标签」按钮全量兜底）");
                try
                {
                    await writeLog($"[IGW注册] 项目 {projectId} 执行异常: {ex.GetBaseException().Message}").ConfigureAwait(false);
                }
                catch
                {
                    // 日志写入失败不能掩盖原始异常
                }
                if (extras != null)
                {
                    await requeueExtras(extras).ConfigureAwait(false);
                }
                throw;
            }

            // 最后一轮的范围 ⊇ 之前各轮（并集），以最后一轮结果为准
            EnsureNotFailed(outcome);
        }

        /// <summary>
        /// M-1：执行异常时，合并进来的额外范围（不是本任务自身参数——那部分随本任务 Failed、由面板重试）另入队一次，不再丢弃；
        /// 入队后落库（范围已进 Hangfire 参数 → 清除本机记录）。
        /// 入队失败（M-1 fix round 2）→ 范围放回闸门（未占位则结转、已占位则并入待补跑）并重新落库：
        /// 下一次同项目执行成功消费后才清除记录（进程重启则由启动恢复入队）。
        /// </summary>
        internal static async Task RequeueExtrasAsync(long projectId, IgwRegistrationArgs extras,
            Func<IgwRegistrationArgs, Task<IgwEnqueueResult>> enqueue, IPLC_IgwRegistrationStateService stateService, ILogger logger)
        {
            if (extras == null)
            {
                return;
            }
            try
            {
                await enqueue(extras).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Gate.CarryOrMerge(projectId, extras);
                await PersistExtrasAsync(projectId, stateService, logger).ConfigureAwait(false);
                logger.LogWarning(ex, "[IGW注册] 项目 {ProjectId} 待补跑范围重新入队失败，已放回并落库，下次同项目注册（或进程重启恢复）时一并执行", projectId);
                return;
            }
            await PersistExtrasAsync(projectId, stateService, logger).ConfigureAwait(false);
            logger.LogInformation("[IGW注册] 项目 {ProjectId} 本轮异常，已把合并进来的待补跑范围另行入队", projectId);
        }

        /// <summary>项目网关是 IGW（IotUrl 非空且不是 2500 老网关）：上传链路 / CSV 导入据此决定入队本任务（不探测网关类型）</summary>
        public static bool IsIgwProject(string iotUrl) =>
            !string.IsNullOrWhiteSpace(iotUrl) && !DcsIgwConsts.IsLegacyIotGatewayUrl(iotUrl);

        /// <summary>Failed（含 IGW 不可达 / 版本过低 / 请求失败）→ 抛异常让 Hangfire 标记 Failed；Succeeded / Skipped 不抛。</summary>
        internal static void EnsureNotFailed(IgwRegistrationOutcome outcome)
        {
            if (outcome?.Status == IgwRegistrationStatus.Failed)
            {
                throw new InvalidOperationException($"IGW 注册失败: {outcome.Detail}");
            }
        }

        private async Task<IgwRegistrationOutcome> RunOnceAsync(long projectId, IgwRegistrationArgs args)
        {
            var project = await _proj.GetByIdAsync(projectId).ConfigureAwait(false);
            var info = project == null
                ? null
                : new IgwRegistrationProjectInfo(project.Id, project.ProjectName, project.ProductionLineName, project.PlcCode,
                    project.IotUrl, project.PlcIp, project.PlcPort ?? 0) { IsDeleted = project.IsDeleted };
            // 非老网关 URL 不做网关类型探测（IGW 宕机时探测会退化成「非 IGW」→ 静默跳过）；runner 以批量接口探测为准：
            // 网络异常 / 版本过低 → Failed。老网关 URL 由上传链路 / 按钮分派到 PLC_TagRegistrationJob，runner 内也会 Skipped
            var runner = new IgwRegistrationRunner(
                _bulkClient,
                new DbStore(this),
                text => WriteLogHelper.WriteLogsAsync(text, "PLC上传"),
                _logger,
                AppsettingsConfig.IgwRegistration,
                RequestGraphRegistrationAsync,
                new IgwProjectStateStore(_stateService));
            return await runner.RunAsync(info, args, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// 过渡安全（I-1）：旧 GRAPH 设备被清理 / 台账无 Graph 行而项目有 GRAPH 目标时，请求一次全项目 GRAPH 步位注册。
        /// 同项目 GRAPH 任务执行中时登记「待补跑」，该任务结束后自动再入队一次全项目运行（见 GraphStepTagRegistrationJob.EnqueueWholeProject）。
        /// </summary>
        private async Task RequestGraphRegistrationAsync(long projectId, string reason)
        {
            var enqueued = GraphStepTagRegistrationJob.EnqueueWholeProject(projectId);
            var text = enqueued
                ? $"[IGW注册] 项目 {projectId} 已入队全项目 GRAPH 步位注册：{reason}"
                : $"[IGW注册] 项目 {projectId} GRAPH 步位注册任务执行中，已登记结束后补跑全项目：{reason}";
            _logger.LogInformation("{Text}", text);
            await WriteLogHelper.WriteLogsAsync(text, "PLC上传").ConfigureAwait(false);
        }

        /// <summary>上报到每一个相关上传任务的进度槽（补跑合并了多次上传时，每次上传都能看到最终状态）</summary>
        private void Report(IEnumerable<string> taskIds, string status, string detail)
        {
            foreach (var taskId in taskIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
            {
                _progressService.SetPostProcess(taskId, PostProcessKey, PostProcessTitle, status, detail);
            }
        }

        /// <summary>生产取数 / 台账实现（SqlSugar 服务）</summary>
        private sealed class DbStore : IIgwRegistrationStore
        {
            private readonly PLC_IgwRegistrationJob _job;

            public DbStore(PLC_IgwRegistrationJob job) => _job = job;

            public Task<string> GetChannelNameAsync(long projectId) => _job._registrationService.GetChannelNameAsync(projectId);

            public Task<bool> HasGraphTargetsAsync(long projectId) => GraphStepTagRegistrationJob.HasGraphTargetsAsync(_job._db, projectId);

            /// <summary>项目行按 Id 直查（不加 IsDeleted 条件，与 RunOnceAsync 取项目一致）：行不存在或已软删 → 已删除</summary>
            public async Task<bool> IsProjectRemovedAsync(long projectId)
            {
                var project = await _job._proj.GetByIdAsync(projectId).ConfigureAwait(false);
                return project == null || project.IsDeleted;
            }

            public Task SetChannelNameAsync(long projectId, string channelName) =>
                _job._registrationService.SetChannelNameAsync(projectId, channelName);

            public Task<IgwLedgerSnapshot> GetLedgerAsync(long projectId) => _job._registrationService.GetLedgerAsync(projectId);

            public Task ReplaceLedgerAsync(long projectId, IReadOnlyList<PLC_IgwRegistrationEntity> rows,
                IReadOnlyList<(string RootKind, string RootName)> removedRoots) =>
                _job._registrationService.ReplaceForProjectAsync(projectId, rows, removedRoots);

            /// <summary>
            /// 项目全部未删除 DB 块名（ProgrammingLanguage=="DB"，按块 Id 排序，供块根组分配 / 清除判定）
            /// + 请求范围内块（<paramref name="rowBlocks"/> 为 null = 全部块；增量 = 变更块）的叶子接口行
            /// （只投影需要的 7 列，按接口行 Id 稳定排序，按路径过滤容器行）+ （需要时）IO 标签（按 Id 排序）。
            /// <para>
            /// GRAPH 实例 DB（ProgrammingLanguage 同为 "DB"）在这里按普通 DB 块注册到 S7Plus 设备（设计如此）：
            /// 读链经 S1500_{ip} 别名读取其非容器成员；其 S_NO / RT_DATA.EXEC_BITS 另由 Task 7 的 GRAPH 注册
            /// 写入 GRAPH 设备（逻辑名 S7Plus_{ip}），两者逻辑名不同、互不冲突。
            /// </para>
            /// </summary>
            public async Task<IgwRegistrationData> LoadDataAsync(long projectId, bool includeIoTags, IReadOnlyCollection<string> rowBlocks)
            {
                var blocks = await _job._blockService.GetListAsync(it => it.IsDeleted == false
                    && it.ProjectId == projectId
                    && it.ProgrammingLanguage == "DB").ConfigureAwait(false);
                blocks = blocks.Where(b => !string.IsNullOrWhiteSpace(b.BlockName)).OrderBy(b => b.Id).ToList();
                var blockById = blocks.ToDictionary(b => b.Id);
                var wanted = rowBlocks == null ? null : new HashSet<string>(rowBlocks, StringComparer.OrdinalIgnoreCase);
                var ids = blocks.Where(b => wanted == null || wanted.Contains(b.BlockName)).Select(b => b.Id).ToList();

                List<PLC_BlockInterfaceEntity> interfaces = new();
                if (ids.Count > 0)
                {
                    // 投影：接口表列多（InitialValue/ArrayInfo/Remanence…），大项目 10 万行级只取注册需要的列
                    interfaces = await _job._blockInterfaceService.GetDb().AsQueryable()
                        .Where(it => it.IsDeleted == false
                            && ids.Contains(it.BlockId.Value)
                            && !string.IsNullOrEmpty(it.VariablePath))
                        .Select(it => new PLC_BlockInterfaceEntity
                        {
                            Id = it.Id,
                            BlockId = it.BlockId,
                            VariablePath = it.VariablePath,
                            DataType = it.DataType,
                            OffsetByte = it.OffsetByte,
                            OffsetBit = it.OffsetBit,
                            Comment = it.Comment,
                        })
                        .ToListAsync().ConfigureAwait(false);
                }

                var ordered = interfaces
                    .Where(r => r.BlockId.HasValue && blockById.ContainsKey(r.BlockId.Value))
                    .OrderBy(r => r.Id)
                    .ThenBy(r => r.BlockId)
                    .ToList();
                var leaves = IgwLeafRowFilter.FilterLeaves(ordered, r => r.BlockId.Value.ToString(), r => r.VariablePath);
                var rows = leaves.Select(r =>
                {
                    var block = blockById[r.BlockId.Value];
                    return new PlannerDbRow(block.BlockName, block.BlockNumber ?? 0, r.VariablePath, r.DataType,
                        r.OffsetByte, r.OffsetBit, r.Comment);
                }).ToList();

                List<PlannerIoTag> ioTags = new();
                if (includeIoTags)
                {
                    var tags = await _job._ioTagService.GetListAsync(it => it.IsDeleted == false && it.ProjectId == projectId).ConfigureAwait(false);
                    ioTags = tags.OrderBy(t => t.Id)
                        .Select(t => new PlannerIoTag(t.TableName, t.TagName, t.Address, t.DataType, t.Comment))
                        .ToList();
                }

                // 工步相关块（GRAPH FB 的实例 DB）——这些块的点位注册成 GraphDbScanRateMs（100ms），
                // 其余 DB 点保持 DbScanRateMs。查不到不影响注册，退化成全部用 DbScanRateMs（旧行为）。
                IReadOnlyCollection<string> graphDbBlocks = null;
                try
                {
                    graphDbBlocks = await GraphStepTagRegistrationJob
                        .QueryGraphDbBlockNamesAsync(_job._db, projectId).ConfigureAwait(false);
                }
                catch (Exception graphEx)
                {
                    _job._logger?.LogWarning(graphEx,
                        "[IGW注册] 查询工步块失败，本次全部按 DbScanRateMs 注册 Project={ProjectId}", projectId);
                }

                return new IgwRegistrationData(
                    blocks.Select(b => b.BlockName).ToList(), rows, ioTags, graphDbBlocks);
            }
        }
    }
}
