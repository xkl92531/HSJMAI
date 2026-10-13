using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC
{
    /// <summary>
    /// 按约束运算符语义，基于真实信号值智能构造违规场景。
    /// 纯静态工具类，不依赖 DB/Redis，方便单元测试。
    /// </summary>
    public static class ConstraintViolationFabricator
    {
        public static List<ViolationScenario> FabricateViolations(
            List<PLC_IOConstraintRuleEntity> rules,
            Dictionary<long, List<PLC_IOConstraintSignalEntity>> signalsByRule,
            Dictionary<string, bool> currentValues,
            int maxPerOperator = 3,
            long? seed = null)
        {
            var rng = seed.HasValue ? new Random(unchecked((int)seed.Value)) : new Random();
            var scenarios = new List<ViolationScenario>();
            var countByOperator = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var shuffled = rules.OrderBy(_ => rng.Next()).ToList();

            foreach (var rule in shuffled)
            {
                var op = rule.Operator ?? string.Empty;
                countByOperator.TryGetValue(op, out var count);
                if (count >= maxPerOperator) continue;

                if (!signalsByRule.TryGetValue(rule.Id, out var signals) || signals.Count < 2)
                    continue;

                var scenario = op switch
                {
                    "AT_MOST_ONE" => FabricateAtMostOne(rule, signals, currentValues, rng),
                    "EXACTLY_ONE" => FabricateExactlyOne(rule, signals, currentValues, rng),
                    "ALL_TRUE" => FabricateAllTrue(rule, signals, currentValues, rng),
                    "IMPLIES" => FabricateImplies(rule, signals, currentValues),
                    "REQUIRES_0" => FabricateRequires0(rule, signals, currentValues),
                    "CHAIN_ORDER" => FabricateChainOrder(rule, signals, currentValues),
                    "CUSTOM" => FabricateCustom(rule, signals, currentValues, rng),
                    _ => null
                };

                if (scenario == null) continue;
                scenarios.Add(scenario);
                countByOperator[op] = count + 1;
            }

            return scenarios;
        }

        private static ViolationScenario? FabricateAtMostOne(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> values,
            Random rng)
        {
            var members = signals.Where(s => s.SignalRole == "Member").ToList();
            if (members.Count < 2) return null;

            var picked = members.OrderBy(_ => rng.Next()).Take(2).ToList();
            var tampered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var mutations = new List<TamperedSignal>();

            foreach (var sig in picked)
            {
                var orig = FindValue(values, sig.SignalName);
                tampered[sig.SignalName] = true;
                if (orig != true)
                    mutations.Add(new TamperedSignal { SignalName = sig.SignalName, OriginalValue = orig ?? false, TamperedValue = true });
            }

            return BuildScenario(rule, signals, values, tampered, mutations,
                rule.Description ?? string.Empty);
        }

        private static ViolationScenario? FabricateExactlyOne(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> values,
            Random rng)
        {
            var members = signals.Where(s => s.SignalRole == "Member").ToList();
            if (members.Count < 2) return null;

            var tampered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var mutations = new List<TamperedSignal>();

            bool allTrue = rng.Next(2) == 0;
            foreach (var sig in members)
            {
                var orig = FindValue(values, sig.SignalName);
                tampered[sig.SignalName] = allTrue;
                if (orig != allTrue)
                    mutations.Add(new TamperedSignal { SignalName = sig.SignalName, OriginalValue = orig ?? false, TamperedValue = allTrue });
            }

            return BuildScenario(rule, signals, values, tampered, mutations,
                rule.Description ?? string.Empty);
        }

        private static ViolationScenario? FabricateAllTrue(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> values,
            Random rng)
        {
            var sources = signals.Where(s => s.SignalRole == "Source").ToList();
            var members = signals.Where(s => s.SignalRole == "Member").ToList();
            var targets = signals.Where(s => s.SignalRole == "Target").ToList();

            var checkableSignals = members.Count > 0 ? members : targets;
            if (checkableSignals.Count == 0) return null;

            var tampered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var mutations = new List<TamperedSignal>();

            foreach (var src in sources)
            {
                var orig = FindValue(values, src.SignalName);
                tampered[src.SignalName] = true;
                if (orig != true)
                    mutations.Add(new TamperedSignal { SignalName = src.SignalName, OriginalValue = orig ?? false, TamperedValue = true });
            }

            var victim = checkableSignals[rng.Next(checkableSignals.Count)];
            var victimOrig = FindValue(values, victim.SignalName);
            tampered[victim.SignalName] = false;
            if (victimOrig != false)
                mutations.Add(new TamperedSignal { SignalName = victim.SignalName, OriginalValue = victimOrig ?? true, TamperedValue = false });

            return BuildScenario(rule, signals, values, tampered, mutations,
                rule.Description ?? string.Empty);
        }

        private static ViolationScenario? FabricateImplies(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> values)
        {
            var sources = signals.Where(s => s.SignalRole == "Source").ToList();
            var targets = signals.Where(s => s.SignalRole == "Target").ToList();
            if (sources.Count == 0 || targets.Count == 0) return null;

            var tampered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var mutations = new List<TamperedSignal>();

            foreach (var src in sources)
            {
                var orig = FindValue(values, src.SignalName);
                tampered[src.SignalName] = true;
                if (orig != true)
                    mutations.Add(new TamperedSignal { SignalName = src.SignalName, OriginalValue = orig ?? false, TamperedValue = true });
            }
            foreach (var tgt in targets)
            {
                var orig = FindValue(values, tgt.SignalName);
                tampered[tgt.SignalName] = false;
                if (orig != false)
                    mutations.Add(new TamperedSignal { SignalName = tgt.SignalName, OriginalValue = orig ?? true, TamperedValue = false });
            }

            return BuildScenario(rule, signals, values, tampered, mutations,
                rule.Description ?? string.Empty);
        }

        private static ViolationScenario? FabricateRequires0(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> values)
        {
            var sources = signals.Where(s => s.SignalRole == "Source").ToList();
            var targets = signals.Where(s => s.SignalRole == "Target").ToList();
            if (sources.Count == 0 || targets.Count == 0) return null;

            var tampered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var mutations = new List<TamperedSignal>();

            foreach (var src in sources)
            {
                var orig = FindValue(values, src.SignalName);
                tampered[src.SignalName] = false;
                if (orig != false)
                    mutations.Add(new TamperedSignal { SignalName = src.SignalName, OriginalValue = orig ?? true, TamperedValue = false });
            }
            foreach (var tgt in targets)
            {
                var orig = FindValue(values, tgt.SignalName);
                tampered[tgt.SignalName] = true;
                if (orig != true)
                    mutations.Add(new TamperedSignal { SignalName = tgt.SignalName, OriginalValue = orig ?? false, TamperedValue = true });
            }

            return BuildScenario(rule, signals, values, tampered, mutations,
                rule.Description ?? string.Empty);
        }

        private static ViolationScenario? FabricateChainOrder(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> values)
        {
            var ordered = signals
                .Where(s => s.SignalRole == "Member")
                .OrderBy(s => s.SortOrder ?? 0)
                .ToList();
            if (ordered.Count < 2) return null;

            var tampered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var mutations = new List<TamperedSignal>();

            var earlier = ordered[0];
            var later = ordered[^1];

            var earlierOrig = FindValue(values, earlier.SignalName);
            tampered[earlier.SignalName] = false;
            if (earlierOrig != false)
                mutations.Add(new TamperedSignal { SignalName = earlier.SignalName, OriginalValue = earlierOrig ?? true, TamperedValue = false });

            var laterOrig = FindValue(values, later.SignalName);
            tampered[later.SignalName] = true;
            if (laterOrig != true)
                mutations.Add(new TamperedSignal { SignalName = later.SignalName, OriginalValue = laterOrig ?? false, TamperedValue = true });

            return BuildScenario(rule, signals, values, tampered, mutations,
                rule.Description ?? string.Empty);
        }

        private static ViolationScenario? FabricateCustom(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> values,
            Random rng)
        {
            if (string.IsNullOrWhiteSpace(rule.ValidStates)) return null;

            List<Dictionary<string, bool>>? validStates;
            try
            {
                validStates = JsonSerializer.Deserialize<List<Dictionary<string, bool>>>(rule.ValidStates);
            }
            catch
            {
                return null;
            }

            if (validStates == null || validStates.Count == 0) return null;

            var firstValid = validStates[0];
            var signalNames = firstValid.Keys.ToList();
            if (signalNames.Count == 0) return null;

            var tampered = new Dictionary<string, bool>(firstValid, StringComparer.OrdinalIgnoreCase);
            var flipTarget = signalNames[rng.Next(signalNames.Count)];
            tampered[flipTarget] = !tampered[flipTarget];

            bool isStillValid = validStates.Any(vs =>
                vs.All(kv => tampered.TryGetValue(kv.Key, out var v) && v == kv.Value));

            if (isStillValid)
            {
                foreach (var name in signalNames)
                    tampered[name] = !firstValid[name];
            }

            var mutations = new List<TamperedSignal>();
            foreach (var name in signalNames)
            {
                var orig = FindValue(values, name);
                if (orig != tampered[name])
                    mutations.Add(new TamperedSignal { SignalName = name, OriginalValue = orig ?? false, TamperedValue = tampered[name] });
            }

            return BuildScenario(rule, signals, values, tampered, mutations,
                rule.Description ?? string.Empty);
        }

        private static ViolationScenario BuildScenario(
            PLC_IOConstraintRuleEntity rule,
            List<PLC_IOConstraintSignalEntity> signals,
            Dictionary<string, bool> allValues,
            Dictionary<string, bool> tampered,
            List<TamperedSignal> mutations,
            string reason)
        {
            var relevantOriginal = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var sig in signals)
            {
                var v = FindValue(allValues, sig.SignalName);
                if (v.HasValue)
                    relevantOriginal[sig.SignalName] = v.Value;
            }

            return new ViolationScenario
            {
                RuleId = rule.Id,
                RuleName = rule.RuleName ?? string.Empty,
                RuleType = rule.RuleType ?? string.Empty,
                Operator = rule.Operator ?? string.Empty,
                OriginalValues = relevantOriginal,
                TamperedValues = tampered,
                Mutations = mutations,
                ExpectedViolationReason = reason
            };
        }

        private static bool? FindValue(Dictionary<string, bool> values, string signalName)
        {
            if (values.TryGetValue(signalName, out var v)) return v;
            var match = values.FirstOrDefault(kv =>
                string.Equals(kv.Key, signalName, StringComparison.OrdinalIgnoreCase));
            return match.Key != null ? match.Value : null;
        }
    }

    public sealed class ViolationScenario
    {
        public long RuleId { get; init; }
        public string RuleName { get; init; } = string.Empty;
        public string RuleType { get; init; } = string.Empty;
        public string Operator { get; init; } = string.Empty;
        public Dictionary<string, bool> OriginalValues { get; init; } = new();
        public Dictionary<string, bool> TamperedValues { get; init; } = new();
        public List<TamperedSignal> Mutations { get; init; } = new();
        public string ExpectedViolationReason { get; init; } = string.Empty;
    }

    public sealed class TamperedSignal
    {
        public string SignalName { get; init; } = string.Empty;
        public bool OriginalValue { get; init; }
        public bool TamperedValue { get; init; }
    }
}
