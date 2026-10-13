#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Diagnostics;

public sealed record PlcSourceExceptionObserved(PlcSourceExceptionEntry Entry,string Status,string Code,bool? Value,DateTimeOffset? ObservedAt);

public static class PlcSourceExceptionObservation
{
    public static IReadOnlyList<PlcSourceExceptionObserved> EvaluateWithFingerprint(long projectId,string sourceFingerprint,
        IReadOnlyList<PlcSourceExceptionEntry> entries,DiagnosticSnapshot snapshot,int maxAgeMs=5000,TimeProvider? clock=null) {
        if(projectId<=0||string.IsNullOrWhiteSpace(sourceFingerprint))throw new PipelineException("InvalidSourceIdentity");
        ArgumentNullException.ThrowIfNull(entries);
        clock??=TimeProvider.System;
        var now=clock.GetUtcNow();
        var snapshotCode=snapshot==null||snapshot.ProjectId!=projectId?"ProjectScopeMismatch":
            maxAgeMs is <1 or >60000?"InvalidMaxAge":
            snapshot.Observations==null?"ObservationsRequired":
            snapshot.Timestamp>now?"FutureSnapshot":
            now-snapshot.Timestamp>TimeSpan.FromMilliseconds(maxAgeMs)?"StaleSnapshot":null;
        var result=new List<PlcSourceExceptionObserved>(entries.Count);
        foreach(var entry in entries) {
            PlcSourceExceptionObserved Unknown(string code)=>new(entry,"Unknown",code,null,null);
            if(entry.SourceFingerprint!=sourceFingerprint){result.Add(Unknown("SourceFingerprintChanged"));continue;}
            if(snapshotCode!=null){result.Add(Unknown(snapshotCode));continue;}
            if(string.IsNullOrWhiteSpace(entry.SamplePoint)){result.Add(Unknown("NoObservablePoint"));continue;}
            var matches=snapshot!.Observations.Where(o=>o!=null&&string.Equals(o.Name,entry.SamplePoint,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length!=1){result.Add(Unknown(matches.Length==0?"MissingObservation":"AmbiguousObservation"));continue;}
            var observed=matches[0];
            var code=observed.ProjectId!=projectId?"ProjectScopeMismatch":
                observed.Quality!="Good"?"QualityNotGood":
                observed.Value is null?"NullValue":
                observed.Timestamp!=snapshot.Timestamp?"NonAtomicSnapshot":
                observed.Timestamp>now?"FutureObservation":null;
            if(code!=null){result.Add(Unknown(code));continue;}
            var provenType=(entry.Category is "PlcProgramError" or "PlcNativeError" or "ProgramAlarm")&&
                (entry.Severity is "Error" or "Warning");
            if(!provenType||entry.ActiveWhen is null) {
                result.Add(new(entry,"ObservedCandidate",entry.ActiveWhen is null?"PolarityUnknown":"CategoryOrSeverityUnverified",observed.Value,observed.Timestamp));
                continue;
            }
            var active=observed.Value==entry.ActiveWhen;
            result.Add(new(entry,active?"Active":"Inactive",active?"DeclaredExceptionActive":"DeclaredExceptionInactive",observed.Value,observed.Timestamp));
        }
        return result;
    }
    public static IReadOnlyList<PlcSourceExceptionObserved> Evaluate(PipelineSnapshot source,IReadOnlyList<PlcSourceExceptionEntry> entries,
        DiagnosticSnapshot snapshot,int maxAgeMs=5000,TimeProvider? clock=null)=>
        EvaluateWithFingerprint(source.ProjectId,source.Fingerprint(),entries,snapshot,maxAgeMs,clock);
}
