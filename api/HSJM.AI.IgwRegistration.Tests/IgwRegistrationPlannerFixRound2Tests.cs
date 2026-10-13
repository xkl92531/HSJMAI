using System.Text.Json;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>Fix round 2：地址守卫 / Full 替换含删除块 / 保留原名去重 / 块根组唯一化 / 配置绑定 / 只读 JSON 选项</summary>
public class IgwRegistrationPlannerFixRound2Tests
{
    private const string Ip = "192.168.1.10";

    private static PlannerInput Input(
        IReadOnlyList<PlannerDbRow> rows = null,
        IReadOnlyCollection<string> changed = null,
        IReadOnlyCollection<string> deleted = null,
        bool full = true) =>
        new("线体A_PLC1", Ip, 102,
            rows ?? new List<PlannerDbRow>(),
            new List<PlannerIoTag>(),
            changed ?? new List<string>(),
            deleted ?? new List<string>(),
            false, full, new IgwRegistrationConfig());

    private static List<IgwBulkItem> ItemsOf(PlanResult plan, IgwDeviceKind kind) =>
        plan.Requests.Where(r => r.Device == kind).SelectMany(r => r.Request.Items).ToList();

    // ---------- 地址守卫 ----------
    [Theory]
    [InlineData("DB1.A", true, true)]
    [InlineData("DB1.Motor 1", true, false)]
    [InlineData("DB1.A\tB", true, false)]
    [InlineData("DB1", true, false)]
    [InlineData("", true, false)]
    [InlineData(null, false, false)]
    [InlineData("DB10.DBX0.1", false, true)]
    public void Address_guard(string address, bool symbolic, bool expected) =>
        Assert.Equal(expected, IgwRegistrationPlanner.IsValidAddress(address, symbolic));

    [Fact]
    public void Address_guard_rejects_over_512_chars() =>
        Assert.False(IgwRegistrationPlanner.IsValidAddress("DB1." + new string('x', 509), symbolic: true));

    [Fact]
    public void Symbolic_row_with_whitespace_member_is_skipped_rest_registered()
    {
        var rows = new List<PlannerDbRow>
        {
            new("DB2", 2, "Motor 1.Speed", "Real", null, null, null),
            new("DB2", 2, "Motor2.Speed", "Real", null, null, null),
            new("DB2", 2, "Name", "String", null, null, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        var items = ItemsOf(plan, IgwDeviceKind.S7Plus);
        Assert.Equal(new[] { "DB2.Motor2.Speed", "DB2.Name" }, items.Select(i => i.Address));
        Assert.Equal(1, plan.SkipReasons["invalid_address"]);
        var block = Assert.Single(plan.Blocks);
        Assert.Equal(IgwDeviceKind.S7Plus, block.Device);
        Assert.Equal(2, block.TagCount);
        Assert.Equal(1, block.Skipped);
    }

    [Fact]
    public void Classic_block_keeps_whitespace_member_because_classic_address_is_valid()
    {
        // 经典地址不含成员名，守卫通过；IGW 名称规则只禁首尾空白，中间空格合法
        var rows = new List<PlannerDbRow>
        {
            new("DB3", 3, "Motor 1", "Int", 0m, 0m, null),
            new("DB3", 3, "Motor2", "Int", 2m, 0m, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        var items = ItemsOf(plan, IgwDeviceKind.S7);
        Assert.Equal(new[] { "DB3.DBW0", "DB3.DBW2" }, items.Select(i => i.Address));
        Assert.Equal("Motor 1", items[0].Name);
        Assert.False(plan.SkipReasons.ContainsKey("invalid_address"));
    }

    [Fact]
    public void Symbolic_row_with_600_char_path_is_skipped()
    {
        var longPath = string.Join(".", Enumerable.Range(0, 5).Select(i => new string((char)('a' + i), 120)));
        Assert.True(longPath.Length > 600);
        var rows = new List<PlannerDbRow>
        {
            new("DB4", 4, longPath, "Real", null, null, null),
            new("DB4", 4, "Short", "Real", null, null, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        var item = Assert.Single(ItemsOf(plan, IgwDeviceKind.S7Plus));
        Assert.Equal("DB4.Short", item.Address);
        Assert.Equal(1, plan.SkipReasons["invalid_address"]);
        Assert.All(plan.Requests.SelectMany(r => r.Request.Items), i => Assert.True(i.Address.Length <= 512));
    }

    // ---------- Full 替换 = DbRows 块 ∪ 删除块 ----------
    [Fact]
    public void Full_mode_replaces_deleted_blocks_too()
    {
        var rows = new List<PlannerDbRow> { new("DB1", 1, "A", "Int", 0m, 0m, null) };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, deleted: new[] { "DB_Gone" }, full: true));

        foreach (var device in new[] { IgwDeviceKind.S7, IgwDeviceKind.S7Plus })
        {
            var groups = plan.Requests.Where(r => r.Device == device).SelectMany(r => r.Request.ReplaceGroups).ToList();
            Assert.Contains(groups, g => g.SequenceEqual(new[] { "DB1" }));
            Assert.Contains(groups, g => g.SequenceEqual(new[] { "DB_Gone" }));
        }
    }

    // ---------- 去重：保留原名 ----------
    [Fact]
    public void Genuine_suffixed_name_keeps_its_name_and_duplicate_skips_it()
    {
        var rows = new List<PlannerDbRow>
        {
            new("DB5", 5, "X", "Int", 0m, 0m, null),
            new("DB5", 5, "x", "Int", 2m, 0m, null),
            new("DB5", 5, "X_2", "Int", 4m, 0m, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        Assert.Equal(new[] { "X", "x_3", "X_2" }, ItemsOf(plan, IgwDeviceKind.S7).Select(i => i.Name));
        Assert.Single(plan.DbRenames);
        Assert.Equal("x_3", plan.DbRenames[("DB5", "x")]);
        Assert.Equal(1, plan.SkipReasons["renamed_duplicate"]);
    }

    // ---------- 块根组唯一化 ----------
    [Fact]
    public void Colliding_block_roots_rename_later_block()
    {
        var rows = new List<PlannerDbRow>
        {
            new("DB.A", 1, "V", "Int", 0m, 0m, null),
            new("DB_A", 2, "V", "Int", 0m, 0m, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        Assert.Single(plan.BlockRootRenames);
        Assert.Equal("DB_A_2", plan.BlockRootRenames["DB_A"]);
        var items = ItemsOf(plan, IgwDeviceKind.S7);
        Assert.Equal(new[] { "DB_A" }, items.Single(i => i.Address == "DB1.DBW0").GroupPath);
        Assert.Equal(new[] { "DB_A_2" }, items.Single(i => i.Address == "DB2.DBW0").GroupPath);
        var groups = plan.Requests.Where(r => r.Device == IgwDeviceKind.S7Plus).SelectMany(r => r.Request.ReplaceGroups).ToList();
        Assert.Contains(groups, g => g.SequenceEqual(new[] { "DB_A" }));
        Assert.Contains(groups, g => g.SequenceEqual(new[] { "DB_A_2" }));
        Assert.Empty(plan.DbRenames); // 根组不同，不再是同组重名
    }

    [Fact]
    public void Io_clash_root_skips_real_io_db_block()
    {
        var rows = new List<PlannerDbRow>
        {
            new("IO", 1, "V", "Int", 0m, 0m, null),
            new("IO_DB", 2, "V", "Int", 0m, 0m, null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        Assert.Equal("IO_DB_2", plan.BlockRootRenames["IO"]);
        Assert.False(plan.BlockRootRenames.ContainsKey("IO_DB"));
    }

    [Fact]
    public void Root_renames_are_stable_between_full_and_incremental()
    {
        var rows = new List<PlannerDbRow>
        {
            new("DB.A", 1, "V", "Int", 0m, 0m, null),
            new("DB_A", 2, "V", "Int", 0m, 0m, null),
        };

        var full = IgwRegistrationPlanner.Plan(Input(rows));
        var incremental = IgwRegistrationPlanner.Plan(Input(rows, changed: new[] { "DB_A" }, full: false));

        Assert.Equal(full.BlockRootRenames["DB_A"], incremental.BlockRootRenames["DB_A"]);
        Assert.Equal(new[] { "DB_A_2" }, ItemsOf(incremental, IgwDeviceKind.S7).Single().GroupPath);
    }

    // ---------- 配置绑定（与 Program.InitConfig 相同方式） ----------
    [Fact]
    public void Igw_registration_config_binds_from_webconfig_section()
    {
        var original = AppsettingsConfig.IgwRegistration;
        try
        {
            AppsettingsConfig.IgwRegistration = new IgwRegistrationConfig();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["WebConfig:IgwRegistration:DbScanRateMs"] = "500",
                    ["WebConfig:IgwRegistration:MaxItemsPerRequest"] = "5000",
                })
                .Build();

            var config = new AppsettingsConfig();
            configuration.Bind("WebConfig", config);

            Assert.Equal(500, AppsettingsConfig.IgwRegistration.DbScanRateMs);
            Assert.Equal(5000, AppsettingsConfig.IgwRegistration.MaxItemsPerRequest);
            Assert.Equal(100, AppsettingsConfig.IgwRegistration.IoScanRateMs); // 未配置项保留默认
        }
        finally
        {
            AppsettingsConfig.IgwRegistration = original;
        }
    }

    // ---------- JSON 选项只读 ----------
    [Fact]
    public void Bulk_json_options_are_read_only()
    {
        Assert.True(IgwBulkJson.Options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => IgwBulkJson.Options.WriteIndented = true);
        Assert.Contains("\"groupPath\"", JsonSerializer.Serialize(new IgwBulkItem { Name = "a" }, IgwBulkJson.Options));
    }
}
