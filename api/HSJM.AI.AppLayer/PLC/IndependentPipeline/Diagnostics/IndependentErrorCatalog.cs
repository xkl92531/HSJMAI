#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Diagnostics;

public sealed record PlcNativeAlarmMetadata(string Name, string Origin, string Severity, string DataType, string SourcePath);
public sealed record ErrorCatalogImportRequest(long ProjectId, string SourceKind, string SourceReference, string SourceRevision,
    string SourceArtifactSha256, IReadOnlyList<PlcNativeAlarmMetadata> Entries);
public sealed record ErrorCatalogProvenance(string SourceKind, string SourceReference, string SourceRevision,
    string SourceArtifactSha256, string Authority);
public sealed record PersistedErrorCatalog(long ProjectId, string CatalogId, string SourceFingerprint, DateTimeOffset ImportedAt,
    ErrorCatalogProvenance Provenance, IReadOnlyList<PlcNativeAlarmMetadata> Errors, int ExcludedCount);
public sealed record ErrorCatalogReadResult(long ProjectId, string Status, PersistedErrorCatalog? Catalog);
public sealed record StoredErrorCheckRequest(long ProjectId, DiagnosticSnapshot Snapshot, int MaxAgeMs = 5000);
public sealed record StoredErrorCheckResult(DiagnosticCheckResult Result, ErrorCatalogReadResult Catalog);

public interface IIndependentErrorCatalogStore
{
    Task<string> SourceFingerprintAsync(long project, CancellationToken token);
    Task SaveAsync(PersistedErrorCatalog catalog, CancellationToken token);
    Task<PersistedErrorCatalog?> LoadAsync(long project, CancellationToken token);
}

/// <summary>
/// Registers an explicit native metadata declaration with provenance. This does not authenticate the
/// caller's source artifact and never upgrades legacy inferred alarm classifications to native Error.
/// </summary>
public sealed class IndependentErrorCatalogService(IIndependentErrorCatalogStore store) : ISelfScopedAutoInject
{
    public async Task<PersistedErrorCatalog> ImportAsync(ErrorCatalogImportRequest request, CancellationToken token = default) {
        if (request == null || request.ProjectId <= 0) throw new PipelineException("ProjectIdRequired");
        if (request.SourceKind != "PlcNativeMetadata") throw new PipelineException("ExplicitNativeMetadataRequired");
        if (string.IsNullOrWhiteSpace(request.SourceReference) || string.IsNullOrWhiteSpace(request.SourceRevision) ||
            request.SourceArtifactSha256?.Length != 64 || !request.SourceArtifactSha256.All(Uri.IsHexDigit))
            throw new PipelineException("CatalogProvenanceRequired");
        if (request.Entries == null || request.Entries.Count > 100000) throw new PipelineException("InvalidCatalogEntries");
        if (request.Entries.Any(e => e == null || string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.SourcePath) ||
                e.Origin != "PLC" || e.Severity is not ("Error" or "Warning" or "Info") || e.DataType != "Bool"))
            throw new PipelineException("InvalidNativeAlarmMetadata");
        if (request.Entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Entries.Count)
            throw new PipelineException("AmbiguousCatalogSignal");
        var fingerprint = await store.SourceFingerprintAsync(request.ProjectId, token);
        var errors = request.Entries.Where(e => e.Severity == "Error").OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        var provenance = new ErrorCatalogProvenance(request.SourceKind, request.SourceReference, request.SourceRevision,
            request.SourceArtifactSha256.ToUpperInvariant(), "CallerDeclaredPlcMetadata");
        var id = PipelineHash.Of(JsonSerializer.Serialize(new { request.ProjectId, fingerprint, provenance,
            Entries = request.Entries.OrderBy(e => e.Name, StringComparer.Ordinal) }));
        var catalog = new PersistedErrorCatalog(request.ProjectId, id, fingerprint, DateTimeOffset.UtcNow,
            provenance, errors, request.Entries.Count - errors.Length);
        await store.SaveAsync(catalog, token);
        return catalog;
    }

    public async Task<ErrorCatalogReadResult> ReadAsync(long projectId, CancellationToken token = default) {
        if (projectId <= 0) throw new PipelineException("ProjectIdRequired");
        var catalog = await store.LoadAsync(projectId, token);
        if (catalog == null) return new(projectId, "NotRegistered", null);
        if (catalog.ProjectId != projectId) return new(projectId, "InvalidScope", null);
        var current = await store.SourceFingerprintAsync(projectId, token);
        return new(projectId, current == catalog.SourceFingerprint ? "Current" : "Stale", catalog);
    }

    public async Task<StoredErrorCheckResult> CheckAsync(StoredErrorCheckRequest request, IndependentDiagnosticsService diagnostics, CancellationToken token = default) {
        ErrorCatalogReadResult result;
        try { result = await ReadAsync(request.ProjectId, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) {
            return new(new(request.ProjectId, "PLCError", "Error", [new("", "Error", "ErrorCatalogReadFailed")]), new(request.ProjectId, "ReadFailed", null));
        }
        if (result.Status != "Current")
            return new(new(request.ProjectId, "PLCError", "Unknown", [new("", "Unknown", "ErrorCatalog" + result.Status)]), result);
        var declarations = result.Catalog!.Errors.Select(e => new PlcErrorDeclaration(request.ProjectId, e.Name, e.Origin, e.Severity)).ToArray();
        return new(diagnostics.CheckErrors(new(request.ProjectId, request.Snapshot, declarations, request.MaxAgeMs)), result);
    }
}
