#nullable enable
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

public class AlarmCatalogMappingTests
{
    static readonly AlarmCatalogRow Row = new("M010_0_Alarm.Type.1.Cylinder[4].Alarm1", "Timeout", "Q122.5_OP1090-YV-01",
        ["I1002.1_OP1090-CYL_000-01OP"], "10.0", "FB121_Cylinder", 17, "Valve[4]", "FB3100_ValveControl", ["FB3100/5/14"])
    { LogicUnitId = 555 };

    static readonly Dictionary<string, PLC_IOTagEntity> Tags = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["Q122.5_OP1090-YV-01"] = new PLC_IOTagEntity { TagName = "Q122.5_OP1090-YV-01", TableName = "IO", Address = "%Q122.5" },
        ["I1002.1_OP1090-CYL_000-01OP"] = new PLC_IOTagEntity { TagName = "I1002.1_OP1090-CYL_000-01OP", TableName = "IO", Address = "%I1002.1" },
    };

    static readonly Dictionary<long, LogicUnitLocation> NoUnits = new();

    static Dictionary<long, LogicUnitLocation> Units(params LogicUnitLocation[] units) => units.ToDictionary(u => u.LogicUnitId);

    [Fact]
    public void MapsRowsToDefinitionsWithIoReferencesAndLocation()
    {
        var d = Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [Row], Tags, Units(new LogicUnitLocation(555, 77, "fb121_cylinder", 18))));
        Assert.Equal("M010_0_Alarm.Type.1.Cylinder[4].Alarm1", d.AlarmVariable);
        Assert.Equal("Timeout", d.Kind);
        Assert.Equal("", d.Direction);
        Assert.Equal(555, d.LogicUnitId);
        Assert.Equal(77, d.BlockId);
        // 显示的网络号取库里逻辑单元自己的 networkid，不取清单行
        Assert.Equal(18, d.NetworkId);
        Assert.Equal("IO", d.Output!.TableName);
        Assert.Equal("%I1002.1", Assert.Single(d.Feedbacks).Address);
    }

    [Fact]
    public void RowWithoutLogicUnitIdIsNotLocatedByNetworkNumber()
    {
        // 旧清单行（无 logic_unit_id）的网络号是归档根元素序号，在含空网络的块里与库 networkid 差 1：不按它定位
        var d = Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [Row with { LogicUnitId = null }], Tags,
            Units(new LogicUnitLocation(555, 77, "FB121_Cylinder", 17))));
        Assert.Equal(0, d.LogicUnitId);
        Assert.Null(d.BlockId);
        Assert.Null(d.NetworkId);
        Assert.Equal("FB121_Cylinder", d.BlockName);
    }

    [Fact]
    public void LogicUnitIdThatNoLongerExistsIsNotLocated()
    {
        // 重新上传后逻辑单元 Id 变了、清单还没重新发布：找不到即不定位，只剩按块名的绑定
        var d = Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [Row], Tags, Units(new LogicUnitLocation(556, 77, "FB121_Cylinder", 17))));
        Assert.Equal(0, d.LogicUnitId);
        Assert.Null(d.BlockId);
        Assert.Null(d.NetworkId);
    }

    [Fact]
    public void LogicUnitInAnotherBlockIsNotLocated()
    {
        var d = Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [Row], Tags, Units(new LogicUnitLocation(555, 78, "FB122_Other", 17))));
        Assert.Equal(0, d.LogicUnitId);
        Assert.Null(d.BlockId);
    }

    [Fact]
    public void FeedbackGroupsUseTheSameNamesAsTheFeedbacks()
    {
        var row = Row with { FeedbackSignals = ["i1002.1_op1090-cyl_000-01op", "DB_X.P"] };
        row = row with { FeedbackGroups = [["i1002.1_op1090-cyl_000-01op"], [" DB_X.P "]] };
        var d = Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [row], Tags, NoUnits));
        Assert.Equal(new[] { new[] { "I1002.1_OP1090-CYL_000-01OP" }, new[] { "DB_X.P" } },
            d.FeedbackGroups.Select(g => g.ToArray()).ToArray());
        Assert.Equal("I1002.1_OP1090-CYL_000-01OP", d.Feedbacks[0].Name);
        Assert.Empty(AlarmCatalogMapping.ToDefinitions(9, [Row], Tags, NoUnits)[0].FeedbackGroups);
    }

    [Fact]
    public void UnknownLocationAndNonIoSignalsAreKeptReadable()
    {
        var row = Row with { OutputSignal = "DB_X.Q", FeedbackSignals = [] };
        var d = Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [row], Tags, NoUnits));
        Assert.Equal(0, d.LogicUnitId);
        Assert.Null(d.BlockId);
        Assert.Null(d.Output!.TableName);
        Assert.Equal("DB_X.Q", d.Output.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MissingOutputMapsToNullInsteadOfEmptyNamedSignal(string? output)
    {
        var row = Row with { OutputSignal = output };
        var d = Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [row], Tags, NoUnits));
        Assert.Null(d.Output);
        Assert.Equal("%I1002.1", Assert.Single(d.Feedbacks).Address);
    }

    [Fact]
    public void BlankFeedbackNamesAreDropped()
    {
        var d = Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [Row with { FeedbackSignals = ["", "DB_Y.Pos"] }], Tags, NoUnits));
        var f = Assert.Single(d.Feedbacks);
        Assert.Equal("DB_Y.Pos", f.Name);
        Assert.Null(f.TableName);
        Assert.Equal(9, d.ProjectId);
        Assert.Equal("Valve[4]", d.ValveInstance);
    }

    static ValveAlarmDefinition Def(string symbol, string kind) =>
        new(1, symbol, kind, "", "V", "FB", 0, null, null, null, []);

    [Fact]
    public void MergeKeepsCatalogFirstAndAddsLegacyAlarmsNotInTheCatalog()
    {
        var merged = AlarmCatalogMapping.MergeWithLegacy(
            [Def("DB_A.Valve[1].FwdErr", "Timeout"), Def("DB_A.Valve[1].Conflict", "PositionConflict")],
            [Def(" db_a.valve[1].fwderr ", "FwdError"), Def("DB_A.Valve[1].FwdLockErr", "FwdLock"), Def("DB_A.Valve[2].BkdErr", "BkdError")]);
        Assert.Equal(new[] { "DB_A.Valve[1].FwdErr", "DB_A.Valve[1].Conflict", "DB_A.Valve[1].FwdLockErr", "DB_A.Valve[2].BkdErr" },
            merged.Select(d => d.AlarmVariable).ToArray());
        // 同一报警位：清单那条胜出（种类来自代码判定，不是引脚名）
        Assert.Equal("Timeout", merged[0].Kind);
    }

    [Fact]
    public void MergeWithEmptyCatalogIsTheLegacyList()
    {
        var legacy = new[] { Def("DB_A.X", "FwdError"), Def("DB_A.Y", "BkdError") };
        Assert.Equal(legacy, AlarmCatalogMapping.MergeWithLegacy([], legacy));
    }

    [Fact]
    public void LatchedBitIsAttributedToItsSetUnitThroughTheStationChain()
    {
        // 锁存报警位：清单行定位到置位所在程序段 → 逻辑单元绑定 → 块绑定 → 块工位（现有工位解析链）
        var row = new AlarmCatalogRow("M010_0_Alarm.Alarm.X_3", "Latched", null, [], null, "FB130_Sensor", 7, "", "", ["置位：FB130_Sensor 网络 7：P2"])
            { LogicUnitId = 700 };
        var d = Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [row], Tags, Units(new LogicUnitLocation(700, 70, "FB130_Sensor", 7))));
        Assert.Equal("Latched", d.Kind);
        Assert.Equal("", d.ValveInstance);
        Assert.Null(d.Output);
        Assert.Empty(d.Feedbacks);
        Assert.Equal(700, d.LogicUnitId);
        var luBinding = new PLC_StationLogicUnitBindingEntity { ProjectId = 9, LogicUnitId = 700, StationCode = "OP010" };
        Assert.Equal("OP010", ValveAlarmStationResolver.Resolve(d.LogicUnitId, d.BlockId, d.BlockName, [luBinding], [], "OP090",
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "OP010", "OP090" }));
        // 没有逻辑单元绑定时回落到块工位
        Assert.Equal("OP090", ValveAlarmStationResolver.Resolve(d.LogicUnitId, d.BlockId, d.BlockName, [], [], "OP090",
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "OP010", "OP090" }));
    }

    [Fact]
    public void FallbackTextBecomesTheDefinitionTextUntilADbCommentReplacesIt()
    {
        var row = new AlarmCatalogRow("CylDB.1.Cylinder[3].Alarm3", "Latched", null, [], null, "FC_Caller", 7, "Valve[3]", "FB_Valve", [])
            { FallbackText = "缩回到位超时" };
        Assert.Equal("缩回到位超时", Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [row], Tags, NoUnits)).Text);
        Assert.Null(Assert.Single(AlarmCatalogMapping.ToDefinitions(9, [row with { FallbackText = null }], Tags, NoUnits)).Text);
    }

    [Fact]
    public void MemberInfoMapsTheCatalogPathToTheParserPathAndTakesItsComment()
    {
        // 终审 I-2：6.2 M010_0_Alarm 的解析器写法带容器占位段（成员名 4.Servo 带点，规整去不掉）
        static ValveAlarmDefinition D(string v, string? text = null) =>
            new(9, v, "Latched", "", "", "FB124", 0, null, null, null, []) { Text = text };
        var members = new (string?, string?)[]
        {
            ("Type.4.Servo.4.Servo[10].HomeInterLockAlarm6", "回原点联锁"),
            ("Type.Sensor.Sensor[1].ON", null),
            ("Word1", null),
        };
        var result = ValveAlarmCatalogService.ApplyMemberInfo(
            [D("M010_0_Alarm.Type.4.Servo[10].HomeInterLockAlarm6"), D("M010_0_Alarm.Type.Sensor[1].ON", "General Alarm"),
             D("M010_0_Alarm.Word1.%X3"), D("M010_0_Alarm.Nope")], members);
        Assert.Equal("M010_0_Alarm.Type.4.Servo.4.Servo[10].HomeInterLockAlarm6", result[0].ReadVariable);
        Assert.Equal("回原点联锁", result[0].Text);
        Assert.Equal("M010_0_Alarm.Type.Sensor.Sensor[1].ON", result[1].ReadVariable);
        Assert.Equal("General Alarm", result[1].Text);                 // 没有成员注释：保留后备文本
        Assert.Equal("M010_0_Alarm.Word1.%X3", result[2].ReadVariable);
        Assert.Null(result[3].ReadVariable);                           // 对不上接口行：照清单写法读
        Assert.Equal("M010_0_Alarm.Type.4.Servo[10].HomeInterLockAlarm6", result[0].AlarmVariable);
    }
}
