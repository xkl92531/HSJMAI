using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpImportRepository(ISqlSugarClient db) : ILineUpImportRepository, IAutoInject
{
    /// <summary>
    /// 批次表手写 SQL 查询的行绑定类型（无参构造 class；SqlSugar 对 record 投影绑定不稳定，见 GetBatchAsync 注释）
    /// </summary>
    private sealed class LineUpImportBatchRow
    {
        public long Id { get; set; }
        public string StoredFilePath { get; set; } = string.Empty;
        public string SourceFileName { get; set; } = string.Empty;
        public string SourceFileHash { get; set; } = string.Empty;
        public long LineId { get; set; }
        public long DeviceId { get; set; }
        public long? PlcProjectId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ErrorSummary { get; set; }
        public string? ParsingLeaseToken { get; set; }
        public DateTime? ParsingLeaseExpiresAt { get; set; }
        public int PreviewSchemaVersion { get; set; }
        public int PreviewRevision { get; set; }
        public string? PreviewHash { get; set; }
        public string? PreviewJson { get; set; }
        public string? SummaryJson { get; set; }
        public string? CommitRequestHash { get; set; }
        public string? CommitReceiptJson { get; set; }
        public string? CommitMode { get; set; }
        public DateTime? CommittedAt { get; set; }

        public LineUpImportBatchData ToData() => new(
            Id, StoredFilePath, SourceFileName, SourceFileHash,
            LineId, DeviceId, PlcProjectId, Status, ErrorSummary,
            ParsingLeaseToken, ParsingLeaseExpiresAt,
            PreviewSchemaVersion, PreviewRevision, PreviewHash,
            PreviewJson, SummaryJson, CommitRequestHash,
            CommitReceiptJson, CommitMode, CommittedAt);
    }

    public async Task CreateBatchAsync(LineUpImportBatchCreate request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = new LineUpImportBatchEntity
        {
            Id = request.BatchId,
            StoredFilePath = request.StorageKey,
            SourceFileName = request.SourceFileName,
            SourceFileHash = request.SourceFileHash,
            LineId = request.LineId,
            DeviceId = request.DeviceId,
            Status = "Uploaded",
            PreviewSchemaVersion = 0,
            PreviewRevision = 0,
            IsDeleted = false,
            CreateTime = DateTime.UtcNow
        };
        try
        {
            await db.Insertable(entity).ExecuteCommandAsync();
        }
        catch (Exception)
        {
            var generated = db.Insertable(entity).ToSql();
            Console.WriteLine("[INSERT-PARAM] " + generated.Key);
            Console.WriteLine("[INSERT-PARS] " + string.Join(" | ", generated.Value.Select(p => p.ParameterName + "=" + (p.Value?.ToString() ?? "NULL"))));
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<bool> FailUploadAsync(
        long batchId,
        string safeErrorCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // SqlSugar Updateable 在引号列名（PG 大小写保留表）下生成非法 SQL（42601/二次引号），改手写 SQL
        var affected = await db.Ado.ExecuteCommandAsync(
            """
            UPDATE "AI_LineUp_ImportBatch"
            SET "Status" = 'Failed', "ErrorSummary" = @ErrorCode, "ModifyTime" = @Now
            WHERE "IsDeleted" = FALSE AND "Id" = @BatchId AND "Status" = 'Uploaded'
            """,
            new SugarParameter("@ErrorCode", safeErrorCode),
            new SugarParameter("@Now", DateTime.UtcNow),
            new SugarParameter("@BatchId", batchId));
        cancellationToken.ThrowIfCancellationRequested();
        return affected == 1;
    }

    public async Task<LineUpImportBatchData?> GetBatchAsync(
        long batchId,
        CancellationToken cancellationToken)
    {
        // SqlSugar 对 record 投影的 DataReader 绑定不稳定（无参构造缺失 → ArgumentNullException），
        // 与引号列名叠加时必炸：改手写 SQL + class 绑定（已实验验证的可靠路径）
        var rows = await db.Ado.SqlQueryAsync<LineUpImportBatchRow>(
            """
            SELECT "Id", "StoredFilePath", "SourceFileName", "SourceFileHash",
                   "LineId", "DeviceId", "PlcProjectId", "Status", "ErrorSummary",
                   "ParsingLeaseToken", "ParsingLeaseExpiresAt",
                   "PreviewSchemaVersion", "PreviewRevision", "PreviewHash",
                   "PreviewJson"::text AS "PreviewJson", "SummaryJson"::text AS "SummaryJson", "CommitRequestHash",
                   "CommitReceiptJson"::text AS "CommitReceiptJson", "CommitMode", "CommittedAt"
            FROM "AI_LineUp_ImportBatch"
            WHERE "IsDeleted" = FALSE AND "Id" = @BatchId
            """,
            new SugarParameter[] { new("@BatchId", batchId) });
        return rows.Count == 1 ? rows[0].ToData() : null;
    }

    public async Task WriteCommitRejectionAsync(
        long batchId,
        string code,
        string detail,
        CancellationToken cancellationToken)
    {
        // ErrorSummary 是 text 列不限长；追加式保留历史，单次拒绝截 2000 字防失控
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] commit rejected {code}: {detail}";
        if (line.Length > 2000)
        {
            line = line[..2000];
        }
        await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_ImportBatch"
                SET "ErrorSummary" = COALESCE("ErrorSummary" || E'\n', '') || @Line
                WHERE "Id" = @BatchId
                """,
                new SugarParameter("@Line", line),
                new SugarParameter("@BatchId", batchId))
            .WaitAsync(cancellationToken);
    }

    public async Task<LineUpImportBatchData?> TryAcquireParsingLeaseAsync(
        long batchId,
        string leaseToken,
        DateTime now,
        DateTime expiresAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseToken) || expiresAt <= now)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // SqlSugar Updateable 在引号列名（PG 大小写保留表）下生成非法 SQL（42601/二次引号），改手写 SQL
        var affected = await db.Ado.ExecuteCommandAsync(
            """
            UPDATE "AI_LineUp_ImportBatch"
            SET "Status" = 'Parsing', "ParsingLeaseToken" = @LeaseToken,
                "ParsingLeaseExpiresAt" = @ExpiresAt, "ErrorSummary" = NULL, "ModifyTime" = @Now
            WHERE "IsDeleted" = FALSE AND "Id" = @BatchId
              AND ("Status" = 'Uploaded'
                   OR ("Status" = 'Parsing' AND "ParsingLeaseExpiresAt" IS NOT NULL
                       AND "ParsingLeaseExpiresAt" <= @Now))
            """,
            new SugarParameter("@LeaseToken", leaseToken),
            new SugarParameter("@ExpiresAt", expiresAt),
            new SugarParameter("@Now", now),
            new SugarParameter("@BatchId", batchId));
        cancellationToken.ThrowIfCancellationRequested();
        if (affected != 1)
        {
            return null;
        }

        // 同 GetBatchAsync：record 投影绑定不可靠，手写 SQL + class 绑定
        var rows = await db.Ado.SqlQueryAsync<LineUpImportBatchRow>(
            """
            SELECT "Id", "StoredFilePath", "SourceFileName", "SourceFileHash",
                   "LineId", "DeviceId", "PlcProjectId", "Status", "ErrorSummary",
                   "ParsingLeaseToken", "ParsingLeaseExpiresAt",
                   "PreviewSchemaVersion", "PreviewRevision", "PreviewHash",
                   "PreviewJson"::text AS "PreviewJson", "SummaryJson"::text AS "SummaryJson", "CommitRequestHash",
                   "CommitReceiptJson"::text AS "CommitReceiptJson", "CommitMode", "CommittedAt"
            FROM "AI_LineUp_ImportBatch"
            WHERE "IsDeleted" = FALSE AND "Id" = @BatchId AND "Status" = 'Parsing'
              AND "ParsingLeaseToken" = @LeaseToken
            """,
            new SugarParameter[] { new("@BatchId", batchId), new("@LeaseToken", leaseToken) });
        return rows.Count == 1 ? rows[0].ToData() : null;
    }

    public async Task<bool> CompletePreviewAsync(
        LineUpImportPreviewCompletion completion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        db.Ado.BeginTran();
        try
        {
            var now = DateTime.UtcNow;
            // SqlSugar Updateable 在引号列名（PG 大小写保留表）下生成非法 SQL（42601/二次引号），改手写 SQL；
            // jsonb 列用显式 ::jsonb 转换避免 Npgsql 42804
            var affected = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_ImportBatch"
                SET "Status" = 'PreviewReady',
                    "PreviewSchemaVersion" = @SchemaVersion,
                    "PreviewRevision" = "PreviewRevision" + 1,
                    "PreviewJson" = @PreviewJson::jsonb,
                    -- jsonb 列存储会规范化 JSON 文本（键重排/空格），读回文本 != 写入文本，
                    -- 因此 hash 必须由 PG 对规范化文本计算（与 C# 读回 ::text 重算结果一致），否则 GetPreviewAsync 自校验必失败
                    "PreviewHash" = encode(sha256(CONVERT_TO(@PreviewJson::jsonb::text, 'UTF8')), 'hex'),
                    "SummaryJson" = @SummaryJson::jsonb,
                    "PlcProjectId" = @PlcProjectId::bigint,
                    "ParsingLeaseToken" = NULL,
                    "ParsingLeaseExpiresAt" = NULL,
                    "ErrorSummary" = NULL,
                    "ModifyTime" = @Now
                WHERE "IsDeleted" = FALSE AND "Id" = @BatchId
                  AND "Status" = 'Parsing' AND "ParsingLeaseToken" = @LeaseToken
                """,
                new SugarParameter("@SchemaVersion", completion.PreviewSchemaVersion),
                new SugarParameter("@PreviewJson", (object?)completion.PreviewJson ?? DBNull.Value),
                new SugarParameter("@SummaryJson", (object?)completion.SummaryJson ?? DBNull.Value),
                new SugarParameter("@PlcProjectId", completion.PlcProjectId.HasValue ? completion.PlcProjectId.Value : DBNull.Value),
                new SugarParameter("@Now", now),
                new SugarParameter("@BatchId", completion.BatchId),
                new SugarParameter("@LeaseToken", completion.LeaseToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (affected != 1)
            {
                db.Ado.RollbackTran();
                return false;
            }

            await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_ImportIssue"
                SET "IsDeleted" = TRUE, "ModifyTime" = @Now
                WHERE "IsDeleted" = FALSE AND "ImportBatchId" = @BatchId
                """,
                new SugarParameter("@Now", now),
                new SugarParameter("@BatchId", completion.BatchId));
            if (completion.Issues.Count > 0)
            {
                var issueRows = completion.Issues.Select(issue => new LineUpImportIssueEntity
                {
                    ImportBatchId = completion.BatchId,
                    Severity = issue.Severity,
                    IssueType = issue.IssueType,
                    SourceKey = issue.SourceKey,
                    Message = issue.Message,
                    ResolutionJson = issue.ResolutionJson,
                    IsDeleted = false,
                    CreateTime = now
                }).ToArray();
                await db.Insertable(issueRows).ExecuteCommandAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            db.Ado.CommitTran();
            return true;
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    public async Task<bool> FailParsingAsync(
        long batchId,
        string leaseToken,
        string safeErrorCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // SqlSugar Updateable 在引号列名（PG 大小写保留表）下生成非法 SQL（42601/二次引号），改手写 SQL
        var affected = await db.Ado.ExecuteCommandAsync(
            """
            UPDATE "AI_LineUp_ImportBatch"
            SET "Status" = 'Failed', "ErrorSummary" = @ErrorCode,
                "ParsingLeaseToken" = NULL, "ParsingLeaseExpiresAt" = NULL, "ModifyTime" = @Now
            WHERE "IsDeleted" = FALSE AND "Id" = @BatchId AND "Status" = 'Parsing'
              AND "ParsingLeaseToken" = @LeaseToken
            """,
            new SugarParameter("@ErrorCode", safeErrorCode),
            new SugarParameter("@Now", DateTime.UtcNow),
            new SugarParameter("@BatchId", batchId),
            new SugarParameter("@LeaseToken", leaseToken));
        cancellationToken.ThrowIfCancellationRequested();
        return affected == 1;
    }

    public async Task<LineUpExistingGraphState> LoadExistingGraphStateAsync(
        IReadOnlyCollection<long> plcProjectIds,
        IReadOnlyCollection<long> graphBlockIds,
        IReadOnlyCollection<string> incomingSequenceSourceKeys,
        CancellationToken cancellationToken)
    {
        var projectIds = plcProjectIds.Where(x => x > 0).Distinct().ToArray();
        var blockIds = graphBlockIds.Where(x => x > 0).Distinct().ToArray();
        var sourceKeys = incomingSequenceSourceKeys
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (projectIds.Length == 0)
        {
            return EmptyGraphState();
        }

        // SqlSugar Queryable 的 Contains/可空转换表达式翻译在此环境炸 op_Implicit，改手写 SQL 绑回同一实体
        static SugarParameter[] IdParams(string prefix, IReadOnlyCollection<long> ids) =>
            ids.Select((value, index) => new SugarParameter($"@{prefix}{index}", value)).ToArray();
        const char Q = (char)34;
        static string ParamList(IEnumerable<SugarParameter> ps) =>
            string.Join(", ", ps.Select(p => p.ParameterName));

        var pidParams = IdParams("sp", projectIds);
        var pidList = ParamList(pidParams);
        var seqFilter = $"{Q}IsDeleted{Q} = FALSE AND {Q}PlcProjectId{Q} IN ({pidList})";
        var extraParams = new List<SugarParameter>(pidParams);
        if (blockIds.Length > 0 && sourceKeys.Length > 0)
        {
            var bp = IdParams("sb", blockIds);
            var kp = sourceKeys.Select((v, i) => new SugarParameter($"@sk{i}", v)).ToArray();
            seqFilter += $" AND ({Q}GraphBlockId{Q} IN ({ParamList(bp)}) OR {Q}SourceKey{Q} IN ({ParamList(kp)}))";
            extraParams.AddRange(bp);
            extraParams.AddRange(kp);
        }
        else if (blockIds.Length > 0)
        {
            var bp = IdParams("sb", blockIds);
            seqFilter += $" AND {Q}GraphBlockId{Q} IN ({ParamList(bp)})";
            extraParams.AddRange(bp);
        }
        else if (sourceKeys.Length > 0)
        {
            var kp = sourceKeys.Select((v, i) => new SugarParameter($"@sk{i}", v)).ToArray();
            seqFilter += $" AND {Q}SourceKey{Q} IN ({ParamList(kp)})";
            extraParams.AddRange(kp);
        }
        else
        {
            return EmptyGraphState();
        }

        var sequenceRows = await db.Ado.SqlQueryAsync<LineUpGraphSequenceEntity>(
                $"SELECT * FROM {Q}AI_LineUp_GraphSequence{Q} WHERE {seqFilter}",
                extraParams.ToArray())
            .WaitAsync(cancellationToken);
        var sequenceIds = sequenceRows.Select(x => x.Id).Distinct().ToArray();
        if (sequenceIds.Length == 0)
        {
            return EmptyGraphState();
        }

        var sidParams = IdParams("ss", sequenceIds);
        var sidList = ParamList(sidParams);
        var stepRows = await db.Ado.SqlQueryAsync<LineUpGraphStepEntity>(
                $"SELECT * FROM {Q}AI_LineUp_GraphStep{Q} WHERE {Q}IsDeleted{Q} = FALSE AND {Q}SequenceId{Q} IN ({sidList})",
                sidParams)
            .WaitAsync(cancellationToken);
        var transitionRows = await db.Ado.SqlQueryAsync<LineUpGraphTransitionEntity>(
                $"SELECT * FROM {Q}AI_LineUp_GraphTransition{Q} WHERE {Q}IsDeleted{Q} = FALSE AND {Q}SequenceId{Q} IN ({sidList})",
                sidParams)
            .WaitAsync(cancellationToken);
        var stepIds = stepRows.Select(x => x.Id).Distinct().ToArray();
        IReadOnlyList<LineUpGraphActionEntity> actionRows;
        if (stepIds.Length == 0)
        {
            actionRows = [];
        }
        else
        {
            var stp = IdParams("sa", stepIds);
            actionRows = await db.Ado.SqlQueryAsync<LineUpGraphActionEntity>(
                    $"SELECT * FROM {Q}AI_LineUp_GraphAction{Q} WHERE {Q}IsDeleted{Q} = FALSE AND {Q}StepId{Q} IN ({ParamList(stp)})",
                    stp)
                .WaitAsync(cancellationToken);
        }

        var edgeRows = await db.Ado.SqlQueryAsync<LineUpGraphEdgeEntity>(
                $"SELECT * FROM {Q}AI_LineUp_GraphEdge{Q} WHERE {Q}IsDeleted{Q} = FALSE AND {Q}SequenceId{Q} IN ({sidList})",
                sidParams)
            .WaitAsync(cancellationToken);
        var bindingRows = await db.Ado.SqlQueryAsync<LineUpSubStationGraphBindingEntity>(
                $"SELECT * FROM {Q}AI_LineUp_SubStationGraphBinding{Q} WHERE {Q}IsDeleted{Q} = FALSE AND {Q}SequenceId{Q} IN ({sidList})",
                sidParams)
            .WaitAsync(cancellationToken);

        var orderedSequenceRows = sequenceRows.OrderBy(x => x.PlcProjectId).ThenBy(x => x.GraphBlockId)
            .ThenBy(x => x.SourceKey, StringComparer.Ordinal).ToArray();
        var orderedStepRows = stepRows.OrderBy(x => x.SequenceId).ThenBy(x => x.SourceKey, StringComparer.Ordinal).ToArray();
        var orderedTransitionRows = transitionRows.OrderBy(x => x.SequenceId).ThenBy(x => x.SourceKey, StringComparer.Ordinal).ToArray();
        var orderedActionRows = actionRows.OrderBy(x => x.StepId).ThenBy(x => x.SourceKey, StringComparer.Ordinal).ToArray();
        var orderedEdgeRows = edgeRows.OrderBy(x => x.SequenceId).ThenBy(x => x.SourceKey, StringComparer.Ordinal).ToArray();
        var orderedBindingRows = bindingRows.OrderBy(x => x.LineId).ThenBy(x => x.DeviceId).ThenBy(x => x.SequenceId).ToArray();
        // 改派桥接序列（EnsureGraphSequenceAsync 为落绑定先占位、无任何步/转换/边子行的空壳）
        // 不进 StateHash：它们是「预览→改派」流程自身的产物，即将到来的提交会按 sequenceWrites
        // 填充它们。计入哈希会让未保存过结构的顺控在首次改派后保存永远 409（PreviewStale）。
        // 状态对象仍保留桥接行，提交按 (Project, Block, SourceKey) 复用其行 Id。
        var sequencesWithChildren = new HashSet<long>(
            stepRows.Select(x => x.SequenceId)
                .Concat(transitionRows.Select(x => x.SequenceId))
                .Concat(edgeRows.Select(x => x.SequenceId)));
        var hashableSequenceRows = orderedSequenceRows
            .Where(x => sequencesWithChildren.Contains(x.Id)).ToArray();
        var hashMaterial = string.Join("\n",
            hashableSequenceRows.Select(x =>
                    $"S|{x.Id}|{x.PlcProjectId}|{x.GraphBlockId}|{x.InstanceDbBlockId}|{x.SourceKey}|" +
                    $"{x.SourceName}|{x.DisplayName}|{x.LayoutJson}|{x.SourceHash}")
                .Concat(orderedStepRows.Select(x =>
                    $"NS|{x.Id}|{x.SequenceId}|{x.SourceKey}|{x.SourceName}|{x.DisplayName}|{x.StepNumber}|" +
                    $"{x.IsInitial}|{x.IsFinal}|{x.IsSafety}|{x.Comment}|{x.SourceVariablePath}|{x.BlockInterfaceId}|" +
                    $"{x.MaximumStepTime}|{x.WarningTime}"))
                .Concat(orderedTransitionRows.Select(x =>
                    $"NT|{x.Id}|{x.SequenceId}|{x.SourceKey}|{x.SourceName}|{x.DisplayName}|{x.TransitionNumber}|" +
                    $"{x.ConditionExpression}|{x.ConditionTokensJson}|{x.SourceVariablePath}|{x.BlockInterfaceId}|" +
                    $"{x.IsJump}|{x.ProgrammingLanguage}|{x.IsMissing}"))
                .Concat(orderedActionRows.Select(x =>
                    $"A|{x.Id}|{x.StepId}|{x.SourceKey}|{x.SourceName}|{x.DisplayName}|{x.Qualifier}|" +
                    $"{x.ActionCode}|{x.CallBlockName}|{x.ExecuteTime}|{x.Ordinal}|{x.TokensJson}|{x.ActionText}"))
                .Concat(orderedEdgeRows.Select(x =>
                    $"E|{x.Id}|{x.SequenceId}|{x.SourceKey}|{x.FromNodeType}|{x.FromNodeId}|" +
                    $"{x.ToNodeType}|{x.ToNodeId}|{x.EdgeKind}|{x.BranchOrder}")));
                // 绑定行(B|)不进 StateHash：层级改派只写绑定行，而提交会用 sequenceWrites
                // 重写这些序列的绑定 —— 把绑定计入并发令牌会让「预览→改派→保存」永远 409。
                // 结构行（S|NS|NT|A|E|）仍全量参与，提交间真实的结构竞争照常被检出。
        var stateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashMaterial))).ToLowerInvariant();
        var orderedSequences = orderedSequenceRows.Select(x => new LineUpExistingSequenceState(
            x.Id, x.PlcProjectId, x.GraphBlockId, x.SourceKey, x.DisplayName, x.LayoutJson)).ToArray();
        var nodes = orderedStepRows.Select(x => new LineUpExistingNodeState(
                x.Id, x.SequenceId, "Step", x.SourceKey, x.DisplayName))
            .Concat(orderedTransitionRows.Select(x => new LineUpExistingNodeState(
                x.Id, x.SequenceId, "Transition", x.SourceKey, x.DisplayName))).ToArray();
        var orderedActions = orderedActionRows.Select(x => new LineUpExistingActionState(
            x.Id, x.StepId, x.SourceKey, x.DisplayName)).ToArray();
        var orderedEdges = orderedEdgeRows.Select(x => new LineUpExistingEdgeState(
            x.Id, x.SequenceId, x.SourceKey)).ToArray();
        var orderedBindings = orderedBindingRows.Select(x => new LineUpExistingBindingState(
            x.Id, x.LineId, x.DeviceId, x.SequenceId, x.PlcProjectId, x.GraphBlockId, x.BindingStatus)).ToArray();
        // owner 块活性：项目重传替换块后，存量序列的 GraphBlockId 指向软删/消失的旧块。
        // 这些序列与新块是同 key 的同一张图（新版本），预览/提交侧据此放行接管，不再判 owner 冲突。
        var ownerBlockIds = sequenceRows.Select(x => x.GraphBlockId).Distinct().ToArray();
        IReadOnlyCollection<long> deadOwnerGraphBlockIds = Array.Empty<long>();
        if (ownerBlockIds.Length > 0)
        {
            var obp = IdParams("ob", ownerBlockIds);
            var aliveOwnerIds = await db.Ado.SqlQueryAsync<long>(
                    $"SELECT id FROM ai_plc_block_new WHERE id IN ({ParamList(obp)}) AND isdeleted = FALSE",
                    obp.ToArray())
                .WaitAsync(cancellationToken);
            var aliveSet = new HashSet<long>(aliveOwnerIds);
            deadOwnerGraphBlockIds = ownerBlockIds.Where(x => !aliveSet.Contains(x)).ToArray();
        }
        return new(stateHash, orderedSequences, nodes, orderedActions, orderedEdges, orderedBindings, deadOwnerGraphBlockIds);
    }

    public async Task<LineUpCommitPersistenceOutcome> CommitAsync(
        LineUpCommitPersistencePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // ReadyForMonitoring 的结构落库与 StructureOnly 完全一致（监控启用在事务提交后由应用层编排）
        if (!string.Equals(plan.CommitMode, "StructureOnly", StringComparison.Ordinal) &&
            !string.Equals(plan.CommitMode, "ReadyForMonitoring", StringComparison.Ordinal) ||
            plan.BatchId <= 0 || plan.LineId <= 0 || plan.DeviceId <= 0 ||
            plan.Documents.Count == 0 || string.IsNullOrWhiteSpace(plan.ExpectedPreviewHash) ||
            string.IsNullOrWhiteSpace(plan.ExpectedGraphStateHash) ||
            string.IsNullOrWhiteSpace(plan.DiffToken) ||
            string.IsNullOrWhiteSpace(plan.CommitRequestHash) ||
            string.IsNullOrWhiteSpace(plan.CommitReceiptJson) ||
            plan.Documents.SelectMany(x => x.Sequences).Any(x =>
                !string.Equals(x.BindingStatus, "Unresolved", StringComparison.Ordinal) &&
                !string.Equals(x.BindingStatus, "NeedsReview", StringComparison.Ordinal)))
        {
            throw CommitError("CommitPlanInvalid", "The internal Phase 1 commit plan is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        db.Ado.BeginTran();
        try
        {
            // SqlSugar Updateable 锁行模式在引号列下生成非法 SQL(42601)，改手写（见 LockCommitOwnersAsync 注释）
            var guarded = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_ImportBatch" SET "ModifyTime" = "ModifyTime"
                WHERE "IsDeleted" = FALSE AND "Id" = @BatchId AND "Status" = 'PreviewReady'
                  AND "LineId" = @LineId AND "DeviceId" = @DeviceId
                  AND "PreviewSchemaVersion" = @SchemaVersion AND "PreviewRevision" = @Revision
                  AND "PreviewHash" = @Hash
                """,
                new SugarParameter("@BatchId", plan.BatchId),
                new SugarParameter("@LineId", plan.LineId),
                new SugarParameter("@DeviceId", plan.DeviceId),
                new SugarParameter("@SchemaVersion", plan.ExpectedPreviewSchemaVersion),
                new SugarParameter("@Revision", plan.ExpectedPreviewRevision),
                new SugarParameter("@Hash", plan.ExpectedPreviewHash));
            cancellationToken.ThrowIfCancellationRequested();
            if (guarded != 1)
            {
                var existing = await GetBatchAsync(plan.BatchId, cancellationToken);
                if (existing is not null &&
                    string.Equals(existing.Status, "Committed", StringComparison.Ordinal) &&
                    string.Equals(existing.CommitRequestHash, plan.CommitRequestHash, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(existing.CommitReceiptJson))
                {
                    db.Ado.CommitTran();
                    return new(true, existing.CommitReceiptJson);
                }

                throw CommitError("PreviewStale", "The import batch no longer matches the committed preview.");
            }

            var allSequences = plan.Documents.SelectMany(x => x.Sequences).ToArray();
            var projectIds = plan.Documents.Select(x => x.PlcProjectId).Distinct().ToArray();
            var graphBlockIds = plan.Documents.Select(x => x.GraphBlockId).Distinct().ToArray();
            var sourceKeys = allSequences.Select(x => x.SourceKey).Distinct(StringComparer.Ordinal).ToArray();
            await LockCommitOwnersAsync(plan, allSequences, cancellationToken);
            var hierarchy = await LoadHierarchyContextAsync(plan, allSequences, cancellationToken);
            var state = await LoadExistingGraphStateAsync(
                projectIds, graphBlockIds, sourceKeys, cancellationToken);
            if (!string.Equals(state.StateHash, plan.ExpectedGraphStateHash, StringComparison.Ordinal))
            {
                throw CommitError("PreviewStale", "The persisted GRAPH changed before the transaction acquired its owner locks.");
            }

            var desiredSequences = new Dictionary<(long ProjectId, long GraphBlockId, string SourceKey), long>();
            foreach (var document in plan.Documents
                         .OrderBy(x => x.PlcProjectId)
                         .ThenBy(x => x.GraphBlockId)
                         .ThenBy(x => x.SourceHash, StringComparer.Ordinal))
            {
                foreach (var sequence in document.Sequences.OrderBy(x => x.SourceKey, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var subStationId = await ResolveSubStationIdAsync(
                        plan, sequence, hierarchy, cancellationToken);
                    var sequenceId = await UpsertSequenceAsync(document, sequence, cancellationToken);
                    await UpsertSequenceChildrenAsync(sequenceId, sequence, cancellationToken);
                    await UpsertBindingAsync(
                        plan, document, sequence, sequenceId, subStationId, cancellationToken);
                    if (!desiredSequences.TryAdd(
                            (document.PlcProjectId, document.GraphBlockId, sequence.SourceKey),
                            sequenceId))
                    {
                        throw CommitError("DuplicateSourceKey", "A Sequence source key is duplicated in the commit plan.");
                    }
                }
            }

            await SoftDeleteRemovedSequencesAsync(plan.Documents, desiredSequences, cancellationToken);
            var completed = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_ImportBatch"
                SET "Status" = 'Committed', "CommitMode" = @Mode, "CommitRequestHash" = @ReqHash,
                    "CommitReceiptJson" = @Receipt::jsonb, "CommittedAt" = @At,
                    "ErrorSummary" = NULL, "ModifyTime" = @At
                WHERE "IsDeleted" = FALSE AND "Id" = @BatchId AND "Status" = 'PreviewReady'
                  AND "PreviewSchemaVersion" = @SchemaVersion AND "PreviewRevision" = @Revision
                  AND "PreviewHash" = @Hash
                """,
                new SugarParameter("@Mode", plan.CommitMode),
                new SugarParameter("@ReqHash", plan.CommitRequestHash),
                new SugarParameter("@Receipt", plan.CommitReceiptJson),
                new SugarParameter("@At", plan.CommittedAt),
                new SugarParameter("@BatchId", plan.BatchId),
                new SugarParameter("@SchemaVersion", plan.ExpectedPreviewSchemaVersion),
                new SugarParameter("@Revision", plan.ExpectedPreviewRevision),
                new SugarParameter("@Hash", plan.ExpectedPreviewHash));
            if (completed != 1)
            {
                throw CommitError("PreviewStale", "The import batch changed during commit.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            db.Ado.CommitTran();
            return new(false, plan.CommitReceiptJson);
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    private sealed class CommittedAssetRow
    {
        public long GraphAssetId { get; set; }
        public long LatestRevisionId { get; set; }
        public long PlcProjectId { get; set; }
        public long GraphBlockId { get; set; }
        public long? InstanceDbBlockId { get; set; }
    }

    public async Task<LineUpCommittedAssetRevision?> LoadCommittedAssetRevisionAsync(
        long plcProjectId,
        long graphBlockId,
        CancellationToken cancellationToken)
    {
        var rows = await db.Ado.SqlQueryAsync<CommittedAssetRow>(
            """
            SELECT a."Id" AS "GraphAssetId", a."LatestRevisionId" AS "LatestRevisionId",
                   a."PlcProjectId" AS "PlcProjectId", a."CurrentPlcBlockId" AS "GraphBlockId",
                   NULL::bigint AS "InstanceDbBlockId"
            FROM "AI_LineUp_GraphAsset" a
            WHERE a."IsDeleted" = FALSE AND a."PlcProjectId" = @ProjectId AND a."CurrentPlcBlockId" = @GraphBlockId
            ORDER BY a."Id" DESC
            LIMIT 1
            """,
            new SugarParameter("@ProjectId", plcProjectId),
            new SugarParameter("@GraphBlockId", graphBlockId)).WaitAsync(cancellationToken);
        var row = rows.FirstOrDefault();
        return row is null || row.LatestRevisionId <= 0
            ? null
            : new(row.GraphAssetId, row.LatestRevisionId, row.PlcProjectId, row.GraphBlockId, null);
    }

    public async Task<long> EnsureGraphStationBindingAsync(
        LineUpGraphStationBindingEnsure request,
        CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var existingRows = await db.Ado.SqlQueryAsync<CommittedAssetRow>(
            """
            SELECT "Id" AS "GraphAssetId", COALESCE("PublishedRevisionId", 0) AS "LatestRevisionId",
                   0 AS "PlcProjectId", 0 AS "GraphBlockId", "InstanceDbBlockId" AS "InstanceDbBlockId"
            FROM "AI_LineUp_GraphStationBinding"
            WHERE "IsDeleted" = FALSE AND "GraphAssetId" = @AssetId AND "SubStationId" = @SubStationId
            ORDER BY "Id" DESC LIMIT 1
            """,
            new SugarParameter("@AssetId", request.GraphAssetId),
            new SugarParameter("@SubStationId", request.SubStationId)).WaitAsync(cancellationToken);
        var existing = existingRows.FirstOrDefault();
        if (existing is not null)
        {
            var updated = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_GraphStationBinding"
                SET "LineId" = @LineId, "ProcessId" = @ProcessId, "StationId" = @StationId, "DeviceId" = @DeviceId,
                    "InstanceDbBlockId" = @InstanceDbBlockId::bigint, "PublishedRevisionId" = @RevisionId,
                    "BindingStatus" = 'Unresolved', "RuntimeBindingJson" = NULL, "RuntimeValidationJson" = NULL,
                    "ValidationRevision" = NULL, "MonitoringEnabled" = FALSE, "LastValidatedAt" = NULL,
                    "ModifyTime" = @Now
                WHERE "Id" = @Id AND "IsDeleted" = FALSE
                """,
                new SugarParameter("@LineId", request.LineId),
                new SugarParameter("@ProcessId", request.ProcessId),
                new SugarParameter("@StationId", request.StationId),
                new SugarParameter("@DeviceId", request.DeviceId),
                new SugarParameter("@InstanceDbBlockId", request.InstanceDbBlockId.HasValue ? request.InstanceDbBlockId.Value : DBNull.Value),
                new SugarParameter("@RevisionId", request.RevisionId),
                new SugarParameter("@Now", now),
                new SugarParameter("@Id", existing.GraphAssetId)).WaitAsync(cancellationToken);
            if (updated != 1)
            {
                throw CommitError("BindingOwnerConflict", "The runtime GRAPH binding changed during commit.");
            }

            return existing.GraphAssetId;
        }

        var id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        var inserted = await db.Ado.ExecuteCommandAsync(
            """
            INSERT INTO "AI_LineUp_GraphStationBinding"
                ("Id", "GraphAssetId", "LineId", "ProcessId", "StationId", "SubStationId", "DeviceId",
                 "InstanceDbBlockId", "PublishedRevisionId", "BindingStatus", "RuntimeBindingJson",
                 "RuntimeValidationJson", "ValidationRevision", "MonitoringEnabled", "LastValidatedAt",
                 "IsDeleted", "CreaterId", "CreateTime")
            VALUES (@Id, @AssetId, @LineId, @ProcessId, @StationId, @SubStationId, @DeviceId,
                    @InstanceDbBlockId::bigint, @RevisionId, 'Unresolved', NULL, NULL, NULL, FALSE, NULL,
                    FALSE, 0, @Now)
            """,
            new SugarParameter("@Id", id),
            new SugarParameter("@AssetId", request.GraphAssetId),
            new SugarParameter("@LineId", request.LineId),
            new SugarParameter("@ProcessId", request.ProcessId),
            new SugarParameter("@StationId", request.StationId),
            new SugarParameter("@SubStationId", request.SubStationId),
            new SugarParameter("@DeviceId", request.DeviceId),
            new SugarParameter("@InstanceDbBlockId", request.InstanceDbBlockId.HasValue ? request.InstanceDbBlockId.Value : DBNull.Value),
            new SugarParameter("@RevisionId", request.RevisionId),
            new SugarParameter("@Now", now)).WaitAsync(cancellationToken);
        if (inserted != 1)
        {
            throw CommitError("BindingOwnerConflict", "The runtime GRAPH binding could not be created.");
        }

        return id;
    }

    public async Task<IReadOnlyList<LineUpActiveSubStationBindingLookup>> LoadActiveSubStationBindingsAsync(
        long lineId,
        long deviceId,
        IReadOnlyCollection<string> sequenceSourceKeys,
        CancellationToken cancellationToken)
    {
        if (lineId <= 0 || deviceId <= 0 || sequenceSourceKeys is null || sequenceSourceKeys.Count == 0)
        {
            return Array.Empty<LineUpActiveSubStationBindingLookup>();
        }

        var keyParams = sequenceSourceKeys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Select((value, index) => new SugarParameter($"@sk{index}", value))
            .ToArray();
        if (keyParams.Length == 0)
        {
            return Array.Empty<LineUpActiveSubStationBindingLookup>();
        }

        var keyList = string.Join(", ", keyParams.Select(p => p.ParameterName));
        var rows = await db.Ado.SqlQueryAsync<ActiveSubStationBindingRow>(
                $"""
                 SELECT b."Id" AS "BindingId", s."SourceKey" AS "SequenceSourceKey",
                        b."ProcessId", b."StationId", b."SubStationId"
                 FROM "AI_LineUp_SubStationGraphBinding" b
                 INNER JOIN "AI_LineUp_GraphSequence" s ON b."SequenceId" = s."Id"
                 WHERE b."IsDeleted" = FALSE AND s."IsDeleted" = FALSE
                   AND b."LineId" = @LineId AND b."DeviceId" = @DeviceId
                   AND s."SourceKey" IN ({keyList})
                 ORDER BY b."Id"
                 """,
                keyParams.Concat(
                [
                    new SugarParameter("@LineId", lineId),
                    new SugarParameter("@DeviceId", deviceId)
                ]).ToArray())
            .WaitAsync(cancellationToken);
        return rows.Select(x => new LineUpActiveSubStationBindingLookup(
                x.BindingId,
                x.SequenceSourceKey ?? string.Empty,
                x.ProcessId,
                x.StationId,
                x.SubStationId))
            .ToArray();
    }

    public async Task<bool> ReassignSubStationBindingHierarchyAsync(
        long bindingId,
        long processId,
        long stationId,
        long subStationId,
        int hierarchyDepth,
        CancellationToken cancellationToken)
    {
        // hierarchyDepth 决定更新到哪一级：1=仅工序；2=工序+工站；3=三级。
        // 未覆盖的层级保留原值——改上级时不清空下级绑定。
        var setClauses = new List<string> { "\"ProcessId\" = @ProcessId", "\"ModifyTime\" = @Now" };
        var parameters = new List<SugarParameter>
        {
            new("@ProcessId", processId),
            new("@Now", DateTime.Now),
            new("@BindingId", bindingId)
        };
        if (hierarchyDepth >= 2)
        {
            setClauses.Add("\"StationId\" = @StationId");
            parameters.Add(new SugarParameter("@StationId", stationId));
        }
        if (hierarchyDepth >= 3)
        {
            setClauses.Add("\"SubStationId\" = @SubStationId");
            parameters.Add(new SugarParameter("@SubStationId", subStationId));
        }

        var updated = await db.Ado.ExecuteCommandAsync(
                $"UPDATE \"AI_LineUp_SubStationGraphBinding\" SET {string.Join(", ", setClauses)} WHERE \"Id\" = @BindingId AND \"IsDeleted\" = FALSE",
                parameters.ToArray())
            .WaitAsync(cancellationToken);
        return updated == 1;
    }

    private sealed class ActiveSubStationBindingRow
    {
        public long BindingId { get; set; }
        public string? SequenceSourceKey { get; set; }
        public long ProcessId { get; set; }
        public long StationId { get; set; }
        public long SubStationId { get; set; }
    }

    private async Task LockCommitOwnersAsync(
        LineUpCommitPersistencePlan plan,
        IReadOnlyCollection<LineUpCommitSequenceWrite> sequences,
        CancellationToken cancellationToken)
    {
        var projectIds = plan.Documents.Select(x => x.PlcProjectId).Distinct().ToArray();
        var graphBlockIds = plan.Documents.Select(x => x.GraphBlockId).Distinct().ToArray();
        var instanceDbIds = plan.Documents.Where(x => x.InstanceDbBlockId is > 0)
            .Select(x => x.InstanceDbBlockId!.Value).Distinct().ToArray();
        var blockIds = graphBlockIds.Concat(instanceDbIds).Distinct().ToArray();
        var processIds = sequences.Select(x => x.ProcessId).Distinct().ToArray();
        var stationIds = sequences.Select(x => x.StationId).Distinct().ToArray();
        var subStationIds = sequences.Where(x => x.SubStationId is > 0)
            .Select(x => x.SubStationId!.Value).Distinct().ToArray();

        // 全部锁行 UPDATE 改手写：SqlSugar Updateable 在引号列（Basic_*/AI_LineUp_*）下生成非法 SQL（42601），
        // ai_plc_* 小写表虽然不走引号，但统一手写保持一致性
        var lockedLines = await db.Ado.ExecuteCommandAsync(
            """UPDATE "Basic_ProductionLine" SET "ModifyTime" = "ModifyTime" WHERE "IsDeleted" = FALSE AND "Id" = @Id""",
            new SugarParameter("@Id", plan.LineId));
        var lockedDevices = await db.Ado.ExecuteCommandAsync(
            """UPDATE "Basic_Device" SET "ModifyTime" = "ModifyTime" WHERE "IsDeleted" = FALSE AND "Id" = @Id AND "LineID" = @LineId""",
            new SugarParameter("@Id", plan.DeviceId), new SugarParameter("@LineId", plan.LineId));
        var projParams = BuildIdParams("lp", projectIds);
        var lockedProjects = await db.Ado.ExecuteCommandAsync(
            $"UPDATE ai_plc_project_new SET modifytime = modifytime WHERE isdeleted = FALSE AND id IN ({ParamList(projParams)})",
            projParams);
        if (lockedLines != 1 || lockedDevices != 1 || lockedProjects != projectIds.Length)
        {
            throw CommitError("GraphOwnerMissing", "The selected line, device, or PLC project is no longer available.");
        }

        if (graphBlockIds.Length > 0)
        {
            var blockParams = BuildIdParams("lb", blockIds);
            var lockedBlocks = await db.Ado.ExecuteCommandAsync(
                $"UPDATE ai_plc_block_new SET modifytime = modifytime WHERE isdeleted = FALSE AND id IN ({ParamList(blockParams)})",
                blockParams);
            if (lockedBlocks != blockIds.Length)
            {
                throw CommitError("GraphOwnerMissing", "A selected GRAPH or InstanceDB block is no longer available.");
            }

            var blocks = await db.Ado.SqlQueryAsync<BlockOwnerRow>(
                    $$"""SELECT id AS "Id", projectid AS "ProjectId", blocktype AS "BlockType", programminglanguage AS "ProgrammingLanguage" FROM ai_plc_block_new WHERE isdeleted = FALSE AND id IN ({{ParamList(blockParams)}})""",
                    blockParams)
                .WaitAsync(cancellationToken);
            foreach (var document in plan.Documents)
            {
                var graph = blocks.SingleOrDefault(x => x.Id == document.GraphBlockId);
                if (graph is null || graph.ProjectId != document.PlcProjectId ||
                    !string.Equals(graph.BlockType?.Trim(), "FB", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(graph.ProgrammingLanguage?.Trim(), "GRAPH", StringComparison.OrdinalIgnoreCase))
                {
                    throw CommitError("GraphOwnerMissing", "The selected GRAPH block owner or type changed.");
                }

                if (document.InstanceDbBlockId is > 0)
                {
                    var instanceDb = blocks.SingleOrDefault(x => x.Id == document.InstanceDbBlockId.Value);
                    if (instanceDb is null || instanceDb.ProjectId != document.PlcProjectId ||
                        !string.Equals(instanceDb.BlockType?.Trim(), "InstanceDB", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(instanceDb.ProgrammingLanguage?.Trim(), "DB", StringComparison.OrdinalIgnoreCase))
                    {
                        throw CommitError("GraphOwnerMissing", "The selected InstanceDB owner or type changed.");
                    }
                }
            }
        }

        var procParams = BuildIdParams("lc", processIds);
        var lockedProcesses = await db.Ado.ExecuteCommandAsync(
            """UPDATE "Basic_Process" SET "ModifyTime" = "ModifyTime" WHERE "IsDeleted" = FALSE AND "Id" IN (@@IDS@@)"""
                .Replace("@@IDS@@", ParamList(procParams)),
            procParams);

        if (stationIds.Length > 0)
        {
            var stParams = BuildIdParams("lw", stationIds);
            var lockedStations = await db.Ado.ExecuteCommandAsync(
                """UPDATE "Basic_WorkStation" SET "ModifyTime" = "ModifyTime" WHERE "IsDeleted" = FALSE AND "Id" IN (@@IDS@@)"""
                    .Replace("@@IDS@@", ParamList(stParams)),
                stParams);
            if (lockedStations != stationIds.Length)
            {
                throw CommitError("HierarchyOwnerMissing", "A selected work station is no longer available.");
            }
        }

        var lockedSubstations = 0;
        if (subStationIds.Length > 0)
        {
            var ssParams = BuildIdParams("ls", subStationIds);
            lockedSubstations = await db.Ado.ExecuteCommandAsync(
                """UPDATE "Basic_SubStation" SET "ModifyTime" = "ModifyTime" WHERE "IsDeleted" = FALSE AND "Id" IN (@@IDS@@)"""
                    .Replace("@@IDS@@", ParamList(ssParams)),
                ssParams);
        }
        if (lockedProcesses != processIds.Length || lockedSubstations != subStationIds.Length)
        {
            throw CommitError("HierarchyOwnerMissing", "A selected process or substation is no longer available.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<CommitHierarchyContext> LoadHierarchyContextAsync(
        LineUpCommitPersistencePlan plan,
        IReadOnlyCollection<LineUpCommitSequenceWrite> sequences,
        CancellationToken cancellationToken)
    {
        var lines = await db.Ado.SqlQueryAsync<LineUpProductionLineEntity>(
                """SELECT * FROM "Basic_ProductionLine" WHERE "IsDeleted" = FALSE AND "Id" = @Id""",
                new SugarParameter("@Id", plan.LineId))
            .WaitAsync(cancellationToken);
        var devices = await db.Ado.SqlQueryAsync<LineUpDeviceEntity>(
                """SELECT * FROM "Basic_Device" WHERE "IsDeleted" = FALSE AND "Id" = @Id AND "LineID" = @LineId""",
                new SugarParameter("@Id", plan.DeviceId), new SugarParameter("@LineId", plan.LineId))
            .WaitAsync(cancellationToken);
        var processIds = sequences.Select(x => x.ProcessId).Distinct().ToArray();
        var stationIds = sequences.Select(x => x.StationId).Distinct().ToArray();
        var subStationIds = sequences.Where(x => x.SubStationId is > 0)
            .Select(x => x.SubStationId!.Value).Distinct().ToArray();
        var procParams = BuildIdParams("hp", processIds);
        var processes = await db.Ado.SqlQueryAsync<LineUpProcessEntity>(
                """SELECT * FROM "Basic_Process" WHERE "IsDeleted" = FALSE AND "Id" IN (@@IDS@@)""".Replace("@@IDS@@", ParamList(procParams)),
                procParams)
            .WaitAsync(cancellationToken);
        var stParams = BuildIdParams("hw", stationIds);
        var stations = await db.Ado.SqlQueryAsync<LineUpWorkStationEntity>(
                """SELECT * FROM "Basic_WorkStation" WHERE "IsDeleted" = FALSE AND "Id" IN (@@IDS@@)""".Replace("@@IDS@@", ParamList(stParams)),
                stParams)
            .WaitAsync(cancellationToken);
        IReadOnlyList<LineUpSubStationEntity> substations;
        if (subStationIds.Length == 0)
        {
            substations = [];
        }
        else
        {
            var ssParams = BuildIdParams("hs", subStationIds);
            substations = await db.Ado.SqlQueryAsync<LineUpSubStationEntity>(
                    """SELECT * FROM "Basic_SubStation" WHERE "IsDeleted" = FALSE AND "Id" IN (@@IDS@@)""".Replace("@@IDS@@", ParamList(ssParams)),
                    ssParams)
                .WaitAsync(cancellationToken);
        }

        if (lines.Count != 1 || devices.Count != 1 || processes.Count != processIds.Length ||
            stations.Count != stationIds.Length || substations.Count != subStationIds.Length)
        {
            throw CommitError("HierarchyOwnerMissing", "The selected line, device, or hierarchy owner changed.");
        }

        var context = new CommitHierarchyContext(
            lines[0], devices[0],
            processes.ToDictionary(x => x.Id),
            stations.ToDictionary(x => x.Id),
            substations.ToDictionary(x => x.Id));
        foreach (var sequence in sequences)
        {
            context.Validate(sequence, plan.LineId, plan.DeviceId);
        }

        return context;
    }

    private async Task<long?> ResolveSubStationIdAsync(
        LineUpCommitPersistencePlan plan,
        LineUpCommitSequenceWrite sequence,
        CommitHierarchyContext hierarchy,
        CancellationToken cancellationToken)
    {
        if (sequence.SubStationId is > 0)
        {
            return sequence.SubStationId;
        }

        if (!string.Equals(sequence.CandidateKind, "ProposedSubStation", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sequence.ProposedSubStationCode) ||
            string.IsNullOrWhiteSpace(sequence.ProposedSubStationName))
        {
            throw CommitError("HierarchyCandidateInvalid", "Only a validated proposed substation may create hierarchy data.");
        }

        var code = sequence.ProposedSubStationCode.Trim();
        var normalizedCode = code.ToLowerInvariant();
        var matches = await db.Ado.SqlQueryAsync<LineUpSubStationEntity>(
                """
                SELECT * FROM "Basic_SubStation"
                WHERE "IsDeleted" = FALSE AND "ProductionLineID" = @LineId AND "WorkStationID" = @StationId
                  AND "PlcID" = @DeviceId AND "SubStationCode" IS NOT NULL
                  AND LOWER(TRIM("SubStationCode")) = @Code
                """,
                new SugarParameter("@LineId", plan.LineId),
                new SugarParameter("@StationId", sequence.StationId),
                new SugarParameter("@DeviceId", plan.DeviceId),
                new SugarParameter("@Code", normalizedCode))
            .WaitAsync(cancellationToken);
        if (matches.Count > 1)
        {
            throw CommitError("HierarchyCandidateAmbiguous", "The proposed substation code is no longer unique.");
        }

        if (matches.Count == 1)
        {
            var match = matches[0];
            if (match.ProcessID != sequence.ProcessId)
            {
                throw CommitError("HierarchyCandidateStale", "The proposed substation code now belongs to another process.");
            }

            return match.Id;
        }

        var process = hierarchy.Processes[sequence.ProcessId];
        var station = hierarchy.Stations[sequence.StationId];
        var entity = new LineUpSubStationEntity
        {
            ProcessID = process.Id,
            ProcessCode = process.Code,
            ProcessName = process.Name,
            WorkStationID = station.Id,
            WorkStationCode = station.Code,
            WorkStationName = station.Name,
            ProductionLineID = hierarchy.Line.Id,
            ProductionLineCode = hierarchy.Line.Code,
            ProductionLineName = hierarchy.Line.Name,
            PlcID = hierarchy.Device.Id,
            PlcName = hierarchy.Device.DeviceName,
            SubStationCode = code,
            SubStationName = sequence.ProposedSubStationName.Trim(),
            SubStationMode = "GRAPH",
            IsDeleted = false,
            CreateTime = plan.CommittedAt
        };
        RequireSingle(await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO "Basic_SubStation"
                    ("Id", "ProcessID", "ProcessCode", "ProcessName", "WorkStationID", "WorkStationCode",
                     "WorkStationName", "ProductionLineID", "ProductionLineCode", "ProductionLineName",
                     "PlcID", "PlcName", "SubStationCode", "SubStationName", "SubStationMode",
                     "IsDeleted", "CreateTime")
                VALUES (@Id, @ProcessID, @ProcessCode, @ProcessName, @WorkStationID, @WorkStationCode,
                        @WorkStationName, @ProductionLineID, @ProductionLineCode, @ProductionLineName,
                        @PlcID, @PlcName, @SubStationCode, @SubStationName, @SubStationMode,
                        FALSE, @CreateTime)
                """,
                new SugarParameter("@Id", entity.Id),
                new SugarParameter("@ProcessID", entity.ProcessID),
                new SugarParameter("@ProcessCode", (object?)entity.ProcessCode ?? DBNull.Value),
                new SugarParameter("@ProcessName", (object?)entity.ProcessName ?? DBNull.Value),
                new SugarParameter("@WorkStationID", entity.WorkStationID),
                new SugarParameter("@WorkStationCode", (object?)entity.WorkStationCode ?? DBNull.Value),
                new SugarParameter("@WorkStationName", (object?)entity.WorkStationName ?? DBNull.Value),
                new SugarParameter("@ProductionLineID", entity.ProductionLineID),
                new SugarParameter("@ProductionLineCode", (object?)entity.ProductionLineCode ?? DBNull.Value),
                new SugarParameter("@ProductionLineName", (object?)entity.ProductionLineName ?? DBNull.Value),
                new SugarParameter("@PlcID", entity.PlcID),
                new SugarParameter("@PlcName", (object?)entity.PlcName ?? DBNull.Value),
                new SugarParameter("@SubStationCode", entity.SubStationCode ?? (object)DBNull.Value),
                new SugarParameter("@SubStationName", entity.SubStationName ?? (object)DBNull.Value),
                new SugarParameter("@SubStationMode", entity.SubStationMode ?? (object)DBNull.Value),
                new SugarParameter("@CreateTime", plan.CommittedAt)),
            "HierarchyCandidateStale", "The proposed substation could not be created exactly once.");
        cancellationToken.ThrowIfCancellationRequested();
        return entity.Id;
    }

    private async Task<long> UpsertSequenceAsync(
        LineUpCommitDocumentWrite document,
        LineUpCommitSequenceWrite sequence,
        CancellationToken cancellationToken)
    {
        var matches = await db.Ado.SqlQueryAsync<LineUpGraphSequenceEntity>(
                """
                SELECT * FROM "AI_LineUp_GraphSequence"
                WHERE "IsDeleted" = FALSE AND "PlcProjectId" = @ProjectId AND "SourceKey" = @SourceKey
                """,
                new SugarParameter("@ProjectId", document.PlcProjectId),
                new SugarParameter("@SourceKey", sequence.SourceKey))
            .WaitAsync(cancellationToken);
        if (matches.Count > 1)
        {
            throw CommitError("DuplicateSourceKey", "The active Sequence source key is not unique.");
        }

        var now = DateTime.UtcNow;
        if (matches.Count == 1)
        {
            var current = matches[0];
            if (current.GraphBlockId != document.GraphBlockId)
            {
                // owner 不一致：仅当旧 owner 块仍存活时才是真冲突。
                // 项目重传替换块后，存量序列指向软删/消失的旧块——同 key 同图的新版本，
                // 按接管处理（下方 UPDATE 把 owner 刷成本次文档的块），否则预览/提交互相死锁。
                var oldOwnerAlive = await db.Ado.SqlQueryAsync<long>(
                        "SELECT id FROM ai_plc_block_new WHERE id = @Id AND isdeleted = FALSE",
                        new SugarParameter("@Id", current.GraphBlockId))
                    .WaitAsync(cancellationToken);
                if (oldOwnerAlive.Count > 0)
                {
                    throw CommitError("SourceKeyOwnerConflict", "The Sequence source key belongs to another GRAPH block.");
                }
            }

            var displayName = sequence.RequestedDisplayName ?? current.DisplayName;
            var updated = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_GraphSequence"
                SET "SourceName" = @SourceName, "DisplayName" = @DisplayName, "GraphBlockId" = @GraphBlockId,
                    "InstanceDbBlockId" = @InstanceDbBlockId::bigint, "SourceHash" = @SourceHash, "ModifyTime" = @Now
                WHERE "IsDeleted" = FALSE AND "Id" = @Id AND "PlcProjectId" = @ProjectId AND "GraphBlockId" = @MatchBlockId
                """,
                new SugarParameter("@SourceName", sequence.SourceName),
                new SugarParameter("@DisplayName", displayName ?? (object)DBNull.Value),
                new SugarParameter("@GraphBlockId", document.GraphBlockId),
                new SugarParameter("@InstanceDbBlockId", document.InstanceDbBlockId.HasValue ? document.InstanceDbBlockId.Value : DBNull.Value),
                new SugarParameter("@SourceHash", document.SourceHash),
                new SugarParameter("@Now", now),
                new SugarParameter("@Id", current.Id),
                new SugarParameter("@ProjectId", document.PlcProjectId),
                new SugarParameter("@MatchBlockId", current.GraphBlockId));
            RequireSingle(updated, "PreviewStale", "The selected Sequence changed during commit.");
            return current.Id;
        }

        var created = new LineUpGraphSequenceEntity
        {
            SourceKey = sequence.SourceKey,
            SourceName = sequence.SourceName,
            DisplayName = sequence.RequestedDisplayName ?? sequence.InitialDisplayName,
            PlcProjectId = document.PlcProjectId,
            GraphBlockId = document.GraphBlockId,
            InstanceDbBlockId = document.InstanceDbBlockId,
            SourceHash = document.SourceHash,
            IsDeleted = false,
            CreateTime = now
        };
        RequireSingle(await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO "AI_LineUp_GraphSequence"
                    ("Id", "SourceKey", "SourceName", "DisplayName", "PlcProjectId", "GraphBlockId",
                     "InstanceDbBlockId", "SourceHash", "IsDeleted", "CreateTime")
                VALUES (@Id, @SourceKey, @SourceName, @DisplayName, @PlcProjectId, @GraphBlockId,
                        @InstanceDbBlockId::bigint, @SourceHash, FALSE, @CreateTime)
                """,
                new SugarParameter("@Id", created.Id),
                new SugarParameter("@SourceKey", created.SourceKey),
                new SugarParameter("@SourceName", created.SourceName),
                new SugarParameter("@DisplayName", created.DisplayName ?? (object)DBNull.Value),
                new SugarParameter("@PlcProjectId", created.PlcProjectId),
                new SugarParameter("@GraphBlockId", created.GraphBlockId),
                new SugarParameter("@InstanceDbBlockId", created.InstanceDbBlockId.HasValue ? created.InstanceDbBlockId.Value : DBNull.Value),
                new SugarParameter("@SourceHash", created.SourceHash),
                new SugarParameter("@CreateTime", now)),
            "DuplicateSourceKey", "The Sequence could not be created exactly once.");
        cancellationToken.ThrowIfCancellationRequested();
        return created.Id;
    }

    private async Task UpsertSequenceChildrenAsync(
        long sequenceId,
        LineUpCommitSequenceWrite sequence,
        CancellationToken cancellationToken)
    {
        var existingSteps = await db.Ado.SqlQueryAsync<LineUpGraphStepEntity>(
                """SELECT * FROM "AI_LineUp_GraphStep" WHERE "IsDeleted" = FALSE AND "SequenceId" = @Sid""",
                new SugarParameter("@Sid", sequenceId))
            .WaitAsync(cancellationToken);
        var existingTransitions = await db.Ado.SqlQueryAsync<LineUpGraphTransitionEntity>(
                """SELECT * FROM "AI_LineUp_GraphTransition" WHERE "IsDeleted" = FALSE AND "SequenceId" = @Sid""",
                new SugarParameter("@Sid", sequenceId))
            .WaitAsync(cancellationToken);
        var duplicateNode = existingSteps.Select(x => x.SourceKey)
            .Concat(existingTransitions.Select(x => x.SourceKey))
            .GroupBy(x => x, StringComparer.Ordinal).FirstOrDefault(x => x.Count() > 1);
        if (duplicateNode is not null)
        {
            throw CommitError("DuplicateSourceKey", "An active node source key is not unique inside its Sequence.");
        }

        var nodeIds = new Dictionary<(string Kind, string Key), long>();
        foreach (var node in sequence.Nodes.OrderBy(x => x.SourceKey, StringComparer.Ordinal))
        {
            if (string.Equals(node.NodeKind, "Step", StringComparison.Ordinal))
            {
                var current = existingSteps.SingleOrDefault(x =>
                    string.Equals(x.SourceKey, node.SourceKey, StringComparison.Ordinal));
                var id = await UpsertStepAsync(sequenceId, node, current);
                nodeIds.Add(("Step", node.SourceKey), id);
            }
            else if (string.Equals(node.NodeKind, "Transition", StringComparison.Ordinal))
            {
                var current = existingTransitions.SingleOrDefault(x =>
                    string.Equals(x.SourceKey, node.SourceKey, StringComparison.Ordinal));
                var id = await UpsertTransitionAsync(sequenceId, node, current);
                nodeIds.Add(("Transition", node.SourceKey), id);
            }
            else
            {
                throw CommitError("GraphNodeKindInvalid", "Only Step and Transition nodes can be persisted.");
            }
        }

        await UpsertActionsAsync(sequenceId, sequence.Actions, nodeIds, cancellationToken);
        await UpsertEdgesAsync(sequenceId, sequence.Edges, nodeIds, cancellationToken);
        await SoftDeleteRemovedChildrenAsync(
            sequenceId, sequence, nodeIds, existingSteps, existingTransitions, cancellationToken);
    }

    private async Task<long> UpsertStepAsync(
        long sequenceId,
        LineUpCommitNodeWrite node,
        LineUpGraphStepEntity? current)
    {
        var now = DateTime.UtcNow;
        if (current is not null)
        {
            var updated = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_GraphStep"
                SET "SourceName" = @SourceName, "DisplayName" = @DisplayName, "StepNumber" = @StepNumber,
                    "IsInitial" = @IsInitial, "IsFinal" = @IsFinal, "Comment" = @Comment,
                    "MaximumStepTime" = @MaximumStepTime, "WarningTime" = @WarningTime, "ModifyTime" = @Now
                WHERE "IsDeleted" = FALSE AND "Id" = @Id AND "SequenceId" = @Sid
                """,
                new SugarParameter("@SourceName", node.SourceName),
                new SugarParameter("@DisplayName", node.RequestedDisplayName ?? current.DisplayName ?? (object)DBNull.Value),
                new SugarParameter("@StepNumber", node.Number),
                new SugarParameter("@IsInitial", node.IsInitial),
                new SugarParameter("@IsFinal", node.IsFinal),
                new SugarParameter("@Comment", node.Comment ?? (object)DBNull.Value),
                new SugarParameter("@MaximumStepTime", node.MaximumStepTime ?? (object)DBNull.Value),
                new SugarParameter("@WarningTime", node.WarningTime ?? (object)DBNull.Value),
                new SugarParameter("@Now", now),
                new SugarParameter("@Id", current.Id),
                new SugarParameter("@Sid", sequenceId));
            RequireSingle(updated, "PreviewStale", "A Step changed during commit.");
            return current.Id;
        }

        var created = new LineUpGraphStepEntity
        {
            SequenceId = sequenceId,
            SourceKey = node.SourceKey,
            SourceName = node.SourceName,
            DisplayName = node.RequestedDisplayName ?? node.InitialDisplayName,
            StepNumber = node.Number,
            IsInitial = node.IsInitial,
            IsFinal = node.IsFinal,
            IsSafety = false,
            Comment = node.Comment,
            MaximumStepTime = node.MaximumStepTime,
            WarningTime = node.WarningTime,
            IsDeleted = false,
            CreateTime = now
        };
        RequireSingle(await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO "AI_LineUp_GraphStep"
                    ("Id", "SequenceId", "SourceKey", "SourceName", "DisplayName", "StepNumber",
                     "IsInitial", "IsFinal", "IsSafety", "Comment", "MaximumStepTime", "WarningTime",
                     "IsDeleted", "CreateTime")
                VALUES (@Id, @Sid, @SourceKey, @SourceName, @DisplayName, @StepNumber,
                        @IsInitial, @IsFinal, @IsSafety, @Comment, @MaximumStepTime, @WarningTime,
                        FALSE, @CreateTime)
                """,
                new SugarParameter("@Id", created.Id),
                new SugarParameter("@Sid", created.SequenceId),
                new SugarParameter("@SourceKey", created.SourceKey),
                new SugarParameter("@SourceName", created.SourceName),
                new SugarParameter("@DisplayName", created.DisplayName ?? (object)DBNull.Value),
                new SugarParameter("@StepNumber", created.StepNumber),
                new SugarParameter("@IsInitial", created.IsInitial),
                new SugarParameter("@IsFinal", created.IsFinal),
                new SugarParameter("@IsSafety", created.IsSafety),
                new SugarParameter("@Comment", created.Comment ?? (object)DBNull.Value),
                new SugarParameter("@MaximumStepTime", created.MaximumStepTime ?? (object)DBNull.Value),
                new SugarParameter("@WarningTime", created.WarningTime ?? (object)DBNull.Value),
                new SugarParameter("@CreateTime", now)),
            "DuplicateSourceKey", "The Step could not be created exactly once.");
        return created.Id;
    }

    private async Task<long> UpsertTransitionAsync(
        long sequenceId,
        LineUpCommitNodeWrite node,
        LineUpGraphTransitionEntity? current)
    {
        var now = DateTime.UtcNow;
        if (current is not null)
        {
            var updated = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_GraphTransition"
                SET "SourceName" = @SourceName, "DisplayName" = @DisplayName, "TransitionNumber" = @TransitionNumber,
                    "IsJump" = @IsJump, "ProgrammingLanguage" = @ProgrammingLanguage, "IsMissing" = @IsMissing,
                    "ModifyTime" = @Now
                WHERE "IsDeleted" = FALSE AND "Id" = @Id AND "SequenceId" = @Sid
                """,
                new SugarParameter("@SourceName", node.SourceName),
                new SugarParameter("@DisplayName", node.RequestedDisplayName ?? current.DisplayName ?? (object)DBNull.Value),
                new SugarParameter("@TransitionNumber", node.Number),
                new SugarParameter("@IsJump", node.IsJump),
                new SugarParameter("@ProgrammingLanguage", node.ProgrammingLanguage ?? (object)DBNull.Value),
                new SugarParameter("@IsMissing", node.IsMissing),
                new SugarParameter("@Now", now),
                new SugarParameter("@Id", current.Id),
                new SugarParameter("@Sid", sequenceId));
            RequireSingle(updated, "PreviewStale", "A Transition changed during commit.");
            return current.Id;
        }

        var created = new LineUpGraphTransitionEntity
        {
            SequenceId = sequenceId,
            SourceKey = node.SourceKey,
            SourceName = node.SourceName,
            DisplayName = node.RequestedDisplayName ?? node.InitialDisplayName,
            TransitionNumber = node.Number,
            IsJump = node.IsJump,
            ProgrammingLanguage = node.ProgrammingLanguage,
            IsMissing = node.IsMissing,
            IsDeleted = false,
            CreateTime = now
        };
        RequireSingle(await db.Ado.ExecuteCommandAsync(
                """
                INSERT INTO "AI_LineUp_GraphTransition"
                    ("Id", "SequenceId", "SourceKey", "SourceName", "DisplayName", "TransitionNumber",
                     "IsJump", "ProgrammingLanguage", "IsMissing", "IsDeleted", "CreateTime")
                VALUES (@Id, @Sid, @SourceKey, @SourceName, @DisplayName, @TransitionNumber,
                        @IsJump, @ProgrammingLanguage, @IsMissing, FALSE, @CreateTime)
                """,
                new SugarParameter("@Id", created.Id),
                new SugarParameter("@Sid", created.SequenceId),
                new SugarParameter("@SourceKey", created.SourceKey),
                new SugarParameter("@SourceName", created.SourceName),
                new SugarParameter("@DisplayName", created.DisplayName ?? (object)DBNull.Value),
                new SugarParameter("@TransitionNumber", created.TransitionNumber),
                new SugarParameter("@IsJump", created.IsJump),
                new SugarParameter("@ProgrammingLanguage", created.ProgrammingLanguage ?? (object)DBNull.Value),
                new SugarParameter("@IsMissing", created.IsMissing),
                new SugarParameter("@CreateTime", now)),
            "DuplicateSourceKey", "The Transition could not be created exactly once.");
        return created.Id;
    }

    private async Task UpsertActionsAsync(
        long sequenceId,
        IReadOnlyList<LineUpCommitActionWrite> actions,
        IReadOnlyDictionary<(string Kind, string Key), long> nodeIds,
        CancellationToken cancellationToken)
    {
        var stepIds = nodeIds.Where(x => x.Key.Kind == "Step").Select(x => x.Value).ToArray();
        IReadOnlyList<LineUpGraphActionEntity> existing;
        if (stepIds.Length == 0)
        {
            existing = [];
        }
        else
        {
            var saParams = BuildIdParams("ua", stepIds);
            existing = await db.Ado.SqlQueryAsync<LineUpGraphActionEntity>(
                    """SELECT * FROM "AI_LineUp_GraphAction" WHERE "IsDeleted" = FALSE AND "StepId" IN (@@IDS@@)"""
                        .Replace("@@IDS@@", ParamList(saParams)),
                    saParams)
                .WaitAsync(cancellationToken);
        }
        foreach (var action in actions.OrderBy(x => x.SourceKey, StringComparer.Ordinal))
        {
            if (!nodeIds.TryGetValue(("Step", action.StepSourceKey), out var stepId))
            {
                throw CommitError("GraphActionOwnerInvalid", "An Action references a missing Step source key.");
            }

            var matches = existing.Where(x => x.StepId == stepId &&
                                               string.Equals(x.SourceKey, action.SourceKey, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length > 1)
            {
                throw CommitError("DuplicateSourceKey", "An active Action source key is not unique inside its Step.");
            }

            var now = DateTime.UtcNow;
            if (matches.Length == 1)
            {
                var current = matches[0];
                var updated = await db.Ado.ExecuteCommandAsync(
                    """
                    UPDATE "AI_LineUp_GraphAction"
                    SET "SourceName" = @SourceName, "DisplayName" = @DisplayName, "Qualifier" = @Qualifier,
                        "Ordinal" = @Ordinal, "TokensJson" = @TokensJson::jsonb, "ActionText" = @ActionText,
                        "ModifyTime" = @Now
                    WHERE "IsDeleted" = FALSE AND "Id" = @Id AND "StepId" = @StepId
                    """,
                    new SugarParameter("@SourceName", action.SourceName),
                    new SugarParameter("@DisplayName", action.RequestedDisplayName ?? current.DisplayName ?? (object)DBNull.Value),
                    new SugarParameter("@Qualifier", action.Qualifier ?? (object)DBNull.Value),
                    new SugarParameter("@Ordinal", action.Ordinal),
                    new SugarParameter("@TokensJson", JsonSerializer.Serialize(action.Tokens)),
                    new SugarParameter("@ActionText", action.ActionText ?? (object)DBNull.Value),
                    new SugarParameter("@Now", now),
                    new SugarParameter("@Id", current.Id),
                    new SugarParameter("@StepId", stepId));
                RequireSingle(updated, "PreviewStale", "An Action changed during commit.");
            }
            else
            {
                var created = new LineUpGraphActionEntity
                {
                    StepId = stepId,
                    SourceKey = action.SourceKey,
                    SourceName = action.SourceName,
                    DisplayName = action.RequestedDisplayName ?? action.InitialDisplayName,
                    Qualifier = action.Qualifier,
                    Ordinal = action.Ordinal,
                    TokensJson = JsonSerializer.Serialize(action.Tokens),
                    ActionText = action.ActionText,
                    IsDeleted = false,
                    CreateTime = now
                };
                RequireSingle(await db.Ado.ExecuteCommandAsync(
                        """
                        INSERT INTO "AI_LineUp_GraphAction"
                            ("Id", "StepId", "SourceKey", "SourceName", "DisplayName", "Qualifier",
                             "Ordinal", "TokensJson", "ActionText", "IsDeleted", "CreateTime")
                        VALUES (@Id, @StepId, @SourceKey, @SourceName, @DisplayName, @Qualifier,
                                @Ordinal, @TokensJson::jsonb, @ActionText, FALSE, @CreateTime)
                        """,
                        new SugarParameter("@Id", created.Id),
                        new SugarParameter("@StepId", created.StepId),
                        new SugarParameter("@SourceKey", created.SourceKey),
                        new SugarParameter("@SourceName", created.SourceName),
                        new SugarParameter("@DisplayName", created.DisplayName ?? (object)DBNull.Value),
                        new SugarParameter("@Qualifier", created.Qualifier ?? (object)DBNull.Value),
                        new SugarParameter("@Ordinal", created.Ordinal),
                        new SugarParameter("@TokensJson", created.TokensJson ?? (object)DBNull.Value),
                        new SugarParameter("@ActionText", created.ActionText ?? (object)DBNull.Value),
                        new SugarParameter("@CreateTime", now)),
                    "DuplicateSourceKey", "The Action could not be created exactly once.");
            }
        }
    }

    private async Task UpsertEdgesAsync(
        long sequenceId,
        IReadOnlyList<LineUpCommitEdgeWrite> edges,
        IReadOnlyDictionary<(string Kind, string Key), long> nodeIds,
        CancellationToken cancellationToken)
    {
        var existing = await db.Ado.SqlQueryAsync<LineUpGraphEdgeEntity>(
                """SELECT * FROM "AI_LineUp_GraphEdge" WHERE "IsDeleted" = FALSE AND "SequenceId" = @Sid""",
                new SugarParameter("@Sid", sequenceId))
            .WaitAsync(cancellationToken);
        foreach (var edge in edges.OrderBy(x => x.SourceKey, StringComparer.Ordinal))
        {
            if (!nodeIds.TryGetValue((edge.FromNodeKind, edge.FromSourceKey), out var fromId) ||
                !nodeIds.TryGetValue((edge.ToNodeKind, edge.ToSourceKey), out var toId))
            {
                throw CommitError("GraphEdgeEndpointInvalid", "An Edge references a missing node source key.");
            }

            var matches = existing.Where(x =>
                    string.Equals(x.SourceKey, edge.SourceKey, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length > 1)
            {
                throw CommitError("DuplicateSourceKey", "An active Edge source key is not unique inside its Sequence.");
            }

            var now = DateTime.UtcNow;
            if (matches.Length == 1)
            {
                var current = matches[0];
                var updated = await db.Ado.ExecuteCommandAsync(
                    """
                    UPDATE "AI_LineUp_GraphEdge"
                    SET "FromNodeType" = @FromNodeType, "FromNodeId" = @FromNodeId,
                        "ToNodeType" = @ToNodeType, "ToNodeId" = @ToNodeId,
                        "EdgeKind" = @EdgeKind, "BranchOrder" = @BranchOrder::int, "ModifyTime" = @Now
                    WHERE "IsDeleted" = FALSE AND "Id" = @Id AND "SequenceId" = @Sid
                    """,
                    new SugarParameter("@FromNodeType", edge.FromNodeKind),
                    new SugarParameter("@FromNodeId", fromId),
                    new SugarParameter("@ToNodeType", edge.ToNodeKind),
                    new SugarParameter("@ToNodeId", toId),
                    new SugarParameter("@EdgeKind", edge.EdgeKind),
                    new SugarParameter("@BranchOrder", edge.BranchOrder.HasValue ? edge.BranchOrder.Value : DBNull.Value),
                    new SugarParameter("@Now", now),
                    new SugarParameter("@Id", current.Id),
                    new SugarParameter("@Sid", sequenceId));
                RequireSingle(updated, "PreviewStale", "An Edge changed during commit.");
            }
            else
            {
                var created = new LineUpGraphEdgeEntity
                {
                    SequenceId = sequenceId,
                    SourceKey = edge.SourceKey,
                    FromNodeType = edge.FromNodeKind,
                    FromNodeId = fromId,
                    ToNodeType = edge.ToNodeKind,
                    ToNodeId = toId,
                    EdgeKind = edge.EdgeKind,
                    BranchOrder = edge.BranchOrder,
                    IsDeleted = false,
                    CreateTime = now
                };
                RequireSingle(await db.Ado.ExecuteCommandAsync(
                        """
                        INSERT INTO "AI_LineUp_GraphEdge"
                            ("Id", "SequenceId", "SourceKey", "FromNodeType", "FromNodeId",
                             "ToNodeType", "ToNodeId", "EdgeKind", "BranchOrder", "IsDeleted", "CreateTime")
                        VALUES (@Id, @Sid, @SourceKey, @FromNodeType, @FromNodeId,
                                @ToNodeType, @ToNodeId, @EdgeKind, @BranchOrder::int, FALSE, @CreateTime)
                        """,
                        new SugarParameter("@Id", created.Id),
                        new SugarParameter("@Sid", created.SequenceId),
                        new SugarParameter("@SourceKey", created.SourceKey),
                        new SugarParameter("@FromNodeType", created.FromNodeType),
                        new SugarParameter("@FromNodeId", created.FromNodeId),
                        new SugarParameter("@ToNodeType", created.ToNodeType),
                        new SugarParameter("@ToNodeId", created.ToNodeId),
                        new SugarParameter("@EdgeKind", created.EdgeKind),
                        new SugarParameter("@BranchOrder", created.BranchOrder.HasValue ? created.BranchOrder.Value : DBNull.Value),
                        new SugarParameter("@CreateTime", now)),
                    "DuplicateSourceKey", "The Edge could not be created exactly once.");
            }
        }
    }

    private async Task UpsertBindingAsync(
        LineUpCommitPersistencePlan plan,
        LineUpCommitDocumentWrite document,
        LineUpCommitSequenceWrite sequence,
        long sequenceId,
        long? subStationId,
        CancellationToken cancellationToken)
    {
        var matches = await db.Ado.SqlQueryAsync<LineUpSubStationGraphBindingEntity>(
                """
                SELECT * FROM "AI_LineUp_SubStationGraphBinding"
                WHERE "IsDeleted" = FALSE AND "LineId" = @LineId AND "DeviceId" = @DeviceId AND "SequenceId" = @Sid
                """,
                new SugarParameter("@LineId", plan.LineId),
                new SugarParameter("@DeviceId", plan.DeviceId),
                new SugarParameter("@Sid", sequenceId))
            .WaitAsync(cancellationToken);
        if (matches.Count > 1)
        {
            throw CommitError("BindingOwnerConflict", "The active line/device/Sequence binding is not unique.");
        }

        var now = DateTime.UtcNow;
        if (matches.Count == 1)
        {
            var current = matches[0];
            var updated = await db.Ado.ExecuteCommandAsync(
                """
                UPDATE "AI_LineUp_SubStationGraphBinding"
                SET "ProcessId" = @ProcessId::bigint, "StationId" = @StationId::bigint,
                    "SubStationId" = @SubStationId::bigint, "PlcProjectId" = @PlcProjectId,
                    "GraphBlockId" = @GraphBlockId, "InstanceDbBlockId" = @InstanceDbBlockId::bigint,
                    "BindingStatus" = @BindingStatus, "ModifyTime" = @Now
                WHERE "IsDeleted" = FALSE AND "Id" = @Id AND "LineId" = @LineId
                  AND "DeviceId" = @DeviceId AND "SequenceId" = @Sid
                """,
                new SugarParameter("@ProcessId", sequence.ProcessId),
                new SugarParameter("@StationId", sequence.StationId),
                new SugarParameter("@SubStationId", subStationId.HasValue ? subStationId.Value : DBNull.Value),
                new SugarParameter("@PlcProjectId", document.PlcProjectId),
                new SugarParameter("@GraphBlockId", document.GraphBlockId),
                new SugarParameter("@InstanceDbBlockId", document.InstanceDbBlockId.HasValue ? document.InstanceDbBlockId.Value : DBNull.Value),
                new SugarParameter("@BindingStatus", sequence.BindingStatus),
                new SugarParameter("@Now", now),
                new SugarParameter("@Id", current.Id),
                new SugarParameter("@LineId", plan.LineId),
                new SugarParameter("@DeviceId", plan.DeviceId),
                new SugarParameter("@Sid", sequenceId));
            RequireSingle(updated, "PreviewStale", "The binding changed during commit.");
        }
        else
        {
            var created = new LineUpSubStationGraphBindingEntity
            {
                LineId = plan.LineId,
                ProcessId = sequence.ProcessId,
                StationId = sequence.StationId,
                SubStationId = subStationId,
                DeviceId = plan.DeviceId,
                PlcProjectId = document.PlcProjectId,
                SequenceId = sequenceId,
                GraphBlockId = document.GraphBlockId,
                InstanceDbBlockId = document.InstanceDbBlockId,
                RuntimeBindingJson = null,
                BindingStatus = sequence.BindingStatus,
                LastValidatedAt = null,
                IsDeleted = false,
                CreateTime = now
            };
            RequireSingle(await db.Ado.ExecuteCommandAsync(
                    """
                    INSERT INTO "AI_LineUp_SubStationGraphBinding"
                        ("Id", "LineId", "ProcessId", "StationId", "SubStationId", "DeviceId",
                         "PlcProjectId", "SequenceId", "GraphBlockId", "InstanceDbBlockId",
                         "RuntimeBindingJson", "BindingStatus", "LastValidatedAt", "IsDeleted", "CreateTime")
                    VALUES (@Id, @LineId, @ProcessId::bigint, @StationId::bigint, @SubStationId::bigint, @DeviceId,
                            @PlcProjectId, @Sid, @GraphBlockId, @InstanceDbBlockId::bigint,
                            NULL::jsonb, @BindingStatus, NULL, FALSE, @CreateTime)
                    """,
                    new SugarParameter("@Id", created.Id),
                    new SugarParameter("@LineId", created.LineId),
                    new SugarParameter("@ProcessId", created.ProcessId),
                    new SugarParameter("@StationId", created.StationId),
                    new SugarParameter("@SubStationId", created.SubStationId.HasValue ? created.SubStationId.Value : DBNull.Value),
                    new SugarParameter("@DeviceId", created.DeviceId),
                    new SugarParameter("@PlcProjectId", created.PlcProjectId),
                    new SugarParameter("@Sid", created.SequenceId),
                    new SugarParameter("@GraphBlockId", created.GraphBlockId),
                    new SugarParameter("@InstanceDbBlockId", created.InstanceDbBlockId.HasValue ? created.InstanceDbBlockId.Value : DBNull.Value),
                    new SugarParameter("@BindingStatus", created.BindingStatus),
                    new SugarParameter("@CreateTime", now)),
                "BindingOwnerConflict", "The binding could not be created exactly once.");
        }
    }

    private async Task SoftDeleteRemovedChildrenAsync(
        long sequenceId,
        LineUpCommitSequenceWrite sequence,
        IReadOnlyDictionary<(string Kind, string Key), long> nodeIds,
        IReadOnlyList<LineUpGraphStepEntity> existingSteps,
        IReadOnlyList<LineUpGraphTransitionEntity> existingTransitions,
        CancellationToken cancellationToken)
    {
        var desiredSteps = sequence.Nodes.Where(x => x.NodeKind == "Step")
            .Select(x => x.SourceKey).ToHashSet(StringComparer.Ordinal);
        var desiredTransitions = sequence.Nodes.Where(x => x.NodeKind == "Transition")
            .Select(x => x.SourceKey).ToHashSet(StringComparer.Ordinal);
        var removedStepIds = existingSteps.Where(x => !desiredSteps.Contains(x.SourceKey))
            .Select(x => x.Id).ToArray();
        var removedTransitionIds = existingTransitions.Where(x => !desiredTransitions.Contains(x.SourceKey))
            .Select(x => x.Id).ToArray();
        var desiredEdges = sequence.Edges.Select(x => x.SourceKey).ToHashSet(StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        if (removedStepIds.Length > 0)
        {
            await MarkDeletedByColumnAsync("AI_LineUp_GraphAction", "StepId", removedStepIds, now);
            await MarkDeletedByIdsAsync("AI_LineUp_GraphStep", removedStepIds, now);
        }
        if (removedTransitionIds.Length > 0)
        {
            await MarkDeletedByIdsAsync("AI_LineUp_GraphTransition", removedTransitionIds, now);
        }

        foreach (var step in nodeIds.Where(x => x.Key.Kind == "Step"))
        {
            var desiredActions = sequence.Actions
                .Where(x => string.Equals(x.StepSourceKey, step.Key.Key, StringComparison.Ordinal))
                .Select(x => x.SourceKey)
                .ToHashSet(StringComparer.Ordinal);
            var storedActions = await db.Ado.SqlQueryAsync<IdSourceRow>(
                    """SELECT "Id" AS "Id", "SourceKey" AS "SourceKey" FROM "AI_LineUp_GraphAction" WHERE "IsDeleted" = FALSE AND "StepId" = @StepId""",
                    new SugarParameter("@StepId", step.Value))
                .WaitAsync(cancellationToken);
            var ids = storedActions.Where(x => !desiredActions.Contains(x.SourceKey ?? string.Empty)).Select(x => x.Id).ToArray();
            if (ids.Length > 0) await MarkDeletedByIdsAsync("AI_LineUp_GraphAction", ids, now);
        }

        var staleEdgeIds = await db.Ado.SqlQueryAsync<IdSourceRow>(
                """SELECT "Id" AS "Id", "SourceKey" AS "SourceKey" FROM "AI_LineUp_GraphEdge" WHERE "IsDeleted" = FALSE AND "SequenceId" = @Sid""",
                new SugarParameter("@Sid", sequenceId))
            .WaitAsync(cancellationToken);
        var edgeIds = staleEdgeIds.Where(x => !desiredEdges.Contains(x.SourceKey ?? string.Empty)).Select(x => x.Id).ToArray();
        if (edgeIds.Length > 0) await MarkDeletedByIdsAsync("AI_LineUp_GraphEdge", edgeIds, now);
    }

    private async Task SoftDeleteRemovedSequencesAsync(
        IReadOnlyList<LineUpCommitDocumentWrite> documents,
        IReadOnlyDictionary<(long ProjectId, long GraphBlockId, string SourceKey), long> desired,
        CancellationToken cancellationToken)
    {
        foreach (var owner in documents.Select(x => (x.PlcProjectId, x.GraphBlockId)).Distinct())
        {
            var existing = await db.Ado.SqlQueryAsync<LineUpGraphSequenceEntity>(
                    """
                    SELECT * FROM "AI_LineUp_GraphSequence"
                    WHERE "IsDeleted" = FALSE AND "PlcProjectId" = @ProjectId AND "GraphBlockId" = @GraphBlockId
                    """,
                    new SugarParameter("@ProjectId", owner.PlcProjectId),
                    new SugarParameter("@GraphBlockId", owner.GraphBlockId))
                .WaitAsync(cancellationToken);
            var removed = existing.Where(x => !desired.ContainsKey((owner.PlcProjectId, owner.GraphBlockId, x.SourceKey)))
                .Select(x => x.Id).ToArray();
            foreach (var sequenceId in removed)
            {
                await SoftDeleteSequenceAsync(sequenceId, cancellationToken);
            }
        }
    }

    private async Task SoftDeleteSequenceAsync(long sequenceId, CancellationToken cancellationToken)
    {
        var stepIds = await db.Ado.SqlQueryAsync<long>(
                """SELECT "Id" FROM "AI_LineUp_GraphStep" WHERE "IsDeleted" = FALSE AND "SequenceId" = @Sid""",
                new SugarParameter("@Sid", sequenceId))
            .WaitAsync(cancellationToken);
        var now = DateTime.UtcNow;
        if (stepIds.Count > 0)
        {
            await MarkDeletedByColumnAsync("AI_LineUp_GraphAction", "StepId", stepIds.ToArray(), now);
        }

        await MarkDeletedBySequenceAsync("AI_LineUp_GraphStep", sequenceId, now);
        await MarkDeletedBySequenceAsync("AI_LineUp_GraphTransition", sequenceId, now);
        await MarkDeletedBySequenceAsync("AI_LineUp_GraphEdge", sequenceId, now);
        await MarkDeletedBySequenceAsync("AI_LineUp_SubStationGraphBinding", sequenceId, now);
        await MarkDeletedByIdsAsync("AI_LineUp_GraphSequence", [sequenceId], now);
    }

    private sealed record CommitHierarchyContext(
        LineUpProductionLineEntity Line,
        LineUpDeviceEntity Device,
        IReadOnlyDictionary<long, LineUpProcessEntity> Processes,
        IReadOnlyDictionary<long, LineUpWorkStationEntity> Stations,
        IReadOnlyDictionary<long, LineUpSubStationEntity> SubStations)
    {
        public void Validate(LineUpCommitSequenceWrite sequence, long lineId, long deviceId)
        {
            if (!Processes.TryGetValue(sequence.ProcessId, out var process) ||
                process.ProductionLineId != lineId ||
                !Stations.TryGetValue(sequence.StationId, out var station) ||
                station.ProcessId != process.Id || station.ProductionLineId != lineId)
            {
                throw CommitError("HierarchyCandidateStale", "The selected process/work-station relationship changed.");
            }

            if (sequence.SubStationId is > 0 &&
                (!SubStations.TryGetValue(sequence.SubStationId.Value, out var substation) ||
                 substation.ProcessID != process.Id || substation.WorkStationID != station.Id ||
                 substation.ProductionLineID != lineId || substation.PlcID != deviceId))
            {
                throw CommitError("HierarchyCandidateStale", "The selected substation relationship changed.");
            }
        }
    }

    private const char Quote = '"';

    private sealed class IdSourceRow
    {
        public long Id { get; set; }
        public string? SourceKey { get; set; }
    }

    private Task<int> MarkDeletedByIdsAsync(string table, long[] ids, DateTime now)
    {
        var ps = BuildIdParams("md", ids);
        var sql = $"UPDATE {Quote}{table}{Quote} SET {Quote}IsDeleted{Quote} = TRUE, {Quote}ModifyTime{Quote} = @Now" +
                  $" WHERE {Quote}IsDeleted{Quote} = FALSE AND {Quote}Id{Quote} IN ({ParamList(ps)})";
        return db.Ado.ExecuteCommandAsync(sql, ps.Append(new SugarParameter("@Now", now)).ToArray());
    }

    private Task<int> MarkDeletedByColumnAsync(string table, string column, long[] values, DateTime now)
    {
        var ps = BuildIdParams("mc", values);
        var sql = $"UPDATE {Quote}{table}{Quote} SET {Quote}IsDeleted{Quote} = TRUE, {Quote}ModifyTime{Quote} = @Now" +
                  $" WHERE {Quote}IsDeleted{Quote} = FALSE AND {Quote}{column}{Quote} IN ({ParamList(ps)})";
        return db.Ado.ExecuteCommandAsync(sql, ps.Append(new SugarParameter("@Now", now)).ToArray());
    }

    private Task<int> MarkDeletedBySequenceAsync(string table, long sequenceId, DateTime now)
    {
        var sql = $"UPDATE {Quote}{table}{Quote} SET {Quote}IsDeleted{Quote} = TRUE, {Quote}ModifyTime{Quote} = @Now" +
                  $" WHERE {Quote}IsDeleted{Quote} = FALSE AND {Quote}SequenceId{Quote} = @Sid";
        return db.Ado.ExecuteCommandAsync(sql, new SugarParameter("@Sid", sequenceId), new SugarParameter("@Now", now));
    }

    private static SugarParameter[] BuildIdParams(string prefix, IReadOnlyCollection<long> ids) =>
        ids.Select((value, index) => new SugarParameter($"@{prefix}{index}", value)).ToArray();

    private static string ParamList(IEnumerable<SugarParameter> ps) =>
        string.Join(", ", ps.Select(p => p.ParameterName));

    private sealed class BlockOwnerRow
    {
        public long Id { get; set; }
        public long? ProjectId { get; set; }
        public string? BlockType { get; set; }
        public string? ProgrammingLanguage { get; set; }
    }

    private static void RequireSingle(int affected, string code, string message)
    {
        if (affected != 1)
        {
            throw CommitError(code, message);
        }
    }

    private static LineUpCommitPersistenceException CommitError(string code, string message) => new(code, message);

    private ISugarQueryable<LineUpImportBatchEntity> BatchQuery() =>
        db.Queryable<LineUpImportBatchEntity>();

    private static LineUpExistingGraphState EmptyGraphState()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        return new(hash, [], [], [], [], []);
    }

}
