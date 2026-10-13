#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

public sealed class IndependentPipelineRunner(IPipelineStore store, IPipelineInference inference)
{
    public async Task<PipelineRun> RunAsync(Guid runId, long projectId, bool regenerateConstraints,
        CancellationToken token = default)
    {
        var wall = Stopwatch.StartNew(); var times = new Dictionary<string, long>();
        var items = new List<PipelineItem>(); var sourceKey = "";
        PipelineRun Result(string status, string? error = null) {
            times["Total"] = wall.ElapsedMilliseconds;
            return new(runId, projectId, status, sourceKey, items.ToArray(), new Dictionary<string,long>(times), error);
        }
        try {
            if (projectId <= 0) throw new PipelineException("ProjectIdRequired");
            var stage = Stopwatch.StartNew();
            var snapshot = await store.LoadSourceAsync(projectId, token);
            if (snapshot.ProjectId != projectId) throw new PipelineException("SourceProjectMismatch");
            sourceKey = snapshot.Fingerprint();
            var compilation = new PipelineCompilationContext(snapshot);
            var cache = await store.LoadDescriptionsAsync(projectId, token);
            times["Load"] = stage.ElapsedMilliseconds; stage.Restart();
            var ready = new Dictionary<long, PipelineDescription>(); var fresh = new List<PipelineDescription>();
            var failedBlocks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unit in snapshot.Units) {
                token.ThrowIfCancellationRequested();
                try {
                    var key = PipelineEvidenceBuilder.Key(sourceKey, unit);
                    if (cache.TryGetValue(key, out var cached) && cached.LogicUnitId == unit.Id && cached.Fingerprint == key
                        && !string.IsNullOrWhiteSpace(cached.EvidenceJson) && !string.IsNullOrWhiteSpace(cached.Markdown))
                        ready[unit.Id] = cached;
                    else {
                        var description = PipelineEvidenceBuilder.Build(snapshot, unit, sourceKey,compilation);
                        ready[unit.Id] = description; fresh.Add(description);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) {
                    failedBlocks.Add(unit.BlockName); items.Add(new(unit.Id, "Failed", ErrorCode: Code(ex)));
                }
            }
            // Propagate dependency failures to callers, including indirect callers.
            bool changed;
            do {
                changed = false;
                foreach (var unit in snapshot.Units.Where(u => ready.ContainsKey(u.Id)))
                    if (unit.Callees.Any(failedBlocks.Contains)) {
                        ready.Remove(unit.Id); failedBlocks.Add(unit.BlockName); changed = true;
                        items.Add(new(unit.Id, "Failed", ErrorCode: "DependencyFailed"));
                    }
            } while (changed);
            fresh.RemoveAll(d => !ready.ContainsKey(d.LogicUnitId));
            times["BuildAndValidateDescription"] = stage.ElapsedMilliseconds; stage.Restart();
            if (fresh.Count > 0) await PipelineRecovery.WriteAsync(() => store.SaveDescriptionsAsync(projectId, fresh, token),token);
            times["SaveDescriptions"] = stage.ElapsedMilliseconds;
            if (ready.Values.Any(d => !d.IsTemplate)) await inference.InitializeAsync(token);
            var batchStore=store as IPipelineBatchStore;
            var completions=batchStore is null || regenerateConstraints ? null : await batchStore.LoadCompletionsAsync(projectId,token);
            var pending=new List<PipelineCommit>();
            async Task FlushAsync() {
                if(pending.Count==0)return;
                var commitWatch=Stopwatch.StartNew();
                try {
                    await PipelineRecovery.WriteAsync(()=>batchStore!.CommitBatchAsync(projectId,runId,pending,token),token);
                    foreach(var item in pending) {
                        var index=items.FindIndex(i=>i.LogicUnitId==item.Description.LogicUnitId&&i.Status=="Prepared");
                        items[index]=new(item.Description.LogicUnitId,item.Rules.Count==0?"CompletedEmpty":"Completed",item.Rules.Count);
                    }
                }
                catch(Exception ex) when(ex is not OperationCanceledException || !token.IsCancellationRequested) {
                    foreach(var item in pending) {
                        var index=items.FindIndex(i=>i.LogicUnitId==item.Description.LogicUnitId&&i.Status=="Prepared");
                        items[index]=new(item.Description.LogicUnitId,"Failed",ErrorCode:Code(ex));
                    }
                }
                finally {pending.Clear();times["CommitBatches"]=times.GetValueOrDefault("CommitBatches")+commitWatch.ElapsedMilliseconds;}
            }
            // All description writes precede the first inference. Old descriptions/rules are never cleared.
            foreach (var unit in snapshot.Units.Where(u => ready.ContainsKey(u.Id))) {
                token.ThrowIfCancellationRequested(); stage.Restart();
                var description = ready[unit.Id];
                if (description.IsTemplate) { items.Add(new(unit.Id,"TemplateReady")); continue; }
                var generationKey = PipelineRuleVerifier.GenerationKey(description.Fingerprint,inference.Version);
                try {
                    if (!regenerateConstraints && (completions is not null ? completions.GetValueOrDefault(unit.Id)==generationKey
                        : await store.IsCompleteAsync(projectId, unit.Id, generationKey, token))) {
                        items.Add(new(unit.Id, "Cached")); continue;
                    }
                    var proposals = await inference.InferAsync(description, token);
                    var accepted = new List<PipelineRule>();
                    foreach (var rule in proposals) {
                        var validation = PipelineRuleVerifier.Validate(rule, description);
                        if (!validation.Success) throw new PipelineException(validation.ErrorCode!);
                        accepted.Add(rule with { Description = PipelineRuleVerifier.Describe(rule) });
                    }
                    if (description.Limitations.Count > 0)
                        throw new PipelineException("UnresolvedSemantics");
                    var unique=accepted.DistinctBy(r=>r.Key).ToArray();
                    if(batchStore is null) {
                        await PipelineRecovery.WriteAsync(() => store.CommitAsync(projectId,runId,description,generationKey,unique,token),token);
                        items.Add(new(unit.Id,unique.Length==0?"CompletedEmpty":"Completed",unique.Length));
                    }
                    else {
                        pending.Add(new(description,generationKey,unique));items.Add(new(unit.Id,"Prepared"));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) {
                    if (ex is PipelineException { Code: "ModelChangedDuringRun" }) throw;
                    items.Add(new(unit.Id, "Failed", ErrorCode: Code(ex)));
                }
                finally { times["InferValidateCommit"] = times.GetValueOrDefault("InferValidateCommit") + stage.ElapsedMilliseconds; }
                if(pending.Count>=16)await FlushAsync();
                await store.SaveRunAsync(Result("Running"), token);
            }
            await FlushAsync();
            var result = Result(items.Any(i => i.Status == "Failed") ? "PartialFailure" : "Completed");
            await store.SaveRunAsync(result, token); return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {
            for(int i=0;i<items.Count;i++)if(items[i].Status=="Prepared")items[i]=items[i] with {Status="Cancelled",ErrorCode="CancelledBeforeCommit"};
            var cancelled = Result("Cancelled", "Cancelled");
            await store.SaveRunAsync(cancelled, CancellationToken.None); throw;
        }
        catch (Exception ex) {
            for(int i=0;i<items.Count;i++)if(items[i].Status=="Prepared")items[i]=items[i] with {Status="Failed",ErrorCode="AbortedBeforeCommit"};
            var failed = Result("Failed", Code(ex));
            await store.SaveRunAsync(failed, token); return failed;
        }
    }
    private static string Code(Exception ex) => ex is PipelineException p ? p.Code : ex is OperationCanceledException ? "ModelTimeout" : "StageFailed";
}
