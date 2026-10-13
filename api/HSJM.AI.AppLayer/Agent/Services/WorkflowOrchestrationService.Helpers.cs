using System;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// WorkflowOrchestrationService 分部类 - 智能化辅助方法
    /// 🆕 阶段1.1：提供智能字段计算逻辑，避免主文件过大
    /// </summary>
    public partial class WorkflowOrchestrationService
    {
        #region 智能化辅助方法

        /// <summary>
        /// 生成智能化工作流名称（体现任务本质）
        /// </summary>
        private string GenerateWorkflowName(string prefix, string userInput)
        {
            if (string.IsNullOrEmpty(userInput))
                return prefix;
            
            // 提取前30个字符作为摘要
            var summary = userInput.Length > 30 
                ? userInput.Substring(0, 30) + "..." 
                : userInput;
            
            return $"{prefix}-{summary}";
        }

        /// <summary>
        /// 智能选择推理模式（根据任务复杂度）
        /// </summary>
        private string DetermineThinkingMode(string input, int taskCount)
        {
            if (string.IsNullOrEmpty(input))
                return "ReAct"; // 默认
            
            // 简单问题：直接执行
            if (input.Length < 50 && taskCount <= 2)
            {
                _logger.LogDebug("[智能选择] 任务简单，使用 Direct 模式");
                return "Direct";
            }
            
            // 中等复杂度：ReAct
            if (taskCount <= 5)
            {
                _logger.LogDebug("[智能选择] 任务中等复杂，使用 ReAct 模式");
                return "ReAct";
            }
            
            // 高复杂度：计划执行
            _logger.LogDebug("[智能选择] 任务高度复杂，使用 PlanAndExecute 模式");
            return "PlanAndExecute";
        }

        /// <summary>
        /// 动态优先级计算（检测紧急关键词）
        /// </summary>
        private int CalculatePriority(string input)
        {
            if (string.IsNullOrEmpty(input))
                return 5; // 默认优先级
            
            var urgentKeywords = new[] 
            { 
                "紧急", "立即", "urgent", "asap", 
                "故障", "报警", "critical", "emergency",
                "停机", "down", "crashed"
            };
            
            var isUrgent = urgentKeywords.Any(k => 
                input.Contains(k, StringComparison.OrdinalIgnoreCase));
            
            var priority = isUrgent ? 10 : 5;
            
            if (isUrgent)
            {
                _logger.LogWarning("[优先级] 检测到紧急关键词，优先级提升至 {Priority}", priority);
            }
            
            return priority;
        }

        /// <summary>
        /// 预估工作流执行时长（基于任务数量）
        /// </summary>
        private int EstimateWorkflowDuration(int taskCount)
        {
            if (taskCount == 0) 
                return 30; // 默认30秒
            
            // 每个任务平均 15 秒 + 10 秒协调时间
            var estimated = taskCount * 15 + 10;
            
            _logger.LogDebug("[预估耗时] 任务数={TaskCount}, 预估={EstDuration}秒", 
                taskCount, estimated);
            
            return estimated;
        }

        /// <summary>
        /// 生成执行计划 DAG（Mermaid 格式，支持可视化）
        /// </summary>
        private string GeneratePlanDAG(long? routerId, System.Collections.Generic.Dictionary<long, string> expertRoleIds)
        {
            if (expertRoleIds == null || expertRoleIds.Count == 0)
                return "graph TD\n  Start[开始] --> End[结束]";
            
            var sb = new StringBuilder();
            sb.AppendLine("graph TD");
            sb.AppendLine("  Start[用户输入] --> Router[Router分析]");
            
            if (routerId.HasValue)
            {
                sb.AppendLine($"  Router[Router_{routerId}]");
            }
            
            foreach (var (expertId, expertDesc) in expertRoleIds)
            {
                // 避免 Mermaid 语法冲突
                var safeDesc = expertDesc
                    .Replace("[", "")
                    .Replace("]", "")
                    .Replace("\"", "'");
                
                sb.AppendLine($"  Router --> Expert_{expertId}[\"{safeDesc}\"]");
                sb.AppendLine($"  Expert_{expertId} --> Summary[汇总结果]");
            }
            
            sb.AppendLine("  Summary --> End[完成]");
            
            return sb.ToString();
        }

        /// <summary>
        /// 生成顺序链 DAG（Planner → Executor → Reviewer）
        /// </summary>
        private string GenerateSequentialChainDAG(long plannerId, long executorId, long reviewerId)
        {
            var sb = new StringBuilder();
            sb.AppendLine("graph LR");
            sb.AppendLine("  Start[用户输入] --> Planner[规划者]");
            sb.AppendLine($"  Planner[Planner_{plannerId}]");
            sb.AppendLine($"  Planner --> Executor[Executor_{executorId}]");
            sb.AppendLine($"  Executor --> Reviewer[Reviewer_{reviewerId}]");
            sb.AppendLine("  Reviewer --> End[完成]");
            
            return sb.ToString();
        }

        /// <summary>
        /// 标记工作流为已取消状态
        /// </summary>
        public async System.Threading.Tasks.Task MarkWorkflowCancelledAsync(long workflowId)
        {
            try
            {
                var workflow = await _workflowService.GetByIdAsync(workflowId);
                if (workflow != null)
                {
                    workflow.Status = 5; // 5 = 已取消
                    //workflow.EndTime = DateTime.Now;
                    workflow.PausedReason = "用户取消";
                    await _workflowService.UpdateAsync(workflow);
                    
                    _logger.LogInformation("[工作流] 已标记为取消: WorkflowId={WorkflowId}", workflowId);
                    
                    // 通过 SignalR 通知前端
                    await _hubContext.Clients.Group($"workflow-{workflowId}")
                        .SendAsync("WorkflowCancelled", new 
                        { 
                            workflowId = workflowId.ToString(),
                            message = "工作流已被用户取消",
                            timestamp = DateTime.Now
                        });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[工作流] 标记取消失败: WorkflowId={WorkflowId}", workflowId);
            }
        }

        #endregion
    }
}
