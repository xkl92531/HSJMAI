using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

public class IgwRegistrationPlannerTests
{
    private const string Ip = "192.168.1.10";

    private static PlannerDbRow Row(string block, int number, string path, string type, decimal? offsetByte, decimal? offsetBit = null, string comment = null) =>
        new(block, number, path, type, offsetByte, offsetBit, comment);

    private static PlannerInput Input(
        IReadOnlyList<PlannerDbRow> rows = null,
        IReadOnlyList<PlannerIoTag> io = null,
        IReadOnlyCollection<string> changed = null,
        IReadOnlyCollection<string> deleted = null,
        bool ioRefreshed = false,
        bool full = true,
        IgwRegistrationConfig config = null) =>
        new("线体A_PLC1", Ip, 102,
            rows ?? new List<PlannerDbRow>(),
            io ?? new List<PlannerIoTag>(),
            changed ?? new List<string>(),
            deleted ?? new List<string>(),
            ioRefreshed, full, config ?? new IgwRegistrationConfig());

    private static List<PlannedRequest> For(PlanResult plan, IgwDeviceKind kind) =>
        plan.Requests.Where(r => r.Device == kind).ToList();

    private static List<IgwBulkItem> ItemsOf(PlanResult plan, IgwDeviceKind kind) =>
        For(plan, kind).SelectMany(r => r.Request.Items).ToList();

    private static bool HasReplace(PlannedRequest r, params string[] path) =>
        r.Request.ReplaceGroups.Any(g => g.SequenceEqual(path));

    // 1. 全偏移 Bool/Int/Real 块 → S7 经典地址
    [Fact]
    public void Fully_offset_block_goes_to_s7_with_classic_addresses()
    {
        var rows = new List<PlannerDbRow>
        {
            Row("DB_Data", 10, "Flag", "Bool", 0m, 1m, "标志位"),
            Row("DB_Data", 10, "Count", "Int", 2m, 0m),
            Row("DB_Data", 10, "Speed", "Real", 4m, 0m),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        var items = ItemsOf(plan, IgwDeviceKind.S7);
        Assert.Equal(new[] { "DB10.DBX0.1", "DB10.DBW2", "DB10.DBD4" }, items.Select(i => i.Address));
        Assert.Equal(new[] { "bool", "int16", "float" }, items.Select(i => i.DataType));
        Assert.All(items, i => Assert.Equal(new[] { "DB_Data" }, i.GroupPath));
        Assert.All(items, i => Assert.Equal(1000, i.ScanRateMs));
        Assert.All(items, i => Assert.Equal("readWrite", i.Access));
        Assert.Equal("标志位", items[0].Description);
        Assert.Empty(ItemsOf(plan, IgwDeviceKind.S7Plus));
        var block = Assert.Single(plan.Blocks);
        Assert.Equal(IgwDeviceKind.S7, block.Device);
        Assert.Equal(3, block.TagCount);
        Assert.Equal(0, block.Skipped);
    }

    // 2. 含 String 或缺偏移 → 整块 S7Plus，符号地址还原数组路径
    [Fact]
    public void Block_with_string_goes_to_s7plus_with_symbolic_address()
    {
        var rows = new List<PlannerDbRow>
        {
            Row("DB490", 490, "Type.Robot.Robot[2].X", "Real", 0m, 0m),
            Row("DB490", 490, "Type.Name", "String[20]", 4m, 0m),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        Assert.Empty(ItemsOf(plan, IgwDeviceKind.S7));
        var items = ItemsOf(plan, IgwDeviceKind.S7Plus);
        Assert.Equal("DB490.Type.Robot[2].X", items[0].Address);
        Assert.Equal(new[] { "DB490", "Type", "Robot[2]" }, items[0].GroupPath);
        Assert.Equal("X", items[0].Name);
        Assert.Equal("string", items[1].DataType);
        Assert.Equal(20, items[1].StringLength);
        Assert.Equal(IgwDeviceKind.S7Plus, Assert.Single(plan.Blocks).Device);
    }

    [Fact]
    public void Block_with_missing_offset_goes_to_s7plus()
    {
        var rows = new List<PlannerDbRow>
        {
            Row("DB20", 20, "A", "Bool", 0m, 0m),
            Row("DB20", 20, "B", "Int", null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        var items = ItemsOf(plan, IgwDeviceKind.S7Plus);
        Assert.Equal(new[] { "DB20.A", "DB20.B" }, items.Select(i => i.Address));
    }

    [Fact]
    public void Planned_group_and_name_line_up_with_db_read_alias()
    {
        var rows = new List<PlannerDbRow> { Row("DB_Status", 30, "Status.Robot.Robot[2].Clock_2.5Hz", "Bool", null) };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        var item = Assert.Single(ItemsOf(plan, IgwDeviceKind.S7Plus));
        var alias = "通用." + IgwTagNaming.LogicalDeviceName(IgwDeviceKind.S7Plus, Ip) + "." + string.Join(".", item.GroupPath.Append(item.Name));
        Assert.Equal(IgwTagNaming.DbReadAlias(Ip, IgwDeviceKind.S7Plus, "DB_Status", "Status.Robot.Robot[2].Clock_2.5Hz"), alias);
    }

    // 3. 不可映射行跳过并计原因；深度 17 跳过
    [Fact]
    public void Unmappable_and_too_deep_rows_are_skipped_with_reasons()
    {
        var deep = string.Join(".", Enumerable.Range(1, 16).Select(i => "L" + i)); // 16 段 + 块 = 17
        var ok15 = string.Join(".", Enumerable.Range(1, 15).Select(i => "L" + i)); // 15 段 + 块 = 16
        var rows = new List<PlannerDbRow>
        {
            Row("DB1", 1, "Good", "Int", 0m, 0m),
            Row("DB1", 1, "Udt", "\"UDT_X\"", 2m, 0m),
            Row("DB1", 1, "Arr", "Array[0..3] of Bool", 4m, 0m),
            Row("DB1", 1, deep, "Bool", 6m, 0m),
            Row("DB1", 1, ok15, "Bool", 6m, 1m),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        var items = ItemsOf(plan, IgwDeviceKind.S7);
        Assert.Equal(2, items.Count);
        Assert.Equal(15, items[1].GroupPath.Count);
        Assert.Equal(2, plan.SkipReasons["unmapped_type"]);
        Assert.Equal(1, plan.SkipReasons["too_deep"]);
        var block = Assert.Single(plan.Blocks);
        Assert.Equal(2, block.TagCount);
        Assert.Equal(3, block.Skipped);
    }

    // 4. 同组重名追加 _2
    [Fact]
    public void Duplicate_names_in_same_group_get_suffix()
    {
        var rows = new List<PlannerDbRow>
        {
            Row("DB5", 5, "Sub.Flag", "Bool", 0m, 0m),
            Row("DB5", 5, "Sub.flag", "Bool", 0m, 1m),
            Row("DB5", 5, "Sub.FLAG", "Bool", 0m, 2m),
            Row("DB5", 5, "Other.Flag", "Bool", 0m, 3m),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows));

        Assert.Equal(new[] { "Flag", "flag_2", "FLAG_3", "Flag" }, ItemsOf(plan, IgwDeviceKind.S7).Select(i => i.Name));
        Assert.Equal(2, plan.SkipReasons["renamed_duplicate"]);
    }

    // 5. IO 点
    [Fact]
    public void Io_tags_are_mapped_to_s7_io_group()
    {
        var io = new List<PlannerIoTag>
        {
            new("PLC1<<>>PLC2", "Start_PB", "%I4057.4", "Bool", "启动按钮"),
            new("PLC1<<>>PLC2", "Out_W", "%QW2022", "Word", null),
            new("PLC1<<>>PLC2", "Drive_Tel", "%I100.0", "PD_TEL105_IN\"\"", null),
            new("PLC1<<>>PLC2", "BadAddr", "DB10.DBX0.0", "Bool", null),
            new("Clock", "Clock_2.5Hz", "%M0.3", "Bool", null),
            new("Clock", "Clock_2_5Hz", "%M0.4", "Bool", null),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(io: io, full: false, ioRefreshed: true));

        var items = ItemsOf(plan, IgwDeviceKind.S7);
        Assert.Equal(4, items.Count);
        Assert.Equal(4, plan.IoTagCount);
        Assert.Equal(new[] { "IO", "PLC1<<>>PLC2" }, items[0].GroupPath);
        Assert.Equal("Start_PB", items[0].Name);
        Assert.Equal("I4057.4", items[0].Address);
        Assert.Equal("bool", items[0].DataType);
        Assert.Equal(100, items[0].ScanRateMs);
        Assert.Equal("QW2022", items[1].Address);
        Assert.Equal("uInt16", items[1].DataType);
        Assert.Equal("Clock_2_5Hz", items[2].Name);
        Assert.Equal("Clock_2_5Hz_2", items[3].Name);
        Assert.Equal(1, plan.SkipReasons["io_type"]);
        Assert.Equal(1, plan.SkipReasons["io_address"]);
        Assert.Equal(1, plan.SkipReasons["renamed_duplicate"]);
        Assert.Equal("Clock_2_5Hz_2", plan.IoRenames[("Clock", "Clock_2_5Hz")]);
        Assert.Single(plan.IoRenames);
        Assert.Empty(ItemsOf(plan, IgwDeviceKind.S7Plus));
    }

    [Fact]
    public void Io_not_planned_when_not_refreshed_in_incremental_mode()
    {
        var io = new List<PlannerIoTag> { new("T", "A", "%I0.0", "Bool", null) };

        var plan = IgwRegistrationPlanner.Plan(Input(io: io, full: false, ioRefreshed: false));

        Assert.Empty(plan.Requests);
        Assert.Equal(0, plan.IoTagCount);
    }

    // 6. 切分
    [Fact]
    public void Requests_split_at_block_boundaries_and_oversized_block_is_chunked()
    {
        var rows = new List<PlannerDbRow>();
        void Add(string block, int n, int count)
        {
            for (var i = 0; i < count; i++)
            {
                rows.Add(Row(block, n, "V" + i, "Int", i * 2m, 0m));
            }
        }
        Add("DBA", 1, 2);
        Add("DBB", 2, 2);
        Add("DBC", 3, 5);
        var config = new IgwRegistrationConfig { MaxItemsPerRequest = 3 };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, config: config));

        var s7 = For(plan, IgwDeviceKind.S7);
        Assert.Equal(4, s7.Count);
        Assert.Equal(new[] { 2, 2, 3, 2 }, s7.Select(r => r.Request.Items.Count));
        Assert.All(s7, r => Assert.True(r.Request.Items.Count <= 3));
        Assert.True(HasReplace(s7[0], "DBA"));
        Assert.True(HasReplace(s7[1], "DBB"));
        Assert.True(HasReplace(s7[2], "DBC"));
        Assert.False(HasReplace(s7[3], "DBC"));
        Assert.Equal(1, s7.Count(r => HasReplace(r, "DBC")));
        Assert.All(s7[2].Request.Items.Concat(s7[3].Request.Items), i => Assert.Equal("DBC", i.GroupPath[0]));
        Assert.Contains("DBC", s7[3].BlockNames);
    }

    // 7. replaceGroups
    [Fact]
    public void Incremental_changed_block_is_replaced_on_both_devices()
    {
        var rows = new List<PlannerDbRow>
        {
            Row("DB1", 1, "A", "Int", 0m, 0m),
            Row("DB2", 2, "B", "Int", 0m, 0m),
        };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, changed: new[] { "DB1" }, full: false));

        var s7 = Assert.Single(For(plan, IgwDeviceKind.S7));
        Assert.True(HasReplace(s7, "DB1"));
        Assert.False(HasReplace(s7, "DB2"));
        Assert.Single(s7.Request.Items); // 只为变更块生成点
        var plus = Assert.Single(For(plan, IgwDeviceKind.S7Plus));
        Assert.True(HasReplace(plus, "DB1"));
        Assert.Empty(plus.Request.Items);
        Assert.Single(plan.Blocks);
    }

    [Fact]
    public void Deleted_block_produces_replace_only_requests()
    {
        var plan = IgwRegistrationPlanner.Plan(Input(deleted: new[] { "DB9" }, full: false));

        Assert.Equal(2, plan.Requests.Count);
        Assert.All(plan.Requests, r =>
        {
            Assert.Empty(r.Request.Items);
            Assert.True(HasReplace(r, "DB9"));
            Assert.Single(r.Request.ReplaceGroups);
        });
        Assert.Contains(plan.Requests, r => r.Device == IgwDeviceKind.S7);
        Assert.Contains(plan.Requests, r => r.Device == IgwDeviceKind.S7Plus);
    }

    [Fact]
    public void Io_refresh_replaces_io_root_on_s7_only()
    {
        var io = new List<PlannerIoTag> { new("T", "A", "%I0.0", "Bool", null) };

        var plan = IgwRegistrationPlanner.Plan(Input(io: io, ioRefreshed: true, full: false));

        var s7 = Assert.Single(plan.Requests);
        Assert.Equal(IgwDeviceKind.S7, s7.Device);
        Assert.True(HasReplace(s7, "IO"));
    }

    [Fact]
    public void Full_replaces_all_blocks_on_both_devices_and_io_on_s7()
    {
        var rows = new List<PlannerDbRow>
        {
            Row("DB1", 1, "A", "Int", 0m, 0m),
            Row("DB2", 2, "B", "String", 0m, 0m),
        };
        var io = new List<PlannerIoTag> { new("T", "A", "%I0.0", "Bool", null) };

        var plan = IgwRegistrationPlanner.Plan(Input(rows, io, full: true));

        var s7 = Assert.Single(For(plan, IgwDeviceKind.S7));
        var plus = Assert.Single(For(plan, IgwDeviceKind.S7Plus));
        Assert.True(HasReplace(s7, "DB1") && HasReplace(s7, "DB2") && HasReplace(s7, "IO"));
        Assert.True(HasReplace(plus, "DB1") && HasReplace(plus, "DB2"));
        Assert.False(HasReplace(plus, "IO"));
        Assert.Equal(2, s7.Request.Items.Count); // IO + DB1
        Assert.Single(plus.Request.Items);       // DB2
        Assert.Equal("S7", s7.Request.Device.Name);
        Assert.Equal("S7Plus", plus.Request.Device.Name);
        Assert.Equal("线体A_PLC1", s7.Request.Channel.Name);
        Assert.Equal("Siemens.S7", s7.Request.Channel.DriverId);
    }

    // 8. 通道/设备 spec
    [Fact]
    public void Device_spec_has_wizard_defaults_and_logical_device()
    {
        var spec = IgwRegistrationPlanner.DeviceSpec(IgwDeviceKind.S7Plus, Ip, 0, "S7-1500");

        Assert.Equal("S7Plus", spec.Name);
        Assert.Equal(Ip + ":102", spec.Endpoint);
        Assert.Equal("S1500_" + Ip, spec.Settings["dcsLogicalDevice"]);
        // ⚠️ 不要"规范化"成 "active"：≤1.6.85 的 IGW 只认这个中文标签，"active" 会掉进
        // `_ => "all"` 兜底，让整台设备几十万点全量轮询（2026-09-22 查 IGW 源码确认）。
        // 中文标签在所有版本都被显式识别，是唯一跨版本安全的值。
        Assert.Equal("遵循客户端指定的扫描速率", spec.Settings["scanMode"]);
        Assert.Equal("禁用", spec.Settings["initialFromCache"]);
        Assert.Equal("3", spec.Settings["connTimeout"]);
        Assert.Equal("2000", spec.Settings["timeoutMs"]);
        Assert.Equal("2", spec.Settings["retries"]);
        Assert.Equal("0", spec.Settings["requestDelay"]);
        Assert.Equal("启用", spec.Settings["dataCollection"]);
        Assert.Equal("禁用", spec.Settings["demote"]);
        Assert.Equal("3", spec.Settings["demoteAfter"]);
        Assert.Equal("10000", spec.Settings["demotePeriodMs"]);
        Assert.Equal("S7-1500", spec.Settings["model"]);
        Assert.Equal("0", spec.Settings["rack"]);
        Assert.Equal("1", spec.Settings["slot"]);
        Assert.Equal("big", spec.Settings["byteOrder"]);

        var s7 = IgwRegistrationPlanner.DeviceSpec(IgwDeviceKind.S7, Ip, 1102, "S7-1500");
        Assert.Equal("S7", s7.Name);
        Assert.Equal(Ip + ":1102", s7.Endpoint);
        Assert.Equal("S7_" + Ip, s7.Settings["dcsLogicalDevice"]);
        Assert.Equal("GRAPH", IgwRegistrationPlanner.DeviceSpec(IgwDeviceKind.Graph, Ip, 102, "S7-1500").Name);
    }

    [Fact]
    public void Channel_spec_has_wizard_defaults()
    {
        var spec = IgwRegistrationPlanner.ChannelSpec("线体A_PLC1");

        Assert.Equal("线体A_PLC1", spec.Name);
        Assert.Equal("Siemens.S7", spec.DriverId);
        Assert.Equal("ABCD", spec.Settings["wordOrder"]);
        Assert.Equal("2000", spec.Settings["timeoutMs"]);
        Assert.Equal("auto", spec.Settings["batchingMode"]);
        Assert.Equal("仅写入非布尔型标记的最新值", spec.Settings["writeOptimization"]);
        Assert.Equal("10", spec.Settings["dutyCycle"]);
        Assert.Equal("替换为零", spec.Settings["nonNormalFloat"]);
        Assert.Equal("禁用", spec.Settings["diagnostics"]);
        Assert.Equal("默认值", spec.Settings["networkAdapter"]);
    }

    [Fact]
    public void Config_defaults_work_without_configuration()
    {
        var c = new IgwRegistrationConfig();
        Assert.Equal(100, c.IoScanRateMs);
        Assert.Equal(1000, c.DbScanRateMs);
        Assert.Equal(100, c.GraphScanRateMs);
        Assert.Equal(100_000, c.MaxItemsPerRequest);
        Assert.Equal("S7-1500", c.PlcModel);
        Assert.NotNull(AppsettingsConfig.IgwRegistration);
    }
}
