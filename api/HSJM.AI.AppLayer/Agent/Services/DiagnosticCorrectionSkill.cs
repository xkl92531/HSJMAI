//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V1  描述:V1_多场景诊断校正技能  添加时间:2026/4/1
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// AI诊断校正技能（多场景支持）
    /// 支持：constraint=约束生成，stuck_point=卡滞诊断，energy_flow=能量流分析
    /// </summary>
    public class DiagnosticCorrectionSkill : ISelfScopedAutoInject
    {
        private readonly DiagnosticMemoryService _memoryService;
        private readonly IDiagnosticCorrectionLogService _correctionLogService;
        private readonly IAiModelPurposeResolver _purposeResolver;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DiagnosticCorrectionSkill> _logger;

        private const int AI_CALL_TIMEOUT_SECONDS = 120;

        public DiagnosticCorrectionSkill(
            DiagnosticMemoryService memoryService,
            IDiagnosticCorrectionLogService correctionLogService,
            IAiModelPurposeResolver purposeResolver,
            IHttpClientFactory httpClientFactory,
            ILogger<DiagnosticCorrectionSkill> logger)
        {
            _memoryService = memoryService;
            _correctionLogService = correctionLogService;
            _purposeResolver = purposeResolver;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// 处理诊断校正反馈
        /// </summary>
        public async Task<CorrectionProcessResult> ProcessCorrectionAsync(
            long userId,
            string sceneType,
            long? productionLineId,
            string userFeedback,
            string signalName = null)
        {
            try
            {
                _logger.LogInformation("[DiagnosticCorrection] 处理校正反馈: UserId={UserId}, Scene={Scene}, Signal={Signal}", 
                    userId, sceneType, signalName);

                var patternSummary = await SummarizePatternAsync(sceneType, userFeedback);
                if (patternSummary == null)
                {
                    return new CorrectionProcessResult
                    {
                        Success = false,
                        ErrorMessage = "AI归纳失败，请稍后重试"
                    };
                }

                var correctionLog = new DiagnosticCorrectionLogEntity
                {
                    UserId = userId,
                    SceneType = sceneType,
                    ProductionLineId = productionLineId,
                    SignalName = signalName,
                    UserFeedback = userFeedback,
                    AiSummary = JsonSerializer.Serialize(patternSummary),
                    CorrectionStatus = "pending",
                    Generalizable = patternSummary.Generalizable,
                    ApplicableScope = patternSummary.ApplicableScope
                };

                await _correctionLogService.InsertAsync(correctionLog);

                _logger.LogInformation("[DiagnosticCorrection] 校正日志创建成功: Id={Id}, Scene={Scene}", correctionLog.Id, sceneType);

                return new CorrectionProcessResult
                {
                    Success = true,
                    CorrectionLogId = correctionLog.Id,
                    PatternSummary = patternSummary,
                    Status = "awaiting_confirmation"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DiagnosticCorrection] 处理校正反馈失败");
                return new CorrectionProcessResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// 确认校正（用户点击确认后）
        /// </summary>
        public async Task<CorrectionConfirmResult> ConfirmCorrectionAsync(long correctionLogId)
        {
            try
            {
                var correctionLog = await _correctionLogService.GetByIdAsync(correctionLogId);
                if (correctionLog == null)
                {
                    return new CorrectionConfirmResult { Success = false, ErrorMessage = "校正记录不存在" };
                }

                var patternSummary = JsonSerializer.Deserialize<PatternSummary>(correctionLog.AiSummary);
                if (patternSummary == null)
                {
                    return new CorrectionConfirmResult { Success = false, ErrorMessage = "AI归纳结果解析失败" };
                }

                correctionLog.UserConfirmed = true;
                correctionLog.CorrectionStatus = "approved";
                correctionLog.ModifyTime = DateTime.Now;
                await _correctionLogService.UpdateAsync(correctionLog);

                var memory = new DiagnosticMemoryEntity
                {
                    SceneType = correctionLog.SceneType,
                    ProductionLineId = correctionLog.Generalizable ? null : correctionLog.ProductionLineId,
                    LayerLevel = 0,
                    MemoryType = "core_rule",
                    Content = patternSummary.Pattern,
                    PatternTags = JsonSerializer.Serialize(patternSummary.Tags ?? new List<string>()),
                    SignalType = patternSummary.Tags != null && patternSummary.Tags.Count > 0 
                        ? DetectSignalType(patternSummary.Tags) : null,
                    Source = "manual",
                    ImportanceScore = 8.0f,
                    IsVerified = true,
                    IsActive = true
                };

                await _memoryService.SaveMemoryAsync(memory);

                _logger.LogInformation("[DiagnosticCorrection] 校正确认成功: MemoryId={MemoryId}, Scene={Scene}", 
                    memory.Id, correctionLog.SceneType);

                return new CorrectionConfirmResult
                {
                    Success = true,
                    MemoryId = memory.Id,
                    LearnedContent = patternSummary.Pattern
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DiagnosticCorrection] 确认校正失败: Id={Id}", correctionLogId);
                return new CorrectionConfirmResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>
        /// 补充校正
        /// </summary>
        public async Task<CorrectionProcessResult> SupplementCorrectionAsync(long correctionLogId, string additionalInput)
        {
            try
            {
                var correctionLog = await _correctionLogService.GetByIdAsync(correctionLogId);
                if (correctionLog == null)
                {
                    return new CorrectionProcessResult { Success = false, ErrorMessage = "校正记录不存在" };
                }

                var combinedFeedback = $"{correctionLog.UserFeedback}\n\n用户补充：{additionalInput}";
                var patternSummary = await SummarizePatternAsync(correctionLog.SceneType, combinedFeedback);
                if (patternSummary == null)
                {
                    return new CorrectionProcessResult { Success = false, ErrorMessage = "AI重新归纳失败" };
                }

                correctionLog.UserFeedback = combinedFeedback;
                correctionLog.AiSummary = JsonSerializer.Serialize(patternSummary);
                correctionLog.Generalizable = patternSummary.Generalizable;
                correctionLog.ApplicableScope = patternSummary.ApplicableScope;
                correctionLog.ModifyTime = DateTime.Now;
                await _correctionLogService.UpdateAsync(correctionLog);

                return new CorrectionProcessResult
                {
                    Success = true,
                    CorrectionLogId = correctionLog.Id,
                    PatternSummary = patternSummary,
                    Status = "awaiting_confirmation"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DiagnosticCorrection] 补充校正失败: Id={Id}", correctionLogId);
                return new CorrectionProcessResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>
        /// 拒绝校正
        /// </summary>
        public async Task<bool> RejectCorrectionAsync(long correctionLogId)
        {
            try
            {
                var correctionLog = await _correctionLogService.GetByIdAsync(correctionLogId);
                if (correctionLog == null)
                {
                    return false;
                }

                correctionLog.CorrectionStatus = "rejected";
                correctionLog.ModifyTime = DateTime.Now;
                await _correctionLogService.UpdateAsync(correctionLog);

                _logger.LogInformation("[DiagnosticCorrection] 校正已拒绝: Id={Id}", correctionLogId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DiagnosticCorrection] 拒绝校正失败: Id={Id}", correctionLogId);
                return false;
            }
        }

        /// <summary>
        /// AI归纳pattern
        /// </summary>
        private async Task<PatternSummary> SummarizePatternAsync(string sceneType, string userFeedback)
        {
            try
            {
                var model = await SelectChatModelAsync();
                var prompt = BuildSummarizePrompt(sceneType, userFeedback);
                var response = await CallAIAsync(model, prompt);
                if (string.IsNullOrEmpty(response)) return null;
                return ParsePatternSummary(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DiagnosticCorrection] AI归纳失败");
                return null;
            }
        }

        private string BuildSummarizePrompt(string sceneType, string userFeedback)
        {
            var sceneName = sceneType switch
            {
                DiagnosticMemoryService.SceneConstraint => "约束生成",
                DiagnosticMemoryService.SceneStuckPoint => "卡滞诊断",
                DiagnosticMemoryService.SceneEnergyFlow => "能量流分析",
                _ => sceneType
            };

            return $@"你是{sceneName}经验提炼专家。用户反馈了AI判断错误：

""{userFeedback}""

请提炼成结构化经验：

{{
  ""pattern"": ""一句话描述核心纠正内容"",
  ""core_reason"": ""错误本质是什么"",
  ""correct_action"": ""正确做法是什么"",
  ""generalizable"": true/false,
  ""applicable_scope"": ""all_xxx / only_current"",
  ""tags"": [""标签1"", ""标签2""]
}}

要求：
1. pattern 必须简洁明确
2. generalizable 表示是否适用于所有同类场景
3. tags 使用有意义的标签
4. 只输出JSON";
        }

        private PatternSummary ParsePatternSummary(string response)
        {
            try
            {
                var jsonText = ExtractJsonText(response);
                if (string.IsNullOrEmpty(jsonText)) return null;

                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                var summary = new PatternSummary
                {
                    Pattern = GetString(root, "pattern"),
                    CoreReason = GetString(root, "core_reason"),
                    CorrectAction = GetString(root, "correct_action"),
                    Generalizable = GetBool(root, "generalizable"),
                    ApplicableScope = GetString(root, "applicable_scope")
                };

                if (root.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array)
                {
                    summary.Tags = new List<string>();
                    foreach (var tag in tagsElement.EnumerateArray())
                        summary.Tags.Add(tag.GetString());
                }

                return summary;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DiagnosticCorrection] 解析PatternSummary失败");
                return null;
            }
        }

        private static string ExtractJsonText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var startTag = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (startTag >= 0)
            {
                var contentStart = startTag + 7;
                var endTag = text.IndexOf("```", contentStart, StringComparison.OrdinalIgnoreCase);
                if (endTag > contentStart) return text.Substring(contentStart, endTag - contentStart).Trim();
            }

            var objStart = text.IndexOf('{');
            var objEnd = text.LastIndexOf('}');
            if (objStart >= 0 && objEnd > objStart) return text.Substring(objStart, objEnd - objStart + 1);
            return string.Empty;
        }

        private static string GetString(JsonElement element, string property)
        {
            if (element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static bool GetBool(JsonElement element, string property)
        {
            if (element.TryGetProperty(property, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
                    return prop.GetBoolean();
                if (bool.TryParse(prop.GetString(), out var result)) return result;
            }
            return false;
        }

        private static string DetectSignalType(List<string> tags)
        {
            if (tags == null || tags.Count == 0) return null;
            var signalTypes = new[] { "cylinder", "valve", "motor", "sensor", "safety", "timer", "counter" };
            foreach (var tag in tags)
                foreach (var type in signalTypes)
                    if (tag.ToLower().Contains(type)) return type;
            return tags[0];
        }

        private async Task<(string baseUrl, string apiKey, string modelCode, int timeoutSeconds)> SelectChatModelAsync()
        {
            var selected = (await _purposeResolver.ResolveAsync(AiModelPurposes.MemoryChat)).Model;
            if (!Uri.TryCreate(selected.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(selected.ModelCode))
                throw new InvalidOperationException($"AI 用途 {AiModelPurposes.MemoryChat} 的模型 {selected.Id} 缺少有效地址或编码");
            return (selected.BaseUrl, selected.ApiKey ?? string.Empty,
                selected.ModelCode, AppsettingsConfig.AIAgent?.TimeoutSeconds ?? 120);
        }

        private async Task<string> CallAIAsync((string baseUrl, string apiKey, string modelCode, int timeoutSeconds) model, string prompt)
        {
            var url = model.baseUrl.TrimEnd('/') + "/chat/completions";
            var payload = JsonSerializer.Serialize(new
            {
                model = model.modelCode,
                temperature = 0.1,
                max_tokens = 800,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = "你是专业的PLC诊断经验提炼专家。" },
                    new { role = "user", content = prompt }
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(model.apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model.apiKey);

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(10, model.timeoutSeconds));

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
    }

    #region 结果类

    public class PatternSummary
    {
        public string Pattern { get; set; }
        public string CoreReason { get; set; }
        public string CorrectAction { get; set; }
        public bool Generalizable { get; set; }
        public string ApplicableScope { get; set; }
        public List<string> Tags { get; set; }
    }

    public class CorrectionProcessResult
    {
        public bool Success { get; set; }
        public long? CorrectionLogId { get; set; }
        public PatternSummary PatternSummary { get; set; }
        public string Status { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class CorrectionConfirmResult
    {
        public bool Success { get; set; }
        public long? MemoryId { get; set; }
        public string LearnedContent { get; set; }
        public string ErrorMessage { get; set; }
    }

    #endregion
}
