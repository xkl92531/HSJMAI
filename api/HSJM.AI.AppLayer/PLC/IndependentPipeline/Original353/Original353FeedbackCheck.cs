#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Diagnostics;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353FeedbackCheckResult(string Status,string Code,string RuleKey,long LogicUnitId);

public static class Original353FeedbackCheck
{
    public static Original353FeedbackCheckResult Evaluate(PipelineSnapshot source,PipelineUnit unit,
        Original353ConditionalFeedback group,DiagnosticSnapshot snapshot,bool? settled,int maxAgeMs=5000,
        TimeProvider? clock=null,bool trustedSettlingEvidence=false) {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(group);
        Original353FeedbackCheckResult Result(string status,string code)=>new(status,code,group.Rule.Key,unit.Id);
        if(!source.Units.Any(u=>u.Id==unit.Id&&u.RawContent==unit.RawContent)||
            !group.Evidence.Contains("SourceHash:"+PipelineHash.Of(unit.RawContent)))
            return Result("Unknown","SourceVersionMismatch");
        clock??=TimeProvider.System;
        var now=clock.GetUtcNow();
        if(snapshot==null||snapshot.ProjectId!=source.ProjectId)return Result("Unknown","ProjectScopeMismatch");
        if(snapshot.Observations==null)return Result("Unknown","ObservationsRequired");
        if(maxAgeMs is <1 or >60000)return Result("Unknown","InvalidMaxAge");
        if(snapshot.Timestamp>now||now-snapshot.Timestamp>TimeSpan.FromMilliseconds(maxAgeMs))
            return Result("Unknown","SnapshotStaleOrFuture");
        if(settled is null)return Result("Unknown","SettlingEvidenceRequired");
        if(settled==false)return Result("NotApplicable","CommandHasNotSettled");
        if(!trustedSettlingEvidence)return Result("Unknown","TrustedSettlingTraceRequired");
        var names=group.Rule.Signals.Select(s=>s.Name).Concat(group.BypassSignals).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var values=new Dictionary<string,bool?>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in names) {
            var observations=snapshot.Observations.Where(o=>o!=null&&o.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(observations.Length!=1)return Result("Unknown",observations.Length==0?"MissingObservation:"+name:"AmbiguousObservation:"+name);
            var observed=observations[0];
            if(observed.ProjectId!=source.ProjectId||observed.Quality!="Good"||observed.Timestamp!=snapshot.Timestamp||observed.Value is null)
                return Result("Unknown","ObservationNotCoherent:"+name);
            values[name]=observed.Value;
        }
        var status=group.Evaluate(values,true);
        return Result(status,status switch {
            "Violation"=>"FeedbackMissingAfterAssertedSettling",
            "Pass"=>"FeedbackObservedAfterAssertedSettling",
            "NotApplicable"=>"BypassOrDriveNotApplicable",
            _=>"ConditionObservationUnavailable"});
    }
}
