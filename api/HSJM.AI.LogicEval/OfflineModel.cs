using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;
using HSJM.AI.IDbService;
using Npgsql;

namespace HSJM.AI.LogicEval;

/// <summary>离线评测的模型：与发布器同一用途绑定、同一参数（UnderstandingModelSetup）、同一指纹、同一请求体与 finish_reason 检查，
/// 缓存键与生产一致（cache-push 后发布器直接命中）。只比生产多一处：绕过本机代理（UseProxy=false）。</summary>
sealed class OfflineModel(LLM_ModelEntity model, string fingerprint) : IDisposable
{
    readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(15) };

    public LLM_ModelEntity Model { get; } = model;
    public string Fingerprint { get; } = fingerprint;
    public string ModelName => Model.ModelCode ?? Fingerprint;

    /// <summary>计划 5 之前离线工具的指纹（只用于 --legacy-cache 迁移旧缓存）：baseurl|modelcode|thinking|0.1|16000。</summary>
    public string LegacyFingerprint => PipelineHash.Of(Model.BaseUrl + "|" + Model.ModelCode + "|thinking|0.1|16000");

    public static async Task<OfflineModel> LoadAsync(NpgsqlConnection db)
    {
        await using var cmd = new NpgsqlCommand("""
            select to_jsonb(m)::text from ai_model_purpose_binding b
            join ai_llm_model m on m.id=b.model_id
            where b.purpose_code='independent_plc' and m.isenabled=true and not m.isdeleted
            """, db);
        var json = await cmd.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException("网站上未绑定可用的「独立 PLC 解析与候选分析」模型");
        // 与 AiModelPurposeBindingService.ResolveAsync 同一反序列化（属性名不区分大小写），指纹才与生产一致
        var raw = JsonSerializer.Deserialize<LLM_ModelEntity>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("绑定的模型无法读取");
        var (prepared, fingerprint) = UnderstandingModelSetup.Prepare(raw);
        return new OfflineModel(prepared, fingerprint);
    }

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct, string? baseUrlOverride = null)
    {
        var baseUrl = baseUrlOverride ?? Model.BaseUrl;
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
        if (!string.IsNullOrWhiteSpace(Model.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Model.ApiKey);
        request.Content = new StringContent(PipelineModelCatalog.BuildRequest(Model, system, user).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"模型请求失败：HTTP {(int)response.StatusCode}");
        using var json = JsonDocument.Parse(text);
        // 与生产同一读取：回复被截断（finish_reason 不是 stop）或为空时抛出，该 FB/程序段记 Failed
        return PipelineModelCatalog.ReadCompleteContent(json.RootElement);
    }

    public void Dispose() => http.Dispose();
}
