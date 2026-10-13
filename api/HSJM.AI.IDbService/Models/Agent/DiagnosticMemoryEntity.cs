//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V1  描述:V1_诊断长期记忆(多场景)  添加时间:2026/4/1
using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// AI诊断长期记忆表
    /// 支持多种诊断场景：constraint=约束生成，stuck_point=卡滞诊断，energy_flow=能量流分析
    /// </summary>
    [SugarTable("AI_IOConstraintMemory", tableDescription: "AI诊断长期记忆表")]
    public class DiagnosticMemoryEntity : BaseField
    {
        public DiagnosticMemoryEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        /// <summary>
        /// 主键ID
        /// </summary>
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true, ColumnDescription = "主键ID")]
        public long Id { get; set; }

        /// <summary>
        /// 场景类型：constraint=约束生成，stuck_point=卡滞诊断，energy_flow=能量流分析
        /// </summary>
        [SugarColumn(ColumnName = "SceneType", IsNullable = false, Length = 50, ColumnDescription = "场景类型")]
        public string SceneType { get; set; }

        /// <summary>
        /// 产线ID，NULL表示全局通用
        /// </summary>
        [SugarColumn(ColumnName = "ProductionLineId", IsNullable = true, ColumnDescription = "产线ID")]
        public long? ProductionLineId { get; set; }

        /// <summary>
        /// 记忆层级：0=核心规则，1=案例经验
        /// </summary>
        [SugarColumn(ColumnName = "LayerLevel", IsNullable = false, ColumnDescription = "记忆层级")]
        public int LayerLevel { get; set; }

        /// <summary>
        /// 记忆类型：core_rule=核心规则，pattern=模式经验
        /// </summary>
        [SugarColumn(ColumnName = "MemoryType", IsNullable = false, ColumnDescription = "记忆类型")]
        public string MemoryType { get; set; }

        /// <summary>
        /// 记忆内容
        /// </summary>
        [SugarColumn(ColumnName = "Content", IsNullable = false, ColumnDataType = "text", ColumnDescription = "记忆内容")]
        public string Content { get; set; }

        /// <summary>
        /// 模式标签，JSON格式
        /// </summary>
        [SugarColumn(ColumnName = "PatternTags", IsNullable = true, ColumnDataType = "text", ColumnDescription = "模式标签")]
        public string PatternTags { get; set; }

        /// <summary>
        /// 信号类型
        /// </summary>
        [SugarColumn(ColumnName = "SignalType", IsNullable = true, ColumnDescription = "信号类型")]
        public string SignalType { get; set; }

        /// <summary>
        /// 记忆来源：manual=用户校正，ai_extract=AI提取，auto_learn=自动学习
        /// </summary>
        [SugarColumn(ColumnName = "Source", IsNullable = false, DefaultValue = "manual", ColumnDescription = "来源")]
        public string Source { get; set; } = "manual";

        /// <summary>
        /// 重要性评分，1-10分
        /// </summary>
        [SugarColumn(ColumnName = "ImportanceScore", IsNullable = false, DefaultValue = "5", ColumnDescription = "重要性评分")]
        public float ImportanceScore { get; set; } = 5.0f;

        /// <summary>
        /// 是否已验证
        /// </summary>
        [SugarColumn(ColumnName = "IsVerified", IsNullable = false, DefaultValue = "true", ColumnDescription = "是否已验证")]
        public bool IsVerified { get; set; } = true;

        /// <summary>
        /// 是否激活
        /// </summary>
        [SugarColumn(ColumnName = "IsActive", IsNullable = false, DefaultValue = "true", ColumnDescription = "是否激活")]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 向量嵌入
        /// </summary>
        [SugarColumn(ColumnName = "Embedding", IsNullable = true, ColumnDescription = "向量嵌入")]
        public string Embedding { get; set; }
    }
}
