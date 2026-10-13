#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record ContractDifference(string Fb, IReadOnlyList<string> OnlyA, IReadOnlyList<string> OnlyB);

/// <summary>spec §14：同一 FB 两次读出的契约是否一致。签名 = 状态 + 每条通过核对的关系（种类、输出、输入、到位变量、时间参数、报警位、
/// 是否代码证明）；不含证据编号（同一关系引用不同语句不算不一致），也不含派生来源（派生反馈与 AI 给的同一关系签名相同）。</summary>
public static class ContractSignatures
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Of(IReadOnlyDictionary<string, FbContractOutcome> outcomes) =>
        outcomes.ToDictionary(kv => kv.Key, kv => Signature(kv.Value), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ContractDifference> Diff(IReadOnlyDictionary<string, FbContractOutcome> a,
        IReadOnlyDictionary<string, FbContractOutcome> b)
    {
        var sa = Of(a);
        var sb = Of(b);
        var result = new List<ContractDifference>();
        foreach (var fb in sa.Keys.Concat(sb.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            var x = sa.TryGetValue(fb, out var xa) ? xa : ["status:未读取"];
            var y = sb.TryGetValue(fb, out var yb) ? yb : ["status:未读取"];
            var onlyA = x.Except(y, StringComparer.Ordinal).ToArray();
            var onlyB = y.Except(x, StringComparer.Ordinal).ToArray();
            if (onlyA.Length > 0 || onlyB.Length > 0) result.Add(new(fb, onlyA, onlyB));
        }
        return result;
    }

    static IReadOnlyList<string> Signature(FbContractOutcome outcome)
    {
        var lines = new List<string> { "status:" + outcome.Status };
        if (outcome.Contract is not null)
            lines.AddRange(outcome.Contract.Accepted.Select(Relation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return lines;
    }

    static string Relation(ValidatedRelation v)
    {
        var r = v.Relation;
        static string U(string? s) => (s ?? "").Trim().ToUpperInvariant();
        static string Set(IEnumerable<string> xs) => string.Join("&", xs.Select(U).Distinct().Order(StringComparer.Ordinal));
        return string.Join("|", r.Kind, Set(r.Outputs), Set(r.Inputs), U(r.PositionVar), U(r.DelayParam), U(r.Alarm), v.Proven ? "proven" : "ai");
    }
}
