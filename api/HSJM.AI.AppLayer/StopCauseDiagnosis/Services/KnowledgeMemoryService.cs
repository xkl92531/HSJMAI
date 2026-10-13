using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// PLC 项目知识记忆库管理服务。
    /// 
    /// 职责：
    /// 1. 记忆加载：根据场景（诊断/约束生成）加载相关记忆作为 LLM 上下文
    /// 2. 记忆写入：诊断完成/约束确认后自动追加知识
    /// 3. 记忆裁剪：根据 token 预算动态选择最相关的记忆
    /// 4. 记忆维护：去重、过期清理、引用计数更新
    /// </summary>
    public class KnowledgeMemoryService : ISelfScopedAutoInject
    {
        private readonly ILogger<KnowledgeMemoryService> _logger;
        private readonly IPLC_KnowledgeMemoryService _memoryDbService;

        /// <summary>默认最大上下文字符数（约 8K token）</summary>
        private const int DefaultMaxContextChars = 16000;

        public KnowledgeMemoryService(
            ILogger<KnowledgeMemoryService> logger,
            IPLC_KnowledgeMemoryService memoryDbService)
        {
            _logger = logger;
            _memoryDbService = memoryDbService;
        }

        #region 记忆加载

        /// <summary>
        /// 加载诊断相关记忆（供全线诊断 LLM 使用）。
        /// 按优先级加载：该工位历史诊断 → 项目级知识 → 约束参考。
        /// </summary>
        public async Task<string> LoadDiagnosisContextAsync(
            long projectId,
            List<string> stationCodes = null,
            int maxChars = DefaultMaxContextChars)
        {
            // 只加载人工确认过的记忆，未验证的不参与 LLM 上下文（避免错误结论自我强化）
            var memories = await _memoryDbService.GetListAsync(m =>
                m.ProjectId == projectId
                && !m.IsDeleted
                && m.IsVerified
                && (m.MemoryType == "Diagnosis" || m.MemoryType == "StationProfile" || m.MemoryType == "Project"));

            if (stationCodes != null && stationCodes.Count > 0)
            {
                memories = memories
                    .OrderByDescending(m => stationCodes.Contains(m.StationCode) ? 1 : 0)
                    .ThenByDescending(m => m.ImportanceScore ?? 0)
                    .ThenByDescending(m => m.CreateTime)
                    .ToList();
            }
            else
            {
                memories = memories
                    .OrderByDescending(m => m.ImportanceScore ?? 0)
                    .ThenByDescending(m => m.CreateTime)
                    .ToList();
            }

            return BuildContextMarkdown(memories, maxChars, "诊断参考知识");
        }

        /// <summary>
        /// 加载约束生成相关记忆（供约束推理 LLM 使用）。
        /// 包含已验证约束模式、被否决约束及原因。
        /// </summary>
        public async Task<string> LoadConstraintContextAsync(
            long projectId,
            string equipmentId = null,
            int maxChars = DefaultMaxContextChars)
        {
            // 只加载人工确认过的记忆
            var memories = await _memoryDbService.GetListAsync(m =>
                m.ProjectId == projectId
                && !m.IsDeleted
                && m.IsVerified
                && (m.MemoryType == "Constraint" || m.MemoryType == "Project"));

            if (!string.IsNullOrEmpty(equipmentId))
            {
                memories = memories
                    .OrderByDescending(m => m.Content != null && m.Content.Contains(equipmentId) ? 1 : 0)
                    .ThenByDescending(m => m.ImportanceScore ?? 0)
                    .ThenByDescending(m => m.CreateTime)
                    .ToList();
            }
            else
            {
                memories = memories
                    .OrderByDescending(m => m.ImportanceScore ?? 0)
                    .ThenByDescending(m => m.CreateTime)
                    .ToList();
            }

            return BuildContextMarkdown(memories, maxChars, "约束参考知识");
        }

        /// <summary>
        /// 加载指定工位的记忆（工位画像 + 历史诊断 + 约束）。
        /// </summary>
        public async Task<string> LoadStationContextAsync(
            long projectId,
            string stationCode,
            int maxChars = DefaultMaxContextChars)
        {
            // 只加载人工确认过的记忆
            var memories = await _memoryDbService.GetListAsync(m =>
                m.ProjectId == projectId
                && !m.IsDeleted
                && m.IsVerified
                && (m.StationCode == stationCode || m.MemoryType == "Project"));

            memories = memories
                .OrderByDescending(m => m.StationCode == stationCode ? 1 : 0)
                .ThenByDescending(m => m.ImportanceScore ?? 0)
                .ThenByDescending(m => m.CreateTime)
                .ToList();

            return BuildContextMarkdown(memories, maxChars, $"工位 {stationCode} 参考知识");
        }

        #endregion

        #region 记忆写入

        /// <summary>
        /// 记录一次诊断结论。
        /// </summary>
        public async Task SaveDiagnosisMemoryAsync(
            long projectId,
            string stationCode,
            string title,
            string content,
            float importance = 0.5f)
        {
            await SaveMemoryAsync(projectId, "Diagnosis", stationCode, title, content, "AutoDiagnosis", importance);
        }

        /// <summary>
        /// 记录约束知识（已验证/被否决）。
        /// </summary>
        public async Task SaveConstraintMemoryAsync(
            long projectId,
            string title,
            string content,
            bool isVerified = false,
            float importance = 0.6f)
        {
            await SaveMemoryAsync(projectId, "Constraint", null, title, content,
                isVerified ? "Manual" : "AutoConstraint", importance, isVerified);
        }

        /// <summary>
        /// 记录工位画像。
        /// </summary>
        public async Task SaveStationProfileAsync(
            long projectId,
            string stationCode,
            string content,
            float importance = 0.7f)
        {
            await SaveMemoryAsync(projectId, "StationProfile", stationCode,
                $"{stationCode} 工位画像", content, "AutoDiagnosis", importance);
        }

        /// <summary>
        /// 记录误判纠正。
        /// </summary>
        public async Task SaveCorrectionMemoryAsync(
            long projectId,
            string stationCode,
            string wrongConclusion,
            string correctConclusion)
        {
            var content = $"### 误判纠正\n- **错误结论**: {wrongConclusion}\n- **正确结论**: {correctConclusion}\n- **纠正时间**: {DateTime.Now:yyyy-MM-dd HH:mm}";
            await SaveMemoryAsync(projectId, "Diagnosis", stationCode,
                $"误判纠正 - {stationCode}", content, "Correction", 0.9f, true);
        }

        /// <summary>
        /// 保存项目级知识。
        /// </summary>
        public async Task SaveProjectKnowledgeAsync(
            long projectId,
            string title,
            string content,
            float importance = 0.8f)
        {
            await SaveMemoryAsync(projectId, "Project", null, title, content, "Manual", importance, true);
        }

        #endregion

        #region 记忆查询

        /// <summary>
        /// 查找指定工位在指定时间之后的诊断记忆（用于去重判断）。
        /// </summary>
        public async Task<PLC_KnowledgeMemoryEntity> FindRecentDiagnosisAsync(
            long projectId, string stationCode, DateTime afterUtc)
        {
            var list = await _memoryDbService.GetListAsync(m =>
                m.ProjectId == projectId
                && !m.IsDeleted
                && m.MemoryType == "Diagnosis"
                && m.StationCode == stationCode
                && m.CreateTime >= afterUtc);

            return list?.OrderByDescending(m => m.CreateTime).FirstOrDefault();
        }

        #endregion

        #region 记忆维护

        /// <summary>
        /// 更新引用计数（每次 LLM 使用记忆时调用）。
        /// </summary>
        public async Task IncrementReferenceCountAsync(List<long> memoryIds)
        {
            if (memoryIds == null || memoryIds.Count == 0) return;

            var memories = await _memoryDbService.GetListAsync(m => memoryIds.Contains(m.Id));
            foreach (var m in memories)
            {
                m.ReferenceCount++;
                m.LastReferencedAt = DateTime.Now;
            }
            if (memories.Count > 0)
                await _memoryDbService.UpdateRangeAsync(memories);
        }

        /// <summary>
        /// 清理过期低质量记忆（引用次数为0且超过指定天数的非验证记忆）。
        /// </summary>
        public async Task CleanupStaleMemoriesAsync(long projectId, int staleDays = 90)
        {
            var threshold = DateTime.Now.AddDays(-staleDays);
            var staleMemories = await _memoryDbService.GetListAsync(m =>
                m.ProjectId == projectId
                && !m.IsDeleted
                && !m.IsVerified
                && m.ReferenceCount == 0
                && m.CreateTime < threshold);

            if (staleMemories.Count > 0)
            {
                foreach (var m in staleMemories)
                    m.IsDeleted = true;
                await _memoryDbService.UpdateRangeAsync(staleMemories);
                _logger.LogInformation("[KnowledgeMemory] 清理 {Count} 条过期记忆, ProjectId={ProjectId}",
                    staleMemories.Count, projectId);
            }
        }

        /// <summary>
        /// 去重：合并相同类型+相同工位+标题相似的记忆。
        /// </summary>
        public async Task DeduplicateAsync(long projectId)
        {
            var memories = await _memoryDbService.GetListAsync(m =>
                m.ProjectId == projectId && !m.IsDeleted);

            var groups = memories
                .GroupBy(m => $"{m.MemoryType}|{m.StationCode ?? ""}|{m.Title ?? ""}")
                .Where(g => g.Count() > 1);

            int removedCount = 0;
            foreach (var group in groups)
            {
                // 保留最新的一条（或已验证的），其余软删
                var keeper = group.OrderByDescending(m => m.IsVerified ? 1 : 0)
                    .ThenByDescending(m => m.CreateTime)
                    .First();

                foreach (var dup in group.Where(m => m.Id != keeper.Id))
                {
                    dup.IsDeleted = true;
                    removedCount++;
                }
            }

            if (removedCount > 0)
            {
                await _memoryDbService.UpdateRangeAsync(memories.Where(m => m.IsDeleted).ToList());
                _logger.LogInformation("[KnowledgeMemory] 去重删除 {Count} 条, ProjectId={ProjectId}",
                    removedCount, projectId);
            }
        }

        #endregion

        #region 私有方法

        private async Task SaveMemoryAsync(
            long projectId, string memoryType, string stationCode,
            string title, string content, string source,
            float importance, bool isVerified = false)
        {
            var entity = new PLC_KnowledgeMemoryEntity
            {
                ProjectId = projectId,
                MemoryType = memoryType,
                StationCode = stationCode,
                Title = title,
                Content = content,
                Source = source,
                ImportanceScore = importance,
                IsVerified = isVerified,
            };

            await _memoryDbService.InsertAsync(entity);

            _logger.LogInformation(
                "[KnowledgeMemory] 保存记忆: Type={Type}, Station={Station}, Title={Title}",
                memoryType, stationCode ?? "全局", title);
        }

        /// <summary>
        /// 将记忆列表组装为 Markdown 上下文，按 token 预算裁剪。
        /// </summary>
        private string BuildContextMarkdown(List<PLC_KnowledgeMemoryEntity> memories, int maxChars, string sectionTitle)
        {
            if (memories == null || memories.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine($"## {sectionTitle}");
            sb.AppendLine();

            int currentChars = sb.Length;
            var includedIds = new List<long>();

            foreach (var m in memories)
            {
                var entry = FormatMemoryEntry(m);

                if (currentChars + entry.Length > maxChars)
                    break;

                sb.Append(entry);
                currentChars += entry.Length;
                includedIds.Add(m.Id);
            }

            // 异步更新引用计数（fire-and-forget）
            if (includedIds.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try { await IncrementReferenceCountAsync(includedIds); }
                    catch { /* 引用计数更新失败不影响主流程 */ }
                });
            }

            return sb.ToString();
        }

        private string FormatMemoryEntry(PLC_KnowledgeMemoryEntity m)
        {
            var sb = new StringBuilder();
            var verifiedTag = m.IsVerified ? " ✅已验证" : " ⚠️待验证";
            var stationTag = !string.IsNullOrEmpty(m.StationCode) ? $" [{m.StationCode}]" : "";

            sb.AppendLine($"### {m.Title ?? m.MemoryType}{stationTag}{verifiedTag}");

            if (!string.IsNullOrEmpty(m.Content))
                sb.AppendLine(m.Content);

            sb.AppendLine();
            return sb.ToString();
        }

        #endregion
    }
}
