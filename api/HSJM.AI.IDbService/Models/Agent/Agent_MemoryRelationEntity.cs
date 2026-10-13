using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 记忆图谱关系边表
    /// </summary>
    [SugarTable("agent_memory_relations", tableDescription: "记忆图谱关系边表")]
    public class Agent_MemoryRelationEntity : BaseField
    {
        public Agent_MemoryRelationEntity()
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

        [SugarColumn(ColumnName = "subject_entity", Length = 128, IsNullable = false, ColumnDescription = "主语实体")]
        public string SubjectEntity { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "subject_type", Length = 50, IsNullable = true, ColumnDescription = "主语类型")]
        public string SubjectType { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "relation_type", Length = 64, IsNullable = false, ColumnDescription = "关系类型")]
        public string RelationType { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "object_entity", Length = 256, IsNullable = false, ColumnDescription = "宾语实体")]
        public string ObjectEntity { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "object_type", Length = 50, IsNullable = true, ColumnDescription = "宾语类型")]
        public string ObjectType { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "normalized_key", Length = 256, IsNullable = true, ColumnDescription = "归一化关系键")]
        public string NormalizedKey { get; set; } = string.Empty;

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
