using System.Collections.Generic;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// Workflow详情返回结果，包含任务列表
    /// </summary>
    public class WorkflowDetailResult
    {
        /// <summary>
        /// Workflow实体
        /// </summary>
        public Agent_WorkflowEntity Workflow { get; set; }

        /// <summary>
        /// 关联的任务列表
        /// </summary>
        public List<Agent_TaskEntity> Tasks { get; set; } = new List<Agent_TaskEntity>();

        /// <summary>
        /// 状态文本
        /// </summary>
        public string StatusText => GetStatusText(Workflow?.Status);

        private static string GetStatusText(int? status)
        {
            return status switch
            {
                0 => "待规划",
                1 => "规划中",
                2 => "执行中",
                3 => "已完成",
                4 => "失败",
                5 => "已取消",
                _ => "未知"
            };
        }
    }
}
