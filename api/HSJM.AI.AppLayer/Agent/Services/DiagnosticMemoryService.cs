using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// AI诊断长期记忆服务
    /// 当前版本仅使用数据库表查询，不再依赖向量检索或 Embedding 接口。
    /// </summary>
    public class DiagnosticMemoryService : ISelfScopedAutoInject
    {
        public const string SceneConstraint = "constraint";
        public const string SceneStuckPoint = "stuck_point";
        public const string SceneEnergyFlow = "energy_flow";

        private const string DiagnosticMemoryTableName = "AI_IOConstraintMemory";
        private const int DefaultTopK = 5;

        private readonly IDiagnosticMemoryService _memoryService;
        private readonly IDiagnosticCorrectionLogService _correctionLogService;
        private readonly ILogger<DiagnosticMemoryService> _logger;
        private bool? _memoryTableExists;

        public DiagnosticMemoryService(
            IDiagnosticMemoryService memoryService,
            IDiagnosticCorrectionLogService correctionLogService,
            ILLM_ModelService llmModelService,
            IHttpClientFactory httpClientFactory,
            ILogger<DiagnosticMemoryService> logger)
        {
            _memoryService = memoryService;
            _correctionLogService = correctionLogService;
            _logger = logger;
        }

        /// <summary>
        /// 检索相关记忆
        /// </summary>
        public async Task<List<MemoryRetrievalResult>> SearchMemoriesAsync(
            string sceneType,
            long? productionLineId,
            string context,
            int topK = DefaultTopK)
        {
            try
            {
                if (!await HasDiagnosticMemoryTableAsync())
                {
                    return new List<MemoryRetrievalResult>();
                }

                var memories = await SearchWithoutVectorAsync(sceneType, productionLineId, context, topK);
                return memories.Take(topK).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DiagnosticMemory] 记忆检索失败");
                return new List<MemoryRetrievalResult>();
            }
        }

        private async Task<List<MemoryRetrievalResult>> SearchWithoutVectorAsync(
            string sceneType,
            long? productionLineId,
            string context,
            int topK)
        {
            var db = _memoryService.GetDb().Context;
            var keywords = string.IsNullOrWhiteSpace(context)
                ? Array.Empty<string>()
                : context.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var query = db.Queryable<DiagnosticMemoryEntity>()
                .Where(m => m.IsActive == true && m.IsVerified == true && m.SceneType == sceneType)
                .Where(m => productionLineId == null || m.ProductionLineId == productionLineId);

            if (keywords.Length > 0)
            {
                foreach (var keyword in keywords)
                {
                    query = query.Where(m =>
                        m.Content.Contains(keyword) ||
                        (m.PatternTags != null && m.PatternTags.Contains(keyword)));
                }
            }

            return await query
                .OrderByDescending(m => m.ImportanceScore)
                .Take(topK)
                .Select(m => new MemoryRetrievalResult
                {
                    Id = m.Id,
                    Content = m.Content,
                    MemoryType = m.MemoryType,
                    LayerLevel = m.LayerLevel,
                    SignalType = m.SignalType,
                    PatternTags = m.PatternTags,
                    ImportanceScore = m.ImportanceScore,
                    CreatedAt = m.CreateTime,
                    Score = m.ImportanceScore / 10.0
                })
                .ToListAsync();
        }

        /// <summary>
        /// 保存新记忆
        /// </summary>
        public async Task SaveMemoryAsync(DiagnosticMemoryEntity memory)
        {
            try
            {
                if (!await HasDiagnosticMemoryTableAsync())
                {
                    _logger.LogInformation("[DiagnosticMemory] 记忆表不存在，跳过保存。Scene={Scene}", memory?.SceneType);
                    return;
                }

                memory.Embedding = null;
                await _memoryService.InsertAsync(memory);

                _logger.LogInformation(
                    "[DiagnosticMemory] 保存记忆成功: Scene={Scene}, LayerLevel={LayerLevel}",
                    memory.SceneType,
                    memory.LayerLevel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DiagnosticMemory] 保存记忆失败");
                throw;
            }
        }

        /// <summary>
        /// 获取所有激活记忆
        /// </summary>
        public async Task<List<DiagnosticMemoryEntity>> GetActiveMemoriesAsync(string sceneType, long? productionLineId)
        {
            if (!await HasDiagnosticMemoryTableAsync())
            {
                return new List<DiagnosticMemoryEntity>();
            }

            var db = _memoryService.GetDb().Context;
            return await db.Queryable<DiagnosticMemoryEntity>()
                .Where(m => m.IsActive == true && m.IsVerified == true && m.SceneType == sceneType)
                .Where(m => productionLineId == null || m.ProductionLineId == productionLineId)
                .OrderByDescending(m => m.ImportanceScore)
                .ToListAsync();
        }

        /// <summary>
        /// 删除记忆（软删除）
        /// </summary>
        public async Task DeleteMemoryAsync(long memoryId)
        {
            if (!await HasDiagnosticMemoryTableAsync())
            {
                return;
            }

            var memory = await _memoryService.GetByIdAsync(memoryId);
            if (memory != null)
            {
                memory.IsActive = false;
                memory.ModifyTime = DateTime.Now;
                await _memoryService.UpdateAsync(memory);
            }
        }

        /// <summary>
        /// 构建记忆上下文文本
        /// </summary>
        public string BuildMemoryContextPrompt(string sceneType, List<MemoryRetrievalResult> memories)
        {
            if (memories == null || memories.Count == 0)
            {
                return string.Empty;
            }

            var sceneName = sceneType switch
            {
                SceneConstraint => "约束生成",
                SceneStuckPoint => "卡滞诊断",
                SceneEnergyFlow => "能量流分析",
                _ => sceneType
            };

            var sb = new StringBuilder();
            sb.AppendLine($"\n=== {sceneName}相关经验 ===");

            for (var i = 0; i < memories.Count; i++)
            {
                var memory = memories[i];
                var tags = string.IsNullOrEmpty(memory.PatternTags) ? string.Empty : $"[{memory.PatternTags}] ";
                sb.AppendLine($"#{i + 1} {tags}{memory.Content}");
            }

            sb.AppendLine("=======================\n");
            return sb.ToString();
        }

        private async Task<bool> HasDiagnosticMemoryTableAsync()
        {
            if (_memoryTableExists.HasValue)
            {
                return _memoryTableExists.Value;
            }

            try
            {
                var db = _memoryService.GetDb().Context;
                if (db.CurrentConnectionConfig.DbType != SqlSugar.DbType.PostgreSQL)
                {
                    _memoryTableExists = true;
                    return true;
                }

                var exists = await db.Ado.SqlQuerySingleAsync<int>(
                    @"SELECT CASE WHEN EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = current_schema()
                          AND lower(table_name) = lower(@tableName)
                    ) THEN 1 ELSE 0 END",
                    new SugarParameter("@tableName", DiagnosticMemoryTableName));

                _memoryTableExists = exists == 1;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DiagnosticMemory] 检查记忆表是否存在时失败，默认按不存在处理");
                _memoryTableExists = false;
            }

            if (_memoryTableExists == false)
            {
                _logger.LogWarning("[DiagnosticMemory] 记忆表 {TableName} 不存在，已跳过记忆检索/存储。", DiagnosticMemoryTableName);
            }

            return _memoryTableExists.Value;
        }
    }

    /// <summary>
    /// 记忆检索结果
    /// </summary>
    public class MemoryRetrievalResult
    {
        public long Id { get; set; }
        public string Content { get; set; }
        public string MemoryType { get; set; }
        public int LayerLevel { get; set; }
        public string SignalType { get; set; }
        public string PatternTags { get; set; }
        public float ImportanceScore { get; set; }
        public DateTime? CreatedAt { get; set; }
        public double Score { get; set; }
    }
}
