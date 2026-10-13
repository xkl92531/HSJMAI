#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Diagnostics;

public sealed record PlcSourceExceptionEntry(
    string Name,
    string Category,
    string Severity,
    long LogicUnitId,
    long BlockId,
    IReadOnlyList<string> EvidenceIds,
    string SourceFingerprint,
    string SourceHash,
    string? SamplePoint,
    bool? ActiveWhen,
    string TriggerCondition,
    string ResetCondition,
    IReadOnlyList<string> IssueCodes);

public sealed record PlcDbDeclaration(string SymbolPath, string DataType, bool ExternalAccessible, string SourceHash)
{
    /// <summary>Reads a Siemens GlobalDB declaration explicitly supplied by the importer.</summary>
    public static IReadOnlyList<PlcDbDeclaration> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new PipelineException("DbDeclarationXmlRequired");
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new System.IO.StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException) { throw new PipelineException("InvalidDbDeclarationXml"); }
        var blocks = document.Descendants().Where(x => x.Name.LocalName == "SW.Blocks.GlobalDB").ToArray();
        if (blocks.Length != 1) throw new PipelineException("GlobalDbDeclarationRequired");
        var attributes = Child(blocks[0], "AttributeList");
        var name = Child(attributes, "Name")?.Value.Trim();
        var sectionRoot = Child(Child(attributes, "Interface"), "Sections");
        if (string.IsNullOrWhiteSpace(name) || sectionRoot == null) throw new PipelineException("GlobalDbDeclarationIncomplete");
        var hash = PipelineHash.Of(xml);
        return sectionRoot.Descendants().Where(x => x.Name.LocalName == "Member" &&
            !string.IsNullOrWhiteSpace((string?)x.Attribute("Name"))).Select(member =>
        {
            var path = member.AncestorsAndSelf().Where(x => x.Name.LocalName == "Member")
                .Reverse().Select(x => (string?)x.Attribute("Name") ?? "").ToArray();
            var ownFlag = Child(Child(member, "AttributeList"), "BooleanAttribute", "Name", "ExternalAccessible");
            var declaredAccessible = ownFlag != null && ownFlag.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            var ancestorBlocked = member.Ancestors().Where(x => x.Name.LocalName == "Member")
                .Select(x => Child(Child(x, "AttributeList"), "BooleanAttribute", "Name", "ExternalAccessible"))
                .Any(x => x != null && x.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));
            return new PlcDbDeclaration(name + "." + string.Join('.', path),
                (string?)member.Attribute("Datatype") ?? "", declaredAccessible && !ancestorBlocked, hash);
        }).ToArray();
    }

    private static XElement? Child(XElement? parent, string name, string? attribute = null, string? value = null) =>
        parent?.Elements().FirstOrDefault(x => x.Name.LocalName == name &&
            (attribute == null || (string?)x.Attribute(attribute) == value));
}

public static class PlcSourceExceptionCatalog
{
    public static string CompositeFingerprint(PipelineSnapshot source,
        IReadOnlyList<PlcDbDeclaration> dbDeclarations)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dbDeclarations);
        // Include the complete declaration identity, not just the LU snapshot: changing a DB XML
        // artifact invalidates the catalog even when program networks and tags are unchanged.
        return PipelineHash.Of(JsonSerializer.Serialize(new
        {
            Program = source.Fingerprint(),
            DbDeclarations = dbDeclarations.OrderBy(x => x.SymbolPath, StringComparer.Ordinal)
                .ThenBy(x => x.SourceHash, StringComparer.Ordinal)
                .ThenBy(x => x.DataType, StringComparer.Ordinal)
                .ThenBy(x => x.ExternalAccessible)
                .Select(x => new { x.SymbolPath, x.DataType, x.ExternalAccessible, x.SourceHash })
        }));
    }

    public static IReadOnlyList<PlcSourceExceptionEntry> Discover(PipelineSnapshot source,
        IReadOnlyList<PlcDbDeclaration> dbDeclarations)
    {
        ArgumentNullException.ThrowIfNull(dbDeclarations);
        var entries = Discover(source);
        var fingerprint = CompositeFingerprint(source, dbDeclarations);
        var declarations = dbDeclarations.GroupBy(x => x.SymbolPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var setTargets = entries.Where(e => e.EvidenceIds.Any(id => id.StartsWith("Part:SCoil:", StringComparison.Ordinal)))
            .Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return entries.Select(entry =>
        {
            entry = entry with { SourceFingerprint = fingerprint };
            if (!declarations.TryGetValue(entry.Name, out var matching)) return entry;
            if (matching.Length != 1) return entry with
            {
                SamplePoint = null, ActiveWhen = null,
                IssueCodes = entry.IssueCodes.Concat(["AmbiguousDbDeclaration"]).Distinct(StringComparer.Ordinal).ToArray()
            };
            if (!matching[0].DataType.Equals("Bool", StringComparison.OrdinalIgnoreCase)) return entry with
            {
                SamplePoint = null, ActiveWhen = null,
                IssueCodes = entry.IssueCodes.Concat(["DbTypeConflict", "BoolTypeUnverified"]).Distinct(StringComparer.Ordinal).ToArray()
            };
            if (!matching[0].ExternalAccessible) return entry with
            {
                SamplePoint = null, ActiveWhen = null,
                IssueCodes = entry.IssueCodes.Concat(["DbNotExternallyAccessible"]).Distinct(StringComparer.Ordinal).ToArray()
            };
            if (!setTargets.Contains(entry.Name) || matching[0].SourceHash.Length != 64 ||
                !matching[0].SourceHash.All(Uri.IsHexDigit)) return entry;
            var segments = entry.Name.Split('.');
            if (segments.Length != 4 || !segments[1].Equals("Type", StringComparison.OrdinalIgnoreCase)) return entry;
            var (category, severity, code) = segments[2] switch
            {
                "Errors" => ("PlcProgramError", "Error", "DeclaredBoolErrorBit"),
                "Warnings" => ("ProgramAlarm", "Warning", "DeclaredBoolWarningBit"),
                _ => ("Unknown", "Unknown", "")
            };
            if (category == "Unknown") return entry;
            return entry with
            {
                Category = category,
                Severity = severity,
                SamplePoint = entry.Name,
                ActiveWhen = true,
                EvidenceIds = entry.EvidenceIds.Concat(["DbMember:" + entry.Name, "DbSourceHash:" + matching[0].SourceHash]).ToArray(),
                IssueCodes = [code, "UnmappedObservationAddress"]
            };
        }).ToArray();
    }
    /// <summary>
    /// Source-only leads for exception review. A latched flag is not itself an alarm declaration;
    /// no inferred entry here is promoted to a PLC native error or assigned a severity.
    /// </summary>
    public static IReadOnlyList<PlcSourceExceptionEntry> Discover(PipelineSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var fingerprint = source.Fingerprint();
        var tags = source.Tags.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var entries = new List<PlcSourceExceptionEntry>();
        foreach (var unit in source.Units.OrderBy(u => u.BlockId).ThenBy(u => u.NetworkOrder).ThenBy(u => u.Id))
        {
            if (string.IsNullOrWhiteSpace(unit.RawContent) || !unit.RawContent.Contains("Coil", StringComparison.Ordinal))
                continue;
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(new System.IO.StringReader(unit.RawContent),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document = XDocument.Load(reader);
            }
            catch (XmlException) { continue; }
            if (document.Root?.Name.LocalName != "FlgNet") continue;

            var parts = document.Descendants().Where(x => x.Name.LocalName == "Part" &&
                (Attr(x, "Name") is "SCoil" or "RCoil") && !string.IsNullOrWhiteSpace(Attr(x, "UId"))).ToArray();
            if (parts.Length == 0) continue;
            var accesses = document.Descendants().Where(x => x.Name.LocalName == "Access" &&
                !string.IsNullOrWhiteSpace(Attr(x, "UId"))).GroupBy(x => Attr(x, "UId"), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var wires = document.Descendants().Where(x => x.Name.LocalName == "Wire").ToArray();
            foreach (var part in parts)
            {
                var partId = Attr(part, "UId");
                // A disconnected symbol and an unwired coil are not evidence of a program write.
                if (!wires.Any(w => HasConnector(w, "NameCon", partId, "in"))) continue;
                foreach (var operandWire in wires.Where(w => HasConnector(w, "NameCon", partId, "operand")))
                foreach (var ident in operandWire.Elements().Where(x => x.Name.LocalName == "IdentCon"))
                {
                    var accessId = Attr(ident, "UId");
                    if (!accesses.TryGetValue(accessId, out var access) || Attr(access, "Scope") != "GlobalVariable") continue;
                    var target = SymbolName(access);
                    if (string.IsNullOrWhiteSpace(target) || !LooksLikeExceptionLead(target)) continue;
                    var issues = new List<string> { "ProgramLatchedFlagCandidate", "AlarmContractUnverified" };
                    string? samplePoint = null;
                    if (!IsDbInternalPath(target) && tags.TryGetValue(target, out var matchingTags) && matchingTags.Length == 1 &&
                        matchingTags[0].DataType.Equals("Bool", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(matchingTags[0].Address))
                        samplePoint = matchingTags[0].Name;
                    else
                    {
                        issues.Add("BoolTypeUnverified");
                        issues.Add("UnmappedObservationAddress");
                    }
                    entries.Add(new(target, "Unknown", "Unknown", unit.Id, unit.BlockId,
                        ["LogicUnit:" + unit.Id, "Part:" + Attr(part, "Name") + ":" + partId, "Access:" + accessId], fingerprint,
                        PipelineHash.Of(unit.RawContent), samplePoint, null, "Unknown", "Unknown", issues));
                }
            }
        }
        // Preserve each source network as a separate evidence row: a second writer can change
        // meaning, so merging their S/R operations into one claim would hide that uncertainty.
        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.LogicUnitId)
            .ThenBy(e => e.EvidenceIds[1], StringComparer.Ordinal).ToArray();
    }

    private static string Attr(XElement element, string name) => (string?)element.Attribute(name) ?? "";

    private static bool HasConnector(XElement wire, string elementName, string uid, string? name = null) =>
        wire.Elements().Any(x => x.Name.LocalName == elementName && Attr(x, "UId") == uid &&
            (name == null || Attr(x, "Name").Equals(name, StringComparison.OrdinalIgnoreCase)));

    private static string SymbolName(XElement access)
    {
        var symbol = access.Elements().FirstOrDefault(x => x.Name.LocalName == "Symbol");
        if (symbol == null) return "";
        // Nested symbols and array indices require separate binding/type proof.
        var components = symbol.Elements().Where(x => x.Name.LocalName == "Component").ToArray();
        if (components.Length == 0 || components.Any(x => x.HasElements || string.IsNullOrWhiteSpace(Attr(x, "Name")))) return "";
        return string.Join('.', components.Select(x => Attr(x, "Name")));
    }

    private static bool LooksLikeExceptionLead(string name) =>
        name.Split('.').Any(segment => segment.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("Alarm", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("Fault", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("Warn", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("故障", StringComparison.Ordinal) ||
            segment.Contains("报警", StringComparison.Ordinal));

    private static bool IsDbInternalPath(string name) =>
        name.StartsWith("DB", StringComparison.OrdinalIgnoreCase) && name.Contains('.');
}
