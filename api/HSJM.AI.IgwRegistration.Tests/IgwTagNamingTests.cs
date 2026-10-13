using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

public class IgwTagNamingTests
{
    [Theory]
    [InlineData("Sensor.Sensor[3]", "Sensor[3]")]
    [InlineData("Status.Robot.Robot[2].X", "Status.Robot[2].X")]
    [InlineData("RT_DATA.EXEC_BITS.EXEC_BITS[5]", "RT_DATA.EXEC_BITS[5]")]
    [InlineData("General.StepNum", "General.StepNum")]
    [InlineData("A.B[1].B[2]", "A.B[1].B[2]")]
    [InlineData("Alarm", "Alarm")]
    public void Normalizes_parser_array_paths(string raw, string expected) =>
        Assert.Equal(expected, IgwTagNaming.NormalizeVariablePath(raw));

    [Theory]
    [InlineData("Clock_2.5Hz", "Clock_2_5Hz")]
    [InlineData("  PLC1 to PLC2急停 ", "PLC1 to PLC2急停")]
    [InlineData("a\tb", "ab")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Sanitizes_names(string raw, string expected) =>
        Assert.Equal(expected, IgwTagNaming.SanitizeName(raw));

    [Fact]
    public void Sanitize_truncates_to_128() =>
        Assert.Equal(128, IgwTagNaming.SanitizeName(new string('x', 300)).Length);

    [Fact]
    public void Splits_normalized_path_keeping_indexes() =>
        Assert.Equal(new[] { "Status", "Robot[2]", "X" }, IgwTagNaming.SplitSegments("Status.Robot[2].X"));

    [Theory]
    [InlineData("泰兴线", "PLC1", "泰兴PLC1", "泰兴线_PLC1")]
    [InlineData(null, "PLC1", "泰兴PLC1", "泰兴PLC1")]
    [InlineData("线.A", "PLC1", "p", "线_A_PLC1")]
    [InlineData(null, null, null, null)]
    public void Builds_channel_name(string line, string code, string project, string expected) =>
        Assert.Equal(expected, IgwTagNaming.ChannelName(line, code, project));

    [Fact]
    public void Builds_read_aliases()
    {
        Assert.Equal("通用.S1500_10.0.0.1.DB490.Type.Robot[2].X",
            IgwTagNaming.DbReadAlias("10.0.0.1", IgwDeviceKind.S7Plus, "DB490", "Type.Robot.Robot[2].X"));
        Assert.Equal("通用.S7_10.0.0.1.DB10.Run",
            IgwTagNaming.DbReadAlias("10.0.0.1", IgwDeviceKind.S7, "DB10", "Run"));
        Assert.Equal("通用.S7_10.0.0.1.IO.默认变量表.Clock_2_5Hz",
            IgwTagNaming.IoReadAlias("10.0.0.1", "默认变量表", "Clock_2.5Hz"));
        Assert.Equal("通用.S7Plus_10.0.0.1.OP010.DB161_HomeGraph.RT_DATA.EXEC_BITS",
            IgwTagNaming.GraphReadAlias("10.0.0.1", "OP010", "DB161_HomeGraph", "RT_DATA.EXEC_BITS"));
        Assert.Equal("通用.S7Plus_10.0.0.1._未分组.DB161_HomeGraph.S_NO",
            IgwTagNaming.GraphReadAlias("10.0.0.1", "", "DB161_HomeGraph", "S_NO"));
    }
}
