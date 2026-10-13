#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>spec §9：按名字或猜测识别的旧规则族。每族一个开关（默认全开）；下线前只关开关、不删代码。</summary>
public static class NameRuleFamily
{
    /// <summary>阀门 FB 引脚名配对：QFwd/QBkd 互斥、IWP/IHP 互斥与到位、FwdAuto/BkdAuto 间接线圈（Legacy/PldStructuralConstraintAugmenter，未打 family 标记的产出）。</summary>
    public const string PinNameAugment = "PinNameAugment";
    /// <summary>同一调用恰好 3 个只接一个常开 %I 的输入、同字节 → 互斥（没有被调块程序体证据的猜测）。</summary>
    public const string PositionGroupMutex = "PositionGroupMutex";
    /// <summary>同网络触点集合相同的两个线圈 → SAME_STATE。</summary>
    public const string CoilSyncState = "CoilSyncState";
    /// <summary>屏蔽态 SCoil 报警 → MUST_BE_FALSE（按占位符名 AlwaysTRUE/AlwaysFALSE/Null 判屏蔽态）。</summary>
    public const string MaskedAlarm = "MaskedAlarm";
    /// <summary>按接口前两个 Bool 输入/输出的位置配对到位反馈（Original353FeedbackGroups）。</summary>
    public const string InterfacePositionFeedback = "InterfacePositionFeedback";
    /// <summary>调用实例输出与其取反线圈 EXACTLY_ONE（Original353SafetyRules）。</summary>
    public const string SafetyInversion = "SafetyInversion";
    /// <summary>多成员互斥改写为保持输出蕴含（Original353HoldingOutputRepair）。</summary>
    public const string HoldingOutputRepair = "HoldingOutputRepair";
    public static readonly IReadOnlyList<string> GeneratorFamilies =
        [PinNameAugment, PositionGroupMutex, CoilSyncState, MaskedAlarm, InterfacePositionFeedback, SafetyInversion, HoldingOutputRepair];

    /// <summary>全部族。用户 2026-10-02 决定一键排查不改，所以只含生成器族。</summary>
    public static readonly IReadOnlyList<string> All = GeneratorFamilies;
}

/// <summary>名字规则开关。配置 IndependentPlcPipeline:NameRules:&lt;族名&gt;，只有显式写 false 才关。</summary>
public sealed class NameRuleSwitches
{
    public const string ConfigurationSection = "IndependentPlcPipeline:NameRules";

    public static NameRuleSwitches AllOn { get; } = new([]);
    public static NameRuleSwitches AllOff { get; } = new(NameRuleFamily.All);

    public NameRuleSwitches(IEnumerable<string> disabled) => Disabled = disabled.ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<string> Disabled { get; }

    public bool IsOn(string family) => !Disabled.Contains(family);

    public NameRuleSwitches Without(string family) => new(Disabled.Append(family));

    /// <summary>关掉的生成器族（序数排序，用 + 连接）；全开时为空串。</summary>
    public string GeneratorSignature =>
        string.Join("+", NameRuleFamily.GeneratorFamilies.Where(f => !IsOn(f)).Order(StringComparer.Ordinal));

    /// <summary>缺省或无法解析一律视为开（R1）。</summary>
    public static NameRuleSwitches FromConfiguration(IConfiguration configuration) =>
        new(NameRuleFamily.All.Where(f => bool.TryParse(configuration[$"{ConfigurationSection}:{f}"], out var on) && !on));
}

/// <summary>旧补全器 JSON → 规则；每条按其 family 字段（缺省为 defaultFamily）决定取舍。转换与改动前的生成器逐字一致。</summary>
public static class NameRuleFamilies
{
    public static IReadOnlyList<PipelineRule> ToRules(string augmenterJson, string defaultFamily, NameRuleSwitches switches)
    {
        using var document = JsonDocument.Parse(augmenterJson);
        var rules = new List<PipelineRule>();
        foreach (var r in document.RootElement.EnumerateArray())
        {
            var family = r.TryGetProperty("family", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : defaultFamily;
            if (!switches.IsOn(family)) continue;
            rules.Add(new(r.GetProperty("operator").GetString()!,
                r.GetProperty("signals").EnumerateArray()
                    .Select(s => new PipelineSignal(s.GetProperty("signalName").GetString()!, s.GetProperty("signalRole").GetString()!)).ToArray(),
                r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""));
        }
        return rules;
    }
}
