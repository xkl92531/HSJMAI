using System;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosisProgress;

public interface IDiagnosisProgressReporter
{
    Task ReportAsync(DiagnosisProgressEvent progress, CancellationToken cancellationToken = default);
}

public sealed class NullDiagnosisProgressReporter : IDiagnosisProgressReporter
{
    public static NullDiagnosisProgressReporter Instance { get; } = new();

    private NullDiagnosisProgressReporter()
    {
    }

    public Task ReportAsync(DiagnosisProgressEvent progress, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class DelegateDiagnosisProgressReporter : IDiagnosisProgressReporter
{
    private readonly Func<DiagnosisProgressEvent, CancellationToken, Task> _callback;

    public DelegateDiagnosisProgressReporter(
        Func<DiagnosisProgressEvent, CancellationToken, Task> callback) =>
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public Task ReportAsync(DiagnosisProgressEvent progress, CancellationToken cancellationToken = default) =>
        _callback(progress, cancellationToken);
}
