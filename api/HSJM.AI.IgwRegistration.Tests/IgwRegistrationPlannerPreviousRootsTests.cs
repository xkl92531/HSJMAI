using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 块根组稳定性（task-6 carry-over 3）：台账记录的上次根组（PreviousBlockRoots）优先于「先到先得」改名，
/// 存活块的根组永不漂移；另覆盖块级跳过原因（PlannedBlock.SkipReasons，供台账 SkipSummary）。
/// </summary>
public class IgwRegistrationPlannerPreviousRootsTests
{
    private const string Ip = "192.168.1.10";

    private static PlannerInput Input(
        IReadOnlyList<PlannerDbRow> rows,
        IReadOnlyCollection<string> changed = null,
        IReadOnlyCollection<string> deleted = null,
        bool full = true,
        IReadOnlyDictionary<string, string> previous = null) =>
        new("线体A_PLC1", Ip, 102, rows, new List<PlannerIoTag>(),
            changed ?? new List<string>(), deleted ?? new List<string>(),
            false, full, new IgwRegistrationConfig(), previous);

    private static List<IgwBulkItem> ItemsOf(PlanResult plan, IgwDeviceKind kind) =>
        plan.Requests.Where(r => r.Device == kind).SelectMany(r => r.Request.Items).ToList();

    private static List<List<string>> ReplaceOf(PlanResult plan, IgwDeviceKind kind) =>
        plan.Requests.Where(r => r.Device == kind).SelectMany(r => r.Request.ReplaceGroups).ToList();

    [Fact]
    public void Surviving_block_keeps_previous_root_after_colliding_block_is_deleted()
    {
        // 上次：DB.A → DB_A，DB_A → DB_A_2；本次 DB.A 被删除
        var previous = new Dictionary<string, string> { ["DB.A"] = "DB_A", ["DB_A"] = "DB_A_2" };
        var rows = new List<PlannerDbRow> { new("DB_A", 2, "V", "Int", 0m, 0m, null) };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, deleted: new[] { "DB.A" }, previous: previous));

        Assert.Equal(new[] { "DB_A_2" }, ItemsOf(plan, IgwDeviceKind.S7).Single().GroupPath);
        Assert.Equal("DB_A_2", plan.BlockRootRenames["DB_A"]);
        // 删除块用它自己的上次根组清除，而不是被挤到别的名字
        foreach (var kind in new[] { IgwDeviceKind.S7, IgwDeviceKind.S7Plus })
        {
            var groups = ReplaceOf(plan, kind);
            Assert.Contains(groups, g => g.SequenceEqual(new[] { "DB_A" }));
            Assert.Contains(groups, g => g.SequenceEqual(new[] { "DB_A_2" }));
        }
    }

    [Fact]
    public void Previous_roots_win_over_input_order()
    {
        var previous = new Dictionary<string, string> { ["DB.A"] = "DB_A", ["DB_A"] = "DB_A_2" };
        // 行顺序颠倒（DB_A 先出现）：没有台账时 DB_A 会抢到 DB_A
        var rows = new List<PlannerDbRow>
        {
            new("DB_A", 2, "V", "Int", 0m, 0m, null),
            new("DB.A", 1, "V", "Int", 0m, 0m, null),
        };

        var withoutLedger = IgwRegistrationPlanner.Plan(Input(rows));
        var withLedger = IgwRegistrationPlanner.Plan(Input(rows, previous: previous));

        Assert.Equal("DB_A_2", withoutLedger.BlockRootRenames["DB.A"]);
        var items = ItemsOf(withLedger, IgwDeviceKind.S7);
        Assert.Equal(new[] { "DB_A" }, items.Single(i => i.Address == "DB1.DBW0").GroupPath);
        Assert.Equal(new[] { "DB_A_2" }, items.Single(i => i.Address == "DB2.DBW0").GroupPath);
    }

    [Fact]
    public void New_block_colliding_with_previous_root_of_other_block_is_renamed()
    {
        // DB_A 上次被分配 DB_A_2；本次新出现一个真名 DB_A_2 的块 → 新块让路
        var previous = new Dictionary<string, string> { ["DB_A"] = "DB_A_2" };
        var rows = new List<PlannerDbRow>
        {
            new("DB_A_2", 3, "V", "Int", 0m, 0m, null),
            new("DB_A", 2, "V", "Int", 0m, 0m, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, previous: previous));

        var items = ItemsOf(plan, IgwDeviceKind.S7);
        Assert.Equal(new[] { "DB_A_2" }, items.Single(i => i.Address == "DB2.DBW0").GroupPath);
        Assert.Equal(new[] { "DB_A_2_2" }, items.Single(i => i.Address == "DB3.DBW0").GroupPath);
    }

    [Fact]
    public void Invalid_or_duplicate_previous_roots_are_ignored()
    {
        // X 的台账根组是 IO（非法：与 IO 根组冲突）；Y、Z 台账根组相同（脏数据）→ 先出现者保留
        var previous = new Dictionary<string, string> { ["X"] = "IO", ["Y"] = "Shared", ["Z"] = "Shared" };
        var rows = new List<PlannerDbRow>
        {
            new("X", 1, "V", "Int", 0m, 0m, null),
            new("Y", 2, "V", "Int", 0m, 0m, null),
            new("Z", 3, "V", "Int", 0m, 0m, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, previous: previous));

        var items = ItemsOf(plan, IgwDeviceKind.S7);
        Assert.Equal(new[] { "X" }, items.Single(i => i.Address == "DB1.DBW0").GroupPath);
        Assert.Equal(new[] { "Shared" }, items.Single(i => i.Address == "DB2.DBW0").GroupPath);
        Assert.Equal(new[] { "Z" }, items.Single(i => i.Address == "DB3.DBW0").GroupPath);
    }

    [Fact]
    public void Incremental_plan_with_previous_roots_matches_ledger()
    {
        var previous = new Dictionary<string, string> { ["DB.A"] = "DB_A", ["DB_A"] = "DB_A_2" };
        var rows = new List<PlannerDbRow>
        {
            new("DB_A", 2, "V", "Int", 0m, 0m, null),
            new("DB.A", 1, "V", "Int", 0m, 0m, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, changed: new[] { "DB_A" }, full: false, previous: previous));

        Assert.Equal(new[] { "DB_A_2" }, ItemsOf(plan, IgwDeviceKind.S7).Single().GroupPath);
        Assert.All(new[] { IgwDeviceKind.S7, IgwDeviceKind.S7Plus },
            kind => Assert.Equal(new[] { "DB_A_2" }, ReplaceOf(plan, kind).Single()));
    }

    [Fact]
    public void Known_blocks_without_rows_take_part_in_root_assignment()
    {
        // 增量只传变更块 DB_A 的行；未变更块 DB.A 只出现在 KnownBlocks，但仍参与根组分配（占位、可被改名）
        var rows = new List<PlannerDbRow> { new("DB_A", 2, "V", "Int", 0m, 0m, null) };
        var input = Input(rows, changed: new[] { "DB_A" }, full: false) with { KnownBlocks = new[] { "DB.A", "DB_A" } };

        var plan = IgwRegistrationPlanner.Plan(input);

        // DbRows 块排在 KnownBlocks 之前（保持全量模式顺序不变）：此处 DB_A 先占 DB_A，DB.A 只参与分配、不生成点
        Assert.Equal(new[] { "DB_A" }, ItemsOf(plan, IgwDeviceKind.S7).Single().GroupPath);
        Assert.Equal("DB_A_2", plan.BlockRootRenames["DB.A"]);
        Assert.All(new[] { IgwDeviceKind.S7, IgwDeviceKind.S7Plus },
            kind => Assert.Equal(new[] { "DB_A" }, ReplaceOf(plan, kind).Single()));

        // 有台账根组时未变更块保持根组
        var withLedger = IgwRegistrationPlanner.Plan(input with { PreviousBlockRoots = new Dictionary<string, string> { ["DB.A"] = "DB_A" } });
        Assert.Equal(new[] { "DB_A_2" }, ItemsOf(withLedger, IgwDeviceKind.S7).Single().GroupPath);
    }

    [Fact]
    public void Planned_block_exposes_its_own_skip_reasons()
    {
        var rows = new List<PlannerDbRow>
        {
            new("DB1", 1, "A", "Int", 0m, 0m, null),
            // DTL/LTime 已于 2026-09-21 放开，这里改用仍然不可映射的类型
            new("DB1", 1, "B", "Struct", 2m, 0m, null),
            new("DB2", 2, "C", "Array[*] of Int", 0m, 0m, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        var db1 = plan.Blocks.Single(b => b.BlockName == "DB1");
        var db2 = plan.Blocks.Single(b => b.BlockName == "DB2");
        Assert.Equal(1, db1.SkipReasons[IgwRegistrationPlanner.ReasonUnmappedType]);
        Assert.Equal(1, db2.SkipReasons[IgwRegistrationPlanner.ReasonUnmappedType]);
        Assert.Equal(2, plan.SkipReasons[IgwRegistrationPlanner.ReasonUnmappedType]);
    }
}
