//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V1  描述:V1_诊断记忆快照(多场景)  添加时间:2026/4/1
using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// AI诊断记忆压缩快照表
    /// 记录每次压缩前的状态，用于回退
    /// </summary>
    [SugarTable("AI_IOConstraintSnapshot", tableDescription: "记忆压缩快照表")]
    public class DiagnosticSnapshotEntity : BaseField
    {
        public DiagnosticSnapshotEntity()
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
        /// 产线ID，NULL表示全局记忆压缩
        /// </summary>
        [SugarColumn(ColumnName = "ProductionLineId", IsNullable = true, ColumnDescription = "产线ID")]
        public long? ProductionLineId { get; set; }

        /// <summary>
        /// 快照日期
        /// </summary>
        [SugarColumn(ColumnName = "SnapshotDate", IsNullable = false, ColumnDescription = "快照日期")]
        public DateTime SnapshotDate { get; set; }

        /// <summary>
        /// 快照文件存储路径
        /// </summary>
        [SugarColumn(ColumnName = "SnapshotPath", IsNullable = true, Length = 500, ColumnDescription = "快照文件存储路径")]
        public string SnapshotPath { get; set; }

        /// <summary>
        /// 快照时L0核心规则数量
        /// </summary>
        [SugarColumn(ColumnName = "L0Count", IsNullable = true, ColumnDescription = "L0核心规则数量")]
        public int? L0Count { get; set; }

        /// <summary>
        /// 快照时L1案例数量
        /// </summary>
        [SugarColumn(ColumnName = "L1Count", IsNullable = true, ColumnDescription = "L1案例数量")]
        public int? L1Count { get; set; }

        /// <summary>
        /// 快照时L2原始日志数量
        /// </summary>
        [SugarColumn(ColumnName = "L2Count", IsNullable = true, ColumnDescription = "L2原始日志数量")]
        public int? L2Count { get; set; }

        /// <summary>
        /// 验证状态：pending=待验证，passed=通过，failed=失败
        /// </summary>
        [SugarColumn(ColumnName = "VerificationStatus", IsNullable = false, DefaultValue = "pending", ColumnDescription = "验证状态")]
        public string VerificationStatus { get; set; } = "pending";

        /// <summary>
        /// AI自检报告，JSON格式
        /// </summary>
        [SugarColumn(ColumnName = "VerificationReport", IsNullable = true, ColumnDataType = "jsonb", ColumnDescription = "AI自检报告")]
        public string VerificationReport { get; set; }

        /// <summary>
        /// 压缩失败时的错误信息
        /// </summary>
        [SugarColumn(ColumnName = "ErrorMessage", IsNullable = true, ColumnDataType = "text", ColumnDescription = "错误信息")]
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 失败影响的记忆ID列表，JSON数组
        /// </summary>
        [SugarColumn(ColumnName = "AffectedMemoryIds", IsNullable = true, ColumnDataType = "jsonb", ColumnDescription = "影响记忆ID列表")]
        public string AffectedMemoryIds { get; set; }
    }
}