#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

/// <summary>K4：期望状态报警配置输入的全项目常量解析（三种来源中的前两种；未接线取接口初值见 ExpectedStateSubstitutionTests）。</summary>
public class ConfigConstantsTests
{
    static readonly PipelineTag[] Tags = [new("SQ", "%I0.0", "Bool"), new("Flag", "%M5.0", "Bool")];

    static IrExpr V(string n) => IrExpr.Var(n);
    static IrStatement A(string id, string target, IrExpr value, IrExpr? guard = null) => new(id, IrStatementKinds.Assign, target, guard ?? IrExpr.True, value);
    static IrUnit U(long id, string block, params IrStatement[] s) => new(id, block, 1, "LAD", "FlgNet", s, [], []);
    static IrUnit CallOf(string callee) => U(99, "OB1", new IrStatement("OB1/1/1", IrStatementKinds.Call, null, IrExpr.True, null,
        new IrCall(callee, "FB", "IDB_" + callee, IrExpr.Unwired(), [])));

    const string Db = """
        <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Static">
          <Member Name="Plain" Datatype="Bool"><StartValue>TRUE</StartValue></Member>
          <Member Name="Unset" Datatype="Bool" />
          <Member Name="Act" Datatype="Array[0..100] of Struct">
            <Member Name="ON" Datatype="Bool"><Subelement Path="7"><StartValue>TRUE</StartValue></Subelement></Member>
            <Member Name="OFF" Datatype="Bool"><StartValue>TRUE</StartValue></Member>
          </Member>
          <Member Name="Bits" Datatype="Array[0..10] of Bool"><Subelement Path="3"><StartValue>1</StartValue></Subelement></Member>
          <Member Name="Num" Datatype="Int"><StartValue>5</StartValue></Member>
        </Section></Sections></Interface>
        """;

    static ConfigConstants Make(params IrUnit[] units) =>
        new(units, new ProjectWrites(units, Tags), b => b == "OB1", new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase) { ["DB_A"] = Db });

    [Fact]
    public void EveryWriteBeingTheSameConstantGivesThatConstant()
    {
        var c = Make(U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", IrExpr.Const("TRUE"))), CallOf("FB_W"));
        Assert.True(c.Resolve("DB_X.Mode"));
    }

    [Fact]
    public void WriteOfAnotherResolvableVariableIsFollowed()
    {
        var c = Make(U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", V("DB_X.Base")), A("FB_W/1/2", "DB_X.Base", IrExpr.Const("FALSE"))), CallOf("FB_W"));
        Assert.False(c.Resolve("DB_X.Mode"));
    }

    [Fact]
    public void WriteOfAnUnwrittenMemoryBitIsNotResolved()   // 「:= 恒真位」：%M 程序不写，值由硬件配置决定，看不到
    {
        var c = Make(U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", V("Flag"))), CallOf("FB_W"));
        Assert.Null(c.Resolve("DB_X.Mode"));
    }

    [Fact]
    public void WriteOfAConstantBitTakesItsValue()   // 6.2 FB130 网络 36：Sensor_Act[36].OFF := AlwaysTRUE（系统存储位特例）
    {
        IrUnit[] units = [U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", V("Flag"))), CallOf("FB_W")];
        var bits = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase) { ["Flag"] = true };
        Assert.True(new ConfigConstants(units, new ProjectWrites(units, Tags), b => b == "OB1", null, bits).Resolve("DB_X.Mode"));
        Assert.True(new ConfigConstants(units, new ProjectWrites(units, Tags), b => b == "OB1", null, bits).Resolve("Flag"));
    }

    [Fact]
    public void WriterCalledWithAConstantBitEnRunsEveryScan()
    {
        IrUnit[] units = [U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", IrExpr.Const("TRUE"))),
            U(99, "OB1", new IrStatement("OB1/1/1", IrStatementKinds.Call, null, IrExpr.True, null, new IrCall("FB_W", "FB", "IDB_W", V("Flag"), [])))];
        var bits = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase) { ["Flag"] = true };
        Assert.True(new ConfigConstants(units, new ProjectWrites(units, Tags), b => b == "OB1", null, bits).Resolve("DB_X.Mode"));
        Assert.Null(new ConfigConstants(units, new ProjectWrites(units, Tags), b => b == "OB1", null).Resolve("DB_X.Mode"));
    }

    [Fact]
    public void DifferentConstantsAreNotResolved()
    {
        var c = Make(U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", IrExpr.Const("TRUE")), A("FB_W/1/2", "DB_X.Mode", IrExpr.Const("FALSE"))), CallOf("FB_W"));
        Assert.Null(c.Resolve("DB_X.Mode"));
    }

    [Fact]
    public void ConditionalWriteIsNotResolved()   // 条件不成立时保持起始值
    {
        var c = Make(U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", IrExpr.Const("TRUE"), V("SQ"))), CallOf("FB_W"));
        Assert.Null(c.Resolve("DB_X.Mode"));
    }

    [Fact]
    public void WriteInABlockThatNeverRunsIsNotResolved()
    {
        var c = Make(U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", IrExpr.Const("TRUE"))));
        Assert.Null(c.Resolve("DB_X.Mode"));
    }

    [Fact]
    public void WriteInABlockWithAJumpIsNotResolved()
    {
        var c = Make(U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", IrExpr.Const("TRUE")),
            new IrStatement("FB_W/1/2", IrStatementKinds.JumpNot, null, IrExpr.True, V("SQ"), null, "L1")), CallOf("FB_W"));
        Assert.Null(c.Resolve("DB_X.Mode"));
    }

    [Fact]
    public void WriteToTheParentStructureIsNotResolved()
    {
        var c = Make(U(1, "FB_W", A("FB_W/1/1", "DB_X", V("DB_Y"))), CallOf("FB_W"));
        Assert.Null(c.Resolve("DB_X.Mode"));
    }

    [Fact]
    public void VariableMentionedInTextIsNotResolved()
    {
        var text = U(2, "FB_T", new IrStatement("FB_T/1/1", IrStatementKinds.Source, null, IrExpr.True, null, null, "DB_A.Plain := FALSE;"));
        Assert.Null(Make(text, CallOf("FB_T")).Resolve("DB_A.Plain"));
        Assert.Null(Make(U(1, "FB_W", A("FB_W/1/1", "DB_X.Mode", IrExpr.Const("TRUE"))), CallOf("FB_W"), text with { UnitId = 3 })
            .Resolve("DB_A.Plain"));
    }

    [Theory]
    [InlineData("DB_A.Plain", true)]
    [InlineData("DB_A.Unset", false)]        // 导出没写起始值：Bool 默认 false
    [InlineData("DB_A.Act[7].ON", true)]     // 结构数组成员按下标的元素起始值
    [InlineData("DB_A.Act[8].ON", false)]
    [InlineData("DB_A.Act[36].OFF", true)]   // 成员自身的起始值
    [InlineData("DB_A.Bits[3]", true)]
    [InlineData("DB_A.Bits[4]", false)]
    [InlineData("\"DB_A\".Plain", true)]
    public void NeverWrittenDbMemberTakesItsStartValue(string name, bool expected)
    {
        Assert.Equal(expected, Make(CallOf("FB_W")).Resolve(name));
    }

    [Theory]
    [InlineData("DB_A.Num")]          // 不是 Bool
    [InlineData("DB_A.Missing")]      // 成员找不到
    [InlineData("DB_B.Plain")]        // 没有该 DB 的接口
    [InlineData("DB_A.Act[#i].ON")]   // 非字面量下标
    [InlineData("Flag")]              // %M：不是 DB 成员
    public void UnknownStartValueIsNotResolved(string name)
    {
        Assert.Null(Make(CallOf("FB_W")).Resolve(name));
    }

    [Fact]
    public void GlobalDbInterfaceIsReadFromTheExport()
    {
        const string xml = """
            <Document><SW.Blocks.GlobalDB ID="0"><AttributeList><Interface><Sections><Section Name="Static">
              <Member Name="X" Datatype="Bool"><StartValue>TRUE</StartValue></Member></Section></Sections></Interface>
              <Name>DB_A</Name></AttributeList></SW.Blocks.GlobalDB></Document>
            """;
        Assert.True(PipelineExportReader.TryReadGlobalDbInterface(xml, out var name, out var iface));
        Assert.Equal("DB_A", name);
        Assert.True(ConfigConstants.StartValue(iface, ["X"]));
        Assert.False(PipelineExportReader.TryReadGlobalDbInterface("<Document><SW.Blocks.FB/></Document>", out _, out _));
        Assert.False(PipelineExportReader.TryReadGlobalDbInterface("not xml", out _, out _));
    }
}
