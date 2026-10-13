using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed class LineUpReadPlanCompiler : ISelfSingletonAutoInject
{
    private readonly ConcurrentDictionary<
        (long BindingId, string Revision, string ContentFingerprint),
        LineUpReadPlanCompilation> cache = new();

    public LineUpReadPlanCompilation Compile(LineUpReadPlanCompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var revision = request.BindingRevision?.Trim() ?? string.Empty;
        var normalizedRequest = request with
        {
            BindingRevision = revision,
            Sources = request.Sources?.ToArray() ?? []
        };
        var contentFingerprint = ContentFingerprint(normalizedRequest);
        return cache.GetOrAdd(
            (request.BindingId, revision, contentFingerprint),
            _ => CompileCore(normalizedRequest));
    }

    public void Invalidate(long bindingId)
    {
        foreach (var key in cache.Keys.Where(x => x.BindingId == bindingId))
        {
            cache.TryRemove(key, out _);
        }
    }

    private static LineUpReadPlanCompilation CompileCore(LineUpReadPlanCompileRequest request)
    {
        var issues = new List<LineUpReadPlanIssue>();
        var items = new List<LineUpRuntimeReadItem>();
        if (request.BindingId <= 0 || request.PlcProjectId <= 0 ||
            string.IsNullOrWhiteSpace(request.BindingRevision))
        {
            issues.Add(Issue("ReadPlanBindingInvalid", "The runtime binding identity is incomplete.", string.Empty));
            return Result(request, items, issues);
        }

        var sourceGroups = (request.Sources ?? [])
            .Where(x => x is not null)
            .OrderBy(x => x.OwnerSourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.NetworkSourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.PartSourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.Role)
            .ThenBy(x => x.StepNumber)
            .ThenBy(x => x.ResolvedBlockId)
            .ThenBy(x => x.ResolvedBlockName, StringComparer.Ordinal)
            .ThenBy(x => x.ResolvedBlockInterfaceId)
            .ThenBy(x => x.VariablePath, StringComparer.Ordinal)
            .ThenBy(x => x.CanonicalPlcPath, StringComparer.Ordinal)
            .ThenBy(x => x.DataType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DataType, StringComparer.Ordinal)
            .ThenBy(x => x.NumericAddress, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.NumericAddress, StringComparer.Ordinal)
            .ThenBy(x => x.IsResolved)
            .GroupBy(
                x => x.PartSourceKey?.Trim() ?? string.Empty,
                StringComparer.Ordinal);
        foreach (var sourceGroup in sourceGroups)
        {
            var group = sourceGroup.ToArray();
            var source = group[0];
            var ownerSourceKey = source.OwnerSourceKey?.Trim() ?? string.Empty;
            var networkSourceKey = source.NetworkSourceKey?.Trim() ?? string.Empty;
            var partSourceKey = source.PartSourceKey?.Trim() ?? string.Empty;
            if (group.Length > 1 && group.Skip(1).Any(x => !SameBinding(source, x)))
            {
                issues.Add(Issue("DuplicateSourceConflict", "Conflicting runtime bindings share one immutable source identity.", partSourceKey));
                continue;
            }

            if (group.Length > 1)
            {
                issues.Add(Issue("DuplicateSourceIgnored", "A duplicate runtime source was ignored.", partSourceKey));
            }

            if (!source.IsResolved || source.ResolvedBlockId is null or <= 0 ||
                source.ResolvedBlockInterfaceId is null or <= 0 ||
                string.IsNullOrWhiteSpace(source.ResolvedBlockName))
            {
                issues.Add(Issue("RuntimeInterfaceUnresolved", "The runtime source has no exact BlockInterface binding.", partSourceKey));
                continue;
            }

            var blockName = source.ResolvedBlockName.Trim();
            var variablePath = source.VariablePath?.Trim() ?? string.Empty;
            var canonicalPlcPath = source.CanonicalPlcPath?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ownerSourceKey) || string.IsNullOrWhiteSpace(networkSourceKey) ||
                string.IsNullOrWhiteSpace(partSourceKey) || string.IsNullOrWhiteSpace(variablePath) ||
                string.IsNullOrWhiteSpace(source.DataType))
            {
                issues.Add(Issue("RuntimeSourceInvalid", "The runtime source identity, path, or type is incomplete.", partSourceKey));
                continue;
            }
            if (!string.Equals(canonicalPlcPath, $"{blockName}.{variablePath}", StringComparison.Ordinal))
            {
                issues.Add(Issue("RuntimeSourcePathMismatch", "The runtime source full PLC path does not match its exact block and variable path.", partSourceKey));
                continue;
            }

            if (source.Role == LineUpRuntimeVariableRole.StepActive)
            {
                if (!string.Equals(ownerSourceKey, networkSourceKey, StringComparison.Ordinal) ||
                    !string.Equals(ownerSourceKey, partSourceKey, StringComparison.Ordinal))
                {
                    issues.Add(Issue("StepExecutionOwnershipInvalid", "The GRAPH step execution source must own its exact runtime value.", partSourceKey));
                    continue;
                }
                if (source.StepNumber is null or <= 0)
                {
                    issues.Add(Issue("StepNumberInvalid", "The GRAPH step number must be greater than zero.", partSourceKey));
                    continue;
                }

                var expectedPath = $"RT_DATA.EXEC_BITS.EXEC_BITS[{source.StepNumber.Value - 1}]";
                if (!string.Equals(variablePath, expectedPath, StringComparison.Ordinal))
                {
                    issues.Add(Issue("StepExecutionPathMismatch", "The step execution path does not match its immutable GRAPH step number.", partSourceKey));
                    continue;
                }

                if (!IsBoolean(source.DataType))
                {
                    issues.Add(Issue("StepExecutionTypeInvalid", "The step execution interface is not Boolean-compatible.", partSourceKey));
                    continue;
                }
            }

            var numericAddress = request.AddressMode == LineUpRuntimeAddressMode.StandardDb
                ? NullIfWhiteSpace(source.NumericAddress)
                : null;
            var canonicalKey = CanonicalKey(
                request.BindingId,
                request.BindingRevision,
                source.Role,
                ownerSourceKey,
                networkSourceKey,
                partSourceKey,
                source.ResolvedBlockId.Value,
                blockName,
                source.ResolvedBlockInterfaceId.Value,
                canonicalPlcPath);
            items.Add(new(
                canonicalKey,
                ownerSourceKey,
                networkSourceKey,
                partSourceKey,
                source.Role,
                source.ResolvedBlockId.Value,
                blockName,
                source.ResolvedBlockInterfaceId.Value,
                variablePath,
                canonicalPlcPath,
                source.DataType.Trim(),
                numericAddress,
                new PLC_ExactReadRequest(canonicalKey, blockName, variablePath)));
        }

        return Result(request, items, issues);
    }

    private static LineUpReadPlanCompilation Result(
        LineUpReadPlanCompileRequest request,
        IEnumerable<LineUpRuntimeReadItem> items,
        IEnumerable<LineUpReadPlanIssue> issues) =>
        new(
            LineUpRuntimeReadPlan.Create(
                request.BindingId,
                request.BindingRevision,
                request.PlcProjectId,
                items,
                TimeSpan.FromSeconds(5)),
            issues.ToImmutableArray());

    private static bool IsBoolean(string dataType) =>
        string.Equals(dataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(dataType?.Trim(), "Boolean", StringComparison.OrdinalIgnoreCase);

    private static bool SameBinding(
        LineUpRuntimeSourceVariable left,
        LineUpRuntimeSourceVariable right) =>
        string.Equals(left.OwnerSourceKey?.Trim(), right.OwnerSourceKey?.Trim(), StringComparison.Ordinal) &&
        string.Equals(left.NetworkSourceKey?.Trim(), right.NetworkSourceKey?.Trim(), StringComparison.Ordinal) &&
        string.Equals(left.PartSourceKey?.Trim(), right.PartSourceKey?.Trim(), StringComparison.Ordinal) &&
        left.Role == right.Role &&
        left.StepNumber == right.StepNumber &&
        left.ResolvedBlockId == right.ResolvedBlockId &&
        string.Equals(left.ResolvedBlockName?.Trim(), right.ResolvedBlockName?.Trim(), StringComparison.Ordinal) &&
        left.ResolvedBlockInterfaceId == right.ResolvedBlockInterfaceId &&
        left.IsResolved == right.IsResolved &&
        string.Equals(left.VariablePath?.Trim(), right.VariablePath?.Trim(), StringComparison.Ordinal) &&
        string.Equals(left.CanonicalPlcPath?.Trim(), right.CanonicalPlcPath?.Trim(), StringComparison.Ordinal) &&
        string.Equals(left.DataType?.Trim(), right.DataType?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(NullIfWhiteSpace(left.NumericAddress), NullIfWhiteSpace(right.NumericAddress), StringComparison.OrdinalIgnoreCase);

    private static string CanonicalKey(
        long bindingId,
        string bindingRevision,
        LineUpRuntimeVariableRole role,
        string ownerSourceKey,
        string networkSourceKey,
        string partSourceKey,
        long blockId,
        string blockName,
        long interfaceId,
        string canonicalPlcPath)
    {
        var identity = string.Join('|', bindingId, bindingRevision, (int)role,
            ownerSourceKey, networkSourceKey, partSourceKey,
            blockId, blockName, interfaceId, canonicalPlcPath);
        return $"lrv2:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()}";
    }

    private static string ContentFingerprint(LineUpReadPlanCompileRequest request)
    {
        var sourceFingerprints = (request.Sources ?? [])
            .Select(SourceFingerprint)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var material = new StringBuilder();
        AppendField(material, request.PlcProjectId.ToString(CultureInfo.InvariantCulture));
        AppendField(material, ((int)request.AddressMode).ToString(CultureInfo.InvariantCulture));
        AppendField(material, sourceFingerprints.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var sourceFingerprint in sourceFingerprints)
        {
            AppendField(material, sourceFingerprint);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    private static string SourceFingerprint(LineUpRuntimeSourceVariable source)
    {
        if (source is null) return "<null-source>";
        var material = new StringBuilder();
        AppendField(material, source.OwnerSourceKey?.Trim());
        AppendField(material, source.NetworkSourceKey?.Trim());
        AppendField(material, source.PartSourceKey?.Trim());
        AppendField(material, ((int)source.Role).ToString(CultureInfo.InvariantCulture));
        AppendField(material, source.StepNumber?.ToString(CultureInfo.InvariantCulture));
        AppendField(material, source.ResolvedBlockId?.ToString(CultureInfo.InvariantCulture));
        AppendField(material, source.ResolvedBlockName?.Trim());
        AppendField(material, source.ResolvedBlockInterfaceId?.ToString(CultureInfo.InvariantCulture));
        AppendField(material, source.VariablePath?.Trim());
        AppendField(material, source.CanonicalPlcPath?.Trim());
        AppendField(material, source.DataType?.Trim());
        AppendField(material, NullIfWhiteSpace(source.NumericAddress));
        AppendField(material, source.IsResolved ? "1" : "0");
        return material.ToString();
    }

    private static void AppendField(StringBuilder material, string value)
    {
        if (value is null)
        {
            material.Append("-1:");
        }
        else
        {
            material.Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value);
        }
        material.Append('|');
    }

    private static string NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LineUpReadPlanIssue Issue(string code, string message, string sourceKey) =>
        new(code, message, sourceKey);
}
