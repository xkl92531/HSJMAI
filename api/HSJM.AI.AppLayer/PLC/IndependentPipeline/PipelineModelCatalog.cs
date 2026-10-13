#nullable enable
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Npgsql;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

/// <summary>Independent test-database model configuration and OpenAI-compatible transport; no old generation services.</summary>
public sealed class PipelineModelCatalog(PostgresPipelineStore store,IHttpClientFactory clients) : ISelfScopedAutoInject
{
    public async Task<LLM_ModelEntity> LoadAsync(long modelId,CancellationToken token) {
        await using var connection=new NpgsqlConnection(store.ConnectionString);await connection.OpenAsync(token);
        await using var cmd=new NpgsqlCommand("select to_jsonb(m)::text from public.ai_llm_model m where id=@id and isenabled=true and not isdeleted",connection);
        cmd.Parameters.AddWithValue("id",modelId);
        var json=await cmd.ExecuteScalarAsync(token) as string ?? throw new PipelineException("ModelUnavailable");
        return JsonSerializer.Deserialize<LLM_ModelEntity>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
            ?? throw new PipelineException("ModelUnavailable");
    }
    public static JsonObject BuildRequest(LLM_ModelEntity model,string system,string user) {
        var body=string.IsNullOrWhiteSpace(model.CustomRequestParams)?new JsonObject():JsonNode.Parse(model.CustomRequestParams) as JsonObject
            ?? throw new PipelineException("InvalidModelParameters");
        body["model"]=model.ModelCode;
        body["messages"]=new JsonArray(new JsonObject{{"role","system"},{"content",system}},new JsonObject{{"role","user"},{"content",user}});
        body["stream"]=false;
        if(model.MaxTokens>0)body["max_tokens"]=model.MaxTokens;
        body.Remove("stream_options");
        return body;
    }
    public async Task<string> CompleteAsync(LLM_ModelEntity model,string system,string user,CancellationToken token,JsonObject? responseFormat=null) {
        if(!Uri.TryCreate(model.BaseUrl,UriKind.Absolute,out var endpoint)||endpoint.Scheme is not("http" or "https"))
            throw new PipelineException("ExplicitModelEndpointRequired");
        using var client=clients.CreateClient("IndependentPlcPipeline");client.Timeout=Timeout.InfiniteTimeSpan;
        using var request=new HttpRequestMessage(HttpMethod.Post,model.BaseUrl.TrimEnd('/')+"/chat/completions");
        if(!string.IsNullOrWhiteSpace(model.ApiKey))request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",model.ApiKey);
        var body=BuildRequest(model,system,user);
        if(responseFormat!=null)body["response_format"]=responseFormat.DeepClone();
        request.Content=JsonContent.Create(body);
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(!response.IsSuccessStatusCode)throw new PipelineException("ModelHttp"+(int)response.StatusCode);
        using var json=await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token),cancellationToken:token);
        return ReadCompleteContent(json.RootElement);
    }
    public static string ReadCompleteContent(JsonElement json) {
        if(!json.TryGetProperty("choices",out var choices)||choices.ValueKind!=JsonValueKind.Array||choices.GetArrayLength()==0)throw new PipelineException("EmptyModelResponse");
        if(!choices[0].TryGetProperty("finish_reason",out var finish)||finish.GetString()!="stop")throw new PipelineException("IncompleteModelResponse");
        if(!choices[0].TryGetProperty("message",out var message)||!message.TryGetProperty("content",out var content)||content.ValueKind!=JsonValueKind.String)
            throw new PipelineException("UnsupportedModelContent");
        var value=content.GetString();
        return string.IsNullOrWhiteSpace(value)?throw new PipelineException("EmptyModelResponse"):value;
    }
}
