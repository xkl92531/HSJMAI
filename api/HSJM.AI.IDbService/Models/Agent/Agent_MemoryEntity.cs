using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 智能体记忆表
    /// </summary>
    [SugarTable("agent_memories", tableDescription: "智能体记忆表")]
    public class Agent_MemoryEntity : BaseField
    {
        public Agent_MemoryEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
            CreatedAt = DateTime.Now;
            UpdatedAt = DateTime.Now;
            IsActive = true;
        }

        /// <summary>
        /// 主键ID
        /// </summary>
        [SugarColumn(ColumnName = "id", IsPrimaryKey = true, ColumnDescription = "主键ID")]
        public long Id { get; set; }

        /// <summary>
        /// 系统标识（支持多系统隔离）
        /// </summary>
        [SugarColumn(ColumnName = "system_id", Length = 64, DefaultValue = "default", ColumnDescription = "系统标识")]
        public string SystemId { get; set; }

        /// <summary>
        /// 用户标识
        /// </summary>
        [SugarColumn(ColumnName = "user_id", Length = 64, IsNullable = false, ColumnDescription = "用户标识")]
        public string UserId { get; set; }

        /// <summary>
        /// 智能体标识
        /// </summary>
        [SugarColumn(ColumnName = "agent_id", Length = 64, IsNullable = true, ColumnDescription = "智能体标识")]
        public string AgentId { get; set; }

        /// <summary>
        /// 记忆类型（SHORT_TERM/LONG_TERM/preference/fact/event）
        /// </summary>
        [SugarColumn(ColumnName = "memory_type", Length = 20, DefaultValue = "LONG_TERM", ColumnDescription = "记忆类型")]
        public string MemoryType { get; set; }

        /// <summary>
        /// 记忆内容
        /// </summary>
        [SugarColumn(ColumnName = "content", ColumnDataType = "text", IsNullable = false, ColumnDescription = "记忆内容")]
        public string Content { get; set; }

        /// <summary>
        /// 向量表示（pgvector）
        /// </summary>
        [SugarColumn(ColumnName = "embedding", ColumnDataType = "vector(1536)", IsNullable = true, ColumnDescription = "向量表示")]
        public string Embedding { get; set; }

        /// <summary>
        /// 是否高质量记忆
        /// </summary>
        [SugarColumn(ColumnName = "is_high_quality", DefaultValue = "false", ColumnDescription = "是否高质量记忆")]
        public bool IsHighQuality { get; set; }

        /// <summary>
        /// 重要性得分
        /// </summary>
        [SugarColumn(ColumnName = "importance_score", DefaultValue = "0", ColumnDescription = "重要性得分")]
        public float ImportanceScore { get; set; }

        /// <summary>
        /// 来源会话ID
        /// </summary>
        [SugarColumn(ColumnName = "source_session_id", Length = 64, IsNullable = true, ColumnDescription = "来源会话ID")]
        public string SourceSessionId { get; set; }

        /// <summary>
        /// 记忆链路ID（同主题版本链）
        /// </summary>
        [SugarColumn(ColumnName = "lineage_id", Length = 64, IsNullable = true, ColumnDescription = "记忆链路ID")]
        public string LineageId { get; set; }

        /// <summary>
        /// 当前记录替代的旧记忆ID
        /// </summary>
        [SugarColumn(ColumnName = "supersedes_memory_id", IsNullable = true, ColumnDescription = "替代的旧记忆ID")]
        public long? SupersedesMemoryId { get; set; }

        /// <summary>
        /// 冲突/主题键，用于同主题版本识别
        /// </summary>
        [SugarColumn(ColumnName = "conflict_key", Length = 128, IsNullable = true, ColumnDescription = "冲突主题键")]
        public string ConflictKey { get; set; }

        /// <summary>
        /// 记忆状态：active / superseded / archived / conflict
        /// </summary>
        [SugarColumn(ColumnName = "memory_status", Length = 20, DefaultValue = "active", ColumnDescription = "记忆状态")]
        public string MemoryStatus { get; set; } = "active";

        /// <summary>
        /// 生效时间
        /// </summary>
        [SugarColumn(ColumnName = "valid_from", IsNullable = true, ColumnDescription = "生效时间")]
        public DateTime? ValidFrom { get; set; }

        /// <summary>
        /// 失效时间
        /// </summary>
        [SugarColumn(ColumnName = "valid_to", IsNullable = true, ColumnDescription = "失效时间")]
        public DateTime? ValidTo { get; set; }

        /// <summary>
        /// 是否有效
        /// </summary>
        [SugarColumn(ColumnName = "is_active", DefaultValue = "true", ColumnDescription = "是否有效")]
        public bool IsActive { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        [SugarColumn(ColumnName = "created_at", ColumnDescription = "创建时间")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        [SugarColumn(ColumnName = "updated_at", ColumnDescription = "更新时间")]
        public DateTime UpdatedAt { get; set; }
    }
}
