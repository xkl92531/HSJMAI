using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public class DeviceHealthSemanticClassifierService : ISelfScopedAutoInject
    {
        public const string ClassifierVersion = "device-health-semantic-v1";

        private const int BatchSize = 12;
        private const int MaxConcurrency = 2;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private const string SystemPrompt =
            """
            你是资深西门子 TIA Portal / PLC 设备诊断语义分析专家。
            你的任务是根据 PLC 逻辑片段的结构上下文，对候选输出变量做“设备健康诊断角色”分类。

            重要原则：
            - 不要主要依赖变量名关键词。变量名可能极不规范、拼写错误、混用中英文。
            - 优先参考同一 Network 的输入变量、输出变量、块名、网络标题、变量作用域、写入位置上下文。
            - 区分“根因锚点”和“症状/汇总/上下文/内部派生位”。
            - LocalOnly 内部变量可作为诊断证据，但通常不建议直接作为运行时监控根因点。
            - Message/HMI 显示位通常是上下文，不是根因锚点。
            - Warning/Alarm 汇总位可以监控，但若同一网络存在更细的 Torque/Fault/Drive/Hold/Done 等信号，优先级应低于更细根因。
            - 位置完成/到位/Done/OK 类反馈通常 False 为异常。
            - 报警/故障/保持/互锁类通常 True 为异常。
            - 模拟量、设定值、当前位置、扭矩、电流等通常是 ContextOnly，除非明显是阈值报警输出。

            anchorType 只能取以下值之一：
            - DeviceAlarmState：设备报警/故障/驱动异常/安全异常输出位，通常 True 异常
            - DeviceMotionFeedback：动作完成、到位、回原、任务完成等反馈位，通常 False 异常
            - DeviceHoldState：保持、暂停、互锁、禁止、阻塞等状态，通常 True 异常
            - DevicePermitState：许可、就绪、允许、使能等状态，通常 False 异常
            - DeviceAnalogContext：设定值、当前位置、实际值、速度、扭矩、电流等上下文
            - DeviceMessageContext：HMI 消息、提示、显示位、文本选择位
            - InternalDerivedState：内部派生/中间状态，可解释但不宜直接监控
            - Ignore：与设备健康停机诊断无关
            - Unknown：信息不足

            monitoringRole 只能取以下值之一：
            - RootCause：适合作为设备内部停机根因监控点
            - Symptom：适合监控但更像报警汇总/症状
            - Context：只作为解释上下文
            - InternalEvidence：内部证据，可帮助 LLM 推断，但不直接监控
            - Ignore：忽略
            - Review：需要人工复核

            activationRule 只能取：BOOL_TRUE / BOOL_FALSE / UNKNOWN
            abnormalWhen 只能取：True / False / ContextOnly / Unknown

            rootPriority 是 0-100：
            - 85-100：强根因候选，建议监控
            - 65-84：有价值症状或强反馈，建议按需监控
            - 35-64：上下文/内部证据
            - 0-34：忽略或弱相关

            请严格输出 JSON 数组，不要输出额外文字。字段：
            [
              {
                "varName": "原变量名",
                "anchorType": "DeviceAlarmState",
                "semanticCategory": "AlarmFaultWarning",
                "semanticLabel": "简短中文标签",
                "monitoringRole": "RootCause",
                "activationRule": "BOOL_TRUE",
                "abnormalWhen": "True",
                "rootPriority": 90,
                "confidence": 0.88,
                "reason": "一句话理由",
                "contextVariables": ["相关变量1", "相关变量2"]
              }
            ]
            """;

        private readonly LLMModelService _llmModelService;
        private readonly IPLC_DeviceHealthSemanticFindingService _semanticFindingService;
        private readonly ILogger<DeviceHealthSemanticClassifierService> _logger;

        public DeviceHealthSemanticClassifierService(
            LLMModelService llmModelService,
            IPLC_DeviceHealthSemanticFindingService semanticFindingService,
            ILogger<DeviceHealthSemanticClassifierService> logger)
        {
            _llmModelService = llmModelService;
            _semanticFindingService = semanticFindingService;
            _logger = logger;
        }

        public async Task<DeviceHealthSemanticEnrichmentResult> EnrichAsync(
            DeviceHealthPreExtractRequestDto request,
            List<DeviceHealthPreExtractCandidateDto> candidates)
        {
            var result = new DeviceHealthSemanticEnrichmentResult
            {
                ClassifierVersion = ClassifierVersion
            };

            if (request == null || candidates == null || candidates.Count == 0)
            {
                return result;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var candidatesWithHash = candidates
                .Where(it => it.LogicUnitId.HasValue)
                .Where(it => !string.IsNullOrWhiteSpace(it.VariableName))
                .Where(it => !string.IsNullOrWhiteSpace(it.SourceHash))
                .ToList();

            if (candidatesWithHash.Count == 0)
            {
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }

            var pending = candidatesWithHash;
            if (request.UseSemanticCache && !request.RefreshSemanticCache)
            {
                try
                {
                    var cached = await LoadCachedRowsAsync(request.ProjectId, candidatesWithHash);
                    result.CacheHitCount = ApplyCachedRows(candidatesWithHash, cached);
                    pending = candidatesWithHash
                        .Where(it => !string.Equals(it.SemanticSource, "Cache", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DeviceHealthSemantic] Semantic cache unavailable; continuing without cache.");
                    result.ErrorMessage = $"Semantic cache unavailable: {ex.Message}";
                }
            }

            var limit = ResolveLlmCandidateLimit(request.MaxLlmCandidates, 80, 5000);
            var pendingBeforeLimitCount = pending.Count;
            pending = pending
                .OrderByDescending(it => it.RecommendedForMonitoring)
                .ThenByDescending(it => it.Score)
                .ThenBy(it => it.BlockName)
                .ThenBy(it => it.NetworkId)
                .ThenBy(it => it.VariableName)
                .Take(limit)
                .ToList();
            result.LimitSkippedCount = Math.Max(0, pendingBeforeLimitCount - pending.Count);

            result.SentCount = pending.Count;
            if (pending.Count == 0)
            {
                result.ElapsedMs = sw.ElapsedMilliseconds;
                return result;
            }

            try
            {
                var model = await _llmModelService.RecommendModelAsync(requiredContextSize: 12000);
                if (model == null)
                {
                    result.ErrorMessage = "No available LLM model.";
                    result.ElapsedMs = sw.ElapsedMilliseconds;
                    return result;
                }

                var chatClient = await _llmModelService.CreateChatClientByModelIdAsync(model.Id);
                var batches = pending
                    .Select((it, idx) => (it, idx))
                    .GroupBy(it => it.idx / BatchSize)
                    .Select(g => g.Select(x => x.it).ToList())
                    .ToList();

                var sem = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
                var tasks = batches.Select(async batch =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        return await ClassifyBatchAsync(chatClient, batch);
                    }
                    finally
                    {
                        sem.Release();
                    }
                });

                var batchResults = await Task.WhenAll(tasks);
                var rowsToPersist = new List<PLC_DeviceHealthSemanticFindingEntity>();
                foreach (var batchResult in batchResults)
                {
                    result.ReturnedCount += batchResult.Items.Count;
                    foreach (var item in batchResult.Items)
                    {
                        var candidate = pending.FirstOrDefault(it =>
                            string.Equals(it.VariableName, item.VarName, StringComparison.OrdinalIgnoreCase));
                        if (candidate == null)
                        {
                            continue;
                        }

                        ApplyLlmResult(candidate, item, batchResult.RawResponse);
                        rowsToPersist.Add(BuildEntity(request.ProjectId, candidate, item, batchResult.RawResponse));
                    }
                }

                if (request.PersistSemanticCache && rowsToPersist.Count > 0)
                {
                    await PersistRowsAsync(request.ProjectId, rowsToPersist);
                    result.PersistedCount = rowsToPersist.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DeviceHealthSemantic] LLM semantic classification failed.");
                result.ErrorMessage = ex.Message;
            }

            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        private async Task<List<PLC_DeviceHealthSemanticFindingEntity>> LoadCachedRowsAsync(
            long projectId,
            IReadOnlyList<DeviceHealthPreExtractCandidateDto> candidates)
        {
            var logicUnitIds = candidates
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            if (logicUnitIds.Count == 0)
            {
                return new List<PLC_DeviceHealthSemanticFindingEntity>();
            }

            var rows = await _semanticFindingService.GetListAsync(it =>
                it.IsDeleted == false
                && it.ProjectId == projectId
                && it.LogicUnitId.HasValue
                && logicUnitIds.Contains(it.LogicUnitId.Value)
                && it.ClassifierVersion == ClassifierVersion);

            var wantedKeys = candidates
                .Select(BuildCacheKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return rows
                .Where(it => wantedKeys.Contains(BuildCacheKey(it.LogicUnitId, it.VariableName, it.SourceHash)))
                .ToList();
        }

        private static int ApplyCachedRows(
            IReadOnlyList<DeviceHealthPreExtractCandidateDto> candidates,
            IReadOnlyList<PLC_DeviceHealthSemanticFindingEntity> cachedRows)
        {
            if (cachedRows == null || cachedRows.Count == 0)
            {
                return 0;
            }

            var byKey = cachedRows
                .GroupBy(it => BuildCacheKey(it.LogicUnitId, it.VariableName, it.SourceHash), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(it => it.ModifyTime ?? it.CreateTime ?? DateTime.MinValue).First(), StringComparer.OrdinalIgnoreCase);

            var count = 0;
            foreach (var candidate in candidates)
            {
                if (!byKey.TryGetValue(BuildCacheKey(candidate), out var row))
                {
                    continue;
                }

                ApplyCachedRow(candidate, row);
                count++;
            }

            return count;
        }

        private async Task<DeviceHealthBatchResult> ClassifyBatchAsync(
            OpenAI.Chat.ChatClient chatClient,
            IReadOnlyList<DeviceHealthPreExtractCandidateDto> batch)
        {
            var prompt = BuildPrompt(batch);
            await WriteLogHelper.WriteLogsAsync(
                $"=== DeviceHealthSemantic LLM Input ({batch.Count}) ===\n{prompt}",
                "DeviceHealthSemantic_LLM_Input");

            var response = await _llmModelService.ChatAsync(chatClient, prompt, SystemPrompt);
            var content = response?.Content ?? string.Empty;

            await WriteLogHelper.WriteLogsAsync(
                $"=== DeviceHealthSemantic LLM Output ({batch.Count}) ===\n{content}",
                "DeviceHealthSemantic_LLM_Output");

            return new DeviceHealthBatchResult
            {
                RawResponse = content,
                Items = ParseResponse(content)
            };
        }

        private static string BuildPrompt(IReadOnlyList<DeviceHealthPreExtractCandidateDto> batch)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请对以下 PLC 设备健康候选变量逐一分类。");
            sb.AppendLine("注意：规则预分类仅供参考，不要被变量名关键词强行带偏。");
            sb.AppendLine();

            foreach (var group in batch
                .GroupBy(it => $"{it.BlockName}|{it.LogicUnitId}|{it.NetworkId}", StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                sb.AppendLine($"--- LogicUnitId={first.LogicUnitId}, Block={first.BlockName}, Network={first.NetworkId} ---");
                if (!string.IsNullOrWhiteSpace(first.NetworkTitle))
                {
                    sb.AppendLine($"网络标题: {first.NetworkTitle}");
                }
                sb.AppendLine($"UnitType: {first.UnitType}");
                sb.AppendLine($"候选输出全集: {JoinLimited(first.OutputVariables, 16)}");
                sb.AppendLine($"输入变量: {JoinLimited(first.InputVariables, 24)}");
                sb.AppendLine();
                foreach (var c in group)
                {
                    sb.AppendLine($"变量: {c.VariableName}");
                    sb.AppendLine($"  规则预判: anchorType={c.AnchorType}, category={c.SemanticCategory}, abnormalWhen={c.AbnormalWhen}, score={c.Score}");
                    sb.AppendLine($"  可监控性: {c.Monitorability}, scope={c.VariableScope}, recommended={c.RecommendedForMonitoring}");
                    sb.AppendLine($"  规则证据: {JoinLimited(c.Evidence, 4)}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static List<DeviceHealthLlmItem> ParseResponse(string content)
        {
            var result = new List<DeviceHealthLlmItem>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return result;
            }

            try
            {
                var start = content.IndexOf('[');
                var end = content.LastIndexOf(']');
                if (start < 0 || end <= start)
                {
                    return result;
                }

                var json = content[start..(end + 1)];
                var items = JsonSerializer.Deserialize<List<DeviceHealthLlmItem>>(json, JsonOptions);
                if (items == null)
                {
                    return result;
                }

                return items
                    .Where(it => !string.IsNullOrWhiteSpace(it.VarName))
                    .Select(NormalizeItem)
                    .ToList();
            }
            catch
            {
                return result;
            }
        }

        private static DeviceHealthLlmItem NormalizeItem(DeviceHealthLlmItem item)
        {
            item.AnchorType = NormalizeAnchorType(item.AnchorType);
            item.MonitoringRole = NormalizeMonitoringRole(item.MonitoringRole);
            item.ActivationRule = NormalizeActivationRule(item.ActivationRule);
            item.AbnormalWhen = NormalizeAbnormalWhen(item.AbnormalWhen);
            item.RootPriority = Math.Max(0, Math.Min(100, item.RootPriority));
            item.Confidence = Math.Max(0m, Math.Min(0.99m, item.Confidence));
            if (string.IsNullOrWhiteSpace(item.SemanticCategory))
            {
                item.SemanticCategory = item.AnchorType;
            }
            return item;
        }

        private static void ApplyLlmResult(
            DeviceHealthPreExtractCandidateDto candidate,
            DeviceHealthLlmItem item,
            string rawResponse)
        {
            candidate.AnchorType = item.AnchorType;
            candidate.SemanticCategory = string.IsNullOrWhiteSpace(item.SemanticCategory) ? item.AnchorType : item.SemanticCategory;
            candidate.SemanticLabel = item.SemanticLabel;
            candidate.MonitoringRole = item.MonitoringRole;
            candidate.ActivationRule = item.ActivationRule;
            candidate.AbnormalWhen = item.AbnormalWhen;
            candidate.RootPriority = item.RootPriority;
            candidate.Confidence = item.Confidence <= 0 ? candidate.Confidence : item.Confidence;
            candidate.Score = item.RootPriority;
            candidate.SemanticSource = "LLM";
            candidate.RecommendedForMonitoring = ShouldRecommend(candidate);

            candidate.Evidence ??= new List<string>();
            candidate.Evidence.Insert(0, string.IsNullOrWhiteSpace(item.Reason)
                ? "LLM semantic classification applied."
                : $"LLM: {item.Reason}");
            if (item.ContextVariables?.Count > 0)
            {
                candidate.Evidence.Add($"LLM context: {string.Join(", ", item.ContextVariables.Take(8))}");
            }
        }

        private static void ApplyCachedRow(
            DeviceHealthPreExtractCandidateDto candidate,
            PLC_DeviceHealthSemanticFindingEntity row)
        {
            candidate.AnchorType = row.AnchorType;
            candidate.SemanticCategory = row.SemanticCategory;
            candidate.SemanticLabel = row.SemanticLabel;
            candidate.MonitoringRole = row.MonitoringRole;
            candidate.ActivationRule = row.ActivationRule;
            candidate.AbnormalWhen = row.AbnormalWhen;
            candidate.RootPriority = row.RootPriority;
            candidate.Confidence = row.Confidence ?? candidate.Confidence;
            candidate.Score = row.RootPriority ?? candidate.Score;
            candidate.SemanticSource = "Cache";
            candidate.RecommendedForMonitoring = ShouldRecommend(candidate);

            candidate.Evidence ??= new List<string>();
            if (!string.IsNullOrWhiteSpace(row.Reason))
            {
                candidate.Evidence.Insert(0, $"Semantic cache: {row.Reason}");
            }
        }

        private static PLC_DeviceHealthSemanticFindingEntity BuildEntity(
            long projectId,
            DeviceHealthPreExtractCandidateDto candidate,
            DeviceHealthLlmItem item,
            string rawResponse)
        {
            return new PLC_DeviceHealthSemanticFindingEntity
            {
                ProjectId = projectId,
                StationCode = candidate.StationCode,
                BlockId = candidate.BlockId,
                BlockName = candidate.BlockName,
                LogicUnitId = candidate.LogicUnitId,
                NetworkId = candidate.NetworkId,
                VariableName = candidate.VariableName,
                AnchorType = item.AnchorType,
                SemanticCategory = string.IsNullOrWhiteSpace(item.SemanticCategory) ? item.AnchorType : item.SemanticCategory,
                SemanticLabel = item.SemanticLabel,
                MonitoringRole = item.MonitoringRole,
                ActivationRule = item.ActivationRule,
                AbnormalWhen = item.AbnormalWhen,
                RootPriority = item.RootPriority,
                Confidence = item.Confidence,
                Reason = item.Reason,
                ContextVariablesJson = Serialize(item.ContextVariables ?? new List<string>()),
                InputVariablesJson = Serialize(candidate.InputVariables ?? new List<string>()),
                OutputVariablesJson = Serialize(candidate.OutputVariables ?? new List<string>()),
                VariableScope = candidate.VariableScope,
                Monitorability = candidate.Monitorability,
                RecommendedForMonitoring = candidate.RecommendedForMonitoring,
                ClassifierVersion = ClassifierVersion,
                SourceHash = candidate.SourceHash,
                RawResponse = rawResponse?.Length > 4000 ? rawResponse[..4000] : rawResponse,
                CreateTime = DateTime.Now,
                ModifyTime = DateTime.Now
            };
        }

        private async Task PersistRowsAsync(long projectId, IReadOnlyList<PLC_DeviceHealthSemanticFindingEntity> rows)
        {
            var logicUnitIds = rows
                .Where(it => it.LogicUnitId.HasValue)
                .Select(it => it.LogicUnitId.Value)
                .Distinct()
                .ToList();

            if (logicUnitIds.Count == 0)
            {
                return;
            }

            await _semanticFindingService.SoftDeleteAsync(it =>
                it.ProjectId == projectId
                && it.LogicUnitId.HasValue
                && logicUnitIds.Contains(it.LogicUnitId.Value)
                && it.ClassifierVersion == ClassifierVersion);

            await _semanticFindingService.BulkCopyAsync(rows.ToList());
        }

        private static bool ShouldRecommend(DeviceHealthPreExtractCandidateDto candidate)
        {
            if (candidate == null) return false;
            if (candidate.AlreadyInMonitoring) return false;
            if (!string.Equals(candidate.Monitorability, "RuntimeReadable", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(candidate.AnchorType, "DeviceAnalogContext", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(candidate.AnchorType, "DeviceMessageContext", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(candidate.AnchorType, "InternalDerivedState", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(candidate.AnchorType, "Ignore", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(candidate.AnchorType, "Unknown", StringComparison.OrdinalIgnoreCase)) return false;

            var priority = candidate.RootPriority ?? candidate.Score;
            if (string.Equals(candidate.MonitoringRole, "RootCause", StringComparison.OrdinalIgnoreCase))
            {
                return priority >= 75;
            }

            if (string.Equals(candidate.MonitoringRole, "Symptom", StringComparison.OrdinalIgnoreCase))
            {
                return priority >= 60
                       && string.Equals(candidate.AnchorType, "DeviceAlarmState", StringComparison.OrdinalIgnoreCase);
            }

            return priority >= 80;
        }

        private static string NormalizeAnchorType(string value)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "DeviceAlarmState",
                "DeviceMotionFeedback",
                "DeviceHoldState",
                "DevicePermitState",
                "DeviceAnalogContext",
                "DeviceMessageContext",
                "InternalDerivedState",
                "Ignore",
                "Unknown"
            };
            return allowed.Contains(value ?? string.Empty) ? value : "Unknown";
        }

        private static string NormalizeMonitoringRole(string value)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "RootCause", "Symptom", "Context", "InternalEvidence", "Ignore", "Review"
            };
            return allowed.Contains(value ?? string.Empty) ? value : "Review";
        }

        private static string NormalizeActivationRule(string value)
        {
            if (string.Equals(value, "BOOL_TRUE", StringComparison.OrdinalIgnoreCase)) return "BOOL_TRUE";
            if (string.Equals(value, "BOOL_FALSE", StringComparison.OrdinalIgnoreCase)) return "BOOL_FALSE";
            return "UNKNOWN";
        }

        private static string NormalizeAbnormalWhen(string value)
        {
            if (string.Equals(value, "True", StringComparison.OrdinalIgnoreCase)) return "True";
            if (string.Equals(value, "False", StringComparison.OrdinalIgnoreCase)) return "False";
            if (string.Equals(value, "ContextOnly", StringComparison.OrdinalIgnoreCase)) return "ContextOnly";
            return "Unknown";
        }

        private static string BuildCacheKey(DeviceHealthPreExtractCandidateDto candidate)
            => BuildCacheKey(candidate?.LogicUnitId, candidate?.VariableName, candidate?.SourceHash);

        private static string BuildCacheKey(long? logicUnitId, string variableName, string sourceHash)
            => $"{logicUnitId?.ToString() ?? string.Empty}:{variableName?.Trim() ?? string.Empty}:{sourceHash?.Trim() ?? string.Empty}";

        private static int ResolveLlmCandidateLimit(int value, int defaultValue, int maxValue)
        {
            if (value == 0) return maxValue;
            if (value < 0) return defaultValue;
            return Math.Min(value, maxValue);
        }

        private static string Serialize<T>(T value)
            => value == null ? string.Empty : JsonSerializer.Serialize(value, JsonOptions);

        private static string JoinLimited(IEnumerable<string> values, int limit)
            => values == null ? string.Empty : string.Join(", ", values.Where(it => !string.IsNullOrWhiteSpace(it)).Take(limit));

        private sealed class DeviceHealthBatchResult
        {
            public string RawResponse { get; set; }

            public List<DeviceHealthLlmItem> Items { get; set; } = new List<DeviceHealthLlmItem>();
        }

        private sealed class DeviceHealthLlmItem
        {
            public string VarName { get; set; }

            public string AnchorType { get; set; }

            public string SemanticCategory { get; set; }

            public string SemanticLabel { get; set; }

            public string MonitoringRole { get; set; }

            public string ActivationRule { get; set; }

            public string AbnormalWhen { get; set; }

            public int RootPriority { get; set; }

            public decimal Confidence { get; set; }

            public string Reason { get; set; }

            public List<string> ContextVariables { get; set; } = new List<string>();
        }
    }

    public class DeviceHealthSemanticEnrichmentResult
    {
        public string ClassifierVersion { get; set; }

        public int CacheHitCount { get; set; }

        public int SentCount { get; set; }

        public int ReturnedCount { get; set; }

        public int PersistedCount { get; set; }

        public int LimitSkippedCount { get; set; }

        public long ElapsedMs { get; set; }

        public string ErrorMessage { get; set; }
    }
}
