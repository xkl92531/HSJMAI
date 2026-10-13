using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.AppLayer.StopCauseDiagnosis;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using ModelContextProtocol;

namespace HSJM.AI.Mcp
{
    /// <summary>
    /// 将产线诊断页面使用的详细过程事件无损投影为 MCP Progress。
    /// 每个业务事件对应一条 Progress，消息正文为 camelCase JSON。
    /// </summary>
    public sealed class McpLineDiagnosisProgressStream
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            WriteIndented = false,
        };

        private readonly IProgress<ProgressNotificationValue> _progress;
        private readonly Func<ProgressNotificationValue, CancellationToken, Task>? _onReported;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private float _lastProgress;

        public McpLineDiagnosisProgressStream(
            IProgress<ProgressNotificationValue> progress,
            Func<ProgressNotificationValue, CancellationToken, Task>? onReported = null)
        {
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _onReported = onReported;
        }

        public Task ReportEventAsync(
            LineDiagnosisProgressEventDto evt,
            CancellationToken cancellationToken = default)
        {
            if (evt == null)
            {
                return Task.CompletedTask;
            }

            return ReportCoreAsync(evt, ResolveProgress(evt), cancellationToken);
        }

        public Task CompleteAsync(
            string taskId,
            LineDiagnosisResultDto result,
            CancellationToken cancellationToken = default) =>
            ReportCoreAsync(new LineDiagnosisProgressEventDto
            {
                Type = "complete",
                TaskId = taskId,
                Message = "Line diagnosis completed.",
                CompletedCount = result?.DiagnosedStations ?? 0,
                TotalCount = result?.TotalStations ?? 0,
                StationCodes = result?.StationSummaries?
                    .ConvertAll(summary => summary.StationCode),
                Payload = result,
            }, 100, cancellationToken);

        public Task ErrorAsync(
            string taskId,
            string errorMessage,
            LineDiagnosisResultDto partialResult,
            CancellationToken cancellationToken = default) =>
            ReportCoreAsync(new LineDiagnosisProgressEventDto
            {
                Type = "error",
                TaskId = taskId,
                Message = errorMessage,
                CompletedCount = partialResult?.DiagnosedStations ?? 0,
                TotalCount = partialResult?.TotalStations ?? 0,
                Payload = partialResult,
            }, 100, cancellationToken);

        private async Task ReportCoreAsync(
            LineDiagnosisProgressEventDto evt,
            float requestedProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (evt.Timestamp == default)
                {
                    evt.Timestamp = DateTime.Now;
                }

                var normalizedProgress = float.IsFinite(requestedProgress)
                    ? Math.Clamp(requestedProgress, 0, 100)
                    : _lastProgress;
                normalizedProgress = Math.Max(_lastProgress, normalizedProgress);
                _lastProgress = normalizedProgress;

                var value = new ProgressNotificationValue
                {
                    Progress = normalizedProgress,
                    Total = 100,
                    Message = JsonSerializer.Serialize(evt, JsonOptions),
                };

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _progress.Report(value);
                    if (_onReported != null)
                    {
                        await _onReported(value, cancellationToken).ConfigureAwait(false);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException exception)
                    when (cancellationToken.IsCancellationRequested
                          || exception.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Progress transport/logging is best effort and must not change diagnosis results.
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private static float ResolveProgress(LineDiagnosisProgressEventDto evt)
        {
            if (string.Equals(evt.Type, "constraint_progress", StringComparison.OrdinalIgnoreCase)
                && evt.Payload is DiagnosisProgressEvent nested)
            {
                var normalized = nested.Normalize();
                return 70 + (12 * normalized.Progress / Math.Max(normalized.Total, 1));
            }

            return LineDiagnosisBusinessProgress.Map(evt).Normalize().Progress;
        }
    }
}
