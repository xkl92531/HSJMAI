#nullable enable
using System.Text.Json.Nodes;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>理解步骤调用模型的固定参数（温度、思考开关、最大输出），发布器与离线评测共用：
/// 同一条网站模型配置得到同一个指纹与请求体，缓存键因此一致（final-review Minor 3）。</summary>
public static class UnderstandingModelSetup
{
    public const double Temperature = 0.1;
    public const int MaxTokens = 16000;

    /// <summary>原地改参数并返回同一实例与指纹。调用方传入的必须是刚从库里反序列化出的新实体。</summary>
    public static (LLM_ModelEntity Model, string Fingerprint) Prepare(LLM_ModelEntity model)
    {
        var parameters = UnderstandingPublishGuard.ParseParameters(model.CustomRequestParams);
        parameters["temperature"] = Temperature;
        var template = parameters["chat_template_kwargs"] as JsonObject ?? new JsonObject();
        template["enable_thinking"] = true;
        parameters["chat_template_kwargs"] = template;
        model.CustomRequestParams = parameters.ToJsonString();
        model.MaxTokens = MaxTokens;
        return (model, PipelineModelInference.ModelFingerprint(model));
    }
}
