//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V1  描述:V1_诊断校正日志(多场景)  添加时间:2026/4/1
using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// AI诊断校正日志表
    /// 记录用户每次校正反馈的完整原始数据，支持多场景
    /// </summary>
    [SugarTable("AI_IOConstraintCorrectionLog", tableDescription: "AI诊断校正日志表")]
    public class DiagnosticCorrectionLogEntity : BaseField
    {
        public DiagnosticCorrectionLogEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        /// <summary>
        /// 主键ID
        /// </summary>
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true, ColumnDescription = "主键ID")]
        public long Id { get; set; }

        /// <summary>
        /// 用户ID
        /// </summary>
        [SugarColumn(ColumnName = "UserId", IsNullable = false, ColumnDescription = "用户ID")]
        public long UserId { get; set; }

        /// <summary>
        /// 场景类型：constraint=约束生成，stuck_point=卡滞诊断，energy_flow=能量流分析
        /// </summary>
        [SugarColumn(ColumnName = "SceneType", IsNullable = false, Length = 50, ColumnDescription = "场景类型")]
        public string SceneType { get; set; }

        /// <summary>
        /// 产线ID
        /// </summary>
        [SugarColumn(ColumnName = "ProductionLineId", IsNullable = true, ColumnDescription = "产线ID")]
        public long? ProductionLineId { get; set; }

        /// <summary>
        /// 涉及的信号名称
        /// </summary>
        [SugarColumn(ColumnName = "SignalName", IsNullable = true, Length = 100, ColumnDescription = "涉及的信号名称")]
        public string SignalName { get; set; }

        /// <summary>
        /// 用户原始反馈内容
        /// </summary>
        [SugarColumn(ColumnName = "UserFeedback", IsNullable = false, ColumnDataType = "text", ColumnDescription = "用户原始反馈内容")]
        public string UserFeedback { get; set; }

        /// <summary>
        /// AI归纳后的pattern内容
        /// </summary>
        [SugarColumn(ColumnName = "AiSummary", IsNullable = true, ColumnDataType = "text", ColumnDescription = "AI归纳后的pattern内容")]
        public string AiSummary { get; set; }

        /// <summary>
        /// 用户是否已确认归纳内容
        /// </summary>
        [SugarColumn(ColumnName = "UserConfirmed", IsNullable = false, DefaultValue = "false", ColumnDescription = "用户是否已确认归纳内容")]
        public bool UserConfirmed { get; set; } = false;

        /// <summary>
        /// 是否可泛化至所有同类信号/场景
        /// </summary>
        [SugarColumn(ColumnName = "Generalizable", IsNullable = false, DefaultValue = "false", ColumnDescription = "是否可泛化")]
        public bool Generalizable { get; set; } = false;

        /// <summary>
        /// 适用范围
        /// </summary>
        [SugarColumn(ColumnName = "ApplicableScope", IsNullable = true, Length = 100, ColumnDescription = "适用范围")]
        public string ApplicableScope { get; set; }

        /// <summary>
        /// 校正状态：pending=待确认，approved=已确认，rejected=已拒绝
        /// </summary>
        [SugarColumn(ColumnName = "CorrectionStatus", IsNullable = false, DefaultValue = "pending", ColumnDescription = "校正状态")]
        public string CorrectionStatus { get; set; } = "pending";

        /// <summary>
        /// 向量嵌入，用于语义检索
        /// </summary>
        [SugarColumn(ColumnName = "Embedding", IsNullable = true, ColumnDataType = "vector(1536)", ColumnDescription = "向量嵌入")]
        public string Embedding { get; set; }
    }
}