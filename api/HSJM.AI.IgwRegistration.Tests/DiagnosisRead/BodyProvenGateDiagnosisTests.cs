using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.DiagnosisRead.DiagnosisAppHarness;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>FB 程序体证明的急停门控：Source 是 DB 变量，经精确读补值；判为违反时二次读取复核。</summary>
public class BodyProvenGateDiagnosisTests
{
    private const long Plc1 = 884001;

    static Func<IReadOnlyCollection<PLC_ExactReadRequest>, IReadOnlyList<PLC_ExactReadResult>> Values(
        Dictionary<string, object> values) =>
        requests => requests.Select(r => values.TryGetValue(r.RequestKey, out var v)
            ? new PLC_ExactReadResult(r.RequestKey, r.BlockName, r.VariablePath, r.RequestKey, v, PLC_ExactReadQuality.Good, null, null)
            : new PLC_ExactReadResult(r.RequestKey, r.BlockName, r.VariablePath, r.RequestKey, null, PLC_ExactReadQuality.Bad, null, "Bad"))
            .ToArray();

    /// <summary>生产映射器写入的门控证明标记（ValidStates.Proof = FbContract:Gate）。</summary>
    static string GateProofStates(string proof = "FbContract:Gate") =>
        System.Text.Json.JsonSerializer.Serialize(new { Generator = "test", Proof = proof });

    static DiagnosisAppHarness Gate(string source = "XMLV2", string? proof = "FbContract:Gate")
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddIoTag(Plc1, "T", "YV_W", "%Q1.0");
        h.AddIoTag(Plc1, "T", "YV_H", "%Q1.1");
        h.SetRead(Plc1, Good("T.YV_W", true), Good("T.YV_H", false));
        var rule = h.AddRule(Plc1, "REQUIRES_0_WHEN_FALSE", "Mode.Estop_联锁",
            ("Mode.Estop", "Source"), ("YV_W", "Target"), ("YV_H", "Target"));
        rule.Source = source;
        rule.ValidStates = proof == null ? null : GateProofStates(proof);
        return h;
    }

    [Fact]
    public async Task XmlV2GateWithoutBodyProofDoesNotReadDbSource()
    {
        // F9：只有带 FbContract:Gate 证明标记的门控才走 DB 源补读 / 复核
        var h = Gate(proof: null);
        h.ExactReads.Add(Values(new() { ["Mode.Estop"] = false }));
        var app = h.Build();
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        var outcome = await app.CheckConstraintsAsync([Plc1], live);

        Assert.Equal(0, h.ExactReadCalls);
        Assert.Equal(0, outcome.Violated);
    }

    [Fact]
    public async Task UnrequestableGateSourceNeverBorrowsASuffixMatch()
    {
        // F7：门控源不在本 PLC 点表、名字里没有 '.'（构造不出 DB 精确读请求），而本 PLC 另有一个
        // 后缀能对上的 Good 点：不能借它的值判「通过」，只能无法判定
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddIoTag(Plc1, "T", "YV_W", "%Q1.0");
        h.AddIoTag(Plc1, "T", "Safe_Estop", "%I9.0");
        h.SetRead(Plc1, Good("T.YV_W", true), Good("T.Safe_Estop", true));
        var rule = h.AddRule(Plc1, "REQUIRES_0_WHEN_FALSE", "Estop_联锁", ("Estop", "Source"), ("YV_W", "Target"));
        rule.Source = "XMLV2";
        rule.ValidStates = GateProofStates();
        var app = h.Build();
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        var outcome = await app.CheckConstraintsAsync([Plc1], live);

        Assert.Equal(0, outcome.Judged);
        var issue = Assert.Single(outcome.Undetermined);
        Assert.Contains("未参与诊断读取", issue.Reason); // NotRead（说明「门控源不可读取」写入调试日志）
        Assert.Contains("Estop", issue.Reason);
    }

    [Fact]
    public async Task DbSourceIsReadAndConfirmedViolationIsReported()
    {
        var h = Gate();
        h.ExactReads.Add(Values(new() { ["Mode.Estop"] = false }));
        h.ExactReads.Add(Values(new() { ["Mode.Estop"] = false, ["YV_W"] = true, ["YV_H"] = false }));
        var app = h.Build();
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        var outcome = await app.CheckConstraintsAsync([Plc1], live);

        Assert.Equal(1, outcome.Violated);
        Assert.Equal(2, h.ExactReadCalls);
    }

    [Fact]
    public async Task ViolationNotReproducedOnRecheckIsUndetermined()
    {
        var h = Gate();
        h.ExactReads.Add(Values(new() { ["Mode.Estop"] = false }));
        h.ExactReads.Add(Values(new() { ["Mode.Estop"] = true, ["YV_W"] = true, ["YV_H"] = false }));
        var app = h.Build();
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        var outcome = await app.CheckConstraintsAsync([Plc1], live);

        Assert.Equal(0, outcome.Violated);
        Assert.Contains(outcome.Undetermined, i => i.Reason.Contains("信号在变化"));
    }

    [Fact]
    public async Task UnreadableDbSourceIsUndeterminedNotPassed()
    {
        var h = Gate();
        h.ExactReads.Add(Values(new()));
        var app = h.Build();
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        var outcome = await app.CheckConstraintsAsync([Plc1], live);

        Assert.Equal(0, outcome.Violated);
        Assert.Single(outcome.Undetermined);
    }

    [Fact]
    public async Task NonXmlV2GateDoesNotReadDbSource()
    {
        var h = Gate(source: "AI");
        h.ExactReads.Add(Values(new() { ["Mode.Estop"] = false }));
        var app = h.Build();
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        var outcome = await app.CheckConstraintsAsync([Plc1], live);

        Assert.Equal(0, h.ExactReadCalls);
        Assert.Equal(0, outcome.Violated);
    }

    // ---- 评审修复：门控 DB 值不进 PlcLiveSignals（不让非门控规则后缀兜底借到）----

    private const long Plc2 = 884002;

    static DiagnosisAppHarness SuffixProbe(bool withGate)
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddPlc(Plc2, "PLC2");
        h.AddIoTag(Plc1, "T", "YV_W", "%Q1.0");
        h.AddIoTag(Plc1, "T", "YV_H", "%Q1.1");
        // 只在另一台 PLC 的点表里：全局粗过滤放行，但对 PLC1 不是已知点名 → PLC1 规则会走后缀兜底
        h.AddIoTag(Plc2, "T", "Estop", "%I9.0");
        h.SetRead(Plc1, Good("T.YV_W", true), Good("T.YV_H", false));
        var probe = h.AddRule(Plc1, "AT_MOST_ONE", "Estop_互斥探针", ("Estop", "Member"), ("YV_W", "Member"));
        probe.Source = "AI";
        if (withGate)
        {
            var gate = h.AddRule(Plc1, "REQUIRES_0_WHEN_FALSE", "Mode.Estop_联锁",
                ("Mode.Estop", "Source"), ("YV_H", "Target"));
            gate.Source = "XMLV2";
            gate.ValidStates = GateProofStates();
            h.ExactReads.Add(Values(new() { ["Mode.Estop"] = false }));
        }
        return h;
    }

    static async Task<(int Violated, string[] ProbeReasons, PlcLiveSignals Live)> RunProbe(bool withGate)
    {
        var h = SuffixProbe(withGate);
        var app = h.Build();
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);
        var outcome = await app.CheckConstraintsAsync([Plc1, Plc2], live);
        var probeViolated = outcome.Violations.Count(i => i.RelatedRule == "Estop_互斥探针");
        var probeReasons = outcome.Undetermined.Where(i => i.RelatedRule == "Estop_互斥探针").Select(i => i.Reason).ToArray();
        return (probeViolated, probeReasons, live[Plc1]);
    }

    [Fact]
    public async Task GateDbValueDoesNotLeakIntoNonGateSuffixMatching()
    {
        var without = await RunProbe(withGate: false);
        var with = await RunProbe(withGate: true);

        Assert.Equal(without.Violated, with.Violated);
        Assert.Equal(without.ProbeReasons, with.ProbeReasons);
        Assert.Single(with.ProbeReasons); // Estop 未采到 → 无法判定，而不是借 Mode.Estop=false 判通过
        Assert.False(with.Live.Values.ContainsKey("Mode.Estop"));
        Assert.False(with.Live.Statuses.ContainsKey("Mode.Estop"));
    }

    [Fact]
    public async Task RecheckWithUnreadableTargetKeepsItsOwnUndeterminedReason()
    {
        var h = Gate();
        h.ExactReads.Add(Values(new() { ["Mode.Estop"] = false }));
        h.ExactReads.Add(Values(new() { ["Mode.Estop"] = false, ["YV_H"] = false })); // 复核时 YV_W 读不到
        var app = h.Build();
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        var outcome = await app.CheckConstraintsAsync([Plc1], live);

        Assert.Equal(0, outcome.Violated);
        var issue = Assert.Single(outcome.Undetermined);
        Assert.DoesNotContain("信号在变化", issue.Reason);
        Assert.Contains("YV_W", issue.Reason);
    }

    [Theory]
    // F9：PLC_VariableAccessService 实际产出的错误码逐个显式映射
    [InlineData("PlcExactReadFailed", "CommFailure")]
    [InlineData("PlcExactProjectReadFailed", "CommFailure")]
    [InlineData("PlcExactMetadataReadFailed", "CommFailure")]
    [InlineData("PlcExactTagMissing", "SymbolNotFound")]
    [InlineData("PlcExactResponseMissing", "Missing")]
    [InlineData("PlcExactProjectMissing", "Missing")]
    [InlineData("PlcExactTagAmbiguous", "DeviceError")]
    [InlineData("IgwLedgerUnavailableAmbiguous", "DeviceError")]
    // 其余错误码仍按包含匹配兜底
    [InlineData("SymbolMissing", "SymbolNotFound")]
    [InlineData("BadNodeIdNotFound", "SymbolNotFound")]
    [InlineData("ReadTimeout", "Timeout")]
    [InlineData("BadCommFailure", "CommFailure")]
    [InlineData("GatewayUnavailable", "CommFailure")]
    [InlineData("DeviceOffline", "CommFailure")]
    [InlineData("Bad", "BadQuality")]
    [InlineData(null, "BadQuality")]
    public void ApplyMapsErrorCodeToReadStatus(string errorCode, string expectedCode)
    {
        var request = ConstraintDbSignals.Request("Mode.Estop");
        var results = new[]
        {
            new PLC_ExactReadResult("Mode.Estop", "Mode", "Estop", "Mode.Estop", null, PLC_ExactReadQuality.Bad, null, errorCode),
        };
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var statuses = new Dictionary<string, SignalUnknownReason>(StringComparer.OrdinalIgnoreCase);

        ConstraintDbSignals.Apply([request], results, values, statuses);

        Assert.Empty(values);
        Assert.Equal(expectedCode, statuses["Mode.Estop"].Code);
    }

    [Fact]
    public void ApplyMarksAbsentResultMissingAndKeepsGoodValue()
    {
        var requests = new[] { ConstraintDbSignals.Request("Mode.Estop"), ConstraintDbSignals.Request("Mode.Reset") };
        var results = new[]
        {
            new PLC_ExactReadResult("Mode.Estop", "Mode", "Estop", "Mode.Estop", true, PLC_ExactReadQuality.Good, null, null),
        };
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var statuses = new Dictionary<string, SignalUnknownReason>(StringComparer.OrdinalIgnoreCase);

        ConstraintDbSignals.Apply(requests, results, values, statuses);

        Assert.Equal(true, values["Mode.Estop"]);
        Assert.Equal("Missing", statuses["Mode.Reset"].Code);
    }
}
