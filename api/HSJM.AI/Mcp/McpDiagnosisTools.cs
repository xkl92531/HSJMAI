using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HSJM.AI.Mcp
{
    [McpServerToolType]
    public sealed class McpDiagnosisTools
    {
        private static readonly McpInvocationLogger NoopInvocationLogger =
            new McpInvocationLogger(new NoopMcpInvocationLogSink());

        private readonly Func<EnergyFlowDiagnosticRequestDto, IDiagnosisProgressReporter, CancellationToken,
            Task<HttpResponseResultModel<string>>> _diagnoseEnergyFlowAsync;
        private readonly Func<LineDiagnosisRequestDto, string, Func<LineDiagnosisProgressEventDto, Task>,
            CancellationToken, Task<HttpResponseResultModel<LineDiagnosisResultDto>>>
            _diagnoseLineStoppageAsync;
        private readonly McpInvocationLogger _invocationLogger;

        public McpDiagnosisTools(
            DiagnosticStrategyFactoryApp diagnosticStrategyFactoryApp,
            StopCauseDiagnosisApp stopCauseDiagnosisApp,
            McpInvocationLogger invocationLogger)
        {
            if (diagnosticStrategyFactoryApp == null)
            {
                throw new ArgumentNullException(nameof(diagnosticStrategyFactoryApp));
            }

            _diagnoseEnergyFlowAsync = diagnosticStrategyFactoryApp.DiagnoseEnergyFlowAsync;
            if (stopCauseDiagnosisApp == null)
            {
                throw new ArgumentNullException(nameof(stopCauseDiagnosisApp));
            }

            _diagnoseLineStoppageAsync = (request, taskId, onEvent, cancellationToken) =>
                stopCauseDiagnosisApp.DiagnoseLineStoppageForMcpAsync(
                    request,
                    taskId,
                    onEvent,
                    cancellationToken);
            _invocationLogger = invocationLogger
                ?? throw new ArgumentNullException(nameof(invocationLogger));
        }

        internal McpDiagnosisTools(
            Func<EnergyFlowDiagnosticRequestDto, IDiagnosisProgressReporter, CancellationToken,
                Task<HttpResponseResultModel<string>>> diagnoseEnergyFlowAsync)
        {
            _diagnoseEnergyFlowAsync = diagnoseEnergyFlowAsync
                ?? throw new ArgumentNullException(nameof(diagnoseEnergyFlowAsync));
            _invocationLogger = NoopInvocationLogger;
        }

        internal McpDiagnosisTools(
            Func<LineDiagnosisRequestDto, string, Func<LineDiagnosisProgressEventDto, Task>,
                CancellationToken, Task<HttpResponseResultModel<LineDiagnosisResultDto>>>
                diagnoseLineStoppageAsync)
        {
            _diagnoseLineStoppageAsync = diagnoseLineStoppageAsync
                ?? throw new ArgumentNullException(nameof(diagnoseLineStoppageAsync));
            _invocationLogger = NoopInvocationLogger;
        }

        [McpServerTool(
            Name = "diagnose_energy_flow",
            Title = "产线 IO 约束异常检测",
            UseStructuredContent = true,
            OutputSchemaType = typeof(HttpResponseResultModel<string>))]
        [Description("执行原产线 EnergyFlow/IO 约束诊断流程，并通过 MCP Progress 报告阶段状态与安全结果片段；最终结果保持原业务响应结构。")]
        public async Task<HttpResponseResultModel<string>> DiagnoseEnergyFlowAsync(
            [Description("产线 ID，默认 1。")]
            long productionLineId = 1,
            [Description("用户对异常现象的描述，按原诊断流程使用。")]
            string userDescription = null,
            [Description("是否按原流程强制深度分析。")]
            bool forceDeepAnalysis = false,
            [Description("原业务诊断 ID；它与 MCP progressToken 相互独立。")]
            long diagnosticId = 0,
            IProgress<ProgressNotificationValue> progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new EnergyFlowDiagnosticRequestDto
            {
                ProductionLineId = productionLineId,
                UserDescription = userDescription,
                ForceDeepAnalysis = forceDeepAnalysis,
                DiagnosticId = diagnosticId,
            };
            var invocation = await _invocationLogger.StartAsync(
                "diagnose_energy_flow",
                request);
            var adapter = CreateAdapter(progress, invocation);

            try
            {
                var result = await _diagnoseEnergyFlowAsync(
                    request,
                    adapter,
                    cancellationToken);
                await invocation.CompleteAsync(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                await invocation.CancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                await invocation.FailAsync(ex);
                throw;
            }
        }

        [McpServerTool(
            Name = "diagnose_line_stoppage",
            Title = "工位停机诊断",
            UseStructuredContent = true,
            OutputSchemaType = typeof(HttpResponseResultModel<LineDiagnosisResultDto>))]
        [Description("执行与 /linediagnosis2 页面相同的产线停机诊断；每个页面过程事件通过 MCP Progress 以 camelCase JSON 实时返回，最终 backResult 直接返回页面原始 LineDiagnosisResultDto。")]
        public async Task<HttpResponseResultModel<LineDiagnosisResultDto>> DiagnoseLineStoppageAsync(
            [Description("可选单个 PLC Project ID。")]
            long? projectId = null,
            [Description("可选 PLC Project ID 列表。")]
            List<long> projectIds = null,
            [Description("可选单个工位编码。")]
            string stationCode = null,
            [Description("可选工位编码列表。")]
            List<string> stationCodes = null,
            [Description("最大并行工位数，默认 3。")]
            int maxParallelStations = 3,
            [Description("每个工位最多求值的锚点数，默认 500。")]
            int maxAnchorsPerStation = 500,
            [Description("原请求的跨工位模型分析开关。")]
            bool useLlmAnalysis = true,
            [Description("输出语言，默认 zh-CN。")]
            string outputLanguage = "zh-CN",
            [Description("无异常时是否强制生成原模型总结；默认与页面一致为 true。")]
            bool forceLlmSummaryWhenNoAbnormal = true,
            [Description("原请求的 Agent 诊断开关。")]
            bool useAgentDiagnosis = false,
            [Description("Agent 模式最大迭代次数，默认 12。")]
            int agentMaxIterations = 12,
            [Description("是否启用工位级模型诊断。")]
            bool enablePerStationLlm = false,
            [Description("原业务超时秒数；0 表示沿用原默认行为。")]
            int timeoutSeconds = 0,
            [Description("可选任务 ID；为空时原业务自动生成。")]
            string taskId = null,
            [Description("是否只诊断 GRAPH 工位。")]
            bool graphOnlyMode = true,
            [Description("是否检查设备关注信号。")]
            bool enableDeviceFocusCheck = false,
            [Description("设备关注信号类型列表。")]
            List<string> deviceFocusAnchorTypes = null,
            [Description("GRAPH 步骤激活宽限秒数，默认 8。")]
            int activeGraphGraceSeconds = 8,
            [Description("是否附带 IO 约束分析；默认与页面一致为 true。")]
            bool includeConstraintAnalysis = true,
            [Description("FB 穿透追踪最大深度，默认 4。")]
            int maxFbPenetrationDepth = 4,
            IProgress<ProgressNotificationValue> progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new LineDiagnosisRequestDto
            {
                ProjectIds = MergeProjectIds(projectId, projectIds),
                StationCodes = MergeStationCodes(stationCode, stationCodes),
                MaxParallelStations = maxParallelStations,
                MaxAnchorsPerStation = maxAnchorsPerStation,
                UseLlmAnalysis = useLlmAnalysis,
                OutputLanguage = outputLanguage,
                ForceLlmSummaryWhenNoAbnormal = forceLlmSummaryWhenNoAbnormal,
                UseAgentDiagnosis = useAgentDiagnosis,
                AgentMaxIterations = agentMaxIterations,
                EnablePerStationLlm = enablePerStationLlm,
                TimeoutSeconds = timeoutSeconds,
                TaskId = taskId,
                GraphOnlyMode = graphOnlyMode,
                EnableDeviceFocusCheck = enableDeviceFocusCheck,
                DeviceFocusAnchorTypes = deviceFocusAnchorTypes ?? new List<string>(),
                ActiveGraphGraceSeconds = activeGraphGraceSeconds,
                IncludeConstraintAnalysis = includeConstraintAnalysis,
                MaxFbPenetrationDepth = maxFbPenetrationDepth,
            };
            var invocation = await _invocationLogger.StartAsync(
                "diagnose_line_stoppage",
                request);
            var stream = new McpLineDiagnosisProgressStream(
                progress ?? new Progress<ProgressNotificationValue>(),
                (value, _) => invocation.ReportProgressAsync(value));

            try
            {
                var result = await _diagnoseLineStoppageAsync(
                    request,
                    taskId,
                    evt => stream.ReportEventAsync(evt, cancellationToken),
                    cancellationToken);
                if (result?.IsSuccess == true)
                {
                    await stream.CompleteAsync(
                        request.TaskId ?? taskId,
                        result.BackResult,
                        cancellationToken);
                }
                else
                {
                    await stream.ErrorAsync(
                        request.TaskId ?? taskId,
                        result?.ErrorMessage ?? "Line diagnosis failed.",
                        result?.BackResult,
                        cancellationToken);
                }
                await invocation.CompleteAsync(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                await invocation.CancelAsync();
                throw;
            }
            catch (Exception ex)
            {
                await invocation.FailAsync(ex);
                throw;
            }
        }

        private static List<long> MergeProjectIds(long? projectId, IEnumerable<long> projectIds)
        {
            var merged = new List<long>();
            if (projectId.GetValueOrDefault() > 0)
            {
                merged.Add(projectId.Value);
            }

            if (projectIds != null)
            {
                merged.AddRange(projectIds.Where(id => id > 0));
            }

            return merged.Distinct().ToList();
        }

        private static List<string> MergeStationCodes(string stationCode, IEnumerable<string> stationCodes)
        {
            var merged = new List<string>();
            if (!string.IsNullOrWhiteSpace(stationCode))
            {
                merged.Add(stationCode.Trim());
            }

            if (stationCodes != null)
            {
                merged.AddRange(stationCodes
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim()));
            }

            return merged.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static McpDiagnosisProgressAdapter CreateAdapter(
            IProgress<ProgressNotificationValue> progress,
            McpInvocationScope invocation)
        {
            return new McpDiagnosisProgressAdapter(
                progress ?? new Progress<ProgressNotificationValue>(),
                onReported: (value, _) => invocation.ReportProgressAsync(value));
        }

        private sealed class NoopMcpInvocationLogSink : IMcpInvocationLogSink
        {
            public Task WriteAsync(string line) => Task.CompletedTask;
        }
    }
}
