using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.PLC_Upload;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>审查 P2/P3：重置 / 重跑任务的同项目防重、发布锁与失败说明。</summary>
public class PlcXmlV2RerunJobTests
{
    [Fact]
    public void Same_project_cannot_be_claimed_twice_until_released()
    {
        var project = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        Assert.True(PLC_XmlV2RerunJob.TryClaim(project));
        Assert.True(PLC_XmlV2RerunJob.IsQueuedOrRunning(project));
        Assert.False(PLC_XmlV2RerunJob.TryClaim(project));
        Assert.True(PLC_XmlV2RerunJob.TryClaim(project - 1)); // 别的项目不受影响
        PLC_XmlV2RerunJob.Release(project);
        PLC_XmlV2RerunJob.Release(project - 1);
        Assert.False(PLC_XmlV2RerunJob.IsQueuedOrRunning(project));
        Assert.True(PLC_XmlV2RerunJob.TryClaim(project));
        PLC_XmlV2RerunJob.Release(project);
    }

    [Fact]
    public async Task Publish_lock_serializes_the_same_project()
    {
        var project = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        var first = await PLC_XmlV2RerunJob.AcquirePublishLockAsync(project);
        var second = PLC_XmlV2RerunJob.AcquirePublishLockAsync(project);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        first.Dispose();
        first.Dispose(); // 重复释放无害
        using (await second) { }
        using (await PLC_XmlV2RerunJob.AcquirePublishLockAsync(project)) { }
    }

    [Fact]
    public async Task Try_lock_fails_fast_while_the_project_is_publishing()
    {
        // 审查复核 M-1：请求线程读保存包前先拿发布锁（拿不到立即失败），不和上传替换保存包抢文件
        var project = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        using (await PLC_XmlV2RerunJob.AcquirePublishLockAsync(project))
            Assert.Null(PLC_XmlV2RerunJob.TryAcquirePublishLock(project));
        using (var held = PLC_XmlV2RerunJob.TryAcquirePublishLock(project))
        {
            Assert.NotNull(held);
            Assert.Null(PLC_XmlV2RerunJob.TryAcquirePublishLock(project));
        }
        using (PLC_XmlV2RerunJob.TryAcquirePublishLock(project)) { }
        Assert.Contains("正在发布", PLC_XmlV2RerunJob.DescribeError(new PipelineException("ProjectPublishBusy")));
    }

    [Theory]
    [InlineData("NoSavedArchive", "没有保存的 XML 包")]
    [InlineData("RerunLogicUnitNotInArchive:FB1#2", "找不到对应程序段")]
    [InlineData("RerunScopeMissingSource:FB9(FB)", "没有程序体")]
    [InlineData("RerunFbContractUnavailable:FB_Valve", "FB 契约读取失败")]
    public void Errors_are_described_in_chinese_with_the_code(string code, string text)
    {
        var message = PLC_XmlV2RerunJob.DescribeError(new PipelineException(code));
        Assert.Contains(text, message);
        Assert.Contains(code, message);
    }
}
