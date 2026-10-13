using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    internal static class StationCodeHeuristics
    {
        private static readonly Regex ConservativeStationRegex = new Regex(
            @"(?<![A-Z0-9])(?:工位\s*[-_]?\s*\d{1,4}|\d{1,3}#|(?:OP|ST|STA|LINE|CELL|UNIT|WS)\s*[-_]?\s*\d{1,4})(?![A-Z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EmbeddedStationRegex = new Regex(
            @"(?<![A-Z0-9])(?:OP|ST|STA|LINE|CELL|UNIT|WS)\s*[-_]?\s*\d{1,4}(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> AllowedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OP", "ST", "STA", "LINE", "CELL", "UNIT", "WS"
        };

        private static readonly HashSet<string> ComponentPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CYL", "CYLINDER", "YV", "SV", "VALVE", "VLV", "MOTOR", "MTR",
            "SEN", "SENSOR", "ACT", "ACTUATOR", "ALM", "ERR", "POS", "CLAMP", "GRIP"
        };

        public static IEnumerable<string> ExtractStationCodes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in ConservativeStationRegex.Matches(text))
            {
                if (!match.Success)
                {
                    continue;
                }

                var normalized = NormalizeStationCode(match.Value);
                if (!IsLikelyStationCode(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                yield return normalized;
            }

            foreach (Match match in EmbeddedStationRegex.Matches(text))
            {
                if (!match.Success)
                {
                    continue;
                }

                var normalized = NormalizeStationCode(match.Value);
                if (!IsLikelyStationCode(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                yield return normalized;
            }
        }

        public static bool IsLikelyStationCode(string stationCode)
        {
            if (string.IsNullOrWhiteSpace(stationCode))
            {
                return false;
            }

            var normalized = NormalizeStationCode(stationCode);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (Regex.IsMatch(normalized, @"^\d{1,3}#$", RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (Regex.IsMatch(normalized, @"^工位\d{1,4}$", RegexOptions.IgnoreCase))
            {
                return true;
            }

            var prefix = new string(normalized.TakeWhile(ch => !char.IsDigit(ch)).ToArray());
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return false;
            }

            if (ComponentPrefixes.Contains(prefix))
            {
                return false;
            }

            return AllowedPrefixes.Contains(prefix);
        }

        public static int GetStationPriority(string stationCode)
        {
            var normalized = NormalizeStationCode(stationCode);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return 0;
            }

            if (normalized.StartsWith("OP", StringComparison.OrdinalIgnoreCase))
            {
                return 6;
            }

            if (normalized.StartsWith("工位", StringComparison.OrdinalIgnoreCase))
            {
                return 5;
            }

            if (normalized.EndsWith("#", StringComparison.OrdinalIgnoreCase))
            {
                return 4;
            }

            if (normalized.StartsWith("LINE", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            if (normalized.StartsWith("CELL", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("UNIT", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("ST", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("WS", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return 1;
        }

        public static string GetAliasFamilyKey(string stationCode)
        {
            var normalized = NormalizeStationCode(stationCode);
            if (!IsLikelyStationCode(normalized))
            {
                return string.Empty;
            }

            var prefixed = Regex.Match(normalized, @"^(OP|ST|STA|LINE|CELL|UNIT|WS)(\d{1,4})$", RegexOptions.IgnoreCase);
            if (prefixed.Success && int.TryParse(prefixed.Groups[2].Value, out var prefixedValue))
            {
                return $"{prefixed.Groups[1].Value.ToUpperInvariant()}:{prefixedValue}";
            }

            var chinese = Regex.Match(normalized, @"^工位(\d{1,4})$", RegexOptions.IgnoreCase);
            if (chinese.Success && int.TryParse(chinese.Groups[1].Value, out var chineseValue))
            {
                return $"NUM:{chineseValue}";
            }

            var hash = Regex.Match(normalized, @"^(\d{1,3})#$", RegexOptions.IgnoreCase);
            if (hash.Success && int.TryParse(hash.Groups[1].Value, out var hashValue))
            {
                return $"NUM:{hashValue}";
            }

            return normalized;
        }

        public static string NormalizeStationCode(string stationCode)
        {
            if (string.IsNullOrWhiteSpace(stationCode))
                return string.Empty;

            var cleaned = Regex.Replace(stationCode.Trim().ToUpperInvariant(), @"[\s_-]+", string.Empty);

            // 零填充：OP20 → OP020, ST5 → ST005（统一3位数字）
            var m = Regex.Match(cleaned, @"^([A-Z]+)(\d{1,3})$");
            if (m.Success && AllowedPrefixes.Contains(m.Groups[1].Value))
            {
                return m.Groups[1].Value + m.Groups[2].Value.PadLeft(3, '0');
            }

            return cleaned;
        }
    }
}