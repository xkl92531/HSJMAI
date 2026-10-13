using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 2026-09-22 IGW 契约变更：/Tag 六个读接口从下个版本起**默认不再开启轮询**，
/// 需要保持轮询的调用方显式加 ?activate=true。
///
/// 我们两类读诉求相反，且**共用同一条读链**（ReadExactViaIgwAsync）：
///   - 约束诊断 / 停机诊断快照：读一次就走 → 默认（不带参数）
///   - LineUp 设备监控（工步监控）：100ms 持续盯同一批点 → 必须 activate=true，
///     否则退化成每次现场补读 PLC，值对但慢，现场容易误判成别的问题
/// </summary>
public sealed class IgwReadPathTests
{
    [Fact]
    public void Snapshot_reads_use_the_bare_path()
    {
        Assert.Equal("Tag/ReadTagsAsList", IgwReadPath.TagsAsList(keepPolling: false));
    }

    [Fact]
    public void Polling_reads_ask_to_stay_activated()
    {
        Assert.Equal("Tag/ReadTagsAsList?activate=true", IgwReadPath.TagsAsList(keepPolling: true));
    }

    /// <summary>默认不激活——大多数调用方是"读一次看看"，让它们不必显式关掉副作用。</summary>
    [Fact]
    public void The_default_is_snapshot()
    {
        Assert.Equal(IgwReadPath.TagsAsList(false), IgwReadPath.TagsAsList());
    }
}
