#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business.Rendering;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Admits an exact current template contract only after checking its observable binding.
/// This is not a proof of model-derived template semantics, and never uses a unit model vote as proof.</summary>
public static class BusinessCallAcceptanceGate
{
    // A source snapshot and rendered description are immutable for a run. Cache source-derived
    // binding facts only; provenance/model decisions remain inputs on every validation call.
    private static readonly ConditionalWeakTable<PipelineSnapshot,SnapshotContext> Contexts=new();
    private sealed class SnapshotContext(PipelineSnapshot source)
    {
        public readonly string Fingerprint=source.Fingerprint();
        private readonly BusinessDescriptionContext descriptions=new(source);
        private readonly ConcurrentDictionary<long,Lazy<BusinessDescription>> templates=new();
        private readonly ConditionalWeakTable<BusinessDescription,ConcurrentDictionary<string,Lazy<BusinessTemplateBindingResult>>> bindings=new();
        private readonly ConditionalWeakTable<BusinessDescription,Lazy<IReadOnlyList<string>>> outputConflicts=new();
        public BusinessDescription Template(long id)=>templates.GetOrAdd(id,key=>new(()=>{lock(descriptions)return descriptions.BuildBlock(key);})).Value;
        public IReadOnlyList<string> OutputConflicts(PipelineUnit unit,BusinessDescription description)=>outputConflicts.GetValue(description,_=>new(()=>ExplicitOutputConflicts(source,unit,description))).Value;
        public BusinessTemplateBindingResult Bind(PipelineUnit unit,BusinessDescription description,BusinessDescription template,BusinessCandidate probe,bool strict)
        {
            var cache=bindings.GetValue(description,_=>new());
            var key=unit.Id+"|"+template.Fingerprint+"|"+strict+"|"+BusinessCandidateValidator.Key(probe);
            return cache.GetOrAdd(key,_=>new(()=>BusinessTemplateBinder.BindWithDiagnostics(source,unit,description,template,[probe],strict))).Value;
        }
    }

    public static BusinessDecision Validate(PipelineSnapshot source,PipelineUnit unit,BusinessDescription description,
        BusinessCandidate candidate,BusinessTemplateCandidateResult? templates)
    {
        BusinessDecision Unknown(string code)=>new(candidate,"Unknown",code);
        BusinessDecision Reject(string code)=>new(candidate,"Rejected",code);
        var context=Contexts.GetValue(source,s=>new(s));var fingerprint=context.Fingerprint;
        var units=source.Units.Where(u=>u.Id==unit.Id).ToArray();
        if(units.Length!=1||units[0].RawContent!=unit.RawContent||units[0].BlockId!=unit.BlockId||
            description.Status!="Rendered"||description.LogicUnitId!=unit.Id||description.BlockId!=unit.BlockId||
            description.SourceHash!=PipelineHash.Of(unit.RawContent)||description.Fingerprint!=BusinessDescriptionBuilder.Key(fingerprint,unit))
            return Unknown("CallSourceMismatch");
        var needed=References(candidate).ToHashSet(StringComparer.Ordinal);
        if(needed.Count==0||needed.Any(a=>!description.SignalAliases.ContainsKey(a)))return Unknown("CallUnknownActualBinding");
        foreach(var outputConflict in context.OutputConflicts(unit,description))
            if(needed.Any(a=>BusinessMemoryAliases.For(source).Compare(description.SignalAliases[a],outputConflict)==BusinessMemoryRelation.Overlap))
                return Reject("CallOutputOverwritten:"+outputConflict);
        var key=BusinessCandidateValidator.Key(candidate);
        var hasCandidate=templates?.Candidates.Any(c=>BusinessCandidateValidator.Key(c)==key)==true;
        var matches=hasCandidate?templates!.Provenance.Where(p=>BusinessCandidateValidator.Key(p.Candidate)==key).ToArray():[];
        if(matches.Length==0)return Unknown("CurrentTemplateContractRequired");
        foreach(var name in unit.Callees.Distinct(StringComparer.OrdinalIgnoreCase)) {
            var bodies=source.Units.Where(u=>u.BlockName.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
            var ids=bodies.Select(u=>u.BlockId).Distinct().ToArray();
            if(ids.Length!=1||bodies.Any(u=>string.IsNullOrWhiteSpace(u.RawContent)))continue;
            var template=context.Template(ids[0]);
            if(template.Status!="TemplateRendered")continue;
            var bound=new HashSet<string>(StringComparer.Ordinal);
            var evidence=template.Evidence.Where(e=>e.Kind=="SourceStructure").Select(e=>e.Id).ToArray();
            if(evidence.Length==0)continue;
            // Unary probes verify only substitution, writers and observation epochs. Their invented
            // assertions are NEVER accepted as constraints or used as semantic evidence.
            var symbols=(template.Symbols??[]).Where(s=>s.BlockId==template.BlockId&&s.Section is "Input" or "Output" or "InOut")
                .Select(s=>(s.Alias,s.DataType)).Concat(template.SignalAliases.Keys.Select(a=>(Alias:a,DataType:template.SignalTypes.GetValueOrDefault(a,""))))
                .Distinct().Take(513).ToArray();
            if(symbols.Length>512)continue;
            foreach(var symbol in symbols) {
                BusinessCandidate probe;
                if(symbol.DataType.Equals("Bool",StringComparison.OrdinalIgnoreCase))
                    probe=new("MUST_BE_FALSE",[new(symbol.Alias,"Target")],"Binding probe only",evidence,[],"ProgramRelation");
                else {
                    try {PipelineNumeric.Coerce(0,symbol.DataType);}catch(PipelineException){continue;}
                    var scalar=new LogicTerm("ScalarSignal",symbol.Alias,DataType:symbol.DataType);
                    probe=new("EXPRESSION",[new(symbol.Alias,"Member")],"Binding probe only",evidence,[],"ProgramRelation",
                        Assertion:new("Compare","Eq",[scalar,scalar],symbol.DataType));
                }
                var atCall=context.Bind(unit,description,template,probe,false);
                var stable=context.Bind(unit,description,template,probe with{Applicability="StableDiagnosticSnapshot"},true);
                var mapped=atCall.Candidates.SelectMany(References).ToHashSet(StringComparer.Ordinal);
                foreach(var diagnostic in stable.Diagnostics.Concat(atCall.Diagnostics)) {
                    const string conflict="ConflictingCallerWrite:";
                    var at=diagnostic.IndexOf(conflict,StringComparison.Ordinal);
                    if(at>=0) {
                        var path=diagnostic[(at+conflict.Length)..];if(path.StartsWith("G:",StringComparison.Ordinal))path=path[2..];
                        if(needed.Any(a=>BusinessMemoryAliases.For(source).Compare(description.SignalAliases[a],path)==BusinessMemoryRelation.Overlap))
                            return Reject("CallBindingConflict:"+diagnostic);
                    }
                    if(diagnostic.Contains("CallerInputOrEnableWrittenWithinScan",StringComparison.Ordinal)&&mapped.Overlaps(needed))
                        return Reject("CallBindingEpochConflict:"+diagnostic);
                }
                if(stable.StableWriterInventoryComplete)bound.UnionWith(stable.Candidates.SelectMany(References));
            }
            var current=matches.Where(p=>p.Callee.Equals(name,StringComparison.OrdinalIgnoreCase)&&p.TemplateFingerprint==template.Fingerprint&&
                p.Origin is "ModelCodeReviewed" or "CodeDerivedStableSlice" or "FallbackCodeDerivedStableSlice").ToArray();
            if(current.Length==0||!needed.IsSubsetOf(bound))continue;
            foreach(var provenance in current) {
                if(provenance.Origin=="ModelCodeReviewed") {
                    var code=provenance.DecisionCode;
                    if(!(code=="TemplateModelCodeReviewAccepted"||code.StartsWith("TemplateModelCodeReviewAccepted:",StringComparison.Ordinal)||
                         code=="TemplateModelCodeReviewCorrected"||code.StartsWith("TemplateModelCodeReviewCorrected:",StringComparison.Ordinal)))continue;
                    return new(candidate,"Validated","CurrentTemplateBindingValidated");
                }
                // A claimed code-derived origin must also be reproducible from the present body.
                var proof=BusinessTemplateBinder.BindWithDiagnostics(source,unit,description,template,BusinessTemplateInference.StableSliceCandidates(template),true);
                if(proof.StableWriterInventoryComplete&&proof.Candidates.Any(c=>BusinessCandidateValidator.Key(c)==key))
                    return new(candidate,"Validated","CurrentCodeTemplateBindingValidated");
            }
        }
        return Unknown(matches.Length==0?"CurrentTemplateContractRequired":"CallBindingProofUnavailable");
    }

    private static IEnumerable<string> References(BusinessCandidate candidate)=>candidate.Signals.Select(s=>s.Name)
        .Concat(candidate.Conditions.Select(c=>c.Signal)).Concat(candidate.SettlingTimeSignal==null?[]:[candidate.SettlingTimeSignal])
        .Concat(new[]{candidate.Assertion,candidate.ApplicabilityCondition}.Where(t=>t!=null)
            .SelectMany(t=>PipelineTerms.Walk(t!)).Where(t=>t.Kind is "Signal" or "ScalarSignal").Select(t=>t.Name!));

    // A known direct second writer remains a conflict even if the callee body is unavailable.
    // Do not infer absence of writers here; all positive acceptance still goes through the binder.
    private static IReadOnlyList<string> ExplicitOutputConflicts(PipelineSnapshot source,PipelineUnit caller,BusinessDescription description)
    {
        var writes=new List<(string Path,bool Call)>();
        foreach(var unit in source.Units.Where(u=>u.BlockId==caller.BlockId)) {
            try {
                using var reader=XmlReader.Create(new StringReader(unit.RawContent),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32_000_000});
                var root=XElement.Load(reader);var networks=root.DescendantsAndSelf().Where(e=>e.Name.LocalName=="FlgNet").ToArray();
                if(networks.Length!=1)continue;var graph=networks[0];
                var parts=graph.Elements().Single(e=>e.Name.LocalName=="Parts").Elements().ToDictionary(e=>A(e,"UId"));
                var wires=graph.Elements().Single(e=>e.Name.LocalName=="Wires").Elements().ToArray();
                string? Target(string uid,string pin) {
                    var found=wires.Where(w=>w.Elements().Any(e=>e.Name.LocalName=="NameCon"&&A(e,"UId")==uid&&A(e,"Name")==pin)).ToArray();
                    if(found.Length!=1||found[0].Elements().Count()!=2)return null;
                    var ident=found[0].Elements().SingleOrDefault(e=>e.Name.LocalName=="IdentCon");
                    if(ident==null||!parts.TryGetValue(A(ident,"UId"),out var access)||access.Name.LocalName!="Access")return null;
                    var components=access.Elements().SingleOrDefault(e=>e.Name.LocalName=="Symbol")?.Elements().ToArray();
                    if(components==null||components.Length==0||components.Any(c=>c.Name.LocalName!="Component"||c.Attribute("SliceAccessModifier")!=null))return null;
                    var path=string.Join(".",components.Select(PipelineFlgNetCompiler.ComponentName));
                    if(A(access,"Scope")=="GlobalVariable")return path;
                    if(A(access,"Scope")!="LocalVariable")return null;
                    var aliases=(description.Symbols??[]).Where(s=>s.BlockId==caller.BlockId&&s.Path.Equals(path,StringComparison.OrdinalIgnoreCase)&&description.SignalAliases.ContainsKey(s.Alias))
                        .Select(s=>description.SignalAliases[s.Alias]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    return aliases.Length==1?aliases[0]:null;
                }
                foreach(var part in parts.Values) {
                    if(part.Name.LocalName=="Call") {
                        foreach(var parameter in part.Descendants().Where(e=>e.Name.LocalName=="Parameter"&&A(e,"Section") is "Output" or "InOut"))
                            if(Target(A(part,"UId"),A(parameter,"Name")) is { } output)writes.Add((output,true));
                    } else if(A(part,"Name") is "Coil" or "SCoil" or "RCoil") {
                        if(Target(A(part,"UId"),"operand") is { } output)writes.Add((output,false));
                    }
                }
            }catch(Exception error) when(error is XmlException or PipelineException or ArgumentException or InvalidOperationException){/* Unknown syntax is not an absence-of-writes proof. */}
        }
        var memory=BusinessMemoryAliases.For(source);
        return writes.Where(w=>w.Call).Where(output=>writes.Count(w=>memory.Compare(w.Path,output.Path)==BusinessMemoryRelation.Overlap)>1)
            .Select(w=>w.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string A(XElement element,string name)=>(string?)element.Attribute(name)??"";
}
