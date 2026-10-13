using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 记忆图谱实体节点表
    /// </summary>
    [SugarTable("agent_memory_entities", tableDescription: "记忆图谱实体节点表")]
    public class Agent_MemoryNodeEntity : BaseField
    {
        public Agent_MemoryNodeEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
            CreatedAt = DateTime.Now;
            UpdatedAt = DateTime.Now;
            IsActive = true;
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true, ColumnDescription = "主键ID")]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "system_id", Length = 64, DefaultValue = "default", ColumnDescription = "系统标识")]
        public string SystemId { get; set; } = "default";

        [SugarColumn(ColumnName = "user_id", Length = 64, IsNullable = false, ColumnDescription = "用户标识")]
        public string UserId { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "memory_id", IsNullable = false, ColumnDescription = "所属记忆ID")]
        public long MemoryId { get; set; }

        [SugarColumn(ColumnName = "entity_name", Length = 128, IsNullable = false, ColumnDescription = "实体名称")]
        public string EntityName { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "entity_type", Length = 50, DefaultValue = "concept", ColumnDescription = "实体类型")]
        public string EntityType { get; set; } = "concept";

        [SugarColumn(ColumnName = "normalized_name", Length = 128, IsNullable = true, ColumnDescription = "归一化名称")]
        public string NormalizedName { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "confidence_score", DefaultValue = "0.75", ColumnDescription = "置信度")]
        public float ConfidenceScore { get; set; }

        [SugarColumn(ColumnName = "is_active", DefaultValue = "true", ColumnDescription = "是否有效")]
        public bool IsActive { get; set; }

        [SugarColumn(ColumnName = "created_at", ColumnDescription = "创建时间")]
        public DateTime CreatedAt { get; set; }

        [SugarColumn(ColumnName = "updated_at", ColumnDescription = "更新时间")]
        public DateTime UpdatedAt { get; set; }
    }
}
