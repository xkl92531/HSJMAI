#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed record BusinessEvidence(string Id,string Kind,string Text);
public sealed record BusinessSymbol(string Alias,long BlockId,string Path,string Section,string DataType);
public sealed record BusinessDescription(long LogicUnitId,long BlockId,string BlockName,string Fingerprint,string SourceHash,
    string Status,string Markdown,string ModelText,IReadOnlyDictionary<string,string> SignalAliases,
    IReadOnlyList<BusinessEvidence> Evidence,IReadOnlyList<string> Limitations,IReadOnlyList<string> Callees,
    IReadOnlyList<BusinessSymbol>? Symbols=null)
{
    public IReadOnlyDictionary<string,string> SignalTypes {get;init;}=new Dictionary<string,string>(StringComparer.Ordinal);
}
public sealed record BusinessCondition(string Signal,bool Value);
public sealed record BusinessCandidate(string Operator,IReadOnlyList<PipelineSignal> Signals,string Explanation,
    IReadOnlyList<string> EvidenceIds,IReadOnlyList<BusinessCondition> Conditions,string Applicability,
    long? SettlingTimeMs=null,string? SettlingTimeSignal=null,LogicTerm? ApplicabilityCondition=null,double SettlingTimeScale=1,
    LogicTerm? Assertion=null);
public sealed record BusinessDecision(BusinessCandidate Candidate,string Status,string Code)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConclusiveRejection => Status=="Rejected"&&(Code.StartsWith("ModelCodeReviewRejected:",StringComparison.Ordinal)||
        Code=="SourceSemanticCounterexample"||Code.StartsWith("CallOutputOverwritten:",StringComparison.Ordinal)||
        Code.StartsWith("CallBindingConflict:",StringComparison.Ordinal)||Code.StartsWith("CallBindingEpochConflict:",StringComparison.Ordinal));
}
public sealed record BusinessUnitResult(long LogicUnitId,string DescriptionFingerprint,string ModelVersion,
    IReadOnlyList<BusinessDecision> Decisions,string Status,string? ErrorCode=null)
{
    public BusinessCoverage? Coverage {get;init;}
}
public interface IBusinessInference
{
    string Version {get;}
    Task InitializeAsync(CancellationToken token);
    Task<IReadOnlyList<BusinessCandidate>> InferAsync(BusinessDescription description,CancellationToken token);
}
public interface IBusinessReviewInference
{
    Task<IReadOnlyList<BusinessDecision>> ReviewAsync(BusinessDescription description,IReadOnlyList<BusinessCandidate> candidates,CancellationToken token);
}
public interface IBusinessPipelineStore
{
    Task InitializeAsync(CancellationToken token);
    Task<IReadOnlyDictionary<string,BusinessDescription>> LoadDescriptionsAsync(long project,CancellationToken token);
    Task SaveDescriptionsAsync(long project,IReadOnlyList<BusinessDescription> descriptions,CancellationToken token);
    Task SaveResultsAsync(long project,Guid run,string sourceFingerprint,IReadOnlyList<BusinessUnitResult> results,CancellationToken token);
    Task<IReadOnlyList<BusinessUnitResult>> LoadResultsAsync(long project,string sourceFingerprint,string modelVersion,CancellationToken token);
}
