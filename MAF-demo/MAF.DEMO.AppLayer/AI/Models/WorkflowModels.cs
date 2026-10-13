// Copyright (c) Microsoft. All rights reserved.
// Workflow结果和请求模型

using System;
using System.Collections.Generic;

namespace MAF.DEMO.AppLayer.AI.Models
{
    /// <summary>
    /// Workflow执行结果
    /// </summary>
    public class WorkflowResult
    {
        /// <summary>
        /// 工作流ID（对应数据库AI_Agent_Workflow.ID）
        /// </summary>
        public string WorkflowId { get; set; } = string.Empty;

        /// <summary>
        /// 工作流类型: ConditionalRouting, FanOutFanIn, SequentialChain, HybridOrchestration
        /// </summary>
        public string WorkflowType { get; set; } = string.Empty;

        /// <summary>
        /// 执行状态: Pending, Running, Completed, Failed, Cancelled
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// 最终输出结果
        /// </summary>
        public string FinalOutput { get; set; } = string.Empty;

        /// <summary>
        /// 错误信息（如果失败）
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 执行的步骤列表
        /// </summary>
        public List<string> StepsExecuted { get; set; } = new();

        /// <summary>
        /// 执行耗时（毫秒）
        /// </summary>
        public int ExecutionTimeMs { get; set; }

        /// <summary>
        /// 开始时间
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// 完成时间
        /// </summary>
        public DateTime? CompletionTime { get; set; }

        /// <summary>
        /// 扩展数据（JSON格式）
        /// </summary>
        public string? ExtensionData { get; set; }
    }

    /// <summary>
    /// Workflow流式输出块
    /// </summary>
    public class WorkflowStreamChunk
    {
        /// <summary>
        /// 块类型: status, step, agent_output, final, error
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// 内容
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Agent名称（agent_output、agent_start等类型时有值，系统消息时为空字符串）
        /// </summary>
        public string AgentName { get; set; } = string.Empty;

        /// <summary>
        /// 时间戳
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Workflow执行请求
    /// </summary>
    public class WorkflowRequest
    {
        /// <summary>
        /// 用户输入
        /// </summary>
        public string Input { get; set; } = string.Empty;

        /// <summary>
        /// 工作流类型（可选）
        /// ConditionalRouting, FanOutFanIn, SequentialChain, HybridOrchestration, Auto
        /// </summary>
        public string WorkflowType { get; set; } = "Auto";

        /// <summary>
        /// 是否启用Checkpoint（持久化）
        /// </summary>
        public bool EnableCheckpoint { get; set; } = false;

        /// <summary>
        /// 工作流ID（恢复执行时使用）
        /// </summary>
        public string? WorkflowId { get; set; }

        /// <summary>
        /// 扩展参数
        /// </summary>
        public Dictionary<string, string>? ExtensionParams { get; set; }
    }

    /// <summary>
    /// 路由决策（Router Agent输出）
    /// </summary>
    public class RouterDecision
    {
        public bool NeedsDataAnalysis { get; set; }
        public bool NeedsTimeInfo { get; set; }
        public bool NeedsWeather { get; set; }
        public bool NeedsDeveloper { get; set; }
        public string Complexity { get; set; } = "medium"; // simple, medium, complex
        public int EstimatedDuration { get; set; } // 预估秒数
    }

    /// <summary>
    /// 分析器决策（Analyzer Agent输出）
    /// </summary>
    public class AnalyzerDecision
    {
        public string Complexity { get; set; } = "medium";
        public bool NeedsPlanning { get; set; }
        public List<string> RecommendedExperts { get; set; } = new();
        public string? ReasoningPath { get; set; }
    }

    /// <summary>
    /// Checkpoint数据（用于恢复）
    /// </summary>
    public class CheckpointData
    {
        public string WorkflowId { get; set; } = string.Empty;
        public string WorkflowType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string CurrentStep { get; set; } = string.Empty;
        public string ExecutionContextJson { get; set; } = string.Empty;
        public DateTime CheckpointTime { get; set; }
    }

    /// <summary>
    /// 执行计划（Master Agent输出）
    /// </summary>
    public class ExecutionPlan
    {
        public List<TaskDefinition> Tasks { get; set; } = new();
        public string ExecutionMode { get; set; } = "sequential"; // parallel | sequential | hybrid
        public string Reasoning { get; set; } = string.Empty;
        public string Complexity { get; set; } = "medium"; // simple | medium | complex
    }

    /// <summary>
    /// 任务定义
    /// </summary>
    public class TaskDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string AssignTo { get; set; } = string.Empty; // Agent类型
        public int Priority { get; set; } = 5; // 1-10
        public int EstimatedDurationSeconds { get; set; } = 10;
        public List<string> RequiredTools { get; set; } = new();
        public List<string> Dependencies { get; set; } = new(); // 依赖的任务ID
    }

    /// <summary>
    /// 任务执行结果
    /// </summary>
    public class TaskExecutionResult
    {
        public string TaskId { get; set; } = string.Empty;
        public string TaskDescription { get; set; } = string.Empty;
        public string AssignedAgent { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public bool Success { get; set; }
        public int ExecutionTimeMs { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
