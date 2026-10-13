using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// PLC 项目知识记忆表
    /// 存储项目级、工位级、约束级的知识记忆，供约束生成和诊断分析时加载为 LLM 上下文。
    /// 
    /// 记忆类型：
    /// - Project: 项目级（设备清单、工位拓扑、信号命名规范）
    /// - Diagnosis: 诊断级（历史诊断结论、常见故障模式、误判纠正）
    /// - Constraint: 约束级（已验证约束、被否决约束及原因）
    /// - StationProfile: 工位画像（该工位的设备特征、常见问题）
    /// </summary>
    [SugarTable("AI_PLC_KnowledgeMemory", tableDescription: "PLC项目知识记忆表")]
    public class PLC_KnowledgeMemoryEntity : BaseField
    {
        public PLC_KnowledgeMemoryEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 关联的项目ID
        /// </summary>
        [SugarColumn(IsNullable = false,ColumnName ="project_id", ColumnDescription = "关联的项目ID")]
        public long ProjectId { get; set; }

        /// <summary>
        /// 记忆类型：Project / Diagnosis / Constraint / StationProfile
        /// </summary>
        [SugarColumn(IsNullable = false, ColumnName = "memory_type", Length = 30, ColumnDescription = "记忆类型")]
        public string MemoryType { get; set; } = "Project";

        /// <summary>
        /// 关联的工位编码（工位级记忆必填，项目级可为空）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnName = "station_code", Length = 50, ColumnDescription = "关联的工位编码")]
        public string StationCode { get; set; }

        /// <summary>
        /// 记忆标题（便于检索）
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 200, ColumnDescription = "记忆标题")]
        public string Title { get; set; }

        /// <summary>
        /// 记忆内容（Markdown 格式）
        /// </summary>
        [SugarColumn(IsNullable = false, ColumnDataType = "text", ColumnDescription = "记忆内容（Markdown格式）")]
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 来源：AutoDiagnosis（自动诊断生成）/ AutoConstraint（约束分析生成）/ Manual（人工录入）/ Correction（误判纠正）
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 30, ColumnDescription = "来源")]
        public string Source { get; set; }

        /// <summary>
        /// 重要性得分（0~1），用于上下文裁剪时排序
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnName = "importance_score", ColumnDescription = "重要性得分")]
        public float? ImportanceScore { get; set; }

        /// <summary>
        /// 是否已被人工确认/验证
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnName = "is_verified", DefaultValue = "false", ColumnDescription = "是否已人工确认")]
        public bool IsVerified { get; set; }

        /// <summary>
        /// 引用次数（被加载到 LLM 上下文的次数）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnName = "reference_count", DefaultValue = "0", ColumnDescription = "引用次数")]
        public int ReferenceCount { get; set; }

        /// <summary>
        /// 最后引用时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnName = "last_referenced_at", ColumnDescription = "最后引用时间")]
        public DateTime? LastReferencedAt { get; set; }
    }
}
