using HSJM.AI.IDbService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.Mcp
{
    internal sealed class McpDiagnosisRegistrationHostedService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<McpDiagnosisRegistrationHostedService> _logger;

        public McpDiagnosisRegistrationHostedService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<McpDiagnosisRegistrationHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var endpoint = _configuration["McpDiagnosisRegistration:Endpoint"];
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException(
                    "McpDiagnosisRegistration:Endpoint is required when diagnosis registration is enabled.");
            }

            using var scope = _scopeFactory.CreateScope();
            var serverService = scope.ServiceProvider.GetRequiredService<IMcp_ServerService>();
            var toolService = scope.ServiceProvider.GetRequiredService<ITool_DefinitionService>();

            var allServers = await serverService.GetListAsync(item => !item.IsDeleted)
                .ConfigureAwait(false);
            var plan = McpDiagnosisRegistrationPlanner.Reconcile(allServers, endpoint);

            var saved = plan.IsNew
                ? await serverService.InsertAsync(plan.Keeper).ConfigureAwait(false)
                : await serverService.UpdateAsync(plan.Keeper).ConfigureAwait(false);
            if (!saved)
            {
                throw new InvalidOperationException("Failed to upsert MCP Manager service hsjm-diagnosis.");
            }

            if (plan.DuplicateIds.Count > 0)
            {
                var duplicateIds = plan.DuplicateIds.ToHashSet();
                var linkedTools = (await toolService
                        .GetListAsync(item => !item.IsDeleted && item.McpServerID.HasValue)
                        .ConfigureAwait(false))
                    .Where(item => duplicateIds.Contains(item.McpServerID.Value))
                    .ToList();

                if (linkedTools.Count > 0)
                {
                    linkedTools.ForEach(item => item.McpServerID = plan.Keeper.Id);
                    var updatedCount = await toolService.UpdateRangeAsync(linkedTools).ConfigureAwait(false);
                    if (updatedCount != linkedTools.Count)
                    {
                        throw new InvalidOperationException(
                            "Failed to migrate every tool reference from duplicate hsjm-diagnosis services.");
                    }
                }

                var deleted = await serverService.SoftDeleteByIdsAsync(plan.DuplicateIds.ToList())
                    .ConfigureAwait(false);
                if (!deleted)
                {
                    throw new InvalidOperationException("Failed to remove duplicate hsjm-diagnosis services.");
                }
            }

            _logger.LogInformation(
                "MCP Manager service {ServerName} registered: Id={ServerId}, Endpoint={Endpoint}, RemovedDuplicates={DuplicateCount}",
                McpDiagnosisRegistrationPlanner.ServerName,
                plan.Keeper.Id,
                endpoint,
                plan.DuplicateIds.Count);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
