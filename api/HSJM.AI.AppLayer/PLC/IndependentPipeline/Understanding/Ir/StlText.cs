#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>把以 XML 导出的 STL（StatementList）还原成源码：每个 StlStatement 一行，行内各项以单个空格连接。
/// StlStatement 以外的子元素：注释照写，其它非空元素明示为〔元素名〕并记录，完全为空的略过。</summary>
public static class StlText
{
    public static string Render(XElement statementList, ISet<string>? unknown = null)
    {
        var lines = new List<string>();
        foreach (var child in statementList.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "StlStatement":
                    lines.Add(string.Join(" ", child.Elements().Select(item => Item(item, unknown)).Where(t => t.Length > 0)));
                    break;
                case "LineComment": case "Comment": lines.Add(Comment(child)); break;
                default:
                    if (!child.HasElements && child.Value.Trim().Length == 0) break;
                    lines.Add($"〔{child.Name.LocalName}〕");
                    unknown?.Add("未识别的 STL 元素 " + child.Name.LocalName);
                    break;
            }
        }
        return string.Join("\n", lines);
    }

    static string Item(XElement item, ISet<string>? unknown)
    {
        switch (item.Name.LocalName)
        {
            case "StlToken":
            {
                var text = (string?)item.Attribute("Text");
                if (!string.IsNullOrEmpty(text)) return text;
                var inner = item.Elements().FirstOrDefault();
                if (inner != null) return (string?)inner.Attribute("Text") is { Length: > 0 } innerText ? innerText : inner.Name.LocalName;
                return item.Value.Trim() is { Length: > 0 } value ? value : "〔StlToken〕";
            }
            case "Access": return SclText.AccessText(item, unknown);
            case "LabelDeclaration":
                return LabelName(item.Descendants().FirstOrDefault(e => e.Name.LocalName == "Label") ?? item) + ":";
            case "Label": return LabelName(item); // 跳转指令的目标标签
            case "LineComment": case "Comment": return Comment(item);
            case "Blank": case "NewLine": return "";
            default:
                unknown?.Add("未识别的 STL 元素 " + item.Name.LocalName);
                return $"〔{item.Name.LocalName}〕";
        }
    }

    static string Comment(XElement comment)
    {
        var texts = comment.Descendants().Where(d => d.Name.LocalName == "Text").Select(d => d.Value).ToArray();
        return "// " + (texts.Length > 0 ? string.Concat(texts) : comment.Value.Trim());
    }

    static string LabelName(XElement label) => (string?)label.Attribute("Name") is { Length: > 0 } name ? name : "?";
}
