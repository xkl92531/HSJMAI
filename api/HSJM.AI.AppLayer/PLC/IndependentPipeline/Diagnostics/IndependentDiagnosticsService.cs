#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Diagnostics;

/// <summary>Read-only evaluation of explicitly supplied observations; never reads a PLC or creates constraints.</summary>
public sealed class IndependentDiagnosticsService(IIndependentRuleReader rules, TimeProvider? clock = null) : ISelfScopedAutoInject
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public DiagnosticCheckResult CheckIo(IoCheckRequest request) {
        var invalid = Validate(request.ProjectId, request.Snapshot, request.MaxAgeMs);
        if (invalid != null) return Unknown(request.ProjectId, "IO", invalid);
        if (request.Signals == null || request.Signals.Count == 0) return Unknown(request.ProjectId, "IO", "NoSignalsSelected");
        var items = request.Signals.Select(signal => {
            if (signal == null || string.IsNullOrWhiteSpace(signal.Name)) return new DiagnosticCheckItem("", "Unknown", "InvalidSignal");
            var value = Observe(request.ProjectId, request.Snapshot, signal.Name, request.MaxAgeMs);
            if (value.Status == "Unknown") return value;
            return value with { ExpectedValue = signal.ExpectedValue,
                Status = signal.ExpectedValue is null ? "Observed" : value.Value == signal.ExpectedValue ? "Passed" : "Failed",
                Code = signal.ExpectedValue is null ? "ObservedBoolean" : value.Value == signal.ExpectedValue ? "ExpectationMet" : "ExpectationMismatch" };
        }).ToArray();
        return Result(request.ProjectId, "IO", items);
    }

    public DiagnosticCheckResult CheckErrors(ErrorCheckRequest request) {
        var invalid = Validate(request.ProjectId, request.Snapshot, request.MaxAgeMs);
        if (invalid != null) return Unknown(request.ProjectId, "PLCError", invalid);
        if (request.Catalog == null || request.Catalog.Count == 0) return Unknown(request.ProjectId, "PLCError", "NoDeclaredErrors");
        var items = request.Catalog.Select(error => {
            if (error == null || string.IsNullOrWhiteSpace(error.Name)) return new DiagnosticCheckItem("", "Unknown", "InvalidErrorDeclaration");
            if (error.ProjectId != request.ProjectId || error.Origin != "PLC")
                return new DiagnosticCheckItem(error.Name, "Unknown", "InvalidErrorDeclaration");
            if (error.Severity != "Error") return new DiagnosticCheckItem(error.Name, "NotApplicable", "NotDeclaredPlcError");
            var value = Observe(request.ProjectId, request.Snapshot, error.Name, request.MaxAgeMs);
            return value.Status == "Unknown" ? value : value with {
                Status = value.Value == true ? "Failed" : "Passed", Code = value.Value == true ? "PlcErrorActive" : "PlcErrorInactive" };
        }).ToArray();
        return Result(request.ProjectId, "PLCError", items);
    }

    public async Task<DiagnosticCheckResult> CheckConstraintsAsync(ConstraintCheckRequest request, CancellationToken token = default) {
        var invalid = Validate(request.ProjectId, request.Snapshot, request.MaxAgeMs);
        if (invalid != null) return Unknown(request.ProjectId, "Constraints", invalid);
        if (string.IsNullOrWhiteSpace(request.ModelVersion)) return Unknown(request.ProjectId, "Constraints", "ExplicitModelVersionRequired");
        if (request.LogicUnitIds == null || request.LogicUnitIds.Count == 0 || request.LogicUnitIds.Any(x => x <= 0))
            return Unknown(request.ProjectId, "Constraints", "LogicUnitsRequired");
        if (request.Snapshot.EvaluationPoint != "AfterLogicUnit" || request.Snapshot.LogicUnitId == null)
            return Unknown(request.ProjectId, "Constraints", "AfterLogicUnitSnapshotRequired");
        IReadOnlyList<PipelineUnitResult> current;
        try { current = await rules.ReadAsync(request.ProjectId, request.ModelVersion, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(request.ProjectId, "Constraints", "Error", [new("", "Error", "RuleReadFailed")]); }
        var items = new List<DiagnosticCheckItem>();
        foreach (var lu in request.LogicUnitIds.Distinct()) {
            if (request.Snapshot.LogicUnitId != lu) { items.Add(new($"LU:{lu}", "Unknown", "LogicUnitSnapshotMismatch", LogicUnitId: lu)); continue; }
            var candidates = current.Where(x => x.LogicUnitId == lu).ToArray();
            if (candidates.Length != 1 || candidates[0].Status != "Current") {
                items.Add(new($"LU:{lu}", "Unknown", candidates.Length == 1 ? "RulesNotCurrent" : "CurrentRulesUnavailable", LogicUnitId: lu)); continue;
            }
            var unit = candidates[0];
            if (unit.EvaluationPoint != "AfterLogicUnit") { items.Add(new($"LU:{lu}", "Unknown", "UnsupportedEvaluationPoint", LogicUnitId: lu)); continue; }
            if (unit.Rules == null || unit.Rules.Count == 0) { items.Add(new($"LU:{lu}", "NotApplicable", "CurrentEmptyRules", LogicUnitId: lu)); continue; }
            foreach (var rule in unit.Rules) items.Add(EvaluateRule(request, lu, rule));
        }
        return Result(request.ProjectId, "Constraints", items);
    }

    public async Task<CombinedDiagnosticResult> CheckAllAsync(CombinedDiagnosticRequest request, CancellationToken token = default) {
        // Retain direct PLC errors even when IO is incomplete or persisted-rule retrieval fails.
        var errors = CheckErrors(new(request.ProjectId, request.Snapshot, request.ErrorCatalog, request.MaxAgeMs));
        var io = CheckIo(new(request.ProjectId, request.Snapshot, request.Signals, request.MaxAgeMs));
        var constraints = await CheckConstraintsAsync(new(request.ProjectId, request.Snapshot, request.ModelVersion, request.LogicUnitIds, request.MaxAgeMs), token);
        return new(request.ProjectId, Aggregate([io.Status, constraints.Status, errors.Status]), io, constraints, errors);
    }

    private DiagnosticCheckItem EvaluateRule(ConstraintCheckRequest request, long lu, PipelineRule rule) {
        if (rule == null || rule.Signals == null || rule.Signals.Any(x => x == null || string.IsNullOrWhiteSpace(x.Name)))
            return new($"LU:{lu}", "Unknown", "InvalidRule", LogicUnitId: lu);
        var names = rule.Signals.Select(x => x.Name).ToArray();
        var members = rule.Signals.Where(x => x.Role == "Member").ToArray();
        var sources = rule.Signals.Where(x => x.Role == "Source").ToArray();
        var targets = rule.Signals.Where(x => x.Role == "Target").ToArray();
        var validRoles = names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length && (rule.Operator switch {
            "AT_MOST_ONE" or "EXACTLY_ONE" or "SAME_STATE" => members.Length >= 2 && members.Length == names.Length,
            "IMPLIES" or "REQUIRES_0" => sources.Length == 1 && targets.Length >= 1 && sources.Length + targets.Length == names.Length,
            "ALL_TRUE" => sources.Length == 1 && members.Length >= 1 && sources.Length + members.Length == names.Length,
            "MUST_BE_FALSE" => targets.Length == 1 && names.Length == 1,
            _ => false
        });
        var item = new DiagnosticCheckItem(rule.Key, "Unknown", "UnsupportedOperatorOrRoles", LogicUnitId: lu, Signals: names);
        if (!validRoles) return item;
        // A multi-feedback plant expectation is not an instant Boolean implication. This
        // endpoint has one after-LU snapshot and cannot establish command settling or bypass
        // history. The dedicated feedback check evaluates it only with explicit evidence.
        if(rule.Applicability=="AfterSettling"||rule.RequiresSettlingEvidence)
            return item with {Code="SettlingEvidenceRequired"};
        if(rule.BypassSignals.Count>0)return item with {Code="BypassConditionRequiresEvaluation"};
        var observations = names.Select(name => Observe(request.ProjectId, request.Snapshot, name, request.MaxAgeMs)).ToArray();
        if (observations.Any(x => x.Status == "Unknown"))
            return item with { Code = "ObservationUnavailable:" + string.Join(",", observations.Where(x => x.Status == "Unknown").Select(x => x.Key + ":" + x.Code)) };
        // Arbitrary reads with different timestamps are not a coherent after-LU frame.
        if (names.Any(name => request.Snapshot.Observations.Single(x => x != null && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).Timestamp != request.Snapshot.Timestamp))
            return item with { Code = "NonAtomicSnapshot" };
        var values = observations.ToDictionary(x => x.Key, x => x.Value!.Value, StringComparer.OrdinalIgnoreCase);
        bool V(PipelineSignal signal) => values[signal.Name];
        bool passes = rule.Operator switch {
            "AT_MOST_ONE" => members.Count(V) <= 1,
            "EXACTLY_ONE" => members.Count(V) == 1,
            "SAME_STATE" => members.All(V) || members.All(x => !V(x)),
            "IMPLIES" => !V(sources[0]) || targets.All(V),
            "REQUIRES_0" => !V(sources[0]) || targets.All(x => !V(x)),
            "ALL_TRUE" => !V(sources[0]) || members.All(V),
            "MUST_BE_FALSE" => !V(targets[0]),
            _ => throw new InvalidOperationException("Validated operator missing evaluator")
        };
        return item with { Status = passes ? "Passed" : "Failed", Code = passes ? "RuleSatisfiedAtLogicUnit" : "RuleViolatedAtLogicUnit" };
    }

    private string? Validate(long project, DiagnosticSnapshot? snapshot, int maxAgeMs) {
        if (project <= 0 || snapshot == null || snapshot.ProjectId != project) return "ProjectScopeMismatch";
        if (maxAgeMs <= 0 || maxAgeMs > 60000) return "InvalidMaxAge";
        if (snapshot.Observations == null) return "ObservationsRequired";
        var age = clock.GetUtcNow() - snapshot.Timestamp;
        return age < TimeSpan.Zero ? "FutureSnapshot" : age.TotalMilliseconds > maxAgeMs ? "StaleSnapshot" : null;
    }

    private DiagnosticCheckItem Observe(long project, DiagnosticSnapshot snapshot, string name, int maxAgeMs) {
        var observations = snapshot.Observations.Where(x => x != null && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (observations.Length != 1) return new(name, "Unknown", observations.Length == 0 ? "MissingObservation" : "AmbiguousObservation");
        var value = observations[0];
        var age = clock.GetUtcNow() - value.Timestamp;
        var code = value.ProjectId != project ? "ProjectScopeMismatch" : value.Quality != "Good" ? "QualityNotGood" :
            value.Value == null ? "NullValue" : age < TimeSpan.Zero || value.Timestamp > snapshot.Timestamp ? "FutureObservation" :
            age.TotalMilliseconds > maxAgeMs ? "StaleObservation" : null;
        return code == null ? new(name, "Observed", "ObservedBoolean", value.Value) : new(name, "Unknown", code);
    }

    private static DiagnosticCheckResult Unknown(long project, string check, string code) => new(project, check, "Unknown", [new("", "Unknown", code)]);
    private static DiagnosticCheckResult Result(long project, string check, IReadOnlyList<DiagnosticCheckItem> items) => new(project, check, Aggregate(items.Select(x => x.Status)), items);
    private static string Aggregate(IEnumerable<string> states) {
        var all = states.ToArray();
        return all.Contains("Failed") ? "Failed" : all.Contains("Error") ? "Error" : all.Contains("Unknown") ? "Unknown" :
            all.Length == 0 || all.All(x => x == "NotApplicable") ? "Unknown" : all.Contains("Observed") ? "Observed" : "Passed";
    }
}
