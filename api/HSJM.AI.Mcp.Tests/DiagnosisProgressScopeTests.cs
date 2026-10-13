using HSJM.AI.AppLayer.DiagnosisProgress;
using Xunit;

namespace HSJM.AI.Mcp.Tests;

public class DiagnosisProgressScopeTests
{
    [Fact]
    public async Task ChildScope_MapsLocalProgressIntoParentRange()
    {
        var received = new List<DiagnosisProgressEvent>();
        var root = new DelegateDiagnosisProgressReporter((progress, _) =>
        {
            received.Add(progress);
            return Task.CompletedTask;
        });
        var parent = new DiagnosisProgressScope(root, 0, 100, "line_stoppage");
        var child = parent.CreateChild(70, 82, "energy_flow");

        await child.ReportAsync(DiagnosisProgressEvent.Running(string.Empty, "constraints", 0, "Start IO constraint validation."));
        await child.ReportAsync(DiagnosisProgressEvent.Running(string.Empty, "constraints", 50, "IO constraint validation is running."));
        await child.ReportAsync(DiagnosisProgressEvent.Completed(string.Empty, "constraints", "IO constraint validation completed."));

        Assert.Equal([70f, 76f, 82f], received.Select(item => item.Progress));
        Assert.All(received, item => Assert.Equal<float?>(100f, item.Total));
        Assert.All(received, item => Assert.Equal("energy_flow", item.Operation));
    }

    [Fact]
    public async Task ConcurrentScopes_DoNotShareState()
    {
        var first = new List<DiagnosisProgressEvent>();
        var second = new List<DiagnosisProgressEvent>();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        async Task CaptureAsync(List<DiagnosisProgressEvent> received, DiagnosisProgressEvent progress)
        {
            received.Add(progress);
            if (Interlocked.Increment(ref entered) == 2)
            {
                bothEntered.TrySetResult();
            }

            await release.Task;
        }

        var scope1 = new DiagnosisProgressScope(
            new DelegateDiagnosisProgressReporter((progress, _) => CaptureAsync(first, progress)),
            0, 100, "energy_flow");
        var scope2 = new DiagnosisProgressScope(
            new DelegateDiagnosisProgressReporter((progress, _) => CaptureAsync(second, progress)),
            0, 100, "line_stoppage");

        var reports = Task.WhenAll(
            scope1.ReportAsync(DiagnosisProgressEvent.Running(string.Empty, "read", 25, "Read signals.")),
            scope2.ReportAsync(DiagnosisProgressEvent.Running(string.Empty, "stations", 60, "Diagnose stations.")));
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await reports;

        Assert.Equal([25f], first.Select(item => item.Progress));
        Assert.Equal([60f], second.Select(item => item.Progress));
        Assert.Equal("energy_flow", Assert.Single(first).Operation);
        Assert.Equal("line_stoppage", Assert.Single(second).Operation);
    }

    [Fact]
    public async Task CreateChild_ClampsOutOfRangeBounds()
    {
        var received = new List<DiagnosisProgressEvent>();
        var parent = new DiagnosisProgressScope(
            new DelegateDiagnosisProgressReporter((progress, _) =>
            {
                received.Add(progress);
                return Task.CompletedTask;
            }),
            0, 100, "line_stoppage");

        var child = parent.CreateChild(150, 200, "energy_flow");
        await child.ReportAsync(DiagnosisProgressEvent.Running(string.Empty, "constraints", 50, "Validate constraints."));

        var progress = Assert.Single(received);
        Assert.Equal(100f, progress.Progress);
        Assert.Equal("energy_flow", progress.Operation);
    }
}
