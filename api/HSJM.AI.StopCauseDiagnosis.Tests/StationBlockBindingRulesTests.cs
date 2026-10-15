#nullable enable
using HSJM.AI.AppLayer.PLC.PLC_GraphStationBinding;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 块↔工站绑定改为「自动建议 + 人工确认」：状态记在 Reason 里，手动绑定说了算，
/// 自动建议只在没有手动绑定时兜底，被拒绝的建议不再出现。
/// </summary>
public class StationBlockBindingRulesTests
{
    private const long Project = 1;

    private static PLC_StationBlockBindingEntity Row(string blockName, string station, string? reason, decimal? confidence = null, long? blockId = null) => new()
    {
        ProjectId = Project,
        BlockId = blockId,
        BlockName = blockName,
        StationCode = station,
        Reason = reason,
        Confidence = confidence,
        IsDeleted = false,
    };

    private static PLC_StationBlockBindingEntity Manual(string blockName, string station) => Row(blockName, station, StationBlockBindingRules.ManualReason, 1m);
    private static PLC_StationBlockBindingEntity Suggested(string blockName, string station, decimal confidence = 0.9m) =>
        Row(blockName, station, StationBlockBindingRules.SuggestedReason("测试"), confidence);
    private static PLC_StationBlockBindingEntity Rejected(string blockName, string station) => Row(blockName, station, StationBlockBindingRules.RejectedReason);
    private static GraphBlockRef Block(long id, string name, string? station = null) => new(id, Project, name, station);

    private static HashSet<string> Allowed(params string[] codes) => new(codes, StringComparer.OrdinalIgnoreCase);

    // ── 状态 ──

    [Theory]
    [InlineData("手动绑定", StationBindingStatus.Manual)]
    [InlineData("已拒绝", StationBindingStatus.Rejected)]
    [InlineData("自动建议:名称含工位号", StationBindingStatus.Suggested)]
    [InlineData("DerivedFromStationColoring+BlockCall", StationBindingStatus.Suggested)] // 10-09 之前的自动行
    [InlineData(null, StationBindingStatus.Suggested)]
    public void Status_is_read_from_reason(string? reason, StationBindingStatus expected)
    {
        Assert.Equal(expected, StationBlockBindingRules.Classify(reason));
    }

    // ── 有效工位：手动 → 块工位 → 高置信建议 ──

    [Fact]
    public void Manual_binding_wins_over_block_station_and_suggestions()
    {
        var result = StationBlockBindingRules.ResolveBlockStation(
            [Suggested("FB300_Graph", "OP020"), Manual("FB300_Graph", "OP030")], "OP040", null);

        Assert.Equal(new StationBindingResolution("OP030", StationBindingSource.Manual), result);
    }

    [Fact]
    public void Block_station_comes_before_suggestions()
    {
        var result = StationBlockBindingRules.ResolveBlockStation([Suggested("FB300_Graph", "OP020")], "OP040", null);

        Assert.Equal(new StationBindingResolution("OP040", StationBindingSource.BlockStation), result);
    }

    [Fact]
    public void High_confidence_suggestion_is_used_when_nothing_else_exists()
    {
        Assert.Equal(
            new StationBindingResolution("OP020", StationBindingSource.Suggested),
            StationBlockBindingRules.ResolveBlockStation([Suggested("FB300_Graph", "OP020", 0.8m)], "", null));
        Assert.Null(StationBlockBindingRules.ResolveBlockStation([Suggested("FB300_Graph", "OP020", 0.4m)], null, null));
    }

    [Fact]
    public void Tied_suggestions_on_different_stations_are_not_used()
    {
        Assert.Null(StationBlockBindingRules.ResolveBlockStation(
            [Suggested("FB300_Graph", "OP020", 0.8m), Suggested("FB300_Graph", "OP030", 0.8m)], null, null));
    }

    [Fact]
    public void Rejected_rows_are_never_used()
    {
        Assert.Null(StationBlockBindingRules.ResolveBlockStation([Rejected("FB300_Graph", "OP020")], null, null));
    }

    [Fact]
    public void Stations_outside_the_plc_whitelist_are_ignored()
    {
        var result = StationBlockBindingRules.ResolveBlockStation(
            [Manual("FB300_Graph", "OP090")], "OP030", Allowed("OP010", "OP020", "OP030"));

        Assert.Equal(new StationBindingResolution("OP030", StationBindingSource.BlockStation), result);
    }

    // ── 建议生成 ──

    [Fact]
    public void Block_name_with_one_station_number_gives_a_suggestion()
    {
        var suggestions = StationBlockBindingRules.BuildSuggestions(
            Project, [], [Block(7, "FB220_OP20Manual")], [], null);

        var s = Assert.Single(suggestions);
        Assert.Equal(("FB220_OP20Manual", "OP020", 7L), (s.BlockName, s.StationCode, s.BlockId!.Value));
        Assert.Equal(StationBindingStatus.Suggested, StationBlockBindingRules.Classify(s.Reason));
        Assert.True(s.Confidence >= StationBlockBindingRules.SuggestionUseThreshold);
    }

    [Fact]
    public void Block_name_with_several_or_no_station_numbers_gives_no_name_suggestion()
    {
        var suggestions = StationBlockBindingRules.BuildSuggestions(
            Project, [], [Block(1, "FB182_OP070_OP080_Transfer"), Block(2, "FB3002_Valve")], [], null);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void Shared_or_call_propagation_only_candidates_are_dropped()
    {
        var scored = new[]
        {
            Row("FB3002_Valve", "OP010", "SharedBlock:ConflictingStations", 0.7m, 2).AsShared(),
            Row("FB400_Sub", "OP020", "ReviewRequired:BlockCallOnly", 0.65m, 3),
            Row("FB500_Graph", "OP030", "DerivedFromStationColoring+BlockCall", 0.8m, 4),
        };

        var suggestions = StationBlockBindingRules.BuildSuggestions(
            Project, scored, [Block(2, "FB3002_Valve"), Block(3, "FB400_Sub"), Block(4, "FB500_Graph")], [], null);

        var s = Assert.Single(suggestions);
        Assert.Equal(("FB500_Graph", "OP030"), (s.BlockName, s.StationCode));
    }

    [Fact]
    public void Name_rule_wins_over_coloring_for_the_same_block()
    {
        var suggestions = StationBlockBindingRules.BuildSuggestions(
            Project,
            [Row("FB220_OP20Manual", "OP030", "DerivedFromStationColoring+BlockCall", 0.9m, 7)],
            [Block(7, "FB220_OP20Manual")], [], null);

        Assert.Equal("OP020", Assert.Single(suggestions).StationCode);
    }

    [Fact]
    public void Blocks_with_a_manual_binding_get_no_suggestion()
    {
        var suggestions = StationBlockBindingRules.BuildSuggestions(
            Project, [], [Block(7, "FB220_OP20Manual")], [Manual("fb220_op20manual", "OP021")], null);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void A_rejected_suggestion_does_not_come_back()
    {
        var suggestions = StationBlockBindingRules.BuildSuggestions(
            Project, [], [Block(7, "FB220_OP20Manual")], [Rejected("FB220_OP20Manual", "OP020")], null);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void Suggestions_outside_the_whitelist_are_dropped()
    {
        var suggestions = StationBlockBindingRules.BuildSuggestions(
            Project, [], [Block(7, "FB920_OP90Manual")], [], Allowed("OP010", "OP020"));

        Assert.Empty(suggestions);
    }

    [Fact]
    public void Same_named_blocks_get_one_suggestion()
    {
        var suggestions = StationBlockBindingRules.BuildSuggestions(
            Project,
            [Row("FB220_OP20Manual", "OP020", "DerivedFromStationColoring+BlockCall", 0.9m, 7),
             Row("FB220_OP20Manual", "OP020", "DerivedFromStationColoring+BlockCall", 0.9m, 8)],
            [Block(7, "FB220_OP20Manual"), Block(8, "FB220_OP20Manual")], [], null);

        Assert.Single(suggestions);
    }

    // ── 手动绑定：同名块只绑一次 ──

    [Fact]
    public void Same_named_blocks_in_one_request_are_bound_once()
    {
        var plan = StationBlockBindingRules.PlanManualBind(
            "OP030", [Block(7, "FB300_Graph"), Block(8, "FB300_Graph")], [], 1, "tester", DateTime.Now);

        var row = Assert.Single(plan.ToInsert);
        Assert.Equal(("FB300_Graph", "OP030", StationBlockBindingRules.ManualReason), (row.BlockName, row.StationCode, row.Reason));
        Assert.Equal(2, plan.BlocksToStamp.Count);
    }

    [Fact]
    public void Existing_manual_binding_is_not_duplicated_and_duplicates_are_cleaned()
    {
        var keep = Manual("FB300_Graph", "OP030");
        var dup = Manual("FB300_Graph", "OP030");
        var plan = StationBlockBindingRules.PlanManualBind(
            "OP030", [Block(7, "FB300_Graph", "OP030")], [keep, dup], 1, "tester", DateTime.Now);

        Assert.Empty(plan.ToInsert);
        Assert.Equal([dup.Id], plan.SoftDeleteIds);
        Assert.Contains(plan.Skipped, s => s.BlockName == "FB300_Graph");
    }

    [Fact]
    public void Binding_to_a_new_station_removes_old_rows_of_that_name()
    {
        var oldManual = Manual("FB300_Graph", "OP020");
        var suggestion = Suggested("fb300_graph", "OP030");
        var rejected = Rejected("FB300_Graph", "OP030");
        var otherBlock = Manual("FB400_Graph", "OP020");

        var plan = StationBlockBindingRules.PlanManualBind(
            "OP030", [Block(7, "FB300_Graph", "OP020")], [oldManual, suggestion, rejected, otherBlock], 1, "tester", DateTime.Now);

        Assert.Single(plan.ToInsert);
        Assert.Equal(
            new[] { oldManual.Id, suggestion.Id, rejected.Id }.OrderBy(x => x),
            plan.SoftDeleteIds.OrderBy(x => x));
    }

    // ── 解绑不清空 ──

    [Fact]
    public void After_unbinding_the_block_falls_back_to_a_suggestion()
    {
        Assert.Equal("OP020", StationBlockBindingRules.ResolveStationAfterUnbind([Suggested("FB300_Graph", "OP020")], [], null));
    }

    [Fact]
    public void After_unbinding_without_suggestion_the_coloring_station_is_used()
    {
        Assert.Equal("OP030", StationBlockBindingRules.ResolveStationAfterUnbind(
            [Rejected("FB300_Graph", "OP020")],
            [("OP030", 0.9m), ("OP030", 0.8m), ("OP020", 0.95m)],
            null));
    }

    [Fact]
    public void After_unbinding_with_nothing_left_the_station_is_cleared()
    {
        Assert.Null(StationBlockBindingRules.ResolveStationAfterUnbind([], [], null));
    }

    // ── 排查前提示 ──

    [Fact]
    public void Coverage_warns_about_unconfirmed_and_unassigned_graph_blocks()
    {
        var blocks = new[]
        {
            Block(1, "FB310_Graph", "OP030"),       // 块工位有值、没人工确认
            Block(2, "FB320_Graph"),                // 只有自动建议
            Block(3, "FB330_Graph"),                // 什么都没有
            Block(4, "FB110_Graph", "OP010"),       // 已人工确认
            Block(5, "FB510_Graph", "OP050"),       // 不在本次排查范围
            Block(6, "FB310_Graph", "OP030"),       // 同名重复行，只算一次
        };
        var bindings = new[] { Suggested("FB320_Graph", "OP030"), Manual("FB110_Graph", "OP010") };

        var warnings = StationBlockBindingRules.BuildCoverageWarnings(
            blocks, bindings, new HashSet<string>(["OP010", "OP030"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("OP030 有 2 个 GRAPH 块未人工确认工站") && w.Contains("FB310_Graph") && w.Contains("FB320_Graph"));
        Assert.Contains(warnings, w => w.StartsWith("有 1 个 GRAPH 块没有工站归属") && w.Contains("FB330_Graph"));
        Assert.DoesNotContain(warnings, w => w.Contains("FB110_Graph") || w.Contains("FB510_Graph"));
    }

    [Fact]
    public void Coverage_is_silent_when_every_block_is_confirmed()
    {
        var warnings = StationBlockBindingRules.BuildCoverageWarnings(
            [Block(4, "FB110_Graph", "OP010")], [Manual("FB110_Graph", "OP010")],
            new HashSet<string>(["OP010"], StringComparer.OrdinalIgnoreCase));

        Assert.Empty(warnings);
    }

    // ── 重新提取时回填块工位：不能把手动绑定改乱 ──

    [Fact]
    public void Backfill_keeps_a_manually_bound_station()
    {
        Assert.Null(StationBlockBindingRules.DecideBackfillStation("OP030", "OP070", null, "OP030", null));
    }

    [Fact]
    public void Backfill_repairs_a_block_station_that_drifted_from_its_manual_binding()
    {
        Assert.Equal("OP030", StationBlockBindingRules.DecideBackfillStation("OP070", "OP070", null, "OP030", null));
    }

    [Fact]
    public void Backfill_uses_coloring_then_lets_block_text_override_it()
    {
        Assert.Equal("OP070", StationBlockBindingRules.DecideBackfillStation("", "OP070", null, null, null));
        Assert.Equal("OP030", StationBlockBindingRules.DecideBackfillStation("", "OP070", "OP030", null, null));
    }

    [Fact]
    public void Backfill_text_only_fills_an_empty_block_station()
    {
        Assert.Equal("OP020", StationBlockBindingRules.DecideBackfillStation(null, null, "OP020", null, null));
        Assert.Null(StationBlockBindingRules.DecideBackfillStation("OP030", null, "OP020", null, null));
    }

    [Fact]
    public void Backfill_never_writes_a_station_outside_the_whitelist()
    {
        Assert.Null(StationBlockBindingRules.DecideBackfillStation("OP030", "OP090", null, null, Allowed("OP010", "OP020", "OP030")));
    }

    [Fact]
    public void Backfill_uses_the_whitelist_spelling_of_the_station()
    {
        Assert.Equal("OP20", StationBlockBindingRules.DecideBackfillStation(null, null, "OP020", null, Allowed("OP10", "OP20")));
    }

    // ── 同一 GRAPH 模板 FB 的多个实例 DB 按各自名字归工位 ──

    [Theory]
    [InlineData("DB310_OP030_Graph", "OP010", "OP030")]   // 实例名写着 OP030，就归 OP030
    [InlineData("DB320_OP40_Graph", "OP010", "OP040")]    // OP40 = OP040
    [InlineData("DB115_AxisCombo", "OP010", "OP010")]     // 名字里没有工位号，沿用 FB 的工位
    [InlineData("DB182_OP070_OP080", "OP070", "OP070")]   // 两个工位号，说不清，沿用 FB 的工位
    public void Instance_db_station_comes_from_its_own_name_first(string dbName, string fbStation, string expected)
    {
        Assert.Equal(expected, StationBlockBindingRules.ResolveInstanceStation(dbName, fbStation, null));
    }

    [Fact]
    public void Instance_db_naming_a_station_outside_the_whitelist_falls_back_to_the_fb_station()
    {
        Assert.Equal("OP010", StationBlockBindingRules.ResolveInstanceStation("DB910_OP090_Graph", "OP010", Allowed("OP010", "OP030")));
    }

    [Theory]
    [InlineData("OP30", "op030", true)]
    [InlineData("OP030", "OP030", true)]
    [InlineData("OP030", "OP040", false)]
    [InlineData(null, "OP030", false)]
    public void Station_codes_compare_by_number(string? left, string right, bool expected)
    {
        Assert.Equal(expected, StationBlockBindingRules.IsSameStation(left, right));
    }

    [Theory]
    [InlineData("DB201_Unit2_Graph.RT_DATA.EXEC_BITS.EXEC_BITS[0]")]  // Unit2 不是工位
    [InlineData("DB100_ST1_Graph.RT_DATA.EXEC_BITS.EXEC_BITS[0]")]    // ST1 不是本产线的工位写法
    public void Station_like_tokens_of_another_naming_scheme_do_not_exclude_exec_bits(string variable)
    {
        Assert.True(StationBlockBindingRules.ExecBitBelongsToStation(variable, "OP030", null));
    }

    [Fact]
    public void Instance_name_with_another_naming_scheme_falls_back_to_the_fb_station()
    {
        Assert.Equal("OP010", StationBlockBindingRules.ResolveInstanceStation("DB201_Unit2_Graph", "OP010", null));
    }

    [Fact]
    public void Without_whitelist_only_op_station_numbers_in_block_names_become_suggestions()
    {
        var suggestions = StationBlockBindingRules.BuildSuggestions(
            Project, [], [Block(1, "FB201_Unit2_Graph"), Block(2, "FB220_OP20Manual")], [], null);

        Assert.Equal("OP020", Assert.Single(suggestions).StationCode);
    }

    [Fact]
    public void Exec_bits_of_another_stations_instance_do_not_belong_to_this_station()
    {
        const string op030Bit = "DB310_OP030_Graph.RT_DATA.EXEC_BITS.EXEC_BITS[2]";
        const string sharedBit = "DB115_AxisCombo.RT_DATA.EXEC_BITS.EXEC_BITS[0]";

        Assert.False(StationBlockBindingRules.ExecBitBelongsToStation(op030Bit, "OP010", null));
        Assert.True(StationBlockBindingRules.ExecBitBelongsToStation(op030Bit, "OP030", null));
        Assert.True(StationBlockBindingRules.ExecBitBelongsToStation(sharedBit, "OP010", null));
    }

    // ── 其它使用方取有效行 ──

    [Fact]
    public void Effective_rows_are_manual_only_when_a_manual_row_exists()
    {
        var manual = Manual("FB300_Graph", "OP030");
        Assert.Equal([manual], StationBlockBindingRules.SelectEffective([Suggested("FB300_Graph", "OP020"), manual, Rejected("FB300_Graph", "OP040")]));
    }

    [Fact]
    public void Effective_rows_fall_back_to_suggestions_and_skip_rejected()
    {
        var s = Suggested("FB300_Graph", "OP020");
        Assert.Equal([s], StationBlockBindingRules.SelectEffective([s, Rejected("FB300_Graph", "OP040")]));
    }
}

internal static class BindingRowTestExtensions
{
    public static PLC_StationBlockBindingEntity AsShared(this PLC_StationBlockBindingEntity row)
    {
        row.IsShared = true;
        return row;
    }
}
