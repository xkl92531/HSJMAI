using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 2026-09-22 上传自动跑全量分析：上传是高频动作，而带 LLM 的全量分析很贵，
/// 必须能问出「这个项目现在是不是已经在跑」，否则连传几次就堆起一摞重复任务。
/// 进度存在静态字典里、按 taskId 存，所以判定要按 ProjectId 反查 Running 条目。
/// </summary>
public class FullAnalysisProgressServiceTests
{
    /// <summary>每个用例用互不相同的 projectId，避免静态字典在用例间串味。</summary>
    private static long NextProjectId() => System.DateTime.Now.Ticks;

    [Fact]
    public void Unknown_project_is_not_running()
    {
        var svc = new FullAnalysisProgressService();
        Assert.False(svc.IsRunning(NextProjectId()));
    }

    [Fact]
    public void A_started_task_makes_its_project_running()
    {
        var svc = new FullAnalysisProgressService();
        var projectId = NextProjectId();

        svc.Start($"t_{projectId}", projectId, stationCode: null);

        Assert.True(svc.IsRunning(projectId));
    }

    [Fact]
    public void Another_project_is_unaffected()
    {
        var svc = new FullAnalysisProgressService();
        var running = NextProjectId();
        var other = running + 1;

        svc.Start($"t_{running}", running, null);

        Assert.True(svc.IsRunning(running));
        Assert.False(svc.IsRunning(other));
    }

    [Fact]
    public void Completing_the_task_releases_the_project()
    {
        var svc = new FullAnalysisProgressService();
        var projectId = NextProjectId();
        var taskId = $"t_{projectId}";

        svc.Start(taskId, projectId, null);
        svc.Complete(taskId, new FullAnalysisSummaryDto());

        Assert.False(svc.IsRunning(projectId));
    }

    /// <summary>失败也必须放行，否则一次失败就永远挡住后续上传的自动分析。</summary>
    [Fact]
    public void Failing_the_task_releases_the_project()
    {
        var svc = new FullAnalysisProgressService();
        var projectId = NextProjectId();
        var taskId = $"t_{projectId}";

        svc.Start(taskId, projectId, null);
        svc.Fail(taskId, 3, "AST 编译", "boom");

        Assert.False(svc.IsRunning(projectId));
    }

    /// <summary>同一项目跑过一轮又起一轮：旧的已结束，新的在跑，应判为在跑。</summary>
    [Fact]
    public void A_new_run_after_a_finished_one_counts_as_running()
    {
        var svc = new FullAnalysisProgressService();
        var projectId = NextProjectId();

        svc.Start($"t1_{projectId}", projectId, null);
        svc.Complete($"t1_{projectId}", new FullAnalysisSummaryDto());
        svc.Start($"t2_{projectId}", projectId, null);

        Assert.True(svc.IsRunning(projectId));
    }
}
