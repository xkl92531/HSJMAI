#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

public sealed record PipelineInterfaceMember(string Path, string DataType, string Scope);

/// <summary>Rebuilds the typed top-level declaration contract already extracted by the
/// current PLC importer. No meaning is inferred from comments or signal names.</summary>
public static class PipelineInterfaceXmlBuilder
{
    public static string Build(IReadOnlyList<PipelineInterfaceMember> members)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["IN"] = "Input", ["OUT"] = "Output", ["INOUT"] = "InOut",
            ["STAT"] = "Static", ["TEMP"] = "Temp", ["CONST"] = "Constant"
        };
        var rows = members.Where(row => row is not null && !string.IsNullOrWhiteSpace(row.Path)
            && !row.Path.Contains('[') && row.Path.Count(ch => ch == '.') <= 1
            && !string.IsNullOrWhiteSpace(row.DataType) && names.ContainsKey(row.Scope))
            .GroupBy(row => names[row.Scope]);
        var sections = new List<XElement>();
        foreach (var group in rows)
        {
            var section = new XElement("Section", new XAttribute("Name", group.Key));
            var top = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in group.Where(row => !row.Path.Contains('.'))
                .GroupBy(row => row.Path, StringComparer.OrdinalIgnoreCase).Select(items => items.First()))
            {
                var member = new XElement("Member", new XAttribute("Name", row.Path),
                    new XAttribute("Datatype", row.DataType));
                top[row.Path] = member;
                section.Add(member);
            }
            foreach (var row in group.Where(row => row.Path.Contains('.'))
                .GroupBy(row => row.Path, StringComparer.OrdinalIgnoreCase).Select(items => items.First()))
            {
                var parts = row.Path.Split('.', 2);
                if (top.TryGetValue(parts[0], out var parent))
                    parent.Add(new XElement("Member", new XAttribute("Name", parts[1]),
                        new XAttribute("Datatype", row.DataType)));
            }
            sections.Add(section);
        }
        return new XElement("Interface", new XElement("Sections", sections)).ToString(SaveOptions.DisableFormatting);
    }
}
