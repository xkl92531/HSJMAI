using System;
using System.IO;
using HSJM.AI.AppLayer.PLC.PLC_Upload;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 上传进度服务的终态语义（2026-10-02 审查 P1 建立）：
/// 已失败的保留第一条具体原因；已完成的不翻成失败；运行中可标失败。
/// 2026-10-09 上传约束后台化后这些语义不变，仍是外层兜底的依据。
/// </summary>
public class PlcUploadConstraintStageTests
{
    private static (PLC_UploadProgressService Progress, string TaskId) NewTask()
    {
        var progress = new PLC_UploadProgressService();
        var taskId = "t" + Guid.NewGuid().ToString("N");
        progress.CreateProgress(taskId, 7, "a.zip");
        progress.SetSavingPhase(taskId);
        return (progress, taskId);
    }

    [Fact]
    public void Later_failure_keeps_the_first_failure_message()
    {
        var (progress, taskId) = NewTask();
        progress.MarkFailed(taskId, "保存 XML 包失败，约束生成未入队: disk full");
        Assert.False(progress.TryMarkFailed(taskId, "上传处理失败: disk full"));
        Assert.Equal("保存 XML 包失败，约束生成未入队: disk full", progress.GetProgress(taskId).ErrorDetails);
    }

    [Fact]
    public void Failure_after_completion_does_not_flip_completed_state()
    {
        var (progress, taskId) = NewTask();
        progress.MarkCompleted(taskId, "7", "ok");
        Assert.False(progress.TryMarkFailed(taskId, "上传处理失败: x"));
        Assert.Equal(UploadStatus.Completed, progress.GetProgress(taskId).Status);
    }

    [Fact]
    public void Running_task_can_be_marked_failed()
    {
        var (progress, taskId) = NewTask();
        Assert.True(progress.TryMarkFailed(taskId, "上传处理失败: x"));
        Assert.Equal(UploadStatus.Failed, progress.GetProgress(taskId).Status);
    }

    [Fact]
    public void Post_process_slot_reports_terminal_status_with_detail()
    {
        // 2026-10-09 约束后台化：约束任务的进度经后处理槽呈现，终态带 FinishTime
        var (progress, taskId) = NewTask();
        progress.MarkCompleted(taskId, "7", "上传解析完成");
        progress.SetPostProcess(taskId, PLC_UploadConstraintJob.PostProcessKey, PLC_UploadConstraintJob.PostProcessTitle, "Running", "已提交后台约束生成...");
        progress.SetPostProcess(taskId, PLC_UploadConstraintJob.PostProcessKey, PLC_UploadConstraintJob.PostProcessTitle,
            "Failed", "XMLV2 约束发布失败: LLM 不可用（保存包已保留，可用「重置规则」重试）");
        var p = progress.GetProgress(taskId);
        Assert.Equal(UploadStatus.Completed, p.Status); // 上传主体不受约束任务失败影响
        var slot = Assert.Single(p.PostProcesses);
        Assert.Equal(PLC_UploadConstraintJob.PostProcessKey, slot.Key);
        Assert.Equal("Failed", slot.Status);
        Assert.NotNull(slot.FinishTime);
        Assert.Contains("重置规则", slot.Detail);
    }
}

/// <summary>审查复核 I-1：上传后台任务最外层兜底（ExecuteBackgroundUploadAsync 的 catch）走 ReportUploadFailure，
/// 不覆盖具体失败原因、不把已完成翻成失败。</summary>
public class PlcUploadOuterFailureTests
{
    private static (PLC_UploadProgressService Progress, string TaskId) NewTask()
    {
        var progress = new PLC_UploadProgressService();
        var taskId = "t" + Guid.NewGuid().ToString("N");
        progress.CreateProgress(taskId, 7, "a.zip");
        progress.SetSavingPhase(taskId);
        return (progress, taskId);
    }

    /// <summary>与 ExecuteBackgroundUploadAsync 同形：主体异常一律交给外层兜底。</summary>
    private static async Task RunLikeUploadJob(PLC_UploadProgressService progress, string taskId, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { PLC_UploadConstraintStage.ReportUploadFailure(progress, taskId, ex); }
    }

    [Fact]
    public async Task Other_failures_are_reported_with_the_root_cause()
    {
        var (progress, taskId) = NewTask();
        await RunLikeUploadJob(progress, taskId, () => throw new Exception("数据库保存失败", new TimeoutException("socket timeout")));
        var p = progress.GetProgress(taskId);
        Assert.Equal(UploadStatus.Failed, p.Status);
        Assert.Equal("上传处理失败: socket timeout", p.ErrorDetails);
    }

    [Fact]
    public void Upload_job_outer_catch_is_wired_to_ReportUploadFailure()
    {
        // 接线守卫：ExecuteBackgroundUploadAsync 的最外层 catch 必须走 ReportUploadFailure，不能再直接 MarkFailed 覆盖
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HSJM.AI.AppLayer", "PLC", "PLC_Upload", "PLC_UploadApp.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var code = File.ReadAllText(Path.Combine(dir!.FullName, "HSJM.AI.AppLayer", "PLC", "PLC_Upload", "PLC_UploadApp.cs"));
        var start = code.IndexOf("public async Task ExecuteBackgroundUploadAsync(string taskId, string zipPath, long projectId, bool fullProjectUpdate)", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = code.IndexOf("private async Task ExecuteBulkUpdateVariable", start, StringComparison.Ordinal);
        var method = code[start..end];
        Assert.Contains("PLC_UploadConstraintStage.ReportUploadFailure(progressService, taskId, ex)", method);
        Assert.DoesNotContain("MarkFailed(taskId, $\"上传处理失败", method);
    }

    [Fact]
    public void Upload_job_hands_constraint_publishing_to_the_background_job()
    {
        // 接线守卫（2026-10-09 约束后台化）：上传任务在发布锁内「保存包+meta → 入队约束任务 → MarkCompleted」，
        // 不得回退为任务内同步发布（旧 PLC_UploadConstraintStage.RunAsync 已删除）。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HSJM.AI.AppLayer", "PLC", "PLC_Upload", "PLC_UploadApp.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var code = File.ReadAllText(Path.Combine(dir!.FullName, "HSJM.AI.AppLayer", "PLC", "PLC_Upload", "PLC_UploadApp.cs"));
        var start = code.IndexOf("public async Task ExecuteBackgroundUploadAsync(string taskId, string zipPath, long projectId, bool fullProjectUpdate)", StringComparison.Ordinal);
        Assert.True(start > 0);
        var end = code.IndexOf("private async Task ExecuteBulkUpdateVariable", start, StringComparison.Ordinal);
        var method = code[start..end];
        Assert.Contains("PLC_UploadConstraintJob.Enqueue(projectId, taskId, libraryFingerprint", method);
        Assert.Contains("archiveStore.SaveWithMeta(projectId, zipPath", method);
        Assert.DoesNotContain("PLC_UploadConstraintStage.RunAsync", method);
        Assert.DoesNotContain("publisher.PublishArchiveAsync", method); // 发布只发生在后台约束任务里
    }
}

/// <summary>
/// 上传约束后台任务的版本校验判定（2026-10-09）：包与库仍是入队版本才发布；
/// 被更新的上传接管时让位；同版本已发布过时跳过。
/// </summary>
public class PlcUploadConstraintVersionTests
{
    private const string Fp = "AAAA1111BBBB2222";
    private static PlcArchiveMeta Meta(string fingerprint, string published = null) =>
        new(fingerprint, "task-a", DateTime.UtcNow, published);

    [Fact]
    public void Matching_unpublished_version_publishes()
    {
        Assert.Equal(PLC_UploadConstraintJob.ConstraintVersionDecision.Publish,
            PLC_UploadConstraintJob.Decide(Meta(Fp), Fp, Fp));
    }

    [Fact]
    public void Missing_meta_is_superseded()
    {
        // 存量保存包（2026-10-09 之前）没有 meta：按版本不明跳过，不拿错版本发布
        Assert.Equal(PLC_UploadConstraintJob.ConstraintVersionDecision.SkipSuperseded,
            PLC_UploadConstraintJob.Decide(null, Fp, Fp));
    }

    [Fact]
    public void Archive_from_a_newer_upload_is_superseded()
    {
        // 背靠背上传：B 入库后保存包 meta 已是 B 的指纹，A 的约束任务让位
        Assert.Equal(PLC_UploadConstraintJob.ConstraintVersionDecision.SkipSuperseded,
            PLC_UploadConstraintJob.Decide(Meta("CCCC3333"), Fp, "CCCC3333"));
    }

    [Fact]
    public void Library_from_a_newer_upload_is_superseded()
    {
        // meta 还没被覆盖（崩溃窗口）但库已是新版本：同样让位
        Assert.Equal(PLC_UploadConstraintJob.ConstraintVersionDecision.SkipSuperseded,
            PLC_UploadConstraintJob.Decide(Meta(Fp), Fp, "DDDD4444"));
    }

    [Fact]
    public void Already_published_version_skips_republish()
    {
        // 背靠背上传的第二个约束任务：同版本已发布过，不重复整项目发布
        Assert.Equal(PLC_UploadConstraintJob.ConstraintVersionDecision.SkipAlreadyPublished,
            PLC_UploadConstraintJob.Decide(Meta(Fp, published: Fp), Fp, Fp));
    }

    [Fact]
    public void Published_marker_of_a_different_version_still_publishes()
    {
        // 上次发布的是旧版本（PublishedFingerprint != 当前）：本次版本需要发布
        Assert.Equal(PLC_UploadConstraintJob.ConstraintVersionDecision.Publish,
            PLC_UploadConstraintJob.Decide(Meta(Fp, published: "EEEE5555"), Fp, Fp));
    }
}

/// <summary>保存包版本元数据（2026-10-09 上传约束后台执行）：SaveWithMeta / MarkPublished / Discard 的文件生命周期。</summary>
public class PlcArchiveStoreMetaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plc-archive-meta-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PlcArchiveStore _store;
    private readonly string _sourceZip;

    public PlcArchiveStoreMetaTests()
    {
        Directory.CreateDirectory(_root);
        _store = new PlcArchiveStore(_root);
        _sourceZip = Path.Combine(_root, "source.zip");
        File.WriteAllText(_sourceZip, "zip-content");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SaveWithMeta_round_trips_the_meta()
    {
        var meta = new PlcArchiveMeta("FP1", "task-1", new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        _store.SaveWithMeta(7, _sourceZip, meta);
        Assert.True(_store.Exists(7));
        Assert.Equal("FP1", _store.ReadMeta(7).Fingerprint);
        Assert.Equal("task-1", _store.ReadMeta(7).TaskId);
        Assert.Null(_store.ReadMeta(7).PublishedFingerprint);
    }

    [Fact]
    public void ReadMeta_returns_null_when_missing()
    {
        Assert.Null(_store.ReadMeta(7));
    }

    [Fact]
    public void ReadMeta_returns_null_for_corrupt_meta()
    {
        File.WriteAllText(_store.PathFor(7), "zip");
        File.WriteAllText(_store.MetaPathFor(7), "{ not json");
        Assert.Null(_store.ReadMeta(7));
    }

    [Fact]
    public void MarkPublished_records_the_published_fingerprint()
    {
        _store.SaveWithMeta(7, _sourceZip, new PlcArchiveMeta("FP1", "task-1", DateTime.UtcNow));
        _store.MarkPublished(7, "FP1", "task-2");
        var meta = _store.ReadMeta(7);
        Assert.Equal("FP1", meta.PublishedFingerprint);
        Assert.NotNull(meta.PublishedAtUtc);
        Assert.Equal("task-1", meta.TaskId); // 保存时的信息保留
    }

    [Fact]
    public void MarkPublished_rebuilds_a_minimal_meta_when_missing()
    {
        File.WriteAllText(_store.PathFor(7), "zip");
        _store.MarkPublished(7, "FP1");
        Assert.Equal("FP1", _store.ReadMeta(7).Fingerprint);
        Assert.Equal("FP1", _store.ReadMeta(7).PublishedFingerprint);
    }

    [Fact]
    public void Discard_removes_both_zip_and_meta()
    {
        _store.SaveWithMeta(7, _sourceZip, new PlcArchiveMeta("FP1", "task-1", DateTime.UtcNow));
        Assert.True(_store.Discard(7));
        Assert.False(_store.Exists(7));
        Assert.Null(_store.ReadMeta(7));
        Assert.False(File.Exists(_store.MetaPathFor(7)));
    }

    [Fact]
    public void SaveWithMeta_replaces_a_previous_archive_atomically()
    {
        _store.SaveWithMeta(7, _sourceZip, new PlcArchiveMeta("FP1", "task-1", DateTime.UtcNow));
        File.WriteAllText(_sourceZip, "zip-content-v2");
        _store.SaveWithMeta(7, _sourceZip, new PlcArchiveMeta("FP2", "task-2", DateTime.UtcNow));
        Assert.Equal("FP2", _store.ReadMeta(7).Fingerprint);
        Assert.Equal("zip-content-v2", File.ReadAllText(_store.PathFor(7)));
        // 没有残留临时文件
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
