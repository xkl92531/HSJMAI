#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed record LineUpRuntimeLogicPinDefinition(string Name, string Direction);

public sealed record LineUpRuntimeLogicPartDefinition(
    string SourceKey,
    int UId,
    string PartName,
    string Scope,
    ImmutableArray<string> SymbolPath,
    string ConstantValue,
    bool Negated,
    ImmutableSortedDictionary<string, string> Attributes,
    ImmutableSortedDictionary<string, string> TemplateValues,
    ImmutableArray<LineUpRuntimeLogicPinDefinition> Pins);

public sealed record LineUpRuntimeLogicEndpointDefinition(string Kind, int? UId, string Name);

public sealed record LineUpRuntimeLogicWireDefinition(
    string SourceKey,
    int UId,
    ImmutableArray<LineUpRuntimeLogicEndpointDefinition> Endpoints);

public sealed record LineUpRuntimeLogicNetworkDefinition(
    string SequenceSourceKey,
    string OwnerSourceKey,
    string NetworkSourceKey,
    string Role,
    string ProgrammingLanguage,
    ImmutableArray<LineUpRuntimeLogicPartDefinition> Parts,
    ImmutableArray<LineUpRuntimeLogicWireDefinition> Wires,
    string CoilPartSourceKey);

public sealed record LineUpLadNetworkCompilation(
    ImmutableArray<LineUpRuntimeLogicNetworkDefinition> Networks,
    ImmutableArray<LineUpRuntimeBindingIssueDto> Issues);

public sealed class LineUpLadNetworkCompiler
{
    private const string Blocking = "Blocking";
    private const string Warning = "Warning";
    private static readonly HashSet<string> Comparators =
        ["Eq", "Ge", "Gt", "Le", "Lt", "Ne"];
    private static readonly HashSet<string> SupportedParts =
        ["Access", "Contact", "Eq", "Ge", "Gt", "IlCoil", "Le", "Lt", "Ne", "Not", "O", "SvCoil", "TrCoil"];

    public LineUpLadNetworkCompilation Compile(LineUpPublishedGraphDefinition definition) =>
        CompileCore(definition, null, null);

    internal LineUpLadNetworkCompilation Compile(
        LineUpPublishedGraphDefinition definition,
        IReadOnlyList<LineUpRuntimeBindingTag> tags,
        IReadOnlyList<LineUpRuntimeBindingStaticOperand> operands) =>
        CompileCore(definition, tags ?? throw new ArgumentNullException(nameof(tags)),
            operands ?? throw new ArgumentNullException(nameof(operands)));

    private static LineUpLadNetworkCompilation CompileCore(
        LineUpPublishedGraphDefinition definition,
        IReadOnlyList<LineUpRuntimeBindingTag>? tags,
        IReadOnlyList<LineUpRuntimeBindingStaticOperand>? operands)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var networks = new List<LineUpRuntimeLogicNetworkDefinition>();
        var issues = new List<LineUpRuntimeBindingIssueDto>();
        foreach (var sequence in definition.Sequences.OrderBy(item => item.SequenceSourceKey, StringComparer.Ordinal))
        foreach (var owner in sequence.Steps.Concat(sequence.Transitions)
                     .OrderBy(item => item.SourceKey, StringComparer.Ordinal))
        foreach (var network in owner.LogicNetworks.OrderBy(item => item.SourceKey, StringComparer.Ordinal))
        {
            var compiled = CompileNetwork(sequence.SequenceSourceKey, owner.SourceKey, network, issues);
            networks.Add(compiled);
            if (tags is not null && operands is not null)
                ValidateOperandMetadata(compiled, tags, operands, issues);
        }

        return new(
            networks.OrderBy(item => item.SequenceSourceKey, StringComparer.Ordinal)
                .ThenBy(item => item.OwnerSourceKey, StringComparer.Ordinal)
                .ThenBy(item => item.NetworkSourceKey, StringComparer.Ordinal)
                .ToImmutableArray(),
            issues.Distinct().OrderBy(item => item.Severity, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.OwnerSourceKey, StringComparer.Ordinal)
                .ThenBy(item => item.NetworkSourceKey, StringComparer.Ordinal)
                .ThenBy(item => item.PartSourceKey, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static LineUpRuntimeLogicNetworkDefinition CompileNetwork(
        string sequenceSourceKey,
        string ownerSourceKey,
        LineUpPublishedLogicNetworkDefinition source,
        ICollection<LineUpRuntimeBindingIssueDto> issues)
    {
        var parts = source.Parts.OrderBy(item => item.UId).ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            .Select(item => CompilePart(item, source, ownerSourceKey, issues))
            .ToImmutableArray();
        var wires = source.Wires.OrderBy(item => item.UId).ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            .Select(item => new LineUpRuntimeLogicWireDefinition(
                item.SourceKey,
                item.UId,
                CanonicalEndpoints(item.Endpoints.Select(endpoint => new LineUpRuntimeLogicEndpointDefinition(
                    endpoint.Kind, endpoint.UId, endpoint.Name)).ToImmutableArray())))
            .ToImmutableArray();

        var allUids = parts.Select(item => item.UId).Concat(wires.Select(item => item.UId)).ToArray();
        if (allUids.Any(uid => uid <= 0) || allUids.Distinct().Count() != allUids.Length)
            Add(issues, "RuntimeNetworkDuplicateUId", ownerSourceKey, source.SourceKey, string.Empty, Blocking);

        var byUid = parts.GroupBy(item => item.UId).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var connectedPins = new HashSet<(int UId, string Pin)>();
        var edges = new Dictionary<int, HashSet<int>>();
        foreach (var wire in wires)
        {
            if (wire.Endpoints.Length < 2 || !TrySource(wire.Endpoints[0], byUid, out var sourceUid))
            {
                Add(issues, "RuntimeNetworkWireEndpointInvalid", ownerSourceKey, source.SourceKey,
                    string.Empty, Blocking);
                continue;
            }
            if (wire.Endpoints[0].Kind == "NameCon")
                connectedPins.Add((wire.Endpoints[0].UId!.Value, wire.Endpoints[0].Name));
            for (var index = 1; index < wire.Endpoints.Length; index++)
            {
                var endpoint = wire.Endpoints[index];
                if (endpoint.Kind == "OpenCon") continue;
                if (endpoint.Kind != "NameCon" || endpoint.UId is not { } targetUid ||
                    !byUid.TryGetValue(targetUid, out var target) ||
                    !ValidTargetPin(target, endpoint.Name))
                {
                    Add(issues, "RuntimeNetworkWireEndpointInvalid", ownerSourceKey, source.SourceKey,
                        string.Empty, Blocking);
                    continue;
                }
                if (!connectedPins.Add((targetUid, endpoint.Name)))
                    Add(issues, "RuntimeNetworkPinAmbiguous", ownerSourceKey, source.SourceKey,
                        target.SourceKey, Blocking);
                if (sourceUid is { } from)
                {
                    if (!edges.TryGetValue(from, out var targets)) edges[from] = targets = [];
                    targets.Add(targetUid);
                }
            }
        }

        foreach (var part in parts.Where(item => item.PartName != "Access" && SupportedParts.Contains(item.PartName)))
        foreach (var pin in part.Pins)
        {
            if (!connectedPins.Contains((part.UId, pin.Name)))
                Add(issues, "RuntimeNetworkPinMissing", ownerSourceKey, source.SourceKey, part.SourceKey, Blocking);
        }

        if (HasCycle(parts.Select(item => item.UId), edges))
            Add(issues, "RuntimeNetworkCycle", ownerSourceKey, source.SourceKey, string.Empty, Blocking);

        var coils = parts.Where(item => item.PartName is "TrCoil" or "IlCoil" or "SvCoil").ToArray();
        if (coils.Length != 1)
            Add(issues, coils.Length == 0 ? "RuntimeNetworkCoilMissing" : "RuntimeNetworkCoilAmbiguous",
                ownerSourceKey, source.SourceKey, string.Empty, Blocking);
        else if (!CoilMatchesRole(coils[0].PartName, source.Role))
            Add(issues, "RuntimeNetworkCoilRoleInvalid", ownerSourceKey, source.SourceKey,
                coils[0].SourceKey, Blocking);

        ValidateConstants(parts, wires, ownerSourceKey, source.SourceKey, issues);
        return new(sequenceSourceKey, ownerSourceKey, source.SourceKey, source.Role,
            source.ProgrammingLanguage, parts, wires, coils.Length == 1 ? coils[0].SourceKey : string.Empty);
    }

    private static LineUpRuntimeLogicPartDefinition CompilePart(
        LineUpPublishedLogicPartDefinition source,
        LineUpPublishedLogicNetworkDefinition network,
        string ownerSourceKey,
        ICollection<LineUpRuntimeBindingIssueDto> issues)
    {
        var pins = Pins(source);
        if (!SupportedParts.Contains(source.PartName))
            Add(issues, "RuntimeNetworkPartUnsupported", ownerSourceKey, network.SourceKey,
                source.SourceKey, Warning);
        else if (source.PartName != "Access")
        {
            var declared = source.PinNames.OrderBy(item => item, StringComparer.Ordinal).ToArray();
            var expected = pins.Select(item => item.Name).OrderBy(item => item, StringComparer.Ordinal).ToArray();
            if (pins.Any(pin => string.Equals(pin.Direction, "Unknown", StringComparison.Ordinal)) ||
                !declared.SequenceEqual(expected, StringComparer.Ordinal))
                Add(issues, "RuntimeNetworkPinSchemaInvalid", ownerSourceKey, network.SourceKey,
                    source.SourceKey, Blocking);
        }

        return new(
            source.SourceKey,
            source.UId,
            source.PartName,
            source.Scope,
            source.SymbolPath.ToImmutableArray(),
            source.ConstantValue,
            source.Negated,
            source.Attributes.ToImmutableSortedDictionary(StringComparer.Ordinal),
            source.TemplateValues.ToImmutableSortedDictionary(StringComparer.Ordinal),
            pins);
    }

    private static ImmutableArray<LineUpRuntimeLogicPinDefinition> Pins(LineUpPublishedLogicPartDefinition part)
    {
        static ImmutableArray<LineUpRuntimeLogicPinDefinition> Fixed(string[] inputs, string[] outputs) =>
            inputs.Select(name => new LineUpRuntimeLogicPinDefinition(name, "Input"))
                .Concat(outputs.Select(name => new LineUpRuntimeLogicPinDefinition(name, "Output")))
                .ToImmutableArray();
        return part.PartName switch
        {
            "Access" => [],
            "Contact" => Fixed(["in", "operand"], ["out"]),
            // 2026-09-10：补 Ne（不等于）——TIA 比较器六件套 Eq/Ne/Ge/Gt/Le/Lt，
            // 原表漏 Ne 导致 FB461_RobotGraph 13 个 Ne 部件全部 PartUnsupported → factory 构建失败。
            "Eq" or "Ge" or "Gt" or "Le" or "Lt" or "Ne" => Fixed(["pre", "in1", "in2"], ["out"]),
            "Not" => Fixed(["in"], ["out"]),
            "O" when part.TemplateValues.TryGetValue("Card", out var cardinality) &&
                     int.TryParse(cardinality, NumberStyles.None, CultureInfo.InvariantCulture, out var count) &&
                     count is >= 2 and <= 32 => Fixed(
                // 2026-09-10：TIA 的 OR 盒最多 32 输入；原上限 4 导致 Card=5/6 的真实网络
                // 落到 Unknown 引脚 → PinSchemaInvalid/PinMissing/WireEndpointInvalid 连锁 Blocking，
                // FB460/FB461/FB161 三条绑定全部因此无法启用。求值器按 Input 引脚折叠，无基数限制。
                Enumerable.Range(1, count).Select(index => $"in{index}").ToArray(), ["out"]),
            "IlCoil" or "SvCoil" or "TrCoil" => Fixed(["in"], []),
            _ => part.PinNames.Select(name => new LineUpRuntimeLogicPinDefinition(name, "Unknown"))
                .ToImmutableArray()
        };
    }

    private static bool TrySource(
        LineUpRuntimeLogicEndpointDefinition endpoint,
        IReadOnlyDictionary<int, LineUpRuntimeLogicPartDefinition> parts,
        out int? sourceUid)
    {
        sourceUid = null;
        if (endpoint.Kind == "Powerrail" && endpoint.UId is null) return true;
        if (endpoint.UId is not { } uid || !parts.TryGetValue(uid, out var part)) return false;
        if (endpoint.Kind == "IdentCon" && part.PartName == "Access")
        {
            sourceUid = uid;
            return true;
        }
        if (endpoint.Kind == "NameCon" && part.Pins.Any(pin => pin.Direction == "Output" &&
                                                               string.Equals(pin.Name, endpoint.Name, StringComparison.Ordinal)))
        {
            sourceUid = uid;
            return true;
        }
        if (endpoint.Kind == "NameCon" && !SupportedParts.Contains(part.PartName) &&
            part.Pins.Any(pin => string.Equals(pin.Name, endpoint.Name, StringComparison.Ordinal)))
        {
            sourceUid = uid;
            return true;
        }
        return false;
    }

    private static bool ValidTargetPin(LineUpRuntimeLogicPartDefinition part, string pinName) =>
        part.Pins.Any(pin => string.Equals(pin.Name, pinName, StringComparison.Ordinal) &&
                              (string.Equals(pin.Direction, "Input", StringComparison.Ordinal) ||
                               !SupportedParts.Contains(part.PartName)));

    private static ImmutableArray<LineUpRuntimeLogicEndpointDefinition> CanonicalEndpoints(
        ImmutableArray<LineUpRuntimeLogicEndpointDefinition> endpoints)
    {
        if (endpoints.IsDefaultOrEmpty) return [];
        return endpoints.Take(1).Concat(endpoints.Skip(1)
                .OrderBy(endpoint => endpoint.Kind, StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.UId)
                .ThenBy(endpoint => endpoint.Name, StringComparer.Ordinal))
            .ToImmutableArray();
    }

    private static void ValidateConstants(
        ImmutableArray<LineUpRuntimeLogicPartDefinition> parts,
        ImmutableArray<LineUpRuntimeLogicWireDefinition> wires,
        string ownerSourceKey,
        string networkSourceKey,
        ICollection<LineUpRuntimeBindingIssueDto> issues)
    {
        var byUid = parts.GroupBy(item => item.UId).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        foreach (var access in parts.Where(item => item.PartName == "Access" &&
                                                   item.Scope is "LiteralConstant" or "TypedConstant"))
        {
            var consumers = wires.Where(wire => wire.Endpoints.Length > 1 &&
                                                wire.Endpoints[0].Kind == "IdentCon" &&
                                                wire.Endpoints[0].UId == access.UId)
                .SelectMany(wire => wire.Endpoints.Skip(1))
                .Where(endpoint => endpoint.Kind == "NameCon" && endpoint.UId.HasValue)
                .Select(endpoint => byUid.GetValueOrDefault(endpoint.UId!.Value))
                .Where(part => part is not null)
                .ToArray();
            var declaredTypes = consumers
                .Where(part => Comparators.Contains(part!.PartName))
                .Select(part => part!.TemplateValues.GetValueOrDefault("SrcType"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (declaredTypes.Length != 1)
            {
                Add(issues, "RuntimeNetworkConstantTypeMismatch", ownerSourceKey, networkSourceKey,
                    access.SourceKey, Warning);
                continue;
            }
            if (!LineUpLadConstantParser.TryParse(access.ConstantValue, access.Scope, declaredTypes[0]!,
                    out _, out var issueCode))
                Add(issues, issueCode, ownerSourceKey, networkSourceKey, access.SourceKey, Warning);
        }
    }

    private static void ValidateOperandMetadata(
        LineUpRuntimeLogicNetworkDefinition network,
        IReadOnlyList<LineUpRuntimeBindingTag> tags,
        IReadOnlyList<LineUpRuntimeBindingStaticOperand> operands,
        ICollection<LineUpRuntimeBindingIssueDto> issues)
    {
        var byUid = network.Parts.GroupBy(item => item.UId).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        foreach (var access in network.Parts.Where(item => item.PartName == "Access"))
        {
            var consumers = network.Wires
                .Where(wire => wire.Endpoints.Length > 1 &&
                               wire.Endpoints[0].Kind == "IdentCon" &&
                               wire.Endpoints[0].UId == access.UId)
                .SelectMany(wire => wire.Endpoints.Skip(1))
                .Where(endpoint => endpoint.Kind == "NameCon" && endpoint.UId.HasValue &&
                                   byUid.ContainsKey(endpoint.UId.Value))
                .Select(endpoint => (Part: byUid[endpoint.UId!.Value], Pin: endpoint.Name))
                .ToArray();
            var validConsumers = consumers.Where(consumer =>
                    consumer.Part.PartName == "Contact" && consumer.Pin == "operand" ||
                    Comparators.Contains(consumer.Part.PartName) && consumer.Pin is "in1" or "in2")
                .ToArray();
            if (consumers.Length > 0 && validConsumers.Length != consumers.Length)
            {
                Add(issues, "RuntimeNetworkOperandConsumerInvalid", network.OwnerSourceKey,
                    network.NetworkSourceKey, access.SourceKey, Warning);
                continue;
            }

            if (access.Scope is "LiteralConstant" or "TypedConstant")
            {
                var matches = operands.Where(item => MetadataMatches(item.SequenceSourceKey, item.Role,
                        item.OwnerSourceKey, item.NetworkSourceKey, item.PartSourceKey, network, access) &&
                    string.Equals(item.Scope, access.Scope, StringComparison.Ordinal) &&
                    string.Equals(item.ConstantValue, access.ConstantValue, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                    Add(issues, "RuntimeNetworkOperandMetadataMissing", network.OwnerSourceKey,
                        network.NetworkSourceKey, access.SourceKey, Warning);
                continue;
            }

            if (!string.Equals(access.Scope, "GlobalVariable", StringComparison.Ordinal)) continue;
            var tagMatches = tags.Where(item => MetadataMatches(item.SequenceSourceKey, item.Role,
                item.OwnerSourceKey, item.NetworkSourceKey, item.PartSourceKey, network, access)).ToArray();
            if (tagMatches.Length != 1 || string.IsNullOrWhiteSpace(tagMatches[0].DataType))
            {
                Add(issues, "RuntimeNetworkOperandMetadataMissing", network.OwnerSourceKey,
                    network.NetworkSourceKey, access.SourceKey, Warning);
                continue;
            }
            var dataType = tagMatches[0].DataType!;
            if (validConsumers.Any(consumer => !ConsumerAccepts(consumer.Part, dataType)))
                Add(issues, "RuntimeNetworkOperandTypeMismatch", network.OwnerSourceKey,
                    network.NetworkSourceKey, access.SourceKey, Warning);
        }
    }

    private static bool MetadataMatches(
        string? sequenceSourceKey,
        string? role,
        string? ownerSourceKey,
        string? networkSourceKey,
        string? partSourceKey,
        LineUpRuntimeLogicNetworkDefinition network,
        LineUpRuntimeLogicPartDefinition access) =>
        string.Equals(sequenceSourceKey, network.SequenceSourceKey, StringComparison.Ordinal) &&
        string.Equals(role, BindingRole(network.Role), StringComparison.Ordinal) &&
        string.Equals(ownerSourceKey, network.OwnerSourceKey, StringComparison.Ordinal) &&
        string.Equals(networkSourceKey, network.NetworkSourceKey, StringComparison.Ordinal) &&
        string.Equals(partSourceKey, access.SourceKey, StringComparison.Ordinal);

    private static string BindingRole(string role) => role switch
    {
        "TransitionCondition" => "TransitionCondition",
        "StepInterlock" => "Interlock",
        "StepSupervision" => "Supervision",
        _ => "Unknown"
    };

    private static bool ConsumerAccepts(LineUpRuntimeLogicPartDefinition consumer, string dataType)
    {
        if (consumer.PartName == "Contact")
            return dataType.Equals("Bool", StringComparison.OrdinalIgnoreCase) ||
                   dataType.Equals("Boolean", StringComparison.OrdinalIgnoreCase);
        return Comparators.Contains(consumer.PartName) &&
               consumer.TemplateValues.TryGetValue("SrcType", out var declaredType) &&
               string.Equals(dataType, declaredType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCycle(IEnumerable<int> nodes, IReadOnlyDictionary<int, HashSet<int>> edges)
    {
        var states = new Dictionary<int, int>();
        bool Visit(int node)
        {
            if (states.GetValueOrDefault(node) == 1) return true;
            if (states.GetValueOrDefault(node) == 2) return false;
            states[node] = 1;
            if (edges.TryGetValue(node, out var targets) && targets.Any(Visit)) return true;
            states[node] = 2;
            return false;
        }
        return nodes.Any(Visit);
    }

    private static bool CoilMatchesRole(string coil, string role) => (coil, role) switch
    {
        ("TrCoil", "TransitionCondition") => true,
        ("IlCoil", "StepInterlock") => true,
        ("SvCoil", "StepSupervision") => true,
        _ => false
    };

    private static void Add(
        ICollection<LineUpRuntimeBindingIssueDto> issues,
        string code,
        string owner,
        string network,
        string part,
        string severity) => issues.Add(new(code, owner, severity, network, part));
}

internal static class LineUpLadConstantParser
{
    private static readonly Regex Time = new(
        "^(?:T|TIME)#(?<segments>(?:[0-9]+(?:D|H|MS|M|S))+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Segment = new(
        "(?<value>[0-9]+)(?<unit>MS|D|H|M|S)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryParse(
        string value,
        string scope,
        string declaredType,
        out object? typedValue,
        out string issueCode)
    {
        typedValue = null;
        issueCode = "RuntimeNetworkConstantInvalid";
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            if (string.Equals(declaredType, "Time", StringComparison.OrdinalIgnoreCase))
            {
                if (scope != "TypedConstant")
                {
                    issueCode = "RuntimeNetworkConstantTypeMismatch";
                    return false;
                }
                var match = Time.Match(text);
                if (!match.Success) return false;
                var units = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    { ["D"] = 5, ["H"] = 4, ["M"] = 3, ["S"] = 2, ["MS"] = 1 };
                var previous = int.MaxValue;
                long milliseconds = 0;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var segments = Segment.Matches(match.Groups["segments"].Value);
                if (segments.Count == 0 || string.Concat(segments.Select(item => item.Value)) != match.Groups["segments"].Value)
                    return false;
                foreach (Match segment in segments)
                {
                    var unit = segment.Groups["unit"].Value;
                    var order = units[unit];
                    if (order >= previous || !seen.Add(unit) ||
                        !long.TryParse(segment.Groups["value"].Value, NumberStyles.None,
                            CultureInfo.InvariantCulture, out var number)) return false;
                    previous = order;
                    var factor = unit.ToUpperInvariant() switch
                    {
                        "D" => 86_400_000L, "H" => 3_600_000L, "M" => 60_000L,
                        "S" => 1_000L, _ => 1L
                    };
                    milliseconds = checked(milliseconds + checked(number * factor));
                }
                typedValue = TimeSpan.FromMilliseconds(milliseconds);
                return true;
            }
            if (scope != "LiteralConstant")
            {
                issueCode = "RuntimeNetworkConstantTypeMismatch";
                return false;
            }
            if (declaredType.Equals("Bool", StringComparison.OrdinalIgnoreCase) ||
                declaredType.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
            {
                if (!bool.TryParse(text, out var boolean)) return false;
                typedValue = boolean;
                return true;
            }
            if (declaredType.Equals("Int", StringComparison.OrdinalIgnoreCase))
            {
                if (!short.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    return false;
                typedValue = integer;
                return true;
            }
            if (declaredType.Equals("DInt", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    return false;
                typedValue = integer;
                return true;
            }
            if (declaredType.Equals("Real", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var source) ||
                    !double.IsFinite(source)) return false;
                var real = (float)source;
                if (!float.IsFinite(real) || source != 0d && real == 0f) return false;
                typedValue = real;
                return true;
            }
            if (declaredType.Equals("String", StringComparison.OrdinalIgnoreCase))
            {
                typedValue = text;
                return true;
            }
            issueCode = "RuntimeNetworkConstantTypeMismatch";
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
