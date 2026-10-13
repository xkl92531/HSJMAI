#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353ManualModeRead(long ProjectId, string Status, string Reason)
{
    public string ProjectName { get; init; } = "";
    public IReadOnlyDictionary<string, bool?> Values { get; init; } = new Dictionary<string, bool?>();
    public IReadOnlyList<string> ManualGroups { get; init; } = [];
    public IReadOnlyList<Original353ModeConflict> Conflicts { get; init; } = [];
}

/// <summary>Reads only source-proven ManualMod/AutoMod DB bits through the registered IGW alias.</summary>
public sealed class Original353ManualModeReader(
    IConfiguration configuration, PostgresPipelineStore testStore,
    IPLC_ProjectService projects, IPLC_IgwRegistrationService registrations,
    IIotService iot, ILogger<Original353ManualModeReader> logger) : ISelfScopedAutoInject
{
    public static IReadOnlyDictionary<string, string> BuildAliases(string plcIp, IgwLedgerSnapshot ledger,
        IReadOnlyList<Original353ManualModeBinding> bindings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            foreach (var path in new[] { binding.VariablePath, binding.AutoVariablePath, binding.AutoRunVariablePath }.Where(path => path is not null))
            {
                var key = binding.BlockName + "." + path;
                result.TryAdd(key, IgwReadAliasResolver.ResolveDb(plcIp, ledger, binding.BlockName, path));
            }
        }
        return result;
    }

    public async Task<Original353ManualModeRead> ReadAsync(long projectId,
        IReadOnlyList<Original353ManualModeBinding> bindings, CancellationToken token)
    {
        if (!configuration.GetValue<bool>("TestConstraintV2:ManualModeReadEnabled"))
            return new(projectId, "Unknown", "ManualModeReadDisabled");
        try { _ = testStore.ConnectionString; }
        catch (Exception) { return new(projectId, "Unknown", "TestDatabaseRequired"); }
        if (bindings is null || bindings.Count is 0 or > 32
            || bindings.Any(binding => string.IsNullOrWhiteSpace(binding.BlockName)
                || binding.BlockName.Length > 160
                || binding.VariablePath != "General.ManualMod"
                || binding.AutoVariablePath != "General.AutoMod"
                || binding.AutoRunVariablePath is not (null or "General.AutoRunMod")))
            return new(projectId, "Unknown", "SourceProvenModeBindingsRequired");
        token.ThrowIfCancellationRequested();
        try
        {
            var project = projects.GetById(projectId);
            if (project is null || project.IsDeleted || project.Id != projectId
                || string.IsNullOrWhiteSpace(project.PlcIp) || string.IsNullOrWhiteSpace(project.IotUrl))
                return new(projectId, "Unknown", "ProjectGatewayUnavailable");
            var ledger = await registrations.GetLedgerForReadAsync(projectId).WaitAsync(token);
            if (ledger is null || bindings.Any(binding => !ledger.DeviceKindByBlock.TryGetValue(binding.BlockName, out var kind)
                || kind != IgwDeviceKind.S7Plus))
                return new(projectId, "Unknown", "ModeDbNotRegisteredAsS7Plus");
            var aliases = BuildAliases(project.PlcIp.Trim(), ledger, bindings);
            var response = await iot.ReadTagsAsList(project.IotUrl.Trim(), aliases.Values.ToList(),
                keepPolling: false, requestTimeout: TimeSpan.FromSeconds(3)).WaitAsync(token);
            if (response is not { IsSuccess: true, BackResult: not null })
                return new(projectId, "Unknown", "ModeReadFailed");
            var byAlias = response.BackResult.Where(row => row is not null && !string.IsNullOrWhiteSpace(row.TagName))
                .GroupBy(row => row.TagName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var values = new Dictionary<string, bool?>(StringComparer.Ordinal);
            foreach (var (key, alias) in aliases)
            {
                values[key] = byAlias.TryGetValue(alias, out var rows) && rows.Length == 1 && rows[0].IsGood
                    && PlcLiveSignals.TryConvertToBool(rows[0].Value, out var value) ? value : null;
            }
            var status = Original353ManualModePolicy.Classify(bindings, values);
            return new(projectId, status, status == "Unknown" ? "ModeBitsUnavailable" : "SourceProvenModeBitsRead") {
                ProjectName = project.ProjectName ?? "", Values = values,
                Conflicts = Original353ManualModePolicy.FindConflicts(bindings, values),
                ManualGroups = Original353ManualModePolicy.ManualGroups(bindings, values)
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XMLV2 manual-mode read failed for project {ProjectId}", projectId);
            return new(projectId, "Unknown", "ModeReadFailed");
        }
    }
}
