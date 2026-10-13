#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Presentation only. Covered callee wire trees are represented by persisted operational facts;
/// caller and unsupported source trees retain the existing reversible source projection.</summary>
public static class BusinessUnitModelProjection
{
    public const string Version="business-unit-model-projection-1";
    public sealed record Result(string ModelText,IReadOnlyList<string> ProjectedEvidenceIds,IReadOnlyDictionary<string,string> RetainedSources,bool IncludesNumericEvidence);
    static readonly HashSet<string> Boolean=new(["Contact","A","O","And","Or","Not","NOT","Coil","SCoil","RCoil"],StringComparer.Ordinal);
    static readonly HashSet<string> Numeric=new(["Add","Sub","Mul","Div","Move","Ge","Gt","Le","Lt","Eq","Ne","PContact","NContact","PBox","NBox"],StringComparer.Ordinal);
    static readonly HashSet<string> Coils=new(["Coil","SCoil","RCoil"],StringComparer.Ordinal);
    static readonly Regex BadReference=new(@"OPAQUE_INSTRUCTION|WIRE_REFERENCE|STATE_OR_CYCLE|UNCONNECTED|UNKNOWN_|CONNECTED\(|Unknown\(",RegexOptions.CultureInvariant);
    static string A(XElement e,string name)=>(string?)e.Attribute(name)??"";
    static string? Lu(string text)=>Regex.Match(text,@"\[source LU:(\d+)(?:\s|\])").Groups[1] is {Success:true} g?g.Value:null;
    static string Block(string text)=>Regex.Match(text,@"\bBlockRef:([^\s;]+)").Groups[1].Value;
    public static string Build(BusinessDescription description)=>Project(description).ModelText;
    public static Result Project(BusinessDescription description) {
        ArgumentNullException.ThrowIfNull(description);
        var text=BusinessModelProjection.Build(description);
        var numeric=BusinessNumericEvidence.Build(description);
        var projected=new List<string>();var retained=new Dictionary<string,string>(StringComparer.Ordinal);
        var rootSources=description.Evidence.Where(e=>e.Kind=="SourceStructure"&&Lu(e.Text)==description.LogicUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var rootBlocks=rootSources.Select(e=>Block(e.Text)).ToHashSet(StringComparer.Ordinal);
        foreach(var source in description.Evidence.Where(e=>e.Kind=="SourceStructure")) {
            var original="["+source.Id+"] "+source.Text;
            if(!description.ModelText.Contains(original,StringComparison.Ordinal))continue;
            if(rootSources.Length==0||rootBlocks.Contains(Block(source.Text))) {retained[source.Id]="CallerOrUnresolvedRoot";continue;}
            var lu=Lu(source.Text);
            var facts=description.Evidence.Where(e=>e.Kind is "ControlFlow" or "CallBinding" or "CallGate" or "Timer" or "Unit" or "BlockBody").Where(e=>Lu(e.Text)==lu).ToArray();
            var numbers=numeric.Where(f=>f.EvidenceId==source.Id).ToArray();
            string? reason=null;string? compact=null;
            try {
                var start=source.Text.IndexOf('<');var end=source.Text.LastIndexOf('>');
                if(start<0||end<start){reason="MissingSourceTree";}
                else {
                    var xml=source.Text[start..(end+1)];
                    using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32_000_000});
                    var tree=XElement.Load(reader);
                    reason=Covered(tree,source,facts,numbers,description);
                    if(reason is null)compact="["+source.Id+"] "+source.Text[..start]+"\n"+BusinessModelProjection.EncodeCompact(xml)+source.Text[(end+1)..];
                    if(reason is null&&!text.Contains(compact,StringComparison.Ordinal))reason="ProjectionAnchorMismatch";
                    if(reason is null) {
                        var header=source.Text[..start].Replace("complete source:","operational source projection:",StringComparison.Ordinal);
                        var refs=string.Join(",",facts.Where(f=>f.Kind=="ControlFlow").Select(f=>f.Id));
                        var replacement="["+source.Id+"] "+header+" Complete supported effects are retained in ControlFlow evidence ["+refs+"]"+
                            (numbers.Length>0?" and the numeric wiring projection under this same evidence ID.":".")+" This replaces only the redundant callee wire tree, not a state/time proof."+source.Text[(end+1)..];
                        text=text.Replace(compact!,replacement,StringComparison.Ordinal);projected.Add(source.Id);
                    }
                }
            }catch(Exception ex)when(ex is XmlException or FormatException or ArgumentException){reason="MalformedOrAmbiguousSource";}
            if(reason is not null)retained[source.Id]=reason;
        }
        var numericText=BusinessNumericEvidence.Format(numeric);
        if(numericText.Length>0)text+="\n"+numericText;
        return new(text,projected,retained,true);
    }
    static string? Covered(XElement root,BusinessEvidence source,BusinessEvidence[] facts,BusinessNumericFact[] numeric,BusinessDescription description) {
        if(root.Name.LocalName!="FlgNet")return "NonFlgNetSource";
        if(root.Attributes().Any()||root.Nodes().Any(n=>n is not XElement&&n is not XText)||root.Nodes().OfType<XText>().Any(n=>!string.IsNullOrWhiteSpace(n.Value)))return "UnrepresentedSourceMetadata";
        if(!Regex.IsMatch(source.Text,@"\bNetworkOrder:\d+\b")||Lu(source.Text) is null)return "MissingNetworkOrderOrLocation";
        var parts=root.Elements().SingleOrDefault(e=>e.Name.LocalName=="Parts");var wires=root.Elements().SingleOrDefault(e=>e.Name.LocalName=="Wires");
        if(parts is null||wires is null||root.Elements().Any(e=>e!=parts&&e!=wires))return "IncompleteNetwork";
        if(root.DescendantsAndSelf().Any(e=>e.Name.NamespaceName.Length>0))return "UnrecognizedNamespace";
        if(parts.Elements().Any(e=>e.Name.LocalName is not("Access" or "Part")))return "CallOrUnknownInstruction";
        var nodes=parts.Elements().ToArray();
        if(nodes.Any(e=>A(e,"UId").Length==0)||nodes.Select(e=>A(e,"UId")).Distinct().Count()!=nodes.Length)return "AmbiguousNodeIdentity";
        var byId=nodes.ToDictionary(e=>A(e,"UId"),StringComparer.Ordinal);
        var operations=nodes.Where(e=>e.Name.LocalName=="Part").ToArray();
        if(operations.Length==0||operations.Any(p=>!Boolean.Contains(A(p,"Name"))&&!Numeric.Contains(A(p,"Name"))))return "UnknownInstruction";
        if(operations.Any(p=>p.Attributes().Any(a=>a.Name.LocalName is not("Name" or "UId" or "DisabledENO"))||
            p.Elements().Any(c=>c.Name.LocalName is not("TemplateValue" or "Negated"))||
            p.Elements().Where(c=>c.Name.LocalName=="TemplateValue").Any(c=>A(c,"Name") is not("Card" or "SrcType"))))return "UnrepresentedInstructionMetadata";
        foreach(var access in nodes.Where(e=>e.Name.LocalName=="Access")) {
            if(A(access,"Scope") is not("LocalVariable" or "GlobalVariable" or "LiteralConstant" or "TypedConstant")||access.Attributes().Any(a=>a.Name.LocalName is not("UId" or "Scope" or "SymbolRef")))return "UnrepresentedAccessScope";
            if(access.Descendants().Any(e=>e.Name.LocalName is "Address" or "Indirect"||e.Attribute("AccessModifier") is not null||e.Name.LocalName=="Access"))return "IndexedOrIndirectAccess";
            if(A(access,"Scope") is "LocalVariable" or "GlobalVariable"&&!Regex.IsMatch(A(access,"SymbolRef"),@"^[PS]\d+$"))return "MissingSymbolIdentity";
        }
        var pins=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach(var wire in wires.Elements()) {
            var ends=wire.Elements().ToArray();if(wire.Name.LocalName!="Wire"||ends.Length<2)return "IncompleteWire";
            if(wire.Attributes().Any(a=>a.Name.LocalName!="UId")||ends.Any(e=>e.Elements().Any()||e.Attributes().Any(a=>a.Name.LocalName is not("UId" or "Name"))))return "UnrepresentedWireMetadata";
            var outputs=0;var inputs=0;var identities=0;var rails=0;
            foreach(var end in ends) {
                if(end.Name.LocalName=="Powerrail"){rails++;continue;}
                if(end.Name.LocalName is not("NameCon" or "IdentCon")||!byId.TryGetValue(A(end,"UId"),out var node))return "UnknownOrOpenEndpoint";
                if(end.Name.LocalName=="IdentCon") {if(node.Name.LocalName!="Access")return "InvalidIdentCon";identities++;continue;}
                if(node.Name.LocalName!="Part")return "InvalidNameCon";
                var pin=A(end,"Name");if(pin.Length==0)return "UnknownPin";
                var key=A(end,"UId")+"."+pin;pins[key]=pins.GetValueOrDefault(key)+1;
                if(pin is "out" or "eno"||Regex.IsMatch(pin,@"^out\d+$"))outputs++;else if(pin is "in" or "operand" or "en" or "pre" or "bit"||Regex.IsMatch(pin,@"^in\d+$"))inputs++;else return "UnknownPin";
            }
            if(outputs+rails+(outputs==0?identities:0)!=1||inputs+identities==0)return "AmbiguousWireDrivers";
        }
        if(pins.Values.Any(n=>n!=1))return "MultiplePinConnections";
        foreach(var part in operations) {
            var kind=A(part,"Name");var uid=A(part,"UId");var required=kind switch {
                "Contact" or "Coil" or "SCoil" or "RCoil"=>new[]{"in","operand"},"Not" or "NOT"=>["in"],
                "PContact" or "NContact"=>["pre","operand","bit"],"PBox" or "NBox"=>["in","bit"],
                "Move"=>["en","in"],"Ge" or "Gt" or "Le" or "Lt" or "Eq" or "Ne"=>["pre","in1","in2"],
                "Add" or "Sub" or "Mul" or "Div"=>["en","in1","in2"],_=>Array.Empty<string>()};
            if(kind is "A" or "O" or "And" or "Or") {
                if(!int.TryParse(part.Elements().FirstOrDefault(e=>e.Name.LocalName=="TemplateValue"&&A(e,"Name")=="Card")?.Value,out var arity)||arity<1||arity>32)return "UnknownBooleanArity";
                required=Enumerable.Range(1,arity).Select(i=>"in"+i).ToArray();
            }
            if(required.Any(pin=>!pins.ContainsKey(uid+"."+pin)))return "MissingRequiredPin";
            if(Boolean.Contains(kind)) {
                if(part.Attribute("DisabledENO") is not null||part.Elements().Where(e=>e.Name.LocalName=="TemplateValue").Any(e=>A(e,"Name")!="Card"||kind is not("A" or "O" or "And" or "Or")))return "UnrepresentedBooleanMetadata";
                var allowed=required.Append("out").ToHashSet(StringComparer.OrdinalIgnoreCase);
                if(pins.Keys.Where(k=>k.StartsWith(uid+".",StringComparison.Ordinal)).Any(k=>!allowed.Contains(k[(uid.Length+1)..])))return "UnrepresentedBooleanPin";
            }
        }
        var flows=facts.Where(f=>f.Kind=="ControlFlow").ToArray();
        if(facts.Any(f=>!description.ModelText.Contains("["+f.Id+"] "+f.Text,StringComparison.Ordinal)))return "HiddenOrOverlongOperationalFact";
        if(facts.Any(f=>Regex.IsMatch(f.Text,@"WIRE_REFERENCE|STATE_OR_CYCLE|UNCONNECTED|UNKNOWN_|CONNECTED\(")))return "UnexpandedOperationalReference";
        if(numeric.Any(n=>BadReference.IsMatch(n.Enable+" "+n.Precondition+" "+n.Expression+" "+string.Join(" ",n.Inputs.Values)+" "+string.Join(" ",n.Outputs.Select(o=>o.Target+" "+o.Via)))))return "IncompleteNumericProjection";
        var ordinal=root.DescendantsAndSelf().Select((node,index)=>(node,index:index+1)).ToDictionary(p=>p.node,p=>p.index);
        foreach(var part in operations.Where(p=>Coils.Contains(A(p,"Name"))||Numeric.Contains(A(p,"Name")))) {
            var uid=A(part,"UId");var resolved=numeric.Where(n=>n.Uid==uid).ToArray();
            if(resolved.Length==1)continue;
            if(!Coils.Contains(A(part,"Name")))return "MissingNumericFact";
            var summaries=flows.Where(f=>Regex.IsMatch(f.Text,@"\bxml:"+ordinal[part]+@"\b")).ToArray();
            if(summaries.Length!=1||BadReference.IsMatch(summaries[0].Text))return "IncompleteControlFlow";
        }
        if(flows.Length==0&&numeric.Length==0)return "NoOperationalEffects";
        return null;
    }
}
