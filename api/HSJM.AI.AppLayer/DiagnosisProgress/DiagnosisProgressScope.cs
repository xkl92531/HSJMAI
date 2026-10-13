using System;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosisProgress;

public sealed class DiagnosisProgressScope : IDiagnosisProgressReporter
{
    private readonly IDiagnosisProgressReporter _target;
    private readonly float _start;
    private readonly float _end;
    private readonly string _operation;

    public DiagnosisProgressScope(IDiagnosisProgressReporter target, float start, float end, string operation)
    {
        _target = target ?? NullDiagnosisProgressReporter.Instance;
        _start = Math.Clamp(start, 0, 100);
        _end = Math.Clamp(end, _start, 100);
        _operation = operation ?? string.Empty;
    }

    public Task ReportAsync(DiagnosisProgressEvent progress, CancellationToken cancellationToken = default)
    {
        var local = progress.Normalize();
        var ratio = local.Total <= 0 ? 0d : (double)local.Progress / local.Total;

        return _target.ReportAsync(local with
        {
            Operation = string.IsNullOrWhiteSpace(local.Operation) ? _operation : local.Operation,
            Progress = (float)(_start + (_end - _start) * ratio),
            Total = 100
        }, cancellationToken);
    }

    public DiagnosisProgressScope CreateChild(float start, float end, string operation)
    {
        var localStart = Math.Clamp(start, 0, 100);
        var localEnd = Math.Clamp(end, localStart, 100);
        var childStart = _start + (_end - _start) * localStart / 100;
        var childEnd = _start + (_end - _start) * localEnd / 100;

        return new DiagnosisProgressScope(_target, childStart, childEnd, operation);
    }
}
