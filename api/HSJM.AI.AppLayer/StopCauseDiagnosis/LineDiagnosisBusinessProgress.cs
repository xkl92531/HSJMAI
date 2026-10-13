using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.DiagnosisProgress;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis;

/// <summary>
/// 组合停机诊断的业务进度与原有详细过程事件。
/// </summary>
public sealed class LineDiagnosisBusinessProgress
{
    private const string ResultFragmentPrefix = "结果片段：";
    private readonly DiagnosisProgressScope _lineProgress;
    private readonly Func<LineDiagnosisProgressEventDto, Task> _onEvent;
    private int _terminalReported;

    public LineDiagnosisBusinessProgress(
        DiagnosisProgressScope lineProgress,
        Func<LineDiagnosisProgressEventDto, Task> onEvent = null)
    {
        _lineProgress = lineProgress ?? throw new ArgumentNullException(nameof(lineProgress));
        _onEvent = onEvent;
    }

    public async Task EmitAsync(
        LineDiagnosisProgressEventDto evt,
        CancellationToken cancellationToken = default)
    {
        if (evt == null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await EmitDetailedAsync(evt);
        await ReportBestEffortAsync(_lineProgress, Map(evt), cancellationToken);
    }

    public IDiagnosisProgressReporter CreateEnergyFlowReporter(string taskId)
    {
        var constraintScope = _lineProgress.CreateChild(70, 82, "energy_flow");
        return new DelegateDiagnosisProgressReporter(async (evt, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReportBestEffortAsync(constraintScope, evt, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await EmitDetailedAsync(new LineDiagnosisProgressEventDto
            {
                Type = "constraint_progress",
                TaskId = taskId,
                Message = evt?.Message,
                Payload = evt,
            });
        });
    }

    public async Task<T> RunToTerminalAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<T, bool> isSuccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isSuccess);

        try
        {
            var result = await operation(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (isSuccess(result))
            {
                await CompleteAsync(cancellationToken);
            }
            else
            {
                await ErrorAsync(cancellationToken);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await ErrorAsync(CancellationToken.None);
            throw;
        }
    }

    public Task CompleteAsync(CancellationToken cancellationToken = default) =>
        ReportTerminalAsync(
            DiagnosisProgressEvent.Completed(
                "line_stoppage", "complete", "工位停机诊断完成"),
            cancellationToken);

    public Task ErrorAsync(CancellationToken cancellationToken = default) =>
        ReportTerminalAsync(
            DiagnosisProgressEvent.Failed(
                "line_stoppage", "error", "工位停机诊断失败"),
            cancellationToken);

    public static DiagnosisProgressEvent Map(LineDiagnosisProgressEventDto evt)
    {
        var type = (evt?.Type ?? string.Empty).Trim().ToLowerInvariant();
        return type switch
        {
            "start" => DiagnosisProgressEvent.Started(
                "line_stoppage", "start", "开始工位停机诊断"),
            "station_start" => DiagnosisProgressEvent.Running(
                "line_stoppage", "station", MapStation(evt, false), SafeStationStart(evt)),
            "station_done" => DiagnosisProgressEvent.Running(
                "line_stoppage", "station", MapStation(evt, true), SafeStationResult(evt),
                new DiagnosisProgressMetrics
                {
                    CompletedCount = evt.CompletedCount,
                    TotalCount = evt.TotalCount,
                }),
            "constraint_start" => DiagnosisProgressEvent.Running(
                "line_stoppage", "constraint", 70, "开始 IO 约束检查", force: true),
            "constraint_done" => DiagnosisProgressEvent.Running(
                "line_stoppage", "constraint", 82, "IO 约束检查完成", force: true),
            "constraint_error" => DiagnosisProgressEvent.Running(
                "line_stoppage", "constraint_failed", 82, "IO 约束检查未完成", force: true),
            "llm_start" => DiagnosisProgressEvent.Running(
                "line_stoppage", "model", 82, "开始原模型诊断阶段", force: true),
            "agent_step" => DiagnosisProgressEvent.Running(
                "line_stoppage", "model", 90, SafeAgentStepResult(evt)),
            "llm_done" => DiagnosisProgressEvent.Running(
                "line_stoppage", "model", 98, SafeLlmResult(evt), force: true),
            "complete" => DiagnosisProgressEvent.Completed(
                "line_stoppage", "complete", "工位停机诊断完成"),
            "error" => DiagnosisProgressEvent.Failed(
                "line_stoppage", "error", "工位停机诊断失败"),
            _ => DiagnosisProgressEvent.Running(
                "line_stoppage", "running", 5, "工位停机诊断进行中"),
        };
    }

    internal static LineDiagnosisProgressEventDto CreateLlmDoneEvent(
        string taskId,
        LineDiagnosisLlmReport report,
        IEnumerable<string> allowedStationCodes)
    {
        ArgumentNullException.ThrowIfNull(report);

        var safeStationCodes = (allowedStationCodes ?? Array.Empty<string>())
            .Where(stationCode => !string.IsNullOrWhiteSpace(stationCode))
            .Select(stationCode => stationCode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LineDiagnosisProgressEventDto
        {
            Type = "llm_done",
            TaskId = taskId,
            LlmReport = report,
            ResultFragment = new LineDiagnosisResultFragmentDto
            {
                Success = report.Success,
                RootCauseStation = report.RootCauseStation,
                AllowedStationCodes = safeStationCodes,
                Confidence = report.Confidence,
                SuggestedActionCount = report.SuggestedActions?.Count ?? 0,
            },
            Payload = new
            {
                report.Success,
                report.RootCauseStation,
                report.Confidence,
                report.ExecutionTimeMs,
                report.AgentIterations,
                RootCauseDescriptionLength = report.RootCauseDescription?.Length ?? 0,
                ReasoningLength = report.Reasoning?.Length ?? 0,
                SuggestedActionCount = report.SuggestedActions?.Count ?? 0,
                StationAnalysisCount = report.StationAnalysis?.Count ?? 0,
                SecondaryCauseCount = report.SecondaryCauses?.Count ?? 0,
            },
            Message = "AI 跨工站诊断结论已生成。",
        };
    }

    private async Task EmitDetailedAsync(LineDiagnosisProgressEventDto evt)
    {
        if (_onEvent == null || evt == null)
        {
            return;
        }

        if (evt.Timestamp == default)
        {
            evt.Timestamp = DateTime.Now;
        }

        await _onEvent(evt);
    }

    private Task ReportTerminalAsync(
        DiagnosisProgressEvent evt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Interlocked.CompareExchange(ref _terminalReported, 1, 0) == 0
            ? ReportBestEffortAsync(_lineProgress, evt, cancellationToken)
            : Task.CompletedTask;
    }

    private static async Task ReportBestEffortAsync(
        IDiagnosisProgressReporter reporter,
        DiagnosisProgressEvent evt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await reporter.ReportAsync(evt, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Progress transport is best-effort and must not change diagnosis results.
        }
    }

    private static float MapStation(LineDiagnosisProgressEventDto evt, bool includeCurrent)
    {
        var safeTotal = Math.Max(evt?.TotalCount ?? 0, 1);
        var completed = evt?.CompletedCount ?? 0;
        var effectiveCompleted = includeCurrent ? completed : Math.Max(completed - 1, 0);
        var ratio = Math.Clamp((float)effectiveCompleted / safeTotal, 0, 1);
        return 10 + 60 * ratio;
    }

    private static string SafeStationStart(LineDiagnosisProgressEventDto evt) =>
        string.IsNullOrWhiteSpace(evt?.StationCode)
            ? "开始诊断工位"
            : $"开始诊断工位 {SafeIdentifier(evt.StationCode)}";

    private static string SafeStationDone(LineDiagnosisProgressEventDto evt) =>
        string.IsNullOrWhiteSpace(evt?.StationCode)
            ? $"工位诊断进度 {evt?.CompletedCount ?? 0}/{Math.Max(evt?.TotalCount ?? 0, 1)}"
            : $"工位 {SafeIdentifier(evt.StationCode)} 诊断完成（{evt.CompletedCount}/{Math.Max(evt.TotalCount, 1)}）";

    private static string SafeStationResult(LineDiagnosisProgressEventDto evt)
    {
        var station = evt?.Station;
        if (station == null)
        {
            return SafeStationDone(evt);
        }

        return $"{ResultFragmentPrefix}工位={SafeIdentifier(station.StationCode)}, " +
               $"OverallStatus={SafeIdentifier(station.OverallStatus)}, " +
               $"DiagnosisStrategy={SafeIdentifier(station.DiagnosisStrategy)}, " +
               $"FalseAnchorCount={Math.Max(station.FalseAnchorCount, 0)}, " +
               $"KeyFindingsCount={station.KeyFindings?.Count ?? 0}";
    }

    private static string SafeAgentStepResult(LineDiagnosisProgressEventDto evt)
    {
        var fragment = evt?.ResultFragment;
        if (fragment?.Iteration is not int iteration)
        {
            return "Agent 诊断步骤完成";
        }

        return $"{ResultFragmentPrefix}Agent Iteration={Math.Max(iteration, 0)}, " +
               $"Phase={SafeEnum(fragment.Phase)}, Decision={SafeEnum(fragment.Decision)}";
    }

    private static string SafeLlmResult(LineDiagnosisProgressEventDto evt)
    {
        var fragment = evt?.ResultFragment;
        if (fragment?.Success is not bool success)
        {
            return "原模型诊断阶段完成";
        }

        var confidence = fragment.Confidence ?? 0;
        var safeConfidence = double.IsFinite(confidence) ? Math.Clamp(confidence, 0, 1) : 0;
        return $"{ResultFragmentPrefix}Success={success.ToString().ToLowerInvariant()}, " +
               $"RootCauseStation={SafeAllowedStation(fragment.RootCauseStation, fragment.AllowedStationCodes)}, " +
               $"Confidence={safeConfidence.ToString("0.###", CultureInfo.InvariantCulture)}, " +
               $"SuggestedActionCount={Math.Max(fragment.SuggestedActionCount ?? 0, 0)}";
    }

    private static string SafeAllowedStation(
        string rootCauseStation,
        IEnumerable<string> allowedStationCodes)
    {
        var candidate = rootCauseStation?.Trim();
        if (string.IsNullOrEmpty(candidate) || allowedStationCodes == null)
        {
            return "N/A";
        }

        foreach (var allowedStationCode in allowedStationCodes)
        {
            if (string.Equals(candidate, allowedStationCode?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return SafeIdentifier(allowedStationCode);
            }
        }

        return "N/A";
    }

    private static string SafeEnum<TEnum>(TEnum? value)
        where TEnum : struct, Enum =>
        value.HasValue && Enum.IsDefined(typeof(TEnum), value.Value)
            ? value.Value.ToString()
            : "N/A";

    private static string SafeIdentifier(string value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 64)
        {
            return "N/A";
        }

        foreach (var character in trimmed)
        {
            if (!char.IsLetterOrDigit(character) && character != '_' && character != '-' && character != '.')
            {
                return "N/A";
            }
        }

        return trimmed;
    }
}
