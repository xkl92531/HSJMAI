using System.Net;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.LineUp;

internal static class LineUpControllerResults
{
    internal static ObjectResult Success<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(
        new HttpResponseResultModel<T>
        {
            HttpStatusCode = status,
            IsSuccess = true,
            BackResult = value,
            ErrorMessage = null,
            ExceptionMessage = null
        })
    {
        StatusCode = (int)status
    };

    internal static ObjectResult BusinessFailure(LineUpImportException error)
    {
        var status = MapStatus(error.Code);
        // ErrorMessage 保持机器可读 code；detail（含定位上下文与 issue 统计）放 ExceptionMessage，
        // 否则 422 只剩一个 "MonitoringEnableFailed" 无从排查。
        return Failure(error.Code, status, error.SafeSummary is { Length: > 0 } ? error.SafeSummary : error.Message);
    }

    internal static ObjectResult Failure(string safeCode, HttpStatusCode status, string? detail = null) => new(
        new HttpResponseResultModel<object>
        {
            HttpStatusCode = status,
            IsSuccess = false,
            BackResult = null,
            ErrorMessage = safeCode,
            ExceptionMessage = detail is { Length: > 0 }
                ? (detail.Length <= 4000 ? detail : detail[..4000] + "…")
                : null
        })
    {
        StatusCode = (int)status
    };

    private static HttpStatusCode MapStatus(string code) => code switch
    {
        "SourceTooLarge" or "PreviewTooLarge" => HttpStatusCode.RequestEntityTooLarge,
        "BatchMissing" or "MigrationBatchMissing" or
            "GraphAssetMissing" or "GraphBindingMissing" or "GraphRevisionMissing" or
            "SourceBlockMissing" or "SourceFileMissing" or "SourceContentMissing" or
            "HierarchyBindingMissing" => HttpStatusCode.NotFound,
        "PreviewNotReady" or "PreviewStale" or "BatchAlreadyCommitted" or
            "RuntimeBindingUnresolved" or "GraphStructureBlocked" or
            "HierarchyDecisionRequired" or "HierarchyCandidateStale" or
            "HierarchyCandidateAmbiguous" or "CommitConflict" or
            "MigrationBatchAlreadyStarted" or "MigrationPreflightExpired" or
            "MigrationFingerprintMismatch" or "MigrationSourceChanged" or
            "MigrationSourcePreflightBlocked" or "MigrationBatchConflict" or
            "RuntimeBindingConflict" or "RuntimeBindingStale" or
            "RuntimeBindingNotPrepared" or "RuntimeBindingNotValidated" or
            "LineUpMonitorDisabled" or "GraphRevisionStale" or
            "GraphPublishConflict" or "GraphBindingProjectMismatch" or
            "HierarchyBindingAmbiguous" or "HierarchyBindingStale" or
            "HierarchyEnsureAmbiguous" => HttpStatusCode.Conflict,
        "InvalidSelection" or "InvalidSourceName" or "InvalidBatch" or
            "UnsupportedExtension" or "UnsupportedFileExtension" or
            "HierarchyDecisionInvalid" or "DisplayNameChangeInvalid" or
            "DisplayNameTargetInvalid" or "InvalidCommitMode" or
            "InvalidMigrationRequest" or "InvalidMigrationQuery" or "ContinueOnErrorUnsupported" or
            "MigrationTokenInvalid" or "RuntimeBindingInvalid" or
            "GraphCatalogInvalidId" or "GraphCatalogQueryInvalid" or
            "GraphBindingInvalid" or
            "HierarchyReassignEmpty" or "HierarchyReassignDuplicate" or "HierarchyReassignInvalid" or
            "HierarchyRelationshipInvalid" => HttpStatusCode.BadRequest,
        // 基础设施瞬时故障（DB 写入/Hangfire 入队失败）→ 503，不再落 422 兜底造成歧义
        "BatchCreateFailed" or "JobScheduleFailed" => HttpStatusCode.ServiceUnavailable,
        _ => HttpStatusCode.UnprocessableEntity
    };
}
