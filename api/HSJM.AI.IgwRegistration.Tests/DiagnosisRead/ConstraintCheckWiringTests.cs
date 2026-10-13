using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.DiagnosisRead.DiagnosisAppHarness;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>D-1 Task 6：实时信号保留状态，约束按所属 PLC 三值判定。</summary>
public class ConstraintCheckWiringTests
{
    private const long Plc1 = 883001;
    private const long Plc2 = 883002;

    [Fact]
    public async Task PartlyManualProjectDoesNotJudgeOrdinaryInstantRules()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddIoTag(Plc1, "T", "A", "%Q1.0");
        h.AddIoTag(Plc1, "T", "B", "%Q1.1");
        h.SetRead(Plc1, Good("T.A", true), Good("T.B", true));
        var rule = h.AddRule(Plc1, "AT_MOST_ONE", "A_互斥", ("A", "Member"), ("B", "Member"));
        rule.Source = "XMLV2";
        rule.ValidStates = System.Text.Json.JsonSerializer.Serialize(new {
            ManualModeBindings = new[] { new Original353ManualModeBinding("DB_Mode", "General.ManualMod") }
        });
        var app = h.Build();
        app.ManualModeReadOverride = (id, _, _) => Task.FromResult(new Original353ManualModeRead(id, "Other", "") {
            ManualGroups = ["DB_Mode"]
        });
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);
        var outcome = await app.CheckConstraintsAsync([Plc1], live);
        Assert.Equal(0, outcome.Judged);
        Assert.Empty(outcome.Violations);
        Assert.Single(outcome.IoIssues);
    }

    [Fact]
    public async Task ModeConflictUsesQualityCheckedDbValuesWithoutIoTagsAndSurvivesEarlyReturn()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        var bindings = new[] { new Original353ManualModeBinding("DB_Mode", "General.ManualMod", "General.AutoMod", "General.AutoRunMod") };
        var rule = h.AddRule(Plc1, "AT_MOST_ONE", "自动运行与手动模式检查", ("DB_Mode.General.ManualMod", "Member"), ("DB_Mode.General.AutoRunMod", "Member"));
        rule.Source = "XMLV2_MODE";
        rule.ValidStates = System.Text.Json.JsonSerializer.Serialize(new {
            ManualModeBindings = bindings,
            ModeConflictSignals = new[] { new { Name = "DB_Mode.General.ManualMod" }, new { Name = "DB_Mode.General.AutoRunMod" } }
        });
        var app = h.Build();
        var values = new Dictionary<string, bool?> { ["DB_Mode.General.ManualMod"] = true,
            ["DB_Mode.General.AutoMod"] = false, ["DB_Mode.General.AutoRunMod"] = true };
        app.ManualModeReadOverride = (id, _, _) => Task.FromResult(new Original353ManualModeRead(id, "Conflict", "") { Values = values });
        var result = await app.CheckConstraintsAsync([Plc1], [], CancellationToken.None);
        Assert.Equal(1, result.Violated);
        Assert.Contains("自动运行与手动模式同时开启", Assert.Single(result.Violations).Reason);
        Assert.Empty(result.IoIssues);
        values["DB_Mode.General.AutoRunMod"] = null;
        result = await app.CheckConstraintsAsync([Plc1], [], CancellationToken.None);
        Assert.Equal(0, result.Judged);
        Assert.Single(result.Undetermined);
    }

    [Fact]
    public async Task ModePreflightReadsOnlyRequestedProjects()
    {
        var h = new DiagnosisAppHarness();
        foreach (var id in new[] { Plc1, Plc2 })
        {
            h.AddPlc(id, "PLC");
            var rule = h.AddRule(id, "AT_MOST_ONE", "test", ("A", "Member"), ("B", "Member"));
            rule.Source = "XMLV2";
            rule.ValidStates = System.Text.Json.JsonSerializer.Serialize(new { ManualModeBindings = new[] { new Original353ManualModeBinding("DB", "General.ManualMod") } });
        }
        var app = h.Build();
        var read = new List<long>();
        app.ManualModeReadOverride = (id, _, _) => { read.Add(id); return Task.FromResult(new Original353ManualModeRead(id, "Manual", "")); };
        var modes = await app.ReadOperatingModesAsync([Plc1]);
        Assert.Equal(Plc1, Assert.Single(read));
        Assert.Equal(Plc1, Assert.Single(modes).ProjectId);
    }

    [Fact]
    public async Task ConfirmedManualModeSkipsConstraintVerdictsAndReportsIoSuspicion()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddIoTag(Plc1, "T", "A", "%Q1.0");
        h.AddIoTag(Plc1, "T", "B", "%Q1.1");
        h.SetRead(Plc1, Good("T.A", true), Good("T.B", true));
        var rule = h.AddRule(Plc1, "AT_MOST_ONE", "A_互斥", ("A", "Member"), ("B", "Member"));
        rule.Source = "XMLV2";
        rule.ValidStates = System.Text.Json.JsonSerializer.Serialize(new {
            ManualModeBindings = new[] { new Original353ManualModeBinding("DB_Mode", "General.ManualMod") }
        });
        var app = h.Build();
        app.ManualModeReadOverride = (projectId, bindings, _) => Task.FromResult(
            new Original353ManualModeRead(projectId, "Manual", "SourceProvenModeBitsRead"));

        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);
        var outcome = await app.CheckConstraintsAsync([Plc1], live, CancellationToken.None);

        Assert.Equal(0, outcome.Judged);
        Assert.Empty(outcome.Violations);
        Assert.Empty(outcome.Undetermined);
        var issue = Assert.Single(outcome.IoIssues);
        Assert.Equal("IO_TAG", issue.Category);
        Assert.Contains("疑似手动模式触发", issue.Reason);
        var report = DiagnosticStrategyFactoryApp.BuildDirectDiagnosticReport("io_tag", [issue], [], outcome);
        Assert.Contains("疑似手动模式触发", report);
        Assert.Contains("工位: 整线", report);
        Assert.DoesNotContain("互斥约束违反", report);

        app.ManualModeReadOverride = (projectId, bindings, _) => Task.FromResult(
            new Original353ManualModeRead(projectId, "Unknown", "ModeBitsUnavailable"));
        var unknownMode = await app.CheckConstraintsAsync([Plc1], live, CancellationToken.None);
        Assert.Equal(1, unknownMode.Violated);
        Assert.Empty(unknownMode.IoIssues);
    }

    [Fact]
    public async Task TwoManualPlcsProduceOneLineLevelIoHint()
    {
        var h = new DiagnosisAppHarness();
        foreach (var projectId in new[] { Plc1, Plc2 })
        {
            h.AddPlc(projectId, projectId == Plc1 ? "PLC1" : "PLC2");
            var rule = h.AddRule(projectId, "AT_MOST_ONE", "A_互斥", ("A", "Member"), ("B", "Member"));
            rule.Source = "XMLV2";
            rule.ValidStates = System.Text.Json.JsonSerializer.Serialize(new {
                ManualModeBindings = new[] { new Original353ManualModeBinding("DB_Mode", "General.ManualMod") }
            });
        }
        var app = h.Build();
        app.ManualModeReadOverride = (projectId, _, _) => Task.FromResult(
            new Original353ManualModeRead(projectId, "Manual", "SourceProvenModeBitsRead"));
        var live = new Dictionary<long, PlcLiveSignals> {
            [Plc1] = PlcLiveSignals.Failed(Plc1, "PLC1", PLC_DiagnosticProjectStatus.ProjectUnavailable, "unused"),
            [Plc2] = PlcLiveSignals.Failed(Plc2, "PLC2", PLC_DiagnosticProjectStatus.ProjectUnavailable, "unused")
        };
        var outcome = await app.CheckConstraintsAsync([Plc1, Plc2], live, CancellationToken.None);
        var issue = Assert.Single(outcome.IoIssues);
        Assert.Contains("PLC1", issue.Reason);
        Assert.Contains("PLC2", issue.Reason);
        Assert.Equal(0, outcome.Judged);
    }

    [Fact]
    public async Task Live_signals_keep_good_values_and_non_good_statuses_per_plc()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddPlc(Plc2, "PLC2");
        h.SetRead(Plc1, Good("T.OP010_A", true), Bad("T.OP010_B", PLC_SignalReadStatus.SymbolNotFound, "BadNodeIdUnknown"));
        h.SetReadFailure(Plc2, new HttpRequestException("refused"));

        var live = await h.Build().GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        Assert.Equal(true, live[Plc1].Values["T.OP010_A"]);
        Assert.False(live[Plc1].Values.ContainsKey("T.OP010_B")); // 坏点不以值参与
        Assert.Equal("SymbolNotFound", live[Plc1].Statuses["T.OP010_B"].Code);
        Assert.Equal(1, live[Plc1].GoodCount);
        Assert.Equal(2, live[Plc1].TotalCount);
        Assert.Equal(PLC_DiagnosticProjectStatus.CommFailure, live[Plc2].ProjectStatus);
        Assert.Equal("HttpRequestException: refused", live[Plc2].ProjectError);
        Assert.Equal(0, live[Plc2].GoodCount);
    }

    [Fact]
    public void Resolve_prefers_exact_status_over_suffix_value_and_flags_non_boolean()
    {
        var live = PlcLiveSignals.FromReadResult(1, "PLC1", new PLC_DiagnosticReadResult
        {
            ProjectStatus = PLC_DiagnosticProjectStatus.Ok,
            Signals = new[]
            {
                Good("X.SIG", true),
                Bad("SIG", PLC_SignalReadStatus.WaitingForData),
                Good("T.NUM", "abc"),
                Good("T.ONE", "1"),
            },
        });
        var rule = ConstraintTestData.Rule("MUST_BE_FALSE");
        var good = new Dictionary<string, bool>();
        var unknown = new Dictionary<string, SignalUnknownReason>();

        live.Resolve(ConstraintTestData.Signals(rule, ("SIG", "Target"), ("NUM", "Target"), ("ONE", "Target"), ("GONE", "Target")),
            good, unknown);

        Assert.Equal("WaitingForData", unknown["SIG"].Code);
        Assert.Equal(SignalUnknownReason.NotBoolean, unknown["NUM"].Code);
        Assert.True(good["ONE"]);
        Assert.Equal("Missing", unknown["GONE"].Code);
    }

    [Fact]
    public async Task Each_rule_is_violated_passed_or_undetermined_using_its_own_plc()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddPlc(Plc2, "PLC2");
        foreach (var (tag, address) in new[] { ("OP010_A", "%I1.0"), ("OP010_B", "%I1.1"), ("OP010_C", "%I1.2"), ("OP010_D", "%I1.3"), ("OP010_F", "%I1.5") })
        {
            h.AddIoTag(Plc1, "T", tag, address);
        }
        h.AddIoTag(Plc2, "T", "OP020_E", "%I2.0");
        h.SetRead(Plc1,
            Good("T.OP010_A", true),
            Bad("T.OP010_B", PLC_SignalReadStatus.NotRegistered, "BadNodeIdUnknown(0x80340000) NotRegistered"),
            Good("T.OP010_C", true),
            Good("T.OP010_D", false));
        h.SetReadFailure(Plc2, new HttpRequestException("refused"));
        h.AddRule(Plc1, "IMPLIES", "R_IMPLIES_OP010", ("OP010_A", "Source"), ("OP010_B", "Target"));
        h.AddRule(Plc1, "AT_MOST_ONE", "R_MUTEX_OP010", ("OP010_A", "Member"), ("OP010_C", "Member"));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_OP010_D", ("OP010_D", "Target"));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_OP010_F", ("OP010_F", "Target"));
        h.AddRule(Plc2, "MUST_BE_FALSE", "R_MBF_OP020_E", ("OP020_E", "Target"));
        var app = h.Build();

        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);
        var outcome = await app.CheckConstraintsAsync(new List<long> { Plc1, Plc2 }, live, CancellationToken.None);

        Assert.Null(outcome.FailureMessage);
        Assert.Equal(1, outcome.Passed);
        Assert.Equal(1, outcome.Violated);
        Assert.Equal(2, outcome.Judged);
        Assert.Equal(3, outcome.UndeterminedCount);
        Assert.Equal(1, outcome.UndeterminedByBucket[UndeterminedBucket.NameMissing]);
        // OP010_F 是 Plc1 已登记的 IO 点，只是本次读结果里没有它（诊断口径未纳入）→ NotRead「其它」，
        // 不是网关/通讯的问题，不再计入通讯故障（fix round 2，2026-09-20）。Plc2 整体读取失败才是真通讯故障。
        Assert.Equal(1, outcome.UndeterminedByBucket[UndeterminedBucket.CommFailure]);
        Assert.Equal(1, outcome.UndeterminedByBucket[UndeterminedBucket.Other]);
        Assert.Equal("R_MUTEX_OP010", Assert.Single(outcome.Violations).RelatedRule);

        var implies = outcome.Undetermined.Single(i => i.RelatedRule == "R_IMPLIES_OP010");
        Assert.Equal("采集失败", implies.Category);
        Assert.Contains("OP010_B", implies.Reason);
        var detail = Assert.Single(implies.SignalDetails);
        Assert.Equal("点名不存在：IGW 未注册", detail.ReadStatus);
        Assert.Equal("%I1.1", detail.SignalAddress);
        Assert.Contains(outcome.Undetermined, i => i.RelatedRule == "R_MBF_OP010_F" && i.Reason.Contains("未参与诊断读取"));
        Assert.Contains(outcome.Undetermined, i => i.RelatedRule == "R_MBF_OP020_E" && i.Reason.Contains("通讯故障"));
        Assert.Equal("约束检查：已判定 2 条（违规 1 条），无法判定 3 条（点名不存在 1 / 通讯故障 1 / 首值未到 0 / 其它 1）",
            outcome.BuildSummaryLine());
    }

    [Fact]
    public async Task Rule_service_failure_is_reported_not_swallowed()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.RuleServiceFailure = new InvalidOperationException("db down");
        var app = h.Build();

        var outcome = await app.CheckConstraintsAsync(new List<long> { Plc1 },
            new Dictionary<long, PlcLiveSignals>(), CancellationToken.None);

        Assert.Equal("约束检查执行失败: InvalidOperationException: db down", outcome.FailureMessage);
        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public async Task Unrequested_cancellation_from_db_is_reported_as_failure()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.RuleServiceFailure = new TaskCanceledException("command timeout");
        var app = h.Build();

        var outcome = await app.CheckConstraintsAsync(new List<long> { Plc1 },
            new Dictionary<long, PlcLiveSignals>(), CancellationToken.None);

        Assert.Equal("约束检查执行失败: TaskCanceledException: command timeout", outcome.FailureMessage);
    }

    [Fact]
    public async Task Requested_cancellation_propagates()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Build().CheckConstraintsAsync(
            new List<long> { Plc1 }, new Dictionary<long, PlcLiveSignals>(), cts.Token));
    }

    [Fact]
    public async Task Mid_loop_failure_keeps_violations_consistent_with_counts()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddPlc(Plc2, "PLC2");
        h.AddIoTag(Plc1, "T", "OP010_A", "%I1.0");
        h.AddIoTag(Plc1, "T", "OP010_C", "%I1.2");
        h.AddIoTag(Plc2, "T", "OP020_E", "%I2.0");
        h.SetRead(Plc1, Good("T.OP010_A", true), Good("T.OP010_C", true));
        h.AddRule(Plc1, "AT_MOST_ONE", "R_MUTEX_OP010", ("OP010_A", "Member"), ("OP010_C", "Member"));
        h.AddRule(Plc2, "MUST_BE_FALSE", "R_MBF_OP020_E", ("OP020_E", "Target"));
        var app = h.Build();
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);
        live[Plc2] = null; // 第二条规则处理时抛出

        var outcome = await app.CheckConstraintsAsync(new List<long> { Plc1, Plc2 }, live, CancellationToken.None);

        Assert.StartsWith("约束检查执行失败: NullReferenceException", outcome.FailureMessage);
        Assert.Equal(1, outcome.Violated);
        Assert.Equal("R_MUTEX_OP010", Assert.Single(outcome.Violations).RelatedRule);
    }

    [Fact]
    public async Task Debug_log_write_failure_does_not_fail_the_check()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddIoTag(Plc1, "T", "OP010_A", "%I1.0");
        h.AddIoTag(Plc1, "T", "OP010_C", "%I1.2");
        h.SetRead(Plc1, Good("T.OP010_A", true), Good("T.OP010_C", true));
        h.AddRule(Plc1, "AT_MOST_ONE", "R_MUTEX_OP010", ("OP010_A", "Member"), ("OP010_C", "Member"));
        var app = h.Build();
        string fileName = null;
        app.ConstraintLogWriter = (name, _, _) =>
        {
            fileName = name;
            throw new IOException("file in use");
        };
        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        var outcome = await app.CheckConstraintsAsync(new List<long> { Plc1 }, live, CancellationToken.None);

        Assert.Null(outcome.FailureMessage);
        Assert.Equal(1, outcome.Violated);
        Assert.Single(outcome.Violations);
        Assert.Matches(@"^constraint_check_\d{8}_\d{6}_\d{3}_[0-9a-f]{8}\.log$", fileName);
    }

    private static (Dictionary<string, bool> Good, Dictionary<string, SignalUnknownReason> Unknown) ResolveOne(
        string ruleSignal, params PLC_DiagnosticSignal[] read)
    {
        var live = PlcLiveSignals.FromReadResult(1, "PLC1", new PLC_DiagnosticReadResult
        {
            ProjectStatus = PLC_DiagnosticProjectStatus.Ok,
            Signals = read,
        });
        var good = new Dictionary<string, bool>();
        var unknown = new Dictionary<string, SignalUnknownReason>();
        live.Resolve(ConstraintTestData.Signals(ConstraintTestData.Rule("MUST_BE_FALSE"), (ruleSignal, "Target")), good, unknown);
        return (good, unknown);
    }

    [Fact]
    public void Suffix_match_never_borrows_a_good_value_from_another_point()
    {
        // 规则 CYL01_W：真实点 T.CYL01_W 未注册；T.OP030_CYL01_W 是另一个 Good 点（"_X" 后缀也能匹配）
        var (good, unknown) = ResolveOne("CYL01_W",
            Good("T.OP030_CYL01_W", true),
            Bad("T.CYL01_W", PLC_SignalReadStatus.NotRegistered));

        Assert.False(good.ContainsKey("CYL01_W"));
        Assert.Equal("NotRegistered", unknown["CYL01_W"].Code);
    }

    [Fact]
    public void Same_suffix_tier_with_disagreeing_points_is_ambiguous()
    {
        var (good, unknown) = ResolveOne("CYL01_W",
            Good("A_CYL01_W", true),
            Good("B]CYL01_W", false));

        Assert.Empty(good);
        Assert.Equal("DeviceError", unknown["CYL01_W"].Code);
        Assert.StartsWith(PlcLiveSignals.AmbiguousSuffixDetail, unknown["CYL01_W"].Detail);
    }

    [Fact]
    public void Suffix_candidate_that_is_not_good_makes_the_tier_ambiguous()
    {
        var (good, unknown) = ResolveOne("CYL01_W",
            Good("A_CYL01_W", true),
            Bad("B_CYL01_W", PLC_SignalReadStatus.WaitingForData));

        Assert.Empty(good);
        Assert.Equal("DeviceError", unknown["CYL01_W"].Code);
    }

    [Fact]
    public void Bare_tag_name_is_matched_exactly_via_old_tag_name()
    {
        // 精确裸点名优先于后缀：X.OP030_CYL01_W 的后缀也能匹配 CYL01_W，但不参与
        var (good, unknown) = ResolveOne("CYL01_W",
            Good("X.OP030_CYL01_W", true, "OP030_CYL01_W"),
            Good("T.CYL01_W", false, "CYL01_W"));

        Assert.Empty(unknown);
        Assert.False(good["CYL01_W"]);
    }

    [Fact]
    public void Bare_tag_name_in_two_tables_agreeing_is_good()
    {
        var (good, unknown) = ResolveOne("SIG",
            Good("T1.SIG", true, "SIG"),
            Good("T2.SIG", "1", "SIG"));

        Assert.Empty(unknown);
        Assert.True(good["SIG"]);
    }

    [Fact]
    public void Bare_tag_name_in_two_tables_disagreeing_is_ambiguous()
    {
        var (good, unknown) = ResolveOne("SIG",
            Good("T1.SIG", true, "SIG"),
            Good("T2.SIG", false, "SIG"));

        Assert.Empty(good);
        Assert.Equal("DeviceError", unknown["SIG"].Code);
        Assert.Equal(PlcLiveSignals.AmbiguousTagNameDetail + "：T1.SIG, T2.SIG", unknown["SIG"].Detail);
    }

    [Fact]
    public void Bare_tag_name_in_two_tables_with_one_bad_is_ambiguous()
    {
        var (good, unknown) = ResolveOne("SIG",
            Good("T1.SIG", true, "SIG"),
            Bad("T2.SIG", PLC_SignalReadStatus.NotRegistered, oldTagName: "SIG"));

        Assert.Empty(good);
        Assert.Equal("DeviceError", unknown["SIG"].Code);
    }

    // ---- D-1 最终评审修复 I-1：大小写不同的重名键 ----

    [Fact]
    public void Case_variant_keys_good_and_bad_are_device_error_not_inherited_value()
    {
        var (good, unknown) = ResolveOne("T.Flag",
            Good("T.flag", true),
            Bad("T.Flag", PLC_SignalReadStatus.NotRegistered));

        Assert.Empty(good);
        Assert.Equal("DeviceError", unknown["T.Flag"].Code);
        Assert.Contains(PlcLiveSignals.CaseVariantCollisionDetail, unknown["T.Flag"].Detail);
    }

    [Fact]
    public void Case_variant_keys_bad_then_good_is_device_error()
    {
        var (good, unknown) = ResolveOne("T.flag",
            Bad("T.Flag", PLC_SignalReadStatus.NotRegistered),
            Good("T.flag", true));

        Assert.Empty(good);
        Assert.Equal("DeviceError", unknown["T.flag"].Code);
        Assert.Contains(PlcLiveSignals.CaseVariantCollisionDetail, unknown["T.flag"].Detail);
    }

    [Fact]
    public void Case_variant_keys_both_good_with_different_values_are_device_error()
    {
        var (good, unknown) = ResolveOne("T.flag",
            Good("T.flag", true),
            Good("T.Flag", false));

        Assert.Empty(good);
        Assert.Equal("DeviceError", unknown["T.flag"].Code);
    }

    [Fact]
    public void Case_variant_keys_both_good_with_equal_values_keep_the_value()
    {
        var (good, unknown) = ResolveOne("T.FLAG",
            Good("T.flag", true),
            Good("T.Flag", true));

        Assert.Empty(unknown);
        Assert.True(good["T.FLAG"]);
    }

    [Fact]
    public void Case_variant_keys_resolved_via_bare_tag_name_are_device_error()
    {
        var (good, unknown) = ResolveOne("flag",
            Good("T.flag", true, "flag"),
            Bad("T.Flag", PLC_SignalReadStatus.NotRegistered, oldTagName: "Flag"));

        Assert.Empty(good);
        Assert.Equal("DeviceError", unknown["flag"].Code);
        Assert.Contains(PlcLiveSignals.CaseVariantCollisionDetail, unknown["flag"].Detail);
    }

    // ---- Minor 2：本 PLC 已知 IO 点名但未参与读取 → NotRead（不是网关没返回），不走后缀兜底 ----
    // fix round 2（2026-09-20）：原先复用 Missing 会让「未参与诊断读取」被计入「通讯故障」桶、报告文案
    // 也写成「网关未返回」，与它自己的 Detail「该点未参与诊断读取」自相矛盾。改用专门的 NotRead 状态，
    // 归「其它」桶，文案如实说明。

    [Fact]
    public void Known_io_tag_name_that_was_not_read_is_notread_and_never_borrows_suffix_value()
    {
        var live = PlcLiveSignals.FromReadResult(1, "PLC1", new PLC_DiagnosticReadResult
        {
            ProjectStatus = PLC_DiagnosticProjectStatus.Ok,
            Signals = new[] { Good("T.OP030_CYL01_W", true, "OP030_CYL01_W") },
        });
        var good = new Dictionary<string, bool>();
        var unknown = new Dictionary<string, SignalUnknownReason>();

        live.Resolve(ConstraintTestData.Signals(ConstraintTestData.Rule("MUST_BE_FALSE"), ("CYL01_W", "Target")),
            good, unknown, name => string.Equals(name, "CYL01_W", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(good);
        Assert.Equal("NotRead", unknown["CYL01_W"].Code);
        Assert.Equal(PlcLiveSignals.NotReadForDiagnosisDetail, unknown["CYL01_W"].Detail);
        Assert.Equal(UndeterminedBucket.Other, unknown["CYL01_W"].Bucket);
    }

    [Fact]
    public void Name_not_in_io_table_still_uses_suffix_fallback()
    {
        var live = PlcLiveSignals.FromReadResult(1, "PLC1", new PLC_DiagnosticReadResult
        {
            ProjectStatus = PLC_DiagnosticProjectStatus.Ok,
            Signals = new[] { Good("T.OP030_CYL01_W", true, "OP030_CYL01_W") },
        });
        var good = new Dictionary<string, bool>();
        var unknown = new Dictionary<string, SignalUnknownReason>();

        live.Resolve(ConstraintTestData.Signals(ConstraintTestData.Rule("MUST_BE_FALSE"), ("CYL01_W", "Target")),
            good, unknown, _ => false);

        Assert.Empty(unknown);
        Assert.True(good["CYL01_W"]);
    }

    [Fact]
    public void Known_io_tag_name_on_failed_plc_keeps_plc_level_reason()
    {
        var live = PlcLiveSignals.Failed(1, "PLC1", PLC_DiagnosticProjectStatus.CommFailure, "refused");
        var good = new Dictionary<string, bool>();
        var unknown = new Dictionary<string, SignalUnknownReason>();

        live.Resolve(ConstraintTestData.Signals(ConstraintTestData.Rule("MUST_BE_FALSE"), ("CYL01_W", "Target")),
            good, unknown, _ => true);

        Assert.Equal("CommFailure", unknown["CYL01_W"].Code);
    }

    [Fact]
    public async Task Constraint_check_treats_unread_known_io_tag_as_notread_not_suffix_match()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddIoTag(Plc1, "T", "CYL01_W", "%I1.0");
        h.AddIoTag(Plc1, "T", "OP030_CYL01_W", "%I1.1");
        // CYL01_W 未参与读取（如 DataType 被诊断口径过滤），读结果里只有 OP030_CYL01_W
        h.SetRead(Plc1, Good("T.OP030_CYL01_W", true, "OP030_CYL01_W"));
        h.AddRule(Plc1, "MUST_BE_FALSE", "R_MBF_CYL01_W", ("CYL01_W", "Target"));
        var app = h.Build();

        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);
        var outcome = await app.CheckConstraintsAsync(new List<long> { Plc1 }, live, CancellationToken.None);

        Assert.Null(outcome.FailureMessage);
        Assert.Equal(0, outcome.Violated);
        Assert.Equal(1, outcome.UndeterminedCount);
        var issue = Assert.Single(outcome.Undetermined);
        Assert.Contains("未参与诊断读取", issue.Reason); // NotRead，不再误标「网关未返回」
        Assert.DoesNotContain("网关未返回", issue.Reason);
        Assert.Contains("CYL01_W", issue.Reason);
        // 归「其它」桶而非「通讯故障」——这个点根本没被发去读，不是网关/通讯的问题
        Assert.Equal(1, outcome.UndeterminedByBucket[UndeterminedBucket.Other]);
        Assert.Equal(0, outcome.UndeterminedByBucket[UndeterminedBucket.CommFailure]);
    }

    // ---- I-3：大小写不同 / 空点名的 IO Tag 不让整个约束检查失败 ----

    [Fact]
    public async Task Case_variant_and_null_io_tag_names_do_not_fail_the_check()
    {
        var h = new DiagnosisAppHarness();
        h.AddPlc(Plc1, "PLC1");
        h.AddIoTag(Plc1, "T", "OP010_A", "%I1.0");
        h.AddIoTag(Plc1, "T2", "op010_a", "%I9.0");
        h.AddIoTag(Plc1, "T", null, "%I9.1");
        h.AddIoTag(Plc1, "T", "", "%I9.2");
        h.AddIoTag(Plc1, "T", "OP010_C", "%I1.2");
        h.SetRead(Plc1, Good("T.OP010_A", true, "OP010_A"), Good("T.OP010_C", true, "OP010_C"));
        h.AddRule(Plc1, "AT_MOST_ONE", "R_MUTEX_OP010", ("OP010_A", "Member"), ("OP010_C", "Member"));
        var app = h.Build();

        var live = await app.GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);
        var outcome = await app.CheckConstraintsAsync(new List<long> { Plc1 }, live, CancellationToken.None);

        Assert.Null(outcome.FailureMessage);
        Assert.Equal(1, outcome.Violated);
    }

    // ---- Minor 4：多 PLC 并行读取（有界并发），结果按 PLC 归位 ----

    [Fact]
    public async Task Plcs_are_read_in_parallel_and_results_map_to_their_own_project()
    {
        var h = new DiagnosisAppHarness();
        var ids = new[] { 884001L, 884002L, 884003L };
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var id in ids)
        {
            h.AddPlc(id, "PLC" + id);
            var pid = id;
            h.Reads[pid] = async () =>
            {
                if (Interlocked.Increment(ref started) == ids.Length)
                {
                    allStarted.TrySetResult();
                }
                // 顺序读取时第一个 PLC 永远等不到其它 PLC 开始 → 超时抛出
                if (await Task.WhenAny(allStarted.Task, Task.Delay(5000)) != allStarted.Task)
                {
                    throw new TimeoutException("not parallel");
                }
                if (pid == 884002L)
                {
                    throw new HttpRequestException("refused");
                }
                return new PLC_DiagnosticReadResult
                {
                    ProjectId = pid,
                    ProjectStatus = PLC_DiagnosticProjectStatus.Ok,
                    Signals = new[] { Good("T.SIG_" + pid, true) },
                };
            };
        }

        var live = await h.Build().GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        Assert.Equal(3, live.Count);
        Assert.Equal(true, Assert.Single(live[884001L].Values, kv => kv.Key == "T.SIG_884001").Value);
        Assert.Single(live[884001L].Values);
        Assert.Equal("PLC884001", live[884001L].PlcName);
        Assert.Equal(PLC_DiagnosticProjectStatus.CommFailure, live[884002L].ProjectStatus);
        Assert.Equal("HttpRequestException: refused", live[884002L].ProjectError);
        Assert.Equal(true, Assert.Single(live[884003L].Values, kv => kv.Key == "T.SIG_884003").Value);
        Assert.Single(live[884003L].Values);
    }

    [Fact]
    public async Task Parallel_plc_reads_are_bounded()
    {
        var h = new DiagnosisAppHarness();
        var current = 0;
        var max = 0;
        for (var i = 0; i < 10; i++)
        {
            var pid = 885000L + i;
            h.AddPlc(pid, "PLC" + pid);
            h.Reads[pid] = async () =>
            {
                var now = Interlocked.Increment(ref current);
                int seen;
                while ((seen = Volatile.Read(ref max)) < now && Interlocked.CompareExchange(ref max, now, seen) != seen)
                {
                }
                await Task.Delay(50);
                Interlocked.Decrement(ref current);
                return new PLC_DiagnosticReadResult { ProjectId = pid, ProjectStatus = PLC_DiagnosticProjectStatus.Ok };
            };
        }

        var live = await h.Build().GetLiveSignalsByPlcAsync(h.Projects, CancellationToken.None);

        Assert.Equal(10, live.Count);
        Assert.InRange(max, 2, DiagnosticStrategyFactoryApp.LiveSignalReadParallelism);
    }

    [Fact]
    public async Task Requested_cancellation_during_parallel_reads_propagates()
    {
        var h = new DiagnosisAppHarness();
        using var cts = new CancellationTokenSource();
        for (var i = 0; i < 3; i++)
        {
            var pid = 886000L + i;
            h.AddPlc(pid, "PLC" + pid);
            h.Reads[pid] = () =>
            {
                cts.Cancel();
                return Task.FromCanceled<PLC_DiagnosticReadResult>(cts.Token);
            };
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Build().GetLiveSignalsByPlcAsync(h.Projects, cts.Token));
    }
}
