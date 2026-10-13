#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Diagnostics;

// Values are explicitly Boolean. Analog values need a separately declared comparison contract.
public sealed record DiagnosticObservation(long ProjectId, string Name, DateTimeOffset Timestamp, bool? Value, string Quality);
public sealed record DiagnosticSnapshot(long ProjectId, DateTimeOffset Timestamp, IReadOnlyList<DiagnosticObservation> Observations,
    string? EvaluationPoint = null, long? LogicUnitId = null);
public sealed record IoExpectation(string Name, bool? ExpectedValue = null);
// Origin and severity are explicit metadata supplied by the PLC catalog adapter/caller, never inferred from names.
public sealed record PlcErrorDeclaration(long ProjectId, string Name, string Origin, string Severity);
public sealed record IoCheckRequest(long ProjectId, DiagnosticSnapshot Snapshot, IReadOnlyList<IoExpectation> Signals, int MaxAgeMs = 5000);
public sealed record ErrorCheckRequest(long ProjectId, DiagnosticSnapshot Snapshot, IReadOnlyList<PlcErrorDeclaration> Catalog, int MaxAgeMs = 5000);
public sealed record ConstraintCheckRequest(long ProjectId, DiagnosticSnapshot Snapshot, string ModelVersion, IReadOnlyList<long> LogicUnitIds, int MaxAgeMs = 5000);
public sealed record CombinedDiagnosticRequest(long ProjectId, DiagnosticSnapshot Snapshot, IReadOnlyList<IoExpectation> Signals,
    string ModelVersion, IReadOnlyList<long> LogicUnitIds, IReadOnlyList<PlcErrorDeclaration> ErrorCatalog, int MaxAgeMs = 5000);
public sealed record DiagnosticCheckItem(string Key, string Status, string Code, bool? Value = null, bool? ExpectedValue = null,
    long? LogicUnitId = null, IReadOnlyList<string>? Signals = null);
public sealed record DiagnosticCheckResult(long ProjectId, string Check, string Status, IReadOnlyList<DiagnosticCheckItem> Items);
public sealed record CombinedDiagnosticResult(long ProjectId, string Status, DiagnosticCheckResult Io, DiagnosticCheckResult Constraints, DiagnosticCheckResult Errors);

public interface IIndependentRuleReader
{
    Task<IReadOnlyList<PipelineUnitResult>> ReadAsync(long projectId, string modelVersion, CancellationToken token);
}
