using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services;

internal static class LineDiagnosisObservationRecheck
{
    internal static async Task<IReadOnlyList<StationDiagnosisSummaryDto>> RefreshAsync(
        IReadOnlyList<StationDiagnosisSummaryDto> stations, int parallelism,
        Func<StationDiagnosisSummaryDto, CancellationToken, Task<StationDiagnosisSummaryDto>> refresh,
        CancellationToken cancellationToken, Action<StationDiagnosisSummaryDto> onUpdated = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var station in stations)
            onUpdated?.Invoke(new StationDiagnosisSummaryDto { ProjectId = station.ProjectId, StationCode = station.StationCode,
                StationOrder = station.StationOrder, OverallStatus = "Pending", DiagnosisStrategy = "ObservationRecheckPending",
                ErrorMessage = "观察后复核尚未完成，首轮等待条件不作为当前证据", KeyFindings = new() });
        // This is a new observation frame. Reusing the initial 15-second cache would make waiting pointless.
        using var frame = StopCauseSignalReadScope.Begin();
        using var slots = new SemaphoreSlim(Math.Clamp(parallelism, 1, 6));
        return await Task.WhenAll(stations.Select(async station =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var current = await refresh(station, cancellationToken);
                if (current == null || current.ProjectId != station.ProjectId
                    || !string.Equals(current.StationCode, station.StationCode, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Recheck station identity mismatch");
                current.StationOrder = station.StationOrder;
                current.DiagnosisStrategy += "+ObservedRecheck";
                if (station.OverallStatus == "Running" && current.OverallStatus is "SomeFalse" or "Running")
                {
                    current.OverallStatus = "Running";
                    current.DiagnosisStrategy += "+ActivityPreserved";
                }
                onUpdated?.Invoke(current);
                return current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // An old False/wait edge is not current evidence when the refresh fails.
                var unavailable = new StationDiagnosisSummaryDto { ProjectId = station.ProjectId, StationCode = station.StationCode,
                    StationOrder = station.StationOrder, OverallStatus = "Error", DiagnosisStrategy = "ObservationRecheckFailed",
                    ErrorMessage = "观察后复核失败，不能沿用首轮信号判断：" + ex.GetType().Name, KeyFindings = new() };
                onUpdated?.Invoke(unavailable);
                return unavailable;
            }
            finally { slots.Release(); }
        }));
    }
}
