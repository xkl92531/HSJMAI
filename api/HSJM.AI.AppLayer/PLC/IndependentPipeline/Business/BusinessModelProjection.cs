#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Lossless presentation of already anonymized source; never performs semantic inference.</summary>
public static class BusinessModelProjection
{
    public const string Version = "business-model-projection-1";
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly IReadOnlyDictionary<string, string[]> Columns = new Dictionary<string, string[]>(StringComparer.Ordinal) {
        ["Access"] = ["UId", "Scope", "SymbolRef"], ["Part"] = ["UId", "Name"], ["Call"] = ["UId"],
        ["Wire"] = ["UId"], ["NameCon"] = ["UId", "Name"], ["IdentCon"] = ["UId"], ["OpenCon"] = ["UId"],
        ["Component"] = ["Name"], ["Negated"] = ["Name"], ["TemplateValue"] = ["Name", "Type"],
        ["Member"] = ["Name", "Datatype", "Remanence", "Accessibility"], ["Section"] = ["Name"],
        ["Parameter"] = ["Name", "Section", "Type"], ["CallInfo"] = ["Name", "BlockType"],
        ["BooleanAttribute"] = ["Name", "SystemDefined"], ["Subelement"] = ["Path"]
    };

    public static string Build(BusinessDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var text = description.ModelText;
        var changed = false;
        foreach (var evidence in description.Evidence.Where(e => e.Kind is "SourceStructure" or "InterfaceStructure"))
        {
            // Only source facts already selected by the renderer are model-visible. Do not expose raw symbols.
            var original = "[" + evidence.Id + "] " + evidence.Text;
            if (!text.Contains(original, StringComparison.Ordinal)) continue;
            var start = evidence.Text.IndexOf('<');
            var end = evidence.Text.LastIndexOf('>');
            if (start < 0 || end < start) continue;
            var xml = evidence.Text[start..(end + 1)];
            try
            {
                var root = Parse(xml);
                // Script/GRAPH/unknown documents keep their exact anonymous XML as a conservative fallback.
                if (root.Name.LocalName is not ("FlgNet" or "Interface")) continue;
                var compact = Encode(root);
                var replacement = "[" + evidence.Id + "] " + evidence.Text[..start] + "\n" + compact + evidence.Text[(end + 1)..];
                text = text.Replace(original, replacement, StringComparison.Ordinal);
                changed = true;
            }
            catch (XmlException) { /* Preserve malformed evidence verbatim, including existing limitations. */ }
        }
        if (!changed) return text;
        var legend = new StringBuilder();
        legend.AppendLine("Lossless source tables (" + Version + "). UIds are local to EACH FlgNet/source LU; never join UIds across networks. SymbolRef is the canonical identity across networks. Each Wire lists ALL endpoints, not a Boolean equation; preserve EN/pre, negations, branch fanout and execution order.");
        legend.AppendLine("Tree syntax: Tag[positional attributes;extraName=value]{ordered children}; omitted trailing columns and ~ mean absent (not false). =value is exact text; quoted values use JSON escaping. Unlisted tags/attributes remain literal. XML=quotedXML preserves special XML nodes. DEF @Dnumber=tree defines an exact reusable subtree; @Dnumber expands to that entire subtree (no omitted metadata). Wire endpoints: @digits is IdentCon(UId); digits.pin is NameCon(UId,Name). These shorthands apply only to attribute-only endpoints; extended endpoints retain full trees. Access Scope codes LV/GV/LC/TC mean LocalVariable/GlobalVariable/LiteralConstant/TypedConstant. Tag aliases A=Access, W=Wire, C=Component (quoted tags remain literal). Quoted Scope codes remain literal. DEF names are local to each evidence table, never shared between tables. Fixed columns:");
        foreach (var pair in Columns) legend.Append(pair.Key).Append('[').AppendJoin(' ', pair.Value).Append("] ");
        legend.AppendLine();
        return legend.Append(text).ToString();
    }

    /// <summary>Encode the XML infoset without dropping attributes, text, children, or namespace identities.</summary>
    public static string EncodeCompact(string xml) => Encode(Parse(xml));

    /// <summary>Inverse of EncodeCompact; attribute ordering and empty-element spelling are not semantic.</summary>
    public static string DecodeCompact(string compact)
    {
        if (compact.StartsWith("XML=", StringComparison.Ordinal))
            return JsonSerializer.Deserialize<string>(compact[4..]) ?? throw new FormatException("Missing XML value.");
        var parser = new CompactReader(compact);
        parser.ReadDefinitions();
        var result = parser.ReadElement();
        parser.RequireEnd();
        return result.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null
        });
        return XElement.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string Encode(XElement root)
    {
        // CDATA/comments/PI are uncommon in anonymous source. Keeping their original XML is lossless.
        if (root.DescendantNodes().Any(n => n is not XElement && n.GetType() != typeof(XText)))
            return "XML=" + JsonSerializer.Serialize(root.ToString(SaveOptions.DisableFormatting), JsonOptions);
        // Deduplicate only structurally identical subtrees, never merely similar declarations.
        var plain = root.DescendantsAndSelf().ToDictionary(e => e, e => {
            var builder = new StringBuilder(); Write(e, builder); return builder.ToString();
        });
        var repeated = plain.GroupBy(p => p.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1 && (g.Key.Length - 8) * (g.Count() - 1) > 24)
            .OrderBy(g => g.Key.Length).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var references = new Dictionary<XElement, string>();
        for (var i = 0; i < repeated.Length; i++)
            foreach (var entry in repeated[i]) references[entry.Key] = "@D" + (i + 1);
        var output = new StringBuilder();
        for (var i = 0; i < repeated.Length; i++)
        {
            output.Append("DEF @D").Append(i + 1).Append('=');
            Write(repeated[i].First().Key, output, references, true);
            output.AppendLine();
        }
        Write(root, output, references);
        return output.ToString();
    }

    private static void Write(XElement element, StringBuilder output, IReadOnlyDictionary<XElement, string>? references = null, bool inline = false)
    {
        if (!inline && references != null && references.TryGetValue(element, out var reference))
        {
            output.Append(reference);
            return;
        }
        var name = element.Name.ToString();
        var uid = (string?)element.Attribute("UId");
        var pin = (string?)element.Attribute("Name");
        if (!element.Nodes().Any() && uid is { Length: > 0 } && uid.All(char.IsAsciiDigit))
        {
            if (name == "IdentCon" && element.Attributes().Count() == 1) { output.Append('@').Append(uid); return; }
            if (name == "NameCon" && element.Attributes().Count() == 2 && pin is { Length: > 0 } && Token(pin) == pin)
            { output.Append(uid).Append('.').Append(pin); return; }
        }
        output.Append(name switch { "Access" => "A", "Wire" => "W", "Component" => "C",
            "A" or "W" or "C" => JsonSerializer.Serialize(name, JsonOptions), _ => Token(name) });
        var columns = Columns.TryGetValue(name, out var found) ? found : [];
        var values = columns.Select(c => (string?)element.Attribute(c)).ToArray();
        // Codes are introduced only for recognized source enum values; unknown scopes stay literal.
        if (name == "Access" && values.Length > 1)
            values[1] = values[1] switch { "LocalVariable" => "LV", "GlobalVariable" => "GV", "LiteralConstant" => "LC", "TypedConstant" => "TC", _ => values[1] };
        var last = Array.FindLastIndex(values, v => v != null);
        var extras = element.Attributes().Where(a => !columns.Contains(a.Name.ToString(), StringComparer.Ordinal)).ToArray();
        if (last >= 0 || extras.Length > 0)
        {
            output.Append('[');
            for (var i = 0; i <= last; i++)
            {
                if (i > 0) output.Append(' ');
                var literalScopeCode = name == "Access" && i == 1 && (string?)element.Attribute("Scope") is "LV" or "GV" or "LC" or "TC";
                output.Append(values[i] == null ? "~" : literalScopeCode ? JsonSerializer.Serialize(values[i], JsonOptions) : Token(values[i]!));
            }
            if (extras.Length > 0)
            {
                output.Append(';');
                foreach (var attribute in extras)
                    output.Append(Token(attribute.Name.ToString())).Append('=').Append(Token(attribute.Value)).Append(' ');
                output.Length--;
            }
            output.Append(']');
        }
        if (!element.Nodes().Any()) return;
        output.Append('{');
        var first = true;
        foreach (var node in element.Nodes())
        {
            if (!first) output.Append(name is "Parts" or "Wires" ? '\n' : ' ');
            first = false;
            if (node is XElement child) Write(child, output, references);
            else output.Append('=').Append(Token(((XText)node).Value));
        }
        output.Append('}');
    }

    private static string Token(string value) => value.Length > 0 && value.All(c =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or ':' or '+' or '-' or '.' or '/' or '#')
        ? value : JsonSerializer.Serialize(value, JsonOptions);

    private sealed class CompactReader(string source)
    {
        private int position;
        private readonly Dictionary<string, XElement> definitions = new(StringComparer.Ordinal);
        private char Current => position < source.Length ? source[position] : '\0';
        private void Space() { while (char.IsWhiteSpace(Current)) position++; }
        private void Expect(char character)
        {
            Space();
            if (Current != character) throw new FormatException($"Expected {character} at {position}.");
            position++;
        }
        public void RequireEnd()
        {
            Space();
            if (position != source.Length) throw new FormatException("Trailing compact source.");
        }
        private string Value()
        {
            Space();
            var start = position;
            if (Current == '"')
            {
                position++;
                while (Current != '\0')
                {
                    var c = source[position++];
                    if (c == '\\') { if (Current == '\0') break; position++; }
                    else if (c == '"') return JsonSerializer.Deserialize<string>(source[start..position])!;
                }
                throw new FormatException("Unterminated quoted value.");
            }
            while (Current != '\0' && !char.IsWhiteSpace(Current) && Current is not ('[' or ']' or '{' or '}' or ';' or '=')) position++;
            if (position == start) throw new FormatException($"Missing value at {position}.");
            return source[start..position];
        }
        public void ReadDefinitions()
        {
            Space();
            while (source.AsSpan(position).StartsWith("DEF ", StringComparison.Ordinal))
            {
                position += 4;
                var reference = Value();
                Expect('=');
                definitions.Add(reference, ReadElement());
                Space();
            }
        }
        public XElement ReadElement()
        {
            Space();
            var literalTag = Current == '"';
            var name = Value();
            if (!literalTag) name = name switch { "A" => "Access", "W" => "Wire", "C" => "Component", _ => name };
            if (name.StartsWith("@D", StringComparison.Ordinal))
                return definitions.TryGetValue(name, out var definition) ? new XElement(definition)
                    : throw new FormatException("Undefined subtree " + name);
            if (name.StartsWith('@') && name.Length > 1 && name[1..].All(char.IsAsciiDigit))
                return new XElement("IdentCon", new XAttribute("UId", name[1..]));
            var dot = name.IndexOf('.');
            if (dot > 0 && name[..dot].All(char.IsAsciiDigit))
                return new XElement("NameCon", new XAttribute("UId", name[..dot]), new XAttribute("Name", name[(dot + 1)..]));
            var element = new XElement(XName.Get(name));
            var columns = Columns.TryGetValue(name, out var found) ? found : [];
            Space();
            if (Current == '[')
            {
                position++;
                var index = 0;
                Space();
                while (Current is not (']' or ';' or '\0'))
                {
                    if (index >= columns.Length) throw new FormatException("Too many fixed columns.");
                    var absent = Current == '~';
                    var literalValue = Current == '"';
                    var value = Value();
                    if (!literalValue && name == "Access" && index == 1)
                        value = value switch { "LV" => "LocalVariable", "GV" => "GlobalVariable", "LC" => "LiteralConstant", "TC" => "TypedConstant", _ => value };
                    if (!absent) element.Add(new XAttribute(columns[index], value));
                    index++;
                    Space();
                }
                if (Current == ';')
                {
                    position++;
                    Space();
                    while (Current is not (']' or '\0'))
                    {
                        var attribute = Value();
                        Expect('=');
                        element.Add(new XAttribute(XName.Get(attribute), Value()));
                        Space();
                    }
                }
                Expect(']');
            }
            Space();
            if (Current == '{')
            {
                position++;
                Space();
                while (Current is not ('}' or '\0'))
                {
                    if (Current == '=') { position++; element.Add(new XText(Value())); }
                    else element.Add(ReadElement());
                    Space();
                }
                Expect('}');
            }
            return element;
        }
    }
}
