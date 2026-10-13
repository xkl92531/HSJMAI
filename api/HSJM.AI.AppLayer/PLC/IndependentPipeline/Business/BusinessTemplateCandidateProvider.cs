#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business.Rendering;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed record BusinessTemplateCalleeResult(string Callee,long? BlockId,string Status,string CacheStatus,
    int ReviewedCandidateCount,int BoundTemplateCandidateCount,int BoundCandidateCount,string CoverageStatus,string? ErrorCode=null,int CodeDerivedCandidateCount=0,int RejectedCodeDerivedClaimCount=0)
{
    public IReadOnlyList<string> BindingDiagnostics {get;init;}=[];
}
public sealed record BusinessTemplateBoundCandidate(BusinessCandidate Candidate,string Origin,string DecisionCode,string TemplateFingerprint,string Callee);
public sealed record BusinessTemplateCandidateResult(IReadOnlyList<BusinessCandidate> Candidates,
    IReadOnlyList<BusinessTemplateCalleeResult> Callees,string Status)
{
    // Template substitution never establishes complete LU coverage or activates a constraint.
    public bool RequiresUnitInference=>true;
    public IReadOnlyList<BusinessTemplateBoundCandidate> Provenance {get;init;}=[];
    public IReadOnlyList<BusinessCompletedTemplateContext> CompletedTemplates {get;init;}=[];
}

/// <summary>Run-scoped template reuse for an immutable source snapshot. The supplied rendering context must
/// belong to that same snapshot. Delegates may load/store analyses, but this provider only returns candidates.
/// Individual waiter cancellation does not cancel shared analysis; runCancellation owns that lifetime.</summary>
public sealed class BusinessTemplateCandidateProvider
{
    public string Version=>"business-template-provider-7|"+BusinessMemoryAliases.Version+"|"+modelVersion;
    private readonly PipelineSnapshot source;
    private readonly BusinessDescriptionContext descriptions;
    private readonly string modelVersion;
    private readonly CancellationToken runCancellation;
    private readonly Func<long,string,string,CancellationToken,Task<BusinessTemplateAnalysis?>> load;
    private readonly Func<BusinessDescription,CancellationToken,Task<BusinessTemplateAnalysis>> analyze;
    private readonly Func<long,BusinessTemplateAnalysis,CancellationToken,Task> save;
    private readonly Dictionary<string,long[]> blockIds;
    private readonly Dictionary<long,PipelineUnit[]> units;
    private readonly ConcurrentDictionary<TemplateKey,Lazy<Task<Resolved>>> flights=new();
    private readonly ConcurrentDictionary<TemplateKey,int> attempts=new();
    private readonly ConcurrentDictionary<long,Lazy<BusinessDescription>> templates=new();
    private readonly ConcurrentDictionary<string,Lazy<IReadOnlyList<BusinessCandidate>>> slices=new(StringComparer.Ordinal);
    private sealed record TemplateKey(long BlockId,string Fingerprint,string Version);
    private sealed record Resolved(BusinessTemplateAnalysis Analysis,string CacheStatus,IReadOnlySet<string> CodeDerivedKeys);
    private sealed class Failure(string code):Exception(code) {public string Code {get;}=code;}

    public BusinessTemplateCandidateProvider(PipelineSnapshot source,BusinessDescriptionContext descriptions,string modelVersion,
        Func<long,string,string,CancellationToken,Task<BusinessTemplateAnalysis?>> load,
        Func<BusinessDescription,CancellationToken,Task<BusinessTemplateAnalysis>> analyze,
        Func<long,BusinessTemplateAnalysis,CancellationToken,Task> save,CancellationToken runCancellation=default)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(descriptions);
        ArgumentNullException.ThrowIfNull(load);ArgumentNullException.ThrowIfNull(analyze);ArgumentNullException.ThrowIfNull(save);
        if(string.IsNullOrWhiteSpace(modelVersion))throw new ArgumentException("A fixed initialized model version is required.",nameof(modelVersion));
        if(!ReferenceEquals(source,descriptions.Source)||descriptions.Fingerprint!=source.Fingerprint())
            throw new ArgumentException("Rendering context must describe this exact immutable snapshot.",nameof(descriptions));
        this.source=source;this.descriptions=descriptions;this.modelVersion=modelVersion;
        this.load=load;this.analyze=analyze;this.save=save;this.runCancellation=runCancellation;
        blockIds=source.Units.GroupBy(u=>u.BlockName,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.Select(u=>u.BlockId).Distinct().ToArray(),StringComparer.OrdinalIgnoreCase);
        units=source.Units.GroupBy(u=>u.Id).ToDictionary(g=>g.Key,g=>g.ToArray());
    }

    public async Task<BusinessTemplateCandidateResult> GetCandidatesAsync(PipelineUnit caller,BusinessDescription description,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();runCancellation.ThrowIfCancellationRequested();
        if(!units.TryGetValue(caller.Id,out var matches)||matches.Length!=1||matches[0].BlockId!=caller.BlockId||matches[0].RawContent!=caller.RawContent||
            description.LogicUnitId!=caller.Id||description.BlockId!=caller.BlockId||description.Status!="Rendered"||
            description.Fingerprint!=BusinessDescriptionBuilder.Key(descriptions.Fingerprint,matches[0])||description.SourceHash!=PipelineHash.Of(caller.RawContent))
            return new([],[new(caller.BlockName,caller.BlockId,"Failed","NotLoaded",0,0,0,"NotEstablished","CallerSourceMismatch")],"Failed");
        caller=matches[0];
        var candidates=new List<BusinessCandidate>();var reports=new List<BusinessTemplateCalleeResult>();var provenance=new List<BusinessTemplateBoundCandidate>();var completed=new List<BusinessCompletedTemplateContext>();
        foreach(var callee in caller.Callees.Distinct(StringComparer.OrdinalIgnoreCase)) {
            token.ThrowIfCancellationRequested();runCancellation.ThrowIfCancellationRequested();
            if(!blockIds.TryGetValue(callee,out var ids)||ids.Length!=1) {
                reports.Add(new(callee,null,"Failed","NotLoaded",0,0,0,"NotEstablished",ids==null?"MissingCalleeBlock":"AmbiguousCalleeBlock"));continue;
            }
            var key=new TemplateKey(ids[0],BusinessDescriptionBuilder.BlockKey(descriptions.Fingerprint,ids[0]),modelVersion);
            var flight=flights.GetOrAdd(key,k=>new(()=>ResolveAsync(k),LazyThreadSafetyMode.ExecutionAndPublication));
            Resolved resolved;
            try {resolved=await flight.Value.WaitAsync(token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested||runCancellation.IsCancellationRequested){throw;}
            catch(Exception error) {
                // Exact-value removal cannot evict a newer retry created by a different waiter.
                if(attempts.GetValueOrDefault(key)<2)((ICollection<KeyValuePair<TemplateKey,Lazy<Task<Resolved>>>>)flights).Remove(new(key,flight));
                var fallback=CodeFallback(caller,description,callee,key,error is Failure failure?failure.Code:"TemplateAnalysisFailed",token);
                candidates.AddRange(fallback.Candidates);reports.AddRange(fallback.Callees);provenance.AddRange(fallback.Provenance);continue;
            }
            var partial=resolved.Analysis.Status=="PartiallyAnalyzedNotInstantiated";
            if(partial&&attempts.GetValueOrDefault(key)<2)((ICollection<KeyValuePair<TemplateKey,Lazy<Task<Resolved>>>>)flights).Remove(new(key,flight));
            var rejectedCode=resolved.Analysis.Decisions.Count(d=>d.Status=="Validated"&&CodeDerived(d)&&!resolved.CodeDerivedKeys.Contains(BusinessCandidateValidator.Key(d.Candidate)));
            var eligible=resolved.Analysis.Decisions.Where(Eligible)
                .Where(d=>!CodeDerived(d)||resolved.CodeDerivedKeys.Contains(BusinessCandidateValidator.Key(d.Candidate))).OrderByDescending(CodeDerived)
                .DistinctBy(d=>BusinessCandidateValidator.Key(d.Candidate)).ToArray();
            if(eligible.Length==0) {
                reports.Add(new(callee,ids[0],"NoReviewedCandidates",resolved.CacheStatus,0,0,0,"NotEstablished",
                    rejectedCode>0?"CodeDerivedTemplateProofMismatch":partial?"TemplateAnalysisIncomplete":null,RejectedCodeDerivedClaimCount:rejectedCode));continue;
            }
            if(!partial)completed.Add(new(ids[0],resolved.Analysis.Template.Fingerprint,
                resolved.Analysis.Template.Evidence.Select(e=>e.Id).Distinct().ToArray(),source.Units.Where(u=>u.BlockId==ids[0]).Select(u=>u.Id).ToArray()));
            int boundTemplates=0;var bound=new List<BusinessCandidate>();var bindingDiagnostics=new List<string>();
            foreach(var decision in eligible) {
                token.ThrowIfCancellationRequested();runCancellation.ThrowIfCancellationRequested();
                var codeDerived=CodeDerived(decision);
                var binding=BusinessTemplateBinder.BindWithDiagnostics(source,caller,description,resolved.Analysis.Template,[decision.Candidate],codeDerived);
                var instantiated=codeDerived&&!binding.StableWriterInventoryComplete?[]:binding.Candidates;bindingDiagnostics.AddRange(binding.Diagnostics);
                if(instantiated.Count>0)boundTemplates++;
                bound.AddRange(instantiated);
                var origin=codeDerived?"CodeDerivedStableSlice":binding.StableWriterInventoryComplete?"ModelCodeReviewed":"ModelCodeReviewedUnprovenBinding";
                provenance.AddRange(instantiated.Select(candidate=>new BusinessTemplateBoundCandidate(candidate,origin,decision.Code,resolved.Analysis.Template.Fingerprint,callee)));
            }
            var unique=bound.DistinctBy(BusinessCandidateValidator.Key).ToArray();candidates.AddRange(unique);
            // This count concerns reviewed template candidates only, never all call sites or the entire LU.
            var coverage=boundTemplates==eligible.Length?"EligibleTemplateCandidatesBound":boundTemplates>0?"PartialTemplateCandidateBinding":"NoBinding";
            reports.Add(new(callee,ids[0],boundTemplates==eligible.Length?"Bound":boundTemplates>0?"PartialBinding":"BindingProducedNoCandidates",
                resolved.CacheStatus,eligible.Count(d=>!CodeDerived(d)),boundTemplates,unique.Length,coverage,rejectedCode>0?"CodeDerivedTemplateProofMismatch":partial?"TemplateAnalysisIncomplete":null,
                CodeDerivedCandidateCount:eligible.Count(CodeDerived),RejectedCodeDerivedClaimCount:rejectedCode){BindingDiagnostics=bindingDiagnostics.Distinct().ToArray()});
        }
        var result=candidates.DistinctBy(BusinessCandidateValidator.Key).ToArray();
        var status=reports.Any(r=>r.ErrorCode!=null)?result.Length>0?"PartialFailure":"Failed":reports.Count==0?"NoCallees":result.Length>0?"HasCandidates":"NoCandidates";
        return new(result,reports,status){Provenance=provenance.DistinctBy(p=>(BusinessCandidateValidator.Key(p.Candidate),p.TemplateFingerprint,p.Origin,p.DecisionCode)).ToArray(),CompletedTemplates=completed};
    }
    private static bool Reviewed(string code)=>code=="TemplateModelCodeReviewAccepted"||code.StartsWith("TemplateModelCodeReviewAccepted:",StringComparison.Ordinal)||
        code=="TemplateModelCodeReviewCorrected"||code.StartsWith("TemplateModelCodeReviewCorrected:",StringComparison.Ordinal);
    private static bool CodeDerived(BusinessDecision decision)=>decision.Code=="TemplateCodeDerivedStableSlice"&&decision.Candidate.Applicability=="StableDiagnosticSnapshot";
    private static bool Eligible(BusinessDecision decision)=>decision.Status=="Validated"&&(Reviewed(decision.Code)||CodeDerived(decision));
    private static bool Readable(BusinessDecision decision)=>decision!=null&&decision.Code!=null&&decision.Candidate!=null&&
        (!Eligible(decision)||decision.Candidate.Signals is {Count:>0 and <=64} signals&&signals.All(s=>s!=null&&!string.IsNullOrWhiteSpace(s.Name)&&!string.IsNullOrWhiteSpace(s.Role))&&
        decision.Candidate.Conditions!=null&&decision.Candidate.Conditions.All(c=>c!=null&&!string.IsNullOrWhiteSpace(c.Signal))&&
        decision.Candidate.EvidenceIds is {Count:>0} evidence&&evidence.All(e=>!string.IsNullOrWhiteSpace(e))&&!string.IsNullOrWhiteSpace(decision.Candidate.Operator));
    private bool Matches(BusinessTemplateAnalysis analysis,BusinessDescription expected,bool allowPartial=false)=>analysis!=null&&analysis.Template!=null&&
        analysis.Decisions is {Count:<=512}&&analysis.Decisions.All(Readable)&&!string.IsNullOrWhiteSpace(analysis.Summary)&&analysis.ModelVersion==modelVersion&&
        (analysis.Status=="AnalyzedNotInstantiated"||allowPartial&&analysis.Status=="PartiallyAnalyzedNotInstantiated")&&analysis.Template.Status=="TemplateRendered"&&analysis.Template.BlockId==expected.BlockId&&
        analysis.Template.LogicUnitId==expected.LogicUnitId&&analysis.Template.BlockName==expected.BlockName&&
        analysis.Template.Fingerprint==expected.Fingerprint&&analysis.Template.SourceHash==expected.SourceHash;
    private BusinessDescription Template(TemplateKey key)
    {
        var template=templates.GetOrAdd(key.BlockId,id=>new(()=>{lock(descriptions)return descriptions.BuildBlock(id);},LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if(template.Status!="TemplateRendered"||template.Fingerprint!=key.Fingerprint)throw new Failure("TemplateDescriptionUnavailable");
        return template;
    }
    private IReadOnlyList<BusinessCandidate> CodeSlices(BusinessDescription template)=>slices.GetOrAdd(template.Fingerprint,_=>new(
        ()=>BusinessTemplateInference.StableSliceCandidates(template),LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    private BusinessTemplateCandidateResult CodeFallback(PipelineUnit caller,BusinessDescription description,string callee,TemplateKey key,string error,CancellationToken token)
    {
        try {
            token.ThrowIfCancellationRequested();runCancellation.ThrowIfCancellationRequested();
            var template=Template(key);var code=CodeSlices(template).Where(c=>c.Applicability=="StableDiagnosticSnapshot").ToArray();
            var bound=new List<BusinessCandidate>();var bindingDiagnostics=new List<string>();int boundTemplates=0;
            foreach(var candidate in code) {
                token.ThrowIfCancellationRequested();runCancellation.ThrowIfCancellationRequested();
                var binding=BusinessTemplateBinder.BindWithDiagnostics(source,caller,description,template,[candidate],true);
                var instantiated=binding.StableWriterInventoryComplete?binding.Candidates:[];bindingDiagnostics.AddRange(binding.Diagnostics);
                if(instantiated.Count>0)boundTemplates++;bound.AddRange(instantiated);
            }
            var unique=bound.DistinctBy(BusinessCandidateValidator.Key).ToArray();
            return new(unique,[new(callee,key.BlockId,unique.Length>0?"FallbackBound":"Failed","NotStored",0,boundTemplates,unique.Length,
                boundTemplates==code.Length&&code.Length>0?"FallbackTemplateCandidatesBound":boundTemplates>0?"PartialFallbackBinding":"NotEstablished",error,CodeDerivedCandidateCount:code.Length){BindingDiagnostics=bindingDiagnostics.Distinct().ToArray()}],
                unique.Length>0?"PartialFailure":"Failed") {
                Provenance=unique.Select(c=>new BusinessTemplateBoundCandidate(c,"FallbackCodeDerivedStableSlice","TemplateCodeDerivedStableSlice",template.Fingerprint,callee)).ToArray()
            };
        }catch(OperationCanceledException) when(token.IsCancellationRequested||runCancellation.IsCancellationRequested){throw;}
        catch(Exception){return new([],[new(callee,key.BlockId,"Failed","NotStored",0,0,0,"FallbackUnavailable",error)],"Failed");}
    }
    private async Task<Resolved> ResolveAsync(TemplateKey key)
    {
        runCancellation.ThrowIfCancellationRequested();
        // At most two attempts per template/run; all remaining callers share typed failure/partial
        // evidence. A fresh run can retry. Never repeat an outage once for every plant instance.
        attempts.AddOrUpdate(key,1,(_,count)=>count+1);
        var template=Template(key);
        BusinessTemplateAnalysis? cached;
        try {cached=await load(source.ProjectId,key.Fingerprint,modelVersion,runCancellation);}
        catch(OperationCanceledException) when(runCancellation.IsCancellationRequested){throw;}
        catch(Exception){throw new Failure("TemplateLoadFailed");}
        // The block fingerprint derives from the verified snapshot fingerprint, which includes ProjectId.
        // Both the store lookup key and payload identity are checked before any cached decisions are used.
        Resolved Resolve(BusinessTemplateAnalysis analysis,string cacheStatus)=>new(analysis with{Template=template},cacheStatus,
            analysis.Decisions.Any(d=>d.Status=="Validated"&&CodeDerived(d))
                ?CodeSlices(template).Select(BusinessCandidateValidator.Key).ToHashSet(StringComparer.Ordinal):new HashSet<string>(StringComparer.Ordinal));
        if(cached!=null&&Matches(cached,template))return Resolve(cached,"Hit");
        BusinessTemplateAnalysis analyzed;
        try {analyzed=await analyze(template,runCancellation);}
        catch(OperationCanceledException) when(runCancellation.IsCancellationRequested){throw;}
        catch(Exception){throw new Failure("TemplateAnalyzeFailed");}
        if(!Matches(analyzed,template,true))throw new Failure("TemplateAnalysisIdentityMismatch");
        analyzed=analyzed with{Template=template};
        if(analyzed.Status=="PartiallyAnalyzedNotInstantiated")return Resolve(analyzed,"PartialNotStored");
        try {await save(source.ProjectId,analyzed,runCancellation);}
        catch(OperationCanceledException) when(runCancellation.IsCancellationRequested){throw;}
        catch(Exception){throw new Failure("TemplateSaveFailed");}
        return Resolve(analyzed,cached==null?"Miss":"Rejected");
    }
}
