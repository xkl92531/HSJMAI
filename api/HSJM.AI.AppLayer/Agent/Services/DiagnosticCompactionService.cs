//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V1  描述:V1_多场景诊断记忆压缩服务  添加时间:2026/4/1
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// AI诊断记忆压缩服务（多场景版）
    /// 支持：constraint=约束生成，stuck_point=卡滞诊断，energy_flow=能量流分析
    /// 负责：每月压缩、AI自检对比、回退
    /// </summary>
    public class DiagnosticCompactionService : ISelfScopedAutoInject
    {
        private readonly IDiagnosticMemoryService _memoryService;
        private readonly IDiagnosticCorrectionLogService _correctionLogService;
        private readonly IDiagnosticSnapshotService _snapshotService;
        private readonly IAiModelPurposeResolver _purposeResolver;
        private readonly ILogger<DiagnosticCompactionService> _logger;

        private const string SnapshotDirectory = "wwwroot/snapshots/diagnostic";
        private const int MaxMemoriesBeforeCompaction = 20;
        private const int AI_CALL_TIMEOUT_SECONDS = 120;

        public DiagnosticCompactionService(
            IDiagnosticMemoryService memoryService,
            IDiagnosticCorrectionLogService correctionLogService,
            IDiagnosticSnapshotService snapshotService,
            IAiModelPurposeResolver purposeResolver,
            ILogger<DiagnosticCompactionService> logger)
        {
            _memoryService = memoryService;
            _correctionLogService = correctionLogService;
            _snapshotService = snapshotService;
            _purposeResolver = purposeResolver;
            _logger = logger;
        }

        #region 压缩执行

        /// <summary>
        /// 执行月度压缩（指定产线或全局）
        /// </summary>
        public async Task<CompactionResult> ExecuteMonthlyCompactionAsync(string sceneType, long? productionLineId)
        {
            var result = new CompactionResult { StartTime = DateTime.Now };

            try
            {
                _logger.LogInformation("[Diagnostic压缩] 开始执行: Scene={Scene}, ProductionLineId={ProductionLineId}",
                    sceneType, productionLineId);

                var memories = await GetActiveMemoriesAsync(sceneType, productionLineId);
                result.MemoriesBeforeCount = memories.Count;

                if (memories.Count == 0)
                {
                    result.Skipped = true;
                    result.SkippedReason = "无记忆可压缩";
                    return result;
                }

                if (memories.Count <= MaxMemoriesBeforeCompaction)
                {
                    result.Skipped = true;
                    result.SkippedReason = $"记忆数量({memories.Count})未超过阈值({MaxMemoriesBeforeCompaction})";
                    return result;
                }

                var snapshotId = await CreateSnapshotAsync(sceneType, productionLineId, memories, result);
                if (snapshotId == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "快照创建失败";
                    return result;
                }

                var l0Memories = memories.Where(m => m.LayerLevel == 0).ToList();
                var l1Memories = memories.Where(m => m.LayerLevel == 1).ToList();

                if (l1Memories.Count > MaxMemoriesBeforeCompaction - l0Memories.Count)
                {
                    var aiSummary = await SummarizeMemoriesWithAIAsync(sceneType, l1Memories);
                    if (!string.IsNullOrEmpty(aiSummary))
                    {
                        await CreateCompressedMemoryAsync(sceneType, productionLineId, aiSummary, l1Memories);
                        foreach (var memory in l1Memories)
                        {
                            memory.IsActive = false;
                            memory.ModifyTime = DateTime.Now;
                            await _memoryService.UpdateAsync(memory);
                        }
                    }
                }

                var afterMemories = await GetActiveMemoriesAsync(sceneType, productionLineId);
                result.MemoriesAfterCount = afterMemories.Count;
                result.Success = true;
                result.SnapshotPath = GetSnapshotPath(sceneType, productionLineId);

                _logger.LogInformation("[Diagnostic压缩] 完成: Scene={Scene}, Before={Before}, After={After}",
                    sceneType, result.MemoriesBeforeCount, result.MemoriesAfterCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnostic压缩] 执行失败");
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            result.EndTime = DateTime.Now;
            return result;
        }

        /// <summary>
        /// 执行全局压缩（所有场景）
        /// </summary>
        public async Task<CompactionResult> ExecuteGlobalCompactionAsync()
        {
            return await ExecuteMonthlyCompactionAsync(null, null);
        }

        /// <summary>
        /// 执行指定场景的压缩
        /// </summary>
        public async Task<CompactionResult> ExecuteCompactionBySceneAsync(string sceneType)
        {
            return await ExecuteMonthlyCompactionAsync(sceneType, null);
        }

        #endregion

        #region 快照管理

        /// <summary>
        /// 获取压缩历史
        /// </summary>
        public async Task<List<DiagnosticSnapshotEntity>> GetCompactionHistoryAsync(string sceneType, int limit = 10)
        {
            var db = _snapshotService.GetDb().Context;
            var query = db.Queryable<DiagnosticSnapshotEntity>()
                .Where(s => s.IsDeleted == false);

            if (!string.IsNullOrEmpty(sceneType))
            {
                query = query.Where(s => s.SceneType == sceneType);
            }

            return await query
                .OrderByDescending(s => s.SnapshotDate)
                .Take(limit)
                .ToListAsync();
        }

        /// <summary>
        /// 回退到指定快照
        /// </summary>
        public async Task<bool> RollbackAsync(long snapshotId)
        {
            try
            {
                var snapshot = await _snapshotService.GetByIdAsync(snapshotId);
                if (snapshot == null)
                {
                    _logger.LogWarning("[Diagnostic压缩] 快照不存在: {SnapshotId}", snapshotId);
                    return false;
                }

                if (string.IsNullOrEmpty(snapshot.SnapshotPath) || !File.Exists(snapshot.SnapshotPath))
                {
                    _logger.LogWarning("[Diagnostic压缩] 快照文件不存在: {Path}", snapshot.SnapshotPath);
                    return false;
                }

                var json = await File.ReadAllTextAsync(snapshot.SnapshotPath);
                var memories = JsonSerializer.Deserialize<List<DiagnosticMemoryEntity>>(json);

                if (memories == null || memories.Count == 0)
                {
                    _logger.LogWarning("[Diagnostic压缩] 快照数据为空");
                    return false;
                }

                foreach (var memory in memories)
                {
                    var existing = await _memoryService.GetByIdAsync(memory.Id);
                    if (existing != null)
                    {
                        existing.IsActive = memory.IsActive;
                        existing.Content = memory.Content;
                        existing.LayerLevel = memory.LayerLevel;
                        existing.ImportanceScore = memory.ImportanceScore;
                        existing.ModifyTime = DateTime.Now;
                        await _memoryService.UpdateAsync(existing);
                    }
                    else
                    {
                        memory.CreateTime = DateTime.Now;
                        memory.ModifyTime = DateTime.Now;
                        await _memoryService.InsertAsync(memory);
                    }
                }

                snapshot.IsDeleted = true;
                snapshot.ModifyTime = DateTime.Now;
                await _snapshotService.UpdateAsync(snapshot);

                _logger.LogInformation("[Diagnostic压缩] 回退成功: SnapshotId={SnapshotId}, Count={Count}",
                    snapshotId, memories.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnostic压缩] 回退失败: SnapshotId={SnapshotId}", snapshotId);
                return false;
            }
        }

        /// <summary>
        /// 清理过期快照（保留最近3个月）
        /// </summary>
        public async Task CleanupOldSnapshotsAsync()
        {
            _logger.LogInformation("[Diagnostic压缩] 开始清理过期快照");

            try
            {
                var cutoffDate = DateTime.Now.AddMonths(-3);
                var db = _snapshotService.GetDb().Context;

                var oldSnapshots = await db.Queryable<DiagnosticSnapshotEntity>()
                    .Where(s => s.IsDeleted == false && s.SnapshotDate < cutoffDate)
                    .ToListAsync();

                foreach (var snapshot in oldSnapshots)
                {
                    snapshot.IsDeleted = true;
                    snapshot.ModifyTime = DateTime.Now;
                    await _snapshotService.UpdateAsync(snapshot);

                    if (!string.IsNullOrEmpty(snapshot.SnapshotPath))
                    {
                        try
                        {
                            if (File.Exists(snapshot.SnapshotPath))
                            {
                                File.Delete(snapshot.SnapshotPath);
                            }
                        }
                        catch { }
                    }
                }

                _logger.LogInformation("[Diagnostic压缩] 清理过期快照完成: Count={Count}", oldSnapshots.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnostic压缩] 清理过期快照异常");
            }
        }

        #endregion

        #region 私有方法

        private async Task<List<DiagnosticMemoryEntity>> GetActiveMemoriesAsync(string sceneType, long? productionLineId)
        {
            var db = _memoryService.GetDb().Context;
            var query = db.Queryable<DiagnosticMemoryEntity>()
                .Where(m => m.IsActive == true && m.IsVerified == true);

            if (!string.IsNullOrEmpty(sceneType))
            {
                query = query.Where(m => m.SceneType == sceneType);
            }

            if (productionLineId.HasValue)
            {
                query = query.Where(m => m.ProductionLineId == productionLineId.Value);
            }

            return await query.OrderByDescending(m => m.ImportanceScore).ToListAsync();
        }

        private async Task<long?> CreateSnapshotAsync(string sceneType, long? productionLineId,
            List<DiagnosticMemoryEntity> memories, CompactionResult result)
        {
            try
            {
                Directory.CreateDirectory(SnapshotDirectory);
                var sceneDir = string.IsNullOrEmpty(sceneType) ? "global" : sceneType;
                var dateStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"{sceneDir}_{productionLineId?.ToString() ?? "all"}_{dateStr}.json";
                var filePath = Path.Combine(SnapshotDirectory, fileName);

                var json = JsonSerializer.Serialize(memories, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(filePath, json);

                var snapshot = new DiagnosticSnapshotEntity
                {
                    SceneType = sceneType ?? "global",
                    ProductionLineId = productionLineId,
                    SnapshotDate = DateTime.Now,
                    SnapshotPath = filePath,
                    L0Count = memories.Count(m => m.LayerLevel == 0),
                    L1Count = memories.Count(m => m.LayerLevel == 1),
                    L2Count = 0,
                    VerificationStatus = "pending"
                };

                await _snapshotService.InsertAsync(snapshot);
                return snapshot.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Diagnostic压缩] 创建快照失败");
                result.ErrorMessage = ex.Message;
                return null;
            }
        }

        private string GetSnapshotPath(string sceneType, long? productionLineId)
        {
            var sceneDir = string.IsNullOrEmpty(sceneType) ? "global" : sceneType;
            var dateStr = DateTime.Now.ToString("yyyyMMdd");
            return Path.Combine(SnapshotDirectory, $"{sceneDir}_{productionLineId?.ToString() ?? "all"}_{dateStr}.json");
        }

        private async Task<string> SummarizeMemoriesWithAIAsync(string sceneType, List<DiagnosticMemoryEntity> memories)
        {
            try
            {
                if (memories.Count == 0) return null;

                var contents = string.Join("\n---\n", memories.Select(m => m.Content));
                var prompt = $"请将以下{sceneType}场景的记忆片段归纳为1-2条简洁的核心规则：\n\n{contents}";

                var model = await SelectChatModelAsync();
                var response = await CallAIAsync(prompt, model.baseUrl, model.apiKey, model.modelCode);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Diagnostic压缩] AI归纳失败");
                return null;
            }
        }

        private async Task CreateCompressedMemoryAsync(string sceneType, long? productionLineId,
            string aiSummary, List<DiagnosticMemoryEntity> originalMemories)
        {
            var compressedMemory = new DiagnosticMemoryEntity
            {
                SceneType = sceneType ?? "unknown",
                ProductionLineId = productionLineId,
                LayerLevel = 1,
                MemoryType = "pattern",
                Content = aiSummary,
                Source = "ai_extract",
                ImportanceScore = originalMemories.Average(m => m.ImportanceScore),
                IsVerified = true,
                IsActive = true
            };

            await _memoryService.InsertAsync(compressedMemory);
            _logger.LogInformation("[Diagnostic压缩] 创建压缩记忆: Id={Id}", compressedMemory.Id);
        }

        private async Task<(string baseUrl, string apiKey, string modelCode)> SelectChatModelAsync()
        {
            var selected = (await _purposeResolver.ResolveAsync(AiModelPurposes.MemoryChat)).Model;
            if (!Uri.TryCreate(selected.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(selected.ModelCode))
                throw new InvalidOperationException($"AI 用途 {AiModelPurposes.MemoryChat} 的模型 {selected.Id} 缺少有效地址或编码");
            return (selected.BaseUrl, selected.ApiKey ?? string.Empty, selected.ModelCode);
        }

        private async Task<string> CallAIAsync(string prompt, string baseUrl, string apiKey, string modelCode)
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(AI_CALL_TIMEOUT_SECONDS);

            var payload = new
            {
                model = modelCode,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.3
            };

            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                $"{baseUrl.TrimEnd('/')}/chat/completions");
            request.Content = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                return choices[0].GetProperty("message").GetProperty("content").GetString();
            }

            return null;
        }

        #endregion
    }

    /// <summary>
    /// 压缩结果
    /// </summary>
    public class CompactionResult
    {
        public bool Success { get; set; }
        public bool Skipped { get; set; }
        public string SkippedReason { get; set; }
        public string SnapshotPath { get; set; }
        public int MemoriesBeforeCount { get; set; }
        public int MemoriesAfterCount { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string ErrorMessage { get; set; }
    }
}
