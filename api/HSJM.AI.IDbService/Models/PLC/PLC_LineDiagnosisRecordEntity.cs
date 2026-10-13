using SqlSugar;
using HSJM.AI.Core;
using System;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 全线停机诊断记录持久化表
    /// </summary>
    [SugarTable("ai_plc_line_diagnosis_record", tableDescription: "全线停机诊断记录表")]
    public class PLC_LineDiagnosisRecordEntity
    {
        public PLC_LineDiagnosisRecordEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// SignalR TaskId（可关联前端会话）
        /// </summary>
        [SugarColumn(ColumnName = "task_id", IsNullable = true, Length = 100)]
        public string TaskId { get; set; }

        /// <summary>
        /// 涉及的 ProjectId 列表（逗号分隔）
        /// </summary>
        [SugarColumn(ColumnName = "project_ids", IsNullable = true, Length = 200)]
        public string ProjectIds { get; set; }

        /// <summary>
        /// 诊断发起时间
        /// </summary>
        [SugarColumn(ColumnName = "diagnosed_at", IsNullable = true)]
        public DateTime? DiagnosedAt { get; set; }

        /// <summary>
        /// 总工位数
        /// </summary>
        [SugarColumn(ColumnName = "total_stations", IsNullable = true)]
        public int? TotalStations { get; set; }

        /// <summary>
        /// 已诊断工位数
        /// </summary>
        [SugarColumn(ColumnName = "diagnosed_stations", IsNullable = true)]
        public int? DiagnosedStations { get; set; }

        /// <summary>
        /// 异常工位数
        /// </summary>
        [SugarColumn(ColumnName = "abnormal_stations", IsNullable = true)]
        public int? AbnormalStations { get; set; }

        /// <summary>
        /// 总耗时(ms)
        /// </summary>
        [SugarColumn(ColumnName = "elapsed_ms", IsNullable = true)]
        public long? ElapsedMs { get; set; }

        /// <summary>
        /// 是否超时终止
        /// </summary>
        [SugarColumn(ColumnName = "timed_out", IsNullable = true)]
        public bool? TimedOut { get; set; }

        /// <summary>
        /// 诊断模式：Traditional / Agent
        /// </summary>
        [SugarColumn(ColumnName = "diagnosis_mode", IsNullable = true, Length = 30)]
        public string DiagnosisMode { get; set; }

        // ── LLM 分析结果摘要 ──

        /// <summary>
        /// 根因工位
        /// </summary>
        [SugarColumn(ColumnName = "root_cause_station", IsNullable = true, Length = 50)]
        public string RootCauseStation { get; set; }

        /// <summary>
        /// 根因描述
        /// </summary>
        [SugarColumn(ColumnName = "root_cause_description", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string RootCauseDescription { get; set; }

        /// <summary>
        /// 置信度 0.0~1.0
        /// </summary>
        [SugarColumn(ColumnName = "confidence", IsNullable = true)]
        public double? Confidence { get; set; }

        /// <summary>
        /// 故障影响范围（辐射式描述）
        /// </summary>
        [SugarColumn(ColumnName = "impact_chain", IsNullable = true, Length = 500)]
        public string ImpactChain { get; set; }

        /// <summary>
        /// 建议动作（JSON 数组）
        /// </summary>
        [SugarColumn(ColumnName = "suggested_actions_json", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string SuggestedActionsJson { get; set; }

        /// <summary>
        /// LLM 推理过程
        /// </summary>
        [SugarColumn(ColumnName = "reasoning", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string Reasoning { get; set; }

        /// <summary>
        /// Agent 迭代次数（Agent 模式）
        /// </summary>
        [SugarColumn(ColumnName = "agent_iterations", IsNullable = true)]
        public int? AgentIterations { get; set; }

        /// <summary>
        /// 工具调用摘要（Agent 模式）
        /// </summary>
        [SugarColumn(ColumnName = "tool_call_summary", IsNullable = true, Length = 500)]
        public string ToolCallSummary { get; set; }

        /// <summary>
        /// 完整诊断结果 JSON（含各工位明细，用于历史重放）
        /// </summary>
        [SugarColumn(ColumnName = "result_json", IsNullable = true, ColumnDataType = "nvarchar(max)")]
        public string ResultJson { get; set; }

        /// <summary>
        /// 策略分布（如 "TemplateBased:5,StallPointBased:3,AnchorScan:2"）
        /// </summary>
        [SugarColumn(ColumnName = "strategy_distribution", IsNullable = true, Length = 300)]
        public string StrategyDistribution { get; set; }

        // ── BaseField 属性 ──

        [SugarColumn(ColumnName = "isdeleted")]
        public bool IsDeleted { get; set; }

        [SugarColumn(ColumnName = "createrid", IsNullable = true)]
        public long? CreaterId { get; set; } = 0;

        [SugarColumn(ColumnName = "creatername", IsNullable = true)]
        public string CreaterName { get; set; }

        [SugarColumn(ColumnName = "createtime", IsNullable = true)]
        public DateTime? CreateTime { get; set; }

        [SugarColumn(ColumnName = "modifierid", IsNullable = true)]
        public long? ModifierId { get; set; } = 0;

        [SugarColumn(ColumnName = "modifytime", IsNullable = true)]
        public DateTime? ModifyTime { get; set; }

        [SugarColumn(ColumnName = "modifiername", IsNullable = true)]
        public string ModifierName { get; set; }
    }
}
