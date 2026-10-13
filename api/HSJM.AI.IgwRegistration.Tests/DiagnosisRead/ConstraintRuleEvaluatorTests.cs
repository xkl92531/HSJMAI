using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.IDbService;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.DiagnosisRead.ConstraintTestData;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>D-1 Task 5：三值判定。每个 operator 至少覆盖"未知不影响结论→判定"与"未知决定结论→无法判定"。</summary>
public class ConstraintRuleEvaluatorTests
{
    private static (string, string) M(string name) => (name, "Member");
    private static (string, string) S(string name) => (name, "Source");
    private static (string, string) T(string name) => (name, "Target");
    private static (string, bool) V(string name, bool value) => (name, value);

    private static RuleEvaluation Evaluate(string op, (string, string)[] signals, (string, bool)[] good,
        params (string Name, PLC_SignalReadStatus Status)[] unknown)
    {
        var rule = Rule(op, "R_" + op);
        var unknownMap = unknown.ToDictionary(u => u.Name, u => SignalUnknownReason.FromStatus(u.Status, "test"));
        return ConstraintRuleEvaluator.Evaluate(rule, Signals(rule, signals), Values(good), unknownMap, NoTags);
    }

    private const PLC_SignalReadStatus Comm = PLC_SignalReadStatus.CommFailure;

    [Fact]
    public void At_most_one_two_known_true_is_violated_regardless_of_unknown() =>
        Assert.Equal(RuleVerdict.Violated, Evaluate("AT_MOST_ONE", new[] { M("A"), M("B"), M("C") }, new[] { V("A", true), V("B", true) }, ("C", Comm)).Verdict);

    [Fact]
    public void At_most_one_unknown_deciding_is_undetermined() =>
        Assert.Equal(RuleVerdict.Undetermined, Evaluate("AT_MOST_ONE", new[] { M("A"), M("B") }, new[] { V("A", true) }, ("B", Comm)).Verdict);

    [Fact]
    public void At_most_one_single_unknown_with_other_false_is_passed() =>
        Assert.Equal(RuleVerdict.Passed, Evaluate("AT_MOST_ONE", new[] { M("A"), M("B") }, new[] { V("A", false) }, ("B", Comm)).Verdict);

    [Fact]
    public void Exactly_one_two_known_true_is_violated() =>
        Assert.Equal(RuleVerdict.Violated, Evaluate("EXACTLY_ONE", new[] { M("A"), M("B"), M("C") }, new[] { V("A", true), V("B", true) }, ("C", Comm)).Verdict);

    [Fact]
    public void Exactly_one_unknown_deciding_is_undetermined() =>
        Assert.Equal(RuleVerdict.Undetermined, Evaluate("EXACTLY_ONE", new[] { M("A"), M("B") }, new[] { V("A", true) }, ("B", Comm)).Verdict);

    [Fact]
    public void All_true_inactive_source_is_passed() =>
        Assert.Equal(RuleVerdict.Passed, Evaluate("ALL_TRUE", new[] { S("S"), M("M") }, new[] { V("S", false) }, ("M", Comm)).Verdict);

    [Fact]
    public void All_true_known_false_member_is_violated() =>
        Assert.Equal(RuleVerdict.Violated, Evaluate("ALL_TRUE", new[] { S("S"), M("M1"), M("M2") }, new[] { V("S", true), V("M1", false) }, ("M2", Comm)).Verdict);

    [Fact]
    public void All_true_unknown_member_is_undetermined() =>
        Assert.Equal(RuleVerdict.Undetermined, Evaluate("ALL_TRUE", new[] { S("S"), M("M") }, new[] { V("S", true) }, ("M", Comm)).Verdict);

    [Fact]
    public void Requires_0_known_true_target_is_violated() =>
        Assert.Equal(RuleVerdict.Violated, Evaluate("REQUIRES_0", new[] { T("T1"), T("T2") }, new[] { V("T1", true) }, ("T2", Comm)).Verdict);

    [Fact]
    public void Requires_0_unknown_target_is_undetermined() =>
        Assert.Equal(RuleVerdict.Undetermined, Evaluate("REQUIRES_0", new[] { T("T1"), T("T2") }, new[] { V("T1", false) }, ("T2", Comm)).Verdict);

    [Fact]
    public void Requires_0_unknown_source_with_false_target_is_passed() =>
        Assert.Equal(RuleVerdict.Passed, Evaluate("REQUIRES_0", new[] { S("S"), T("T") }, new[] { V("T", false) }, ("S", Comm)).Verdict);

    [Fact]
    public void Requires_0_when_false_unknown_source_with_false_target_is_passed() =>
        Assert.Equal(RuleVerdict.Passed, Evaluate("REQUIRES_0_WHEN_FALSE", new[] { S("S1"), T("T") }, new[] { V("T", false) }, ("S1", Comm)).Verdict);

    [Fact]
    public void Requires_0_when_false_unknown_source_with_true_target_is_undetermined() =>
        Assert.Equal(RuleVerdict.Undetermined, Evaluate("REQUIRES_0_WHEN_FALSE", new[] { S("S1"), T("T") }, new[] { V("T", true) }, ("S1", Comm)).Verdict);

    [Fact]
    public void Requires_0_when_false_known_false_source_is_violated() =>
        Assert.Equal(RuleVerdict.Violated, Evaluate("REQUIRES_0_WHEN_FALSE", new[] { S("S1"), S("S2"), T("T") }, new[] { V("S1", false), V("T", true) }, ("S2", Comm)).Verdict);

    [Fact]
    public void Must_be_false_unknown_target_is_undetermined() =>
        Assert.Equal(RuleVerdict.Undetermined, Evaluate("MUST_BE_FALSE", new[] { T("T") }, Array.Empty<(string, bool)>(), ("T", Comm)).Verdict);

    [Fact]
    public void Must_be_false_known_true_is_violated_and_unknown_is_marked()
    {
        var result = Evaluate("MUST_BE_FALSE", new[] { T("T1"), T("T2") }, new[] { V("T1", true) }, ("T2", Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        var t2 = result.Issue.SignalDetails.Single(d => d.SignalName == "T2");
        Assert.Equal("通讯故障", t2.ReadStatus);
        Assert.False(t2.CurrentValue);
        Assert.Null(result.Issue.SignalDetails.Single(d => d.SignalName == "T1").ReadStatus);
    }

    [Fact]
    public void Implies_unknown_source_with_true_target_is_passed() =>
        Assert.Equal(RuleVerdict.Passed, Evaluate("IMPLIES", new[] { S("S"), T("T") }, new[] { V("T", true) }, ("S", Comm)).Verdict);

    [Fact]
    public void Implies_true_source_with_unknown_target_is_undetermined() =>
        Assert.Equal(RuleVerdict.Undetermined, Evaluate("IMPLIES", new[] { S("S"), T("T") }, new[] { V("S", true) }, ("T", Comm)).Verdict);

    [Fact]
    public void Implies_false_source_with_unknown_target_is_passed() =>
        Assert.Equal(RuleVerdict.Passed, Evaluate("IMPLIES", new[] { S("S"), T("T") }, new[] { V("S", false) }, ("T", Comm)).Verdict);

    [Fact]
    public void Same_state_known_mismatch_is_violated() =>
        Assert.Equal(RuleVerdict.Violated, Evaluate("SAME_STATE", new[] { M("M1"), M("M2"), M("M3") }, new[] { V("M1", true), V("M2", false) }, ("M3", Comm)).Verdict);

    [Fact]
    public void Same_state_unknown_deciding_is_undetermined() =>
        Assert.Equal(RuleVerdict.Undetermined, Evaluate("SAME_STATE", new[] { M("M1"), M("M2") }, new[] { V("M1", true) }, ("M2", Comm)).Verdict);

    [Fact]
    public void Chain_order_known_violation_is_violated() =>
        Assert.Equal(RuleVerdict.Violated, Evaluate("CHAIN_ORDER", new[] { M("A"), M("B"), M("C") }, new[] { V("A", false), V("B", true) }, ("C", Comm)).Verdict);

    [Fact]
    public void Chain_order_unknown_predecessor_is_undetermined() =>
        Assert.Equal(RuleVerdict.Undetermined, Evaluate("CHAIN_ORDER", new[] { M("A"), M("B") }, new[] { V("B", true) }, ("A", Comm)).Verdict);

    [Fact]
    public void Chain_order_true_predecessor_with_unknown_successor_is_passed() =>
        Assert.Equal(RuleVerdict.Passed, Evaluate("CHAIN_ORDER", new[] { M("A"), M("B") }, new[] { V("A", true) }, ("B", Comm)).Verdict);

    [Fact]
    public void Custom_operator_is_undetermined_as_unsupported()
    {
        var result = Evaluate("CUSTOM", new[] { M("A") }, new[] { V("A", true) });

        Assert.Equal(RuleVerdict.Undetermined, result.Verdict);
        Assert.Equal(UndeterminedBucket.Other, result.Bucket);
        Assert.Contains("不支持的约束类型 CUSTOM", result.Issue.Reason);
    }

    [Fact]
    public void More_than_eight_unknown_signals_is_undetermined()
    {
        var names = Enumerable.Range(0, 9).Select(i => "T" + i).ToArray();

        var result = Evaluate("MUST_BE_FALSE", names.Select(T).ToArray(), Array.Empty<(string, bool)>(),
            names.Select(n => (n, PLC_SignalReadStatus.WaitingForData)).ToArray());

        Assert.Equal(RuleVerdict.Undetermined, result.Verdict);
        Assert.Contains("未采到信号过多", result.Issue.Reason);
        Assert.Equal(UndeterminedBucket.WaitingForData, result.Bucket);
    }

    [Fact]
    public void Undetermined_issue_carries_reason_bucket_and_read_status()
    {
        var result = Evaluate("IMPLIES", new[] { S("OP010_A"), T("OP010_B") }, new[] { V("OP010_A", true) },
            ("OP010_B", PLC_SignalReadStatus.NotRegistered));

        Assert.Equal(UndeterminedBucket.NameMissing, result.Bucket);
        Assert.Equal("采集失败", result.Issue.Category);
        Assert.Equal("R_IMPLIES", result.Issue.RelatedRule);
        Assert.Equal("无法判定：点名不存在：IGW 未注册，未采到 OP010_B（R_IMPLIES）", result.Issue.Reason);
        Assert.Equal("点名不存在：IGW 未注册", Assert.Single(result.Issue.SignalDetails).ReadStatus);
    }

    // 注：Theory 参数类型用 object 而非 UndeterminedBucket——该枚举按接口要求是 internal，
    // 若直接作为 public [Theory] 方法的形参类型会触发 CS0051（可访问性不一致，与 InternalsVisibleTo 无关，
    // 纯语法级检查）；方法体内转型回枚举比较，语义不变。
    [Theory]
    [InlineData(PLC_SignalReadStatus.SymbolNotFound, UndeterminedBucket.NameMissing)]
    [InlineData(PLC_SignalReadStatus.NotRegistered, UndeterminedBucket.NameMissing)]
    [InlineData(PLC_SignalReadStatus.CommFailure, UndeterminedBucket.CommFailure)]
    [InlineData(PLC_SignalReadStatus.Timeout, UndeterminedBucket.CommFailure)]
    [InlineData(PLC_SignalReadStatus.Missing, UndeterminedBucket.CommFailure)]
    [InlineData(PLC_SignalReadStatus.WaitingForData, UndeterminedBucket.WaitingForData)]
    [InlineData(PLC_SignalReadStatus.AccessDenied, UndeterminedBucket.Other)]
    [InlineData(PLC_SignalReadStatus.DeviceError, UndeterminedBucket.Other)]
    [InlineData(PLC_SignalReadStatus.BadQuality, UndeterminedBucket.Other)]
    public void Reason_buckets(PLC_SignalReadStatus status, object bucket) =>
        Assert.Equal((UndeterminedBucket)bucket, SignalUnknownReason.FromStatus(status, "x").Bucket);

    [Fact]
    public void Project_unavailable_counts_as_comm_failure() =>
        Assert.Equal(UndeterminedBucket.CommFailure, new SignalUnknownReason(SignalUnknownReason.ProjectUnavailable, "x").Bucket);

    // ---- Fix round 1 ----

    private const string UnknownLine = "未采到信号（其取值不影响本结论）：";

    /// <summary>
    /// 违规 issue 的任何文字（去掉末尾「未采到」说明行后）都不得提到未知信号；更不得出现「未知名=True/False」。
    /// 名称按「前后不是标识符/./-/$ 字符」的边界匹配，避免 FZ91 命中 OP010-FZ91、General.X 命中 DB.General.X。
    /// </summary>
    private static void AssertNeverStatesUnknown(DiagnosticIssue issue, string unknownName, string reasonLabel = "通讯故障")
    {
        var entry = unknownName + "(" + reasonLabel + ")";
        foreach (var text in new[] { issue.Reason, issue.JudgmentBasis })
        {
            var cut = text.IndexOf(UnknownLine, StringComparison.Ordinal);
            Assert.True(cut >= 0, text);
            Assert.Contains(entry, text.Substring(cut + UnknownLine.Length));
        }

        var escaped = System.Text.RegularExpressions.Regex.Escape(unknownName);
        var token = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_.\-$])" + escaped + @"(?![A-Za-z0-9_.\-$\[])");
        var valued = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_.\-$])" + escaped + @"\s*=\s*(?:True|False)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var text in new[] { issue.Reason, issue.Status, issue.Variable, issue.JudgmentBasis, issue.Suggestion, issue.ExpectedValue })
        {
            Assert.DoesNotMatch(valued, text ?? string.Empty);
            Assert.DoesNotMatch(token, StripUnknownLine(text));
        }
        Assert.Equal(reasonLabel, issue.SignalDetails.Single(d => d.SignalName == unknownName).ReadStatus);
    }

    private static string StripUnknownLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var cut = text.IndexOf(UnknownLine, StringComparison.Ordinal);
        return cut < 0 ? text : text.Substring(0, cut);
    }

    [Fact]
    public void Requires_0_when_false_violation_text_does_not_state_unknown_value()
    {
        var result = Evaluate("REQUIRES_0_WHEN_FALSE", new[] { S("S1"), S("S2"), T("T") }, new[] { V("S1", false), V("T", true) }, ("S2", Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        AssertNeverStatesUnknown(result.Issue, "S2");
        Assert.Contains("S1", result.Issue.Reason);
        Assert.Equal("通讯故障", result.Issue.SignalDetails.Single(d => d.SignalName == "S2").ReadStatus);
    }

    [Fact]
    public void All_true_violation_text_does_not_state_unknown_value()
    {
        var result = Evaluate("ALL_TRUE", new[] { S("S"), M("M1"), M("M2") }, new[] { V("S", true), V("M1", false) }, ("M2", Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        AssertNeverStatesUnknown(result.Issue, "M2");
        Assert.Equal("[M1] 为false", result.Issue.Status);
        Assert.Contains("[Member]M1=False", result.Issue.JudgmentBasis);
    }

    [Theory]
    [InlineData(" IMPLIES")]
    [InlineData("implies")]
    [InlineData("IMPLIES ")]
    public void Operator_is_normalized_identically_in_evaluator_and_check(string op)
    {
        var rule = Rule(op, "R_OP");
        var signals = Signals(rule, S("S"), T("T"));

        Assert.NotNull(DiagnosticStrategyFactoryApp.CheckConstraintViolation(rule, signals, Values(V("S", true), V("T", false)), NoTags));
        var result = ConstraintRuleEvaluator.Evaluate(rule, signals, Values(V("S", true), V("T", false)),
            new Dictionary<string, SignalUnknownReason>(), NoTags);
        Assert.Equal(RuleVerdict.Violated, result.Verdict);
    }

    [Fact]
    public void Case_variant_unknown_names_do_not_throw_and_are_one_unknown()
    {
        var rule = Rule("MUST_BE_FALSE", "R_CASE");
        var signals = Signals(rule, T("T1"), T("X"), T("x"));
        var unknownMap = new Dictionary<string, SignalUnknownReason>(StringComparer.Ordinal)
        {
            ["X"] = SignalUnknownReason.FromStatus(Comm, "a"),
            ["x"] = SignalUnknownReason.FromStatus(Comm, "b"),
        };

        var violated = ConstraintRuleEvaluator.Evaluate(rule, signals, Values(V("T1", true)), unknownMap, NoTags);
        Assert.Equal(RuleVerdict.Violated, violated.Verdict);
        Assert.EndsWith("未采到信号（其取值不影响本结论）：X(通讯故障)", violated.Issue.Reason);

        var undetermined = ConstraintRuleEvaluator.Evaluate(rule, signals, Values(V("T1", false)), unknownMap, NoTags);
        Assert.Equal(RuleVerdict.Undetermined, undetermined.Verdict);
        Assert.Equal("X", Assert.Single(undetermined.Issue.SignalDetails).SignalName);
    }

    [Fact]
    public void Signal_missing_from_both_maps_is_unknown_not_silent_pass()
    {
        var rule = Rule("MUST_BE_FALSE", "R_MISSING");
        var result = ConstraintRuleEvaluator.Evaluate(rule, Signals(rule, T("T")), Values(),
            new Dictionary<string, SignalUnknownReason>(), NoTags);

        Assert.Equal(RuleVerdict.Undetermined, result.Verdict);
        Assert.Equal(UndeterminedBucket.CommFailure, result.Bucket);
        Assert.Equal("网关未返回", Assert.Single(result.Issue.SignalDetails).ReadStatus);
        Assert.Contains("T: Missing 未提供取值", result.Issue.Note);
    }

    [Fact]
    public void Good_values_comparer_is_preserved()
    {
        var rule = Rule("MUST_BE_FALSE", "R_CMP");
        var good = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["t"] = true };

        var result = ConstraintRuleEvaluator.Evaluate(rule, Signals(rule, T("T")), good,
            new Dictionary<string, SignalUnknownReason>(), NoTags);

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
    }

    [Fact]
    public void Exactly_eight_unknown_signals_are_enumerated()
    {
        var names = Enumerable.Range(0, 8).Select(i => "U" + i).ToArray();
        var unknown = names.Select(n => (n, Comm)).ToArray();

        var violated = Evaluate("MUST_BE_FALSE", new[] { T("K") }.Concat(names.Select(T)).ToArray(), new[] { V("K", true) }, unknown);
        Assert.Equal(RuleVerdict.Violated, violated.Verdict);

        var undetermined = Evaluate("MUST_BE_FALSE", names.Select(T).ToArray(), Array.Empty<(string, bool)>(), unknown);
        Assert.Equal(RuleVerdict.Undetermined, undetermined.Verdict);
        Assert.DoesNotContain("未采到信号过多", undetermined.Issue.Reason);
        Assert.Equal(8, undetermined.Issue.SignalDetails.Count);
    }

    [Theory]
    [InlineData("IMPLIES", true, false)]
    [InlineData("IMPLIES", true, true)]
    [InlineData("ALL_TRUE", true, false)]
    [InlineData("REQUIRES_0", true, true)]
    public void Zero_unknowns_equals_plain_check(string op, bool a, bool b)
    {
        var rule = Rule(op, "R_ZERO");
        var signals = op == "ALL_TRUE" ? Signals(rule, S("A"), M("B")) : Signals(rule, S("A"), T("B"));

        var plain = DiagnosticStrategyFactoryApp.CheckConstraintViolation(rule, signals, Values(V("A", a), V("B", b)), NoTags);
        var result = ConstraintRuleEvaluator.Evaluate(rule, signals, Values(V("A", a), V("B", b)),
            new Dictionary<string, SignalUnknownReason>(), NoTags);

        if (plain == null)
        {
            Assert.Equal(RuleVerdict.Passed, result.Verdict);
            Assert.Null(result.Issue);
        }
        else
        {
            Assert.Equal(RuleVerdict.Violated, result.Verdict);
            Assert.Equal(plain.Reason, result.Issue.Reason);
            Assert.Equal(plain.Status, result.Issue.Status);
            Assert.Equal(plain.Variable, result.Issue.Variable);
            Assert.Equal(plain.JudgmentBasis, result.Issue.JudgmentBasis);
        }
    }

    [Fact]
    public void Blank_signal_names_mixed_with_unknown_are_ignored()
    {
        var violated = Evaluate("MUST_BE_FALSE", new[] { T(""), T("  "), T("T1"), T("T2") }, new[] { V("T1", true) }, ("T2", Comm));
        Assert.Equal(RuleVerdict.Violated, violated.Verdict);
        Assert.EndsWith("未采到信号（其取值不影响本结论）：T2(通讯故障)", violated.Issue.Reason);

        var undetermined = Evaluate("MUST_BE_FALSE", new[] { T(""), T("  "), T("T1"), T("T2") }, new[] { V("T1", false) }, ("T2", Comm));
        Assert.Equal(RuleVerdict.Undetermined, undetermined.Verdict);
        Assert.Equal("T2", Assert.Single(undetermined.Issue.SignalDetails).SignalName);
    }

    [Fact]
    public void Same_unknown_in_two_roles_is_one_unknown()
    {
        var undetermined = Evaluate("IMPLIES", new[] { S("A"), T("X"), S("X"), T("B") }, new[] { V("A", true), V("B", true) }, ("X", Comm));
        Assert.Equal(RuleVerdict.Undetermined, undetermined.Verdict);
        Assert.Equal("X", Assert.Single(undetermined.Issue.SignalDetails).SignalName);

        var violated = Evaluate("MUST_BE_FALSE", new[] { T("K"), T("X"), T("X") }, new[] { V("K", true) }, ("X", Comm));
        Assert.Equal(RuleVerdict.Violated, violated.Verdict);
        Assert.EndsWith("未采到信号（其取值不影响本结论）：X(通讯故障)", violated.Issue.Reason);
    }

    // ---- Fix round 2 ----

    [Fact]
    public void Unknown_name_that_is_dot_suffix_of_known_name_keeps_known_value_text()
    {
        const string known = "DB120_OP10Manual.General.E_StopStatus";
        const string unknown = "General.E_StopStatus";
        var result = Evaluate("MUST_BE_FALSE", new[] { T(known), T(unknown) }, new[] { V(known, true) }, (unknown, Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        Assert.Contains(known + "=True", result.Issue.JudgmentBasis);
        var knownDetail = result.Issue.SignalDetails.Single(d => d.SignalName == known);
        Assert.Null(knownDetail.ReadStatus);
        Assert.True(knownDetail.CurrentValue);
        AssertNeverStatesUnknown(result.Issue, unknown);
    }

    [Fact]
    public void Unknown_name_that_is_dash_suffix_of_known_name_keeps_known_value_text()
    {
        var result = Evaluate("MUST_BE_FALSE", new[] { T("OP010-FZ91"), T("FZ91") }, new[] { V("OP010-FZ91", true) }, ("FZ91", Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        Assert.Contains("OP010-FZ91=True", result.Issue.JudgmentBasis);
        AssertNeverStatesUnknown(result.Issue, "FZ91");
    }

    [Fact]
    public void Requires_0_when_false_unknown_source_is_not_listed_as_failed()
    {
        var result = Evaluate("REQUIRES_0_WHEN_FALSE", new[] { S("S1"), S("S2"), T("T1"), T("T2") },
            new[] { V("S1", false), V("T1", true) }, ("S2", Comm), ("T2", Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        AssertNeverStatesUnknown(result.Issue, "S2");
        AssertNeverStatesUnknown(result.Issue, "T2");
        Assert.Contains("1 个 Source 已失效（S1）", result.Issue.JudgmentBasis);
    }

    [Fact]
    public void All_true_unknown_member_is_not_listed_as_false()
    {
        var result = Evaluate("ALL_TRUE", new[] { S("S"), M("M1"), M("M2"), M("M3") },
            new[] { V("S", true), V("M1", false), V("M3", true) }, ("M2", Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        AssertNeverStatesUnknown(result.Issue, "M2");
        Assert.Contains("1个Member信号为false（M1）", result.Issue.JudgmentBasis);
    }

    [Fact]
    public void Implies_violation_from_known_pair_does_not_state_unknown_pair()
    {
        // N:N 配对：S1→T1 已知违规；S2→T2 的 T2 未采到
        var result = Evaluate("IMPLIES", new[] { S("S1"), S("S2"), T("T1"), T("T2") },
            new[] { V("S1", true), V("S2", true), V("T1", false) }, ("T2", Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        Assert.Equal("Source=True, MissingTarget=T1", result.Issue.Status);
        AssertNeverStatesUnknown(result.Issue, "T2");
    }

    [Fact]
    public void Violation_proven_only_by_combination_yields_neutral_issue()
    {
        // 单 Source 多 Target：T2 未采到时纯函数跳过整条配对（返回 null），但 T1=false 使任何取值都违规。
        // （EXACTLY_ONE 不会出现这种情况：已知值不足以违规时，总有一种未知取值让它恰好 1 个为 true。）
        var result = Evaluate("IMPLIES", new[] { S("S"), T("T1"), T("T2") },
            new[] { V("S", true), V("T1", false) }, ("T2", Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        var issue = result.Issue;
        Assert.Equal("约束问题", issue.Category);
        Assert.Equal("警告", issue.Severity);
        Assert.Equal("R_IMPLIES", issue.RelatedRule);
        Assert.Equal("R_IMPLIES(IMPLIES)", issue.Uid);
        Assert.StartsWith("R_IMPLIES: 无论未采到信号取何值均违反约束（IMPLIES）", issue.Reason);
        Assert.Contains("S=True", issue.JudgmentBasis);
        Assert.Contains("T1=False", issue.JudgmentBasis);
        Assert.True(issue.SignalDetails.Single(d => d.SignalName == "S").CurrentValue);
        var t1 = issue.SignalDetails.Single(d => d.SignalName == "T1");
        Assert.False(t1.CurrentValue);
        Assert.Null(t1.ReadStatus);
        AssertNeverStatesUnknown(issue, "T2");
    }

    [Fact]
    public void Neutral_issue_severity_follows_priority()
    {
        var rule = Rule("IMPLIES", "R_CRIT");
        rule.Priority = "Critical";
        var result = ConstraintRuleEvaluator.Evaluate(rule, Signals(rule, S("S"), T("T1"), T("T2")),
            Values(V("S", true), V("T1", false)),
            new Dictionary<string, SignalUnknownReason> { ["T2"] = SignalUnknownReason.FromStatus(Comm, "x") }, NoTags);

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        Assert.Equal("严重", result.Issue.Severity);
    }

    [Fact]
    public void Dollar_in_names_is_reported_literally()
    {
        var result = Evaluate("MUST_BE_FALSE", new[] { T("A$1"), T("B$0") }, new[] { V("A$1", true) }, ("B$0", Comm));

        Assert.Equal(RuleVerdict.Violated, result.Verdict);
        Assert.Contains("A$1=True", result.Issue.JudgmentBasis);
        AssertNeverStatesUnknown(result.Issue, "B$0");
    }

    [Fact]
    public void Rule_signal_known_under_other_case_is_known()
    {
        var rule = Rule("MUST_BE_FALSE", "R_CASE_KNOWN");
        var signals = Signals(rule, T("x"));
        var none = new Dictionary<string, SignalUnknownReason>();

        var violated = ConstraintRuleEvaluator.Evaluate(rule, signals,
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["X"] = true }, none, NoTags);
        Assert.Equal(RuleVerdict.Violated, violated.Verdict);
        Assert.DoesNotContain(UnknownLine, violated.Issue.Reason);
        Assert.Contains("x=True", violated.Issue.JudgmentBasis);

        var passed = ConstraintRuleEvaluator.Evaluate(rule, signals,
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["X"] = false }, none, NoTags);
        Assert.Equal(RuleVerdict.Passed, passed.Verdict);
    }

    [Fact]
    public void Rule_signal_with_conflicting_case_variant_values_is_unknown()
    {
        var rule = Rule("MUST_BE_FALSE", "R_CASE_CONFLICT");
        var result = ConstraintRuleEvaluator.Evaluate(rule, Signals(rule, T("xY")),
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["XY"] = true, ["Xy"] = false },
            new Dictionary<string, SignalUnknownReason>(), NoTags);

        Assert.Equal(RuleVerdict.Undetermined, result.Verdict);
        Assert.Equal("xY", Assert.Single(result.Issue.SignalDetails).SignalName);
    }
}
