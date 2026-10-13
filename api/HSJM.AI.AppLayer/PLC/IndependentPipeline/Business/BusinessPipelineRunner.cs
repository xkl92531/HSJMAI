#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Business.Rendering;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed class BusinessPipelineRunner(IPipelineStore sourceStore,IBusinessPipelineStore store,IBusinessInference inference,
    int concurrency=2,Func<PipelineSnapshot,PipelineUnit,BusinessDescription>? buildOverride=null,
    Func<PipelineSnapshot,CancellationToken,Task<BusinessTemplateCandidateProvider>>? templateFactory=null,
    IBusinessAcceptancePolicy? acceptancePolicy=null)
{
    // Includes code extraction, validation and binding policy, not just the LLM configuration.
    public const string Version="business-pipeline-6|"+BusinessDeterministicAnalysis.Version+"|"+BusinessAcceptancePolicy.Version;
    readonly IBusinessAcceptancePolicy acceptance=acceptancePolicy??new BusinessAcceptancePolicy();
    public string? EffectiveResultVersion {get;private set;}
    public static string ResultVersion(string inferenceVersion,string? templateVersion=null)=>
        Version+"|"+BusinessMemoryAliases.Version+"|"+inferenceVersion+(templateVersion is null?"":"|"+templateVersion);
    public async Task<PipelineRun> RunAsync(Guid runId,long projectId,bool regenerate,CancellationToken token=default) {
        var wall=Stopwatch.StartNew();var times=new Dictionary<string,long>();var items=new ConcurrentDictionary<long,PipelineItem>();
        string fingerprint="";long inferenceMs=0,reviewMs=0,commitMs=0;
        PipelineRun Snapshot(string status,string? error=null) {
            times["InferenceWork"]=Interlocked.Read(ref inferenceMs);times["ReviewWork"]=Interlocked.Read(ref reviewMs);
            times["CommitBatches"]=Interlocked.Read(ref commitMs);times["Total"]=wall.ElapsedMilliseconds;
            return new(runId,projectId,status,fingerprint,items.Values.OrderBy(i=>i.LogicUnitId).ToArray(),new Dictionary<string,long>(times),error);
        }
        try {
            var clock=Stopwatch.StartNew();var source=await sourceStore.LoadSourceAsync(projectId,token);
            if(source.ProjectId!=projectId)throw new PipelineException("SourceProjectMismatch");
            fingerprint=source.Fingerprint();await store.InitializeAsync(token);
            var cache=await store.LoadDescriptionsAsync(projectId,token);times["Load"]=clock.ElapsedMilliseconds;
            var context=buildOverride is null?new BusinessDescriptionContext(source,fingerprint):null;
            var ready=new List<BusinessDescription>();var fresh=new List<BusinessDescription>();clock.Restart();
            foreach(var unit in source.Units) {
                token.ThrowIfCancellationRequested();
                try {
                    var key=BusinessDescriptionBuilder.Key(fingerprint,unit);
                    BusinessDescription description;
                    if(cache.TryGetValue(key,out var cached)&&(cached.Status=="EmptySource"||cached.Status=="Rendered"&&!string.IsNullOrWhiteSpace(cached.ModelText)))description=cached;
                    else {description=buildOverride is null?context!.Build(unit):buildOverride(source,unit);fresh.Add(description);}
                    ValidateIdentity(description,unit,key);
                    if(description.Status=="EmptySource")items[unit.Id]=new(unit.Id,"EmptySource");
                    else if(description.Status!="Rendered")items[unit.Id]=new(unit.Id,"Failed",ErrorCode:description.Status);
                    else if(string.IsNullOrWhiteSpace(description.ModelText))items[unit.Id]=new(unit.Id,"Failed",ErrorCode:"EmptyRenderedDescription");
                    else ready.Add(description);
                }
                catch(Exception ex)when(ex is not OperationCanceledException||!token.IsCancellationRequested) {items[unit.Id]=new(unit.Id,"Failed",ErrorCode:Code(ex));}
            }
            times["BuildDescriptions"]=clock.ElapsedMilliseconds;clock.Restart();
            var batch=new List<BusinessDescription>();long chars=0;
            foreach(var description in fresh) {
                batch.Add(description);chars+=description.Markdown.Length+description.ModelText.Length;
                if(batch.Count>=64||chars>=2_000_000) {
                    await PipelineRecovery.WriteAsync(()=>store.SaveDescriptionsAsync(projectId,batch,token),token);batch.Clear();chars=0;
                }
            }
            if(batch.Count>0)await PipelineRecovery.WriteAsync(()=>store.SaveDescriptionsAsync(projectId,batch,token),token);
            times["SaveDescriptions"]=clock.ElapsedMilliseconds;
            if(ready.Count==0) {
                var empty=Snapshot(items.Values.Any(i=>i.Status=="Failed")?"PartialFailure":"Completed");
                await sourceStore.SaveRunAsync(empty,token);return empty;
            }
            clock.Restart();
            var unitsById=source.Units.ToDictionary(u=>u.Id);
            var plans=ready.ToDictionary(d=>d.LogicUnitId,d=>BusinessDeterministicAnalysis.Analyze(source,unitsById[d.LogicUnitId],d));
            times["DeterministicAnalysis"]=clock.ElapsedMilliseconds;
            var needsModel=plans.Values.Any(p=>p.RequiresAi);
            string? initializationFailure=null;BusinessTemplateCandidateProvider? templates=null;
            try {
                if(needsModel)await inference.InitializeAsync(token);
                if(needsModel&&templateFactory is not null)templates=await templateFactory(source,token);
            }
            catch(Exception ex)when(ex is not OperationCanceledException||!token.IsCancellationRequested) {
                initializationFailure="ModelInitializationFailed:"+Code(ex);
            }
            var modelVersion=EffectiveResultVersion=ResultVersion(needsModel?inference.Version:"CodeOnly",templates?.Version);
            var prior=(await store.LoadResultsAsync(projectId,fingerprint,modelVersion,token)).ToDictionary(r=>r.LogicUnitId);
            // Read persisted descriptions back. No model sees an uncommitted in-memory render.
            // Cache hits already came from committed storage; only a write requires another read.
            var persisted=fresh.Count==0?cache:await store.LoadDescriptionsAsync(projectId,token);
            var freshKeys=fresh.Select(d=>d.Fingerprint).ToHashSet(StringComparer.Ordinal);
            var registeredSignals=source.Tags.Select(t=>t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending=new List<BusinessUnitResult>();long pendingCharacters=0;using var writer=new SemaphoreSlim(1,1);
            var progressWatch=Stopwatch.StartNew();
            async Task FlushAsync() {
                if(pending.Count==0)return;var watch=Stopwatch.StartNew();
                try {
                    await PipelineRecovery.WriteAsync(()=>store.SaveResultsAsync(projectId,runId,fingerprint,pending,token),token);
                    foreach(var result in pending)items[result.LogicUnitId]=new(result.LogicUnitId,result.Status,result.Decisions.Count(d=>d.Status=="Accepted"),result.ErrorCode);
                }
                catch(Exception ex)when(ex is not OperationCanceledException||!token.IsCancellationRequested) {
                    foreach(var result in pending)items[result.LogicUnitId]=new(result.LogicUnitId,"Failed",ErrorCode:Code(ex));
                    throw new PipelineException("BusinessCommitFailed:"+Code(ex));
                }
                finally {pending.Clear();pendingCharacters=0;Interlocked.Add(ref commitMs,watch.ElapsedMilliseconds);}
            }
            await sourceStore.SaveRunAsync(Snapshot("Running"),token);
            await Parallel.ForEachAsync(ready,new ParallelOptions {MaxDegreeOfParallelism=Math.Clamp(concurrency,1,3),CancellationToken=token},async (description,ct)=>{
                try {
                    if(!persisted.TryGetValue(description.Fingerprint,out var input))throw new PipelineException("DescriptionNotPersisted");
                    ValidateIdentity(input,unitsById[description.LogicUnitId],description.Fingerprint);
                    if(input.Status!="Rendered"||string.IsNullOrWhiteSpace(input.ModelText))throw new PipelineException("PersistedDescriptionNotReady");
                    if(freshKeys.Contains(input.Fingerprint)&&BusinessPayloadCodec.Hash(input)!=BusinessPayloadCodec.Hash(description))
                        throw new PipelineException("PersistedDescriptionContentMismatch");
                    if(!regenerate&&prior.TryGetValue(input.LogicUnitId,out var old)&&old.DescriptionFingerprint==input.Fingerprint&&
                        old.ModelVersion==modelVersion&&old.Status is "Completed" or "CompletedEmpty") {
                        items[input.LogicUnitId]=new(input.LogicUnitId,"Cached",old.Decisions.Count(d=>d.Status=="Accepted"));return;
                    }
                    var plan=plans[input.LogicUnitId];
                    var codeVerified=plan.Rules.Concat(plan.Coverage.Status=="CodeComplete"?Array.Empty<BusinessCandidate>():BusinessCallConstraints.Extract(source,unitsById[description.LogicUnitId],input))
                        .Where(c=>BusinessCandidateValidator.Validate(input,c).Status=="Validated").ToArray();
                    var verifiedKeys=codeVerified.Select(BusinessCandidateValidator.Key).ToHashSet(StringComparer.Ordinal);
                    IReadOnlyList<BusinessCandidate> candidates=plan.Coverage.Status=="CodeComplete"?codeVerified:BusinessWireConstraints.Extract(unitsById[description.LogicUnitId],input).Concat(codeVerified).ToArray();
                    string? inferenceFailure=plan.MissingSource?"MissingRequiredSource":plan.RequiresAi?initializationFailure:null;
                    BusinessTemplateCandidateResult? supplemental=null;
                    // Match the original I/O-oriented generation scope, while allowing actual internal
                    // guards alongside registered points. Unrelated data-processing units stay cached.
                    var hasObservablePoint=input.SignalAliases.Values.Any(registeredSignals.Contains);
                    if(hasObservablePoint&&plan.RequiresAi&&initializationFailure is null) {
                        var watch=Stopwatch.StartNew();
                        if(templates!=null) {
                            try {
                                supplemental=await templates.GetCandidatesAsync(unitsById[input.LogicUnitId],input,ct);
                                candidates=candidates.Concat(supplemental.Candidates).ToArray();
                                if(supplemental.Status is "Failed" or "PartialFailure")inferenceFailure="TemplateAnalysisIncomplete";
                            }
                            catch(Exception ex)when(ex is not OperationCanceledException||!ct.IsCancellationRequested){inferenceFailure="TemplateFailed:"+Code(ex);}
                        }
                        var residual=BusinessDeterministicAnalysis.ResidualInput(input,plan,codeVerified);
                        try {candidates=candidates.Concat(supplemental!=null&&inference is IBusinessTemplateContextInference cached
                            ?await cached.InferAsync(residual,supplemental,ct):await inference.InferAsync(residual,ct)).ToArray();}
                        catch(Exception ex)when(ex is not OperationCanceledException||!ct.IsCancellationRequested) {
                            inferenceFailure=Code(ex);if(candidates.Count==0)throw;
                        }
                        finally {Interlocked.Add(ref inferenceMs,watch.ElapsedMilliseconds);}
                    }
                    var decisions=new List<BusinessDecision>();var reviewCandidates=new List<BusinessCandidate>();
                    var proposalKeys=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var candidate in candidates.Where(c=>!codeVerified.Contains(c))) {
                        var shape=BusinessCandidateValidator.Validate(input,candidate);
                        if(shape.Status!="Validated"){decisions.Add(shape);continue;}
                        if(!proposalKeys.Add(BusinessCandidateValidator.Key(candidate)))continue;
                        var admission=acceptance.Validate(source,unitsById[input.LogicUnitId],input,candidate,supplemental);
                        if(admission.Status=="Validated")reviewCandidates.Add(admission.Candidate);
                        else if(admission.Status=="Accepted")decisions.Add(admission with{Candidate=BusinessCandidateValidator.Bind(input,admission.Candidate)});
                        else decisions.Add(admission);
                    }
                    var valid=reviewCandidates.ToArray();
                    decisions.AddRange(codeVerified.Select(c=>new BusinessDecision(BusinessCandidateValidator.Bind(input,c),"Accepted",
                        plan.Rules.Contains(c)?"CodeProven:"+BusinessDeterministicAnalysis.Version+":"+PipelineRuleVerifier.Version+":AfterLogicUnit":"CodeDerivedVerifiedForDeclaredScope")));
                    valid=valid.Where(c=>!verifiedKeys.Contains(BusinessCandidateValidator.Key(c))).ToArray();
                    if(valid.Length>0) {
                        var watch=Stopwatch.StartNew();
                        try {
                            if(inference is not IBusinessReviewInference reviewer)throw new PipelineException("BusinessReviewRequired");
                            if(initializationFailure is not null)throw new PipelineException(initializationFailure);
                            var reviewed=supplemental!=null&&inference is IBusinessTemplateContextInference cached
                                ?await cached.ReviewAsync(input,valid,supplemental,ct):await reviewer.ReviewAsync(input,valid,ct);
                            if(reviewed.Count!=valid.Length||reviewed.Any(d=>d.Status is not("Accepted" or "Rejected"))||
                                !reviewed.Select(d=>BusinessCandidateValidator.Key(d.Candidate)).SequenceEqual(valid.Select(BusinessCandidateValidator.Key)))throw new PipelineException("IncompleteBusinessReview");
                            foreach(var decision in reviewed) {
                                if(decision.Status=="Accepted") {
                                    var admission=acceptance.Validate(source,unitsById[input.LogicUnitId],input,decision.Candidate,supplemental);
                                    if(admission.Status is not("Validated" or "Accepted")){decisions.Add(admission);continue;}
                                    if(admission.Status=="Accepted"){decisions.Add(admission with{Candidate=BusinessCandidateValidator.Bind(input,admission.Candidate)});continue;}
                                    decisions.Add(decision with{Candidate=BusinessCandidateValidator.Bind(input,admission.Candidate),Code="AiAssistedValidated:"+admission.Code});continue;
                                }
                                decisions.Add(decision with {Candidate=BusinessCandidateValidator.Bind(input,decision.Candidate)});
                            }
                        }
                        catch(Exception ex)when(ex is not OperationCanceledException||!ct.IsCancellationRequested) {
                            inferenceFailure="ReviewFailed:"+Code(ex);
                            decisions.AddRange(valid.Select(c=>new BusinessDecision(BusinessCandidateValidator.Bind(input,c),"Rejected",inferenceFailure)));
                        }
                        finally {Interlocked.Add(ref reviewMs,watch.ElapsedMilliseconds);}
                    }
                    if(decisions.Any(d=>d.Code=="MissingOrAmbiguousBusinessReview"))inferenceFailure??="IncompleteBusinessReview";
                    if(inferenceFailure!=null&&prior.TryGetValue(input.LogicUnitId,out var previous)&&
                        previous.ModelVersion==modelVersion&&previous.DescriptionFingerprint==input.Fingerprint) {
                        // A transport/format failure is not new evidence that an unchanged accepted rule
                        // is wrong. Explicit semantic rejections still supersede old decisions.
                        var acceptedKeys=decisions.Where(d=>d.Status=="Accepted").Select(d=>BusinessCandidateValidator.Key(d.Candidate)).ToHashSet(StringComparer.Ordinal);
                        var rejectedKeys=decisions.Where(d=>d.IsConclusiveRejection)
                            .Select(d=>BusinessCandidateValidator.Key(d.Candidate)).ToHashSet(StringComparer.Ordinal);
                        foreach(var decision in previous.Decisions.Where(d=>d.Status=="Accepted")) {
                            var key=BusinessCandidateValidator.Key(decision.Candidate);
                            if(rejectedKeys.Contains(key))continue;
                            var rebound=BusinessAcceptancePolicy.ToAliases(input,decision.Candidate);
                            if(rebound is null)continue;
                            var admission=acceptance.Validate(source,unitsById[input.LogicUnitId],input,rebound,supplemental);
                            if(admission.Status is not("Validated" or "Accepted"))continue;
                            var restored=decision with{Candidate=BusinessCandidateValidator.Bind(input,admission.Candidate),Code=admission.Status=="Accepted"?admission.Code:decision.Code};
                            if(!acceptedKeys.Add(BusinessCandidateValidator.Key(restored.Candidate)))continue;
                            decisions.Add(restored with {Code=restored.Code.StartsWith("RetainedAfterIncompleteRegeneration:",StringComparison.Ordinal)?restored.Code:"RetainedAfterIncompleteRegeneration:"+restored.Code});
                        }
                    }
                    var accepted=decisions.Count(d=>d.Status=="Accepted");
                    // A completed semantic rejection is an answer, not a failed generation to retry.
                    // Malformed candidates and incomplete reviews remain retryable failures.
                    var status=inferenceFailure!=null||decisions.Any(d=>d.Status=="Rejected"&&!d.IsConclusiveRejection)
                        ?"PartialFailure":accepted>0?"Completed":"CompletedEmpty";
                    var result=new BusinessUnitResult(input.LogicUnitId,input.Fingerprint,modelVersion,decisions,status,inferenceFailure??(hasObservablePoint?null:"NoRegisteredSignals")) {Coverage=plan.Coverage with{ProvenRules=codeVerified.Length}};
                    await writer.WaitAsync(ct);
                    try {
                        pending.Add(result);pendingCharacters+=System.Text.Json.JsonSerializer.Serialize(result).Length;items[input.LogicUnitId]=new(input.LogicUnitId,"Prepared");
                        var flush=pending.Count>=128||pendingCharacters>=2_000_000;
                        if(flush)await FlushAsync();
                        if(flush||progressWatch.ElapsedMilliseconds>=2000) {
                            await sourceStore.SaveRunAsync(Snapshot("Running"),ct);progressWatch.Restart();
                        }
                    }
                    finally {writer.Release();}
                }
                catch(Exception ex)when((ex is not OperationCanceledException||!ct.IsCancellationRequested)&&
                    !(ex is PipelineException fatal&&fatal.Code.StartsWith("BusinessCommitFailed:",StringComparison.Ordinal))) {
                    items[description.LogicUnitId]=new(description.LogicUnitId,"Failed",ErrorCode:Code(ex));
                }
            });
            await FlushAsync();
            var final=Snapshot(items.Values.Any(i=>i.Status is "Failed" or "PartialFailure")?"PartialFailure":"Completed");
            await sourceStore.SaveRunAsync(final,token);return final;
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested) {
            foreach(var item in items.Values.Where(i=>i.Status=="Prepared"))items[item.LogicUnitId]=item with {Status="Cancelled",ErrorCode="CancelledBeforeCommit"};
            await sourceStore.SaveRunAsync(Snapshot("Cancelled","Cancelled"),CancellationToken.None);throw;
        }
        catch(Exception ex) {
            foreach(var item in items.Values.Where(i=>i.Status=="Prepared"))items[item.LogicUnitId]=item with {Status="Failed",ErrorCode="AbortedBeforeCommit"};
            var failed=Snapshot("Failed",Code(ex));await sourceStore.SaveRunAsync(failed,token);return failed;
        }
    }
    static void ValidateIdentity(BusinessDescription description,PipelineUnit unit,string key) {
        if(description.Fingerprint!=key||description.LogicUnitId!=unit.Id||description.BlockId!=unit.BlockId||
            description.SourceHash!=PipelineHash.Of(unit.RawContent))throw new PipelineException("DescriptionIdentityMismatch");
    }
    static string Code(Exception ex)=>ex is PipelineException p?p.Code:ex is OperationCanceledException?"ModelTimeout":
        ex is System.Text.Json.JsonException?"InvalidModelJson":ex is System.Net.Http.HttpRequestException?"ModelTransportFailed":"BusinessStageFailed";
}
