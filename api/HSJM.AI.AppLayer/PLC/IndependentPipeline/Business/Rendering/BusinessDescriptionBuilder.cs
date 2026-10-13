#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business.Rendering;

/// <summary>Pure source description. Rendering is deliberately independent of the exact semantic compiler.</summary>
public static class BusinessDescriptionBuilder
{
    public const string Version = "business-source-9";
    public static string Key(string sourceFingerprint, PipelineUnit unit) => PipelineHash.Of(Version + sourceFingerprint + unit.Id);
    public static string BlockKey(string sourceFingerprint, long blockId) => PipelineHash.Of(Version + "|BlockTemplate|" + sourceFingerprint + "|" + blockId);
    public static BusinessDescription Build(PipelineSnapshot source, PipelineUnit unit) => new BusinessDescriptionContext(source).Build(unit);
    public static BusinessDescription BuildBlock(PipelineSnapshot source, long blockId) => new BusinessDescriptionContext(source).BuildBlock(blockId);
}

/// <summary>Reuse for a project: block lookup and securely parsed source trees are cached, never model results.</summary>
public sealed class BusinessDescriptionContext
{
    internal readonly PipelineSnapshot Source;
    internal readonly Dictionary<string, PipelineUnit[]> Blocks;
    internal readonly Dictionary<long, PipelineUnit[]> BlocksById;
    private readonly Dictionary<string, XElement> parsed = new(StringComparer.Ordinal);
    private readonly Dictionary<XElement, Dictionary<XElement, (int Ordinal, int Parent)>> sourceIndexes = [];
    private Dictionary<string, List<RootInstanceCall>>? instanceCalls;
    private bool incompleteInstanceIndex;
    private readonly Dictionary<long, Dictionary<string, SymbolDeclaration>> declarations = [];
    internal readonly string Fingerprint;
    internal readonly Dictionary<long, string> SourceHashes;
    internal readonly Dictionary<string, string> TagTypes = new(StringComparer.OrdinalIgnoreCase);
    public BusinessDescriptionContext(PipelineSnapshot source, string? sourceFingerprint = null)
    {
        Source = source;
        foreach (var tag in source.Tags)
            TagTypes[tag.Name] = BusinessSignalTypes.Merge(TagTypes.GetValueOrDefault(tag.Name), tag.DataType);
        Fingerprint = sourceFingerprint ?? source.Fingerprint();
        SourceHashes = source.Units.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => PipelineHash.Of(g.First().RawContent));
        Blocks = source.Units.GroupBy(x => x.BlockName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.NetworkOrder).ThenBy(x => x.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        BlocksById = source.Units.GroupBy(x => x.BlockId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.NetworkOrder).ThenBy(x => x.Id).ToArray());
    }
    internal XElement Parse(string xml)
    {
        if (parsed.TryGetValue(xml, out var value)) return value;
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32_000_000 });
        var root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
        parsed.Add(xml, root);
        return root;
    }
    internal string Location(XElement node)
    {
        var root = node.AncestorsAndSelf().Last();
        if (!sourceIndexes.TryGetValue(root, out var index))
        {
            index = [];
            int ordinal = 0;
            foreach (var element in root.DescendantsAndSelf())
                index[element] = (++ordinal, element.Parent != null && index.TryGetValue(element.Parent, out var parent) ? parent.Ordinal : 0);
            sourceIndexes.Add(root, index);
        }
        var value = index[node];
        return (root.Name.LocalName == "Interface" ? "interface " : "") + $"xml:{value.Ordinal} parent:{value.Parent}";
    }
    internal (IReadOnlyList<RootInstanceCall> Calls, bool Incomplete) RootInstances(string blockName)
    {
        if (instanceCalls == null)
        {
            instanceCalls = new(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in Source.Units.OrderBy(u => u.Id))
            {
                if (string.IsNullOrWhiteSpace(unit.RawContent)) continue;
                XElement source;
                try { source = Parse(unit.RawContent); }
                catch (XmlException) { incompleteInstanceIndex = true; continue; }
                var declaredCalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var call in source.DescendantsAndSelf().Where(e => e.Name.LocalName == "CallInfo"))
                {
                    var name = (string?)call.Attribute("Name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    declaredCalls.Add(name);
                    var instance = call.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
                    if (!instanceCalls.TryGetValue(name, out var list)) instanceCalls[name] = list = [];
                    var fixedPath = instance != null && !instance.Descendants().Any(e => e.Name.LocalName == "Access" && (string?)e.Attribute("Scope") is not ("LiteralConstant" or "TypedConstant"));
                    list.Add(new(unit, call, (string?)instance?.Attribute("Scope") ?? "", fixedPath ? DescriptionRun.AccessName(instance!) : ""));
                }
                foreach (var name in unit.Callees.Where(n => !declaredCalls.Contains(n)))
                {
                    if (!instanceCalls.TryGetValue(name, out var list)) instanceCalls[name] = list = [];
                    list.Add(new(unit, source, "", ""));
                }
            }
        }
        return (instanceCalls.TryGetValue(blockName, out var calls) ? calls : [], incompleteInstanceIndex);
    }
    public BusinessDescription Build(PipelineUnit unit) => new DescriptionRun(this, unit).Build();
    public BusinessDescription BuildBlock(long blockId)
    {
        if (!BlocksById.TryGetValue(blockId, out var units) || units.Length == 0) throw new PipelineException("MissingBlock");
        return new DescriptionRun(this, units[0], units).Build();
    }
    internal long? UniqueBlockId(string name)
    {
        if (!Blocks.TryGetValue(name, out var units)) return null;
        return units.All(u => u.BlockId == units[0].BlockId) ? units[0].BlockId : null;
    }
    internal SymbolDeclaration? Declaration(long blockId, string path)
    {
        var unsliced = BusinessSignalTypes.WithoutSlice(path);
        if (unsliced != path)
        {
            var owner = Declaration(blockId, unsliced);
            return new(path.TrimStart('#'), owner?.Section ?? "Unknown", BusinessSignalTypes.SliceType(path) ?? "Unknown");
        }
        if (!declarations.TryGetValue(blockId, out var members))
        {
            members = new(StringComparer.OrdinalIgnoreCase);
            if (BlocksById.TryGetValue(blockId, out var units))
                foreach (var xml in units.Select(u => u.InterfaceXml).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
                {
                    XElement contract;
                    try { contract = Parse(xml); }
                    catch (XmlException) { continue; }
                    foreach (var member in contract.Descendants().Where(e => e.Name.LocalName == "Member"))
                    {
                        var name = DescriptionRun.MemberPath(member);
                        var section = (string?)member.Ancestors().LastOrDefault(e => e.Name.LocalName == "Section")?.Attribute("Name") ?? "Unknown";
                        var type = (string?)member.Attribute("Datatype") ?? (string?)member.Attribute("DataType") ?? (string?)member.Attribute("Type") ?? "";
                        var declaration = new SymbolDeclaration(name, section, type);
                        if (members.TryGetValue(name, out var old) && (old.Section != section || !old.DataType.Equals(type, StringComparison.OrdinalIgnoreCase))) declaration = new(name, "Unknown", "Conflict");
                        members[name] = declaration;
                    }
                }
            declarations.Add(blockId, members);
        }
        if (members.TryGetValue(path.TrimStart('#'), out var exact)) return exact;
        var normalized = Regex.Replace(path.TrimStart('#'), @"\[[^\]]*\]", "");
        if (members.TryGetValue(normalized, out var indexed)) return indexed with { Path = path.TrimStart('#'), DataType = Regex.Replace(indexed.DataType, @"^Array\s*\[[^\]]+\]\s+of\s+", "", RegexOptions.IgnoreCase) };
        var ancestor = members.Values.Where(d => normalized.StartsWith(d.Path + ".", StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => d.Path.Length).FirstOrDefault();
        // A containing declaration establishes storage scope, not the type of an absent leaf.
        return ancestor == null ? null : new(path.TrimStart('#'), ancestor.Section, "");
    }
}

internal sealed record RootInstanceCall(PipelineUnit Source, XElement CallInfo, string Scope, string InstancePath);
internal sealed record RootMemberBinding(string ActualPath, XElement Declaration, string Section);
internal sealed record SymbolDeclaration(string Path, string Section, string DataType);

internal sealed class DescriptionRun(BusinessDescriptionContext context, PipelineUnit requested, IReadOnlyList<PipelineUnit>? templateUnits = null)
{
    private readonly bool templateMode = templateUnits != null;
    private readonly IReadOnlyList<PipelineUnit> rootUnits = templateUnits ?? [requested];
    private readonly StringBuilder markdown = new();
    private readonly StringBuilder model = new();
    private readonly List<BusinessEvidence> evidence = [];
    private readonly List<string> limitations = [];
    private readonly Dictionary<string, string> aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> signalTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> unresolvedTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> locals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BusinessSymbol> symbolDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> opaqueNames = new(StringComparer.Ordinal);
    private readonly HashSet<long> emitted = [];
    private readonly HashSet<long> scheduled = [];
    private readonly HashSet<long> emittedInterfaces = [];
    private readonly HashSet<string> callees = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<PipelineUnit> pending = new();
    private readonly Dictionary<string, RootMemberBinding> rootMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> emittedRootMembers = new(StringComparer.OrdinalIgnoreCase);
    private string? rootInstanceEvidence;
    private static readonly HashSet<string> MachineAttributeNames = new(new[] {
        "ExternalAccessible", "ExternalVisible", "ExternalWritable", "SetPoint", "HasQuotes", "CFC_ForTest", "CFC_Visible",
        "S7_HiddenAssignment", "S7_PredefinedAssignment"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Keywords = new(("IF THEN ELSE ELSIF END_IF CASE OF END_CASE FOR TO BY DO END_FOR WHILE END_WHILE REPEAT UNTIL END_REPEAT RETURN EXIT CONTINUE AND OR XOR NOT MOD TRUE FALSE BOOL BYTE WORD DWORD LWORD SINT INT DINT LINT USINT UINT UDINT ULINT REAL LREAL TIME LTIME DATE TOD DT CHAR WCHAR STRING WSTRING VOID ARRAY STRUCT END_STRUCT AT VAR VAR_INPUT VAR_OUTPUT VAR_IN_OUT VAR_TEMP VAR_STAT END_VAR BEGIN END_FUNCTION END_FUNCTION_BLOCK FUNCTION FUNCTION_BLOCK REGION END_REGION A AN O ON X XN U UN L T S R SET CLR SAVE NOP CALL UC CC JU JC JCN JCB JNB JBI JNBI LOOP BE BEC BEU TAK PUSH POP EN ENO IN OUT PT ET Q TON TOF TP TONR IEC_TIMER Input Output InOut Static Temp Constant Return LocalVariable GlobalVariable LiteralConstant LocalConstant GlobalConstant Label True False FB FC OB Number Boolean Bool Int DInt Real Time String Uint UInt USInt DWord Word LReal Void SystemConstant").Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    public BusinessDescription Build()
    {
        string status = templateMode ? "TemplateRendered" : "Rendered";
        markdown.AppendLine(templateMode ? $"# {requested.BlockName} / 完整块模板 / {rootUnits.Count} 个 LU" : $"# {requested.BlockName} / Network {requested.NetworkOrder?.ToString() ?? "?"} / LU {requested.Id}");
        markdown.AppendLine($"源文件：{requested.SourceFile}\n\n以下为完整源码结构与接线事实；未知指令、时序及调用状态没有被假定为恒真，也不代表全部语义已形式化证明。");
        model.AppendLine("Source facts only. Names are opaque identifiers. Do not infer physical meaning from identifiers, pin spelling or comments. Preserve execution order, branch gates, previous state and timer state. A wire is connectivity, not necessarily an equation. Unknown semantics are not TRUE. Evidence is structural, not a complete formal proof. Source xml:N is the 1-based XML element preorder ordinal; parent:N preserves the complete tree structure. Interface ordinals belong to the separate interface document. Access SymbolRef is the canonical signal/formal identity, joined with Member/Parameter aliases; Component names are opaque path components. Instance fields with Datatype=Bnnn refer to that same block body.");
        model.AppendLine("Machine metadata enums are literal source facts. In particular, Remanence=SetInIDB must not be rewritten as Retain or NonRetain without separate source evidence.");
        if (templateMode)
        {
            model.AppendLine($"DescriptionMode=BlockTemplate; root BlockRef:{Block(requested.BlockName)}. Root locals remain P formals regardless of project instances. Only declared root Input/Output/InOut formals can be instantiated as external constraints; Static/Temp are internal state. This artifact is template-cache input, not an activated LU result.");
            if (rootUnits.Count > 1 && (rootUnits.Any(u => u.NetworkOrder == null) || rootUnits.GroupBy(u => u.NetworkOrder).Any(g => g.Count() > 1)))
                Limit($"AmbiguousBlockNetworkOrder: BlockRef:{Block(requested.BlockName)}; missing or duplicate order metadata; LU id only stabilizes display order, not execution semantics.");
        }
        if (rootUnits.All(u => string.IsNullOrWhiteSpace(u.RawContent))) status = "EmptySource";
        else
        {
            foreach (var rootUnit in rootUnits) { pending.Enqueue(rootUnit); scheduled.Add(rootUnit.Id); }
            while (pending.TryDequeue(out var unit))
            {
                if (!emitted.Add(unit.Id)) continue;
                if (string.IsNullOrWhiteSpace(unit.RawContent))
                {
                    Limit($"MissingOrProtectedBody: BlockRef:{Block(unit.BlockName)} LU:{unit.Id}; no internal semantics available.");
                    if (templateMode && unit.BlockId == requested.BlockId) status = "ParseFailed";
                    continue;
                }
                try { RenderUnit(unit, context.Parse(unit.RawContent)); }
                catch (XmlException)
                {
                    Limit($"ParseFailed: LU:{unit.Id}; XML source is invalid; no executable facts available for this unit.");
                    if (unit.Id == requested.Id || templateMode && unit.BlockId == requested.BlockId) status = "ParseFailed";
                }
            }
        }
        if (status is not ("Rendered" or "TemplateRendered")) Limit($"{status}: LU:{requested.Id}; no successful source description.");
        var sourceHash = templateMode ? PipelineHash.Of(JsonSerializer.Serialize(rootUnits.Select(u => new {u.NetworkOrder,u.Language,u.BlockKind,u.RawContent,u.InterfaceXml,u.Bindings,Callees=u.Callees.OrderBy(c=>c,StringComparer.Ordinal)}))) : PipelineHash.Of(requested.RawContent);
        model.AppendLine("SignalTypes (source evidence; Unknown and Conflict cannot be used as typed operands): " + string.Join("; ", signalTypes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + TypeValue(p.Value))));
        return new(requested.Id, requested.BlockId, requested.BlockName,
            templateMode ? BusinessDescriptionBuilder.BlockKey(context.Fingerprint, requested.BlockId) : BusinessDescriptionBuilder.Key(context.Fingerprint, requested), sourceHash,
            status, markdown.ToString(), model.ToString(), aliases, evidence, limitations, callees.OrderBy(x => x, StringComparer.Ordinal).ToArray(),symbolDefinitions.Values.OrderBy(s=>s.Alias,StringComparer.Ordinal).ToArray()) { SignalTypes = signalTypes };
    }

    private void RenderUnit(PipelineUnit unit, XElement root)
    {
        if (!templateMode && unit.Id == requested.Id) PrepareRootInstance(root);
        var block = Block(unit.BlockName);
        Fact(unit, "/", unit.Id == requested.Id ? "Unit" : "BlockBody",
            $"程序块 {unit.BlockName}（{unit.BlockKind}），网络顺序 {unit.NetworkOrder?.ToString() ?? "未提供"}，语言 {unit.Language}，结构 {root.Name.LocalName}。",
            $"BlockRef:{block}; block kind={unit.BlockKind}; network order={unit.NetworkOrder?.ToString() ?? "unknown"}; language={unit.Language}; root={root.Name.LocalName}.", root);
        if (!string.IsNullOrWhiteSpace(unit.InterfaceXml) && emittedInterfaces.Add(unit.BlockId))
        {
            try
            {
                var contract = context.Parse(unit.InterfaceXml);
                RenderTree(unit, contract, "/Interface", true);
                Fact(unit, "/Interface/compact", "InterfaceStructure", "模型接口投影保留全部被引用声明的层级、方向、类型、初值与属性；完整接口保留在逐节点证据。",
                    $"BlockRef:{block} referenced interface declarations (unused declarations omitted; types, initial values and attributes retained): " + AnonymousTree(unit, RelevantInterface(unit, contract), true)?.ToString(SaveOptions.DisableFormatting), contract);
            }
            catch (XmlException) { Limit($"InterfaceParseFailed: BlockRef:{block} LU:{unit.Id}."); }
        }
        RenderTree(unit, root, "/" + root.Name.LocalName + "[1]", false);
        CollectSignalTypes(unit, root);
        Fact(unit, "/compact", "SourceStructure", "完整结构保留全部指令、接线、门控、参数、字面量和执行顺序；名称已匿名化的同构结构供推理引用。",
            $"BlockRef:{block} NetworkOrder:{unit.NetworkOrder?.ToString() ?? "unknown"} complete source: " + AnonymousTree(unit, root, false)?.ToString(SaveOptions.DisableFormatting), root);
        foreach (var script in root.DescendantsAndSelf().Where(x => x.Name.LocalName is "StructuredText" or "StatementList" or "STL"))
        {
            string Sequence(XElement element, bool anonymize)
            {
                var kind = element.Name.LocalName;
                if (kind.Contains("Comment", StringComparison.OrdinalIgnoreCase)) return "";
                if (kind == "Access") return anonymize ? AccessAlias(unit, element, AccessName(element)) : AccessName(element);
                if (kind == "StlToken") return Attr(element, "Text");
                if (kind == "Token") return anonymize ? Scrub(unit, Attr(element, "Text")) : Attr(element, "Text");
                if (kind == "NewLine") return "\n";
                if (kind == "Blank") return " ";
                return string.Join(" ", element.Nodes().Select(n => n is XElement child ? Sequence(child, anonymize) : n is XText t ? anonymize ? Scrub(unit, t.Value) : t.Value : ""));
            }
            Fact(unit, Path(script) + "/sequence", "ScriptSequence",
                "完整语句序列（保持条件、循环、跳转与赋值顺序；不能把未执行分支的赋值当成无条件关系）：" + Sequence(script, false),
                "Complete ordered statement sequence; retain IF/CASE/loop/jump gates and sequential writes; an unexecuted assignment does not update its target: " + Sequence(script, true), script);
        }
        foreach (var net in root.DescendantsAndSelf().Where(x => x.Name.LocalName == "FlgNet")) RenderNetwork(unit, net);
        foreach (var call in root.DescendantsAndSelf().Where(x => x.Name.LocalName == "CallInfo")) IncludeCallee(unit, call);
        // Source metadata can identify script calls whose XML does not use CallInfo.
        foreach (var callee in unit.Callees)
            if (!root.DescendantsAndSelf().Any(x => x.Name.LocalName == "CallInfo" && Attr(x, "Name") == callee)) IncludeBody(unit, callee, "metadata", "/Callees");
    }

    private void PrepareRootInstance(XElement root)
    {
        if (!requested.BlockKind.Equals("FB", StringComparison.OrdinalIgnoreCase)) return;
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var access in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Access" && Attr(e, "Scope") == "LocalVariable")) references.Add(AccessName(access));
        // Do not make comments or string literals observable program references.
        foreach (var text in root.DescendantsAndSelf().Where(e => !e.AncestorsAndSelf().Any(a => a.Name.LocalName.Contains("Comment", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(e => e.Attributes().Where(a => a.Name.LocalName == "Text").Select(a => a.Value).Concat(e.Nodes().OfType<XText>().Select(t => t.Value))))
        {
            var code = Regex.Replace(text, @"'([^']|'')*'|(?s:\(\*.*?\*\)|/\*.*?\*/)|//[^\r\n]*", " ");
            foreach (Match match in Regex.Matches(code, @"#([\p{L}_][\p{L}\p{N}_]*(?:\.[\p{L}_][\p{L}\p{N}_]*)*)")) references.Add(match.Groups[1].Value);
        }
        if (references.Count == 0) return;
        var block = Block(requested.BlockName);
        var lookup = context.RootInstances(requested.BlockName);
        var globals = lookup.Calls.Where(c => c.Scope.Equals("GlobalVariable", StringComparison.OrdinalIgnoreCase) &&
            c.InstancePath.Length > 0 && !c.InstancePath.StartsWith("UNKNOWN_ACCESS_", StringComparison.Ordinal) && !c.InstancePath.Contains("[dynamic]", StringComparison.Ordinal)).ToArray();
        var paths = globals.Select(c => c.InstancePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (lookup.Incomplete || globals.Length != lookup.Calls.Count || paths.Length != 1)
        {
            var reason = paths.Length > 1 ? "MultipleRootInstances" : lookup.Incomplete || globals.Length != lookup.Calls.Count ? "UnresolvedRootInstances" : "MissingRootInstance";
            Limit($"{reason}: BlockRef:{block} LU:{requested.Id}; global instance count={paths.Length}; persistent locals remain template formals; no instance is selected by block or variable name.");
            return;
        }
        if (string.IsNullOrWhiteSpace(requested.InterfaceXml))
        {
            Limit($"RootInstanceInterfaceMissing: BlockRef:{block} LU:{requested.Id}; member storage duration cannot be established."); return;
        }
        XElement contract;
        try { contract = context.Parse(requested.InterfaceXml); }
        catch (XmlException) { Limit($"RootInstanceInterfaceInvalid: BlockRef:{block} LU:{requested.Id}."); return; }
        var declarations = contract.Descendants().Where(e => e.Name.LocalName == "Member").Select(e => (Node: e, Name: MemberPath(e))).ToArray();
        var binding = globals[0];
        foreach (var reference in references)
        {
            var normalized = Regex.Replace(reference, @"\[[^\]]*\]", "");
            var declaration = declarations.Where(d => normalized.Equals(d.Name, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(d.Name + ".", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.Name.Length).FirstOrDefault();
            var section = declaration.Node?.Ancestors().LastOrDefault(e => e.Name.LocalName == "Section");
            var scope = Attr(section, "Name");
            if (declaration.Node == null || scope is not ("Static" or "Input" or "Output") || reference.Contains("[dynamic]", StringComparison.Ordinal))
            {
                Limit($"RootLocalScopeNotObservable: BlockRef:{block} SymbolRef:{Local(requested.BlockName, reference)} Section:{(scope.Length == 0 ? "unknown" : scope)}; only declared Static/Input/Output members with fixed paths can bind to the global instance.");
                continue;
            }
            var inaccessible = declaration.Node.AncestorsAndSelf().Where(e => e.Name.LocalName == "Member")
                .SelectMany(e => e.Elements().Where(a => a.Name.LocalName == "AttributeList").SelectMany(a => a.Elements()))
                .Any(e => e.Name.LocalName == "BooleanAttribute" && Attr(e, "Name") == "ExternalAccessible" && e.Value.Equals("false", StringComparison.OrdinalIgnoreCase));
            if (inaccessible)
            {
                Limit($"RootMemberNotExternallyAccessible: BlockRef:{block} SymbolRef:{Local(requested.BlockName, reference)}; explicit interface metadata denies external access."); continue;
            }
            rootMembers[reference] = new(binding.InstancePath + "." + reference, declaration.Node, scope);
        }
        if (rootMembers.Count == 0) return;
        rootInstanceEvidence = Fact(binding.Source, Path(binding.CallInfo) + "/root-instance-" + requested.Id, "RootInstanceBinding",
            $"根程序块 {requested.BlockName} 在已导入项目中唯一明确全局实例为 {binding.InstancePath}；来源是实际调用实例声明。仅本根LU使用的持久成员可绑定，Temp/InOut 与公共被调块模板不由此映射。",
            $"RootBlockRef:{block} uses the unique explicit global InstanceRef:{Opaque(binding.InstancePath)} in imported CallInfo; root persistent Static/Input/Output members can be observable S symbols. This does not select an instance for callee templates, Temp or unresolved InOut. RootLU:{requested.Id}.", binding.CallInfo);
    }

    private string RootOrLocal(PipelineUnit unit, string name)
    {
        var localName = name.TrimStart('#');
        if (unit.Id != requested.Id || !rootMembers.TryGetValue(localName, out var binding)) return Local(unit.BlockName, localName, unit.BlockId);
        var alias = GlobalAlias(binding.ActualPath, true);
        RecordSymbol(alias, unit.BlockId, localName);
        if (emittedRootMembers.Add(localName))
        {
            var type = signalTypes.GetValueOrDefault(alias, "Unknown");
            var declarationFormal = Local(unit.BlockName, MemberPath(binding.Declaration));
            var sliceOffset = localName.LastIndexOf(".%", StringComparison.Ordinal);
            var declaration = sliceOffset < 0
                ? $"declarationFormal={declarationFormal}; local access and declaration identify the same persistent root instance member."
                : $"accessFormal={Local(unit.BlockName, localName, unit.BlockId)}; containerDeclarationFormal={declarationFormal}; containerType={TypeValue(Attr(binding.Declaration, "Datatype").Length > 0 ? Attr(binding.Declaration, "Datatype") : Attr(binding.Declaration, "DataType").Length > 0 ? Attr(binding.Declaration, "DataType") : Attr(binding.Declaration, "Type"))}; slice={localName[(sliceOffset + 1)..]}; the selected slice and its whole container are distinct operands.";
            Fact(unit, "/Interface" + Path(binding.Declaration) + "/root-member-" + PipelineHash.Of(localName)[..10], "RootMemberBinding",
                $"局部持久成员 {localName}（{binding.Section}）绑定实际实例路径 {binding.ActualPath}；访问类型 {type}。",
                $"RootMember symbol={alias}; section={binding.Section}; type={TypeValue(type)}; instanceEvidence={rootInstanceEvidence}; {declaration}", binding.Declaration);
        }
        return alias;
    }

    private XElement RelevantInterface(PipelineUnit unit, XElement contract)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bodyUnits = templateMode && unit.BlockId == requested.BlockId ? rootUnits : unit.Id == requested.Id ? new[] { unit } : context.Blocks[unit.BlockName];
        foreach (var body in bodyUnits)
        {
            if (string.IsNullOrWhiteSpace(body.RawContent)) continue;
            XElement source;
            try { source = context.Parse(body.RawContent); }
            catch (XmlException) { return contract; } // Cannot prove any field unused in an unreadable body.
            foreach (var access in source.DescendantsAndSelf().Where(e => e.Name.LocalName is "Access" or "Instance" && Attr(e, "Scope") is "LocalVariable" or "LocalConstant" or "AddressConstant"))
                used.Add(Regex.Replace(AccessName(access), @"\[[^\]]*\]", ""));
            // Text-only script exports can contain local paths outside Access nodes.
            foreach (var token in source.DescendantsAndSelf().Where(e => e.Name.LocalName is "Token" or "Text"))
                foreach (Match match in Regex.Matches(Attr(token, "Text") + token.Value, @"#([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)")) used.Add(match.Groups[1].Value);
            foreach (Match match in Regex.Matches(source.Value, @"#([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)")) used.Add(match.Groups[1].Value);
        }
        var projection = new XElement(contract);
        foreach (var member in projection.Descendants().Where(e => e.Name.LocalName == "Member").ToArray())
        {
            if (member.Parent == null) continue;
            var path = MemberPath(member);
            if (!used.Any(reference => reference.Equals(path, StringComparison.OrdinalIgnoreCase) || reference.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase) || path.StartsWith(reference + ".", StringComparison.OrdinalIgnoreCase))) member.Remove();
        }
        return projection;
    }

    private XElement? AnonymousTree(PipelineUnit unit, XElement node, bool contract)
    {
        var kind = node.Name.LocalName;
        if (kind.Contains("Comment", StringComparison.OrdinalIgnoreCase) || kind is "Title" or "MultilingualText" or "MultilingualTextItem") return null;
        var result = new XElement(kind);
        foreach (var attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration && !a.Name.LocalName.Contains("Comment", StringComparison.OrdinalIgnoreCase)))
            result.SetAttributeValue(attribute.Name.LocalName, AttributeValue(unit, node, attribute, contract));
        if (kind == "Access") result.SetAttributeValue("SymbolRef", AccessAlias(unit, node, AccessName(node)));
        foreach (var child in node.Nodes())
        {
            if (child is XElement element)
            {
                var anonymous = AnonymousTree(unit, element, contract);
                if (anonymous != null) result.Add(anonymous);
            }
            else if (child is XText text && !string.IsNullOrWhiteSpace(text.Value))
                result.Add(new XText(TextValue(unit, node, text.Value)));
        }
        return result;
    }

    private void RenderTree(PipelineUnit unit, XElement node, string path, bool contract)
    {
        var kind = node.Name.LocalName;
        // Human commentary is never a premise or a model input.
        if (kind.Contains("Comment", StringComparison.OrdinalIgnoreCase) || kind is "Title" or "MultilingualText" or "MultilingualTextItem") return;
        if (kind == "Access")
        {
            var name = AccessName(node);
            var scope = Attr(node, "Scope");
            var literal = scope == "LiteralConstant" || node.Descendants().Any(x => x.Name.LocalName == "ConstantValue") && !node.Descendants().Any(x => x.Name.LocalName == "Component");
            var alias = literal ? name : AccessAlias(unit, node, name);
            if (name.Contains("[dynamic]", StringComparison.Ordinal)) Limit($"DynamicAccess: LU:{unit.Id} Path:{path} SymbolRef:{alias}; indexed target depends on runtime index; no fixed observable alias is asserted.");
            // The DInt literal inside an array index describes the index, never the accessed element.
            var type = literal ? node.Descendants().FirstOrDefault(x => x.Name.LocalName == "ConstantType")?.Value ?? Attr(node, "Type") : Attr(node, "Type");
            if (!literal)
            {
                AddSignalType(alias, type);
                type = signalTypes.GetValueOrDefault(alias, "Unknown");
            }
            Fact(unit, path, "Access", $"读取/写入引用：{name}；Scope={scope}；UId={Attr(node, "UId")}；Type={type}。", $"Access UId={Attr(node, "UId")}; scope={scope}; symbol={alias}; type={TypeValue(type)}.", node);
            // Preserve all structural metadata, including dynamic index Access nodes and address slices.
            foreach (var child in node.Elements()) RenderAccessStructure(unit, child, path + "/" + child.Name.LocalName, name, alias);
            return;
        }
        var attributes = node.Attributes().Where(a => !a.IsNamespaceDeclaration && !a.Name.LocalName.Contains("Comment", StringComparison.OrdinalIgnoreCase)).ToArray();
        var actual = string.Join("; ", attributes.Select(a => a.Name.LocalName + "=" + a.Value));
        var anonymous = string.Join("; ", attributes.Select(a => a.Name.LocalName + "=" + AttributeValue(unit, node, a, contract)));
        var text = string.Concat(node.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (text.Length > 0)
        {
            actual += "; text=" + text;
            anonymous += "; text=" + TextValue(unit, node, text);
        }
        var label = kind switch {
            "Wire" => "导线（以下端点属于同一连接）", "NameCon" => "具名端口", "IdentCon" => "变量引用端点",
            "Powerrail" => "电源轨（该连接恒真）", "Part" => "指令元件", "Call" => "调用点", "CallInfo" => "被调用接口",
            "Parameter" => "形式参数声明", "Negated" => "指定端口取反", "StructuredText" => "结构化文本，按以下顺序执行",
            "StatementList" or "STL" => "语句表，保留全部指令与跳转", "Token" => "语句词元", "Step" => "GRAPH 步骤",
            "Transition" => "GRAPH 转移，保留条件与前后步骤", "Interlock" => "GRAPH 联锁条件", "Supervision" => "GRAPH 监视条件",
            "Actions" => "GRAPH 动作，保留限定符与顺序", "Member" => "接口/状态变量声明", _ => kind };
        Fact(unit, path, contract ? "Interface" : kind, $"{label}：{actual}", $"{kind}: {anonymous}", node);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in node.Elements())
        {
            ordinals.TryGetValue(child.Name.LocalName, out var i); ordinals[child.Name.LocalName] = ++i;
            RenderTree(unit, child, path + "/" + child.Name.LocalName + "[" + i + "]", contract);
        }
    }

    private void RenderAccessStructure(PipelineUnit unit, XElement node, string path, string actualName, string alias)
    {
        if (node.Name.LocalName.Contains("Comment", StringComparison.OrdinalIgnoreCase)) return;
        if (node.Name.LocalName == "Access") { RenderTree(unit, node, path, false); return; }
        var attrs = node.Attributes().Where(a => !a.IsNamespaceDeclaration).ToArray();
        string Anon(XAttribute a) => AttributeValue(unit, node, a, false);
        var text = node.HasElements ? "" : node.Value.Trim();
        Fact(unit, path, "AccessStructure", node.Name.LocalName + ": " + string.Join("; ", attrs.Select(a => a.Name.LocalName + "=" + a.Value)) + (text.Length > 0 ? "; " + text : ""),
            $"{node.Name.LocalName} of {alias}: " + string.Join("; ", attrs.Select(a => a.Name.LocalName + "=" + Anon(a))) + (text.Length > 0 ? "; " + TextValue(unit, node, text) : ""), node);
        int index = 0;
        foreach (var child in node.Elements()) RenderAccessStructure(unit, child, path + "/" + child.Name.LocalName + "[" + (++index) + "]", actualName, alias);
    }

    private string AttributeValue(PipelineUnit unit, XElement node, XAttribute attr, bool contract)
    {
        var key = attr.Name.LocalName;
        var kind = node.Name.LocalName;
        var value = attr.Value;
        if (key is "DataType" or "Datatype" or "Type") return TypeValue(value);
        if (key == "Name")
        {
            if (kind == "CallInfo") return Block(value);
            if (kind == "Constant" && Attr(node.Parent, "Scope") == "AddressConstant") return AddressConstantAlias(unit, value);
            if (kind == "Member") return RootOrLocal(unit, MemberPath(node));
            if (kind == "Parameter")
            {
                if (node.Parent?.Name.LocalName == "Instruction") return value;
                var alias = Local(Attr(node.Parent, "Name"), value);
                AddSignalType(alias, Attr(node, "Type"));
                if (symbolDefinitions.TryGetValue(alias, out var definition) && definition.Section == "Unknown" && context.Declaration(definition.BlockId, value) == null && Attr(node, "Section").Length > 0)
                    symbolDefinitions[alias] = definition with { Section = Attr(node, "Section"), DataType = Attr(node, "Type") };
                return alias;
            }
            if (kind == "NameCon")
            {
                var net = node.Ancestors().FirstOrDefault(x => x.Name.LocalName == "FlgNet");
                var call = net?.Descendants().FirstOrDefault(x => x.Name.LocalName == "Call" && Attr(x, "UId") == Attr(node, "UId"));
                var callInfo = call?.Elements().FirstOrDefault(x => x.Name.LocalName == "CallInfo");
                return callInfo != null && !value.Equals("en", StringComparison.OrdinalIgnoreCase) && !value.Equals("eno", StringComparison.OrdinalIgnoreCase) ? Local(Attr(callInfo, "Name"), value) : value;
            }
            if (kind == "Negated" && node.Parent?.Name.LocalName == "Call")
            {
                var callInfo = node.Parent.Elements().FirstOrDefault(x => x.Name.LocalName == "CallInfo");
                return value.Equals("en", StringComparison.OrdinalIgnoreCase) || value.Equals("eno", StringComparison.OrdinalIgnoreCase) ? value : Local(Attr(callInfo, "Name"), value);
            }
            if (kind is "Part" or "Instruction" or "Negated" or "Section" or "TemplateValue" or "AutomaticTyped") return value;
            if (kind.EndsWith("Attribute", StringComparison.Ordinal) && (Attr(node, "SystemDefined").Equals("true", StringComparison.OrdinalIgnoreCase) || MachineAttributeNames.Contains(value))) return value;
            return Opaque(value);
        }
        if (key == "Text") return kind == "StlToken" ? value : Scrub(unit, value);
        // The remaining XML attributes encode machine metadata, not user identifiers. In
        // particular do not replace Retain/NonRetain/SetInIDB, Public/Private, bit slices,
        // event qualifiers, step times, enum values or SystemDefined flags with opaque names.
        return value;
    }

    private string TypeValue(string value)
    {
        if (context.Blocks.ContainsKey(value)) return Block(value);
        // Openness quotes user-defined type names; primitive/system type spelling, array
        // bounds and machine enum Type fields stay intact.
        return Regex.Replace(value, "\"([^\"]+)\"", m => context.Blocks.ContainsKey(m.Groups[1].Value) ? Block(m.Groups[1].Value) : Opaque(m.Groups[1].Value));
    }

    private string TextValue(PipelineUnit unit, XElement node, string value)
    {
        var kind = node.Name.LocalName;
        if (kind is "ConstantValue" or "Value" or "BooleanAttribute" or "IntegerAttribute" or "LinkType" or "Equation") return value;
        if (kind is "ConstantType" or "DataType" or "Datatype") return TypeValue(value);
        if (kind == "TemplateValue") return Attr(node, "Type") == "Type" ? TypeValue(value) : value;
        if (kind == "StringAttribute" && Attr(node, "Name") == "S7_HiddenAssignment") return value;
        return Scrub(unit, value);
    }

    private void IncludeCallee(PipelineUnit unit, XElement call)
    {
        var uid = Attr(call.Parent, "UId");
        IncludeBody(unit, Attr(call, "Name"), uid, Path(call));
    }
    private void IncludeBody(PipelineUnit unit, string name, string callUid, string path)
    {
        if (string.IsNullOrWhiteSpace(name)) { Limit($"MissingCalleeIdentity: LU:{unit.Id} CallUID:{callUid}."); return; }
        callees.Add(name);
        var block = Block(name);
        if (!context.Blocks.TryGetValue(name, out var bodies) || bodies.All(b => string.IsNullOrWhiteSpace(b.RawContent)))
        {
            var id = Fact(unit, path + "/body", "MissingBlockBody", $"调用点 {callUid} 的被调块 {name} 没有可读块体（可能缺失或受保护），不依据引脚名字推断行为。", $"CallUID:{callUid} BlockRef:{block}: missing or protected body; internal behavior unknown.");
            Limit($"MissingOrProtectedBody: EvidenceId:{id} CallUID:{callUid} BlockRef:{block}; only candidates requiring this call's internal behavior are unavailable.");
            return;
        }
        if (bodies.Select(x => x.BlockId).Distinct().Count() != 1)
        {
            Limit($"AmbiguousCallee: LU:{unit.Id} CallUID:{callUid} BlockRef:{block}; multiple block identities."); return;
        }
        Fact(unit, path + "/body", "CallBodyReference", $"调用点 {callUid} 引用 {name} 的全部网络：{string.Join(",", bodies.Select(b => b.Id))}；通过相同形式参数与调用接线绑定实参；各实例状态分别保存。", $"CallUID:{callUid} references BlockRef:{block} body LUs:{string.Join(",", bodies.Select(b => b.Id))}; formal identifiers join interface/local accesses/call ports. Actual arguments are the connected endpoints. Each instance has separate state; execution remains gated by EN.");
        if (bodies.Length > 1 && bodies.Any(x => x.NetworkOrder == null)) Limit($"UnknownNetworkOrder: LU:{unit.Id} CallUID:{callUid} BlockRef:{block}; cross-network ordering cannot be assumed.");
        foreach (var body in bodies) if (scheduled.Add(body.Id)) pending.Enqueue(body);
    }

    private void RenderNetwork(PipelineUnit unit, XElement net)
    {
        var parts = net.Elements().FirstOrDefault(x => x.Name.LocalName == "Parts");
        var wires = net.Elements().FirstOrDefault(x => x.Name.LocalName == "Wires");
        if (parts == null || wires == null) { Limit($"IncompleteNetwork: LU:{unit.Id} Path:{Path(net)}; Parts/Wires absent."); return; }
        var nodes = parts.Elements().Where(x => Attr(x, "UId").Length > 0).GroupBy(x => Attr(x, "UId")).ToDictionary(g => g.Key, g => g.First());
        var connections = new Dictionary<string, List<XElement>>(StringComparer.OrdinalIgnoreCase);
        var expressionSummaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wire in wires.Elements())
            foreach (var endpoint in wire.Elements())
            {
                var key = EndpointKey(endpoint);
                if (!connections.TryGetValue(key, out var peers)) connections[key] = peers = [];
                peers.AddRange(wire.Elements().Where(x => x != endpoint));
            }
        string Expr(string uid, string pin, bool anon, HashSet<string> trail)
        {
            var key = uid + ":" + pin;
            var portRef = uid + "." + Port(uid, pin, anon);
            if (!trail.Add(key)) return $"STATE_OR_CYCLE({portRef})";
            // This is a readable summary, not the source representation. Long cones refer to the
            // complete wire facts already emitted instead of duplicating a potentially exponential DAG.
            if (trail.Count > 24) return $"WIRE_REFERENCE({portRef}; see complete connection facts)";
            var cacheKey = key + ":" + anon;
            if (expressionSummaries.TryGetValue(cacheKey, out var cached)) return cached;
            if (!connections.TryGetValue("NameCon:" + key, out var peers)) return $"UNCONNECTED({portRef})";
            var terms = peers.Where(IsDriver).Select(p => From(p, anon, new HashSet<string>(trail))).Distinct().ToArray();
            if (terms.Length == 0) return $"WIRE_REFERENCE({portRef}; no identified driver)";
            var result = terms.Sum(t => t.Length) > 8192 ? $"WIRE_REFERENCE({portRef}; see complete connection facts)" : terms.Length == 1 ? terms[0] : "CONNECTED(" + string.Join(", ", terms) + ")";
            expressionSummaries[cacheKey] = result;
            return result;
        }
        string Port(string uid, string pin, bool anon)
        {
            if (!anon || pin.Equals("en", StringComparison.OrdinalIgnoreCase) || pin.Equals("eno", StringComparison.OrdinalIgnoreCase)) return pin;
            if (!nodes.TryGetValue(uid, out var node)) return Opaque(pin);
            return node.Name.LocalName == "Call" ? Local(Attr(node.Elements().FirstOrDefault(e => e.Name.LocalName == "CallInfo"), "Name"), pin) : pin;
        }
        bool IsDriver(XElement endpoint)
        {
            if (endpoint.Name.LocalName is "Powerrail" or "IdentCon") return true;
            if (endpoint.Name.LocalName != "NameCon" || !nodes.TryGetValue(Attr(endpoint, "UId"), out var node)) return false;
            var pin = Attr(endpoint, "Name");
            if (node.Name.LocalName == "Call")
                return pin.Equals("eno", StringComparison.OrdinalIgnoreCase) || node.Descendants().Any(x => x.Name.LocalName == "Parameter" && Attr(x, "Name").Equals(pin, StringComparison.OrdinalIgnoreCase) && Attr(x, "Section") is "Output" or "InOut" or "Return");
            return pin.Equals("out", StringComparison.OrdinalIgnoreCase) || pin.Equals("Q", StringComparison.OrdinalIgnoreCase) || pin.Equals("ET", StringComparison.OrdinalIgnoreCase) || pin.Equals("ENO", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(pin, "^out[0-9]+$", RegexOptions.IgnoreCase);
        }
        string From(XElement endpoint, bool anon, HashSet<string> trail)
        {
            if (endpoint.Name.LocalName == "Powerrail") return "TRUE";
            var uid = Attr(endpoint, "UId"); var pin = Attr(endpoint, "Name");
            if (!nodes.TryGetValue(uid, out var node)) return $"UNKNOWN_NODE({uid}.{Port(uid, pin, anon)})";
            if (node.Name.LocalName == "Access") return anon ? AccessAlias(unit, node, AccessName(node)) : AccessName(node);
            if (node.Name.LocalName == "Call")
            {
                var info = node.Elements().FirstOrDefault(x => x.Name.LocalName == "CallInfo");
                return $"CALL({uid}).{(anon ? Local(Attr(info, "Name"), pin) : pin)}";
            }
            var kind = Attr(node, "Name");
            bool Neg(string port) => node.Elements().Any(x => x.Name.LocalName == "Negated" && Attr(x, "Name").Equals(port, StringComparison.OrdinalIgnoreCase));
            string Input(string port) { var value = Expr(uid, port, anon, new HashSet<string>(trail)); return Neg(port) ? "NOT(" + value + ")" : value; }
            string result;
            if (kind == "Contact") result = "AND(" + Input("in") + ", " + Input("operand") + ")";
            else if (kind is "O" or "A" or "Or" or "And" or "Xor")
            {
                var ports = connections.Keys.Where(k => k.StartsWith("NameCon:" + uid + ":", StringComparison.Ordinal)).Select(k => k[("NameCon:" + uid + ":").Length..]).Where(p => p.StartsWith("in", StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
                result = ports.Length == 0 ? $"UNKNOWN_INPUTS({uid})" : (kind is "O" or "Or" ? "OR" : kind == "Xor" ? "XOR" : "AND") + "(" + string.Join(", ", ports.Select(Input)) + ")";
            }
            else if (kind is "Not" or "NOT") result = "NOT(" + Input("in") + ")";
            else if (kind is "TON" or "TOF" or "TP" or "TONR") result = $"{kind}_STATE({uid}, IN={Input("in")}, PT={Input("PT")}, port={pin})";
            else result = $"OPAQUE_INSTRUCTION({kind}, {uid}.{pin})";
            return Neg(pin) ? "NOT(" + result + ")" : result;
        }
        foreach (var part in parts.Elements().Where(x => x.Name.LocalName == "Part"))
        {
            var kind = Attr(part, "Name"); var uid = Attr(part, "UId");
            if (kind is "Coil" or "SCoil" or "RCoil" or "TrCoil" or "IlCoil" or "SvCoil")
            {
                string Describe(bool anon)
                {
                    var input = Expr(uid, "in", anon, []);
                    foreach (var negation in part.Elements().Where(x => x.Name.LocalName == "Negated" && Attr(x, "Name") is "in" or "operand")) input = "NOT(" + input + ")";
                    var target = Expr(uid, "operand", anon, []);
                    return kind switch { "SCoil" => $"WHEN {input}: SET {target}; ELSE keep previous state", "RCoil" => $"WHEN {input}: RESET {target}; ELSE keep previous state", _ => $"{target} := {input} ({kind})" };
                }
                var anonymous = Describe(true);
                var id = Fact(unit, Path(part) + "/flow", "ControlFlow", "执行关系：" + Describe(false), anonymous, part);
                if (anonymous.Contains("OPAQUE_INSTRUCTION", StringComparison.Ordinal)) Limit($"OpaqueControlFlow: EvidenceId:{id} LU:{unit.Id} NodeUID:{uid}; instruction semantics require separate analysis from retained source wiring.");
            }
            else if (kind is "TON" or "TOF" or "TP" or "TONR")
                Fact(unit, Path(part) + "/state", "Timer", $"定时器 {kind} UId={uid} 保留 IN/PT/ET/Q/复位及实例接线；输出依赖计时状态，不等同于输入。", $"Timer {kind} UID:{uid}: retain IN/PT/ET/Q/reset and instance wiring; output depends on timer history, never treated as input passthrough.", part);
        }
        foreach (var call in parts.Elements().Where(x => x.Name.LocalName == "Call"))
        {
            var uid = Attr(call, "UId"); var info = call.Elements().FirstOrDefault(x => x.Name.LocalName == "CallInfo");
            bool PinNegated(string pin) => call.Elements().Any(e => e.Name.LocalName == "Negated" && Attr(e, "Name").Equals(pin, StringComparison.OrdinalIgnoreCase));
            string Gate(bool anon) => PinNegated("en") ? "NOT(" + Expr(uid, "en", anon, []) + ")" : Expr(uid, "en", anon, []);
            Fact(unit, Path(call) + "/gate", "CallGate", $"调用 {uid} 的执行门控：{Gate(false)}。", $"CallUID:{uid} EN gate={Gate(true)}; unexecuted calls can retain state/output.", call);
            foreach (var parameter in info?.Elements().Where(x => x.Name.LocalName == "Parameter") ?? [])
            {
                var pin = Attr(parameter, "Name"); var section = Attr(parameter, "Section");
                Fact(unit, Path(parameter) + "/binding", "CallBinding", $"调用 {uid} {section} 参数 {pin} 连接：{Expr(uid, pin, false, [])}。",
                    $"CallUID:{uid} BlockRef:{Block(Attr(info, "Name"))} {section} formal={Local(Attr(info, "Name"), pin)} actual={Expr(uid, pin, true, [])}; pinNegated={PinNegated(pin)}; type={TypeValue(Attr(parameter, "Type"))}.", parameter);
            }
        }
    }

    private string AccessAlias(PipelineUnit unit, XElement access, string name)
    {
        var scope = Attr(access, "Scope");
        if (scope == "LiteralConstant" || access.Descendants().Any(x => x.Name.LocalName == "ConstantValue") && !access.Descendants().Any(x => x.Name.LocalName == "Component")) return name;
        if (scope == "LocalVariable") return RootOrLocal(unit, name);
        if (scope == "LocalConstant") return Local(unit.BlockName, name, unit.BlockId);
        if (scope == "Label") return Opaque(name);
        if (scope == "AddressConstant") return AddressConstantAlias(unit, name);
        return GlobalAlias(name, scope is "GlobalVariable" or "Absolute");
    }
    private string GlobalAlias(string name, bool observable)
    {
        if (!symbols.TryGetValue(name, out var alias))
        {
            alias = "S" + (symbols.Count + 1).ToString("D4"); symbols[name] = alias;
        }
        RegisterPathType(alias, name);
        if (context.TagTypes.TryGetValue(name, out var tagType)) AddSignalType(alias, tagType);
        if (observable && !aliases.ContainsKey(alias) && !name.Contains("[dynamic]", StringComparison.Ordinal) && !name.StartsWith("UNKNOWN_ACCESS_", StringComparison.Ordinal)) aliases[alias] = name;
        return alias;
    }
    internal static string AccessName(XElement access)
    {
        if (Attr(access, "Scope") is "LiteralConstant" or "TypedConstant") return access.Descendants().FirstOrDefault(x => x.Name.LocalName == "ConstantValue")?.Value ?? "UNKNOWN_LITERAL";
        if (Attr(access, "Scope") == "Label") return Attr(access.Elements().FirstOrDefault(x => x.Name.LocalName == "Label"), "Name");
        if (Attr(access, "Scope") == "AddressConstant") return Attr(access.Elements().FirstOrDefault(x => x.Name.LocalName == "Constant"), "Name");
        var symbol = access.Elements().FirstOrDefault(x => x.Name.LocalName == "Symbol");
        var components = (symbol ?? access).Elements().Where(x => x.Name.LocalName == "Component").Select(x => {
            var name = Attr(x, "Name");
            if (Attr(x, "AccessModifier") == "Array")
            {
                var indices = x.Descendants().Where(y => y.Name.LocalName == "ConstantValue").Select(y => y.Value).ToArray();
                name += indices.Length == 0 || x.Descendants().Any(y => y.Name.LocalName == "Access" && Attr(y, "Scope") is not ("LiteralConstant" or "TypedConstant")) ? "[dynamic]" : "[" + string.Join(",", indices) + "]";
            }
            var slice = Attr(x, "SliceAccessModifier");
            if (x.Attribute("SliceAccessModifier") != null)
                name += Regex.IsMatch(slice, @"^[xbwd][0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? ".%" + slice.ToUpperInvariant() : ".%UNKNOWN(" + slice + ")";
            return name;
        });
        var path = string.Join(".", components);
        if (path.Length > 0) return path;
        var address = access.Descendants().FirstOrDefault(x => x.Name.LocalName is "Address" or "Indirect");
        return address == null ? "UNKNOWN_ACCESS_" + Attr(access, "UId") : string.Join(";", address.Attributes().Select(a => a.Name.LocalName + "=" + a.Value));
    }
    private string AddressConstantAlias(PipelineUnit unit, string name)
    {
        if (name.StartsWith("P#", StringComparison.OrdinalIgnoreCase)) return name;
        if (!string.IsNullOrWhiteSpace(unit.InterfaceXml))
        {
            try
            {
                if (context.Parse(unit.InterfaceXml).Descendants().Any(e => e.Name.LocalName == "Member" && MemberPath(e).Equals(name.TrimStart('#'), StringComparison.OrdinalIgnoreCase))) return "ADDRESS_OF(" + RootOrLocal(unit, name) + ")";
            }
            catch (XmlException) { }
        }
        return Opaque(name);
    }
    private string Block(string value) { if (!blocks.TryGetValue(value, out var alias)) blocks[value] = alias = "B" + (blocks.Count + 1).ToString("D3"); return alias; }
    private string Local(string block, string value, long? knownBlockId = null)
    {
        var path = value.TrimStart('#');
        var blockId = knownBlockId ?? context.UniqueBlockId(block);
        var key = (blockId.HasValue ? "BlockId:" + blockId.Value : "UnresolvedBlock:" + block) + "\0" + path;
        if (!locals.TryGetValue(key, out var alias))
        {
            locals[key] = alias = "P" + (locals.Count + 1).ToString("D4");
            if (!blockId.HasValue) Limit($"UnresolvedSymbolBlock: SymbolRef:{alias} BlockRef:{Block(block)}; missing or ambiguous source block identity; no declaration binding is asserted.");
        }
        if (blockId.HasValue) RecordSymbol(alias, blockId.Value, path);
        RegisterPathType(alias, path);
        return alias;
    }
    private void RecordSymbol(string alias, long blockId, string path)
    {
        var declaration = context.Declaration(blockId, path);
        if (symbolDefinitions.TryGetValue(alias, out var existing) && declaration == null) return;
        symbolDefinitions[alias] = new(alias, blockId, declaration?.Path ?? path, declaration?.Section ?? "Unknown", declaration?.DataType ?? "");
        RegisterPathType(alias, path);
        AddSignalType(alias, declaration?.DataType);
    }

    private void RegisterPathType(string alias, string path)
    {
        if (path.Contains("%UNKNOWN(", StringComparison.Ordinal) || path.Contains("[dynamic]", StringComparison.Ordinal) || path.StartsWith("UNKNOWN_ACCESS_", StringComparison.Ordinal))
        {
            unresolvedTypes.Add(alias); signalTypes[alias] = "Unknown"; return;
        }
        AddSignalType(alias, BusinessSignalTypes.SliceType(path));
    }
    private void AddSignalType(string alias, string? type)
    {
        if (unresolvedTypes.Contains(alias)) return;
        signalTypes[alias] = BusinessSignalTypes.Merge(signalTypes.GetValueOrDefault(alias), type);
    }

    private void CollectSignalTypes(PipelineUnit unit, XElement root)
    {
        foreach (var net in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "FlgNet"))
        {
            var parts = net.Elements().FirstOrDefault(e => e.Name.LocalName == "Parts");
            if (parts == null) continue;
            var nodes = parts.Elements().Where(e => Attr(e,"UId").Length > 0).GroupBy(e => Attr(e,"UId"), StringComparer.Ordinal)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            string? PinType(XElement node, string pin)
            {
                if (node.Name.LocalName == "Call")
                {
                    if (pin.Equals("en",StringComparison.OrdinalIgnoreCase)||pin.Equals("eno",StringComparison.OrdinalIgnoreCase)) return "Bool";
                    var info = node.Elements().FirstOrDefault(e => e.Name.LocalName == "CallInfo");
                    var parameter = info?.Elements().FirstOrDefault(e => e.Name.LocalName == "Parameter" && Attr(e,"Name").Equals(pin,StringComparison.OrdinalIgnoreCase));
                    return parameter == null ? null : Attr(parameter,"Type");
                }
                var name = Attr(node,"Name");
                var src = node.Elements().FirstOrDefault(e => e.Name.LocalName == "TemplateValue" && Attr(e,"Name") == "SrcType")?.Value;
                if (name is "Eq" or "Ne" or "Gt" or "Ge" or "Lt" or "Le") return pin is "in1" or "in2" ? src : pin is "pre" or "out" ? "Bool" : null;
                if (name is "Contact" or "PContact" or "NContact" or "Coil" or "SCoil" or "RCoil" or "PBox" or "NBox") return pin is "operand" or "bit" or "in" or "out" or "pre" ? "Bool" : null;
                if (name is "A" or "O" or "X" or "Not") return pin == "out" || Regex.IsMatch(pin,@"^in[0-9]*$") ? "Bool" : null;
                if (name is "Add" or "Sub" or "Mul" or "Div") return pin is "en" or "eno" ? "Bool" : pin == "out" || Regex.IsMatch(pin,@"^in[0-9]+$") ? src : null;
                return null;
            }
            foreach (var wire in net.Elements().Where(e => e.Name.LocalName == "Wires").SelectMany(e => e.Elements().Where(w => w.Name.LocalName == "Wire")))
            {
                var types = wire.Elements().Where(e => e.Name.LocalName == "NameCon")
                    .Select(e => nodes.TryGetValue(Attr(e,"UId"),out var node) ? PinType(node,Attr(e,"Name")) : null).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
                foreach (var endpoint in wire.Elements().Where(e => e.Name.LocalName == "IdentCon"))
                    if (nodes.TryGetValue(Attr(endpoint,"UId"),out var access) && access.Name.LocalName == "Access" && Attr(access,"Scope") is "LocalVariable" or "GlobalVariable" or "Absolute")
                        foreach (var type in types) AddSignalType(AccessAlias(unit,access,AccessName(access)),type);
            }
        }
    }
    private string Opaque(string value) { if (!opaqueNames.TryGetValue(value, out var alias)) opaqueNames[value] = alias = "N" + (opaqueNames.Count + 1).ToString("D4"); return alias; }
    private string Scrub(PipelineUnit unit, string text)
    {
        text = Regex.Replace(text, @"'([^']|'')*'|(?s:\(\*.*?\*\)|/\*.*?\*/)|//[^\r\n]*", m => m.Value.StartsWith("'", StringComparison.Ordinal) ? m.Value : " ");
        // Literals are preserved exactly; identifier spelling is not a semantic premise.
        return Regex.Replace(text, "'([^']|'')*'|[A-Za-z_][A-Za-z0-9_]*#[A-Za-z0-9_#+.:-]+|[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?|#[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*|[A-Za-z_][A-Za-z0-9_]*", m =>
            m.Value.StartsWith("'", StringComparison.Ordinal) || char.IsDigit(m.Value[0]) || m.Value.Contains('#') && !m.Value.StartsWith("#", StringComparison.Ordinal) || Keywords.Contains(m.Value) ? m.Value : m.Value.StartsWith("#", StringComparison.Ordinal) ? RootOrLocal(unit, m.Value) : Opaque(m.Value));
    }
    internal static string MemberPath(XElement member) => string.Join(".", member.AncestorsAndSelf().Where(x => x.Name.LocalName == "Member").Reverse().Select(x => Attr(x, "Name")));
    private static string EndpointKey(XElement endpoint) => endpoint.Name.LocalName + ":" + Attr(endpoint, "UId") + ":" + Attr(endpoint, "Name");
    private static string Attr(XElement? e, string name) => e?.Attribute(name)?.Value ?? "";
    private static string Path(XElement node) => "/" + string.Join("/", node.AncestorsAndSelf().Reverse().Select(n => n.Name.LocalName + "[" + (n.ElementsBeforeSelf().Count(e => e.Name == n.Name) + 1) + "]"));
    private string Fact(PipelineUnit unit, string path, string kind, string actual, string anonymous, XElement? sourceNode = null)
    {
        var hash = context.SourceHashes.TryGetValue(unit.Id, out var sourceHash) ? sourceHash : PipelineHash.Of(unit.RawContent);
        var id = "E_" + PipelineHash.Of(unit.Id + "|" + path + "|" + kind + "|" + hash)[..20];
        var location = $"LU:{unit.Id} {path}";
        markdown.AppendLine($"- [{id}] {actual}（{location}）");
        var fact = $"{anonymous} [source LU:{unit.Id} {(sourceNode == null ? path : context.Location(sourceNode))}]";
        // The complete trace remains available in Evidence/Markdown. The model sees the lossless
        // compact tree once, plus concise operational summaries, not thousands of repeated paths.
        if (kind is "SourceStructure" or "InterfaceStructure" or "Unit" or "BlockBody" or "CallGate" or "CallBinding" or "CallBodyReference" or "MissingBlockBody" or "Timer" or "RootInstanceBinding" or "RootMemberBinding" || kind == "ControlFlow" && anonymous.Length <= 1024)
            model.AppendLine($"[{id}] {fact}");
        evidence.Add(new(id, kind, fact));
        return id;
    }
    private void Limit(string text) { limitations.Add(text); markdown.AppendLine("- 限制：" + text); model.AppendLine("LIMITATION: " + text); }
}
