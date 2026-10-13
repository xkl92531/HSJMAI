using HSJM.AI.AppLayer.LineUp.Runtime;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

/// <summary>
/// 2026-09-22 工步监控实时性：轮询间隔决定「IGW 侧值已经变了，我们多久才去取」。
///
/// 工步执行位（{db}.RT_DATA.EXEC_BITS.*、{db}.S_NO）注册在 GRAPH 设备上，ScanRateMs=100——
/// IGW 每 100ms 就刷新一次，而我们原来 500ms 才去取一次，延迟的大头其实在我们这边。
/// 对齐到 100ms 后端到端延迟从「最差 600ms」降到「最差 200ms」。
///
/// 下限 50ms 是留给「GraphScanRateMs 也调下去」的将来；在那之前设到 50 只会让一半请求
/// 拿到完全相同的缓存值，白白翻倍 HTTP 压力——所以默认值取 100，与 GraphScanRateMs 对齐。
/// </summary>
public sealed class LineUpMonitorPollIntervalTests
{
    [Fact]
    public void Missing_config_falls_back_to_the_graph_scan_rate()
    {
        Assert.Equal(100, LineUpMonitorPollInterval.Resolve(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12.5")]
    public void Unparsable_config_falls_back_too(string raw)
    {
        Assert.Equal(100, LineUpMonitorPollInterval.Resolve(raw));
    }

    [Fact]
    public void A_sane_value_is_used_as_is()
    {
        Assert.Equal(250, LineUpMonitorPollInterval.Resolve("250"));
    }

    /// <summary>留出 50ms 的余地：将来 GraphScanRateMs 降下去时不用再改代码。</summary>
    [Fact]
    public void Fifty_is_allowed()
    {
        Assert.Equal(50, LineUpMonitorPollInterval.Resolve("50"));
    }

    [Theory]
    [InlineData("0", 50)]
    [InlineData("-1", 50)]
    [InlineData("10", 50)]
    [InlineData("99999", 5000)]
    public void Out_of_range_values_are_clamped(string raw, int expected)
    {
        Assert.Equal(expected, LineUpMonitorPollInterval.Resolve(raw));
    }

    [Fact]
    public void Surrounding_space_does_not_break_it()
    {
        Assert.Equal(200, LineUpMonitorPollInterval.Resolve("  200  "));
    }
}
