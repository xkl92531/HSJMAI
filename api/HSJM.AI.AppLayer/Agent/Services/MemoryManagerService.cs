using HSJM.AI.Core;
using HSJM.AI.IDbService;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// 提取的记忆项（公开DTO，供外部调用）
    /// </summary>
    public class ExtractedMemoryDto
    {
        public string Content { get; set; }
        public string MemoryType { get; set; }
        public float ImportanceScore { get; set; }
        public bool IsCoreMemory { get; set; }
        public string LineageId { get; set; }
        public string ConflictKey { get; set; }
        public string MemoryStatus { get; set; }
    }

    public class MemoryGraphRelationDto
    {
        public long MemoryId { get; set; }
        public string SubjectEntity { get; set; }
        public string SubjectType { get; set; }
        public string RelationType { get; set; }
        public string ObjectEntity { get; set; }
        public string ObjectType { get; set; }
        public float ConfidenceScore { get; set; }
        public int HopCount { get; set; } = 1;
        public DateTime CreatedAt { get; set; }
    }

    public class MemoryManagerService : ISelfScopedAutoInject
    {
        private const double RetrievalWeightSimilarity = 0.65d; // A
        private const double RetrievalWeightImportance = 0.25d; // B
        private const double RetrievalWeightTimeDecay = 0.10d;  // C
        private const double TimeDecayLambdaPerDay = 0.08d;     // 时间衰减速率
        private const int MaxExtractedMemoryCount = 5;
        private const float DefaultExtractImportance = 5.0f;
        private const string CoreMemoryType = "core_profile";
        private const int WorkingMemoryBufferLimit = 12;
        private const int WorkingMemorySearchWindow = 8;
        private const string WorkingMemoryKeyPrefix = "wm";

        private readonly IAgent_MemoryService _memoryService;
        private readonly IAiModelPurposeResolver _purposeResolver;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MemoryManagerService> _logger;
        
        // 短期记忆缓存 (userId -> (key -> value))
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _shortTermMemoryCache 
            = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();
        private static readonly SemaphoreSlim _memoryEnhancementSchemaLock = new SemaphoreSlim(1, 1);
        private static volatile bool _memoryEnhancementSchemaChecked;
        private static volatile bool _memoryEnhancementSchemaAvailable;

        private sealed class WorkingMemoryBufferEntry
        {
            public string SystemId { get; set; } = "default";
            public string SessionId { get; set; }
            public string Content { get; set; } = string.Empty;
            public string MemoryType { get; set; } = "event";
            public float ImportanceScore { get; set; } = DefaultExtractImportance;
            public bool IsHighQuality { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.Now;
            public string Source { get; set; } = "runtime";
        }

        public MemoryManagerService(
            IAgent_MemoryService memoryService,
            IAiModelPurposeResolver purposeResolver,
            IHttpClientFactory httpClientFactory,
            ILogger<MemoryManagerService> logger)
        {
            _memoryService = memoryService;
            _purposeResolver = purposeResolver;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        #region 短期记忆 (Short-Term Memory)
        
        public void EnsureUserMemoryLoaded(string userId)
        {
            if (!_shortTermMemoryCache.ContainsKey(userId))
            {
                _shortTermMemoryCache.TryAdd(userId, new ConcurrentDictionary<string, string>());
            }
        }

        public void SetShortTermMemory(string userId, string key, string value)
        {
            EnsureUserMemoryLoaded(userId);
            _shortTermMemoryCache[userId][key] = value;
        }

        public string GetShortTermMemory(string userId, string key)
        {
            EnsureUserMemoryLoaded(userId);
            if (_shortTermMemoryCache[userId].TryGetValue(key, out var value))
            {
                return value;
            }
            return null;
        }

        public Dictionary<string, string> GetAllShortTermMemory(string userId)
        {
            EnsureUserMemoryLoaded(userId);
            return _shortTermMemoryCache[userId].ToDictionary(k => k.Key, v => v.Value);
        }

        public void RememberWorkingMemory(
            string userId,
            string content,
            string memoryType = "event",
            float importanceScore = DefaultExtractImportance,
            string sessionId = null,
            string systemId = "default",
            string source = "runtime")
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            EnsureUserMemoryLoaded(userId);
            var normalizedSystemId = string.IsNullOrWhiteSpace(systemId) ? "default" : systemId.Trim();
            var normalizedType = NormalizeMemoryType(memoryType);
            var normalizedContent = content.Trim();
            var cache = _shortTermMemoryCache[userId];

            var entry = new WorkingMemoryBufferEntry
            {
                SystemId = normalizedSystemId,
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim(),
                Content = normalizedContent,
                MemoryType = normalizedType,
                ImportanceScore = Math.Clamp(importanceScore, 1.0f, 10.0f),
                IsHighQuality = normalizedType == CoreMemoryType || importanceScore >= 8.0f,
                CreatedAt = DateTime.Now,
                Source = string.IsNullOrWhiteSpace(source) ? "runtime" : source.Trim()
            };

            var fingerprint = ComputeStableContentFingerprint($"{normalizedSystemId}|{entry.SessionId}|{normalizedType}|{normalizedContent}");
            var key = $"{WorkingMemoryKeyPrefix}:{normalizedSystemId}:{entry.SessionId ?? "global"}:{fingerprint}";
            cache[key] = JsonSerializer.Serialize(entry);

            TrimWorkingMemoryEntries(cache, normalizedSystemId);
        }

        public void RememberDialogInWorkingMemory(string userId, string dialogText, string sessionId = null, string systemId = "default")
        {
            if (string.IsNullOrWhiteSpace(dialogText))
            {
                return;
            }

            var compactDialog = dialogText.Trim();
            if (compactDialog.Length > 1200)
            {
                compactDialog = compactDialog.Substring(compactDialog.Length - 1200);
            }

            RememberWorkingMemory(userId, compactDialog, "event", 6.5f, sessionId, systemId, "dialog_buffer");
        }

        private List<Agent_MemoryEntity> SearchWorkingMemoryBuffer(string userId, string queryText, int topK, string systemId = "default", string sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(userId) || !_shortTermMemoryCache.TryGetValue(userId, out var cache) || cache.Count == 0)
            {
                return new List<Agent_MemoryEntity>();
            }

            var normalizedSystemId = string.IsNullOrWhiteSpace(systemId) ? "default" : systemId.Trim();
            var prefix = $"{WorkingMemoryKeyPrefix}:{normalizedSystemId}:";
            var keywordTerms = BuildKeywordTerms(queryText);

            return cache
                .Where(item => item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(item => TryDeserializeWorkingMemoryEntry(item.Value))
                .Where(entry => entry != null)
                .Select(entry => new
                {
                    Entry = entry,
                    Score = GetKeywordMatchScore(entry.Content, keywordTerms)
                          + (!string.IsNullOrWhiteSpace(sessionId) && string.Equals(entry.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                          + (entry.IsHighQuality ? 1 : 0)
                })
                .Where(item => item.Score > 0 || keywordTerms.Count == 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Entry.CreatedAt)
                .Take(Math.Clamp(topK, 1, WorkingMemorySearchWindow))
                .Select(item => new Agent_MemoryEntity
                {
                    Id = ComputeTransientMemoryId($"{item.Entry.SystemId}|{item.Entry.SessionId}|{item.Entry.MemoryType}|{item.Entry.Content}"),
                    SystemId = item.Entry.SystemId,
                    UserId = userId,
                    MemoryType = item.Entry.MemoryType,
                    Content = item.Entry.Content,
                    ImportanceScore = item.Entry.ImportanceScore,
                    IsHighQuality = item.Entry.IsHighQuality,
                    SourceSessionId = item.Entry.SessionId,
                    IsActive = true,
                    CreatedAt = item.Entry.CreatedAt,
                    UpdatedAt = item.Entry.CreatedAt
                })
                .ToList();
        }

        private static WorkingMemoryBufferEntry TryDeserializeWorkingMemoryEntry(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<WorkingMemoryBufferEntry>(raw);
            }
            catch
            {
                return null;
            }
        }

        private static void TrimWorkingMemoryEntries(ConcurrentDictionary<string, string> cache, string systemId)
        {
            var prefix = $"{WorkingMemoryKeyPrefix}:{systemId}:";
            var staleKeys = cache
                .Where(item => item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(item => new { item.Key, Entry = TryDeserializeWorkingMemoryEntry(item.Value) })
                .Where(item => item.Entry != null)
                .OrderByDescending(item => item.Entry.CreatedAt)
                .Skip(WorkingMemoryBufferLimit)
                .Select(item => item.Key)
                .ToList();

            foreach (var staleKey in staleKeys)
            {
                cache.TryRemove(staleKey, out _);
            }
        }

        private static string ComputeStableContentFingerprint(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes[..8]);
        }

        private static long ComputeTransientMemoryId(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            var value = BitConverter.ToInt64(bytes, 0);
            return value == long.MinValue ? long.MaxValue : -Math.Abs(value);
        }

        #endregion

        #region 记忆提取 (Extraction)

        /// <summary>
        /// 仅提取记忆内容，不保存（供第三方接口使用）
        /// </summary>
        /// <param name="dialogText">对话内容</param>
        /// <returns>提取的记忆列表</returns>
        public async Task<List<ExtractedMemoryDto>> ExtractMemoriesOnlyAsync(string dialogText)
        {
            if (string.IsNullOrWhiteSpace(dialogText))
            {
                return new List<ExtractedMemoryDto>();
            }

            var items = await ExtractMemoryItemsAsync(dialogText);
            return items.Select(x => new ExtractedMemoryDto
            {
                Content = x.Content,
                MemoryType = x.MemoryType,
                ImportanceScore = x.ImportanceScore,
                IsCoreMemory = IsCoreMemoryCandidate(x.MemoryType, x.Content, x.ImportanceScore)
            }).ToList();
        }

        public async Task<int> ExtractAndSaveLongTermMemoriesAsync(
            string userId,
            string dialogText,
            string sessionId = null,
            string systemId = "default")
        {
            if (string.IsNullOrWhiteSpace(dialogText))
            {
                return 0;
            }

            try
            {
                var extracted = await ExtractMemoryItemsAsync(dialogText);
                if (extracted.Count == 0)
                {
                    await SaveLongTermMemoryAsync(userId, dialogText, "event", DefaultExtractImportance, sessionId, systemId);
                    return 1;
                }

                var uniqueItems = extracted
                    .Where(x => !string.IsNullOrWhiteSpace(x.Content))
                    .GroupBy(x => x.Content.Trim())
                    .Select(x => x.First())
                    .Take(MaxExtractedMemoryCount)
                    .ToList();

                var savedCount = 0;
                foreach (var item in uniqueItems)
                {
                    await SaveLongTermMemoryAsync(
                        userId,
                        item.Content,
                        item.MemoryType,
                        item.ImportanceScore,
                        sessionId,
                        systemId);

                    await PromoteToCoreMemoryIfNeededAsync(
                        userId,
                        item.Content,
                        item.MemoryType,
                        item.ImportanceScore,
                        sessionId,
                        systemId);

                    savedCount++;
                }

                _logger.LogInformation("[Memory] 记忆提取完成: User={UserId}, Extracted={Count}", userId, savedCount);
                return savedCount;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Memory] 记忆提取失败，降级为原文入库");
                await SaveLongTermMemoryAsync(userId, dialogText, "event", DefaultExtractImportance, sessionId, systemId);
                return 1;
            }
        }

        private async Task<List<ExtractedMemoryItem>> ExtractMemoryItemsAsync(string dialogText)
        {
            try
            {
                var model = await SelectChatModelAsync();
                var responseContent = await RequestMemoryExtractionAsync(
                    dialogText,
                    model.baseUrl,
                    model.apiKey,
                    model.modelCode,
                    model.timeoutSeconds);

                var items = ParseExtractedMemories(responseContent);
                return items;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Memory] 调用提取模型失败");
                return new List<ExtractedMemoryItem>();
            }
        }

        private async Task<(string baseUrl, string apiKey, string modelCode, int timeoutSeconds)> SelectChatModelAsync()
        {
            var selected = (await _purposeResolver.ResolveAsync(AiModelPurposes.MemoryChat)).Model;
            return (RequireModelBaseUrl(selected, AiModelPurposes.MemoryChat),
                selected.ApiKey ?? string.Empty,
                RequireModelCode(selected, AiModelPurposes.MemoryChat),
                AppsettingsConfig.AIAgent?.TimeoutSeconds ?? 120);
        }

        private async Task<string> RequestMemoryExtractionAsync(string dialogText, string baseUrl, string apiKey, string modelCode, int timeoutSeconds)
        {
            var url = baseUrl.TrimEnd('/') + "/chat/completions";
            var systemPrompt = @"你是记忆提取器。请从输入文本中提取长期有价值信息，忽略寒暄和一次性噪声。
仅返回 JSON，不要解释。
返回格式：
{
  ""memories"": [
    { ""content"": ""一句话记忆"", ""memory_type"": ""preference|fact|event"", ""importance"": 1-10 }
  ]
}
要求：
1) content 必须简洁、客观、可复用
2) memory_type 仅能是 preference/fact/event
3) importance 为 1~10 数字
4) 无可提取内容时返回 {""memories"": [] }";

            var payload = JsonSerializer.Serialize(new
            {
                model = modelCode,
                temperature = 0.1,
                max_tokens = 800,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = dialogText }
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds));

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return content ?? "{\"memories\":[]}";
        }

        private List<ExtractedMemoryItem> ParseExtractedMemories(string text)
        {
            var result = new List<ExtractedMemoryItem>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            var jsonText = ExtractJsonText(text);
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                JsonElement memoriesElement;

                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("memories", out var m))
                {
                    memoriesElement = m;
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    memoriesElement = doc.RootElement;
                }
                else
                {
                    return result;
                }

                if (memoriesElement.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var item in memoriesElement.EnumerateArray())
                {
                    var content = TryGetString(item, "content");
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    var type = NormalizeMemoryType(TryGetString(item, "memory_type"));
                    var importance = ClampImportance(TryGetNumber(item, "importance", DefaultExtractImportance));

                    result.Add(new ExtractedMemoryItem
                    {
                        Content = content.Trim(),
                        MemoryType = type,
                        ImportanceScore = importance
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Memory] 解析提取结果失败");
            }

            return result;
        }

        private static string ExtractJsonText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var startTag = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (startTag >= 0)
            {
                var contentStart = startTag + 7;
                var endTag = text.IndexOf("```", contentStart, StringComparison.OrdinalIgnoreCase);
                if (endTag > contentStart)
                {
                    return text.Substring(contentStart, endTag - contentStart).Trim();
                }
            }

            var objStart = text.IndexOf('{');
            var objEnd = text.LastIndexOf('}');
            if (objStart >= 0 && objEnd > objStart)
            {
                return text.Substring(objStart, objEnd - objStart + 1);
            }

            var arrStart = text.IndexOf('[');
            var arrEnd = text.LastIndexOf(']');
            if (arrStart >= 0 && arrEnd > arrStart)
            {
                return text.Substring(arrStart, arrEnd - arrStart + 1);
            }

            return string.Empty;
        }

        private static string TryGetString(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }

            return string.Empty;
        }

        private static float TryGetNumber(JsonElement element, string propertyName, float defaultValue)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            {
                return defaultValue;
            }

            if (property.ValueKind == JsonValueKind.Number)
            {
                if (property.TryGetSingle(out var floatValue))
                {
                    return floatValue;
                }

                if (property.TryGetDouble(out var doubleValue))
                {
                    return (float)doubleValue;
                }
            }

            if (property.ValueKind == JsonValueKind.String && float.TryParse(property.GetString(), out var parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        private static string NormalizeMemoryType(string memoryType)
        {
            if (string.IsNullOrWhiteSpace(memoryType))
            {
                return "fact";
            }

            var normalized = memoryType.Trim().ToLowerInvariant();
            return normalized switch
            {
                "preference" => "preference",
                "fact" => "fact",
                "event" => "event",
                "core" => CoreMemoryType,
                "persona" => CoreMemoryType,
                "profile" => CoreMemoryType,
                "user_profile" => CoreMemoryType,
                "core_profile" => CoreMemoryType,
                _ => "fact"
            };
        }

        private static bool IsCoreMemoryCandidate(string memoryType, string content, float importanceScore)
        {
            var normalizedType = NormalizeMemoryType(memoryType);
            if (normalizedType == CoreMemoryType)
            {
                return true;
            }

            if (normalizedType == "preference")
            {
                return importanceScore >= 6.0f || ContainsCoreProfileKeyword(content);
            }

            if (normalizedType == "fact")
            {
                return importanceScore >= 8.0f && ContainsCoreProfileKeyword(content);
            }

            return false;
        }

        private static bool ContainsCoreProfileKeyword(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var keywords = new[]
            {
                "我叫", "我是", "职业", "岗位", "偏好", "习惯", "喜欢", "擅长", "主要研究", "常用", "风格", "技术栈", "persona", "profile"
            };

            return keywords.Any(keyword => content.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private static float ClampImportance(float value)
        {
            if (value < 1f) return 1f;
            if (value > 10f) return 10f;
            return value;
        }

        private sealed class ExtractedMemoryItem
        {
            public string Content { get; set; }
            public string MemoryType { get; set; }
            public float ImportanceScore { get; set; }
        }

        #endregion

        #region 长期记忆 (Long-Term Memory)

        public async Task SaveLongTermMemoryAsync(
            string userId,
            string content,
            string memoryType = "LONG_TERM",
            float importanceScore = 5.0f,
            string sessionId = null,
            string systemId = "default",
            bool trackLineage = true,
            string conflictKey = null,
            bool extractGraph = true)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var normalizedSystemId = string.IsNullOrWhiteSpace(systemId) ? "default" : systemId.Trim();
            var normalizedContent = content.Trim();
            var normalizedType = NormalizeMemoryType(memoryType);
            var normalizedImportance = Math.Clamp(importanceScore, 1.0f, 10.0f);
            var db = _memoryService.GetDb().Context;
            var enhancementSchemaReady = await EnsureMemoryEnhancementSchemaAsync();
            var now = DateTime.Now;

            await RecordMemoryLineageAsync(userId, normalizedContent, normalizedType, normalizedImportance, sessionId, normalizedSystemId);

            var normalizedConflictKey = enhancementSchemaReady && trackLineage && ShouldTrackLineageType(normalizedType)
                ? ResolveConflictKey(normalizedType, normalizedContent, conflictKey)
                : string.Empty;

            var existing = await db.Queryable<Agent_MemoryEntity>()
                .Where(m => m.SystemId == normalizedSystemId
                         && m.UserId == userId
                         && m.MemoryType == normalizedType
                         && m.Content == normalizedContent
                         && m.IsActive == true)
                .FirstAsync();

            if (existing != null)
            {
                existing.ImportanceScore = Math.Max(existing.ImportanceScore, normalizedImportance);
                existing.IsHighQuality = existing.IsHighQuality || normalizedType == CoreMemoryType || normalizedImportance >= 8.0f;
                existing.UpdatedAt = now;

                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    existing.SourceSessionId = sessionId;
                }

                if (enhancementSchemaReady && trackLineage && ShouldTrackLineageType(normalizedType))
                {
                    existing.ConflictKey = string.IsNullOrWhiteSpace(existing.ConflictKey) ? normalizedConflictKey : existing.ConflictKey;
                    existing.LineageId = string.IsNullOrWhiteSpace(existing.LineageId)
                        ? BuildLineageId(normalizedSystemId, userId, normalizedType, existing.ConflictKey ?? normalizedConflictKey)
                        : existing.LineageId;
                    existing.MemoryStatus = string.IsNullOrWhiteSpace(existing.MemoryStatus) ? "active" : existing.MemoryStatus;
                    existing.ValidFrom ??= existing.CreatedAt;
                }

                await db.Updateable(existing).ExecuteCommandAsync();

                if (extractGraph && enhancementSchemaReady)
                {
                    await UpsertGraphProjectionAsync(existing.Id, userId, normalizedSystemId, normalizedType, normalizedContent);
                }

                RememberWorkingMemory(userId, normalizedContent, normalizedType, existing.ImportanceScore, existing.SourceSessionId, normalizedSystemId, "long_term_update");
                return;
            }

            Agent_MemoryEntity previousVersion = null;
            var lineageId = string.Empty;
            if (enhancementSchemaReady && trackLineage && ShouldTrackLineageType(normalizedType) && !string.IsNullOrWhiteSpace(normalizedConflictKey))
            {
                previousVersion = await db.Queryable<Agent_MemoryEntity>()
                    .Where(m => m.SystemId == normalizedSystemId
                             && m.UserId == userId
                             && m.MemoryType == normalizedType
                             && m.IsActive == true
                             && m.ConflictKey == normalizedConflictKey
                             && (m.MemoryStatus == null || m.MemoryStatus == "active")
                             && m.Content != normalizedContent)
                    .OrderByDescending(m => m.UpdatedAt)
                    .FirstAsync();

                lineageId = !string.IsNullOrWhiteSpace(previousVersion?.LineageId)
                    ? previousVersion.LineageId
                    : BuildLineageId(normalizedSystemId, userId, normalizedType, normalizedConflictKey);

                if (previousVersion != null)
                {
                    previousVersion.MemoryStatus = "superseded";
                    previousVersion.ValidFrom ??= previousVersion.CreatedAt;
                    previousVersion.ValidTo = now;
                    previousVersion.UpdatedAt = now;
                    await db.Updateable(previousVersion).ExecuteCommandAsync();
                }
            }

            var embedding = await GenerateEmbeddingAsync(normalizedContent);
            var embString = ToPgVectorString(embedding);

            var memory = new Agent_MemoryEntity
            {
                SystemId = normalizedSystemId,
                UserId = userId,
                Content = normalizedContent,
                MemoryType = normalizedType,
                ImportanceScore = normalizedImportance,
                SourceSessionId = sessionId,
                IsHighQuality = normalizedType == CoreMemoryType || normalizedImportance >= 8.0f,
                LineageId = lineageId,
                SupersedesMemoryId = previousVersion?.Id,
                ConflictKey = normalizedConflictKey,
                MemoryStatus = enhancementSchemaReady && trackLineage && ShouldTrackLineageType(normalizedType) ? "active" : "active",
                ValidFrom = now,
                ValidTo = null,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };

            if (db.CurrentConnectionConfig.DbType == SqlSugar.DbType.PostgreSQL && !string.IsNullOrEmpty(embString))
            {
                await db.Insertable(memory).IgnoreColumns(x => x.Embedding).ExecuteCommandAsync();
                await db.Ado.ExecuteCommandAsync("UPDATE agent_memories SET embedding = @emb::vector WHERE id = @id", new { emb = embString, id = memory.Id });
            }
            else
            {
                memory.Embedding = embString;
                await _memoryService.InsertAsync(memory);
            }

            if (extractGraph && enhancementSchemaReady)
            {
                await UpsertGraphProjectionAsync(memory.Id, userId, normalizedSystemId, normalizedType, normalizedContent);
            }

            RememberWorkingMemory(userId, normalizedContent, normalizedType, normalizedImportance, sessionId, normalizedSystemId, "long_term_insert");
        }

        public async Task<bool> PromoteToCoreMemoryIfNeededAsync(
            string userId,
            string content,
            string memoryType,
            float importanceScore,
            string sessionId = null,
            string systemId = "default")
        {
            var normalizedType = NormalizeMemoryType(memoryType);
            if (!IsCoreMemoryCandidate(normalizedType, content, importanceScore))
            {
                return false;
            }

            await UpsertCoreMemoryAsync(userId, content, importanceScore, sessionId, systemId);
            return true;
        }

        public async Task UpsertCoreMemoryAsync(
            string userId,
            string content,
            float importanceScore = 8.5f,
            string sessionId = null,
            string systemId = "default")
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var db = _memoryService.GetDb().Context;
            var normalizedContent = content.Trim();
            var existing = await db.Queryable<Agent_MemoryEntity>()
                .Where(m => m.SystemId == systemId
                         && m.UserId == userId
                         && m.MemoryType == CoreMemoryType
                         && m.IsActive == true
                         && m.Content == normalizedContent)
                .FirstAsync();

            if (existing != null)
            {
                existing.IsHighQuality = true;
                existing.ImportanceScore = Math.Max(existing.ImportanceScore, Math.Max(importanceScore, 8.0f));
                existing.UpdatedAt = DateTime.Now;
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    existing.SourceSessionId = sessionId;
                }

                await db.Updateable(existing).ExecuteCommandAsync();
                RememberWorkingMemory(userId, normalizedContent, CoreMemoryType, existing.ImportanceScore, existing.SourceSessionId, systemId, "core_memory_refresh");
                return;
            }

            await SaveLongTermMemoryAsync(
                userId,
                normalizedContent,
                CoreMemoryType,
                Math.Max(importanceScore, 8.0f),
                sessionId,
                systemId);
        }

        public async Task<List<Agent_MemoryEntity>> GetCoreMemoriesAsync(string userId, string systemId = "default", int topK = 8)
        {
            topK = Math.Clamp(topK, 1, 20);
            var db = _memoryService.GetDb().Context;

            var explicitCore = await db.Queryable<Agent_MemoryEntity>()
                .Where(m => m.SystemId == systemId
                         && m.UserId == userId
                         && m.IsActive == true
                         && m.MemoryType == CoreMemoryType)
                .OrderByDescending(m => m.ImportanceScore)
                .OrderByDescending(m => m.UpdatedAt)
                .Take(topK)
                .ToListAsync();

            if (explicitCore.Count > 0)
            {
                return explicitCore;
            }

            return await db.Queryable<Agent_MemoryEntity>()
                .Where(m => m.SystemId == systemId
                         && m.UserId == userId
                         && m.IsActive == true
                         && (m.MemoryType == "preference" || (m.IsHighQuality == true && m.ImportanceScore >= 7.0f)))
                .OrderByDescending(m => m.ImportanceScore)
                .OrderByDescending(m => m.UpdatedAt)
                .Take(topK)
                .ToListAsync();
        }

        public async Task<string> GetCoreMemoryPromptBlockAsync(string userId, string systemId = "default", int topK = 6)
        {
            var coreMemories = await GetCoreMemoriesAsync(userId, systemId, topK);
            if (coreMemories == null || coreMemories.Count == 0)
            {
                return string.Empty;
            }

            var lines = coreMemories
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .Select(m => $"- {m.Content.Trim()}")
                .Distinct()
                .ToList();

            if (lines.Count == 0)
            {
                return string.Empty;
            }

            return $"<User_Profile>\n{string.Join("\n", lines)}\n</User_Profile>";
        }

        private async Task RecordMemoryLineageAsync(
            string userId,
            string content,
            string memoryType,
            float importanceScore,
            string sessionId = null,
            string systemId = "default")
        {
            if (memoryType != "preference" && memoryType != "fact")
            {
                return;
            }

            var topicKey = ExtractLineageTopicKey(content);
            if (string.IsNullOrWhiteSpace(topicKey))
            {
                return;
            }

            var db = _memoryService.GetDb().Context;
            var activeMemories = await db.Queryable<Agent_MemoryEntity>()
                .Where(m => m.SystemId == systemId
                         && m.UserId == userId
                         && m.MemoryType == memoryType
                         && m.IsActive == true)
                .OrderByDescending(m => m.UpdatedAt)
                .Take(20)
                .ToListAsync();

            var previous = activeMemories.FirstOrDefault(m =>
                !string.Equals(m.Content?.Trim(), content, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ExtractLineageTopicKey(m.Content ?? string.Empty), topicKey, StringComparison.OrdinalIgnoreCase));

            if (previous == null)
            {
                return;
            }

            var lineageContent = $"【记忆演化】{topicKey}：曾记录“{previous.Content.Trim()}”，当前更新为“{content}”。";
            var hasLineage = await db.Queryable<Agent_MemoryEntity>()
                .Where(m => m.SystemId == systemId
                         && m.UserId == userId
                         && m.MemoryType == "event"
                         && m.IsActive == true
                         && m.Content == lineageContent)
                .AnyAsync();

            if (!hasLineage)
            {
                await SaveLongTermMemoryAsync(userId, lineageContent, "event", Math.Max(importanceScore, 7.0f), sessionId, systemId, trackLineage: false, conflictKey: null, extractGraph: false);
            }
        }

        private static string ExtractLineageTopicKey(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            var normalized = content.Replace("用户", string.Empty).Trim();
            var markers = new[]
            {
                "主要研究方向是", "主要研究", "主要使用", "偏好", "喜欢", "习惯", "常用", "职业是", "身份是", "从事", "负责", "使用", "是"
            };

            foreach (var marker in markers)
            {
                var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    return normalized.Substring(0, Math.Min(normalized.Length, index + marker.Length)).Trim();
                }
            }

            var firstSegment = normalized
                .Split(new[] { '，', ',', '。', '.', '；', ';', '：', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            return firstSegment?.Trim() ?? string.Empty;
        }

        private sealed class GraphTriple
        {
            public string Subject { get; set; }
            public string SubjectType { get; set; }
            public string Relation { get; set; }
            public string Object { get; set; }
            public string ObjectType { get; set; }
            public float ConfidenceScore { get; set; } = 0.75f;
        }

        private async Task<bool> EnsureMemoryEnhancementSchemaAsync()
        {
            if (_memoryEnhancementSchemaChecked)
            {
                return _memoryEnhancementSchemaAvailable;
            }

            await _memoryEnhancementSchemaLock.WaitAsync();
            try
            {
                if (_memoryEnhancementSchemaChecked)
                {
                    return _memoryEnhancementSchemaAvailable;
                }

                var db = _memoryService.GetDb().Context;
                db.CodeFirst.InitTables<Agent_MemoryNodeEntity>();
                db.CodeFirst.InitTables<Agent_MemoryRelationEntity>();

                if (db.CurrentConnectionConfig.DbType == SqlSugar.DbType.PostgreSQL)
                {
                    var sqlStatements = new[]
                    {
                        "ALTER TABLE agent_memories ADD COLUMN IF NOT EXISTS lineage_id varchar(64)",
                        "ALTER TABLE agent_memories ADD COLUMN IF NOT EXISTS supersedes_memory_id bigint NULL",
                        "ALTER TABLE agent_memories ADD COLUMN IF NOT EXISTS conflict_key varchar(128) NULL",
                        "ALTER TABLE agent_memories ADD COLUMN IF NOT EXISTS memory_status varchar(20) NULL DEFAULT 'active'",
                        "ALTER TABLE agent_memories ADD COLUMN IF NOT EXISTS valid_from timestamp NULL",
                        "ALTER TABLE agent_memories ADD COLUMN IF NOT EXISTS valid_to timestamp NULL",
                        "CREATE INDEX IF NOT EXISTS idx_agent_memories_lineage_id ON agent_memories(lineage_id)",
                        "CREATE INDEX IF NOT EXISTS idx_agent_memories_conflict_key ON agent_memories(system_id, user_id, conflict_key)",
                        "CREATE INDEX IF NOT EXISTS idx_agent_memory_entities_lookup ON agent_memory_entities(system_id, user_id, normalized_name)",
                        "CREATE INDEX IF NOT EXISTS idx_agent_memory_relations_lookup ON agent_memory_relations(system_id, user_id, normalized_key)"
                    };

                    foreach (var sql in sqlStatements)
                    {
                        await db.Ado.ExecuteCommandAsync(sql);
                    }
                }

                _memoryEnhancementSchemaAvailable = true;
                _logger.LogInformation("[Memory] 已启用 Lineage / GraphRAG 增强表结构");
            }
            catch (Exception ex)
            {
                _memoryEnhancementSchemaAvailable = false;
                _logger.LogWarning(ex, "[Memory] 初始化 Lineage / GraphRAG 表结构失败，将回退到基础记忆模式");
            }
            finally
            {
                _memoryEnhancementSchemaChecked = true;
                _memoryEnhancementSchemaLock.Release();
            }

            return _memoryEnhancementSchemaAvailable;
        }

        private static bool ShouldTrackLineageType(string memoryType)
        {
            var normalizedType = NormalizeMemoryType(memoryType);
            return normalizedType == "preference" || normalizedType == "fact" || normalizedType == CoreMemoryType;
        }

        private static string ResolveConflictKey(string memoryType, string content, string providedConflictKey = null)
        {
            if (!string.IsNullOrWhiteSpace(providedConflictKey))
            {
                return NormalizeKeyToken(providedConflictKey);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            var normalizedType = NormalizeMemoryType(memoryType);
            var ipMatch = Regex.Match(content, @"(?<subject>[^，。,.；;\s]+)\s*的?\s*(IP|ip|地址)");
            if (ipMatch.Success)
            {
                return $"device_ip:{NormalizeKeyToken(ipMatch.Groups["subject"].Value)}";
            }

            if (content.Contains("负责", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(content, @"(?<subject>[^，。,.；;\s]+)\s*负责\s*(?<object>[^，。,.；;\s]+)");
                if (match.Success)
                {
                    return $"responsibility:{NormalizeKeyToken(match.Groups["object"].Value)}";
                }
            }

            if (content.Contains("汇报给", StringComparison.OrdinalIgnoreCase) || content.Contains("上级", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(content, @"(?<subject>[^，。,.；;\s]+)\s*(汇报给|上级是|直属上级是)\s*(?<object>[^，。,.；;\s]+)");
                if (match.Success)
                {
                    return $"reporting:{NormalizeKeyToken(match.Groups["subject"].Value)}";
                }
            }

            var topicKey = NormalizeKeyToken(ExtractLineageTopicKey(content));
            if (string.IsNullOrWhiteSpace(topicKey))
            {
                topicKey = ComputeStableContentFingerprint($"{normalizedType}:{content.Trim()}");
            }

            return $"{normalizedType}:{topicKey}";
        }

        private static string NormalizeKeyToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9\u4e00-\u9fa5]+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
        }

        private static string BuildLineageId(string systemId, string userId, string memoryType, string conflictKey)
        {
            var raw = $"{systemId}|{userId}|{NormalizeMemoryType(memoryType)}|{conflictKey}";
            return $"lin_{ComputeStableContentFingerprint(raw)}";
        }

        public async Task<List<Agent_MemoryEntity>> SearchLineageMemoriesAsync(string userId, string queryText, int topK = 6, string systemId = "default")
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(queryText))
            {
                return new List<Agent_MemoryEntity>();
            }

            if (!await EnsureMemoryEnhancementSchemaAsync())
            {
                return new List<Agent_MemoryEntity>();
            }

            var db = _memoryService.GetDb().Context;
            var keywordTerms = BuildKeywordTerms(queryText);
            var includeHistory = queryText.Contains("以前", StringComparison.OrdinalIgnoreCase)
                              || queryText.Contains("之前", StringComparison.OrdinalIgnoreCase)
                              || queryText.Contains("曾经", StringComparison.OrdinalIgnoreCase)
                              || queryText.Contains("历史", StringComparison.OrdinalIgnoreCase)
                              || queryText.Contains("变更", StringComparison.OrdinalIgnoreCase)
                              || queryText.Contains("演化", StringComparison.OrdinalIgnoreCase);

            var candidates = await db.Queryable<Agent_MemoryEntity>()
                .Where(m => m.SystemId == systemId
                         && m.UserId == userId
                         && m.IsActive == true
                         && m.LineageId != null
                         && m.LineageId != "")
                .ToListAsync();

            if (candidates.Count == 0)
            {
                return new List<Agent_MemoryEntity>();
            }

            var matched = candidates
                .Select(memory => new
                {
                    Memory = memory,
                    Score = GetKeywordMatchScore(memory.Content, keywordTerms)
                          + GetKeywordMatchScore(memory.ConflictKey, keywordTerms)
                          + (includeHistory && !string.Equals(memory.MemoryStatus, "active", StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                })
                .Where(item => item.Score > 0 || includeHistory)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Memory.ValidFrom ?? item.Memory.CreatedAt)
                .Take(Math.Clamp(topK * 2, 4, 20))
                .ToList();

            if (matched.Count == 0)
            {
                return new List<Agent_MemoryEntity>();
            }

            var lineageIds = matched
                .Select(item => item.Memory.LineageId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return candidates
                .Where(memory => !string.IsNullOrWhiteSpace(memory.LineageId) && lineageIds.Contains(memory.LineageId))
                .OrderByDescending(memory => memory.ValidFrom ?? memory.CreatedAt)
                .Take(Math.Clamp(topK * 3, 6, 30))
                .ToList();
        }

        public async Task<List<MemoryGraphRelationDto>> SearchGraphRelationsAsync(string userId, string queryText, int topK = 6, string systemId = "default", int graphHop = 1)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(queryText))
            {
                return new List<MemoryGraphRelationDto>();
            }

            if (!await EnsureMemoryEnhancementSchemaAsync())
            {
                return new List<MemoryGraphRelationDto>();
            }

            var db = _memoryService.GetDb().Context;
            var keywordTerms = BuildKeywordTerms(queryText);
            var relations = await db.Queryable<Agent_MemoryRelationEntity>()
                .Where(r => r.SystemId == systemId && r.UserId == userId && r.IsActive == true)
                .ToListAsync();

            if (relations.Count == 0)
            {
                return new List<MemoryGraphRelationDto>();
            }

            var seedItems = relations
                .Select(relation => new
                {
                    Relation = relation,
                    Score = ComputeGraphRelationScore(relation, queryText, keywordTerms),
                    Hop = 1
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Relation.ConfidenceScore)
                .Take(Math.Clamp(topK * 3, 8, 30))
                .ToList();

            if (seedItems.Count == 0)
            {
                return new List<MemoryGraphRelationDto>();
            }

            var expanded = seedItems
                .Select(item => (item.Relation, item.Score, item.Hop))
                .ToList();

            if (Math.Clamp(graphHop, 1, 2) > 1)
            {
                var seedEntities = seedItems
                    .SelectMany(item => new[] { item.Relation.SubjectEntity, item.Relation.ObjectEntity })
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToList();

                var secondHop = relations
                    .Where(relation => seedEntities.Any(seed =>
                        string.Equals(relation.SubjectEntity, seed, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(relation.ObjectEntity, seed, StringComparison.OrdinalIgnoreCase)))
                    .Select(relation => (relation, ComputeGraphRelationScore(relation, queryText, keywordTerms) * 0.85d + 0.25d, 2))
                    .Where(item => item.Item2 > 0.2d)
                    .ToList();

                expanded.AddRange(secondHop);
            }

            return expanded
                .GroupBy(item => item.Relation.NormalizedKey ?? $"{item.Relation.SubjectEntity}|{item.Relation.RelationType}|{item.Relation.ObjectEntity}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.Score).First())
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Relation.ConfidenceScore)
                .Take(Math.Clamp(topK, 1, 20))
                .Select(item => new MemoryGraphRelationDto
                {
                    MemoryId = item.Relation.MemoryId,
                    SubjectEntity = item.Relation.SubjectEntity,
                    SubjectType = item.Relation.SubjectType,
                    RelationType = item.Relation.RelationType,
                    ObjectEntity = item.Relation.ObjectEntity,
                    ObjectType = item.Relation.ObjectType,
                    ConfidenceScore = item.Relation.ConfidenceScore,
                    HopCount = item.Hop,
                    CreatedAt = item.Relation.CreatedAt
                })
                .ToList();
        }

        public async Task<List<MemoryGraphRelationDto>> GetGraphRelationsByMemoryIdAsync(long memoryId, string userId, string systemId = "default")
        {
            if (memoryId <= 0 || string.IsNullOrWhiteSpace(userId) || !await EnsureMemoryEnhancementSchemaAsync())
            {
                return new List<MemoryGraphRelationDto>();
            }

            var db = _memoryService.GetDb().Context;
            var relations = await db.Queryable<Agent_MemoryRelationEntity>()
                .Where(r => r.SystemId == systemId && r.UserId == userId && r.MemoryId == memoryId && r.IsActive == true)
                .OrderByDescending(r => r.ConfidenceScore)
                .ToListAsync();

            return relations.Select(relation => new MemoryGraphRelationDto
            {
                MemoryId = relation.MemoryId,
                SubjectEntity = relation.SubjectEntity,
                SubjectType = relation.SubjectType,
                RelationType = relation.RelationType,
                ObjectEntity = relation.ObjectEntity,
                ObjectType = relation.ObjectType,
                ConfidenceScore = relation.ConfidenceScore,
                HopCount = 1,
                CreatedAt = relation.CreatedAt
            }).ToList();
        }

        private async Task<int> UpsertGraphProjectionAsync(long memoryId, string userId, string systemId, string memoryType, string content)
        {
            if (memoryId <= 0 || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(content))
            {
                return 0;
            }

            var triples = ExtractGraphTriples(content, memoryType);
            var db = _memoryService.GetDb().Context;
            await db.Deleteable<Agent_MemoryNodeEntity>().Where(x => x.MemoryId == memoryId).ExecuteCommandAsync();
            await db.Deleteable<Agent_MemoryRelationEntity>().Where(x => x.MemoryId == memoryId).ExecuteCommandAsync();

            if (triples.Count == 0)
            {
                return 0;
            }

            var now = DateTime.Now;
            var nodes = triples
                .SelectMany(triple => new[]
                {
                    new Agent_MemoryNodeEntity
                    {
                        SystemId = systemId,
                        UserId = userId,
                        MemoryId = memoryId,
                        EntityName = triple.Subject,
                        EntityType = triple.SubjectType,
                        NormalizedName = NormalizeKeyToken(triple.Subject),
                        ConfidenceScore = triple.ConfidenceScore,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    new Agent_MemoryNodeEntity
                    {
                        SystemId = systemId,
                        UserId = userId,
                        MemoryId = memoryId,
                        EntityName = triple.Object,
                        EntityType = triple.ObjectType,
                        NormalizedName = NormalizeKeyToken(triple.Object),
                        ConfidenceScore = triple.ConfidenceScore,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now
                    }
                })
                .Where(node => !string.IsNullOrWhiteSpace(node.EntityName))
                .GroupBy(node => $"{node.MemoryId}|{node.NormalizedName}|{node.EntityType}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (nodes.Count > 0)
            {
                await db.Insertable(nodes).ExecuteCommandAsync();
            }

            var relations = triples.Select(triple => new Agent_MemoryRelationEntity
            {
                SystemId = systemId,
                UserId = userId,
                MemoryId = memoryId,
                SubjectEntity = triple.Subject,
                SubjectType = triple.SubjectType,
                RelationType = triple.Relation,
                ObjectEntity = triple.Object,
                ObjectType = triple.ObjectType,
                NormalizedKey = NormalizeKeyToken($"{triple.Subject}|{triple.Relation}|{triple.Object}"),
                ConfidenceScore = triple.ConfidenceScore,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            }).ToList();

            if (relations.Count > 0)
            {
                await db.Insertable(relations).ExecuteCommandAsync();
            }

            return relations.Count;
        }

        private static List<GraphTriple> ExtractGraphTriples(string content, string memoryType)
        {
            var result = new List<GraphTriple>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return result;
            }

            var normalizedContent = content.Trim();
            var rules = new[]
            {
                (Pattern: new Regex(@"(?<subject>[^，。,.；;\s]+)\s*的?\s*(IP|ip|地址)\s*(是|为|改为|变更为)\s*(?<object>\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.IgnoreCase), Relation: "ip_address", SubjectType: "device", ObjectType: "network_parameter", Score: 0.96f),
                (Pattern: new Regex(@"(?<subject>[^，。,.；;\s]+)\s*(负责|管理|维护)\s*(?<object>[^，。,.；;\s]+)", RegexOptions.IgnoreCase), Relation: "responsible_for", SubjectType: "person", ObjectType: "line", Score: 0.88f),
                (Pattern: new Regex(@"(?<subject>[^，。,.；;\s]+)\s*(使用|采用|用的是)\s*(?<object>[^，。,.；;\s]+)", RegexOptions.IgnoreCase), Relation: "uses", SubjectType: "line", ObjectType: "device", Score: 0.84f),
                (Pattern: new Regex(@"(?<subject>[^，。,.；;\s]+)\s*(汇报给|上级是|直属上级是)\s*(?<object>[^，。,.；;\s]+)", RegexOptions.IgnoreCase), Relation: "reports_to", SubjectType: "person", ObjectType: "person", Score: 0.90f),
                (Pattern: new Regex(@"(?<subject>[^，。,.；;\s]+)\s*(属于|归属|隶属于)\s*(?<object>[^，。,.；;\s]+)", RegexOptions.IgnoreCase), Relation: "belongs_to", SubjectType: "device", ObjectType: "line", Score: 0.82f),
                (Pattern: new Regex(@"(?<subject>[^，。,.；;\s]+)\s*(连接到|连接至|连接)\s*(?<object>[^，。,.；;\s]+)", RegexOptions.IgnoreCase), Relation: "connected_to", SubjectType: "device", ObjectType: "device", Score: 0.80f)
            };

            foreach (var rule in rules)
            {
                foreach (Match match in rule.Pattern.Matches(normalizedContent))
                {
                    var subject = match.Groups["subject"].Value.Trim();
                    var obj = match.Groups["object"].Value.Trim().Trim('。', '，', ',', ';', '；');
                    if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(obj))
                    {
                        continue;
                    }

                    result.Add(new GraphTriple
                    {
                        Subject = subject,
                        SubjectType = InferEntityType(subject, rule.Relation == "responsible_for" ? "person" : rule.SubjectType),
                        Relation = rule.Relation,
                        Object = obj,
                        ObjectType = InferEntityType(obj, rule.ObjectType),
                        ConfidenceScore = rule.Score
                    });
                }
            }

            if (NormalizeMemoryType(memoryType) == "preference" || normalizedContent.Contains("喜欢", StringComparison.OrdinalIgnoreCase) || normalizedContent.Contains("偏好", StringComparison.OrdinalIgnoreCase) || normalizedContent.Contains("常用", StringComparison.OrdinalIgnoreCase))
            {
                var preferenceMatch = Regex.Match(normalizedContent, @"(喜欢|偏好|常用|习惯用|主要用)\s*(?<object>[^，。;；]+)", RegexOptions.IgnoreCase);
                if (preferenceMatch.Success)
                {
                    var obj = preferenceMatch.Groups["object"].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(obj))
                    {
                        result.Add(new GraphTriple
                        {
                            Subject = "用户",
                            SubjectType = "person",
                            Relation = "prefers",
                            Object = obj,
                            ObjectType = InferEntityType(obj, "technology"),
                            ConfidenceScore = 0.78f
                        });
                    }
                }
            }

            return result
                .Where(item => !string.IsNullOrWhiteSpace(item.Subject) && !string.IsNullOrWhiteSpace(item.Object))
                .GroupBy(item => $"{NormalizeKeyToken(item.Subject)}|{item.Relation}|{NormalizeKeyToken(item.Object)}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.ConfidenceScore).First())
                .ToList();
        }

        private static string InferEntityType(string token, string fallbackType = "concept")
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return fallbackType;
            }

            if (Regex.IsMatch(token, @"^\d{1,3}(?:\.\d{1,3}){3}$"))
            {
                return "network_parameter";
            }

            if (token.Contains("PLC", StringComparison.OrdinalIgnoreCase) || token.Contains("传感器", StringComparison.OrdinalIgnoreCase) || token.Contains("变频器", StringComparison.OrdinalIgnoreCase) || token.Contains("设备", StringComparison.OrdinalIgnoreCase))
            {
                return "device";
            }

            if (token.Contains("产线", StringComparison.OrdinalIgnoreCase) || token.Contains("车间", StringComparison.OrdinalIgnoreCase) || token.Contains("工位", StringComparison.OrdinalIgnoreCase))
            {
                return "line";
            }

            if (token.Contains("项目", StringComparison.OrdinalIgnoreCase) || token.Contains("任务", StringComparison.OrdinalIgnoreCase))
            {
                return "project";
            }

            if (token.EndsWith("工", StringComparison.OrdinalIgnoreCase) || token.Contains("主管", StringComparison.OrdinalIgnoreCase) || token.Contains("经理", StringComparison.OrdinalIgnoreCase) || token.Contains("负责人", StringComparison.OrdinalIgnoreCase))
            {
                return "person";
            }

            return fallbackType;
        }

        private static double ComputeGraphRelationScore(Agent_MemoryRelationEntity relation, string queryText, List<string> keywordTerms)
        {
            if (relation == null)
            {
                return 0d;
            }

            var combined = $"{relation.SubjectEntity} {relation.RelationType} {relation.ObjectEntity}";
            var keywordScore = GetKeywordMatchScore(combined, keywordTerms);
            var exactBonus = (!string.IsNullOrWhiteSpace(relation.SubjectEntity) && queryText.Contains(relation.SubjectEntity, StringComparison.OrdinalIgnoreCase) ? 1.2d : 0d)
                           + (!string.IsNullOrWhiteSpace(relation.ObjectEntity) && queryText.Contains(relation.ObjectEntity, StringComparison.OrdinalIgnoreCase) ? 0.8d : 0d);

            return keywordScore + exactBonus + ComputeRelationIntentBonus(queryText, relation.RelationType) + (relation.ConfidenceScore * 0.25d);
        }

        private static double ComputeRelationIntentBonus(string queryText, string relationType)
        {
            if (string.IsNullOrWhiteSpace(queryText) || string.IsNullOrWhiteSpace(relationType))
            {
                return 0d;
            }

            var normalizedQuery = queryText.Trim().ToLowerInvariant();
            var normalizedRelation = relationType.Trim().ToLowerInvariant();

            if ((normalizedQuery.Contains("负责人") || normalizedQuery.Contains("谁负责") || normalizedQuery.Contains("处理")) && normalizedRelation == "responsible_for")
            {
                return 2.2d;
            }

            if ((normalizedQuery.Contains("上级") || normalizedQuery.Contains("汇报") || normalizedQuery.Contains("主管")) && (normalizedRelation == "reports_to" || normalizedRelation == "responsible_for"))
            {
                return 2.4d;
            }

            if ((normalizedQuery.Contains("ip") || normalizedQuery.Contains("地址") || normalizedQuery.Contains("端口")) && normalizedRelation == "ip_address")
            {
                return 2.1d;
            }

            if ((normalizedQuery.Contains("属于") || normalizedQuery.Contains("哪条线") || normalizedQuery.Contains("归属")) && normalizedRelation == "belongs_to")
            {
                return 1.9d;
            }

            if ((normalizedQuery.Contains("连接") || normalizedQuery.Contains("关联") || normalizedQuery.Contains("依赖")) && (normalizedRelation == "connected_to" || normalizedRelation == "uses"))
            {
                return 1.6d;
            }

            if ((normalizedQuery.Contains("偏好") || normalizedQuery.Contains("喜欢") || normalizedQuery.Contains("常用")) && normalizedRelation == "prefers")
            {
                return 1.5d;
            }

            return 0d;
        }

        /// <summary>
        /// 去重、合并与压缩用户的长期记忆
        /// </summary>
        public async Task MaintainAndMergeUserMemoriesAsync(string userId, string systemId = "default")
        {
            var db = _memoryService.GetDb().Context;
            var currentSystemId = systemId; // 局部变量，用于 Lambda 捕获
            var enhancementSchemaReady = await EnsureMemoryEnhancementSchemaAsync();

            // 1. 获取用户所有活跃的长期记忆
            var activeMemories = await db.Queryable<Agent_MemoryEntity>()
                .Where(m => m.SystemId == currentSystemId
                         && m.UserId == userId
                         && m.IsActive == true
                         && (!enhancementSchemaReady || m.MemoryStatus == null || m.MemoryStatus == "active"))
                .OrderBy(m => m.CreatedAt) // 按时间排序，保留时间轴
                .ToListAsync();

            if (activeMemories.Count < 5) 
            {
                // 如果记忆数量较少，无需合并压缩
                return;
            }

            try
            {
                var dictContent = activeMemories.Select(m => $"[{m.MemoryType}] {m.Content} (Score:{m.ImportanceScore})").ToList();
                string mergeInput = string.Join("\n", dictContent);

                var model = await SelectChatModelAsync();
                var systemPrompt = @"你是一个记忆整理与压缩专家。以下是同一个用户累积的杂乱的机器记忆列表。
请执行以下操作：
1. 去重：剔除语义重复的信息。
2. 合并：将矛盾或补充的信息进行合并（以最新的事实为准）。
3. 压缩：将多条零散关联的记忆浓缩为干练的条目（如习惯、事实或特定事件）。
仅返回 JSON 格式结果，不包含Markdown块标记，不解释。
返回格式：
{
  ""memories"": [
    { ""content"": ""合并后一句话记忆..."", ""memory_type"": ""preference|fact|event"", ""importance"": 1-10 }
  ]
}";

                var payload = JsonSerializer.Serialize(new
                {
                    model = model.modelCode,
                    temperature = 0.1,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = mergeInput }
                    }
                });

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(model.timeoutSeconds) };
                if (!string.IsNullOrWhiteSpace(model.apiKey))
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", model.apiKey);

                var rsp = await http.PostAsync(model.baseUrl.TrimEnd('/') + "/chat/completions", new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
                if (rsp.IsSuccessStatusCode)
                {
                    var json = await rsp.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

                    var parsedItems = ParseExtractedMemories(text);
                    if (parsedItems.Count > 0)
                    {
                        // 2. 保留时间线，仅对完全重复的旧记忆做软归档，避免“以新覆盖旧”导致历史丢失
                        var duplicateIds = activeMemories
                            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                            .GroupBy(m => $"{m.MemoryType}::{m.Content.Trim()}", StringComparer.OrdinalIgnoreCase)
                            .SelectMany(group => group.OrderByDescending(m => m.UpdatedAt).Skip(1).Select(m => m.Id))
                            .ToList();

                        if (duplicateIds.Count > 0)
                        {
                            await db.Updateable<Agent_MemoryEntity>()
                                .SetColumns(m => m.IsActive == false)
                                .Where(m => duplicateIds.Contains(m.Id))
                                .ExecuteCommandAsync();
                        }

                        // 3. 写入新合并的记忆摘要（保留原始时间线，同时新增整理后的摘要）
                        foreach (var item in parsedItems)
                        {
                            if (string.IsNullOrWhiteSpace(item.Content)) continue;
                            await SaveLongTermMemoryAsync(userId, item.Content, item.MemoryType, item.ImportanceScore, null, currentSystemId);
                        }

                        _logger.LogInformation("[Memory] 记忆合并完成: User={UserId}, 原始数量={Old}, 合并后={New}", userId, activeMemories.Count, parsedItems.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Memory] 长期记忆维护与合并失败 User={UserId}", userId);
            }
        }

        public async Task<List<Agent_MemoryEntity>> SearchLongTermMemoryAsync(
            string userId,
            string sessionId,
            string queryText,
            int topK = 5,
            string systemId = "default",
            string timeRange = null,
            DateTime? startTime = null,
            DateTime? endTime = null,
            bool preferKeywordSearch = false)
        {
            topK = Math.Clamp(topK, 1, 20);
            var candidateTopK = Math.Clamp(topK * 5, 20, 100);
            var db = _memoryService.GetDb().Context;
            var enhancementSchemaReady = await EnsureMemoryEnhancementSchemaAsync();
            List<Agent_MemoryEntity>? memories = null;
            var hasSemanticRanking = false;

            if (db.CurrentConnectionConfig.DbType == SqlSugar.DbType.PostgreSQL)
            {
                try
                {
                    var queryEmbedding = await GenerateEmbeddingAsync(queryText);
                    var vectorLiteral = ToPgVectorString(queryEmbedding);

                    var sql = @"SELECT
                                    id as Id,
                                    system_id as SystemId,
                                    user_id as UserId,
                                    agent_id as AgentId,
                                    memory_type as MemoryType,
                                    content as Content,
                                    embedding::text as Embedding,
                                    is_high_quality as IsHighQuality,
                                    importance_score as ImportanceScore,
                                    source_session_id as SourceSessionId,
                                    is_active as IsActive,
                                    created_at as CreatedAt,
                                    updated_at as UpdatedAt
                                FROM agent_memories
                                WHERE system_id = @systemId
                                    AND user_id = @userId
                                    AND is_active = true
                                    AND (@useLineage = false OR COALESCE(memory_status, 'active') = 'active')
                                ORDER BY
                                    (
                                        @weightA * (
                                            CASE
                                                WHEN embedding IS NULL THEN 0.0
                                                ELSE GREATEST(0.0, 1.0 - (embedding <=> @queryVector::vector))
                                            END
                                        )
                                        +
                                        @weightB * LEAST(1.0, GREATEST(0.0, COALESCE(importance_score, 0.0) / 10.0))
                                        +
                                        @weightC * EXP(
                                            -@timeDecayLambda * GREATEST(
                                                0.0,
                                                EXTRACT(EPOCH FROM (NOW() - COALESCE(created_at, NOW()))) / 86400.0
                                            )
                                        )
                                    ) DESC,
                                    created_at DESC
                                LIMIT @candidateTopK";

                    memories = await db.Ado.SqlQueryAsync<Agent_MemoryEntity>(
                        sql,
                        new SugarParameter("@systemId", systemId),
                        new SugarParameter("@userId", userId),
                        new SugarParameter("@queryVector", vectorLiteral),
                        new SugarParameter("@sessionId", sessionId),
                        new SugarParameter("@useLineage", enhancementSchemaReady),
                        new SugarParameter("@weightA", RetrievalWeightSimilarity),
                        new SugarParameter("@weightB", RetrievalWeightImportance),
                        new SugarParameter("@weightC", RetrievalWeightTimeDecay),
                        new SugarParameter("@timeDecayLambda", TimeDecayLambdaPerDay),
                        new SugarParameter("@candidateTopK", candidateTopK));
                    hasSemanticRanking = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Memory] PostgreSQL 向量检索失败，降级到普通检索");
                }
            }

            if (memories == null)
            {
                // 降级检索：使用 Linq
                memories = await db.Queryable<Agent_MemoryEntity>()
                    .Where(m => m.SystemId == systemId
                             && m.UserId == userId
                             && m.IsActive == true
                             && (!enhancementSchemaReady || m.MemoryStatus == null || m.MemoryStatus == "active"))
                    .Select(m => new Agent_MemoryEntity
                    {
                        Id = m.Id,
                        SystemId = m.SystemId,
                        UserId = m.UserId,
                        AgentId = m.AgentId,
                        MemoryType = m.MemoryType,
                        Content = m.Content,
                        IsHighQuality = m.IsHighQuality,
                        ImportanceScore = m.ImportanceScore,
                        SourceSessionId = m.SourceSessionId,
                        IsActive = m.IsActive,
                        CreatedAt = m.CreatedAt,
                        UpdatedAt = m.UpdatedAt
                    })
                    .ToListAsync();
            }

            var workingMemories = SearchWorkingMemoryBuffer(userId, queryText, Math.Min(candidateTopK, WorkingMemorySearchWindow), systemId, sessionId);
            if (workingMemories.Count > 0)
            {
                memories = memories
                    .Concat(workingMemories)
                    .GroupBy(m => $"{m.MemoryType}::{m.Content?.Trim()}" ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(m => m.CreatedAt).First())
                    .ToList();
            }

            return ApplySearchFiltersAndRanking(memories, queryText, topK, timeRange, startTime, endTime, preferKeywordSearch, hasSemanticRanking);
        }

        private List<Agent_MemoryEntity> ApplySearchFiltersAndRanking(
            List<Agent_MemoryEntity> memories,
            string queryText,
            int topK,
            string timeRange,
            DateTime? startTime,
            DateTime? endTime,
            bool preferKeywordSearch,
            bool hasSemanticRanking)
        {
            if (memories == null || memories.Count == 0)
            {
                return new List<Agent_MemoryEntity>();
            }

            var original = memories;
            var filtered = memories.AsEnumerable();
            var (resolvedStart, resolvedEnd) = ResolveTimeWindow(timeRange, startTime, endTime);

            if (resolvedStart.HasValue)
            {
                filtered = filtered.Where(m => m.CreatedAt >= resolvedStart.Value);
            }

            if (resolvedEnd.HasValue)
            {
                filtered = filtered.Where(m => m.CreatedAt <= resolvedEnd.Value);
            }

            var filteredList = filtered.ToList();
            var hasTimeWindow = resolvedStart.HasValue || resolvedEnd.HasValue;
            if (filteredList.Count == 0 && !hasTimeWindow)
            {
                filteredList = original;
            }

            var keywordTerms = BuildKeywordTerms(queryText);
            var reranked = filteredList
                .Select((memory, index) => new
                {
                    Memory = memory,
                    KeywordScore = GetKeywordMatchScore(memory.Content, keywordTerms),
                    Score = (hasSemanticRanking
                                ? Math.Max(0d, 1d - (index / (double)Math.Max(1, filteredList.Count))) * 0.45d
                                : 0d)
                            + (preferKeywordSearch
                                ? GetKeywordMatchScore(memory.Content, keywordTerms) * 0.20d
                                : GetKeywordMatchScore(memory.Content, keywordTerms) * 0.10d)
                            + ComputeExactPhraseBonus(queryText, memory.Content) * 0.15d
                            + ComputeQueryIntentBonus(queryText, memory.MemoryType) * 0.10d
                            + (memory.ImportanceScore / 10d) * 0.20d
                            + ComputeRecencyBoost(memory.CreatedAt) * 0.10d
                })
                .ToList();

            if (preferKeywordSearch && keywordTerms.Count > 0 && reranked.Any(item => item.KeywordScore > 0))
            {
                reranked = reranked
                    .Where(item => item.KeywordScore > 0 || item.Memory.IsHighQuality)
                    .ToList();
            }

            return reranked
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Memory.ImportanceScore)
                .ThenByDescending(item => item.Memory.CreatedAt)
                .Take(topK)
                .Select(item => item.Memory)
                .ToList();
        }

        private static (DateTime? Start, DateTime? End) ResolveTimeWindow(string timeRange, DateTime? startTime, DateTime? endTime)
        {
            if (startTime.HasValue || endTime.HasValue)
            {
                return (startTime, endTime);
            }

            if (string.IsNullOrWhiteSpace(timeRange))
            {
                return (null, null);
            }

            var now = DateTime.Now;
            var normalized = timeRange.Trim().ToLowerInvariant();

            return normalized switch
            {
                "today" or "今天" => (now.Date, now),
                "yesterday" or "昨天" => (now.Date.AddDays(-1), now.Date.AddTicks(-1)),
                "last_7_days" or "last7days" or "最近7天" => (now.Date.AddDays(-7), now),
                "last_30_days" or "last30days" or "最近30天" => (now.Date.AddDays(-30), now),
                "last_week" or "上周" => ResolveLastWeek(now),
                "this_week" or "本周" => ResolveThisWeek(now),
                "last_month" or "上个月" => ResolveLastMonth(now),
                "this_month" or "本月" => (new DateTime(now.Year, now.Month, 1), now),
                "all" or "全部" => (null, null),
                _ => (null, null)
            };
        }

        private static (DateTime? Start, DateTime? End) ResolveLastWeek(DateTime now)
        {
            var dayOfWeek = ((int)now.DayOfWeek + 6) % 7;
            var thisWeekStart = now.Date.AddDays(-dayOfWeek);
            var lastWeekStart = thisWeekStart.AddDays(-7);
            return (lastWeekStart, thisWeekStart.AddTicks(-1));
        }

        private static (DateTime? Start, DateTime? End) ResolveThisWeek(DateTime now)
        {
            var dayOfWeek = ((int)now.DayOfWeek + 6) % 7;
            var thisWeekStart = now.Date.AddDays(-dayOfWeek);
            return (thisWeekStart, now);
        }

        private static (DateTime? Start, DateTime? End) ResolveLastMonth(DateTime now)
        {
            var firstDayOfThisMonth = new DateTime(now.Year, now.Month, 1);
            var firstDayOfLastMonth = firstDayOfThisMonth.AddMonths(-1);
            return (firstDayOfLastMonth, firstDayOfThisMonth.AddTicks(-1));
        }

        private static List<string> BuildKeywordTerms(string queryText)
        {
            if (string.IsNullOrWhiteSpace(queryText))
            {
                return new List<string>();
            }

            return queryText
                .Split(new[] { ' ', '\t', '\r', '\n', '，', ',', '。', '.', '、', ';', '；', ':', '：', '?', '？' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(term => term.Trim())
                .Where(term => term.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }

        private static int GetKeywordMatchScore(string? content, List<string> keywordTerms)
        {
            if (string.IsNullOrWhiteSpace(content) || keywordTerms == null || keywordTerms.Count == 0)
            {
                return 0;
            }

            var score = 0;
            foreach (var term in keywordTerms)
            {
                if (content.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    score++;
                }
            }

            return score;
        }

        private static double ComputeExactPhraseBonus(string queryText, string? content)
        {
            if (string.IsNullOrWhiteSpace(queryText) || string.IsNullOrWhiteSpace(content))
            {
                return 0d;
            }

            var normalizedQuery = queryText.Trim();
            if (content.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                return 1.5d;
            }

            var keywordTerms = BuildKeywordTerms(queryText);
            if (keywordTerms.Count == 0)
            {
                return 0d;
            }

            var matchedChars = keywordTerms
                .Where(term => content.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Sum(term => term.Length);

            return Math.Min(1.0d, matchedChars / Math.Max(1d, normalizedQuery.Length));
        }

        private static double ComputeQueryIntentBonus(string queryText, string? memoryType)
        {
            if (string.IsNullOrWhiteSpace(queryText) || string.IsNullOrWhiteSpace(memoryType))
            {
                return 0d;
            }

            var normalizedQuery = queryText.Trim().ToLowerInvariant();
            var normalizedType = memoryType.Trim().ToLowerInvariant();

            if ((normalizedQuery.Contains("偏好") || normalizedQuery.Contains("喜欢") || normalizedQuery.Contains("习惯") || normalizedQuery.Contains("风格")) && normalizedType == "preference")
            {
                return 1.0d;
            }

            if ((normalizedQuery.Contains("身份") || normalizedQuery.Contains("画像") || normalizedQuery.Contains("persona") || normalizedQuery.Contains("profile")) && normalizedType == CoreMemoryType)
            {
                return 1.2d;
            }

            if ((normalizedQuery.Contains("上周") || normalizedQuery.Contains("最近") || normalizedQuery.Contains("之前") || normalizedQuery.Contains("发生")) && normalizedType == "event")
            {
                return 0.8d;
            }

            if ((normalizedQuery.Contains("事实") || normalizedQuery.Contains("信息") || normalizedQuery.Contains("资料") || normalizedQuery.Contains("结论")) && normalizedType == "fact")
            {
                return 0.8d;
            }

            return 0d;
        }

        private static double ComputeRecencyBoost(DateTime createdAt)
        {
            if (createdAt == default)
            {
                return 0d;
            }

            var days = Math.Max(0d, (DateTime.Now - createdAt).TotalDays);
            return Math.Exp(-0.12d * days);
        }

        private async Task<float[]> GenerateEmbeddingAsync(string text)
        {
            try
            {
                var model = await SelectEmbeddingModelAsync();
                var result = await RequestEmbeddingAsync(
                    text,
                    model.baseUrl,
                    model.apiKey,
                    model.modelCode,
                    model.timeoutSeconds);

                if (result != null && result.Length > 0)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Memory] 调用 Embedding 接口失败，使用本地兜底向量");
            }

            return BuildDeterministicFallbackEmbedding(text);
        }

        private async Task<(string baseUrl, string apiKey, string modelCode, int timeoutSeconds)> SelectEmbeddingModelAsync()
        {
            var selected = (await _purposeResolver.ResolveAsync(AiModelPurposes.MemoryEmbedding)).Model;
            return (RequireModelBaseUrl(selected, AiModelPurposes.MemoryEmbedding),
                selected.ApiKey ?? string.Empty,
                RequireModelCode(selected, AiModelPurposes.MemoryEmbedding),
                AppsettingsConfig.AIAgent?.TimeoutSeconds ?? 120);
        }

        private static string RequireModelBaseUrl(LLM_ModelEntity model, string purpose)
        {
            if (!Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException($"AI 用途 {purpose} 的模型 {model.Id} 未配置有效连接地址");
            return model.BaseUrl;
        }

        private static string RequireModelCode(LLM_ModelEntity model, string purpose)
        {
            if (string.IsNullOrWhiteSpace(model.ModelCode))
                throw new InvalidOperationException($"AI 用途 {purpose} 的模型 {model.Id} 未配置模型编码");
            return model.ModelCode;
        }

        private async Task<float[]> RequestEmbeddingAsync(string text, string baseUrl, string apiKey, string modelCode, int timeoutSeconds)
        {
            var url = baseUrl.TrimEnd('/') + "/embeddings";
            var payload = JsonSerializer.Serialize(new
            {
                model = modelCode,
                input = text
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds));

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var data = root.GetProperty("data");

            if (data.GetArrayLength() == 0)
            {
                return Array.Empty<float>();
            }

            var embedding = data[0].GetProperty("embedding");
            var values = new float[embedding.GetArrayLength()];
            var index = 0;
            foreach (var item in embedding.EnumerateArray())
            {
                values[index++] = item.GetSingle();
            }

            return values;
        }

        private static string ToPgVectorString(float[] vector)
        {
            if (vector == null || vector.Length == 0)
            {
                return null;
            }

            var sb = new StringBuilder(vector.Length * 8);
            sb.Append('[');
            for (var i = 0; i < vector.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(vector[i].ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static float[] BuildDeterministicFallbackEmbedding(string text)
        {
            const int dim = 1536;
            var source = string.IsNullOrWhiteSpace(text) ? "empty" : text;
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
            var vec = new float[dim];

            for (var i = 0; i < dim; i++)
            {
                var b = bytes[i % bytes.Length];
                vec[i] = (b / 127.5f) - 1f;
            }

            return vec;
        }

        #endregion
    }
}
