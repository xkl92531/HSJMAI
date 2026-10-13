using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 2026-09-22 只追加注册（用户拍板）：XML 上传解析这条链路**只负责新增**。
///   - 根组内容指纹一致 → **整根跳过**，一个点都不发
///   - 不一致 → 发该根 items 走 upsert 更新
///   - **任何情况都不发 replaceGroups**：PLC 里删掉的东西留在 IGW 上不管它
///
/// 动机：现场实测一次全量注册 29 万点的设备要 519 秒，基本都耗在「整组删光再重写」上。
/// 上传是高频动作，不该每次都付这个代价。
///
/// ⚠️ 台账里没有指纹（新加的列，存量行全是 null）必须当作「**必须重发**」。
/// 反过来当成"一致"的话，整个功能从第一天起就是死的，而且不报错——
/// 这也顺带让上线后的第一次注册天然是一次全量重发，今天加的工步块 100ms 扫描率会在那一次落地。
/// </summary>
public sealed class IgwRegistrationPlannerAppendOnlyTests
{
    private static readonly IgwRegistrationConfig Config = new();

    private static PlannerDbRow Row(string block, string path) =>
        new(block, 100, path, "Bool", 0m, 0m, null);

    private static PlannerInput Input(
        IReadOnlyDictionary<string, PlannerRootState> states,
        bool appendOnly = true) =>
        new("线_PLC1", "192.0.2.10", 102,
            new[] { Row("DB1", "A"), Row("DB1", "B") },
            Array.Empty<PlannerIoTag>(),
            Array.Empty<string>(), Array.Empty<string>(),
            IoRefreshed: false, Full: true, Config,
            AppendOnly: appendOnly, RootStates: states);

    private static PlanResult Plan(PlannerInput input) => IgwRegistrationPlanner.Plan(input);

    private static string[] Points(PlanResult plan) =>
        plan.Requests.SelectMany(r => r.Request.Items).Select(i => i.Name).OrderBy(n => n).ToArray();

    private static int ReplaceGroups(PlanResult plan) =>
        plan.Requests.Sum(r => r.Request.ReplaceGroups?.Count ?? 0);

    /// <summary>规划器为每个根算出的指纹要能拿到——runner 成功后要把它写回台账。</summary>
    private static string PlannedFingerprint(PlanResult plan, string block) =>
        plan.Blocks.Single(b => b.BlockName == block).Fingerprint;

    [Fact]
    public void Append_only_never_asks_the_gateway_to_delete_anything()
    {
        Assert.Equal(0, ReplaceGroups(Plan(Input(states: null))));
    }

    [Fact]
    public void Without_a_recorded_fingerprint_everything_is_sent()
    {
        Assert.Equal(new[] { "A", "B" }, Points(Plan(Input(states: null))));
    }

    [Fact]
    public void A_matching_fingerprint_skips_the_whole_root()
    {
        var first = Plan(Input(states: null));
        var fp = PlannedFingerprint(first, "DB1");

        var second = Plan(Input(new Dictionary<string, PlannerRootState>(StringComparer.OrdinalIgnoreCase)
        {
            ["DB1"] = new(IgwDeviceKind.S7, fp),
        }));

        Assert.Empty(Points(second));
        Assert.Equal(0, ReplaceGroups(second));
    }

    [Fact]
    public void A_different_fingerprint_resends_the_root_but_still_never_deletes()
    {
        var plan = Plan(Input(new Dictionary<string, PlannerRootState>(StringComparer.OrdinalIgnoreCase)
        {
            ["DB1"] = new(IgwDeviceKind.S7, "stale-fingerprint"),
        }));

        Assert.Equal(new[] { "A", "B" }, Points(plan));
        Assert.Equal(0, ReplaceGroups(plan));
    }

    /// <summary>块搬到另一台设备：新设备上什么都没有，指纹再一致也必须整块重发。</summary>
    [Fact]
    public void Moving_to_another_device_resends_even_when_the_fingerprint_matches()
    {
        var fp = PlannedFingerprint(Plan(Input(states: null)), "DB1");

        var plan = Plan(Input(new Dictionary<string, PlannerRootState>(StringComparer.OrdinalIgnoreCase)
        {
            ["DB1"] = new(IgwDeviceKind.S7Plus, fp),
        }));

        Assert.Equal(new[] { "A", "B" }, Points(plan));
    }

    /// <summary>关掉 AppendOnly 必须逐字回到旧行为，否则 753 个既有用例的语义就悄悄变了。</summary>
    [Fact]
    public void With_append_only_off_the_old_behaviour_is_untouched()
    {
        var fp = PlannedFingerprint(Plan(Input(states: null)), "DB1");

        var plan = Plan(Input(
            new Dictionary<string, PlannerRootState>(StringComparer.OrdinalIgnoreCase)
            {
                ["DB1"] = new(IgwDeviceKind.S7, fp),
            },
            appendOnly: false));

        Assert.Equal(new[] { "A", "B" }, Points(plan));
        Assert.True(ReplaceGroups(plan) > 0);
    }

    /// <summary>跳过的根仍要出现在块结果里（台账要保留它那一行，否则下轮又当成"没注册过"）。</summary>
    [Fact]
    public void A_skipped_root_still_reports_its_fingerprint()
    {
        var fp = PlannedFingerprint(Plan(Input(states: null)), "DB1");

        var second = Plan(Input(new Dictionary<string, PlannerRootState>(StringComparer.OrdinalIgnoreCase)
        {
            ["DB1"] = new(IgwDeviceKind.S7, fp),
        }));

        Assert.Equal(fp, PlannedFingerprint(second, "DB1"));
    }
}
