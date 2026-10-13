#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

public sealed record IfaceMember(string Section, string Name, string DataType, string? StartValue, string? Comment,
    IReadOnlyList<IfaceMember> Children);

/// <summary>块接口读取：只取根元素下 Sections 的直接子 Section（多重背景成员内嵌的 Sections 不是本块接口）。</summary>
public static class InterfaceReader
{
    static bool Is(XElement e, string name) => e.Name.LocalName == name;

    public static IReadOnlyList<IfaceMember> Read(string interfaceXml)
    {
        if (string.IsNullOrWhiteSpace(interfaceXml)) return [];
        XElement root;
        try { root = XElement.Parse(interfaceXml); } catch (XmlException) { return []; }
        var sections = Is(root, "Sections") ? root : root.Elements().FirstOrDefault(e => Is(e, "Sections"));
        if (sections == null) return [];
        return sections.Elements().Where(e => Is(e, "Section"))
            .SelectMany(s => s.Elements().Where(e => Is(e, "Member")).Select(m => Member((string?)s.Attribute("Name") ?? "", m, 0)))
            .ToArray();
    }

    static IfaceMember Member(string section, XElement m, int depth)
    {
        var start = m.Elements().FirstOrDefault(e => Is(e, "StartValue"))?.Value;
        var comment = m.Elements().FirstOrDefault(e => Is(e, "Comment"))?.Descendants()
            .FirstOrDefault(e => Is(e, "MultiLanguageText") || Is(e, "Text"))?.Value.Trim();
        IReadOnlyList<IfaceMember> children = depth >= 3 ? [] : m.Elements().Where(e => Is(e, "Member")).Select(c => Member(section, c, depth + 1)).ToArray();
        return new IfaceMember(section, (string?)m.Attribute("Name") ?? "", (string?)m.Attribute("Datatype") ?? "",
            start, string.IsNullOrWhiteSpace(comment) ? null : comment, children);
    }

    public static string Render(IReadOnlyList<IfaceMember> members, int maxPerSection = 60)
    {
        var sb = new StringBuilder();
        foreach (var group in members.GroupBy(m => m.Section))
        {
            sb.Append($"- {group.Key}:\n");
            foreach (var m in group.Take(maxPerSection))
            {
                sb.Append($"  - {m.Name} : {m.DataType}");
                if (m.StartValue != null) sb.Append($" 默认={m.StartValue}");
                if (m.Comment != null) sb.Append($" // {m.Comment}");
                if (m.Children.Count > 0)
                    sb.Append(" { " + string.Join(", ", m.Children.Take(24).Select(c => $"{c.Name}:{c.DataType}")) + (m.Children.Count > 24 ? ", …" : "") + " }");
                sb.Append('\n');
            }
            if (group.Count() > maxPerSection) sb.Append($"  - …另有 {group.Count() - maxPerSection} 个成员\n");
        }
        return sb.ToString().TrimEnd();
    }
}
