#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>Test-only required all-rule AI wording pass, backed by source-derived plain language and a durable cache.</summary>
public sealed class Original353RuleTextService(
    IConfiguration configuration, PostgresPipelineStore store, PipelineModelCatalog catalog,
    IAiModelPurposeResolver purposeResolver,
    ILogger<Original353RuleTextService> logger) : ISelfScopedAutoInject
{
    private sealed record Candidate(long UnitId, PipelineRule Rule, string Key, string CacheKey,
        string PlainLanguage, IReadOnlyList<string> Evidence);

    /// <summary>refreshUnits（审查 P3 refreshAi，2026-10-02）：这些程序段的规则措辞不读缓存、重新生成，成功后覆盖写缓存。</summary>
    public async Task<IReadOnlyDictionary<(long UnitId, string RuleKey), Original353RuleWording>>
        BuildAsync(PipelineSnapshot source, Original353ProjectResult generated, CancellationToken token,
            IReadOnlySet<long>? refreshUnits = null)
    {
        var selection = await purposeResolver.ResolveAsync(AiModelPurposes.ConstraintDescription, token);
        var model = selection.Model;
        var parameters = string.IsNullOrWhiteSpace(model.CustomRequestParams)
            ? new JsonObject() : JsonNode.Parse(model.CustomRequestParams)?.AsObject() ?? new JsonObject();
        parameters["temperature"] = 0.1;
        var template = parameters["chat_template_kwargs"] as JsonObject ?? new JsonObject();
        template["enable_thinking"] = false;
        parameters["chat_template_kwargs"] = template;
        parameters.Remove("response_format");
        model.CustomRequestParams = parameters.ToJsonString();
        model.MaxTokens = 6000;

        var modelHash = PipelineModelInference.ModelFingerprint(model);
        var byUnit = source.Units.ToDictionary(unit => unit.Id);
        // 只挂新流程规则（AI:*/Review:*/Code:*）的程序段不需要 AI 说明（映射器退回模板）：缺源程序段或白话文时跳过，不让发布失败
        var unitsForAi = new List<PipelineUnit>();
        var skippedNewFlowOnlyUnits = 0;
        foreach (var unit in generated.Units)
        {
            var selected = Original353AiWordingPolicy.Rules(unit);
            if (selected.Count == 0) continue;
            if (byUnit.TryGetValue(unit.LogicUnitId, out var found)) unitsForAi.Add(found);
            else if (!Original353AiWordingPlan.NewFlowOnly(selected))
                throw new PipelineException($"AiDescriptionSourceMissing:{unit.LogicUnitId}");
        }
        var plainByUnit = await LoadOrBuildPlainLanguageAsync(source.ProjectId,
            unitsForAi.DistinctBy(unit => unit.Id).ToArray(), source.Tags, token);
        var candidates = new List<Candidate>();
        foreach (var unit in generated.Units)
        {
            var selected = Original353AiWordingPolicy.Rules(unit);
            if (selected.Count == 0) continue;
            if (!plainByUnit.TryGetValue(unit.LogicUnitId, out var plainLanguage))
            {
                if (!Original353AiWordingPlan.NewFlowOnly(selected))
                    throw new PipelineException($"AiDescriptionPlainLanguageMissing:{unit.LogicUnitId}");
                skippedNewFlowOnlyUnits++;
                logger.LogWarning("XMLV2 AI wording skipped for new-flow-only unit {UnitId}: no plain language; template text is used", unit.LogicUnitId);
                continue;
            }
            foreach (var rule in selected)
            {
                var evidence = unit.SafetyRules.Where(item => item.Rule.Key == rule.Key)
                    .SelectMany(item => item.Evidence)
                    .Concat(unit.ConditionalFeedbackRules.Where(item => item.Rule.Key == rule.Key)
                        .SelectMany(item => item.Evidence)).ToArray();
                var key = unit.LogicUnitId + ":" + rule.Key;
                var cacheKey = PipelineHash.Of(JsonSerializer.Serialize(new {
                    Original353RuleTextAi.PromptVersion, Model = modelHash, rule.Key,
                    rule.Description, rule.Applicability, rule.RequiresSettlingEvidence,
                    rule.BypassSignals, rule.MaskedFeedbackInputs, PlainLanguage = plainLanguage, Evidence = evidence
                }));
                if (Original353RulePresentation.IsDoorSignalCheck(rule))
                    cacheKey = PipelineHash.Of(cacheKey + ":operator-door-text-1:" + rule.DisplayLabel);
                candidates.Add(new(unit.LogicUnitId, rule, key, cacheKey, plainLanguage, evidence));
            }
        }
        if (candidates.Count == 0) return Empty();

        var result = new Dictionary<(long, string), Original353RuleWording>();
        await using var db = new NpgsqlConnection(store.ConnectionString);
        await db.OpenAsync(token);
        await EnsureCacheAsync(db, token);
        var cached = await LoadCacheAsync(db, source.ProjectId, candidates, token);
        foreach (var candidate in candidates)
            if (refreshUnits?.Contains(candidate.UnitId) != true
                && cached.TryGetValue(candidate.CacheKey, out var wording)
                && Original353AiWordingPolicy.IsAccepted(candidate.Rule, wording))
                result[(candidate.UnitId, candidate.Rule.Key)] = wording;
        var cacheHits = result.Count;
        var pending = candidates.Where(item => !result.ContainsKey((item.UnitId, item.Rule.Key))).ToArray();
        var callSeconds = Math.Clamp(configuration.GetValue<int?>("TestConstraintV2:DescriptionTimeoutSeconds") ?? 45, 15, 120);
        logger.LogInformation("XMLV2 all-AI wording start project={ProjectId} total={Total} cached={Cached} pending={Pending}",
            source.ProjectId, candidates.Count, cacheHits, pending.Length);

        async Task<bool> GenerateBatchAsync(Candidate[] batch, int attempts)
        {
            var prompt = JsonSerializer.Serialize(new {
                Purpose = "每条约束都生成中文说明。name原样复制NameHint；名称用于响应匹配，界面名称仍由程序决定。结构、信号及条件不可修改。",
                PlainLanguage = batch[0].PlainLanguage,
                Items = batch.Select(item => new {
                    key = item.Key, NameHint = Original353ModeConflictRules.IsModeRule(item.Rule)
                        ? Original353RuleText.Render(item.Rule, new("ModeCheck", "")).Name : Original353RulePresentation.IsDoorSignalCheck(item.Rule)
                        ? item.Rule.DisplayLabel + "控制信号检查" : item.Rule.Signals[0].Name,
                    OperatorFacingLabel = Original353RulePresentation.IsDoorSignalCheck(item.Rule) ? item.Rule.DisplayLabel : null,
                    ModeConflictLabel = Original353ModeConflictRules.IsModeRule(item.Rule) ? item.Rule.DisplayLabel : null,
                    item.Rule.Operator, item.Rule.Signals, item.Rule.Applicability,
                    item.Rule.BypassSignals, item.Rule.RequiresSettlingEvidence,
                    item.Rule.MaskedFeedbackInputs, CodeEvidence = item.Evidence,
                    DescriptionRequirements = Original353ModeConflictRules.IsModeRule(item.Rule)
                        ? "名称原样复制NameHint。只说本工位的自动（或自动运行）与手动模式同时开启时，请检查模式选择。区分自动模式与自动运行，不写DB路径和布尔术语。"
                        : Original353RulePresentation.IsDoorSignalCheck(item.Rule)
                        ? "面向不懂PLC的操作员，只写本安全门控制信号异常时请检查相关控制线路。不得说安全门已经打开/关闭/未锁好，不写DB路径、true/false、一真一假、快照、反相。"
                        : item.Rule.Operator switch {
                        "SAME_STATE" => "必须保留：在程序执行条件成立时应保持相同状态；不得扩展为任意时刻都必须相同。",
                        "EXACTLY_ONE" => "必须保留：在程序执行后的同一快照中应一真一假；不要另加最多一个、至少一个、始终等其他判断。",
                        _ => "保留VerifiedMeaning中的所有适用条件，只润色中文，不改变关系。"
                    },
                    VerifiedMeaning = Original353RuleText.Render(item.Rule,
                        Original353ActivationPolicy.Classify(item.Rule, source.Tags)).Description
                }).ToArray()
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var call = CancellationTokenSource.CreateLinkedTokenSource(token);
                    call.CancelAfter(TimeSpan.FromSeconds(callSeconds));
                    var response = await catalog.CompleteAsync(model, Original353RuleTextAi.SystemPrompt,
                        prompt + (attempt > 1 ? "\n上次响应未通过格式或说明校验，请严格逐条返回JSON并保留所有信号、时间和旁路条件。" : ""), call.Token);
                    var accepted = Original353RuleTextAi.TryParse(response, batch.ToDictionary(item => item.Key, item => item.Rule));
                    if (accepted.Count != batch.Length)
                    {
                        logger.LogWarning("XMLV2 AI wording rejected project={ProjectId} unit={UnitId} batch={Count} attempt={Attempt}",
                            source.ProjectId, batch[0].UnitId, batch.Length, attempt);
                        if (attempt == attempts)
                            logger.LogWarning("XMLV2 rejected wording response unit={UnitId}: {Response}",
                                batch[0].UnitId, response.Length <= 2000 ? response : response[..2000]);
                        continue;
                    }
                    var rows = batch.Select(item => (item.CacheKey, Wording: accepted[item.Key] with {
                        PlainLanguage = item.PlainLanguage
                    })).ToArray();
                    // Commit every successful model response so a retry never starts over.
                    await SaveBatchAsync(db, source.ProjectId, rows, token);
                    for (var i = 0; i < batch.Length; i++)
                        result[(batch[i].UnitId, batch[i].Rule.Key)] = rows[i].Wording;
                    logger.LogInformation("XMLV2 all-AI wording progress project={ProjectId} completed={Completed} total={Total} cached={Cached}",
                        source.ProjectId, result.Count, candidates.Count, cacheHits);
                    return true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "XMLV2 AI wording attempt failed project={ProjectId} unit={UnitId} attempt={Attempt}",
                        source.ProjectId, batch[0].UnitId, attempt);
                }
            }
            return false;
        }

        // Share each plain-language document (already clipped to the limit) once per batch.
        // 新流程规则（AI:*/Review:*/Code:*）单独成批、只试一次（失败即由映射器退回模板），不拖累同段其它规则的重试
        foreach (var batch in Original353AiWordingPlan.Batches(pending, item => item.UnitId, item => item.Rule))
        {
            if (await GenerateBatchAsync(batch.Items, batch.Attempts)) continue;
            if (batch.RetryEach && batch.Items.Length > 1)
                foreach (var item in batch.Items) await GenerateBatchAsync([item], 3);
        }

        Original353AiWordingPolicy.RequireComplete(generated, result);
        var newFlowFallback = candidates.Count(item => UnderstandingProofs.IsNewFlow(item.Rule.Proof) && !result.ContainsKey((item.UnitId, item.Rule.Key)));
        logger.LogInformation("XMLV2 all-AI wording completed project={ProjectId} total={Total} cached={Cached} generated={Generated} newFlowTemplateFallback={Fallback} newFlowOnlyUnitsSkipped={Skipped}",
            source.ProjectId, candidates.Count, cacheHits, result.Count - cacheHits, newFlowFallback, skippedNewFlowOnlyUnits);
        return result;
    }

    public static string PlainLanguageKey(PipelineUnit unit, string tagsFingerprint)
        => PipelineHash.Of(JsonSerializer.Serialize(new {
            Renderer = RendererFor(unit), unit.RawContent, unit.BlockName,
            unit.NetworkOrder, Tags = tagsFingerprint
        }));

    /// <summary>白话文渲染版本，进缓存键。logic-ir-2：超长译文截断（旧缓存里可能有未截断的超长译文，须重生成）。
    /// logic-ir-3：指令/块的 eno 不再等于 en，改写为「en 且〔…的 ENO：执行未出错〕」。</summary>
    public const string PlainLanguageRenderer = "logic-ir-3";

    /// <summary>SCL 程序段改为逐语句翻译后的白话文版本：只让原文含 StructuredText 的程序段失效，其它程序段的缓存保留。</summary>
    public const string SclPlainLanguageRenderer = "logic-ir-3-scl";

    /// <summary>程序段白话文的渲染版本：原文含 &lt;StructuredText（SCL 程序段，以及带 SCL 前后置操作的顺控块）用 SCL 版本，其它不变。</summary>
    public static string RendererFor(PipelineUnit unit) =>
        (unit.RawContent ?? "").Contains("<StructuredText", StringComparison.Ordinal) ? SclPlainLanguageRenderer : PlainLanguageRenderer;

    /// <summary>单个程序段白话文给措辞模型的上限（字符）。设计 §3.2 规则 5：按 6 万字分段；模型上下文 100k token，再长不安全。</summary>
    public const int PlainLanguageMaxChars = 60000;

    /// <summary>截断标记行的长度余量（标记含两个长度数字，远小于此值）。</summary>
    const int ClipMarkerAllowance = 200;

    /// <summary>给 AI 的程序段白话文：新翻译器逐部件如实翻译（不再使用会写出「始终成立」的旧渲染器）。</summary>
    /// <param name="tags">项目点表（SCL 里 Bool 点的逻辑运算按逻辑翻译）；与 PlainLanguageKey 的点表指纹同源。</param>
    public static string RenderPlainLanguage(PipelineUnit unit, IReadOnlyList<PipelineTag>? tags = null)
        => Understanding.IrText.Unit(Understanding.UnitTranslator.Translate(unit, tags));

    /// <summary>超长译文截到上限内最后一个换行处，并追加一行说明原文与保留长度；不超限原样返回。
    /// 实测 GRAPH 程序段译文可达 26 万字，整段丢弃会让该段规则缺白话文、整个 XMLV2 发布中止。</summary>
    public static string ClipPlainLanguage(string text, int max = PlainLanguageMaxChars)
    {
        if (text.Length <= max) return text;
        string Marker(int keptLength) => $"\n…（程序段翻译过长：原文 {text.Length} 字，此处仅保留前 {keptLength} 字）";
        // 连同标记一起不超过上限：结果再截一次保持不变（缓存读出后还会再过一遍）。
        var budget = Math.Max(1, max - Marker(max).Length);
        var cut = text.LastIndexOf('\n', budget);
        var kept = (cut > 0 ? text[..cut] : text[..budget]).TrimEnd('\r');
        return kept + Marker(kept.Length);
    }

    /// <summary>产品用的单段渲染步骤：渲染 + 截断 + 可用性检查。任何异常都收在 error 里不外抛，
    /// 一个程序段翻译失败不能中断其它程序段。rawLength 为截断前长度（渲染失败为 -1）。</summary>
    public static bool TryRenderPlainLanguage(PipelineUnit unit, out string text, out int rawLength, out Exception? error,
        IReadOnlyList<PipelineTag>? tags = null)
    {
        try
        {
            var raw = RenderPlainLanguage(unit, tags);
            rawLength = raw.Length;
            text = ClipPlainLanguage(raw);
            error = null;
            return UsablePlainLanguage(text);
        }
        catch (Exception ex)
        {
            text = "";
            rawLength = -1;
            error = ex;
            return false;
        }
    }

    public static bool TryRenderPlainLanguage(PipelineUnit unit, out string text, out Exception? error)
        => TryRenderPlainLanguage(unit, out text, out _, out error);

    private async Task<Dictionary<long, string>> LoadOrBuildPlainLanguageAsync(long projectId,
        IReadOnlyList<PipelineUnit> units, IReadOnlyList<PipelineTag> tags, CancellationToken token)
    {
        var result = new Dictionary<long, string>();
        if (units.Count == 0) return result;
        var tagsFingerprint = PipelineHash.Of(JsonSerializer.Serialize(tags
            .OrderBy(tag => tag.Name, StringComparer.Ordinal)
            .ThenBy(tag => tag.Address, StringComparer.Ordinal)));
        var keys = units.ToDictionary(unit => unit.Id,
            unit => PlainLanguageKey(unit, tagsFingerprint));
        var contentByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var db = new NpgsqlConnection(store.ConnectionString);
        await db.OpenAsync(token);
        await EnsureCacheAsync(db, token);
        await using (var command = new NpgsqlCommand("""
            SELECT source_hash,content FROM plc_independent.plain_language
            WHERE project_id=@project AND source_hash=ANY(@keys)
            """, db))
        {
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("keys", keys.Values.Distinct().ToArray());
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                contentByKey.Add(reader.GetString(0), reader.GetString(1));
        }
        var toSave = new Dictionary<string, string>(StringComparer.Ordinal);
        var hits = 0;
        foreach (var unit in units)
        {
            token.ThrowIfCancellationRequested();
            var key = keys[unit.Id];
            if (contentByKey.TryGetValue(key, out var cached))
            {
                var clippedCache = ClipPlainLanguage(cached);
                if (UsablePlainLanguage(clippedCache))
                {
                    result[unit.Id] = clippedCache;
                    hits++;
                    continue;
                }
            }
            // Oversize text is clipped with an explicit marker; a failed explanation is never given to the model.
            if (TryRenderPlainLanguage(unit, out var plain, out _, out var error, tags))
            {
                result[unit.Id] = plain;
                toSave[key] = plain;
            }
            else if (error is OperationCanceledException && token.IsCancellationRequested)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            else if (error != null)
                logger.LogWarning(error, "XMLV2 plain-language render failed for unit {UnitId}", unit.Id);
        }
        if (toSave.Count > 0)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO plc_independent.plain_language(project_id,source_hash,content)
                SELECT @project,entry.source_hash,entry.content
                FROM unnest(@keys::text[],@contents::text[]) AS entry(source_hash,content)
                ON CONFLICT(project_id,source_hash) DO UPDATE SET content=EXCLUDED.content
                """, db);
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("keys", toSave.Keys.ToArray());
            command.Parameters.AddWithValue("contents", toSave.Values.ToArray());
            await command.ExecuteNonQueryAsync(token);
        }
        logger.LogInformation("XMLV2 plain-language project={ProjectId} selectedUnits={Selected} cacheHits={Hits} generated={Generated}",
            projectId, units.Count, hits, toSave.Count);
        return result;
    }

    internal static bool UsablePlainLanguage(string value)
        => value.Length > 0 && value.Length <= PlainLanguageMaxChars + ClipMarkerAllowance
           && !value.Contains("XML 解析失败", StringComparison.Ordinal)
           && !value.Contains("XML 无法解析", StringComparison.Ordinal);

    private static async Task EnsureCacheAsync(NpgsqlConnection db, CancellationToken token)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS plc_independent;
            CREATE TABLE IF NOT EXISTS plc_independent.rule_text_cache (
              project_id bigint NOT NULL, cache_key text NOT NULL,
              name text NOT NULL, description text NOT NULL, plain_language text NOT NULL,
              origin text NOT NULL DEFAULT 'AIValidated',
              created_at timestamptz NOT NULL DEFAULT now(),
              PRIMARY KEY(project_id, cache_key));
            ALTER TABLE plc_independent.rule_text_cache ADD COLUMN IF NOT EXISTS origin text NOT NULL DEFAULT 'AIValidated';
            CREATE TABLE IF NOT EXISTS plc_independent.plain_language (
              project_id bigint NOT NULL, source_hash text NOT NULL, content text NOT NULL,
              created_at timestamptz NOT NULL DEFAULT now(),
              PRIMARY KEY(project_id,source_hash));
            """;
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<Dictionary<string, Original353RuleWording>> LoadCacheAsync(
        NpgsqlConnection db, long projectId, IReadOnlyList<Candidate> candidates, CancellationToken token)
    {
        var result = new Dictionary<string, Original353RuleWording>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("""
            SELECT cache_key,name,description,plain_language,origin FROM plc_independent.rule_text_cache
            WHERE project_id=@project AND cache_key=ANY(@keys) AND origin='AIValidated'
            """, db);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("keys", candidates.Select(item => item.CacheKey).Distinct().ToArray());
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(reader.GetString(0), new(reader.GetString(1), reader.GetString(2),
                reader.GetString(4) == "AIValidated" ? "AICache" : reader.GetString(4), reader.GetString(3)));
        return result;
    }

    private static async Task SaveBatchAsync(NpgsqlConnection db, long projectId,
        IReadOnlyList<(string CacheKey, Original353RuleWording Wording)> rows, CancellationToken token)
    {
        if (rows.Count == 0) return;
        var unique = rows.DistinctBy(row => row.CacheKey).ToArray();
        await using var command = new NpgsqlCommand("""
            INSERT INTO plc_independent.rule_text_cache(project_id,cache_key,name,description,plain_language,origin)
            SELECT @project,entry.cache_key,entry.name,entry.description,entry.plain_language,entry.origin
            FROM unnest(@keys::text[],@names::text[],@descriptions::text[],@plains::text[],@origins::text[])
              AS entry(cache_key,name,description,plain_language,origin)
            ON CONFLICT(project_id,cache_key) DO UPDATE SET
              name=EXCLUDED.name,description=EXCLUDED.description,
              plain_language=EXCLUDED.plain_language,origin=EXCLUDED.origin
            """, db);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("keys", unique.Select(row => row.CacheKey).ToArray());
        command.Parameters.AddWithValue("names", unique.Select(row => row.Wording.Name).ToArray());
        command.Parameters.AddWithValue("descriptions", unique.Select(row => row.Wording.Description).ToArray());
        command.Parameters.AddWithValue("plains", unique.Select(row => row.Wording.PlainLanguage ?? "").ToArray());
        command.Parameters.AddWithValue("origins", unique.Select(row => row.Wording.Origin).ToArray());
        await command.ExecuteNonQueryAsync(token);
    }

    private static IReadOnlyDictionary<(long, string), Original353RuleWording> Empty()
        => new Dictionary<(long, string), Original353RuleWording>();
}

/// <summary>措辞阶段的分批规划（纯函数，可单测）。</summary>
public static class Original353AiWordingPlan
{
    public sealed record Batch<T>(T[] Items, int Attempts, bool RetryEach);

    /// <summary>程序段所选规则全是新流程规则（Proof=AI:*/Review:*/Code:*）：它们缺 AI 说明时由映射器退回模板，不需要白话文。</summary>
    public static bool NewFlowOnly(IReadOnlyList<PipelineRule> rules) =>
        rules.Count > 0 && rules.All(rule => UnderstandingProofs.IsNewFlow(rule.Proof));

    /// <summary>按程序段分组（保持首次出现顺序），每段内非新流程规则每 4 条一批（整批试 2 次，失败再逐条各试 3 次）；
    /// 新流程规则（AI:*/Review:*/Code:*）单独成批（每 4 条一批）、只试 1 次、失败不逐条重试。</summary>
    public static IReadOnlyList<Batch<T>> Batches<T>(IEnumerable<T> pending, Func<T, long> unitOf, Func<T, PipelineRule> ruleOf)
    {
        var result = new List<Batch<T>>();
        foreach (var unit in pending.GroupBy(unitOf))
        {
            foreach (var chunk in unit.Where(item => !UnderstandingProofs.IsNewFlow(ruleOf(item).Proof)).Chunk(4))
                result.Add(new(chunk, 2, true));
            foreach (var chunk in unit.Where(item => UnderstandingProofs.IsNewFlow(ruleOf(item).Proof)).Chunk(4))
                result.Add(new(chunk, 1, false));
        }
        return result;
    }
}
