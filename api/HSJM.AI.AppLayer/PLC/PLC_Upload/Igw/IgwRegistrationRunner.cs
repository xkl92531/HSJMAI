using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>注册任务使用的项目信息（与 PLC_ProjectEntity 解耦，便于无数据库单测）</summary>
    public sealed record IgwRegistrationProjectInfo(long ProjectId, string ProjectName, string ProductionLineName,
        string PlcCode, string IotUrl, string PlcIp, int PlcPort)
    {
        /// <summary>项目行已软删（删除前已排队的注册任务执行时）→ Skipped，不发任何 IGW 请求，免得重建刚被清理的通道/设备</summary>
        public bool IsDeleted { get; init; }
    }

    /// <summary>
    /// 一次注册的范围参数。Full=true 全量（项目全部 DB 块 + IO）；否则增量：ChangedBlocks 生成点并整组替换，
    /// DeletedBlocks 只清除，IoRefreshed 时整体替换 IO。TaskId 为上传进度对象（null = 不上报）。
    /// </summary>
    public sealed record IgwRegistrationArgs(IReadOnlyCollection<string> ChangedBlocks, IReadOnlyCollection<string> DeletedBlocks,
        bool IoRefreshed, bool Full, string TaskId)
    {
        /// <summary>合并进来的其它上传任务 ID（不含 <see cref="TaskId"/>）；执行结果要上报到每一个进度槽</summary>
        public IReadOnlyList<string> MergedTaskIds { get; init; } = Array.Empty<string>();

        /// <summary>本范围涉及的全部上传任务 ID（去重，非空）</summary>
        public IReadOnlyList<string> AllTaskIds =>
            MergedTaskIds.Append(TaskId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>
        /// 合并两次注册范围（执行中又来一次上传时用）：变更块/删除块取并集，IoRefreshed/Full 取或，
        /// TaskId 取后到者，其余任务 ID 保留在 MergedTaskIds（最终状态上报到所有进度槽）。
        /// </summary>
        public static IgwRegistrationArgs Union(IgwRegistrationArgs first, IgwRegistrationArgs second)
        {
            if (first == null) return second;
            if (second == null) return first;
            var taskId = second.TaskId ?? first.TaskId;
            return new IgwRegistrationArgs(
                Merge(first.ChangedBlocks, second.ChangedBlocks),
                Merge(first.DeletedBlocks, second.DeletedBlocks),
                first.IoRefreshed || second.IoRefreshed,
                first.Full || second.Full,
                taskId)
            {
                MergedTaskIds = first.AllTaskIds.Concat(second.AllTaskIds)
                    .Where(x => !string.Equals(x, taskId, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
            };
        }

        private static string[] Merge(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b) =>
            (a ?? Array.Empty<string>()).Concat(b ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
    }

    /// <summary>
    /// 注册所需的数据库数据：LiveDbBlocks = 项目全部未删除 DB 块名（按块 Id 排序，块根组分配 / 清除判定用）；
    /// DbRows = 请求范围内块（全量 = 全部 DB 块，增量 = 变更块）的叶子接口行
    /// （已按接口行 Id 稳定排序、已过 <see cref="IgwLeafRowFilter"/>）；IoTags = IO 标签（按 Id 排序；未请求时为空）。
    /// </summary>
    /// <param name="GraphDbBlocks">
    /// 工步相关块（GRAPH FB 的实例 DB）的块名。这些块的点位用 <see cref="IgwRegistrationConfig.GraphDbScanRateMs"/>
    /// 采集，其余 DB 点仍用 DbScanRateMs——工步状态要 100ms 级，别的点没必要，
    /// 而整体提速会把 S7-1500 的连接压断（2026-09-22）。为 null 时全部走 DbScanRateMs（旧行为）。
    /// </param>
    public sealed record IgwRegistrationData(IReadOnlyCollection<string> LiveDbBlocks, IReadOnlyList<PlannerDbRow> DbRows,
        IReadOnlyList<PlannerIoTag> IoTags, IReadOnlyCollection<string> GraphDbBlocks = null);

    /// <summary>通道名 + 台账的持久化依赖（DB/IO 注册与 GRAPH 注册共用；生产实现走 IPLC_IgwRegistrationService，单测用内存实现）</summary>
    public interface IIgwLedgerStore
    {
        Task<string> GetChannelNameAsync(long projectId);

        Task SetChannelNameAsync(long projectId, string channelName);

        Task<IgwLedgerSnapshot> GetLedgerAsync(long projectId);

        Task ReplaceLedgerAsync(long projectId, IReadOnlyList<PLC_IgwRegistrationEntity> rows,
            IReadOnlyList<(string RootKind, string RootName)> removedRoots);
    }

    /// <summary>注册任务对持久化层的全部依赖（生产实现走 SqlSugar 服务，单测用内存实现）</summary>
    public interface IIgwRegistrationStore : IIgwLedgerStore
    {
        /// <summary>rowBlocks = 需要加载接口行的块名（null = 全部 DB 块；增量时只传变更块，避免每次上传全表加载）</summary>
        Task<IgwRegistrationData> LoadDataAsync(long projectId, bool includeIoTags, IReadOnlyCollection<string> rowBlocks);

        /// <summary>项目是否有 GRAPH 步进 DB（绑定表命中 GRAPH FB 的实例 DB）——台账无 Graph 行时据此决定是否触发 GRAPH 重注册</summary>
        Task<bool> HasGraphTargetsAsync(long projectId);

        /// <summary>
        /// 项目行现在是否已被删除（软删 / 行已不存在）。发完请求、写台账前复查一次：本次执行期间项目被删除时
        /// 不再写台账 / 登记身份，免得给已删除项目留下"活的"注册记录（删项目的清理任务只跑一次，之后没人认领）。
        /// </summary>
        Task<bool> IsProjectRemovedAsync(long projectId);
    }

    public enum IgwRegistrationStatus
    {
        Succeeded,
        Skipped,
        Failed,
    }

    /// <summary>一次注册的结果（Status 对应上传进度槽状态；Detail 为展示文案）</summary>
    public sealed record IgwRegistrationOutcome(IgwRegistrationStatus Status, string Detail);

    /// <summary>
    /// IGW 自动注册编排核心（sub-project B spec §4.4 / §5），与 Hangfire / 数据库解耦：
    /// 项目校验 → 批量接口探测 → 通道名（台账列优先，缺失时生成并回写）→ 旧设备清理 → 取数 → 规划 →
    /// 按 S7、S7Plus 顺序发送（同设备串行；瞬态失败停发该设备后续请求；IGW 按内容拒绝时按根组定位/二分隔离，被拒根组记 Rejected、其余根组照常写入）→ 写台账 → 读别名缓存失效 → 写日志。
    /// 除调用方取消外不抛异常，一律折叠为 <see cref="IgwRegistrationOutcome"/>；Failed 由 Hangfire 任务转成异常。
    /// </summary>
    public sealed class IgwRegistrationRunner
    {
        /// <summary>日志 / 台账 SkipSummary 列出的跳过原因条数</summary>
        private const int TopReasons = 5;

        private const int MaxSkipSummaryLength = 500;
        private const int MaxErrorLength = 1000;

        private static readonly JsonSerializerOptions RenamesJsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly IgwBulkClient _client;
        private readonly IIgwRegistrationStore _store;
        private readonly Func<string, Task> _writeLog;
        private readonly ILogger _logger;
        private readonly IgwRegistrationConfig _config;
        private readonly Func<long, string, Task> _requestGraphRegistration;
        private readonly IIgwProjectStateStore _stateStore;
        private readonly Func<DateTime> _now;

        /// <param name="requestGraphRegistration">
        /// 请求对项目做一次全项目 GRAPH 步位重注册（参数：项目 ID、原因）。生产实现入队 GraphStepTagRegistrationJob（全项目）；
        /// null = 不请求。调用失败只记日志，不影响 DB/IO 注册结果。
        /// </param>
        /// <param name="stateStore">项目级注册状态（GRAPH 失败冷却 / 注册身份）；null = 不启用这些功能（单测兼容）</param>
        /// <param name="now">当前本地时间（单测注入）；null = DateTime.Now</param>
        public IgwRegistrationRunner(IgwBulkClient client, IIgwRegistrationStore store,
            Func<string, Task> writeLog, ILogger logger, IgwRegistrationConfig config,
            Func<long, string, Task> requestGraphRegistration = null,
            IIgwProjectStateStore stateStore = null, Func<DateTime> now = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _writeLog = writeLog ?? (_ => Task.CompletedTask);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? new IgwRegistrationConfig();
            _requestGraphRegistration = requestGraphRegistration;
            _stateStore = stateStore;
            _now = now ?? (() => DateTime.Now);
        }

        /// <summary>
        /// 注册身份变化时访问旧位置的总时限（I-3）：既限制旧位置清理（跨全部树读取 / 删除调用），也限制旧位置被另一项目占用时
        /// 对旧通道的只读检查（<see cref="CheckOccupiedOldChannelAsync"/>）。清理超时按「旧 IGW 不可达」处理——
        /// 旧位置在另一 IGW → 跳过并提示手工清理；同一 IGW → 任务失败（不发请求，下次重试）；只读检查超时 → 任务失败。
        /// </summary>
        public TimeSpan IdentityCleanupDeadline { get; set; } = TimeSpan.FromSeconds(90);

        /// <summary>一个根组单元（设备 + 根组名）在本次运行中的结果（R-1）</summary>
        private sealed class UnitResult
        {
            /// <summary>至少有一片写入了 IGW（200 或 runtime_apply_failed）</summary>
            public bool Applied;

            /// <summary>任一片失败 / 未发送 / 被拒</summary>
            public bool Failed;

            /// <summary>IGW 按内容永久拒绝（定位到本单元）</summary>
            public bool Rejected;

            /// <summary>已写入 IGW 的点数（200 / runtime_apply_failed 的请求里属于本单元的点）；部分分片失败时台账 TagCount 取此值</summary>
            public int AppliedItems;

            public string Error;

            public void Fail(string error)
            {
                Failed = true;
                Error ??= error;
            }

            public void Reject(string error)
            {
                Failed = true;
                Rejected = true;
                Error = error;
            }
        }

        /// <summary>一次发送的结果：类别、可读错误、IGW 逐项错误下标</summary>
        private sealed record SendAttempt(IgwBulkFailureKind Kind, string Error, IReadOnlyCollection<int> ItemErrorIndexes);

        /// <summary>
        /// 一个原始请求（规划器生成的请求）及其全部隔离重发（剔除重发 / 二分）的记账：
        /// ItemKeys = 原请求中带点（items）的单元——只有它们能提供「设备接受内容」或「逐个都被拒」的证据；
        /// 只带清除组的单元（replaceGroups 无点）两头都不算：IGW 对纯清除请求可能返回 200，
        /// 而同一设备上带点的请求全部被 mixed_siemens_address_mode 拒绝，所以纯清除成功不能证明设备接受内容。
        /// AnyApplied = 某次写入成功的请求带了 ≥1 个点（设备接受其它内容的证据）；NoIndexRejected = 单独发送仍被无下标拒绝的单元。
        /// 无下标拒绝无法区分「单元内容非法」与「设备当前状态导致一律拒绝」：只有同一原请求有带点的单元写入成功时，
        /// 无下标被拒的单元才记 Rejected；否则改记瞬态 Failed（带 IGW 错误原文），下次注册自动重试。
        /// </summary>
        private sealed class Origin
        {
            public Origin(IgwBulkRequest request)
            {
                ItemKeys = new HashSet<string>(request.Items.Select(i => IgwRequestUnits.UnitOf(i.GroupPath)), StringComparer.OrdinalIgnoreCase);
            }

            public HashSet<string> ItemKeys { get; }

            public bool AnyApplied { get; set; }

            public HashSet<string> NoIndexRejected { get; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>本原请求首次无下标拒绝的 IGW 错误</summary>
            public string Error { get; set; }
        }

        public async Task<IgwRegistrationOutcome> RunAsync(IgwRegistrationProjectInfo project, IgwRegistrationArgs args, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(args);
            var total = Stopwatch.StartNew();
            if (project == null)
            {
                return await SkipAsync(null, "项目不存在").ConfigureAwait(false);
            }
            if (project.IsDeleted)
            {
                return await SkipAsync(project, "项目已删除，不再注册（IGW 通道由删除项目的清理任务处理）").ConfigureAwait(false);
            }

            var changedArg = Clean(args.ChangedBlocks);
            var deletedArg = Clean(args.DeletedBlocks);
            if (!args.Full && changedArg.Count == 0 && deletedArg.Count == 0 && !args.IoRefreshed)
            {
                return await SkipAsync(project, "无变更块、无删除块且未刷新 IOTag").ConfigureAwait(false);
            }

            // ---------- 配置校验（缺失 → Skipped，不算失败） ----------
            if (string.IsNullOrWhiteSpace(project.IotUrl))
            {
                return await SkipAsync(project, "项目未配置网关地址（IotUrl 为空）").ConfigureAwait(false);
            }
            if (DcsIgwConsts.IsLegacyIotGatewayUrl(project.IotUrl))
            {
                return await SkipAsync(project, "项目网关是老网关（IotUrl 指向老 IoT 网关），不走 IGW 批量注册").ConfigureAwait(false);
            }

            try
            {
                // 非老网关 URL 一律按 IGW 处理，不做「网关类型探测」：IGW 宕机/网络不通时探测会退化成「不是 IGW」，
                // 若据此 Skipped 会让注册静默丢失。下方批量接口探测的网络异常 → Failed（任务抛出），404/405 → Failed（版本过低）
                if (string.IsNullOrWhiteSpace(project.PlcIp))
                {
                    return await SkipAsync(project, "IGW 注册需要 PLC 直连 IP（项目 plcip 为空），请先在项目上填写 PLC IP").ConfigureAwait(false);
                }
                var plcIp = project.PlcIp.Trim();

                // ---------- 批量接口探测 ----------
                bool supported;
                try
                {
                    supported = await _client.SupportsBulkAsync(project.IotUrl, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return await FailAsync(project, $"IGW 批量接口探测失败：{ex.Message}").ConfigureAwait(false);
                }
                if (!supported)
                {
                    return await FailAsync(project,
                        "IGW 版本过低：不支持批量注册接口 POST /api/v1/project/bulk/tags，请升级 IGW 到含批量接口的版本后重试").ConfigureAwait(false);
                }

                // ---------- 通道名：项目列优先（改名不漂移），缺失则生成并回写 ----------
                var channelName = (await _store.GetChannelNameAsync(project.ProjectId).ConfigureAwait(false))?.Trim();
                if (string.IsNullOrEmpty(channelName))
                {
                    channelName = IgwTagNaming.ChannelName(project.ProductionLineName, project.PlcCode, project.ProjectName);
                    if (channelName == null)
                    {
                        return await SkipAsync(project, "无法生成 IGW 通道名（产线名 / PlcCode / 项目名皆为空）").ConfigureAwait(false);
                    }
                    await _store.SetChannelNameAsync(project.ProjectId, channelName).ConfigureAwait(false);
                }

                // ---------- I-3 注册身份：冲突检测；身份变化 → 清理旧位置 + 本次强制全量 ----------
                var identity = IgwRegistrationIdentity.Create(project.IotUrl, plcIp, channelName);
                string identityChange = null;
                var identityNotes = new List<string>();
                var identityRemovals = new List<IgwLegacyDeviceRemoval>();
                // 注册状态表整张不可用（迁移未跑且启动建表也失败）→ 放行：不做身份变化检测 / 冲突检查 / 身份登记（回到加固前行为），
                // 只在结果详情里提示。其它数据库错误仍然抛出 → 本次失败（避免把"读不到状态"误当成"没有状态"）
                var stateTableUnavailable = false;
                if (_stateStore != null && identity != null)
                {
                    try
                    {
                        var peers = await _stateStore.ListPeerProjectsAsync(project.ProjectId).ConfigureAwait(false);
                        var conflicts = IgwIdentityConflicts.Evaluate(identity, peers);
                        if (conflicts.Blocking.Count > 0)
                        {
                            return await FailAsync(project, "注册身份冲突（未发送任何注册请求）：" + string.Join("；", conflicts.Blocking)
                                + "。请删除重复项目（删除项目会自动清理其 IGW 通道）或改正项目 PLC IP / 通道后重试").ConfigureAwait(false);
                        }
                        identityNotes.AddRange(conflicts.Warnings);

                        var previous = (await _stateStore.GetStateAsync(project.ProjectId).ConfigureAwait(false))?.RegisteredIdentity;
                        if (previous != null && !previous.Matches(identity))
                        {
                            identityChange = $"注册身份变化（{previous.Describe()} → {identity.Describe()}）";
                            var sameIgw = previous.SameIgw(identity);
                            var sameChannel = sameIgw && string.Equals(previous.ChannelName, identity.ChannelName, StringComparison.OrdinalIgnoreCase);
                            // 旧通道被另一未删除项目登记（同一 IGW 同通道）→ 即使空了也不删通道，只删本项目设备
                            var oldChannelSharedByPeer = peers.Any(p => p.RegisteredIdentity != null && p.RegisteredIdentity.SameIgw(previous)
                                && string.Equals(p.RegisteredIdentity.ChannelName, previous.ChannelName, StringComparison.OrdinalIgnoreCase));
                            // 旧位置（同 IGW + IP + 通道）被另一项目登记或配置 → 旧通道里的 S7_/S1500_/S7Plus_{旧IP} 可能是对方的，不删
                            var occupant = IgwIdentityConflicts.FindOccupantOfPreviousLocation(previous, peers);
                            if (occupant != null)
                            {
                                // 同一 IGW 且 PLC IP 未变（只换了通道）：旧通道里的 S7_/S1500_/S7Plus_{ip} 与新位置的逻辑设备名相同，
                                // IGW 内逻辑名全局解析 → 新建设备会与之重名，又不能替对方删 → 先查 IGW 上旧通道是否还有这些设备：
                                // 仍有 → 失败交人工处理（不登记身份、不发请求）；已无（人工删过 / 旧通道已不存在）→ 不动旧位置，继续注册
                                if (sameIgw && string.Equals(previous.PlcIp, identity.PlcIp, StringComparison.OrdinalIgnoreCase))
                                {
                                    var (occupiedFailure, oldChannelFound) = await CheckOccupiedOldChannelAsync(previous, identity, occupant, ct).ConfigureAwait(false);
                                    if (occupiedFailure != null)
                                    {
                                        return await FailAsync(project, $"{identityChange}，{occupiedFailure}").ConfigureAwait(false);
                                    }
                                    identityNotes.Add($"旧位置（{previous.Describe()}）与项目 {occupant.ProjectName}({occupant.ProjectId}) 的登记/配置相同，"
                                        + (oldChannelFound ? "旧通道中已无与新位置同名的逻辑设备" : "旧通道已不存在") + "，旧位置未做任何改动");
                                }
                                else
                                {
                                    identityNotes.Add($"旧位置（{previous.Describe()}）与项目 {occupant.ProjectName}({occupant.ProjectId}) 的登记/配置相同，"
                                        + "未清理旧位置设备以免误删对方设备，请人工核对");
                                }
                            }
                            else
                            {
                                var cleanupFailure = await CleanPreviousLocationAsync(previous, identity, sameIgw,
                                    deleteChannelIfEmpty: !sameChannel && !oldChannelSharedByPeer, identityNotes, identityRemovals, ct).ConfigureAwait(false);
                                if (cleanupFailure != null)
                                {
                                    return await FailAsync(project, $"{identityChange}，{cleanupFailure}").ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (IgwSqlErrorClassifier.IsUndefinedTable(ex))
                    {
                        stateTableUnavailable = true;
                        identityNotes.Add("注册状态表不可用（表不存在），本次跳过注册身份变化检测 / 冲突检查，"
                            + $"也不登记注册身份（请执行迁移脚本建表）：{ex.GetBaseException().Message}");
                        _logger.LogWarning(ex, "[IGW注册] 项目 {ProjectId} 注册状态表不可用（表不存在），本次按加固前行为注册", project.ProjectId);
                    }
                }

                // ---------- 旧设备清理（失败 → 整个任务失败：旧别名残留会让读链解析冲突） ----------
                IReadOnlyList<IgwLegacyDeviceRemoval> removedLegacyDevices;
                try
                {
                    removedLegacyDevices = await _client.RemoveLegacyDevicesDetailedAsync(project.IotUrl, plcIp, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    return await FailAsync(project, $"清理旧设备失败（未发送任何注册请求）：{ex.Message}").ConfigureAwait(false);
                }
                var removedLegacy = removedLegacyDevices.Select(x => x.Description).ToList();

                // ---------- 台账 + 过渡安全：增量自动升级全量 ----------
                // 台账无 DbBlock/Io 行（项目从未经批量接口注册过——切换后的第一次上传）或本次清理删掉了旧设备（旧 AddTags 注册的
                // 点刚被整体删除）：未变更块在 IGW 上已无（或从未有）新注册，只注册变更块会让它们读不到 → 本次自动升级为全量
                var ledger = await _store.GetLedgerAsync(project.ProjectId).ConfigureAwait(false) ?? IgwLedgerSnapshot.Empty;
                string escalation = null;
                if (!args.Full)
                {
                    if (identityChange != null)
                    {
                        escalation = identityChange;
                    }
                    else if (!ledger.HasRootKind(IgwLedgerRootKind.DbBlock) && !ledger.HasRootKind(IgwLedgerRootKind.Io))
                    {
                        escalation = "台账无 DB/IO 注册记录（首次批量注册）";
                    }
                    // 注意：升级后的全量运行会重发台账里 Rejected 的根（全量不区分状态），其隔离重发次数受 MaxIsolationRequests 限制；
                    // 只有台账同时有瞬态 Failed 行（下一分支）或旧设备被清理时才会升级，不会因 Rejected 行本身反复升级
                    else if (!ledger.RegisteredRootKinds.Contains(IgwLedgerRootKind.DbBlock)
                             && !ledger.RegisteredRootKinds.Contains(IgwLedgerRootKind.Io)
                             && (ledger.FailedDbBlocks.Count > 0 || ledger.HasFailedIo))
                    {
                        // 首次（全量）注册瞬态失败后的 Hangfire 重试仍带原增量参数：台账无成功行且有瞬态失败行 → 继续按全量
                        // （台账只有 Rejected 行时不升级：永久被拒的根只在自身变更或按钮全量时重发）
                        escalation = "台账无注册成功的 DB/IO 记录（此前注册失败）";
                    }
                    else if (removedLegacyDevices.Count > 0)
                    {
                        escalation = $"本次清理了 {removedLegacyDevices.Count} 个旧设备";
                    }
                }
                var full = args.Full || escalation != null;
                if (escalation != null)
                {
                    _logger.LogInformation("[IGW注册] 项目 {ProjectId} 增量注册自动升级为全量：{Reason}", project.ProjectId, escalation);
                }

                // 增量：台账里 Failed / FailedPendingClear 的块并入变更范围整块重做（不再存活的由 ResolveScope 按存活块过滤掉）、Io 行 Failed 视为 IO 已刷新——
                // 否则失败的根只能等按钮全量兜底（Hangfire 重试 / 下次上传只重做本次变更块）；整块重做时规划器随之重发另一设备上的清除组。
                // PendingClear 块（内容已写入，只剩台账 DeviceKind 以外另一台设备上的旧组清除）不并入变更范围：只对那台设备重发清除组
                // （ClearOnly），块自身内容不重发——重发会让本设备每次上传都重启一次；台账 DeviceKind 缺失时退回整块重做
                var ioRefreshed = args.IoRefreshed;
                var retriedBlocks = new List<string>();
                var clearOnlyCandidates = new List<string>();
                if (!full)
                {
                    bool Unchanged(string b) => !changedArg.Contains(b, StringComparer.OrdinalIgnoreCase);
                    clearOnlyCandidates = ledger.PendingClearDbBlocks
                        .Where(Unchanged)
                        .Where(b => ledger.DeviceKindByBlock.TryGetValue(b, out var kind) && kind is IgwDeviceKind.S7 or IgwDeviceKind.S7Plus)
                        .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    retriedBlocks = ledger.FailedDbBlocks
                        .Concat(ledger.FailedPendingClearDbBlocks)
                        .Concat(ledger.PendingClearDbBlocks.Except(clearOnlyCandidates, StringComparer.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(Unchanged)
                        .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    changedArg = changedArg.Concat(retriedBlocks).ToList();
                    ioRefreshed = ioRefreshed || ledger.HasFailedIo;
                }

                // ---------- 取数 ----------
                var includeIo = ioRefreshed || full;
                var data = await _store.LoadDataAsync(project.ProjectId, includeIo, full ? null : changedArg).ConfigureAwait(false);
                if (data == null)
                {
                    // 绝不能把「取数失败」当成「项目无存活块」：ResolveScope 会把台账里所有块当作已不存在而清空——
                    // 必须直接失败，让调用方看到、重试，而不是静默清光整个台账
                    return await FailAsync(project, "读取项目数据失败（LoadDataAsync 返回空）").ConfigureAwait(false);
                }

                // 展示用：重试的失败块里排除已不存活的（不再计入"含重试失败块 N"，避免把已删除块的历史失败也算进重试数）
                if (!full && retriedBlocks.Count > 0)
                {
                    var liveForRetry = new HashSet<string>(data.LiveDbBlocks ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                    retriedBlocks = retriedBlocks.Where(liveForRetry.Contains).ToList();
                }

                var scope = ResolveScope(full, changedArg, deletedArg, clearOnlyCandidates, data, ledger);
                if (!full)
                {
                    // 只追加注册（2026-09-22 用户拍板）：上传解析这条链路只负责新增。
                    // PLC 里删掉的块不去清 IGW，台账行也一并保留——不清 IGW 却删台账会让两边失配，
                    // 读链还会丢掉这个块的改名映射。真要收拾残留走「注册PLC标签」全量。
                    scope = scope with { ReplaceOnly = Array.Empty<string>() };
                }
                if (!full && scope.Changed.Count == 0 && scope.ReplaceOnly.Count == 0 && scope.ClearOnly.Count == 0 && !ioRefreshed)
                {
                    var noop = "本次变更块均不是已存在的 DB 块、删除块也从未注册过，无需注册";
                    var noopGraph = await RequestGraphIfNeededAsync(project, removedLegacyDevices.Concat(identityRemovals).ToList(), ledger, args.Full,
                        identityChange != null, identitySavedThisRun: false).ConfigureAwait(false);
                    if (noopGraph != null)
                    {
                        noop += "；" + noopGraph;
                    }
                    await WriteLogSafeAsync($"[IGW注册] 项目 {Describe(project)}：{noop}").ConfigureAwait(false);
                    return new IgwRegistrationOutcome(IgwRegistrationStatus.Succeeded, noop);
                }

                // ---------- 规划 ----------
                var plan = IgwRegistrationPlanner.Plan(new PlannerInput(channelName, plcIp, project.PlcPort,
                    data.DbRows ?? Array.Empty<PlannerDbRow>(), data.IoTags ?? Array.Empty<PlannerIoTag>(),
                    scope.Changed, scope.ReplaceOnly, ioRefreshed, full, _config, ledger.RootGroupByBlock,
                    data.LiveDbBlocks, data.GraphDbBlocks,
                    // 只追加注册（2026-09-22）：按**调用方原始意图**判，不看升级后的 full。
                    //   上传 / CSV 导入（args.Full=false）→ 只追加：指纹一致的根整根跳过，永不发 replaceGroups
                    //   「注册PLC标签」按钮（args.Full=true）→ 保持旧的全删重建，留作人工兜底（用户 2026-09-22 决定）
                    // 增量被自动升级为全量的几种情形（台账无行 / 旧设备刚被清理 / 注册身份变化）
                    // 下沿用只追加是安全的：那几种情形下 IGW 上本来就没有该留的旧内容可清。
                    // 注意用 full 不是 args.Full：增量自动升级为全量（首次注册、旧设备被删、注册身份变化）
                    // 必须按全量语义跑，否则这层安全网会被只追加悄悄架空
                    AppendOnly: !full,
                    PendingClearRoots: full ? null : BuildPendingClearRoots(scope.Changed, ledger),
                    RootStates: BuildRootStates(ledger)));

                // ---------- 只重发旧组清除（PendingClear 块）：每台设备追加独立的纯清除请求，排在该设备规划请求之后 ----------
                var clearOnly = ResolveClearOnly(scope, plan, ledger, out var clearOnlySkipped);
                if (clearOnlySkipped.Count > 0)
                {
                    _logger.LogWarning("[IGW注册] 项目 {ProjectId} {Count} 个待清除块的根组与台账不一致或未分配，本次跳过其旧组清除（台账保持 PendingClear）：{Blocks}",
                        project.ProjectId, clearOnlySkipped.Count, string.Join("、", clearOnlySkipped));
                }
                var clearRequests = BuildClearOnlyRequests(clearOnly, channelName, plcIp, project.PlcPort);

                // ---------- 发送：S7 → S7Plus，同设备串行；IGW 按内容拒绝（400/413）时按根组定位/二分隔离，其余根组照常写入 ----------
                var unitResults = new Dictionary<IgwDeviceKind, Dictionary<string, UnitResult>>();
                var deviceSummaries = new List<string>();
                var rejectedRoots = new List<string>();
                foreach (var device in new[] { IgwDeviceKind.S7, IgwDeviceKind.S7Plus })
                {
                    var units = new Dictionary<string, UnitResult>(StringComparer.OrdinalIgnoreCase);
                    unitResults[device] = units;
                    var requests = plan.Requests.Where(r => r.Device == device).Select(r => r.Request)
                        .Concat(clearRequests.Where(r => r.Device == device).Select(r => r.Request))
                        .ToList();
                    if (requests.Count == 0)
                    {
                        deviceSummaries.Add($"{device}：无请求");
                        continue;
                    }
                    deviceSummaries.Add(await SendDeviceAsync(project, device, requests, units, rejectedRoots, ct).ConfigureAwait(false));
                }

                // ---------- 台账 ----------
                // M-4 竞态：本次执行期间项目被删除（删项目的清理任务可能已经清过 IGW）→ 台账 / 身份都不再写。
                // 否则已删除项目会留下"活的"注册记录：清理任务只跑一次，之后没人认领这些记录与它指向的设备
                var projectRemoved = await IsProjectRemovedAsync(project.ProjectId, identityNotes).ConfigureAwait(false);
                string removedDuringRunNote = null;
                string ledgerError = null;
                List<string> deadRoots = null;
                if (projectRemoved)
                {
                    removedDuringRunNote = "项目已删除（在本次注册执行期间）：未写入台账、未登记注册身份；"
                        + "本次写入 IGW 的内容由删除项目的清理任务处理（必要时可在 Hangfire 面板重新入队该清理）";
                    _logger.LogWarning("[IGW注册] 项目 {ProjectId} 在注册执行期间被删除，跳过台账写入与身份登记", project.ProjectId);
                }
                else
                {
                    var (ledgerRows, removedRoots, ledgerDeadRoots) = BuildLedger(plan, unitResults, scope, clearOnly, ledger, includeIo);
                    deadRoots = ledgerDeadRoots;
                    try
                    {
                        await _store.ReplaceLedgerAsync(project.ProjectId, ledgerRows, removedRoots).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        ledgerError = ex.GetBaseException().Message;
                        _logger.LogError(ex, "[IGW注册] 项目 {ProjectId} 台账写入失败", project.ProjectId);
                    }
                }
                IgwAliasCacheInvalidation.Invalidate(project.ProjectId);

                // I-3：至少一个根组写入了 IGW → 登记本次注册身份（全部失败则保留旧身份，下次继续判定变化并清理）。
                // 登记前再查一次冲突（缩小与另一项目并发注册的竞态窗口）：此时出现阻断冲突 → 不登记、本次记失败
                string identityConflictAtSave = null;
                var identitySavedThisRun = false;
                if (_stateStore != null && identity != null && !stateTableUnavailable && !projectRemoved
                    && unitResults.Values.SelectMany(m => m.Values).Any(u => u.Applied))
                {
                    try
                    {
                        var recheck = IgwIdentityConflicts.Evaluate(identity,
                            await _stateStore.ListPeerProjectsAsync(project.ProjectId).ConfigureAwait(false));
                        if (recheck.Blocking.Count > 0)
                        {
                            identityConflictAtSave = "登记注册身份前发现冲突（本次已写入 IGW，未登记身份）：" + string.Join("；", recheck.Blocking)
                                + "。请删除重复项目或改正项目 PLC IP / 通道后重新注册；本次已写入的数据留在 IGW 新位置（通道 " + channelName
                                + "），冲突解决后重新注册时会复用（位置不变）或随注册身份变化自动清理（位置改变）";
                        }
                        else
                        {
                            await _stateStore.SaveIdentityAsync(project.ProjectId, identity).ConfigureAwait(false);
                            identitySavedThisRun = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[IGW注册] 项目 {ProjectId} 登记注册身份失败（下次注册会重复判定身份变化，幂等）", project.ProjectId);
                        identityNotes.Add(IgwSqlErrorClassifier.IsUndefinedTable(ex)
                            ? $"注册状态表不可用（表不存在），本次未登记注册身份（请执行迁移脚本建表）：{ex.GetBaseException().Message}"
                            : $"登记注册身份失败（不影响本次注册，下次注册会重新判定，幂等）：{ex.GetBaseException().Message}");
                    }
                }

                // ---------- GRAPH 重注册（DB 注册之后；与 DB/IO 结果无关——旧 GRAPH 设备一旦删掉就必须重建） ----------
                // 项目已删除时不再请求（清理任务正在/已经清空这个项目的 IGW 位置）
                var graphNote = projectRemoved
                    ? null
                    : await RequestGraphIfNeededAsync(project, removedLegacyDevices.Concat(identityRemovals).ToList(), ledger, args.Full,
                        identityChange != null, identitySavedThisRun).ConfigureAwait(false);

                // ---------- 汇总 / 日志 ----------
                var failed = unitResults.Values.SelectMany(m => m.Values).Any(u => u.Failed) || ledgerError != null
                    || identityConflictAtSave != null;
                var dbSkipped = plan.Blocks.Sum(b => b.Skipped);
                var ioSkipped = IoSkipped(plan.SkipReasons);
                var mode = args.Full
                    ? "全量"
                    : escalation != null
                        ? $"全量（由增量自动升级：{escalation}）"
                        : $"增量（变更块 {scope.Changed.Count}（含重试失败块 {retriedBlocks.Count}），含待清除块 {clearOnly.Count}（只重发另一设备旧组清除），"
                          + $"清除块 {scope.ReplaceOnly.Count}，IO 刷新 {(ioRefreshed ? "是" : "否")}）";
                var detail = new StringBuilder()
                    .Append($"通道 {channelName}，{mode}；")
                    .Append(string.Join("；", deviceSummaries))
                    .Append($"；IO 点 {plan.IoTagCount}；跳过 DB 行 {dbSkipped}、IO {ioSkipped}");
                var topReasons = FormatReasons(plan.SkipReasons);
                if (topReasons.Length > 0)
                {
                    detail.Append($"（{topReasons}）");
                }
                detail.Append(removedLegacy.Count > 0
                    ? $"；删除旧设备 {removedLegacy.Count} 个：{string.Join("、", removedLegacy)}"
                    : "；无旧设备需清理");
                if (identityChange != null && args.Full)
                {
                    detail.Append($"；{identityChange}");
                }
                if (clearOnlySkipped.Count > 0)
                {
                    detail.Append($"；{clearOnlySkipped.Count} 个待清除块根组不一致（{string.Join("、", clearOnlySkipped.Take(TopReasons))}"
                        + $"{(clearOnlySkipped.Count > TopReasons ? " 等" : string.Empty)}），本次未清除其旧组，需「注册PLC标签」全量");
                }
                if (deadRoots is { Count: > 0 })
                {
                    detail.Append($"；{deadRoots.Count} 个根组名无法生成的块未清除，需人工处理（{string.Join("、", deadRoots.Take(TopReasons))}"
                        + $"{(deadRoots.Count > TopReasons ? " 等" : string.Empty)}）");
                }
                foreach (var note in identityNotes)
                {
                    detail.Append($"；{note}");
                }
                if (rejectedRoots.Count > 0)
                {
                    detail.Append($"；IGW 拒绝（不自动重试，块变更或全量注册时重发）：{string.Join("；", rejectedRoots.Take(TopReasons))}");
                    if (rejectedRoots.Count > TopReasons)
                    {
                        detail.Append($" 等 {rejectedRoots.Count} 个");
                    }
                }
                if (graphNote != null)
                {
                    detail.Append($"；{graphNote}");
                }
                if (removedDuringRunNote != null)
                {
                    detail.Append($"；{removedDuringRunNote}");
                }
                if (ledgerError != null)
                {
                    detail.Append($"；台账写入失败：{ledgerError}");
                }
                if (identityConflictAtSave != null)
                {
                    detail.Append($"；{identityConflictAtSave}");
                }
                detail.Append($"；总耗时 {total.Elapsed.TotalSeconds:0.0}s");

                await WriteLogSafeAsync($"[IGW注册] 项目 {Describe(project)} {(failed ? "失败" : "完成")}：{detail}").ConfigureAwait(false);
                _logger.LogInformation("[IGW注册] 项目 {ProjectId} {Result}：{Detail}", project.ProjectId, failed ? "失败" : "完成", detail);
                return new IgwRegistrationOutcome(failed ? IgwRegistrationStatus.Failed : IgwRegistrationStatus.Succeeded, detail.ToString());
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "[IGW注册] 项目 {ProjectId} 执行异常", project.ProjectId);
                return await FailAsync(project, $"执行异常：{ex.GetBaseException().Message}").ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 注册身份变化时清理旧位置（I-3）：删除旧通道中本项目带批量标记的设备（通道空了按需删除），结果写进
        /// <paramref name="notes"/> / <paramref name="removals"/>。返回 null = 可以继续注册；否则为失败原因（不发任何请求、不登记新身份）：
        /// 同一 IGW 清理失败 / 超时；同一 IGW 上被跳过的无批量标记设备与新身份的逻辑设备名相同（新设备会与之重名，需人工清理）；
        /// 另一 IGW 可达但拒绝删除。另一 IGW 不可达 / 超时 → 只记提示，继续。
        /// </summary>
        private async Task<string> CleanPreviousLocationAsync(IgwRegistrationIdentity previous, IgwRegistrationIdentity identity, bool sameIgw,
            bool deleteChannelIfEmpty, List<string> notes, List<IgwLegacyDeviceRemoval> removals, CancellationToken ct)
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(IdentityCleanupDeadline);
            IgwProjectDeviceCleanup cleanup;
            try
            {
                cleanup = await _client.RemoveProjectDevicesAsync(previous.IgwBaseUrl, previous.ChannelName, previous.PlcIp,
                    deleteChannelIfEmpty, deadlineCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var deadlineHit = deadlineCts.IsCancellationRequested;
                var reason = deadlineHit
                    ? $"清理超过 {IdentityCleanupDeadline.TotalSeconds:0.#}s 未完成"
                    : ex.GetBaseException().Message;
                if (sameIgw)
                {
                    // 同一 IGW：旧 IP 的 dcsLogicalDevice 残留会让新别名解析不到 / 冲突 → 不能继续
                    return $"清理旧位置失败（未发送任何注册请求）：{reason}";
                }
                // 注：删除过程中连接中断时，RemoveProjectDevicesAsync 把逐设备删除错误折叠成 InvalidOperationException，
                // 这里会按「拒绝」处理（任务 Failed、身份不登记、下次重试）而非「不可达」——偏保守，可接受
                if (!deadlineHit && !IsUnreachable(ex))
                {
                    // 旧 IGW 可达但拒绝删除：不登记新身份，下次注册继续清理
                    return $"旧 IGW（{previous.IgwBaseUrl}）拒绝清理旧位置（未发送任何注册请求，下次注册重试）：{reason}";
                }
                notes.Add($"旧 IGW（{previous.IgwBaseUrl}）不可达，清理已跳过，请手工删除其通道 {previous.ChannelName} 中本项目的设备：{reason}");
                return null;
            }

            removals.AddRange(cleanup.Removed);
            var text = $"旧位置清理：删除设备 {cleanup.Removed.Count} 个";
            if (cleanup.ChannelDeleted)
            {
                text += $"，删除空通道 {previous.ChannelName}";
            }
            if (cleanup.SkippedUnmanaged.Count > 0)
            {
                text += $"，跳过无批量标记设备 {cleanup.SkippedUnmanaged.Count} 个（{string.Join("、", cleanup.SkippedUnmanaged.Select(x => x.Description))}，请人工核对）";
            }
            notes.Add(text);

            if (sameIgw)
            {
                // IGW 内 DCS 逻辑设备名全局解析：留下的无标记设备若与新身份的逻辑名相同，新建设备会与之重名
                var newNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    IgwTagNaming.LogicalDeviceName(IgwDeviceKind.S7, identity.PlcIp),
                    IgwTagNaming.LogicalDeviceName(IgwDeviceKind.S7Plus, identity.PlcIp),
                    IgwTagNaming.LogicalDeviceName(IgwDeviceKind.Graph, identity.PlcIp),
                };
                var colliding = cleanup.SkippedUnmanaged.Where(x => newNames.Contains(x.LogicalName)).ToList();
                if (colliding.Count > 0)
                {
                    return $"旧位置有 {colliding.Count} 个无批量标记设备与新位置的逻辑设备名相同，不能自动删除（未发送任何注册请求）："
                        + $"{string.Join("、", colliding.Select(x => x.Description))}。请在 IGW 上人工删除这些设备后重新注册";
                }
            }
            return null;
        }

        /// <summary>
        /// 旧位置被另一项目占用且与新位置同 IGW、同 PLC IP 时（I-3）：只读查询 IGW 上旧通道里逻辑名属于新身份
        /// （S7_/S1500_/S7Plus_{ip}）的设备。Failure = null → 已无此类设备（或旧通道不存在，见 ChannelFound），可继续注册（不删任何东西）；
        /// 否则为失败原因（列出设备与可行的处理方式）。读取失败 / 超时 → 失败（同一 IGW 本身必须可达）。
        /// </summary>
        private async Task<(string Failure, bool ChannelFound)> CheckOccupiedOldChannelAsync(IgwRegistrationIdentity previous, IgwRegistrationIdentity identity,
            IgwPeerProjectRow occupant, CancellationToken ct)
        {
            var owner = $"项目 {occupant.ProjectName}({occupant.ProjectId})";
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(IdentityCleanupDeadline);
            IgwProjectDeviceScan scan;
            try
            {
                scan = await _client.FindProjectDevicesAsync(previous.IgwBaseUrl, previous.ChannelName, identity.PlcIp, deadlineCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var reason = deadlineCts.IsCancellationRequested
                    ? $"读取超过 {IdentityCleanupDeadline.TotalSeconds:0.#}s 未完成"
                    : ex.GetBaseException().Message;
                return ($"旧位置与{owner}的登记/配置相同，读取 IGW 旧通道 {previous.ChannelName} 失败，无法确认是否有重名设备（未发送任何注册请求）：{reason}", false);
            }
            var remaining = scan.Devices;
            if (remaining.Count == 0)
            {
                return (null, scan.ChannelFound);
            }
            return ($"旧位置与{owner}的登记/配置相同，不能自动清理；IGW 旧通道 {previous.ChannelName} 中仍有 {remaining.Count} 个设备与新位置的逻辑设备名相同"
                + $"（同一 PLC IP，新建设备会与之重名）：{string.Join("、", remaining.Select(x => x.Description))}（未发送任何注册请求）。"
                + $"处理方式（任选其一）：① 删除 / 合并重复的{owner}，或改正其 IotUrl / PLC IP / 通道后重新注册（本项目届时会自动清理旧位置）；"
                + $"② 在 IGW 上人工删除通道 {previous.ChannelName} 中上述设备后重新注册", true);
        }

        /// <summary>
        /// 项目是否已被删除（写台账 / 登记身份前复查，M-4 竞态）。查询失败 → false（按未删除处理，保持原有行为：
        /// 宁可给存活项目写台账，也不能因为一次查询失败就丢掉本次注册结果）。
        /// </summary>
        private async Task<bool> IsProjectRemovedAsync(long projectId, List<string> notes)
        {
            try
            {
                return await _store.IsProjectRemovedAsync(projectId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IGW注册] 项目 {ProjectId} 复查项目是否已删除失败（按未删除处理，照常写台账）", projectId);
                notes.Add($"复查项目是否已删除失败（本次按未删除处理，照常写台账 / 登记身份）：{ex.GetBaseException().Message}");
                return false;
            }
        }

        /// <summary>旧 IGW 不可达：连接失败 / 超时（非调用方取消，调用方取消在 catch 过滤器已排除）</summary>
        private static bool IsUnreachable(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Net.Http.HttpRequestException || e is TimeoutException || e is OperationCanceledException)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 需要时请求一次全项目 GRAPH 重注册，返回写进结果详情的说明（无需请求 → null）。触发条件（按顺序）：
        /// ① 本次清理删掉了可承载 GRAPH 的旧设备——不受冷却限制；
        /// ② 注册身份变化且项目有 GRAPH 目标（新位置还没有 GRAPH 设备；旧 IGW 不可达 / 旧设备未删时台账仍有 Graph 行、①不成立）：
        ///    <paramref name="identitySavedThisRun"/>（本次登记了新身份，完成这次身份变化）→ 必定请求、不受冷却限制——
        ///    之后的运行不再判定身份变化，台账 Graph 行也还在，此时不请求 GRAPH 重建就永远丢了；
        ///    新身份未登记（本次 DB/IO 失败 / 冲突，下次还会走到这里）→ 每次身份变化只绕过冷却一次：上次登记旧身份（RegisteredAt）
        ///    之后已记录 GRAPH 失败，视为本次身份变化触发的 GRAPH 已失败过 → 按 N-6 冷却
        ///    （代价：身份变化前恰好有 GRAPH 失败时也会按冷却，只延迟有限的冷却时间）；
        /// ③ 台账无 Graph 行而项目有 GRAPH 目标——GRAPH 注册上次写台账前失败后的冷却期内跳过（N-6）。
        /// 冷却对 <paramref name="explicitRequest"/>（「注册PLC标签」全量）不生效。
        /// 请求失败只记日志（DB/IO 注册结果不受影响，可用 GRAPH 补跑入口手工重跑）。
        /// </summary>
        private async Task<string> RequestGraphIfNeededAsync(IgwRegistrationProjectInfo project,
            IReadOnlyList<IgwLegacyDeviceRemoval> removedDevices, IgwLedgerSnapshot ledger, bool explicitRequest, bool identityChanged,
            bool identitySavedThisRun)
        {
            if (_requestGraphRegistration == null)
            {
                return null;
            }
            string reason = null;
            try
            {
                if (removedDevices.Any(x => x.IsGraphCapable))
                {
                    reason = "本次清理删除了 GRAPH 设备（通用_S7Plus / S7Plus_{ip}）";
                }
                else if (identityChanged && await _store.HasGraphTargetsAsync(project.ProjectId).ConfigureAwait(false))
                {
                    if (!identitySavedThisRun && await GraphFailedSinceIdentitySavedAsync(project.ProjectId).ConfigureAwait(false))
                    {
                        var cooldownNote = await GraphCooldownNoteAsync(project.ProjectId, explicitRequest).ConfigureAwait(false);
                        if (cooldownNote != null)
                        {
                            return cooldownNote;
                        }
                    }
                    reason = "注册身份变化，新位置需重建 GRAPH 设备";
                }
                else if (!ledger.HasRootKind(IgwLedgerRootKind.Graph)
                         && await _store.HasGraphTargetsAsync(project.ProjectId).ConfigureAwait(false))
                {
                    var cooldownNote = await GraphCooldownNoteAsync(project.ProjectId, explicitRequest).ConfigureAwait(false);
                    if (cooldownNote != null)
                    {
                        return cooldownNote;
                    }
                    reason = "项目有 GRAPH 步进 DB 但 IGW 台账无 GRAPH 注册记录";
                }
                if (reason == null)
                {
                    return null;
                }
                await _requestGraphRegistration(project.ProjectId, reason).ConfigureAwait(false);
                _logger.LogInformation("[IGW注册] 项目 {ProjectId} 已请求全项目 GRAPH 步位重注册：{Reason}", project.ProjectId, reason);
                return $"已触发 GRAPH 步位全项目重注册（{reason}）";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IGW注册] 项目 {ProjectId} 请求 GRAPH 重注册失败", project.ProjectId);
                return $"GRAPH 步位重注册请求失败（{reason ?? "判定 GRAPH 目标时出错"}）：{ex.GetBaseException().Message}，可手工补跑 GRAPH 步位注册";
            }
        }

        /// <summary>
        /// 上次登记注册身份（RegisteredAt）之后是否记录过 GRAPH 失败（RegisteredAt 缺失时有失败记录即算；用法见
        /// <see cref="RequestGraphIfNeededAsync"/> ②）。无状态存储 / 读状态失败 → false。
        /// </summary>
        private async Task<bool> GraphFailedSinceIdentitySavedAsync(long projectId)
        {
            if (_stateStore == null)
            {
                return false;
            }
            try
            {
                var state = await _stateStore.GetStateAsync(projectId).ConfigureAwait(false);
                return state?.GraphFailedAt != null
                    && (state.RegisteredAt == null || state.GraphFailedAt.Value > state.RegisteredAt.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IGW注册] 项目 {ProjectId} 读取 GRAPH 失败记录失败（按无失败处理）", projectId);
                return false;
            }
        }

        /// <summary>GRAPH 失败冷却中 → 返回说明；否则 null（无状态存储 / 显式请求 / 无失败记录 / 已过冷却 / 读状态失败）</summary>
        private async Task<string> GraphCooldownNoteAsync(long projectId, bool explicitRequest)
        {
            if (explicitRequest || _stateStore == null)
            {
                return null;
            }
            IgwRegistrationStateRow state;
            try
            {
                state = await _stateStore.GetStateAsync(projectId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IGW注册] 项目 {ProjectId} 读取 GRAPH 失败记录失败（按无冷却处理）", projectId);
                return null;
            }
            if (state?.GraphFailedAt == null)
            {
                return null;
            }
            var cooldown = TimeSpan.FromMinutes(Math.Max(0, _config.GraphRetryCooldownMinutes));
            var elapsed = _now() - state.GraphFailedAt.Value;
            if (elapsed >= cooldown)
            {
                return null;
            }
            var remaining = (int)Math.Ceiling((cooldown - elapsed).TotalMinutes);
            return $"GRAPH 步位注册上次失败（{state.GraphFailedAt.Value:yyyy-MM-dd HH:mm:ss}：{Truncate(state.GraphFailure, 200)}），"
                   + $"{remaining} 分钟内不自动重试（可点「批量绑定工站」或「注册PLC标签」立即重试）";
        }

        /// <summary>
        /// 规划范围：Changed = 带行的变更 DB 块；ReplaceOnly = 需从 IGW 两台设备上清除的根（均限于台账登记过的块）；
        /// ClearOnly = 只重发旧组清除的存活 PendingClear 块（不在 Changed 中：不加载接口行、不重发内容）
        /// </summary>
        private sealed record Scope(IReadOnlyList<string> Changed, IReadOnlyList<string> ReplaceOnly, IReadOnlyList<string> ClearOnly);

        /// <summary>一个只重发旧组清除的块：在 Device 上 replaceGroups [Root]（Device = 台账 DeviceKind 以外的另一台设备）</summary>
        private sealed record ClearOnlyBlock(string Block, IgwDeviceKind Device, string Root);

        /// <summary>
        /// 解析本次范围：
        /// Changed = 全量时全部存活 DB 块，否则 参数变更块 ∩ 存活 DB 块；只保留有叶子行的块（无行的块归入清除）。
        /// ReplaceOnly = （参数删除块 − 存活块）∪（无行的变更块）∪（台账块 − 存活块），且只保留台账里登记过的块——
        /// 从未注册过的名字（如被删除/变更的 FB、FC）在本通道下不存在，无需发 replaceGroups。
        /// ClearOnly = 增量时 PendingClear 候选块 ∩ 存活块 − 变更候选块（不再存活的 PendingClear 块经 ReplaceOnly 在两台设备上清除）。
        /// </summary>
        /// <summary>
        /// 台账快照 → 规划器要的「上次注册成什么样」。跨层映射放在这里：
        /// IDbService 不认识规划器的类型，规划器也不该认识台账行。
        ///
        /// DeviceKind 解析不出来（台账里是自由文本，历史行可能为空）→ **不给该根建条目**，
        /// 于是规划器按"没记过"处理、照常重发。宁可多发一次，不可因为认不出设备而误跳过。
        /// </summary>
        /// <summary>
        /// 只追加模式下，本轮要重发内容的块里，哪些还欠着旧组清除。
        ///
        /// 未变更的 PendingClear 块由 runner 的 ClearOnly 路径单独发纯清除请求；这里补的是**变更块**——
        /// 它们以前靠整块重做顺带把两台设备的 replaceGroups 发出去，而只追加模式已经不发 replaceGroups 了，
        /// 不补的话这份义务会被静默丢掉，旧组永远留在设备上。
        ///
        /// 台账不记是哪台设备待清（块可能搬走又搬回来，欠的就是它当前这台），所以交给规划器在两台上都发。
        /// </summary>
        private static List<string> BuildPendingClearRoots(IReadOnlyList<string> changed, IgwLedgerSnapshot ledger)
        {
            if (ledger == null || changed == null) return new List<string>();
            var owed = new HashSet<string>(ledger.PendingClearDbBlocks, StringComparer.OrdinalIgnoreCase);
            owed.UnionWith(ledger.FailedPendingClearDbBlocks);
            return changed.Where(b => b != null && owed.Contains(b)).ToList();
        }

        private static IReadOnlyDictionary<string, PlannerRootState> BuildRootStates(IgwLedgerSnapshot ledger)
        {
            var states = new Dictionary<string, PlannerRootState>(StringComparer.OrdinalIgnoreCase);
            if (ledger?.DbBlockStats == null) return states;
            foreach (var (block, stats) in ledger.DbBlockStats)
            {
                if (string.IsNullOrWhiteSpace(block) || stats == null) continue;
                if (!Enum.TryParse<IgwDeviceKind>(stats.DeviceKind, ignoreCase: true, out var device)) continue;
                // 指纹可以为空（存量行都没有）——规划器自己会把空指纹当"必须重发"。
                // 但**设备**一栏不能因此丢掉：块换设备时要靠它清掉旧设备上那一份。
                states[block] = new PlannerRootState(device, stats.Fingerprint);
            }
            return states;
        }

        private static Scope ResolveScope(bool full, List<string> changedArg, List<string> deletedArg, List<string> clearOnlyCandidates,
            IgwRegistrationData data, IgwLedgerSnapshot ledger)
        {
            var live = new HashSet<string>((data.LiveDbBlocks ?? Array.Empty<string>()).Where(x => x != null), StringComparer.OrdinalIgnoreCase);
            var withRows = new HashSet<string>((data.DbRows ?? Array.Empty<PlannerDbRow>()).Select(r => r.BlockName).Where(x => x != null),
                StringComparer.OrdinalIgnoreCase);
            var ledgerBlocks = new HashSet<string>(ledger.RootGroupByBlock.Keys.Concat(ledger.DeviceKindByBlock.Keys), StringComparer.OrdinalIgnoreCase);

            var changedCandidates = full
                ? (data.LiveDbBlocks ?? Array.Empty<string>()).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : changedArg.Where(live.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var changed = changedCandidates.Where(withRows.Contains).ToList();

            var replaceOnly = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddReplace(string name)
            {
                if (ledgerBlocks.Contains(name) && seen.Add(name))
                {
                    replaceOnly.Add(name);
                }
            }
            foreach (var name in deletedArg.Where(n => !live.Contains(n)))
            {
                AddReplace(name);
            }
            foreach (var name in changedCandidates.Where(n => !withRows.Contains(n)))
            {
                AddReplace(name);
            }
            // 台账登记过、但已不是存活 DB 块的根：全量与增量一致地清除（R-2：否则增量运行里 Failed 残留的已删块、
            // 上次清除没写入的块会一直挂在台账与 IGW 上，直到下一次全量）
            foreach (var name in ledgerBlocks.Where(n => !live.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                AddReplace(name);
            }
            var clearOnly = full
                ? new List<string>()
                : clearOnlyCandidates.Where(live.Contains)
                    .Where(n => !changedCandidates.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            return new Scope(changed, replaceOnly, clearOnly);
        }

        /// <summary>
        /// 只重发清除的块 → (设备, 根组)。设备 = 台账 DeviceKind 以外的另一台设备；根组取规划器本次分配的根组，
        /// 且必须与台账记录的根组一致（规划器按台账沿用根组，几乎总是一致）——不一致时本次跳过该块（台账行原样保留 PendingClear），
        /// 避免按一个可能属于其它块的根组名清除；全量注册会整块重做。
        /// <paramref name="skipped"/> = 因规划器未分配根组或根组与台账不一致而跳过的块（调用方告警并提示全量）。
        /// </summary>
        private static List<ClearOnlyBlock> ResolveClearOnly(Scope scope, PlanResult plan, IgwLedgerSnapshot ledger, out List<string> skipped)
        {
            var result = new List<ClearOnlyBlock>();
            skipped = new List<string>();
            foreach (var block in scope.ClearOnly)
            {
                if (!ledger.DeviceKindByBlock.TryGetValue(block, out var kind))
                {
                    continue;
                }
                if (!plan.BlockRoots.TryGetValue(block, out var root)
                    || (ledger.RootGroupByBlock.TryGetValue(block, out var ledgerRoot)
                        && !string.Equals(ledgerRoot, root, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped.Add(block);
                    continue;
                }
                result.Add(new ClearOnlyBlock(block, kind == IgwDeviceKind.S7 ? IgwDeviceKind.S7Plus : IgwDeviceKind.S7, root));
            }
            return result;
        }

        /// <summary>只重发清除的块 → 每台设备的纯清除请求（replaceGroups，不带点；每请求至多 MaxReplaceGroupsPerRequest 组）</summary>
        private List<PlannedRequest> BuildClearOnlyRequests(IReadOnlyList<ClearOnlyBlock> clearOnly, string channelName, string plcIp, int plcPort)
        {
            var requests = new List<PlannedRequest>();
            foreach (var group in clearOnly.GroupBy(c => c.Device).OrderBy(g => g.Key))
            {
                foreach (var chunk in group.Chunk(IgwRegistrationPlanner.MaxReplaceGroupsPerRequest))
                {
                    var request = new IgwBulkRequest
                    {
                        Channel = IgwRegistrationPlanner.ChannelSpec(channelName),
                        Device = IgwRegistrationPlanner.DeviceSpec(group.Key, plcIp, plcPort, _config.PlcModel),
                        ReplaceGroups = chunk.Select(c => new List<string> { c.Root }).ToList(),
                    };
                    requests.Add(new PlannedRequest(group.Key, request, chunk.Select(c => c.Block).ToList()));
                }
            }
            return requests;
        }

        /// <summary>
        /// 串行发送一台设备的请求，按根组单元记账（R-1）：
        /// 200 → 单元已写入；瞬态失败 / runtime_apply_failed → 本单元失败并停发该设备后续请求（记「未发送」）；
        /// 与内容无关的永久拒绝（channel_driver_mismatch / template_instance_not_supported / invalid_request / invalid_if_match）
        /// → 本请求单元记瞬态 Failed（带 IGW 错误原文，下次注册自动重试）并停发该设备；
        /// 内容型永久拒绝 → 有逐项下标则拒绝对应单元（Rejected）、剔除后重发其余，否则按单元二分；
        /// 单个单元被无下标拒绝 → 仅当同一原请求有单元写入成功时记 Rejected，否则记瞬态 Failed（见 <see cref="Origin"/>）；
        /// 二分到底原请求每个单元都被无下标拒绝 → 与单个根组内容无关（如设备现有状态导致的地址模式混用），停发该设备。
        /// 已失败 / 被拒的单元从该设备后续请求中剔除——大根组被规划器拆成多片、清除组只挂首片，首片没写入时
        /// 后续分片不能再发（否则会追加进一个从未被替换的组）；单元只有全部分片都写入才算成功。
        /// 隔离重发次数受 MaxIsolationRequests 限制，超限的单元记 Failed。
        /// </summary>
        private async Task<string> SendDeviceAsync(IgwRegistrationProjectInfo project, IgwDeviceKind device,
            IReadOnlyList<IgwBulkRequest> requests, Dictionary<string, UnitResult> units, List<string> rejectedRoots, CancellationToken ct)
        {
            UnitResult Unit(string key)
            {
                if (!units.TryGetValue(key, out var unit))
                {
                    unit = new UnitResult();
                    units[key] = unit;
                }
                return unit;
            }

            foreach (var request in requests)
            {
                foreach (var key in IgwRequestUnits.Units(request))
                {
                    Unit(key);
                }
            }

            var watch = Stopwatch.StartNew();
            var origins = requests.Select(r => new Origin(r)).ToList();
            var queue = new LinkedList<(IgwBulkRequest Request, Origin Origin, bool Retry)>(
                requests.Select((r, i) => (r, origins[i], false)));
            var budget = Math.Max(0, _config.MaxIsolationRequests);
            int sent = 0, ok = 0, isolation = 0, points = 0;
            string stopError = null;
            while (queue.Count > 0)
            {
                var (original, origin, retry) = queue.First.Value;
                queue.RemoveFirst();
                var originalKeys = IgwRequestUnits.Units(original);
                var request = originalKeys.Any(k => Unit(k).Failed)
                    ? IgwRequestUnits.Subset(original, originalKeys.Where(k => !Unit(k).Failed))
                    : original;
                var keys = IgwRequestUnits.Units(request);
                if (keys.Count == 0)
                {
                    continue;
                }
                if (stopError != null)
                {
                    foreach (var key in keys)
                    {
                        Unit(key).Fail($"同设备前序请求失败，未发送：{stopError}");
                    }
                    continue;
                }

                sent++;
                var attempt = await SendAsync(project.IotUrl, request, ct).ConfigureAwait(false);
                switch (attempt.Kind)
                {
                    case IgwBulkFailureKind.None:
                    {
                        ok++;
                        MarkApplied(request, keys, origin);
                        break;
                    }
                    case IgwBulkFailureKind.AppliedTransient:
                    {
                        MarkApplied(request, keys, origin);
                        foreach (var key in keys)
                        {
                            Unit(key).Fail(attempt.Error);
                        }
                        stopError = attempt.Error;
                        break;
                    }
                    case IgwBulkFailureKind.PermanentDevice:
                    {
                        // 与本次内容无关（通道驱动冲突 / 模板实例 / 请求格式 / If-Match）：不是根组的问题，不记永久拒绝，下次注册自动重试
                        foreach (var key in keys)
                        {
                            Unit(key).Fail(attempt.Error);
                        }
                        stopError = attempt.Error;
                        break;
                    }
                    case IgwBulkFailureKind.PermanentRequest:
                    {
                        var culprits = IgwRequestUnits.UnitsAt(request, attempt.ItemErrorIndexes);
                        if (culprits != null && culprits.Count < keys.Count)
                        {
                            foreach (var culprit in culprits)
                            {
                                Unit(culprit).Reject(attempt.Error);
                            }
                            if (isolation < budget)
                            {
                                isolation++;
                                queue.AddFirst((request, origin, true)); // 出队时剔除已拒单元后重发其余
                            }
                            else
                            {
                                foreach (var key in keys.Where(k => !Unit(k).Rejected))
                                {
                                    Unit(key).Fail(attempt.Error + "（隔离重发预算已用完，本次未重发）");
                                }
                            }
                        }
                        else if (culprits != null)
                        {
                            // 逐项下标覆盖了请求内全部单元：定位精确，全部记 Rejected
                            foreach (var key in keys)
                            {
                                Unit(key).Reject(attempt.Error);
                            }
                        }
                        else if (keys.Count == 1)
                        {
                            // 暂记 Rejected；本原请求最终没有任何单元写入成功时，在设备发送结束后统一改记瞬态 Failed
                            Unit(keys[0]).Reject(attempt.Error);
                            origin.Error ??= attempt.Error;
                            origin.NoIndexRejected.Add(keys[0]);
                            // 注意：本原请求里只要有一个带点的单元此前已被剔除（在更早的分片里失败 / 被拒），
                            // 它就永远不会进 NoIndexRejected，本条设备级升级也就不会触发——此时只是少一次"停发该设备"的提前保护，
                            // 后续请求照常逐个走隔离流程（次数受 MaxIsolationRequests 限制，有界），下次注册重试时自然恢复
                            if (origin.ItemKeys.Count > 1 && origin.ItemKeys.All(origin.NoIndexRejected.Contains))
                            {
                                // 每个带点的根组单独发送都被拒（纯清除单元不算证据）：与单个根组内容无关 → 设备级失败，
                                // 停止隔离与该设备后续请求。说明放在 IGW 原文之前，台账截断时保留说明
                                stopError = $"原请求 {origin.ItemKeys.Count} 个根组逐个单独发送均被拒绝，判定为设备级问题"
                                            + $"（如设备现有点与本次地址模式混用；未标记为永久拒绝，下次注册自动重试）：{origin.Error}";
                                foreach (var key in origin.NoIndexRejected)
                                {
                                    var unit = Unit(key);
                                    unit.Rejected = false;
                                    unit.Failed = true;
                                    unit.Error = stopError;
                                }
                            }
                        }
                        else if (isolation + 2 <= budget)
                        {
                            isolation += 2;
                            origin.Error ??= attempt.Error;
                            var half = keys.Count / 2;
                            queue.AddFirst((IgwRequestUnits.Subset(request, keys.Skip(half)), origin, true));
                            queue.AddFirst((IgwRequestUnits.Subset(request, keys.Take(half)), origin, true));
                        }
                        else
                        {
                            foreach (var key in keys)
                            {
                                Unit(key).Fail(attempt.Error + "（隔离重发预算已用完，未能定位被拒绝的根组）");
                            }
                        }
                        break;
                    }
                    default:
                    {
                        foreach (var key in keys)
                        {
                            Unit(key).Fail(attempt.Error);
                        }
                        stopError = attempt.Error;
                        break;
                    }
                }
                if (attempt.Kind != IgwBulkFailureKind.None)
                {
                    // 隔离重发（剔除重发 / 二分）的逐次失败只记 Debug，设备发送结束时统一一条 Warning 汇总
                    _logger.Log(retry ? LogLevel.Debug : LogLevel.Warning,
                        "[IGW注册] 项目 {ProjectId} 设备 {Device} 请求失败（{Kind}，根组 {Units}）：{Error}",
                        project.ProjectId, device, attempt.Kind, string.Join(",", keys.Select(DisplayUnit)), attempt.Error);
                }
            }
            watch.Stop();

            // 无下标拒绝的设备级保护（不依赖二分跑完——预算用完时同样成立）：同一原请求没有任何单元写入成功，
            // 就没有「设备接受其它内容」的证据（纯清除请求写入成功不算），无下标被拒的单元不能判为该根组内容问题 → 改记瞬态 Failed，下次注册自动重试
            foreach (var origin in origins.Where(o => !o.AnyApplied))
            {
                foreach (var key in origin.NoIndexRejected)
                {
                    var unit = Unit(key);
                    if (unit.Rejected)
                    {
                        unit.Rejected = false;
                        unit.Failed = true;
                        unit.Error = $"同一请求没有任何带点的根组写入成功，无法确认是该根组内容问题（未标记为永久拒绝，下次注册自动重试）：{unit.Error}";
                    }
                }
            }

            var deviceRejected = units.Where(kv => kv.Value.Rejected).Select(kv => kv.Key).ToList();
            // 「不自动重试」清单只列带点的单元：纯清除单元（该设备上只有 replaceGroups、没有点）被拒时，所属块按 PendingClear / Failed
            // 自动重试（见 BuildLedger），清除块（ReplaceOnly）也会因仍在台账里而每次重发——列进去会误导
            var itemUnits = new HashSet<string>(origins.SelectMany(o => o.ItemKeys), StringComparer.OrdinalIgnoreCase);
            rejectedRoots.AddRange(deviceRejected.Where(itemUnits.Contains).Select(k => $"{device}/{DisplayUnit(k)}：{units[k].Error}"));
            var summary = $"{device}：请求 {ok}/{sent} 成功，写入点 {points}，耗时 {watch.Elapsed.TotalSeconds:0.0}s";
            if (isolation > 0)
            {
                summary += $"，隔离重发 {isolation} 次";
            }
            if (deviceRejected.Count > 0)
            {
                summary += $"，IGW 拒绝根组 {deviceRejected.Count} 个（{string.Join("、", deviceRejected.Select(DisplayUnit))}）";
            }
            var firstError = stopError ?? units.Values.Where(u => u.Failed).Select(u => u.Error).FirstOrDefault();
            if (firstError != null)
            {
                summary += $"，失败：{firstError}";
            }
            if (isolation > 0)
            {
                _logger.LogWarning("[IGW注册] 项目 {ProjectId} 隔离重发汇总：{Summary}", project.ProjectId, summary);
            }
            return summary;

            void MarkApplied(IgwBulkRequest applied, IReadOnlyList<string> appliedKeys, Origin appliedOrigin)
            {
                points += applied.Items.Count;
                if (applied.Items.Count > 0)
                {
                    // 只有带点的请求写入成功才证明设备接受内容；纯清除请求成功不算（见 Origin）
                    appliedOrigin.AnyApplied = true;
                }
                foreach (var key in appliedKeys)
                {
                    Unit(key).Applied = true;
                }
                foreach (var item in applied.Items)
                {
                    Unit(IgwRequestUnits.UnitOf(item.GroupPath)).AppliedItems++;
                }
            }
        }

        private static string DisplayUnit(string unit) => unit.Length == 0 ? "(设备根)" : unit;

        private async Task<SendAttempt> SendAsync(string iotUrl, IgwBulkRequest request, CancellationToken ct)
        {
            IgwBulkResponse response;
            try
            {
                response = await _client.BulkUpsertAsync(iotUrl, request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new SendAttempt(IgwBulkFailureKind.Transient, $"请求异常：{ex.GetBaseException().Message}", Array.Empty<int>());
            }

            var kind = IgwBulkFailureClassifier.Classify(response);
            if (kind == IgwBulkFailureKind.None)
            {
                return new SendAttempt(kind, null, Array.Empty<int>());
            }
            var error = $"HTTP {response.HttpStatus} {response.Error}".TrimEnd();
            if (!string.IsNullOrEmpty(response.Detail))
            {
                error += "：" + response.Detail;
            }
            if (kind == IgwBulkFailureKind.AppliedTransient)
            {
                error = "已写库但运行态应用失败，" + error;
            }
            return new SendAttempt(kind, error, (IReadOnlyCollection<int>)response.ItemErrorIndexes ?? Array.Empty<int>());
        }

        /// <summary>
        /// 生成台账行（R-1：按根组单元结果）。DB 块：own = 块本次所在设备上的单元，other = 另一设备上的单元（规划器对每个变更块在两台设备上
        /// 都发 replaceGroups，对 other 而言通常是纯清除单元）。
        /// <para>位置（DeviceKind / RootGroup / 重名映射）：旧注册原样留在 IGW（有台账旧行、own 一片都没写入、旧设备上的清除也没写入）→ 沿用台账旧值
        /// （读链继续读仍存在的旧点）；否则按本次规划写（读链走新点）。TagCount：own 全部写入 → 规划点数，部分写入 → 实际写入点数，沿用旧值 → 0。</para>
        /// <para>「哪台设备上可能留着待清除的旧组」只由显式事实携带，不从普通 Failed 推断（Failed 可能是本设备自身的瞬态失败，
        /// 若据此要求清除，另一设备持续故障时该块会每次上传都重注册、错误信息也会错误地归咎另一设备）：
        /// 台账旧 DeviceKind 是另一设备（另一设备上是旧注册）；旧行 PendingClear（DeviceKind 以外那台设备待清除）；
        /// 旧行 FailedPendingClear（不确定哪台，两台都按待清除）。</para>
        /// <para>otherPending = 另一设备须清除且清除失败 / 未发送（沿用另一设备旧注册作读位置时不算）；
        /// ownPending = 本设备须清除（如块换回本设备、本设备上更早的旧组）且 own 一片都没写入（本设备清除组随首片发送）。</para>
        /// <para>状态表：</para>
        /// <list type="table">
        /// <item>无 pending，own 全部写入 → Registered（other 的防御性清除失败只写进 Error 说明）</item>
        /// <item>无 pending，own 失败 → own 被按内容拒绝 ? Rejected : Failed（旧位置为另一设备时，旧 DeviceKind 本身携带清除要求到下一轮）</item>
        /// <item>只有 otherPending 且 own 全部写在本次规划位置 → PendingClear：下次增量只对另一设备重发一个纯清除请求（见 ClearOnly），
        /// 内容不重发；清除写入 → Registered。清除被 IGW 永久拒绝时保持 PendingClear，代价是每次上传一个小的清除请求</item>
        /// <item>其它 pending（own 部分 / 未写入，或 ownPending）→ FailedPendingClear：下次增量整块重做（own 被按内容拒绝也继续重试——
        /// 宁可重发也不能丢掉清除要求）。Error 先写清除未写入说明，再附 own 的错误</item>
        /// </list>
        /// <para>块从未在另一设备上（台账无行，或旧行在本设备且 Registered / Failed / Rejected）时，另一设备的防御性清除失败不影响状态——
        /// 否则一台设备持续故障会让另一台设备的全部块每次上传都重注册。</para>
        /// ClearOnly 块（只重发清除）：位置 / 点数 / 跳过数 / 重名映射原样沿用台账，清除写入 → Registered，否则保持 PendingClear。
        /// IO——单元 (S7, "IO") 同理（无换设备）。清除块——两台设备上涉及它的单元都已写入 → 进入 removedRoots。
        /// </summary>
        private static (List<PLC_IgwRegistrationEntity> Rows, List<(string RootKind, string RootName)> Removed, List<string> DeadRoots) BuildLedger(
            PlanResult plan, Dictionary<IgwDeviceKind, Dictionary<string, UnitResult>> units, Scope scope,
            IReadOnlyList<ClearOnlyBlock> clearOnly, IgwLedgerSnapshot ledger, bool includeIo)
        {
            var now = DateTime.Now;
            var rows = new List<PLC_IgwRegistrationEntity>();
            var removed = new List<(string RootKind, string RootName)>();
            // 块名清洗后为空、没发任何清除请求而被保留的台账行（见下方 ReplaceOnly 循环），供调用方计入结果详情
            var deadRoots = new List<string>();

            UnitResult Find(IgwDeviceKind device, string key) =>
                key != null && units.TryGetValue(device, out var map) && map.TryGetValue(key, out var unit) ? unit : null;

            string RootOf(string block) =>
                plan.BlockRoots.TryGetValue(block, out var root) ? root : IgwTagNaming.SanitizeName(block);

            foreach (var block in plan.Blocks)
            {
                var root = RootOf(block.BlockName);
                var own = Find(block.Device, root);
                var otherDevice = block.Device == IgwDeviceKind.S7 ? IgwDeviceKind.S7Plus : IgwDeviceKind.S7;
                var other = Find(otherDevice, root);
                var hadPrevious = ledger.DeviceKindByBlock.TryGetValue(block.BlockName, out var previousKind);
                // 台账旧行对「哪台设备上可能留着待清除的旧组」的记录：
                // PendingClear = 台账 DeviceKind 以外的另一台设备；FailedPendingClear = 不确定（两台都按待清除处理）；
                // 旧 DeviceKind 是另一设备 = 另一设备上是旧注册（除非本次沿用它作为读位置，否则必须清除）
                var previousPendingClear = hadPrevious && ledger.PendingClearDbBlocks.Contains(block.BlockName);
                var previousFailedPending = hadPrevious && ledger.FailedPendingClearDbBlocks.Contains(block.BlockName);
                var mustClearOther = hadPrevious
                                     && (previousKind == otherDevice || previousFailedPending || (previousPendingClear && previousKind == block.Device));
                var mustClearOwn = previousFailedPending || (previousPendingClear && previousKind == otherDevice);
                var previousWiped = hadPrevious && previousKind != block.Device && Find(previousKind, root)?.Applied == true;
                var usePlanned = own == null || own.Applied || !hadPrevious || previousWiped;
                var keptOnOtherDevice = !usePlanned && previousKind == otherDevice;
                var ownOk = own == null || !own.Failed;
                var otherClearFailed = other is { Failed: true };
                // 另一设备待清除：要求清除且清除失败 / 未发送；沿用另一设备旧注册作读位置时它不是「待清除的旧组」
                var otherPending = mustClearOther && otherClearFailed && !keptOnOtherDevice;
                // 本设备待清除（如块换回本设备、本设备上更早的旧组没清掉）：本设备的清除组随首片发送，一片都没写入 = 旧组没被替换
                var ownPending = mustClearOwn && own is { Applied: false };
                var pending = otherPending || ownPending;
                string error;
                if (pending)
                {
                    var parts = new List<string>();
                    if (otherPending)
                    {
                        parts.Add($"{otherDevice} 设备上的旧组清除未写入（旧点可能仍留在 {otherDevice}，下次注册自动重试清除）：{other.Error}");
                    }
                    if (ownPending)
                    {
                        parts.Add($"{block.Device} 设备上的旧组清除未写入（旧点可能仍留在 {block.Device}，下次注册自动重试清除）：{own.Error}");
                    }
                    else if (!ownOk && own.Error != null)
                    {
                        parts.Add($"{block.Device} 本设备：{own.Error}");
                    }
                    error = string.Join("；", parts);
                }
                else
                {
                    error = own?.Error ?? (otherClearFailed ? $"另一设备旧组清除失败：{other.Error}" : null);
                }
                // pending 时：内容全部写在本次规划位置（只剩另一设备清除）→ PendingClear（下次只重发另一设备清除，不重发内容）；
                // 否则 → FailedPendingClear（下次整块重做，本设备被按内容拒绝也继续重试——不能停在 Rejected 丢掉清除要求）
                var status = pending
                    ? usePlanned && ownOk && !ownPending ? IgwLedgerStatus.PendingClear : IgwLedgerStatus.FailedPendingClear
                    : ownOk
                        ? IgwLedgerStatus.Registered
                        : own.Rejected ? IgwLedgerStatus.Rejected : IgwLedgerStatus.Failed;

                var row = new PLC_IgwRegistrationEntity
                {
                    RootKind = IgwLedgerRootKind.DbBlock,
                    RootName = block.BlockName,
                    SkippedCount = block.Skipped,
                    SkipSummary = Truncate(FormatReasons(block.SkipReasons), MaxSkipSummaryLength),
                    Error = Truncate(error, MaxErrorLength),
                    RegisteredAt = now,
                };
                if (usePlanned)
                {
                    row.RootGroup = root;
                    row.DeviceKind = block.Device.ToString();
                    // 全部分片写入 → 规划点数；有分片失败 → 实际写入的点数（一片都没写入，如首次注册即被拒 → 0）
                    row.TagCount = ownOk ? block.TagCount : own.AppliedItems;
                    row.RenamesJson = SerializeRenames(plan.DbRenames
                        .Where(kv => string.Equals(kv.Key.Block, block.BlockName, StringComparison.Ordinal))
                        .Select(kv => (kv.Key.VariablePath, kv.Value)));
                    row.Status = status;
                    // 指纹只在「本轮整根写全了」或「本轮压根没发（内容与台账一致）」时才记。
                    // 部分写入必须留空：留下指纹会让下一轮跳过一个只写了一半的根，而且永远不会自愈。
                    row.Fingerprint = block.SkippedUnchanged
                        ? block.Fingerprint
                        : (ownOk ? block.Fingerprint : null);
                }
                else
                {
                    row.RootGroup = ledger.RootGroupByBlock.TryGetValue(block.BlockName, out var oldRoot) ? oldRoot : root;
                    row.DeviceKind = ledger.DeviceKindByBlock.TryGetValue(block.BlockName, out var oldKind)
                        ? oldKind.ToString()
                        : block.Device.ToString();
                    row.TagCount = 0;
                    row.RenamesJson = SerializeRenames(ledger.DbRenames
                        .Where(kv => string.Equals(kv.Key.Block, block.BlockName, StringComparison.Ordinal))
                        .Select(kv => (kv.Key.VariablePath, kv.Value)));
                    row.Status = status;
                    // 本轮没规划这个根（内容原样沿用台账）⇒ 指纹也原样沿用，别把它抹成 null 逼下轮白发一次
                    row.Fingerprint = ledger.DbBlockStats.TryGetValue(block.BlockName, out var oldStats)
                        ? oldStats?.Fingerprint
                        : null;
                }
                rows.Add(row);
            }

            // 只重发清除的块：位置 / 点数 / 重名映射原样沿用台账（内容没重发），清除写入 → Registered，否则保持 PendingClear
            foreach (var entry in clearOnly)
            {
                var clear = Find(entry.Device, entry.Root);
                var cleared = clear is { Applied: true, Failed: false };
                ledger.DbBlockStats.TryGetValue(entry.Block, out var stats);
                rows.Add(new PLC_IgwRegistrationEntity
                {
                    RootKind = IgwLedgerRootKind.DbBlock,
                    RootName = entry.Block,
                    RootGroup = ledger.RootGroupByBlock.TryGetValue(entry.Block, out var ledgerRoot) ? ledgerRoot : entry.Root,
                    DeviceKind = ledger.DeviceKindByBlock[entry.Block].ToString(),
                    TagCount = stats?.TagCount,
                    SkippedCount = stats?.SkippedCount,
                    SkipSummary = stats?.SkipSummary,
                    RenamesJson = SerializeRenames(ledger.DbRenames
                        .Where(kv => string.Equals(kv.Key.Block, entry.Block, StringComparison.Ordinal))
                        .Select(kv => (kv.Key.VariablePath, kv.Value))),
                    Status = cleared ? IgwLedgerStatus.Registered : IgwLedgerStatus.PendingClear,
                    Error = cleared
                        ? null
                        : Truncate($"{entry.Device} 设备上的旧组清除未写入（旧点可能仍留在 {entry.Device}，下次注册自动重试清除）：{clear?.Error ?? "未发送"}",
                            MaxErrorLength),
                    RegisteredAt = now,
                });
            }

            foreach (var block in scope.ReplaceOnly)
            {
                var root = RootOf(block);
                if (root == null)
                {
                    // 块名清洗后为空：规划器分不出根组、没发任何清除请求 → 台账行保留（也不能拿 null 去查单元）。
                    // RootOf 只看块名本身，与本次是增量还是全量无关——全量注册会走到同一条 null 分支，
                    // 并不会重新处理 / 清掉这一行；只要该块名清洗后仍为空就会一直卡在这里，需要人工核对（该块在 IGW 上的旧组也不会被清除）
                    deadRoots.Add(block);
                    continue;
                }
                var involved = units.Values
                    .Select(map => map.TryGetValue(root, out var unit) ? unit : null)
                    .Where(unit => unit != null)
                    .ToList();
                // 必须真的有请求涉及到这个根（块名清洗后为空等情况下规划器分不出根组、不发 replaceGroups）——
                // 一个都没有时不能当成"已清除"，台账行保留，等块变更 / 全量注册重新处理
                if (involved.Count > 0 && involved.All(unit => unit.Applied))
                {
                    removed.Add((IgwLedgerRootKind.DbBlock, block));
                }
            }

            if (includeIo)
            {
                var io = Find(IgwDeviceKind.S7, IgwTagNaming.IoRootGroup);
                var applied = io?.Applied ?? false;
                var ioTagCount = io == null ? 0 : !io.Failed ? plan.IoTagCount : io.AppliedItems;
                var ioReasons = plan.SkipReasons.Where(kv => kv.Key.StartsWith("io_", StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                rows.Add(new PLC_IgwRegistrationEntity
                {
                    RootKind = IgwLedgerRootKind.Io,
                    RootName = IgwTagNaming.IoRootGroup,
                    RootGroup = IgwTagNaming.IoRootGroup,
                    DeviceKind = IgwDeviceKind.S7.ToString(),
                    TagCount = ioTagCount,
                    SkippedCount = ioReasons.Values.Sum(),
                    SkipSummary = Truncate(FormatReasons(ioReasons), MaxSkipSummaryLength),
                    Status = io == null
                        ? IgwLedgerStatus.Failed
                        : !io.Failed
                            ? IgwLedgerStatus.Registered
                            : io.Rejected ? IgwLedgerStatus.Rejected : IgwLedgerStatus.Failed,
                    Error = Truncate(io == null ? "IO 请求未生成" : io.Error, MaxErrorLength),
                    RegisteredAt = now,
                    RenamesJson = applied
                        ? SerializeRenames(plan.IoRenames.Select(kv => (kv.Key.Table + IgwTagNaming.KeySeparator + kv.Key.Tag, kv.Value)))
                        : SerializeRenames(ledger.IoRenames.Select(kv => (kv.Key.Table + IgwTagNaming.KeySeparator + kv.Key.Tag, kv.Value))),
                });
            }

            return (rows, removed, deadRoots);
        }

        internal static string SerializeRenames(IEnumerable<(string Key, string Value)> entries)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in entries)
            {
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    map[key] = value;
                }
            }
            return map.Count == 0 ? null : JsonSerializer.Serialize(map, RenamesJsonOptions);
        }

        private static int IoSkipped(IReadOnlyDictionary<string, int> reasons) =>
            reasons.Where(kv => kv.Key.StartsWith("io_", StringComparison.Ordinal)).Sum(kv => kv.Value);

        /// <summary>跳过原因 Top N：「unmapped_type×12、too_deep×3」（renamed_duplicate 只是改名计数，不属跳过，排除）</summary>
        private static string FormatReasons(IReadOnlyDictionary<string, int> reasons) =>
            reasons == null
                ? string.Empty
                : string.Join("、", reasons
                    .Where(kv => kv.Value > 0 && kv.Key != IgwRegistrationPlanner.ReasonRenamedDuplicate)
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Take(TopReasons)
                    .Select(kv => $"{kv.Key}×{kv.Value}"));

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) ? null : text.Length > max ? text.Substring(0, max) : text;

        private static List<string> Clean(IReadOnlyCollection<string> names) =>
            (names ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

        private static string Describe(IgwRegistrationProjectInfo project) =>
            project == null ? "(未知)" : $"{project.ProjectName}({project.ProjectId})";

        private async Task<IgwRegistrationOutcome> SkipAsync(IgwRegistrationProjectInfo project, string reason)
        {
            _logger.LogInformation("[IGW注册] 项目 {Project} 跳过：{Reason}", Describe(project), reason);
            await WriteLogSafeAsync($"[IGW注册] 项目 {Describe(project)} 跳过：{reason}").ConfigureAwait(false);
            return new IgwRegistrationOutcome(IgwRegistrationStatus.Skipped, reason);
        }

        private async Task<IgwRegistrationOutcome> FailAsync(IgwRegistrationProjectInfo project, string reason)
        {
            _logger.LogWarning("[IGW注册] 项目 {Project} 失败：{Reason}", Describe(project), reason);
            await WriteLogSafeAsync($"[IGW注册] 项目 {Describe(project)} 失败：{reason}").ConfigureAwait(false);
            return new IgwRegistrationOutcome(IgwRegistrationStatus.Failed, reason);
        }

        private async Task WriteLogSafeAsync(string text)
        {
            try
            {
                await _writeLog(text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IGW注册] 写「PLC上传」日志失败");
            }
        }
    }
}
