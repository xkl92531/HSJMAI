#nullable enable
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>评测用的最小事实。Key 是比对身份；Preconditions 为 null 表示标准答案未整理前提，不比较。</summary>
public sealed record EvalFact(string Kind, string Key, string Display, string Category, string Block, int? Network,
    IReadOnlyList<string>? Preconditions, string Origin = "", string Note = "");

/// <summary>标准答案覆盖的程序段（按块名 + 网络号）。</summary>
public sealed record EvalUnitRef(string Block, int Network);

/// <summary>标准答案文件。Units 为抽样范围：非 null 时评测只看这些程序段（含标注为零条约束的程序段）；
/// 为 null（旧文件）时按事实所在的程序段取范围。</summary>
public sealed record TruthFile(string Project, string Source, string CreatedAt, IReadOnlyList<EvalFact> Facts,
    IReadOnlyList<EvalUnitRef>? Units = null);

public static class TruthJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNameCaseInsensitive = true
    };
}

/// <summary>规则附加信息：旁路信号（前提）、IMPLIES_ANY 的目标分组、是否为延时类（时序参与比较）。</summary>
public sealed record RuleMeta(IReadOnlyList<string> Bypass, IReadOnlyList<IReadOnlyList<string>>? TargetGroups, bool Delayed = false)
{
    public static readonly RuleMeta None = new([], null);
}
