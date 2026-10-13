using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 2026-09-22：工位显示「无AST」时，把"为什么查不到"说清楚。
/// 后端的判定条件是 projectid + stationcode + compilestatus='Compiled' 四者同时成立，
/// 这里按"哪一个条件没满足"分档给出人话原因。
/// </summary>
public class MissingAstDiagnosisTests
{
    private static (string StationCode, string CompileStatus) Row(string station, string status) => (station, status);

    [Fact]
    public void No_rows_at_all_means_anchor_discovery_never_ran()
    {
        var reason = MissingAstDiagnosis.Describe("OP010", System.Array.Empty<(string, string)>());

        Assert.Contains("没有任何 AST", reason);
        Assert.Contains("锚点发现", reason);
    }

    [Fact]
    public void Null_rows_are_treated_as_none()
    {
        var reason = MissingAstDiagnosis.Describe("OP010", null);
        Assert.Contains("没有任何 AST", reason);
    }

    /// <summary>别的工位有、这个工位没有——多半是工位号对不上。</summary>
    [Fact]
    public void Rows_exist_for_other_stations_only()
    {
        var reason = MissingAstDiagnosis.Describe("OP010", new[]
        {
            Row("OP020", "Compiled"),
            Row("OP030", "Compiled"),
        });

        Assert.Contains("OP010", reason);
        Assert.Contains("OP020", reason);
        Assert.Contains("OP030", reason);
        Assert.DoesNotContain("没有任何 AST", reason);
    }

    /// <summary>PersistAsync 的 stationCode 是调用方传进来的，没带工位就会留空。</summary>
    [Fact]
    public void Blank_station_rows_are_called_out_explicitly()
    {
        var reason = MissingAstDiagnosis.Describe("OP010", new[]
        {
            Row("", "Compiled"),
            Row(null, "Compiled"),
            Row("OP020", "Compiled"),
        });

        Assert.Contains("工位为空", reason);
        Assert.Contains("2", reason);
    }

    /// <summary>工位对得上但没编译成功——要看是 Pending 还是 Failed。</summary>
    [Fact]
    public void Station_matches_but_nothing_compiled_lists_the_statuses()
    {
        var reason = MissingAstDiagnosis.Describe("OP010", new[]
        {
            Row("OP010", "Pending"),
            Row("OP010", "Pending"),
            Row("OP010", "Failed"),
            Row("OP020", "Compiled"),
        });

        Assert.Contains("Pending", reason);
        Assert.Contains("Failed", reason);
        Assert.Contains("3", reason);
        Assert.DoesNotContain("没有任何 AST", reason);
    }

    [Fact]
    public void Station_match_ignores_case_and_surrounding_space()
    {
        var reason = MissingAstDiagnosis.Describe("op010", new[] { Row(" OP010 ", "Pending") });
        Assert.Contains("Pending", reason);
    }

    /// <summary>真有 Compiled 行就不该走到这里；返回空串让调用方别覆盖掉别的信息。</summary>
    [Fact]
    public void Compiled_rows_present_yields_no_reason()
    {
        var reason = MissingAstDiagnosis.Describe("OP010", new[] { Row("OP010", "Compiled") });
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Compiled_match_ignores_case()
    {
        var reason = MissingAstDiagnosis.Describe("OP010", new[] { Row("OP010", "compiled") });
        Assert.Equal(string.Empty, reason);
    }

    /// <summary>工位多的项目不要把整串工位号糊进日志。</summary>
    [Fact]
    public void Known_station_list_is_truncated()
    {
        var rows = new (string, string)[30];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row($"OP{i:D3}", "Compiled");

        var reason = MissingAstDiagnosis.Describe("ZZZ", rows);

        Assert.Contains("…", reason);
        Assert.True(reason.Length < 400, $"原因串过长({reason.Length})：{reason}");
    }
}
