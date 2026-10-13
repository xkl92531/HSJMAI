using System.Collections.Generic;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 步骤交叉引用诊断响应DTO
    /// </summary>
    public class StepCrossReferenceDiagnosticResponseDto
    {
        /// <summary>
        /// 诊断是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 卡住的步骤信息
        /// </summary>
        public StuckStepInfo? StuckStep { get; set; }

        /// <summary>
        /// 出边转换条件分析
        /// </summary>
        public List<TransitionConditionAnalysis> OutgoingTransitions { get; set; } = new List<TransitionConditionAnalysis>();

        /// <summary>
        /// 变量交叉引用分析
        /// </summary>
        public List<VariableCrossReference> VariableCrossReferences { get; set; } = new List<VariableCrossReference>();

        /// <summary>
        /// 根因分析
        /// </summary>
        public RootCauseAnalysis? RootCause { get; set; }

        /// <summary>
        /// 诊断总结
        /// </summary>
        public string? Summary { get; set; }

        /// <summary>
        /// 白话文诊断报告（简化版MD格式）
        /// </summary>
        public string? SimpleReport { get; set; }
    }

    /// <summary>
    /// 卡住的步骤信息
    /// </summary>
    public class StuckStepInfo
    {
        /// <summary>
        /// 步骤ID
        /// </summary>
        public long StepId { get; set; }

        /// <summary>
        /// 步骤名称
        /// </summary>
        public string StepName { get; set; } = "";

        /// <summary>
        /// 步骤类型
        /// </summary>
        public string? StepType { get; set; }

        /// <summary>
        /// 步骤描述
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// 出边数量
        /// </summary>
        public int OutgoingConnectionCount { get; set; }
    }

    /// <summary>
    /// 转换条件分析
    /// </summary>
    public class TransitionConditionAnalysis
    {
        /// <summary>
        /// 转换ID
        /// </summary>
        public long TransitionId { get; set; }

        /// <summary>
        /// 转换名称
        /// </summary>
        public string TransitionName { get; set; } = "";

        /// <summary>
        /// 目标步骤名称
        /// </summary>
        public string TargetStepName { get; set; } = "";

        /// <summary>
        /// 条件是否通过
        /// </summary>
        public bool IsPassed { get; set; }

        /// <summary>
        /// 置信度分数（0-1）
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// 条件详细信息
        /// </summary>
        public string? ConditionDetails { get; set; }

        /// <summary>
        /// 使用的变量列表
        /// </summary>
        public List<string> Variables { get; set; } = new List<string>();

        /// <summary>
        /// 不满足的条件
        /// </summary>
        public List<FailedCondition> FailedConditions { get; set; } = new List<FailedCondition>();

        /// <summary>
        /// AI结构分析结果（新增）
        /// </summary>
        public AIStructureAnalysis? AIAnalysis { get; set; }
    }

    /// <summary>
    /// AI结构分析结果
    /// </summary>
    public class AIStructureAnalysis
    {
        /// <summary>
        /// XML格式类型（LAD/FBD/SCL/STL等）
        /// </summary>
        public string? XmlFormatType { get; set; }

        /// <summary>
        /// 逻辑复杂度（1-5）
        /// </summary>
        public int LogicComplexity { get; set; }

        /// <summary>
        /// AI对条件的语义理解
        /// </summary>
        public string? SemanticDescription { get; set; }

        /// <summary>
        /// 提取的逻辑表达式
        /// </summary>
        public string? LogicExpression { get; set; }

        /// <summary>
        /// 函数块调用（如果有）
        /// </summary>
        public List<FunctionBlockCall> FunctionBlocks { get; set; } = new List<FunctionBlockCall>();

        /// <summary>
        /// AI推断的期望值范围
        /// </summary>
        public Dictionary<string, string> ExpectedValueRanges { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// 函数块调用信息
    /// </summary>
    public class FunctionBlockCall
    {
        /// <summary>
        /// 函数块名称
        /// </summary>
        public string BlockName { get; set; } = "";

        /// <summary>
        /// 函数块类型（FB/FC）
        /// </summary>
        public string BlockType { get; set; } = "";

        /// <summary>
        /// 功能描述
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// 输入参数
        /// </summary>
        public Dictionary<string, string> Inputs { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// 失败的条件
    /// </summary>
    public class FailedCondition
    {
        /// <summary>
        /// 变量名
        /// </summary>
        public string Variable { get; set; } = "";

        /// <summary>
        /// 期望值
        /// </summary>
        public string ExpectedValue { get; set; } = "";

        /// <summary>
        /// 实际值
        /// </summary>
        public string ActualValue { get; set; } = "";

        /// <summary>
        /// 比较运算符
        /// </summary>
        public string Operator { get; set; } = "";

        /// <summary>
        /// 原因说明
        /// </summary>
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// 变量交叉引用
    /// </summary>
    public class VariableCrossReference
    {
        /// <summary>
        /// 变量名
        /// </summary>
        public string VariableName { get; set; } = "";

        /// <summary>
        /// 变量类型（DB、M、I、Q等）
        /// </summary>
        public string VariableType { get; set; } = "";

        /// <summary>
        /// 当前值
        /// </summary>
        public string CurrentValue { get; set; } = "";

        /// <summary>
        /// 引用位置列表
        /// </summary>
        public List<VariableReference> References { get; set; } = new List<VariableReference>();

        /// <summary>
        /// 是否有异常
        /// </summary>
        public bool HasAnomalies { get; set; }

        /// <summary>
        /// 异常描述
        /// </summary>
        public string? AnomalyDescription { get; set; }
    }

    /// <summary>
    /// 变量引用位置
    /// </summary>
    public class VariableReference
    {
        /// <summary>
        /// 引用位置（块名、步骤名等）
        /// </summary>
        public string Location { get; set; } = "";

        /// <summary>
        /// 引用类型（Read/Write/ReadWrite）
        /// </summary>
        public string ReferenceType { get; set; } = "";

        /// <summary>
        /// 块类型（FC/FB/OB/Step/Transition等）
        /// </summary>
        public string BlockType { get; set; } = "";

        /// <summary>
        /// 是否有异常
        /// </summary>
        public bool HasAnomaly { get; set; }

        /// <summary>
        /// 异常描述
        /// </summary>
        public string? AnomalyReason { get; set; }

        /// <summary>
        /// 相关ID（StepID/TransitionID/BlockID等）
        /// </summary>
        public long? RelatedId { get; set; }
    }

    /// <summary>
    /// 根因分析
    /// </summary>
    public class RootCauseAnalysis
    {
        /// <summary>
        /// 根本原因变量
        /// </summary>
        public string RootCauseVariable { get; set; } = "";

        /// <summary>
        /// 根因描述
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// 可能的原因列表
        /// </summary>
        public List<PossibleCause> PossibleCauses { get; set; } = new List<PossibleCause>();

        /// <summary>
        /// 建议的解决方案
        /// </summary>
        public List<string> SuggestedSolutions { get; set; } = new List<string>();

        /// <summary>
        /// 置信度（0-1）
        /// </summary>
        public double Confidence { get; set; }
    }

    /// <summary>
    /// 可能的原因
    /// </summary>
    public class PossibleCause
    {
        /// <summary>
        /// 原因描述
        /// </summary>
        public string Cause { get; set; } = "";

        /// <summary>
        /// 相关位置
        /// </summary>
        public string? RelatedLocation { get; set; }

        /// <summary>
        /// 可能性（0-1）
        /// </summary>
        public double Probability { get; set; }
    }
}
