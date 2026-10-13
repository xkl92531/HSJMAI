#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed record LineUpRuntimeLogicEvaluationContext(long BindingId, string DefinitionRevision);

public sealed record LineUpRuntimeLogicPartEvaluation(
    string CanonicalKey,
    string OwnerSourceKey,
    string NetworkSourceKey,
    string PartSourceKey,
    string PartName,
    bool IsDerived,
    LineUpTriState State,
    LineUpRuntimeQuality Quality,
    object? TypedValue,
    DateTimeOffset? SourceTimestamp,
    string? QualityCode,
    // 2026-09-12：Access(GlobalVariable) 部件回填的完整变量路径（SymbolPath join '.'），
    // 派生部件（Contact/Coil 等）为 null。快照实时条件用它显示变量名（采集开关也挂它）。
    string? VariablePath = null);

public sealed record LineUpRuntimeLogicResult(
    string CoilPartCanonicalKey,
    string OwnerSourceKey,
    string NetworkSourceKey,
    string CoilPartSourceKey,
    string Role,
    LineUpTriState State,
    LineUpRuntimeQuality Quality,
    DateTimeOffset? SourceTimestamp,
    string? QualityCode);

public sealed record LineUpRuntimeLogicEvaluation(
    ImmutableArray<LineUpRuntimeLogicPartEvaluation> PartEvaluations,
    LineUpRuntimeLogicResult Result);

public sealed class LineUpLadNetworkEvaluator
{
    public LineUpRuntimeLogicEvaluation Evaluate(
        LineUpRuntimeLogicNetworkDefinition definition,
        LineUpRuntimeLogicEvaluationContext context,
        IEnumerable<LineUpRuntimeValue> values)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        var byUid = definition.Parts.GroupBy(part => part.UId).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var rawValues = (values ?? []).GroupBy(value => value.PartSourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var inputs = BuildInputs(definition, byUid);
        var cache = new Dictionary<int, Atom>();
        var visiting = new HashSet<int>();
        var evaluations = new Dictionary<int, LineUpRuntimeLogicPartEvaluation>();

        Atom EvaluatePart(LineUpRuntimeLogicPartDefinition part)
        {
            if (cache.TryGetValue(part.UId, out var cached)) return cached;
            if (!visiting.Add(part.UId)) return Atom.Unknown("RuntimeNetworkCycle");
            Atom result;
            try
            {
                if (part.PartName == "Access")
                {
                    result = Access(part, definition, byUid, rawValues.GetValueOrDefault(part.SourceKey));
                }
                else
                {
                    Atom Input(string pin) => inputs.TryGetValue((part.UId, pin), out var source)
                        ? source.Kind switch
                        {
                            SourceKind.Powerrail => Atom.True,
                            SourceKind.Part when byUid.TryGetValue(source.UId, out var upstream) => EvaluatePart(upstream),
                            _ => Atom.Unknown("RuntimeNetworkInputMissing")
                        }
                        : Atom.Unknown("RuntimeNetworkInputMissing");
                    result = part.PartName switch
                    {
                        "Contact" => Contact(Input("in"), Input("operand"), part.Negated),
                        "Not" => Not(Input("in")),
                        "O" => Or(part.Pins.Where(pin => pin.Direction == "Input")
                            .OrderBy(pin => pin.Name, StringComparer.Ordinal).Select(pin => Input(pin.Name)).ToArray()),
                        "Eq" or "Ge" or "Gt" or "Le" or "Lt" or "Ne" => Compare(
                            part.PartName, part.TemplateValues.GetValueOrDefault("SrcType"),
                            Input("pre"), Input("in1"), Input("in2")),
                        "TrCoil" or "IlCoil" or "SvCoil" => Input("in"),
                        _ => Atom.Unknown("RuntimeNetworkPartUnsupported")
                    };
                }
            }
            finally
            {
                visiting.Remove(part.UId);
            }
            cache[part.UId] = result;
            var isDerived = part.PartName != "Access";
            evaluations[part.UId] = ToEvaluation(definition, context, part, result, isDerived);
            return result;
        }

        foreach (var part in definition.Parts.OrderBy(part => part.UId).ThenBy(part => part.SourceKey, StringComparer.Ordinal))
            EvaluatePart(part);
        var coil = definition.Parts.SingleOrDefault(part =>
            string.Equals(part.SourceKey, definition.CoilPartSourceKey, StringComparison.Ordinal));
        var coilEvaluation = coil is not null && evaluations.TryGetValue(coil.UId, out var found)
            ? found
            : new LineUpRuntimeLogicPartEvaluation(
                CanonicalKey(context, definition, definition.CoilPartSourceKey, "derived"),
                definition.OwnerSourceKey, definition.NetworkSourceKey, definition.CoilPartSourceKey,
                string.Empty, true, LineUpTriState.Unknown, LineUpRuntimeQuality.Unknown, null, null,
                "RuntimeNetworkCoilMissing");
        return new(
            evaluations.Values.OrderBy(item => item.PartSourceKey, StringComparer.Ordinal).ToImmutableArray(),
            new(coilEvaluation.CanonicalKey, definition.OwnerSourceKey, definition.NetworkSourceKey,
                definition.CoilPartSourceKey, definition.Role, coilEvaluation.State, coilEvaluation.Quality,
                coilEvaluation.SourceTimestamp, coilEvaluation.QualityCode));
    }

    internal static string CanonicalKey(
        LineUpRuntimeLogicEvaluationContext context,
        LineUpRuntimeLogicNetworkDefinition network,
        string partSourceKey,
        string evaluationKind)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "lineup-runtime-part-evaluation-v1");
        Append(hash, context.BindingId.ToString(CultureInfo.InvariantCulture));
        Append(hash, context.DefinitionRevision);
        Append(hash, network.Role);
        Append(hash, network.OwnerSourceKey);
        Append(hash, network.NetworkSourceKey);
        Append(hash, partSourceKey);
        Append(hash, evaluationKind);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static Dictionary<(int UId, string Pin), Source> BuildInputs(
        LineUpRuntimeLogicNetworkDefinition definition,
        IReadOnlyDictionary<int, LineUpRuntimeLogicPartDefinition> parts)
    {
        var result = new Dictionary<(int UId, string Pin), Source>();
        foreach (var wire in definition.Wires)
        {
            if (wire.Endpoints.IsDefaultOrEmpty) continue;
            var head = wire.Endpoints[0];
            var source = head.Kind switch
            {
                "Powerrail" => new Source(SourceKind.Powerrail, 0),
                "IdentCon" when head.UId.HasValue => new Source(SourceKind.Part, head.UId.Value),
                "NameCon" when head.UId.HasValue => new Source(SourceKind.Part, head.UId.Value),
                _ => new Source(SourceKind.Unknown, 0)
            };
            foreach (var endpoint in wire.Endpoints.Skip(1).Where(endpoint =>
                         endpoint.Kind == "NameCon" && endpoint.UId.HasValue && parts.ContainsKey(endpoint.UId.Value)))
                result[(endpoint.UId!.Value, endpoint.Name)] = source;
        }
        return result;
    }

    private static Atom Access(
        LineUpRuntimeLogicPartDefinition part,
        LineUpRuntimeLogicNetworkDefinition network,
        IReadOnlyDictionary<int, LineUpRuntimeLogicPartDefinition> parts,
        LineUpRuntimeValue[]? matches)
    {
        if (part.Scope is "LiteralConstant" or "TypedConstant")
        {
            var consumers = network.Wires.Where(wire => wire.Endpoints.Length > 1 &&
                                                        wire.Endpoints[0].Kind == "IdentCon" &&
                                                        wire.Endpoints[0].UId == part.UId)
                .SelectMany(wire => wire.Endpoints.Skip(1))
                .Where(endpoint => endpoint.Kind == "NameCon" && endpoint.UId.HasValue &&
                                   endpoint.Name is "in1" or "in2")
                .Select(endpoint => parts.GetValueOrDefault(endpoint.UId!.Value))
                .Where(consumer => consumer?.PartName is "Eq" or "Ge" or "Gt" or "Le" or "Lt")
                .Select(consumer => consumer!.TemplateValues.GetValueOrDefault("SrcType"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (consumers.Length != 1)
                return Atom.Unknown("RuntimeNetworkOperandConsumerInvalid");
            if (!LineUpLadConstantParser.TryParse(
                    part.ConstantValue, part.Scope, consumers.Single()!, out var typed, out var code))
                return Atom.Unknown(code);
            return Atom.FromTyped(typed, []);
        }
        if (!string.Equals(part.Scope, "GlobalVariable", StringComparison.Ordinal))
            return Atom.Unknown("RuntimeNetworkScopeUnsupported");
        if (matches is not { Length: 1 })
            return Atom.Unknown(matches is { Length: > 1 }
                ? "RuntimeNetworkValueDuplicate" : "RuntimeNetworkValueMissing");
        var value = matches[0];
        var state = value is { Quality: LineUpRuntimeQuality.Good, Value: bool boolean }
            ? boolean ? LineUpTriState.True : LineUpTriState.False
            : LineUpTriState.Unknown;
        return new(state, value.Quality, value.Value, [value], value.ErrorCode);
    }

    private static Atom Contact(Atom power, Atom operand, bool negated)
    {
        var operandState = operand.Quality == LineUpRuntimeQuality.Good && operand.TypedValue is bool boolean
            ? boolean ? LineUpTriState.True : LineUpTriState.False
            : LineUpTriState.Unknown;
        if (negated) operandState = operandState switch
        {
            LineUpTriState.True => LineUpTriState.False,
            LineUpTriState.False => LineUpTriState.True,
            _ => LineUpTriState.Unknown
        };
        return And(power, operand with { State = operandState,
            QualityCode = operandState == LineUpTriState.Unknown
                ? operand.QualityCode ?? "RuntimeNetworkTypeMismatch" : null });
    }

    private static Atom Not(Atom input)
    {
        var state = input.State switch
        {
            LineUpTriState.True => LineUpTriState.False,
            LineUpTriState.False => LineUpTriState.True,
            _ => LineUpTriState.Unknown
        };
        return input with
        {
            State = state,
            TypedValue = state switch
            {
                LineUpTriState.True => true,
                LineUpTriState.False => false,
                _ => null
            }
        };
    }

    private static Atom Compare(string operation, string? declaredType, Atom power, Atom left, Atom right)
    {
        if (power.State == LineUpTriState.False) return And(power, Atom.True);
        Atom comparison;
        if (left.Quality != LineUpRuntimeQuality.Good || right.Quality != LineUpRuntimeQuality.Good ||
            left.TypedValue is null || right.TypedValue is null || string.IsNullOrWhiteSpace(declaredType))
        {
            comparison = Atom.Unknown(left.QualityCode ?? right.QualityCode ?? "RuntimeNetworkComparisonUnknown",
                left.DynamicInputs.AddRange(right.DynamicInputs));
        }
        else if (!TryCompare(operation, declaredType, left.TypedValue, right.TypedValue, out var answer))
        {
            comparison = Atom.Unknown("RuntimeNetworkTypeMismatch",
                left.DynamicInputs.AddRange(right.DynamicInputs));
        }
        else
        {
            comparison = new(answer ? LineUpTriState.True : LineUpTriState.False,
                LineUpRuntimeQuality.Good, answer, Distinct(left.DynamicInputs.AddRange(right.DynamicInputs)), null);
        }
        return And(power, comparison);
    }

    private static bool TryCompare(string operation, string declaredType, object left, object right, out bool answer)
    {
        answer = false;
        int comparison;
        try
        {
            if (declaredType.Equals("Bool", StringComparison.OrdinalIgnoreCase) ||
                declaredType.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
            {
                if (left is not bool leftBool || right is not bool rightBool || operation != "Eq") return false;
                comparison = leftBool.CompareTo(rightBool);
            }
            else if (declaredType.Equals("Int", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryIntegral(left, short.MinValue, short.MaxValue, out var leftNumber) ||
                    !TryIntegral(right, short.MinValue, short.MaxValue, out var rightNumber)) return false;
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (declaredType.Equals("DInt", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryIntegral(left, int.MinValue, int.MaxValue, out var leftNumber) ||
                    !TryIntegral(right, int.MinValue, int.MaxValue, out var rightNumber)) return false;
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (declaredType.Equals("Real", StringComparison.OrdinalIgnoreCase))
            {
                if (!TrySingle(left, out var leftNumber) || !TrySingle(right, out var rightNumber)) return false;
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else if (declaredType.Equals("Time", StringComparison.OrdinalIgnoreCase))
            {
                if (left is not TimeSpan leftTime || right is not TimeSpan rightTime) return false;
                comparison = leftTime.CompareTo(rightTime);
            }
            else if (declaredType.Equals("String", StringComparison.OrdinalIgnoreCase))
            {
                if (left is not string leftText || right is not string rightText) return false;
                comparison = string.CompareOrdinal(leftText, rightText);
            }
            else if (declaredType.Equals("DateTime", StringComparison.OrdinalIgnoreCase))
            {
                if (left is DateTime leftDateTime && right is DateTime rightDateTime)
                    comparison = leftDateTime.CompareTo(rightDateTime);
                else if (left is DateTimeOffset leftOffset && right is DateTimeOffset rightOffset)
                    comparison = leftOffset.CompareTo(rightOffset);
                else return false;
            }
            else return false;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
        answer = operation switch
        {
            "Eq" => comparison == 0,
            "Ge" => comparison >= 0,
            "Gt" => comparison > 0,
            "Le" => comparison <= 0,
            "Lt" => comparison < 0,
            "Ne" => comparison != 0,
            _ => false
        };
        return operation is "Eq" or "Ge" or "Gt" or "Le" or "Lt" or "Ne";
    }

    private static bool TryIntegral(object value, decimal minimum, decimal maximum, out decimal number)
    {
        if (!TryDecimal(value, out number) || number < minimum || number > maximum ||
            decimal.Truncate(number) != number)
        {
            number = default;
            return false;
        }
        return true;
    }

    private static bool TrySingle(object value, out float number)
    {
        number = default;
        try
        {
            if (value is not (byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal)) return false;
            var source = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(source)) return false;
            number = (float)source;
            if (!float.IsFinite(number) || source != 0d && number == 0f)
            {
                number = default;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException)
        {
            number = default;
            return false;
        }
    }

    private static bool TryDecimal(object value, out decimal number)
    {
        number = default;
        try
        {
            switch (value)
            {
                case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                    number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    return true;
                case float single when float.IsFinite(single):
                    number = Convert.ToDecimal(single, CultureInfo.InvariantCulture);
                    return true;
                case double real when double.IsFinite(real):
                    number = Convert.ToDecimal(real, CultureInfo.InvariantCulture);
                    return true;
                default:
                    return false;
            }
        }
        catch (OverflowException)
        {
            number = default;
            return false;
        }
    }

    private static Atom And(params Atom[] inputs)
    {
        var determining = inputs.Any(input => input.State == LineUpTriState.False)
            ? inputs.Where(input => input.State == LineUpTriState.False).ToArray()
            : inputs;
        var state = inputs.Any(input => input.State == LineUpTriState.False)
            ? LineUpTriState.False
            : inputs.All(input => input.State == LineUpTriState.True)
                ? LineUpTriState.True : LineUpTriState.Unknown;
        return Combine(state, determining);
    }

    private static Atom Or(params Atom[] inputs)
    {
        var determining = inputs.Any(input => input.State == LineUpTriState.True)
            ? inputs.Where(input => input.State == LineUpTriState.True).ToArray()
            : inputs;
        var state = inputs.Any(input => input.State == LineUpTriState.True)
            ? LineUpTriState.True
            : inputs.All(input => input.State == LineUpTriState.False)
                ? LineUpTriState.False : LineUpTriState.Unknown;
        return Combine(state, determining);
    }

    private static Atom Combine(LineUpTriState state, IReadOnlyCollection<Atom> determining)
    {
        var inputs = Distinct(determining.SelectMany(item => item.DynamicInputs).ToImmutableArray());
        var bad = determining.FirstOrDefault(item => item.Quality != LineUpRuntimeQuality.Good);
        var quality = state != LineUpTriState.Unknown && bad is null
            ? LineUpRuntimeQuality.Good
            : bad?.Quality ?? LineUpRuntimeQuality.Unknown;
        return new(state, quality, state == LineUpTriState.Unknown ? null : state == LineUpTriState.True,
            inputs, state == LineUpTriState.Unknown ? bad?.QualityCode ?? "RuntimeNetworkUnknown" : null);
    }

    private static LineUpRuntimeLogicPartEvaluation ToEvaluation(
        LineUpRuntimeLogicNetworkDefinition network,
        LineUpRuntimeLogicEvaluationContext context,
        LineUpRuntimeLogicPartDefinition part,
        Atom atom,
        bool isDerived) => new(
        CanonicalKey(context, network, part.SourceKey, isDerived ? "derived" : "raw"),
        network.OwnerSourceKey, network.NetworkSourceKey, part.SourceKey, part.PartName, isDerived,
        atom.State, atom.Quality, atom.TypedValue, Timestamp(atom.DynamicInputs), atom.QualityCode,
        VariablePath(part));

    /// <summary>Access(GlobalVariable) 部件取 SymbolPath 拼接的完整变量路径；其余部件返回 null。</summary>
    private static string? VariablePath(LineUpRuntimeLogicPartDefinition part) =>
        part.PartName == "Access" &&
        string.Equals(part.Scope, "GlobalVariable", StringComparison.Ordinal) &&
        !part.SymbolPath.IsDefaultOrEmpty
            ? string.Join(".", part.SymbolPath)
            : null;

    private static DateTimeOffset? Timestamp(ImmutableArray<LineUpRuntimeValue> inputs)
    {
        if (inputs.IsDefaultOrEmpty || inputs.Any(input => input.SourceTimestamp is null)) return null;
        return inputs.Min(input => input.SourceTimestamp);
    }

    private static ImmutableArray<LineUpRuntimeValue> Distinct(ImmutableArray<LineUpRuntimeValue> values) =>
        values.GroupBy(value => value.PartSourceKey, StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(value => value.PartSourceKey, StringComparer.Ordinal)
            .ToImmutableArray();

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            hash.AppendData([0]);
            return;
        }
        hash.AppendData([1]);
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private enum SourceKind { Unknown, Powerrail, Part }
    private sealed record Source(SourceKind Kind, int UId);
    private sealed record Atom(
        LineUpTriState State,
        LineUpRuntimeQuality Quality,
        object? TypedValue,
        ImmutableArray<LineUpRuntimeValue> DynamicInputs,
        string? QualityCode)
    {
        public static readonly Atom True = new(LineUpTriState.True, LineUpRuntimeQuality.Good, true, [], null);
        public static Atom Unknown(string code, ImmutableArray<LineUpRuntimeValue> inputs = default) =>
            new(LineUpTriState.Unknown, LineUpRuntimeQuality.Unknown, null,
                inputs.IsDefault ? [] : inputs, code);
        public static Atom FromTyped(object? value, ImmutableArray<LineUpRuntimeValue> inputs) => new(
            value is bool boolean ? boolean ? LineUpTriState.True : LineUpTriState.False : LineUpTriState.Unknown,
            LineUpRuntimeQuality.Good, value, inputs, null);
    }
}
