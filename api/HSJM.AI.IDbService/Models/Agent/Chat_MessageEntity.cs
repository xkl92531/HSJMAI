using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 会话消息表：存储会话中的每条消息（用户/助手/系统）
    /// </summary>
    [SugarTable("AI_Chat_Message", tableDescription: "会话消息表：存储会话中的每条消息")]
    public class Chat_MessageEntity : BaseField
    {
        public Chat_MessageEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        /// 所属会话ID（外键关联 AI_Chat_Session）
        /// </summary>
        [SugarColumn(IsNullable = false, ColumnDescription = "所属会话ID")]
        public long SessionId { get; set; }

        /// <summary>
        /// 消息角色：user(用户)/assistant(助手)/system(系统)/tool(工具)
        /// </summary>
        [SugarColumn(IsNullable = false, ColumnDescription = "消息角色")]
        public string Role { get; set; }

        /// <summary>
        /// 消息内容
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "消息内容", ColumnDataType = "nvarchar(MAX)")]
        public string Content { get; set; }

        /// <summary>
        /// 消息序号（同一会话内递增）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "消息序号")]
        public int? SequenceNumber { get; set; }

        /// <summary>
        /// 父消息ID（用于支持分支对话）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "父消息ID")]
        public long? ParentMessageId { get; set; }

        /// <summary>
        /// 关联的智能体角色ID（助手消息时有值）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的智能体角色ID")]
        public long? AgentRoleId { get; set; }

        /// <summary>
        /// 关联的Workflow ID（如果消息来自工作流执行）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的Workflow ID")]
        public long? WorkflowId { get; set; }

        /// <summary>
        /// 关联的Task ID（如果消息来自任务执行）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "关联的Task ID")]
        public long? TaskId { get; set; }

        /// <summary>
        /// 工具调用记录 JSON
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "工具调用记录 JSON", ColumnDataType = "nvarchar(MAX)")]
        public string ToolCalls { get; set; }

        /// <summary>
        /// 消息附件 JSON（文件、图片等）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "消息附件 JSON")]
        public string Attachments { get; set; }

        /// <summary>
        /// Token使用量 JSON
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "Token使用量 JSON")]
        public string TokenUsage { get; set; }

        /// <summary>
        /// 模型名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "模型名称")]
        public string ModelName { get; set; }

        /// <summary>
        /// 消息状态：0 正常、1 已编辑、2 已删除、3 生成中、4 生成失败
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "消息状态")]
        public int? Status { get; set; }

        /// <summary>
        /// 错误信息（如果消息生成失败）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "错误信息")]
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 消息评分（用户反馈：1-5星）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "消息评分")]
        public int? Rating { get; set; }

        /// <summary>
        /// 用户反馈备注
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "用户反馈备注")]
        public string FeedbackNote { get; set; }

        /// <summary>
        /// 消息元数据 JSON（扩展字段）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "消息元数据 JSON")]
        public string Metadata { get; set; }

        /// <summary>
        /// 消息发送时间
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "消息发送时间")]
        public DateTime? SentTime { get; set; }
    }
}
