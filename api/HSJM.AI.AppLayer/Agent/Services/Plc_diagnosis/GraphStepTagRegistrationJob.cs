using Hangfire;
using Hangfire.States;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// GRAPH 步位自动注册后台任务
    ///
    /// 职责：
    /// 1. 按绑定表驱动判定：ai_plc_dbfbinding 中绑定了 GRAPH 类型 FB 的实例 DB 即为 GRAPH 步进 DB
    /// 2. 展开步位清单：该 FB 下 STEP_x_ACTION LogicUnit 的 NetworkId（1 基步号）→
    ///     {DB}.RT_DATA.EXEC_BITS.EXEC_BITS[n-1] 逐位 + {DB}.S_NO 校验位
    /// 3. 写台账（AI_PLC_GraphStepTag，先软删同 (ProjectId, DbName) 旧记录再插新行）
    /// 4. 注册到网关（双网关适配，见下）
    /// 5. 读值验证：L1 IsGood / L2 值类型 / L3 S_NO=k 时 EXEC_BITS[k-1]==true 交叉校验
    ///
    /// 双网关适配（按项目 IotUrl 判定，见 ResolveGatewayFlavor：端口 2500 = 老网关，其余 = IGW；2026-09-19 起不再探测，
    /// 避免 IGW 宕机时探测退化为老网关而把老协议请求发给 IGW）：
    /// - 老网关（GatewayFlavor.Legacy，/DriverInfo/*）：ManualAddTagItems（StationId_Plus 设备，2000 条一批）+ ReadTags，行为与历史版本一致
    /// - 新 IGW（GatewayFlavor.NewIgw）：批量接口（sub-project B task-7，2026-09-19）——本项目全部 GRAPH DB 一次
    ///   POST /api/v1/project/bulk/tags 写入项目通道的 GRAPH 设备（逻辑名 S7Plus_{ip}）：每 DB 两点
    ///   [工站|_未分组, DB, RT_DATA]/EXEC_BITS（bool 数组，维度=最大步号）+ [工站|_未分组, DB]/S_NO（int16），
    ///   工站改绑经 replaceGroups 清除旧位置；发送前清理 通用/通用_S7Plus 旧设备（见 GraphIgwRegistrar / GraphRequestBuilder）；
    ///   ReadTagsAsList 整组读回（IgwTagNaming.GraphReadAlias）后本地按 BitIndex 展开逐位结果，沿用同一套 L1/L2/L3 验证
    /// - 新 IGW 需要项目配置 PLC 直连地址（ai_plc_project_new.plcip/plcport）；缺失时该 DB 整体 RegFailed 并写明原因
    ///
    /// 架构：
    /// - 由 Hangfire 后台调度，上传事务提交后自动触发（挂载点见 PLC_UploadApp）
    /// - 入队走本机专用队列 local_&lt;本机&gt;，避免多机共享 Hangfire 库时被旧 dll worker 抢走
    /// - 全流程 try-catch：任何异常只记日志不抛出（Hangfire 任务始终 Succeeded）
    /// </summary>
    public class GraphStepTagRegistrationJob : ISelfScopedAutoInject
    {
        private readonly ISqlSugarClient _db;
        private readonly IPLC_GraphStepTagService _graphStepTagService;
        private readonly IPLC_LogicUnitService _logicUnitService;
        private readonly IIotService _iotService;
        private readonly StationPlcScopeService _stationPlcScopeService;
        private readonly IOConstraintProgressService _constraintProgressService;
        private readonly IgwBulkClient _bulkClient;
        private readonly IPLC_IgwRegistrationService _igwRegistrationService;
        private readonly IPLC_IgwRegistrationStateService _igwStateService;
        private readonly ILogger<GraphStepTagRegistrationJob> _logger;

        /// <summary>EXEC_BIT 类：工步执行位</summary>
        private const string TAG_CATEGORY_EXEC_BIT = "EXEC_BIT";
        /// <summary>STEP_NO 类：工步号（S_NO）校验位</summary>
        private const string TAG_CATEGORY_STEP_NO = "STEP_NO";

        /// <summary>台账状态：待注册</summary>
        private const string STATUS_PENDING = "Pending";
        /// <summary>台账状态：已注册（网关受理成功，待验证）</summary>
        private const string STATUS_REGISTERED = "Registered";
        /// <summary>台账状态：已验证（L1/L2 通过）</summary>
        private const string STATUS_VERIFIED = "Verified";
        /// <summary>台账状态：网关注册失败</summary>
        private const string STATUS_REG_FAILED = "RegFailed";
        /// <summary>台账状态：读值验证失败</summary>
        private const string STATUS_VERIFY_FAILED = "VerifyFailed";

        /// <summary>EXEC_BITS 数组最大 0 基下标（250 位，超出视为异常步号）</summary>
        private const int MAX_BIT_INDEX = 249;
        /// <summary>网关注册单批上限（老网关 /DriverInfo 路径，保持历史行为不变）</summary>
        private const int REGISTER_BATCH_SIZE = 2000;
        /// <summary>新 IGW（/Tag/AddTags）单批上限。IGW 源码 AddTags 无显式上限（MaxBatchSize=1000 只约束读/写），
        /// 按 1000 保守分批：与读上限对齐，且每批成功都会触发一次热重载落盘，小批更稳。GRAPH 步位单 DB ≤251 行，实际不会跨批。</summary>
        private const int NEW_IGW_REGISTER_BATCH_SIZE = 1000;
        /// <summary>VerifyDetail 截断长度（varchar(500) 兜底）</summary>
        private const int VERIFY_DETAIL_MAX_LENGTH = 480;

        /// <summary>GRAPH StepAction LogicUnit 类型（STEP_1_ACTION / STEP_2_ACTION ...）</summary>
        private static readonly Regex StepActionUnitTypeRegex =
            new Regex(@"^STEP_\d+_ACTION$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>防重复执行守卫：正在处理的项目集合（本进程内），EnqueueIfNotRunning 与 ExecuteAsync 双层使用</summary>
        private static readonly ConcurrentDictionary<long, byte> _inFlightProjects = new ConcurrentDictionary<long, byte>();

        /// <summary>待补跑的全项目运行（EnqueueWholeProject 撞上同项目任务执行中时登记，该任务结束时消费并入队一次）</summary>
        private static readonly ConcurrentDictionary<long, byte> _pendingWholeProject = new ConcurrentDictionary<long, byte>();

        /// <summary>网关设备名后缀（与 ExecuteBatchRegistrationTag 的 S7 Plus 设备一致）</summary>
        private const string DEVICE_SUFFIX_PLUS = "_Plus";

        public GraphStepTagRegistrationJob(
            ISqlSugarClient db,
            IPLC_GraphStepTagService graphStepTagService,
            IPLC_LogicUnitService logicUnitService,
            IIotService iotService,
            StationPlcScopeService stationPlcScopeService,
            IOConstraintProgressService constraintProgressService,
            IgwBulkClient bulkClient,
            IPLC_IgwRegistrationService igwRegistrationService,
            IPLC_IgwRegistrationStateService igwStateService,
            ILogger<GraphStepTagRegistrationJob> logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _graphStepTagService = graphStepTagService ?? throw new ArgumentNullException(nameof(graphStepTagService));
            _logicUnitService = logicUnitService ?? throw new ArgumentNullException(nameof(logicUnitService));
            _iotService = iotService ?? throw new ArgumentNullException(nameof(iotService));
            _stationPlcScopeService = stationPlcScopeService ?? throw new ArgumentNullException(nameof(stationPlcScopeService));
            _constraintProgressService = constraintProgressService ?? throw new ArgumentNullException(nameof(constraintProgressService));
            _bulkClient = bulkClient ?? throw new ArgumentNullException(nameof(bulkClient));
            _igwRegistrationService = igwRegistrationService ?? throw new ArgumentNullException(nameof(igwRegistrationService));
            _igwStateService = igwStateService ?? throw new ArgumentNullException(nameof(igwStateService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region 触发入口

        /// <summary>
        /// 触发 GRAPH 步位注册任务（防重复）
        /// 在上传事务提交完成后调用此方法。
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <returns>是否成功入队（false = 本进程内已有同项目任务执行中）</returns>
        public static bool EnqueueIfNotRunning(long projectId)
        {
            return EnqueueIfNotRunning(projectId, chainConstraintGeneration: false);
        }

        /// <summary>
        /// 触发 GRAPH 步位注册任务（防重复），可选在注册流程结束后链式触发 IO 约束生成任务。
        /// 上传主链路（上传 zip → 解析入库 → GRAPH 步位注册 → 约束规则生成）传 chainConstraintGeneration=true，
        /// 保证约束生成在注册完成之后串行执行，而不是两个 Hangfire 任务并行抢资源。
        /// RerunGraphStepRegistration 等单任务补跑入口保持 false（只补注册，不动约束）。
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="chainConstraintGeneration">注册流程结束后是否链式触发 IO 约束生成</param>
        /// <returns>是否成功入队（false = 本进程内已有同项目任务执行中）</returns>
        public static bool EnqueueIfNotRunning(long projectId, bool chainConstraintGeneration)
        {
            return EnqueueIfNotRunning(projectId, chainConstraintGeneration, null);
        }

        /// <summary>
        /// 触发 GRAPH 步位注册任务（防重复），支持按变更范围收窄（P2）：
        /// changedDbNames 非空时只处理集合内的 GRAPH 步进 DB（其余 DB 台账/注册不动），
        /// 为空时全项目（历史行为）。上传链路传入本次实际变更（清理+重插）的块名集合。
        /// </summary>
        public static bool EnqueueIfNotRunning(long projectId, bool chainConstraintGeneration, string[] changedDbNames)
        {
            // 防重复入队：该项目已有同任务在飞行（本进程内执行中）则不再入队，
            // 避免 Hangfire 队列里堆重复任务（软删+重插会让台账状态来回翻转）。
            if (_inFlightProjects.ContainsKey(projectId))
            {
                return false;
            }

            // 显式入队到本机专用队列 local_<本机机器名>，避免多机共享 Hangfire 数据库时
            // 任务被其它机器（dll 版本可能不一致）的 worker 抢去执行。
            // 每个 HSJM.AI 进程启动时 worker 会注册 local_<本机> 队列（见 HSJM.AI/HangfireQueueNames）。
            var queue = BuildCurrentMachineQueue();
            var client = new BackgroundJobClient();
            // 注意：故意不改动 ExecuteAsync/ExecuteChainedAsync 单参签名的现有行为——
            // Hangfire 里已序列化的旧任务按「方法名+参数类型」反序列化；
            // 新增 (long, string[]) 重载与单参版本并存，旧任务反序列化不受影响。
            if (chainConstraintGeneration)
            {
                client.Create<GraphStepTagRegistrationJob>(
                    svc => svc.ExecuteChainedAsync(projectId, changedDbNames),
                    new EnqueuedState(queue));
            }
            else
            {
                client.Create<GraphStepTagRegistrationJob>(
                    svc => svc.ExecuteAsync(projectId, changedDbNames),
                    new EnqueuedState(queue));
            }
            return true;
        }

        /// <summary>
        /// 请求一次全项目 GRAPH 步位注册（IGW 注册任务的过渡安全触发，I-1）：无同项目任务在飞 → 立即入队（返回 true）；
        /// 同项目任务执行中 → 登记待补跑，该任务结束后自动入队一次全项目运行（返回 false）。不会丢请求，也不会并发执行同项目。
        /// 先登记再检查在飞：与 <see cref="ExecuteInternalAsync"/> 结束时的「先摘在飞再消费待补跑」配对，二者恰有一方消费登记。
        /// </summary>
        public static bool EnqueueWholeProject(long projectId)
        {
            _pendingWholeProject[projectId] = 0;
            if (_inFlightProjects.ContainsKey(projectId))
            {
                return false;
            }
            if (!_pendingWholeProject.TryRemove(projectId, out _))
            {
                return false; // 在飞任务恰好结束并已消费登记（已由它入队）
            }
            if (!EnqueueIfNotRunning(projectId, chainConstraintGeneration: false, changedDbNames: null))
            {
                // 检查之后恰有任务开始执行：重新登记，交由它结束时补跑
                _pendingWholeProject[projectId] = 0;
                return false;
            }
            return true;
        }

        /// <summary>结束时消费待补跑登记：有则入队一次全项目运行（入队失败只记日志）</summary>
        private void EnqueuePendingWholeProjectRun(long projectId)
        {
            if (!_pendingWholeProject.TryRemove(projectId, out _))
            {
                return;
            }
            try
            {
                EnqueueIfNotRunning(projectId, chainConstraintGeneration: false, changedDbNames: null);
                _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId} 执行期间有全项目注册请求，已入队补跑", projectId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GRAPH步位注册] 项目 {ProjectId} 全项目补跑入队失败（可手工补跑 GRAPH 步位注册）", projectId);
            }
        }

        /// <summary>计算本机专用 Hangfire 队列名：local_&lt;归一化机器名&gt;。
        /// 与 HSJM.AI/HangfireQueueNames.BuildCurrentServiceQueue 保持等价语义（AppLayer 不能引用 HSJM.AI 顶层项目，故 inline）。</summary>
        private static string BuildCurrentMachineQueue()
        {
            var raw = Environment.GetEnvironmentVariable("HSJM_HANGFIRE_LOCAL_QUEUE");
            if (string.IsNullOrWhiteSpace(raw)) raw = Environment.MachineName;
            var builder = new StringBuilder();
            foreach (var ch in raw.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                    builder.Append(ch);
                else if (builder.Length == 0 || builder[builder.Length - 1] != '_')
                    builder.Append('_');
            }
            var normalized = builder.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(normalized)) normalized = "current";
            return $"local_{normalized}";
        }

        #endregion

        #region 后台执行主流程

        // 注意：故意不加 [Queue(...)] 属性 — Hangfire 的 [Queue] JobFilter 会 override EnqueueIfNotRunning
        // 调用时传入的 EnqueuedState 的 queue，导致本机队列锁定失效。
        // 入队时由 EnqueueIfNotRunning 用 EnqueuedState 显式指定到 local_<本机> 队列。
        /// <summary>
        /// 执行 GRAPH 步位注册（Hangfire 后台调用）。
        /// 全流程 try-catch：任何异常只记日志不抛出。
        /// </summary>
        [AutomaticRetry(Attempts = 2)]
        [Hangfire.JobDisplayName("GRAPH步位注册 · 项目{0}")]
        public Task ExecuteAsync(long projectId)
        {
            return ExecuteInternalAsync(projectId, null);
        }

        /// <summary>范围版（P2）：只处理 changedDbNames 集合内的 GRAPH 步进 DB；空数组=范围内无 DB（快速跳过）。</summary>
        [AutomaticRetry(Attempts = 2)]
        [Hangfire.JobDisplayName("GRAPH步位注册 · 项目{0}")]
        public Task ExecuteAsync(long projectId, string[] changedDbNames)
        {
            return ExecuteInternalAsync(projectId, changedDbNames?.ToHashSet(StringComparer.Ordinal));
        }

        /// <summary>防重复守卫 + 主流程调度（单参与范围版共用；scope=null 全项目）。</summary>
        private async Task ExecuteInternalAsync(long projectId, HashSet<string> changedDbNames)
        {
            _logger.LogInformation("[GRAPH步位注册] 开始: ProjectId={ProjectId}, Scope={Scope}",
                projectId, changedDbNames == null ? "全项目" : $"{changedDbNames.Count} 个块名");
            try
            {
                // 防重复执行（第二层，覆盖 Hangfire 自动重试等未经 EnqueueIfNotRunning 的入口）
                if (!_inFlightProjects.TryAdd(projectId, 0))
                {
                    _logger.LogWarning("[GRAPH步位注册] 项目 {ProjectId} 已有任务执行中，本次跳过", projectId);
                    return;
                }

                try
                {
                    await RunCoreAsync(projectId, changedDbNames).ConfigureAwait(false);
                }
                finally
                {
                    _inFlightProjects.TryRemove(projectId, out _);
                    EnqueuePendingWholeProjectRun(projectId);
                }
            }
            catch (Exception ex)
            {
                // 兜底：任何异常只记日志不抛出（Hangfire 任务保持 Succeeded，避免无限重试）
                _logger.LogError(ex, "[GRAPH步位注册] 项目 {ProjectId} 执行异常终止", projectId);
                await WriteLogHelper.WriteLogsAsync(
                    $"[GRAPH步位注册] 项目 {projectId} 执行异常终止: {ex.Message}\r\n{ex.StackTrace}",
                    "GRAPH步位注册").ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 链式执行（Hangfire 后台调用）：GRAPH 步位注册流程结束后触发 IO 约束生成任务。
        /// 上传主链路使用：上传 zip → 解析入库 → 本任务（注册+验证）→ IOConstraintPreprocessService.ExecuteAsync（约束生成）。
        /// ExecuteAsync 全流程吞异常（永不抛出），因此无论注册成败，约束生成都会被触发——
        /// 约束生成的输入（block/LU/IOTag）不依赖注册产物（台账+网关 tag），注册失败不应阻塞约束生成。
        /// </summary>
        [Hangfire.JobDisplayName("GRAPH步位注册+链式约束生成 · 项目{0}")]
        public async Task ExecuteChainedAsync(long projectId)
        {
            _logger.LogInformation("[GRAPH步位注册] 链式模式开始: ProjectId={ProjectId}（注册完成后将触发约束生成）", projectId);
            try
            {
                await ExecuteAsync(projectId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 理论上 ExecuteAsync 不抛出；此兜底只为保证链路不断
                _logger.LogError(ex, "[GRAPH步位注册] 链式模式执行异常: ProjectId={ProjectId}", projectId);
            }
            finally
            {
                await TryEnqueueConstraintGenerationAsync(projectId).ConfigureAwait(false);
            }
        }

        /// <summary>链式范围版（P2）：注册范围收窄到 changedDbNames，注册完成后仍链式触发约束生成（约束生成本身自带增量过滤）。</summary>
        [Hangfire.JobDisplayName("GRAPH步位注册+链式约束生成 · 项目{0}")]
        public async Task ExecuteChainedAsync(long projectId, string[] changedDbNames)
        {
            _logger.LogInformation("[GRAPH步位注册] 链式模式开始: ProjectId={ProjectId}, Scope={Scope}（注册完成后将触发约束生成）",
                projectId, changedDbNames == null ? "全项目" : $"{changedDbNames.Length} 个块名");
            try
            {
                await ExecuteAsync(projectId, changedDbNames).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 理论上 ExecuteAsync 不抛出；此兜底只为保证链路不断
                _logger.LogError(ex, "[GRAPH步位注册] 链式模式执行异常: ProjectId={ProjectId}", projectId);
            }
            finally
            {
                await TryEnqueueConstraintGenerationAsync(projectId).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 链式模式收尾（2026-10-02 审查 P2）：不再触发老方法约束生成。约束统一走 XMLV2（上传发布 / 约束重置 / 单 LU、单块重跑），
        /// XMLV2 约束不依赖工站绑定，绑定工站后无需重新生成；这里只记一条日志，保留方法供 Hangfire 存量链式任务反序列化。
        /// </summary>
        private async Task TryEnqueueConstraintGenerationAsync(long projectId)
        {
            _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId} 链式注册结束；约束走 XMLV2、不依赖工站绑定，不再触发老方法约束生成", projectId);
            await WriteLogHelper.WriteLogsAsync(
                $"[GRAPH步位注册] 项目 {projectId} 链式注册结束（约束走 XMLV2，不再链式触发老方法约束生成）",
                "GRAPH步位注册").ConfigureAwait(false);
        }

        /// <summary>主流程：判定 → 展开 → 台账 → 注册 → 验证（单个 DB 的失败不影响其它 DB）。
        /// changedDbNames 非空时只处理集合内的 DB（P2 按变更范围收窄）。</summary>
        private async Task RunCoreAsync(long projectId, HashSet<string> changedDbNames = null)
        {
            var targets = await LoadGraphStepTargetsAsync(projectId).ConfigureAwait(false);
            if (changedDbNames != null)
            {
                var beforeCount = targets.Count;
                targets = targets.Where(t => changedDbNames.Contains(t.DbName)).ToList();
                _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId} 按变更范围过滤 GRAPH DB: {Before} → {After}",
                    projectId, beforeCount, targets.Count);
            }
            if (targets.Count == 0)
            {
                var scopeMsg = changedDbNames != null ? "（本次变更范围与项目 GRAPH 步进 DB 无交集）" : "（绑定表未命中）";
                _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId} 无需处理的 GRAPH 步进 DB{Scope}，任务结束", projectId, scopeMsg);
                await WriteLogHelper.WriteLogsAsync($"[GRAPH步位注册] 项目 {projectId} 无需处理的 GRAPH 步进 DB{scopeMsg}，任务结束", "GRAPH步位注册").ConfigureAwait(false);
                if (changedDbNames == null)
                {
                    // 全项目运行且已无 GRAPH 目标：IGW 项目上残留的 Graph 台账根 / GRAPH 设备组一并清除
                    await TryClearStaleGraphWithoutTargetsAsync(projectId).ConfigureAwait(false);
                }
                return;
            }

            _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId} 命中 {Count} 个 GRAPH 步进 DB: {DbNames}",
                projectId, targets.Count, string.Join(", ", targets.Select(t => t.DbName)));
            await WriteLogHelper.WriteLogsAsync(
                $"[GRAPH步位注册] 项目 {projectId} 命中 {targets.Count} 个 GRAPH 步进 DB: {string.Join(", ", targets.Select(t => t.DbName))}",
                "GRAPH步位注册").ConfigureAwait(false);

            // 阶段一：台账构建（每 DB 独立，任何异常只记日志不中断其它 DB）
            var builtItems = new List<(GraphStepTarget Target, List<PLC_GraphStepTagEntity> Rows, List<int> StepNumbers)>();
            // 仍是 GRAPH 目标但本次未构建出台账的 DB（构建异常 / 无步号 / 超容量跳过）：GRAPH 设备失效清理时保留，
            // 只清除已不在目标里的 DB（解绑 / 删除）
            var ledgerFailedDbs = new List<string>();
            foreach (var target in targets)
            {
                try
                {
                    var built = await BuildLedgerAsync(projectId, target).ConfigureAwait(false);
                    if (built != null)
                    {
                        builtItems.Add((target, built.Value.Rows, built.Value.StepNumbers));
                    }
                    else
                    {
                        ledgerFailedDbs.Add(target.DbName);
                    }
                }
                catch (Exception ex)
                {
                    ledgerFailedDbs.Add(target.DbName);
                    _logger.LogError(ex, "[GRAPH步位注册] 项目 {ProjectId} DB {DbName} 台账构建异常，跳过",
                        projectId, target.DbName);
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GRAPH步位注册] 项目 {projectId} DB {target.DbName} 台账构建异常: {ex.Message}",
                        "GRAPH步位注册").ConfigureAwait(false);
                }
            }

            // 阶段二：网关注册——新 IGW：本项目全部 GRAPH DB 合并为一次批量请求写入项目通道的 GRAPH 设备
            // （sub-project B task-7，组 [工站|_未分组, DB, RT_DATA]/EXEC_BITS、[工站|_未分组, DB]/S_NO）；老网关保持每 DB 独立注册
            // 全项目运行（changedDbNames == null）时一并清除 IGW 台账中已不再是 GRAPH 目标的 DB（解绑/删除）；
            // 范围收窄运行不清（其它 DB 只是不在本次范围）
            var igwTarget = targets.FirstOrDefault(t => t.Flavor == GatewayFlavor.NewIgw);
            if (igwTarget != null)
            {
                try
                {
                    await RegisterNewIgwBulkAsync(projectId, igwTarget,
                        builtItems.Where(x => x.Target.Flavor == GatewayFlavor.NewIgw).ToList(),
                        removeStaleDbs: changedDbNames == null, retainDbs: ledgerFailedDbs).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[GRAPH步位注册] 项目 {ProjectId} 新 IGW 批量注册异常", projectId);
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GRAPH步位注册] 项目 {projectId} 新 IGW 批量注册异常: {ex.Message}",
                        "GRAPH步位注册").ConfigureAwait(false);
                }
            }
            foreach (var flavorGroup in builtItems.GroupBy(x => x.Target.Flavor))
            {
                if (flavorGroup.Key == GatewayFlavor.NewIgw)
                {
                    // 已在上方整体批量注册
                }
                else
                {
                    foreach (var item in flavorGroup)
                    {
                        try
                        {
                            await RegisterToGatewayAsync(projectId, item.Target, item.Rows).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[GRAPH步位注册] 项目 {ProjectId} DB {DbName} 注册异常",
                                projectId, item.Target.DbName);
                            await WriteLogHelper.WriteLogsAsync(
                                $"[GRAPH步位注册] 项目 {projectId} DB {item.Target.DbName} 注册异常: {ex.Message}",
                                "GRAPH步位注册").ConfigureAwait(false);
                        }
                    }
                }
            }

            // 阶段三：读值验证（每 DB 独立）
            foreach (var item in builtItems)
            {
                try
                {
                    await VerifyByReadTagsAsync(projectId, item.Target, item.Rows, item.StepNumbers).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[GRAPH步位注册] 项目 {ProjectId} DB {DbName} 读值验证异常",
                        projectId, item.Target.DbName);
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GRAPH步位注册] 项目 {projectId} DB {item.Target.DbName} 读值验证异常: {ex.Message}",
                        "GRAPH步位注册").ConfigureAwait(false);
                }
            }

            _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId} 全部 DB 处理完成", projectId);
            await WriteLogHelper.WriteLogsAsync($"[GRAPH步位注册] 项目 {projectId} 全部 DB 处理完成", "GRAPH步位注册").ConfigureAwait(false);
        }

        /// <summary>GRAPH 步进 DB 判定行（四表 join 的查询结果）。</summary>
        private sealed class GraphStepTarget
        {
            /// <summary>GRAPH FB 块 Id（步号 LogicUnit 挂在该块下）</summary>
            public long FbBlockId { get; init; }
            /// <summary>实例 DB 块 Id（台账 DbBlockId）</summary>
            public long? DbBlockId { get; init; }
            /// <summary>实例 DB 块名（如 DB161_HomeGraph）</summary>
            public string DbName { get; init; }
            /// <summary>绑定的 GRAPH FB 类型名（如 FB161_HomeGraph）</summary>
            public string FbName { get; init; }
            /// <summary>项目工位号（网关设备名前缀）</summary>
            public string StationId { get; init; }
            /// <summary>IOT 网关地址（已规范化：补 scheme、以 / 结尾）</summary>
            public string IotUrl { get; set; }
            /// <summary>PLC 直连 IP（新 IGW 注册用；来自 ai_plc_project_new.plcip，独立查询回填）</summary>
            public string PlcIp { get; set; }
            /// <summary>PLC 直连端口（新 IGW 注册用；null/0 → 网关按协议默认 S7=102）</summary>
            public int? PlcPort { get; set; }
            /// <summary>网关类型（按 IotUrl 探测：新 IGW /Tag/* 或老网关 /DriverInfo/*）</summary>
            public GatewayFlavor Flavor { get; set; }
            /// <summary>工站代码（ai_plc_stationblockbinding 按 FB 名匹配；无绑定为空）</summary>
            public string StationCode { get; set; }
            /// <summary>工站名称（ai_plc_station 主数据；缺失为空）</summary>
            public string StationName { get; set; }
            /// <summary>重建台账前该 DB 未软删台账行的 StationCode 集合（仅新 IGW 采集；空值=旧行未绑定工站 → _未分组），
            /// 用于 GRAPH 设备 replaceGroups 清除工站改绑前的旧位置</summary>
            public List<string> PreviousStationCodes { get; set; } = new List<string>();
        }

        /// <summary>
        /// 判定 SQL（一条查询，四表 join）：
        /// 绑定表 bnd JOIN GRAPH FB 块 fb（同名 FB、GRAPH 语言、本项目）JOIN 实例 DB 块 db（bnd.BlockId）JOIN 项目 p。
        /// 命中即"该项目存在 GRAPH 步进 DB"。
        /// </summary>
        private async Task<List<GraphStepTarget>> LoadGraphStepTargetsAsync(long projectId)
        {
            var targets = await QueryGraphStepTargetsAsync(_db, projectId).ConfigureAwait(false);

            // 网关地址规范化：裸 IP/域名（无 http(s):// 前缀）补 http://，
            // 避免下游构造 Uri 抛 Invalid URI 导致整批注册失败（曾因 ioturl='10.10.10.10' 全量 RegFailed）。
            foreach (var t in targets)
            {
                var normalized = NormalizeGatewayUrl(t.IotUrl);
                if (!string.Equals(normalized, t.IotUrl, StringComparison.Ordinal))
                {
                    _logger.LogWarning("[GRAPH步位注册] 项目 {ProjectId} IotUrl 不规范（{RawUrl}），已自动规范化为 {NormalizedUrl}。建议到项目配置改为完整地址（如 http://<网关IP>:2500/）",
                        projectId, t.IotUrl, normalized);
                    t.IotUrl = normalized;
                }

                // 网关类型按 URL 判定（2026-09-19 I-2）：老网关端口 2500 → 老网关路径，其余一律 IGW。
                // 不再用 DetectGatewayAsync 探测——IGW 宕机/网络不通时探测退化为 Legacy（且缓存 5 分钟），
                // 会把老网关协议请求发给 IGW；IGW 不可达时由批量接口探测报失败（GraphIgwRegistrar）
                t.Flavor = ResolveGatewayFlavor(t.IotUrl);
            }

            return await CompleteTargetsAsync(projectId, targets).ConfigureAwait(false);
        }

        /// <summary>网关类型按 URL 判定：老网关 URL（端口 2500，<see cref="DcsIgwConsts.IsLegacyIotGatewayUrl"/>）→ Legacy，其余 → NewIgw</summary>
        internal static GatewayFlavor ResolveGatewayFlavor(string iotUrl) =>
            DcsIgwConsts.IsLegacyIotGatewayUrl(iotUrl) ? GatewayFlavor.Legacy : GatewayFlavor.NewIgw;

        /// <summary>
        /// 项目是否存在 GRAPH 步进 DB（与 <see cref="LoadGraphStepTargetsAsync"/> 同一判定口径）。
        /// IGW 注册任务在台账无 Graph 行时据此决定是否触发全项目 GRAPH 重注册（I-1 过渡安全）。
        /// </summary>
        public static async Task<bool> HasGraphTargetsAsync(ISqlSugarClient db, long projectId)
        {
            ArgumentNullException.ThrowIfNull(db);
            var targets = await QueryGraphStepTargetsAsync(db, projectId).ConfigureAwait(false);
            return targets.Count > 0;
        }

        /// <summary>
        /// 工步相关块的块名（GRAPH FB 的实例 DB），给 IGW 注册决定扫描率用。
        ///
        /// 2026-09-22：工步状态（转移条件、动作输出、步时长 T/T_MAX）就住在这些块里，
        /// 注册时给它们 GraphDbScanRateMs（100ms），其余 DB 点保持 DbScanRateMs（1000ms）。
        /// **刻意复用 <see cref="QueryGraphStepTargetsAsync"/>**——与 GRAPH 步位注册、
        /// <see cref="HasGraphTargetsAsync"/> 同一套判定，免得两处对"什么是工步块"的定义漂移。
        /// </summary>
        public static async Task<IReadOnlyCollection<string>> QueryGraphDbBlockNamesAsync(
            ISqlSugarClient db, long projectId)
        {
            ArgumentNullException.ThrowIfNull(db);
            var targets = await QueryGraphStepTargetsAsync(db, projectId).ConfigureAwait(false);
            return targets
                .Select(t => t.DbName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>判定 SQL + 脏数据过滤 + 按 DB 名去重（不做网关探测 / 工站解析）</summary>
        private static async Task<List<GraphStepTarget>> QueryGraphStepTargetsAsync(ISqlSugarClient db, long projectId)
        {
            var rows = await db.Queryable<PLC_DbFbBindingEntity>()
                .InnerJoin<PLC_BlockEntity>((bnd, fb) =>
                    fb.BlockName == bnd.InstanceOfName
                    && fb.BlockType == "FB"
                    && fb.ProgrammingLanguage == "GRAPH"
                    && fb.IsDeleted == false
                    && fb.ProjectId == projectId)
                .InnerJoin<PLC_BlockEntity>((bnd, fb, db) =>
                    db.IsDeleted == false
                    // 双路匹配：上传链路产的绑定行带真实 BlockId（主路）；历史/手工插入的绑定行 blockid 可能为 NULL，
                    // 回退按 (DbName, ProjectId, DB 族块类型) 名称匹配（t4 打样发现现网唯一绑定行 blockid IS NULL）。
                    && (db.Id == bnd.BlockId
                        || (db.BlockName == bnd.DbName
                            && db.ProjectId == projectId
                            && (db.BlockType == "InstanceDB" || db.BlockType == "DB"))))
                // 项目已软删（删除前已排队的 GRAPH 任务）→ 无目标，不向 IGW 发请求（免得重建刚被清理的通道/设备）
                .InnerJoin<PLC_ProjectEntity>((bnd, fb, db, p) => p.Id == projectId && p.IsDeleted == false)
                .Where((bnd, fb, db, p) =>
                    bnd.IsDeleted == false
                    && bnd.ProjectId == projectId)
                .Select((bnd, fb, db, p) => new GraphStepTarget
                {
                    FbBlockId = fb.Id,
                    DbBlockId = bnd.BlockId,
                    DbName = bnd.DbName,
                    FbName = bnd.InstanceOfName,
                    StationId = p.StationId,
                    IotUrl = p.IotUrl
                })
                .Distinct()
                .ToListAsync()
                .ConfigureAwait(false);

            // 防御：过滤掉 DbName / BlockId / IotUrl / StationId 缺失的脏数据行
            return rows
                .Where(t => !string.IsNullOrWhiteSpace(t.DbName)
                            && t.DbBlockId > 0
                            && !string.IsNullOrWhiteSpace(t.IotUrl)
                            && !string.IsNullOrWhiteSpace(t.StationId))
                .GroupBy(t => t.DbName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>IGW 目标补 PLC 直连地址、全部目标解析工站/工位</summary>
        private async Task<List<GraphStepTarget>> CompleteTargetsAsync(long projectId, List<GraphStepTarget> targets)
        {
            var newIgwUrls = targets
                .Where(t => t.Flavor == GatewayFlavor.NewIgw)
                .Select(t => t.IotUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (newIgwUrls.Count > 0)
            {
                _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId} 探测到新 IGW 网关（/Tag/* 契约）: {Urls}",
                    projectId, string.Join(", ", newIgwUrls));
                await WriteLogHelper.WriteLogsAsync(
                    $"[GRAPH步位注册] 项目 {projectId} 探测到新 IGW 网关（/Tag/* 契约）: {string.Join(", ", newIgwUrls)}",
                    "GRAPH步位注册").ConfigureAwait(false);

                // 新 IGW 才需要 PLC 直连地址：单独查一次项目表（与主 join 解耦——
                // 若 plcip/plcport 列尚未迁移，这里 catch 降级为空，主流程仍可跑老网关与新 IGW 的明确 RegFailed，
                // 不会让整个任务因 SQL 缺列而失败）。
                string projectPlcIp = null;
                int? projectPlcPort = null;
                try
                {
                    var plcAddr = await _db.Queryable<PLC_ProjectEntity>()
                        .Where(p => p.Id == projectId && p.IsDeleted == false)
                        .Select(p => new PLC_ProjectEntity { Id = p.Id, PlcIp = p.PlcIp, PlcPort = p.PlcPort })
                        .FirstAsync()
                        .ConfigureAwait(false);
                    projectPlcIp = plcAddr?.PlcIp;
                    projectPlcPort = plcAddr?.PlcPort;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[GRAPH步位注册] 项目 {ProjectId} 读取 PLC 直连地址失败（plcip/plcport 列可能未迁移，见 tools/mig_20260825_add_plcip_plcport.sql）: {Error}",
                        projectId, ex.Message);
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GRAPH步位注册] 项目 {projectId} 读取 PLC 直连地址失败（plcip/plcport 列可能未迁移）: {ex.Message}",
                        "GRAPH步位注册").ConfigureAwait(false);
                }

                foreach (var t in targets.Where(x => x.Flavor == GatewayFlavor.NewIgw))
                {
                    t.PlcIp = projectPlcIp;
                    t.PlcPort = projectPlcPort;
                }
            }

            // 工站/工位解析（2026-08-26）：ai_plc_stationblockbinding（按 FB 名 + 项目）→ stationcode，
            // 再经 ai_plc_station（无后端实体，原生 SQL）关联 ai_plc_workcell 取工位。
            // 任一环节数据缺失保持 NULL，不阻塞注册主流程。
            await ResolveStationInfoAsync(projectId, targets).ConfigureAwait(false);

            return targets;
        }

        /// <summary>
        /// 解析工站/工位并回填 targets：stationblockbinding（实体查询）→ stationcode；
        /// station/workcell 主数据表无后端实体，走原生 SQL（表可能不存在/为空，全程 NULL-safe + try-catch 降级）。
        /// 同一 FB 多工站共享（isshared）时按证据锚点数投票取主导工位。
        /// </summary>
        private async Task ResolveStationInfoAsync(long projectId, List<GraphStepTarget> targets)
        {
            try
            {
                var fbNames = targets
                    .Select(t => t.FbName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (fbNames.Count == 0) return;

                var bindings = await _db.Queryable<PLC_StationBlockBindingEntity>()
                    .Where(b => b.ProjectId == projectId && b.IsDeleted == false && fbNames.Contains(b.BlockName))
                    .Select(b => new { b.BlockName, b.StationCode, b.EvidenceJson })
                    .ToListAsync()
                    .ConfigureAwait(false);

                // 工位白名单（2026-08-26）：本项目 PLC 对应的工位（Basic_SubStation.PlcName 绑定），
                // 过滤跨 PLC 的错误绑定候选（如 PLC1 的 FB 绑到 OP090——OP080~OP100 属于 PLC2）
                var allowedStations = await _stationPlcScopeService.GetAllowedStationsAsync(projectId).ConfigureAwait(false);
                if (allowedStations != null)
                {
                    bindings = bindings
                        .Where(b => StationPlcScopeService.IsAllowed(b.StationCode, allowedStations))
                        .ToList();
                }

                var stationCodes = bindings
                    .Select(b => b.StationCode?.Trim())
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (stationCodes.Count == 0) return;

                // 工站名（原生 SQL：ai_plc_station 无后端实体；工位暂不落库——2026-08-26 决策）
                var stationMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var safeCodes = stationCodes.Where(IsSafeDbIdentifier).ToList();
                if (safeCodes.Count > 0)
                {
                    var inList = string.Join(", ", safeCodes.Select(c => $"'{c}'"));
                    var sql = $@"SELECT stationcode, stationname FROM ai_plc_station
WHERE isdeleted = false AND stationcode IN ({inList})";
                    var stationRows = await _db.Ado.SqlQueryAsync<dynamic>(sql).ConfigureAwait(false);
                    foreach (var row in stationRows)
                    {
                        var dict = (IDictionary<string, object>)row;
                        var code = Convert.ToString(dict.TryGetValue("stationcode", out var sc) ? sc : null)?.Trim();
                        if (string.IsNullOrWhiteSpace(code)) continue;
                        stationMap[code] = Convert.ToString(dict.TryGetValue("stationname", out var sn) ? sn : null);
                    }
                }

                foreach (var t in targets)
                {
                    // 多工站共享块（isshared）按证据锚点数投票取主导工位（2026-08-26）：
                    // evidencejson 数组内 "anchorKey" 出现次数 = 该站绑定证据强度；
                    // 平票时按工位代码字典序稳定取值，保证幂等重跑分组一致
                    var code = bindings
                        .Where(b => string.Equals(b.BlockName, t.FbName, StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(b.StationCode))
                        .GroupBy(b => b.StationCode.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Select(g => new { Station = g.Key, Score = g.Sum(x => ScoreStationEvidence(g.Key, x.EvidenceJson)) })
                        .OrderByDescending(x => x.Score)
                        .ThenBy(x => x.Station, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.Station)
                        .FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    t.StationCode = code;
                    if (stationMap.TryGetValue(code, out var stationName))
                    {
                        t.StationName = stationName;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[GRAPH步位注册] 项目 {ProjectId} 工站/工位解析失败（不阻塞注册）: {Error}", projectId, ex.Message);
            }
        }

        /// <summary>
        /// 绑定证据强度计分：锚点条数（"anchorKey" 出现次数）+ 工位代码在证据变量名中出现次数。
        /// 第二项是一致性校验：锚点变量名引用工位命名数据块（DB345_OP90Cylinder / DB3700_HMIDrive_Info.OP090_Info 等）
        /// 时与该站代码一致则加分——染色阶段偶发把带工位标识的锚点投错桶（如 FB360 的 OP090 证据染进 OP010），
        /// 靠该信号纠正。空/坏数据按 1 分计。
        /// </summary>
        private static int ScoreStationEvidence(string stationCode, string evidenceJson)
        {
            if (string.IsNullOrWhiteSpace(evidenceJson)) return 1;
            var anchors = Math.Max(CountOccurrences(evidenceJson, "\"anchorKey\""), 1);
            var codeHits = string.IsNullOrWhiteSpace(stationCode) ? 0 : CountOccurrences(evidenceJson, stationCode);
            return anchors + codeHits;
        }

        private static int CountOccurrences(string text, string token)
        {
            var count = 0;
            var idx = 0;
            while ((idx = text.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                idx += token.Length;
            }
            return count;
        }

        /// <summary>工站代码白名单校验（拼 IN 列表防注入）：字母/数字/下划线/连字符，≤50 字符。</summary>
        private static bool IsSafeDbIdentifier(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                   && value.Length <= 50
                   && value.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');
        }

        /// <summary>规范化网关地址：缺 scheme 时补 http://，统一以 / 结尾。空值原样返回（由上游过滤）。</summary>
        private static string NormalizeGatewayUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            var trimmed = url.Trim();
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "http://" + trimmed;
            }
            return trimmed.TrimEnd('/') + "/";
        }

        /// <summary>构建单个 GRAPH 步进 DB 的台账（步号展开 → 偏移回填 → 落库），返回 (台账行, 步号集合)；跳过时返回 null。</summary>
        private async Task<(List<PLC_GraphStepTagEntity> Rows, List<int> StepNumbers)?> BuildLedgerAsync(long projectId, GraphStepTarget target)
        {
            // 1. 查步号集合：该 FB 下 STEP_x_ACTION LogicUnit 的 NetworkId（1 基步号）
            var fbId = target.FbBlockId;
            var stepActionLus = await _logicUnitService
                .GetListAsync(lu => lu.BlockId == fbId && lu.IsDeleted == false)
                .ConfigureAwait(false);

            var stepNumbers = stepActionLus
                .Where(lu => !string.IsNullOrWhiteSpace(lu.UnitType)
                             && StepActionUnitTypeRegex.IsMatch(lu.UnitType.Trim())
                             && lu.NetworkId.HasValue
                             && lu.NetworkId.Value >= 1)
                .Select(lu => lu.NetworkId.Value)
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            // 2. 健全性检查
            if (stepNumbers.Count == 0)
            {
                _logger.LogWarning("[GRAPH步位注册] DB {DbName}（FB {FbName}）无 STEP_x_ACTION 步号，跳过", target.DbName, target.FbName);
                await WriteLogHelper.WriteLogsAsync(
                    $"[GRAPH步位注册] DB {target.DbName}（FB {target.FbName}）无 STEP_x_ACTION 步号，跳过",
                    "GRAPH步位注册").ConfigureAwait(false);
                return null;
            }

            var maxStep = stepNumbers.Max();
            if (maxStep - 1 > MAX_BIT_INDEX)
            {
                _logger.LogWarning(
                    "[GRAPH步位注册] DB {DbName} 最大步号 {MaxStep} 超出 EXEC_BITS 数组容量（250 位），全量跳过",
                    target.DbName, maxStep);
                await WriteLogHelper.WriteLogsAsync(
                    $"[GRAPH步位注册] DB {target.DbName} 最大步号 {maxStep} 超出 EXEC_BITS 数组容量（250 位），全量跳过",
                    "GRAPH步位注册").ConfigureAwait(false);
                return null;
            }

            var parseRunId = $"graphsteptag_{projectId}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}";

            // 3. 生成台账行：EXEC_BITS[n-1] 逐位 + S_NO 校验位
            var ledgerRows = new List<PLC_GraphStepTagEntity>();
            foreach (var n in stepNumbers)
            {
                ledgerRows.Add(new PLC_GraphStepTagEntity
                {
                    ProjectId = projectId,
                    DbBlockId = target.DbBlockId,
                    DbName = target.DbName,
                    FbName = target.FbName,
                    Symbol = $"{target.DbName}.RT_DATA.EXEC_BITS.EXEC_BITS[{n - 1}]",
                    StationCode = target.StationCode,
                    StationName = target.StationName,
                    TagCategory = TAG_CATEGORY_EXEC_BIT,
                    StepNumber = n,
                    BitIndex = n - 1,
                    SequenceId = string.Empty,
                    BranchLabel = string.Empty,
                    RegStatus = STATUS_PENDING,
                    ParseRunId = parseRunId
                });
            }

            ledgerRows.Add(new PLC_GraphStepTagEntity
            {
                ProjectId = projectId,
                DbBlockId = target.DbBlockId,
                DbName = target.DbName,
                FbName = target.FbName,
                Symbol = $"{target.DbName}.S_NO",
                StationCode = target.StationCode,
                StationName = target.StationName,
                TagCategory = TAG_CATEGORY_STEP_NO,
                StepNumber = null,
                BitIndex = null,
                SequenceId = string.Empty,
                BranchLabel = string.Empty,
                RegStatus = STATUS_PENDING,
                ParseRunId = parseRunId
            });

            // 3.5 偏移量优先回填 DbAddress（2026-08-26）：查 ai_plc_blockinterface 的 OffsetByte/OffsetBit，
            // 可用（非优化块）→ EXEC_BIT=DB{号}.DBX{字}.{位}、S_NO=DB{号}.DBW{字}；查不到保持 NULL（注册时回退数组符号）
            await ApplyOffsetAddressesAsync(target, ledgerRows).ConfigureAwait(false);

            // 3.8 新 IGW：软删前记下旧台账工站（工站改绑时 GRAPH 设备需清除旧工站下的 [工站, DB] 组）
            if (target.Flavor == GatewayFlavor.NewIgw)
            {
                var previousStations = await _db.Queryable<PLC_GraphStepTagEntity>()
                    .Where(x => x.ProjectId == projectId && x.DbName == target.DbName && x.IsDeleted == false)
                    .Select(x => x.StationCode)
                    .Distinct()
                    .ToListAsync()
                    .ConfigureAwait(false);
                target.PreviousStationCodes = previousStations ?? new List<string>();
            }

            // 4. 台账落库：先软删该 (ProjectId, DbName) 旧记录，再插入新行
            await _db.Updateable<PLC_GraphStepTagEntity>()
                .SetColumns(x => new PLC_GraphStepTagEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                .Where(x => x.ProjectId == projectId && x.DbName == target.DbName && x.IsDeleted == false)
                .ExecuteCommandAsync()
                .ConfigureAwait(false);

            foreach (var row in ledgerRows)
            {
                SetAuditFields(row);
            }

            await _graphStepTagService.InsertRangeAsync(ledgerRows).ConfigureAwait(false);

            _logger.LogInformation(
                "[GRAPH步位注册] DB {DbName} 台账已落库：步号 {MinStep}~{MaxStep} 共 {ExecCount} 个 EXEC_BIT + 1 个 S_NO（ParseRunId={ParseRunId}）",
                target.DbName, stepNumbers.Min(), maxStep, stepNumbers.Count, parseRunId);
            await WriteLogHelper.WriteLogsAsync(
                $"[GRAPH步位注册] DB {target.DbName} 台账落库 {ledgerRows.Count} 行（EXEC_BIT {stepNumbers.Count} + S_NO 1，步号 {stepNumbers.Min()}~{maxStep}，ParseRunId={parseRunId}）",
                "GRAPH步位注册").ConfigureAwait(false);

            return (ledgerRows, stepNumbers);
        }

        /// <summary>
        /// 偏移量优先回填 DbAddress（2026-08-26）：查 ai_plc_blockinterface 的 OffsetByte/OffsetBit，
        /// 可用（非优化块，parser 已预计算）→ EXEC_BIT 行=DB{号}.DBX{字}.{位}、S_NO 行=DB{号}.DBW{字}；
        /// 查不到（GRAPH 优化块偏移已按方案B清理，全部 NULL）→ 保持 NULL，注册时回退数组整体符号 / DB.S_NO。
        /// 失败只告警，不阻塞台账。
        /// </summary>
        private async Task ApplyOffsetAddressesAsync(GraphStepTarget target, List<PLC_GraphStepTagEntity> ledgerRows)
        {
            try
            {
                var blockNumber = await _db.Queryable<PLC_BlockEntity>()
                    .Where(b => b.Id == target.DbBlockId && b.IsDeleted == false)
                    .Select(b => b.BlockNumber)
                    .FirstAsync()
                    .ConfigureAwait(false);
                if (blockNumber == null || blockNumber <= 0)
                {
                    return;
                }

                var execRows = ledgerRows
                    .Where(r => string.Equals(r.TagCategory, TAG_CATEGORY_EXEC_BIT, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var wantedPaths = execRows
                    .Select(r => $"RT_DATA.EXEC_BITS.EXEC_BITS[{r.BitIndex ?? ((r.StepNumber ?? 1) - 1)}]")
                    .ToList();
                wantedPaths.Add("S_NO");

                var ifaceRows = await _db.Queryable<PLC_BlockInterfaceEntity>()
                    .Where(i => i.BlockId == target.DbBlockId && i.IsDeleted == false && wantedPaths.Contains(i.VariablePath))
                    .Select(i => new { i.VariablePath, i.OffsetByte, i.OffsetBit })
                    .ToListAsync()
                    .ConfigureAwait(false);

                var offsetMap = ifaceRows
                    .Where(i => i.OffsetByte != null)
                    .GroupBy(i => i.VariablePath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var row in execRows)
                {
                    var path = $"RT_DATA.EXEC_BITS.EXEC_BITS[{row.BitIndex ?? ((row.StepNumber ?? 1) - 1)}]";
                    if (offsetMap.TryGetValue(path, out var hit))
                    {
                        row.DbAddress = $"DB{blockNumber}.DBX{hit.OffsetByte}.{(int)(hit.OffsetBit ?? 0)}";
                    }
                }

                var snoRow = ledgerRows.FirstOrDefault(r =>
                    string.Equals(r.TagCategory, TAG_CATEGORY_STEP_NO, StringComparison.OrdinalIgnoreCase));
                if (snoRow != null && offsetMap.TryGetValue("S_NO", out var snoHit))
                {
                    snoRow.DbAddress = $"DB{blockNumber}.DBW{snoHit.OffsetByte}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[GRAPH步位注册] DB {DbName} 偏移量回填失败（保持 NULL，注册时回退数组符号地址）: {Error}",
                    target.DbName, ex.Message);
            }
        }

        /// <summary>老网关注册（ManualAddTagItems 逐位，2000 条一批），回写台账注册结果；新 IGW 走 RegisterNewIgwStationGroupAsync（工站分组聚合）。</summary>
        private async Task RegisterToGatewayAsync(long projectId, GraphStepTarget target, List<PLC_GraphStepTagEntity> ledgerRows)
        {
            var deviceName = $"{target.StationId}{DEVICE_SUFFIX_PLUS}";
            var tagItems = BuildTagItems(ledgerRows);

            // 老网关逐位注册：地址=符号本身（偏移量可用时 BuildLedgerAsync 已回填绝对地址，此处仅兜底）
            foreach (var row in ledgerRows)
            {
                row.DbAddress ??= row.Symbol;
            }

            for (var offset = 0; offset < tagItems.Count; offset += REGISTER_BATCH_SIZE)
            {
                var batchCount = Math.Min(REGISTER_BATCH_SIZE, tagItems.Count - offset);
                var batchItems = tagItems.GetRange(offset, batchCount);
                var batchRows = ledgerRows.GetRange(offset, batchCount);

                var response = await _iotService
                    .ManualAddTagItems(target.IotUrl, deviceName, batchItems)
                    .ConfigureAwait(false);

                if (response == null || !response.IsSuccess || response.BackResult == null)
                {
                    var error = response == null ? "网关无返回" : response.ErrorMessage;
                    foreach (var row in batchRows)
                    {
                        row.RegStatus = STATUS_REG_FAILED;
                        row.VerifyDetail = TruncateDetail($"网关注册调用失败: {error}");
                    }

                    _logger.LogError("[GRAPH步位注册] 项目 {ProjectId} DB {DbName} 第 {Batch} 批网关注册调用失败: {Error}",
                        projectId, target.DbName, offset / REGISTER_BATCH_SIZE + 1, error);
                    await WriteLogHelper.WriteLogsAsync(
                        $"[GRAPH步位注册] DB {target.DbName} 批 {offset / REGISTER_BATCH_SIZE + 1} 网关注册调用失败: {error}",
                        "GRAPH步位注册").ConfigureAwait(false);
                }
                else
                {
                    var now = DateTime.Now;
                    for (var i = 0; i < batchRows.Count; i++)
                    {
                        // 返回明细与注册列表按索引对位（ManualAddTagItemsResult 无地址字段）
                        var detail = i < response.BackResult.Count ? response.BackResult[i] : null;
                        if (detail != null && detail.IsGood)
                        {
                            batchRows[i].RegStatus = STATUS_REGISTERED;
                            batchRows[i].RegTime = now;
                        }
                        else
                        {
                            batchRows[i].RegStatus = STATUS_REG_FAILED;
                            batchRows[i].VerifyDetail = TruncateDetail($"注册失败: {detail?.ErrorMessage ?? "网关未返回该标签明细"}");
                        }
                    }
                }

                // 本批注册结果落库
                await PersistRowsAsync(batchRows).ConfigureAwait(false);
            }

            var okCount = ledgerRows.Count(r => r.RegStatus == STATUS_REGISTERED);
            var failCount = ledgerRows.Count(r => r.RegStatus == STATUS_REG_FAILED);
            _logger.LogInformation("[GRAPH步位注册] DB {DbName} 网关注册完成：成功 {Ok} / 失败 {Fail}（设备 {Device}）",
                target.DbName, okCount, failCount, deviceName);
            await WriteLogHelper.WriteLogsAsync(
                $"[GRAPH步位注册] DB {target.DbName} 网关注册完成：成功 {okCount} / 失败 {failCount}（设备 {deviceName}）",
                "GRAPH步位注册").ConfigureAwait(false);
        }

        /// <summary>台账行 → 网关 Tag 模型（顺序一致，注册结果按索引对位）。老/新网关共用此中间模型。</summary>
        private static List<IQTagItem> BuildTagItems(List<PLC_GraphStepTagEntity> ledgerRows)
        {
            // 台账行 → 网关 Tag（顺序一致，注册结果按索引对位）
            return ledgerRows.Select(r => new IQTagItem
            {
                Address = r.Symbol,
                ByteSize = 1,
                DataType = string.Equals(r.TagCategory, TAG_CATEGORY_EXEC_BIT, StringComparison.OrdinalIgnoreCase) ? "Bool" : "Int",
                TagPathNames = new List<string>
                {
                    r.DbName,
                    string.Equals(r.TagCategory, TAG_CATEGORY_EXEC_BIT, StringComparison.OrdinalIgnoreCase)
                        ? $"Step{r.StepNumber}"
                        : "S_NO"
                }
            }).ToList();
        }

        /// <summary>
        /// 新 IGW 注册（sub-project B task-7）：本项目全部 GRAPH DB 合并为一次批量请求（POST /api/v1/project/bulk/tags）
        /// 写入项目通道（同 DB/IO 注册的通道名）下的 GRAPH 设备（逻辑名 S7Plus_{ip}），编排见 <see cref="GraphIgwRegistrar"/>：
        /// 每 DB 两点——[工站|_未分组, DB, RT_DATA]/EXEC_BITS（bool 数组，维度=最大 BitIndex+1）+ [工站|_未分组, DB]/S_NO（int16）；
        /// replaceGroups 清除该 DB 在旧台账工站与新工站下的 [工站, DB] 组；发送前清理 通用/通用_S7Plus 旧设备。
        /// 取代原「AddTags 落 通用 通道 → Configurator 搬组到 通用_S7Plus」路径。
        /// 台账仍保留逐位行（溯源/验证明细）；注册结果按 DB 粒度回写全部 EXEC_BIT 行与 S_NO 行（Registered / RegFailed）。
        /// </summary>
        private async Task RegisterNewIgwBulkAsync(
            long projectId,
            GraphStepTarget first,
            List<(GraphStepTarget Target, List<PLC_GraphStepTagEntity> Rows, List<int> StepNumbers)> items,
            bool removeStaleDbs,
            IReadOnlyCollection<string> retainDbs)
        {
            if (items.Count == 0 && !removeStaleDbs)
            {
                return;
            }
            var plcIp = (first.PlcIp ?? string.Empty).Trim();
            var plcPort = first.PlcPort ?? 0; // 0 = 按协议默认（S7=102）
            var allRows = items.SelectMany(x => x.Rows).ToList();

            // 配置缺失守卫：提前失败并写明原因（不调用 IGW）
            if (plcIp.Length == 0)
            {
                if (items.Count == 0)
                {
                    // 仅清失效 DB 的运行：无台账行可标失败，不报错
                    _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId} 仅清失效 GRAPH DB 跳过：项目未配置 PLC 直连 IP（plcip）", projectId);
                    return;
                }
                foreach (var row in allRows)
                {
                    row.RegStatus = STATUS_REG_FAILED;
                    row.VerifyDetail = TruncateDetail("新IGW注册失败: 项目未配置 PLC 直连 IP（plcip）——编辑项目填写 PLC直连IP（或先在设备基础表维护真实 IP 后重新绑定设备）再点重新注册");
                }
                await PersistRowsAsync(allRows).ConfigureAwait(false);
                await RecordGraphFailureSafeAsync(projectId, "项目未配置 PLC 直连 IP（plcip）").ConfigureAwait(false);
                _logger.LogError("[GRAPH步位注册] 项目 {ProjectId}（{DbCount} 个 DB）新 IGW 注册跳过：项目未配置 PLC 直连 IP（plcip），请在项目配置补充后重跑",
                    projectId, items.Count);
                await WriteLogHelper.WriteLogsAsync(
                    $"[GRAPH步位注册] 项目 {projectId}（{items.Count} 个 DB）新 IGW 注册跳过：项目未配置 PLC 直连 IP（plcip）",
                    "GRAPH步位注册").ConfigureAwait(false);
                return;
            }

            // 通道名生成所需的项目信息（通道名已写入项目列时不依赖这些字段；读取失败降级为空）
            string projectName = null, lineName = null, plcCode = null;
            try
            {
                var info = await _db.Queryable<PLC_ProjectEntity>()
                    .Where(p => p.Id == projectId)
                    .Select(p => new PLC_ProjectEntity { Id = p.Id, ProjectName = p.ProjectName, ProductionLineName = p.ProductionLineName, PlcCode = p.PlcCode })
                    .FirstAsync()
                    .ConfigureAwait(false);
                projectName = info?.ProjectName;
                lineName = info?.ProductionLineName;
                plcCode = info?.PlcCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[GRAPH步位注册] 项目 {ProjectId} 读取项目名/产线/PLC 编码失败（通道名以项目列为准）: {Error}", projectId, ex.Message);
            }

            var inputs = new List<GraphIgwDbInput>();
            foreach (var item in items)
            {
                var execRows = item.Rows
                    .Where(r => string.Equals(r.TagCategory, TAG_CATEGORY_EXEC_BIT, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var hasStepNo = item.Rows.Any(r =>
                    string.Equals(r.TagCategory, TAG_CATEGORY_STEP_NO, StringComparison.OrdinalIgnoreCase));
                // dimensions 取最大 BitIndex+1（步号稀疏时 count < maxBitIndex+1，必须用下标上限覆盖）
                int? maxBitIndex = execRows.Count > 0
                    ? execRows.Max(r => r.BitIndex ?? ((r.StepNumber ?? 1) - 1))
                    : (int?)null;
                inputs.Add(new GraphIgwDbInput(item.Target.DbName, item.Target.StationCode, maxBitIndex, hasStepNo,
                    item.Target.PreviousStationCodes));
            }

            var registrar = new GraphIgwRegistrar(
                _bulkClient,
                new GraphLedgerStore(_igwRegistrationService),
                text => WriteLogHelper.WriteLogsAsync(text, "GRAPH步位注册"),
                _logger,
                AppsettingsConfig.IgwRegistration,
                new IgwProjectStateStore(_igwStateService));
            var result = await registrar.RegisterAsync(
                new GraphIgwProjectInfo(projectId, projectName, lineName, plcCode, first.IotUrl, plcIp, plcPort),
                inputs, CancellationToken.None, removeStaleDbs, retainDbs).ConfigureAwait(false);

            // 按 DB 回写台账行：Registered（DbAddress 未回填偏移量时记注册的符号地址）/ RegFailed（写明原因）
            var now = DateTime.Now;
            foreach (var item in items)
            {
                var outcome = result.ByDb.TryGetValue((item.Target.DbName ?? string.Empty).Trim(), out var o)
                    ? o
                    : new GraphIgwDbOutcome(false, "未生成注册请求");
                foreach (var row in item.Rows)
                {
                    if (outcome.Registered)
                    {
                        row.DbAddress ??= string.Equals(row.TagCategory, TAG_CATEGORY_EXEC_BIT, StringComparison.OrdinalIgnoreCase)
                            ? BuildExecBitsArraySymbol(item.Target.DbName)
                            : $"{item.Target.DbName}.S_NO";
                        row.RegStatus = STATUS_REGISTERED;
                        row.RegTime = now;
                        row.VerifyDetail = null;
                    }
                    else
                    {
                        row.RegStatus = STATUS_REG_FAILED;
                        row.VerifyDetail = TruncateDetail($"新IGW注册失败: {outcome.Error}");
                    }
                }
            }
            await PersistRowsAsync(allRows).ConfigureAwait(false);
            if (items.Count == 0)
            {
                // 仅清失效 DB：结果日志由 GraphIgwRegistrar 自行写出（有实际清除动作时）
                _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId} 仅清失效 GRAPH DB：{Summary}", projectId, result.Summary);
                return;
            }

            var okCount = allRows.Count(r => r.RegStatus == STATUS_REGISTERED);
            var failCount = allRows.Count(r => r.RegStatus == STATUS_REG_FAILED);
            _logger.LogInformation("[GRAPH步位注册] 项目 {ProjectId}（{DbCount} 个 DB）新 IGW 批量注册完成：台账行成功 {Ok} / 失败 {Fail}；{Summary}",
                projectId, items.Count, okCount, failCount, result.Summary);
            await WriteLogHelper.WriteLogsAsync(
                $"[GRAPH步位注册] 项目 {projectId}（{items.Count} 个 DB）新 IGW 批量注册完成：台账行成功 {okCount} / 失败 {failCount}；{result.Summary}",
                "GRAPH步位注册").ConfigureAwait(false);
        }

        /// <summary>
        /// 全项目运行但项目已无任何 GRAPH 目标（全部解绑/删除）时的失效清理：IGW 台账有 Graph 根才探测网关，
        /// 新 IGW 则以「仅清失效」模式调用注册器（replaceGroups-only 请求 + 台账移除）。任何失败只记日志。
        /// </summary>
        private async Task TryClearStaleGraphWithoutTargetsAsync(long projectId)
        {
            try
            {
                var ledger = await _igwRegistrationService.GetLedgerAsync(projectId).ConfigureAwait(false);
                if (ledger == null || ledger.StationByDb.Count == 0)
                {
                    return;
                }

                var project = await _db.Queryable<PLC_ProjectEntity>()
                    .Where(p => p.Id == projectId && p.IsDeleted == false)
                    .Select(p => new PLC_ProjectEntity { Id = p.Id, IotUrl = p.IotUrl, PlcIp = p.PlcIp, PlcPort = p.PlcPort })
                    .FirstAsync()
                    .ConfigureAwait(false);
                if (project == null || string.IsNullOrWhiteSpace(project.IotUrl))
                {
                    return;
                }

                var target = new GraphStepTarget
                {
                    IotUrl = NormalizeGatewayUrl(project.IotUrl),
                    PlcIp = project.PlcIp,
                    PlcPort = project.PlcPort,
                };
                target.Flavor = ResolveGatewayFlavor(target.IotUrl);
                if (target.Flavor != GatewayFlavor.NewIgw)
                {
                    return;
                }

                await RegisterNewIgwBulkAsync(projectId, target,
                    new List<(GraphStepTarget Target, List<PLC_GraphStepTagEntity> Rows, List<int> StepNumbers)>(),
                    removeStaleDbs: true, retainDbs: Array.Empty<string>()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GRAPH步位注册] 项目 {ProjectId} 无 GRAPH 目标时清除失效 GRAPH DB 失败（下次全项目运行重试）", projectId);
            }
        }

        /// <summary>N-6：GRAPH 注册在注册器之前就失败（缺 plcip）时同样记失败时间，DB/IO 注册据此冷却自动重请求</summary>
        private async Task RecordGraphFailureSafeAsync(long projectId, string error)
        {
            try
            {
                await _igwStateService.SaveGraphFailureAsync(projectId, DateTime.Now, error).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GRAPH步位注册] 项目 {ProjectId} 记录 GRAPH 注册失败时间失败", projectId);
            }
        }

        /// <summary>GRAPH 注册的通道名/IGW 台账持久化（IPLC_IgwRegistrationService，与 DB/IO 注册同表同通道列）</summary>
        private sealed class GraphLedgerStore : IIgwLedgerStore
        {
            private readonly IPLC_IgwRegistrationService _service;

            public GraphLedgerStore(IPLC_IgwRegistrationService service) => _service = service;

            public Task<string> GetChannelNameAsync(long projectId) => _service.GetChannelNameAsync(projectId);

            public Task SetChannelNameAsync(long projectId, string channelName) => _service.SetChannelNameAsync(projectId, channelName);

            public Task<IgwLedgerSnapshot> GetLedgerAsync(long projectId) => _service.GetLedgerAsync(projectId);

            public Task ReplaceLedgerAsync(long projectId, IReadOnlyList<PLC_IgwRegistrationEntity> rows,
                IReadOnlyList<(string RootKind, string RootName)> removedRoots) =>
                _service.ReplaceForProjectAsync(projectId, rows, removedRoots);
        }

        /// <summary>单点读（整组读失败时降级定位用）：返回该别名的读结果；无返回时构造 IsGood=false 结果。</summary>
        private async Task<PLC_ReadTagResult> ReadPointAsync(string iotUrl, string alias, string batchError)
        {
            var single = await _iotService.ReadTagsAsList(iotUrl, new List<string> { alias }).ConfigureAwait(false);
            if (single != null && single.BackResult != null && single.BackResult.Count > 0)
            {
                return single.BackResult[0];
            }
            return new PLC_ReadTagResult
            {
                IsGood = false,
                TagName = alias,
                ErrorMessage = single == null
                    ? (string.IsNullOrWhiteSpace(batchError) ? "逐点读无返回" : batchError)
                    : (single.ErrorMessage ?? "逐点读无返回"),
            };
        }

        /// <summary>
        /// 2026-09-22：判定 IGW 读值错误是否为连接级瞬态（值得重试）。
        /// 命中 BadNotConnected(0x808A0000) / BadCommunicationError、BadCommFailure(0x80050000，
        /// 2026-09-22 实测 AddTags 工程热重载窗口内的典型码) / BadConnectionClosed /
        /// BadTimeout / Timeout / 超时 等连接通讯类字样；地址类稳定错误（点位不存在 BadNodeIdUnknown、
        /// 无读取权限、数组形态异常、无块段）不在此列——重试也不会变。
        /// </summary>
        private static bool IsTransientIgwReadError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return false;
            }
            return error.Contains("BadNotConnected", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("BadCommunicationError", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("BadCommFailure", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("BadConnectionClosed", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("BadTimeout", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                   || error.Contains("超时", StringComparison.Ordinal);
        }

        /// <summary>ReadTags 读值验证：
        /// L1 IsGood==true；L2 EXEC_BIT 值为 bool / STEP_NO 值为数值；L3 S_NO=k 时 EXEC_BITS[k-1]==true（k 在步号集合内才校验）。</summary>
        private async Task VerifyByReadTagsAsync(
            long projectId,
            GraphStepTarget target,
            List<PLC_GraphStepTagEntity> ledgerRows,
            List<int> stepNumbers)
        {
            var deviceName = $"{target.StationId}{DEVICE_SUFFIX_PLUS}";

            // 只验证注册成功的行：全部 EXEC_BIT + S_NO（需 S_NO 参与 L3 交叉校验）
            var execRows = ledgerRows
                .Where(r => string.Equals(r.TagCategory, TAG_CATEGORY_EXEC_BIT, StringComparison.OrdinalIgnoreCase)
                            && r.RegStatus == STATUS_REGISTERED)
                .ToList();
            var stepNoRow = ledgerRows.FirstOrDefault(r =>
                string.Equals(r.TagCategory, TAG_CATEGORY_STEP_NO, StringComparison.OrdinalIgnoreCase)
                && r.RegStatus == STATUS_REGISTERED);

            var verifyRows = execRows.ToList();
            if (stepNoRow != null)
            {
                verifyRows.Add(stepNoRow);
            }

            if (verifyRows.Count == 0)
            {
                _logger.LogWarning("[GRAPH步位注册] DB {DbName} 无已注册标签可验证，跳过读值验证", target.DbName);
                await WriteLogHelper.WriteLogsAsync(
                    $"[GRAPH步位注册] DB {target.DbName} 无已注册标签可验证，跳过读值验证",
                    "GRAPH步位注册").ConfigureAwait(false);
                return;
            }

            // 读值调用按网关类型分派：结果统一为与 verifyRows 按索引对齐的 List<PLC_ReadTagResult>，
            // 新 IGW 读值经适配层宽松解析后，L1/L2/L3 校验逻辑与老网关完全共用。
            List<PLC_ReadTagResult> results;
            string readError;
            if (target.Flavor == GatewayFlavor.NewIgw)
            {
                var plcIp = (target.PlcIp ?? string.Empty).Trim();
                if (plcIp.Length == 0)
                {
                    // 注册阶段已按 RegFailed 记录过缺 IP；这里兜底（理论上无已注册行则进不到这里）
                    readError = "新IGW读值验证跳过: 项目未配置 PLC 直连 IP（ai_plc_project_new.plcip）";
                    results = null;
                }
                else
                {
                    // 数组整体注册的读值验证：整组读 EXEC_BITS 数组（1 点）+ S_NO（1 点），
                    // 数组值在本地按 BitIndex 展开为逐位 PLC_ReadTagResult，L1/L2/L3 校验逻辑与老网关完全共用。
                    // 别名（sub-project B task-7）：通用.S7Plus_{ip}.{工站|_未分组}.{DB}.RT_DATA.EXEC_BITS / .S_NO，
                    // 与 GraphRequestBuilder 注册的物理路径同一清洗规则（无组回退/双点问题一并消除）
                    var arrayAlias = IgwTagNaming.GraphReadAlias(plcIp, target.StationCode, target.DbName, GraphRequestBuilder.ExecBitsMember);
                    string stepNoAlias = null;
                    if (stepNoRow != null)
                    {
                        stepNoAlias = IgwTagNaming.GraphReadAlias(plcIp, target.StationCode, target.DbName, GraphRequestBuilder.StepNoMember);
                    }
                    var readNames = new List<string> { arrayAlias };
                    if (stepNoAlias != null)
                    {
                        readNames.Add(stepNoAlias);
                    }

                    // 2026-09-22 就绪探测：AddTags 落盘会触发热重载（工程替换→设备重连），实测
                    // 注册返回后数秒内读值为 BadCommFailure(0x80050000)（17:20/17:27 两批整批
                    // VerifyFailed 均为 3.7s 首读撞重载窗口；稳态后 readsErr=0）。在正式 L1/L2/L3
                    // 验证前轮询数组别名直到读通：仅对连接类瞬态错误继续等（最多 ~40s，间隔 2s），
                    // 地址类稳定错误（点位不存在等）立即放行走正常失败路径，保留快速失败语义。
                    for (var probe = 0; probe < 20; probe++)
                    {
                        var probeRead = await ReadPointAsync(target.IotUrl, arrayAlias, null).ConfigureAwait(false);
                        if (probeRead.IsGood)
                        {
                            if (probe > 0)
                            {
                                _logger.LogInformation("[GRAPH步位注册] DB {DbName} 就绪探测第 {Probe} 次通过，开始读值验证", target.DbName, probe + 1);
                            }
                            break;
                        }
                        var probeError = probeRead.ErrorMessage ?? string.Empty;
                        if (probe >= 19 || !IsTransientIgwReadError(probeError))
                        {
                            break;
                        }
                        var probeNote = $"[GRAPH步位注册] DB {target.DbName} 注册后读值未就绪（{probeError}），2s 后重探";
                        _logger.LogWarning("{Note}", probeNote);
                        await WriteLogHelper.WriteLogsAsync(probeNote, "GRAPH步位注册").ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }

                    // 2026-09-22 瞬态抖动重试：IGW 对 PLC 批读存在 ~2% 行级「点位质量异常」抖动
                    // （BadNotConnected 等；压测档案见 PLC_VariableAccessService.IgwLastGoodBits 注释——失败全部
                    // 集中在 PLC2、均匀散布各 DB 的 S7 连接瞬态 busy），一次抖动即令整 DB 标 VerifyFailed。
                    // 对连接级瞬态质量码最多补读 2 次（间隔 1.5s）；地址类稳定错误（点位不存在/无读取权限/
                    // 数组形态异常）不重试，保持快速失败原语义。
                    PLC_ReadTagResult arrayResult = null;
                    PLC_ReadTagResult snoResult = null;
                    List<object> arrayItems = null;
                    string arrayError = null;
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        var response = await _iotService.ReadTagsAsList(target.IotUrl, readNames).ConfigureAwait(false);
                        List<PLC_ReadTagResult> raw;
                        if (response != null && response.IsSuccess && response.BackResult != null)
                        {
                            raw = response.BackResult;
                        }
                        else
                        {
                            // 整组读失败（新 IGW all-or-nothing）→ 降级为数组 / S_NO 各单点读定位
                            var batchError = response == null ? "ReadTagsAsList 无返回" : response.ErrorMessage;
                            _logger.LogWarning("[GRAPH步位注册] DB {DbName} 新 IGW 整组读值失败（{Error}），降级为单点读定位",
                                target.DbName, batchError);
                            await WriteLogHelper.WriteLogsAsync(
                                $"[GRAPH步位注册] DB {target.DbName} 新 IGW 整组读值失败（{batchError}），降级为单点读定位",
                                "GRAPH步位注册").ConfigureAwait(false);

                            raw = new List<PLC_ReadTagResult>();
                            raw.Add(await ReadPointAsync(target.IotUrl, arrayAlias, batchError).ConfigureAwait(false));
                            if (stepNoAlias != null)
                            {
                                raw.Add(await ReadPointAsync(target.IotUrl, stepNoAlias, batchError).ConfigureAwait(false));
                            }
                        }

                        // 展开数组读值为逐位结果（与 verifyRows 按索引对齐，复用既有 L1/L2/L3 循环）
                        arrayResult = raw.Count > 0 ? raw[0] : null;
                        snoResult = stepNoAlias != null && raw.Count > 1 ? raw[1] : null;
                        arrayItems = arrayResult != null && arrayResult.IsGood
                            ? DcsIgwMapper.NormalizeDcsArray(arrayResult.Value)
                            : null;
                        arrayError = arrayResult == null
                            ? "网关未返回数组标签值"
                            : arrayResult.IsGood ? null : (arrayResult.ErrorMessage ?? "数组标签读取失败");

                        if (arrayItems != null || attempt >= 3 || !IsTransientIgwReadError(arrayError))
                        {
                            break;
                        }
                        var retryNote = $"[GRAPH步位注册] DB {target.DbName} 第 {attempt} 次读值遇瞬态抖动（{arrayError}），1.5s 后重试";
                        _logger.LogWarning("{Note}", retryNote);
                        await WriteLogHelper.WriteLogsAsync(retryNote, "GRAPH步位注册").ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
                    }

                    results = new List<PLC_ReadTagResult>(verifyRows.Count);
                    foreach (var row in verifyRows)
                    {
                        if (string.Equals(row.TagCategory, TAG_CATEGORY_EXEC_BIT, StringComparison.OrdinalIgnoreCase))
                        {
                            var bitIndex = row.BitIndex ?? ((row.StepNumber ?? 1) - 1);
                            if (arrayItems != null && bitIndex >= 0 && bitIndex < arrayItems.Count)
                            {
                                results.Add(new PLC_ReadTagResult
                                {
                                    IsGood = true,
                                    TagName = row.Symbol,
                                    Value = arrayItems[bitIndex],
                                });
                            }
                            else
                            {
                                results.Add(new PLC_ReadTagResult
                                {
                                    IsGood = false,
                                    TagName = row.Symbol,
                                    ErrorMessage = arrayItems == null
                                        ? $"数组读值失败: {arrayError ?? $"数组值非数组形态（{DescribeValue(arrayResult?.Value)}）"}"
                                        : $"数组长度不足: 需要位 {bitIndex}，实际 {arrayItems.Count} 位",
                                });
                            }
                        }
                        else
                        {
                            results.Add(snoResult ?? new PLC_ReadTagResult
                            {
                                IsGood = false,
                                TagName = row.Symbol,
                                ErrorMessage = "S_NO 标签未读取",
                            });
                        }
                    }

                    readError = null;
                }
            }
            else
            {
                var readSymbols = verifyRows.Select(r => r.Symbol).ToList();
                var response = await _iotService.ReadTags(target.IotUrl, new PLC_ReadTagParame
                {
                    DeviceName = deviceName,
                    TagList = readSymbols
                }).ConfigureAwait(false);
                if (response == null || !response.IsSuccess || response.BackResult == null)
                {
                    readError = response == null ? "ReadTags 无返回" : response.ErrorMessage;
                    results = null;
                }
                else
                {
                    readError = null;
                    results = response.BackResult;
                }
            }

            var now = DateTime.Now;

            if (results == null)
            {
                var error = readError ?? "读值调用无结果";
                foreach (var row in verifyRows)
                {
                    row.RegStatus = STATUS_VERIFY_FAILED;
                    row.VerifyDetail = TruncateDetail($"L1失败: {error}");
                    row.VerifyTime = now;
                }

                await PersistRowsAsync(verifyRows).ConfigureAwait(false);
                _logger.LogError("[GRAPH步位注册] DB {DbName} 读值验证 L1 整体失败: {Error}", target.DbName, error);
                await WriteLogHelper.WriteLogsAsync(
                    $"[GRAPH步位注册] DB {target.DbName} 读值验证 L1 整体失败: {error}",
                    "GRAPH步位注册").ConfigureAwait(false);
                return;
            }

            // L1 / L2 逐标签校验（返回明细与读取列表按索引对位）
            for (var i = 0; i < verifyRows.Count; i++)
            {
                var row = verifyRows[i];
                var result = i < results.Count ? results[i] : null;

                if (result == null || !result.IsGood)
                {
                    row.RegStatus = STATUS_VERIFY_FAILED;
                    row.VerifyDetail = TruncateDetail($"L1失败: {(result != null ? result.ErrorMessage : "网关未返回该标签明细")}");
                    row.VerifyTime = now;
                    continue;
                }

                // L2：EXEC_BIT 值必须是 bool；STEP_NO 值必须是数值
                if (string.Equals(row.TagCategory, TAG_CATEGORY_EXEC_BIT, StringComparison.OrdinalIgnoreCase))
                {
                    if (result.Value is bool)
                    {
                        row.RegStatus = STATUS_VERIFIED;
                        row.VerifyDetail = "L1/L2通过";
                    }
                    else
                    {
                        row.RegStatus = STATUS_VERIFY_FAILED;
                        row.VerifyDetail = TruncateDetail($"L2失败: EXEC_BIT 值非 bool（{DescribeValue(result.Value)}）");
                    }
                }
                else
                {
                    if (TryConvertToInt(result.Value, out _))
                    {
                        row.RegStatus = STATUS_VERIFIED;
                        row.VerifyDetail = "L1/L2通过";
                    }
                    else
                    {
                        row.RegStatus = STATUS_VERIFY_FAILED;
                        row.VerifyDetail = TruncateDetail($"L2失败: STEP_NO 值非数值（{DescribeValue(result.Value)}）");
                    }
                }

                row.VerifyTime = now;
            }

            // L3 交叉校验：仅 EXEC_BIT 全 Verified 且能读到 S_NO 数值时执行
            var allExecVerified = execRows.Count > 0 && execRows.All(r => r.RegStatus == STATUS_VERIFIED);
            var stepNoIndex = stepNoRow != null ? verifyRows.IndexOf(stepNoRow) : -1;
            var stepNoVerified = stepNoRow != null
                                  && stepNoRow.RegStatus == STATUS_VERIFIED
                                  && stepNoIndex >= 0
                                  && stepNoIndex < results.Count;

            if (allExecVerified && stepNoVerified
                && TryConvertToInt(results[stepNoIndex].Value, out var currentStep)
                && currentStep >= 1)
            {
                if (stepNumbers.Contains(currentStep))
                {
                    var activeBitRow = execRows.FirstOrDefault(r => r.StepNumber == currentStep);
                    if (activeBitRow != null)
                    {
                        var bitIndex = verifyRows.IndexOf(activeBitRow);
                        var bitResult = bitIndex >= 0 && bitIndex < results.Count
                            ? results[bitIndex]
                            : null;

                        if (bitResult != null && bitResult.Value is bool bitValue && bitValue)
                        {
                            activeBitRow.VerifyDetail = $"L3交叉通过 S_NO={currentStep}";
                            _logger.LogInformation(
                                "[GRAPH步位注册] DB {DbName} L3 交叉校验通过：S_NO={Step} 且 EXEC_BITS[{Bit}]=true",
                                target.DbName, currentStep, currentStep - 1);
                            await WriteLogHelper.WriteLogsAsync(
                                $"[GRAPH步位注册] DB {target.DbName} L3 交叉校验通过：S_NO={currentStep} 且 EXEC_BITS[{currentStep - 1}]=true",
                                "GRAPH步位注册").ConfigureAwait(false);
                        }
                        else
                        {
                            activeBitRow.VerifyDetail = TruncateDetail(
                                $"L3交叉未通过 S_NO={currentStep}（EXEC_BITS[{currentStep - 1}]={DescribeValue(bitResult?.Value)}）");
                            _logger.LogWarning(
                                "[GRAPH步位注册] DB {DbName} L3 交叉校验未通过：S_NO={Step} 但 EXEC_BITS[{Bit}]={Value}",
                                target.DbName, currentStep, currentStep - 1, DescribeValue(bitResult?.Value));
                            await WriteLogHelper.WriteLogsAsync(
                                $"[GRAPH步位注册] DB {target.DbName} L3 交叉校验未通过：S_NO={currentStep} 但 EXEC_BITS[{currentStep - 1}]={DescribeValue(bitResult?.Value)}",
                                "GRAPH步位注册").ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "[GRAPH步位注册] DB {DbName} 当前 S_NO={Step} 不在步号集合内，跳过 L3 交叉校验",
                        target.DbName, currentStep);
                }
            }

            // 验证结果落库
            await PersistRowsAsync(verifyRows).ConfigureAwait(false);

            var verifiedCount = verifyRows.Count(r => r.RegStatus == STATUS_VERIFIED);
            var verifyFailedCount = verifyRows.Count(r => r.RegStatus == STATUS_VERIFY_FAILED);
            _logger.LogInformation(
                "[GRAPH步位注册] DB {DbName} 读值验证完成：Verified {Verified} / VerifyFailed {Failed}",
                target.DbName, verifiedCount, verifyFailedCount);
            await WriteLogHelper.WriteLogsAsync(
                $"[GRAPH步位注册] DB {target.DbName} 读值验证完成：Verified {verifiedCount} / VerifyFailed {verifyFailedCount}",
                "GRAPH步位注册").ConfigureAwait(false);
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// EXEC_BITS 整组数组符号路径（数组注册 / 整组读共用）：{DB}.RT_DATA.EXEC_BITS（IGW 数组 tag 的 dbAddress 填数组容器路径，不带末段数组名）。
        /// 注意与元素形态区分：仅取数组元素时才是 {DB}.RT_DATA.EXEC_BITS.EXEC_BITS[n]（台账逐位行 Symbol 保持元素形态）。
        /// </summary>
        private static string BuildExecBitsArraySymbol(string dbName)
        {
            return $"{dbName}.RT_DATA.EXEC_BITS";
        }

        /// <summary>台账行状态持久化（按主键整行更新）。</summary>
        private async Task PersistRowsAsync(List<PLC_GraphStepTagEntity> rows)
        {
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            foreach (var row in rows)
            {
                row.ModifyTime = DateTime.Now;
            }

            await _db.Updateable(rows).ExecuteCommandAsync().ConfigureAwait(false);
        }

        /// <summary>审计字段（与 PLC_UploadApp.SetBaseField 同源语义；Hangfire 后台无 HttpContext 时取默认值）。</summary>
        private static void SetAuditFields(PLC_GraphStepTagEntity entity)
        {
            entity.CreateTime = DateTime.Now;
            entity.ModifyTime = entity.CreateTime;
            entity.IsDeleted = false;
            try
            {
                var currentUser = CurrentUserInfo.Instance;
                entity.CreaterId = currentUser.UserID;
                entity.CreaterName = currentUser.UserName;
                entity.ModifierId = currentUser.UserID;
                entity.ModifierName = currentUser.UserName;
            }
            catch
            {
                // 后台任务无登录上下文：保持默认
            }
        }

        /// <summary>VerifyDetail 截断（description varchar(500) 兜底，与约束体系 TruncateDescription 同思路）。</summary>
        private static string TruncateDetail(string detail)
        {
            if (string.IsNullOrEmpty(detail))
            {
                return detail;
            }

            return detail.Length <= VERIFY_DETAIL_MAX_LENGTH ? detail : detail.Substring(0, VERIFY_DETAIL_MAX_LENGTH) + "…";
        }

        /// <summary>尝试把读到的值转为 int（S_NO 数值判定）。</summary>
        private static bool TryConvertToInt(object value, out int result)
        {
            result = 0;
            if (value == null)
            {
                return false;
            }

            try
            {
                result = Convert.ToInt32(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读值描述（日志/VerifyDetail 用）。</summary>
        private static string DescribeValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            return value is bool b ? (b ? "true" : "false") : $"{value} ({value.GetType().Name})";
        }

        #endregion
    }
}
