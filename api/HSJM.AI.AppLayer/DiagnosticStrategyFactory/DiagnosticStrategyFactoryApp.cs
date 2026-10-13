using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory
{
    internal sealed class EnergyFlowDiagnosisProgress
    {
        private const string Operation = "energy_flow";
        private readonly IDiagnosisProgressReporter _reporter;

        public EnergyFlowDiagnosisProgress(IDiagnosisProgressReporter reporter) =>
            _reporter = reporter ?? NullDiagnosisProgressReporter.Instance;

        public Task StartedAsync(CancellationToken cancellationToken = default) =>
            ReportAsync(
                DiagnosisProgressEvent.Started(
                    Operation,
                    "start",
                    "开始产线 IO 约束异常检测"),
                cancellationToken);

        public Task StrategyReadyAsync(CancellationToken cancellationToken = default) =>
            ReportAsync(
                DiagnosisProgressEvent.Running(
                    Operation,
                    "strategy_ready",
                    10,
                    "IO 约束诊断策略已准备",
                    force: true),
                cancellationToken);

        public Task SignalsLoadedAsync(int signalCount, CancellationToken cancellationToken = default) =>
            ReportAsync(
                DiagnosisProgressEvent.Running(
                    Operation,
                    "signals_loaded",
                    35,
                    "实时信号读取完成",
                    new DiagnosisProgressMetrics { SignalCount = signalCount },
                    force: true),
                cancellationToken);

        public Task ConstraintsEvaluatedAsync(int issueCount, CancellationToken cancellationToken = default) =>
            ReportAsync(
                DiagnosisProgressEvent.Running(
                    Operation,
                    "constraints_evaluated",
                    75,
                    "结果片段：IO 约束检查完成",
                    new DiagnosisProgressMetrics { IssueCount = issueCount },
                    force: true),
                cancellationToken);

        public Task ResultReadyAsync(int issueCount, CancellationToken cancellationToken = default) =>
            ReportAsync(
                DiagnosisProgressEvent.Running(
                    Operation,
                    "result_ready",
                    95,
                    "结果片段：IO 约束诊断结果已生成",
                    new DiagnosisProgressMetrics { IssueCount = issueCount },
                    force: true),
                cancellationToken);

        public Task CompletedAsync(bool isSuccess, CancellationToken cancellationToken = default) =>
            ReportAsync(
                DiagnosisProgressEvent.Completed(
                    Operation,
                    "complete",
                    isSuccess
                        ? "产线 IO 约束异常检测完成"
                        : "产线 IO 约束异常检测结束（业务返回失败）"),
                cancellationToken);

        public Task FailedAsync(CancellationToken cancellationToken = default) =>
            ReportAsync(
                DiagnosisProgressEvent.Failed(
                    Operation,
                    "failed",
                    "产线 IO 约束异常检测失败"),
                cancellationToken);

        private async Task ReportAsync(DiagnosisProgressEvent progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _reporter.ReportAsync(progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Progress transport is best-effort and must not change diagnosis results.
            }
        }
    }

    /// <summary>
    /// 诊断策略工厂应用层服务
    /// 负责PLC诊断的完整流程：安全检查→约束检查→IO点位→卡滞点→能量流分析→交叉引用
    /// </summary>
    /// <param name="diagnosticValidator">诊断验证器接口</param>
    /// <param name="stepCrossRefDiagnostic">步骤交叉引用诊断（AI分析依赖链）</param>
    /// <param name="pLC_LogicUnitService">LogicUnit表服务（查询逻辑单元）</param>
    /// <param name="pLC_BlockService">Block表服务（查询Block信息）</param>
    /// <param name="pLC_VariableAccessService">VariableAccess表服务（查询变量读写记录，用于卡滞点定位）</param>
    /// <param name="pLC_IOConstraintRuleService">IO约束规则服务（查询约束规则）</param>
    /// <param name="pLC_IOConstraintSignalService">IO约束信号服务（查询约束关联的信号）</param>
    /// <param name="pLC_IOTagService">IO点位服务（查询点位和安全变量）</param>
    /// <param name="pLC_ProjectService">PLC项目服务（根据名称查询PLC ID）</param>
    /// <param name="agentRuntimeService">AI Agent运行时服务（调用LLM）</param>
    /// <param name="promptTemplateService">提示词模板服务（加载和渲染Prompt）</param>
    /// <param name="diagnosticVerificationService">诊断验证服务（AI自我反思和优化）</param>
    /// <param name="strategyDecider">诊断策略决策服务</param>
    /// <param name="memoryService">诊断记忆服务（用于卡滞点和能量流分析）</param>
    public class DiagnosticStrategyFactoryApp(
        IDiagnosticValidator diagnosticValidator,
        IStepCrossReferenceDiagnostic stepCrossRefDiagnostic,
        IPLC_LogicUnitService pLC_LogicUnitService,
        IPLC_BlockService pLC_BlockService,
        IPLC_VariableAccessService pLC_VariableAccessService,
        IPLC_IOConstraintRuleService pLC_IOConstraintRuleService,
        IPLC_IOConstraintSignalService pLC_IOConstraintSignalService,
        IPLC_IOTagService pLC_IOTagService,
        IPLC_ProjectService pLC_ProjectService,
        AgentRuntimeService agentRuntimeService,
        PromptTemplateService promptTemplateService,
        DiagnosticVerificationService diagnosticVerificationService,
        DiagnosticStrategyDecider strategyDecider,
        DiagnosticMemoryService memoryService,
        ILogger<DiagnosticStrategyFactoryApp> logger = null,
        HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353ManualModeReader manualModeReader = null) : ISelfScopedAutoInject
    {
        /// <summary>日志（可空：测试直接构造时不注入）</summary>
        private readonly ILogger<DiagnosticStrategyFactoryApp> _logger = logger;
        private readonly HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353ManualModeReader _manualModeReader = manualModeReader;

        internal Func<long, IReadOnlyList<HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353ManualModeBinding>,
            CancellationToken, Task<HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353ManualModeRead>> ManualModeReadOverride { get; set; }

        /// <summary>约束检查调试日志写入器（测试可替换）；写失败只记警告，不影响检查结果。</summary>
        internal Func<string, IEnumerable<string>, CancellationToken, Task> ConstraintLogWriter { get; set; } = WriteConstraintLogFileAsync;

        /// <summary>诊断验证器</summary>
        private readonly IDiagnosticValidator _diagnosticValidator = diagnosticValidator;
        /// <summary>步骤交叉引用诊断</summary>
        private readonly IStepCrossReferenceDiagnostic _stepCrossRefDiagnostic = stepCrossRefDiagnostic;
        /// <summary>LogicUnit表服务</summary>
        private readonly IPLC_LogicUnitService _pLC_LogicUnitService = pLC_LogicUnitService;
        /// <summary>Block表服务</summary>
        private readonly IPLC_BlockService _pLC_BlockService = pLC_BlockService;
        /// <summary>VariableAccess表服务</summary>
        private readonly IPLC_VariableAccessService _pLC_VariableAccessService = pLC_VariableAccessService;
        /// <summary>IO约束规则服务</summary>
        private readonly IPLC_IOConstraintRuleService _pLC_IOConstraintRuleService = pLC_IOConstraintRuleService;
        /// <summary>IO约束信号服务</summary>
        private readonly IPLC_IOConstraintSignalService _pLC_IOConstraintSignalService = pLC_IOConstraintSignalService;
        /// <summary>IO点位服务</summary>
        private readonly IPLC_IOTagService _pLC_IOTagService = pLC_IOTagService;
        /// <summary>PLC项目服务</summary>
        private readonly IPLC_ProjectService _pLC_ProjectService = pLC_ProjectService;
        /// <summary>AI Agent运行时</summary>
        private readonly AgentRuntimeService _agentRuntimeService = agentRuntimeService;
        /// <summary>提示词模板服务</summary>
        private readonly PromptTemplateService _promptTemplateService = promptTemplateService;
        /// <summary>诊断验证服务</summary>
        private readonly DiagnosticVerificationService _diagnosticVerificationService = diagnosticVerificationService;
        /// <summary>诊断策略决策服务</summary>
        private readonly DiagnosticStrategyDecider _strategyDecider = strategyDecider;
        /// <summary>诊断记忆服务</summary>
        private readonly DiagnosticMemoryService _memoryService = memoryService;

        /// <summary>
        /// 缓存的 JSON 序列化选项（避免重复创建）
        /// </summary>
        private static readonly JsonSerializerOptions CachedJsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 诊断 AI Agent Role ID
        /// </summary>
        private int DiagnosticAgentRoleId => AppsettingsConfig.AIAgent.DiagnosticAgentRoleId;

        #region 常量定义

        /// <summary>
        /// 单次约束诊断最多触发 AI 复核的数量
        /// </summary>
        private const int MaxConstraintAiReviewCount = 12;

        /// <summary>
        /// 发送给 AI 的单个逻辑单元原始内容最大长度
        /// </summary>
        private const int ConstraintAiRawContentMaxLength = 6000;

        #endregion

        private static bool TryResolveLiveSignalBool(
            Dictionary<string, object> liveSignals,
            string signalName,
            out bool value)
        {
            value = false;
            if (liveSignals == null || string.IsNullOrWhiteSpace(signalName))
            {
                return false;
            }

            if (TryConvertToBool(liveSignals, signalName, out value))
            {
                return true;
            }

            var exactIgnoreCase = liveSignals.Keys
                .FirstOrDefault(k => string.Equals(k, signalName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exactIgnoreCase)
                && TryConvertToBool(liveSignals, exactIgnoreCase, out value))
            {
                return true;
            }

            var suffixMatch = liveSignals.Keys
                .FirstOrDefault(k =>
                    k.EndsWith("." + signalName, StringComparison.OrdinalIgnoreCase)
                    || k.EndsWith("]" + signalName, StringComparison.OrdinalIgnoreCase)
                    || k.EndsWith("_" + signalName, StringComparison.OrdinalIgnoreCase));

            return !string.IsNullOrWhiteSpace(suffixMatch)
                && TryConvertToBool(liveSignals, suffixMatch, out value);
        }

        private static bool TryConvertToBool(
            Dictionary<string, object> liveSignals,
            string key,
            out bool value)
        {
            value = false;
            if (!liveSignals.TryGetValue(key, out var raw) || raw == null)
            {
                return false;
            }

            if (raw is bool boolValue)
            {
                value = boolValue;
                return true;
            }

            var text = raw.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (bool.TryParse(text, out var parsedBool))
            {
                value = parsedBool;
                return true;
            }

            if (text == "1")
            {
                value = true;
                return true;
            }

            if (text == "0")
            {
                value = false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// PLC诊断入口：6步诊断流程（安全→约束→IO点位→卡滞点→能量流分析→交叉引用）
        /// 支持快速模式和深度模式，返回异常诊断结果
        /// </summary>
        /// <param name="request">诊断请求，包含ProjectId/ForceDeepAnalysis/ConditionDTO/RawContent/LiveSignals等</param>
        /// <param name="progressReporter">可选的逐调用强类型进度上报器</param>
        /// <param name="cancellationToken">外部取消令牌</param>
        /// <returns>诊断结果JSON，包含异常列表、卡滞点、统计信息</returns>
        public async Task<HttpResponseResultModel<string>> DiagnoseEnergyFlowAsync(
            EnergyFlowDiagnosticRequestDto request,
            IDiagnosisProgressReporter progressReporter = null,
            CancellationToken cancellationToken = default)
        {
            progressReporter ??= NullDiagnosisProgressReporter.Instance;
            var diagnosisProgress = new EnergyFlowDiagnosisProgress(progressReporter);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await diagnosisProgress.StartedAsync(cancellationToken);

                // ========== 步骤0: 参数校验和初始化 ==========
                // 1. 产线ID（必传）
                long productionLineId = request.ProductionLineId;

                // 校验产线ID
                if (productionLineId <= 0)
                {
                    var invalidRequestResult = new HttpResponseResultModel<string>
                    {
                        IsSuccess = false,
                        HttpStatusCode = HttpStatusCode.BadRequest,
                        ErrorMessage = "产线ID不能为空",
                        BackResult = JsonSerializer.Serialize(new { error = "参数不足", message = "ProductionLineId 是必传参数" })
                    };
                    await diagnosisProgress.CompletedAsync(invalidRequestResult.IsSuccess, cancellationToken);
                    return invalidRequestResult;
                }

                // ========== 步骤1: 固定策略（已禁用 AI 策略决策） ==========
                // 不再调 _strategyDecider.DecideStrategyAsync：固定只查约束
                // 其它检查项（安全/IO/卡滞/能量流）全部跳过，最大化响应速度
                var strategyDecision = new DiagnosticStrategyDecision
                {
                    SafetyCheck = false,
                    ConstraintCheck = true,
                    IoCheck = false,
                    StuckPointLocate = false,
                    EnergyFlowAnalysis = false,
                    Reason = "已禁用 AI 策略决策，固定只查约束",
                    ProblemType = "约束检查",
                    Priority = "高"
                };
                await diagnosisProgress.StrategyReadyAsync(cancellationToken);


                // 3. 获取实时信号字典
                // 优先使用请求中传入的数据，否则根据产线ID动态查询
                // 根据产线ID动态查询实时信号（获取该产线下所有PLC的数据）
                var plcs = await _pLC_ProjectService.GetListAsync(x => x.IsDeleted == false);
                if (request.ProjectIds is { Count: > 0 })
                    plcs = plcs.Where(plc => request.ProjectIds.Contains(plc.Id)).ToList();
                cancellationToken.ThrowIfCancellationRequested();
                // 按 projectid 分组的实时信号 —— 约束检查用，按 rule.ProjectId 隔离匹配，杜绝跨 PLC 同名信号串值
                var livePlcs = await GetLiveSignalsByPlcAsync(plcs, cancellationToken);
                // 拍平版 —— 安全检查 / 卡滞点定位用（暂不做 PLC 隔离，保持原行为）
                Dictionary<string, object> liveSignals = FlattenGroupedLiveSignals(livePlcs);

                // D-1：进度里的信号数 = Good 信号数
                await diagnosisProgress.SignalsLoadedAsync(livePlcs.Values.Sum(p => p.GoodCount), cancellationToken);

                // D-1：所有 PLC 都没采到 Good 信号 → 采集失败，无法诊断（绝不输出「未发现任何异常」）
                if (livePlcs.Values.Sum(p => p.GoodCount) == 0)
                {
                    await diagnosisProgress.ConstraintsEvaluatedAsync(0, cancellationToken);
                    await diagnosisProgress.ResultReadyAsync(0, cancellationToken);
                    var collectFailed = await BuildDiagnosticResponseAsync("collect_failed", new List<DiagnosticIssue>(),
                        new List<StuckPoint>(), null, livePlcs.Values.ToList());
                    await diagnosisProgress.CompletedAsync(collectFailed.IsSuccess, cancellationToken);
                    return collectFailed;
                }

                // 获取是否强制深度分析模式
                var forceDeepAnalysis = request.ForceDeepAnalysis;

                // 4. 从实时信号中提取涉及的PLC ID列表
                // 当前简化为：如果请求指定了plcId则使用，否则需要根据产线ID查询关联的PLC列表
                List<long> plcIds = plcs.Select(p => p.Id).ToList();

                // ========== 步骤2-4: 根据AI策略决策执行快速检查 ==========
                // 根据AI判断结果决定是否执行各项检查

                // 初始化空结果
                List<DiagnosticIssue> safetyIssues = new();
                List<DiagnosticIssue> constraintIssues = new();
                List<DiagnosticIssue> ioIssues = new();

                // 根据AI决策并行执行需要的检查
                var checkTasks = new List<Task>();
                Task<List<DiagnosticIssue>> safetyTask = null;
                Task<ConstraintCheckOutcome> constraintTask = null;
                ConstraintCheckOutcome constraintOutcome = null;

                // 安全检查
                if (strategyDecision.SafetyCheck)
                {
                    safetyTask = CheckSafetyAsync(plcIds, liveSignals);
                    checkTasks.Add(safetyTask);
                }
                else
                {
                }

                // 约束检查
                if (strategyDecision.ConstraintCheck)
                {   
                    constraintTask = CheckConstraintsAsync(plcIds, livePlcs, cancellationToken);
                    checkTasks.Add(constraintTask);
                }
                else
                {
                }

                // IO点位检查（已禁用，保留注释以便将来启用）
                // if (strategyDecision.IoCheck)
                // {
                //     checkTasks.Add(CheckIOTagsAsync(plcIds, liveSignals).ContinueWith(t => ioIssues = t.Result));
                // }
                // else
                // {
                // }

                // 等待所有检查任务完成
                if (checkTasks.Count > 0)
                {
                    await Task.WhenAll(checkTasks);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (safetyTask != null) safetyIssues = await safetyTask;
                    if (constraintTask != null)
                    {
                        constraintOutcome = await constraintTask;
                        constraintIssues = constraintOutcome.Violations;
                        ioIssues = constraintOutcome.IoIssues.ToList();
                    }
                }
                await diagnosisProgress.ConstraintsEvaluatedAsync(constraintIssues.Count, cancellationToken);

                // 合并所有快速检查发现的问题
                List<DiagnosticIssue> quickCheckIssues = new List<DiagnosticIssue>();
                quickCheckIssues.AddRange(safetyIssues);
                quickCheckIssues.AddRange(constraintIssues);
                quickCheckIssues.AddRange(ioIssues);

                // ========== 步骤5: 卡滞点定位（根据AI策略决策）==========
                List<StuckPoint> stuckPoints = new List<StuckPoint>();
                if (strategyDecision.StuckPointLocate)
                {
                    // 根据检查结果确定要分析的LogicUnit
                    // 优先使用请求中的LogicUnitId，如果没有则根据项目ID查询，再没有则查询所有
                    if (plcIds != null && plcIds.Count > 0)
                    {
                        // 没有LogicUnitId但有PlcIds，查询这些PLC下所有LogicUnit的卡滞点
                        stuckPoints = await LocateStuckPointsByProjectAsync(plcIds, liveSignals);
                    }
                    else
                    {
                        // 既没有LogicUnitId也没有ProjectId，查询所有LogicUnit的卡滞点（全量扫描）
                        stuckPoints = await LocateAllStuckPointsAsync(liveSignals);
                    }
                }
                else
                {
                }

                // 去重：按 VariableName + LogicUnitId 去重
                var distinctStuckPoints = stuckPoints
                    .GroupBy(x => new { x.VariableName, x.LogicUnitId })
                    .Select(g => g.First())
                    .ToList();

                if (distinctStuckPoints.Count != stuckPoints.Count)
                {
                    stuckPoints = distinctStuckPoints;
                }

                // ========== 步骤5: 规则检查完成，直接返回结果 ==========
                // 已禁用 AI 总结：跳过能量流深度分析、主诊断 Agent、交叉引用、自我反思
                // 约束/安全/IO/卡滞 全部按规则引擎结果直返，前端看到的就是约束表里命中的违反清单
                _ = forceDeepAnalysis; // 保留参数以维持接口兼容，但不再据此走 AI 路径

                string resultCategory;
                if (!string.IsNullOrWhiteSpace(constraintOutcome?.FailureMessage)) resultCategory = "check_failed";
                else if (safetyIssues.Any()) resultCategory = "safety";
                else if (constraintIssues.Any()) resultCategory = "constraint";
                else if (ioIssues.Any()) resultCategory = "io_tag";
                else if (stuckPoints.Any()) resultCategory = "stuck";
                else if (quickCheckIssues.Any()) resultCategory = "warning";
                else if (constraintOutcome != null && constraintOutcome.UndeterminedCount > 0) resultCategory = "partial_undetermined";
                else resultCategory = "normal";

                await diagnosisProgress.ResultReadyAsync(quickCheckIssues.Count, cancellationToken);
                var result = await BuildDiagnosticResponseAsync(resultCategory, quickCheckIssues, stuckPoints,
                    constraintOutcome, livePlcs.Values.ToList());
                await diagnosisProgress.CompletedAsync(result.IsSuccess, cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await diagnosisProgress.FailedAsync(CancellationToken.None);
                var genericErrorMessage = "诊断过程发生内部错误，请稍后重试或联系管理员";
                var errorId = Guid.NewGuid().ToString("N")[..8];

                return new HttpResponseResultModel<string>
                {
                    IsSuccess = false,
                    HttpStatusCode = HttpStatusCode.InternalServerError,
                    ErrorMessage = genericErrorMessage,
                    ExceptionMessage = $"ErrorId: {errorId}",
                    BackResult = JsonSerializer.Serialize(new
                    {
                        error = "诊断过程异常",
                        errorId = errorId,
                        message = genericErrorMessage
                    })
                };
            }
        }


        #region 诊断步骤方法

        /// <summary>
        /// 安全检查：检查安全类IO点位是否被触发（如急停、安全门、光幕）
        /// 并行遍历所有IsSafety=true的点位，对比实时信号值
        /// </summary>
        /// <param name="plcIds">PLC ID列表，用于查询安全点位</param>
        /// <param name="liveSignals">实时信号字典，Key=变量名，Value=当前值</param>
        /// <returns>安全问题列表</returns>
        private async Task<List<DiagnosticIssue>> CheckSafetyAsync(
            List<long> plcIds,
            Dictionary<string, object> liveSignals)
        {

            try
            {
                // 获取所有安全相关点位
                List<PLC_IOTagEntity> safetyTags;
                if (plcIds != null && plcIds.Count > 0)
                {
                    safetyTags = await _pLC_IOTagService.GetListAsync(x => plcIds.Contains(x.ProjectId ?? 0) && x.IsSafety == true && x.ConstraintStatus != 6);
                }
                else
                {
                    safetyTags = await _pLC_IOTagService.GetListAsync(x => x.IsSafety == true && x.ConstraintStatus != 6);
                }

                if (safetyTags.Count == 0)
                {
                    return new List<DiagnosticIssue>();
                }


                // 使用ConcurrentBag支持并行写入
                var issuesBag = new System.Collections.Concurrent.ConcurrentBag<DiagnosticIssue>();

                // 使用Parallel并行检查
                Parallel.ForEach(safetyTags, tag =>
                {
                    if (TryResolveLiveSignalBool(liveSignals, tag.TagName, out var signalBool))
                    {
                        var expectedSafeValue = false;
                        if (!string.IsNullOrWhiteSpace(tag.Normal_Value) && bool.TryParse(tag.Normal_Value, out var parsedNormal))
                        {
                            expectedSafeValue = parsedNormal;
                        }

                        if (signalBool != expectedSafeValue)
                        {
                            issuesBag.Add(new DiagnosticIssue
                            {
                                Variable = tag.TagName,
                                Status = signalBool.ToString(),
                                Reason = $"安全变量状态异常: {tag.Comment ?? tag.TagName}，当前={signalBool}，期望={expectedSafeValue}",
                                Severity = "严重",
                                Category = "安全",
                                Address = tag.Address,
                                RelatedRule = null
                            });

                        }
                    }
                });

                var issues = issuesBag.ToList();
                return issues;
            }
            catch (Exception)
            {
                return new List<DiagnosticIssue>();
            }
        }

        /// <summary>
        /// 约束检查（D-1 三值判定）：每条规则用所属 PLC 的 Good 值 + 未采到信号原因，经 ConstraintRuleEvaluator 判定
        /// 违规 / 通过 / 无法判定。未采到的信号绝不当成通过；坏点只影响依赖它的规则。
        /// VariableAccess / IoTag 预过滤保持原样（属于规则范围裁剪，不是采集失败）。
        /// </summary>
        /// <param name="plcIds">PLC ID列表，用于查询约束规则</param>
        /// <param name="livePlcs">按 projectid 分组的诊断实时信号</param>
        /// <returns>违规 / 无法判定清单与计数；执行异常写入 FailureMessage</returns>
        public async Task<IReadOnlyList<Original353ManualModeRead>> ReadOperatingModesAsync(
            List<long> projectIds, CancellationToken cancellationToken = default)
        {
            var read = ManualModeReadOverride;
            if (read is null && _manualModeReader is not null) read = _manualModeReader.ReadAsync;
            if (read is null || projectIds.Count == 0) return [];
            var rules = await _pLC_IOConstraintRuleService.GetListAsync(r => !r.IsDeleted && projectIds.Contains(r.ProjectId ?? 0));
            var modes = new List<Original353ManualModeRead>();
            foreach (var group in rules.Where(r => r.ProjectId.HasValue && projectIds.Contains(r.ProjectId.Value)).GroupBy(r => r.ProjectId.Value))
            {
                var bindings = Original353ManualModeMetadata.Read(group);
                if (bindings.Count > 0) modes.Add(await read(group.Key, bindings, cancellationToken));
            }
            return modes;
        }

        internal async Task<ConstraintCheckOutcome> CheckConstraintsAsync(
            List<long> plcIds,
            Dictionary<long, PlcLiveSignals> livePlcs,
            CancellationToken cancellationToken = default)
        {
            var outcome = new ConstraintCheckOutcome();
            var constraintLogEntries = new List<string>(); // 用于记录所有约束的原始数据
            var rawViolations = new List<DiagnosticIssue>();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 批量获取所有约束规则（含 Safety 类，见 2026-05-26：Safety 约束同样走 CheckConstraintViolation）
                List<PLC_IOConstraintRuleEntity> rules;
                if (plcIds != null && plcIds.Count > 0)
                {
                    rules = await _pLC_IOConstraintRuleService.GetListAsync(x => plcIds.Contains(x.ProjectId ?? 0) && x.IsDeleted == false);
                }
                else
                {
                    rules = await _pLC_IOConstraintRuleService.GetListAsync(x => x.IsDeleted == false);
                }
                cancellationToken.ThrowIfCancellationRequested();

                if (rules.Count == 0)
                {
                    return outcome;
                }

                var modeRead = ManualModeReadOverride;
                if (modeRead is null && _manualModeReader is not null)
                    modeRead = _manualModeReader.ReadAsync;
                if (modeRead is not null)
                {
                    var manualProjects = new HashSet<long>();
                    var handledModeRules = new HashSet<long>();
                    foreach (var group in rules.Where(rule => rule.ProjectId.HasValue
                        && (rule.Source is "XMLV2" or "XMLV2_TIMED" or "XMLV2_REVIEW" or "XMLV2_MODE"))
                        .GroupBy(rule => rule.ProjectId!.Value))
                    {
                        var bindings = HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353
                            .Original353ManualModeMetadata.Read(group);
                        if (bindings.Count == 0) continue;
                        var mode = await modeRead(group.Key, bindings, cancellationToken);
                        foreach (var rule in group.Where(r => r.Source == "XMLV2_MODE"))
                        {
                            var evaluation = Original353ModeConflictRules.Evaluate(rule, bindings, mode.Values);
                            outcome.Record(evaluation);
                            if (evaluation.Verdict == RuleVerdict.Violated) rawViolations.Add(evaluation.Issue);
                            handledModeRules.Add(rule.Id);
                        }
                        // Without a source-proven rule-to-instance link, suppress this project's
                        // ordinary instant rules if any instance is confirmed manual.
                        if (mode.Status != "Manual" && mode.ManualGroups.Count == 0) continue;
                        manualProjects.Add(group.Key);
                        constraintLogEntries.Add($"PLC {group.Key} 已读到手动模式：跳过该 PLC 的约束判断，转为 IO 提示。");
                    }
                    outcome.SetViolations(rawViolations);
                    rules = rules.Where(rule => !handledModeRules.Contains(rule.Id)).ToList();
                    if (manualProjects.Count > 0)
                    {
                        var names = manualProjects.OrderBy(id => id).Select(id =>
                            livePlcs is not null && livePlcs.TryGetValue(id, out var plc)
                                && !string.IsNullOrWhiteSpace(plc.PlcName) ? plc.PlcName : $"PLC {id}");
                        outcome.IoIssues.Add(new DiagnosticIssue {
                            Variable = "产线手动模式",
                            Status = "true",
                            Category = "IO_TAG",
                            Severity = "Info",
                            Reason = $"IO故障，疑似手动模式触发（{string.Join("、", names)}），请检查当前操作状态。",
                            ExpectedValue = "检查手动模式下的 I/O 状态"
                        });
                        rules = rules.Where(rule => !manualProjects.Contains(rule.ProjectId ?? 0)).ToList();
                        if (rules.Count == 0) return outcome;
                    }
                }

                // XMLV2 stores every recognized rule for test comparison, but the legacy
                // evaluator has only a single live frame. Record guarded/unproven rules as
                // undetermined before loading their signals; never reinterpret them as faults.
                var deferredRules = rules.Where(rule =>
                    !HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353DiagnosisGate.CanEvaluateInstantly(rule)).ToArray();
                foreach (var deferred in deferredRules)
                {
                    outcome.Record(new RuleEvaluation
                    {
                        Verdict = RuleVerdict.Undetermined,
                        Bucket = UndeterminedBucket.Other,
                        Issue = new DiagnosticIssue
                        {
                            Variable = deferred.RuleName ?? $"规则 {deferred.Id}",
                            Status = "Unknown",
                            Category = "CONSTRAINT",
                            Severity = "Info",
                            ConstraintOperator = deferred.Operator,
                            RelatedRule = deferred.RuleName,
                            Reason = deferred.Source == "XMLV2_TIMED"
                                ? "已识别反馈关系，缺可信动作持续时间/旁路证据，暂不判故障"
                                : "已识别约束，但当前单帧诊断缺少执行位置或源码证明，暂不判故障"
                        }
                    });
                }
                rules = rules.Where(HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353DiagnosisGate.CanEvaluateInstantly).ToList();
                if (rules.Count == 0) return outcome;

                // 批量获取所有规则关联的信号（避免N+1查询）
                var ruleIds = rules.Select(r => r.Id).ToList();
                var allSignals = await _pLC_IOConstraintSignalService.GetListAsync(x => ruleIds.Contains(x.RuleId));
                cancellationToken.ThrowIfCancellationRequested();

                // 过滤掉没有逻辑片段的信号：查询VariableAccess表，只保留在逻辑片段中存在的信号
                var signalNames = allSignals.Select(s => s.SignalName).Distinct().ToList();
                var signalsWithLogicContext = new HashSet<string>();
                if (signalNames.Count > 0)
                {
                    var variableAccesses = await _pLC_VariableAccessService.GetListAsync(x =>
                        signalNames.Contains(x.VariableName) && !x.IsDeleted);
                    cancellationToken.ThrowIfCancellationRequested();

                    signalsWithLogicContext = new HashSet<string>(
                        variableAccesses.Select(va => va.VariableName).Distinct(),
                        StringComparer.OrdinalIgnoreCase);
                }

                // IMPLIES 的 Target（纯输入反馈）与 REQUIRES_0_WHEN_FALSE 的全部信号（Safety Channel 物理信号）豁免 VariableAccess 过滤
                var impliesRuleIds = new HashSet<long>(
                    rules
                        .Where(r => string.Equals(r.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.Id));
                var reqWhenFalseRuleIds = new HashSet<long>(
                    rules
                        .Where(r => string.Equals(r.Operator, "REQUIRES_0_WHEN_FALSE", StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.Id));

                var filteredSignals = allSignals
                    .Where(s => signalsWithLogicContext.Contains(s.SignalName)
                        || (impliesRuleIds.Contains(s.RuleId)
                            && string.Equals(s.SignalRole, "Target", StringComparison.OrdinalIgnoreCase))
                        || reqWhenFalseRuleIds.Contains(s.RuleId))
                    .ToList();

                // 批量获取 IO Tags 用于翻译信号名（已过滤ConstraintStatus!=6和Comment不含"弃用"）
                List<PLC_IOTagEntity> ioTags;
                if (plcIds != null && plcIds.Count > 0)
                {
                    ioTags = await _pLC_IOTagService.GetListAsync(x => plcIds.Contains(x.ProjectId ?? 0) && x.IsDeleted == false && x.ConstraintStatus != 6 && (x.Comment == null || !x.Comment.Contains("弃用")));
                }
                else
                {
                    ioTags = await _pLC_IOTagService.GetListAsync(x => x.IsDeleted == false && x.ConstraintStatus != 6 && (x.Comment == null || !x.Comment.Contains("弃用")));
                }
                cancellationToken.ThrowIfCancellationRequested();

                // 全局粗过滤用（TagName 可能重复 / 仅大小写不同，取第一条；空点名剔除——
                // 一对大小写变体或一个空点名曾让 ToDictionary 抛出，整次检查所有 PLC 都 check_failed）
                var signalNameToTag = ioTags
                    .Where(t => !string.IsNullOrEmpty(t.TagName))
                    .GroupBy(t => t.TagName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // 按 projectid 分组的 IO Tag 映射（2026-05-29：同名裸地址跨 PLC 不串）
                var ioTagByProject = ioTags
                    .Where(t => t.ProjectId.HasValue && !string.IsNullOrEmpty(t.TagName))
                    .GroupBy(t => t.ProjectId.Value)
                    .ToDictionary(
                        g => g.Key,
                        g => g.GroupBy(t => t.TagName, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(gg => gg.Key, gg => gg.First(), StringComparer.OrdinalIgnoreCase));

                // FB 程序体证明的急停门控（Source=XMLV2）：Source 常是 DB 变量、不在 IO 点表，放行并在判定前精确读补值
                var bodyGateRuleIds = new HashSet<long>(rules.Where(IsBodyProvenGate).Select(r => r.Id));
                filteredSignals = filteredSignals
                    .Where(s => signalNameToTag.ContainsKey(s.SignalName)
                        || (bodyGateRuleIds.Contains(s.RuleId)
                            && string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                var signalsByRule = filteredSignals.GroupBy(s => s.RuleId).ToDictionary(g => g.Key, g => g.ToList());

                // 门控 DB 源读数单独存放（不写进 PlcLiveSignals），只给门控规则的 Source 预填，
                // 避免非门控规则的后缀兜底借到这些 DB 值
                var gateDbSignals = new Dictionary<long, DbSignalSet>();
                var ruleProject = rules.ToDictionary(r => r.Id, r => r.ProjectId ?? 0);
                foreach (var group in filteredSignals
                    .Where(s => bodyGateRuleIds.Contains(s.RuleId)
                        && string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(s => ruleProject[s.RuleId]))
                {
                    if (livePlcs == null || !livePlcs.TryGetValue(group.Key, out var gateLive)) continue;
                    var known = ioTagByProject.TryGetValue(group.Key, out var knownTags) ? knownTags : null;
                    var dbSet = new DbSignalSet();
                    gateDbSignals[group.Key] = dbSet;
                    await ConstraintDbSignals.MergeAsync(group.Key,
                        group.Select(s => s.SignalName).Where(n => (known == null || !known.ContainsKey(n))
                            && !gateLive.Values.ContainsKey(n) && !gateLive.Statuses.ContainsKey(n)),
                        dbSet,
                        (pid, requests, ct) => _pLC_VariableAccessService.ReadExactDataAsync(pid, requests, ct),
                        cancellationToken);
                }

                constraintLogEntries.Add("========== 约束检查原始数据 ==========");
                constraintLogEntries.Add($"检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                constraintLogEntries.Add($"产线/PLC: {(plcIds != null && plcIds.Any() ? string.Join(", ", plcIds) : "全部")}");
                constraintLogEntries.Add("");
                constraintLogEntries.Add("========== 所有约束规则详情 ==========");

                foreach (var rule in rules)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353DiagnosisGate.CanEvaluateInstantly(rule))
                    {
                        constraintLogEntries.Add($"规则 [{rule.Id}] 已识别，缺可信动作时序/上下文，当前快照不判故障。");
                        continue;
                    }
                    if (!signalsByRule.TryGetValue(rule.Id, out var signals) || signals.Count == 0)
                    {
                        continue;
                    }

                    // 按本约束的 ProjectId 取对应 PLC 的实时信号 + IO Tag 子映射（杜绝跨 PLC 同名裸地址串值）
                    var rulePid = rule.ProjectId ?? 0;
                    var plcLive = livePlcs != null && livePlcs.TryGetValue(rulePid, out var pl)
                        ? pl
                        : PlcLiveSignals.Failed(rulePid, rulePid.ToString(), PLC_DiagnosticProjectStatus.ProjectUnavailable,
                            "未读取该约束所属 PLC 的实时信号");
                    var projSignalNameToTag = ioTagByProject.TryGetValue(rulePid, out var pts)
                        ? pts : new Dictionary<string, PLC_IOTagEntity>(StringComparer.OrdinalIgnoreCase);

                    var signalValues = new Dictionary<string, bool>();
                    var unknownSignals = new Dictionary<string, SignalUnknownReason>();
                    if (bodyGateRuleIds.Contains(rule.Id) && gateDbSignals.TryGetValue(rulePid, out var gateDbSet))
                    {
                        // 门控 Source 先用单独读到的 DB 值 / 原因预填；Resolve 跳过已预填的名字
                        gateDbSet.Fill(signals.Where(s => string.Equals(s.SignalRole, "Source", StringComparison.OrdinalIgnoreCase)),
                            signalValues, unknownSignals);
                    }
                    // 本 PLC IO 表里已有的点名若未参与读取 → Missing，不许后缀兜底借别的点的值
                    plcLive.Resolve(signals, signalValues, unknownSignals, projSignalNameToTag.ContainsKey);

                    constraintLogEntries.Add("");
                    constraintLogEntries.Add($"--- 约束规则 [{rule.Id}] ---");
                    constraintLogEntries.Add($"  规则名称: {rule.RuleName}");
                    constraintLogEntries.Add($"  规则类型: {rule.RuleType}");
                    constraintLogEntries.Add($"  约束 Operator: {rule.Operator}");
                    constraintLogEntries.Add($"  优先级: {rule.Priority}");
                    constraintLogEntries.Add($"  描述: {rule.Description}");
                    constraintLogEntries.Add($"  涉及信号数量: {signals.Count}");
                    constraintLogEntries.Add("  信号列表:");
                    foreach (var signal in signals)
                    {
                        var currentValue = signalValues.TryGetValue(signal.SignalName, out var v)
                            ? v.ToString()
                            : unknownSignals.TryGetValue(signal.SignalName, out var why)
                                ? $"未采到（{why.Label}：{why.Detail}）"
                                : "未找到";
                        var tag = projSignalNameToTag.ContainsKey(signal.SignalName) ? projSignalNameToTag[signal.SignalName] : null;
                        constraintLogEntries.Add($"    - 信号名: {signal.SignalName}");
                        constraintLogEntries.Add($"      当前值: {currentValue}");
                        constraintLogEntries.Add($"      地址: {tag?.Address ?? "N/A"}");
                        constraintLogEntries.Add($"      描述: {tag?.Comment ?? "N/A"}");
                        constraintLogEntries.Add($"      角色: {signal.SignalRole}");
                        constraintLogEntries.Add($"      排序: {signal.SortOrder}");
                    }

                    var evaluation = ConstraintRuleEvaluator.Evaluate(rule, signals, signalValues, unknownSignals, projSignalNameToTag);
                    if (evaluation.Verdict == RuleVerdict.Violated && bodyGateRuleIds.Contains(rule.Id))
                    {
                        // DB 源与 %Q 输出可能不在同一台 IGW 设备、不是同一时刻的值：二次读取都违反才报
                        evaluation = await RecheckGateViolationAsync(rule, signals, projSignalNameToTag, cancellationToken);
                        if (evaluation.Verdict != RuleVerdict.Violated)
                            constraintLogEntries.Add($"  复核读取未再现违反：{evaluation.Issue?.Reason}");
                    }
                    outcome.Record(evaluation);
                    switch (evaluation.Verdict)
                    {
                        case RuleVerdict.Violated:
                            evaluation.Issue.RelatedRule = rule.RuleName ?? rule.RuleType;
                            rawViolations.Add(evaluation.Issue);
                            constraintLogEntries.Add("");
                            constraintLogEntries.Add("  >>> 违反状态: VIOLATED <<<");
                            constraintLogEntries.Add($"  违反原因: {evaluation.Issue.Reason}");
                            constraintLogEntries.Add($"  严重级别: {evaluation.Issue.Severity}");
                            break;
                        case RuleVerdict.Undetermined:
                            constraintLogEntries.Add($"  状态: UNDETERMINED（{evaluation.Issue.Reason}）");
                            break;
                        default:
                            constraintLogEntries.Add("  状态: OK (通过)");
                            break;
                    }
                }

                // 已禁用约束 AI 复核：违反直接透传，仅做同类去重合并
                outcome.SetViolations(MergeDuplicateConstraintIssues(rawViolations));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // D-1：检查自身失败不能静默成「没有违规」；已判定的违规照常给出，保证计数与清单一致
                outcome.SetViolations(MergeDuplicateConstraintIssues(rawViolations));
                outcome.FailureMessage = "约束检查执行失败: " + ex.GetType().Name + ": " + ex.Message;
                return outcome;
            }

            // 调试日志只是旁路记录：写失败不影响已完成的判定
            try
            {
                await ConstraintLogWriter(
                    $"constraint_check_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..8]}.log",
                    constraintLogEntries,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[约束检查] 调试日志写入失败，不影响检查结果");
            }

            return outcome;
        }

        /// <summary>FB 程序体证明的急停门控：XMLV2 即时规则 + REQUIRES_0_WHEN_FALSE + ValidStates 里的门控证明标记。</summary>
        private static bool IsBodyProvenGate(PLC_IOConstraintRuleEntity rule) =>
            rule.Source == "XMLV2" && string.Equals(rule.Operator, "REQUIRES_0_WHEN_FALSE", StringComparison.OrdinalIgnoreCase)
            && HasGateProof(rule.ValidStates);

        private static bool HasGateProof(string validStates)
        {
            if (string.IsNullOrWhiteSpace(validStates)) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(validStates);
                return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("Proof", out var proof)
                    && proof.ValueKind == System.Text.Json.JsonValueKind.String
                    && proof.GetString() == HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Original353FbContractRules.GateProof;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        private async Task<RuleEvaluation> RecheckGateViolationAsync(PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals, Dictionary<string, PLC_IOTagEntity> tags, CancellationToken ct)
        {
            var requests = signals
                .Select(s => tags.TryGetValue(s.SignalName, out var tag) && !string.IsNullOrEmpty(tag.TableName)
                    ? new PLC_ExactReadRequest(s.SignalName, tag.TableName, tag.TagName)
                    : ConstraintDbSignals.Request(s.SignalName))
                .Where(r => r != null)
                .DistinctBy(r => r.RequestKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var statuses = new Dictionary<string, SignalUnknownReason>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var results = await _pLC_VariableAccessService.ReadExactDataAsync(rule.ProjectId ?? 0, requests, ct);
                ConstraintDbSignals.Apply(requests, results, values, statuses);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return RecheckUndeterminedResult(rule, "复核读取失败：" + ex.Message, UndeterminedBucket.CommFailure);
            }
            var goodValues = new Dictionary<string, bool>();
            var unknown = new Dictionary<string, SignalUnknownReason>();
            foreach (var signal in signals)
            {
                if (values.TryGetValue(signal.SignalName, out var raw) && PlcLiveSignals.TryConvertToBool(raw, out var value))
                    goodValues[signal.SignalName] = value;
                else
                    unknown[signal.SignalName] = statuses.TryGetValue(signal.SignalName, out var why)
                        ? why : new SignalUnknownReason(SignalUnknownReason.NotBoolean, "复核值不是布尔量");
            }
            var second = ConstraintRuleEvaluator.Evaluate(rule, signals, goodValues, unknown, tags);
            // 复核再违反 → 报；复核自身无法判定 → 沿用其原因与类别；复核通过 → 信号在变化
            return second.Verdict == RuleVerdict.Passed
                ? RecheckUndeterminedResult(rule, "首次读取判为违反，复核读取未再现（信号在变化）", UndeterminedBucket.Other)
                : second;
        }

        private static RuleEvaluation RecheckUndeterminedResult(PLC_IOConstraintRuleEntity rule, string reason,
            UndeterminedBucket bucket) => new()
        {
            Verdict = RuleVerdict.Undetermined,
            Bucket = bucket,
            Issue = new DiagnosticIssue
            {
                Variable = rule.RuleName ?? $"规则 {rule.Id}",
                Status = "Unknown",
                Category = "CONSTRAINT",
                Severity = "Info",
                ConstraintOperator = rule.Operator,
                RelatedRule = rule.RuleName,
                Reason = reason
            }
        };

        private static async Task WriteConstraintLogFileAsync(string fileName, IEnumerable<string> lines, CancellationToken cancellationToken)
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logPath);
            await File.WriteAllLinesAsync(Path.Combine(logPath, fileName), lines, cancellationToken);
        }

        private async Task<List<PreliminaryViolationRecord>> ReviewConstraintViolationsWithAiAsync(
            List<PreliminaryViolationRecord> preliminaryViolations,
            List<long> plcIds,
            Dictionary<string, PLC_IOTagEntity> signalNameToTag,
            Dictionary<string, List<long>> signalToLogicUnitIds,
            List<string> constraintLogEntries)
        {
            if (preliminaryViolations == null || preliminaryViolations.Count == 0)
            {
                return preliminaryViolations ?? new List<PreliminaryViolationRecord>();
            }

            try
            {
                var candidates = preliminaryViolations
                    .Where(ShouldRunAiConstraintReview)
                    .OrderByDescending(x => x.Signals?.Count ?? 0)
                    .ThenByDescending(x => x.SignalValues?.Count ?? 0)
                    .Take(MaxConstraintAiReviewCount)
                    .ToList();

                if (candidates.Count == 0)
                {
                    return preliminaryViolations;
                }

                var allLogicUnitIds = candidates
                    .SelectMany(record => record.Signals ?? new List<PLC_IOConstraintSignalEntity>())
                    .Where(signal => !string.IsNullOrWhiteSpace(signal.SignalName))
                    .SelectMany(signal => signalToLogicUnitIds.TryGetValue(signal.SignalName, out var ids) ? ids : new List<long>())
                    .Distinct()
                    .ToList();

                var logicUnits = allLogicUnitIds.Count > 0
                    ? await _pLC_LogicUnitService.GetListAsync(x => allLogicUnitIds.Contains(x.Id) && x.IsDeleted == false)
                    : new List<PLC_LogicUnitEntity>();
                var logicUnitById = logicUnits.ToDictionary(x => x.Id, x => x);

                var blockIds = logicUnits
                    .Where(x => x.BlockId.HasValue)
                    .Select(x => x.BlockId!.Value)
                    .Distinct()
                    .ToList();
                var blocks = blockIds.Count > 0
                    ? await _pLC_BlockService.GetListAsync(x => blockIds.Contains(x.Id) && x.IsDeleted == false)
                    : new List<PLC_BlockEntity>();
                var blockById = blocks.ToDictionary(x => x.Id, x => x);

                var filtered = new List<PreliminaryViolationRecord>();
                var candidateIds = new HashSet<long>(candidates.Select(x => x.Rule.Id));

                foreach (var record in preliminaryViolations)
                {
                    if (!candidateIds.Contains(record.Rule.Id))
                    {
                        filtered.Add(record);
                        continue;
                    }

                    var review = await ReviewSingleConstraintViolationWithAiAsync(
                        record,
                        plcIds,
                        signalNameToTag,
                        signalToLogicUnitIds,
                        logicUnitById,
                        blockById);

                    lock (constraintLogEntries)
                    {
                        constraintLogEntries.Add("");
                        constraintLogEntries.Add($"  >>> AI复核: RuleId={record.Rule.Id}, RuleName={record.Rule.RuleName}");
                        constraintLogEntries.Add($"      决策: {review?.Decision ?? "none"}");
                        constraintLogEntries.Add($"      置信度: {review?.Confidence.ToString("0.00") ?? "0"}");
                        constraintLogEntries.Add($"      说明: {review?.ReviewerSummary ?? "无"}");
                    }

                    if (review == null)
                    {
                        filtered.Add(record);
                        continue;
                    }

                    var normalizedDecision = review.Decision?.Trim().ToLowerInvariant();
                    if ((normalizedDecision == "reject" || normalizedDecision == "drop")
                        && review.Confidence >= 0.80)
                    {
                        continue;
                    }

                    ApplyAiReviewToViolation(record.Violation, review);
                    filtered.Add(record);
                }

                return filtered;
            }
            catch (Exception)
            {
                return preliminaryViolations;
            }
        }

        private List<DiagnosticIssue> MergeDuplicateConstraintIssues(List<DiagnosticIssue> issues)
        {
            if (issues == null || issues.Count <= 1)
            {
                return issues ?? new List<DiagnosticIssue>();
            }

            var merged = new Dictionary<string, DiagnosticIssue>(StringComparer.OrdinalIgnoreCase);
            foreach (var issue in issues)
            {
                var key = BuildConstraintIssueDedupKey(issue);
                if (!merged.TryGetValue(key, out var existing))
                {
                    merged[key] = issue;
                    continue;
                }

                merged[key] = MergeConstraintIssue(existing, issue);
            }

            return merged.Values.ToList();
        }

        private static string BuildConstraintIssueDedupKey(DiagnosticIssue issue)
        {
            if (issue == null)
            {
                return string.Empty;
            }

            var operatorName = issue.ConstraintOperator ?? string.Empty;
            var signalDetails = issue.SignalDetails ?? new List<SignalDetail>();

            static string JoinNames(IEnumerable<string> names) => string.Join("|",
                names.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            if (operatorName.Contains("IMPLIES", StringComparison.OrdinalIgnoreCase))
            {
                var sourceNames = JoinNames(signalDetails
                    .Where(x => (x.ExpectedValue ?? string.Empty).Contains("Source", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.SignalName));
                var failingTargets = JoinNames(signalDetails
                    .Where(x => (x.ExpectedValue ?? string.Empty).Contains("Target", StringComparison.OrdinalIgnoreCase) && !x.CurrentValue)
                    .Select(x => x.SignalName));
                return $"IMPLIES::{sourceNames}=>{failingTargets}";
            }

            if (operatorName.Contains("ALL_TRUE", StringComparison.OrdinalIgnoreCase))
            {
                var falseMembers = JoinNames(signalDetails
                    .Where(x => !x.CurrentValue)
                    .Select(x => x.SignalName));
                return $"ALL_TRUE::{falseMembers}";
            }

            if (operatorName.Contains("AT_MOST_ONE", StringComparison.OrdinalIgnoreCase))
            {
                var trueMembers = JoinNames(signalDetails
                    .Where(x => x.CurrentValue)
                    .Select(x => x.SignalName));
                return $"AT_MOST_ONE::{trueMembers}";
            }

            if (operatorName.Contains("REQUIRES_0", StringComparison.OrdinalIgnoreCase))
            {
                var trueTargets = JoinNames(signalDetails
                    .Where(x => x.CurrentValue)
                    .Select(x => x.SignalName));
                return $"REQUIRES_0::{trueTargets}";
            }

            if (operatorName.Contains("SAME_STATE", StringComparison.OrdinalIgnoreCase))
            {
                // SAME_STATE 违反 key：把 Member 按当前值分两组拼接，定位"哪些 true / 哪些 false"
                var trueMembers = JoinNames(signalDetails.Where(x => x.CurrentValue).Select(x => x.SignalName));
                var falseMembers = JoinNames(signalDetails.Where(x => !x.CurrentValue).Select(x => x.SignalName));
                return $"SAME_STATE::T[{trueMembers}]/F[{falseMembers}]";
            }

            return $"{operatorName}::{issue.Variable}::{issue.Status}";
        }

        private static DiagnosticIssue MergeConstraintIssue(DiagnosticIssue left, DiagnosticIssue right)
        {
            if (left == null)
            {
                return right;
            }

            if (right == null)
            {
                return left;
            }

            var winner = (right.SignalDetails?.Count ?? 0) > (left.SignalDetails?.Count ?? 0) ? right : left;
            var loser = ReferenceEquals(winner, left) ? right : left;

            winner.RelatedRule = MergeTextDistinct(winner.RelatedRule, loser.RelatedRule, "；");
            winner.Note = MergeTextDistinct(winner.Note, loser.Note, " | ");

            var mergedSignalDetails = (winner.SignalDetails ?? new List<SignalDetail>())
                .Concat(loser.SignalDetails ?? new List<SignalDetail>())
                .GroupBy(x => x.SignalName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            winner.SignalDetails = mergedSignalDetails;

            return winner;
        }

        private static string MergeTextDistinct(string left, string right, string separator)
        {
            var items = new[] { left, right }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .SelectMany(x => x.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries))
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return items.Count == 0 ? left ?? right : string.Join(separator, items);
        }

        private bool ShouldRunAiConstraintReview(PreliminaryViolationRecord record)
        {
            if (record?.Rule == null || record.Signals == null || record.Signals.Count == 0)
            {
                return false;
            }

            var operatorName = record.Rule.Operator?.Trim().ToUpperInvariant();
            var roleCount = record.Signals
                .Select(x => x.SignalRole ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (record.Signals.Count >= 3)
            {
                return true;
            }

            if (roleCount >= 2)
            {
                return true;
            }

            return operatorName is "ALL_TRUE" or "IMPLIES" or "AT_MOST_ONE" or "REQUIRES_0" or "REQUIRES_0_WHEN_FALSE" or "SAME_STATE";
        }

        private async Task<ConstraintAiReviewResult> ReviewSingleConstraintViolationWithAiAsync(
            PreliminaryViolationRecord record,
            List<long> plcIds,
            Dictionary<string, PLC_IOTagEntity> signalNameToTag,
            Dictionary<string, List<long>> signalToLogicUnitIds,
            Dictionary<long, PLC_LogicUnitEntity> logicUnitById,
            Dictionary<long, PLC_BlockEntity> blockById)
        {
            try
            {
                var relatedLogicUnitIds = (record.Signals ?? new List<PLC_IOConstraintSignalEntity>())
                    .Where(s => !string.IsNullOrWhiteSpace(s.SignalName))
                    .SelectMany(s => signalToLogicUnitIds.TryGetValue(s.SignalName, out var ids) ? ids : new List<long>())
                    .Distinct()
                    .Take(8)
                    .ToList();

                var logicUnitsPayload = relatedLogicUnitIds
                    .Where(logicUnitById.ContainsKey)
                    .Select(id =>
                    {
                        var unit = logicUnitById[id];
                        var block = unit.BlockId.HasValue && blockById.TryGetValue(unit.BlockId.Value, out var blockEntity)
                            ? blockEntity
                            : null;
                        return new ConstraintLogicUnitPayload
                        {
                            LogicUnitId = unit.Id,
                            BlockId = unit.BlockId,
                            BlockName = block?.BlockName,
                            ProgrammingLanguage = unit.ProgrammingLanguage,
                            Title = unit.Title,
                            RawContent = TrimForAi(unit.RawContent, ConstraintAiRawContentMaxLength),
                            UidSymbolMapJson = TrimForAi(unit.UidSymbolMapJson, 3000)
                        };
                    })
                    .ToList();

                var deterministicEvidence = BuildDeterministicConstraintEvidence(
                    relatedLogicUnitIds,
                    logicUnitById,
                    blockById,
                    signalNameToTag.Keys);
                var deterministicReview = TryResolveByDeterministicConstraintEvidence(record, deterministicEvidence);
                if (deterministicReview != null)
                {
                    return deterministicReview;
                }

                var signalPayload = (record.Signals ?? new List<PLC_IOConstraintSignalEntity>())
                    .Select(signal =>
                    {
                        signalNameToTag.TryGetValue(signal.SignalName, out var tag);
                        return new
                        {
                            signal.SignalName,
                            signal.SignalRole,
                            signal.SortOrder,
                            CurrentValue = record.SignalValues != null && record.SignalValues.TryGetValue(signal.SignalName, out var current)
                                ? current
                                : (bool?)null,
                            Address = tag?.Address,
                            Comment = tag?.Comment,
                            LogicUnitIds = signalToLogicUnitIds.TryGetValue(signal.SignalName, out var ids) ? ids : new List<long>()
                        };
                    })
                    .ToList();

                var payload = new
                {
                    PlcIds = plcIds,
                    Rule = new
                    {
                        record.Rule.Id,
                        record.Rule.RuleName,
                        record.Rule.Operator,
                        record.Rule.RuleType,
                        record.Rule.Description,
                        record.Rule.Priority
                    },
                    DeterministicIssue = record.Violation,
                    Signals = signalPayload,
                    LogicUnits = logicUnitsPayload,
                    DeterministicConstraintEvidence = deterministicEvidence
                };

                var promptBuilder = new StringBuilder();
                promptBuilder.AppendLine("你是PLC约束诊断复核专家。");
                promptBuilder.AppendLine("任务：判断下面这条“已被程序判定为异常”的约束，是否真的应该报异常。");
                promptBuilder.AppendLine("重点：必须结合规则角色(Source/Member/Target)、程序块XML、可能存在的中间块、以及实时值一起判断。");
                promptBuilder.AppendLine("要求：");
                promptBuilder.AppendLine("1. 如果当前异常不成立，返回 reject。");
                promptBuilder.AppendLine("2. 如果当前异常成立，返回 confirm。");
                promptBuilder.AppendLine("3. 如果异常方向、对象或描述需要修正，但仍属于异常，返回 correct。");
                promptBuilder.AppendLine("4. 不要凭空猜测；若证据不足，优先 confirm，并在 reviewerSummary 中说明不确定点。");
                promptBuilder.AppendLine("5. 输出必须是 JSON，不要输出 markdown。");
                promptBuilder.AppendLine("JSON格式：");
                promptBuilder.AppendLine("{");
                promptBuilder.AppendLine("  \"decision\": \"confirm|reject|correct\",");
                promptBuilder.AppendLine("  \"confidence\": 0.0,");
                promptBuilder.AppendLine("  \"reviewerSummary\": \"简短中文结论\",");
                promptBuilder.AppendLine("  \"correctedReason\": \"可选\",");
                promptBuilder.AppendLine("  \"correctedExpectedValue\": \"可选\",");
                promptBuilder.AppendLine("  \"correctedSuggestion\": \"可选\",");
                promptBuilder.AppendLine("  \"correctedJudgmentBasis\": \"可选\"");
                promptBuilder.AppendLine("}");
                promptBuilder.AppendLine();
                promptBuilder.AppendLine("复核输入：");
                promptBuilder.AppendLine(JsonSerializer.Serialize(payload, CachedJsonOptions));

                var reviewAgent = await _agentRuntimeService.CreateAgentByRoleAsync(DiagnosticAgentRoleId);
                var response = await _agentRuntimeService.RunAgentAsync(reviewAgent, promptBuilder.ToString());
                if (!response.Success || string.IsNullOrWhiteSpace(response.Content))
                {
                    return null;
                }

                var json = ExtractJsonFromResponse(response.Content);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                var parsed = JsonSerializer.Deserialize<ConstraintAiReviewResult>(json, CachedJsonOptions);
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.Decision))
                {
                    return null;
                }

                return parsed;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private List<object> BuildDeterministicConstraintEvidence(
            List<long> relatedLogicUnitIds,
            Dictionary<long, PLC_LogicUnitEntity> logicUnitById,
            Dictionary<long, PLC_BlockEntity> blockById,
            IEnumerable<string>? externalSignalNames = null)
        {
            var evidence = new List<object>();
            var externalSignalSet = externalSignalNames == null
                ? null
                : new HashSet<string>(
                    externalSignalNames.Where(x => !string.IsNullOrWhiteSpace(x)),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var logicUnitId in relatedLogicUnitIds)
            {
                if (!logicUnitById.TryGetValue(logicUnitId, out var unit))
                {
                    continue;
                }

                try
                {
                    var blockName = unit.BlockId.HasValue && blockById.TryGetValue(unit.BlockId.Value, out var block)
                        ? block.BlockName
                        : null;

                    var drafts = SiemensFlgNetConstraintExtractor.ExtractFromLogicUnit(
                        unit.RawContent ?? string.Empty,
                        unit.UidSymbolMapJson,
                        blockName,
                        unit.Title,
                        externalSignalSet);

                    foreach (var draft in drafts.Where(x => string.Equals(x.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)))
                    {
                        var sourceNames = draft.Signals?
                            .Where(x => string.Equals(x.SignalRole, "Source", StringComparison.OrdinalIgnoreCase))
                            .Select(x => x.SignalName)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList() ?? new List<string>();

                        var targetNames = draft.Signals?
                            .Where(x => string.Equals(x.SignalRole, "Target", StringComparison.OrdinalIgnoreCase))
                            .Select(x => x.SignalName)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList() ?? new List<string>();

                        if (sourceNames.Count == 0 || targetNames.Count == 0)
                        {
                            continue;
                        }

                        evidence.Add(new
                        {
                            LogicUnitId = logicUnitId,
                            BlockName = blockName,
                            draft.RuleName,
                            draft.Operator,
                            Sources = sourceNames,
                            Targets = targetNames
                        });
                    }
                }
                catch
                {
                    // 单个 logic unit 提取失败时忽略，不影响整体复核。
                }
            }

            return evidence;
        }

        private ConstraintAiReviewResult TryResolveByDeterministicConstraintEvidence(
            PreliminaryViolationRecord record,
            List<object> deterministicEvidence)
        {
            if (record?.Rule == null
                || !string.Equals(record.Rule.Operator, "IMPLIES", StringComparison.OrdinalIgnoreCase)
                || deterministicEvidence == null
                || deterministicEvidence.Count == 0)
            {
                return null;
            }

            var sourceNames = (record.Signals ?? new List<PLC_IOConstraintSignalEntity>())
                .Where(x => string.Equals(x.SignalRole, "Source", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.SignalName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var targetNames = (record.Signals ?? new List<PLC_IOConstraintSignalEntity>())
                .Where(x => string.Equals(x.SignalRole, "Target", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.SignalName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sourceNames.Count != 1 || targetNames.Count == 0)
            {
                return null;
            }

            var currentSource = sourceNames[0]!;
            var evidenceJson = JsonSerializer.Serialize(deterministicEvidence, CachedJsonOptions);
            using var document = JsonDocument.Parse(evidenceJson);

            var targetToSupportedSources = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var sourceToSupportedTargets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var sources = item.TryGetProperty("Sources", out var sourcesElement) && sourcesElement.ValueKind == JsonValueKind.Array
                    ? sourcesElement.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
                    : new List<string>();
                var targets = item.TryGetProperty("Targets", out var targetsElement) && targetsElement.ValueKind == JsonValueKind.Array
                    ? targetsElement.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
                    : new List<string>();

                foreach (var source in sources)
                {
                    if (!sourceToSupportedTargets.TryGetValue(source, out var targetSet))
                    {
                        targetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        sourceToSupportedTargets[source] = targetSet;
                    }

                    foreach (var target in targets)
                    {
                        targetSet.Add(target);
                    }
                }

                foreach (var target in targets)
                {
                    if (!targetToSupportedSources.TryGetValue(target, out var sourceSet))
                    {
                        sourceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        targetToSupportedSources[target] = sourceSet;
                    }

                    foreach (var source in sources)
                    {
                        sourceSet.Add(source);
                    }
                }
            }

            var contradictedTargets = targetNames
                .Where(target =>
                    targetToSupportedSources.TryGetValue(target, out var supportedSources)
                    && supportedSources.Count > 0
                    && !supportedSources.Contains(currentSource))
                .ToList();

            if (contradictedTargets.Count == targetNames.Count)
            {
                return new ConstraintAiReviewResult
                {
                    Decision = "reject",
                    Confidence = 0.98,
                    ReviewerSummary = $"根据关联程序块和中间块展开后的确定性接线关系，{currentSource} 并不驱动 {string.Join("、", contradictedTargets)}。",
                    CorrectedReason = $"基于程序块实际接线和中间块展开后的确定性关系，{currentSource} 与 {string.Join("、", contradictedTargets)} 之间不存在 Source→Target 驱动链路，当前蕴含异常应撤销。",
                    CorrectedExpectedValue = "该规则缺少实际接线依据，不应按当前 Source/Target 关系报异常。",
                    CorrectedSuggestion = "请以程序块实际接线和中间块展开链路重新校验该配对规则，删除无真实驱动关系的蕴含约束。",
                    CorrectedJudgmentBasis = $"已从关联 LogicUnit 提取确定性 IMPLIES 证据；Target 侧已有其他真实 Source 映射，但不包含当前 Source={currentSource}。"
                };
            }

            if (sourceToSupportedTargets.TryGetValue(currentSource, out var supportedTargets)
                && supportedTargets.Count > 0
                && targetNames.All(target => !supportedTargets.Contains(target)))
            {
                return new ConstraintAiReviewResult
                {
                    Decision = "reject",
                    Confidence = 0.95,
                    ReviewerSummary = $"根据关联程序块和中间块展开后的确定性接线关系，{currentSource} 的真实目标不包含 {string.Join("、", targetNames)}。",
                    CorrectedReason = $"基于程序块实际接线和中间块展开后的确定性关系，{currentSource} 的真实驱动目标不包含 {string.Join("、", targetNames)}，当前蕴含异常应撤销。",
                    CorrectedExpectedValue = "该规则缺少实际接线依据，不应按当前 Source/Target 关系报异常。",
                    CorrectedSuggestion = "请回到中间块和真实接线链路重新生成该配对规则。",
                    CorrectedJudgmentBasis = $"已从关联 LogicUnit 提取确定性 IMPLIES 证据；Source={currentSource} 的真实 Target 集为 [{string.Join(", ", supportedTargets)}]。"
                };
            }

            return null;
        }

        private static void ApplyAiReviewToViolation(DiagnosticIssue violation, ConstraintAiReviewResult review)
        {
            if (violation == null || review == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(review.CorrectedReason))
            {
                violation.Reason = review.CorrectedReason;
            }

            if (!string.IsNullOrWhiteSpace(review.CorrectedExpectedValue))
            {
                violation.ExpectedValue = review.CorrectedExpectedValue;
            }

            if (!string.IsNullOrWhiteSpace(review.CorrectedSuggestion))
            {
                violation.Suggestion = review.CorrectedSuggestion;
            }

            if (!string.IsNullOrWhiteSpace(review.CorrectedJudgmentBasis))
            {
                violation.JudgmentBasis = review.CorrectedJudgmentBasis;
            }

            if (!string.IsNullOrWhiteSpace(review.Decision) || !string.IsNullOrWhiteSpace(review.ReviewerSummary))
            {
                var reviewNote = $"AI复核={review.Decision}, 置信度={review.Confidence:0.00}, 结论={review.ReviewerSummary}";
                violation.Note = string.IsNullOrWhiteSpace(violation.Note)
                    ? reviewNote
                    : $"{violation.Note} | {reviewNote}";
            }
        }

        private static string TrimForAi(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return text.Length <= maxLength
                ? text
                : text.Substring(0, maxLength) + "\n...<truncated>";
        }

        /// <summary>
        /// 检查单个约束规则是否违反（互斥/唯一选择/全满足/禁止状态/蕴含/时序）。
        /// 根据RuleType判断约束类型，返回违反的诊断信息。
        /// 纯函数、确定性：只依赖入参，不读任何实例/静态状态，相同入参永远返回相同结果。
        /// D-1 铁律：<paramref name="signalValues"/> 中没有的信号（缺值/未采集）永远不会单独判定出违反——
        /// 不回退到"全部信号"、不把缺值当 false、不把缺值当已满足；配对类算子（IMPLIES、以及 AT_MOST_ONE 的
        /// Source+Target 配对分支）按规则声明的角色列表构建配对，任一成员缺值则整条配对跳过而非错位配对。
        /// CUSTOM 无可执行语义，恒定返回 null。
        /// 因此本方法自身不能给出"通过"的确定性结论——它只能确认"违反"或"未发现违反（可能因为数据不全）"；
        /// 调用方若要对外呈现"通过"结论，必须自行确认规则涉及的全部信号都已采到值（见 Task 5 的三值判定包装器）。
        /// </summary>
        /// <param name="rule">约束规则实体（包含Operator类型）</param>
        /// <param name="signals">规则关联的信号列表</param>
        /// <param name="signalValues">信号名到当前值的字典；未出现的信号视为"缺值"，不参与判定</param>
        /// <param name="signalNameToTag">信号名到IO Tag实体的映射，用于获取变量描述</param>
        /// <returns>违反的诊断信息；无违反、无法判定（缺值）或 CUSTOM 一律返回 null</returns>
        internal static DiagnosticIssue CheckConstraintViolation(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> signalValues,
            Dictionary<string, PLC_IOTagEntity> signalNameToTag)
        {
            // 辅助方法：将信号名转换为易读的中文描述
            string GetReadableSignalName(string signalName)
            {
                if (signalNameToTag.TryGetValue(signalName, out var tag) && !string.IsNullOrEmpty(tag.Comment))
                {
                    return $"{signalName} ({tag.Comment})";
                }
                return signalName;
            }

            // 辅助方法：批量翻译信号名
            string GetReadableSignalNames(IEnumerable<string> signalNames)
            {
                return string.Join(", ", signalNames.Select(GetReadableSignalName));
            }

            SignalDetail BuildSignalDetail(PLC_IOConstraintSignalEntity signal, bool currentValue, string expectedValue)
            {
                signalNameToTag.TryGetValue(signal.SignalName, out var tag);
                return new SignalDetail
                {
                    SignalName = signal.SignalName,
                    CurrentValue = currentValue,
                    ExpectedValue = expectedValue,
                    Description = GetReadableSignalName(signal.SignalName),
                    Address = tag?.Address
                };
            }

            static List<PLC_IOConstraintSignalEntity> DistinctSignals(IEnumerable<PLC_IOConstraintSignalEntity> candidates)
            {
                return candidates
                    .Where(s => !string.IsNullOrWhiteSpace(s.SignalName))
                    .GroupBy(s => s.SignalName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(x => x.SortOrder ?? int.MaxValue).First())
                    .OrderBy(x => x.SortOrder ?? int.MaxValue)
                    .ToList();
            }

            static bool TryBuildStructuredMutexBranches(
                List<PLC_IOConstraintSignalEntity> memberRoleSignals,
                List<PLC_IOConstraintSignalEntity> resolvedMemberSignals,
                List<PLC_IOConstraintSignalEntity> sourceRoleSignals,
                List<PLC_IOConstraintSignalEntity> resolvedSourceSignals,
                List<PLC_IOConstraintSignalEntity> targetRoleSignals,
                List<PLC_IOConstraintSignalEntity> resolvedTargetSignals,
                List<PLC_IOConstraintSignalEntity> fallbackSignals,
                Dictionary<string, bool> currentValues,
                out List<List<PLC_IOConstraintSignalEntity>> branches)
            {
                branches = new List<List<PLC_IOConstraintSignalEntity>>();

                // D-1：分支路径的选择必须按规则「声明」的角色判断（member/sourceRoleSignals/targetRoleSignals 均为声明列表，
                // 不是已采到值的子集）——否则 Member 全部缺值时会误落到 Source/Target 兜底，把不相关的角色当成互斥分支来判定。
                if (sourceRoleSignals.Count > 0 && targetRoleSignals.Count > 0)
                {
                    // 配对必须基于声明的角色列表构建（避免残值集合导致配对错位，与 IMPLIES 分支同一处 BuildImpliesMappings 调用同理）
                    var mappings = BuildImpliesMappings(sourceRoleSignals, targetRoleSignals);
                    if (mappings.Count == 0)
                    {
                        return false;
                    }

                    foreach (var mapping in mappings)
                    {
                        // D-1：Source 或任一 Target 缺值时整条配对跳过，不构造分支——缺成员的分支不允许被当作"真"分支参与互斥判定
                        if (!currentValues.ContainsKey(mapping.Source.SignalName))
                        {
                            continue;
                        }

                        if (mapping.Targets.Any(t => !currentValues.ContainsKey(t.SignalName)))
                        {
                            continue;
                        }

                        var branch = DistinctSignals(new[] { mapping.Source }.Concat(mapping.Targets));
                        if (branch.Count > 0)
                        {
                            branches.Add(branch);
                        }
                    }

                    return branches.Count >= 2;
                }

                if (memberRoleSignals.Count > 0)
                {
                    foreach (var signal in DistinctSignals(resolvedMemberSignals))
                    {
                        branches.Add(new List<PLC_IOConstraintSignalEntity> { signal });
                    }
                    return branches.Count >= 2;
                }

                if (sourceRoleSignals.Count > 0)
                {
                    foreach (var signal in DistinctSignals(resolvedSourceSignals))
                    {
                        branches.Add(new List<PLC_IOConstraintSignalEntity> { signal });
                    }
                    return branches.Count >= 2;
                }

                if (targetRoleSignals.Count > 0)
                {
                    foreach (var signal in DistinctSignals(resolvedTargetSignals))
                    {
                        branches.Add(new List<PLC_IOConstraintSignalEntity> { signal });
                    }
                    return branches.Count >= 2;
                }

                foreach (var signal in DistinctSignals(fallbackSignals))
                {
                    branches.Add(new List<PLC_IOConstraintSignalEntity> { signal });
                }
                return branches.Count >= 2;
            }

            static bool AreAllSignalsTrue(IEnumerable<PLC_IOConstraintSignalEntity> group, Dictionary<string, bool> currentValues)
            {
                var items = group.Where(s => !string.IsNullOrWhiteSpace(s.SignalName)).ToList();
                return items.Count > 0 && items.All(s => currentValues.TryGetValue(s.SignalName, out var value) && value);
            }

            static List<(PLC_IOConstraintSignalEntity Source, List<PLC_IOConstraintSignalEntity> Targets)> BuildImpliesMappings(
                List<PLC_IOConstraintSignalEntity> sourceRoleSignals,
                List<PLC_IOConstraintSignalEntity> targetRoleSignals)
            {
                var mappings = new List<(PLC_IOConstraintSignalEntity Source, List<PLC_IOConstraintSignalEntity> Targets)>();
                if (sourceRoleSignals.Count == 0 || targetRoleSignals.Count == 0)
                {
                    return mappings;
                }

                if (sourceRoleSignals.Count == 1)
                {
                    mappings.Add((sourceRoleSignals[0], targetRoleSignals));
                    return mappings;
                }

                var orderedSources = sourceRoleSignals
                    .OrderBy(s => s.SortOrder ?? int.MaxValue)
                    .ToList();
                var orderedTargets = targetRoleSignals
                    .OrderBy(t => t.SortOrder ?? int.MaxValue)
                    .ToList();

                if (orderedTargets.Count == 1)
                {
                    foreach (var source in orderedSources)
                    {
                        mappings.Add((source, orderedTargets));
                    }
                    return mappings;
                }

                if (orderedSources.Count == orderedTargets.Count)
                {
                    for (var i = 0; i < orderedSources.Count; i++)
                    {
                        mappings.Add((orderedSources[i], new List<PLC_IOConstraintSignalEntity> { orderedTargets[i] }));
                    }
                }

                return mappings;
            }

            // D-1 Fix round 2：空白/空名信号在这里一次性剔除——declared 角色列表（memberSignals/sourceSignals/targetSignals）
            // 从此不会再含 SignalName 为 null/空白的条目，与 resolvedSignals/allSignalsResolved（本来就已过滤空名）保持一致。
            // 否则下游 resolvedXxxSignals 的 `signalValues.ContainsKey(s.SignalName)` 会在 SignalName 为 null 时抛
            // ArgumentNullException；而空名条目即使不抛异常也永远不可能被采到值，若不在声明列表里就剔除，会让
            // "declared.Count > 0 但 resolved 永远采不到"这种伪声明悄悄影响配对/分支选择（如 AT_MOST_ONE 的路径决策）。
            List<PLC_IOConstraintSignalEntity> GetRoleSignals(string role) => signals
                .Where(s => string.Equals(s.SignalRole, role, StringComparison.OrdinalIgnoreCase))
                .Where(s => !string.IsNullOrWhiteSpace(s.SignalName))
                .OrderBy(s => s.SortOrder ?? int.MaxValue)
                .ToList();

            List<PLC_IOConstraintSignalEntity> memberSignals = GetRoleSignals("Member");
            List<PLC_IOConstraintSignalEntity> sourceSignals = GetRoleSignals("Source");
            List<PLC_IOConstraintSignalEntity> targetSignals = GetRoleSignals("Target");

            var resolvedSignals = signals
                .Where(s => !string.IsNullOrWhiteSpace(s.SignalName) && signalValues.ContainsKey(s.SignalName))
                .OrderBy(s => s.SortOrder ?? int.MaxValue)
                .ToList();
            // D-1：缺值信号不参与判定（三值判定由外层 ConstraintRuleEvaluator 兜底），不再回退到"全部信号"
            var scopedSignals = resolvedSignals;
            var allSignalsResolved = signals
                .Where(s => !string.IsNullOrWhiteSpace(s.SignalName))
                .All(s => signalValues.ContainsKey(s.SignalName));
            var scopedSignalNames = scopedSignals.Select(s => s.SignalName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var trueSignals = scopedSignalNames
                .Where(name => signalValues.TryGetValue(name, out var current) && current)
                .ToList();
            var falseSignals = scopedSignalNames
                .Where(name => signalValues.TryGetValue(name, out var current) && !current)
                .ToList();
            var resolvedMemberSignals = memberSignals.Where(s => signalValues.ContainsKey(s.SignalName)).ToList();
            var resolvedSourceSignals = sourceSignals.Where(s => signalValues.ContainsKey(s.SignalName)).ToList();
            var resolvedTargetSignals = targetSignals.Where(s => signalValues.ContainsKey(s.SignalName)).ToList();

            switch (ConstraintRuleEvaluator.NormalizeOperator(rule.Operator))
            {
                case "AT_MOST_ONE":
                    // 互斥：严格按规则结构分支判断，不再按信号命名推断正反方向
                    {
                        if (!TryBuildStructuredMutexBranches(
                                memberSignals,
                                resolvedMemberSignals,
                                sourceSignals,
                                resolvedSourceSignals,
                                targetSignals,
                                resolvedTargetSignals,
                                scopedSignals,
                                signalValues,
                                out var mutexBranches))
                        {
                            break;
                        }

                        var activeBranches = mutexBranches
                            .Where(branch => branch.Any(s => signalValues.GetValueOrDefault(s.SignalName, false)))
                            .ToList();

                        if (activeBranches.Count <= 1)
                        {
                            break;
                        }

                        var detailSignals = DistinctSignals(activeBranches.SelectMany(branch => branch));
                        var violatingSignals = DistinctSignals(activeBranches
                            .SelectMany(branch => branch)
                            .Where(s => signalValues.GetValueOrDefault(s.SignalName, false)));
                        var violatingSignalNames = violatingSignals.Select(s => s.SignalName).ToList();

                        if (detailSignals.Count < 2 || violatingSignalNames.Count < 2)
                        {
                            break;
                        }

                        var signalDetails = detailSignals
                            .Select(s => BuildSignalDetail(
                                s,
                                signalValues.GetValueOrDefault(s.SignalName, false),
                                "false（互斥分支中最多一个分支允许激活）"))
                            .ToList();

                        var currentValuesList = detailSignals.Select(s =>
                        {
                            var value = signalValues.ContainsKey(s.SignalName) ? signalValues[s.SignalName].ToString() : "未找到";
                            return s.SignalName + "=" + value;
                        }).ToList();
                        var currentValuesStr = string.Join(", ", currentValuesList);
                        var activeBranchLabels = activeBranches
                            .Select(branch => string.Join(" + ", branch.Select(s => s.SignalName)))
                            .ToList();

                        var reasonText = $"互斥约束违反: {activeBranches.Count} 个互斥分支同时激活（{rule.RuleName}）";
                        var expectedText = "同一时刻最多仅允许一个互斥分支处于激活状态";
                        var suggestionText = $"检查以下同时激活的互斥分支：{string.Join(" / ", activeBranchLabels)}。";
                        var judgmentBasis = $"根据约束规则【{rule.RuleName}】（类型：AT_MOST_ONE互斥规则）判断，本次校验严格按规则中的 Member/Source/Target 结构构建互斥分支，不再依赖信号命名推断方向。当前PLC实时数据：[{currentValuesStr}]。检测到 {activeBranches.Count} 个互斥分支同时激活：{string.Join(" / ", activeBranchLabels)}，因此判定违反互斥规则。";

                        return new DiagnosticIssue
                        {
                            Variable = GetReadableSignalNames(violatingSignalNames),
                            Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                            Status = $"[{string.Join(", ", violatingSignalNames)}] 同时为true",
                            Reason = reasonText,
                            Severity = rule.Priority == "Critical" ? "严重" : "警告",
                            Category = "约束问题",
                            RelatedRule = rule.RuleName,
                            ConstraintOperator = "AT_MOST_ONE（互斥）",
                            SignalDetails = signalDetails,
                            ExpectedValue = expectedText,
                            Suggestion = suggestionText,
                            JudgmentBasis = judgmentBasis
                        };
                    }
                    break;

                case "EXACTLY_ONE":
                    // 唯一选择：必须且仅有一个为true
                    // D-1："0 个 true"只有在全部信号都采到时才成立；"≥2 个 true"与缺值无关
                    if (scopedSignals.Count > 0 && (trueSignals.Count > 1 || (trueSignals.Count == 0 && allSignalsResolved)))
                    {
                        var statusDesc = trueSignals.Count == 0 ? "没有信号为true" : $"{trueSignals.Count}个信号为true";
                        var signalDetails = scopedSignals
                            .Select(s => BuildSignalDetail(
                                s,
                                signalValues.GetValueOrDefault(s.SignalName, false),
                                "仅1个为true"))
                            .ToList();

                        // 构建判断依据 - 列出所有信号的当前值
                        var currentValuesList = scopedSignals.Select(s =>
                        {
                            var value = signalValues.ContainsKey(s.SignalName) ? signalValues[s.SignalName].ToString() : "未找到";
                            return s.SignalName + "=" + value;
                        }).ToList();
                        var currentValuesStr = string.Join(", ", currentValuesList);

                        return new DiagnosticIssue
                        {
                            Variable = GetReadableSignalNames(scopedSignals.Select(x => x.SignalName)),
                            Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                            Status = statusDesc,
                            Reason = $"唯一选择约束违反: 必须恰好有1个信号为true，但当前{statusDesc}（{rule.RuleName}）",
                            Severity = rule.Priority == "Critical" ? "严重" : "警告",
                            Category = "约束问题",
                            RelatedRule = rule.RuleName,
                            ConstraintOperator = "EXACTLY_ONE（唯一选择）",
                            SignalDetails = signalDetails,
                            ExpectedValue = "恰好1个信号为true",
                            Suggestion = $"从以下信号中选择1个并设置为true：{GetReadableSignalNames(scopedSignals.Select(x => x.SignalName))}。检查选择逻辑确保只有一个输出被激活。",
                            JudgmentBasis = $"根据约束规则【{rule.RuleName}】（类型：EXACTLY_ONE唯一选择规则）判断，该规则要求：这些信号中必须有且只有1个为true。当前PLC实时数据：[{currentValuesStr}]。不满足条件：当前{statusDesc}，不满足恰好1个为true的要求。请结合规则描述【{rule.Description}】和信号名称分析可能导致的物理后果（如：没有选择任何工位会导致停机）。"
                        };
                    }
                    break;

                case "ALL_TRUE":
                    // 全满足：仅在 Source 整组激活时检查被约束侧；优先检查 Member，没有 Member 时检查 Target。
                    {
                        var requiredSignals = memberSignals.Count > 0 ? resolvedMemberSignals : resolvedTargetSignals;
                        if (requiredSignals.Count == 0)
                        {
                            break;
                        }

                        var requiredSignalNames = requiredSignals.Select(s => s.SignalName).ToList();
                        var sourceSignalNames = resolvedSourceSignals.Select(s => s.SignalName).ToList();
                        var hasActiveSource = resolvedSourceSignals.Count == sourceSignals.Count && AreAllSignalsTrue(resolvedSourceSignals, signalValues);

                        // ALL_TRUE 仅在存在 Source 且 Source=true 时才检查 Member。
                        if (sourceSignalNames.Count == 0 || !hasActiveSource)
                        {
                            break;
                        }

                        // 方向态豁免：如果当前明显处于反方向状态，则不应再报本方向串联条件未满足
                        var detailSignals = scopedSignals;
                        var requiredFalseSignals = requiredSignalNames
                            .Where(name => signalValues.TryGetValue(name, out var current) && !current)
                            .ToList();

                        var safeSignals = requiredFalseSignals
                            .Where(s => s.Contains("安全门请求", StringComparison.OrdinalIgnoreCase) ||
                                        s.Contains("安全门复位", StringComparison.OrdinalIgnoreCase) ||
                                        s.Contains("复位", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        var actualFalseSignals = requiredFalseSignals.Except(safeSignals).ToList();

                        if (actualFalseSignals.Count > 0)
                        {
                            var signalDetails = requiredSignals
                                .Select(s => BuildSignalDetail(
                                    s,
                                    signalValues.GetValueOrDefault(s.SignalName, false),
                                    s.SignalName.Contains("安全门请求", StringComparison.OrdinalIgnoreCase) ||
                                    s.SignalName.Contains("安全门复位", StringComparison.OrdinalIgnoreCase) ||
                                    s.SignalName.Contains("复位", StringComparison.OrdinalIgnoreCase) ? "false（保持）" : "true"))
                                .ToList();

                            // 构建判断依据 - 先列出 Source 信号（触发方），再列出 Member 信号（被约束方）
                            var sourceValuesList = resolvedSourceSignals.Select(s =>
                            {
                                var value = signalValues.ContainsKey(s.SignalName) ? signalValues[s.SignalName].ToString() : "未找到";
                                return $"[Source]{s.SignalName}={value}";
                            }).ToList();
                            var memberValuesList = requiredSignals.Select(s =>
                            {
                                var value = signalValues.ContainsKey(s.SignalName) ? signalValues[s.SignalName].ToString() : "未找到";
                                return $"[Member]{s.SignalName}={value}";
                            }).ToList();
                            var currentValuesStr = string.Join(", ", sourceValuesList.Concat(memberValuesList));

                            var sourceNamesStr = string.Join("、", resolvedSourceSignals.Select(x => x.SignalName));
                            var memberNamesStr = string.Join("、", requiredSignals.Select(x => x.SignalName));
                            var falseMemberNamesStr = string.Join("、", actualFalseSignals);

                            return new DiagnosticIssue
                            {
                                Variable = GetReadableSignalNames(actualFalseSignals),
                                Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                                Status = $"[{string.Join(", ", actualFalseSignals)}] 为false",
                                Reason = $"ALL_TRUE约束违反：Source信号【{sourceNamesStr}】已为true（触发条件成立），但被约束的Member信号未全部为true，当前false的Member：{GetReadableSignalNames(actualFalseSignals)}（{rule.RuleName}）",
                                Severity = rule.Priority == "Critical" ? "严重" : "警告",
                                Category = "约束问题",
                                RelatedRule = rule.RuleName,
                                ConstraintOperator = "ALL_TRUE（全满足）",
                                SignalDetails = signalDetails,
                                ExpectedValue = $"Source信号【{sourceNamesStr}】为true时，Member信号【{memberNamesStr}】必须全部为true",
                                Suggestion = $"以下Member信号需要设置为true才能满足约束：{GetReadableSignalNames(actualFalseSignals)}。检查这些信号的驱动逻辑或硬件反馈。",
                                JudgmentBasis = $"根据约束规则【{rule.RuleName}】（类型：ALL_TRUE全满足规则）判断：触发方（Source）是【{sourceNamesStr}】，当前Source已为true；被约束方（Member）是【{memberNamesStr}】，要求Source为true时Member必须全部为true。当前PLC实时数据：[{currentValuesStr}]。不满足条件：{actualFalseSignals.Count}个Member信号为false（{falseMemberNamesStr}）。请分析这些Member信号为何未能置位，重点检查相关执行机构的硬件反馈信号是否到位、阀门/气缸驱动逻辑是否正常。"
                            };
                        }
                    }
                    break;

                case "REQUIRES_0":
                    // 必须为0：优先只检查 Target；没有 Target 时退化为检查所有信号
                    {
                        var sourceSignalNames = resolvedSourceSignals.Select(s => s.SignalName).ToList();
                        var hasActiveSource = resolvedSourceSignals.Count == sourceSignals.Count && AreAllSignalsTrue(resolvedSourceSignals, signalValues);

                        // 条件性 REQUIRES_0：存在 Source 时，仅在 Source 已激活时才校验禁止侧
                        if (sourceSignals.Count > 0 && !hasActiveSource)
                        {
                            break;
                        }

                        var forbiddenSignals = targetSignals.Count > 0 ? resolvedTargetSignals : scopedSignals;
                        var forbiddenSignalNames = forbiddenSignals.Select(s => s.SignalName).ToList();
                        var forbiddenTrueSignals = forbiddenSignalNames
                            .Where(name => signalValues.TryGetValue(name, out var current) && current)
                            .ToList();

                        var safetyDoorViolations = forbiddenTrueSignals
                            .Where(s => s.Contains("安全门", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        var resetViolations = forbiddenTrueSignals
                            .Where(s => s.Contains("复位", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        // 确定违反的信号（安全门/复位为true时违反，非此类信号为true时违反）
                        var violatingSignals = forbiddenTrueSignals.Where(s =>
                            (!s.Contains("安全门", StringComparison.OrdinalIgnoreCase) && !s.Contains("复位", StringComparison.OrdinalIgnoreCase)) ||
                            safetyDoorViolations.Contains(s) ||
                            resetViolations.Contains(s)
                        ).ToList();

                        // 如果没有违反信号但安全门或复位为true，也属于违反
                        if (violatingSignals.Count == 0 && (safetyDoorViolations.Count > 0 || resetViolations.Count > 0))
                        {
                            violatingSignals.AddRange(safetyDoorViolations);
                            violatingSignals.AddRange(resetViolations);
                        }

                        if (violatingSignals.Count > 0)
                        {
                            var signalDetails = forbiddenSignals
                                .Select(s => BuildSignalDetail(
                                    s,
                                    signalValues.GetValueOrDefault(s.SignalName, false),
                                    s.SignalName.Contains("安全门", StringComparison.OrdinalIgnoreCase) ? "false（关闭）" :
                                    s.SignalName.Contains("复位", StringComparison.OrdinalIgnoreCase) ? "false（未按下）" : "false"))
                                .ToList();

                            // 构建判断依据 - 列出所有信号的当前值
                            var currentValuesList = forbiddenSignals.Select(s =>
                            {
                                var value = signalValues.ContainsKey(s.SignalName) ? signalValues[s.SignalName].ToString() : "未找到";
                                return s.SignalName + "=" + value;
                            }).ToList();
                            var currentValuesStr = string.Join(", ", currentValuesList);

                            return new DiagnosticIssue
                            {
                                Variable = GetReadableSignalNames(violatingSignals),
                                Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                                Status = $"[{string.Join(", ", violatingSignals)}] 为true",
                                Reason = $"以下信号必须为false但当前为true: {GetReadableSignalNames(violatingSignals)}（{rule.RuleName}）",
                                Severity = rule.Priority == "Critical" ? "严重" : "警告",
                                Category = "约束问题",
                                RelatedRule = rule.RuleName,
                                ConstraintOperator = "REQUIRES_0（禁止状态）",
                                SignalDetails = signalDetails,
                                ExpectedValue = resolvedTargetSignals.Count > 0
                                    ? (resolvedSourceSignals.Count > 0
                                        ? $"{GetReadableSignalNames(resolvedSourceSignals.Select(x => x.SignalName))} 成立时，{GetReadableSignalNames(resolvedTargetSignals.Select(x => x.SignalName))} 必须保持为false"
                                        : $"{GetReadableSignalNames(resolvedTargetSignals.Select(x => x.SignalName))} 必须保持为false")
                                    : "约束信号必须全部为false",
                                Suggestion = $"以下信号需要设置为false才能满足约束：{GetReadableSignalNames(violatingSignals)}。检查这些信号的复位逻辑或强制关闭。",
                                JudgmentBasis = resolvedSourceSignals.Count > 0
                                    ? $"根据约束规则【{rule.RuleName}】（类型：REQUIRES_0禁止状态规则）判断，该规则要求：当Source侧信号成立时，禁止侧Target必须保持为false。当前PLC实时数据：[{currentValuesStr}]。检测到Source已激活且{violatingSignals.Count}个禁止信号为true（{string.Join(", ", violatingSignals)}），因此违反禁止状态规则。请结合规则描述【{rule.Description}】和信号名称分析被禁止状态被激活的后果。"
                                    : $"根据约束规则【{rule.RuleName}】（类型：REQUIRES_0禁止状态规则）判断，该规则要求禁止侧信号必须保持为false。当前PLC实时数据：[{currentValuesStr}]。不满足条件：{violatingSignals.Count}个禁止信号为true（{string.Join(", ", violatingSignals)}），违反了禁止状态规则。请结合规则描述【{rule.Description}】和信号名称分析被禁止状态被激活的后果。"
                            };
                        }
                    }
                    break;

                case "REQUIRES_0_WHEN_FALSE":
                    // 反向零态联锁：任一 Source=false 时 Target 必须 false（NEW 2026-05-26）
                    // 用途：表达博图 FB「状态有效」语义信号（E_StopStatus / ResetStatus / SafetyStatus / SafeStop）的急停联锁
                    //   E_StopStatus = true  → 急停链路正常，FB 允许动作
                    //   E_StopStatus = false → 急停按下，FB 强制断电（Target 必须 false）
                    // **多 Source 是 OR 语义**：物理急停链路是「任一工位急停按下 → 整体联锁触发」，
                    //   不是「所有工位全部急停才触发」。
                    // 与 REQUIRES_0 是反向语义：REQUIRES_0 看 Source=true 时校验，本 case 看任一 Source=false 时校验。
                    {
                        var sourceSignalNames = resolvedSourceSignals.Select(s => s.SignalName).ToList();
                        if (sourceSignalNames.Count == 0) break;  // 必须有 Source（REQUIRES_0_WHEN_FALSE 不支持无 Source 形态）

                        // 任一 Source 为 false（采到值且 = false）→ 触发校验
                        // 全部 Source 都没采到值 / 都 = true → 跳过校验（避免误报）
                        var falseSources = resolvedSourceSignals
                            .Where(s => signalValues.TryGetValue(s.SignalName, out var v) && !v)
                            .ToList();
                        if (falseSources.Count == 0) break;

                        var forbiddenSignals = resolvedTargetSignals;
                        if (forbiddenSignals.Count == 0) break;
                        var forbiddenTrueSignals = forbiddenSignals
                            .Where(t => signalValues.TryGetValue(t.SignalName, out var v) && v)
                            .ToList();
                        if (forbiddenTrueSignals.Count == 0) break;

                        var signalDetails = new List<SignalDetail>();
                        signalDetails.AddRange(resolvedSourceSignals.Select(s => BuildSignalDetail(
                            s,
                            signalValues.GetValueOrDefault(s.SignalName, false),
                            "Source 失效（=false）即触发 Target 零态校验")));
                        signalDetails.AddRange(forbiddenSignals.Select(t => BuildSignalDetail(
                            t,
                            signalValues.GetValueOrDefault(t.SignalName, false),
                            "false（任一 Source 失效时强制断电）")));

                        var currentValuesStr = string.Join(", ", signalDetails.Select(s => $"{s.SignalName}={s.CurrentValue}"));
                        var falseSourceNames = falseSources.Select(s => s.SignalName).ToList();

                        return new DiagnosticIssue
                        {
                            Variable = GetReadableSignalNames(forbiddenTrueSignals.Select(t => t.SignalName)),
                            Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                            Status = $"{falseSources.Count} 个 Source 失效 + {forbiddenTrueSignals.Count} 个 Target 仍为 true",
                            Reason = $"反向零态联锁违反: {GetReadableSignalNames(falseSourceNames)} 失效（=false），但 {GetReadableSignalNames(forbiddenTrueSignals.Select(t => t.SignalName))} 仍为 true，强制断电未生效（{rule.RuleName}）",
                            Severity = rule.Priority == "Critical" ? "严重" : "警告",
                            Category = "约束问题",
                            RelatedRule = rule.RuleName,
                            ConstraintOperator = "REQUIRES_0_WHEN_FALSE（反向零态联锁，多 Source 是 OR 语义）",
                            SignalDetails = signalDetails,
                            ExpectedValue = $"任一 Source 失效（=false）时，{GetReadableSignalNames(resolvedTargetSignals.Select(x => x.SignalName))} 必须为 false（强制断电）",
                            Suggestion = $"检查 {GetReadableSignalNames(forbiddenTrueSignals.Select(t => t.SignalName))} 的复位/断电逻辑——以下 Source 已失效：{GetReadableSignalNames(falseSourceNames)}（如急停按下、安全链断开），这些输出应同步断电但仍为 true。可能是中间继电器卡死、PLC 逻辑遗漏断电指令、或 Source 信号采集错误。",
                            JudgmentBasis = $"根据约束规则【{rule.RuleName}】（类型：REQUIRES_0_WHEN_FALSE 反向零态联锁，多 Source 是 OR 语义）判断，该规则要求：任一 Source 信号失效（=false）时，所有 Target 必须同步为 false。当前 PLC 实时数据：[{currentValuesStr}]。{falseSources.Count} 个 Source 已失效（{string.Join(", ", falseSourceNames)}），但 {forbiddenTrueSignals.Count} 个 Target 仍为 true（{string.Join(", ", forbiddenTrueSignals.Select(t => t.SignalName))}），违反联锁。请结合规则描述【{rule.Description}】排查断电链路。"
                        };
                    }

                case "MUST_BE_FALSE":
                    // 单信号永禁：每条约束单 Target 形态（Augmenter 屏蔽态报警 §4.5 输出），所有 Target 必须 false，任一 true 即告警。
                    // 与 REQUIRES_0 的差别：MUST_BE_FALSE 拒绝 Source（约束粒度独立到单个传感器），多 Target 也按 OR 校验。
                    {
                        if (resolvedTargetSignals.Count == 0) break;
                        var mustForbiddenTrueSignals = resolvedTargetSignals
                            .Where(s => signalValues.TryGetValue(s.SignalName, out var v) && v)
                            .ToList();
                        if (mustForbiddenTrueSignals.Count == 0) break;

                        var mustSignalDetails = resolvedTargetSignals
                            .Select(s => BuildSignalDetail(
                                s,
                                signalValues.GetValueOrDefault(s.SignalName, false),
                                "false（屏蔽态报警传感器永远不应触发）"))
                            .ToList();
                        var mustCurrentValuesStr = string.Join(", ", mustSignalDetails.Select(s => $"{s.SignalName}={s.CurrentValue}"));
                        var mustTrueNames = mustForbiddenTrueSignals.Select(s => s.SignalName).ToList();

                        return new DiagnosticIssue
                        {
                            Variable = GetReadableSignalNames(mustTrueNames),
                            Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                            Status = $"{mustForbiddenTrueSignals.Count} 个屏蔽态传感器为 true",
                            Reason = $"屏蔽态报警传感器违反: {GetReadableSignalNames(mustTrueNames)} 当前为 true，应永为 false（{rule.RuleName}）",
                            Severity = rule.Priority == "Critical" ? "严重" : "警告",
                            Category = "约束问题",
                            RelatedRule = rule.RuleName,
                            ConstraintOperator = "MUST_BE_FALSE（单信号永禁/屏蔽态报警传感器）",
                            SignalDetails = mustSignalDetails,
                            ExpectedValue = $"{GetReadableSignalNames(resolvedTargetSignals.Select(x => x.SignalName))} 必须永为 false",
                            Suggestion = $"检查 {GetReadableSignalNames(mustTrueNames)} 的物理状态——这些传感器在程序内被 AlwaysTRUE/AlwaysFALSE 屏蔽开关绕过的报警网络驱动，任一为 true 都是真实异常（典型场景：料盒/缺料/夹爪到位传感器误触发）。",
                            JudgmentBasis = $"根据约束规则【{rule.RuleName}】（类型：MUST_BE_FALSE 单信号永禁约束，源自屏蔽态 SCoil 报警网络识别）判断，该规则要求 Target 信号必须永为 false。当前 PLC 实时数据：[{mustCurrentValuesStr}]。{mustForbiddenTrueSignals.Count} 个传感器违反零态要求（{string.Join(", ", mustTrueNames)}）。请结合规则描述【{rule.Description}】排查物理传感器或接线。"
                        };
                    }

                case "IMPLIES":
                    // 蕴含：A=true → B=true
                    // D-1：配对必须基于声明的角色列表构建（sourceSignals/targetSignals，不是已采到值的子集），
                    // 否则某个 Target 缺值会让配对算法（1 对多 / N 对 N 按序 zip）整体错位，把 Source 错配到别的 Target 上产生假违反。
                    var impliesMappings = BuildImpliesMappings(sourceSignals, targetSignals);
                    foreach (var mapping in impliesMappings)
                    {
                        var sourceSignal = mapping.Source;
                        var targetCandidates = mapping.Targets.ToList();

                        if (targetCandidates.Count == 0)
                        {
                            continue;
                        }

                        if (!signalValues.TryGetValue(sourceSignal.SignalName, out var sourceValue))
                        {
                            continue;
                        }

                        // D-1：本配对的 Source 已读到，但只要有任一 Target 缺值，整条配对就跳过——
                        // 缺值的 Target 不能被当作"暂不违反"而放行，也不能被其它配对借用（见上方 BuildImpliesMappings 调用处说明）
                        if (targetCandidates.Any(t => !signalValues.ContainsKey(t.SignalName)))
                        {
                            continue;
                        }

                        var falseTargets = targetCandidates
                            .Where(t => signalValues.TryGetValue(t.SignalName, out var targetValue) && !targetValue)
                            .ToList();

                        if (sourceValue && falseTargets.Count > 0)
                        {
                            var signalDetails = new List<SignalDetail>();
                            signalDetails.Add(
                                BuildSignalDetail(
                                    sourceSignal,
                                    sourceValue,
                                    "true（作为Source，保持true）"));
                            signalDetails.AddRange(targetCandidates.Select(target => BuildSignalDetail(
                                target,
                                signalValues.GetValueOrDefault(target.SignalName, false),
                                "true（作为Target，必须为true）")));

                            return new DiagnosticIssue
                            {
                                Variable = $"{GetReadableSignalName(sourceSignal.SignalName)} → {GetReadableSignalNames(falseTargets.Select(t => t.SignalName))}",
                                Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                                Status = $"Source={sourceValue}, MissingTarget={string.Join(", ", falseTargets.Select(t => t.SignalName))}",
                                Reason = $"蕴含约束违反: {GetReadableSignalName(sourceSignal.SignalName)}为true，但{GetReadableSignalNames(falseTargets.Select(t => t.SignalName))}为false",
                                Severity = rule.Priority == "Critical" ? "严重" : "警告",
                                Category = "约束问题",
                                RelatedRule = rule.RuleName,
                                ConstraintOperator = "IMPLIES（蕴含）",
                                SignalDetails = signalDetails,
                                ExpectedValue = "当Source为true时，Target必须为true",
                                Suggestion = $"因为 {GetReadableSignalName(sourceSignal.SignalName)} 为true，以下Target也必须为true：{GetReadableSignalNames(falseTargets.Select(t => t.SignalName))}。检查Target的触发条件或逻辑链路。",
                                JudgmentBasis = $"根据约束规则【{rule.RuleName}】（类型：IMPLIES蕴含规则）判断，该规则要求：如果Source信号为true，则其对应Target信号也必须为true。当前PLC实时数据：[{sourceSignal.SignalName}={sourceValue}, {string.Join(", ", targetCandidates.Select(t => $"{t.SignalName}={signalValues.GetValueOrDefault(t.SignalName, false)}"))}]。不满足条件：Source为true但{falseTargets.Count}个Target为false（{string.Join(", ", falseTargets.Select(t => t.SignalName))}），违反了蕴含规则。请结合规则描述【{rule.Description}】和信号名称分析因果关系。"
                            };
                        }
                    }
                    break;

                case "SAME_STATE":
                    // 同步驱动：所有 Member 状态必须一致（同 true 或同 false），用于诊断同步驱动线圈/继电器的接线/硬件故障
                    {
                        var memberPool = memberSignals.Count > 0 ? resolvedMemberSignals : scopedSignals;
                        var resolvedMembers = memberPool.Where(s => signalValues.ContainsKey(s.SignalName)).ToList();
                        if (resolvedMembers.Count < 2)
                        {
                            break;
                        }

                        var trueMembers = resolvedMembers
                            .Where(s => signalValues.GetValueOrDefault(s.SignalName, false))
                            .ToList();
                        var falseMembers = resolvedMembers
                            .Where(s => !signalValues.GetValueOrDefault(s.SignalName, false))
                            .ToList();

                        // 全 true 或全 false → 通过；否则状态不一致 → 违反
                        if (trueMembers.Count == 0 || falseMembers.Count == 0)
                        {
                            break;
                        }

                        var trueNames = trueMembers.Select(s => s.SignalName).ToList();
                        var falseNames = falseMembers.Select(s => s.SignalName).ToList();
                        var allNames = resolvedMembers.Select(s => s.SignalName).ToList();
                        var signalDetails = resolvedMembers
                            .Select(s => BuildSignalDetail(
                                s,
                                signalValues.GetValueOrDefault(s.SignalName, false),
                                "与其它 Member 状态保持一致"))
                            .ToList();
                        var currentValuesList = resolvedMembers.Select(s =>
                        {
                            var value = signalValues.ContainsKey(s.SignalName) ? signalValues[s.SignalName].ToString() : "未找到";
                            return s.SignalName + "=" + value;
                        }).ToList();
                        var currentValuesStr = string.Join(", ", currentValuesList);

                        var reasonText = $"同步驱动约束违反: {trueMembers.Count} 个 Member 为 true、{falseMembers.Count} 个 Member 为 false，应同步（{rule.RuleName}）";
                        var expectedText = $"信号【{string.Join("、", allNames)}】必须同时为 true 或同时为 false";
                        var suggestionText = $"检测到状态不一致——为 true：[{string.Join(", ", trueNames)}]；为 false：[{string.Join(", ", falseNames)}]。这通常意味着同步驱动的线圈/继电器存在硬件故障（线圈断线、继电器卡死、接线松动），请优先检查接线和继电器物理状态。";
                        var judgmentBasis = $"根据约束规则【{rule.RuleName}】（类型：SAME_STATE同步驱动规则）判断，该规则要求所有 Member 信号状态保持一致（同时为 true 或同时为 false）——这些信号在 PLC 程序中被同一表达式同步驱动，物理上理应同步通断。当前PLC实时数据：[{currentValuesStr}]。检测到状态不一致：{trueMembers.Count} 个信号为 true，{falseMembers.Count} 个为 false，违反同步约束。请结合规则描述【{rule.Description}】排查同步驱动链路上的硬件故障。";

                        return new DiagnosticIssue
                        {
                            Variable = GetReadableSignalNames(allNames),
                            Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                            Status = $"true:[{string.Join(", ", trueNames)}] / false:[{string.Join(", ", falseNames)}] 状态不一致",
                            Reason = reasonText,
                            Severity = rule.Priority == "Critical" ? "严重" : "警告",
                            Category = "约束问题",
                            RelatedRule = rule.RuleName,
                            ConstraintOperator = "SAME_STATE（同步驱动）",
                            SignalDetails = signalDetails,
                            ExpectedValue = expectedText,
                            Suggestion = suggestionText,
                            JudgmentBasis = judgmentBasis
                        };
                    }

                case "CUSTOM":
                    // D-1：CUSTOM 没有可执行语义，纯函数显式返回 null；外层 ConstraintRuleEvaluator 计为「无法判定（不支持的约束类型）」
                    return null;

                case "CHAIN_ORDER":
                    // 时序链：A完成才能B
                    var orderedSignals = scopedSignals.OrderBy(x => x.SortOrder).ToList();
                    for (int i = 0; i < orderedSignals.Count - 1; i++)
                    {
                        var current = orderedSignals[i];
                        var next = orderedSignals[i + 1];

                        var currentValue = signalValues[current.SignalName];
                        var nextValue = signalValues[next.SignalName];

                        if (!currentValue && nextValue)
                        {
                            var signalDetails = orderedSignals
                                .Select(s => BuildSignalDetail(
                                    s,
                                    signalValues.GetValueOrDefault(s.SignalName, false),
                                    s.SortOrder <= current.SortOrder ? "true（必须先完成）" : "false（需等待前一步）"))
                                .ToList();

                            // 构建判断依据 - 列出所有信号的当前值和顺序
                            var orderValuesList = orderedSignals.Select(s =>
                            {
                                var value = signalValues.ContainsKey(s.SignalName) ? signalValues[s.SignalName].ToString() : "未找到";
                                return $"步骤{s.SortOrder}: {s.SignalName}={value}";
                            }).ToList();
                            var orderValuesStr = string.Join(", ", orderValuesList);

                            return new DiagnosticIssue
                            {
                                Variable = $"{GetReadableSignalName(current.SignalName)} → {GetReadableSignalName(next.SignalName)}",
                                Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                                Status = $"前一步={currentValue}, 后一步={nextValue}",
                                Reason = $"时序约束违反: {GetReadableSignalName(next.SignalName)}已触发但{GetReadableSignalName(current.SignalName)}未完成",
                                Severity = rule.Priority == "Critical" ? "严重" : "警告",
                                Category = "约束问题",
                                RelatedRule = rule.RuleName,
                                ConstraintOperator = "CHAIN_ORDER（时序链）",
                                SignalDetails = signalDetails,
                                ExpectedValue = "前一步必须完成后才能进入下一步",
                                Suggestion = $"{GetReadableSignalName(next.SignalName)} 已触发，但前一步 {GetReadableSignalName(current.SignalName)} 还未完成。需要先完成当前步骤才能继续。",
                                JudgmentBasis = $"根据约束规则【{rule.RuleName}】（类型：CHAIN_ORDER时序链规则）判断，该规则要求：必须按顺序完成前一步才能进入下一步。当前PLC实时数据：[{orderValuesStr}]。不满足条件：下一步【{next.SignalName}】已为true，但前一步【{current.SignalName}】还未完成（为false），违反了时序规则。请结合规则描述【{rule.Description}】和信号名称分析违反时序的后果（如：工件未到位就进入下一工序会导致工件损坏）。"
                            };
                        }
                    }
                    break;
            }

            return null;
        }

        /// <summary>
        /// IO点位检查：对比实时信号与Normal_Value，找出状态异常的点位
        /// PLINQ并行处理所有点位
        /// </summary>
        /// <param name="plcIds">PLC ID列表，用于查询IO点位</param>
        /// <param name="liveSignals">实时信号字典，Key=变量名，Value=当前值</param>
        /// <returns>IO点位异常列表</returns>
        private async Task<List<DiagnosticIssue>> CheckIOTagsAsync(
            List<long> plcIds,
            Dictionary<string, object> liveSignals)
        {

            try
            {
                // 获取所有IO点位
                List<PLC_IOTagEntity> ioTags;
                if (plcIds != null && plcIds.Count > 0)
                {
                    ioTags = await _pLC_IOTagService.GetListAsync(x => plcIds.Contains(x.ProjectId ?? 0) && x.IsDeleted == false && x.ConstraintStatus != 6 && (x.Comment == null || !x.Comment.Contains("弃用")));
                }
                else
                {
                    ioTags = await _pLC_IOTagService.GetListAsync(x => x.IsDeleted == false && x.ConstraintStatus != 6 && (x.Comment == null || !x.Comment.Contains("弃用")));
                }

                if (ioTags.Count == 0)
                {
                    return new List<DiagnosticIssue>();
                }


                // 使用PLINQ并行检查所有点位
                var issues = ioTags
                    .AsParallel()
                    .Where(tag =>
                    {
                        if (!liveSignals.TryGetValue(tag.TagName, out var signalValue))
                            return false;

                        var currentValue = signalValue?.ToString();
                        var normalValue = tag.Normal_Value;

                        if (string.IsNullOrEmpty(normalValue))
                            return false;

                        if (string.Equals(currentValue, normalValue, StringComparison.OrdinalIgnoreCase))
                            return false;

                        // bool类型比较
                        if (bool.TryParse(currentValue, out var currentBool) &&
                            bool.TryParse(normalValue, out var normalBool))
                        {
                            return currentBool != normalBool;
                        }

                        return false;
                    })
                    .Select(tag =>
                    {
                        var currentValue = liveSignals[tag.TagName]?.ToString();

                        // 变量名翻译为易读的中文描述
                        var readableName = !string.IsNullOrEmpty(tag.Comment)
                            ? $"{tag.TagName} ({tag.Comment})"
                            : tag.TagName;

                        return new DiagnosticIssue
                        {
                            Variable = readableName,
                            Status = currentValue,
                            Reason = $"IO点位状态异常: 当前值={currentValue}, 正常值={tag.Normal_Value}",
                            Severity = "警告",
                            Category = "IO点位",
                            Address = tag.Address
                        };
                    })
                    .ToList();

                return issues;
            }
            catch (Exception)
            {
                return new List<DiagnosticIssue>();
            }
        }

        /// <summary>
        /// 卡滞点定位：从VariableAccess获取Write类型变量，判断Coil/Timer/Counter/Step是否卡滞
        /// Parallel并行检查每个写入变量
        /// </summary>
        /// <param name="logicUnitId">LogicUnit ID，从VariableAccess表查询该单元的写入记录</param>
        /// <param name="liveSignals">实时信号字典，Key=变量名，Value=当前值</param>
        /// <returns>卡滞点列表</returns>
        private async Task<List<StuckPoint>> LocateStuckPointsAsync(
            long logicUnitId,
            Dictionary<string, object> liveSignals)
        {

            try
            {
                // 获取该逻辑单元的所有变量访问记录（写入操作）
                var writeAccesses = await _pLC_VariableAccessService.GetListAsync(x =>
                    x.LogicUnitId == logicUnitId &&
                    x.AccessType == "Write" &&
                    x.IsDeleted == false);

                if (writeAccesses.Count == 0)
                {
                    return new List<StuckPoint>();
                }


                // 使用ConcurrentBag支持并行写入
                var stuckPointsBag = new System.Collections.Concurrent.ConcurrentBag<StuckPoint>();

                // 使用Parallel并行检查
                Parallel.ForEach(writeAccesses, access =>
                {
                    var variableName = access.VariableName;
                    var instruction = access.Instruction;
                    var isStepVar = access.IsStepVariable == true;

                    // 获取当前值
                    object currentValueObj;
                    liveSignals.TryGetValue(variableName, out currentValueObj);
                    var currentValue = currentValueObj?.ToString() ?? "false";

                    // 根据类型判断是否卡滞
                    var stuckType = GetStuckType(variableName, instruction, isStepVar, currentValue, liveSignals);
                    if (stuckType != null)
                    {
                        stuckPointsBag.Add(new StuckPoint
                        {
                            VariableName = variableName,
                            StuckType = stuckType.Value.stuckType,
                            LogicUnitId = access.LogicUnitId,
                            CurrentValue = currentValue,
                            ExpectedValue = stuckType.Value.expectedValue,
                            Reason = stuckType.Value.reason
                        });

                    }
                });

                var stuckPoints = stuckPointsBag.ToList();
                return stuckPoints;
            }
            catch (Exception)
            {
                return new List<StuckPoint>();
            }
        }

        /// <summary>
        /// 按项目查询卡滞点：查询指定项目下所有LogicUnit的写入变量，判断是否卡滞
        /// 用于没有指定具体LogicUnitId时，对整个项目进行诊断
        /// </summary>
        /// <param name="plcIds">项目ID</param>
        /// <param name="liveSignals">实时信号字典</param>
        /// <returns>卡滞点列表</returns>
        private async Task<List<StuckPoint>> LocateStuckPointsByProjectAsync(
            List<long> plcIds,
            Dictionary<string, object> liveSignals)
        {

            try
            {
                // 1. 获取该项目下的所有LogicUnit
                var logicUnits = await _pLC_LogicUnitService.GetListAsync(x =>
                    x.BlockId != null && x.IsDeleted == false);

                if (logicUnits.Count == 0)
                {
                    return new List<StuckPoint>();
                }


                // 2. 获取这些LogicUnit的所有写入变量
                var logicUnitIds = logicUnits.Select(x => x.Id).ToList();
                var allWriteAccesses = await _pLC_VariableAccessService.GetListAsync(x =>
                    x.LogicUnitId.HasValue && logicUnitIds.Contains(x.LogicUnitId.Value) &&
                    x.AccessType == "Write" &&
                    x.IsDeleted == false);


                // 3. 使用ConcurrentBag支持并行检查
                var stuckPointsBag = new System.Collections.Concurrent.ConcurrentBag<StuckPoint>();

                // 4. 按LogicUnit分组，并行检查每个单元
                var accessesByUnit = allWriteAccesses.GroupBy(x => x.LogicUnitId).ToList();

                Parallel.ForEach(accessesByUnit, group =>
                {
                    foreach (var access in group)
                    {
                        var variableName = access.VariableName;
                        var instruction = access.Instruction;
                        var isStepVar = access.IsStepVariable == true;

                        // 获取当前值
                        object currentValueObj;
                        liveSignals.TryGetValue(variableName, out currentValueObj);
                        var currentValue = currentValueObj?.ToString() ?? "false";

                        // 判断是否卡滞
                        var stuckType = GetStuckType(variableName, instruction, isStepVar, currentValue, liveSignals);
                        if (stuckType != null)
                        {
                            stuckPointsBag.Add(new StuckPoint
                            {
                                VariableName = variableName,
                                StuckType = stuckType.Value.stuckType,
                                LogicUnitId = access.LogicUnitId,
                                CurrentValue = currentValue,
                                ExpectedValue = stuckType.Value.expectedValue,
                                Reason = stuckType.Value.reason
                            });

                        }
                    }
                });

                var stuckPoints = stuckPointsBag.ToList();
                return stuckPoints;
            }
            catch (Exception)
            {
                return new List<StuckPoint>();
            }
        }

        /// <summary>多 PLC 诊断读取的并发上限（每个 PLC 内部仍按批顺序读）。</summary>
        internal const int LiveSignalReadParallelism = 4;

        /// <summary>
        /// D-1：读取诊断实时信号（Good 值 + 非 Good 原因 + PLC 级状态），按 projectid 分组。
        /// 分组的原因见 2026-05-29：同一裸地址在不同 PLC 语义不同，约束只在自己所属 PLC 内匹配。
        /// PLC 级读取失败不再吞成空字典，而是记为该 PLC CommFailure。
        /// 多个 PLC 并行读取（上限 <see cref="LiveSignalReadParallelism"/>），单个 PLC 的异常只影响它自己；
        /// 调用方取消照常抛出。
        /// </summary>
        internal async Task<Dictionary<long, PlcLiveSignals>> GetLiveSignalsByPlcAsync(
            List<PLC_ProjectEntity> plcProjectEntities,
            CancellationToken cancellationToken = default)
        {
            var plcs = (plcProjectEntities ?? new List<PLC_ProjectEntity>()).Where(p => p != null).ToList();
            using var gate = new SemaphoreSlim(LiveSignalReadParallelism);

            // fix round 2（2026-09-20）：原实现直接 plcs.Select(...).ToList() 并行发起，安全性依赖一个隐藏前提——
            // 调用方（DiagnoseEnergyFlowAsync 及更早的祖先帧）在 fork 之前没有同步碰过 SqlSugarScope。
            // SqlSugarScope 把"当前流应使用哪个底层客户端"缓存进 AsyncLocal；若祖先帧已经解析并缓存过一次，
            // 下面几路并行分支都是从同一个已带值的 ExecutionContext fork 出来的，会直接继承同一个客户端引用，
            // 在并发 Npgsql 命令下互相打架抛异常。改为在 ExecutionContext.SuppressFlow() 范围内发起 Task.Run：
            // 每路分支以完全空白的 AsyncLocal 起步，逼 SqlSugarScope 在各自的流里重新解析/缓存客户端，从根本上
            // 排除"祖先是否碰过库"这个前提假设，覆盖本读取链路上所有 DB touch 点（诊断 IO 点加载器、项目
            // GetById、IGW 台账/GRAPH 工站加载），不必逐处补 CopyNew()。
            // 选 SuppressFlow 而非逐处 CopyNew() 的依据：核对过本读取链路（PLC_VariableAccessService /
            // IotService / IgwDiagnosticReader / PLC_IgwRegistrationService / RequestToHttpHelper）不依赖
            // IHttpContextAccessor、当前用户等随流传递的环境——IGW 请求本就不带鉴权凭据（无 ambient user），
            // HTTP 调用只用 IHttpClientFactory，没有 ILogger.BeginScope。SuppressFlow 会顺带切断这几路并行
            // 读取内部的日志外层 scope/文化设置，但没有实质依赖，代价可接受。
            var reads = new List<Task<PlcLiveSignals>>(plcs.Count);
            foreach (var plc in plcs)
            {
                Task<PlcLiveSignals> task;
                using (ExecutionContext.SuppressFlow())
                {
                    task = Task.Run(() => ReadOnePlcLiveSignalsAsync(plc, gate, cancellationToken), cancellationToken);
                }
                reads.Add(task);
            }
            var results = await Task.WhenAll(reads);

            // 按输入顺序归位（同一 Id 重复出现时后者覆盖，与原顺序实现一致）
            var grouped = new Dictionary<long, PlcLiveSignals>();
            for (var i = 0; i < plcs.Count; i++)
            {
                grouped[plcs[i].Id] = results[i];
            }
            return grouped;
        }

        private async Task<PlcLiveSignals> ReadOnePlcLiveSignalsAsync(
            PLC_ProjectEntity plc, SemaphoreSlim gate, CancellationToken cancellationToken)
        {
            var plcName = string.IsNullOrWhiteSpace(plc.ProjectName) ? plc.Id.ToString() : plc.ProjectName;
            await gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await _pLC_VariableAccessService.ReadDiagnosticSignalsAsync(plc.Id, cancellationToken);
                return PlcLiveSignals.FromReadResult(plc.Id, plcName, read);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return PlcLiveSignals.Failed(plc.Id, plcName, PLC_DiagnosticProjectStatus.CommFailure,
                    ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>拍平为单字典（仅 Good 值，同名后者覆盖），供不做 PLC 隔离的安全/卡滞检查用（当前均已禁用）。</summary>
        private static Dictionary<string, object> FlattenGroupedLiveSignals(Dictionary<long, PlcLiveSignals> grouped)
        {
            var flat = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (grouped == null) return flat;
            foreach (var plc in grouped.Values)
                foreach (var kv in plc.Values)
                    flat[kv.Key] = kv.Value;
            return flat;
        }

        /// <summary>
        /// 查询所有卡滞点：对全系统所有LogicUnit进行卡滞点扫描
        /// 用于没有指定任何ID时的全量诊断场景
        /// </summary>
        /// <param name="liveSignals">实时信号字典</param>
        /// <returns>卡滞点列表</returns>
        private async Task<List<StuckPoint>> LocateAllStuckPointsAsync(
            Dictionary<string, object> liveSignals)
        {

            try
            {
                // 1. 获取所有LogicUnit
                var logicUnits = await _pLC_LogicUnitService.GetListAsync(x => x.IsDeleted == false);

                if (logicUnits.Count == 0)
                {
                    return new List<StuckPoint>();
                }


                // 2. 获取所有写入变量
                var logicUnitIds = logicUnits.Select(x => x.Id).ToList();
                var allWriteAccesses = await _pLC_VariableAccessService.GetListAsync(x =>
                    x.LogicUnitId.HasValue && logicUnitIds.Contains(x.LogicUnitId.Value) &&
                    x.AccessType == "Write" &&
                    x.IsDeleted == false);


                // 3. 并行检查所有变量
                var stuckPointsBag = new System.Collections.Concurrent.ConcurrentBag<StuckPoint>();
                var accessesByUnit = allWriteAccesses.GroupBy(x => x.LogicUnitId).ToList();

                Parallel.ForEach(accessesByUnit, group =>
                {
                    foreach (var access in group)
                    {
                        var variableName = access.VariableName;
                        var instruction = access.Instruction;
                        var isStepVar = access.IsStepVariable == true;

                        object currentValueObj;
                        liveSignals.TryGetValue(variableName, out currentValueObj);
                        var currentValue = currentValueObj?.ToString() ?? "false";

                        var stuckType = GetStuckType(variableName, instruction, isStepVar, currentValue, liveSignals);
                        if (stuckType != null)
                        {
                            stuckPointsBag.Add(new StuckPoint
                            {
                                VariableName = variableName,
                                StuckType = stuckType.Value.stuckType,
                                LogicUnitId = access.LogicUnitId,
                                CurrentValue = currentValue,
                                ExpectedValue = stuckType.Value.expectedValue,
                                Reason = stuckType.Value.reason
                            });

                        }
                    }
                });

                var stuckPoints = stuckPointsBag.ToList();
                return stuckPoints;
            }
            catch (Exception)
            {
                return new List<StuckPoint>();
            }
        }

        /// <summary>
        /// 卡滞判断：根据Instruction(Coil/Timer/Counter/Step)判断变量是否卡滞
        /// 返回卡滞类型、期望值、原因元组，无卡滞返回null
        /// </summary>
        /// <param name="variableName">变量名</param>
        /// <param name="instruction">指令类型：Coil/Timer/Counter/Step</param>
        /// <param name="isStepVariable">是否为步序变量</param>
        /// <param name="currentValue">当前值</param>
        /// <param name="liveSignals">实时信号字典，用于查找相关变量</param>
        /// <returns>元组(卡滞类型, 期望值, 原因)，无卡滞返回null</returns>
        private (string stuckType, string expectedValue, string reason)? GetStuckType(
            string variableName,
            string instruction,
            bool isStepVariable,
            string currentValue,
            Dictionary<string, object> liveSignals)
        {
            // 检查是否为步序变量卡滞
            if (isStepVariable)
            {
                if (currentValue.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    return ("Step", "true", "步序/状态未激活，无法转换到下一步");
                }
            }

            // 根据指令类型判断
            if (!string.IsNullOrEmpty(instruction))
            {
                var inst = instruction.ToUpper().Trim();

                // 线圈指令 (=, =R, =S)
                if (inst == "=" || inst == "=R" || inst == "=S")
                {
                    if (currentValue.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        return ("Coil", "true", "输出线圈未励磁，可能被前级逻辑阻断");
                    }
                }

                // 定时器指令 (SD, SS, SF, SP)
                if (inst == "SD" || inst == "SS" || inst == "SF" || inst == "SP")
                {
                    if (currentValue == "0" || currentValue == "false")
                    {
                        return ("Timer", "running", "定时器未启动或未计时");
                    }
                }

                // 计数器指令 (CU, CD, CS)
                if (inst == "CU" || inst == "CD" || inst == "CS")
                {
                    if (currentValue == "0" || currentValue == "false")
                    {
                        return ("Counter", ">0", "计数器未计数");
                    }
                }
            }

            // 变量名模式匹配辅助判断 - 仅用于有明确指令标记的情况
            var varName = variableName?.ToUpper() ?? "";

            // 只有当 instruction 字段有明确标记时才判断为定时器/计数器
            // 不再使用变量名模式匹配作为辅助判断，避免误报

            // 默认：只有步序变量且当前值为 false 时才检查
            if (isStepVariable && currentValue.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return ("Step", "true", "步序变量未激活，无法转换到下一步");
            }

            return null;
        }
        #endregion
        /// <summary>
        /// 批量诊断验证：验证多个诊断请求的数据完整性
        /// </summary>
        /// <param name="request">包含多个诊断Payload的请求</param>
        /// <returns>每个Payload的验证结果</returns>
        public async Task<HttpResponseResultModel<DiagnosticStrategyFactoryResponseDto>> ValidateDataAsync(
            DiagnosticStrategyFactoryRequestDto request)
        {

            try
            {
                if (request.Payload == null || request.Payload.Count == 0)
                {
                    return new HttpResponseResultModel<DiagnosticStrategyFactoryResponseDto>
                    {
                        IsSuccess = false,
                        HttpStatusCode = HttpStatusCode.BadRequest,
                        ErrorMessage = "Payload 不能为空"
                    };
                }

                var stopwatch = Stopwatch.StartNew();

                var result = await _diagnosticValidator.ValidateBatchAsync(
                    request.Payload,
                    request.MaxDegreeOfParallelism ?? 5);

                stopwatch.Stop();


                return new HttpResponseResultModel<DiagnosticStrategyFactoryResponseDto>
                {
                    IsSuccess = true,
                    HttpStatusCode = HttpStatusCode.OK,
                    BackResult = result
                };
            }
            catch (Exception ex)
            {

                return new HttpResponseResultModel<DiagnosticStrategyFactoryResponseDto>
                {
                    IsSuccess = false,
                    HttpStatusCode = HttpStatusCode.InternalServerError,
                    ErrorMessage = ex.Message,
                    ExceptionMessage = ex.ToString()
                };
            }
        }


        /// <summary>
        /// 验证单条数据：解析并验证单条PLC逻辑数据
        /// </summary>
        /// <param name="data">要验证的JSON数据</param>
        /// <returns>验证结果</returns>
        public async Task<HttpResponseResultModel<ValidationResultDto>> ValidateSingleDataAsync(string data)
        {

            try
            {
                var result = await _diagnosticValidator.ValidateSingleAsync(data);

                return new HttpResponseResultModel<ValidationResultDto>
                {
                    IsSuccess = result.Success,
                    HttpStatusCode = HttpStatusCode.OK,
                    BackResult = result
                };
            }
            catch (Exception ex)
            {

                return new HttpResponseResultModel<ValidationResultDto>
                {
                    IsSuccess = false,
                    HttpStatusCode = HttpStatusCode.InternalServerError,
                    ErrorMessage = ex.Message,
                    ExceptionMessage = ex.ToString()
                };
            }
        }


        /// <summary>
        /// 步骤交叉引用诊断：AI深度分析卡住的步骤，查找依赖关系
        /// </summary>
        /// <param name="request">包含CurrentStepId、ProjectId、LiveSignals的请求对象</param>
        /// <returns>交叉引用诊断结果</returns>
        public async Task<HttpResponseResultModel<StepCrossReferenceDiagnosticResponseDto>> DiagnoseStepWithCrossReferenceAsync(
            StepCrossReferenceDiagnosticRequestDto request)
        {

            try
            {
                var result = await _stepCrossRefDiagnostic.DiagnoseStepWithCrossReferenceAsync(request);

                return new HttpResponseResultModel<StepCrossReferenceDiagnosticResponseDto>
                {
                    IsSuccess = result.Success,
                    HttpStatusCode = result.Success ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                    BackResult = result,
                    ErrorMessage = result.ErrorMessage
                };
            }
            catch (Exception ex)
            {

                return new HttpResponseResultModel<StepCrossReferenceDiagnosticResponseDto>
                {
                    IsSuccess = false,
                    HttpStatusCode = HttpStatusCode.InternalServerError,
                    ErrorMessage = ex.Message,
                    ExceptionMessage = ex.ToString()
                };
            }
        }

        /// <summary>
        /// 构建诊断响应：IsSuccess 恒 true，BackResult 为纯文本报告（MCP diagnose_energy_flow 与停机诊断 ConstraintReport 直接使用）。
        /// </summary>
        /// <param name="status">safety/constraint/io_tag/stuck/warning/normal/partial_undetermined/collect_failed/check_failed</param>
        /// <param name="issues">诊断发现的问题列表（违规）</param>
        /// <param name="stuckPoints">卡滞点列表</param>
        /// <param name="constraintOutcome">约束检查结果（汇总行 / 无法判定条目 / 失败信息），可为 null</param>
        /// <param name="plcs">各 PLC 实时信号（采集失败报告逐 PLC 列原因），可为 null</param>
        private Task<HttpResponseResultModel<string>> BuildDiagnosticResponseAsync(
            string status,
            List<DiagnosticIssue> issues,
            List<StuckPoint> stuckPoints,
            ConstraintCheckOutcome constraintOutcome = null,
            IReadOnlyCollection<PlcLiveSignals> plcs = null)
        {
            return Task.FromResult(new HttpResponseResultModel<string>
            {
                IsSuccess = true,
                HttpStatusCode = HttpStatusCode.OK,
                BackResult = BuildDirectDiagnosticReport(status, issues, stuckPoints, constraintOutcome, plcs),
            });
        }

        /// <summary>
        /// 从 AI 响应中提取纯 JSON 内容
        /// 支持直接 JSON、markdown 代码块包裹、plain text 混合等情况
        /// </summary>
        /// <param name="aiResponse">AI 原始响应字符串</param>
        /// <returns>提取出的纯 JSON 字符串，提取失败返回 null</returns>
        private string ExtractJsonFromResponse(string aiResponse)
        {
            if (string.IsNullOrWhiteSpace(aiResponse))
            {
                return null;
            }

            // 尝试直接解析（如果已经是纯 JSON）
            var trimmed = aiResponse.Trim();
            if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
            {
                return trimmed;
            }

            // 查找 ```json ... ``` 代码块
            var jsonBlockStart = aiResponse.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (jsonBlockStart >= 0)
            {
                var jsonStart = aiResponse.IndexOf('\n', jsonBlockStart) + 1;
                var jsonEnd = aiResponse.IndexOf("```", jsonStart);
                if (jsonEnd > jsonStart)
                {
                    return aiResponse.Substring(jsonStart, jsonEnd - jsonStart).Trim();
                }
            }

            // 查找 ``` ... ``` 代码块（无语言标识）
            var codeBlockStart = aiResponse.IndexOf("```");
            if (codeBlockStart >= 0)
            {
                var jsonStart = aiResponse.IndexOf('\n', codeBlockStart) + 1;
                var jsonEnd = aiResponse.IndexOf("```", jsonStart);
                if (jsonEnd > jsonStart)
                {
                    var content = aiResponse.Substring(jsonStart, jsonEnd - jsonStart).Trim();
                    if (content.StartsWith("[") || content.StartsWith("{"))
                    {
                        return content;
                    }
                }
            }

            // 尝试查找第一个 [ 或 { 到最后一个 ] 或 }
            var arrayStart = aiResponse.IndexOf('[');
            var objectStart = aiResponse.IndexOf('{');
            var start = arrayStart >= 0 && objectStart >= 0
                ? Math.Min(arrayStart, objectStart)
                : Math.Max(arrayStart, objectStart);

            if (start >= 0)
            {
                var arrayEnd = aiResponse.LastIndexOf(']');
                var objectEnd = aiResponse.LastIndexOf('}');
                var end = Math.Max(arrayEnd, objectEnd);

                if (end > start)
                {
                    return aiResponse.Substring(start, end - start + 1);
                }
            }


            return null;
        }

        /// <summary>
        /// 执行查询策略：AI决策查询范围（当前/本Block/跨Block），分页批量查询 
        /// </summary>
        /// <param name="queryStrategy">AI返回的查询策略（SearchScope范围）</param>
        /// <param name="currentLogicUnitId">当前卡住的LogicUnitId</param>
        /// <returns>RelatedLogicData包含相关LogicUnit列表</returns>
        private async Task<RelatedLogicData> ExecuteQueryStrategyAsync(
            QueryStrategy queryStrategy,
            long currentLogicUnitId)
        {

            var result = new RelatedLogicData
            {
                CurrentUnit = await _pLC_LogicUnitService.GetByIdAsync(currentLogicUnitId),
                SearchStrategy = queryStrategy
            };

            List<LogicUnitWithContext> relatedUnits = new List<LogicUnitWithContext>();

            try
            {
                // 智能查询决策
                switch (queryStrategy.SearchScope?.ToLower())
                {
                    case "same_step":
                        relatedUnits = await QueryLogicUnitsAsync(currentLogicUnitId, LogicUnitQueryScope.SameStep);
                        break;

                    case "same_block":
                        relatedUnits = await QueryLogicUnitsAsync(currentLogicUnitId, LogicUnitQueryScope.SameBlock);
                        break;

                    case "all_project":
                        // 智能限制：根据搜索变量数量决定查询范围
                        int maxResults = DetermineMaxResults(queryStrategy);
                        relatedUnits = await QueryLogicUnitsAsync(currentLogicUnitId, LogicUnitQueryScope.AllProject, maxResults);
                        break;

                    default:
                        relatedUnits = await QueryLogicUnitsAsync(currentLogicUnitId, LogicUnitQueryScope.SameBlock);
                        break;
                }

                // 过滤包含目标变量的 LogicUnit
                if (queryStrategy.SearchVariables != null && queryStrategy.SearchVariables.Count > 0)
                {
                    result.RelatedUnits = FilterUnitsByVariables(relatedUnits, queryStrategy.SearchVariables);
                }
                else
                {
                    result.RelatedUnits = relatedUnits;
                }

                // 智能优化：如果结果数量过少，尝试扩大范围
                if (result.RelatedUnits.Count == 0 && queryStrategy.SearchScope?.ToLower() == "same_block")
                {
                    relatedUnits = await QueryLogicUnitsAsync(currentLogicUnitId, LogicUnitQueryScope.SameStep);
                    result.RelatedUnits = FilterUnitsByVariables(relatedUnits, queryStrategy.SearchVariables);
                }
            }
            catch (Exception)
            {
                result.RelatedUnits = new List<LogicUnitWithContext>();
            }

            return result;
        }

        /// <summary>
        /// 根据查询策略智能决定最大结果数
        /// </summary>
        /// <param name="queryStrategy">查询策略</param>
        /// <returns>最大结果数（0 表示不限制）</returns>
        private int DetermineMaxResults(QueryStrategy queryStrategy)
        {
            // 如果搜索的变量很少（1-2个），限制结果数以提高性能
            if (queryStrategy.SearchVariables != null && queryStrategy.SearchVariables.Count <= 2)
            {
                return 500;
            }

            // 如果是系统级变量（通常命名包含 System, Global, Emergency 等），可能需要查询更多
            if (queryStrategy.SearchVariables != null &&
                queryStrategy.SearchVariables.Any(v =>
                    v.Contains("System", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("Global", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("Emergency", StringComparison.OrdinalIgnoreCase)))
            {
                return 2000;
            }

            // 默认限制为 1000
            return 1000;
        }

        /// <summary>
        /// 统一查询LogicUnit：根据Scope（当前/本Block/跨Block/全项目）查询相关单元
        /// </summary>
        /// <param name="currentLogicUnitId">当前逻辑单元 ID</param>
        /// <param name="scope">查询范围</param>
        /// <param name="maxResults">最大结果数（仅对 AllProject 有效）</param>
        /// <returns>逻辑单元列表</returns>
        private async Task<List<LogicUnitWithContext>> QueryLogicUnitsAsync(
            long currentLogicUnitId,
            LogicUnitQueryScope scope,
            int maxResults = 1000)
        {

            var result = new List<LogicUnitWithContext>();

            // 1. 获取当前 LogicUnit
            var currentUnit = await _pLC_LogicUnitService.GetByIdAsync(currentLogicUnitId);
            if (currentUnit == null || !currentUnit.BlockId.HasValue)
            {
                return result;
            }

            // 2. 获取当前 Block
            var currentBlock = await _pLC_BlockService.GetByIdAsync(currentUnit.BlockId.Value);
            if (currentBlock == null)
            {
                return result;
            }

            // 3. 根据 scope 决定查询范围
            List<PLC_BlockEntity> targetBlocks;
            PLC_VariableAccessEntity currentStep = null;

            switch (scope)
            {
                case LogicUnitQueryScope.SameBlock:
                    // 只查询当前 Block
                    targetBlocks = new List<PLC_BlockEntity> { currentBlock };
                    break;

                case LogicUnitQueryScope.SameStep:
                    // 查询同一 Step 下的所有 Block
                    var currentStepList = await _pLC_VariableAccessService.GetListAsync(x => x.BlockId == currentBlock.Id);
                    currentStep = currentStepList?.FirstOrDefault();

                    if (currentStep == null)
                    {
                        return result;
                    }

                    // 获取同一 Step 下的所有 Block
                    var allBlocks = await _pLC_BlockService.GetListAsync(x => x.IsDeleted == false);
                    var allSteps = await _pLC_VariableAccessService.GetListAsync(x => x.IsDeleted == false);

                    var blockIdsInSameStep = allSteps
                        .Where(s => s.Id == currentStep.Id)
                        .Select(s => s.BlockId)
                        .Distinct()
                        .ToList();

                    targetBlocks = allBlocks.Where(b => blockIdsInSameStep.Contains(b.Id)).ToList();
                    break;

                case LogicUnitQueryScope.AllProject:
                    // 查询整个项目（带智能排序和限制）
                    targetBlocks = await _pLC_BlockService.GetListAsync(x => x.IsDeleted == false);

                    // 智能排序：当前 Block 优先
                    if (targetBlocks.Count > 100)
                    {
                        var currentBlockId = currentBlock.Id;
                        targetBlocks = targetBlocks
                            .OrderBy(b => b.Id == currentBlockId ? 0 : 1)
                            .ThenBy(b => Math.Abs(b.Id - currentBlockId))
                            .ToList();
                    }

                    break;

                default:
                    return result;
            }

            if (targetBlocks.Count == 0)
            {
                return result;
            }

            // 4. 预加载 Step 信息（避免循环查询）
            Dictionary<long, PLC_VariableAccessEntity> stepByBlockIdDict;
            if (scope == LogicUnitQueryScope.SameBlock)
            {
                // SameBlock 模式下直接查询当前 Step
                var stepList = await _pLC_VariableAccessService.GetListAsync(x => x.BlockId == currentBlock.Id);
                currentStep = stepList?.FirstOrDefault();
                stepByBlockIdDict = new Dictionary<long, PLC_VariableAccessEntity>
                {
                    [currentBlock.Id] = currentStep
                };
            }
            else if (scope == LogicUnitQueryScope.SameStep && currentStep != null)
            {
                // SameStep 模式下已经有 currentStep
                stepByBlockIdDict = targetBlocks.ToDictionary(b => b.Id, b => currentStep);
            }
            else
            {
                // AllProject 模式下预加载所有 Step
                var allSteps = await _pLC_VariableAccessService.GetListAsync(x => x.IsDeleted == false);
                stepByBlockIdDict = allSteps
                    .Where(s => s.BlockId.HasValue)
                    .GroupBy(s => s.BlockId.Value)
                    .ToDictionary(g => g.Key, g => g.FirstOrDefault());
            }


            // 5. 批量查询 LogicUnit
            var blockIds = targetBlocks.Select(b => b.Id).ToList();
            var blockDict = targetBlocks.ToDictionary(b => b.Id, b => b);

            List<PLC_LogicUnitEntity> allLogicUnits;

            if (scope == LogicUnitQueryScope.AllProject && targetBlocks.Count > 500)
            {
                // 大规模查询时分页处理
                result = await QueryLogicUnitsInBatchesAsync(blockIds, maxResults, blockDict, stepByBlockIdDict);
            }
            else
            {
                // 小规模查询直接批量查询
                allLogicUnits = await _pLC_LogicUnitService.GetListAsync(x => blockIds.Contains(x.BlockId.Value));

                // 6. 组装结果
                foreach (var unit in allLogicUnits)
                {
                    if (!unit.BlockId.HasValue || !blockDict.TryGetValue(unit.BlockId.Value, out var block))
                        continue;

                    stepByBlockIdDict.TryGetValue(block.Id, out var step);

                    result.Add(new LogicUnitWithContext
                    {
                        LogicUnit = unit,
                        StepName = step?.VariableName ?? "N/A",
                        BlockName = block.BlockName ?? $"Block-{block.Id}"
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 分页批量查询：跨Block查询时每500条分页，防止内存溢出
        /// </summary>
        /// <param name="blockIds">目标BlockId列表</param>
        /// <param name="maxResults">最大结果数</param>
        /// <param name="blockDict">Block缓存字典</param>
        /// <param name="stepByBlockIdDict">步序变量缓存</param>
        /// <returns>LogicUnit列表</returns>
        private async Task<List<LogicUnitWithContext>> QueryLogicUnitsInBatchesAsync(
            List<long> blockIds,
            int maxResults,
            Dictionary<long, PLC_BlockEntity> blockDict,
            Dictionary<long, PLC_VariableAccessEntity> stepByBlockIdDict)
        {
            var result = new List<LogicUnitWithContext>();
            const int pageSize = 500;

            var blockIdPages = blockIds
                .Select((id, index) => new { id, index })
                .GroupBy(x => x.index / pageSize)
                .Select(g => g.Select(x => x.id).ToList());

            int pageNumber = 0;
            foreach (var pageBlockIds in blockIdPages)
            {
                pageNumber++;

                var pageLogicUnits = await _pLC_LogicUnitService.GetListAsync(
                    x => pageBlockIds.Contains(x.BlockId.Value) && x.IsDeleted == false);


                foreach (var unit in pageLogicUnits)
                {
                    if (!unit.BlockId.HasValue || !blockDict.TryGetValue(unit.BlockId.Value, out var block))
                        continue;

                    stepByBlockIdDict.TryGetValue(block.Id, out var step);

                    result.Add(new LogicUnitWithContext
                    {
                        LogicUnit = unit,
                        StepName = step?.VariableName ?? "N/A",
                        BlockName = block.BlockName ?? $"Block-{block.Id}"
                    });

                    // 检查是否达到最大结果数限制
                    if (maxResults > 0 && result.Count >= maxResults)
                    {
                        return result;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 过滤包含目标变量的 LogicUnit
        /// 检查每个单元的 UidSymbolMapJson 是否包含目标变量名
        /// </summary>
        /// <param name="units">待过滤的 LogicUnit 列表</param>
        /// <param name="variables">目标变量名列表</param>
        /// <returns>包含目标变量的 LogicUnit 列表</returns>
        private List<LogicUnitWithContext> FilterUnitsByVariables(
            List<LogicUnitWithContext> units,
            List<string> variables)
        {
            var result = new List<LogicUnitWithContext>();

            foreach (var unit in units)
            {
                if (unit.LogicUnit == null || string.IsNullOrEmpty(unit.LogicUnit.UidSymbolMapJson))
                {
                    continue;
                }

                // 检查 UidSymbolMapJson 是否包含任何目标变量
                bool containsVariable = false;
                foreach (var variable in variables)
                {
                    if (unit.LogicUnit.UidSymbolMapJson.Contains(variable, StringComparison.OrdinalIgnoreCase))
                    {
                        containsVariable = true;
                        break;
                    }
                }

                if (containsVariable)
                {
                    result.Add(unit);
                }
            }

            return result;
        }

        /// <summary>
        /// 直接生成卡点清单：返回工位、卡住对象、当前值、应有值、原因
        /// </summary>
        /// <param name="issues">诊断异常列表</param>
        /// <returns>直接可读的诊断清单</returns>
        private async Task<string> GenerateSummaryTableAsync(List<DiagnosticIssue> issues)
        {
            return await Task.FromResult(BuildDirectDiagnosticReport("diagnosed", issues, null));
        }

        /// <summary>
        /// 直接诊断清单生成
        /// </summary>
        private string GenerateFallbackSummary(List<DiagnosticIssue> issues, int criticalCount, int warningCount)
        {
            return BuildDirectDiagnosticReport("diagnosed", issues, null);
        }

        internal static string BuildDirectDiagnosticReport(
            string status,
            List<DiagnosticIssue> issues,
            List<StuckPoint> stuckPoints,
            ConstraintCheckOutcome constraintOutcome = null,
            IReadOnlyCollection<PlcLiveSignals> plcs = null)
        {
            issues ??= new List<DiagnosticIssue>();
            stuckPoints ??= new List<StuckPoint>();
            var undetermined = constraintOutcome?.Undetermined ?? new List<DiagnosticIssue>();
            var summaryLine = constraintOutcome?.BuildSummaryLine();

            if (string.Equals(status, "collect_failed", StringComparison.OrdinalIgnoreCase))
            {
                return BuildCollectFailedReport(plcs);
            }

            if (string.Equals(status, "check_failed", StringComparison.OrdinalIgnoreCase))
            {
                var failed = new StringBuilder();
                failed.AppendLine("诊断结果");
                failed.AppendLine();
                failed.AppendLine($"状态: {TranslateDiagnosticStatus(status)}");
                // Task 6 写入的 FailureMessage 已带「约束检查执行失败: 」前缀，这里再拼一次外层短语会重复出现，
                // 组装前先剥掉前缀，保证短语只出现一次。
                var failureDetail = NormalizeInlineText(constraintOutcome?.FailureMessage, "未知错误");
                failureDetail = Regex.Replace(failureDetail, @"^约束检查执行失败[:：]\s*", string.Empty);
                failed.AppendLine($"结论: 约束检查执行失败，无法诊断（{failureDetail}）");
                return failed.ToString();
            }

            if (status == "normal" && issues.Count == 0 && stuckPoints.Count == 0 && undetermined.Count == 0)
            {
                const string normalText = "✅ 诊断完成\n\n所有检查项均通过，未发现任何异常。产线运行正常。";
                return string.IsNullOrWhiteSpace(summaryLine) ? normalText : normalText + "\n\n" + summaryLine;
            }

            var sb = new StringBuilder();
            var criticalCount = issues.Count(i =>
                i.Severity?.ToUpper() == "CRITICAL" ||
                i.Severity == "严重");
            var warningCount = issues.Count - criticalCount;

            sb.AppendLine("诊断结果");
            sb.AppendLine();
            sb.AppendLine($"状态: {TranslateDiagnosticStatus(status)}");
            if (!string.IsNullOrWhiteSpace(summaryLine))
            {
                sb.AppendLine(summaryLine);
            }
            sb.AppendLine($"异常数: {issues.Count}（严重 {criticalCount}，警告 {warningCount}）");
            if (issues.Count == 0 && undetermined.Count > 0)
            {
                sb.AppendLine($"结论: 没有确定的违规，但 {undetermined.Count} 条约束无法判定，不能视为正常");
            }

            if (stuckPoints.Count > 0)
            {
                sb.AppendLine($"卡点数: {stuckPoints.Count}");
            }

            var sortedIssues = issues
                .OrderBy(i =>
                    i.Severity?.ToUpper() == "CRITICAL" ||
                    i.Severity == "严重" ? 0 : 1)
                .ThenBy(i => ExtractStationCode(i))
                .ThenBy(i => i.RelatedRule)
                .ThenBy(i => i.Variable)
                .ToList();

            var index = 1;
            foreach (var issue in sortedIssues)
            {
                sb.AppendLine();
                sb.AppendLine($"{index}. 工位: {(issue.Category == "IO_TAG" && issue.Variable == "产线手动模式" ? "整线" : ExtractStationCode(issue))}");
                sb.AppendLine($"卡住对象: {ExtractBlockedObject(issue)}");
                sb.AppendLine($"当前值: {BuildCurrentValueText(issue)}");
                sb.AppendLine($"应有值: {BuildExpectedValueText(issue)}");
                var iPointText = BuildPhysicalInputAddressText(issue);
                if (!string.IsNullOrWhiteSpace(iPointText))
                {
                    sb.AppendLine($"I点地址: {iPointText}");
                }
                sb.AppendLine($"原因: {NormalizeInlineText(issue.Reason, "未提供原因")}");
                index++;
            }

            foreach (var issue in undetermined
                .OrderBy(i => ExtractStationCode(i))
                .ThenBy(i => i.RelatedRule)
                .ToList())
            {
                sb.AppendLine();
                sb.AppendLine($"{index}. 工位: {ExtractStationCode(issue)}");
                sb.AppendLine($"无法判定规则: {NormalizeInlineText(issue.RelatedRule, "未知规则")}");
                var unknownDetails = issue.SignalDetails?
                    .Where(d => !string.IsNullOrWhiteSpace(d?.ReadStatus))
                    .Take(8)
                    .ToList() ?? new List<SignalDetail>();
                if (unknownDetails.Count > 0)
                {
                    sb.AppendLine($"未采到信号: {string.Join(", ", unknownDetails.Select(FormatSignalValue))}");
                }
                sb.AppendLine($"原因: {NormalizeInlineText(issue.Reason, "无法判定")}");
                index++;
            }

            if (index == 1 && stuckPoints.Count > 0)
            {
                foreach (var stuckPoint in stuckPoints)
                {
                    sb.AppendLine();
                    sb.AppendLine($"{index}. 工位: {ExtractStationCode(stuckPoint?.VariableName)}");
                    sb.AppendLine($"卡住对象: {NormalizeInlineText(stuckPoint?.VariableName, "未知对象")}");
                    sb.AppendLine($"当前值: {NormalizeInlineText(stuckPoint?.CurrentValue, "未知")}");
                    sb.AppendLine($"应有值: {NormalizeInlineText(stuckPoint?.ExpectedValue, "未提供")}");
                    sb.AppendLine($"原因: {NormalizeInlineText(stuckPoint?.Reason, "未提供原因")}");
                    index++;
                }
            }

            return sb.ToString();
        }

        /// <summary>D-1：所有 PLC 都没采到 Good 信号时的报告（逐 PLC 列出原因）。</summary>
        private static string BuildCollectFailedReport(IReadOnlyCollection<PlcLiveSignals> plcs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("诊断结果");
            sb.AppendLine();
            sb.AppendLine($"状态: {TranslateDiagnosticStatus("collect_failed")}");
            sb.AppendLine("结论: 采集失败，无法诊断（所有 PLC 均未采到有效信号）");
            if (plcs == null || plcs.Count == 0)
            {
                sb.AppendLine("- 未找到任何 PLC 项目");
                return sb.ToString();
            }
            foreach (var plc in plcs.OrderBy(p => p.ProjectId))
            {
                sb.AppendLine($"- PLC {NormalizeInlineText(plc.PlcName, plc.ProjectId.ToString())}: {DescribePlcCollection(plc)}");
            }
            return sb.ToString();
        }

        private static string DescribePlcCollection(PlcLiveSignals plc)
        {
            if (plc.ProjectStatus == PLC_DiagnosticProjectStatus.ProjectUnavailable && plc.Statuses.Count == 0)
            {
                return $"项目不可读（{NormalizeInlineText(plc.ProjectError, "未知原因")}）";
            }
            if (plc.ProjectStatus == PLC_DiagnosticProjectStatus.CommFailure && plc.Statuses.Count == 0)
            {
                return $"通讯故障（{NormalizeInlineText(plc.ProjectError, "未知原因")}）";
            }
            if (plc.TotalCount == 0)
            {
                return "没有可诊断的 IO 点";
            }
            var groups = plc.Statuses.Values
                .GroupBy(reason => reason.Label)
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key} ×{group.Count()}");
            var sample = plc.Statuses.Values.Select(reason => reason.Detail).FirstOrDefault(detail => !string.IsNullOrWhiteSpace(detail));
            var text = $"有效信号 {plc.GoodCount} / 共 {plc.TotalCount}；{string.Join("、", groups)}";
            return sample == null ? text : $"{text}（示例：{NormalizeInlineText(sample, string.Empty)}）";
        }

        private static string TranslateDiagnosticStatus(string status)
        {
            return status?.ToLowerInvariant() switch
            {
                "safety" => "安全问题",
                "constraint" => "约束问题",
                "io_tag" => "IO点位问题",
                "energy_flow" => "能量流问题",
                "stuck" => "存在卡点",
                "warning" => "告警",
                "partial" => "部分完成",
                "diagnosed" => "已诊断",
                "normal" => "正常",
                "partial_undetermined" => "部分无法判定",
                "collect_failed" => "采集失败",
                "check_failed" => "约束检查失败",
                _ => string.IsNullOrWhiteSpace(status) ? "未知" : status
            };
        }

        private static string ExtractStationCode(DiagnosticIssue issue)
        {
            if (issue == null)
            {
                return "未知工位";
            }

            var candidates = new List<string>
            {
                issue.Variable,
                issue.RelatedRule,
                issue.Reason,
                issue.Status,
                issue.ExpectedValue
            };

            if (issue.SignalDetails != null)
            {
                candidates.AddRange(issue.SignalDetails.Select(x => x.SignalName));
                candidates.AddRange(issue.SignalDetails.Select(x => x.Description));
            }

            foreach (var candidate in candidates)
            {
                var station = ExtractStationCode(candidate);
                if (!string.Equals(station, "未知工位", StringComparison.Ordinal))
                {
                    return station;
                }
            }

            return "未知工位";
        }

        private static string ExtractStationCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "未知工位";
            }

            var match = Regex.Match(text, @"OP\d{3}", RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToUpperInvariant() : "未知工位";
        }

        private static string ExtractBlockedObject(DiagnosticIssue issue)
        {
            if (!string.IsNullOrWhiteSpace(issue?.RelatedRule))
            {
                return NormalizeInlineText(issue.RelatedRule, "未知对象");
            }

            var mismatchedSignals = GetMismatchSignals(issue);
            if (mismatchedSignals.Count > 0)
            {
                return string.Join(" / ", mismatchedSignals.Select(x => NormalizeInlineText(x.SignalName, "未知信号")));
            }

            return NormalizeInlineText(issue?.Variable, "未知对象");
        }

        private static string BuildCurrentValueText(DiagnosticIssue issue)
        {
            // 已采到信号：违规信号优先，没有则取前两个；未采到信号一律输出「=未采到(原因)」，绝不输出猜测值
            var parts = GetMismatchSignals(issue).Concat(GetUnknownSignals(issue)).Select(FormatSignalValue).ToList();
            return parts.Count > 0 ? string.Join(", ", parts) : NormalizeInlineText(issue?.Status, "未知");
        }

        private static string BuildExpectedValueText(DiagnosticIssue issue)
        {
            var parts = GetMismatchSignals(issue)
                .Select(x => $"{FormatSignalLabel(x)}={NormalizeInlineText(x.ExpectedValue, "未提供")}")
                .Concat(GetUnknownSignals(issue).Select(FormatSignalValue))
                .ToList();
            return parts.Count > 0 ? string.Join(", ", parts) : NormalizeInlineText(issue?.ExpectedValue, "未提供");
        }

        /// <summary>信号名(物理地址)；地址优先 SignalAddress，其次 Address，都没有则只输出信号名（§8.2）。</summary>
        internal static string FormatSignalLabel(SignalDetail detail)
        {
            var name = NormalizeInlineText(detail?.SignalName, "未知信号");
            var address = !string.IsNullOrWhiteSpace(detail?.SignalAddress) ? detail.SignalAddress.Trim() : detail?.Address?.Trim();
            return string.IsNullOrWhiteSpace(address) ? name : $"{name}({address})";
        }

        /// <summary>信号名(地址)=true/false；D-1 未采到（ReadStatus 非空）时输出 =未采到(原因)，不输出 CurrentValue。</summary>
        internal static string FormatSignalValue(SignalDetail detail)
        {
            var value = string.IsNullOrWhiteSpace(detail?.ReadStatus)
                ? (detail?.CurrentValue ?? false).ToString().ToLowerInvariant()
                : $"未采到({NormalizeInlineText(detail.ReadStatus, "未知原因")})";
            return $"{FormatSignalLabel(detail)}={value}";
        }

        /// <summary>D-1：已采到实时值的信号（ReadStatus 为空）。</summary>
        private static List<SignalDetail> GetKnownSignals(DiagnosticIssue issue) =>
            issue?.SignalDetails?.Where(x => x != null && string.IsNullOrWhiteSpace(x.ReadStatus)).ToList()
            ?? new List<SignalDetail>();

        /// <summary>D-1：未采到的信号（ReadStatus 非空；其 CurrentValue/ExpectedValue 没有意义）。</summary>
        private static List<SignalDetail> GetUnknownSignals(DiagnosticIssue issue) =>
            issue?.SignalDetails?.Where(x => x != null && !string.IsNullOrWhiteSpace(x.ReadStatus)).ToList()
            ?? new List<SignalDetail>();

        private static string BuildPhysicalInputAddressText(DiagnosticIssue issue)
        {
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(issue?.Address)
                && issue.Address.Trim().StartsWith("%I", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add($"{NormalizeInlineText(issue.Variable, "未知信号")}={issue.Address.Trim()}");
            }

            if (issue?.SignalDetails != null)
            {
                // D-1：未采到的信号（ReadStatus 非空）没有真实地址证据，不能列入 I 点地址证据行，
                // 否则停机诊断 LLM 会把「点名不存在/通讯故障」当成已确认的故障点。
                candidates.AddRange(issue.SignalDetails
                    .Where(x => string.IsNullOrWhiteSpace(x.ReadStatus)
                        && !string.IsNullOrWhiteSpace(x.Address)
                        && x.Address.Trim().StartsWith("%I", StringComparison.OrdinalIgnoreCase))
                    .Select(x =>
                    {
                        var label = !string.IsNullOrWhiteSpace(x.Description)
                            ? x.Description
                            : x.SignalName;
                        return $"{NormalizeInlineText(label, "未知信号")}={x.Address.Trim()}";
                    }));
            }

            var distinct = candidates
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            return distinct.Count == 0 ? null : string.Join(", ", distinct);
        }

        private static List<SignalDetail> GetMismatchSignals(DiagnosticIssue issue)
        {
            if (issue?.SignalDetails == null || issue.SignalDetails.Count == 0)
            {
                return new List<SignalDetail>();
            }

            // D-1：未采到的信号（ReadStatus 非空）不参与 mismatch 选择与兜底，它们由 GetUnknownSignals 单独以「未采到」呈现
            var knownSignals = GetKnownSignals(issue);
            var mismatches = knownSignals
                .Where(x => IsSignalMismatch(x))
                .ToList();

            return mismatches.Count > 0
                ? mismatches
                : knownSignals.Take(2).ToList();
        }

        private static bool IsSignalMismatch(SignalDetail detail)
        {
            if (detail == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(detail.ReadStatus))
            {
                return false; // D-1：未采到的信号 CurrentValue 无意义，不能据此判定不一致
            }

            var expected = detail.ExpectedValue;
            if (string.IsNullOrWhiteSpace(expected))
            {
                return true;
            }

            if (expected.Contains("true", StringComparison.OrdinalIgnoreCase))
            {
                return detail.CurrentValue != true;
            }

            if (expected.Contains("false", StringComparison.OrdinalIgnoreCase))
            {
                return detail.CurrentValue != false;
            }

            return true;
        }

        private static string NormalizeInlineText(string text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            var normalized = Regex.Replace(text, @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
        }

        /// <summary>
        /// 创建部分响应：诊断中途失败时返回已收集的数据+错误说明
        /// </summary>
        /// <param name="issues">已收集的问题列表</param>
        /// <param name="criticalCount">CRITICAL数量</param>
        /// <param name="note">错误说明</param>
        /// <returns>部分诊断结果</returns>
        private async Task<EnergyFlowDiagnosticResponse> CreatePartialResponseAsync(
            List<DiagnosticIssue> issues,
            int criticalCount,
            string note)
        {
            // 使用 AI 生成摘要表格
            var summary = await GenerateSummaryTableAsync(issues);

            return new EnergyFlowDiagnosticResponse
            {
                Status = "partial",
                Summary = summary,
                Statistics = new DiagnosticStatistics
                {
                    TotalIssues = issues?.Count ?? 0,
                    CriticalCount = criticalCount,
                    WarningCount = (issues?.Count ?? 0) - criticalCount,
                    AnalyzedVariables = 0
                },
                Issues = issues,
                Note = note
            };
        }

        /// <summary>
        /// 约束违反初步记录
        /// </summary>
        private class PreliminaryViolationRecord
        {
            public PLC_IOConstraintRuleEntity Rule { get; set; }
            public List<PLC_IOConstraintSignalEntity> Signals { get; set; }
            public Dictionary<string, bool> SignalValues { get; set; }
            public DiagnosticIssue Violation { get; set; }
            public string ConstraintOperator { get; set; }
        }

        private class ConstraintLogicUnitPayload
        {
            public long LogicUnitId { get; set; }
            public long? BlockId { get; set; }
            public string BlockName { get; set; }
            public string ProgrammingLanguage { get; set; }
            public string Title { get; set; }
            public string RawContent { get; set; }
            public string UidSymbolMapJson { get; set; }
        }

        private class ConstraintAiReviewResult
        {
            public string Decision { get; set; }
            public double Confidence { get; set; }
            public string ReviewerSummary { get; set; }
            public string CorrectedReason { get; set; }
            public string CorrectedExpectedValue { get; set; }
            public string CorrectedSuggestion { get; set; }
            public string CorrectedJudgmentBasis { get; set; }
        }

        #region 🆕 诊断记忆与自学习

        /// <summary>
        /// 保存诊断经验到长期记忆
        /// </summary>
        private async Task SaveDiagnosticExperienceAsync(
            EnergyFlowDiagnosticRequestDto request,
            List<DiagnosticIssue> issues,
            List<DiagnosticIssue> criticalIssues,
            List<CrossReferenceResult> crossRefResults)
        {
            if (_memoryService == null || issues == null || issues.Count == 0)
            {
                return;
            }

            try
            {

                foreach (var issue in criticalIssues)
                {
                    if (string.IsNullOrEmpty(issue.Variable) || string.IsNullOrEmpty(issue.Reason))
                    {
                        continue;
                    }

                    var patternSummary = $"变量[{issue.Variable}]在[{issue.Category}]类别中出现异常，原因：{issue.Reason}。状态：{issue.Status}。建议：{issue.Suggestion ?? "检查相关逻辑"}";

                    var memory = new DiagnosticMemoryEntity
                    {
                        SceneType = DiagnosticMemoryService.SceneEnergyFlow,
                        ProductionLineId = request.ProductionLineId > 0 ? request.ProductionLineId : null,
                        LayerLevel = 1,
                        MemoryType = "pattern",
                        Content = patternSummary,
                        PatternTags = JsonSerializer.Serialize(new { category = issue.Category, severity = issue.Severity }),
                        SignalType = issue.Category,
                        Source = "diagnostic_extract",
                        ImportanceScore = issue.Severity?.ToUpper() == "CRITICAL" ? 8.0f : 5.0f,
                        IsVerified = false,
                        IsActive = true
                    };

                    await _memoryService.SaveMemoryAsync(memory);
                }

            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 自学习：从诊断结果中提取模式并更新记忆
        /// </summary>
        private async Task LearnFromDiagnosticAsync(
            EnergyFlowDiagnosticRequestDto request,
            List<DiagnosticIssue> issues,
            List<CrossReferenceResult> crossRefResults)
        {
            if (_memoryService == null)
            {
                return;
            }

            try
            {
                // 检查是否有反复出现的问题模式
                if (issues == null || issues.Count < 3)
                {
                    return;
                }

                // 统计各类别问题数量
                var categoryStats = issues
                    .Where(i => !string.IsNullOrEmpty(i.Category))
                    .GroupBy(i => i.Category)
                    .Select(g => new { Category = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .ToList();

                // 如果某类问题频繁出现，提取为高层模式
                var frequentCategories = categoryStats.Where(c => c.Count >= 3).ToList();
                foreach (var stat in frequentCategories)
                {
                    var patternContent = $"产线{request.ProductionLineId}频繁出现[{stat.Category}]问题，共{stat.Count}次。" +
                        $"用户描述：{request.UserDescription ?? "未提供"}。" +
                        $"这可能表明存在系统性问题，建议检查相关传感器和约束配置。";

                    var existingMemories = await _memoryService.SearchMemoriesAsync(
                        DiagnosticMemoryService.SceneEnergyFlow,
                        request.ProductionLineId > 0 ? request.ProductionLineId : null,
                        $"频繁问题{stat.Category}",
                        topK: 1);

                    if (existingMemories.Count == 0)
                    {
                        var memory = new IDbService.DiagnosticMemoryEntity
                        {
                            SceneType = DiagnosticMemoryService.SceneEnergyFlow,
                            ProductionLineId = request.ProductionLineId > 0 ? request.ProductionLineId : null,
                            LayerLevel = 0,
                            MemoryType = "core_rule",
                            Content = patternContent,
                            PatternTags = JsonSerializer.Serialize(new { pattern = "frequent_issue", category = stat.Category }),
                            Source = "auto_learn",
                            ImportanceScore = 7.0f,
                            IsVerified = false,
                            IsActive = true
                        };

                        await _memoryService.SaveMemoryAsync(memory);
                    }
                }

                // 从交叉引用结果中提取根因模式
                if (crossRefResults != null && crossRefResults.Count > 0)
                {
                    var rootCauses = crossRefResults
                        .Where(r => r.RootCause != null && !string.IsNullOrEmpty(r.RootCause.Description))
                        .Take(3)
                        .ToList();

                    foreach (var rootCause in rootCauses)
                    {
                        var patternContent = $"根因：{rootCause.RootCause.Description}。涉及变量：{string.Join(",", rootCause.DependencyChain ?? new List<string>())}。建议：{rootCause.RootCause.SuggestedAction ?? "检查相关传感器"}";

                        var memory = new IDbService.DiagnosticMemoryEntity
                        {
                            SceneType = DiagnosticMemoryService.SceneEnergyFlow,
                            ProductionLineId = request.ProductionLineId > 0 ? request.ProductionLineId : null,
                            LayerLevel = 1,
                            MemoryType = "pattern",
                            Content = patternContent,
                            PatternTags = JsonSerializer.Serialize(new { pattern = "root_cause", variable = rootCause.RootCause.Variable }),
                            Source = "auto_learn",
                            ImportanceScore = 6.0f,
                            IsVerified = false,
                            IsActive = true
                        };

                        await _memoryService.SaveMemoryAsync(memory);
                    }
                }

            }
            catch (Exception)
            {
            }
        }

        #endregion
    }
}
