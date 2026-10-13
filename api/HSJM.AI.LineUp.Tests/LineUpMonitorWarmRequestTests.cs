using HSJM.AI.AppLayer.Hubs;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

/// <summary>
/// 预热请求的解析（2026-09-21）：bindingId 由前端从「选中工位的绑定列表」直接带上来，
/// 所以必须当成不可信输入——过滤非法值、去重、封顶。
/// 封顶是资源保护：一个工位正常只有几个 graph，传几千个就是异常调用。
/// </summary>
public sealed class LineUpMonitorWarmRequestTests
{
    /// <summary>保留前端给的顺序（树序）：封顶截断时留下的是列表靠前的 graph，可预期。</summary>
    [Fact]
    public void Parses_and_deduplicates_keeping_the_caller_order()
    {
        Assert.Equal(new long[] { 42, 7 }, LineUpMonitorHub.ParseWarmBindingIds(["42", "7", "42"]));
    }

    [Fact]
    public void Drops_unparsable_and_non_positive_values()
    {
        Assert.Equal(new long[] { 5 }, LineUpMonitorHub.ParseWarmBindingIds(["abc", "0", "-3", "", "  ", "5", null!]));
    }

    [Fact]
    public void Tolerates_surrounding_whitespace()
    {
        Assert.Equal(new long[] { 9 }, LineUpMonitorHub.ParseWarmBindingIds([" 9 "]));
    }

    [Fact]
    public void A_null_request_clears_the_warm_set()
    {
        Assert.Empty(LineUpMonitorHub.ParseWarmBindingIds(null));
    }

    [Fact]
    public void Caps_an_oversized_request()
    {
        var ids = Enumerable.Range(1, LineUpMonitorHub.MaxWarmBindings + 50)
            .Select(i => i.ToString())
            .ToArray();
        Assert.Equal(LineUpMonitorHub.MaxWarmBindings, LineUpMonitorHub.ParseWarmBindingIds(ids).Count);
    }
}
