#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Contracts;

public class SclContractTests
{
    sealed class MemoryCache : IFbContractCache
    {
        public readonly Dictionary<string, FbContractCacheEntry> Items = new();
        public Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Items.TryGetValue(key, out var e) ? e : null);
        public Task SaveAsync(FbContractCacheEntry entry, CancellationToken ct) { Items[entry.Key] = entry; return Task.CompletedTask; }
    }

    static XElement BoolMember(string name) => new("Member", new XAttribute("Name", name), new XAttribute("Datatype", "Bool"));

    // FB_S（SCL）：PosOk := I_Pos; QF := I_Cmd; O_Out := QF;   OB1 调用它：I_Pos ← I_X(%I)，O_Out → Q_X(%Q)
    static PipelineSnapshot Project(string body, string language)
    {
        var ob = new FlgNetBuilder();
        ob.CallWired("FB_S", "DB_S", ("I_Cmd", "Input", "Bool", "M_Cmd"), ("I_Pos", "Input", "Bool", "I_X"), ("O_Out", "Output", "Bool", "Q_X"));
        return new PipelineSnapshot(1,
        [
            new(10, "FB_S", 110, language, body, "", [], 1,
                FlgNetBuilder.Interface(["I_Cmd", "I_Pos"], ["O_Out"], statics: [BoolMember("PosOk"), BoolMember("QF")]), "FB"),
            new(11, "OB1", 120, "LAD", ob.Build(), "", ["FB_S"], 1, "", "OB"),
        ],
        [new("Q_X", "%Q0.0", "Bool"), new("I_X", "%I0.0", "Bool")]);
    }

    static readonly string SclBody = SclXml.Build("$PosOk", ":=", "$I_Pos", ";", "$QF", ":=", "$I_Cmd", ";", "$O_Out", ":=", "$QF", ";");

    [Fact]
    public async Task SclFbGoesThroughTheContractFlow()
    {
        var source = Project(SclBody, "SCL");
        Assert.False(IrBlock.Load(source, "FB_S").IsTextOnly);
        const string reply = """{"fb":"FB_S","relations":[{"kind":"feedback","output":"O_Out","positionVar":"PosOk","evidence":["FB_S/1/1"]}]}""";
        var reader = new FbContractReader(new MemoryCache(), (_, _, _) => Task.FromResult(reply), "m1");
        var generated = new Original353ProjectResult(1, "v", "fp", source.Units.Select(u => new Original353UnitResult(u.Id, [], [], [])).ToArray(), 0, 0, []);
        var (result, report) = await UnderstandingRuleSource.ApplyAsync(source, generated, reader, "m1", CancellationToken.None);
        Assert.Equal(FbContractStatus.Ok, report.FbStatus["FB_S"]);
        var rule = Assert.Single(result.Units.Single(u => u.LogicUnitId == 120).Rules, r => r.Operator == "IMPLIES");
        Assert.Equal("Q_X", rule.Signals.Single(s => s.Role == "Source").Name);
        Assert.Equal("I_X", rule.Signals.Single(s => s.Role == "Target").Name);
        Assert.Equal(UnderstandingProofs.AiFbContract, rule.Proof);
    }

    [Fact]
    public async Task StlFbIsStillTextOnly()
    {
        var source = Project("      A #I_Pos\n      = #O_Out", "STL");
        var reader = new FbContractReader(new MemoryCache(), (_, _, _) => throw new System.InvalidOperationException("不应调用"), "m1");
        var result = await reader.ReadAsync(source, ["FB_S"], CancellationToken.None);
        Assert.Equal(FbContractStatus.TextOnly, result["FB_S"].Status);
    }

    [Fact]
    public async Task FullyOpaqueSclFbWithReturnIsStillTextOnly()
    {
        // 裁定 F7：整段翻不了、只因源码里有 RETURN 合成了返回语句的块，仍按纯文字块处理，不调 AI
        var body = SclXml.Build("CASE", "$N", "OF", "#1", ":", "RETURN", ";", "END_CASE", ";");
        var source = Project(body, "SCL");
        var reader = new FbContractReader(new MemoryCache(), (_, _, _) => throw new System.InvalidOperationException("不应调用"), "m1");
        var result = await reader.ReadAsync(source, ["FB_S"], CancellationToken.None);
        Assert.Equal(FbContractStatus.TextOnly, result["FB_S"].Status);
    }
}
