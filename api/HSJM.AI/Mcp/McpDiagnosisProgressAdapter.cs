using System;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.DiagnosisProgress;
using ModelContextProtocol;

namespace HSJM.AI.Mcp
{
    /// <summary>
    /// Adapts normalized diagnosis progress to MCP notifications/progress.
    /// Each tool invocation owns one adapter and therefore one independent ordering state.
    /// </summary>
    public sealed class McpDiagnosisProgressAdapter : IDiagnosisProgressReporter
    {
        private readonly IProgress<ProgressNotificationValue> _progress;
        private readonly TimeSpan _minimumInterval;
        private readonly Func<ProgressNotificationValue, CancellationToken, Task>? _onReported;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private DateTime _lastReportAt = DateTime.MinValue;
        private float _lastProgress = -1;

        public McpDiagnosisProgressAdapter(
            IProgress<ProgressNotificationValue> progress,
            TimeSpan? minimumInterval = null,
            Func<ProgressNotificationValue, CancellationToken, Task>? onReported = null)
        {
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(150);
            _onReported = onReported;
        }

        public async Task ReportAsync(
            DiagnosisProgressEvent progress,
            CancellationToken cancellationToken = default)
        {
            if (progress == null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var normalized = progress.Normalize();

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (normalized.Progress < _lastProgress
                    || (normalized.Progress == _lastProgress && !normalized.Force))
                {
                    return;
                }

                var now = DateTime.UtcNow;
                if (!normalized.Force
                    && _lastProgress >= 0
                    && now - _lastReportAt < _minimumInterval)
                {
                    return;
                }

                // Commit ordering state before invoking IProgress.Report. Report implementations
                // may perform the external side effect and then throw; that must not reopen an
                // already accepted range and allow a later regression through.
                _lastProgress = normalized.Progress;
                _lastReportAt = now;

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var value = new ProgressNotificationValue
                    {
                        Progress = normalized.Progress,
                        Total = normalized.Total,
                        Message = McpProgressMessageFormatter.Format(normalized)
                    };
                    _progress.Report(value);
                    if (_onReported != null)
                    {
                        await _onReported(value, cancellationToken);
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
                    // MCP progress is best effort and must not change diagnosis results.
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
