#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed record BusinessCompletedTemplateContext(long BlockId,string Fingerprint,IReadOnlyList<string> EvidenceIds,IReadOnlyList<long> SourceLuIds);
public interface IBusinessTemplateContextInference
{
    Task<IReadOnlyList<BusinessCandidate>> InferAsync(BusinessDescription description,BusinessTemplateCandidateResult templates,CancellationToken token);
    Task<IReadOnlyList<BusinessDecision>> ReviewAsync(BusinessDescription description,IReadOnlyList<BusinessCandidate> candidates,BusinessTemplateCandidateResult templates,CancellationToken token);
}

/// <summary>Reuses completed template conclusions, not another interpretation of the same callee source.
/// Caller code and unhandled/failed template sources retain the normal complete context.</summary>
public static class BusinessTemplateReuseContext
{
    public const string Version="business-template-reuse-context-2";
    public static bool IsCallAdapterOnly(BusinessDescription description) {
        var sources=description.Evidence.Where(e=>e.Kind=="SourceStructure"&&e.Text.Contains("[source LU:"+description.LogicUnitId+" ",StringComparison.Ordinal)).ToArray();
        if(sources.Length!=1)return false;
        try {
            var text=sources[0].Text;var start=text.IndexOf('<');var end=text.LastIndexOf('>');if(start<0||end<start)return false;
            using var reader=XmlReader.Create(new StringReader(text[start..(end+1)]),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32_000_000});
            var root=XElement.Load(reader);if(root.Name.LocalName!="FlgNet")return false;
            var parts=root.Elements().Single(e=>e.Name.LocalName=="Parts").Elements().ToArray();
            var calls=parts.Where(e=>e.Name.LocalName=="Call").ToArray();if(calls.Length!=1)return false;
            string A(XElement e,string n)=>(string?)e.Attribute(n)??"";
            var call=calls[0];var callId=A(call,"UId");
            if(call.Elements().Count(e=>e.Name.LocalName=="CallInfo")!=1)return false;
            var outputs=call.Descendants().Where(e=>e.Name.LocalName=="Parameter"&&A(e,"Section")=="Output").Select(e=>A(e,"Name")).ToHashSet(StringComparer.Ordinal);
            var wires=root.Elements().Single(e=>e.Name.LocalName=="Wires").Elements().ToArray();
            foreach(var part in parts.Where(e=>e.Name.LocalName!="Access"&&e.Name.LocalName!="Call")) {
                if(part.Name.LocalName!="Part")return false;var kind=A(part,"Name");var uid=A(part,"UId");
                if(kind is not("Contact" or "A" or "O" or "And" or "Or" or "Not" or "NOT" or "Coil"))return false;
                if(kind=="Coil") {
                    if(part.Elements().Any(e=>e.Name.LocalName=="Negated"))return false;
                    var feeding=wires.Where(w=>w.Elements().Any(e=>e.Name.LocalName=="NameCon"&&A(e,"UId")==uid&&A(e,"Name")=="in")).ToArray();
                    if(feeding.Length!=1||!feeding[0].Elements().Any(e=>e.Name.LocalName=="NameCon"&&A(e,"UId")==callId&&outputs.Contains(A(e,"Name"))))return false;
                } else if(wires.Any(w=>w.Elements().Any(e=>e.Name.LocalName=="NameCon"&&A(e,"UId")==uid&&A(e,"Name")=="out")&&w.Elements().Any(e=>e.Name.LocalName=="IdentCon")))return false;
            }
            return true;
        }catch(Exception ex)when(ex is XmlException or InvalidOperationException or ArgumentException){return false;}
    }
    public sealed record Result(BusinessDescription Description,string ContractContext,IReadOnlyList<long> ReusedBlocks,IReadOnlySet<string> ReusedEvidenceIds,IReadOnlySet<string> ContractKeys);
    static readonly JsonSerializerOptions Json=new(){Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    static readonly Regex Conflict=new("Aliased|OverlapConflict|WrittenWithinScan|EpochConflict|InvalidCandidateBinding|Conflicting|InputOutputAlias",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    static string Scrub(string value)=>Regex.Replace(value,@"Callee:[^:|\r\n]+","Callee:OpaqueCallee");
    public static Result Build(BusinessDescription description,BusinessTemplateCandidateResult templates) {
        var text=description.ModelText;var removed=new HashSet<string>(StringComparer.Ordinal);var reused=new List<long>();var records=new List<object>();var keys=new HashSet<string>(StringComparer.Ordinal);
        var diagnostics="\nTemplate binding diagnostics (data; known alias/epoch/scan conflicts must not be accepted):\n"+
            JsonSerializer.Serialize(templates.Callees.Select(c=>new{c.BlockId,c.Status,c.CoverageStatus,c.ErrorCode,diagnostics=c.BindingDiagnostics.Select(Scrub)}),Json);
        foreach(var complete in templates.CompletedTemplates) {
            if(complete.SourceLuIds.Contains(description.LogicUnitId))continue;
            var reports=templates.Callees.Where(c=>c.BlockId==complete.BlockId).ToArray();
            if(reports.Length!=1)continue;var report=reports[0];
            if(report.ErrorCode!=null||report.Status is not("Bound" or "PartialBinding")||report.CacheStatus is "PartialNotStored" or "NotStored"||
                report.RejectedCodeDerivedClaimCount>0||report.BindingDiagnostics.Any(d=>Conflict.IsMatch(d)))continue;
            var contracts=templates.Provenance.Where(p=>p.Callee.Equals(report.Callee,StringComparison.OrdinalIgnoreCase)&&p.TemplateFingerprint==complete.Fingerprint).ToArray();
            if(contracts.Length==0||contracts.Any(p=>p.Origin is not("CodeDerivedStableSlice" or "ModelCodeReviewed" or "ModelCodeReviewedUnprovenBinding")||
                BusinessCandidateValidator.Validate(description,p.Candidate).Status!="Validated"))continue;
            var rootIds=description.Evidence.Where(e=>e.Kind=="SourceStructure"&&e.Text.Contains("[source LU:"+description.LogicUnitId+" ",StringComparison.Ordinal)).Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
            if(rootIds.Count==0||contracts.Any(p=>!p.Candidate.EvidenceIds.Any(rootIds.Contains)))continue;
            var evidence=complete.EvidenceIds.ToHashSet(StringComparer.Ordinal);
            // Only exact existing, same-template evidence entries are removed. Caller binding facts stay.
            var entries=description.Evidence.Where(e=>evidence.Contains(e.Id)&&Regex.Match(e.Text,@"\[source LU:(\d+)(?:\s|\])") is {Success:true} m&&
                long.TryParse(m.Groups[1].Value,out var lu)&&complete.SourceLuIds.Contains(lu)).ToArray();
            if(!entries.Any(e=>e.Kind=="SourceStructure"))continue;
            foreach(var entry in entries) {
                var original="["+entry.Id+"] "+entry.Text;
                if(text.Contains(original,StringComparison.Ordinal)){text=text.Replace(original,"",StringComparison.Ordinal);removed.Add(entry.Id);}
            }
            // These limitations concern the reused callee interpretation. Retain them once as boundaries,
            // rather than letting the old OPAQUE rendering note request the omitted callee XML again.
            var limits=description.Limitations.Where(l=>complete.SourceLuIds.Any(lu=>Regex.IsMatch(l,@"\bLU:"+lu+@"\b"))).ToArray();
            foreach(var limit in limits)text=text.Replace("LIMITATION: "+limit,"",StringComparison.Ordinal);
            records.Add(new {
                template=complete.Fingerprint,blockId=complete.BlockId,sourceLuIds=complete.SourceLuIds,
                bindingStatus=report.Status,bindingDiagnostics=report.BindingDiagnostics.Select(Scrub),
                templateInterpretationBoundaries=limits.Select(Scrub),
                contracts=contracts.Select(p=>new {
                    origin=p.Origin,bindingProved=p.Origin!="ModelCodeReviewedUnprovenBinding",
                    rule=p.Candidate with {Explanation=Scrub(p.Candidate.Explanation)},
                    decisionCode=Scrub(p.DecisionCode)
                })
            });
            foreach(var contract in contracts)keys.Add(BusinessCandidateValidator.Key(contract.Candidate));
            reused.Add(complete.BlockId);
        }
        if(reused.Count==0)return new(description,diagnostics,[],removed,keys);
        var kept=description.Evidence.Where(e=>!removed.Contains(e.Id)).ToArray();
        var remaining=description with {ModelText=text,Evidence=kept};
        var context="\nCompleted template contracts (data; reuse their declared semantics):\n"+JsonSerializer.Serialize(records,Json)+
            "\nEvidence retained in the immutable template cache, usable only for these supplied contracts:\n"+string.Join(",",removed.Order())+
            "\nDo not regenerate or reinterpret the completed callee body. Analyze remaining caller code and actual parameter/EN bindings; check all guards, copied-input/output epochs and diagnostics. Unproven binding is NOT code proof. Known conflicts must be rejected. These contracts are not a claim that unrelated/remaining code has been covered.";
        return new(remaining,context+diagnostics,reused,removed,keys);
    }
}
