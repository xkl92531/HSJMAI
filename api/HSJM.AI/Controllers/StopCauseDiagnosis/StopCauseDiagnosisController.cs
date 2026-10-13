using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using HSJM.AI.AppLayer.StopCauseDiagnosis;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace HSJM.AI.Controllers.StopCauseDiagnosis
{
    /// <summary>
    /// Q点停机推导 - 测试入口
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    [ApiExplorerSettings(GroupName = "PLC")]
    public class StopCauseDiagnosisController : ControllerBase
    {
        private const int PreviewItemLimit = 20;

        private readonly StopCauseDiagnosisApp _app;
        private readonly StationShadowRecurringJobService _stationShadowRecurringJobService;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly AstPlainLanguageExplainService _astPlainLanguageExplainService;
        private readonly FbLocalVariableConcretizer _fbLocalVariableConcretizer;

        public StopCauseDiagnosisController(
            StopCauseDiagnosisApp app,
            StationShadowRecurringJobService stationShadowRecurringJobService,
            IBackgroundJobClient backgroundJobClient,
            AstPlainLanguageExplainService astPlainLanguageExplainService,
            FbLocalVariableConcretizer fbLocalVariableConcretizer)
        {
            _app = app;
            _stationShadowRecurringJobService = stationShadowRecurringJobService;
            _backgroundJobClient = backgroundJobClient;
            _astPlainLanguageExplainService = astPlainLanguageExplainService;
            _fbLocalVariableConcretizer = fbLocalVariableConcretizer;
        }

        /// <summary>
        /// 批量具体化 FB/FC 内部局部变量为全局符号路径（LAD 梯形图展示用，2026-10-07）。
        /// 形参链：#I_Mode.DryRunMode → DB10050_Mode.M010_Mode.DryRunMode；
        /// STAT 镜像：#S_AutoRunMode → IDB150_OP1030_电芯上线扫码枪自动流程.S_AutoRunMode。
        /// 不可唯一具体化的路径不进 map（前端按原样显示）。
        /// </summary>
        [HttpPost("concretize-fb-locals")]
        public async Task<IActionResult> ConcretizeFbLocals([FromBody] ConcretizeFbLocalsRequestDto request)
        {
            var response = new HttpResponseResultModel<Dictionary<string, string>>();
            if (request == null || request.ProjectId <= 0
                || string.IsNullOrWhiteSpace(request.BlockName)
                || request.LocalPaths == null || request.LocalPaths.Count == 0)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "ProjectId、BlockName、LocalPaths 均不能为空";
                return Ok(response);
            }

            if (request.LocalPaths.Count > 200)
            {
                response.IsSuccess = false;
                response.ErrorMessage = "LocalPaths 单次最多 200 个";
                return Ok(response);
            }

            var map = await _fbLocalVariableConcretizer
                .TryConcretizeRangeAsync(request.ProjectId, request.BlockName.Trim(), request.LocalPaths);
            response.BackResult = map;
            response.IsSuccess = true;
            return Ok(response);
        }

        /// <summary>
        /// 独立执行工位提取与标记。
        /// 适合作为导入解析完成后的第一步，先形成工位桶和 LogicUnit 工位标签，再进入 Q 点识别与 AST 阶段。
        ///
        /// 最简请求示例：
        /// {
        ///   "projectId": 1,
        ///   "useLlmAnnotation": true,
        ///   "includePhysicalTags": true,
        ///   "includeVariableGroups": true,
        ///   "includeLogicUnitMarking": true,
        ///   "includeSourceItems": true,
        ///   "maxDetailCountPerCategory": 200
        /// }
        ///
        /// 如需回填绑定表和块工位，可传：
        /// {
        ///   "projectId": 1,
        ///   "useLlmAnnotation": true,
        ///   "includePhysicalTags": true,
        ///   "includeVariableGroups": true,
        ///   "includeLogicUnitMarking": true,
        ///   "includeSourceItems": true,
        ///   "persistStationMarking": true,
        ///   "updateBlockStationCode": true
        /// }
        /// </summary>
        [HttpPost("extract-stations")]
        public async Task<IActionResult> ExtractStations([FromBody] StationExtractionRequestDto request)
        {
            var result = await _app.ExtractStationsAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 发现锚点（物理Q点 / 虚拟Q点 / 握手位）
        /// 如只想测试虚拟Q点，可传：IncludePhysicalAnchors=false, IncludeHandshakeAnchors=false, IncludeVirtualAnchors=true
        /// </summary>
        [HttpPost("discover-anchors")]
        public async Task<IActionResult> DiscoverAnchors([FromBody] AnchorDiscoveryRequestDto request)
        {
            var result = await _app.DiscoverAnchorsAsync(request);
            WriteLogHelper.WriteLogsAsync(JsonHelper.SerializeObject(result), "发现锚点测试");
            return Ok(BuildDiscoverPreview(result));
        }

        /// <summary>
        /// 仅查看工位地图与变量传播链，适合测试“工位识别是否合理”。
        /// 推荐先用该接口校验 StationBuckets / StationConflicts / OrphanAnchors / VariablePropagations，
        /// 再决定是否继续看 AST 与染色结果。
        /// </summary>
        [HttpPost("preview-station-map")]
        public async Task<IActionResult> PreviewStationMap([FromBody] AnchorDiscoveryRequestDto request)
        {
            var result = await _app.DiscoverAnchorsAsync(request);
            WriteLogHelper.WriteLogsAsync(JsonHelper.SerializeObject(result), "工位地图预览测试");
            return Ok(BuildStationMapPreview(result));
        }

        /// <summary>
        /// 构建 writer-first 第一阶段目录：物理Q + 写入逻辑 AST + AST 语义识别预览。
        /// 测试时建议把 IncludeAstSeeds 和 IncludeAstCompileResults 都设为 true。
        ///
        /// 最简请求示例：
        /// {
        ///   "projectId": 1,
        ///   "includePhysicalAnchors": false,
        ///   "includeVirtualAnchors": true,
        ///   "includeHandshakeAnchors": false,
        ///   "includeAstSeeds": true,
        ///   "includeAstCompileResults": true,
        ///   "rawContentMaxLength": 2000
        /// }
        /// </summary>
        [HttpPost("compile-preview")]
        public async Task<IActionResult> CompilePreview([FromBody] AnchorDiscoveryRequestDto request)
        {
            var result = await _app.BuildPhaseOneCatalogAsync(request);
            WriteLogHelper.WriteLogsAsync(JsonHelper.SerializeObject(result), "编译预览测试");
            return Ok(BuildCompilePreview(result));
        }

        /// <summary>
        /// Recompile and optionally persist AST for a single LogicUnit.
        /// Useful for quickly verifying compiler fixes without rerunning full analysis.
        /// </summary>
        [HttpPost("recompile-logicunit-ast")]
        public async Task<IActionResult> RecompileLogicUnitAst([FromBody] RecompileLogicUnitAstRequestDto request)
        {
            var result = await _app.RecompileLogicUnitAstAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// Pre-extract device/internal health candidates that Graph transition tracing may not directly expose.
        /// This preview endpoint does not mutate station bindings or shadow signals.
        /// </summary>
        [HttpPost("device-health/pre-extract")]
        public async Task<IActionResult> PreExtractDeviceHealthAnchors([FromBody] DeviceHealthPreExtractRequestDto request)
        {
            var result = await _app.PreExtractDeviceHealthAnchorsAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// Query device-focused diagnostic signals synchronized into station shadow signal config.
        /// UI wording: 设备关注信号.
        /// </summary>
        [HttpGet("device-focus-signals")]
        public async Task<IActionResult> GetDeviceFocusSignals([FromQuery] DeviceFocusSignalQueryDto request)
        {
            var result = await _app.GetDeviceFocusSignalsAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// Analyze PLC AST/LLM cache and synchronize device-focused diagnostic signals into station shadow signal config.
        /// </summary>
        [HttpPost("device-focus-signals/refresh")]
        public async Task<IActionResult> RefreshDeviceFocusSignals([FromBody] DeviceFocusSignalSeedRequestDto request)
        {
            var result = await _app.SeedDeviceFocusSignalsAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 启动一键分析入库后台任务，立即返回 taskId，前端通过轮询 full-analysis-progress 获取进度。
        /// 合并了 extract-stations → station-map → compile-preview 三步。
        /// </summary>
        /// <summary>
        /// Run standalone device-focused signal diagnosis for one station, independent of GRAPH activation.
        /// </summary>
        [HttpPost("device-focus-signals/diagnose")]
        public async Task<IActionResult> DiagnoseDeviceFocusSignals([FromBody] DeviceFocusDiagnosisRequestDto request)
        {
            var result = await _app.DiagnoseDeviceFocusSignalsAsync(request);
            return Ok(result);
        }

        [HttpPost("start-full-analysis")]
        public IActionResult StartFullAnalysis([FromBody] StartFullAnalysisRequestDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return Ok(new HttpResponseResultModel<StartFullAnalysisResultDto>
                {
                    IsSuccess = false,
                    ErrorMessage = "ProjectId 不能为空"
                });
            }

            var taskId = $"full_analysis_{request.ProjectId}_{DateTime.Now:yyyyMMddHHmmssfff}";
            var progressService = HttpContext.RequestServices.GetRequiredService<FullAnalysisProgressService>();
            progressService.Start(taskId, request.ProjectId, request.StationCode);

            var jobId = _backgroundJobClient.Create(
                Job.FromExpression<StopCauseDiagnosisApp>(app => app.ExecuteFullAnalysisAsync(
                    taskId,
                    request.ProjectId,
                    request.StationCode,
                    request.BlockId,
                    request.UseLlmAnnotation,
                    request.RawContentMaxLength)),
                new EnqueuedState(HangfireQueueNames.CurrentServiceQueue));

            return Ok(new HttpResponseResultModel<StartFullAnalysisResultDto>
            {
                IsSuccess = true,
                BackResult = new StartFullAnalysisResultDto
                {
                    TaskId = taskId,
                    HangfireJobId = jobId,
                    HangfireQueueName = HangfireQueueNames.CurrentServiceQueue,
                    Message = "一键分析任务已启动，正在后台执行..."
                }
            });
        }

        /// <summary>
        /// 查询一键分析入库进度
        /// </summary>
        [HttpGet("full-analysis-progress")]
        public IActionResult GetFullAnalysisProgress([FromQuery] string taskId)
        {
            var result = _app.GetFullAnalysisProgress(taskId);
            return Ok(result ?? new FullAnalysisProgressDto
            {
                TaskId = taskId,
                Status = "NotFound",
                Message = "未找到该任务"
            });
        }

        /// <summary>
        /// 根据符号全路径读取在线实时值，并组装工位诊断快照。
        /// 适合在接入监控引擎前先校验 ReadTags 路径、符号命中率和时序字段是否完整。
        /// </summary>
        [HttpPost("acquire-diagnostic-snapshot")]
        public async Task<IActionResult> AcquireDiagnosticSnapshot([FromBody] AcquireDiagnosticSnapshotRequestDto request)
        {
            var result = await _app.AcquireDiagnosticSnapshotAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 在线读取已持久化 AST 所需信号，并返回布尔求值结果与首条失败路径。
        /// 推荐优先传 LogicUnitId；也支持按 VariableName + StationCode 回查目标 AST。
        /// </summary>
        [HttpPost("evaluate-persisted-ast")]
        public async Task<IActionResult> EvaluatePersistedAst([FromBody] EvaluatePersistedAstRequestDto request)
        {
            var result = await _app.EvaluatePersistedAstAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 单工位 GRAPH 诊断调试 — 走完整诊断流程，适合打断点跟踪 GRAPH 转移条件查找逻辑。
        /// GET /api/stop-cause-diagnosis/debug-diagnose-station?projectId=1&amp;stationCode=OP010&amp;maxAnchors=200&amp;enableDeviceFocusCheck=false
        /// </summary>
        [HttpGet("debug-diagnose-station")]
        public async Task<IActionResult> DebugDiagnoseStation(
            [FromQuery] long projectId,
            [FromQuery] string stationCode,
            [FromQuery] int maxAnchors = 200,
            [FromQuery] bool enableDeviceFocusCheck = false,
            [FromQuery] List<string> deviceFocusAnchorTypes = null)
        {
            var result = await _app.DebugDiagnoseStationAsync(projectId, stationCode, maxAnchors, enableDeviceFocusCheck, deviceFocusAnchorTypes);
            return Ok(result);
        }

        /// <summary>
        /// AST 深度追踪测试 — 独立验证单个逻辑单元的 AST 求值 + 完整失败路径追踪。
        /// 支持传 LogicUnitId（直接定位）或 VariableName（自动搜索匹配 AST）。
        /// 返回完整的求值树、失败路径、GRAPH 转移条件分析、跨 LogicUnit 写入源追踪、信号历史分析。
        /// </summary>
        [HttpPost("ast-deep-trace-test")]
        public async Task<IActionResult> AstDeepTraceTest([FromBody] AstDeepTraceTestRequestDto request)
        {
            var result = await _app.AstDeepTraceTestAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 输入 LogicUnitId，将已持久化 AST 表达式解释成白话结构；可按 FB/FC Call 引脚绑定继续下钻被调用块 AST。
        /// </summary>
        [HttpPost("ast/explain")]
        public async Task<IActionResult> ExplainAstPlainLanguage([FromBody] AstPlainExplainRequestDto request)
        {
            var result = await _astPlainLanguageExplainService.ExplainAsync(request);
            return Ok(result);
        }

        [HttpGet("persisted-writer-ast-details")]
        public async Task<IActionResult> GetPersistedWriterAstDetails([FromQuery] PersistedWriterAstQueryDto request)
        {
            var result = await _app.GetPersistedWriterAstDetailsAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                Count = result.Count,
                Data = result
            });
        }

        [HttpGet("persisted-writer-ast-paged")]
        public async Task<IActionResult> GetPersistedWriterAstPaged([FromQuery] HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS.PersistedWriterAstPagedQueryDto request)
        {
            var result = await _app.GetPersistedWriterAstDetailsPagedAsync(request);
            return Ok(result);
        }

        [HttpGet("persisted-station-map")]
        public async Task<IActionResult> GetPersistedStationMap([FromQuery] PersistedStationMapQueryDto request)
        {
            var result = await _app.GetPersistedStationMapAsync(request);
            var anchorAssignments = result?.AnchorAssignments ?? new List<HSJM.AI.AppLayer.StopCauseDiagnosis.Models.AnchorStationAssignment>();
            var orphanAnchors = result?.OrphanAnchors ?? new List<HSJM.AI.AppLayer.StopCauseDiagnosis.Models.OrphanAnchor>();
            var stationCodes = (result?.StationBuckets ?? new List<HSJM.AI.AppLayer.StopCauseDiagnosis.Models.StationBucket>())
                .Select(it => it.StationCode?.Trim())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(it => it, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = request?.ProjectId,
                StationCode = request?.StationCode,
                Stations = stationCodes,
                ExtractionSummary = new
                {
                    StationCount = result?.StationBuckets?.Count ?? 0,
                    TaggedPhysicalTagCount = anchorAssignments.Count(it =>
                        !string.IsNullOrWhiteSpace(it.AnchorType) &&
                        (it.AnchorType.Contains("Physical", StringComparison.OrdinalIgnoreCase) ||
                         it.AnchorType.Contains("QPoint", StringComparison.OrdinalIgnoreCase))),
                    VariableGroupCount = anchorAssignments.Count + orphanAnchors.Count,
                    TaggedVariableGroupCount = anchorAssignments.Count,
                    ConflictLogicUnitCount = result?.StationConflicts?.Count ?? 0
                },
                StationMapCounts = new
                {
                    StationBuckets = result?.StationBuckets?.Count ?? 0,
                    StationConflicts = result?.StationConflicts?.Count ?? 0,
                    OrphanAnchors = result?.OrphanAnchors?.Count ?? 0,
                    VariablePropagations = result?.VariablePropagations?.Count ?? 0
                },
                StationBuckets = result?.StationBuckets?.Take(PreviewItemLimit).Select(it => new
                {
                    it.StationCode,
                    it.Confidence,
                    it.AnchorCount,
                    it.LogicUnitCount,
                    it.BlockCount,
                    VariableNames = it.VariableNames.Take(6).ToList()
                }).ToList(),
                AnchorAssignments = result?.AnchorAssignments?.Take(PreviewItemLimit).ToList(),
                StationConflicts = result?.StationConflicts?.Take(PreviewItemLimit).ToList(),
                OrphanAnchors = result?.OrphanAnchors?.Take(PreviewItemLimit).ToList(),
                VariablePropagations = result?.VariablePropagations?.Take(PreviewItemLimit).ToList()
            });
        }

        [HttpGet("persisted-semantic-findings-paged")]
        public async Task<IActionResult> GetPersistedSemanticFindingsPaged([FromQuery] PersistedSemanticFindingPagedQueryDto request)
        {
            var result = await _app.GetPersistedSemanticFindingsPagedAsync(request);
            return Ok(result);
        }

        [HttpGet("unassigned-blocks")]
        public async Task<IActionResult> GetUnassignedBlocks([FromQuery] HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS.UnassignedBlocksQueryDto request)
        {
            var result = await _app.GetUnassignedBlocksAsync(request);
            return Ok(new { IsSuccess = true, BackResult = result });
        }

        [HttpGet("ast-semantic-progress")]
        public IActionResult GetAstSemanticProgress([FromQuery] string progressKey)
        {
            return Ok(_app.GetAstSemanticProgress(progressKey));
        }

        [HttpGet("monitoring/stations")]
        public async Task<IActionResult> GetMonitoringStations([FromQuery] long projectId)
        {
            var result = await _app.GetMonitoringStationsAsync(projectId);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = projectId,
                Count = result.Count,
                Data = result
            });
        }

        /// <summary>
        /// 获取所有工位最近的信号变化（产线信号总览看板）
        /// </summary>
        [HttpGet("monitoring/all-station-changes")]
        public async Task<IActionResult> GetAllStationChanges([FromQuery] long projectId, [FromQuery] int changesPerStation = 20)
        {
            var result = await _app.GetAllStationRecentChangesAsync(projectId, changesPerStation);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = projectId,
                Count = result.Count,
                Data = result
            });
        }

        [HttpGet("monitoring/station-overview")]
        public async Task<IActionResult> GetMonitoringStationOverview([FromQuery] StationShadowQueryDto request)
        {
            var result = await _app.GetMonitoringStationOverviewAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                Data = result
            });
        }

        [HttpGet("monitoring/signals")]
        public async Task<IActionResult> GetMonitoringSignals([FromQuery] StationShadowSignalQueryDto request)
        {
            var result = await _app.GetMonitoringSignalsAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = request?.ProjectId,
                StationCode = request?.StationCode,
                Count = result.Count,
                Data = result
            });
        }

        [HttpGet("monitoring/changes")]
        public async Task<IActionResult> GetMonitoringChanges([FromQuery] StationShadowTimelineQueryDto request)
        {
            var result = await _app.GetMonitoringChangesAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = request?.ProjectId,
                StationCode = request?.StationCode,
                Count = result.Count,
                Data = result
            });
        }

        [HttpGet("monitoring/bundles")]
        public async Task<IActionResult> GetMonitoringBundles([FromQuery] StationShadowTimelineQueryDto request)
        {
            var result = await _app.GetMonitoringBundlesAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = request?.ProjectId,
                StationCode = request?.StationCode,
                Count = result.Count,
                Data = result
            });
        }

        [HttpGet("monitoring/events")]
        public async Task<IActionResult> GetMonitoringEvents([FromQuery] StationShadowTimelineQueryDto request)
        {
            var result = await _app.GetMonitoringEventsAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = request?.ProjectId,
                StationCode = request?.StationCode,
                Count = result.Count,
                Data = result
            });
        }

        [HttpGet("monitoring/history")]
        public async Task<IActionResult> GetMonitoringHistory([FromQuery] StationShadowHistoryQueryDto request)
        {
            var (items, total) = await _app.GetMonitoringHistoryAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = request?.ProjectId,
                StationCode = request?.StationCode,
                Total = total,
                Count = items.Count,
                Data = items
            });
        }

        [HttpGet("monitoring/sample-library")]
        public async Task<IActionResult> GetMonitoringSampleLibrary([FromQuery] StationShadowSampleLibraryQueryDto request)
        {
            var result = await _app.GetMonitoringSampleLibraryAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = request?.ProjectId,
                StationCode = request?.StationCode,
                Count = result.Count,
                Data = result
            });
        }

        [HttpGet("monitoring/pattern-templates")]
        public async Task<IActionResult> GetMonitoringPatternTemplates([FromQuery] StationShadowPatternTemplateQueryDto request)
        {
            var result = await _app.GetMonitoringPatternTemplatesAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = request?.ProjectId,
                StationCode = request?.StationCode,
                StationMode = request?.StationMode,
                Count = result.Count,
                Data = result
            });
        }

        [HttpPost("monitoring/pattern-templates/rebuild")]
        public async Task<IActionResult> RebuildMonitoringPatternTemplates([FromBody] StationShadowPatternTemplateRebuildRequestDto request)
        {
            var result = await _app.RebuildMonitoringPatternTemplatesAsync(request);
            return Ok(result);
        }

        [HttpPost("monitoring/diagnose-pending")]
        public async Task<IActionResult> DiagnosePendingMonitoringEvents([FromBody] StationShadowDiagnosisRunRequestDto request)
        {
            var result = await _app.DiagnosePendingMonitoringEventsAsync(request);
            return Ok(result);
        }

        [HttpPost("monitoring/jobs/sync")]
        public async Task<IActionResult> SyncMonitoringJobs()
        {
            var result = await _stationShadowRecurringJobService.SyncRecurringJobsAsync();
            return Ok(new
            {
                IsSuccess = true,
                Data = result
            });
        }

        [HttpPost("monitoring/worker/pause")]
        public IActionResult PauseWorker()
        {
            StationShadowWorkerConfig.Paused = true;
            return Ok(new { IsSuccess = true, Message = "BackgroundWorker 已暂停，轮询循环将跳过执行直到恢复。" });
        }

        [HttpPost("monitoring/worker/resume")]
        public IActionResult ResumeWorker()
        {
            StationShadowWorkerConfig.Paused = false;
            return Ok(new { IsSuccess = true, Message = "BackgroundWorker 已恢复。" });
        }

        [HttpGet("monitoring/worker/status")]
        public IActionResult GetWorkerStatus()
        {
            var cfg = StationShadowWorkerConfig.Current;
            return Ok(new
            {
                IsSuccess = true,
                Enabled = cfg.Enabled,
                Paused = StationShadowWorkerConfig.Paused,
                IntervalSeconds = cfg.IntervalSeconds,
                Status = !cfg.Enabled ? "Disabled" : StationShadowWorkerConfig.Paused ? "Paused" : "Running"
            });
        }

        [HttpPost("monitoring/jobs/run-sweep")]
        public IActionResult RunMonitoringSweep()
        {
            var jobId = _backgroundJobClient.Enqueue<StationShadowRecurringJobService>(service => service.ExecuteMonitoringSweepAsync());
            return Ok(new
            {
                IsSuccess = true,
                JobId = jobId
            });
        }

        [HttpPost("monitoring/jobs/run-diagnosis")]
        public IActionResult RunMonitoringDiagnosis([FromBody] StationShadowDiagnosisRunRequestDto request)
        {
            if (request == null || request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.StationCode))
            {
                return Ok(new HttpResponseResultModel<bool>
                {
                    IsSuccess = false,
                    ErrorMessage = "ProjectId 和 StationCode 不能为空"
                });
            }

            var jobId = _backgroundJobClient.Enqueue<StationShadowRecurringJobService>(service => service.ExecutePendingDiagnosisAsync(request.ProjectId, request.StationCode));
            return Ok(new
            {
                IsSuccess = true,
                JobId = jobId
            });
        }

        [HttpGet("monitoring/snapshot")]
        public async Task<IActionResult> GetMonitoringSnapshot([FromQuery] StationShadowSnapshotQueryDto request)
        {
            var result = await _app.GetMonitoringSnapshotAsync(request);
            return Ok(new
            {
                IsSuccess = true,
                ProjectId = request?.ProjectId,
                StationCode = request?.StationCode,
                SnapshotId = request?.SnapshotId,
                Data = result
            });
        }

        [HttpPost("monitoring/run-cycle")]
        public async Task<IActionResult> RunMonitoringCycle([FromBody] RunStationShadowCycleRequestDto request)
        {
            var result = await _app.RunMonitoringCycleAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 读取工位监控信号的 IoT 实时值
        /// </summary>
        [HttpPost("monitoring/live-read")]
        public async Task<IActionResult> LiveReadMonitoringSignals([FromBody] StationShadowLiveReadRequestDto request)
        {
            var result = await _app.LiveReadMonitoringSignalsAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 从锚点绑定表自动播种监控信号到 shadow_signal 表
        /// </summary>
        [HttpPost("monitoring/seed-signals")]
        public async Task<IActionResult> SeedMonitoringSignals([FromBody] StationShadowSeedSignalsRequestDto request)
        {
            var result = await _app.SeedMonitoringSignalsAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 启动批量监控信号播种后台任务，立即返回 taskId，前端通过轮询 full-analysis-progress 获取进度。
        /// POST { "projectId": 1, "replaceExisting": true }
        /// </summary>
        [HttpPost("monitoring/batch-seed-signals")]
        public IActionResult BatchSeedMonitoringSignals([FromBody] BatchSeedRequestDto request)
        {
            if (request == null || request.ProjectId <= 0)
                return BadRequest("ProjectId 不能为空");

            var taskId = $"batch_seed_{request.ProjectId}_{DateTime.Now:yyyyMMddHHmmssfff}";
            var progressService = HttpContext.RequestServices.GetRequiredService<FullAnalysisProgressService>();
            progressService.Start(taskId, request.ProjectId, null);

            var jobId = _backgroundJobClient.Create(
                Job.FromExpression<StopCauseDiagnosisApp>(app =>
                    app.ExecuteBatchSeedSignalsAsync(taskId, request.ProjectId, request.ReplaceExisting)),
                new EnqueuedState(HangfireQueueNames.CurrentServiceQueue));

            return Ok(new HttpResponseResultModel<StartFullAnalysisResultDto>
            {
                IsSuccess = true,
                BackResult = new StartFullAnalysisResultDto
                {
                    TaskId = taskId,
                    HangfireJobId = jobId,
                    HangfireQueueName = HangfireQueueNames.CurrentServiceQueue,
                    Message = "批量监控信号播种任务已启动，正在后台执行..."
                }
            });
        }

        // ==================== 工位监控配置 CRUD ====================

        [HttpGet("monitoring/configs")]
        public async Task<IActionResult> GetMonitoringConfigs([FromQuery] StationShadowConfigQueryDto query)
        {
            var result = await _app.GetMonitoringConfigsAsync(query ?? new StationShadowConfigQueryDto());
            return Ok(new { IsSuccess = true, Count = result.Count, Data = result });
        }

        [HttpPost("monitoring/configs")]
        public async Task<IActionResult> CreateMonitoringConfig([FromBody] StationShadowConfigCreateDto dto)
        {
            var result = await _app.CreateMonitoringConfigAsync(dto);
            return Ok(result);
        }

        [HttpPut("monitoring/configs")]
        public async Task<IActionResult> UpdateMonitoringConfig([FromBody] StationShadowConfigUpdateDto dto)
        {
            var result = await _app.UpdateMonitoringConfigAsync(dto);
            return Ok(result);
        }

        [HttpDelete("monitoring/configs/{id}")]
        public async Task<IActionResult> DeleteMonitoringConfig(long id)
        {
            var result = await _app.DeleteMonitoringConfigAsync(id);
            return Ok(result);
        }

        [HttpPost("monitoring/configs/{id}/toggle")]
        public async Task<IActionResult> ToggleMonitoringConfig(long id, [FromQuery] bool enabled)
        {
            var result = await _app.ToggleMonitoringConfigAsync(id, enabled);
            return Ok(result);
        }

        /// <summary>
        /// 启动批量 Q 点提取后台任务，立即返回 taskId，前端通过轮询 full-analysis-progress 获取进度。
        /// stationCodes 为空时自动枚举全部工位。
        /// </summary>
        [HttpPost("batch-discover")]
        public IActionResult BatchDiscover([FromBody] BatchDiscoverRequestDto request)
        {
            if (request == null || request.ProjectId <= 0)
            {
                return BadRequest("ProjectId 不能为空");
            }

            var taskId = $"batch_qpoint_{request.ProjectId}_{DateTime.Now:yyyyMMddHHmmssfff}";
            var progressService = HttpContext.RequestServices.GetRequiredService<FullAnalysisProgressService>();
            progressService.Start(taskId, request.ProjectId, null);

            var jobId = _backgroundJobClient.Create(
                Job.FromExpression<StopCauseDiagnosisApp>(app => app.ExecuteBatchQPointDiscoveryAsync(
                    taskId,
                    request.ProjectId,
                    request.IncludePhysicalAnchors,
                    request.IncludeVirtualAnchors,
                    request.IncludeHandshakeAnchors,
                    request.IncludeAstSeeds,
                    request.IncludeAstCompileResults,
                    request.UseLlmAnnotation,
                    request.RawContentMaxLength)),
                new EnqueuedState(HangfireQueueNames.CurrentServiceQueue));

            return Ok(new HttpResponseResultModel<StartFullAnalysisResultDto>
            {
                IsSuccess = true,
                BackResult = new StartFullAnalysisResultDto
                {
                    TaskId = taskId,
                    HangfireJobId = jobId,
                    HangfireQueueName = HangfireQueueNames.CurrentServiceQueue,
                    Message = "批量 Q 点提取任务已启动，正在后台执行..."
                }
            });
        }

        /// <summary>
        /// 全线停机诊断（异步）：立即返回 taskId，后台并行排查所有工位，通过 SignalR 推送实时进度。
        ///
        /// 最简请求（排查所有 PLC 的所有工位）：
        /// {}
        ///
        /// 完整请求：
        /// {
        ///   "projectIds": [1, 2],
        ///   "stationCodes": ["OP010", "OP020", "OP030"],
        ///   "maxParallelStations": 3,
        ///   "maxAnchorsPerStation": 5,
        ///   "useLlmAnalysis": true
        /// }
        ///
        /// SignalR 事件（Hub: /hubs/station-shadow-monitoring, 调用 SubscribeLineDiagnosis(taskId)）：
        /// - LineDiagnosisStarted: { taskId, stationCodes, totalStations }
        /// - LineDiagnosisStationDone: { taskId, station, completedCount, totalCount }
        /// - LineDiagnosisLlmStart: { taskId }
        /// - LineDiagnosisCompleted: { taskId, result }
        /// - LineDiagnosisFailed: { taskId, errorMessage }
        /// </summary>
        [HttpPost("diagnose-line-stoppage")]
        public IActionResult DiagnoseLineStoppage([FromBody] LineDiagnosisRequestDto request)
        {
            request ??= new LineDiagnosisRequestDto();
            // 优先使用前端预生成的 taskId（前端先连 SignalR 再发 HTTP，避免事件丢失）
            var taskId = string.IsNullOrWhiteSpace(request.TaskId)
                ? $"line_diag_{DateTime.Now:yyyyMMddHHmmssfff}"
                : request.TaskId.Trim();

            _backgroundJobClient.Create(
                Job.FromExpression<StopCauseDiagnosisApp>(app =>
                    app.DiagnoseLineStoppageAsync(request, taskId)),
                new EnqueuedState(HangfireQueueNames.CurrentServiceQueue));

            return Ok(new HttpResponseResultModel<object>
            {
                IsSuccess = true,
                BackResult = new
                {
                    TaskId = taskId,
                    HangfireQueueName = HangfireQueueNames.CurrentServiceQueue,
                    Message = LineDiagnosisI18n.T(
                        "ack.started",
                        LineDiagnosisI18n.Normalize(request?.OutputLanguage)),
                }
            });
        }

        private static object BuildDiscoverPreview(HttpResponseResultModel<AnchorDiscoveryResultDto> result)
        {
            if (result?.BackResult == null)
            {
                return new
                {
                    IsSuccess = result?.IsSuccess ?? false,
                    ErrorMessage = result?.ErrorMessage,
                    ExceptionMessage = result?.ExceptionMessage
                };
            }

            var dto = result.BackResult;
            return new
            {
                result.IsSuccess,
                result.ErrorMessage,
                result.ExceptionMessage,
                dto.ProjectId,
                dto.StationCode,
                dto.GeneratedAt,
                dto.Summary,
                AnchorCounts = new
                {
                    Physical = dto.PhysicalAnchors.Count,
                    CorePhysical = dto.CorePhysicalAnchors.Count,
                    Virtual = dto.VirtualAnchors.Count,
                    CoreVirtual = dto.CoreVirtualAnchors.Count,
                    Handshake = dto.HandshakeAnchors.Count,
                    Review = dto.VirtualAnchorReviewQueue.Count
                },
                CorePhysicalAnchors = dto.CorePhysicalAnchors.Take(PreviewItemLimit).Select(BuildAnchorPreview).ToList(),
                CoreVirtualAnchors = dto.CoreVirtualAnchors.Take(PreviewItemLimit).Select(BuildAnchorPreview).ToList(),
                HandshakeAnchors = dto.HandshakeAnchors.Take(PreviewItemLimit).Select(BuildAnchorPreview).ToList(),
                ReviewQueue = dto.VirtualAnchorReviewQueue.Take(PreviewItemLimit).Select(BuildAnchorPreview).ToList(),
                LlmDiagnostics = BuildLlmDiagnosticsPreview(dto.LlmDiagnostics),
                dto.Warnings,
                FullPayloadWrittenToLog = true
            };
        }

        private static object BuildStationMapPreview(HttpResponseResultModel<AnchorDiscoveryResultDto> result)
        {
            if (result?.BackResult == null)
            {
                return new
                {
                    IsSuccess = result?.IsSuccess ?? false,
                    ErrorMessage = result?.ErrorMessage,
                    ExceptionMessage = result?.ExceptionMessage
                };
            }

            var dto = result.BackResult;
            return new
            {
                result.IsSuccess,
                result.ErrorMessage,
                result.ExceptionMessage,
                dto.ProjectId,
                dto.StationCode,
                dto.GeneratedAt,
                dto.Summary,
                StationMapCounts = new
                {
                    StationBuckets = dto.StationMap?.StationBuckets?.Count ?? 0,
                    StationConflicts = dto.StationMap?.StationConflicts?.Count ?? 0,
                    OrphanAnchors = dto.StationMap?.OrphanAnchors?.Count ?? 0,
                    VariablePropagations = dto.StationMap?.VariablePropagations?.Count ?? 0
                },
                StationBuckets = dto.StationMap?.StationBuckets?.Take(PreviewItemLimit).Select(it => new
                {
                    it.StationCode,
                    it.Confidence,
                    it.AnchorCount,
                    it.LogicUnitCount,
                    it.BlockCount,
                    VariableNames = it.VariableNames.Take(6).ToList()
                }).ToList(),
                StationConflicts = dto.StationMap?.StationConflicts?.Take(PreviewItemLimit).Select(it => new
                {
                    it.VariableName,
                    it.RecommendedStation,
                    it.RecommendedConfidence,
                    it.CandidateStations,
                    it.Reason
                }).ToList(),
                OrphanAnchors = dto.StationMap?.OrphanAnchors?.Take(PreviewItemLimit).Select(it => new
                {
                    it.AnchorKey,
                    it.VariableName,
                    it.AnchorType,
                    it.Reason
                }).ToList(),
                dto.Warnings,
                FullPayloadWrittenToLog = true
            };
        }

        private static object BuildCompilePreview(HttpResponseResultModel<AnchorDiscoveryResultDto> result)
        {
            if (result?.BackResult == null)
            {
                return new
                {
                    IsSuccess = result?.IsSuccess ?? false,
                    ErrorMessage = result?.ErrorMessage,
                    ExceptionMessage = result?.ExceptionMessage
                };
            }

            var dto = result.BackResult;
            return new
            {
                result.IsSuccess,
                result.ErrorMessage,
                result.ExceptionMessage,
                dto.ProjectId,
                dto.StationCode,
                dto.GeneratedAt,
                dto.Summary,
                Counts = new
                {
                    AstSeeds = dto.AstSeeds.Count,
                    AstCompileResults = dto.AstCompileResults.Count,
                    AstSemanticFindings = dto.AstSemanticFindings.Count,
                    StationColoring = dto.StationColoringResult.Count,
                    CorePhysical = dto.CorePhysicalAnchors.Count,
                    CoreVirtual = dto.CoreVirtualAnchors.Count,
                    Handshake = dto.HandshakeAnchors.Count
                },
                LlmDiagnostics = BuildLlmDiagnosticsPreview(dto.LlmDiagnostics),
                AstSemanticFindings = dto.AstSemanticFindings.Take(PreviewItemLimit).Select(it => new
                {
                    it.LogicUnitId,
                    it.BlockName,
                    it.StationCode,
                    it.VariableName,
                    it.Category,
                    it.SemanticLabel,
                    it.Confidence,
                    it.IoMapping,
                    RelatedIOTags = it.RelatedIOTags.Take(6).ToList(),
                    it.Reason
                }).ToList(),
                CompileResults = dto.AstCompileResults.Select(it => new
                {
                    VariableName = it.Anchor?.VariableName,
                    AnchorType = it.Anchor?.AnchorType,
                    StationCode = it.Anchor?.StationCode,
                    it.CompileStatus,
                    it.CompilerName,
                    RootNode = BuildAstNodePreview(it.RootNode, 2),
                    CoverageReportCount = it.CoverageReports?.Count ?? 0,
                    CoverageReports = it.CoverageReports?.Take(3).Select(report => (object)new
                    {
                        report.LogicUnitId,
                        report.BlockName,
                        report.NetworkId,
                        report.SourceType,
                        report.UnitType,
                        report.Title,
                        report.CompileStatus,
                        report.UnsupportedReason,
                        InputVariables = report.InputVariables.Take(6).ToList(),
                        OutputVariables = report.OutputVariables.Take(6).ToList(),
                        RawContentExcerpt = report.RawContentExcerpt,
                        report.SuggestedUpgradeCategory
                    }).ToList() ?? new List<object>(),
                    Messages = it.Messages?.Take(4).ToList() ?? new List<string>()
                }).ToList(),
                dto.Warnings,
                FullPayloadWrittenToLog = true
            };
        }

        private static object BuildAstNodePreview(HSJM.AI.AppLayer.StopCauseDiagnosis.Models.StopCauseAstNode node, int depth)
        {
            if (node == null)
            {
                return null;
            }

            return new
            {
                node.NodeType,
                node.DisplayName,
                node.VariableName,
                node.Operator,
                node.ExpectedValue,
                Metadata = node.Metadata?.Count > 0 ? node.Metadata : null,
                Children = depth <= 0
                    ? new List<object>()
                    : (node.Children ?? new List<HSJM.AI.AppLayer.StopCauseDiagnosis.Models.StopCauseAstNode>())
                        .Take(4)
                        .Select(child => BuildAstNodePreview(child, depth - 1))
                        .ToList()
            };
        }

        private static object BuildBatchStationPreview(AnchorDiscoveryResultDto dto)
        {
            return new
            {
                dto.ProjectId,
                dto.StationCode,
                dto.GeneratedAt,
                dto.Summary,
                AnchorCounts = new
                {
                    Physical = dto.PhysicalAnchors.Count,
                    CorePhysical = dto.CorePhysicalAnchors.Count,
                    Virtual = dto.VirtualAnchors.Count,
                    CoreVirtual = dto.CoreVirtualAnchors.Count,
                    Handshake = dto.HandshakeAnchors.Count,
                    Review = dto.VirtualAnchorReviewQueue.Count
                },
                dto.Warnings
            };
        }

        private static object BuildAnchorPreview(HSJM.AI.AppLayer.StopCauseDiagnosis.Models.AnchorCandidate anchor)
        {
            return new
            {
                anchor.AnchorKey,
                anchor.AnchorType,
                anchor.VariableName,
                anchor.StationCode,
                anchor.SemanticLabel,
                anchor.Confidence,
                anchor.CoreScore,
                anchor.CoreReason,
                anchor.PrimaryReason,
                anchor.LogicSourceType
            };
        }

        private static object BuildLlmDiagnosticsPreview(HSJM.AI.AppLayer.StopCauseDiagnosis.Models.LlmDiscoveryDiagnostics diagnostics)
        {
            diagnostics ??= new HSJM.AI.AppLayer.StopCauseDiagnosis.Models.LlmDiscoveryDiagnostics();
            return new
            {
                diagnostics.TotalContextCount,
                diagnostics.StationFilteredCount,
                diagnostics.SentToLlmCount,
                diagnostics.SuppressedCount,
                diagnostics.LlmReturnedCount,
                diagnostics.ConfirmedByLlmCount,
                diagnostics.HandshakeByLlmCount,
                diagnostics.ReviewByLlmCount,
                diagnostics.UnknownDroppedCount,
                diagnostics.StateFlagDroppedCount,
                SuppressedSamples = diagnostics.SuppressedSamples.Take(12).Select(it => new
                {
                    it.VariableName,
                    it.Decision,
                    it.Reason,
                    it.StationCode,
                    it.LogicSourceType
                }).ToList(),
                ClassifiedSamples = diagnostics.ClassifiedSamples.Take(16).Select(it => new
                {
                    it.VariableName,
                    it.Decision,
                    it.Category,
                    it.Reason,
                    it.StationCode,
                    it.LogicSourceType
                }).ToList()
            };
        }
    }
}
