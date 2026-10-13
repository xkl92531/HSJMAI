using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory
{
    /// <summary>约束检查结果（D-1）：违规 / 无法判定清单与计数。Violated 按规则计数（Violations 是去重合并后的 issue）。</summary>
    internal sealed class ConstraintCheckOutcome
    {
        private readonly object _sync = new();

        public List<DiagnosticIssue> Violations { get; private set; } = new List<DiagnosticIssue>();

        public List<DiagnosticIssue> Undetermined { get; } = new List<DiagnosticIssue>();

        /// <summary>Confirmed manual mode is reported as an IO hint, not a constraint violation.</summary>
        public List<DiagnosticIssue> IoIssues { get; } = new List<DiagnosticIssue>();

        public int Passed { get; private set; }

        public int Violated { get; private set; }

        public int Judged => Passed + Violated;

        public int UndeterminedCount => Undetermined.Count;

        public Dictionary<UndeterminedBucket, int> UndeterminedByBucket { get; } =
            Enum.GetValues<UndeterminedBucket>().ToDictionary(bucket => bucket, _ => 0);

        /// <summary>约束检查自身执行失败（读库异常等）；非空时诊断结论不得为「正常」</summary>
        public string FailureMessage { get; set; }

        /// <summary>计入一条规则的判定（违规 issue 由调用方收集后去重写入 Violations）。</summary>
        public void Record(RuleEvaluation evaluation)
        {
            lock (_sync)
            {
                switch (evaluation.Verdict)
                {
                    case RuleVerdict.Passed:
                        Passed++;
                        break;
                    case RuleVerdict.Violated:
                        Violated++;
                        break;
                    default:
                        Undetermined.Add(evaluation.Issue);
                        UndeterminedByBucket[evaluation.Bucket]++;
                        break;
                }
            }
        }

        /// <summary>写入去重合并后的违规 issue（null → 空列表）。</summary>
        public void SetViolations(IEnumerable<DiagnosticIssue> violations)
        {
            lock (_sync)
            {
                Violations = violations?.ToList() ?? new List<DiagnosticIssue>();
            }
        }

        public string BuildSummaryLine() =>
            $"约束检查：已判定 {Judged} 条（违规 {Violated} 条），无法判定 {UndeterminedCount} 条" +
            $"（点名不存在 {UndeterminedByBucket[UndeterminedBucket.NameMissing]}" +
            $" / 通讯故障 {UndeterminedByBucket[UndeterminedBucket.CommFailure]}" +
            $" / 首值未到 {UndeterminedByBucket[UndeterminedBucket.WaitingForData]}" +
            $" / 其它 {UndeterminedByBucket[UndeterminedBucket.Other]}）";
    }
}
