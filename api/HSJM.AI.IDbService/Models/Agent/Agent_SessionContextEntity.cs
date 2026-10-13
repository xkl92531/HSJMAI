using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 会话完整上下文表：存储每个会话的完整对话内容，供记忆提取使用
    /// </summary>
    [SugarTable("agent_session_contexts", tableDescription: "会话完整上下文表")]
    public class Agent_SessionContextEntity : BaseField
    {
        public Agent_SessionContextEntity()
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
        /// 会话ID（关联 AI_Chat_Session）
        /// </summary>
        [SugarColumn(ColumnName = "session_id", IsNullable = false, ColumnDescription = "会话ID")]
        public long SessionId { get; set; }

        /// <summary>
        /// 对话轮次序号（从1开始递增）
        /// </summary>
        [SugarColumn(ColumnName = "turn_number", ColumnDescription = "对话轮次序号")]
        public int TurnNumber { get; set; }

        /// <summary>
        /// 用户消息内容
        /// </summary>
        [SugarColumn(ColumnName = "user_message", ColumnDataType = "nvarchar(MAX)", IsNullable = false, ColumnDescription = "用户消息内容")]
        public string UserMessage { get; set; }

        /// <summary>
        /// 助手回复内容
        /// </summary>
        [SugarColumn(ColumnName = "assistant_message", ColumnDataType = "nvarchar(MAX)", IsNullable = true, ColumnDescription = "助手回复内容")]
        public string AssistantMessage { get; set; }

        /// <summary>
        /// 完整的上下文JSON（包含更多详细信息，如工具调用、思考过程等）
        /// </summary>
        [SugarColumn(ColumnName = "context_json", ColumnDataType = "nvarchar(MAX)", IsNullable = true, ColumnDescription = "完整上下文JSON")]
        public string ContextJson { get; set; }

        /// <summary>
        /// 关联的智能体角色ID（如果是AI回复）
        /// </summary>
        [SugarColumn(ColumnName = "agent_role_id", IsNullable = true, ColumnDescription = "关联的智能体角色ID")]
        public long? AgentRoleId { get; set; }

        /// <summary>
        /// 关联的Workflow ID（如果是工作流对话）
        /// </summary>
        [SugarColumn(ColumnName = "workflow_id", IsNullable = true, ColumnDescription = "关联的Workflow ID")]
        public long? WorkflowId { get; set; }

        /// <summary>
        /// 是否已提取过记忆（避免重复提取）
        /// </summary>
        [SugarColumn(ColumnName = "is_memory_extracted", DefaultValue = "false", ColumnDescription = "是否已提取过记忆")]
        public bool IsMemoryExtracted { get; set; }

        /// <summary>
        /// 记忆提取时间
        /// </summary>
        [SugarColumn(ColumnName = "extracted_at", IsNullable = true, ColumnDescription = "记忆提取时间")]
        public DateTime? ExtractedAt { get; set; }

        /// <summary>
        /// Token使用量统计
        /// </summary>
        [SugarColumn(ColumnName = "token_usage", IsNullable = true, ColumnDescription = "Token使用量统计JSON")]
        public string TokenUsage { get; set; }

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