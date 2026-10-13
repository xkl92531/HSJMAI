using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace HSJM.AI.Mcp
{
    public sealed class McpDiagnosisRegistrationPlan
    {
        public Mcp_ServerEntity Keeper { get; init; }

        public IReadOnlyList<long> DuplicateIds { get; init; } = Array.Empty<long>();

        public bool IsNew { get; init; }
    }

    public static class McpDiagnosisRegistrationPlanner
    {
        public const string ServerName = "hsjm-diagnosis";

        public static McpDiagnosisRegistrationPlan Reconcile(
            IEnumerable<Mcp_ServerEntity> existingServers,
            string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
                || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("MCP diagnosis endpoint must be an absolute HTTP(S) URL.", nameof(endpoint));
            }

            var matches = (existingServers ?? Array.Empty<Mcp_ServerEntity>())
                .Where(item => item != null
                    && !item.IsDeleted
                    && string.Equals(item.ServerName, ServerName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Id)
                .ToList();

            var isNew = matches.Count == 0;
            var keeper = isNew ? new Mcp_ServerEntity() : matches[0];
            keeper.ServerName = ServerName;
            keeper.ServerType = "HTTP";
            keeper.ConnectionConfig = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["Url"] = endpointUri.AbsoluteUri,
            });
            keeper.Description = "HSJM PLC IO constraint and line stoppage diagnosis MCP service";
            keeper.IsEnabled = true;
            keeper.IsDeleted = false;
            keeper.ModifyTime = DateTime.Now;
            if (isNew)
            {
                keeper.CreateTime = DateTime.Now;
            }

            return new McpDiagnosisRegistrationPlan
            {
                Keeper = keeper,
                DuplicateIds = matches.Skip(1).Select(item => item.Id).ToArray(),
                IsNew = isNew,
            };
        }
    }
}
