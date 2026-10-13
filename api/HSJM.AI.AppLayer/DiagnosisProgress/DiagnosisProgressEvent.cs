using System;

namespace HSJM.AI.AppLayer.DiagnosisProgress;

public sealed record DiagnosisProgressMetrics
{
    public int? SignalCount { get; init; }
    public int? CompletedCount { get; init; }
    public int? TotalCount { get; init; }
    public int? RuleCount { get; init; }
    public int? IssueCount { get; init; }
    public long? ElapsedMs { get; init; }
}

public sealed record DiagnosisProgressEvent
{
    public string Operation { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string Status { get; init; } = "running";
    public float Progress { get; init; }
    public float Total { get; init; } = 100;
    public string Message { get; init; } = string.Empty;
    public DiagnosisProgressMetrics Metrics { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public bool Force { get; init; }

    public DiagnosisProgressEvent Normalize()
    {
        var total = float.IsFinite(Total) && Total > 0 ? Total : 100;
        var progress = float.IsNaN(Progress) || float.IsNegativeInfinity(Progress)
            ? 0
            : float.IsPositiveInfinity(Progress)
                ? total
                : Math.Clamp(Progress, 0, total);

        return this with
        {
            Total = total,
            Progress = progress
        };
    }

    public static DiagnosisProgressEvent Started(
        string operation, string stage, string message,
        DiagnosisProgressMetrics metrics = null) =>
        new()
        {
            Operation = operation,
            Stage = stage,
            Status = "started",
            Progress = 0,
            Message = message,
            Metrics = metrics,
            Force = true
        };

    public static DiagnosisProgressEvent Running(
        string operation, string stage, float progress, string message,
        DiagnosisProgressMetrics metrics = null, bool force = false) =>
        new()
        {
            Operation = operation,
            Stage = stage,
            Status = "running",
            Progress = progress,
            Message = message,
            Metrics = metrics,
            Force = force
        };

    public static DiagnosisProgressEvent Completed(
        string operation, string stage, string message,
        DiagnosisProgressMetrics metrics = null) =>
        new()
        {
            Operation = operation,
            Stage = stage,
            Status = "completed",
            Progress = 100,
            Message = message,
            Metrics = metrics,
            Force = true
        };

    public static DiagnosisProgressEvent Failed(
        string operation, string stage, string message,
        DiagnosisProgressMetrics metrics = null) =>
        new()
        {
            Operation = operation,
            Stage = stage,
            Status = "failed",
            Progress = 99,
            Message = message,
            Metrics = metrics,
            Force = true
        };
}
