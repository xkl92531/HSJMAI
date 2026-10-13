#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>把 StructuredText / 顺控动作里的词元原样还原成源码。</summary>
public static class SclText
{
    public static string Render(XElement parent) => Render(parent, null);

    /// <summary>unknown 非空时，认不出的 Access 与元素记入其中（写入 IrUnit.UnknownParts）。</summary>
    public static string Render(XElement parent, ISet<string>? unknown)
    {
        var sb = new StringBuilder();
        Walk(parent, sb, unknown);
        return sb.ToString().Trim();
    }

    static void Walk(XElement element, StringBuilder sb, ISet<string>? unknown)
    {
        foreach (var child in element.Elements()) Item(child, sb, unknown);
    }

    static void Item(XElement child, StringBuilder sb, ISet<string>? unknown)
    {
        switch (child.Name.LocalName)
        {
            case "Token": sb.Append((string?)child.Attribute("Text") ?? ""); break;
            case "Blank": sb.Append(' ', int.TryParse((string?)child.Attribute("Num"), out var n) ? n : 1); break;
            case "NewLine": sb.Append('\n', int.TryParse((string?)child.Attribute("Num"), out var m) ? m : 1); break;
            case "Access": sb.Append(AccessText(child, unknown)); break;
            case "LineComment": sb.Append(LineComment(child)); break;
            case "Comment": sb.Append(BlockComment(child)); break;
            case "Parameter": sb.Append(ParameterText(child, unknown)); break;
            case "LabelDeclaration":
                sb.Append(((string?)child.Descendants().FirstOrDefault(e => e.Name.LocalName == "Label")?.Attribute("Name") ?? "?") + ":");
                break;
            case "Label": sb.Append((string?)child.Attribute("Name") ?? "?"); break;
            case "Title": case "Text": case "Instance": case "TemplateValue": break; // 实例与模板值由调用头部输出
            default:
                if (child.HasElements) Walk(child, sb, unknown);
                else Leaf(child, sb, unknown);
                break;
        }
    }

    /// <summary>认不出的叶子元素：有属性或文字就明示并记录，完全为空才略过。</summary>
    static void Leaf(XElement child, StringBuilder sb, ISet<string>? unknown)
    {
        var attributes = child.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => $" {a.Name.LocalName}={a.Value}").ToArray();
        var text = child.Value.Trim();
        if (attributes.Length == 0 && text.Length == 0) return;
        sb.Append($"〔{child.Name.LocalName}{string.Concat(attributes)}{(text.Length == 0 ? "" : "：" + text)}〕");
        unknown?.Add("未识别的 SCL 元素 " + child.Name.LocalName);
    }

    static string CommentText(XElement comment) =>
        string.Concat(comment.Descendants().Where(d => d.Name.LocalName == "Text").Select(d => d.Value));

    internal static string LineComment(XElement comment) => "// " + CommentText(comment);
    static string BlockComment(XElement comment) => "(* " + CommentText(comment) + " *)";

    /// <summary>一个 Access 的源码文字：变量/常量原样；块调用（CallInfo）与博图内置指令（Instruction）写成「名[实例](实参…)」；
    /// 认不出的写成〔未识别的…〕并记入 unknown；Access 自带的注释接在后面。</summary>
    internal static string AccessText(XElement access, ISet<string>? unknown)
    {
        string text;
        var call = access.Elements().FirstOrDefault(e => e.Name.LocalName is "CallInfo" or "Instruction");
        if (call != null) text = CallText(call, unknown);
        else
        {
            var value = SymbolReader.Access(access);
            if (value.Kind is IrKinds.Var or IrKinds.Const) text = value.Name!;
            else
            {
                if (value.Kind == IrKinds.Unknown) unknown?.Add(value.Name ?? "未识别的访问");
                text = IrText.Expr(value);
            }
        }
        foreach (var comment in access.Elements().Where(e => e.Name.LocalName is "LineComment" or "Comment"))
            text += " " + (comment.Name.LocalName == "LineComment" ? LineComment(comment) : BlockComment(comment));
        return text;
    }

    static string CallText(XElement call, ISet<string>? unknown)
    {
        var name = (string?)call.Attribute("Name") ?? "?";
        var templates = call.Elements().Where(e => e.Name.LocalName == "TemplateValue")
            .Select(t => $"{(string?)t.Attribute("Name") ?? "?"}={t.Value.Trim()}").ToArray();
        var instance = call.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
        var instanceName = "";
        if (instance != null)
        {
            var symbol = instance.Elements().FirstOrDefault(e => e.Name.LocalName == "Symbol");
            instanceName = symbol != null ? SymbolReader.Symbol(symbol) : SymbolReader.Components(instance);
        }
        var head = name + (templates.Length == 0 ? "" : $"<{string.Join(",", templates)}>")
            + (instanceName.Length == 0 ? "" : $"[{instanceName}]");

        // 导出里带括号/逗号等词元时按文档顺序原样还原（引脚之间的注释也保留）；没有词元时补上括号与逗号。
        if (call.Elements().Any(e => e.Name.LocalName == "Token"))
        {
            var sb = new StringBuilder(head);
            Walk(call, sb, unknown);
            return sb.ToString();
        }
        var items = new List<string>();
        foreach (var child in call.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Parameter": items.Add(ParameterText(child, unknown)); break;
                case "Comment": case "LineComment": items.Add(BlockComment(child)); break;
                default:
                {
                    var sb = new StringBuilder();
                    Item(child, sb, unknown);
                    if (sb.ToString().Trim() is { Length: > 0 } other) items.Add(other);
                    break;
                }
            }
        }
        return $"{head}({string.Join(", ", items)})";
    }

    static string ParameterText(XElement parameter, ISet<string>? unknown)
    {
        var content = Render(parameter, unknown);
        // 实参前可能带赋值符号词元；有则沿用，没有按引脚方向补上（输出引脚为 =>）。
        var op = (string?)parameter.Attribute("Section") == "Output" ? "=>" : ":=";
        foreach (var candidate in new[] { ":=", "=>" })
            if (content.StartsWith(candidate)) { op = candidate; content = content[candidate.Length..].TrimStart(); break; }
        return $"{(string?)parameter.Attribute("Name") ?? "?"} {op} {content}";
    }
}
