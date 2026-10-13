#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353ManualModeBinding(string BlockName, string VariablePath,
    string AutoVariablePath = "General.AutoMod", string? AutoRunVariablePath = null);

/// <summary>Finds actual FB3002 manual input wiring and its instance DB; names alone are insufficient.</summary>
public static class Original353ManualModeBindings
{
    private static bool Is(XElement value, string name) => value.Name.LocalName == name;
    private static XElement? Child(XElement? value, string name)
        => value?.Elements().FirstOrDefault(item => Is(item, name));
    private static XElement? Desc(XContainer? value, string name)
        => value?.Descendants().FirstOrDefault(item => Is(item, name));
    private static string? BlockName(XElement block)
        => Child(Child(block, "AttributeList"), "Name")?.Value.Trim();

    public static IReadOnlyList<Original353ManualModeBinding> Read(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            throw new PipelineException("UploadedArchiveUnavailable");
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length is 0 or > 1024 || entries.Sum(entry => entry.Length) > 512_000_000)
            throw new PipelineException("UploadedXmlBudgetExceeded");
        var docs = new List<OriginalXmlDocument>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Length > 32_000_000) throw new PipelineException("UploadedXmlBudgetExceeded");
            using var reader = new StreamReader(entry.Open());
            docs.Add(new(entry.FullName, reader.ReadToEnd()));
        }
        return Extract(docs);
    }

    public static IReadOnlyList<Original353ManualModeBinding> Extract(IReadOnlyList<OriginalXmlDocument> files)
    {
        var docs = new List<XDocument>();
        foreach (var file in files ?? [])
        {
            if (string.IsNullOrWhiteSpace(file?.Content)) continue;
            try
            {
                using var reader = XmlReader.Create(new StringReader(file.Content), new XmlReaderSettings {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null
                });
                docs.Add(XDocument.Load(reader));
            }
            catch (XmlException) { /* Unreadable XML cannot prove a mode binding. */ }
        }
        var boolUdts = docs.Select(doc => Desc(doc, "SW.Types.PlcStruct"))
            .Where(block => block is not null)
            .Select(block => (Block: block!, Name: BlockName(block!)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name)
                && item.Block.Descendants().Any(member => Is(member, "Member")
                    && (string?)member.Attribute("Name") == "ManualMod"
                    && (string?)member.Attribute("Datatype") == "Bool")
                && item.Block.Descendants().Any(member => Is(member, "Member")
                    && (string?)member.Attribute("Name") == "AutoMod"
                    && (string?)member.Attribute("Datatype") == "Bool"))
            .Select(item => item.Name!).ToHashSet(StringComparer.Ordinal);

        var instanceDbs = docs.Select(doc => Desc(doc, "SW.Blocks.InstanceDB"))
            .Where(block => block is not null)
            .Select(block => {
                var attrs = Child(block!, "AttributeList");
                var general = Desc(Child(attrs, "Interface"), "Member");
                return new {
                    Name = BlockName(block!),
                    Of = Child(attrs, "InstanceOfName")?.Value.Trim(),
                    GeneralUdt = general?.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "General") == true
                        ? ((string?)general.Attribute("Datatype"))?.Trim('"') : null
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Of)
                && item.GeneralUdt is not null && boolUdts.Contains(item.GeneralUdt))
            .ToArray();

        var verifiedFbs = docs.Select(doc => Desc(doc, "SW.Blocks.FB"))
            .Where(block => block is not null && HasVerifiedValveModeWire(block!))
            .Select(block => BlockName(block!))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        var runUdts = docs.Select(doc => Desc(doc, "SW.Types.PlcStruct"))
            .Where(block => block is not null && block.Descendants().Any(member => Is(member, "Member")
                && (string?)member.Attribute("Name") == "AutoRunMod" && (string?)member.Attribute("Datatype") == "Bool"))
            .Select(block => BlockName(block!)).ToHashSet(StringComparer.Ordinal);
        return instanceDbs.Where(db => verifiedFbs.Contains(db.Of!))
            .Select(db => new Original353ManualModeBinding(db.Name!, "General.ManualMod", "General.AutoMod",
                runUdts.Contains(db.GeneralUdt) ? "General.AutoRunMod" : null))
            .Distinct().OrderBy(item => item.BlockName, StringComparer.Ordinal).ToArray();
    }

    private static bool HasVerifiedValveModeWire(XElement block)
    {
        foreach (var net in block.Descendants().Where(item => Is(item, "FlgNet")))
        {
            var access = net.Descendants().Where(item => Is(item, "Access"))
                .Where(item => item.Attribute("UId") is not null)
                .ToDictionary(item => (string)item.Attribute("UId")!, Symbol);
            foreach (var call in net.Descendants().Where(item => Is(item, "Call")
                && (string?)Desc(item, "CallInfo")?.Attribute("Name") == "FB3002_Valve"))
            {
                var uid = (string?)call.Attribute("UId");
                if (uid is null) continue;
                if (DirectInput(net, access, uid, "ManualMod") == "General.ManualMod"
                    && DirectInput(net, access, uid, "AutoRunMod") == "General.AutoMod")
                    return true;
            }
        }
        return false;
    }

    private static string? DirectInput(XElement net, IReadOnlyDictionary<string, string> access,
        string callUid, string pin)
    {
        var wires = net.Descendants().Where(wire => Is(wire, "Wire")
            && wire.Elements().Any(connector => Is(connector, "NameCon")
                && (string?)connector.Attribute("UId") == callUid
                && (string?)connector.Attribute("Name") == pin)).ToArray();
        if (wires.Length != 1) return null;
        var connectors = wires[0].Elements().ToArray();
        if (connectors.Length != 2) return null;
        var ident = connectors.SingleOrDefault(item => Is(item, "IdentCon"));
        var identUid = (string?)ident?.Attribute("UId");
        return identUid is not null && access.TryGetValue(identUid, out var symbol) ? symbol : null;
    }

    private static string Symbol(XElement access)
    {
        var symbol = Child(access, "Symbol");
        return symbol is null ? "" : string.Join('.', symbol.Elements()
            .Where(item => Is(item, "Component"))
            .Select(item => (string?)item.Attribute("Name") ?? ""));
    }
}
