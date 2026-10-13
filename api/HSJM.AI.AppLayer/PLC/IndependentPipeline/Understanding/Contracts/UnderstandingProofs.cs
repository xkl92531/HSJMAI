#nullable enable
using System;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>规则来源标记。FbContract:* 为第一期代码证明；Code:* 为本期代码证明；Review:* 为代码证明但依赖未接线输入取接口初值；
/// AI:* 为 AI 指认、代码核对通过但未证明。用户决定（2026-10-02）：不设「待确认」等级——新流程（Code:* / Review:* / AI:*）的瞬时规则
/// 满足形状守卫即判故障（Original353ActivationPolicy），延时类仍按延时处理；只有 Code:* 的互斥用来证伪已有 SAME_STATE。</summary>
public static class UnderstandingProofs
{
    public const string AiFbContract = "AI:FbContract";
    public const string AiNetwork = "AI:Network";
    public const string CodeNetwork = "Code:Network";
    public const string CodeCallSite = "Code:CallSite";
    public const string ReviewCallSiteDefaults = "Review:CallSiteDefaults";
    /// <summary>跨块执行顺序的同步（CallOrderProof）。</summary>
    public const string CodeCallOrder = "Code:CallOrder";

    public static bool IsAi(string? proof) => proof?.StartsWith("AI:", StringComparison.Ordinal) == true;

    /// <summary>本期代码证明（Code:*，可判故障）。</summary>
    public static bool IsCode(string? proof) => proof?.StartsWith("Code:", StringComparison.Ordinal) == true;

    /// <summary>AI 指认或依赖接口初值的证明（不是纯代码证明）。不再决定分级（2026-10-02 起新流程瞬时规则都判故障）。</summary>
    public static bool IsReviewOnly(string? proof) => IsAi(proof) || proof?.StartsWith("Review:", StringComparison.Ordinal) == true;

    /// <summary>本期新流程产出（AI:*、Review:*、Code:*）：AI 说明缺失时用模板，不阻断发布。</summary>
    public static bool IsNewFlow(string? proof) => IsReviewOnly(proof) || proof?.StartsWith("Code:", StringComparison.Ordinal) == true;
}
