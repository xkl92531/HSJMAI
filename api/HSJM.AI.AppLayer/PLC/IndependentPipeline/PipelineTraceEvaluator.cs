#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

/// <summary>Explicit instruction-execution trace, not inference of unobserved PLC states between sparse samples.</summary>
public static class PipelineTraceEvaluator
{
    public static IReadOnlyList<PipelineTraceResult> Evaluate(PipelineDescription description,
        IReadOnlyList<PipelineTraceFrame> frames, IReadOnlyDictionary<string,bool>? initialState = null,
        IReadOnlyDictionary<string,PipelineTimerState>? initialTimers = null, CancellationToken token = default)
    {
        if (description.IsTemplate || description.Limitations.Count > 0) throw new PipelineException("TraceRequiresBoundProgram");
        if (frames is null || frames.Count == 0) throw new PipelineException("TraceFramesRequired");
        if (frames.Count > 5000) throw new PipelineException("TraceFrameLimit");
        var state = (initialState ?? new Dictionary<string,bool>()).ToDictionary(p => p.Key,p => p.Value,StringComparer.OrdinalIgnoreCase);
        var timers = (initialTimers ?? new Dictionary<string,PipelineTimerState>()).ToDictionary(p => p.Key,p => p.Value,StringComparer.OrdinalIgnoreCase);
        if (timers.Values.Any(t => t is null || t.StartedAtMs < 0)) throw new PipelineException("InvalidTimerInitialState");
        var result = new List<PipelineTraceResult>(); long previousTime = -1;
        foreach (var frame in frames) {
            token.ThrowIfCancellationRequested();
            if (frame?.Inputs is null) throw new PipelineException("TraceInputsRequired");
            if (frame.TimestampMs < 0 || frame.TimestampMs < previousTime) throw new PipelineException("NonMonotonicTraceTime");
            previousTime = frame.TimestampMs;
            var values = new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in frame.Inputs) {
                if (pair.Key.Contains('\u001f')) throw new PipelineException("ReservedTraceSignal");
                if (pair.Value.HasValue && !values.TryAdd(pair.Key,pair.Value.Value)) throw new PipelineException("AmbiguousTraceSignal");
            }
            foreach (var pair in state) values[PipelineTerms.PreviousKey(pair.Key)] = pair.Value;
            var numeric=(frame.Scalars ?? new Dictionary<string,double?>()).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
            foreach(var predicate in description.Assignments.SelectMany(a=>PipelineTerms.Atoms(a.Expression))
                .Concat((description.StateUpdates ?? []).SelectMany(a=>PipelineTerms.Atoms(a.Expression)))
                .Concat((description.Timers ?? []).SelectMany(t=>PipelineTerms.Atoms(t.Input))).Where(t=>t.Kind=="Compare"))
                values[PipelineTerms.AtomKey(predicate)]=PipelineNumeric.Compare(predicate,numeric);
            var nextTimers = new Dictionary<string,PipelineTimerState>(timers,StringComparer.OrdinalIgnoreCase);
            try {
                foreach (var timer in description.Timers ?? []) {
                    var input = PipelineRuleVerifier.Evaluate(timer.Input,values);
                    if (!input) nextTimers[timer.Key] = new(null);
                    else {
                        if (!timers.TryGetValue(timer.Key,out var prior)) throw new PipelineException("MissingTimerInitialState");
                        var started = prior.StartedAtMs ?? frame.TimestampMs;
                        if (started < 0 || started > frame.TimestampMs) throw new PipelineException("InvalidTimerInitialTime");
                        nextTimers[timer.Key] = new(started);
                    }
                    values[PipelineTerms.TimerKey(timer.Key)] = input && frame.TimestampMs - nextTimers[timer.Key].StartedAtMs!.Value >= timer.DelayMs;
                }
                var outputs = new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var assignment in description.Assignments)
                    outputs[assignment.Target] = PipelineRuleVerifier.Evaluate(assignment.Expression,values);
                var instanceState = new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var update in description.StateUpdates ?? [])
                    instanceState[update.Target] = PipelineRuleVerifier.Evaluate(update.Expression,values);
                foreach (var output in outputs) state[output.Key] = output.Value;
                foreach (var update in instanceState) state[update.Key] = update.Value;
                timers = nextTimers;
                result.Add(new(frame.TimestampMs,outputs,new Dictionary<string,PipelineTimerState>(timers,StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string,bool>(state,StringComparer.OrdinalIgnoreCase)));
            }
            catch (KeyNotFoundException) { throw new PipelineException("MissingSignalOrPreviousState"); }
        }
        return result;
    }
}
