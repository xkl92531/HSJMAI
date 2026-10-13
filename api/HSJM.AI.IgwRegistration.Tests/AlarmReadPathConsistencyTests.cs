using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 已知问题第三批终审 I-2：泰星报警 DB 成员名本身带点（"4.Servo"、"1.Cylinder"、"0.System"、"0.1Estop"）或就叫 "[1]"。
/// 一键排查清单里的报警位是程序（IR）写法：M010_0_Alarm.Type.4.Servo[10].HomeInterLockAlarm6；
/// IGW 注册用的是解析器的 VariablePath（ai_plc_blockinterface，2026-10-02 用 6.2 归档实测）：数组容器前多一段同名占位
/// Type.4.Servo.4.Servo[10].HomeInterLockAlarm6。NormalizeVariablePath 只认不带点的容器名（Sensor.Sensor[1]），带点的去不掉，
/// 两边按点切段后组路径不同 → 读别名对不上。读取侧改为先按块接口把清单写法换成解析器写法再读（AlarmVariablePaths）。
/// </summary>
public class AlarmReadPathConsistencyTests
{
    private const string Ip = "192.168.1.10";

    // 6.2 M010_0_Alarm（Standard，带偏移 → S7 经典设备）与同形态的优化块（无偏移 → S7Plus 设备）
    private static readonly (string Parser, string Program, decimal? Byte, decimal? Bit)[] Members =
    {
        ("Type.4.Servo.4.Servo[10].HomeInterLockAlarm6", "Type.4.Servo[10].HomeInterLockAlarm6", 450m, 5m),
        ("Type.1.Cylinder.1.Cylinder[3].Alarm3", "Type.1.Cylinder[3].Alarm3", 132m, 2m),
        ("Type.0.System.0.1Estop.[7]", "Type.0.System.0.1Estop.[7]", 26m, 6m),
        ("Type.Sensor.Sensor[1].ON", "Type.Sensor[1].ON", 632m, 0m),
    };

    private static (Dictionary<string, string> Planned, IgwLedgerSnapshot Snapshot) Plan(string block, bool withOffsets)
    {
        var rows = Members.Select(m => new PlannerDbRow(block, 10, m.Parser, "Bool", withOffsets ? m.Byte : null, withOffsets ? m.Bit : null, null)).ToList();
        var plan = IgwRegistrationPlanner.Plan(new PlannerInput("线体A_PLC1", Ip, 102, rows, new List<PlannerIoTag>(), new List<string>(),
            new List<string>(), true, true, new IgwRegistrationConfig()));
        var snapshot = IgwLedgerSnapshotBuilder.Build(IgwReadAliasResolverTests.LedgerRowsLikeRunner(plan), out var malformed);
        Assert.Equal(0, malformed);
        // 注册侧：每个接口行一个点（规划器按行顺序产出），读别名 = 通用.{设备}.{组路径}.{点名}
        var items = plan.Requests.SelectMany(r => r.Request.Items.Select(i => IgwTagNaming.ReadAlias(r.Device, Ip, i.GroupPath, i.Name))).ToList();
        Assert.Equal(Members.Length, items.Count);
        return (Members.Zip(items).ToDictionary(x => x.First.Parser, x => x.Second), snapshot);
    }

    [Theory]
    [InlineData("M010_0_Alarm", true)]    // 经典地址（S7 设备）——泰星四个报警 DB 都是 Standard
    [InlineData("M020_0_Alarm", false)]   // 符号地址（S7Plus 设备）
    public void CatalogPathsResolveToTheRegisteredAliasAfterMappingToTheParserPath(string block, bool withOffsets)
    {
        var (planned, snapshot) = Plan(block, withOffsets);
        var index = AlarmVariablePaths.Index(Members.Select(m => m.Parser));
        foreach (var m in Members)
        {
            var readPath = AlarmVariablePaths.Find(index, m.Program);
            Assert.Equal(m.Parser, readPath);
            Assert.Equal(planned[m.Parser], IgwReadAliasResolver.ResolveDb(Ip, snapshot, block, readPath!));
        }
    }

    [Fact]
    public void WithoutMappingArrayOfDottedNamesWouldMissTheRegisteredAlias()
    {
        // 证据：直接用程序写法读，带点的数组容器（4.Servo、1.Cylinder）对不上；不带点的（Sensor）与成员名叫 [1] 的对得上
        var (planned, snapshot) = Plan("M010_0_Alarm", true);
        string Direct(int i) => IgwReadAliasResolver.ResolveDb(Ip, snapshot, "M010_0_Alarm", Members[i].Program);
        Assert.NotEqual(planned[Members[0].Parser], Direct(0));
        Assert.NotEqual(planned[Members[1].Parser], Direct(1));
        Assert.Equal(planned[Members[2].Parser], Direct(2));
        Assert.Equal(planned[Members[3].Parser], Direct(3));
        Assert.Equal("通用.S7_192.168.1.10.M010_0_Alarm.Type.4.Servo.4.Servo[10].HomeInterLockAlarm6", planned[Members[0].Parser]);
    }

    [Fact]
    public void LegacyGatewayTagPathIsTheSameParserPath()
    {
        // 老网关（2500）：PLC_TagRegistrationJob 以 [块名, VariablePath] 登记点名，读取按「块名.VariablePath」——同样须用解析器写法
        var index = AlarmVariablePaths.Index(Members.Select(m => m.Parser));
        Assert.Equal("M010_0_Alarm." + Members[0].Parser, "M010_0_Alarm." + AlarmVariablePaths.Find(index, Members[0].Program));
    }

    [Theory]
    [InlineData("Type.Sensor.Sensor[1].ON", "Type.Sensor[1].ON")]
    [InlineData("Type.4.Servo.4.Servo[10].X", "Type.4.Servo[10].X")]
    [InlineData("a.b.a.b[1]", "a.b[1]")]
    [InlineData("Type.0.System.0.1Estop.[7]", "Type.0.System.0.1Estop.[7]")]
    [InlineData("Arr[3].Arr[2]", "Arr[3].Arr[2]")]
    [InlineData("Plain.Flag", "Plain.Flag")]
    public void ContainerPlaceholdersAreStripped(string parser, string program)
    {
        Assert.Equal(program, AlarmVariablePaths.StripContainerPlaceholders(parser));
    }

    [Fact]
    public void BitSliceMapsItsParentAndAmbiguousOrUnknownPathsStayUnmapped()
    {
        var index = AlarmVariablePaths.Index(["W.W[2]", "X.X[1]", "X[1]", "Plain"]);
        Assert.Equal("W.W[2].%X3", AlarmVariablePaths.Find(index, "W[2].%X3"));
        Assert.Null(AlarmVariablePaths.Find(index, "X[1]"));          // 两行规整后相同：不认
        Assert.Null(AlarmVariablePaths.Find(index, "Nope"));
        Assert.Equal("Plain", AlarmVariablePaths.Find(index, "plain"));
    }
}
