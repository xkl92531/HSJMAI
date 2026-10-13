using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 会话管理表：记录多轮对话的会话信息
    /// </summary>
    [SugarTable("AI_Chat_Session", tableDescription: "会话管理表：记录多轮对话的会话信息")]
    public class Chat_SessionEntity : BaseField
    {
        public Chat_SessionEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 会话标题（自动生成或用户自定义）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "会话标题")]
        public string Title { get; set; }

        /// <summary>
        /// 会话描述或摘要
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "会话描述或摘要")]
        public string Description { get; set; }

        /// <summary>
        /// 会话类型：DirectChat(直接对话)/WorkflowChat(工作流对话)
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "会话类型")]
        public string SessionType { get; set; }

        /// <summary>
        /// 会话状态：0 活跃、1 已归档、2 已删除
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "会话状态：0 活跃、1 已归档、2 已删除")]
        public int? Status { get; set; }

        /// <summary>
        /// 关联的智能体角色ID（主要对话角色）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的智能体角色ID")]
        public long? PrimaryAgentRoleId { get; set; }

        /// <summary>
        /// 会话配置 JSON（如：模型参数、温度、最大Token等）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "会话配置 JSON")]
        public string SessionConfig { get; set; }

        /// <summary>
        /// 会话元数据 JSON（自定义扩展字段）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "会话元数据 JSON")]
        public string Metadata { get; set; }

        /// <summary>
        /// 最后一条消息的时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "最后一条消息的时间")]
        public DateTime? LastMessageTime { get; set; }

        /// <summary>
        /// 消息总数（冗余字段，用于快速查询）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "消息总数")]
        public int? MessageCount { get; set; }

        /// <summary>
        /// 关联的Workflow ID（如果是通过工作流创建的会话）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的Workflow ID")]
        public long? WorkflowId { get; set; }

        /// <summary>
        /// 会话标签（多标签用逗号分隔）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "会话标签")]
        public string Tags { get; set; }

        /// <summary>
        /// 是否固定（置顶）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否固定")]
        public bool? IsPinned { get; set; }

        /// <summary>
        /// 会话开始时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "会话开始时间")]
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// 会话结束时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "会话结束时间")]
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// Token使用统计 JSON
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "Token使用统计 JSON")]
        public string TokenUsageStats { get; set; }
    }
}
