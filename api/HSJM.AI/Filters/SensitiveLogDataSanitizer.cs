using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HSJM.AI.Filters
{
    internal static class SensitiveLogDataSanitizer
    {
        private const string Redacted = "REDACTED";

        private static readonly HashSet<string> SensitiveNames = new HashSet<string>(
            new[]
            {
                "password",
                "pwd",
                "authorization",
                "accesstoken",
                "refreshtoken",
                "securitykey",
                "apikey",
            },
            StringComparer.OrdinalIgnoreCase);

        internal static Dictionary<string, object> SanitizeActionArguments(
            IDictionary<string, object> actionArguments)
        {
            var token = actionArguments == null
                ? new JObject()
                : JObject.FromObject(actionArguments);

            RedactToken(token);
            return token.ToObject<Dictionary<string, object>>()
                ?? new Dictionary<string, object>();
        }

        internal static string SanitizeHeaders(string headers)
        {
            if (string.IsNullOrEmpty(headers))
            {
                return headers;
            }

            return Regex.Replace(
                headers,
                @"(?im)^(Authorization|Cookie|Set-Cookie)\s*:\s*.*$",
                "$1: REDACTED");
        }

        private static void RedactToken(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (SensitiveNames.Contains(property.Name))
                    {
                        property.Value = Redacted;
                    }
                    else
                    {
                        RedactToken(property.Value);
                    }
                }

                return;
            }

            if (token is JArray array)
            {
                foreach (var item in array)
                {
                    RedactToken(item);
                }
            }
        }
    }
}
