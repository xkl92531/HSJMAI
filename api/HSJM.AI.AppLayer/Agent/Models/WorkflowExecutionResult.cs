using System.Collections.Generic;

namespace HSJM.AI.AppLayer.Agent.Models
{
    /// <summary>
    /// 工作流执行结果模型
    /// </summary>
    public class WorkflowExecutionResult
    {
        /// <summary>
        /// 工作流 ID
        /// </summary>
        public long WorkflowId { get; set; }

        /// <summary>
        /// 会话 ID（用于多轮对话）
        /// </summary>
        public long? SessionId { get; set; }

        /// <summary>
        /// 是否执行成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 执行结果内容
        /// </summary>
        public string Result { get; set; } = "";

        /// <summary>
        /// 错误消息（失败时）
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 执行耗时（毫秒）
        /// </summary>
        public double ExecutionTimeMs { get; set; }

        /// <summary>
        /// Router 的决策内容（条件路由模式）
        /// </summary>
        public string? RouterDecision { get; set; }

        /// <summary>
        /// 使用的专家列表（条件路由模式）
        /// </summary>
        public List<string>? ExpertsUsed { get; set; }

        /// <summary>
        /// 已完成的步骤数（顺序链模式）
        /// </summary>
        public int StepsCompleted { get; set; }

        /// <summary>
        /// 校正日志ID（约束校正场景）
        /// </summary>
        public long? CorrectionLogId { get; set; }

        /// <summary>
        /// 状态标识（约束校正场景：awaiting_confirmation）
        /// </summary>
        public string? Status { get; set; }
    }
}
