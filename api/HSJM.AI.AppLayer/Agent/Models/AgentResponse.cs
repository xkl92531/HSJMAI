namespace HSJM.AI.AppLayer.Agent.Models
{
    /// <summary>
    /// 智能体响应模型
    /// </summary>
    public class AgentResponse
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 响应内容
        /// </summary>
        public string Content { get; set; } = "";

        /// <summary>
        /// 错误消息
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}
