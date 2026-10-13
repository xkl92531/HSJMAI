using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 叶子行过滤（task-6 carry-over 1）：接口表无 ParentId/is_structure 可用，按路径判容器——
/// 同块内某行路径 P 是另一行路径的前缀且紧跟 '.' 或 '[' → P 是容器行，丢弃（与 make_fixtures.py db_rows_of 同规则）。
/// </summary>
public class IgwLeafRowFilterTests
{
    private sealed record Row(string Block, string Path, int Id);

    private static List<string> Leaves(params Row[] rows) =>
        IgwLeafRowFilter.FilterLeaves(rows, r => r.Block, r => r.Path).Select(r => r.Block + ":" + r.Path).ToList();

    [Fact]
    public void Struct_and_array_containers_are_dropped()
    {
        var result = Leaves(
            new Row("DB1", "Status", 1),
            new Row("DB1", "Status.Robot", 2),
            new Row("DB1", "Status.Robot.Robot[1]", 3),
            new Row("DB1", "Status.Robot.Robot[1].X", 4),
            new Row("DB1", "Status.Ready", 5),
            new Row("DB1", "Sensor", 6),
            new Row("DB1", "Sensor.Sensor[0]", 7),
            new Row("DB1", "Arr", 8),
            new Row("DB1", "Arr[2]", 9));

        Assert.Equal(new[]
        {
            "DB1:Status.Robot.Robot[1].X", "DB1:Status.Ready", "DB1:Sensor.Sensor[0]", "DB1:Arr[2]",
        }, result);
    }

    [Fact]
    public void Prefix_without_separator_is_not_a_container()
    {
        // "Motor" 与 "Motor1" / "Motor_2" 只是名字前缀相同，不是父子
        var result = Leaves(
            new Row("DB1", "Motor", 1),
            new Row("DB1", "Motor1", 2),
            new Row("DB1", "Motor_2", 3));

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Containment_is_evaluated_per_block()
    {
        var result = Leaves(
            new Row("DB1", "A", 1),
            new Row("DB2", "A.B", 2));

        Assert.Equal(new[] { "DB1:A", "DB2:A.B" }, result);
    }

    [Fact]
    public void Empty_paths_are_dropped_and_order_is_preserved()
    {
        var result = Leaves(
            new Row("DB1", "Z", 1),
            new Row("DB1", "", 2),
            new Row("DB1", null, 3),
            new Row("DB1", "A", 4));

        Assert.Equal(new[] { "DB1:Z", "DB1:A" }, result);
    }
}
