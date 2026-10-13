using System;
using System.Linq;
using System.Text;

namespace HSJM.AI
{
    public static class HangfireQueueNames
    {
        public const string Critical = "critical";
        public const string Default = "default";
        public const string Low = "low";

        public static string CurrentServiceQueue { get; } = BuildCurrentServiceQueue();

        public static string[] BuildServerQueues()
        {
            return new[]
            {
                Critical,
                CurrentServiceQueue,
                Default,
                Low
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        }

        private static string BuildCurrentServiceQueue()
        {
            var raw = Environment.GetEnvironmentVariable("HSJM_HANGFIRE_LOCAL_QUEUE");
            if (string.IsNullOrWhiteSpace(raw))
                raw = Environment.MachineName;

            var normalized = NormalizeQueueToken(raw);
            return $"local_{normalized}";
        }

        private static string NormalizeQueueToken(string value)
        {
            var builder = new StringBuilder();
            foreach (var ch in value.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    builder.Append(ch);
                }
                else if (builder.Length == 0 || builder[builder.Length - 1] != '_')
                {
                    builder.Append('_');
                }
            }

            var result = builder.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "current" : result;
        }
    }
}
