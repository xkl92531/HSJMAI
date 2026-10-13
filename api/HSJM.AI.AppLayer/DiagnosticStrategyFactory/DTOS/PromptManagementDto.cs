using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 提示词模板类型枚举
    /// </summary>
    public enum PromptTemplateType
    {
        /// <summary>
        /// 能量流诊断
        /// </summary>
        EnergyFlowDiagnostic = 1,

        /// <summary>
        /// 查询策略生成
        /// </summary>
        QueryStrategy = 2,

        /// <summary>
        /// 交叉引用分析
        /// </summary>
        CrossReference = 3,

        /// <summary>
        /// 摘要报告生成
        /// </summary>
        SummaryReport = 4,

        /// <summary>
        /// 诊断策略决策（判断需要执行哪些检查步骤）
        /// </summary>
        StrategyDecision = 5
    }

    /// <summary>
    /// 提示词模板
    /// </summary>
    public class PromptTemplateDto
    {
        /// <summary>
        /// 模板类型
        /// </summary>
        public PromptTemplateType TemplateType { get; set; }

        /// <summary>
        /// 模板名称
        /// </summary>
        public string TemplateName { get; set; }

        /// <summary>
        /// 模板文件路径
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// 模板内容
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// 变量占位符
        /// </summary>
        public Dictionary<string, string> Placeholders { get; set; }
    }

    /// <summary>
    /// 提示词优化请求
    /// </summary>
    public class PromptOptimizationRequestDto
    {
        /// <summary>
        /// 模板类型
        /// </summary>
        public PromptTemplateType TemplateType { get; set; }

        /// <summary>
        /// 原始提示词
        /// </summary>
        public string OriginalPrompt { get; set; }

        /// <summary>
        /// 优化说明（可选）
        /// </summary>
        public string OptimizationNotes { get; set; }

        /// <summary>
        /// 测试数据（用于验证）
        /// </summary>
        public string TestData { get; set; }

        /// <summary>
        /// 预期输出格式（用于验证）
        /// </summary>
        public string ExpectedOutputFormat { get; set; }
    }

    /// <summary>
    /// 提示词优化结果
    /// </summary>
    public class PromptOptimizationResultDto
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 优化后的提示词
        /// </summary>
        public string OptimizedPrompt { get; set; }

        /// <summary>
        /// 优化说明
        /// </summary>
        public string OptimizationExplanation { get; set; }

        /// <summary>
        /// 验证结果
        /// </summary>
        public PromptValidationResultDto ValidationResult { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// 提示词验证请求
    /// </summary>
    public class PromptValidationRequestDto
    {
        /// <summary>
        /// 提示词
        /// </summary>
        public string Prompt { get; set; }

        /// <summary>
        /// 测试数据
        /// </summary>
        public string TestData { get; set; }

        /// <summary>
        /// 预期输出格式（JSON Schema 或示例）
        /// </summary>
        public string ExpectedOutputFormat { get; set; }

        /// <summary>
        /// AI 响应结果
        /// </summary>
        public string AIResponse { get; set; }
    }

    /// <summary>
    /// 提示词验证结果
    /// </summary>
    public class PromptValidationResultDto
    {
        /// <summary>
        /// 是否通过验证
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// 验证得分（0-100）
        /// </summary>
        public int Score { get; set; }

        /// <summary>
        /// 验证详情
        /// </summary>
        public List<ValidationIssue> Issues { get; set; }

        /// <summary>
        /// AI 评估说明
        /// </summary>
        public string Evaluation { get; set; }

        /// <summary>
        /// 建议的改进方向
        /// </summary>
        public string SuggestedImprovements { get; set; }
    }

    /// <summary>
    /// 验证问题
    /// </summary>
    public class ValidationIssue
    {
        /// <summary>
        /// 问题类型
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// 问题描述
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 严重程度
        /// </summary>
        public string Severity { get; set; }

        /// <summary>
        /// 位置（行号等）
        /// </summary>
        public string Location { get; set; }
    }

    /// <summary>
    /// 提示词版本记录
    /// </summary>
    public class PromptVersionRecordDto
    {
        /// <summary>
        /// 版本号
        /// </summary>
        public int Version { get; set; }

        /// <summary>
        /// 模板类型
        /// </summary>
        public PromptTemplateType TemplateType { get; set; }

        /// <summary>
        /// 提示词内容
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// 是否为优化后的版本
        /// </summary>
        public bool IsOptimized { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedTime { get; set; }

        /// <summary>
        /// 创建人
        /// </summary>
        public string CreatedBy { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        public string Remarks { get; set; }
    }
}
