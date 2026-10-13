#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests.Understanding.Networks;

public class NetworkMergeTests
{
    static readonly PipelineTag[] Tags =
    [
        new("Q1", "%Q0.0", "Bool"), new("Q2", "%Q0.1", "Bool"), new("Q3", "%Q0.2", "Bool"), new("Q4", "%Q0.3", "Bool"),
        new("A", "%I0.0", "Bool"), new("B", "%I0.1", "Bool"), new("C", "%I0.2", "Bool"), new("K", "%M1.2", "Bool")
    ];

    static AiNetworkConstraint Same(string a, string b, string evidence) =>
        new("SAME_STATE", [new(a, "Member"), new(b, "Member")], false, null, [evidence]);

    static AiNetworkConstraint Mutex(string a, string b, params string[] evidence) =>
        new("AT_MOST_ONE", [new(a, "Member"), new(b, "Member")], false, null, evidence);

    static NetworkAiOutcome Ok(long id, string block, int network, params AiNetworkConstraint[] constraints) =>
        new(id, block, network, NetworkAiStatus.Ok, new AiNetworkReply("", constraints), "nk" + id, null);

    static Original353ProjectResult Empty(PipelineSnapshot source) =>
        new(1, "v", "fp", source.Units.Select(u => new Original353UnitResult(u.Id, [], [], [])).ToArray(), 0, 0, []);

    static readonly Dictionary<string, FbContractOutcome> NoContracts = new();

    [Fact]
    public void ValidatedNetworkConclusionsAreMergedAndRejectionsReported()
    {
        var ob = new FlgNetBuilder(); ob.Series("Coil", "Q3", ("K", false), ("C", false));     // Q3 := K AND C（代码不认 K 恒真）
        var fc = new FlgNetBuilder(); fc.Series("Coil", "Q4", ("C", false));
        var source = new PipelineSnapshot(1,
        [
            new(1, "OB1", 200, "LAD", ob.Build(), "", [], 2, "", "OB"),
            new(2, "FC1", 300, "LAD", fc.Build(), "", [], 1, "", "FC"),
        ], Tags);
        var outcomes = new Dictionary<long, NetworkAiOutcome>
        {
            [200] = Ok(200, "OB1", 2, Same("Q3", "C", "OB1/2/1"), Same("Q3", "K", "OB1/2/1")),
            [300] = Ok(300, "FC1", 1, Same("Q4", "C", "FC1/1/1")),
        };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), NoContracts, "m1", outcomes);
        var rule = Assert.Single(result.Units.Single(u => u.LogicUnitId == 200).Rules);
        Assert.Equal("SAME_STATE", rule.Operator);
        Assert.Equal(UnderstandingProofs.AiNetwork, rule.Proof);
        Assert.Equal("nk200", rule.Trace!.CacheKey);
        Assert.Empty(result.Units.Single(u => u.LogicUnitId == 300).Rules);    // FC 不管
        Assert.Equal(NetworkAiStatus.Ok, report.NetworkStatus[200]);
        Assert.Equal("SignalNotIo:K", report.NetworkRejectReasons["OB1/2|SAME_STATE|1"]);
        Assert.Equal(1, report.NoteCounts["NetworkRule:AI:Network"]);
        Assert.Equal(1, report.NoteCounts["NetworkCallerIsFc"]);
    }

    /// <summary>Q1 := DB.M ∧ A；Q2 := ¬DB.M ∧ B（DB.M 由别处程序写入），可证互斥。</summary>
    static (PipelineSnapshot Source, PipelineUnit Unit) Interlock(params PipelineUnit[] extra)
    {
        var ob = new FlgNetBuilder("GlobalVariable");
        ob.Series("Coil", "Q1", ("DB.M", false), ("A", false));
        ob.Series("Coil", "Q2", ("DB.M", true), ("B", false));
        var writer = new FlgNetBuilder("GlobalVariable"); writer.Series("Coil", "DB.M", ("C", false));
        var unit = new PipelineUnit(1, "OB1", 200, "LAD", ob.Build(), "", [], 1, "", "OB");
        return (new PipelineSnapshot(1, [unit, new(2, "OB2", 210, "LAD", writer.Build(), "", [], 1, "", "OB"), .. extra], Tags), unit);
    }

    [Fact]
    public void CodeLevelNetworkMutexRemovesAContradictingSameStateAndCountsTheProof()
    {
        var (source, unit) = Interlock();
        var ir = UnitTranslator.Translate(unit, Tags);
        var ids = ir.Statements.Select(s => s.Id).ToArray();
        var old = new PipelineRule("SAME_STATE", [new("Q1", "Member"), new("Q2", "Member")], "旧规则");
        var generated = Empty(source) with
        {
            Units = [new Original353UnitResult(200, [old], [], []), new Original353UnitResult(210, [], [], [])]
        };
        var outcomes = new Dictionary<long, NetworkAiOutcome> { [200] = Ok(200, "OB1", 1, Mutex("Q1", "Q2", ids)) };
        var (result, report) = UnderstandingRuleSource.Merge(source, generated, NoContracts, "m1", outcomes);
        var rules = result.Units.Single(u => u.LogicUnitId == 200).Rules;
        var mutex = Assert.Single(rules);
        Assert.Equal("AT_MOST_ONE", mutex.Operator);
        Assert.Equal(1, report.NoteCounts["SameStateContradictsMutex"]);
        // 同 Key 的代码证明先到（Prove 已产出），网络结论不覆盖
        Assert.True(mutex.Proof is UnderstandingProofs.CodeNetwork);
        Assert.Equal(1, report.NoteCounts["NetworkRuleKeptEarlier"]);
    }

    [Fact]
    public void NetworkRuleOnAnOutputWrittenElsewhereIsDropped()
    {
        var other = new FlgNetBuilder("GlobalVariable"); other.Series("Coil", "Q1", ("C", false));
        var (source, unit) = Interlock(new PipelineUnit(3, "OB3", 220, "LAD", other.Build(), "", [], 1, "", "OB"));
        var ids = UnitTranslator.Translate(unit, Tags).Statements.Select(s => s.Id).ToArray();
        var outcomes = new Dictionary<long, NetworkAiOutcome>
        {
            [200] = Ok(200, "OB1", 1, new AiNetworkConstraint("IMPLIES", [new("Q1", "Source"), new("A", "Target")], true, 2, ids))
        };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), NoContracts, "m1", outcomes);
        Assert.Empty(result.Units.Single(u => u.LogicUnitId == 200).Rules);
        Assert.Equal(1, report.NoteCounts["NetworkQWrittenElsewhere"]);
    }

    [Fact]
    public void NetworksInBlocksWithControlFlowAreNotUsed()
    {
        var ret = new FlgNetBuilder();
        var part = ret.Part("Return");
        ret.Wire(FlgNetBuilder.Rail(), FlgNetBuilder.Pin(part, "in"));
        var (source, unit) = Interlock(new PipelineUnit(1, "OB1", 201, "LAD", ret.Build(), "", [], 2, "", "OB"));
        var ids = UnitTranslator.Translate(unit, Tags).Statements.Select(s => s.Id).ToArray();
        var outcomes = new Dictionary<long, NetworkAiOutcome> { [200] = Ok(200, "OB1", 1, Same("Q1", "A", ids[0])) };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), NoContracts, "m1", outcomes);
        Assert.Empty(result.Units.Single(u => u.LogicUnitId == 200).Rules);
        Assert.Equal(1, report.NoteCounts["NetworkBlockHasControlFlow"]);
    }

    [Fact]
    public void UnusableOutcomesOnlyReportTheirStatus()
    {
        var (source, _) = Interlock();
        var outcomes = new Dictionary<long, NetworkAiOutcome>
        {
            [200] = new(200, "OB1", 1, NetworkAiStatus.Deadline, null, "nk", "ReadingDeadline"),
            [210] = new(210, "OB2", 1, NetworkAiStatus.TooLarge, null, "nk2", null),
        };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), NoContracts, "m1", outcomes);
        Assert.Equal(NetworkAiStatus.Deadline, report.NetworkStatus[200]);
        Assert.Equal(NetworkAiStatus.TooLarge, report.NetworkStatus[210]);
        Assert.DoesNotContain(report.NoteCounts.Keys, k => k.StartsWith("NetworkRule:", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutNetworkOutcomesTheReportIsEmpty()
    {
        var (source, _) = Interlock();
        var (_, report) = UnderstandingRuleSource.Merge(source, Empty(source), NoContracts, "m1");
        Assert.Empty(report.NetworkStatus);
        Assert.Empty(report.NetworkRejectReasons);
        Assert.Null(report.NetworkReadError);
    }

    sealed class EmptyFbCache : IFbContractCache
    {
        public Task<FbContractCacheEntry?> GetAsync(string key, CancellationToken ct) => Task.FromResult<FbContractCacheEntry?>(null);
        public Task SaveAsync(FbContractCacheEntry entry, CancellationToken ct) => Task.CompletedTask;
    }

    sealed class NullNetworkCache : INetworkAiCache
    {
        public Task<NetworkAiCacheEntry?> GetAsync(string key, CancellationToken ct) => Task.FromResult<NetworkAiCacheEntry?>(null);
        public Task SaveAsync(NetworkAiCacheEntry entry, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task ApplyReadsNetworksAfterContractsAndMergesThem()
    {
        var (source, unit) = Interlock();
        var ids = UnitTranslator.Translate(unit, Tags).Statements.Select(s => s.Id).ToArray();
        var reply = $$"""{"constraints":[{"operator":"REQUIRES_0_WHEN_FALSE","signals":[{"name":"A","role":"Source"},{"name":"Q1","role":"Target"}],"evidence":["{{ids[0]}}"]}]}""";
        var reader = new FbContractReader(new EmptyFbCache(), (_, _, _) => throw new InvalidOperationException("无 FB"), "m1");
        var networks = new NetworkAiReader(new NullNetworkCache(), (_, _, _) => Task.FromResult(reply), "m1");
        var (result, report) = await UnderstandingRuleSource.ApplyAsync(source, Empty(source), reader, "m1", CancellationToken.None, networks);
        Assert.Equal(NetworkAiStatus.Ok, report.NetworkStatus[200]);
        Assert.Contains(result.Units.Single(u => u.LogicUnitId == 200).Rules,
            r => r.Operator == "REQUIRES_0_WHEN_FALSE" && r.Proof == UnderstandingProofs.AiNetwork);
    }

    [Fact]
    public async Task ANetworkStageFailureKeepsTheContractResultsAndCancellationStillPropagates()
    {
        var (source, _) = Interlock();
        var reader = new FbContractReader(new EmptyFbCache(), (_, _, _) => throw new InvalidOperationException("无 FB"), "m1");
        var (result, report) = await UnderstandingRuleSource.ApplyAsync(source, Empty(source), reader, "m1", CancellationToken.None,
            (_, _) => throw new InvalidOperationException("网络阶段出错"));
        Assert.Contains("网络阶段出错", report.NetworkReadError);
        Assert.Empty(report.NetworkStatus);
        Assert.Contains(result.Units.Single(u => u.LogicUnitId == 200).Rules, r => r.Proof == UnderstandingProofs.CodeNetwork);

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UnderstandingRuleSource.ApplyAsync(source, Empty(source), reader, "m1",
            cts.Token, (_, _) => { cts.Cancel(); throw new OperationCanceledException(cts.Token); }));
    }

    /// <summary>FB_V：O := IWP，契约给反馈与超时报警；OB1 以全局背景 DB_V 调用并接 IO。另有 OB2 是普通网络。</summary>
    static (PipelineSnapshot Source, Dictionary<string, FbContractOutcome> Contracts) ValveProject()
    {
        var v = new FlgNetBuilder(); v.Series("Coil", "O", ("IWP", false));
        var ob = new FlgNetBuilder { CallInstanceScope = "GlobalVariable" };
        ob.CallWired("FB_V", "DB_V", ("IWP", "Input", "Bool", "C"), ("O", "Output", "Bool", "Q4"));
        var net = new FlgNetBuilder("GlobalVariable"); net.Series("Coil", "Q1", ("A", false), ("B", false));
        var source = new PipelineSnapshot(1,
        [
            new(10, "FB_V", 100, "LAD", v.Build(), "", [], 1, FlgNetBuilder.Interface(["IWP"], ["O", "A"]), "FB"),
            new(11, "OB1", 110, "LAD", ob.Build(), "", ["FB_V"], 1, "", "OB"),
            new(12, "OB2", 120, "LAD", net.Build(), "", [], 1, "", "OB"),
        ], Tags);
        var feedback = new AiContractRelation("feedback", ["O"], [], "IWP", null, null, null, ["x"], null);
        var alarm = new AiContractRelation("timeoutAlarm", ["O"], [], "IWP", null, null, "A", ["x"], null);
        return (source, new Dictionary<string, FbContractOutcome>(StringComparer.OrdinalIgnoreCase)
        {
            ["FB_V"] = new("FB_V", FbContractStatus.Ok, new ValidatedFbContract("FB_V",
                [new ValidatedRelation(feedback, false, null), new ValidatedRelation(alarm, false, null)]), "ck", null)
        });
    }

    [Fact]
    public void ValidationFailureDropsOnlyThatNetwork()   // 审查 I-1：契约规则与报警清单照常，发布不回退
    {
        var (source, contracts) = ValveProject();
        var (baseline, baseReport) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.NotEmpty(baseReport.Alarms);
        Assert.NotEqual(0, baseline.RuleCount);
        var outcomes = new Dictionary<long, NetworkAiOutcome> { [120] = Ok(120, "OB2", 1, Same("Q1", "A", "OB2/1/1")) };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1", outcomes,
            (_, _, _) => throw new InvalidOperationException("核对出错"));
        Assert.Equal(baseline.Units.SelectMany(u => u.Rules).Select(r => r.Key).Order(),
            result.Units.SelectMany(u => u.Rules).Select(r => r.Key).Order());
        Assert.Equal(baseReport.Alarms.Select(a => a.Alarm.AlarmSymbol), report.Alarms.Select(a => a.Alarm.AlarmSymbol));
        Assert.Equal(1, report.NoteCounts["NetworkValidateFailed"]);
        Assert.Equal(NetworkAiStatus.Failed, report.NetworkStatus[120]);
        Assert.Empty(result.Units.Single(u => u.LogicUnitId == 120).Rules);
    }

    static string[] ContractRuleKeys(Original353ProjectResult r) =>
        r.Units.Single(u => u.LogicUnitId == 110).Rules.Select(x => x.Key).Order().ToArray();

    [Fact]
    public void CodeProofFailureInOneSegmentKeepsContractRulesAndAlarms()   // 整计划审查 I-1
    {
        var (source, contracts) = ValveProject();
        var (baseline, baseReport) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        Assert.NotEmpty(ContractRuleKeys(baseline));
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1", null, null,
            prove: (_, _) => throw new InvalidOperationException("证明出错"));
        Assert.Equal(ContractRuleKeys(baseline), ContractRuleKeys(result));
        Assert.Equal(baseReport.Alarms.Select(a => a.Alarm.AlarmSymbol), report.Alarms.Select(a => a.Alarm.AlarmSymbol));
        Assert.True(report.NoteCounts["CodeProofFailed"] >= 1);
        Assert.DoesNotContain(report.NoteCounts.Keys, k => k.StartsWith("CodeProof:", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectIndexFailureDisablesOnlyCodeProofs()   // 整计划审查 I-1
    {
        var (source, contracts) = ValveProject();
        var (baseline, baseReport) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        var outcomes = new Dictionary<long, NetworkAiOutcome> { [120] = Ok(120, "OB2", 1, Same("Q1", "A", "OB2/1/1")) };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1", outcomes, null,
            buildProofs: _ => throw new InvalidOperationException("索引出错"));
        Assert.Equal(1, report.NoteCounts["CodeProofDisabled"]);
        Assert.Equal(ContractRuleKeys(baseline), ContractRuleKeys(result));
        Assert.Equal(baseReport.Alarms.Select(a => a.Alarm.AlarmSymbol), report.Alarms.Select(a => a.Alarm.AlarmSymbol));
        Assert.Equal(NetworkAiStatus.Ok, report.NetworkStatus[120]);   // 网络 AI 照常核对
    }

    [Fact]
    public void CodeProofDeadlineStopsOnlyCodeProofs()   // 协调方裁定：合并阶段到期只停代码证明，契约规则与报警清单照常输出
    {
        var (source, contracts) = ValveProject();
        var (baseline, baseReport) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        var outcomes = new Dictionary<long, NetworkAiOutcome> { [120] = Ok(120, "OB2", 1, Same("Q1", "A", "OB2/1/1")) };
        using var deadline = new System.Threading.CancellationTokenSource();
        deadline.Cancel();
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1", outcomes, null, proofDeadline: deadline.Token);
        Assert.Equal(1, report.NoteCounts["CodeProofDeadline"]);
        Assert.DoesNotContain("CodeProofDisabled", report.NoteCounts.Keys);
        Assert.Equal(ContractRuleKeys(baseline), ContractRuleKeys(result));
        Assert.Equal(baseReport.Alarms.Select(a => a.Alarm.AlarmSymbol), report.Alarms.Select(a => a.Alarm.AlarmSymbol));
        Assert.Equal(NetworkAiStatus.Ok, report.NetworkStatus[120]);
    }

    [Fact]
    public void CodeProofDeadlineDuringProofsKeepsTheRest()
    {
        var (source, contracts) = ValveProject();
        var (baseline, baseReport) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1");
        using var deadline = new System.Threading.CancellationTokenSource();
        var calls = 0;
        // 第一个程序段证明期间期限到了（证明器抛出取消）：之后的程序段不再证明，合并照常完成
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1", null, null,
            prove: (_, _) => { calls++; deadline.Cancel(); throw new OperationCanceledException(deadline.Token); },
            proofDeadline: deadline.Token);
        Assert.Equal(1, calls);
        Assert.Equal(1, report.NoteCounts["CodeProofDeadline"]);
        Assert.Equal(ContractRuleKeys(baseline), ContractRuleKeys(result));
        Assert.Equal(baseReport.Alarms.Select(a => a.Alarm.AlarmSymbol), report.Alarms.Select(a => a.Alarm.AlarmSymbol));
    }

    [Fact]
    public void CallerCancellationStillPropagatesWithAProofDeadline()
    {
        var (source, contracts) = ValveProject();
        using var caller = new System.Threading.CancellationTokenSource();
        caller.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1", null, null, ct: caller.Token, proofDeadline: caller.Token));
    }

    [Fact]
    public void MergeStopsWhenCancelled()
    {
        var (source, contracts) = ValveProject();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UnderstandingRuleSource.Merge(source, Empty(source), contracts, "m1", null, null, ct: cts.Token));
    }

    [Fact]
    public void NetworkRuleOnOutputsNeedsAllOutputAreaWritesResolved()
    {
        // 别处的 STL 源码以寄存器间接写输出区：Q 区写入算不出范围
        var (source, unit) = Interlock(new PipelineUnit(4, "OB4", 230, "STL", "      T QB[AR1,P#0.0]", "", [], 1, "", "OB"));
        var ids = UnitTranslator.Translate(unit, Tags).Statements.Select(s => s.Id).ToArray();
        var outcomes = new Dictionary<long, NetworkAiOutcome>
        {
            [200] = Ok(200, "OB1", 1, new AiNetworkConstraint("REQUIRES_0_WHEN_FALSE", [new("A", "Source"), new("Q1", "Target")], false, null, ids))
        };
        var (result, report) = UnderstandingRuleSource.Merge(source, Empty(source), NoContracts, "m1", outcomes);
        Assert.DoesNotContain(result.Units.Single(u => u.LogicUnitId == 200).Rules, r => r.Proof == UnderstandingProofs.AiNetwork);
        Assert.Equal(1, report.NoteCounts["NetworkUnresolvedQWrite"]);
    }

    [Fact]
    public void ActivationPolicyIsTheLastGateForNetworkRules()   // 形状守卫：只有两个 %Q 的互斥/同步与 %Q 门控判故障
    {
        PipelineRule Rule(string op, params (string Name, string Role)[] s) =>
            new PipelineRule(op, s.Select(x => new PipelineSignal(x.Name, x.Role)).ToArray(), "") { Proof = UnderstandingProofs.AiNetwork };
        string Status(PipelineRule r) => Original353ActivationPolicy.Classify(r, Tags).Status;
        Assert.Equal("Active", Status(Rule("AT_MOST_ONE", ("Q1", "Member"), ("Q2", "Member"))));
        Assert.Equal("Active", Status(Rule("REQUIRES_0_WHEN_FALSE", ("K", "Source"), ("Q1", "Target"))));
        Assert.Equal("Unknown", Status(Rule("REQUIRES_0_WHEN_FALSE", ("A", "Source"), ("Q1", "Target"))));   // %I 作 Source 不判（审查 I-2）
        Assert.Equal("Unknown", Status(Rule("SAME_STATE", ("Q1", "Member"), ("A", "Member"))));
        Assert.Equal("Unknown", Status(Rule("MUST_BE_FALSE", ("Q1", "Target"))));
        Assert.Equal("Unknown", Status(Rule("EXACTLY_ONE", ("Q1", "Member"), ("Q2", "Member"))));
        Assert.Equal("RequiresRuntimeGuard", Status(Rule("SAME_STATE", ("Q1", "Member"), ("Q2", "Member")) with { Applicability = "AfterSettling" }));
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(0, 3)]
    [InlineData(-2, 3)]
    [InlineData(5, 5)]
    public void NetworkParallelismDefaultsToThree(int? configured, int expected) =>
        Assert.Equal(expected, UnderstandingPublishStep.NetworkAiParallelism(configured));
}
