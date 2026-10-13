using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory
{
    /// <summary>「无法判定」按原因聚合的类别（报告汇总行用）。</summary>
    internal enum UndeterminedBucket
    {
        /// <summary>点名不存在：SymbolNotFound + NotRegistered</summary>
        NameMissing,
        /// <summary>通讯故障：CommFailure + Timeout + ProjectUnavailable + Missing</summary>
        CommFailure,
        /// <summary>首值未到：WaitingForData</summary>
        WaitingForData,
        /// <summary>其它：AccessDenied + DeviceError + BadQuality + NotBoolean + 不支持的约束类型</summary>
        Other,
    }

    /// <summary>
    /// 诊断层"没采到可信值"的原因（信号级，D-1）。Code 为稳定英文码：<see cref="PLC_SignalReadStatus"/> 名称，
    /// 或 <see cref="ProjectUnavailable"/> / <see cref="NotBoolean"/>。
    /// </summary>
    internal sealed record SignalUnknownReason(string Code, string Detail)
    {
        /// <summary>PLC 项目不可读（配置缺失 / 未读取）</summary>
        public const string ProjectUnavailable = "ProjectUnavailable";

        /// <summary>采到了 Good 值但无法解析为布尔量</summary>
        public const string NotBoolean = "NotBoolean";

        public static SignalUnknownReason FromStatus(PLC_SignalReadStatus status, string detail) =>
            new(status.ToString(), detail);

        public UndeterminedBucket Bucket => Code switch
        {
            nameof(PLC_SignalReadStatus.SymbolNotFound) or nameof(PLC_SignalReadStatus.NotRegistered) => UndeterminedBucket.NameMissing,
            nameof(PLC_SignalReadStatus.CommFailure) or nameof(PLC_SignalReadStatus.Timeout)
                or nameof(PLC_SignalReadStatus.Missing) or ProjectUnavailable => UndeterminedBucket.CommFailure,
            nameof(PLC_SignalReadStatus.WaitingForData) => UndeterminedBucket.WaitingForData,
            // NotRead：该点根本没被纳入本次诊断读取，不是网关/通讯的问题，归「其它」而非「通讯故障」。
            nameof(PLC_SignalReadStatus.NotRead) => UndeterminedBucket.Other,
            _ => UndeterminedBucket.Other,
        };

        public string Label => Code switch
        {
            nameof(PLC_SignalReadStatus.SymbolNotFound) => "点名不存在：PLC 程序无此符号",
            nameof(PLC_SignalReadStatus.NotRegistered) => "点名不存在：IGW 未注册",
            nameof(PLC_SignalReadStatus.CommFailure) => "通讯故障",
            nameof(PLC_SignalReadStatus.Timeout) => "读取超时",
            nameof(PLC_SignalReadStatus.Missing) => "网关未返回",
            nameof(PLC_SignalReadStatus.NotRead) => "未参与诊断读取",
            nameof(PLC_SignalReadStatus.WaitingForData) => "首值未到",
            nameof(PLC_SignalReadStatus.AccessDenied) => "无读取权限",
            nameof(PLC_SignalReadStatus.DeviceError) => "设备错误",
            nameof(PLC_SignalReadStatus.BadQuality) => "质量异常",
            ProjectUnavailable => "PLC 项目不可读",
            NotBoolean => "值不是布尔量",
            _ => Code,
        };
    }

    internal enum RuleVerdict
    {
        Passed,
        Violated,
        Undetermined,
    }

    /// <summary>单条约束的三值判定结果。Violated → 违规 issue；Undetermined → 无法判定 issue；Passed → Issue=null。</summary>
    internal sealed class RuleEvaluation
    {
        public static readonly RuleEvaluation PassedResult = new() { Verdict = RuleVerdict.Passed };

        public RuleVerdict Verdict { get; init; }

        public DiagnosticIssue Issue { get; init; }

        /// <summary>仅 Undetermined 有意义</summary>
        public UndeterminedBucket Bucket { get; init; }
    }

    /// <summary>
    /// D-1 三值判定（spec §4.2-3）：用 Good 值 + 未采到信号，枚举未知信号的全部 true/false 组合调用纯函数
    /// <see cref="DiagnosticStrategyFactoryApp.CheckConstraintViolation"/>。全违规 → 违规；全通过 → 通过；否则 → 无法判定。
    /// 坏点只影响真正依赖它的结论，且绝不把"采不到"当成通过。
    /// </summary>
    internal static class ConstraintRuleEvaluator
    {
        internal const int MaxUnknownSignals = 8;

        /// <summary>已规范化（<see cref="NormalizeOperator"/>）的受支持 operator。</summary>
        internal static readonly IReadOnlySet<string> SupportedOperators = new HashSet<string>(StringComparer.Ordinal)
        {
            "AT_MOST_ONE", "EXACTLY_ONE", "ALL_TRUE", "REQUIRES_0", "REQUIRES_0_WHEN_FALSE",
            "MUST_BE_FALSE", "IMPLIES", "SAME_STATE", "CHAIN_ORDER",
        };

        public static RuleEvaluation Evaluate(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> goodValues,
            Dictionary<string, SignalUnknownReason> unknownSignals,
            Dictionary<string, PLC_IOTagEntity> signalNameToTag)
        {
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }
            signals ??= new List<PLC_IOConstraintSignalEntity>();
            goodValues ??= new Dictionary<string, bool>();
            unknownSignals ??= new Dictionary<string, SignalUnknownReason>();
            signalNameToTag ??= new Dictionary<string, PLC_IOTagEntity>(StringComparer.OrdinalIgnoreCase);

            // 规则里声明的每个（非空白）信号名：有 Good 值 → 已知；否则 → 未知（没有原因的按 Missing「未提供取值」，
            // 保证"没取到值的信号绝不静默参与通过"在本函数内自洽）。Good 值按 OrdinalIgnoreCase 查找（与
            // CheckConstraintViolation 内部 DistinctSignals 的合并口径一致）：规则写 x、Good 值是 X 也算已知，
            // 并以规则写法补一份别名进工作字典；大小写变体取值互相矛盾时不猜，按未知处理。
            // 未知大小写变体按 OrdinalIgnoreCase 合并为一个未知量，Variants 记录规则里出现过的全部写法。
            var known = new Dictionary<string, bool>(goodValues, goodValues.Comparer);
            var unknownNames = new List<string>();
            foreach (var name in signals
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.SignalName))
                .OrderBy(s => s.SortOrder ?? int.MaxValue)
                .Select(s => s.SignalName)
                .Distinct(StringComparer.Ordinal))
            {
                if (known.ContainsKey(name))
                {
                    continue;
                }
                var matches = goodValues
                    .Where(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Value)
                    .Distinct()
                    .ToList();
                if (matches.Count == 1)
                {
                    known[name] = matches[0];
                }
                else
                {
                    unknownNames.Add(name);
                }
            }
            var unknown = unknownNames
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new UnknownSignal(g.First(), ResolveReason(g, unknownSignals), g.ToList()))
                .ToList();

            var op = NormalizeOperator(rule.Operator);
            if (!SupportedOperators.Contains(op))
            {
                var note = "不支持的约束类型 " + (op.Length == 0 ? "(空)" : op) + "，规则引擎无法判定";
                return Undetermined(rule, signals, new List<(string Name, SignalUnknownReason Reason)>(),
                    signalNameToTag, note, UndeterminedBucket.Other);
            }

            if (unknown.Count == 0)
            {
                var issue = DiagnosticStrategyFactoryApp.CheckConstraintViolation(rule, signals, known, signalNameToTag);
                return issue == null
                    ? RuleEvaluation.PassedResult
                    : new RuleEvaluation { Verdict = RuleVerdict.Violated, Issue = issue };
            }

            var unknownPairs = unknown.Select(u => (u.Name, u.Reason)).ToList();
            if (unknown.Count > MaxUnknownSignals)
            {
                return Undetermined(rule, signals, unknownPairs, signalNameToTag,
                    $"未采到信号过多（{unknown.Count} 个，超过 {MaxUnknownSignals} 个不做推断）", unknown[0].Reason.Bucket);
            }

            // 判定：枚举未知信号的全部 true/false 组合（结论只取决于是否"全违规 / 全通过"）。
            var anyPass = false;
            var anyViolation = false;
            var combinations = 1 << unknown.Count;
            var values = new Dictionary<string, bool>(known, known.Comparer);
            for (var mask = 0; mask < combinations && !(anyPass && anyViolation); mask++)
            {
                for (var i = 0; i < unknown.Count; i++)
                {
                    var value = (mask & (1 << i)) != 0;
                    foreach (var variant in unknown[i].Variants)
                    {
                        values[variant] = value;
                    }
                }
                if (DiagnosticStrategyFactoryApp.CheckConstraintViolation(rule, signals, values, signalNameToTag) == null)
                {
                    anyPass = true;
                }
                else
                {
                    anyViolation = true;
                }
            }

            if (!anyViolation)
            {
                return RuleEvaluation.PassedResult;
            }
            if (anyPass)
            {
                return Undetermined(rule, signals, unknownPairs, signalNameToTag, null, unknown[0].Reason.Bucket);
            }

            // 报告：枚举组合里的 issue 文字会写出未知信号的假设值，一律不用。只用真实采到的值再调一次纯函数——
            // 纯函数从不让缺值信号单独判定违规、也只读它拿到的值，因此它返回的 issue 只描述真实值；
            // 返回 null（只有把各种可能组合起来才能证明违规）时，按规则自行构建中性 issue。
            var reported = DiagnosticStrategyFactoryApp.CheckConstraintViolation(rule, signals, known, signalNameToTag)
                ?? BuildCombinationOnlyIssue(rule, signals, known, signalNameToTag);
            MarkUnknownSignals(reported, unknown, signalNameToTag);
            return new RuleEvaluation { Verdict = RuleVerdict.Violated, Issue = reported };
        }

        /// <summary>operator 规范化：Trim + ToUpperInvariant。CheckConstraintViolation 的 switch 使用同一函数。</summary>
        internal static string NormalizeOperator(string op) => op?.Trim().ToUpperInvariant() ?? string.Empty;

        private sealed record UnknownSignal(string Name, SignalUnknownReason Reason, List<string> Variants);

        private static readonly SignalUnknownReason NotProvided =
            SignalUnknownReason.FromStatus(PLC_SignalReadStatus.Missing, "未提供取值");

        private static SignalUnknownReason ResolveReason(IEnumerable<string> variants, Dictionary<string, SignalUnknownReason> unknownSignals)
        {
            foreach (var variant in variants)
            {
                if (unknownSignals.TryGetValue(variant, out var reason) && reason != null)
                {
                    return reason;
                }
            }
            return NotProvided;
        }

        /// <summary>构建「无法判定」issue（行格式由报告层按「工位: / 无法判定规则: / 未采到信号: / 原因:」输出）。</summary>
        internal static DiagnosticIssue BuildUndeterminedIssue(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            List<(string Name, SignalUnknownReason Reason)> unknown,
            Dictionary<string, PLC_IOTagEntity> signalNameToTag,
            string note)
        {
            string Readable(string name) => ReadableName(name, signalNameToTag);

            var unknownNames = unknown.Select(u => u.Name).ToList();
            string reason;
            if (unknown.Count == 0)
            {
                reason = $"无法判定：{note}";
            }
            else
            {
                var labels = string.Join("、", unknown.Select(u => u.Reason.Label).Distinct());
                reason = $"无法判定：{labels}，未采到 {string.Join(", ", unknownNames.Select(Readable))}";
                if (!string.IsNullOrWhiteSpace(note))
                {
                    reason += $"（{note}）";
                }
            }
            reason += $"（{rule.RuleName ?? rule.RuleType}）";

            var ruleSignalNames = signals
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.SignalName))
                .Select(s => s.SignalName)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var bucket = unknown.Count == 0 ? UndeterminedBucket.Other : unknown[0].Reason.Bucket;

            return new DiagnosticIssue
            {
                Variable = string.Join(", ", (unknownNames.Count > 0 ? unknownNames : ruleSignalNames).Select(Readable)),
                Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                Status = "无法判定",
                Reason = reason,
                Severity = "提示",
                Category = "采集失败",
                RelatedRule = rule.RuleName ?? rule.RuleType,
                ConstraintOperator = rule.Operator,
                SignalDetails = unknown.Select(u => new SignalDetail
                {
                    SignalName = u.Name,
                    SignalAddress = AddressOf(u.Name, signalNameToTag),
                    Address = AddressOf(u.Name, signalNameToTag),
                    CurrentValue = false,
                    ExpectedValue = "无法判定（未采到）",
                    Description = Readable(u.Name),
                    ReadStatus = u.Reason.Label,
                }).ToList(),
                ExpectedValue = "无法判定（信号未采到）",
                Suggestion = SuggestionFor(bucket, unknown.Count == 0),
                Note = string.Join("；", unknown.Select(u => $"{u.Name}: {u.Reason.Code} {u.Reason.Detail}".Trim())),
            };
        }

        private static RuleEvaluation Undetermined(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            List<(string Name, SignalUnknownReason Reason)> unknown,
            Dictionary<string, PLC_IOTagEntity> signalNameToTag,
            string note,
            UndeterminedBucket bucket) =>
            new()
            {
                Verdict = RuleVerdict.Undetermined,
                Issue = BuildUndeterminedIssue(rule, signals, unknown, signalNameToTag, note),
                Bucket = bucket,
            };

        private static string ReadableName(string name, Dictionary<string, PLC_IOTagEntity> signalNameToTag) =>
            signalNameToTag.TryGetValue(name, out var tag) && !string.IsNullOrEmpty(tag?.Comment) ? $"{name} ({tag.Comment})" : name;

        private static string AddressOf(string name, Dictionary<string, PLC_IOTagEntity> signalNameToTag) =>
            signalNameToTag.TryGetValue(name, out var tag) ? tag?.Address : null;

        private static List<string> DeclaredNames(List<PLC_IOConstraintSignalEntity> signals) => signals
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.SignalName))
            .OrderBy(s => s.SortOrder ?? int.MaxValue)
            .Select(s => s.SignalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>
        /// 只靠"未知信号各种取值组合"才能证明违规时（纯函数用真实值判不出）的中性违规 issue：
        /// 字段约定与纯函数的违规 issue 一致；文字只列真实采到的"信号=值"，不点名任何具体违规信号，也不写未知信号的取值。
        /// </summary>
        private static DiagnosticIssue BuildCombinationOnlyIssue(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> known,
            Dictionary<string, PLC_IOTagEntity> signalNameToTag)
        {
            var op = NormalizeOperator(rule.Operator);
            var ruleName = rule.RuleName ?? rule.RuleType;
            var knownNames = DeclaredNames(signals).Where(known.ContainsKey).ToList();
            var knownValuesStr = string.Join(", ", knownNames.Select(n => $"{n}={known[n]}"));

            return new DiagnosticIssue
            {
                Variable = knownNames.Count > 0 ? string.Join(", ", knownNames.Select(n => ReadableName(n, signalNameToTag))) : ruleName,
                Uid = string.IsNullOrEmpty(rule.RuleName) ? rule.Operator : $"{rule.RuleName}({rule.Operator})",
                Status = "无论未采到信号取何值均违反约束",
                Reason = $"{ruleName}: 无论未采到信号取何值均违反约束（{op}）",
                Severity = rule.Priority == "Critical" ? "严重" : "警告",
                Category = "约束问题",
                RelatedRule = rule.RuleName,
                ConstraintOperator = op,
                SignalDetails = knownNames.Select(n => new SignalDetail
                {
                    SignalName = n,
                    SignalAddress = AddressOf(n, signalNameToTag),
                    Address = AddressOf(n, signalNameToTag),
                    CurrentValue = known[n],
                    ExpectedValue = $"满足约束（{op}）",
                    Description = ReadableName(n, signalNameToTag),
                }).ToList(),
                ExpectedValue = $"满足约束（{op}）",
                Suggestion = "已采到的信号已足以确定违反约束；恢复未采到信号的采集后重新诊断可定位具体违规信号",
                JudgmentBasis = $"根据约束规则【{ruleName}】（类型：{op}）判断：当前PLC实时数据：[{knownValuesStr}]。对未采到信号的全部取值组合逐一校验，均违反该约束。",
            };
        }

        /// <summary>
        /// 给违规 issue 补上未采到信号：SignalDetails 中标记/补充 ReadStatus（不带实测值含义），
        /// Reason 与 JudgmentBasis 末尾追加一行「未采到信号（其取值不影响本结论）」。只追加，不改写已有文字。
        /// </summary>
        private static void MarkUnknownSignals(DiagnosticIssue issue, List<UnknownSignal> unknown, Dictionary<string, PLC_IOTagEntity> signalNameToTag)
        {
            issue.SignalDetails ??= new List<SignalDetail>();
            foreach (var u in unknown)
            {
                var matched = issue.SignalDetails
                    .Where(d => d?.SignalName != null && u.Variants.Contains(d.SignalName, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                foreach (var detail in matched)
                {
                    detail.ReadStatus = u.Reason.Label;
                    detail.CurrentValue = false;
                    detail.ExpectedValue = "无法判定（未采到）";
                }
                if (matched.Count == 0)
                {
                    issue.SignalDetails.Add(new SignalDetail
                    {
                        SignalName = u.Name,
                        SignalAddress = AddressOf(u.Name, signalNameToTag),
                        Address = AddressOf(u.Name, signalNameToTag),
                        CurrentValue = false,
                        ExpectedValue = "无法判定（未采到）",
                        Description = ReadableName(u.Name, signalNameToTag),
                        ReadStatus = u.Reason.Label,
                    });
                }
            }

            var line = "未采到信号（其取值不影响本结论）：" + string.Join(", ", unknown.Select(u => $"{u.Name}({u.Reason.Label})"));
            issue.Reason = string.IsNullOrEmpty(issue.Reason) ? line : issue.Reason + "\n" + line;
            issue.JudgmentBasis = string.IsNullOrEmpty(issue.JudgmentBasis) ? line : issue.JudgmentBasis + "\n" + line;

            var note = $"含未采到信号（{string.Join(", ", unknown.Select(u => u.Name))}），正常值已足以确定违规";
            issue.Note = string.IsNullOrWhiteSpace(issue.Note) ? note : issue.Note + "；" + note;
        }

        private static string SuggestionFor(UndeterminedBucket bucket, bool unsupported)
        {
            if (unsupported)
            {
                return "规则引擎不支持该约束类型，需人工核对";
            }
            return bucket switch
            {
                UndeterminedBucket.NameMissing => "检查点名/PLC 程序，或在 PLC 项目上重新「注册PLC标签」后再诊断",
                UndeterminedBucket.CommFailure => "检查 PLC 与网关的通讯后再诊断",
                UndeterminedBucket.WaitingForData => "设备刚连上或点定义刚变更，稍后重新诊断",
                _ => "检查网关读取权限与设备状态后再诊断",
            };
        }
    }
}
