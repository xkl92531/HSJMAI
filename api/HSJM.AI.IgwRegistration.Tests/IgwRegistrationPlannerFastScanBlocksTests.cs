using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 2026-09-22 工步监控实时性：工步状态住在 GRAPH FB 的实例 DB 里，那些点要 100ms 级；
/// 其余 DB 点保持 1000ms。IGW 的 ScanRateMs 是按点位存的（TagDef.ScanRateMs），
/// 扫描计划按各点自己的周期分组，所以同一台设备上两种频率并存是支持的。
///
/// 为什么不整体把 DbScanRateMs 调到 100：现场实测同一台 S7-1500 上 S7 Plus 一个请求
/// 93 点必断连、46~56 点才稳；几百个符号点拆成 5~10 个请求还要在 100ms 周期内跑完，
/// 会频繁触发自适应窗口收窄——那正是刚给现场止住的血。按块设只让工步块那几十个点加速。
/// </summary>
public sealed class IgwRegistrationPlannerFastScanBlocksTests
{
    private static readonly IgwRegistrationConfig Config = new();

    private static PlannerDbRow Row(string block, string path) =>
        new(block, 100, path, "Bool", 0m, 0m, null);

    private static PlannerInput Input(IReadOnlyCollection<string> fastBlocks) =>
        new("线_PLC1", "192.0.2.10", 102,
            new[]
            {
                Row("DB117_AxisCombo", "Step1.X"),
                Row("DB900_Data", "Flag"),
            },
            System.Array.Empty<PlannerIoTag>(),
            System.Array.Empty<string>(), System.Array.Empty<string>(),
            IoRefreshed: false, Full: true, Config,
            FastScanBlocks: fastBlocks);

    private static IReadOnlyDictionary<string, int> ScanRateByPointName(PlannerInput input) =>
        IgwRegistrationPlanner.Plan(input).Requests
            .SelectMany(r => r.Request.Items)
            .ToDictionary(i => i.Name, i => i.ScanRateMs);

    [Fact]
    public void Listed_blocks_get_the_fast_rate_and_the_others_keep_the_normal_one()
    {
        var rates = ScanRateByPointName(Input(new[] { "DB117_AxisCombo" }));

        Assert.Equal(Config.GraphDbScanRateMs, rates["X"]);
        Assert.Equal(Config.DbScanRateMs, rates["Flag"]);
    }

    /// <summary>没传快扫块时行为必须与改动前完全一致，免得波及没有 GRAPH 的项目。</summary>
    [Fact]
    public void Without_a_list_every_db_point_keeps_the_normal_rate()
    {
        var rates = ScanRateByPointName(Input(null));

        Assert.All(rates.Values, rate => Assert.Equal(Config.DbScanRateMs, rate));
    }

    [Fact]
    public void An_empty_list_behaves_like_no_list()
    {
        var rates = ScanRateByPointName(Input(System.Array.Empty<string>()));

        Assert.All(rates.Values, rate => Assert.Equal(Config.DbScanRateMs, rate));
    }

    /// <summary>块名来源与注册侧其它地方一致，大小写不该决定采集快慢。</summary>
    [Fact]
    public void Block_matching_ignores_case()
    {
        var rates = ScanRateByPointName(Input(new[] { "db117_axiscombo" }));

        Assert.Equal(Config.GraphDbScanRateMs, rates["X"]);
    }

    [Fact]
    public void Fast_rate_defaults_to_the_graph_scan_rate()
    {
        Assert.Equal(100, new IgwRegistrationConfig().GraphDbScanRateMs);
    }
}
