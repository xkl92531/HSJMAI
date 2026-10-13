#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using HSJM.AI.Core;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Jobs;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

public sealed class IndependentPipelineService(PostgresPipelineStore store, PipelineModelInference inference) : ISelfScopedAutoInject
{
    public async Task<PipelineRun> ExecuteAsync(Guid runId, long projectId, bool regenerateConstraints, CancellationToken token) {
        await using var execution = await IndependentProjectExecutionLock.AcquireAsync(store, projectId, token);
        return await new IndependentPipelineRunner(store, inference).RunAsync(runId, projectId, regenerateConstraints, execution.Token);
    }

    public async Task CreateRunAsync(Guid runId, long projectId, CancellationToken token) {
        _ = store.ConnectionString; _ = inference.Version;
        if (projectId <= 0) throw new PipelineException("ProjectIdRequired");
        await store.SaveRunAsync(new(runId, projectId, "Running", "", [], new Dictionary<string,long>()), token);
    }

    public async Task<object> ReplayAsync(long projectId,PipelineTraceRequest request,CancellationToken token) {
        var source=await store.LoadSourceAsync(projectId,token);
        var unit=source.Units.SingleOrDefault(u => u.Id==request.LogicUnitId) ?? throw new PipelineException("LogicUnitNotFound");
        var description=PipelineEvidenceBuilder.Build(source,unit);
        return new { source.ProjectId, request.LogicUnitId, description.Fingerprint,
            EvaluationPoint="AfterLogicUnit", Mode="ExplicitExecutionTrace",
            Frames=PipelineTraceEvaluator.Evaluate(description,request.Frames,request.InitialState,request.InitialTimers,token) };
    }

    public async Task<IReadOnlyList<object>> ReadDescriptionsAsync(long projectId,CancellationToken token) {
        var source=await store.LoadSourceAsync(projectId,token);
        var cache=await store.LoadDescriptionsAsync(projectId,token);
        var fingerprint=source.Fingerprint();
        return source.Units.Select(u => {
            var key=PipelineEvidenceBuilder.Key(fingerprint,u);
            var current=cache.TryGetValue(key,out var description);
            return (object)new { LogicUnitId=u.Id,u.BlockName,Status=current ? (description!.IsTemplate ? "TemplateReady" : "Ready") : "NotGenerated",
                Fingerprint=key, Description=current ? description : null };
        }).ToArray();
    }
}
