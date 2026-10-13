using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests.Hardening;

/// <summary>台账不可用时的改名风险判定（与注册规划器同一套规则）。</summary>
public class IgwLedgerOutageGuardTests
{
    [Fact]
    public void Ambiguous_blocks_follow_planner_root_rename_rules()
    {
        var ambiguous = IgwLedgerOutageGuard.FindAmbiguousBlocks(new[]
        {
            "DB1", "DB.A", "DB_A", "Motor", "Motor_2", "IO", "IO_DB", "Station_2", "Station_3", new string('x', 130),
        });

        Assert.Equal(new[] { "DB.A", "DB_A", "IO", "IO_DB", "Motor", "Motor_2", new string('x', 130) }.OrderBy(x => x),
            ambiguous.OrderBy(x => x));
    }

    [Fact]
    public void Soft_deleted_block_names_count_but_same_name_duplicates_collapse()
    {
        // 生产加载器输入含软删块名：已删 Motor 仍可能占用根组 → 存活 Motor_2 有改名风险；
        // 同一块名的存活 / 软删两行（大小写不同）只算一个名字，不因「重名」误判
        var ambiguous = IgwLedgerOutageGuard.FindAmbiguousBlocks(new[] { "Motor", "Motor_2", "DB7", "db7", "DB8" });

        Assert.Contains("Motor_2", ambiguous);
        Assert.DoesNotContain("DB7", ambiguous);
        Assert.DoesNotContain("db7", ambiguous);
        Assert.DoesNotContain("DB8", ambiguous);
    }

    [Fact]
    public void Colliding_paths_use_leaf_rows_and_normalized_segments()
    {
        var colliding = IgwLedgerOutageGuard.FindCollidingPaths(new[]
        {
            "Flag", "flag", "Status.Robot", "Status.Robot.X", "A.B", "A_B", "Arr.Arr[1]", "Arr[1]", "Ok",
        });

        Assert.Equal(new[] { "Arr.Arr[1]", "Arr[1]", "Flag", "flag" }.OrderBy(x => x, StringComparer.Ordinal),
            colliding.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Io_groups_collide_on_sanitized_table_and_tag()
    {
        var groups = IgwLedgerOutageGuard.FindCollidingIoGroups(new[]
        {
            ("表1", "I1.0_X"), ("表1", "I1_0_X"), ("表1", "i1_0_x"), ("表2", "A"), ("表1", "B"),
        }, t => t.Item1, t => t.Item2);

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Value.Count);
        Assert.Null(IgwLedgerOutageGuard.IoKey("表1", "  "));
        Assert.Equal(IgwLedgerOutageGuard.IoKey("表1", "I1.0_X"), IgwLedgerOutageGuard.IoKey("表1", "i1_0_x"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Io_clash_root_matches_the_planner() =>
        Assert.Equal(IgwRegistrationPlanner.IoClashBlockRoot, IgwLedgerOutageGuard.IoClashRoot);
}
