#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;

namespace HSJM.AI.AppLayer.LineUp.Runtime;

public sealed class LineUpMonitorBindingSource : ILineUpMonitorBindingSource, IScopedAutoInject
{
    private readonly ISqlSugarClient db;
    private readonly ILineUpGraphRuntimeRepository runtimeRepository;
    private readonly ILineUpPlcMetadataRepository metadataRepository;
    private readonly LineUpPublishedGraphProjector projector;
    private readonly LineUpReadPlanCompiler compiler;
    private readonly LineUpMonitorRuntimeGate runtimeGate;
    private readonly LineUpMonitorBindingFactory factory;
    private readonly LineUpTagCollectionRuleCache collectionRuleCache;
    private readonly ILineUpTagCollectionRuleRepository collectionRuleRepository;
    private readonly LineUpMonitorBindingCache bindingCache;

    public LineUpMonitorBindingSource(
        ISqlSugarClient db,
        ILineUpGraphRuntimeRepository runtimeRepository,
        ILineUpPlcMetadataRepository metadataRepository,
        LineUpPublishedGraphProjector projector,
        LineUpReadPlanCompiler compiler,
        LineUpMonitorRuntimeGate runtimeGate,
        LineUpTagCollectionRuleCache collectionRuleCache,
        ILineUpTagCollectionRuleRepository collectionRuleRepository,
        LineUpMonitorBindingCache bindingCache)
    {
        this.db = db;
        this.runtimeRepository = runtimeRepository;
        this.metadataRepository = metadataRepository;
        this.projector = projector;
        this.compiler = compiler;
        this.runtimeGate = runtimeGate;
        this.collectionRuleCache = collectionRuleCache;
        this.collectionRuleRepository = collectionRuleRepository;
        this.bindingCache = bindingCache;
        factory = new(projector, compiler);
    }

    public async Task<IReadOnlyList<LineUpMonitorBindingSummaryDto>> LoadCatalogAsync(
        long lineId,
        long deviceId,
        long stationId,
        CancellationToken cancellationToken)
    {
        var subStationId = await ResolveSubStationAsync(lineId, deviceId, stationId, cancellationToken);
        return subStationId is > 0
            ? await LoadCatalogBySubStationAsync(subStationId.Value, cancellationToken)
            : [];
    }

    public async Task<long?> ResolveSubStationAsync(
        long lineId,
        long deviceId,
        long stationId,
        CancellationToken cancellationToken)
    {
        if (lineId <= 0 || deviceId <= 0 || stationId <= 0) return null;
        var parameters = new SugarParameter[]
        {
            new("@LineId", lineId, global::System.Data.DbType.Int64),
            new("@DeviceId", deviceId, global::System.Data.DbType.Int64),
            new("@StationId", stationId, global::System.Data.DbType.Int64)
        };
        var rows = await db.Ado.SqlQueryAsync<SubStationIdRow>(
            """
            SELECT DISTINCT "SubStationId"
            FROM "AI_LineUp_GraphStationBinding"
            WHERE "IsDeleted" = FALSE
              AND "LineId" = @LineId
              AND "DeviceId" = @DeviceId
              AND "StationId" = @StationId
              AND "SubStationId" IS NOT NULL
              AND "PublishedRevisionId" IS NOT NULL
            ORDER BY "SubStationId"
            """,
            (object)parameters,
            cancellationToken);
        return rows.Count == 1 ? rows[0].SubStationId : null;
    }

    public async Task<IReadOnlyList<LineUpMonitorBindingSummaryDto>> LoadCatalogBySubStationAsync(
        long subStationId,
        CancellationToken cancellationToken)
    {
        if (subStationId <= 0) return [];
        var rows = await db.Ado.SqlQueryAsync<BindingIdRow>(
                """
                SELECT "Id", "SubStationId"
                FROM "AI_LineUp_GraphStationBinding"
                WHERE "IsDeleted" = FALSE
                  AND "SubStationId" = @SubStationId
                  AND "PublishedRevisionId" IS NOT NULL
                ORDER BY "Id"
                """,
                (object)new SugarParameter[]
                {
                    new("@SubStationId", subStationId, global::System.Data.DbType.Int64)
                },
                cancellationToken);
        var result = new List<LineUpMonitorBindingSummaryDto>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = await runtimeRepository.LoadContextAsync(row.Id, cancellationToken);
            if (context is null) continue;
            LineUpPublishedGraphDefinition definition;
            try
            {
                definition = projector.Project(context);
            }
            catch (LineUpPublishedGraphProjectionException)
            {
                continue;
            }

            result.Add(new(
                context.Binding.Id.ToString(),
                context.Asset.Id.ToString(),
                context.PublishedRevision.Id.ToString(),
                context.Binding.LineId.ToString(),
                context.Binding.DeviceId.ToString(),
                context.Binding.ProcessId.ToString(),
                context.Binding.StationId.ToString(),
                subStationId.ToString(CultureInfo.InvariantCulture),
                context.Asset.PlcProjectId.ToString(CultureInfo.InvariantCulture),
                context.Asset.SourceBlockName,
                context.Binding.BindingStatus,
                context.Binding.MonitoringEnabled,
                context.Binding.LastValidatedAt is not null &&
                !string.IsNullOrWhiteSpace(context.Binding.RuntimeBindingJson),
                definition.Sequences
                    .OrderBy(sequence => sequence.SequenceSourceKey, StringComparer.Ordinal)
                    .Select(sequence => new LineUpMonitorSequenceSummaryDto(
                        sequence.SequenceSourceKey,
                        sequence.DisplayName,
                        sequence.Steps.Count,
                        sequence.Transitions.Count,
                        string.Empty))
                    .ToImmutableArray()));
        }
        return result;
    }

    public async Task<IReadOnlyList<LineUpMonitorBinding>> LoadEnabledAsync(
        IReadOnlyCollection<long> bindingIds,
        CancellationToken cancellationToken)
    {
        if (!runtimeGate.IsEnabled) return [];
        // 2026-09-21 页面驱动监控：bindingIds = 当前有活跃 SignalR 订阅者的绑定。
        // 过滤放在 SQL 里而不是拉全量再筛——未被订阅的绑定连轻量行都不该读出来。
        var watched = bindingIds?.Where(id => id > 0).Distinct().ToArray() ?? [];
        if (watched.Length == 0) return [];
        // 2026-09-14 热路径轻量化：轻量行（不含 RuntimeBindingJson/RuntimeValidationJson 两个大 jsonb，
        // 实测平均 ~400KB/行）先过绑定缓存，命中直接返回构建产物；仅未命中（首载/行版本变更/
        // 屏蔽集变更/30~44s 元数据复查到期）才走 LoadAsync 全量重建。详见 LineUpMonitorBindingCache。
        // 2026-10-08 修复：SqlSugar 会把数组参数内联成逗号列表，= ANY(@BindingIds) 实际下发
        // 成 ANY(1,2,...) 触发 42601 syntax error at or near ","（整轮监控周期失败）。
        // 改为逐个参数的 IN (...)，与 PLC_ProjectApp 级联的参数拼法同风格。
        var idParams = watched
            .Select((value, index) => new SugarParameter($"@b{index}", value))
            .ToArray();
        var idList = string.Join(", ", idParams.Select(p => p.ParameterName));
        var rows = await db.Ado.SqlQueryAsync<EnabledBindingRow>(
                $"""
                SELECT b."Id", b."SubStationId", b."PublishedRevisionId", b."ValidationRevision",
                       a."PlcProjectId"
                FROM "AI_LineUp_GraphStationBinding" b
                INNER JOIN "AI_LineUp_GraphAsset" a
                  ON a."Id" = b."GraphAssetId" AND a."IsDeleted" = FALSE
                WHERE b."Id" IN ({idList})
                  AND b."IsDeleted" = FALSE
                  AND b."MonitoringEnabled" = TRUE
                  AND b."BindingStatus" = 'Resolved'
                  AND b."LastValidatedAt" IS NOT NULL
                  AND b."PublishedRevisionId" IS NOT NULL
                  AND b."InstanceDbBlockId" IS NOT NULL
                  AND b."RuntimeBindingJson" IS NOT NULL
                  AND b."RuntimeValidationJson" IS NOT NULL
                  AND b."ValidationRevision" IS NOT NULL
                  AND b."SubStationId" IS NOT NULL
                ORDER BY b."Id"
                """,
                idParams)
            .WaitAsync(cancellationToken);
        var result = new List<LineUpMonitorBinding>(rows.Count);
        var activeIds = new List<long>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Id <= 0 || row.SubStationId <= 0) continue;
            activeIds.Add(row.Id);
            var binding = await ResolveAsync(row, cancellationToken);
            if (binding is not null) result.Add(binding);
        }
        // 停用/软删的绑定条目即时清理（轻量行已含全部启用绑定，PruneExcept 防滞留泄漏）。
        bindingCache.PruneExcept(activeIds);
        return result;
    }

    public async Task<LineUpMonitorBinding?> LoadByBindingAsync(
        long bindingId,
        CancellationToken cancellationToken)
    {
        if (!runtimeGate.IsEnabled || bindingId <= 0) return null;
        var rows = await db.Ado.SqlQueryAsync<EnabledBindingRow>(
                """
                SELECT b."Id", b."SubStationId", b."PublishedRevisionId", b."ValidationRevision",
                       a."PlcProjectId"
                FROM "AI_LineUp_GraphStationBinding" b
                INNER JOIN "AI_LineUp_GraphAsset" a
                  ON a."Id" = b."GraphAssetId" AND a."IsDeleted" = FALSE
                WHERE b."Id" = @BindingId
                  AND b."IsDeleted" = FALSE
                  AND b."MonitoringEnabled" = TRUE
                  AND b."BindingStatus" = 'Resolved'
                  AND b."LastValidatedAt" IS NOT NULL
                  AND b."PublishedRevisionId" IS NOT NULL
                  AND b."InstanceDbBlockId" IS NOT NULL
                  AND b."RuntimeBindingJson" IS NOT NULL
                  AND b."RuntimeValidationJson" IS NOT NULL
                  AND b."ValidationRevision" IS NOT NULL
                  AND b."SubStationId" IS NOT NULL
                """,
                new SugarParameter("@BindingId", bindingId))
            .WaitAsync(cancellationToken);
        return rows.Count == 1 ? await ResolveAsync(rows[0], cancellationToken) : null;
    }

    /// <summary>缓存命中即返回；未命中走完整 LoadAsync（重路径含 IsCurrentAsync 元数据时效校验）并回填条目。</summary>
    private async Task<LineUpMonitorBinding?> ResolveAsync(
        EnabledBindingRow row,
        CancellationToken cancellationToken)
    {
        var cached = bindingCache.TryGet(
            row.Id, row.ValidationRevision, row.PublishedRevisionId,
            collectionRuleCache.ContentVersion(row.PlcProjectId), DateTimeOffset.UtcNow);
        if (cached is not null) return cached;

        var binding = await LoadAsync(row.Id, row.SubStationId, requireEnabled: true, cancellationToken);
        if (binding is null) return null;
        // 轻量行的版本在 LoadAsync 期间可能已被并发重写——以查询时版本回填，下一轮比对失配自然重建（自愈）。
        bindingCache.Set(
            binding, row.ValidationRevision, row.PublishedRevisionId, row.PlcProjectId,
            collectionRuleCache.ContentVersion(row.PlcProjectId), DateTimeOffset.UtcNow);
        return binding;
    }

    public async Task<LineUpMonitorEventPageDto> LoadEventsAsync(
        LineUpMonitorKey key,
        long? beforeEventId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!key.IsValid)
        {
            return new(ImmutableArray<LineUpMonitorEventDto>.Empty, null, false);
        }

        var binding = await runtimeRepository.LoadBindingAsync(key.BindingId, cancellationToken);
        if (binding?.PublishedRevisionId is not > 0)
        {
            return new(ImmutableArray<LineUpMonitorEventDto>.Empty, null, false);
        }

        var take = Math.Clamp(limit, 1, 200);
        var before = beforeEventId is > 0 ? beforeEventId.Value : long.MaxValue;
        var rows = await db.Ado.SqlQueryAsync<LineUpRuntimeEventEntity>(
                """
                SELECT "Id", "BindingId", "SequenceId", "SequenceSourceKey", "OwnerSourceKey",
                       "EventType", "PreviousState", "CurrentState", "OccurredAtUtc", "DurationMs",
                       "Quality", "QualityCode", "DefinitionRevision", "GatewayStreamId",
                       "GatewaySequence", "EventOrdinal", "DetailsJson"
                FROM "AI_LineUp_RuntimeEvent"
                WHERE "IsDeleted" = FALSE
                  AND "BindingId" = @BindingId
                  AND "SequenceSourceKey" = @SequenceSourceKey
                  AND "Id" < @BeforeEventId
                ORDER BY "Id" DESC
                LIMIT @Take
                """,
                new SugarParameter("@BindingId", key.BindingId),
                new SugarParameter("@SequenceSourceKey", key.SequenceSourceKey),
                new SugarParameter("@BeforeEventId", before),
                new SugarParameter("@Take", take + 1))
            .WaitAsync(cancellationToken);
        var hasMore = rows.Count > take;
        var page = rows.Take(take).Select(row => new LineUpMonitorEventDto(
                row.Id.ToString(),
                row.BindingId.ToString(),
                row.SequenceSourceKey ?? string.Empty,
                row.OwnerSourceKey,
                row.EventType,
                row.PreviousState,
                row.CurrentState,
                LineUpMonitorWireProjection.UtcTimestamp(
                    new DateTimeOffset(DateTime.SpecifyKind(row.OccurredAtUtc, DateTimeKind.Utc))),
                LineUpMonitorWireProjection.DecimalInteger(row.DurationMs),
                row.Quality ?? string.Empty,
                row.QualityCode ?? string.Empty,
                row.DefinitionRevision,
                row.GatewayStreamId,
                row.GatewaySequence.ToString(),
                row.EventOrdinal,
                row.DetailsJson))
            .ToImmutableArray();
        return new(page, hasMore && page.Length > 0 ? page[^1].Id : null, hasMore);
    }

    public async Task PersistAsync(
        LineUpMonitorBinding runtimeBinding,
        ImmutableArray<LineUpRuntimeEvent> events,
        ImmutableArray<LineUpRuntimeValue> samples,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entities = events.Select(ToEntity).ToList();
        if (!samples.IsDefaultOrEmpty)
        {
            var sampleSequence = samples.Max(sample => sample.ReceivedTimestamp.UtcTicks);
            var ordinal = 0;
            foreach (var sample in samples.OrderBy(sample => sample.CanonicalKey, StringComparer.Ordinal))
            {
                if (!runtimeBinding.ValueSequenceOwners.TryGetValue(sample.SourceKey, out var sequenceSourceKey) ||
                    string.IsNullOrWhiteSpace(sequenceSourceKey))
                {
                    continue;
                }
                entities.Add(new()
                {
                    BindingId = runtimeBinding.BindingId,
                    SequenceId = null,
                    SequenceSourceKey = sequenceSourceKey,
                    OwnerSourceKey = sample.OwnerSourceKey,
                    EventType = "SampleCaptured",
                    CurrentState = sample.Quality.ToString(),
                    OccurredAtUtc = sample.ReceivedTimestamp.UtcDateTime,
                    Quality = sample.Quality.ToString(),
                    QualityCode = sample.ErrorCode,
                    DefinitionRevision = runtimeBinding.DefinitionRevision,
                    GatewayStreamId = $"hsjm-samples:{runtimeBinding.ReadPlan.BindingRevision}",
                    GatewaySequence = sampleSequence,
                    EventOrdinal = ordinal++,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        sample.CanonicalKey,
                        sample.ValueKind,
                        sample.Value,
                        sample.SourceTimestamp
                    }),
                    CreateTime = DateTime.UtcNow,
                    ModifyTime = DateTime.UtcNow
                });
            }
        }
        if (entities.Count == 0) return;
        foreach (var batch in entities.Chunk(100))
        {
            await InsertEventsAsync(batch, cancellationToken);
        }
    }

    private async Task<LineUpMonitorBinding?> LoadAsync(
        long bindingId,
        long subStationId,
        bool requireEnabled,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0 || subStationId <= 0) return null;
        var status = await runtimeRepository.LoadBindingAsync(bindingId, cancellationToken);
        if (status is null || status.PublishedRevisionId is not > 0 ||
            !string.Equals(status.BindingStatus, "Resolved", StringComparison.Ordinal) ||
            status.LastValidatedAt is null || string.IsNullOrWhiteSpace(status.RuntimeBindingJson) ||
            string.IsNullOrWhiteSpace(status.RuntimeValidationJson) ||
            string.IsNullOrWhiteSpace(status.ValidationRevision) ||
            (requireEnabled && !status.MonitoringEnabled))
        {
            return null;
        }
        var context = await runtimeRepository.LoadContextAsync(bindingId, cancellationToken);
        if (context is null)
        {
            // 2026-09-10（审计触发器实证）：PUT 开启后 1 秒内即被此处下线——LoadContextAsync 的四表 JOIN
            // 在网络/连接池瞬态故障时返回空，被当成"硬漂移"清空绑定（RuntimeBindingJson 置 NULL），
            // 用户侧表现=监控永远开不住。真正的结构性删除（项目/资产级联）由 LineUpProjectCascadeApp
            // 挂靠传递软删绑定行，无需监控服务代劳下线——这里同样降级为跳过+日志。
            Console.WriteLine(
                "[LineUpMonitor] 绑定上下文加载失败（可能瞬态），本轮跳过采样 binding=" + status.Id +
                "（绑定保持，不重置）");
            return null;
        }
        if (context.Binding.PublishedRevisionId != status.PublishedRevisionId ||
            !string.Equals(context.Binding.RuntimeBindingJson, status.RuntimeBindingJson, StringComparison.Ordinal) ||
            !string.Equals(context.Binding.ValidationRevision, status.ValidationRevision, StringComparison.Ordinal) ||
            !LineUpMonitorRuntimeMetadataValidator.TryReadDocument(status.RuntimeBindingJson, out var document) ||
            !string.Equals(document!.DefinitionRevision, status.ValidationRevision, StringComparison.Ordinal))
        {
            // 2026-09-10：行内快照自洽性异常（并发写入中途/json 超限读取失败等瞬态）不再 Invalidate 下线，
            // 与下方元数据时效分支同策略：跳过本轮采样并留日志，下一轮自然重试。
            Console.WriteLine(
                "[LineUpMonitor] 绑定快照读取异常，本轮跳过采样 binding=" + status.Id +
                " status=" + status.BindingStatus + "（绑定保持，不重置）");
            return null;
        }
        // 2026-09-12 采集开关：取项目级屏蔽变量集（缓存 30s，写操作主动失效）。
        var excludedVariablePaths = await collectionRuleCache.GetOrLoadAsync(
            context.Asset.PlcProjectId, collectionRuleRepository, cancellationToken);
        var binding = factory.Build(context, excludedVariablePaths, out var buildFailure);
        if (binding is null)
        {
            // 2026-09-10：factory 构建失败此前完全静默（编译计划 issue/步对齐/转移匹配任一不满即 null），
            // 采样侧与 Definition 404 均无法定位。现每个否决出口带出具体原因（buildFailure）。
            Console.WriteLine(
                "[LineUpMonitor] 运行时绑定构建失败，本轮跳过采样 binding=" +
                status.Id + " 原因=" + (buildFailure ?? "未知") + "（绑定保持，不重置）");
            return null;
        }
        var metadataMismatch = await LineUpMonitorRuntimeMetadataValidator.IsCurrentAsync(
            context, document!, metadataRepository, cancellationToken);
        if (metadataMismatch is null) return binding with { SubStationId = subStationId };

        // 2026-09-10：元数据时效校验不一致（快照 tag/issue 与现场重算有差异）不再 Invalidate 下线绑定。
        // 早前行为把所有开启监控的绑定在数秒内全部 Reset（含刚三连验证通过的），用户侧表现=监控永远开不住。
        // 现降级为本轮跳过采样（行保持 Resolved/Enabled，下一轮自动重试），仅日志留痕；
        // 硬漂移（上方 context/文档加载失败分支）仍走 InvalidateCurrentAsync 真正下线。
        Console.WriteLine(
            "[LineUpMonitor] 元数据时效校验不一致，本轮跳过采样 binding=" + status.Id +
            " 首个差异=" + metadataMismatch + "（绑定保持，不重置）");
        return null;
    }

    private async Task InvalidateCurrentAsync(
        LineUpGraphRuntimeBindingStatusData status,
        CancellationToken cancellationToken)
    {
        await runtimeRepository.InvalidateAsync(
            status.Id,
            status.PublishedRevisionId!.Value,
            status.ValidationRevision!,
            cancellationToken);
        compiler.Invalidate(status.Id);
        bindingCache.Invalidate(status.Id);
    }

    private static LineUpRuntimeEventEntity ToEntity(LineUpRuntimeEvent runtimeEvent) =>
        new()
        {
            BindingId = runtimeEvent.BindingId,
            SequenceId = null,
            SequenceSourceKey = runtimeEvent.SequenceSourceKey,
            OwnerSourceKey = runtimeEvent.OwnerSourceKey,
            EventType = runtimeEvent.EventType.ToString(),
            PreviousState = runtimeEvent.PreviousState,
            CurrentState = runtimeEvent.CurrentState,
            OccurredAtUtc = runtimeEvent.OccurredAtUtc.UtcDateTime,
            DurationMs = runtimeEvent.DurationMs,
            Quality = runtimeEvent.Quality?.ToString(),
            QualityCode = runtimeEvent.QualityCode,
            DefinitionRevision = runtimeEvent.DefinitionRevision,
            GatewayStreamId = runtimeEvent.GatewayStreamId,
            GatewaySequence = runtimeEvent.GatewaySequence,
            EventOrdinal = runtimeEvent.EventOrdinal,
            DetailsJson = string.IsNullOrWhiteSpace(runtimeEvent.DetailsJson) ? "{}" : runtimeEvent.DetailsJson,
            CreateTime = DateTime.UtcNow,
            ModifyTime = DateTime.UtcNow
        };

    private async Task InsertEventsAsync(
        IReadOnlyList<LineUpRuntimeEventEntity> entities,
        CancellationToken cancellationToken)
    {
        var values = new List<string>(entities.Count);
        var parameters = new List<SugarParameter>(entities.Count * 19);
        for (var index = 0; index < entities.Count; index++)
        {
            var entity = entities[index];
            var suffix = index.ToString();
            values.Add($"(@Id{suffix}, @BindingId{suffix}, @SequenceId{suffix}, @SequenceSourceKey{suffix}, " +
                       $"@OwnerSourceKey{suffix}, @EventType{suffix}, @PreviousState{suffix}, @CurrentState{suffix}, " +
                       $"CAST(@OccurredAtUtc{suffix} AS TIMESTAMPTZ), @DurationMs{suffix}, " +
                       $"@Quality{suffix}, @QualityCode{suffix}, " +
                       $"@DefinitionRevision{suffix}, @GatewayStreamId{suffix}, @GatewaySequence{suffix}, " +
                       $"@EventOrdinal{suffix}, CAST(@DetailsJson{suffix} AS JSONB), FALSE, " +
                       $"CAST(@CreateTime{suffix} AS TIMESTAMP), CAST(@ModifyTime{suffix} AS TIMESTAMP))");
            parameters.Add(Long($"@Id{suffix}", entity.Id));
            parameters.Add(Long($"@BindingId{suffix}", entity.BindingId));
            parameters.Add(NullableLong($"@SequenceId{suffix}", entity.SequenceId));
            parameters.Add(Text($"@SequenceSourceKey{suffix}", entity.SequenceSourceKey));
            parameters.Add(Text($"@OwnerSourceKey{suffix}", entity.OwnerSourceKey));
            parameters.Add(Text($"@EventType{suffix}", entity.EventType));
            parameters.Add(Text($"@PreviousState{suffix}", entity.PreviousState));
            parameters.Add(Text($"@CurrentState{suffix}", entity.CurrentState));
            parameters.Add(UtcTimestampText($"@OccurredAtUtc{suffix}", entity.OccurredAtUtc));
            parameters.Add(NullableLong($"@DurationMs{suffix}", entity.DurationMs));
            parameters.Add(Text($"@Quality{suffix}", entity.Quality));
            parameters.Add(Text($"@QualityCode{suffix}", entity.QualityCode));
            parameters.Add(Text($"@DefinitionRevision{suffix}", entity.DefinitionRevision));
            parameters.Add(Text($"@GatewayStreamId{suffix}", entity.GatewayStreamId));
            parameters.Add(Long($"@GatewaySequence{suffix}", entity.GatewaySequence));
            parameters.Add(new SugarParameter(
                $"@EventOrdinal{suffix}",
                entity.EventOrdinal,
                global::System.Data.DbType.Int32));
            parameters.Add(Text($"@DetailsJson{suffix}", entity.DetailsJson));
            parameters.Add(TimestampText($"@CreateTime{suffix}", entity.CreateTime ?? DateTime.UtcNow));
            parameters.Add(TimestampText($"@ModifyTime{suffix}", entity.ModifyTime ?? DateTime.UtcNow));
        }

        var sql =
            """
            INSERT INTO "AI_LineUp_RuntimeEvent"
                ("Id", "BindingId", "SequenceId", "SequenceSourceKey", "OwnerSourceKey", "EventType",
                 "PreviousState", "CurrentState", "OccurredAtUtc", "DurationMs", "Quality", "QualityCode",
                 "DefinitionRevision", "GatewayStreamId", "GatewaySequence", "EventOrdinal", "DetailsJson",
                 "IsDeleted", "CreateTime", "ModifyTime")
            VALUES
            """ + string.Join(",\n", values) +
            """

            ON CONFLICT ("BindingId", "GatewayStreamId", "GatewaySequence", "EventOrdinal")
            WHERE "IsDeleted" = FALSE
            DO NOTHING
            """;
        await db.Ado.ExecuteCommandAsync(sql, parameters.ToArray()).WaitAsync(cancellationToken);
    }

    private static SugarParameter Long(string name, long value) =>
        new(name, value, global::System.Data.DbType.Int64);

    private static SugarParameter NullableLong(string name, long? value) =>
        new(name, value, global::System.Data.DbType.Int64);

    private static SugarParameter Text(string name, string? value) =>
        new(name, value, global::System.Data.DbType.String);

    private static SugarParameter UtcTimestampText(string name, DateTime value) =>
        Text(name, DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));

    private static SugarParameter TimestampText(string name, DateTime value) =>
        Text(name, DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));

    private sealed class BindingIdRow
    {
        public long Id { get; set; }
        public long SubStationId { get; set; }
    }

    /// <summary>热路径轻量行：仅标识与版本列（无 jsonb 大字段），供缓存比对与按需重建。</summary>
    private sealed class EnabledBindingRow
    {
        public long Id { get; set; }
        public long SubStationId { get; set; }
        public long PublishedRevisionId { get; set; }
        public string ValidationRevision { get; set; } = string.Empty;
        public long PlcProjectId { get; set; }
    }

    private sealed class SubStationIdRow
    {
        public long? SubStationId { get; set; }
    }
}

internal static class LineUpMonitorRuntimeMetadataValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };

    internal static bool TryReadDocument(string json, out LineUpRuntimeBindingDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > LineUpRuntimeBindingApp.MaximumRuntimeBindingBytes)
            return false;
        try
        {
            document = JsonSerializer.Deserialize<LineUpRuntimeBindingDocument>(json, JsonOptions);
            return document is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>返回 null 表示元数据时效一致；否则返回首个差异描述（2026-09-10 诊断增强）。</summary>
    internal static async Task<string?> IsCurrentAsync(
        LineUpGraphRuntimeContext context,
        LineUpRuntimeBindingDocument document,
        ILineUpPlcMetadataRepository metadataRepository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(metadataRepository);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var stepTags = document.Tags
                .Where(tag => string.Equals(tag.Role, "StepExecution", StringComparison.Ordinal))
                .OrderBy(tag => tag.VariablePath, StringComparer.Ordinal)
                .ToArray();
            var instance = await metadataRepository.LoadVariableDataAsync(
                context.Asset.PlcProjectId,
                context.InstanceDb.Id,
                stepTags.Select(tag => tag.VariablePath ?? string.Empty).ToArray(),
                cancellationToken);
            var instanceBlocks = instance.Blocks.Where(block =>
                    block.Id == context.InstanceDb.Id &&
                    block.ProjectId == context.Asset.PlcProjectId &&
                    string.Equals(block.BlockName?.Trim(), context.InstanceDb.BlockName?.Trim(), StringComparison.Ordinal) &&
                    string.Equals(block.ProgrammingLanguage?.Trim(), "DB", StringComparison.Ordinal) &&
                    string.Equals(block.BlockType?.Trim(), "InstanceDB", StringComparison.Ordinal))
                .ToArray();
            if (instanceBlocks.Length != 1)
                return "实例DB块身份不匹配（现场加载未返回唯一匹配块）";
            foreach (var tag in stepTags)
            {
                if (tag.ResolvedBlockId != context.InstanceDb.Id)
                    return $"步tag块ID不一致 path={tag.VariablePath} doc={tag.ResolvedBlockId} 期望={context.InstanceDb.Id}";
                if (!string.Equals(tag.ResolvedBlockName?.Trim(), context.InstanceDb.BlockName?.Trim(), StringComparison.Ordinal))
                    return $"步tag块名不一致 path={tag.VariablePath} doc={tag.ResolvedBlockName}";
                if (!HasExactSingleInterface(instance.Interfaces, tag))
                    return $"步tag接口非唯一命中 path={tag.VariablePath}";
            }

            var networkTags = document.Tags
                .Where(tag => !string.Equals(tag.Role, "StepExecution", StringComparison.Ordinal))
                .ToArray();
            if (document.ParserSchemaVersion < 3) return networkTags.Length == 0 ? null : "PSV<3 但存在网络 tag";
            var definition = new LineUpPublishedGraphProjector().Project(context);
            var globalParts = definition.Sequences
                .SelectMany(sequence => sequence.Steps.Concat(sequence.Transitions)
                    .SelectMany(owner => owner.LogicNetworks.SelectMany(network => network.Parts
                        .Where(part => string.Equals(part.PartName, "Access", StringComparison.Ordinal) &&
                                       string.Equals(part.Scope, "GlobalVariable", StringComparison.Ordinal) &&
                                       part.SymbolPath.Count >= 2 &&
                                       part.SymbolPath.All(value => !string.IsNullOrWhiteSpace(value)))
                        .Select(part => new PreviewGlobalPart(sequence, owner, network, part)))))
                .OrderBy(item => item.Sequence.SequenceSourceKey, StringComparer.Ordinal)
                .ThenBy(item => item.Owner.SourceKey, StringComparer.Ordinal)
                .ThenBy(item => item.Network.SourceKey, StringComparer.Ordinal)
                .ThenBy(item => item.Part.SourceKey, StringComparer.Ordinal)
                .ToArray();
            if (globalParts.Length == 0) return networkTags.Length == 0 ? null : "无全局变量部件但存在网络 tag";

            var requests = globalParts.Select(item => new LineUpGraphSymbolLookupRequest(
                    item.Part.SymbolPath[0].Trim(),
                    string.Join('.', item.Part.SymbolPath.Skip(1).Select(value => value.Trim()))))
                .Distinct()
                .ToArray();
            var lookup = await metadataRepository.LoadGraphSymbolDataAsync(
                context.Asset.PlcProjectId, requests, cancellationToken);
            foreach (var item in globalParts)
            {
                var blockName = item.Part.SymbolPath[0].Trim();
                var variablePath = string.Join('.', item.Part.SymbolPath.Skip(1).Select(value => value.Trim()));
                var results = lookup.Symbols.Where(result =>
                        string.Equals(result.BlockName, blockName, StringComparison.Ordinal) &&
                        string.Equals(result.VariablePath, variablePath, StringComparison.Ordinal))
                    .ToArray();
                var current = ResolveGraphOutcome(
                    context.Asset.PlcProjectId,
                    blockName,
                    variablePath,
                    results,
                    LineUpNetworkSymbolFallback.InferAccessDataType(item.Network, item.Part));
                var tags = networkTags.Where(tag =>
                        string.Equals(tag.PartSourceKey, item.Part.SourceKey, StringComparison.Ordinal))
                    .ToArray();
                var issues = document.StaticIssues.Where(issue =>
                        string.Equals(issue.PartSourceKey, item.Part.SourceKey, StringComparison.Ordinal))
                    .ToArray();
                if (current.IssueCode is not null)
                {
                    if (tags.Length != 0)
                        return $"现场有issue但doc留了tag part={item.Part.SourceKey} issue={current.IssueCode}";
                    // 2026-09-11：容忍码（结构性告警屏蔽集，见 BuildAsync 过滤）不落库 doc——
                    // 跳过 doc 一致性核对。否则屏蔽生效后每轮都判"issue码缺失"跳过采样，监控快照停更。
                    if (LineUpRuntimeBindingApp.ToleratedNetworkWarningCodes.Contains(current.IssueCode))
                        continue;
                    // 2026-09-10：写入器对同一部件分层落多条 issue（InterfaceMissing + OperandMetadataMissing 同挂），
                    // 复查器现场只重算一个码——由"恰好一条"放宽为"包含现场码且该条目 Warning/属主/网络键匹配"，
                    // 其余派生 issue 容忍。否则所有含未解析全局变量的绑定永远卡在 issue数量不一致（docIssues=2）。
                    var matched = issues.FirstOrDefault(issue =>
                        string.Equals(issue.Code, current.IssueCode, StringComparison.Ordinal));
                    if (matched is null)
                        return $"issue码缺失 part={item.Part.SourceKey} 现场={current.IssueCode} doc={string.Join(",", issues.Select(issue => issue.Code))}";
                    if (!string.Equals(matched.Severity, "Warning", StringComparison.Ordinal))
                        return $"issue严重度不一致 part={item.Part.SourceKey} doc={matched.Severity}";
                    if (!string.Equals(matched.OwnerSourceKey, item.Owner.SourceKey, StringComparison.Ordinal))
                        return $"issue属主不一致 part={item.Part.SourceKey}";
                    if (!string.Equals(matched.NetworkSourceKey, item.Network.SourceKey, StringComparison.Ordinal))
                        return $"issue网络键不一致 part={item.Part.SourceKey}";
                    continue;
                }

                // 2026-09-10：现场解析成功时 doc 允许 tag 与 Warning issue 共存——写入器对类型不匹配等
                // 情况会留 OperandTypeMismatch 等留痕 issue（与 tag 不互斥），与 ValidStaticMaterial 同口径。
                if (tags.Length != 1)
                    return $"tag数量不一致 part={item.Part.SourceKey} docTags={tags.Length}";
                if (current.Block is null || current.Interface is null)
                    return $"现场符号解析为空 part={item.Part.SourceKey}";
                var tag = tags[0];
                if (!string.Equals(tag.SequenceSourceKey, item.Sequence.SequenceSourceKey, StringComparison.Ordinal))
                    return $"tag顺控键不一致 part={item.Part.SourceKey}";
                if (!string.Equals(tag.Role, RuntimeRole(item.Network.Role), StringComparison.Ordinal))
                    return $"tag角色不一致 part={item.Part.SourceKey} doc={tag.Role} 期望={RuntimeRole(item.Network.Role)}";
                if (!string.Equals(tag.OwnerSourceKey, item.Owner.SourceKey, StringComparison.Ordinal))
                    return $"tag属主不一致 part={item.Part.SourceKey}";
                if (!string.Equals(tag.NetworkSourceKey, item.Network.SourceKey, StringComparison.Ordinal))
                    return $"tag网络键不一致 part={item.Part.SourceKey}";
                if (!string.Equals(tag.Scope, "GlobalVariable", StringComparison.Ordinal))
                    return $"tag作用域不一致 part={item.Part.SourceKey} doc={tag.Scope}";
                if (tag.ResolvedBlockId != current.Block.Id)
                    return $"tag块ID不一致 part={item.Part.SourceKey} doc={tag.ResolvedBlockId} 现场={current.Block.Id}";
                if (!string.Equals(tag.ResolvedBlockName, blockName, StringComparison.Ordinal))
                    return $"tag块名不一致 part={item.Part.SourceKey} doc={tag.ResolvedBlockName} 现场={blockName}";
                if (tag.ResolvedBlockInterfaceId != current.Interface.Id)
                    return $"tag接口ID不一致 part={item.Part.SourceKey} doc={tag.ResolvedBlockInterfaceId} 现场={current.Interface.Id}";
                // 2026-09-11：回退绑定后 doc 的变量路径/规范路径 = 现场回退实际绑定路径（数组展开形态可与部件路径不同）
                var boundPath = current.BoundVariablePath ?? variablePath;
                if (!string.Equals(tag.VariablePath, boundPath, StringComparison.Ordinal))
                    return $"tag变量路径不一致 part={item.Part.SourceKey} doc={tag.VariablePath} 现场={boundPath}";
                if (!string.Equals(tag.CanonicalPlcPath, $"{blockName}.{boundPath}", StringComparison.Ordinal))
                    return $"tag规范路径不一致 part={item.Part.SourceKey} doc={tag.CanonicalPlcPath}";
                if (!string.Equals(tag.DataType, current.Interface.DataType?.Trim(), StringComparison.Ordinal))
                    return $"tag数据类型不一致 part={item.Part.SourceKey} doc={tag.DataType} 现场={current.Interface.DataType?.Trim()}";
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return "时效校验内部异常: " + error.GetType().Name + " - " + error.Message;
        }
    }

    private static CurrentGraphOutcome ResolveGraphOutcome(
        long plcProjectId,
        string blockName,
        string variablePath,
        IReadOnlyList<LineUpGraphSymbolLookupResult> results,
        string? inferredLeafDataType = null)
    {
        if (results.Count == 0)
        {
            // 2026-09-11 跨 PLC 通信块归外部引用（与生成侧 BindNetworkPartsAsync 同口径）
            return new(LineUpNetworkSymbolFallback.IsExternalReferenceBlockName(blockName)
                ? "RuntimeNetworkExternalReference"
                : "RuntimeNetworkBlockMissing", null, null);
        }
        if (results.Count != 1) return new("RuntimeNetworkBlockAmbiguous", null, null);
        var candidates = results[0].Blocks.Where(block =>
                    block.ProjectId == plcProjectId &&
                    string.Equals(block.BlockName?.Trim(), blockName, StringComparison.Ordinal))
            .ToArray();
        var blocks = candidates.Where(IsSupportedNamedDb).OrderBy(block => block.Id).ToArray();
        if (blocks.Length == 0)
            return new(candidates.Length == 0 ? "RuntimeNetworkBlockMissing" : "RuntimeNetworkBlockUnsupported", null, null);
        if (blocks.Length != 1) return new("RuntimeNetworkBlockAmbiguous", null, null);
        var block = blocks[0];
        var interfaces = results[0].Interfaces.Where(item =>
                item.Id > 0 && item.BlockId == block.Id &&
                string.Equals(item.VariablePath?.Trim(), variablePath, StringComparison.Ordinal))
            .OrderBy(item => item.Id)
            .ToArray();
        if (interfaces.Length == 0)
        {
            // 2026-09-11 深层符号回退（与生成侧 BindNetworkPartsAsync 同口径）：
            // 数组同名展开 X[i]→X.X[i] 精确命中；否则最长前缀容器行 + 消费者推断类型，以原路径绑定。
            var fallback = LineUpNetworkSymbolFallback.ResolveDeepLeaf(
                results[0].Interfaces, block.Id, variablePath, inferredLeafDataType);
            if (fallback is not null)
                return new(null, block, fallback.Value.Interface, fallback.Value.BoundVariablePath);
            return new("RuntimeNetworkInterfaceMissing", null, null);
        }
        if (interfaces.Length != 1) return new("RuntimeNetworkInterfaceAmbiguous", null, null);
        if (string.IsNullOrWhiteSpace(interfaces[0].DataType))
            return new("RuntimeNetworkInterfaceTypeMissing", null, null);
        return new(null, block, interfaces[0], variablePath);
    }

    private static bool HasExactSingleInterface(
        IReadOnlyList<LineUpBlockInterfaceLookup> interfaces,
        LineUpRuntimeBindingTag tag)
    {
        var matches = interfaces.Where(item =>
            item.BlockId == tag.ResolvedBlockId &&
            string.Equals(item.VariablePath?.Trim(), tag.VariablePath?.Trim(), StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 &&
               matches[0].Id == tag.ResolvedBlockInterfaceId &&
               string.Equals(matches[0].DataType?.Trim(), tag.DataType?.Trim(), StringComparison.Ordinal);
    }

    private static bool IsSupportedNamedDb(LineUpBlockLookup block) =>
        string.Equals(block.ProgrammingLanguage?.Trim(), "DB", StringComparison.Ordinal) &&
        block.BlockType?.Trim() is { } type &&
        (string.Equals(type, "DB", StringComparison.Ordinal) ||
         string.Equals(type, "GlobalDB", StringComparison.Ordinal) ||
         string.Equals(type, "InstanceDB", StringComparison.Ordinal));

    private static string RuntimeRole(string role) => role switch
    {
        "TransitionCondition" => "TransitionCondition",
        "StepInterlock" => "Interlock",
        "StepSupervision" => "Supervision",
        _ => "Unknown"
    };

    private sealed record PreviewGlobalPart(
        LineUpPublishedSequenceDefinition Sequence,
        LineUpPublishedNodeDefinition Owner,
        LineUpPublishedLogicNetworkDefinition Network,
        LineUpPublishedLogicPartDefinition Part);

    private sealed record CurrentGraphOutcome(
        string? IssueCode,
        LineUpBlockLookup? Block,
        LineUpBlockInterfaceLookup? Interface,
        // 2026-09-11 深层符号回退后实际绑定的变量路径（数组同名展开后与请求路径不同）；null=与请求路径一致
        string? BoundVariablePath = null);
}

internal sealed class LineUpMonitorBindingFactory(
    LineUpPublishedGraphProjector projector,
    LineUpReadPlanCompiler compiler)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };

    /// <summary>构建运行时绑定；失败时 failureReason 带出首个否决点（2026-09-10 诊断增强）。
    /// excludedVariablePaths（2026-09-12 采集开关）：被屏蔽的条件类变量（完整 PLC 规范路径）
    /// 不进 ReadPlan——引用它的转移条件求值落 Unknown（结构性缺失，不降级连接状态）。
    /// 工步执行位（StepActive）不受屏蔽影响；生成/校验链路（BuildAsync）不传此参数=全量。</summary>
    public LineUpMonitorBinding? Build(
        LineUpGraphRuntimeContext context,
        IReadOnlySet<string>? excludedVariablePaths,
        out string? failureReason)
    {
        failureReason = null;
        ArgumentNullException.ThrowIfNull(context);
        var json = context.Binding.RuntimeBindingJson;
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > LineUpRuntimeBindingApp.MaximumRuntimeBindingBytes ||
            context.Binding.Id <= 0 || context.Binding.StationId <= 0 ||
            context.Binding.PublishedRevisionId <= 0 || context.Binding.InstanceDbBlockId is not > 0)
        {
            failureReason = "绑定身份/文档大小检查未过 bytes=" + (json is null ? 0 : Encoding.UTF8.GetByteCount(json));
            return null;
        }

        LineUpRuntimeBindingDocument? runtime;
        try
        {
            runtime = JsonSerializer.Deserialize<LineUpRuntimeBindingDocument>(json, JsonOptions);
        }
        catch (JsonException error)
        {
            failureReason = "RuntimeBindingJson 反序列化失败: " + error.Message;
            return null;
        }
        if (!ValidIdentity(runtime, context)) { failureReason = "doc 身份与绑定上下文不一致"; return null; }
        var runtimeDefinitionRevision = runtime!.DefinitionRevision!;

        LineUpPublishedGraphDefinition published;
        try
        {
            published = projector.Project(context);
        }
        catch (LineUpPublishedGraphProjectionException)
        {
            failureReason = "发布投影异常";
            return null;
        }
        if (!ValidSequences(runtime!, published)) { failureReason = "doc 序列与发布序列不一致"; return null; }
        if (!ValidStaticMaterial(runtime!, published)) { failureReason = "doc 静态材料与发布不一致"; return null; }
        var currentNetworks = new LineUpLadNetworkCompiler().Compile(
            published, runtime.Tags, runtime.StaticOperands);
        var blocking = currentNetworks.Issues.FirstOrDefault(issue =>
            string.Equals(issue.Severity, "Blocking", StringComparison.Ordinal));
        if (blocking is not null)
        {
            failureReason = "当前编译存在 Blocking: " + blocking.Code + " @" + blocking.PartSourceKey;
            return null;
        }
        if (!CompiledTopologyEquals(runtime.CompiledNetworks!, currentNetworks.Networks))
        {
            failureReason = "编译拓扑与 doc 不一致 doc网络=" + (runtime.CompiledNetworks?.Count ?? 0) +
                            " 当前网络=" + currentNetworks.Networks.Length;
            return null;
        }
        // 2026-09-12 修复：存量 doc（2026-09-10 及更早旧代码生成/校验落库）的 StaticIssues 仍含
        // 容忍码（实测 4618129432547299328 doc=137 条全为 BlockMissing/InterfaceMissing/ConstantTypeMismatch/
        // OperandMetadataMissing/ScopeUnsupported），而下方 currentIssues 已按容忍集过滤（当前=0）。
        // doc 侧不同口径过滤时，CompilerIssuesEqual 白名单里与容忍集交叉的码
        // （ConstantTypeMismatch/OperandMetadataMissing）使比对必然不一致 → 每轮跳过采样 →
        // 快照停更 → 树上恒「已启用」。doc 侧提取时同口径过滤（docRawIssues 保留未过滤口径
        // 供 DefinitionRevision 双口径终检）；新生成 doc（生成侧 BuildAsync 落库前已过滤）此过滤
        // 为幂等 no-op。Blocking 不在容忍集，不受影响。
        var docRawIssues = runtime.StaticIssues.Select(issue => new LineUpRuntimeBindingIssueDto(
            issue.Code ?? string.Empty,
            issue.OwnerSourceKey ?? string.Empty,
            issue.Severity ?? string.Empty,
            issue.NetworkSourceKey ?? string.Empty,
            issue.PartSourceKey ?? string.Empty)).ToArray();
        var staticIssues = docRawIssues
            .Where(issue => !LineUpRuntimeBindingApp.ToleratedNetworkWarningCodes.Contains(issue.Code))
            .ToArray();
        // 2026-09-11：doc 静态 issue 在生成侧（BuildAsync）已按容忍集过滤展示/落库；
        // 当前编译的同类结构性 Warning 必须同口径过滤后再比对，否则屏蔽生效后所有
        // 重新生成的绑定在工厂构建阶段永远"静态issue集与当前编译不一致"→ 每轮跳过采样
        // → 快照停更 → 树上恒"已启用"（实测 11 个 PLC1 绑定全部命中）。Blocking 不在容忍集，不受影响。
        var currentIssues = currentNetworks.Issues
            .Where(issue => !LineUpRuntimeBindingApp.ToleratedNetworkWarningCodes.Contains(issue.Code))
            .ToArray();
        if (!CompilerIssuesEqual(staticIssues, currentIssues))
        {
            var onlyDoc = staticIssues.Select(issue => issue.Code + "@" + issue.PartSourceKey)
                .Except(currentIssues.Select(issue => issue.Code + "@" + issue.PartSourceKey)).FirstOrDefault();
            var onlyNow = currentIssues.Select(issue => issue.Code + "@" + issue.PartSourceKey)
                .Except(staticIssues.Select(issue => issue.Code + "@" + issue.PartSourceKey)).FirstOrDefault();
            failureReason = $"静态issue集与当前编译不一致 doc={staticIssues.Length} 当前={currentIssues.Length} doc独有={onlyDoc ?? "无"} 当前独有={onlyNow ?? "无"}";
            return null;
        }
        // 2026-09-12：DefinitionRevision 终检双口径。生成侧 BuildAsync 落库前已按容忍集过滤
        // orderedIssues，故新生成 doc 的 revision 以「过滤后 issue 集」参与 hash；而存量 doc
        // （2026-09-10 及更早）的 revision 以「未过滤（含容忍码）issue 集」参与 hash。仅用新口径
        // 重算会让所有存量 doc 挂在终检（实测 doc=137 条容忍码的绑定，两种口径 hash 必然不同）。
        // 双口径任一匹配即通过——非容忍码部分的一致性已由上方 CompilerIssuesEqual 严格比对保障，
        // 两种口径只差容忍码集合，不构成漂移漏检。
        var currentRevision = LineUpRuntimeBindingApp.DefinitionRevision(
            context,
            runtime.Tags,
            runtime.StaticOperands,
            staticIssues,
            runtime.PersistSamples,
            runtime.CompiledNetworks);
        var legacyRevision = LineUpRuntimeBindingApp.DefinitionRevision(
            context,
            runtime.Tags,
            runtime.StaticOperands,
            docRawIssues,
            runtime.PersistSamples,
            runtime.CompiledNetworks);
        if (!string.Equals(runtimeDefinitionRevision, currentRevision, StringComparison.Ordinal) &&
            !string.Equals(runtimeDefinitionRevision, legacyRevision, StringComparison.Ordinal))
        {
            failureReason = "DefinitionRevision 不一致 doc=" + runtimeDefinitionRevision +
                            " 现场=" + currentRevision[..12] + "…";
            return null;
        }

        var publishedBySequence = published.Sequences.ToDictionary(
            sequence => sequence.SequenceSourceKey,
            StringComparer.Ordinal);
        var sources = new List<LineUpRuntimeSourceVariable>();
        var sourceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < runtime!.Tags.Count; index++)
        {
            var tag = runtime.Tags[index];
            if (tag is null || string.IsNullOrWhiteSpace(tag.SequenceSourceKey) ||
                !TryRole(tag.Role, out var role) ||
                !publishedBySequence.TryGetValue(tag.SequenceSourceKey, out var sequence) ||
                tag.ResolvedBlockId <= 0 || string.IsNullOrWhiteSpace(tag.ResolvedBlockName) ||
                tag.ResolvedBlockInterfaceId <= 0 || string.IsNullOrWhiteSpace(tag.OwnerSourceKey) ||
                string.IsNullOrWhiteSpace(tag.NetworkSourceKey) || string.IsNullOrWhiteSpace(tag.PartSourceKey) ||
                string.IsNullOrWhiteSpace(tag.VariablePath) || string.IsNullOrWhiteSpace(tag.CanonicalPlcPath) ||
                string.IsNullOrWhiteSpace(tag.DataType))
            {
                failureReason = "tag 身份不完整 idx=" + index;
                return null;
            }

            var expectedCanonicalPath = $"{tag.ResolvedBlockName.Trim()}.{tag.VariablePath.Trim()}";
            if (!string.Equals(tag.CanonicalPlcPath.Trim(), expectedCanonicalPath, StringComparison.Ordinal))
            {
                failureReason = "tag 规范路径不一致 part=" + tag.PartSourceKey;
                return null;
            }
            var sourceKey = tag.PartSourceKey.Trim();
            if (!sourceOwners.TryAdd(sourceKey, tag.SequenceSourceKey))
            {
                failureReason = "重复源属主 part=" + sourceKey;
                return null;
            }

            int? stepNumber = null;
            if (role == LineUpRuntimeVariableRole.StepActive)
            {
                if (!string.Equals(tag.OwnerSourceKey, tag.NetworkSourceKey, StringComparison.Ordinal) ||
                    !string.Equals(tag.OwnerSourceKey, tag.PartSourceKey, StringComparison.Ordinal) ||
                    !string.Equals(tag.Scope, "InstanceDb", StringComparison.Ordinal) ||
                    tag.ResolvedBlockId != context.InstanceDb.Id ||
                    !string.Equals(tag.ResolvedBlockName, context.InstanceDb.BlockName?.Trim(), StringComparison.Ordinal))
                {
                    failureReason = "步tag上下文不一致 part=" + tag.PartSourceKey;
                    return null;
                }
                var matches = sequence.Steps.Where(step =>
                    string.Equals(step.SourceKey, tag.OwnerSourceKey, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                {
                    failureReason = "步tag找不到发布步 part=" + tag.PartSourceKey;
                    return null;
                }
                if (!string.Equals(matches[0].ExecutionPath, tag.VariablePath, StringComparison.Ordinal) ||
                    !IsBoolean(tag.DataType))
                {
                    failureReason = "步执行路径/类型不一致 part=" + tag.PartSourceKey;
                    return null;
                }
                stepNumber = matches[0].Number;
            }
            else
            {
                if (!TryPreviewPart(sequence, tag, role, out var owner) || owner is null)
                {
                    failureReason = "tag无预览部件 part=" + tag.PartSourceKey;
                    return null;
                }
            }

            // 2026-09-12 采集开关：被屏蔽的条件类变量不进读计划（值缺失→转移 Unknown，不降级）。
            // StepActive（工步执行位）硬豁免：屏蔽它会破坏步状态/定位/事件链路，API 层另有入口拦截。
            if (excludedVariablePaths is not null &&
                role != LineUpRuntimeVariableRole.StepActive &&
                excludedVariablePaths.Contains(tag.CanonicalPlcPath.Trim()))
            {
                continue;
            }

            sources.Add(new(
                tag.OwnerSourceKey.Trim(),
                tag.NetworkSourceKey.Trim(),
                sourceKey,
                string.Empty,
                role,
                stepNumber,
                tag.ResolvedBlockId,
                tag.ResolvedBlockName.Trim(),
                tag.ResolvedBlockInterfaceId,
                tag.VariablePath.Trim(),
                tag.CanonicalPlcPath.Trim(),
                tag.DataType.Trim(),
                tag.NumericAddress,
                true));
        }

        var compilation = compiler.Compile(new(
            context.Binding.Id,
            runtimeDefinitionRevision,
            context.Asset.PlcProjectId,
            context.InstanceDb.BlockName!,
            runtime.Tags.Any(tag => !string.IsNullOrWhiteSpace(tag.NumericAddress))
                ? LineUpRuntimeAddressMode.StandardDb
                : LineUpRuntimeAddressMode.OptimizedSymbolic,
            sources));
        var blocking2 = compilation.Issues.FirstOrDefault(issue => issue.Code != "DuplicateSourceIgnored");
        if (compilation.Plan.Items.IsDefaultOrEmpty ||
            blocking2 is not null ||
            compilation.Plan.Items.Length != sources.Count)
        {
            failureReason = "读计划编译: " +
                            (compilation.Plan.Items.IsDefaultOrEmpty ? "计划为空" :
                             blocking2 is not null ? "issue=" + blocking2.Code + " @" + blocking2.SourceKey :
                             $"计划项{compilation.Plan.Items.Length}!=源{sources.Count}");
            return null;
        }

        foreach (var sequence in published.Sequences)
        {
            var expected = sequence.Steps.Select(step => step.SourceKey)
                .OrderBy(key => key, StringComparer.Ordinal);
            var actual = runtime.Tags
                .Where(tag => string.Equals(tag.SequenceSourceKey, sequence.SequenceSourceKey, StringComparison.Ordinal) &&
                              string.Equals(tag.Role, "StepExecution", StringComparison.Ordinal))
                .Select(tag => tag.OwnerSourceKey)
                .OrderBy(key => key, StringComparer.Ordinal);
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                var miss = expected.Except(actual).FirstOrDefault() ?? actual.Except(expected).FirstOrDefault() ?? "?";
                failureReason = "步对齐不一致 seq=" + sequence.SequenceSourceKey + " 差异=" + miss;
                return null;
            }
        }

        var evaluationContext = new LineUpRuntimeLogicEvaluationContext(
            context.Binding.Id, runtimeDefinitionRevision);
        var transitionCoilKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var transition in published.Sequences.SelectMany(sequence => sequence.Transitions))
        {
            var matches = runtime.CompiledNetworks!.Where(network =>
                string.Equals(network.OwnerSourceKey, transition.SourceKey, StringComparison.Ordinal) &&
                string.Equals(network.Role, "TransitionCondition", StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0 && runtime.ParserSchemaVersion < 3)
            {
                transitionCoilKeys[transition.SourceKey] = string.Empty;
                continue;
            }
            if (matches.Length != 1)
            {
                failureReason = "转移网络匹配数=" + matches.Length + " transition=" + transition.SourceKey;
                return null;
            }
            var network = matches[0];
            var coil = network.Parts.Where(part =>
                string.Equals(part.SourceKey, network.CoilPartSourceKey, StringComparison.Ordinal) &&
                string.Equals(part.PartName, "TrCoil", StringComparison.Ordinal)).ToArray();
            if (coil.Length != 1 || !transitionCoilKeys.TryAdd(
                    transition.SourceKey,
                    LineUpLadNetworkEvaluator.CanonicalKey(
                        evaluationContext, network, network.CoilPartSourceKey, "derived")))
            {
                failureReason = "转移线圈缺失/重复 transition=" + transition.SourceKey;
                return null;
            }
        }

        var viewerDefinitions = LineUpMonitorViewerProjector.Project(published).Sequences
            .ToImmutableDictionary(
                sequence => sequence.SequenceSourceKey,
                sequence => sequence,
                StringComparer.Ordinal);
        var definitions = published.Sequences
            .OrderBy(sequence => sequence.SequenceSourceKey, StringComparer.Ordinal)
            .Select(sequence => new LineUpRuntimeStateDefinition(
                context.Binding.Id,
                context.PublishedRevision.Id,
                context.Binding.StationId,
                sequence.SequenceSourceKey,
                sequence.DisplayName,
                runtimeDefinitionRevision,
                sequence.Steps.Select(step => new LineUpRuntimeStepDefinition(
                        step.SourceKey,
                        step.DisplayName,
                        step.IsInitial,
                        step.WarningTime,
                        step.MaximumStepTime))
                    .ToImmutableArray(),
                sequence.Transitions.Select(transition => new LineUpRuntimeTransitionDefinition(
                        transition.SourceKey,
                        transition.DisplayName,
                        [transitionCoilKeys[transition.SourceKey]]))
                    .ToImmutableArray(),
                sequence.Edges.Select(edge => new LineUpRuntimeEdgeDefinition(
                        edge.SourceKey,
                        edge.FromSourceKey,
                        edge.ToSourceKey,
                        edge.EdgeKind))
                    .ToImmutableArray(),
                BuildExclusiveStepGroups(sequence),
                runtime.CompiledNetworks.Where(network => string.Equals(
                        network.SequenceSourceKey, sequence.SequenceSourceKey, StringComparison.Ordinal))
                    .ToImmutableArray()))
            .ToImmutableArray();
        var pollMs = runtime.Tags.Select(tag => tag.ExpectedScanMs)
            .Where(value => value > 0)
            .DefaultIfEmpty(500)
            .Min();
        return new(
            context.Binding.Id,
            context.PublishedRevision.Id,
            context.Binding.StationId,
            runtimeDefinitionRevision,
            definitions,
            compilation.Plan,
            sourceOwners,
            TimeSpan.FromMilliseconds(Math.Clamp(pollMs, 100, 5_000)),
            runtime.PersistSamples)
        {
            ViewerDefinitions = viewerDefinitions
        };
    }

    /// <summary>
    /// 2026-09-13：编译交替分支（Selection）头步互斥组。
    /// Edge 语义（LineUpGraphNormalizer 规范）：Sequence=步↔转移直连；SelectionSplit=步→分支头转移
    /// （BranchOrder 区分分支）；SelectionMerge=分支尾转移→汇合步；ParallelSplit/Merge=并发分支（不互斥）；
    /// Jump=转移→跳转目标步。同一 SelectionSplit 源的各分支头转移的出边目标（必为步）即各分支头步；
    /// S7 GRAPH 交替分支不变量 = 任一时刻最多一个分支在执行，故各分支头步两两互斥。
    /// 嵌套分支由「同源分组」天然覆盖（内层 SelectionSplit 自成一组）。并发分支不产生互斥组。
    /// 简化边界：分支头转移的多条出边（理论单出口，Jump 目标属跨路径）只取序最小的一条作头步，避免跳转目标误入组。
    /// </summary>
    private static ImmutableArray<ImmutableArray<string>> BuildExclusiveStepGroups(
        LineUpPublishedSequenceDefinition sequence)
    {
        var stepKeys = sequence.Steps.Select(x => x.SourceKey).ToHashSet(StringComparer.Ordinal);
        var transitionHeads = sequence.Edges
            .Where(edge => stepKeys.Contains(edge.ToSourceKey))
            .GroupBy(edge => edge.FromSourceKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.ToSourceKey)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
        var groups = new List<ImmutableArray<string>>();
        foreach (var split in sequence.Edges
                     .Where(edge => string.Equals(edge.EdgeKind, "SelectionSplit", StringComparison.Ordinal))
                     .GroupBy(edge => edge.FromSourceKey, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var heads = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var branch in split)
            {
                if (transitionHeads.TryGetValue(branch.ToSourceKey, out var head))
                {
                    heads.Add(head);
                }
            }
            if (heads.Count >= 2)
            {
                groups.Add(heads.ToImmutableArray());
            }
        }
        return groups.ToImmutableArray();
    }

    private static bool ValidIdentity(LineUpRuntimeBindingDocument? runtime, LineUpGraphRuntimeContext context) =>
        runtime is not null &&
        string.Equals(runtime.Schema, LineUpRuntimeBindingApp.RuntimeBindingSchema, StringComparison.Ordinal) &&
        runtime.BindingId == context.Binding.Id &&
        runtime.GraphAssetId == context.Asset.Id &&
        runtime.PublishedRevisionId == context.PublishedRevision.Id &&
        string.Equals(runtime.SourceHash, context.PublishedRevision.SourceHash, StringComparison.Ordinal) &&
        runtime.ParserSchemaVersion == context.PublishedRevision.ParserSchemaVersion &&
        !string.IsNullOrWhiteSpace(runtime.DefinitionRevision) &&
        !runtime.PersistSamples &&
        runtime.PlcProjectId == context.Asset.PlcProjectId &&
        runtime.InstanceDbBlockId == context.InstanceDb.Id &&
        !string.IsNullOrWhiteSpace(context.InstanceDb.BlockName) &&
        string.Equals(runtime.InstanceDbName, context.InstanceDb.BlockName?.Trim(), StringComparison.Ordinal) &&
        runtime.Sequences is { Count: > 0 } && runtime.Sequences.All(sequence => sequence is not null) &&
        runtime.Tags is { Count: > 0 } && runtime.Tags.All(tag => tag is not null) &&
        runtime.StaticOperands is not null && runtime.StaticOperands.All(item => item is not null) &&
        runtime.StaticIssues is not null && runtime.StaticIssues.All(item => item is not null) &&
        runtime.CompiledNetworks is not null && runtime.CompiledNetworks.All(item => item is not null);

    private static bool CompiledTopologyEquals(
        IReadOnlyList<LineUpRuntimeLogicNetworkDefinition> stored,
        IReadOnlyList<LineUpRuntimeLogicNetworkDefinition> current)
    {
        try
        {
            return JsonNode.DeepEquals(
                JsonNode.Parse(JsonSerializer.Serialize(stored, JsonOptions)),
                JsonNode.Parse(JsonSerializer.Serialize(current, JsonOptions)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CompilerIssuesEqual(
        IEnumerable<LineUpRuntimeBindingIssueDto> stored,
        IEnumerable<LineUpRuntimeBindingIssueDto> current)
    {
        static bool IsCompilerIssue(LineUpRuntimeBindingIssueDto issue) => issue.Code is
            "RuntimeNetworkDuplicateUId" or
            "RuntimeNetworkWireEndpointInvalid" or
            "RuntimeNetworkPinAmbiguous" or
            "RuntimeNetworkPinMissing" or
            "RuntimeNetworkCycle" or
            "RuntimeNetworkCoilMissing" or
            "RuntimeNetworkCoilAmbiguous" or
            "RuntimeNetworkCoilRoleInvalid" or
            "RuntimeNetworkPartUnsupported" or
            "RuntimeNetworkPinSchemaInvalid" or
            "RuntimeNetworkConstantTypeMismatch" or
            "RuntimeNetworkConstantInvalid" or
            "RuntimeNetworkOperandConsumerInvalid" or
            "RuntimeNetworkOperandMetadataMissing" or
            "RuntimeNetworkOperandTypeMismatch";
        static IOrderedEnumerable<LineUpRuntimeBindingIssueDto> Order(
            IEnumerable<LineUpRuntimeBindingIssueDto> issues) => issues
            .Where(IsCompilerIssue)
            .OrderBy(issue => issue.Severity, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.OwnerSourceKey, StringComparer.Ordinal)
            .ThenBy(issue => issue.NetworkSourceKey, StringComparer.Ordinal)
            .ThenBy(issue => issue.PartSourceKey, StringComparer.Ordinal);
        return Order(stored).SequenceEqual(Order(current));
    }

    private static bool ValidSequences(
        LineUpRuntimeBindingDocument runtime,
        LineUpPublishedGraphDefinition published)
    {
        if (runtime.Sequences.Any(sequence =>
                sequence is null || string.IsNullOrWhiteSpace(sequence.SequenceSourceKey) ||
                sequence.StepSourceKeys is null ||
                sequence.StepSourceKeys.Any(string.IsNullOrWhiteSpace) ||
                sequence.StepSourceKeys.Distinct(StringComparer.Ordinal).Count() != sequence.StepSourceKeys.Count) ||
            runtime.Sequences.Select(sequence => sequence.SequenceSourceKey)
            .Distinct(StringComparer.Ordinal).Count() != runtime.Sequences.Count)
        {
            return false;
        }
        var runtimeByKey = runtime.Sequences.ToDictionary(
            sequence => sequence.SequenceSourceKey,
            StringComparer.Ordinal);
        if (runtimeByKey.Count != published.Sequences.Count) return false;
        foreach (var sequence in published.Sequences)
        {
            if (!runtimeByKey.TryGetValue(sequence.SequenceSourceKey, out var runtimeSequence) ||
                runtimeSequence.StepSourceKeys is null ||
                !runtimeSequence.StepSourceKeys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(
                    sequence.Steps.Select(step => step.SourceKey).OrderBy(key => key, StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidStaticMaterial(
        LineUpRuntimeBindingDocument runtime,
        LineUpPublishedGraphDefinition published)
    {
        if (runtime.StaticOperands.Any(item =>
                item is null || string.IsNullOrWhiteSpace(item.SequenceSourceKey) ||
                string.IsNullOrWhiteSpace(item.Role) || string.IsNullOrWhiteSpace(item.OwnerSourceKey) ||
                string.IsNullOrWhiteSpace(item.NetworkSourceKey) || string.IsNullOrWhiteSpace(item.PartSourceKey) ||
                item.Scope is not ("LiteralConstant" or "TypedConstant")) ||
            runtime.StaticOperands.Select(item => item.PartSourceKey)
                .Distinct(StringComparer.Ordinal).Count() != runtime.StaticOperands.Count ||
            runtime.StaticIssues.Any(item =>
                item is null || !string.Equals(item.Severity, "Warning", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.Code)))
        {
            return false;
        }

        if (published.ParserSchemaVersion < 3)
        {
            return runtime.Tags.All(tag => string.Equals(tag.Role, "StepExecution", StringComparison.Ordinal)) &&
                   runtime.StaticOperands.Count == 0 &&
                   runtime.StaticIssues.Count(item =>
                       string.Equals(item.Code, "RuntimeNetworkParserSchemaUnsupported", StringComparison.Ordinal) &&
                       string.IsNullOrEmpty(item.OwnerSourceKey) && string.IsNullOrEmpty(item.NetworkSourceKey) &&
                       string.IsNullOrEmpty(item.PartSourceKey)) == 1;
        }

        if (runtime.StaticIssues.Any(item =>
                string.IsNullOrWhiteSpace(item.OwnerSourceKey) ||
                string.IsNullOrWhiteSpace(item.NetworkSourceKey) ||
                string.IsNullOrWhiteSpace(item.PartSourceKey)))
        {
            return false;
        }

        var tagsByPart = runtime.Tags
            .Where(tag => !string.Equals(tag.Role, "StepExecution", StringComparison.Ordinal))
            .GroupBy(tag => tag.PartSourceKey ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var operandsByPart = runtime.StaticOperands
            .GroupBy(item => item.PartSourceKey ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var issuesByPart = runtime.StaticIssues
            .Where(item => !string.IsNullOrWhiteSpace(item.PartSourceKey))
            .GroupBy(item => item.PartSourceKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var seenParts = published.Sequences
            .SelectMany(sequence => sequence.Steps.Concat(sequence.Transitions))
            .SelectMany(owner => owner.LogicNetworks)
            .SelectMany(network => network.Parts)
            .Select(part => part.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        var seenAccessParts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sequence in published.Sequences)
        foreach (var owner in sequence.Steps.Concat(sequence.Transitions))
        foreach (var network in owner.LogicNetworks)
        foreach (var part in network.Parts.Where(part => string.Equals(part.PartName, "Access", StringComparison.Ordinal)))
        {
            if (!seenAccessParts.Add(part.SourceKey)) return false;
            if (part.Scope is "LiteralConstant" or "TypedConstant")
            {
                if (!operandsByPart.TryGetValue(part.SourceKey, out var matches) || matches.Length != 1)
                    return false;
                var operand = matches[0];
                if (!string.Equals(operand.SequenceSourceKey, sequence.SequenceSourceKey, StringComparison.Ordinal) ||
                    !string.Equals(operand.Role, RuntimeRole(network.Role), StringComparison.Ordinal) ||
                    !string.Equals(operand.OwnerSourceKey, owner.SourceKey, StringComparison.Ordinal) ||
                    !string.Equals(operand.NetworkSourceKey, network.SourceKey, StringComparison.Ordinal) ||
                    !string.Equals(operand.Scope, part.Scope, StringComparison.Ordinal) ||
                    !string.Equals(operand.ConstantValue, part.ConstantValue, StringComparison.Ordinal))
                    return false;
                continue;
            }

            var hasTag = tagsByPart.TryGetValue(part.SourceKey, out var tags) && tags.Length == 1;
            var hasIssue = issuesByPart.TryGetValue(part.SourceKey, out var issues) && issues.Length >= 1;
            if (hasIssue && issues!.Any(issue =>
                    !string.Equals(issue.OwnerSourceKey, owner.SourceKey, StringComparison.Ordinal) ||
                    !string.Equals(issue.NetworkSourceKey, network.SourceKey, StringComparison.Ordinal)))
            {
                return false;
            }
            if (string.Equals(part.Scope, "GlobalVariable", StringComparison.Ordinal) && part.SymbolPath.Count >= 2)
            {
                // 2026-09-10：由"tag/issue 互斥"放宽为"至少其一"。写入器允许符号解析成功(写 tag)的同时
                // 因类型不匹配等挂 Warning issue（FB461/FB261 的 KUKA RobToPLC/PLCToRob 10 个部件即此形态，
                // OperandTypeMismatch），原 XOR 要求使这两条绑定永远构建失败。tag 已由后续 TryPreviewPart
                // 严格校验，issue 属主/网络一致性上方已核——共存不损失漂移检测。
                // 2026-09-12：再放宽为"两无=容忍跳过"。生成侧（BuildAsync）落库前按容忍集过滤静态
                // issue，BlockMissing/InterfaceMissing/ExternalReference 等结构性告警部件在新口径 doc 里
                // 合法地既无 tag 也无 issue；若此处仍强制"至少其一"，容忍码屏蔽生效后一切绑定
                // （含重新生成的）永远构建失败 → 快照停更 → 树上恒「已启用」。
                // "两无但现场解析成功（应产 tag）"的真漂移由后续 CompiledTopologyEquals 拓扑比对拦截。
                if (!hasTag && !hasIssue)
                {
                    continue;
                }
            }
            else if (hasTag)
            {
                // 非 GlobalVariable/常量部件（LocalVariable、短路径等）生成侧只落容忍码
                // RuntimeNetworkScopeUnsupported（落库前被滤）→ doc 合法无物；生成侧从不为这类
                // 部件产 tag，出现 tag 即材料异常。
                return false;
            }
        }

        return tagsByPart.Keys.All(seenParts.Contains) &&
               operandsByPart.Keys.All(seenParts.Contains) &&
               issuesByPart.Keys.All(seenParts.Contains);
    }

    private static bool TryPreviewPart(
        LineUpPublishedSequenceDefinition sequence,
        LineUpRuntimeBindingTag tag,
        LineUpRuntimeVariableRole role,
        out LineUpPublishedNodeDefinition? owner)
    {
        owner = sequence.Steps.Concat(sequence.Transitions).SingleOrDefault(item =>
            string.Equals(item.SourceKey, tag.OwnerSourceKey, StringComparison.Ordinal));
        if (owner is null) return false;
        var networks = owner.LogicNetworks.Where(network =>
            string.Equals(network.SourceKey, tag.NetworkSourceKey, StringComparison.Ordinal)).ToArray();
        if (networks.Length != 1 || !TryRole(RuntimeRole(networks[0].Role), out var expectedRole) || expectedRole != role)
            return false;
        var parts = networks[0].Parts.Where(part =>
            string.Equals(part.SourceKey, tag.PartSourceKey, StringComparison.Ordinal)).ToArray();
        if (parts.Length != 1) return false;
        var part = parts[0];
        if (!string.Equals(part.PartName, "Access", StringComparison.Ordinal) ||
            !string.Equals(part.Scope, "GlobalVariable", StringComparison.Ordinal) ||
            !string.Equals(tag.Scope, part.Scope, StringComparison.Ordinal) ||
            part.SymbolPath.Count < 2 || part.SymbolPath.Any(string.IsNullOrWhiteSpace))
            return false;
        var blockName = part.SymbolPath[0].Trim();
        var variablePath = string.Join('.', part.SymbolPath.Skip(1).Select(value => value.Trim()));
        return string.Equals(tag.ResolvedBlockName, blockName, StringComparison.Ordinal) &&
               string.Equals(tag.VariablePath, variablePath, StringComparison.Ordinal) &&
               string.Equals(tag.CanonicalPlcPath, $"{blockName}.{variablePath}", StringComparison.Ordinal);
    }

    private static string RuntimeRole(string role) => role switch
    {
        "TransitionCondition" => "TransitionCondition",
        "StepInterlock" => "Interlock",
        "StepSupervision" => "Supervision",
        _ => "Unknown"
    };

    private static bool IsBoolean(string? dataType) =>
        string.Equals(dataType?.Trim(), "Bool", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(dataType?.Trim(), "Boolean", StringComparison.OrdinalIgnoreCase);

    private static bool TryRole(string? value, out LineUpRuntimeVariableRole role)
    {
        role = value switch
        {
            "StepExecution" => LineUpRuntimeVariableRole.StepActive,
            "TransitionCondition" => LineUpRuntimeVariableRole.TransitionCondition,
            "AutoMode" => LineUpRuntimeVariableRole.AutoMode,
            "Interlock" => LineUpRuntimeVariableRole.Interlock,
            "Supervision" => LineUpRuntimeVariableRole.Supervision,
            _ => default
        };
        return value is "StepExecution" or "TransitionCondition" or "AutoMode" or "Interlock" or "Supervision";
    }

}
