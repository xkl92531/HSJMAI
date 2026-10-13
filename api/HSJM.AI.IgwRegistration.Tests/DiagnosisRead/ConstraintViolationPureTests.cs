using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using Xunit;
using static HSJM.AI.IgwRegistration.Tests.DiagnosisRead.ConstraintTestData;

namespace HSJM.AI.IgwRegistration.Tests.DiagnosisRead;

/// <summary>
/// D-1 Task 4：CheckConstraintViolation 是纯函数，缺值信号不参与判定。
/// Fix round 1：补齐每个 operator 的"全部信号都采到值"基线用例（违反 + 通过各一个），
/// 并补两条配对/分支错位的回归测试（IMPLIES 配对错位、AT_MOST_ONE Member 全缺值误落到 Source 分支）。
/// Fix round 2：GetRoleSignals 从源头剔除空名/空白名 SignalName（不再对 resolvedMemberSignals 等抛
/// ArgumentNullException，空名信号如同未声明）；补 AT_MOST_ONE Source+Target 配对分支路径的两条用例
/// （全在场两条配对都激活 → 违反；一条配对因缺 Target 被整条丢弃、只剩单条完整配对不够构成互斥比较
/// → 不违反）。
/// </summary>
public class ConstraintViolationPureTests
{
    private static DiagnosticIssue Check(string op, (string Name, string Role)[] signals, params (string Name, bool Value)[] values)
    {
        var rule = Rule(op);
        return DiagnosticStrategyFactoryApp.CheckConstraintViolation(rule, Signals(rule, signals), Values(values), NoTags);
    }

    private static readonly (string, string)[] TwoMembers = { ("A", "Member"), ("B", "Member") };
    private static readonly (string, string)[] SourceTarget = { ("S", "Source"), ("T", "Target") };
    private static readonly (string, string)[] TargetOnly = { ("T", "Target") };

    // ---------- EXACTLY_ONE ----------

    [Fact]
    public void Exactly_one_with_all_signals_missing_is_not_a_violation() =>
        Assert.Null(Check("EXACTLY_ONE", TwoMembers));

    [Fact]
    public void Exactly_one_with_zero_true_among_partial_values_is_not_a_violation() =>
        Assert.Null(Check("EXACTLY_ONE", TwoMembers, ("A", false)));

    [Fact]
    public void Exactly_one_with_complete_values_and_zero_true_is_a_violation() =>
        Assert.NotNull(Check("EXACTLY_ONE", TwoMembers, ("A", false), ("B", false)));

    [Fact]
    public void Exactly_one_with_two_true_is_a_violation() =>
        Assert.NotNull(Check("EXACTLY_ONE", TwoMembers, ("A", true), ("B", true)));

    [Fact]
    public void Exactly_one_with_all_present_and_exactly_one_true_is_not_a_violation() =>
        Assert.Null(Check("EXACTLY_ONE", TwoMembers, ("A", true), ("B", false)));

    // ---------- AT_MOST_ONE ----------

    [Fact]
    public void At_most_one_with_all_present_and_two_active_branches_is_a_violation() =>
        Assert.NotNull(Check("AT_MOST_ONE", TwoMembers, ("A", true), ("B", true)));

    [Fact]
    public void At_most_one_with_all_present_and_one_active_branch_is_not_a_violation() =>
        Assert.Null(Check("AT_MOST_ONE", TwoMembers, ("A", true), ("B", false)));

    /// <summary>
    /// Fix round 1 item 2 回归：声明的 Member 全部缺值时，不允许兜底落到 Source 分支——
    /// 否则会把两个互不相关、且都为 true 的 Source 信号当成互斥分支，产生假违反。
    /// </summary>
    [Fact]
    public void At_most_one_with_all_members_missing_does_not_fall_back_to_source_branches() =>
        Assert.Null(Check(
            "AT_MOST_ONE",
            new[] { ("M1", "Member"), ("M2", "Member"), ("S1", "Source"), ("S2", "Source") },
            ("S1", true),
            ("S2", true)));

    /// <summary>
    /// Fix round 2 item 2a：2 Source × 2 Target 按 1:1 顺序配对（S1↔T1、S2↔T2，配对算法同 IMPLIES 的
    /// BuildImpliesMappings）。全部信号都在场，且两条配对都"激活"（Source=true 且对应 Target=true，
    /// 分支内任一信号为 true 即算激活）时，两个互斥分支同时激活，触碰 AT_MOST_ONE（至多一个分支为真）
    /// → 判定违反。
    /// </summary>
    [Fact]
    public void At_most_one_source_target_pairing_with_all_present_and_both_pairings_active_is_a_violation() =>
        Assert.NotNull(Check(
            "AT_MOST_ONE",
            new[] { ("S1", "Source"), ("S2", "Source"), ("T1", "Target"), ("T2", "Target") },
            ("S1", true),
            ("T1", true),
            ("S2", true),
            ("T2", true)));

    /// <summary>
    /// Fix round 2 item 2b：T1 缺值。S1→T1 这条配对因"任一 Target 缺值"整条被跳过——一个缺成员的配对
    /// 不允许被当作"分支"参与互斥比较（既不算激活也不算不激活，是根本不存在）。剩下唯一一条完整配对
    /// S2→T2（S2=true 且 T2=true，本身是"激活"的），但 AT_MOST_ONE 判定互斥违反的前提是至少要有 2 个
    /// 可比较的分支（branches.Count >= 2）；只剩 1 条完整配对无法构成互斥比较，因此不判定违反
    /// —— 读代码可见：缺成员的配对永远不会被计为"真"，它是被整条丢弃，不是被当成"假"分支。
    /// </summary>
    [Fact]
    public void At_most_one_source_target_pairing_with_missing_target_drops_that_pairing_and_the_remaining_single_complete_pairing_is_not_a_violation() =>
        Assert.Null(Check(
            "AT_MOST_ONE",
            new[] { ("S1", "Source"), ("S2", "Source"), ("T1", "Target"), ("T2", "Target") },
            ("S1", true),
            ("S2", true),
            ("T2", true)));

    // ---------- ALL_TRUE ----------

    [Fact]
    public void All_true_with_partially_missing_sources_is_not_active() =>
        Assert.Null(Check("ALL_TRUE", new[] { ("S1", "Source"), ("S2", "Source"), ("M", "Member") }, ("S1", true), ("M", false)));

    [Fact]
    public void All_true_with_missing_members_does_not_fall_back_to_targets() =>
        Assert.Null(Check("ALL_TRUE", new[] { ("S", "Source"), ("M", "Member"), ("T", "Target") }, ("S", true), ("T", false)));

    [Fact]
    public void All_true_with_active_source_and_false_member_is_a_violation() =>
        Assert.NotNull(Check("ALL_TRUE", new[] { ("S", "Source"), ("M", "Member") }, ("S", true), ("M", false)));

    [Fact]
    public void All_true_with_active_source_and_true_member_is_not_a_violation() =>
        Assert.Null(Check("ALL_TRUE", new[] { ("S", "Source"), ("M", "Member") }, ("S", true), ("M", true)));

    // ---------- IMPLIES ----------

    [Fact]
    public void Implies_with_all_present_true_source_and_false_target_is_a_violation() =>
        Assert.NotNull(Check("IMPLIES", SourceTarget, ("S", true), ("T", false)));

    [Fact]
    public void Implies_with_all_present_true_source_and_true_target_is_not_a_violation() =>
        Assert.Null(Check("IMPLIES", SourceTarget, ("S", true), ("T", true)));

    /// <summary>
    /// Fix round 1 item 1 回归：声明 2 个 Source 配 2 个 Target（1:1 按序配对 S1↔T1、S2↔T2）。
    /// T1 缺值时，配对算法若退化成"用已读到的信号重新配对"会把 S1 错配到 T2 上（因为只剩 1 个可读
    /// Target），S1=true 且 T2=false 就会被误判为违反。正确做法是配对仍按声明角色构建，
    /// S1→T1 因 T1 缺值整条跳过，S2→T2 因 S2=false 不触发，最终不应有违反。
    /// </summary>
    [Fact]
    public void Implies_pairing_does_not_shift_when_a_target_is_missing()
    {
        var issue = Check(
            "IMPLIES",
            new[] { ("S1", "Source"), ("S2", "Source"), ("T1", "Target"), ("T2", "Target") },
            ("S1", true),
            ("S2", false),
            ("T2", false));

        Assert.Null(issue);
    }

    // ---------- REQUIRES_0（含 Source） ----------

    [Fact]
    public void Requires_0_with_all_targets_missing_does_not_fall_back_to_the_source() =>
        Assert.Null(Check("REQUIRES_0", SourceTarget, ("S", true)));

    [Fact]
    public void Requires_0_with_missing_source_is_not_checked_unconditionally() =>
        Assert.Null(Check("REQUIRES_0", SourceTarget, ("T", true)));

    [Fact]
    public void Requires_0_with_active_source_and_true_target_is_a_violation() =>
        Assert.NotNull(Check("REQUIRES_0", SourceTarget, ("S", true), ("T", true)));

    [Fact]
    public void Requires_0_with_active_source_and_false_target_is_not_a_violation() =>
        Assert.Null(Check("REQUIRES_0", SourceTarget, ("S", true), ("T", false)));

    // ---------- REQUIRES_0（不含 Source，仅 Target，无条件禁止态） ----------

    [Fact]
    public void Requires_0_without_source_and_true_target_is_a_violation() =>
        Assert.NotNull(Check("REQUIRES_0", TargetOnly, ("T", true)));

    [Fact]
    public void Requires_0_without_source_and_false_target_is_not_a_violation() =>
        Assert.Null(Check("REQUIRES_0", TargetOnly, ("T", false)));

    // ---------- REQUIRES_0_WHEN_FALSE ----------

    [Fact]
    public void Requires_0_when_false_with_inactive_source_and_true_target_is_a_violation() =>
        Assert.NotNull(Check("REQUIRES_0_WHEN_FALSE", SourceTarget, ("S", false), ("T", true)));

    [Fact]
    public void Requires_0_when_false_with_active_source_is_not_a_violation() =>
        Assert.Null(Check("REQUIRES_0_WHEN_FALSE", SourceTarget, ("S", true), ("T", true)));

    // ---------- MUST_BE_FALSE ----------

    [Fact]
    public void Must_be_false_with_true_target_is_a_violation() =>
        Assert.NotNull(Check("MUST_BE_FALSE", TargetOnly, ("T", true)));

    [Fact]
    public void Must_be_false_with_false_target_is_not_a_violation() =>
        Assert.Null(Check("MUST_BE_FALSE", TargetOnly, ("T", false)));

    // ---------- SAME_STATE ----------

    [Fact]
    public void Same_state_with_missing_members_does_not_fall_back_to_other_roles() =>
        Assert.Null(Check("SAME_STATE", new[] { ("M1", "Member"), ("M2", "Member"), ("X", "Source"), ("Y", "Target") }, ("X", true), ("Y", false)));

    [Fact]
    public void Same_state_with_all_present_and_mismatched_members_is_a_violation() =>
        Assert.NotNull(Check("SAME_STATE", TwoMembers, ("A", true), ("B", false)));

    [Fact]
    public void Same_state_with_all_present_and_matched_members_is_not_a_violation() =>
        Assert.Null(Check("SAME_STATE", TwoMembers, ("A", true), ("B", true)));

    // ---------- CHAIN_ORDER ----------

    [Fact]
    public void Chain_order_missing_signal_is_not_treated_as_false() =>
        Assert.Null(Check("CHAIN_ORDER", TwoMembers, ("B", true)));

    [Fact]
    public void Chain_order_violation_is_still_detected() =>
        Assert.NotNull(Check("CHAIN_ORDER", TwoMembers, ("A", false), ("B", true)));

    [Fact]
    public void Chain_order_with_all_present_and_in_order_is_not_a_violation() =>
        Assert.Null(Check("CHAIN_ORDER", TwoMembers, ("A", true), ("B", true)));

    // ---------- CUSTOM ----------

    /// <summary>
    /// CUSTOM 没有可执行语义，代码恒定 `return null`（见 CheckConstraintViolation 的 case "CUSTOM"）——
    /// 它没有"违反"分支，所以不像其它 operator 那样有"全部信号都采到值时的违反用例"，
    /// 这一条本身就是它在全部信号都采到值时的唯一行为基线。
    /// </summary>
    [Fact]
    public void Custom_operator_returns_null_for_outer_undetermined_handling() =>
        Assert.Null(Check("CUSTOM", new[] { ("A", "Member") }, ("A", true)));

    // ---------- 空/空白 SignalName 声明（Fix round 2） ----------

    /// <summary>
    /// Fix round 2 item 1：规则信号里混入 SignalName 为 null 的声明（脏数据/上游生成异常）。修复前，
    /// resolvedMemberSignals/resolvedSourceSignals/resolvedTargetSignals 在方法一开始就对全部声明信号
    /// 无条件跑 `signalValues.ContainsKey(s.SignalName)`（不区分最终走哪个 operator 分支），
    /// SignalName 为 null 时 Dictionary.ContainsKey(null) 直接抛 ArgumentNullException——
    /// 哪怕最终执行的 operator（这里用 EXACTLY_ONE）根本不读这三个变量。修复后 GetRoleSignals 从源头
    /// 剔除空名/空白名，不再抛出。
    /// </summary>
    [Fact]
    public void Exactly_one_with_a_null_named_member_does_not_throw()
    {
        var exception = Record.Exception(() => Check(
            "EXACTLY_ONE",
            new (string, string)[] { (null, "Member"), ("A", "Member"), ("B", "Member") },
            ("A", true),
            ("B", false)));

        Assert.Null(exception);
    }

    /// <summary>
    /// 同上，覆盖 IsNullOrWhiteSpace 的空白（非 null）分支：全空格的 SignalName 同样必须被当作未声明，
    /// 不抛异常。
    /// </summary>
    [Fact]
    public void At_most_one_with_a_whitespace_named_member_does_not_throw()
    {
        var exception = Record.Exception(() => Check(
            "AT_MOST_ONE",
            new (string, string)[] { ("   ", "Member"), ("A", "Member"), ("B", "Member") },
            ("A", true),
            ("B", true)));

        Assert.Null(exception);
    }

    /// <summary>
    /// Fix round 2 item 1：不仅"不抛异常"，声明里的空名信号还必须"如同从未声明"——即判定结果（含文案）
    /// 与直接不写这条空名声明时完全一致。用 ALL_TRUE 验证：混入一个空名 Source 不应改变
    /// resolvedSourceSignals 参与拼接的 Reason/JudgmentBasis 文案（这两处都会枚举 Source 信号名）。
    /// </summary>
    [Fact]
    public void All_true_with_a_null_named_source_behaves_exactly_as_if_that_signal_were_not_declared()
    {
        var withBlankSource = Check(
            "ALL_TRUE",
            new (string, string)[] { (null, "Source"), ("S", "Source"), ("M", "Member") },
            ("S", true),
            ("M", false));
        var withoutBlankSource = Check(
            "ALL_TRUE",
            new[] { ("S", "Source"), ("M", "Member") },
            ("S", true),
            ("M", false));

        Assert.NotNull(withBlankSource);
        Assert.NotNull(withoutBlankSource);
        Assert.Equal(withoutBlankSource.Reason, withBlankSource.Reason);
        Assert.Equal(withoutBlankSource.JudgmentBasis, withBlankSource.JudgmentBasis);
        Assert.Equal(withoutBlankSource.ExpectedValue, withBlankSource.ExpectedValue);
    }
}
