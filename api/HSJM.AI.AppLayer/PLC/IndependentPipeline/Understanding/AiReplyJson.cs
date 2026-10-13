#nullable enable
using System;
using System.Text.RegularExpressions;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>从模型回复里取 JSON 对象正文（FB 契约、普通网络、评测候选三处共用）：去掉成对的思考块；模板把 &lt;think&gt; 放进提示词时
/// 回复只含结束标签，取其后的正文；仍有未闭合的思考块、或找不到成对的花括号 → null（按不可解析处理）。</summary>
internal static class AiReplyJson
{
    static readonly Regex ThinkBlock = new("<think>.*?</think>", RegexOptions.Singleline);
    const string ThinkOpen = "<think>", ThinkClose = "</think>";

    public static string? ObjectText(string content)
    {
        var text = ThinkBlock.Replace(content, "");
        var close = text.LastIndexOf(ThinkClose, StringComparison.Ordinal);
        if (close >= 0) text = text[(close + ThinkClose.Length)..];
        if (text.Contains(ThinkOpen, StringComparison.Ordinal)) return null;
        var s = text.IndexOf('{');
        var e = text.LastIndexOf('}');
        return s < 0 || e <= s ? null : text[s..(e + 1)];
    }
}
