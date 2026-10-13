using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 验证结果数据传输对象
    /// </summary>
    public class DiagnosticStrategyFactoryResponseDto
    {
        /// <summary>
        /// 总数据数量
        /// </summary>
        public int TotalCount { get; init; }

        /// <summary>
        /// 验证成功的数量（不含验证失败和接口调用失败）
        /// </summary>
        public int SuccessCount { get; init; }

        /// <summary>
        /// 验证失败的数量（接口调用失败或异常）
        /// </summary>
        public int FailureCount { get; init; }

        /// <summary>
        /// 通过验证的数量（IsValid = true）
        /// </summary>
        public int PassedCount { get; init; }

        /// <summary>
        /// 未通过验证的数量（IsValid = false）
        /// </summary>
        public int NotPassedCount { get; init; }

        /// <summary>
        /// 成功率（成功数 / 总数）
        /// </summary>
        public double SuccessRate { get; init; }

        /// <summary>
        /// 平均验证得分（0-1）
        /// </summary>
        public double AverageScore { get; init; }

        /// <summary>
        /// 总耗时（秒）
        /// </summary>
        public double TotalDurationSeconds { get; init; }

        /// <summary>
        /// 验证成功且通过的结果列表
        /// </summary>
        public List<ValidationResultDto> SuccessResults { get; init; } = new();

        /// <summary>
        /// 验证失败的结果列表（接口调用失败）
        /// </summary>
        public List<ValidationResultDto> FailedResults { get; init; } = new();

        /// <summary>
        /// 验证成功但未通过的结果列表（IsValid = false）
        /// </summary>
        public List<ValidationResultDto> NotPassedResults { get; init; } = new();
    }


    /// <summary>
    /// 数据的验证结果数据传输对象
    /// </summary>
    public record ValidationResultDto
    {
        /// <summary>
        /// 数据标识或文件路径
        /// </summary>
        public string FilePath { get; init; } = string.Empty;

        /// <summary>
        /// AI 模型响应结果
        /// </summary>
        public AiModelResponse? Response { get; init; }

        /// <summary>
        /// 验证是否成功执行（不代表数据是否合法）
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// 错误消息（仅在 Success = false 时有值）
        /// </summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// AI 模型验证响应记录
    /// </summary>
    /// <param name="IsValid">数据是否有效/合法（AI 判定结果）</param>
    /// <param name="Score">验证得分（0-1，越高表示数据质量越好）</param>
    /// <param name="Reason">验证原因或说明（AI 给出的解释）</param>
    /// <param name="Parsed">解析后的结构化数据（可选，如果 AI 能够解析数据内容）</param>
    /// <param name="RawResponse">AI 返回的原始响应文本（用于调试和审计）</param>
    public record AiModelResponse(bool IsValid, double Score, string Reason, ParsedInsertPayload? Parsed, string RawResponse);

    /// <summary>
    /// 解析后的插入数据负载
    /// 用于存储 AI 从数据中提取的结构化信息
    /// </summary>
    public record ParsedInsertPayload
    {
        /// <summary>
        /// 项目名称
        /// </summary>
        public string? ProjectName { get; init; }

        /// <summary>
        /// 行号/线路 ID
        /// </summary>
        public int? LineId { get; init; }

        /// <summary>
        /// 交叉引用列表（数据中引用的其他对象）
        /// </summary>
        public List<CrossReference>? CrossReferences { get; init; }
    }

    /// <summary>
    /// 交叉引用记录
    /// 表示数据中引用的其他对象或实体
    /// </summary>
    public record CrossReference
    {
        /// <summary>
        /// 引用对象的名称
        /// </summary>
        public string? Name { get; init; }

        /// <summary>
        /// 引用对象的类型名称
        /// </summary>
        public string? TypeName { get; init; }
    }
}
