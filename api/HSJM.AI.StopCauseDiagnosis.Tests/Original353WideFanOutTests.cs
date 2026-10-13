#nullable enable
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353.Legacy;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests;

/// <summary>
/// 宽扇出网络：一根连线上并联很多触点（实测泰星正式 PLC2 FB3010_Detection_RM3545 第 15 网络，
/// 比较器输出同时连 16 个触点，汇入 16 输入 OR 驱动线圈）。
/// 反向追溯不能按「同一根线上各端点的排列」逐条展开，否则要试 N! 条路径、永不结束。
/// </summary>
public class Original353WideFanOutTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>前缀触点 Prefix → 比较器 Eq（输出无法化成布尔表达式）→ 公共连线挂 N 个并联触点 S1..SN → N 输入 OR → 线圈 Target。</summary>
    private static string FanOutNetwork(int branches, bool comparatorPrefix = true)
    {
        static XElement Access(int id, string name) => new("Access",
            new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", id),
            new XElement("Symbol", new XElement("Component", new XAttribute("Name", name))));
        static XElement Part(int id, string name) => new("Part", new XAttribute("Name", name), new XAttribute("UId", id));
        static XElement Id(int uid) => new("IdentCon", new XAttribute("UId", uid));
        static XElement Pin(int uid, string name) => new("NameCon", new XAttribute("UId", uid), new XAttribute("Name", name));
        var wireId = 1000;
        XElement Wire(params XElement[] connectors) => new("Wire", new XAttribute("UId", wireId++), connectors);

        var parts = new XElement("Parts", Access(1, "Prefix"), Access(2, "Step"), Access(3, "StepTarget"), Access(99, "Target"),
            Part(50, "Contact"), Part(51, "Eq"), Part(91, "Coil"),
            new XElement("Part", new XAttribute("Name", "O"), new XAttribute("UId", 90),
                new XElement("TemplateValue", new XAttribute("Name", "Card"), new XAttribute("Type", "Cardinality"), branches)));
        var wires = new XElement("Wires",
            Wire(new XElement("Powerrail"), Pin(50, "in")),
            Wire(Id(1), Pin(50, "operand")),
            Wire(Pin(50, "out"), Pin(51, "pre")),
            Wire(Id(2), Pin(51, "in1")),
            Wire(Id(3), Pin(51, "in2")),
            Wire(Pin(90, "out"), Pin(91, "in")),
            Wire(Id(99), Pin(91, "operand")));
        var sharedNode = new List<XElement> { comparatorPrefix ? Pin(51, "out") : Pin(50, "out") };
        for (var i = 1; i <= branches; i++)
        {
            parts.Add(Access(10 + i, "S" + i), Part(100 + i, "Contact"));
            sharedNode.Add(Pin(100 + i, "in"));
            wires.Add(Wire(Id(10 + i), Pin(100 + i, "operand")));
            wires.Add(Wire(Pin(100 + i, "out"), Pin(90, "in" + i)));
        }
        wires.Add(Wire(sharedNode.ToArray()));
        return new XElement("FlgNet", parts, wires).ToString(SaveOptions.DisableFormatting);
    }

    private static List<PldAnchorBlock> ExtractWithin(string xml)
    {
        var task = Task.Run(() => SiemensFlgNetConstraintExtractor.ExtractAnchorBlocks(xml));
        Assert.True(task.Wait(Budget), $"ExtractAnchorBlocks 超过 {Budget.TotalSeconds}s 未返回（宽扇出网络追溯爆炸）");
        return task.Result;
    }

    private static string[] CoilContacts(List<PldAnchorBlock> anchors) =>
        anchors.Single(a => a.Type == PldAnchorType.Coil && a.Anchor == "Target")
            .DirectContacts.Select(c => c.Name).Distinct().ToArray();

    [Fact]
    public void SixteenParallelContactsOnOneComparatorNodeFinishQuickly()
    {
        var contacts = CoilContacts(ExtractWithin(FanOutNetwork(16)));

        Assert.All(Enumerable.Range(1, 16), i => Assert.Contains("S" + i, contacts));
    }

    [Fact]
    public void OrBlockReadsEveryInputBeyondTheEighth()
    {
        var contacts = CoilContacts(ExtractWithin(FanOutNetwork(12)));

        Assert.All(Enumerable.Range(9, 4), i => Assert.Contains("S" + i, contacts));
    }

    [Fact]
    public void SharedUpstreamContactStaysInEveryParallelBranch()
    {
        // 共用上游触点 Prefix（无比较器）：每个并联分支都应是 Prefix AND Si
        var anchors = ExtractWithin(FanOutNetwork(3, comparatorPrefix: false));
        var coil = anchors.Single(a => a.Type == PldAnchorType.Coil && a.Anchor == "Target");

        Assert.Equal(3, coil.DirectContacts.Count(c => c.Name == "Prefix"));
        Assert.All(Enumerable.Range(1, 3), i => Assert.Contains(coil.DirectContacts, c => c.Name == "S" + i));
    }
}
