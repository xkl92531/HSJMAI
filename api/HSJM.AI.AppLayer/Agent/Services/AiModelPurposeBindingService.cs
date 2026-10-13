#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace HSJM.AI.AppLayer.Agent.Services;

public static class AiModelPurposes
{
    public const string DefaultChat = "default_chat";
    public const string ConstraintDescription = "constraint_description";
    public const string IndependentPlc = "independent_plc";
    public const string MemoryChat = "memory_chat";
    public const string MemoryEmbedding = "memory_embedding";

    public static IReadOnlyList<(string Code, string DisplayName)> All { get; } =
    [
        (DefaultChat, "默认聊天与诊断智能体"),
        (ConstraintDescription, "I/O 约束说明"),
        (IndependentPlc, "独立 PLC 解析与候选分析"),
        (MemoryChat, "记忆提取与诊断压缩"),
        (MemoryEmbedding, "记忆向量化")
    ];

    public static bool IsKnown(string purpose) => All.Any(item => item.Code == purpose);

    public static bool IsAllowedModelType(string purpose, string? modelType) =>
        IsKnown(purpose) && (purpose == MemoryEmbedding
            ? string.Equals(modelType, "Embedding", StringComparison.OrdinalIgnoreCase)
            : string.Equals(modelType, "Chat", StringComparison.OrdinalIgnoreCase)
              || string.Equals(modelType, "VL", StringComparison.OrdinalIgnoreCase));
}

public sealed record ResolvedAiModel(LLM_ModelEntity Model, long Revision);

public interface IAiModelPurposeResolver
{
    Task<ResolvedAiModel> ResolveAsync(string purpose, CancellationToken token = default);
}

public sealed class AiModelPurposeException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record AiModelPurposeBindingDto(
    string PurposeCode, string DisplayName, string? ModelId, string? ModelName,
    string? ModelCode, string? ModelType, long Revision, string Status);

public sealed record AiModelOptionDto(
    string Id, string ModelName, string ModelCode, string ModelType, bool IsEnabled);

public sealed record AiModelPurposeListDto(
    IReadOnlyList<AiModelPurposeBindingDto> Bindings,
    IReadOnlyList<AiModelOptionDto> Models);

public sealed class AiModelPurposeBindingService(IConfiguration configuration)
    : IAiModelPurposeResolver, ISelfScopedAutoInject
{
    private static readonly ConcurrentDictionary<string, Lazy<Task>> SchemaTasks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions ModelJson = new() { PropertyNameCaseInsensitive = true };

    private string ConnectionString => configuration["WebConfig:DBList:0:ConnectionString"]
        ?? throw new AiModelPurposeException("DatabaseNotConfigured", "模型配置数据库未配置");

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken token)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task EnsureSchemaAsync()
    {
        var connectionString = ConnectionString;
        var lazy = SchemaTasks.GetOrAdd(connectionString,
            _ => new Lazy<Task>(() => InitializeSchemaAsync(connectionString),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try { await lazy.Value.ConfigureAwait(false); }
        catch
        {
            SchemaTasks.TryRemove(new KeyValuePair<string, Lazy<Task>>(connectionString, lazy));
            throw;
        }
    }

    private static async Task InitializeSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // Serialize first-use setup across application instances. The seed runs only when the table is empty.
        await using (var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(17290228, 1)", connection, transaction))
            await guard.ExecuteNonQueryAsync();
        await using (var create = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS public.ai_model_purpose_binding (
              purpose_code text PRIMARY KEY,
              model_id bigint NULL,
              revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
              updated_at timestamptz NOT NULL DEFAULT now()
            )
            """, connection, transaction))
            await create.ExecuteNonQueryAsync();

        await using (var countCommand = new NpgsqlCommand(
            "SELECT count(*) FROM public.ai_model_purpose_binding", connection, transaction))
        {
            var count = (long)(await countCommand.ExecuteScalarAsync())!;
            if (count == 0)
            {
                // Preserve existing website-managed role choices. No model code or model ID is embedded here.
                const string seedSql = """
                    INSERT INTO public.ai_model_purpose_binding(purpose_code, model_id, revision)
                    SELECT x.purpose_code, x.model_id, CASE WHEN x.model_id IS NULL THEN 0 ELSE 1 END
                    FROM (
                      SELECT 'default_chat' AS purpose_code,
                        (SELECT m.id FROM public.ai_agent_role r JOIN public.ai_llm_model m ON m.id=r.modelid
                         WHERE lower(r.rolecode)='general_assistant' AND r.isenabled=true AND NOT r.isdeleted
                           AND m.isenabled=true AND NOT m.isdeleted AND lower(m.modeltype) IN ('chat','vl')
                         ORDER BY r.id DESC LIMIT 1) AS model_id
                      UNION ALL
                      SELECT 'constraint_description',
                        (SELECT m.id FROM public.ai_agent_role r JOIN public.ai_llm_model m ON m.id=r.modelid
                         WHERE lower(r.rolecode)='plc_expert' AND r.isenabled=true AND NOT r.isdeleted
                           AND m.isenabled=true AND NOT m.isdeleted AND lower(m.modeltype) IN ('chat','vl')
                         ORDER BY r.id DESC LIMIT 1)
                      UNION ALL
                      SELECT 'independent_plc',
                        (SELECT m.id FROM public.ai_agent_role r JOIN public.ai_llm_model m ON m.id=r.modelid
                         WHERE lower(r.rolecode)='plc_expert' AND r.isenabled=true AND NOT r.isdeleted
                           AND m.isenabled=true AND NOT m.isdeleted AND lower(m.modeltype) IN ('chat','vl')
                         ORDER BY r.id DESC LIMIT 1)
                      UNION ALL
                      SELECT 'memory_chat',
                        (SELECT m.id FROM public.ai_agent_role r JOIN public.ai_llm_model m ON m.id=r.modelid
                         WHERE lower(r.rolecode)='general_assistant' AND r.isenabled=true AND NOT r.isdeleted
                           AND m.isenabled=true AND NOT m.isdeleted AND lower(m.modeltype) IN ('chat','vl')
                         ORDER BY r.id DESC LIMIT 1)
                      UNION ALL
                      SELECT 'memory_embedding',
                        (SELECT CASE WHEN count(*)=1 THEN max(id) ELSE NULL END
                         FROM public.ai_llm_model WHERE isenabled=true AND NOT isdeleted AND lower(modeltype)='embedding')
                    ) AS x
                    ON CONFLICT (purpose_code) DO NOTHING
                    """;
                await using var seed = new NpgsqlCommand(seedSql, connection, transaction);
                await seed.ExecuteNonQueryAsync();
            }
        }
        // A partially migrated table must still expose every fixed purpose as an explicitly unbound row.
        await using (var fillMissing = new NpgsqlCommand("""
            INSERT INTO public.ai_model_purpose_binding(purpose_code, model_id, revision)
            VALUES ('default_chat', NULL, 0), ('constraint_description', NULL, 0),
                   ('independent_plc', NULL, 0), ('memory_chat', NULL, 0),
                   ('memory_embedding', NULL, 0)
            ON CONFLICT (purpose_code) DO NOTHING
            """, connection, transaction))
            await fillMissing.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task<ResolvedAiModel> ResolveAsync(string purpose, CancellationToken token = default)
    {
        if (!AiModelPurposes.IsKnown(purpose))
            throw new AiModelPurposeException("UnknownPurpose", $"未知模型用途：{purpose}");
        await EnsureSchemaAsync();
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT b.model_id, b.revision, to_jsonb(m)::text
            FROM public.ai_model_purpose_binding b
            LEFT JOIN public.ai_llm_model m ON m.id=b.model_id
            WHERE b.purpose_code=@purpose
            """, connection);
        command.Parameters.AddWithValue("purpose", purpose);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
            throw new AiModelPurposeException("PurposeUnbound", $"模型用途 {purpose} 尚未绑定模型");
        var revision = reader.GetInt64(1);
        if (reader.IsDBNull(2))
            throw new AiModelPurposeException("ModelUnavailable", $"模型用途 {purpose} 绑定的模型不存在");
        var model = JsonSerializer.Deserialize<LLM_ModelEntity>(reader.GetString(2), ModelJson)
            ?? throw new AiModelPurposeException("ModelUnavailable", $"模型用途 {purpose} 绑定的模型无法读取");
        ValidateModel(purpose, model);
        return new ResolvedAiModel(model, revision);
    }

    private static void ValidateModel(string purpose, LLM_ModelEntity model)
    {
        if (model.IsDeleted || model.IsEnabled != true)
            throw new AiModelPurposeException("ModelDisabled", $"模型用途 {purpose} 绑定的模型已停用或删除");
        if (!AiModelPurposes.IsAllowedModelType(purpose, model.ModelType))
            throw new AiModelPurposeException("ModelTypeMismatch", $"模型用途 {purpose} 与模型类型不匹配");
        if (string.IsNullOrWhiteSpace(model.ModelCode) || !HasValidEndpoint(model.BaseUrl))
            throw new AiModelPurposeException("ModelIncomplete", $"模型用途 {purpose} 绑定的模型缺少编码或基础地址");
    }

    private static bool HasValidEndpoint(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme is "http" or "https";

    public async Task<AiModelPurposeListDto> ListAsync(CancellationToken token = default)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenAsync(token);
        var bindingRows = new Dictionary<string, (long? ModelId, long Revision)>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            "SELECT purpose_code, model_id, revision FROM public.ai_model_purpose_binding", connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                bindingRows[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2));

        var summaries = new Dictionary<long, (string Name, string Code, string Type, string BaseUrl, bool Enabled, bool Deleted)>();
        await using (var command = new NpgsqlCommand(
            "SELECT id, modelname, modelcode, modeltype, baseurl, isenabled, isdeleted FROM public.ai_llm_model", connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                summaries[reader.GetInt64(0)] = (
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    !reader.IsDBNull(5) && reader.GetBoolean(5),
                    !reader.IsDBNull(6) && reader.GetBoolean(6));

        var bindings = AiModelPurposes.All.Select(purpose =>
        {
            bindingRows.TryGetValue(purpose.Code, out var binding);
            var summary = default((string Name, string Code, string Type, string BaseUrl, bool Enabled, bool Deleted));
            var found = binding.ModelId.HasValue && summaries.TryGetValue(binding.ModelId.Value, out summary);
            var status = !binding.ModelId.HasValue ? "Unbound" : !found ? "ModelUnavailable"
                : !summary.Enabled || summary.Deleted ? "ModelDisabled"
                : !AiModelPurposes.IsAllowedModelType(purpose.Code, summary.Type) ? "ModelTypeMismatch"
                : string.IsNullOrWhiteSpace(summary.Code) || !HasValidEndpoint(summary.BaseUrl)
                    ? "ModelIncomplete" : "Ready";
            return new AiModelPurposeBindingDto(purpose.Code, purpose.DisplayName,
                binding.ModelId?.ToString(CultureInfo.InvariantCulture), found ? summary.Name : null,
                found ? summary.Code : null, found ? summary.Type : null, binding.Revision, status);
        }).ToArray();
        var models = summaries.Where(row => row.Value.Enabled && !row.Value.Deleted
                && !string.IsNullOrWhiteSpace(row.Value.Code) && HasValidEndpoint(row.Value.BaseUrl)
                && (AiModelPurposes.IsAllowedModelType(AiModelPurposes.DefaultChat, row.Value.Type)
                    || AiModelPurposes.IsAllowedModelType(AiModelPurposes.MemoryEmbedding, row.Value.Type)))
            .OrderBy(row => row.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(row => new AiModelOptionDto(row.Key.ToString(CultureInfo.InvariantCulture), row.Value.Name,
                row.Value.Code, row.Value.Type, true)).ToArray();
        return new AiModelPurposeListDto(bindings, models);
    }

    public async Task<AiModelPurposeBindingDto> SetAsync(string purposeCode, string modelId,
        long expectedRevision, CancellationToken token = default)
    {
        if (!AiModelPurposes.IsKnown(purposeCode))
            throw new AiModelPurposeException("UnknownPurpose", $"未知模型用途：{purposeCode}");
        if (!long.TryParse(modelId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new AiModelPurposeException("InvalidModelId", "模型 ID 无效");
        if (expectedRevision < 0)
            throw new AiModelPurposeException("InvalidRevision", "模型用途修订号无效");
        await EnsureSchemaAsync();
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        LLM_ModelEntity model;
        await using (var modelCommand = new NpgsqlCommand(
            "SELECT to_jsonb(m)::text FROM public.ai_llm_model m WHERE m.id=@id FOR SHARE", connection, transaction))
        {
            modelCommand.Parameters.AddWithValue("id", id);
            var json = await modelCommand.ExecuteScalarAsync(token) as string;
            if (json is null)
                throw new AiModelPurposeException("ModelUnavailable", "指定模型不存在");
            model = JsonSerializer.Deserialize<LLM_ModelEntity>(json, ModelJson)
                ?? throw new AiModelPurposeException("ModelUnavailable", "指定模型无法读取");
            ValidateModel(purposeCode, model);
        }
        long nextRevision;
        await using (var command = new NpgsqlCommand("""
            UPDATE public.ai_model_purpose_binding
            SET model_id=@model, revision=revision+1, updated_at=now()
            WHERE purpose_code=@purpose AND revision=@expected
            RETURNING revision
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("model", id);
            command.Parameters.AddWithValue("purpose", purposeCode);
            command.Parameters.AddWithValue("expected", expectedRevision);
            var next = await command.ExecuteScalarAsync(token);
            if (next is null)
                throw new AiModelPurposeException("RevisionConflict", "用途绑定已被其他人修改，请刷新后重试");
            nextRevision = (long)next;
        }
        await transaction.CommitAsync(token);
        var displayName = AiModelPurposes.All.Single(purpose => purpose.Code == purposeCode).DisplayName;
        return new AiModelPurposeBindingDto(purposeCode, displayName,
            id.ToString(CultureInfo.InvariantCulture), model.ModelName, model.ModelCode,
            model.ModelType, nextRevision, "Ready");
    }
}
