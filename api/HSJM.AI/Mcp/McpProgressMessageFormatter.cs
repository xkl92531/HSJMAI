using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HSJM.AI.AppLayer.DiagnosisProgress;

namespace HSJM.AI.Mcp
{
    /// <summary>
    /// Formats the public, allowlisted portion of a diagnosis progress event.
    /// </summary>
    public static class McpProgressMessageFormatter
    {
        private const string RedactedMessage = "诊断阶段正在执行";

        private const RegexOptions SafeRegexOptions =
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

        // Credential words alone are normal operational language. Only explicit assignments or
        // recognized authentication header values are treated as secret-bearing.
        private static readonly Regex CredentialAssignmentPattern = new Regex(
            @"\b(?:password|passwd|pwd|token|access[_-]?token|refresh[_-]?token|id[_-]?token|api[_-]?key|account[_-]?key|accountkey|shared[_-]?access[_-]?key|sharedaccesskey|secret[_-]?access[_-]?key|client[_-]?secret|clientsecret)\b\s*[:=]\s*(?:""[^""]+""|'[^']+'|[^\s,;]+)",
            SafeRegexOptions);

        private static readonly Regex AuthenticationHeaderPattern = new Regex(
            @"(?:\bauthorization\s*[:=]\s*\S+|\bbearer\s+\S+)",
            SafeRegexOptions);

        private static readonly Regex ConnectionStringPattern = new Regex(
            @"(?:\b(?:connection[_-]?string|dsn)\b\s*[:=]\s*\S+|(?:^|[;\s])(?:server|host|data\s+source|database|initial\s+catalog|user\s+id|uid|port|accountendpoint|endpointsuffix|defaultendpointsprotocol|trusted[_ ]connection|integrated\s+security)\s*=\s*[^;\s]+)",
            SafeRegexOptions);

        private static readonly Regex UriUserInfoPattern = new Regex(
            @"(?:\b[a-z][a-z0-9+.-]*://[^\s/@]*@[A-Za-z0-9._-]+|\b[A-Za-z0-9._-]+[/\:][^@\s/;]+@[A-Za-z0-9._-]+)",
            SafeRegexOptions);

        private static readonly Regex ExceptionOrStackFramePattern = new Regex(
            @"(?:\b[\w.]+(?:exception|error)\s*:|(?:^|\r?\n)\s*at\s+(?:[\w.<>+`]+\.)*[\w.<>+`]+\s*\([^\r\n)]*\)|\b(?:[A-Za-z_]\w*\.)+[A-Za-z_]\w*\s*\([^\r\n)]*\))",
            SafeRegexOptions);

        private static readonly Regex PlcSignalAssignmentPattern = new Regex(
            @"(?:%(?:(?:I|Q|M)X?|DB|IW|QW|MW)\d+(?:\.\d+)*|\bDB\d+\.(?:DBX|DBB|DBW|DBD)\d+(?:\.\d+)?|\b(?:I|Q|M)X?\d+\.\d+)\s*[:=]\s*\S+",
            SafeRegexOptions);

        private static readonly Regex StructuredPayloadPattern = new Regex(
            @"(?:^\s*[\{\[]|\{[^{}]*[""']?[A-Za-z_]\w*[""']?\s*:|<[A-Za-z_][^>]*>|(?:^|\r?\n)\s*(?:-\s+)?[A-Za-z_][\w.-]*\s*:\s+\S+|(?:^|\r?\n)\s*-\s+\S+|\b(?:payload|raw[_-]?payload)\s*[:=]\s*\S+)",
            SafeRegexOptions);

        public static string Format(DiagnosisProgressEvent progress)
        {
            if (progress == null)
            {
                return RedactedMessage;
            }

            var message = string.IsNullOrWhiteSpace(progress.Message)
                ? RedactedMessage
                : progress.Message.Trim();
            if (IsSensitiveMessage(message))
            {
                return RedactedMessage;
            }

            var metrics = FormatMetrics(progress.Metrics);
            return metrics.Count == 0
                ? message
                : $"{message} | {string.Join(", ", metrics)}";
        }

        private static bool IsSensitiveMessage(string message) =>
            CredentialAssignmentPattern.IsMatch(message)
            || AuthenticationHeaderPattern.IsMatch(message)
            || ConnectionStringPattern.IsMatch(message)
            || UriUserInfoPattern.IsMatch(message)
            || ExceptionOrStackFramePattern.IsMatch(message)
            || PlcSignalAssignmentPattern.IsMatch(message)
            || StructuredPayloadPattern.IsMatch(message);

        private static List<string> FormatMetrics(DiagnosisProgressMetrics metrics)
        {
            var values = new List<string>();
            if (metrics == null)
            {
                return values;
            }

            if (metrics.SignalCount.HasValue)
            {
                values.Add($"SignalCount={metrics.SignalCount.Value}");
            }

            if (metrics.CompletedCount.HasValue || metrics.TotalCount.HasValue)
            {
                if (metrics.CompletedCount.HasValue && metrics.TotalCount.HasValue)
                {
                    values.Add($"CompletedCount={metrics.CompletedCount.Value}/TotalCount={metrics.TotalCount.Value}");
                }
                else if (metrics.CompletedCount.HasValue)
                {
                    values.Add($"CompletedCount={metrics.CompletedCount.Value}");
                }
                else
                {
                    values.Add($"TotalCount={metrics.TotalCount.Value}");
                }
            }

            if (metrics.RuleCount.HasValue)
            {
                values.Add($"RuleCount={metrics.RuleCount.Value}");
            }

            if (metrics.IssueCount.HasValue)
            {
                values.Add($"IssueCount={metrics.IssueCount.Value}");
            }

            if (metrics.ElapsedMs.HasValue)
            {
                values.Add($"ElapsedMs={metrics.ElapsedMs.Value}");
            }

            return values;
        }
    }
}
