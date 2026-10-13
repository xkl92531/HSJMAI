using Hangfire;
using Hangfire.States;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// IO 约束规则预处理服务
    /// 
    /// 职责：
    /// 1. 扫描项目中未处理的 Bool IO Tag
    /// 2. 逐信号调用 AI 推理约束关系（可配置并发数）
    /// 3. 将推理结果写入约束规则表
    /// 4. 更新 IOTag 的处理状态
    /// 
    /// 架构：
    /// - 由 Hangfire 后台调度，上传完成后自动触发
    /// - 使用 IOConstraintProgressService（Singleton）防重复 + 追踪进度
    /// - 直接调用 LLMModelService 获取 ChatClient，不走 SkillExecutionEngine
    /// - 复用 PlcDiagnosisService 中的 GetSignalLogicContextAsync / SaveIOConstraintsAsync
    /// - 支持长期记忆检索，将历史经验拼入Prompt提升约束推理准确性
    /// </summary>
    public class IOConstraintPreprocessService : ISelfScopedAutoInject
    {
        private readonly ILogger<IOConstraintPreprocessService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly PlcDiagnosisService _diagnosisService;
        private readonly LLMModelService _llmModelService;
        private readonly IOConstraintProgressService _progressService;
        private readonly DiagnosticMemoryService _memoryService;
        private readonly IPrompt_TemplateService _promptTemplateService;
        /// <summary>本服务实例（单次 Hangfire job）内缓存的系统提示词，避免每 LU 查库。</summary>
        private string? _cachedSystemPrompt;
        private static readonly SemaphoreSlim _databaseWriteSemaphore = new(1, 1);
        private static readonly SemaphoreSlim _logicContextReadSemaphore = new(1, 1);

        /// <summary>Phase1 关联发现超时（秒，约 2 次 Hangfire 心跳）</summary>
        private const int PHASE1_AI_TIMEOUT_SECONDS = 60;
        /// <summary>Phase2 约束推理超时（秒，约 4 次 Hangfire 心跳）</summary>
        private const int PHASE2_AI_TIMEOUT_SECONDS = 120;
        /// <summary>默认并发数</summary>
        private const int DEFAULT_CONCURRENCY = 1;
        /// <summary>Phase1 失败后重试次数</summary>
        private const int PHASE1_MAX_RETRIES = 0;
        /// <summary>Phase2 失败后重试次数</summary>
        private const int PHASE2_MAX_RETRIES = 0;
        /// <summary>每次AI调用检索的记忆条数</summary>
        private const int MEMORY_TOP_K = 5;
        /// <summary>状态批量落库阈值</summary>
        private const int STATUS_FLUSH_BATCH_SIZE = 50;
        /// <summary>处理中状态续跑超时时间</summary>
        private static readonly TimeSpan PROCESSING_RECOVERY_TIMEOUT = TimeSpan.FromMinutes(20);
        /// <summary>Phase2 最多带入的关联信号数（多点夹紧场景需要8个传感器+源信号，不能低于12）</summary>
        private const int MAX_PHASE2_RELATED_SIGNALS = 12;
        /// <summary>单个信号最多保留的逻辑上下文条数。
        /// 每次 Phase2 推理一个信号时，只喂入按分数排序的最高 1 条 LogicContext。
        /// 多个 LogicContext 拼一起会导致 AI 跨上下文混淆（例如把 LU A 的 Q→I 链跟 LU B 的引脚绑定串错），
        /// 进而推出错误约束。一个一个来更稳。</summary>
        private const int MAX_PROMPT_LOGIC_CONTEXTS = 1;
        /// <summary>Prompt 中单条紧凑逻辑最大长度</summary>
        private const int MAX_PROMPT_COMPACT_LOGIC = 2500;
        /// <summary>Prompt 中单条 AST root_node_json 最大长度</summary>
        private const int MAX_PROMPT_AST_NODE_JSON = 6000;

        /// <summary>
        /// FB 关键引脚名集合。当 DB 内部变量绑定在这些引脚上时，
        /// 允许该 DB 变量参与约束生成（不被 IO-only 过滤层拦截）。
        /// 包含：安全联锁、运行模式、许可条件、复位等语义关键引脚。
        /// </summary>
        internal static readonly HashSet<string> KeyFbPinNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "E_StopStatus",    // 急停安全联锁
            "ManualMod",       // 手动模式
            "AutoRunMod",      // 自动运行模式
            "FwdPmt",          // 前进许可
            "BkdPmt",          // 后退许可
            "ResetStatus",     // 复位状态
            "Model",           // 型号/模式选择
            "PmtDebug",        // 调试许可
            "NeedSafeAckFwd",  // 安全确认（前进）
            "NeedSafeAckBkd",  // 安全确认（后退）
        };

        /// <summary>
        /// 从逻辑上下文中提取关键引脚绑定的 DB 变量名集合。
        /// 这些变量虽然不是 IO 标签，但语义上对约束推理至关重要（如模式互斥、安全联锁）。
        /// </summary>
        private static HashSet<string> CollectKeyPinDbVarNames(
            Dictionary<string, string>? callBindingSummary,
            Dictionary<string, List<string>>? callPinSignalHints,
            HashSet<string> projectTagNames)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 从 CallBindingSummary 收集：pin名 → 实际信号名
            if (callBindingSummary != null)
            {
                foreach (var (pinName, signalName) in callBindingSummary)
                {
                    if (KeyFbPinNames.Contains(pinName)
                        && !string.IsNullOrWhiteSpace(signalName)
                        && !projectTagNames.Contains(signalName))
                    {
                        result.Add(signalName);
                    }
                }
            }

            // 从 CallPinSignalHints 收集：pin名 → [信号名列表]
            if (callPinSignalHints != null)
            {
                foreach (var (pinName, signals) in callPinSignalHints)
                {
                    if (!KeyFbPinNames.Contains(pinName)) continue;
                    foreach (var s in signals)
                    {
                        if (!string.IsNullOrWhiteSpace(s) && !projectTagNames.Contains(s))
                            result.Add(s);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 判断信号名是否在 IO 标签集或关键引脚 DB 变量集中（即允许参与约束的信号）
        /// </summary>
        private static bool IsAllowedSignal(string signalName, HashSet<string> projectTagNames, HashSet<string>? keyPinDbVars)
        {
            if (string.IsNullOrWhiteSpace(signalName)) return false;
            return projectTagNames.Contains(signalName) || (keyPinDbVars != null && keyPinDbVars.Contains(signalName));
        }

        /// <summary>
        /// 从多信号的原始上下文结果中，扫描所有 CallBindingSummary 和 CallPinSignalHints，
        /// 收集绑定在关键 FB 引脚上的 DB 变量名集合。
        /// </summary>
        private static HashSet<string> CollectKeyPinDbVarNamesFromContexts(
            Dictionary<string, object> rawContextResults,
            HashSet<string> projectTagNames)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawContext in rawContextResults.Values)
            {
                try
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(rawContext));
                    if (!doc.RootElement.TryGetProperty("LogicContexts", out var contexts)
                        || contexts.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var context in contexts.EnumerateArray())
                    {
                        var callBinding = TryGetDictionaryOfString(context, "CallBindingSummary");
                        var callPinHints = TryGetDictionaryOfStringArray(context, "CallPinSignalHints");
                        var collected = CollectKeyPinDbVarNames(callBinding, callPinHints, projectTagNames);
                        foreach (var name in collected)
                            result.Add(name);
                    }
                }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// 构建扩展的信号白名单 = IO 标签 ∪ 关键引脚 DB 变量。
        /// 用于替代纯 IO 标签的过滤，使安全联锁、模式互斥、许可条件等 DB 变量能参与约束生成。
        /// </summary>
        private static HashSet<string> BuildExtendedTagNames(
            HashSet<string> projectTagNames,
            HashSet<string> keyPinDbVarNames)
        {
            if (keyPinDbVarNames.Count == 0) return projectTagNames;
            var extended = new HashSet<string>(projectTagNames, StringComparer.OrdinalIgnoreCase);
            foreach (var name in keyPinDbVarNames)
                extended.Add(name);
            return extended;
        }

        public IOConstraintPreprocessService(
            ILogger<IOConstraintPreprocessService> logger,
            IServiceProvider serviceProvider,
            IPLC_IOTagService ioTagService,
            PlcDiagnosisService diagnosisService,
            LLMModelService llmModelService,
            IOConstraintProgressService progressService,
            DiagnosticMemoryService memoryService,
            IPrompt_TemplateService promptTemplateService)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _ioTagService = ioTagService;
            _diagnosisService = diagnosisService;
            _llmModelService = llmModelService;
            _progressService = progressService;
            _memoryService = memoryService;
            _promptTemplateService = promptTemplateService;
        }

        private sealed class SignalLogicContextCacheItem
        {
            public object RawResult { get; init; }
            public string PromptJson { get; init; }
            public bool HasUsableLogicContext { get; init; }
        }

        private sealed class ConstraintBatchContext
        {
            public required List<PLC_IOTagEntity> AllBoolTags { get; init; }
            public required List<object> CompactTagList { get; init; }
            public required HashSet<string> ProjectTagNames { get; init; }
            public required Dictionary<string, PLC_IOTagEntity> TagByName { get; init; }
            public required PlcDiagnosisService.SignalLogicContextProjectCache LogicContextProjectCache { get; init; }
            /// <summary>中间变量 → 物理IO标签名的反查表（从 iomapping 字段解析构建）</summary>
            public required Dictionary<string, string> SourceVarToPhysicalTagDict { get; init; }
            public ConcurrentDictionary<string, Task<SignalLogicContextCacheItem>> LogicContextCache { get; } =
                new(StringComparer.OrdinalIgnoreCase);
            public ConcurrentDictionary<long, PendingTagStatus> PendingTagStatuses { get; } = new();
            public int PendingStatusCount;
        }

        private sealed class PendingTagStatus
        {
            public int Status { get; init; }
            public DateTime? AnalyzedAt { get; init; }
        }

        private sealed class CallPinConstraintEvidence
        {
            public string SignalName { get; init; } = string.Empty;
            public string CallName { get; init; } = string.Empty;
            public string PinName { get; init; } = string.Empty;
            public string PinSection { get; init; } = string.Empty;
            public string? BlockName { get; init; }
        }

        private sealed class UnresolvedIntermediateContextEvidence
        {
            public string RootSignalName { get; init; } = string.Empty;
            public HashSet<string> RelatedSignals { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> RelatedSignalFamilies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public bool MissingLogicBody { get; init; }
        }

        #region 触发入口

        /// <summary>
        /// 触发 IO 约束预处理任务（防重复）
        /// 在 XML 上传完成后调用此方法
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="concurrency">并发数（默认1，即串行）</param>
        /// <returns>是否成功触发（false = 已有运行中的任务）</returns>
        public static bool EnqueueIfNotRunning(long projectId, int concurrency = DEFAULT_CONCURRENCY)
        {
            // 显式入队到本机专用队列 local_<本机机器名>，避免多机共享 Hangfire 数据库时
            // 任务被其它机器（dll 版本可能不一致）的 worker 抢去执行。
            // 每个 HSJM.AI 进程启动时 worker 会注册 local_<本机> 队列（见 HSJM.AI/HangfireQueueNames），
            // 因此入到这个队列的任务只能被启动它的进程拿到。
            //
            // 注意：ExecuteAsync 上的 [Queue("default")] 属性在此被 EnqueuedState 显式参数 override，
            // 因此不需要删属性也能生效。其它调用入口（如全自动重试触发的任务）若没经此方法，
            // 仍会落到 default 队列由多机共抢——后续若有这类入口需要再单独处理。
            var queue = BuildCurrentMachineQueue();
            var client = new BackgroundJobClient();
            client.Create<IOConstraintPreprocessService>(
                svc => svc.ExecuteAsync(projectId, concurrency),
                new EnqueuedState(queue));
            return true;
        }

        /// <summary>
        /// 调试用：单 LU 入队（绕过 ExecuteAsync 全项目循环，只跑指定 LU 一条）。
        /// 同样落到本机队列保持版本一致性。
        /// </summary>
        public static bool EnqueueSingleLu(long luId)
        {
            var queue = BuildCurrentMachineQueue();
            var client = new BackgroundJobClient();
            client.Create<IOConstraintPreprocessService>(
                svc => svc.ExecuteSingleLuAsync(luId),
                new EnqueuedState(queue));
            return true;
        }

        /// <summary>
        /// 调试用：单 Block 入队（绕过 ExecuteAsync 全项目循环，只跑指定 Block 下的全部 LU）。
        /// 同样落到本机队列保持版本一致性。
        /// </summary>
        public static bool EnqueueSingleBlock(long blockId)
        {
            var queue = BuildCurrentMachineQueue();
            var client = new BackgroundJobClient();
            client.Create<IOConstraintPreprocessService>(
                svc => svc.ExecuteSingleBlockAsync(blockId),
                new EnqueuedState(queue));
            return true;
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

        /// <summary>
        /// 执行 IO 约束预处理（Hangfire 后台调用）
        /// 
        /// 流程：
        /// 1. 获取项目锁（防重复）
        /// 2. 查询未处理的 Bool IO Tag
        /// 3. 逐信号获取逻辑上下文 → AI 推理 → 保存约束
        /// 4. 更新 IOTag 状态
        /// </summary>
        // 注意：故意不加 [Queue(...)] 属性 — Hangfire 的 [Queue] JobFilter 会 override EnqueueIfNotRunning
        // 调用时传入的 EnqueuedState 的 queue，导致本机队列锁定失效。
        // 入队时由 EnqueueIfNotRunning 用 EnqueuedState 显式指定到 local_<本机> 队列。
        /// <summary>
        /// 调试用：只跑指定 LU 一条的约束生成（绕过 ExecuteAsync 的全项目循环）。
        /// 用于 /PLC_Upload/RerunSingleLu/{luId} 路径，便于调改 Step1 渲染 / Phase2 prompt / 入库过滤后快速看单 LU 效果。
        /// 流程：
        ///   1. 查 LU + Block + 项目锁
        ///   2. 项目级加载 ioTagDict（约束入库要用）
        ///   3. ExtractLuSignals 出本 LU 信号清单
        ///   4. 调 ProcessSingleLogicUnitAsync 走完整 Phase2 流程
        /// 不跑 Step1：PLD 由调用方（RerunSingleLuAsync）已置 NULL，但 ExecuteAsync 全跑量太大；
        /// 单 LU 模式由本方法负责显式调一次 ConvertAndSaveCompactExpressionAsync 前的单 LU 渲染补丁。
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        [Hangfire.JobDisplayName("约束生成·单LU重跑 · LU{0}")]
        public async Task ExecuteSingleLuAsync(long luId)
        {
            _logger.LogInformation("[IOConstraint] 单 LU 模式开始: LuId={LuId}", luId);
            try
            {
                using var luScope = _serviceProvider.CreateScope();
                var luService = luScope.ServiceProvider.GetRequiredService<IPLC_LogicUnitService>();
                var lus = await luService.GetListAsync(x => x.Id == luId && !x.IsDeleted);
                var lu = lus.FirstOrDefault();
                if (lu == null || !lu.BlockId.HasValue)
                {
                    _logger.LogWarning("[IOConstraint] 单 LU 模式：LU#{LuId} 不存在或缺 BlockId", luId);
                    return;
                }

                using var blockScope = _serviceProvider.CreateScope();
                var blockService = blockScope.ServiceProvider.GetRequiredService<IPLC_BlockService>();
                var blocks = await blockService.GetListAsync(b => b.Id == lu.BlockId.Value && !b.IsDeleted);
                var block = blocks.FirstOrDefault();
                if (block == null || !block.ProjectId.HasValue)
                {
                    _logger.LogWarning("[IOConstraint] 单 LU 模式：LU#{LuId} 关联 Block / Project 缺失", luId);
                    return;
                }

                var pid = block.ProjectId.Value;

                // 如果 PLD 为空（典型走 RerunSingleLuAsync 后）补一次单 LU 渲染（不触发全项目 Step1）
                if (string.IsNullOrWhiteSpace(lu.ParsedLogicDescription))
                {
                    using var stepScope = _serviceProvider.CreateScope();
                    var xmlExtract = stepScope.ServiceProvider.GetRequiredService<XmlConstraintExtractService>();
                    var singleRes = await xmlExtract.ConvertSingleLuAsync(luId);
                    _logger.LogInformation("[IOConstraint] 单 LU 模式 Step1 完成: {Result}", JsonHelper.SerializeObject(singleRes));
                    // 重读取 PLD
                    var refreshLus = await luService.GetListAsync(x => x.Id == luId && !x.IsDeleted);
                    lu = refreshLus.FirstOrDefault() ?? lu;
                }

                if (string.IsNullOrWhiteSpace(lu.ParsedLogicDescription))
                {
                    _logger.LogWarning("[IOConstraint] 单 LU 模式：LU#{LuId} 渲染后 PLD 仍为空，跳过 Phase2", luId);
                    return;
                }

                // 项目级 IO Tag 字典
                var projectIoTags = await _ioTagService.GetListAsync(x => x.ProjectId == pid && !x.IsDeleted);
                var ioTagDict = projectIoTags
                    .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                    .GroupBy(t => t.TagName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var (sigList, extras) = ExtractLuSignals(lu, ioTagDict);
                if (sigList.Count + extras.Count < 2)
                {
                    _logger.LogInformation("[IOConstraint] 单 LU 模式：LU#{LuId} 信号数 < 2 (sig={Sig} extra={Extra})，跳过", luId, sigList.Count, extras.Count);
                    return;
                }

                var chatClient = await GetChatClientAsync();
                if (chatClient == null)
                {
                    _logger.LogWarning("[IOConstraint] 单 LU 模式：无可用 LLM，跳过");
                    return;
                }

                var inserted = await ProcessSingleLogicUnitAsync(pid, lu, sigList, extras, ioTagDict, chatClient);
                _logger.LogInformation("[IOConstraint] 单 LU 模式完成: LuId={LuId}, Inserted={Inserted}", luId, inserted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 单 LU 模式异常: LuId={LuId}", luId);
            }
        }

        /// <summary>
        /// 调试用：只跑指定 Block 下全部 LU 的约束生成（绕过 ExecuteAsync 的全项目循环）。
        /// 用于 /PLC_Upload/RerunBlock/{blockId} 路径，便于按程序块粒度单独重跑该块约束。
        /// 流程：
        ///   1. 查 Block + 项目
        ///   2. 查块下全部未删 LU；PLD 为空的逐个补单 LU 渲染（ConvertSingleLuAsync）
        ///   3. 项目级加载 ioTagDict + ChatClient（整块共享一次，不像 N 个单 LU 任务那样重复加载）
        ///   4. 逐 LU 调 ProcessSingleLogicUnitAsync 走完整 Phase2 流程
        /// 不跑全项目 Step1：调用方（RerunBlockAsync）已把这些 LU 的 PLD 置 NULL，这里只补这一块的渲染。
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        [Hangfire.JobDisplayName("约束生成·单Block重跑 · Block{0}")]
        public async Task ExecuteSingleBlockAsync(long blockId)
        {
            _logger.LogInformation("[IOConstraint] 单 Block 模式开始: BlockId={BlockId}", blockId);
            try
            {
                // 1. 查 Block + 项目
                using var blockScope = _serviceProvider.CreateScope();
                var blockService = blockScope.ServiceProvider.GetRequiredService<IPLC_BlockService>();
                var blocks = await blockService.GetListAsync(b => b.Id == blockId && !b.IsDeleted);
                var block = blocks.FirstOrDefault();
                if (block == null || !block.ProjectId.HasValue)
                {
                    _logger.LogWarning("[IOConstraint] 单 Block 模式：Block#{BlockId} 不存在或缺 ProjectId", blockId);
                    return;
                }
                var pid = block.ProjectId.Value;

                // 2. 查块下全部未删 LU
                using var luScope = _serviceProvider.CreateScope();
                var luService = luScope.ServiceProvider.GetRequiredService<IPLC_LogicUnitService>();
                var lus = await luService.GetListAsync(x => x.BlockId == blockId && !x.IsDeleted);
                if (lus.Count == 0)
                {
                    _logger.LogInformation("[IOConstraint] 单 Block 模式：Block#{BlockId} 下无 LU，结束", blockId);
                    return;
                }

                // 2.1 PLD 为空的 LU 补单 LU 渲染（典型走 RerunBlockAsync 把 PLD 置 NULL 之后）
                var needRender = lus.Where(l => string.IsNullOrWhiteSpace(l.ParsedLogicDescription)).ToList();
                if (needRender.Count > 0)
                {
                    using var stepScope = _serviceProvider.CreateScope();
                    var xmlExtract = stepScope.ServiceProvider.GetRequiredService<XmlConstraintExtractService>();
                    foreach (var luToRender in needRender)
                    {
                        try
                        {
                            await xmlExtract.ConvertSingleLuAsync(luToRender.Id);
                        }
                        catch (Exception renderEx)
                        {
                            _logger.LogWarning(renderEx, "[IOConstraint] 单 Block 模式：LU#{LuId} 渲染异常（跳过该 LU）", luToRender.Id);
                        }
                    }
                    // 重读 LU 拿到补好的 PLD
                    lus = await luService.GetListAsync(x => x.BlockId == blockId && !x.IsDeleted);
                }

                // 3. 项目级 IO Tag 字典 + ChatClient（整块共享一次）
                var projectIoTags = await _ioTagService.GetListAsync(x => x.ProjectId == pid && !x.IsDeleted);
                var ioTagDict = projectIoTags
                    .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                    .GroupBy(t => t.TagName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var chatClient = await GetChatClientAsync();
                if (chatClient == null)
                {
                    _logger.LogWarning("[IOConstraint] 单 Block 模式：无可用 LLM，跳过 Block#{BlockId}", blockId);
                    return;
                }

                // 4. 逐 LU 走完整 Phase2
                int totalInserted = 0;
                int processedLu = 0;
                foreach (var lu in lus)
                {
                    if (string.IsNullOrWhiteSpace(lu.ParsedLogicDescription))
                        continue;

                    var (sigList, extras) = ExtractLuSignals(lu, ioTagDict);
                    if (sigList.Count + extras.Count < 2)
                        continue;

                    try
                    {
                        var inserted = await ProcessSingleLogicUnitAsync(pid, lu, sigList, extras, ioTagDict, chatClient);
                        totalInserted += inserted;
                        processedLu++;
                    }
                    catch (Exception luEx)
                    {
                        _logger.LogError(luEx, "[IOConstraint] 单 Block 模式：LU#{LuId} Phase2 异常（跳过该 LU）", lu.Id);
                    }
                }

                _logger.LogInformation("[IOConstraint] 单 Block 模式完成: BlockId={BlockId}, LU 总数={LuCount}, 已处理={Processed}, 入库约束={Inserted}",
                    blockId, lus.Count, processedLu, totalInserted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 单 Block 模式异常: BlockId={BlockId}", blockId);
            }
        }

        [AutomaticRetry(Attempts = 0)]
        [Hangfire.JobDisplayName("约束生成 · 项目{0}")]
        public async Task ExecuteAsync(long projectId, int concurrency = DEFAULT_CONCURRENCY)
        {
            concurrency = 1;

            // 1. 防重复：获取项目锁
            if (!_progressService.TryAcquireLock(projectId))
            {
                _logger.LogWarning("[IOConstraint] 项目 {ProjectId} 已有正在运行的约束分析任务，跳过", projectId);
                return;
            }

            _logger.LogInformation("[IOConstraint] 开始约束预处理: ProjectId={ProjectId}, Concurrency={Concurrency}",
                projectId, concurrency);

            try
            {
                // 0. Step1 预解析已移入下面的 foreach pid 循环内（按每个项目各跑一次）。
                //    原实现把 Step1 放在循环外、用传入的 projectId 跑：projectId<=0（全项目模式）时
                //    会按 ProjectId==0 查 block 查不到 → Step1 空转 → 该项目 PLD 全空 → Phase2 无约束可产。
                //    修复：Step1 改用循环内的 pid，确保全项目模式下每个项目都被正确预解析。

                // 1. 确定本次要处理的项目列表：projectId>0 仅该项目；projectId<=0 → 全项目（未删）。
                var projectIdsToProcess = new List<long>();
                if (projectId > 0)
                {
                    projectIdsToProcess.Add(projectId);
                }
                else
                {
                    using var projScope = _serviceProvider.CreateScope();
                    var projectService = projScope.ServiceProvider.GetRequiredService<IPLC_ProjectService>();
                    var allProjects = await projectService.GetListAsync(p => !p.IsDeleted);
                    projectIdsToProcess.AddRange(allProjects.Select(p => p.Id));
                    _logger.LogInformation("[IOConstraint] projectId<=0，全局处理 {Count} 个未删项目", projectIdsToProcess.Count);
                }

                if (projectIdsToProcess.Count == 0)
                {
                    _logger.LogInformation("[IOConstraint] 无可处理的项目，退出");
                    _progressService.MarkCompleted(projectId);
                    return;
                }

                // 2. 获取 LLM ChatClient（共享所有项目）
                var chatClient = await GetChatClientAsync();
                if (chatClient == null)
                {
                    _progressService.MarkFailed(projectId, "无可用的 LLM 模型");
                    return;
                }

                int totalLus = 0;
                int processedLus = 0;
                int totalConstraintsInserted = 0;

                foreach (var pid in projectIdsToProcess)
                {
                    _logger.LogInformation("[IOConstraint] 开始处理项目 {Pid}（按 LU 驱动）", pid);

                    // 2.0 Step1 预解析（按项目 pid）：把本项目 LU 的 RawContent XML 转换为 ParsedLogicDescription。
                    // 幂等：服务内部只跑 ParsedLogicDescription 为空的 LU，已解析过的不重复消耗。
                    // 进度推送仍用入队时的 projectId 作 key（全项目模式=0），前端才查得到；消息里带 pid 标明当前项目。
                    try
                    {
                        _progressService.SetMessage(projectId, $"Step1 预解析项目 {pid}：将 XML 转为结构化中文描述...");
                        using var step1Scope = _serviceProvider.CreateScope();
                        var xmlExtractService = step1Scope.ServiceProvider.GetRequiredService<XmlConstraintExtractService>();
                        var step1Result = await xmlExtractService.ConvertAndSaveCompactExpressionAsync(pid);
                        _logger.LogInformation("[IOConstraint] 项目 {Pid} Step1 预解析完成: {Result}", pid, JsonHelper.SerializeObject(step1Result));
                    }
                    catch (Exception step1Ex)
                    {
                        _logger.LogWarning(step1Ex, "[IOConstraint] 项目 {Pid} Step1 预解析异常（不阻塞约束生成）", pid);
                    }

                    // 2a. 加载项目 IO Tag 字典
                    var projectIoTags = await _ioTagService.GetListAsync(x =>
                        x.ProjectId == pid && !x.IsDeleted);
                    var ioTagDict = projectIoTags
                        .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                        .GroupBy(t => t.TagName!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                    if (ioTagDict.Count == 0)
                    {
                        _logger.LogInformation("[IOConstraint] 项目 {Pid} 无 IO Tag，跳过", pid);
                        continue;
                    }

                    // 2b. 加载 block 列表
                    using var blockScope = _serviceProvider.CreateScope();
                    var blockService = blockScope.ServiceProvider.GetRequiredService<IPLC_BlockService>();
                    var blocks = await blockService.GetListAsync(b => b.ProjectId == pid && !b.IsDeleted);
                    var blockIds = blocks.Select(b => b.Id).ToHashSet();
                    if (blockIds.Count == 0)
                    {
                        _logger.LogInformation("[IOConstraint] 项目 {Pid} 无 block，跳过", pid);
                        continue;
                    }

                    // 2c. 加载 LU 列表（仅未删 + 含 ParsedLogicDescription 且非空/非失败标记）
                    using var luScope = _serviceProvider.CreateScope();
                    var luService = luScope.ServiceProvider.GetRequiredService<IPLC_LogicUnitService>();
                    var lus = await luService.GetListAsync(x =>
                        x.BlockId.HasValue
                        && blockIds.Contains(x.BlockId.Value)
                        && !x.IsDeleted
                        && x.ParsedLogicDescription != null
                        && x.ParsedLogicDescription != ""
                        && !x.ParsedLogicDescription.StartsWith("["));

                    if (lus.Count == 0)
                    {
                        _logger.LogInformation("[IOConstraint] 项目 {Pid} 无可用 ParsedLogicDescription 的 LU（请先跑 Step1），跳过", pid);
                        continue;
                    }

                    // 2d. 预筛选：每个 LU 解析 UidSymbolMapJson 找出涉及的 IO Tag 信号（命中 ioTagDict 或末段命中 KeyFbPinNames）
                    // 仅保留至少有 2 个允许信号的 LU。
                    var luSignals = new Dictionary<long, List<PLC_IOTagEntity>>();
                    var luAllowedExtras = new Dictionary<long, List<string>>(); // 末段命中 KeyFbPin 的 DB 变量名
                    foreach (var lu in lus)
                    {
                        var (sigList, extras) = ExtractLuSignals(lu, ioTagDict);
                        if (sigList.Count + extras.Count >= 2)
                        {
                            luSignals[lu.Id] = sigList;
                            luAllowedExtras[lu.Id] = extras;
                        }
                    }

                    var luToProcess = lus.Where(lu => luSignals.ContainsKey(lu.Id)).ToList();

                    // 方案 D（增量约束）：剔除已存在约束的 LU。
                    // - 全量场景（ResetRule 后）：existingRuleLuIds 为空，全部 LU 跑（保持原行为）
                    // - 增量场景（上传按 SourceLuId 软删变化 block 的约束后）：未变化 LU 自动跳过，只重跑变化的
                    using (var existingScope = _serviceProvider.CreateScope())
                    {
                        var ruleService = existingScope.ServiceProvider.GetRequiredService<IPLC_IOConstraintRuleService>();
                        var existingRules = await ruleService.GetListAsync(r =>
                            r.ProjectId == pid && r.SourceLuId.HasValue && !r.IsDeleted);
                        var existingRuleLuIds = existingRules
                            .Where(r => r.SourceLuId.HasValue)
                            .Select(r => r.SourceLuId!.Value)
                            .Distinct()
                            .ToHashSet();

                        if (existingRuleLuIds.Count > 0)
                        {
                            var beforeCount = luToProcess.Count;
                            luToProcess = luToProcess.Where(lu => !existingRuleLuIds.Contains(lu.Id)).ToList();
                            var skipped = beforeCount - luToProcess.Count;
                            if (skipped > 0)
                            {
                                _logger.LogInformation(
                                    "[IOConstraint] 项目 {Pid} 增量模式：跳过 {Skipped} 个已有约束的 LU，剩余 {Remain} 待重生",
                                    pid, skipped, luToProcess.Count);
                            }
                        }
                    }

                    totalLus += luToProcess.Count;
                    _logger.LogInformation("[IOConstraint] 项目 {Pid}：LU 共 {LuTotal}，含 IO Tag 信号且 >=2 → 待处理 {Filtered}",
                        pid, lus.Count, luToProcess.Count);

                    _progressService.SetTotalSignals(pid, luToProcess.Count); // 复用现有进度推送字段，把"LU 数"当"待处理数"

                    int luIndex = 0;
                    foreach (var lu in luToProcess)
                    {
                        luIndex++;
                        _progressService.UpdateCurrentSignal(pid, $"LU#{lu.Id} (Network {lu.NetworkId})", luIndex);
                        _progressService.SetMessage(pid, $"按 LU 推约束 {luIndex}/{luToProcess.Count}: LU#{lu.Id}");

                        try
                        {
                            var inserted = await ProcessSingleLogicUnitAsync(pid, lu, luSignals[lu.Id], luAllowedExtras[lu.Id], ioTagDict, chatClient);
                            totalConstraintsInserted += inserted;
                            processedLus++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[IOConstraint] LU#{LuId} 推理约束异常", lu.Id);
                        }
                    }

                    // 2d-2. 合并同组 2 元 Mutex → N 元 Mutex（项目级别后处理）。
                    // LLM 把 N 个互斥信号拆成 C(N,2) 条 2 元 Mutex 是 prompt 引导的常见误差，
                    // 这里在所有 LU 跑完后用图算法识别"完全互斥子图"并合并，避免规则数虚高 + 冗余。
                    try
                    {
                        using var mergeScope = _serviceProvider.CreateScope();
                        var mergeDiagSvc = mergeScope.ServiceProvider.GetRequiredService<PlcDiagnosisService>();
                        await mergeDiagSvc.MergePairMutexIntoNAryAsync(pid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[IOConstraint] 项目 {Pid} 合并同组 Mutex 失败", pid);
                    }

                    // 2d-3. 清理跨 LU 时序导致漏过的物理矛盾 IMPLIES（Target 间含互斥对）。
                    // SaveIOConstraintsAsync.2a-3b 只看本批 inputs 的 Mutex；
                    // 跨 LU 入库时若 IMPLIES LU 比 Mutex LU 先跑，本批没看到 Mutex 漏拦。
                    // 等所有 LU 都跑完后在此扫一次项目级数据补拦。
                    try
                    {
                        using var contraScope = _serviceProvider.CreateScope();
                        var contraDiagSvc = contraScope.ServiceProvider.GetRequiredService<PlcDiagnosisService>();
                        await contraDiagSvc.RemoveContradictoryPairingAsync(pid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[IOConstraint] 项目 {Pid} 清理矛盾 IMPLIES 失败", pid);
                    }

                    // 2d-4. 清理 LLM 按命名启发式产的「同步驱动对」Mutex（项目级聚合 PLD 证据）。
                    // LU 级 FilterOutSyncDrivenMutex 只看本 LU 自己的 PLD；LLM 可能在不含同步证据
                    // 的 LU 也产同样的错 Mutex(A,B)。项目级聚合所有 LU 的同步对证据后扫所有 Mutex 软删。
                    try
                    {
                        using var syncScope = _serviceProvider.CreateScope();
                        var syncDiagSvc = syncScope.ServiceProvider.GetRequiredService<PlcDiagnosisService>();
                        await syncDiagSvc.RemoveSyncDrivenMutexAsync(pid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[IOConstraint] 项目 {Pid} 清理同步驱动对 Mutex 失败", pid);
                    }

                    // 2e. 项目完成后：批量算 IOTag.ConstraintStatus（出现在约束表 = 2，否则 = 0）。
                    try
                    {
                        await UpdateIoTagConstraintStatusAsync(pid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[IOConstraint] 项目 {Pid} 更新 IOTag ConstraintStatus 失败", pid);
                    }
                }

                _progressService.MarkCompleted(projectId);
                _logger.LogInformation("[IOConstraint] 全部完成: 项目={Projects}, LU总数={TotalLus}, 已处理={Processed}, 新增约束={Inserted}",
                    projectIdsToProcess.Count, totalLus, processedLus, totalConstraintsInserted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 约束预处理失败: ProjectId={ProjectId}", projectId);
                _progressService.MarkFailed(projectId, ex.Message);
            }
        }

        #endregion

        #region 信号处理

        /// <summary>
        /// 从 LU 中解析涉及的 IO Tag 信号清单。
        /// 两个数据来源：
        ///   1) UidSymbolMapJson —— 主源。Python 解析器已把每个 UId 映射到**完整路径**信号名
        ///      （如 DB210_OP020_User.Cmd.ByPass_CYL[1]），FlgNet 块覆盖完整。
        ///   2) RawContent 兜底 —— 仅对 SCL/StructuredText 类型的 LU 启用。
        ///      Python 解析器对 SCL 块只收前面少量 Access，导致后面大量独立信号没进 UidMap
        ///      （如 Mapping 块里出现的 OP02011-SQ03 等 IO 标签）。
        ///      只匹配"独立信号"（&lt;Symbol&gt; 内仅 1 个自闭合 &lt;Component Name="X" /&gt;，
        ///      没有 Token/子 Component），避免错误把复合路径拆出来的片段命中 ioTagDict。
        /// 仅 ioTagDict 命中的信号才进 signals；末段命中 KeyFbPinNames 的全局 DB 变量进 extras。
        /// </summary>
        private static (List<PLC_IOTagEntity> Signals, List<string> Extras) ExtractLuSignals(
            PLC_LogicUnitEntity lu,
            Dictionary<string, PLC_IOTagEntity> ioTagDict)
        {
            var signals = new List<PLC_IOTagEntity>();
            var extras = new List<string>();
            var seenSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenExtras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Consider(string? rawName)
            {
                var name = rawName?.Trim();
                if (string.IsNullOrWhiteSpace(name)) return;

                if (ioTagDict.TryGetValue(name, out var ioTag))
                {
                    if (seenSignals.Add(name)) signals.Add(ioTag);
                    return;
                }
                // 末段命中 KeyFbPinNames → extras
                // 允许 Valve[N].xxx 这种 FB 实例.引脚 形式（PLC 程序里真实存在的连接关系）
                var lastDot = name.LastIndexOf('.');
                var leaf = lastDot >= 0 ? name.Substring(lastDot + 1) : name;
                if (!string.IsNullOrWhiteSpace(leaf) && PlcDiagnosisService_KeyFbPinNamesCopy.Contains(leaf))
                {
                    if (seenExtras.Add(name)) extras.Add(name);
                }
            }

            // 1) 主源：UidSymbolMapJson（完整路径，FlgNet 块完整覆盖）
            if (!string.IsNullOrWhiteSpace(lu.UidSymbolMapJson))
            {
                Dictionary<string, string>? uidMap = null;
                try { uidMap = JsonSerializer.Deserialize<Dictionary<string, string>>(lu.UidSymbolMapJson); }
                catch { uidMap = null; }
                if (uidMap != null)
                {
                    foreach (var kv in uidMap) Consider(kv.Value);
                }
            }

            // 2) 兜底：仅对 SCL/StructuredText 类型的 LU，额外扫 RawContent 找"独立信号"
            //    独立信号 = <Symbol> 内仅 1 个自闭合 <Component Name="X" />（含 UId 等属性也可）
            //    复合路径如 DB210_OP020_User.Cmd.ByPass_CYL[1] 在 raw 里是多个 <Component> + <Token> 拼接，
            //    这个正则不会匹配（含 / 自闭合是关键），不会把路径片段拆出来。
            var lang = (lu.ProgrammingLanguage ?? "").Trim();
            bool needsRawScan = string.Equals(lang, "SCL", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(lang, "StructuredText", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(lang, "ST", StringComparison.OrdinalIgnoreCase);
            if (needsRawScan && !string.IsNullOrWhiteSpace(lu.RawContent))
            {
                foreach (Match m in _independentSignalRegex.Matches(lu.RawContent))
                {
                    Consider(m.Groups[1].Value);
                }
            }

            return (signals, extras);
        }

        /// <summary>
        /// 正则：匹配 XML 中的"独立信号"模式 —— &lt;Symbol&gt; 内仅 1 个自闭合 &lt;Component Name="X" .../&gt;。
        /// 关键点：必须是自闭合（以 "/&gt;" 结尾），且 Symbol 紧跟 Component 没有其它内容。
        /// 这样可以避开 DB245_OP20Cylinder.Control.OP020_CYL01_FwdAuto 这种复合路径
        /// （raw 里是 &lt;Component Name="DB245_OP20Cylinder" /&gt; &lt;Token .../&gt; &lt;Component .../&gt; 多个并列）。
        ///
        /// 兼容点：
        ///   - &lt;Symbol&gt; 与 &lt;Component&gt; 之间可有空白
        ///   - &lt;Symbol UId=".."&gt; 等带属性形式
        ///   - &lt;Component Name="X" UId=".." /&gt; 自闭合（无内嵌子节点）
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex _independentSignalRegex =
            new System.Text.RegularExpressions.Regex(
                @"<Symbol\b[^>]*>\s*<Component\s+Name=""([^""]+)""[^/>]*/>\s*</Symbol>",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// 本地副本：KeyFbPinNames 白名单（与 PlcDiagnosisService 内同名集合一致）。
        /// 之所以单独放一份，是因为 PlcDiagnosisService.KeyFbPinNames 是 private，无法跨类引用。
        /// 若 PlcDiagnosisService 升级，此处需同步更新。
        /// </summary>
        private static readonly HashSet<string> PlcDiagnosisService_KeyFbPinNamesCopy = new(StringComparer.OrdinalIgnoreCase)
        {
            "E_StopStatus", "ManualMod", "AutoRunMod", "FwdPmt", "BkdPmt",
            "ResetStatus", "Model", "PmtDebug", "NeedSafeAckFwd", "NeedSafeAckBkd"
        };

        /// <summary>
        /// 对一个 LU 推约束：构建 prompt（ParsedLogicDescription + 信号清单），调 LLM，解析约束 JSON 入库。
        /// 返回新增约束条数（用于汇总日志）。
        /// </summary>
        private async Task<int> ProcessSingleLogicUnitAsync(
            long projectId,
            PLC_LogicUnitEntity lu,
            List<PLC_IOTagEntity> luSignals,
            List<string> extras,
            Dictionary<string, PLC_IOTagEntity> ioTagDict,
            IChatClient chatClient)
        {
            // 跳过纯逻辑 LU：PLD 显示"无 Coil/Call 输出"的 LU 不该参与约束生成
            // 这种 LU 是中间状态计算（如 FC190_OP10Alarm Net4 的报警汇总），即使含 Q/I 信号
            // 也只是被读取参考，没有控制语义。LLM 看到这种 LU 容易把无关信号错配成 Mutex/Pairing。
            var pld = lu.ParsedLogicDescription ?? string.Empty;
            if (pld.Contains("本网络无 Coil/Call 输出", StringComparison.Ordinal))
            {
                _logger.LogDebug("[IOConstraint] LU#{LuId} 是纯逻辑/中间状态 LU（无 Coil/Call 输出），跳过约束生成", lu.Id);
                return 0;
            }

            // 跳过 Sensor Mapping LU：Step1 渲染时识别出的「IO Tag → 同一 DB 数组汇总」LU
            // 这种 LU 没有控制语义（如 FC610_OP60Mapping 把 28 个 FZ 信号汇总到 DB.DI.Sensor），
            // LLM 看到一堆同模式信号容易误推 Mutex/AllRequired。
            if (pld.Contains("**Sensor Mapping LU**", StringComparison.Ordinal))
            {
                _logger.LogDebug("[IOConstraint] LU#{LuId} 是 Sensor Mapping LU（IO Tag 汇总中转），跳过约束生成", lu.Id);
                return 0;
            }

            var prompt = BuildLogicUnitConstraintPrompt(lu, luSignals, extras);

            var response = await CallAIWithRetryAsync(
                chatClient,
                await GetLogicUnitConstraintSystemPromptAsync(),
                prompt,
                $"LU#{lu.Id}",
                "LuPhase2",
                memoryContext: null,
                timeoutSeconds: PHASE2_AI_TIMEOUT_SECONDS,
                maxRetries: PHASE2_MAX_RETRIES);

            var constraintsJson = string.IsNullOrWhiteSpace(response) ? null : ExtractJsonArray(response);
            // 注意：即使 LLM 没产出约束，Augmenter 仍可能从 PLD 结构产出零歧义约束，所以这里不直接 return。
            constraintsJson ??= "[]";

            // 改造（§4.2 铁律强化）：在调 Augmenter 之前先剔除 LLM 用命名启发式产的错 Mutex。
            // 一次性从 LogicUnit XML 抽出锚点结构，复用给后续 4 步（同步对识别 / Pairing 白名单 / Mutex 白名单 / 正向补全），避免重复解析 XML。
            // 提前到 FilterConstraintsByLuScope 之前：anchorBlocks.Anchor（Coil/SCoil/RCoil 驱动的 DB 内部信号）
            // 要参与 luScopeSignals 构建，否则会被 LU 外编造过滤误杀。
            var anchorBlocks = SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks(lu.RawContent);

            // LU 范围内信号清单：本 LU 的 luSignals + extras + Coil/SCoil/RCoil 驱动的目标信号 + KeyFbPin 系统级常驻信号
            // 用来过滤 LLM 跨 LU 编造的信号（典型：本 LU 没有 X 但 LLM 拉了 X 入约束）
            // Coil targets 加入：让 SCoil/RCoil 驱动的 DB 内部信号（如 DB***Alarm.Errors.Error*）作为合法约束信号通过 LU 范围校验
            var luScopeSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in luSignals)
                if (!string.IsNullOrWhiteSpace(s.TagName)) luScopeSignals.Add(s.TagName!);
            foreach (var e in extras) luScopeSignals.Add(e);
            foreach (var b in anchorBlocks)
                if (b.Type == PldAnchorType.Coil && !string.IsNullOrWhiteSpace(b.Anchor))
                    luScopeSignals.Add(b.Anchor.Trim());
            constraintsJson = FilterConstraintsByLuScope(constraintsJson, luScopeSignals, lu.Id);

            // 改造（§4.2 铁律强化）：PLD 真实引用校验
            // 现象：LLM Phase2 prompt 给的「LU 范围信号清单」包含本 LU UidSymbolMapJson 涉及的所有 IO Tag，
            // 但 PLD 实际只引用其中一部分（IWP/IHP 子树、Coil 触点等）。LLM 在更宽的"清单"范围内凭命名
            // 拼配（如 YV12_H → CYL12_1H），产生 PLD 完全没引用的信号对。
            // 强化：约束的每个 signalName 必须在 PLD 反引号字面量内真实出现，否则整条丢。
            // KeyFbPin 系统级常驻信号（General.E_StopStatus 等）即使 PLD 没直接引用也允许（通过 FB 引脚连接体现）。
            // Coil/SCoil/RCoil 的目标信号会在渲染时写入 PLD（`线圈输出: \`X\``），自然通过反引号引用校验。
            constraintsJson = FilterConstraintsByPldReference(
                constraintsJson, lu.ParsedLogicDescription, lu.Id);

            // 识别本 LU 「同步驱动对」（两个独立 Coil 锚点 + 子树触点完全相同），
            // 这种信号对（如 OP080_YV08_W 和 OP080_YV08_H 都被 Valve[8].QBkd 驱动）是同时通电的同步对，**不是反向 Mutex**。
            // LLM 因为信号名 W/H 配对而误产 Mutex(W, H) 的，在此整条丢。
            var syncDrivenPairs = PldStructuralConstraintAugmenter.ExtractSyncDrivenPairsFromBlocks(anchorBlocks);
            if (syncDrivenPairs.Count > 0)
            {
                constraintsJson = FilterOutSyncDrivenMutex(constraintsJson ?? "[]", syncDrivenPairs, lu.Id);
            }

            // 改造（§4.2 铁律强化）：Pairing 白名单——所有 LLM 产的 Pairing 必须命中本 LU 标准 FB QFwd→IWP / QBkd→IHP 结构。
            // 不在白名单的 Pairing（如 `YV_H → CYL_W` 反向、独立 Coil 信号拼对、Source/Target 跨 LU 引用等）整条丢。
            // 多缸同步用 ALL_TRUE 表达，不走 Pairing 路径，因此 Pairing 强制 1:1 QFwd→IWP 标准模式是稳的。
            var validPairings = PldStructuralConstraintAugmenter.ExtractValidPairingsFromBlocks(
                anchorBlocks, ioTagDict);
            // SCoil/RCoil 报警网络改为只产 REQUIRES_0 (Targets=触发 %I 传感器)，不产 sensor→alarm IMPLIES
            // —— DB 内部 Error* 不在 IO Tag 表，实时采集拿不到，IMPLIES Target 缺值无法诊断。
            // 因此 coilDrivenTargets 传空集；FB 标准 QFwd→IWP 路径仍走 validPairings 校验。
            constraintsJson = FilterPairingsByWhitelist(
                constraintsJson ?? "[]",
                validPairings,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lu.Id);

            // 改造（§4.2 铁律强化）：Mutex 白名单——LLM 产的 Mutex 必须满足以下之一才放行：
            // 1. 命中 Augmenter 识别的 FB 反向 Q 对 / 反向 I 对集合（结构证据）
            // 2. 所有信号末段都是 KeyFbPin（模式信号互斥，如 ManualMod ↔ AutoRunMod）
            // 否则整条丢（消除 LLM 凭命名启发式 W/H 配对产的错 Mutex）。
            var validMutexes = PldStructuralConstraintAugmenter.ExtractValidMutexPairsFromBlocks(
                anchorBlocks, ioTagDict);
            constraintsJson = FilterMutexesByWhitelist(constraintsJson ?? "[]", validMutexes, lu.Id);

            // SAME_STATE 白名单：LLM 产的 SAME_STATE 必须所有 Member 两两都在 syncDrivenPairs 集合
            // （= PLD 同 in 表达式同步驱动）才放行，否则整条丢。
            // 防止 LLM 凭命名相似（Error1/2/3/4 顺序号）误产 SAME_STATE。
            constraintsJson = FilterSameStateByWhitelist(constraintsJson ?? "[]", syncDrivenPairs, lu.Id);

            // 基于锚点结构生成「零歧义」约束，补足 LLM 漏识。
            // 仅识别博图 FB 标准模式（QFwd/QBkd 反向 Q 对、IWP/IHP 反向 I 对、独立 Coil 同步对），
            // 输出 JSON 与 LLM 同格式，与 LLM 输出 merge 后统一走 SaveIOConstraintsAsync 入库过滤。
            var structuralJson = PldStructuralConstraintAugmenter.AugmentFromBlocks(anchorBlocks, ioTagDict);
            if (!string.IsNullOrWhiteSpace(structuralJson) && structuralJson != "[]")
            {
                constraintsJson = MergeLlmAndStructuralJson(constraintsJson ?? "[]", structuralJson, lu.Id);
            }

            // 屏蔽态 SCoil 报警网络识别（§4.5）：每个 %I 传感器各产 1 条 MUST_BE_FALSE 单 Target 约束。
            // 不沿 Wire 拓扑追每个 SCoil 的触发条件（TON 等非布尔部件阻断不影响），
            // 改为「LU 整体含 SCoil + AlwaysTRUE/AlwaysFALSE/Null 屏蔽开关 → 所有 %I Access 都进 Targets」。
            // LLM 已在 prompt 里被告知不要为屏蔽态 SCoil 产任何约束，由本方法包圆。
            var maskedAlarmJson = PldStructuralConstraintAugmenter.AugmentMaskedAlarmFromBlocks(anchorBlocks, ioTagDict);
            if (!string.IsNullOrWhiteSpace(maskedAlarmJson) && maskedAlarmJson != "[]")
            {
                constraintsJson = MergeLlmAndStructuralJson(constraintsJson ?? "[]", maskedAlarmJson, lu.Id);
            }

            if (string.IsNullOrWhiteSpace(constraintsJson) || constraintsJson == "[]")
            {
                _logger.LogDebug("[IOConstraint] LU#{LuId} 过滤 LU 外信号 + 结构补全后无可入库约束", lu.Id);
                return 0;
            }

            // 方案 D（增量约束）：把 lu.Id 透传到入库层，约束规则记录 SourceLuId，
            // 后续上传时能按 LU 精准软删 + 重生。
            //
            // 结构化白名单（SCoil/RCoil 支持）：收集本 LU 所有独立 Coil 锚点的 target 信号
            // （含 Coil/SCoil/RCoil 驱动的 DB 内部信号，如 DB***Alarm.Type.Errors.Error*），
            // 作为本次 SaveIOConstraintsAsync 的 extraAllowedSignals 传入。
            // 这样这些被 PLC 程序真实驱动的 DB 信号能通过白名单 + IMPLIES 方向校验 Sensor→Alarm 例外，
            // 但跨 LU 不串、不依赖命名前缀（§4.2 兼容）。
            var coilTargets = anchorBlocks
                .Where(b => b.Type == PldAnchorType.Coil && !string.IsNullOrWhiteSpace(b.Anchor))
                .Select(b => b.Anchor.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var saveResult = await SaveConstraintsSerializedAsync(projectId, constraintsJson, lu.Id, coilTargets);
            var saveJson = JsonHelper.SerializeObject(saveResult);

            int inserted = 0;
            // JsonHelper.SerializeObject 用 CamelCaseNamingStrategy 序列化（`Inserted` → `inserted`）
            // 这里 case-insensitive 找 `"inserted"` 或 `"Inserted"` 都能匹配
            if (saveJson.Contains("\"inserted\":", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(saveJson);
                    if (doc.RootElement.TryGetProperty("inserted", out var ins)
                        || doc.RootElement.TryGetProperty("Inserted", out ins))
                    {
                        inserted = ins.GetInt32();
                        _progressService.IncrementConstraints(projectId, inserted);
                    }
                }
                catch { }
            }
            return inserted;
        }

        /// <summary>
        /// 系统提示词回退选择：DB 内容非空白则用之，否则回退默认。纯函数，便于单测。
        /// </summary>
        public static string ResolveSystemPrompt(string dbContent, string defaultContent)
            => string.IsNullOrWhiteSpace(dbContent) ? defaultContent : dbContent;

        /// <summary>
        /// 取 LU 级 Phase2 system prompt：优先读 Prompt_Template（Key=ConstraintPromptKeys.Phase2System），
        /// 读不到/为空回退硬编码默认。实例内缓存一次，避免每 LU 查库。
        /// </summary>
        private async Task<string> GetLogicUnitConstraintSystemPromptAsync()
        {
            if (_cachedSystemPrompt != null)
            {
                return _cachedSystemPrompt;
            }

            string? dbContent = null;
            try
            {
                var rows = await _promptTemplateService
                    .GetListAsync(t => t.PromptKey == ConstraintPromptKeys.Phase2System && t.IsDeleted == false);
                dbContent = rows.Count > 0 ? rows[0].Content : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] 读取自定义约束系统提示词失败，回退默认");
            }

            _cachedSystemPrompt = ResolveSystemPrompt(dbContent ?? string.Empty, DefaultLogicUnitConstraintSystemPrompt());
            return _cachedSystemPrompt;
        }

        /// <summary>
        /// LU 级 Phase2 system prompt 的出厂默认值（DB 未配置时回退用）。
        /// </summary>
        public static string DefaultLogicUnitConstraintSystemPrompt()
        {
            return @"你是 PLC 约束分析专家。任务：基于给定的 1 个 LogicUnit 的**树形 Markdown 逻辑描述**（来自 Step1：AST 渲染或 FlgNet 渲染）+ 该 LU 涉及的 IO 信号清单，**全面、不保守地**输出该 LU 内的所有约束。

## 树形描述的读法

每条 LU 描述都是缩进列表：
- 根行 `- 锚点: \`主输出\`` 表示本网络驱动的主信号
- 子节点是布尔表达式树：
  - `- **且** (AND) — 以下条件全部满足:` → 后面缩进的所有子条件 AND
  - `- **或** (OR) — 以下条件任一满足:` → 后面缩进的所有子条件 OR
  - `- 触点(常开): \`X\`` → X = TRUE
  - `- 触点(常闭): \`X\`` → X = FALSE（取反）
  - `- 线圈输出: \`Y\`` → 驱动 Y
  - `- 输入引脚: \`PinName\`` / `- 输出引脚: \`PinName\` → \`实际信号\`` → FB 引脚绑定
- AST 节点 `- 外部依赖: \`FB.PinName → 实际信号\`` 表示 FB 调用的引脚→外部信号映射，**约束生成时用映射右边的实际信号**

## 约束类型与 operator 映射（必须严格使用）
- Mutex       → operator: AT_MOST_ONE             （所有 signalRole = Member；不允许同时为 true）
- AllRequired → operator: ALL_TRUE                 （Source 为真时 Member 必须全部为真）
- Pairing     → operator: IMPLIES                  （Source 为真时 Target 必须为真）
- Safety      → operator: REQUIRES_0_WHEN_FALSE    （Source=false 即急停按下时 Target 必须为 false；详见下方 Safety 段铁律 2）
- 【LLM 不再产 REQUIRES_0 / MUST_BE_FALSE】SCoil 屏蔽态报警网络由 PldStructuralConstraintAugmenter.AugmentMaskedAlarm 结构化包圆（§4.5），LLM 跳过所有 SCoil/RCoil 锚点

## 从树形描述提取约束

### Pairing/IMPLIES（Q 驱动 I）
- 树中同一 FB 调用下，「输出引脚: QFwd → A」与「输入引脚: IWP」的实参里出现的 I 信号 X
  → Pairing: Source=A（Q 信号），Target=X（I 信号）
- 「外部依赖: FB.QFwd → A」与「外部依赖: FB.IWP → X」（AST 风格）同理

### AllRequired/ALL_TRUE（同步到位）
- 当 IWP/IHP 引脚下是 `**且** (AND)` 节点串联多个 I 信号 → Source=对应 Q 信号，Member=AND 下的所有 I 信号

### Mutex/AT_MOST_ONE
仅以下三类**结构证据**允许生成 Mutex：
1. 同一 FB 实例的反向 Q 输出对：QFwd 绑定的 A ↔ QBkd 绑定的 B
2. 同一 FB 实例的反向 I 反馈对：IWP 绑定的表达式中**核心 %I 信号** X ↔ IHP 绑定的表达式中**核心 %I 信号** Y。
   - 「核心 %I 信号」= IWP/IHP 实参表达式中**地址为 %I 的 IO Tag**（信号清单里能找到、Direction=I_Input）
   - 允许 IWP/IHP 实参是 `(I 信号 OR 旁路信号) AND NOT(调试信号)` 这种复合表达式 —— 只要各自找到 1 个 %I 信号即可
   - 例如：IWP = `(OP030_CYL01_W OR ByPass_CYL[1]) AND NOT(Debug)` → 核心 I 是 `OP030_CYL01_W`
          IHP = `OP030_CYL01_H OR ByPass_CYL[2]` → 核心 I 是 `OP030_CYL01_H`
          → 输出 Mutex(OP030_CYL01_W, OP030_CYL01_H)
   - 如果 IWP/IHP 实参里有 2+ 个 %I 信号（如多个传感器 AND/OR），优先取在 IO 信号清单里出现的、地址同 station 范围的 I 信号
   - **严禁把多个独立 I 输入传感器（3+ 个 %I 信号互不相关）放同一条 Mutex**
3. 模式选择信号互斥：AutoMod / ManualMod / AutoRunMod 等 KeyFbPin 末段命中的 DB 变量

### Safety 约束（按场景区分两个 operator）

博图 FB 标准 InputParam `E_StopStatus / ResetStatus / SafetyStatus / SafeStop` 是「**状态有效**」语义，**不是「触发**」语义：
- `E_StopStatus = true` → 急停链路正常（安全继电器闭合）→ FB 允许动作
- `E_StopStatus = false` → 急停按下 → FB 禁止动作、输出强制断电
- ResetStatus 同理（=true 是已复位，=false 是未复位）

**铁律 1：LLM 完全不产 REQUIRES_0**：
- 禁止：`Safety(Source=E_StopStatus, Target=[Q 输出])` — 方向反了（这意思是『急停链路正常时输出必须断电』，完全错）
- 禁止：任何带 Source 的 REQUIRES_0 都不要产
- 禁止：任何无 Source 的 REQUIRES_0 也不要产（屏蔽态 SCoil 报警网络的 %I 传感器约束由代码 Augmenter 包圆，产 MUST_BE_FALSE 单 Target，不走 LLM 路径）
- 急停联锁请改用 `REQUIRES_0_WHEN_FALSE`（见铁律 2）

**铁律 2：急停联锁用 REQUIRES_0_WHEN_FALSE（NEW 2026-05-26）**：
- FB 调用的 InputParam `E_StopStatus / ResetStatus / SafetyStatus / SafeStop` 绑定到外部信号 X
- 同 FB 输出有 Q 信号 Y → 产 `REQUIRES_0_WHEN_FALSE(Source=X, Targets=[Y, Y2, ...])`
- 语义：「X 失效（=false，如急停按下）时，所有 Y 必须 false（强制断电）」
- 同 LU 多个 Q 输出合并成 1 条多 Target 约束
- 同 Source 多个 FB 的 Q 输出可以合并；不同 Source 各产一条
- operator 必须严格用 `REQUIRES_0_WHEN_FALSE`（注意不是 `REQUIRES_0`）
- Source 末段**必须**是 `E_StopStatus` / `ResetStatus` / `SafetyStatus` / `SafeStop` 才有意义；其它信号（如 ManualMod 等业务态）不要产 REQUIRES_0_WHEN_FALSE

**正确示例**：
```json
{
  ""ruleType"": ""Safety"",
  ""operator"": ""REQUIRES_0_WHEN_FALSE"",
  ""signals"": [
    {""signalName"": ""DB320_OP30Manual.General.E_StopStatus"", ""signalRole"": ""Source""},
    {""signalName"": ""OP030_YV01_H"", ""signalRole"": ""Target""},
    {""signalName"": ""OP030_YV01_W"", ""signalRole"": ""Target""}
  ]
}
```
诊断引擎实时行为：E_StopStatus=false 时检查 YV01_H/W 是否全 false，任一 true → 告警「急停链路断开但输出未断电」。E_StopStatus=true 时不检查（PLC 正常运行态）。

### 置位/复位线圈（SCoil / RCoil）— LLM 不产任何约束

独立 Coil 锚点行括号末尾如果出现 `— 置位线圈 SCoil` 或 `— 复位线圈 RCoil`，**LLM 整体跳过，不要为该 SCoil/RCoil 产任何约束**。

**理由**：
- **屏蔽态 SCoil 报警网络（含 AlwaysTRUE/AlwaysFALSE/Null 占位符）**：每个 %I 传感器独立成约束「该 %I 必须永为 false」由 `PldStructuralConstraintAugmenter.AugmentMaskedAlarm` **结构化代码包圆**（§4.5，operator=MUST_BE_FALSE，单 Target 形态），不走 LLM 路径。LLM 反追 SCoil 触发条件时会被 TON 等非布尔部件阻断（典型 FZ92 → TON → SCoil(Error2)），漏识率高
- **非屏蔽 SCoil**：DB 内部 Error 报警位拿不到实时值，Pairing(sensor→alarm) 检查不了，不产
- **RCoil**：latch 复位状态机，瞬时布尔约束无法精确表达，不产

**Augmenter 包圆范围**：屏蔽态 SCoil 网络的所有 %I 传感器各产 1 条 `MUST_BE_FALSE` 单 Target 约束，不需要 LLM 介入

## IoMappingHint 语义参考（跨项目通用，不依赖具体 FB 命名）

IO 信号清单每个信号附带 IoMappingHint 字段：

- **Category=VirtualQPoint**：被 FB 内部 Q 输出引脚驱动的虚拟 Q 点
  - 同一 Block 下出现 2 个 VirtualQPoint 的 Q 信号 → 大概率是同 FB 的反向 Q 输出对，输出 Mutex
- **Category=HandshakeSignal**：工位间握手信号
- **Category=Ignore（铁律）**：**禁止作为任何约束的 signalName**。
  - 这些信号是中转映射类（典型如 Sensor Mapping 块里把 -FZ/-SQ/-LS 等汇总到 DI.Sensor[N] 的转接信号），没有控制语义。
  - **不论在任何 LU 出现、不论有什么命名相似性，都不能产 Mutex/Pairing/Safety/AllRequired**。
  - 即使 IoMappingHint.Category=Ignore 的信号出现在树形描述的赋值/触点里，也只是数据搬运，**不能从中推任何约束**。
  - 如果 LU 内**所有**信号都标 Category=Ignore，**直接返回 []**，不产任何约束。

判定优先级：**树形描述 > IoMappingHint > 信号清单的 Direction(%Q/%I)**

## Pairing 方向铁律
- Source 必须是 Q 信号（Direction=Q_Output，地址 %Q），Target 必须是 I 信号（Direction=I_Input，地址 %I）
- 物理因果：电磁阀(Q) → 气缸到位(I)，方向只能从 Q 到 I，反了自动纠正

## 输出格式（必须严格遵守）
只输出纯 JSON 数组，无任何文字、思考、markdown：
[
  {
    ""ruleName"": ""简短规则名"",
    ""ruleType"": ""Mutex|AllRequired|Pairing|Safety"",
    ""operator"": ""AT_MOST_ONE|ALL_TRUE|IMPLIES|REQUIRES_0"",
    ""confidence"": 0.95,
    ""priority"": ""Critical|High|Normal|Info"",
    ""description"": ""自然语言描述"",
    ""signals"": [{""signalName"": ""信号名"", ""signalRole"": ""Source|Target|Member""}]
  }
]

## 铁律
1. **不允许保守拒绝**：宁多勿少，入库有 IsAllowedConstraintSignal 白名单 + operator 兜底过滤错误约束
2. 只允许使用「该 LU 的 IO 信号清单」里的信号或末段命中 KeyFbPin 的全局 DB 变量作 signalName。禁止编造、禁止跨 LU 引用
3. **【FB 形参替换铁律】** signalName 必须是实际 IO Tag 名（如 OP030_YV12_W），禁止出现 FB 实例形参（如 Valve[12].QFwd）。如果树形描述里出现形参，到「输出引脚 → 实际信号」或「外部依赖: FB.Pin → 实际信号」处查表替换。若该引脚未绑定外部 IO Tag（开路），跳过该约束
4. **【独立传感器互斥铁律】** Mutex 只允许在上述 3 种结构上输出。严禁把 3 个以上独立 I 输入传感器（不论命名前后缀）放同一条 Mutex
5. Pairing 的 Source 必须是 Q 输出（%Q），Target 必须是 I 反馈（%I），反了自动纠正
6. 同一信号对的 Mutex 只生成一条；同一 Source 的 AllRequired / Safety 合并成一条多 Member/Target
7. 仅当树形描述明确显示「无 Coil 输出 / 无 FB Call / 无外部依赖」全部成立时才返回 []。否则必须输出至少 1 条约束";
        }

        /// <summary>
        /// LU 级 Phase2 user prompt：把 ParsedLogicDescription + 信号清单组装好交给 AI。
        /// </summary>
        private static string BuildLogicUnitConstraintPrompt(
            PLC_LogicUnitEntity lu,
            List<PLC_IOTagEntity> luSignals,
            List<string> extras)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Network 元信息");
            sb.AppendLine($"- LogicUnitId: {lu.Id}");
            sb.AppendLine($"- NetworkId: {lu.NetworkId}");
            sb.AppendLine($"- 标题: {lu.Title ?? "无"}");
            sb.AppendLine($"- 注释: {lu.Comment ?? "无"}");
            sb.AppendLine();

            sb.AppendLine("## 该 Network 涉及的 IO 信号清单（仅这些信号可作 signalName，其它一律禁止）");
            sb.AppendLine("```json");
            var sigArr = luSignals.Select(t => new
            {
                t.TagName,
                t.Address,
                Direction = ResolveDirection(t.Address),
                t.Comment,
                // IoMappingHint：从 IoTag.IoMapping 字段抽精炼的语义信息给 AI 参考
                // category=VirtualQPoint 表示这是被某个 FB 内部 Q 输出引脚驱动的外部 Q 信号 → 配 Mutex/Pairing 的关键标记
                // sourceVar 表示驱动这个 IO 的程序变量（可能是 FB 引脚结果或别的 IO）
                // 不依赖具体 FB 块名/引脚名，跨项目通用
                IoMappingHint = BuildIoMappingHint(t.IoMapping)
            }).ToList();
            sb.AppendLine(JsonSerializer.Serialize(sigArr, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            sb.AppendLine("```");

            if (extras.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## 该 Network 涉及的关键全局 DB 变量（KeyFbPin 白名单豁免，允许作 signalName）");
                sb.AppendLine("```json");
                sb.AppendLine(JsonSerializer.Serialize(extras));
                sb.AppendLine("```");
            }

            sb.AppendLine();
            sb.AppendLine("## 该 LogicUnit 的树形逻辑描述（Step1 输出：AST 渲染 / FlgNet 渲染）");
            sb.AppendLine(lu.ParsedLogicDescription ?? "(空)");
            sb.AppendLine();
            sb.AppendLine("请按 system prompt 的格式输出该 LogicUnit 内信号之间的全部约束 JSON 数组。");
            return sb.ToString();
        }

        /// <summary>
        /// 从 IoTag.IoMapping 字段抽精炼的语义提示，供 Phase2 prompt 引导 AI。
        /// iomapping 格式（pipe 分隔多条 entry，分号分隔字段）：
        ///   tag=X; sourceVar=...; station=...; block=...; logicUnitId=...; category=VirtualQPoint|Ignore|HandshakeSignal; label=...; mapping=...; reason=...
        /// 只取首条 entry 的关键字段（category/sourceVar/block/label），降低 prompt 大小。
        /// </summary>
        private static object? BuildIoMappingHint(string? ioMapping)
        {
            if (string.IsNullOrWhiteSpace(ioMapping)) return null;
            // 取首条 entry
            var firstEntry = ioMapping.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstEntry)) return null;

            string? category = null, sourceVar = null, block = null, label = null;
            foreach (var part in firstEntry.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var k = part[..eq].Trim();
                var v = part[(eq + 1)..].Trim();
                if (string.Equals(k, "category", StringComparison.OrdinalIgnoreCase)) category = v;
                else if (string.Equals(k, "sourceVar", StringComparison.OrdinalIgnoreCase)) sourceVar = v;
                else if (string.Equals(k, "block", StringComparison.OrdinalIgnoreCase)) block = v;
                else if (string.Equals(k, "label", StringComparison.OrdinalIgnoreCase)) label = v;
            }
            if (category == null && sourceVar == null && block == null && label == null) return null;
            return new { Category = category, SourceVar = sourceVar, Block = block, Label = label };
        }

        private static string ResolveDirection(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "DB_Internal";
            if (address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase)) return "Q_Output";
            if (address.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) return "I_Input";
            if (address.StartsWith("%M", StringComparison.OrdinalIgnoreCase)) return "M_Merker";
            return "DB_Internal";
        }

        /// <summary>
        /// 项目完成后批量算 IOTag.ConstraintStatus：
        /// 出现在约束表 ai_plc_ioconstraintsignal 的 tag = 2 (done)，否则 = 0 (noConstraint)。
        /// </summary>
        private async Task UpdateIoTagConstraintStatusAsync(long projectId)
        {
            using var scope = _serviceProvider.CreateScope();
            var ioTagSvc = scope.ServiceProvider.GetRequiredService<IPLC_IOTagService>();
            var ruleSvc = scope.ServiceProvider.GetRequiredService<IPLC_IOConstraintRuleService>();
            var signalSvc = scope.ServiceProvider.GetRequiredService<IPLC_IOConstraintSignalService>();

            var rules = await ruleSvc.GetListAsync(r => r.ProjectId == projectId && !r.IsDeleted);
            var ruleIds = rules.Select(r => r.Id).ToHashSet();
            var coveredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ruleIds.Count > 0)
            {
                var signals = await signalSvc.GetListAsync(s => ruleIds.Contains(s.RuleId) && !s.IsDeleted);
                foreach (var s in signals)
                {
                    if (!string.IsNullOrWhiteSpace(s.SignalName)) coveredNames.Add(s.SignalName!.Trim());
                }
            }

            // 加载本项目所有 IO Tag（不限制 DataType=Bool；非 Bool 信号同样标记 status，避免它们一直挂在"待处理"列表）
            var tags = await ioTagSvc.GetListAsync(t => t.ProjectId == projectId && !t.IsDeleted);
            var now = DateTime.Now;
            int markedDone = 0, markedCovered = 0;
            foreach (var t in tags)
            {
                // status 语义：
                //   2 = 该信号在本轮约束生成流程中已被处理（不论是否真的出现在约束表里）
                //   - 出现在约束表 → 是约束的一员
                //   - 没出现 → 业务上推不出约束（信号未被任何 LU 使用，或所在 LU 仅有 1 个 IO Tag，或 AI 判定无约束）
                //   保留 ConstraintStatus=0 仅有一种情况：从未跑过任何处理（如新建项目刚上传 IO Tag）
                int newStatus;
                bool covered = !string.IsNullOrWhiteSpace(t.TagName) && coveredNames.Contains(t.TagName!);
                if (covered)
                {
                    newStatus = 2;
                    markedCovered++;
                }
                else
                {
                    newStatus = 2; // 合法跳过也算已处理；前端不再显示到"待处理"
                    markedDone++;
                }
                if (t.ConstraintStatus != newStatus)
                {
                    var captured = t;
                    var st = newStatus;
                    await ioTagSvc.UpdateAsync(
                        u => new PLC_IOTagEntity { ConstraintStatus = st, ConstraintAnalyzedAt = now },
                        u => u.Id == captured.Id);
                }
            }
            _logger.LogInformation("[IOConstraint] 项目 {Pid} IOTag 状态更新：{Covered} 个在约束表中，{Skipped} 个合法跳过（共 {Total} 个 IO Tag）",
                projectId, markedCovered, markedDone, tags.Count);
        }

        /// <summary>
        /// 公开接口：对单个信号执行约束分析（同步等待完成）
        /// 供 PLC_IOConstraintApp.AnalyzeSingleSignalAsync 调用
        /// </summary>
        public async Task ProcessSingleSignalExternalAsync(long projectId, PLC_IOTagEntity tag)
        {
            var chatClient = await GetChatClientAsync();
            if (chatClient == null)
                throw new Exception("无可用的 LLM 模型");

            var allBoolTags = await _ioTagService.GetListAsync(x =>
                x.ProjectId == projectId
                && !x.IsDeleted
                && x.DataType == "Bool");

            var batchContext = await BuildBatchContextAsync(projectId, allBoolTags);
            await ProcessSingleSignalAsync(projectId, tag, chatClient, batchContext);
            await FlushPendingTagStatusesAsync(batchContext);
        }

        /// <summary>串行处理所有信号</summary>
        private async Task ProcessSignalsSequentialAsync(
            long projectId, List<PLC_IOTagEntity> tags, IChatClient chatClient, ConstraintBatchContext batchContext)
        {
            int processedCount = 0;
                
            foreach (var tag in tags)
            {
                processedCount++;
                _progressService.UpdateCurrentSignal(projectId, tag.TagName, processedCount);

                await ProcessSingleSignalAsync(projectId, tag, chatClient, batchContext);
            }
        }

        /// <summary>并发处理所有信号</summary>
        private async Task ProcessSignalsConcurrentAsync(
            long projectId, List<PLC_IOTagEntity> tags, IChatClient chatClient, int concurrency, ConstraintBatchContext batchContext)
        {
            var semaphore = new SemaphoreSlim(concurrency);
            int processedCount = 0;

            try
            {
                var tasks = tags.Select(async tag =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var current = Interlocked.Increment(ref processedCount);
                        _progressService.UpdateCurrentSignal(projectId, tag.TagName, current);
                        using var scope = _serviceProvider.CreateScope();
                        var scopedProcessor = scope.ServiceProvider.GetRequiredService<IOConstraintPreprocessService>();
                        await scopedProcessor.ProcessSingleSignalAsync(projectId, tag, chatClient, batchContext);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }
            finally
            {
                semaphore.Dispose();
            }
        }

        /// <summary>
        /// 处理单个信号（两阶段 AI 分析）：
        ///   Phase 1 — 关联信号发现：AI 根据命名/注释/设备从全量 IOTag 列表中识别关联信号
        ///   Phase 2 — 多信号联合推理：收集所有关联信号的逻辑上下文，AI 联合推理约束
        /// </summary>
        private async Task 
            ProcessSingleSignalAsync(
            long projectId, PLC_IOTagEntity tag, IChatClient chatClient, ConstraintBatchContext batchContext)
        {
            var signalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 标记为处理中
                await StageTagStatusAsync(batchContext, tag.Id, 1, DateTime.Now);
                _progressService.SetMessage(projectId, $"正在分析信号: {tag.TagName}，阶段=Phase1 关联发现");

                // ━━━ Phase 1: 关联信号发现 ━━━
                var phase1Prompt = BuildRelatedSignalDiscoveryPrompt(tag, batchContext.CompactTagList);

                _logger.LogDebug("[IOConstraint] Phase1 关联发现: signal={Signal}, 候选池={Count}",
                    tag.TagName, batchContext.CompactTagList.Count);

                var relatedSignalNames = new List<string>();

                // 调用 AI 识别关联信号
                string phase1Response = await CallAIWithRetryAsync(
                    chatClient,
                    GetPhase1SystemPrompt(),
                    phase1Prompt,
                    tag.TagName,
                    "Phase1",
                    timeoutSeconds: PHASE1_AI_TIMEOUT_SECONDS,
                    maxRetries: PHASE1_MAX_RETRIES);

                if (!string.IsNullOrWhiteSpace(phase1Response))
                {
                    relatedSignalNames = ExtractRelatedSignalNames(phase1Response);
                    // 去掉当前信号自己
                    relatedSignalNames.RemoveAll(n =>
                        n.Equals(tag.TagName, StringComparison.OrdinalIgnoreCase));
                    // 限制 Phase2 的关联信号数量，避免上下文过大导致模型超时
                    if (relatedSignalNames.Count > MAX_PHASE2_RELATED_SIGNALS)
                        relatedSignalNames = relatedSignalNames.Take(MAX_PHASE2_RELATED_SIGNALS).ToList();
                }

                _logger.LogDebug("[IOConstraint] Phase1 结果: signal={Signal}, 关联信号={Count}个: [{Signals}]",
                    tag.TagName, relatedSignalNames.Count, string.Join(", ", relatedSignalNames.Take(8)));

                // ━━━ Phase 2: 仅以 tag 自身 LogicContext 推理约束 ━━━
                // 设计决策（2026-05-16）：只喂 tag 自身的 LogicContext，不再收集 relatedSignalNames 的 LogicContext。
                // 原因：把多个信号的 LogicContext（即多个 Network 的描述）拼到同一个 prompt，会导致 AI 跨 LU 串错引脚绑定、
                // 把 LU A 的 Q→I 链跟 LU B 的拓扑搞混，推出错误约束。
                // signalsToAnalyze 仍然保留用于日志/文档目的，但 LogicContext 只采集 tag 自己一份。
                _progressService.SetMessage(projectId, $"正在分析信号: {tag.TagName}，阶段=逻辑上下文收集");
                var signalsToAnalyze = new List<string> { tag.TagName };
                signalsToAnalyze.AddRange(relatedSignalNames);

                var contextMap = new Dictionary<string, string>();
                var rawContextResults = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    var contextItem = await GetSignalLogicContextCachedAsync(projectId, tag.TagName, batchContext);
                    rawContextResults[tag.TagName] = contextItem.RawResult;

                    if (contextItem.HasUsableLogicContext)
                    {
                        contextMap[tag.TagName] = contextItem.PromptJson;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[IOConstraint] 获取信号 {Signal} 自身 LogicContext 失败，跳过", tag.TagName);
                }

                if (contextMap.Count == 0)
                {
                    // 全部信号都没有逻辑上下文，跳过
                    await StageTagStatusAsync(batchContext, tag.Id, 2, DateTime.Now);
                    _progressService.IncrementNoContext(projectId);
                    _logger.LogDebug("[IOConstraint] 信号 {Signal} 及关联信号均无逻辑上下文，跳过", tag.TagName);
                    return;
                }

                // 收集关键 FB 引脚绑定的 DB 变量（如 General.AutoMod、General.E_StopStatus），
                // 构建扩展信号白名单，使这些语义关键变量能参与约束生成
                var keyPinDbVars = CollectKeyPinDbVarNamesFromContexts(rawContextResults, batchContext.ProjectTagNames);
                var extendedTagNames = BuildExtendedTagNames(batchContext.ProjectTagNames, keyPinDbVars);
                if (keyPinDbVars.Count > 0)
                {
                    _logger.LogDebug("[IOConstraint] 信号 {Signal} 收集到 {Count} 个关键引脚 DB 变量: [{Vars}]",
                        tag.TagName, keyPinDbVars.Count, string.Join(", ", keyPinDbVars.Take(10)));
                }

                var deterministicConstraintsJson = BuildDeterministicConstraintsJson(
                    rawContextResults,
                    extendedTagNames,
                    signalsToAnalyze);
                var callPinEvidence = BuildCallPinConstraintEvidence(
                    rawContextResults,
                    extendedTagNames);

                // ━━━ 检索长期记忆 ━━━
                string memoryContext = null;
                try
                {
                    var signalContext = $"{tag.TagName} {tag.Comment ?? ""} {tag.EquipmentId ?? ""}";
                    var relevantMemories = await _memoryService.SearchMemoriesAsync(DiagnosticMemoryService.SceneConstraint, projectId, signalContext, MEMORY_TOP_K);
                    if (relevantMemories != null && relevantMemories.Count > 0)
                    {
                        memoryContext = _memoryService.BuildMemoryContextPrompt(DiagnosticMemoryService.SceneConstraint, relevantMemories);
                        _logger.LogDebug("[IOConstraint] 记忆检索成功: signal={Signal}, 记忆数={Count}",
                            tag.TagName, relevantMemories.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IOConstraint] 记忆检索失败，继续执行: signal={Signal}", tag.TagName);
                }

                // 构建联合推理 Prompt
                var relatedTagInfos = relatedSignalNames
                    .Select(signalName => batchContext.TagByName.TryGetValue(signalName, out var relatedTag) ? relatedTag : null)
                    .Where(t => t != null)
                    .ToList();
                var phase2Prompt = BuildMultiSignalConstraintPrompt(
                    tag,
                    relatedTagInfos,
                    contextMap,
                    deterministicConstraintsJson);

                _logger.LogDebug("[IOConstraint] Phase2 联合推理: signal={Signal}, 有效上下文={Count}个信号",
                    tag.TagName, contextMap.Count);
                _progressService.SetMessage(projectId, $"正在分析信号: {tag.TagName}，阶段=Phase2 约束推理");

                // 调用 AI 联合推理约束（携带记忆上下文）
                string phase2Response = await CallAIWithRetryAsync(
                    chatClient,
                    GetSystemPrompt(),
                    phase2Prompt,
                    tag.TagName,
                    "Phase2",
                    memoryContext,
                    timeoutSeconds: PHASE2_AI_TIMEOUT_SECONDS,
                    maxRetries: PHASE2_MAX_RETRIES);

                // 解析 AI 返回的约束 JSON → 保存
                var aiConstraintsJson = string.IsNullOrWhiteSpace(phase2Response)
                    ? null
                    : ExtractJsonArray(phase2Response);
                // 构建信号地址字典，用于后处理过滤器基于 %Q/%I 地址前缀判断信号方向（callPinEvidence 的 fallback）
                var signalAddressDict = batchContext.TagByName
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value.Address))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Address, StringComparer.OrdinalIgnoreCase);
                var constraintsJson = MergeConstraintJsonArrays(callPinEvidence, extendedTagNames, signalAddressDict, deterministicConstraintsJson, aiConstraintsJson);
                constraintsJson = AnnotateConstraintSourcesWithIntermediateResolution(constraintsJson, rawContextResults);
                if (!string.IsNullOrEmpty(constraintsJson))
                {
                    _progressService.SetMessage(projectId, $"正在分析信号: {tag.TagName}，阶段=保存约束");
                    var saveResult = await SaveConstraintsSerializedAsync(projectId, constraintsJson);
                    var saveJson = JsonHelper.SerializeObject(saveResult);

                    if (saveJson.Contains("\"Inserted\":"))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(saveJson);
                            if (doc.RootElement.TryGetProperty("Inserted", out var inserted))
                            {
                                _progressService.IncrementConstraints(projectId, inserted.GetInt32());
                            }
                        }
                        catch { }
                    }

                    _logger.LogDebug("[IOConstraint] 信号 {Signal} 两阶段约束推理完成", tag.TagName);
                }
                else
                {
                    _progressService.IncrementZeroInsert(projectId);
                    _logger.LogDebug("[IOConstraint] 信号 {Signal} 未提取到可保存约束", tag.TagName);
                }

                // 标记为已完成
                await StageTagStatusAsync(batchContext, tag.Id, 2, DateTime.Now);
                _logger.LogInformation("[IOConstraint] 信号 {Signal} 处理完成，总耗时 {ElapsedMs} ms",
                    tag.TagName, signalStopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] 信号 {Signal} 处理失败", tag.TagName);

                // 标记为失败
                await StageTagStatusAsync(batchContext, tag.Id, 3, DateTime.Now);

                _progressService.IncrementFailed(projectId);
            }
            finally
            {
                signalStopwatch.Stop();
            }
        }

        /// <summary>
        /// 带重试的 AI 调用封装
        /// </summary>
        public async Task<string> CallAIWithRetryAsync(
            IChatClient chatClient, string systemPrompt, string userPrompt,
            string signalForLog, string phaseForLog, string memoryContext = null,
            int timeoutSeconds = PHASE2_AI_TIMEOUT_SECONDS,
            int maxRetries = PHASE2_MAX_RETRIES)
        {
            for (int retry = 0; retry <= maxRetries; retry++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var messages = new List<ChatMessage>
                    {
                        new ChatMessage(ChatRole.System, systemPrompt)
                    };
                    
                    // 如果有记忆上下文，拼接到systemPrompt后面
                    if (!string.IsNullOrWhiteSpace(memoryContext))
                    {
                        messages.Add(new ChatMessage(ChatRole.System, memoryContext));
                    }
                    
                    messages.Add(new ChatMessage(ChatRole.User, userPrompt));

                    _logger.LogInformation("[IOConstraint] {Phase} 信号 {Signal} 开始AI调用 (retry={Retry}, timeout={Timeout}s, promptLength={PromptLength})",
                        phaseForLog, signalForLog, retry, timeoutSeconds, userPrompt?.Length ?? 0);
                    var response = await chatClient.GetResponseAsync(messages, cancellationToken: cts.Token);
                    stopwatch.Stop();
                    if (!string.IsNullOrWhiteSpace(response?.Text))
                    {
                        _logger.LogInformation("[IOConstraint] {Phase} 信号 {Signal} AI调用成功 (retry={Retry}, elapsedMs={ElapsedMs}, responseLength={ResponseLength})",
                            phaseForLog, signalForLog, retry, stopwatch.ElapsedMilliseconds, response.Text.Length);
                        return response.Text;
                    }

                    _logger.LogWarning("[IOConstraint] {Phase} 信号 {Signal} AI返回为空 (retry={Retry}, elapsedMs={ElapsedMs})",
                        phaseForLog, signalForLog, retry, stopwatch.ElapsedMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[IOConstraint] {Phase} 信号 {Signal} AI调用超时 (retry={Retry})",
                        phaseForLog, signalForLog, retry);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IOConstraint] {Phase} 信号 {Signal} AI调用失败 (retry={Retry})",
                        phaseForLog, signalForLog, retry);
                }
            }
            return null;
        }

        private async Task<ConstraintBatchContext> BuildBatchContextAsync(long projectId, List<PLC_IOTagEntity> allBoolTags)
        {
            var filteredTags = allBoolTags ?? new List<PLC_IOTagEntity>();
            var logicContextProjectCache = await _diagnosisService.LoadProjectLogicContextCacheAsync(projectId);

            // 从 iomapping 字段构建 sourceVar → 物理I点名 的反查表
            // 用于后续将 Mapping 块 AST 里的中间变量（如 DB210.DI.Sensor[N]）还原为物理信号名
            var sourceVarToPhysical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in filteredTags)
            {
                if (string.IsNullOrWhiteSpace(t.IoMapping) || string.IsNullOrWhiteSpace(t.TagName)) continue;
                foreach (var entry in ParseIoMappingEntries(t.IoMapping))
                {
                    if (!string.IsNullOrWhiteSpace(entry.SourceVar) &&
                        !sourceVarToPhysical.ContainsKey(entry.SourceVar))
                        sourceVarToPhysical[entry.SourceVar] = t.TagName!;
                }
            }

            return new ConstraintBatchContext
            {
                AllBoolTags = filteredTags,
                CompactTagList = filteredTags
                    .Select(t => new
                    {
                        t.TagName,
                        t.Address,
                        Comment = t.Comment ?? "",
                        Equipment = t.EquipmentId ?? ""
                    })
                    .OrderBy(x => x.TagName)
                    .Cast<object>()
                    .ToList(),
                ProjectTagNames = new HashSet<string>(
                    filteredTags
                        .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                        .Select(t => t.TagName!),
                    StringComparer.OrdinalIgnoreCase),
                TagByName = filteredTags
                    .Where(t => !string.IsNullOrWhiteSpace(t.TagName))
                    .GroupBy(t => t.TagName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                LogicContextProjectCache = logicContextProjectCache,
                SourceVarToPhysicalTagDict = sourceVarToPhysical
            };
        }

        private sealed class IoMappingEntry
        {
            public string? SourceVar { get; init; }
            public long? LogicUnitId { get; init; }
            public string? Category { get; init; }
            public string? Block { get; init; }
        }

        /// <summary>解析 iomapping 字段（支持多条 pipe 分隔的 entry），提取 sourceVar/logicUnitId/category/block</summary>
        private static List<IoMappingEntry> ParseIoMappingEntries(string ioMapping)
        {
            var result = new List<IoMappingEntry>();
            if (string.IsNullOrWhiteSpace(ioMapping)) return result;
            foreach (var segment in ioMapping.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in segment.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = part[..eq].Trim();
                    var v = part[(eq + 1)..].Trim();
                    fields[k] = v;
                }
                if (fields.Count == 0) continue;
                long? luId = null;
                if (fields.TryGetValue("logicUnitId", out var luStr) && long.TryParse(luStr, out var luParsed))
                    luId = luParsed;
                result.Add(new IoMappingEntry
                {
                    SourceVar = fields.TryGetValue("sourceVar", out var sv) ? sv : null,
                    LogicUnitId = luId,
                    Category = fields.TryGetValue("category", out var cat) ? cat : null,
                    Block = fields.TryGetValue("block", out var blk) ? blk : null,
                });
            }
            return result;
        }

        private Task<SignalLogicContextCacheItem> GetSignalLogicContextCachedAsync(
            long projectId,
            string signalName,
            ConstraintBatchContext batchContext)
        {
            return batchContext.LogicContextCache.GetOrAdd(
                signalName,
                key => LoadSignalLogicContextCacheItemAsync(projectId, key, batchContext));
        }

        private async Task<SignalLogicContextCacheItem> LoadSignalLogicContextCacheItemAsync(
            long projectId,
            string signalName,
            ConstraintBatchContext batchContext)
        {
            await _logicContextReadSemaphore.WaitAsync();
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var diagnosisService = scope.ServiceProvider.GetRequiredService<PlcDiagnosisService>();
                var rawResult = await diagnosisService.GetSignalLogicContextFromCacheAsync(signalName, batchContext.LogicContextProjectCache);

                // 如果该 I 点通过 Mapping 块赋值（category=Ignore），注入 Mapping 块的 AST 作为额外上下文
                // 这样 AI 能看到 Sensor[N] 在 And/Not 链中的真实结构，而不是空的逻辑上下文
                if (batchContext.TagByName.TryGetValue(signalName, out var ioTag) &&
                    !string.IsNullOrWhiteSpace(ioTag.IoMapping))
                {
                    rawResult = InjectMappingBlockContext(rawResult, ioTag.IoMapping, signalName, batchContext);
                }

                var promptJson = BuildPromptLogicContextJson(rawResult, batchContext.ProjectTagNames);
                var hasUsableLogicContext =
                    !promptJson.Contains("\"LogicContextCount\":0", StringComparison.OrdinalIgnoreCase)
                    && !promptJson.Contains("未找到访问记录", StringComparison.OrdinalIgnoreCase);

                return new SignalLogicContextCacheItem
                {
                    RawResult = rawResult,
                    PromptJson = promptJson,
                    HasUsableLogicContext = hasUsableLogicContext
                };
            }
            finally
            {
                _logicContextReadSemaphore.Release();
            }
        }

        /// <summary>
        /// 当信号是通过 Mapping 块赋值的 I 点时，从 iomapping 找到对应的 Mapping 块 AST，
        /// 将 AST 中的中间变量（Sensor[N]）替换为物理 IO 信号名后，注入到逻辑上下文里。
        /// </summary>
        private object InjectMappingBlockContext(object rawResult, string ioMapping, string signalName,
            ConstraintBatchContext batchContext)
        {
            try
            {
                var entries = ParseIoMappingEntries(ioMapping);
                var ignoreEntry = entries.FirstOrDefault(e =>
                    string.Equals(e.Category, "Ignore", StringComparison.OrdinalIgnoreCase) &&
                    e.LogicUnitId.HasValue);
                if (ignoreEntry == null) return rawResult;

                var luId = ignoreEntry.LogicUnitId!.Value;
                if (!batchContext.LogicContextProjectCache.LogicUnitAstDict.TryGetValue(luId, out var mappingAst) ||
                    string.IsNullOrWhiteSpace(mappingAst.RootNodeJson))
                    return rawResult;

                // 把 AST 里的中间变量名替换为物理 IO 信号名
                var resolvedAstJson = ReplaceSensorVarsInAst(mappingAst.RootNodeJson, batchContext.SourceVarToPhysicalTagDict);

                // 构建 CoOccurringSignals：找 AST 里所有替换后的物理信号（排除自身）
                var physicalSignalsInAst = ExtractPhysicalSignalsFromAst(resolvedAstJson,
                    batchContext.ProjectTagNames, signalName);

                var mappingContext = new
                {
                    LogicUnitId = luId,
                    NetworkId = (int?)null,
                    BlockName = ignoreEntry.Block ?? "MappingBlock",
                    ContextSource = "IoMapping",
                    AstRootNodeJson = resolvedAstJson,
                    AstInputVariablesJson = mappingAst.InputVariablesJson,
                    CoOccurringSignals = physicalSignalsInAst.Select(s => new
                    {
                        SignalName = s,
                        Address = batchContext.LogicContextProjectCache.ProjectIOTagDict
                            .TryGetValue(s, out var t) ? t.Address : null,
                        Comment = batchContext.LogicContextProjectCache.ProjectIOTagDict
                            .TryGetValue(s, out t) ? t.Comment : null,
                        AccessTypes = new[] { "READ" },
                        Instructions = Array.Empty<string>()
                    }).ToList<object>()
                };

                // 将 Mapping 块上下文注入到 rawResult 的 LogicContexts 里
                return MergeExtraContext(rawResult, mappingContext);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] InjectMappingBlockContext 失败: signal={Signal}", signalName);
                return rawResult;
            }
        }

        /// <summary>把 AST JSON 字符串里所有中间变量名（Sensor[N]）替换为物理 I 点名</summary>
        private static string ReplaceSensorVarsInAst(string astJson, Dictionary<string, string> sourceVarToPhysical)
        {
            if (sourceVarToPhysical.Count == 0) return astJson;
            // 按 sourceVar 长度降序替换，避免短前缀误替换
            foreach (var kv in sourceVarToPhysical.OrderByDescending(x => x.Key.Length))
            {
                astJson = astJson.Replace(
                    JsonHelper.SerializeObject(kv.Key).Trim('"'),
                    kv.Value,
                    StringComparison.Ordinal);
            }
            return astJson;
        }

        /// <summary>从替换后的 AST JSON 里提取所有出现的物理 IO 信号名（排除自身）</summary>
        private static List<string> ExtractPhysicalSignalsFromAst(string astJson, HashSet<string> projectTagNames, string excludeSignal)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in projectTagNames)
            {
                if (string.Equals(tag, excludeSignal, StringComparison.OrdinalIgnoreCase)) continue;
                if (astJson.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    result.Add(tag);
            }
            return result.ToList();
        }

        /// <summary>将额外的 Mapping 块上下文合并进 rawResult 的 LogicContexts 列表</summary>
        private static object MergeExtraContext(object rawResult, object extraContext)
        {
            try
            {
                var json = JsonHelper.SerializeObject(rawResult);
                var dict = JsonHelper.DeserializeObject<Dictionary<string, object>>(json);
                if (dict == null) return rawResult;

                var contextsJson = dict.TryGetValue("LogicContexts", out var ctxObj)
                    ? JsonHelper.SerializeObject(ctxObj)
                    : "[]";
                var contexts = JsonHelper.DeserializeObject<List<object>>(contextsJson) ?? new List<object>();
                contexts.Add(extraContext);
                dict["LogicContexts"] = contexts;
                return dict;
            }
            catch
            {
                return rawResult;
            }
        }

        private async Task StageTagStatusAsync(ConstraintBatchContext batchContext, long tagId, int status, DateTime? analyzedAt = null)
        {
            batchContext.PendingTagStatuses.AddOrUpdate(
                tagId,
                _ => new PendingTagStatus
                {
                    Status = status,
                    AnalyzedAt = analyzedAt
                },
                (_, existing) =>
                {
                    var finalStatus = status >= existing.Status ? status : existing.Status;
                    return new PendingTagStatus
                    {
                        Status = finalStatus,
                        AnalyzedAt = analyzedAt ?? existing.AnalyzedAt
                    };
                });

            var pendingCount = Interlocked.Increment(ref batchContext.PendingStatusCount);
            if (pendingCount >= STATUS_FLUSH_BATCH_SIZE)
            {
                await FlushPendingTagStatusesAsync(batchContext);
            }
        }

        private async Task FlushPendingTagStatusesAsync(ConstraintBatchContext batchContext)
        {
            if (batchContext.PendingTagStatuses.IsEmpty)
            {
                return;
            }

            await _databaseWriteSemaphore.WaitAsync();
            try
            {
                var snapshot = batchContext.PendingTagStatuses.ToArray();
                if (snapshot.Length == 0)
                {
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var ioTagService = scope.ServiceProvider.GetRequiredService<IPLC_IOTagService>();
                var pendingUpdates = snapshot
                    .Select(kv => new PLC_IOTagEntity
                    {
                        Id = kv.Key,
                        ConstraintStatus = kv.Value.Status,
                        ConstraintAnalyzedAt = kv.Value.AnalyzedAt
                    })
                    .ToList();

                if (pendingUpdates.Count > 0)
                {
                    await ioTagService.UpdateRangeAsync(pendingUpdates);

                    foreach (var item in snapshot)
                    {
                        if (batchContext.PendingTagStatuses.TryGetValue(item.Key, out var current)
                            && ReferenceEquals(current, item.Value))
                        {
                            batchContext.PendingTagStatuses.TryRemove(item.Key, out _);
                        }
                    }

                    Interlocked.Exchange(ref batchContext.PendingStatusCount, batchContext.PendingTagStatuses.Count);
                }
            }
            finally
            {
                _databaseWriteSemaphore.Release();
            }
        }

        /// <summary>
        /// 过滤掉 signals 含 LU 外信号的约束（LLM 跨 LU 编造的标志）。
        /// 规则：约束的每个 signalName 必须命中本 LU 信号清单（luScopeSignals），否则整条丢。
        /// 这是结构判定：Phase2 设计是「单 LU 独立调 LLM」，约束应该只引用本 LU 看到的信号。
        /// 若 LLM 拉了 LU 外信号（如 SafetyDoor1_Request 在 Net2，却出现在 Net8 的 Mutex 里）
        /// → 这是 LLM 命名联想编造，必须拒绝。
        /// </summary>
        private string FilterConstraintsByLuScope(string constraintsJson, HashSet<string> luScopeSignals, long luId)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson)) return constraintsJson;
            try
            {
                using var doc = JsonDocument.Parse(constraintsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return constraintsJson;
                var kept = new List<JsonElement>();
                int rejected = 0;
                foreach (var rule in doc.RootElement.EnumerateArray())
                {
                    if (!rule.TryGetProperty("signals", out var sigs) || sigs.ValueKind != JsonValueKind.Array)
                    {
                        kept.Add(rule);
                        continue;
                    }
                    bool allInScope = true;
                    string? offending = null;
                    foreach (var s in sigs.EnumerateArray())
                    {
                        if (!s.TryGetProperty("signalName", out var n)) continue;
                        var name = n.GetString();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!luScopeSignals.Contains(name.Trim()))
                        {
                            allInScope = false;
                            offending = name;
                            break;
                        }
                    }
                    if (allInScope) kept.Add(rule);
                    else
                    {
                        rejected++;
                        _logger.LogDebug("[IOConstraint] LU#{LuId} 拒绝跨 LU 编造约束（信号 {Sig} 不在本 LU 清单）", luId, offending);
                    }
                }
                if (rejected > 0)
                {
                    _logger.LogInformation("[IOConstraint] LU#{LuId} 过滤掉 {Rej} 条跨 LU 编造约束", luId, rejected);
                }
                if (kept.Count == 0) return "[]";
                // 重新序列化保留的 rules
                using var ms = new System.IO.MemoryStream();
                using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                {
                    w.WriteStartArray();
                    foreach (var r in kept) r.WriteTo(w);
                    w.WriteEndArray();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] LU#{LuId} FilterConstraintsByLuScope 失败，保留原 JSON", luId);
                return constraintsJson;
            }
        }

        // KeyFbPin 末段白名单：这些信号作为系统级常驻 FB 引脚连接，PLD 不直接引用也允许
        private static readonly HashSet<string> PldRefCheckKeyFbPins = new(StringComparer.OrdinalIgnoreCase)
        {
            "E_StopStatus", "ManualMod", "AutoRunMod", "FwdPmt", "BkdPmt",
            "ResetStatus", "Model", "PmtDebug", "NeedSafeAckFwd", "NeedSafeAckBkd",
            "SafetyStatus", "SafeStop"
        };
        // PLD 中 `X` 反引号字面量的正则
        private static readonly Regex PldBacktickRefRe =
            new Regex(@"`([^`]+)`", RegexOptions.Compiled);

        /// <summary>
        /// PLD 真实引用校验：约束的每个 signalName 必须在 PLD 反引号字面量内真实出现。
        /// 不在 PLD 中且末段非 KeyFbPin 的信号 → 整条约束丢。
        ///
        /// 此校验比 FilterConstraintsByLuScope 更严格——后者只验证信号在「LU 范围信号清单」
        /// （含本 LU UidSymbolMapJson 涉及的所有 IO Tag）内，但 LLM 仍能在这个清单内凭命名拼配
        /// 出 PLD 实际未引用的信号对。本校验直接看 PLD 文本，确保信号有"结构证据"。
        /// </summary>
        private string FilterConstraintsByPldReference(string? constraintsJson, string? pld, long luId)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) || constraintsJson == "[]") return constraintsJson ?? "[]";
            if (string.IsNullOrWhiteSpace(pld)) return constraintsJson;

            // 提取 PLD 反引号字面量集合
            var pldSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in PldBacktickRefRe.Matches(pld))
            {
                pldSignals.Add(m.Groups[1].Value.Trim());
            }
            if (pldSignals.Count == 0) return constraintsJson;

            bool IsAllowed(string? sigName)
            {
                if (string.IsNullOrWhiteSpace(sigName)) return true;  // 空信号交给后续过滤
                var n = sigName.Trim();
                if (pldSignals.Contains(n)) return true;
                // 末段命中 KeyFbPin → 放行（系统级常驻信号即使 PLD 没直接引用也允许）
                var lastDot = n.LastIndexOf('.');
                var leaf = lastDot >= 0 ? n[(lastDot + 1)..] : n;
                return PldRefCheckKeyFbPins.Contains(leaf);
            }

            try
            {
                using var doc = JsonDocument.Parse(constraintsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return constraintsJson;
                var kept = new List<JsonElement>();
                int rejected = 0;
                foreach (var rule in doc.RootElement.EnumerateArray())
                {
                    if (!rule.TryGetProperty("signals", out var sigs) || sigs.ValueKind != JsonValueKind.Array)
                    {
                        kept.Add(rule);
                        continue;
                    }
                    bool allOk = true;
                    string? bad = null;
                    foreach (var s in sigs.EnumerateArray())
                    {
                        if (!s.TryGetProperty("signalName", out var n)) continue;
                        var name = n.GetString();
                        if (!IsAllowed(name))
                        {
                            allOk = false;
                            bad = name;
                            break;
                        }
                    }
                    if (allOk) kept.Add(rule);
                    else
                    {
                        rejected++;
                        _logger.LogDebug("[IOConstraint] LU#{LuId} PLD 真实引用校验拒绝（信号 {Sig} 未在 PLD 反引号字面量出现）", luId, bad);
                    }
                }
                if (rejected > 0)
                {
                    _logger.LogInformation("[IOConstraint] LU#{LuId} PLD 真实引用校验：丢弃 {N} 条约束（含 PLD 未引用的信号）", luId, rejected);
                }
                if (kept.Count == 0) return "[]";
                using var ms = new System.IO.MemoryStream();
                using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                {
                    w.WriteStartArray();
                    foreach (var r in kept) r.WriteTo(w);
                    w.WriteEndArray();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] LU#{LuId} FilterConstraintsByPldReference 失败，保留原 JSON", luId);
                return constraintsJson;
            }
        }

        /// <summary>
        /// Mutex 白名单过滤：所有 AT_MOST_ONE 约束必须满足以下之一才放行：
        /// 1. 信号对命中 Augmenter 识别的合法 Mutex 集合（FB 反向 Q 对 / 反向 I 对）
        /// 2. 所有信号末段在 KeyFbPin（模式信号互斥，如 ManualMod ↔ AutoRunMod）
        /// 否则整条丢——消除 LLM 凭命名启发式 W/H 配对产的错 Mutex。
        /// </summary>
        private string FilterMutexesByWhitelist(string constraintsJson, HashSet<string> validMutexes, long luId)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) || constraintsJson == "[]") return constraintsJson;
            try
            {
                using var doc = JsonDocument.Parse(constraintsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return constraintsJson;
                var kept = new List<JsonElement>();
                int rejected = 0;
                foreach (var rule in doc.RootElement.EnumerateArray())
                {
                    bool isMutex = false;
                    if (rule.TryGetProperty("operator", out var op))
                    {
                        var opStr = op.GetString() ?? string.Empty;
                        isMutex = string.Equals(opStr.Trim(), "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase);
                    }
                    if (!isMutex) { kept.Add(rule); continue; }

                    var names = new List<string>();
                    if (rule.TryGetProperty("signals", out var sigs) && sigs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sigs.EnumerateArray())
                        {
                            if (s.TryGetProperty("signalName", out var n))
                            {
                                var name = n.GetString();
                                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!.Trim());
                            }
                        }
                    }
                    if (names.Count < 2) { kept.Add(rule); continue; }  // 异常结构让 2b-2 处理

                    // 检查 1：全部信号末段是 KeyFbPin → 模式互斥放行
                    bool allKeyFbPin = names.All(n =>
                    {
                        var lastDot = n.LastIndexOf('.');
                        var leaf = lastDot >= 0 ? n[(lastDot + 1)..] : n;
                        return PldRefCheckKeyFbPins.Contains(leaf);
                    });
                    if (allKeyFbPin) { kept.Add(rule); continue; }

                    // 检查 2：任意两两组合命中 Augmenter 合法 Mutex 集合
                    bool anyValidPair = false;
                    for (int i = 0; i < names.Count && !anyValidPair; i++)
                        for (int j = i + 1; j < names.Count && !anyValidPair; j++)
                        {
                            var key = BuildMutexKey(names[i], names[j]);
                            if (validMutexes.Contains(key)) anyValidPair = true;
                        }
                    if (anyValidPair) { kept.Add(rule); continue; }

                    rejected++;
                    _logger.LogDebug("[IOConstraint] LU#{LuId} Mutex 白名单拒绝: {Names}（无 PLD FB 反向对证据 + 非模式互斥）",
                        luId, string.Join(",", names));
                }
                if (rejected > 0)
                {
                    _logger.LogInformation("[IOConstraint] LU#{LuId} Mutex 白名单过滤：丢弃 {N} 条 AT_MOST_ONE（无 PLD 反向对/模式互斥证据）",
                        luId, rejected);
                }
                if (kept.Count == 0) return "[]";
                using var ms = new System.IO.MemoryStream();
                using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                {
                    w.WriteStartArray();
                    foreach (var r in kept) r.WriteTo(w);
                    w.WriteEndArray();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] LU#{LuId} FilterMutexesByWhitelist 失败，保留原 JSON", luId);
                return constraintsJson;
            }
        }

        private static string BuildMutexKey(string a, string b)
        {
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0
                ? $"MUTEX|{a}|{b}" : $"MUTEX|{b}|{a}";
        }

        /// <summary>
        /// SAME_STATE 白名单过滤：所有 LLM 产的 SAME_STATE 约束必须满足
        /// 「Members 两两都在 syncDrivenPairs 集合」才放行，否则整条丢。
        /// syncDrivenPairs 来自 PldStructuralConstraintAugmenter.ExtractSyncDrivenPairsFromBlocks，
        /// 表达「同一 in 表达式驱动多个 Coil target」的结构证据（如多缸同步通断阀）。
        /// 防止 LLM 凭命名相似（Error1/2/3/4 顺序号、_W/_H 后缀等）误产 SAME_STATE。
        /// </summary>
        private string FilterSameStateByWhitelist(string constraintsJson, HashSet<string> syncDrivenPairs, long luId)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) || constraintsJson == "[]") return constraintsJson;
            try
            {
                using var doc = JsonDocument.Parse(constraintsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return constraintsJson;
                var kept = new List<JsonElement>();
                int rejected = 0;
                foreach (var rule in doc.RootElement.EnumerateArray())
                {
                    bool isSameState = false;
                    if (rule.TryGetProperty("operator", out var op))
                    {
                        var opStr = op.GetString() ?? string.Empty;
                        isSameState = string.Equals(opStr.Trim(), "SAME_STATE", StringComparison.OrdinalIgnoreCase);
                    }
                    if (!isSameState) { kept.Add(rule); continue; }

                    var names = new List<string>();
                    if (rule.TryGetProperty("signals", out var sigs) && sigs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sigs.EnumerateArray())
                        {
                            if (s.TryGetProperty("signalName", out var n))
                            {
                                var name = n.GetString();
                                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!.Trim());
                            }
                        }
                    }
                    if (names.Count < 2) { kept.Add(rule); continue; }

                    // 检查：所有 Member 两两对都必须在 syncDrivenPairs 集合
                    bool allPairsSync = true;
                    for (int i = 0; i < names.Count && allPairsSync; i++)
                        for (int j = i + 1; j < names.Count && allPairsSync; j++)
                        {
                            var ordered = new[] { names[i], names[j] }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
                            if (!syncDrivenPairs.Contains(string.Join("|", ordered))) allPairsSync = false;
                        }
                    if (allPairsSync) { kept.Add(rule); continue; }

                    rejected++;
                    _logger.LogDebug("[IOConstraint] LU#{LuId} SAME_STATE 白名单拒绝: {Names}（无 PLD 同步驱动证据，触发条件不一致）",
                        luId, string.Join(",", names));
                }
                if (rejected > 0)
                {
                    _logger.LogInformation("[IOConstraint] LU#{LuId} SAME_STATE 白名单过滤：丢弃 {N} 条 SAME_STATE（无 PLD 同步驱动证据）",
                        luId, rejected);
                }
                if (kept.Count == 0) return "[]";
                using var ms = new System.IO.MemoryStream();
                using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                {
                    w.WriteStartArray();
                    foreach (var r in kept) r.WriteTo(w);
                    w.WriteEndArray();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] LU#{LuId} FilterSameStateByWhitelist 失败，保留原 JSON", luId);
                return constraintsJson;
            }
        }

        /// <summary>
        /// Pairing 白名单过滤：所有 IMPLIES 约束必须满足以下任一才放行：
        /// 1. 命中本 LU PLD 提取的「合法 Pairing 集合」(FB QFwd→IWP_%I_触点 / QBkd→IHP_%I_触点 标准模式)
        /// 2. SCoil 模式：所有 Target 都是本 LU 独立 Coil 锚点驱动的信号（sensor→alarm 等程序状态衍生）
        /// 第 2 条是结构判定（§4.2 兼容）：本 LU SCoil/RCoil 真实驱动的 DB 内部信号属于合法的"程序状态因果"，
        /// 与 FB 标准电磁阀→气缸反馈对是平行的两种 IMPLIES 语义。
        /// </summary>
        private string FilterPairingsByWhitelist(
            string constraintsJson,
            HashSet<string> validPairings,
            HashSet<string> coilDrivenTargets,
            long luId)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) || constraintsJson == "[]") return constraintsJson;
            try
            {
                using var doc = JsonDocument.Parse(constraintsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return constraintsJson;
                var kept = new List<JsonElement>();
                int rejected = 0;
                foreach (var rule in doc.RootElement.EnumerateArray())
                {
                    bool isImplies = false;
                    if (rule.TryGetProperty("operator", out var op))
                    {
                        var opStr = op.GetString() ?? string.Empty;
                        isImplies = string.Equals(opStr.Trim(), "IMPLIES", StringComparison.OrdinalIgnoreCase);
                    }
                    if (!isImplies) { kept.Add(rule); continue; }

                    string? src = null;
                    var targets = new List<string>();
                    if (rule.TryGetProperty("signals", out var sigs) && sigs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sigs.EnumerateArray())
                        {
                            var role = (s.TryGetProperty("signalRole", out var r) ? r.GetString() : null) ?? "Member";
                            var name = (s.TryGetProperty("signalName", out var n) ? n.GetString() : null);
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (string.Equals(role, "Source", StringComparison.OrdinalIgnoreCase)) src = name.Trim();
                            else if (string.Equals(role, "Target", StringComparison.OrdinalIgnoreCase)) targets.Add(name.Trim());
                        }
                    }
                    if (string.IsNullOrWhiteSpace(src) || targets.Count == 0)
                    {
                        kept.Add(rule);  // 结构异常的让后续 2b-2 处理
                        continue;
                    }
                    // 条件 1：所有 Target 都在 FB 标准 Pairing 白名单
                    bool allInFbWhitelist = targets.All(t => validPairings.Contains($"{src}→{t}"));
                    // 条件 2：所有 Target 都在本 LU SCoil/Coil 驱动信号集（SCoil sensor→alarm 模式）
                    bool allCoilDriven = coilDrivenTargets.Count > 0 && targets.All(t => coilDrivenTargets.Contains(t));
                    if (allInFbWhitelist || allCoilDriven) kept.Add(rule);
                    else
                    {
                        rejected++;
                        _logger.LogDebug("[IOConstraint] LU#{LuId} Pairing 白名单拒绝: {Src}→{Tgts}（无 FB QFwd→IWP/QBkd→IHP 证据，且 Target 不在本 LU Coil 驱动信号集）",
                            luId, src, string.Join(",", targets));
                    }
                }
                if (rejected > 0)
                {
                    _logger.LogInformation("[IOConstraint] LU#{LuId} Pairing 白名单过滤：丢弃 {N} 条 IMPLIES（无 PLD FB 标准结构证据）",
                        luId, rejected);
                }
                if (kept.Count == 0) return "[]";
                using var ms = new System.IO.MemoryStream();
                using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                {
                    w.WriteStartArray();
                    foreach (var r in kept) r.WriteTo(w);
                    w.WriteEndArray();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] LU#{LuId} FilterPairingsByWhitelist 失败，保留原 JSON", luId);
                return constraintsJson;
            }
        }

        /// <summary>
        /// 从 LLM 输出 JSON 中过滤掉命中「同步驱动对」的 Mutex 约束。
        /// 同步驱动对：PLD 中两个独立 Coil 锚点 + 子树触点完全相同 → 两信号物理上同时通电，**不是反向 Mutex**。
        /// LLM 常因命名规律（W/H 配对）误产此类 Mutex，违反 §4.2 铁律。在此整条丢。
        /// </summary>
        private string FilterOutSyncDrivenMutex(string constraintsJson, HashSet<string> syncDrivenPairs, long luId)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) || constraintsJson == "[]") return constraintsJson;
            try
            {
                using var doc = JsonDocument.Parse(constraintsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return constraintsJson;
                var kept = new List<JsonElement>();
                int dropped = 0;
                foreach (var rule in doc.RootElement.EnumerateArray())
                {
                    bool isMutex = false;
                    if (rule.TryGetProperty("operator", out var op))
                    {
                        var opStr = op.GetString() ?? string.Empty;
                        isMutex = string.Equals(opStr.Trim(), "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase);
                    }
                    if (!isMutex) { kept.Add(rule); continue; }

                    // 提取 Mutex 的所有 signal names
                    var names = new List<string>();
                    if (rule.TryGetProperty("signals", out var sigs) && sigs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sigs.EnumerateArray())
                        {
                            if (s.TryGetProperty("signalName", out var n))
                            {
                                var name = n.GetString();
                                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!.Trim());
                            }
                        }
                    }
                    // 检查任意两两组合是否命中同步对
                    bool hit = false;
                    for (int i = 0; i < names.Count && !hit; i++)
                        for (int j = i + 1; j < names.Count && !hit; j++)
                        {
                            var ordered = new[] { names[i], names[j] }
                                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
                            if (syncDrivenPairs.Contains(string.Join("|", ordered))) hit = true;
                        }
                    if (hit) { dropped++; continue; }
                    kept.Add(rule);
                }
                if (dropped > 0)
                {
                    _logger.LogInformation("[IOConstraint] LU#{LuId} 同步驱动对过滤：丢弃 {N} 条 Mutex（PLD 显示两信号被同一触点表达式同步驱动，不是反向对）",
                        luId, dropped);
                }
                if (kept.Count == 0) return "[]";
                using var ms = new System.IO.MemoryStream();
                using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                {
                    w.WriteStartArray();
                    foreach (var r in kept) r.WriteTo(w);
                    w.WriteEndArray();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] LU#{LuId} FilterOutSyncDrivenMutex 失败，保留原 JSON", luId);
                return constraintsJson;
            }
        }

        /// <summary>
        /// 合并 LLM 输出与 Augmenter 输出两段约束 JSON 数组并去重。
        /// 去重键：operator + 按 SortOrder 排好序的 (role:name) 列表。
        /// 同 key 时保留 LLM 输出（Augmenter 仅做补充）。
        /// </summary>
        private string MergeLlmAndStructuralJson(string llmJson, string structuralJson, long luId)
        {
            if (string.IsNullOrWhiteSpace(structuralJson) || structuralJson == "[]") return llmJson ?? "[]";
            if (string.IsNullOrWhiteSpace(llmJson) || llmJson == "[]") return structuralJson;
            try
            {
                using var docA = JsonDocument.Parse(llmJson);
                using var docB = JsonDocument.Parse(structuralJson);
                if (docA.RootElement.ValueKind != JsonValueKind.Array
                    || docB.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return llmJson;
                }

                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var merged = new List<JsonElement>();

                static string BuildKey(JsonElement rule)
                {
                    var op = rule.TryGetProperty("operator", out var o) ? o.GetString() ?? string.Empty : string.Empty;
                    op = op.Trim().ToUpperInvariant();
                    var sigParts = new List<string>();
                    if (rule.TryGetProperty("signals", out var sigs) && sigs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in sigs.EnumerateArray())
                        {
                            var role = (s.TryGetProperty("signalRole", out var r) ? r.GetString() : null) ?? "Member";
                            var name = (s.TryGetProperty("signalName", out var n) ? n.GetString() : null) ?? string.Empty;
                            sigParts.Add($"{role.ToUpperInvariant()}:{name.Trim()}");
                        }
                    }
                    sigParts.Sort(StringComparer.OrdinalIgnoreCase);
                    return $"{op}|{string.Join(",", sigParts)}";
                }

                foreach (var rule in docA.RootElement.EnumerateArray())
                {
                    var key = BuildKey(rule);
                    if (seenKeys.Add(key)) merged.Add(rule);
                }
                int addedFromB = 0;
                foreach (var rule in docB.RootElement.EnumerateArray())
                {
                    var key = BuildKey(rule);
                    if (seenKeys.Add(key))
                    {
                        merged.Add(rule);
                        addedFromB++;
                    }
                }
                if (addedFromB > 0)
                {
                    _logger.LogInformation("[IOConstraint] LU#{LuId} 结构性补全新增 {Cnt} 条约束（LLM 漏识的零歧义 FB 标准模式）",
                        luId, addedFromB);
                }
                using var ms = new System.IO.MemoryStream();
                using (var w = new System.Text.Json.Utf8JsonWriter(ms))
                {
                    w.WriteStartArray();
                    foreach (var r in merged) r.WriteTo(w);
                    w.WriteEndArray();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IOConstraint] LU#{LuId} 合并 LLM/结构性约束 JSON 失败，使用 LLM 原 JSON", luId);
                return llmJson;
            }
        }

        private async Task<object> SaveConstraintsSerializedAsync(
            long projectId,
            string constraintsJson,
            long? sourceLuId = null,
            IReadOnlyCollection<string>? extraAllowedSignals = null)
        {
            await _databaseWriteSemaphore.WaitAsync();
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var diagnosisService = scope.ServiceProvider.GetRequiredService<PlcDiagnosisService>();
                return await diagnosisService.SaveIOConstraintsAsync(projectId, constraintsJson, sourceLuId, extraAllowedSignals);
            }
            finally
            {
                _databaseWriteSemaphore.Release();
            }
        }

        private static string? AnnotateConstraintSourcesWithIntermediateResolution(
            string? constraintsJson,
            Dictionary<string, object> rawContextResults)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) || rawContextResults.Count == 0)
            {
                return constraintsJson;
            }

            var unresolvedContexts = CollectUnresolvedIntermediateContexts(rawContextResults);
            if (unresolvedContexts.Count == 0)
            {
                return constraintsJson;
            }

            try
            {
                var arrayNode = JsonNode.Parse(constraintsJson) as JsonArray;
                if (arrayNode == null)
                {
                    return constraintsJson;
                }

                foreach (var item in arrayNode.OfType<JsonObject>())
                {
                    var signals = item["signals"] as JsonArray;
                    if (signals == null)
                    {
                        continue;
                    }

                    var constraintSignals = signals
                        .OfType<JsonObject>()
                        .Select(signal => signal["signalName"]?.GetValue<string>())
                        .Where(signalName => !string.IsNullOrWhiteSpace(signalName))
                        .Select(signalName => signalName!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (constraintSignals.Count == 0)
                    {
                        continue;
                    }

                    var matchesUnresolvedContext = unresolvedContexts.Any(context =>
                    {
                        if (!constraintSignals.Contains(context.RootSignalName, StringComparer.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        if (constraintSignals.All(signalName => context.RelatedSignals.Contains(signalName)))
                        {
                            return true;
                        }

                        var operatorName = item["operator"]?.GetValue<string>();
                        if (!context.MissingLogicBody
                            || !string.Equals(operatorName, "IMPLIES", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        var sourceSignals = signals
                            .OfType<JsonObject>()
                            .Where(x => string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase))
                            .Select(x => x["signalName"]?.GetValue<string>())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => x!.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        var nonSourceSignals = signals
                            .OfType<JsonObject>()
                            .Where(x => !string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase))
                            .Select(x => x["signalName"]?.GetValue<string>())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => x!.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        return sourceSignals.Count > 0
                            && sourceSignals.All(signalName => context.RelatedSignals.Contains(signalName))
                            && nonSourceSignals.All(signalName =>
                                SignalMatchesUnresolvedContextFamily(signalName, context));
                    });

                    if (!matchesUnresolvedContext)
                    {
                        continue;
                    }

                    var existingSource = item["source"]?.GetValue<string>();
                    item["source"] = string.IsNullOrWhiteSpace(existingSource)
                        ? "AI_INTERMEDIATE_UNRESOLVED"
                        : $"{existingSource}|AI_INTERMEDIATE_UNRESOLVED";
                }

                return arrayNode.ToJsonString(new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch
            {
                return constraintsJson;
            }
        }

        private static List<UnresolvedIntermediateContextEvidence> CollectUnresolvedIntermediateContexts(
            Dictionary<string, object> rawContextResults)
        {
            var result = new List<UnresolvedIntermediateContextEvidence>();

            foreach (var (signalName, rawContext) in rawContextResults)
            {
                try
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(rawContext));
                    if (!doc.RootElement.TryGetProperty("LogicContexts", out var contexts)
                        || contexts.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var context in contexts.EnumerateArray())
                    {
                        var contextSource = TryGetString(context, "ContextSource");
                        if (!string.Equals(contextSource, "CalleeBlock", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var unresolvedPins = TryGetStringList(context, "UnresolvedCallPins");
                        var rawLogicXml = TryGetString(context, "RawLogicXml");
                        if (unresolvedPins.Count == 0 && !string.IsNullOrWhiteSpace(rawLogicXml))
                        {
                            continue;
                        }

                        var relatedSignals = BuildUnresolvedContextSignalScope(signalName, context, unresolvedPins, rawLogicXml);
                        if (relatedSignals.Count == 0)
                        {
                            relatedSignals.Add(signalName);
                        }

                        result.Add(new UnresolvedIntermediateContextEvidence
                        {
                            RootSignalName = signalName,
                            RelatedSignals = relatedSignals,
                            RelatedSignalFamilies = BuildSignalFamilies(relatedSignals),
                            MissingLogicBody = string.IsNullOrWhiteSpace(rawLogicXml)
                        });
                    }
                }
                catch
                {
                    // 忽略单条上下文解析失败，不影响整体入库
                }
            }

            return result
                .GroupBy(
                    x => $"{x.RootSignalName}|{string.Join(",", x.RelatedSignals.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static HashSet<string> BuildUnresolvedContextSignalScope(
            string rootSignalName,
            JsonElement context,
            List<string> unresolvedPins,
            string? rawLogicXml)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddSignalIfPresent(result, rootSignalName);

            var missingLogicBody = string.IsNullOrWhiteSpace(rawLogicXml);
            var unresolvedPinSet = new HashSet<string>(
                unresolvedPins.Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            var callBindingSummary = TryGetDictionaryOfString(context, "CallBindingSummary");
            var callPinSignalHints = TryGetDictionaryOfStringArray(context, "CallPinSignalHints");

            if (missingLogicBody)
            {
                foreach (var value in callBindingSummary.Values)
                {
                    AddSignalIfPresent(result, value);
                }

                foreach (var values in callPinSignalHints.Values)
                {
                    foreach (var value in values)
                    {
                        AddSignalIfPresent(result, value);
                    }
                }

                foreach (var signal in ExtractSignalNamesFromCoOccurringSignals(TryGetArray(context, "CoOccurringSignals")))
                {
                    AddSignalIfPresent(result, signal);
                }

                foreach (var signal in ExtractSignalNamesFromUidSymbolMapJson(TryGetString(context, "UidSymbolMapJson")))
                {
                    AddSignalIfPresent(result, signal);
                }

                return result;
            }

            if (unresolvedPinSet.Count == 0)
            {
                return result;
            }

            foreach (var (pinName, bindingValue) in callBindingSummary)
            {
                if (unresolvedPinSet.Contains(pinName))
                {
                    AddSignalIfPresent(result, bindingValue);
                }
            }

            foreach (var (pinName, values) in callPinSignalHints)
            {
                if (!unresolvedPinSet.Contains(pinName))
                {
                    continue;
                }

                foreach (var value in values)
                {
                    AddSignalIfPresent(result, value);
                }
            }

            return result;
        }

        private static List<string> ExtractSignalNamesFromCoOccurringSignals(List<object> coOccurringSignals)
        {
            var result = new List<string>();
            foreach (var item in coOccurringSignals)
            {
                if (item is string text)
                {
                    result.Add(text);
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(item));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("SignalName", out var signalNameElement)
                        && signalNameElement.ValueKind == JsonValueKind.String)
                    {
                        var signalName = signalNameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(signalName))
                        {
                            result.Add(signalName!);
                        }
                    }
                }
                catch
                {
                    // ignore single malformed item
                }
            }

            return result;
        }

        private static List<string> ExtractSignalNamesFromUidSymbolMapJson(string? uidSymbolMapJson)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(uidSymbolMapJson))
            {
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(uidSymbolMapJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return result;
                }

                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    result.Add(value!);
                }
            }
            catch
            {
                return result;
            }

            return result;
        }

        private static void AddSignalIfPresent(HashSet<string> result, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = value.Trim();
            if (normalized.IndexOf('.') >= 0
                || normalized.IndexOf('[') >= 0
                || normalized.IndexOf(']') >= 0)
            {
                result.Add(normalized);
                return;
            }

            result.Add(normalized);
        }

        private static bool SignalMatchesUnresolvedContextFamily(
            string signalName,
            UnresolvedIntermediateContextEvidence context)
        {
            if (string.IsNullOrWhiteSpace(signalName))
            {
                return false;
            }

            if (context.RelatedSignals.Contains(signalName))
            {
                return true;
            }

            var family = NormalizeSignalFamily(signalName);
            return !string.IsNullOrWhiteSpace(family)
                && context.RelatedSignalFamilies.Contains(family);
        }

        private static HashSet<string> BuildSignalFamilies(IEnumerable<string> signalNames)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var signalName in signalNames)
            {
                AddSignalFamilyIfPresent(result, signalName);
            }

            return result;
        }

        private static void AddSignalFamilyIfPresent(HashSet<string> result, string? signalName)
        {
            var normalized = NormalizeSignalFamily(signalName);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                result.Add(normalized);
            }
        }

        private static string NormalizeSignalFamily(string? signalName)
        {
            if (string.IsNullOrWhiteSpace(signalName))
            {
                return string.Empty;
            }

            var normalized = signalName.Trim();
            var parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3
                && int.TryParse(parts[^2], out _))
            {
                return string.Join("_", parts.Take(parts.Length - 2).Concat(new[] { parts[^1] }));
            }

            return normalized;
        }

        #endregion

        #region Prompt 构建

        /// <summary>Phase 1 系统提示词：关联信号发现</summary>
        //        private static string GetPhase1SystemPrompt()
        //        {
        //            return @"你是一个工业 PLC 信号关联分析专家。你的任务是根据信号的命名规则、注释和设备分组，
        //从候选信号列表中识别与目标信号存在潜在约束关系的关联信号。

        //## 识别策略
        //1. **命名模式**：同一设备/功能组的信号通常共享前缀（如 CYL01_FwdOK / CYL01_BkdOK）
        //2. **互斥候选**：名称含 Fwd/Bkd、Open/Close、Left/Right、Up/Down 等互补后缀
        //3. **配对关系**：驱动信号与反馈信号（如 _Cmd/_OK, _Out/_FB, _Set/_Ack）
        //4. **安全信号**：同设备的安全联锁信号
        //5. **同工位信号**：同一工位(OP)下的相关执行器/传感器
        //6. **序列信号**：同一动作序列中的步序信号

        //## 严格要求
        //- 只返回有高概率存在约束关系的信号，不要列出无关信号
        //- 选择 5~15 个最相关的信号
        //- 输出纯 JSON 数组，只包含信号名称字符串
        //- 如果没有找到有意义的关联，返回空数组 []";
        //        }

        /// <summary>Phase 1 系统提示词：关联信号发现（强化稳定版）</summary>
        private static string GetPhase1SystemPrompt()
        {
            return @"你是专业的**工业PLC信号关联分析专家**。
你的任务：根据信号命名、注释、设备信息，从候选列表中精准识别**与目标信号存在潜在约束关系**的关联信号。

## 核心识别策略（严格按优先级执行）
1. **同设备优先**：必须优先选择与目标信号同设备/同工位的信号
2. **命名模式匹配**：共享前缀（如 CYL01_xxx）、功能互补、动作配对
3. **互斥关系**：同一 FB 实例的 QFwd/QBkd 反向输出信号
4. **控制配对**：指令/反馈（_Cmd/_OK、_Out/_FB、_Set/_Ack）
5. **安全联锁**：同设备安全相关信号
6. **序列步序**：同一自动化流程内的步序信号

## 输出铁律（绝对禁止违反）
1. **只输出纯 JSON 数组**，不输出任何解释、说明、标题、markdown、代码块
2. 数组内**仅包含信号名称字符串**，无其他任何内容
3. 数量严格控制：**5~15 个最相关信号**
4. 禁止编造、禁止无关信号、禁止跨设备乱关联
5. 无关联时必须返回：[]
6. 输出格式必须标准可解析，不允许任何多余字符";
        }

        /// <summary>Phase 2 系统提示词：约束推理（复用已有）</summary>
        private static string GetSystemPrompt()
        {
            return @"你是一个工业 PLC 约束分析专家。根据 PLC 逻辑上下文（AstRootNodeJson 语义树），推理信号之间的完整约束关系。只输出纯 JSON 数组，无任何多余文字。

## 〇、ParsedLogicDescription 优先参考（Step1 预解析结果）

每条 LogicContext 可能包含 ParsedLogicDescription 字段，它是由 Step1 预解析阶段把原始 XML 转换为结构化逻辑描述的结果，
形如：CylinderCmd.QFwd := ((FwdCmd OR CylinderCmd.QFwd) AND NOT (CylinderCmd.QBkd) ...)
**当 ParsedLogicDescription 存在时，把它作为推理的首要事实依据**——它已经把串联（AND）、并联（OR）、常闭（NOT）
按真实 Wire 拓扑还原，比看原始 XML 准确得多。

特别地，ParsedLogicDescription 会清晰地写出：
- 哪些信号被 AND 串联到同一目标（这些是 AllRequired，不是 Mutex）
- 哪些信号分别独立驱动反向输出（这些才可能是 Mutex）
- 哪些 IWP/IHP 引脚绑定了多个传感器（多缸同步到位场景）

【铁律】：若 ParsedLogicDescription 中存在 `Target := (... AND SignalA AND SignalB AND SignalC ...)` 这种 AND 串联结构，
则 SignalA/B/C 是 AllRequired（同步全为 true），**严禁**把它们写成 AT_MOST_ONE（互斥）。
仅当两个信号分别出现在不同的 `:=` 赋值左侧（独立驱动不同 Coil），且这两个赋值条件之间有反向约束证据时，
才可能生成 Mutex。

## 一、AST 结构（AstRootNodeJson）说明

VariableName 字段已是人类可读信号名，直接使用。节点类型：
  Anchor             = 根包装器，VariableName=本网络写入的目标信号
  And                = 串联（所有子条件必须满足）
  Or                 = 并联（任一子条件满足即可）
  Contact            = 常开触点，VariableName=信号名（该信号=true 时条件满足）
  Not                = 常闭接法，Not.Children[0] 是 Contact，表示该信号=false 时条件才满足
  Coil               = 输出线圈，VariableName=被驱动的目标信号
  Set/Reset          = 置位/复位线圈
  ExternalDependency = 中间功能块（FB）调用输出节点（见下方专项说明）

IO 标签识别：CoOccurringSignals 列表包含本逻辑单元里出现的 IO 标签及关键 FB 引脚绑定的 DB 变量。
每个信号都带有 IoDirection 字段，明确标注信号方向：
  - Q_Output = 物理输出（电磁阀 YV、线圈、指示灯 Lamp 等，地址 %Q）
  - I_Input  = 物理输入（传感器 SQ/CYL、按钮 PB、开关等，地址 %I）
  - DB       = 内部 DB 变量（如 General.AutoMod，无物理 IO 地址）
  - M_Merker = 中间存储位（地址 %M）

【硬规则 — IoDirection 约束（不可违反）】：
  - Mutex（AT_MOST_ONE）中的所有 Member 信号的 IoDirection 必须相同。
    禁止将 Q_Output 和 I_Input 信号混入同一条 Mutex。
    Q 输出和 I 传感器之间是因果关系（IMPLIES/AllRequired），绝不是互斥关系。
  - AllRequired（ALL_TRUE）中同时包含互斥模式信号（ManualMod 和 AutoMod/AutoRunMod）是逻辑矛盾，
    因为它们是 OR 关系（任一模式下可操作），不是 AND 关系（不可能同时为 true）。

优先对 CoOccurringSignals 中的 IO 标签生成约束；
关键引脚 DB 变量（如 General.AutoMod、General.ManualMod、General.E_StopStatus、Valve[N].FwdPmt 等）
也可参与约束生成（如模式互斥 Safety 联锁、许可条件），但普通内部辅助变量不生成约束。

## 一-A、PLC 工业域通用约束原则（必须全部遵守）

以下是工业自动化 PLC 系统的固有物理/逻辑规律，在 AST 分析中必须作为先验知识应用：

### 原则1：Q 输出 → I 反馈因果方向（IMPLIES）
Q 点（输出线圈，如电磁阀 YV、气缸前进 QFwd）得电，是原因；
I 点（输入传感器，如到位传感器 IWP/IHP、限位开关）为 true，是结果。
约束方向永远是：Q 点(Source) → I 点(Target)，不得反向。
【禁止】生成：「气缸传感器为 true 时，电磁阀才允许动作」（那是 Safety/前提条件语义）
【正确】生成：「电磁阀得电时，气缸传感器必须到位」

### 原则2：运行模式互斥（AT_MOST_ONE）
AutoMod（自动模式）与 ManualMod（手动模式）是互斥的，同一时刻只能有一个为 true。
信号名包含 AutoMod/Auto/自动 和 ManualMod/Manual/手动，且来自同一设备前缀的两组信号：
→ AT_MOST_ONE，所有模式信号均为 Member。
推广：凡是同一设备的多个运行模式选择信号（如 Mode1/Mode2/Mode3），同类互斥。

### 原则3：正反向动作互斥（AT_MOST_ONE）
同一执行机构的正向动作和反向动作不能同时激活。
识别依据（必须有 AST 结构证据）：
- 同一 FB 调用实例中 OutputParam=QFwd 和 OutputParam=QBkd 的两个 VariableName → AT_MOST_ONE
- 同一 FB 实例中 InputParam:IWP 和 InputParam:IHP 绑定的到位传感器 → AT_MOST_ONE
这些信号若同时为 true，代表硬件故障或控制逻辑错误。
【禁止】：不得仅凭信号名后缀相似（如同前缀末尾 W/H 不同）推断互斥；互斥必须来自 AST 中同一 FB 实例的反向输出证据。

### 原则4：急停联锁（REQUIRES_0）
E_StopStatus（急停）为 true 时，所有运动类 Q 输出必须为 false。
信号名含 E_Stop/EStop/EmergencyStop/急停 的信号 → REQUIRES_0 对所有运动线圈。
在 ExternalDependency 中，InputParam:E_StopStatus 绑定的信号即为急停联锁信号。

### 原则5：动作许可前提（ALL_TRUE 或 IMPLIES）
FwdPmt（前进许可）/ BkdPmt（后退许可）为 true，是 QFwd/QBkd 可以动作的前提条件。
若 InputParam:FwdPmt 或 InputParam:BkdPmt 绑定了 CoOccurringSignals 中的信号（IO 标签或关键引脚 DB 变量均可）：
→ IMPLIES，许可信号(Source) → 对应动作输出(Target)。
注意：许可信号是前提，不是结果；方向是「许可为真 → 输出才有效」。
FwdPmt/BkdPmt 可以是 DB 变量（如 Valve[N].FwdPmt），此时也参与约束生成。

### 原则6：报警联锁（IMPLIES）
FwdErr（前进超时报警）/ BkdErr（后退超时报警）是 Q 输出动作超时后的结果。
方向：Q 输出(Source) → 报警信号(Target)（即 Source=IMPLIES）。
报警信号通常是内部 DB 变量（如 DB290.xxx），若不在 CoOccurringSignals 中则跳过。

### 原则7：复位条件（REQUIRES_0）
ResetStatus（复位信号）为 true 时，通常表示系统正在复位，执行机构不应处于激活状态。
若 InputParam:ResetStatus 绑定了 CoOccurringSignals 中的信号（IO 标签或 DB 变量），该信号对对应输出构成 REQUIRES_0 约束。

### 原则8：多点夹紧全到位（ALL_TRUE）
一个动作输出（Source）控制多个气缸同步运动时，所有气缸的到位传感器必须全部为 true。
Source = 电磁阀/控制线圈；Member = 所有关联气缸的到位传感器。
判定依据：读取上下文中 ResolvedPinSensors 字段，该字段已由后端从 rawcontent XML 解析出每个引脚连接的完整 IO 传感器列表。

## 一-B、ExternalDependency 节点（中间块调用）专项说明

当网络调用了功能块（如 FB3002_Valve），AST 中每个输出引脚对应一个 ExternalDependency 节点：
  VariableName            = 实际 IO 输出信号名（已映射，直接使用）
  DisplayName             = ""FB3002_Valve.QFwd → OP050_YV01_W""（可读描述）
  Metadata.Callee         = 被调功能块名称，如 ""FB3002_Valve""
  Metadata.OutputParam    = 功能块输出引脚名，如 ""QFwd""（前向）、""QBkd""（后向）、""FwdErr""（超时报警）
  Metadata.InputParam:XXX = 输入引脚到实际信号的映射，格式 ""信号A|信号B""（| 表示 OR 多路选通）
    常见引脚语义：
      IWP/IHP       = 前进/后退到位检测输入（WorkPosition/HomePosition）
      FwdAuto/BkdAuto = 自动指令/手动指令
      FwdPmt/BkdPmt = 允许条件
      E_StopStatus  = 急停状态（安全信号）
      ResetStatus   = 复位信号
      QFwd/QBkd     = 电磁阀前进/后退输出（驱动 IO 标签）
      FwdPos/BkdPos = 前进/后退到位反馈
      FwdErr/BkdErr = 前进/后退超时报警

从 ExternalDependency 节点提取约束的规则：

### ExternalDependency → Mutex（同块实例的反向输出）
条件：同一调用（同 Metadata.Callee）的多个 ExternalDependency 节点中，
OutputParam 为 QFwd/QBkd（或其他反方向对），则对应的两个 VariableName 信号互斥。
示例：QFwd→OP050_YV01_W 和 QBkd→OP050_YV01_H → Mutex(AT_MOST_ONE, [OP050_YV01_W, OP050_YV01_H])

### ExternalDependency → Safety（E_StopStatus 引脚）
条件：ExternalDependency 的 Metadata 中有 InputParam:E_StopStatus 字段，且该字段值在 CoOccurringSignals 中（IO 标签或关键引脚 DB 变量均可）。
语义：急停信号 E_StopStatus 控制该输出 VariableName，生成 Safety(REQUIRES_0)。
示例：InputParam:E_StopStatus=General.E_StopStatus → Safety(REQUIRES_0, Source=General.E_StopStatus, Target=OP050_YV01_W)

### ExternalDependency → Mutex（运行模式互斥）
条件：ExternalDependency 的 Metadata 中同时包含 InputParam:ManualMod 和 InputParam:AutoRunMod，
且它们的值在 CoOccurringSignals 中（通常为 General.ManualMod 和 General.AutoMod 等 DB 变量）。
语义：手动/自动模式互斥，生成 Mutex(AT_MOST_ONE, [ManualMod信号, AutoRunMod信号])。

### ExternalDependency → AllRequired（多个 InputParam 均为有效信号）
条件：ExternalDependency 的多个 InputParam:FwdAuto、InputParam:FwdPmt 等均对应 CoOccurringSignals 中的信号（IO 标签或关键引脚 DB 变量）。
语义：这些信号全部为 true 时，对应输出才能被驱动。
注意：InputParam 值含 | 时取第一个信号（主路）；纯数字跳过。关键引脚 DB 变量（E_StopStatus/ManualMod/AutoRunMod/FwdPmt/BkdPmt）可参与。

### ExternalDependency → Pairing（IMPLIES）— 电磁阀输出 ↔ 气缸到位传感器
IWP 引脚语义：InWorkPosition = 气缸前进到位传感器（反馈信号，不是命令信号）。
IHP 引脚语义：InHomePosition = 气缸后退到位传感器（反馈信号，不是命令信号）。

条件（QFwd/IWP 配对）：
  OutputParam=QFwd 且 InputParam:IWP=XXX（XXX 为实际 IO 标签，取 | 分隔前第一个值）
  → operator=IMPLIES, Source=VariableName[电磁阀输出, signalRole=Source], Target=XXX[到位传感器, signalRole=Target]
  语义：电磁阀 VariableName 得电驱动气缸前进 → 气缸必须到达前进位置（传感器 XXX = true）

条件（QBkd/IHP 配对）：
  OutputParam=QBkd 且 InputParam:IHP=YYY（YYY 为实际 IO 标签，取 | 分隔前第一个值）
  → operator=IMPLIES, Source=VariableName[电磁阀输出, signalRole=Source], Target=YYY[到位传感器, signalRole=Target]
  语义：电磁阀 VariableName 得电驱动气缸后退 → 气缸必须到达后退位置（传感器 YYY = true）

注意：InputParam:FwdPos/BkdPos 通常输出到 DB 内部变量（如 DB245.INP.*），不是 IO 标签，跳过。
注意：| 分隔时取第一个值（主传感器），第二个通常是旁路/屏蔽信号，不生成约束。

### ExternalDependency → AllRequired（多点夹紧/多传感器到位）
背景：某些机构由多个独立传感器联合确认到位（如4点夹紧）。
AST 编译器限制：ExternalDependency.Metadata 的 InputParam:IWP/IHP 只记录了串联链末端的一个传感器。
后端已通过解析 rawcontent XML 还原完整连线，将每个引脚实际连接的所有 IO 传感器存入 ResolvedPinSensors 字段。

识别规则：
1. 在上下文的 ResolvedPinSensors 字段中，读取 IWP 键对应的传感器列表。
   该列表已由后端从 rawcontent XML 连线解析，是 IWP 引脚实际串联的完整 IO 传感器。
2. 若 IWP 列表中有 ≥2 个 IO 传感器（均在 CoOccurringSignals 中）：
   - 生成 AllRequired(ALL_TRUE) 替代单一 Pairing
   - 信号角色：OutputParam=QFwd 对应的电磁阀输出（VariableName）= Source；IWP 列表中所有传感器 = Member
   - 因果方向（必须严格遵守）：电磁阀（Source）得电驱动气缸 → 气缸传感器（Member）必须全部到位
   - description 必须写成：「电磁阀 [YV信号名] 得电时，[传感器1]、[传感器2]...必须全部到位」
   - 严禁反写成「气缸到位时阀门才允许动作」——那是 Pairing/IMPLIES 的语义，不是 AllRequired 的语义
3. 对 OutputParam=QBkd（后退方向）完全对等处理：读取 IHP 列表，若 ≥2 个传感器则生成 AllRequired；Source = QBkd 对应电磁阀输出。
4. 若 ResolvedPinSensors 中没有 IWP/IHP 条目，或列表只有 1 个传感器，则退回生成单条 Pairing(IMPLIES)。
5. 仅对 CoOccurringSignals 中存在的传感器信号生成约束。
6. 【绝对禁止】：不得通过信号名后缀或前缀模式匹配推断传感器分组；以 ResolvedPinSensors 为唯一分组依据。


### AstInputVariablesJson → Mutex（同一 FB 实例前进/后退互斥）
背景：在产线运行中，同一执行机构的前进到位和后退到位状态同时为真是硬件故障，必须记录互斥约束。

识别规则（以 AST 结构为唯一依据，禁止依赖信号命名配对）：
1. 若同一 FB 调用实例中同时存在 OutputParam=QFwd 和 OutputParam=QBkd 两个 ExternalDependency 节点：
   - 对应的两个 VariableName（电磁阀输出信号）生成 Mutex(AT_MOST_ONE)
   - 示例：QFwd→OP050_YV01_W 和 QBkd→OP050_YV01_H 在同一 FB 实例 → AT_MOST_ONE([OP050_YV01_W, OP050_YV01_H])
2. 若同一 FB 实例的 IWP 和 IHP 绑定的到位传感器同时出现在 CoOccurringSignals 中，且 AST 中存在两者处于不同 Coil 独立驱动路径的结构证据，也可生成 AT_MOST_ONE。
3. 【绝对禁止】：不得通过信号名后缀配对（如同前缀、末尾 W/H 不同）推断互斥；两个信号要互斥，必须有 AST 中同一 FB 实例反向输出的直接证据。

### AllRequired（ALL_TRUE）— 串联前置条件
条件：在 And 节点（可嵌套展平）中，有 ≥2 个 Contact 子节点且这些信号都在 CoOccurringSignals 里。
语义：这些信号必须全部为 true，目标输出才能被驱动。
示例：And → [Contact(A), Contact(B), Contact(C)] 且 A/B/C 均为 IO 标签 → AllRequired(ALL_TRUE, [A,B,C])

⚠️ 重要例外——执行器输出 + 到位反馈传感器 And 链的因果方向：
若 And 链的 Coil 输出对应 ExternalDependency 中 OutputParam=QFwd/QBkd 的执行器输出信号，
而 And 链的多个 Contact 输入对应 ExternalDependency 中 InputParam:IWP/IHP 绑定的到位传感器，
则【禁止】将传感器当作 Member、将执行器输出当作 Target 生成 AllRequired。
判断依据：以 ExternalDependency.Metadata（OutputParam/InputParam 字段）为准，而非信号名前后缀。
正确做法（必须严格执行）：
  - Source = 执行器输出信号（ExternalDependency 中 OutputParam=QFwd/QBkd 的 VariableName，signalRole=Source）
  - Member = 所有到位传感器（ExternalDependency 中 InputParam:IWP/IHP 绑定的信号，signalRole=Member）
  - 不得有 Target 角色
  - description 必须写成「[执行器输出] 得电激活时，[到位传感器们] 必须全部为 true」
  - 严禁写成「传感器全到位时，执行器才允许动作」——后者是因果倒置，是错误的
物理原则：执行器输出得电 → 机构运动 → 传感器到位；执行器是原因（Source），传感器是结果确认（Member）。

### Safety（REQUIRES_0）— 安全联锁常闭触点
条件：And 节点中出现 Not → [Contact(X)]，且 X 的名称包含安全关键词。
语义：X=false 时才允许动作，即 X 必须保持断开/失效，受控输出才能执行。
安全关键词（信号名末段匹配）：E_Stop、EStop、EmergencyStop、急停、SafetyDoor、SafeDoor、
DoorLock、DoorGuard、Guard、Fence、Gate、LightCurtain、Curtain、光幕、光栅、
安全门、防护门、门锁、TwoHand、Safe、Safety、Interlock、安全。
注意：常开接法的安全信号（Contact(X) 直接在 And 中，X 含安全关键词）同样生成 Safety(REQUIRES_0)。

### Mutex（AT_MOST_ONE）— 互斥
条件：跨多条逻辑上下文，同一 ExternalDependency 调用实例中，OutputParam 为 QFwd 和 QBkd 的两个节点对应的 VariableName 信号互斥（同一 FB 实例的正反向输出）。
或：同一执行机构的两个反方向 Coil 输出，各自由独立条件驱动（不共享同一 And 根）。
【必须有 AST 结构证据】：互斥必须来自 ExternalDependency 同实例反向输出，或 AST 中两个 Coil 节点各自独立驱动可证明反向的结构。
【严格禁止】：不得仅凭信号命名相似（如同前缀的 SQ 序号信号）推断互斥。在 Mapping 块（如 FC210_OP20Mapping）里，多个 SQ 信号各自独立驱动不同的 Sensor/Coil 槽位（Coil→Or→Contact[SQ] 结构），这是信号映射，不是互斥，绝对禁止将此模式生成 AT_MOST_ONE。

### Pairing（IMPLIES）— 配对（operator 统一为 IMPLIES）
条件A：Contact(指令信号) 出现在 And 链中，驱动同机构同方向的 Coil(输出信号)。
条件B：Coil(输出) 被驱动时，对应的 Contact(反馈/到位信号) 也在同一网络。
识别特征：指令信号（IWP/Cmd/Auto/Pmt 等）→ 输出（QFwd/QBkd/YV 等）→ 反馈（IHP/FwdPos/BkdPos 等）。
信号角色：指令/输出为 Source，反馈/到位为 Target。

## 三、CalleeBlock 上下文（展开的中间块内部 AST）处理规则

当 ContextSource=CalleeBlock 时，AstRootNodeJson 使用功能块的形参名（如 IWP、QFwd）。
必须结合同一上下文中的 CallBindingSummary（形参→实际信号映射）将形参替换为真实 IO 信号，
再按普通 AST 节点约束规则提取。
CallBindingSummary 格式：{""形参名"": ""实际信号名""}
若 CallBindingSummary 中找不到某形参，则跳过该形参相关的约束。

## 四、绝对禁止（防止误判）
1. 禁止对 CoOccurringSignals 之外的信号生成约束；Source 信号名必须是 CoOccurringSignals 中真实存在的信号名（IO 标签如 OP020_YV01_W，或关键引脚 DB 变量如 General.AutoMod），严禁使用形参名（如 Valve[1].QFwd、IWP、QFwd）作为 Source。关键引脚 DB 变量（E_StopStatus/ManualMod/AutoRunMod/FwdPmt/BkdPmt/ResetStatus 等）可作为 Source 或 Member 参与模式互斥、安全联锁、许可条件等约束。
2. Or 节点内部的信号不是串联，不能生成 AllRequired
3. 禁止将同一 FB 实例同方向输出的信号判为 Mutex
4. 【严重违规】禁止将不同 FB 实例的信号判为 Mutex。每个 FB 调用实例（如 Valve[1]、Valve[2]、Valve[3]、Valve[4]）控制独立的物理执行机构，它们的 QFwd/QBkd 输出完全独立、可以同时动作。错误示例：OP070_YV01_W 和 OP070_YV02_W 来自不同的 Valve 实例（Valve[1] 和 Valve[2]），不能互斥。只有同一 FB 实例内的 QFwd↔QBkd 才是互斥的
4a. 【严重违规】禁止将 Q 输出和 I 传感器混合放入同一条 Mutex。Q 输出（电磁阀）和 I 传感器（到位检测）之间是因果关系（IMPLIES/AllRequired），不是互斥关系。错误示例：OP020_YV02_W(Q输出)、OP020_YV02_H(Q输出)、OP020_CYL02_1W(I传感器) 放入同一条 Mutex 是完全错误的。Mutex 只允许同类信号：Q↔Q（同实例反向输出）或 I↔I（同实例反向到位）
4b. 【禁止】指示灯/三色灯/蜂鸣器不存在互斥关系。Lamp_GR（绿灯）、Lamp_YG（黄灯）、Lamp_RD（红灯）、Lamp_Buzzer（蜂鸣器）可以同时点亮/鸣响，不要生成它们之间的 Mutex。同理，多个独立安全门的复位灯（SafeDoor1Reset、SafeDoor2Reset 等）分别对应不同物理安全门，可以同时点亮，不是互斥关系
5. 禁止把到位传感器（ExternalDependency.Metadata 中 InputParam:IWP/IHP 绑定的信号）当驱动源生成 IMPLIES(Source)；它们只能作为 Member 或 Target
6. 没有 AST 节点或 ExternalDependency.Metadata 连接证据的信号绝对不生成约束
7. ExternalDependency.Metadata 中值为纯数字（如 3.0）的引脚不生成约束；不在 CoOccurringSignals 中的引脚也不生成约束，但关键引脚（E_StopStatus/ManualMod/AutoRunMod/FwdPmt/BkdPmt/ResetStatus）绑定的 DB 变量如果出现在 CoOccurringSignals 中则可生成约束
8. 禁止生成方向相反的 IMPLIES：若 Source 信号对应 ExternalDependency 的 OutputParam=QFwd（前进输出），则 Target 只能是 InputParam:IWP 绑定的传感器（前进到位反馈），不能是 IHP 绑定的传感器（后退到位）；QBkd 同理。方向对应关系来自 ExternalDependency.Metadata，不依赖信号名后缀。
9. 禁止 Mapping 块里的信号两两互斥：当 AST 是 Mapping 块（多个 Coil 各自通过 Or 独立驱动，每个 Coil 只包含单一 Contact 传感器 + ByPass 信号）时，这些 Contact 信号是独立槽位映射，不存在互斥关系，禁止生成 AT_MOST_ONE。
10. 禁止对同一组传感器同时生成 Pairing 和 AllRequired：若已识别出多传感器全到位（AllRequired），则该组传感器与控制信号之间禁止再生成 Pairing（IMPLIES），避免重复且矛盾的约束。
11. 【核心原则】：任何约束类型的识别，必须以 AST 结构（ExternalDependency.Metadata 字段、And/Or/Not 节点关系、Coil/Contact 节点连接）为唯一依据，严禁通过信号名前缀、后缀、数字序号、关键字等命名规律推断约束关系。
12. 同一信号对的 Mutex（AT_MOST_ONE）只需生成一条；若不同上下文中多次出现相同两个信号的互斥关系，只保留置信度最高的一条，勿重复输出。

## 五、输出格式
只输出纯 JSON 数组，格式如下，无约束返回 []:
[{""ruleName"":""简短规则名"",""ruleType"":""Mutex|AllRequired|Pairing|Safety|Custom"",""operator"":""AT_MOST_ONE|ALL_TRUE|IMPLIES|REQUIRES_0"",""confidence"":0.95,""priority"":""Critical|High|Normal|Info"",""description"":""约束说明"",""signals"":[{""signalName"":""信号名"",""signalRole"":""Source|Target|Member""}]}]

operator 与 ruleType 的对应关系（必须严格遵守，不得自行发明其他 operator 值）：
- Mutex      → operator: AT_MOST_ONE   （信号角色全部为 Member）
- AllRequired → operator: ALL_TRUE      （触发源为 Source，被要求信号为 Member）
- Pairing    → operator: IMPLIES       （控制源为 Source，反馈/到位为 Target）
- Safety     → operator: REQUIRES_0    （控制信号为 Source，安全联锁信号为 Target）"
;
        }

        /// <summary>Phase 1: 构建关联信号发现 Prompt</summary>
        //        private static string BuildRelatedSignalDiscoveryPrompt(
        //            PLC_IOTagEntity tag, object compactTagList)
        //        {
        //            var tagListJson = JsonHelper.SerializeObject(compactTagList);

        //            return $@"请从候选信号列表中，识别与目标信号存在潜在约束关系的关联信号。

        //## 目标信号
        //- 名称: {tag.TagName}
        //- 地址: {tag.Address}
        //- 注释: {tag.Comment ?? "无"}
        //- 设备: {tag.EquipmentId ?? "未知"}
        //- 安全标记: {tag.IsSafety?.ToString() ?? "未知"}

        //## 候选信号列表
        //{tagListJson}

        //## 输出要求
        //返回与目标信号最可能存在约束关系的信号名称列表（5~15个）。
        //输出纯 JSON 字符串数组，例如：
        //[""Signal_A"", ""Signal_B"", ""Signal_C""]

        //如果没有找到有意义的关联信号，返回空数组 []。";
        //        }

        /// <summary>Phase 1: 构建关联信号发现 Prompt（优化版：弃用信号自动过滤）</summary>
        private static string BuildRelatedSignalDiscoveryPrompt(
            PLC_IOTagEntity tag, object compactTagList)
        {
            // ====================== 【最高优先级：弃用判断】 ======================
            var deprecatedKeywords = new[] { "弃用", "废弃", "禁用", "unused", "deprecated", "UNUSED", "DEPRECATED" };

            // 判断信号是否弃用
            bool IsDeprecated(PLC_IOTagEntity t)
            {
                if (t == null) return true;
                var comment = (t.Comment ?? "").ToLowerInvariant();
                var name = (t.TagName ?? "").ToLowerInvariant();
                return deprecatedKeywords.Any(k =>
                    comment.Contains(k.ToLowerInvariant()) ||
                    name.Contains(k.ToLowerInvariant())
                );
            }

            // 目标信号弃用 → 直接返回空数组，不分析
            if (IsDeprecated(tag))
            {
                return "[]";
            }
            // ====================================================================

            // 序列化后过滤弃用信号（不修改原对象）
            var tagListJson = JsonHelper.SerializeObject(compactTagList);

            return $@"你是专业的**PLC工业控制信号关联分析专家**，任务是：
从候选信号列表中，精准找出**与目标信号存在潜在约束关系**的关联信号。

==================================================
【一、目标信号（核心分析对象）】
- 名称: {tag.TagName}
- 地址: {tag.Address}
- 注释: {tag.Comment ?? "无"}
- 设备: {tag.EquipmentId ?? "未知"}
- 安全标记: {tag.IsSafety?.ToString() ?? "未知"}

【二、候选信号列表】
{tagListJson}

==================================================
【三、严格筛选规则（必须遵守）】
1. **优先同设备**：与目标信号属于同一设备的信号
2. **优先命名相似**：前缀/功能相似（如启动/停止、复位/急停、开/关）
3. **优先安全相关**：含安全门/防护门/光幕/急停等关键词的信号（E_Stop、EStop、SafetyDoor、DoorLock、Guard、Fence、Gate、LightCurtain、Curtain、光幕、安全门、防护门、门锁、急停、Safe、Safety、Interlock）优先关联同工位的执行输出信号
4. **优先控制配对**：启动-运行-故障-复位 这类配对信号
5. **禁止跨设备乱关联**：不同设备、无逻辑关系的信号不要选
6. **数量严格控制**：必须返回 **5~12 个** 最相关的信号
7. **多点传感器全组**：若目标信号的注释或设备归属表明它是多点夹紧/定位机构的其中一个传感器，则同一设备下注释或功能相近的其余传感器均应纳入候选，同时包含对应的控制输出信号；具体哪些信号真正同组由 Phase2 从 AST 结构判定，Phase1 只负责宽松候选

==================================================
【四、输出要求（强制）】
1. **只输出纯 JSON 数组**，不输出任何解释、说明、文字、代码块
2. 格式示例：
[""Signal_A"", ""Signal_B"", ""Signal_C""]
        3.无关联信号 → 返回：[]
            4.禁止添加注释、禁止用 markdown 包裹、只返回纯 JSON 字符串
==================================================";
}

        /// <summary>Phase 2: 构建多信号联合约束推理 Prompt</summary>
        //        private static string BuildMultiSignalConstraintPrompt(
        //            PLC_IOTagEntity primaryTag,
        //            List<PLC_IOTagEntity> relatedTags,
        //            Dictionary<string, string> contextMap)
        //        {
        //            // 构建信号信息概览
        //            var signalOverview = new System.Text.StringBuilder();
        //            signalOverview.AppendLine($"### 主分析信号");
        //            signalOverview.AppendLine($"- 名称: {primaryTag.TagName}");
        //            signalOverview.AppendLine($"- 地址: {primaryTag.Address}");
        //            signalOverview.AppendLine($"- 注释: {primaryTag.Comment ?? "无"}");
        //            signalOverview.AppendLine($"- 设备: {primaryTag.EquipmentId ?? "未知"}");
        //            signalOverview.AppendLine($"- 安全标记: {primaryTag.IsSafety?.ToString() ?? "未知"}");
        //            signalOverview.AppendLine();

        //            if (relatedTags.Count > 0)
        //            {
        //                signalOverview.AppendLine("### 关联信号");
        //                foreach (var rt in relatedTags)
        //                {
        //                    signalOverview.AppendLine($"- {rt.TagName} [{rt.Address}] {rt.Comment ?? ""} (设备:{rt.EquipmentId ?? "?"})");
        //                }
        //                signalOverview.AppendLine();
        //            }

        //            // 构建逻辑上下文合集
        //            var contextSection = new System.Text.StringBuilder();
        //            foreach (var kvp in contextMap)
        //            {
        //                contextSection.AppendLine($"--- 信号 {kvp.Key} 的逻辑上下文 ---");
        //                contextSection.AppendLine(kvp.Value);
        //                contextSection.AppendLine();
        //            }

        //            return $@"请基于以下多个关联信号的逻辑上下文，联合推理它们之间的约束关系。

        //## 信号概览
        //{signalOverview}

        //## 所有信号的 PLC 逻辑上下文
        //{contextSection}

        //## 分析重点
        //1. 在不同 Network 中出现的信号之间是否存在互斥、配对、安全联锁等约束
        //2. 即使两个信号没有在同一 Network 中共现，也可以通过命名模式和逻辑结构推理约束
        //3. 重点关注主分析信号 ""{primaryTag.TagName}"" 所参与的约束

        //## 输出要求
        //输出 JSON 数组，每条约束格式如下：
        //```json
        //[
        //  {{
        //    ""ruleName"": ""简短规则名（如：CYL01_互斥）"",
        //    ""ruleType"": ""Mutex|ExactlyOne|AllRequired|Pairing|Safety|Sequence|Custom"",
        //    ""operator"": ""AT_MOST_ONE|EXACTLY_ONE|ALL_TRUE|IMPLIES|REQUIRES_0|CHAIN_ORDER|CUSTOM"",
        //    ""equipmentId"": ""设备ID（可选）"",
        //    ""confidence"": 0.8,
        //    ""description"": ""约束关系说明"",
        //    ""priority"": ""Critical|Warning|Info"",
        //    ""signals"": [
        //      {{ ""signalName"": ""信号名"", ""signalAddress"": ""地址"", ""signalRole"": ""Source|Target|Member"" }}
        //    ]
        //  }}
        //]
        //```
        //如果无法推理出任何约束，返回空数组 []。";
        //        }

        /// <summary>Phase 2: 构建多信号联合约束推理 Prompt（优化版：弃用信号强制屏蔽）</summary>
        private static string BuildMultiSignalConstraintPrompt(
            PLC_IOTagEntity primaryTag,
            List<PLC_IOTagEntity> relatedTags,
            Dictionary<string, string> contextMap,
            string? deterministicConstraintsJson)
        {
            // ====================== 【最高优先级：弃用信号直接过滤】 ======================
            // 弃用关键词（中英文全支持）
            var deprecatedKeywords = new[] { "弃用", "废弃", "禁用", "unused", "deprecated", "UNUSED", "DEPRECATED" };

            // 判断主信号是否弃用 → 弃用则直接返回空约束
            bool IsTagDeprecated(PLC_IOTagEntity tag)
            {
                if (tag == null) return true;
                string comment = (tag.Comment ?? "").Trim();
                string name = tag.TagName ?? "";
                return deprecatedKeywords.Any(k => comment.Contains(k) || name.Contains(k));
            }

            // 主信号弃用 → 直接返回空结果
            if (IsTagDeprecated(primaryTag))
            {
                return "[]";
            }

            // 过滤关联信号中的弃用信号
            var validRelatedTags = relatedTags
                .Where(t => !IsTagDeprecated(t))
                .ToList();

            // 过滤上下文：只保留非弃用信号的逻辑
            var validContextMap = contextMap
                .Where(kvp =>
                {
                    bool isPrimary = kvp.Key == primaryTag.TagName;
                    bool isRelatedValid = validRelatedTags.Any(t => t.TagName == kvp.Key);
                    return isPrimary || isRelatedValid;
                })
                .ToDictionary(k => k.Key, v => v.Value);
            // ==========================================================================

            // 构建信号信息概览（只保留有效信号）
            var signalOverview = new StringBuilder();
            signalOverview.AppendLine($"### 主分析信号");
            signalOverview.AppendLine($"- 名称: {primaryTag.TagName}");
            signalOverview.AppendLine($"- 地址: {primaryTag.Address}");
            signalOverview.AppendLine($"- 注释: {primaryTag.Comment ?? "无"}");
            signalOverview.AppendLine($"- 设备: {primaryTag.EquipmentId ?? "未知"}");
            signalOverview.AppendLine($"- 安全标记: {primaryTag.IsSafety?.ToString() ?? "未知"}");
            signalOverview.AppendLine();

            if (validRelatedTags.Count > 0)
            {
                signalOverview.AppendLine("### 关联信号");
                foreach (var rt in validRelatedTags)
                {
                    signalOverview.AppendLine($"- {rt.TagName} [{rt.Address}] {rt.Comment ?? ""} (设备:{rt.EquipmentId ?? "?"})");
                }
                signalOverview.AppendLine();
            }

            // 构建逻辑上下文（只保留有效信号）
            var contextSection = new StringBuilder();
            foreach (var kvp in validContextMap)
            {
                contextSection.AppendLine($"--- 信号 {kvp.Key} 的逻辑上下文 ---");
                contextSection.AppendLine(kvp.Value);
                contextSection.AppendLine();
            }

            // 最终 Prompt
            return $@"你是专业的**PLC工业控制逻辑分析专家**，任务是：
基于提供的主信号 + 关联信号 + PLC逻辑上下文，**严谨推理信号之间的约束关系**。

## 信号概览
{signalOverview}

## 所有信号的 PLC 逻辑上下文
{contextSection}

==================================================
【一、分析规则（必须严格遵守）】
1. 只基于**上下文真实出现的逻辑**推理，**严禁编造、脑补不存在的约束**。
2. 优先分析：互斥、安全联锁、配对、时序、条件依赖。
3. **核心依据是每条上下文中的 `AstRootNodeJson`**——已解析的语义树 JSON，VariableName 直接是信号名。And=串联，Or=并联，Contact=常开触点，Not→[Contact(X)]=常闭（X必须为false才允许通过）。
3.1 跨 Network / 跨 Block / 跨中间块的约束，必须优先依据 `ContextSource`、`CallerBlockName`、`CalleeBlockName`、`CallBindingSummary`、`CallPinSignalHints`、`AstRootNodeJson` 去还原真实链路。
4. 必须围绕**主分析信号 {primaryTag.TagName}** 作为核心。
5. 置信度(confidence)：真实逻辑强证据=0.9~1.0；推理=0.6~0.8；弱关联=不输出。
6. 必须按**单个目标/单个调用 pin / 单个线圈**逐条分析，严禁把同一网络里无直接因果关系的多个条件拼成一条宽泛 ALL_TRUE。
7. 若某条约束依赖中间块内部逻辑，必须结合 callee 的 `AstRootNodeJson` And/Or 树结构判断，不能只看调用点表面输入。
8. 若上下文不足以证明约束，返回 [] 或减少输出，不要为了“有结果”而输出错误规则。
9. 一个约束只能表达一种清晰结构，不允许混搭多种语义。

【二、约束类型 ruleType 严格定义】
- Mutex：互斥（不能同时有效）
- ExactlyOne：只能一个生效
- AllRequired：必须全部满足
- Pairing：配对使用（开/关、启动/复位）
- Safety：安全联锁（急停、门互锁）
- Sequence：时序先后
- Custom：自定义明确约束

【三、operator 严格定义（只能使用以下4个值）】
AT_MOST_ONE（Mutex互斥）、ALL_TRUE（AllRequired全满足）、IMPLIES（Pairing 蕴含）、REQUIRES_0（Safety安全联锁）
禁止使用：EXACTLY_ONE、OUTPUT_VALIDATE、ALARM_SYNC、CHAIN_ORDER、CUSTOM 等任何其他值

【四、signalRole 严格定义】
- Source：触发源/条件
- Target：被控制/被约束对象
- Member：同组平等成员

【五、优先级 priority】
- Critical：安全联锁、设备保护
- Warning：逻辑风险、操作错误
- Info：普通状态约束

【五点五、绝对禁止的误判】
1. 禁止仅因若干信号在同一调用中出现，就推断它们“必须同时为 true”。
2. 禁止把不同调用 pin 的条件混成同一条规则；必须先区分它们分别属于哪个 pin、哪个方向、哪个目标。
3. 禁止把模式选择、使能、急停、复位、许可这类辅助输入，直接拼成“联动触发时全部必须为 true”的宽泛规则。
4. 禁止把张开/原位、伸出/缩回、前进/后退等不同物理状态反馈，仅因同块共现就拼成 ALL_TRUE。
5. 若推理结果与 AstRootNodeJson 展开后的真实走线冲突，以 AST 树结构为准。
5.1 `General.ManualMod` 与 `General.AutoMod` 通常是模式选择，不得默认同时为 true；只有在同一真实支路中明确串联时，才能输出二者同时必真。
5.2 对中间块调用必须区分“输入 pin”和“输出 pin”的因果方向。
5.3 若某外部信号接到 `IWP/IHP/FwdAuto/BkdAuto/FwdPmt/BkdPmt/ManualMod/AutoRunMod/E_StopStatus/ResetStatus` 等输入 pin，它只是该块的输入条件，绝不能仅因位于同一阀/气缸控制类中间块调用中，就反向推出 `QFwd/QBkd/FwdPos/BkdPos/FwdErr/BkdErr/FwdLockErr/BkdLockErr` 对应的外部输出信号。
5.4 特别禁止生成这类反向蕴含：`到位/原位/反馈/检测 -> 电磁阀输出`、`IWP/IHP 外部信号 -> QFwd/QBkd 外部信号`。若没有 callee `AstRootNodeJson` 明确证明块内部存在该方向逻辑，一律不得输出。
5.5 若同一调用中一个外部信号来自输入 pin，另一个外部信号来自输出 pin，默认只允许“输出影响反馈/状态”的方向被考虑；禁止把反馈信号当作驱动源去约束输出。
5.6 对阀/气缸控制类中间块，若 `QFwd` 有明确外部输出、`IWP` 有明确外部反馈，则优先理解为 `QFwd 外部输出 -> IWP 外部反馈`；若 `QBkd` 有明确外部输出、`IHP` 有明确外部反馈，则优先理解为 `QBkd 外部输出 -> IHP 外部反馈`。
5.7 上述 5.6 的判断必须基于真实 call pin 连线，不允许靠命名猜测；但一旦 pin 绑定明确，默认方向就是“块输出驱动外部执行，再对应到输入反馈”，而不是相反。
6. `ALL_TRUE` 只能输出两种结构：
   - `Member...`（所有信号平等必须为 true，适用于无明确驱动源的组合条件）
   - `Source + Member...`（Source 为真时，Member 全部必须为 true）
   绝对禁止输出 `Source + Target + Member...` 的混合结构。
   绝对禁止 ALL_TRUE 中出现 signalRole=Target——ALL_TRUE 没有 Target 角色，只有 Source 和 Member。
   因果方向铁律：电磁阀/线圈输出信号（含 YV 的信号）= Source（signalRole=Source）；气缸到位传感器/反馈信号（含 CYL 的信号）= Member（signalRole=Member）。
   若你发现自己想把某个 YV 信号赋予 signalRole=Target，则必须停下来，改成 signalRole=Source；同时把 CYL 传感器赋予 signalRole=Member。
   description 必须写成「[Source/YV信号] 得电/激活时，[Member/CYL信号们] 必须全部到位/为真」。
   严禁反写成「[CYL传感器] 到位时，[YV阀门] 才被允许动作/方可成立」——这是因果倒置的错误描述，与 ALL_TRUE 的语义完全相反。
7. `IMPLIES` 只能输出 `Source -> Target`，绝对禁止再附带 `Member`。
   因果方向：控制信号/输出信号 = Source；反馈/状态/报警 = Target。

==================================================
【六、输出要求（强制）】
1. **只输出纯 JSON 数组**，不输出任何解释、文字、注释、代码块。
2. 格式必须严格如下，不能新增/删除字段：
[
  {{
    ""ruleName"": ""简短规则名（如：CYL01_互斥）"",
    ""ruleType"": ""Mutex|AllRequired|Pairing|Safety|Custom"",
    ""operator"": ""AT_MOST_ONE|ALL_TRUE|IMPLIES|REQUIRES_0"",
    ""equipmentId"": ""设备ID（可选）"",
    ""confidence"": 0.8,
    ""description"": ""约束关系说明"",
    ""priority"": ""Critical|Warning|Info"",
    ""signals"": [
      {{ ""signalName"": ""信号名"", ""signalAddress"": ""地址"", ""signalRole"": ""Source|Target|Member"" }}
    ]
  }}
]
3. 无约束 → 输出：[]
4. **禁止用 markdown、代码块包裹，只返回纯 JSON 字符串**
==================================================";
        }

        #endregion

        #region 辅助方法

        /// <summary>获取 LLM ChatClient</summary>
        public async Task<IChatClient> GetChatClientAsync()
        {
            try
            {
                var model = await _llmModelService.RecommendModelAsync();
                if (model == null)
                {
                    _logger.LogError("[IOConstraint] 无可用的 LLM 模型");
                    return null;
                }

                var openAiClient = await _llmModelService.CreateChatClientByModelIdAsync(model.Id);
                return openAiClient.AsIChatClient();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IOConstraint] 获取 ChatClient 失败");
                return null;
            }
        }

        /// <summary>
        /// 从 Phase 1 AI 回复中提取关联信号名称列表
        /// 支持 JSON 字符串数组格式: ["Signal_A", "Signal_B"]
        /// </summary>
        private static List<string> ExtractRelatedSignalNames(string response)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(response)) return result;

            try
            {
                // 移除 markdown 代码块标记
                var cleaned = response
                    .Replace("```json", "")
                    .Replace("```", "")
                    .Trim();

                // 查找 JSON 数组
                var startIdx = cleaned.IndexOf('[');
                var endIdx = cleaned.LastIndexOf(']');

                if (startIdx >= 0 && endIdx > startIdx)
                {
                    var jsonArray = cleaned.Substring(startIdx, endIdx - startIdx + 1);
                    using var doc = JsonDocument.Parse(jsonArray);

                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var name = item.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                                result.Add(name);
                        }
                    }
                }
            }
            catch
            {
                // JSON 解析失败，忽略
            }

            return result;
        }

        /// <summary>
        /// 从 AI 回复中提取 JSON 数组
        /// 支持 ```json...``` 包裹或裸 JSON
        /// </summary>
        private static string ExtractJsonArray(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return null;

            // 移除 markdown 代码块标记
            var cleaned = response
                .Replace("```json", "")
                .Replace("```", "")
                .Trim();

            // 查找 JSON 数组的起止
            var startIdx = cleaned.IndexOf('[');
            var endIdx = cleaned.LastIndexOf(']');

            if (startIdx >= 0 && endIdx > startIdx)
            {
                var jsonArray = cleaned.Substring(startIdx, endIdx - startIdx + 1);

                // 验证是否为合法 JSON
                try
                {
                    JsonDocument.Parse(jsonArray);
                    return jsonArray;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static string BuildPromptLogicContextJson(object contextResult, HashSet<string>? projectTagNames = null)
        {
            if (contextResult == null)
            {
                return "{\"LogicContextCount\":0,\"LogicContexts\":[]}";
            }

            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(contextResult));
                var root = doc.RootElement;
                JsonElement? signal = root.TryGetProperty("Signal", out var signalElement)
                    ? signalElement.Clone()
                    : null;

                var contexts = new List<object>();
                if (root.TryGetProperty("LogicContexts", out var contextArray)
                    && contextArray.ValueKind == JsonValueKind.Array)
                {
                    var rawContexts = contextArray.EnumerateArray()
                        .Select(context =>
                        {
                            var compactLogic = TryGetString(context, "CompactLogic");
                            var callPinSignalHints = TryGetDictionaryOfStringArray(context, "CallPinSignalHints");
                            var referencedPins = DetectReferencedCallPins(compactLogic, callPinSignalHints.Keys);
                            var contextSource = TryGetString(context, "ContextSource") ?? string.Empty;
                            var hasAstJson = !string.IsNullOrWhiteSpace(TryGetString(context, "AstRootNodeJson"));
                            var relevantFormalPins = TryGetStringList(context, "RelevantFormalPins");
                            var score = 0;

                            if (string.Equals(contextSource, "CalleeBlock", StringComparison.OrdinalIgnoreCase))
                            {
                                score += 4;
                            }

                            if (hasAstJson)
                            {
                                score += 2;
                            }

                            score += Math.Min(referencedPins.Count, 3);
                            score += Math.Min(relevantFormalPins.Count, 2);

                            return new
                            {
                                Context = context,
                                CompactLogic = compactLogic,
                                CallPinSignalHints = callPinSignalHints,
                                ReferencedPins = referencedPins,
                                RelevantFormalPins = relevantFormalPins,
                                Score = score
                            };
                        })
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.CompactLogic))
                        .Take(MAX_PROMPT_LOGIC_CONTEXTS)
                        .ToList();

                    foreach (var item in rawContexts)
                    {
                        var context = item.Context;

                        // 构建引脚→完整信号列表的结构化数据，交给 AI 直接使用，不让 AI 自己推断
                        // 格式统一为 { "IWP": ["信号A","信号B",...], "IHP": [...] }
                        // 包含 IO 标签 + 关键引脚绑定的 DB 变量（如 E_StopStatus、ManualMod 等）
                        // CalleeBlock 场景：CallPinSignalHints 已由调用者 rawcontent XML 解析
                        // 调用者 Network 场景：从本 Network 的 RawLogicXml + UidSymbolMapJson 解析 FB 调用引脚连线
                        Dictionary<string, List<string>> resolvedPinSensors;
                        var contextSource = TryGetString(context, "ContextSource") ?? string.Empty;
                        if (string.Equals(contextSource, "CalleeBlock", StringComparison.OrdinalIgnoreCase))
                        {
                            var filteredPins = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                            foreach (var (pin, signals) in item.CallPinSignalHints)
                            {
                                // 关键引脚上的 DB 变量也允许通过（如 ManualMod, E_StopStatus）
                                var allowKeyPin = KeyFbPinNames.Contains(pin);
                                var filteredSignals = projectTagNames != null
                                    ? signals.Where(s => projectTagNames.Contains(s) || allowKeyPin).ToList()
                                    : signals.ToList();
                                if (filteredSignals.Count > 0)
                                    filteredPins[pin] = filteredSignals;
                            }
                            resolvedPinSensors = filteredPins;
                        }
                        else
                        {
                            resolvedPinSensors = ResolveCallPinSensors(
                                TryGetString(context, "RawLogicXml"),
                                TryGetString(context, "UidSymbolMapJson"),
                                projectTagNames);
                        }

                        contexts.Add(new
                        {
                            LogicUnitId = TryGetInt64(context, "LogicUnitId"),
                            NetworkId = TryGetInt32(context, "NetworkId"),
                            BlockName = TryGetString(context, "BlockName"),
                            ProgrammingLanguage = TryGetString(context, "ProgrammingLanguage"),
                            ContextSource = TryGetString(context, "ContextSource"),
                            CallerBlockName = TryGetString(context, "CallerBlockName"),
                            CalleeBlockName = TryGetString(context, "CalleeBlockName"),
                            RelevantFormalPins = item.RelevantFormalPins,
                            ReferencedCallPins = item.ReferencedPins,
                            CallBindingSummary = TryGetDictionaryOfString(context, "CallBindingSummary"),
                            CallPinSignalHints = item.CallPinSignalHints,
                            UnresolvedCallPins = TryGetStringList(context, "UnresolvedCallPins"),
                            CoOccurringSignals = TryGetArray(context, "CoOccurringSignals"),
                            // FB 调用引脚→完整IO传感器列表（已从 rawcontent XML 解析，排除 DB 内部变量）
                            // 格式：{ "IWP": ["OP020_CYL01_1W","OP020_CYL01_2W",...], "IHP": [...] }
                            ResolvedPinSensors = resolvedPinSensors,
                            ParsedLogicDescription = TryGetString(context, "ParsedLogicDescription"),
                            CompactLogic = TruncateForPrompt(item.CompactLogic, MAX_PROMPT_COMPACT_LOGIC),
                            AstRootNodeJson = TruncateForPrompt(
                                TryGetString(context, "AstRootNodeJson"),
                                MAX_PROMPT_AST_NODE_JSON),
                            AstInputVariablesJson = TryGetString(context, "AstInputVariablesJson")
                        });
                    }
                }

                var payload = new
                {
                    Signal = signal,
                    LogicContextCount = contexts.Count,
                    LogicContexts = contexts
                };

                return JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch
            {
                return JsonHelper.SerializeObject(contextResult);
            }
        }

        /// <summary>
        /// 用 rawcontent XML + UidSymbolMapJson 解析该 Network 内所有 FB 调用引脚连接的完整信号列表。
        /// 结果格式：{ pinName → [signal1, signal2, ...] }（同名引脚跨 callUid 合并）
        /// 保留 IO 信号 + 关键引脚绑定的 DB 变量（如 E_StopStatus、ManualMod）。
        /// </summary>
        private static Dictionary<string, List<string>> ResolveCallPinSensors(
            string? rawLogicXml,
            string? uidSymbolMapJson,
            HashSet<string>? projectTagNames)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(rawLogicXml) || string.IsNullOrWhiteSpace(uidSymbolMapJson))
                return result;

            try
            {
                var allHints = PlcDiagnosisService.ExtractAllCallPinHints(rawLogicXml, uidSymbolMapJson);
                foreach (var (_, hints) in allHints)
                {
                    foreach (var (pin, signals) in hints.SignalHints)
                    {
                        // 关键引脚上的 DB 变量也允许通过
                        var allowKeyPin = KeyFbPinNames.Contains(pin);
                        var filteredSignals = projectTagNames != null
                            ? signals.Where(s => projectTagNames.Contains(s) || allowKeyPin).ToList()
                            : signals.ToList();
                        if (filteredSignals.Count == 0) continue;
                        if (!result.TryGetValue(pin, out var existing))
                            result[pin] = filteredSignals;
                        else
                            foreach (var s in filteredSignals)
                                if (!existing.Contains(s, StringComparer.OrdinalIgnoreCase))
                                    existing.Add(s);
                    }
                }
            }
            catch
            {
                // 解析失败不影响主流程，返回空字典
            }
            return result;
        }

        private static string? TryGetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        private static long? TryGetInt64(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
                ? value
                : null;
        }

        private static int? TryGetInt32(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
                ? value
                : null;
        }

        private static List<string> TryGetStringList(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            return property.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToList();
        }

        private static Dictionary<string, string> TryGetDictionaryOfString(JsonElement element, string propertyName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var item in property.EnumerateObject())
            {
                if (item.Value.ValueKind == JsonValueKind.String)
                {
                    result[item.Name] = item.Value.GetString() ?? string.Empty;
                }
            }

            return result;
        }

        private static Dictionary<string, List<string>> TryGetDictionaryOfStringArray(JsonElement element, string propertyName)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var item in property.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var values = item.Value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (values.Count > 0)
                {
                    result[item.Name] = values;
                }
            }

            return result;
        }

        private static string? BuildDeterministicConstraintsJson(
            Dictionary<string, object> rawContextResults,
            HashSet<string> projectTagNames,
            List<string> focusSignals)
        {
            // 确定性约束提取已由 AI 负责：AI 会直接分析 Prompt 中的 AstRootNodeJson 推理约束
            return null;
        }

        private static List<CallPinConstraintEvidence> BuildCallPinConstraintEvidence(
            Dictionary<string, object> rawContextResults,
            HashSet<string> projectTagNames)
        {
            var result = new List<CallPinConstraintEvidence>();
            if (rawContextResults.Count == 0)
            {
                return result;
            }

            foreach (var rawContext in rawContextResults.Values)
            {
                try
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(rawContext));
                    if (!doc.RootElement.TryGetProperty("LogicContexts", out var contexts)
                        || contexts.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var context in contexts.EnumerateArray())
                    {
                        var rawLogicXml = TryGetString(context, "RawLogicXml");
                        var compactLogic = TryGetString(context, "CompactLogic");
                        var logicXml = !string.IsNullOrWhiteSpace(rawLogicXml)
                            ? rawLogicXml
                            : compactLogic;
                        if (string.IsNullOrWhiteSpace(logicXml))
                        {
                            continue;
                        }

                        var blockName = TryGetString(context, "CalleeBlockName")
                            ?? TryGetString(context, "BlockName")
                            ?? TryGetString(context, "CallerBlockName");
                        var logicTitle = TryGetString(context, "Title") ?? TryGetString(context, "Comment");

                        var mappings = SiemensFlgNetConstraintExtractor.ExtractCallPinMappingsFromLogicUnit(
                            logicXml,
                            blockName,
                            logicTitle);

                        foreach (var mapping in mappings)
                        {
                            if (string.IsNullOrWhiteSpace(mapping.SignalName) ||
                                !projectTagNames.Contains(mapping.SignalName))
                            {
                                continue;
                            }

                            result.Add(new CallPinConstraintEvidence
                            {
                                SignalName = mapping.SignalName,
                                CallName = mapping.CallName,
                                PinName = mapping.PinName,
                                PinSection = mapping.PinSection,
                                BlockName = mapping.BlockName
                            });
                        }
                    }
                }
                catch
                {
                }
            }

            return result
                .GroupBy(x => $"{x.SignalName}|{x.CallName}|{x.PinName}|{x.PinSection}|{x.BlockName}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private static string? MergeConstraintJsonArrays(
            List<CallPinConstraintEvidence> callPinEvidence,
            HashSet<string>? projectTagNames,
            Dictionary<string, string>? signalAddressDict,
            params string?[] jsonArrays)
        {
            var merged = new List<JsonObject>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var json in jsonArrays)
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(json);
                }
                catch
                {
                    continue;
                }

                if (node is not JsonArray array)
                {
                    continue;
                }

                foreach (var item in array)
                {
                    if (item is not JsonObject obj)
                    {
                        continue;
                    }

                    var key = BuildConstraintDedupKey(obj);
                    if (seen.Add(key))
                    {
                        merged.Add(obj);
                    }
                }
            }

            RemoveContradictoryMutexBySharedDriver(merged);
            RemoveInvalidImpliesByCallPinDirection(merged, callPinEvidence);
            // 去除同信号对的重复 Mutex（AT_MOST_ONE），不论信号角色名差异，只保留最先出现的一条
            DeduplicateMutexBySignalSet(merged);
            // 移除跨 FB 实例的错误 Mutex（如 Valve[1].QFwd、Valve[2].QFwd、Valve[3].QFwd 不应互斥）
            RemoveCrossInstanceMutex(merged, callPinEvidence);
            // 移除混合 Q 输出和 I 传感器的错误 Mutex（Q 和 I 之间是因果关系而非互斥）
            // 使用 callPinEvidence + signalAddressDict 双重判断，后者作为 fallback
            RemoveMutexMixingQandISensors(merged, callPinEvidence, signalAddressDict);
            // 移除 AllRequired 中同时要求 ManualMod 和 AutoMod 为 true 的约束（它们互斥，不可能同时为 true）
            RemoveAllRequiredWithConflictingModes(merged);
            // 从 AllRequired 中剥离 E_StopStatus/ResetStatus（它们是 REQUIRES_0 语义，不是 ALL_TRUE 前提条件）
            StripSafetySignalsFromAllRequired(merged);
            // 剥离所有约束中的 FB 形参名信号（如 Valve[3].FwdPmt）——它们应被映射为实际信号但 AI 未完成映射
            StripFormalParameterSignals(merged);
            // 移除指示灯/三色灯/蜂鸣器的错误互斥（Lamp 信号可以同时有效）
            RemoveLampAndIndependentOutputMutex(merged);
            // 去重方向相反但信号对相同的 Pairing（IMPLIES）约束
            DeduplicateReversedPairing(merged);
            // 过滤含非项目 IO 标签信号（如形参名 Valve[1].QFwd）的约束
            if (projectTagNames != null && projectTagNames.Count > 0)
            {
                RemoveConstraintsWithNonIoTagSignals(merged, projectTagNames);
                // 从非 AlarmBinding 约束的 signals 中移除非 IO 标签的 Member/Target 信号
                StripNonIoTagMemberTargetSignals(merged, projectTagNames);
            }
            // 跨方向校正：QFwd(Source) 的 Member/Target 只保留 IWP 方向传感器，QBkd 只保留 IHP
            CorrectCrossDirectionConstraints(merged, callPinEvidence);

            return merged.Count == 0
                ? null
                : JsonSerializer.Serialize(merged, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
        }

        /// <summary>
        /// 去除重复的 Mutex 约束：若多条 AT_MOST_ONE 规则覆盖相同的信号集合（不区分角色），只保留第一条。
        /// 解决 AI 对同一互斥对生成多条不同命名但等价约束的问题。
        /// </summary>
        private static void DeduplicateMutexBySignalSet(List<JsonObject> constraints)
        {
            var seenMutexSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            constraints.RemoveAll(constraint =>
            {
                var op = constraint["operator"]?.GetValue<string>();
                if (!string.Equals(op, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase))
                    return false;

                var signals = constraint["signals"] as JsonArray;
                if (signals == null) return false;

                var names = signals
                    .OfType<JsonObject>()
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (names.Count < 2) return false;

                var setKey = string.Join("|", names);
                return !seenMutexSets.Add(setKey);
            });
        }

        /// <summary>
        /// 移除跨 FB 实例的错误 Mutex 约束。
        /// 当一条 Mutex 中所有信号都绑定在同类 FB 输出引脚（如都是 QFwd 或都是 QBkd），
        /// 但来自不同的 FB 调用实例（如 Valve[1]、Valve[2]、Valve[3]），
        /// 则这是 AI 基于命名相似性的误判——不同物理执行机构可以同时动作。
        /// 仅保留同一 FB 实例内的 QFwd↔QBkd 互斥（已由其他规则处理）。
        /// </summary>
        private static void RemoveCrossInstanceMutex(List<JsonObject> constraints, List<CallPinConstraintEvidence> evidence)
        {
            if (evidence == null || evidence.Count == 0) return;

            // 建立 信号名 → (CallName, PinName) 索引
            var signalToPinInfo = new Dictionary<string, (string CallName, string PinName)>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in evidence)
            {
                if (!string.IsNullOrWhiteSpace(e.SignalName) && !string.IsNullOrWhiteSpace(e.CallName))
                {
                    // 优先取 output pin 信息（QFwd/QBkd 是输出引脚）
                    if (string.Equals(e.PinSection, "output", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.PinName, "QFwd", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.PinName, "QBkd", StringComparison.OrdinalIgnoreCase))
                    {
                        signalToPinInfo[e.SignalName] = (e.CallName, e.PinName);
                    }
                }
            }

            constraints.RemoveAll(constraint =>
            {
                var ruleType = constraint["ruleType"]?.GetValue<string>();
                if (!string.Equals(ruleType, "Mutex", StringComparison.OrdinalIgnoreCase))
                    return false;

                var signals = constraint["signals"] as JsonArray;
                if (signals == null || signals.Count < 3) return false; // 2-signal Mutex is typically QFwd↔QBkd, valid

                var memberNames = signals
                    .OfType<JsonObject>()
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .ToList();

                if (memberNames.Count < 3) return false;

                // 检查是否所有信号都有 pin 信息
                var pinInfos = memberNames
                    .Where(n => signalToPinInfo.ContainsKey(n))
                    .Select(n => signalToPinInfo[n])
                    .ToList();

                if (pinInfos.Count < 3) return false; // 不够 pin 信息，不过滤

                // 如果所有信号来自不同的 FB 实例，且绑定在同一个输出引脚（如都是 QFwd），则是跨实例误判
                var distinctCallNames = pinInfos.Select(p => p.CallName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var distinctPinNames = pinInfos.Select(p => p.PinName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                // 多个不同实例 + 所有引脚相同 → 跨实例 Mutex → 删除
                if (distinctCallNames.Count >= 3 && distinctPinNames.Count == 1)
                    return true;

                // 多个不同实例 + 引脚都是同方向（全 QFwd 或全 QBkd 或全 IWP 或全 IHP）→ 删除
                if (distinctCallNames.Count >= 3 && distinctPinNames.All(p =>
                    p.StartsWith("Q", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("I", StringComparison.OrdinalIgnoreCase)))
                {
                    // 进一步检查：所有 pin 都是输出引脚或都是输入引脚
                    var allOutput = distinctPinNames.All(p =>
                        string.Equals(p, "QFwd", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p, "QBkd", StringComparison.OrdinalIgnoreCase));
                    var allInput = distinctPinNames.All(p =>
                        string.Equals(p, "IWP", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p, "IHP", StringComparison.OrdinalIgnoreCase));
                    if (allOutput || allInput)
                        return true;
                }

                return false;
            });
        }

        /// <summary>
        /// 移除混合 Q 输出和 I 传感器的 Mutex 约束。
        /// Q 输出和 I 传感器之间是因果关系（Q 驱动 → I 反馈），不是互斥关系。
        /// 同一 FB 实例内的 QFwd↔QBkd 或 IWP↔IHP 互斥是合法的，但 Q 和 I 混合互斥一定是错的。
        /// 使用双重判断：先查 callPinEvidence 的 PinSection，若缺失则回退到信号地址前缀 %Q/%I。
        /// </summary>
        private static void RemoveMutexMixingQandISensors(
            List<JsonObject> constraints,
            List<CallPinConstraintEvidence> evidence,
            Dictionary<string, string>? signalAddressDict)
        {
            // 建立 信号名 → PinSection (Input/Output) 索引（来自 callPinEvidence）
            var signalToSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (evidence != null)
            {
                foreach (var e in evidence)
                {
                    if (!string.IsNullOrWhiteSpace(e.SignalName) && !string.IsNullOrWhiteSpace(e.PinSection))
                    {
                        signalToSection.TryAdd(e.SignalName, e.PinSection);
                    }
                }
            }

            constraints.RemoveAll(constraint =>
            {
                var ruleType = constraint["ruleType"]?.GetValue<string>();
                if (!string.Equals(ruleType, "Mutex", StringComparison.OrdinalIgnoreCase))
                    return false;

                var signals = constraint["signals"] as JsonArray;
                if (signals == null || signals.Count < 2) return false;

                var memberNames = signals
                    .OfType<JsonObject>()
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .ToList();

                // 查找信号中有多少是 Q 输出、多少是 I 传感器
                int outputCount = 0, inputCount = 0;
                foreach (var name in memberNames)
                {
                    // 优先使用 callPinEvidence 的 PinSection
                    if (signalToSection.TryGetValue(name, out var section))
                    {
                        if (string.Equals(section, "Output", StringComparison.OrdinalIgnoreCase))
                            outputCount++;
                        else if (string.Equals(section, "Input", StringComparison.OrdinalIgnoreCase))
                            inputCount++;
                    }
                    // fallback：使用信号物理地址前缀 %Q/%I 判断
                    else if (signalAddressDict != null && signalAddressDict.TryGetValue(name, out var address))
                    {
                        if (address.StartsWith("%Q", StringComparison.OrdinalIgnoreCase))
                            outputCount++;
                        else if (address.StartsWith("%I", StringComparison.OrdinalIgnoreCase))
                            inputCount++;
                    }
                }

                // 同时包含 Q 输出和 I 传感器 → 错误的混合 Mutex
                return outputCount > 0 && inputCount > 0;
            });
        }

        /// <summary>
        /// 移除 AllRequired 中同时要求互斥模式信号为 true 的约束。
        /// ManualMod（手动模式）和 AutoMod（自动模式）是互斥的运行模式，不可能同时为 true。
        /// 如果一条 AllRequired(ALL_TRUE) 同时包含它们作为 Member，该约束永远无法满足，应删除。
        /// 处理方式：移除 AllRequired 中的 ManualMod/AutoMod 信号，而非删除整条约束，
        /// 因为约束的其他 Member（如传感器）仍然有效。
        /// </summary>
        private static void RemoveAllRequiredWithConflictingModes(List<JsonObject> constraints)
        {
            var modePatterns = new[] { "ManualMod", "AutoMod", "AutoRunMod" };

            foreach (var constraint in constraints)
            {
                var op = constraint["operator"]?.GetValue<string>();
                if (!string.Equals(op, "ALL_TRUE", StringComparison.OrdinalIgnoreCase))
                    continue;

                var signals = constraint["signals"] as JsonArray;
                if (signals == null) continue;

                var modeSignals = signals
                    .OfType<JsonObject>()
                    .Where(sig =>
                    {
                        var name = sig["signalName"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(name)) return false;
                        return modePatterns.Any(pattern =>
                            name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
                    })
                    .ToList();

                // 如果包含 2 个或以上互斥模式信号，从 signals 中移除它们
                if (modeSignals.Count >= 2)
                {
                    foreach (var modeSig in modeSignals)
                        signals.Remove(modeSig);
                }
            }

            // 移除 Member/Target 为空的约束
            constraints.RemoveAll(constraint =>
            {
                var signals = constraint["signals"] as JsonArray;
                if (signals == null) return true;
                return !signals.OfType<JsonObject>().Any(x =>
                    !string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase));
            });
        }

        /// <summary>
        /// 从 AllRequired(ALL_TRUE) 中剥离 E_StopStatus / ResetStatus 信号。
        /// 这些信号的语义是"为 true 时禁止动作"（REQUIRES_0），不是"必须为 true 才能动作"（ALL_TRUE）。
        /// AI 经常把它们混入 AllRequired，导致语义完全倒反：
        ///   错误：E_StopStatus 必须为 true → 意味着"急停触发才能动作"
        ///   正确：E_StopStatus 为 true → 所有输出必须禁止（REQUIRES_0）
        /// </summary>
        private static void StripSafetySignalsFromAllRequired(List<JsonObject> constraints)
        {
            var safetyPatterns = new[] { "E_StopStatus", "EStop", "EmergencyStop", "ResetStatus" };

            foreach (var constraint in constraints)
            {
                var op = constraint["operator"]?.GetValue<string>();
                if (!string.Equals(op, "ALL_TRUE", StringComparison.OrdinalIgnoreCase))
                    continue;

                var signals = constraint["signals"] as JsonArray;
                if (signals == null) continue;

                var toRemove = signals
                    .OfType<JsonObject>()
                    .Where(sig =>
                    {
                        var role = sig["signalRole"]?.GetValue<string>();
                        if (string.Equals(role, "Source", StringComparison.OrdinalIgnoreCase))
                            return false; // 不移除 Source
                        var name = sig["signalName"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(name)) return false;
                        return safetyPatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
                    })
                    .ToList();

                foreach (var sig in toRemove)
                    signals.Remove(sig);
            }

            // 移除 Member/Target 为空的约束
            constraints.RemoveAll(constraint =>
            {
                var signals = constraint["signals"] as JsonArray;
                if (signals == null) return true;
                return !signals.OfType<JsonObject>().Any(x =>
                    !string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase));
            });
        }

        /// <summary>
        /// 从所有约束中剥离 FB 形参名格式的信号（如 Valve[3].FwdPmt、Valve[2].BkdPmt）。
        /// 这些形参本应在 CallBindingSummary 映射阶段被替换为实际信号名，
        /// 但 AI 有时直接使用形参名生成约束，导致约束指向不存在的信号。
        /// 匹配模式：信号名包含 "[" 和 "]."（如 Xxx[N].Yyy）。
        /// </summary>
        private static void StripFormalParameterSignals(List<JsonObject> constraints)
        {
            foreach (var constraint in constraints)
            {
                var signals = constraint["signals"] as JsonArray;
                if (signals == null) continue;

                var toRemove = signals
                    .OfType<JsonObject>()
                    .Where(sig =>
                    {
                        var name = sig["signalName"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(name)) return false;
                        // 匹配 Xxx[N].Yyy 格式的 FB 形参名
                        return name.Contains('[') && name.Contains("].");
                    })
                    .ToList();

                foreach (var sig in toRemove)
                    signals.Remove(sig);
            }

            // 移除信号不足的约束（至少需要 Source + 1个 Member/Target）
            constraints.RemoveAll(constraint =>
            {
                var signals = constraint["signals"] as JsonArray;
                if (signals == null || signals.Count < 2) return true;
                // 对于需要 Source 的类型，检查是否还有 Member/Target
                var op = constraint["operator"]?.GetValue<string>();
                if (string.Equals(op, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase))
                    return signals.Count < 2; // Mutex 至少 2 个 Member
                return !signals.OfType<JsonObject>().Any(x =>
                    !string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase));
            });
        }

        /// <summary>
        /// 移除指示灯、三色灯、蜂鸣器和独立安全门复位灯的错误 Mutex。
        /// 这些信号虽然来自同一逻辑区域，但物理上完全独立，可以同时有效：
        /// - 三色塔灯（红/黄/绿灯）可同时点亮表示不同状态
        /// - 蜂鸣器和灯可同时有效
        /// - 多个独立安全门的复位灯可同时点亮
        /// 匹配 Mutex 中所有 Member 信号名都包含 Lamp/HL/PL 前缀且都是同类指示灯。
        /// </summary>
        private static void RemoveLampAndIndependentOutputMutex(List<JsonObject> constraints)
        {
            constraints.RemoveAll(constraint =>
            {
                var ruleType = constraint["ruleType"]?.GetValue<string>();
                if (!string.Equals(ruleType, "Mutex", StringComparison.OrdinalIgnoreCase))
                    return false;

                var signals = constraint["signals"] as JsonArray;
                if (signals == null || signals.Count < 3) return false;

                var memberNames = signals
                    .OfType<JsonObject>()
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .ToList();

                if (memberNames.Count < 3) return false;

                // 检查是否全部是灯/蜂鸣器信号
                var allLampOrBuzzer = memberNames.All(name =>
                    name.StartsWith("Lamp_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("HL_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("PL_", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Buzzer", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("_Lamp", StringComparison.OrdinalIgnoreCase));

                if (allLampOrBuzzer)
                    return true;

                // 检查是否是独立安全门复位灯（SafeDoorNReset 模式）
                var allSafeDoorReset = memberNames.All(name =>
                    name.Contains("SafeDoor", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("Reset", StringComparison.OrdinalIgnoreCase));

                if (allSafeDoorReset)
                    return true;

                return false;
            });
        }

        /// <summary>
        /// 去重方向相反但信号对相同的 Pairing（IMPLIES）约束。
        /// 例如 A→B 和 B→A 代表相同的配对关系，只保留第一条。
        /// 信号对的规范化：按字母序排列 Source 和 Target 信号名，生成去重键。
        /// </summary>
        private static void DeduplicateReversedPairing(List<JsonObject> constraints)
        {
            var seenPairingSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            constraints.RemoveAll(constraint =>
            {
                var ruleType = constraint["ruleType"]?.GetValue<string>();
                var op = constraint["operator"]?.GetValue<string>();
                if (!string.Equals(ruleType, "Pairing", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(op, "IMPLIES", StringComparison.OrdinalIgnoreCase))
                    return false;

                var signals = constraint["signals"] as JsonArray;
                if (signals == null) return false;

                var allNames = signals
                    .OfType<JsonObject>()
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (allNames.Count < 2) return false;

                var pairKey = string.Join("|", allNames);
                return !seenPairingSets.Add(pairKey);
            });
        }

        /// <summary>
        /// 过滤含非允许信号 Source 的约束。
        /// 剔除形参名（如 Valve[1].QFwd）或非关键引脚 DB 变量作为 Source 的错误约束。
        /// 注意：projectTagNames 参数已扩展为包含关键引脚 DB 变量（如 General.AutoMod），这些变量允许作为 Source。
        /// </summary>
        private static void RemoveConstraintsWithNonIoTagSignals(List<JsonObject> constraints, HashSet<string> projectTagNames)
        {
            constraints.RemoveAll(constraint =>
            {
                var signals = constraint["signals"] as JsonArray;
                if (signals == null) return false;

                return signals
                    .OfType<JsonObject>()
                    .Where(x => string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Any(name => !projectTagNames.Contains(name!));
            });
        }

        /// <summary>
        /// 从非 AlarmBinding 约束的 signals 中移除非允许信号的 Member/Target。
        /// AlarmBinding 的 Target 允许 DB 变量（报警信号本质是 DB 内部变量）。
        /// 关键引脚 DB 变量（已在 projectTagNames 扩展集中）也允许作为 Member/Target。
        /// 若移除后 Member/Target 为空，则整条约束删除。
        /// </summary>
        private static void StripNonIoTagMemberTargetSignals(List<JsonObject> constraints, HashSet<string> projectTagNames)
        {
            constraints.RemoveAll(constraint =>
            {
                var ruleType = constraint["ruleType"]?.GetValue<string>() ?? string.Empty;
                // AlarmBinding 的 Target 允许 DB 变量
                if (string.Equals(ruleType, "AlarmBinding", StringComparison.OrdinalIgnoreCase))
                    return false;

                var signals = constraint["signals"] as JsonArray;
                if (signals == null) return false;

                // 找出需要移除的非 IO 标签 Member/Target 信号索引
                var toRemove = new List<JsonNode>();
                foreach (var sig in signals.OfType<JsonObject>())
                {
                    var role = sig["signalRole"]?.GetValue<string>();
                    if (string.Equals(role, "Source", StringComparison.OrdinalIgnoreCase))
                        continue; // Source 由 RemoveConstraintsWithNonIoTagSignals 处理

                    var name = sig["signalName"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(name) && !projectTagNames.Contains(name))
                        toRemove.Add(sig);
                }

                foreach (var node in toRemove)
                    signals.Remove(node);

                // 移除后若无 Member/Target 则整条约束无效
                var remainingNonSource = signals
                    .OfType<JsonObject>()
                    .Any(x => !string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase));

                return !remainingNonSource;
            });
        }

        /// <summary>
        /// 校正 AllRequired 和 Pairing 约束的跨方向问题：
        /// QFwd(Source) 对应 IWP 方向传感器，QBkd(Source) 对应 IHP 方向传感器。
        /// 若 AI 将两个方向的传感器混入同一条约束，利用 callPinEvidence
        /// 识别每个 Member/Target 信号所属引脚方向（IWP/IHP），移除反方向的信号。
        /// </summary>
        private static void CorrectCrossDirectionConstraints(
            List<JsonObject> constraints,
            List<CallPinConstraintEvidence> evidence)
        {
            if (constraints == null || constraints.Count == 0 || evidence == null || evidence.Count == 0)
                return;

            // 建立 信号名→引脚名集合 的索引（如 OP020_CYL01_1W → { IWP }，OP020_YV01_W → { QFwd }）
            var signalToPins = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in evidence)
            {
                if (string.IsNullOrWhiteSpace(e.SignalName) || string.IsNullOrWhiteSpace(e.PinName))
                    continue;
                if (!signalToPins.TryGetValue(e.SignalName, out var pins))
                {
                    pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    signalToPins[e.SignalName] = pins;
                }
                pins.Add(e.PinName);
            }

            // 建立 Source 信号 → 同一 FB 实例的 CallName 索引
            var signalToCallNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in evidence)
            {
                if (string.IsNullOrWhiteSpace(e.SignalName) || string.IsNullOrWhiteSpace(e.CallName))
                    continue;
                if (!signalToCallNames.TryGetValue(e.SignalName, out var calls))
                {
                    calls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    signalToCallNames[e.SignalName] = calls;
                }
                calls.Add(e.CallName);
            }

            // 建立 (CallName, PinName) → [信号名列表] 的索引
            // 用于查找同一 FB 实例某个引脚上绑定的全部信号
            var callPinToSignals = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in evidence)
            {
                if (string.IsNullOrWhiteSpace(e.CallName) || string.IsNullOrWhiteSpace(e.PinName)
                    || string.IsNullOrWhiteSpace(e.SignalName))
                    continue;
                var key = $"{e.CallName}|{e.PinName}";
                if (!callPinToSignals.TryGetValue(key, out var sigs))
                {
                    sigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    callPinToSignals[key] = sigs;
                }
                sigs.Add(e.SignalName);
            }

            var fwdInputPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IWP" };
            var bkdInputPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IHP" };

            constraints.RemoveAll(constraint =>
            {
                var op = constraint["operator"]?.GetValue<string>();
                // 只处理 AllRequired(ALL_TRUE) 和 Pairing(IMPLIES)
                var isAllTrue = string.Equals(op, "ALL_TRUE", StringComparison.OrdinalIgnoreCase);
                var isImplies = string.Equals(op, "IMPLIES", StringComparison.OrdinalIgnoreCase);
                if (!isAllTrue && !isImplies) return false;

                var signals = constraint["signals"] as JsonArray;
                if (signals == null) return false;

                // 找 Source 信号
                var sourceSignals = signals
                    .OfType<JsonObject>()
                    .Where(x => string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (sourceSignals.Count != 1) return false;
                var sourceName = sourceSignals[0]!;

                // 判断 Source 是 QFwd 还是 QBkd
                if (!signalToPins.TryGetValue(sourceName, out var sourcePins))
                    return false;

                HashSet<string>? allowedInputPins = null;
                if (sourcePins.Any(p => p.StartsWith("QFwd", StringComparison.OrdinalIgnoreCase) ||
                                        p.StartsWith("Q_Fwd", StringComparison.OrdinalIgnoreCase)))
                    allowedInputPins = fwdInputPins;
                else if (sourcePins.Any(p => p.StartsWith("QBkd", StringComparison.OrdinalIgnoreCase) ||
                                              p.StartsWith("Q_Bkd", StringComparison.OrdinalIgnoreCase)))
                    allowedInputPins = bkdInputPins;

                if (allowedInputPins == null) return false;

                // 对 AllRequired：构建 Source 所属 FB 实例中 IWP/IHP 引脚的合法信号白名单
                // 只有在白名单中的信号才允许作为 Member（精确到 FB 实例级别）
                HashSet<string>? allowedMemberWhitelist = null;
                if (isAllTrue && signalToCallNames.TryGetValue(sourceName, out var sourceCallNames))
                {
                    allowedMemberWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var callName in sourceCallNames)
                    {
                        foreach (var pinName in allowedInputPins)
                        {
                            var key = $"{callName}|{pinName}";
                            if (callPinToSignals.TryGetValue(key, out var pinSignals))
                            {
                                foreach (var s in pinSignals)
                                    allowedMemberWhitelist.Add(s);
                            }
                        }
                    }
                }

                var toRemove = new List<JsonNode>();
                foreach (var sig in signals.OfType<JsonObject>())
                {
                    var role = sig["signalRole"]?.GetValue<string>();
                    if (string.Equals(role, "Source", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sigName = sig["signalName"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(sigName))
                        continue;

                    // AllRequired 白名单过滤：Member 必须在同一 FB 实例的对应引脚信号列表中
                    if (isAllTrue && allowedMemberWhitelist != null && allowedMemberWhitelist.Count > 0
                        && string.Equals(role, "Member", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!allowedMemberWhitelist.Contains(sigName))
                        {
                            toRemove.Add(sig);
                            continue;
                        }
                    }

                    // 跨方向过滤（AllRequired 和 Pairing 共用）
                    if (signalToPins.TryGetValue(sigName, out var sigPins))
                    {
                        var isAllowedDirection = sigPins.Any(p => allowedInputPins.Contains(p));
                        var isOppositeDirection = sigPins.Any(p =>
                            (allowedInputPins == fwdInputPins && bkdInputPins.Contains(p)) ||
                            (allowedInputPins == bkdInputPins && fwdInputPins.Contains(p)));

                        if (isOppositeDirection && !isAllowedDirection)
                            toRemove.Add(sig);
                    }
                }

                foreach (var node in toRemove)
                    signals.Remove(node);

                // 校正后若无 Member/Target 了则删除整条约束
                var hasNonSource = signals
                    .OfType<JsonObject>()
                    .Any(x => !string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase));
                return !hasNonSource;
            });
        }

        private static void RemoveInvalidImpliesByCallPinDirection(
            List<JsonObject> constraints,
            List<CallPinConstraintEvidence> evidence)
        {
            if (constraints == null || constraints.Count == 0 || evidence == null || evidence.Count == 0)
            {
                return;
            }

            var evidenceBySignal = evidence
                .Where(x => !string.IsNullOrWhiteSpace(x.SignalName))
                .GroupBy(x => x.SignalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            constraints.RemoveAll(constraint =>
            {
                var operatorName = constraint["operator"]?.GetValue<string>();
                if (!string.Equals(operatorName, "IMPLIES", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var signals = constraint["signals"] as JsonArray;
                if (signals == null)
                {
                    return false;
                }

                var sources = signals
                    .OfType<JsonObject>()
                    .Where(x => string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var targets = signals
                    .OfType<JsonObject>()
                    .Where(x => string.Equals(x["signalRole"]?.GetValue<string>(), "Target", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (sources.Count != 1 || targets.Count != 1)
                {
                    return false;
                }

                if (!evidenceBySignal.TryGetValue(sources[0]!, out var sourceEntries) ||
                    !evidenceBySignal.TryGetValue(targets[0]!, out var targetEntries))
                {
                    return false;
                }

                return sourceEntries.Any(sourceEntry =>
                    string.Equals(sourceEntry.PinSection, "Input", StringComparison.OrdinalIgnoreCase) &&
                    targetEntries.Any(targetEntry =>
                        string.Equals(targetEntry.PinSection, "Output", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(sourceEntry.CallName, targetEntry.CallName, StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrWhiteSpace(sourceEntry.BlockName) ||
                         string.IsNullOrWhiteSpace(targetEntry.BlockName) ||
                         string.Equals(sourceEntry.BlockName, targetEntry.BlockName, StringComparison.OrdinalIgnoreCase))));
            });
        }

        private static void RemoveContradictoryMutexBySharedDriver(List<JsonObject> constraints)
        {
            if (constraints == null || constraints.Count == 0)
            {
                return;
            }

            var sourceToTargets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var constraint in constraints)
            {
                var operatorName = constraint["operator"]?.GetValue<string>();
                if (!string.Equals(operatorName, "IMPLIES", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var signals = constraint["signals"] as JsonArray;
                if (signals == null)
                {
                    continue;
                }

                var sources = signals
                    .OfType<JsonObject>()
                    .Where(x => string.Equals(x["signalRole"]?.GetValue<string>(), "Source", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var targets = signals
                    .OfType<JsonObject>()
                    .Where(x => string.Equals(x["signalRole"]?.GetValue<string>(), "Target", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (sources.Count != 1 || targets.Count != 1)
                {
                    continue;
                }

                if (!sourceToTargets.TryGetValue(sources[0]!, out var targetSet))
                {
                    targetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    sourceToTargets[sources[0]!] = targetSet;
                }

                targetSet.Add(targets[0]!);
            }

            constraints.RemoveAll(constraint =>
            {
                var operatorName = constraint["operator"]?.GetValue<string>();
                if (!string.Equals(operatorName, "AT_MOST_ONE", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var signals = constraint["signals"] as JsonArray;
                if (signals == null)
                {
                    return false;
                }

                var members = signals
                    .OfType<JsonObject>()
                    .Where(x => string.Equals(x["signalRole"]?.GetValue<string>(), "Member", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x["signalName"]?.GetValue<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (members.Count < 2)
                {
                    return false;
                }

                foreach (var targets in sourceToTargets.Values)
                {
                    var overlapCount = members.Count(member => targets.Contains(member!));
                    if (overlapCount >= 2)
                    {
                        return true;
                    }
                }

                return false;
            });
        }

        private static string BuildConstraintDedupKey(JsonObject obj)
        {
            var ruleType = obj["ruleType"]?.GetValue<string?>() ?? string.Empty;
            var op = obj["operator"]?.GetValue<string?>() ?? string.Empty;
            var validStates = obj["validStates"]?.GetValue<string?>() ?? string.Empty;
            var signalKeys = new List<string>();

            if (obj["signals"] is JsonArray signals)
            {
                foreach (var item in signals.OfType<JsonObject>()
                             .OrderBy(x => x["sortOrder"]?.GetValue<int?>() ?? int.MaxValue)
                             .ThenBy(x => x["signalRole"]?.GetValue<string?>() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(x => x["signalName"]?.GetValue<string?>() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    signalKeys.Add($"{item["signalRole"]?.GetValue<string?>()}:{item["signalName"]?.GetValue<string?>()}");
                }
            }

            return $"{ruleType}|{op}|{validStates}|{string.Join(",", signalKeys)}";
        }

        private static List<object> TryGetArray(JsonElement element, string propertyName)
        {
            var result = new List<object>();
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in property.EnumerateArray().Take(20))
            {
                var parsed = JsonSerializer.Deserialize<object>(item.GetRawText());
                if (parsed != null)
                {
                    result.Add(parsed);
                }
            }

            return result;
        }

        private static List<string> DetectReferencedCallPins(string? compactLogic, IEnumerable<string> candidatePins)
        {
            if (string.IsNullOrWhiteSpace(compactLogic))
            {
                return new List<string>();
            }

            var result = new List<string>();
            foreach (var pin in candidatePins.Where(pin => !string.IsNullOrWhiteSpace(pin)))
            {
                if (Regex.IsMatch(compactLogic, $@"(?<![A-Za-z0-9_]){Regex.Escape(pin)}(?![A-Za-z0-9_])"))
                {
                    result.Add(pin);
                }
            }

            return result;
        }

        private static string? TruncateForPrompt(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value[..maxLength] + "...(truncated)";
        }

        #endregion
    }
}
