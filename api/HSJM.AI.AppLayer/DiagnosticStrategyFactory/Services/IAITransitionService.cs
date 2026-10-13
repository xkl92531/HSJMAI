using System.Collections.Generic;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// AI转换条件验证服务接口
    /// 使用AI解析和验证任何格式的Transition XML
    /// </summary>
    public interface IAITransitionService
    {
        /// <summary>
        /// 使用AI解析和验证Transition条件
        /// </summary>
        /// <param name="transitionXml">TransitionInfo XML内容</param>
        /// <param name="actualValues">实际变量值</param>
        /// <param name="context">额外上下文信息（可选）</param>
        /// <returns>验证结果</returns>
        Task<AITransitionValidationResult> ValidateWithAIAsync(
            string transitionXml,
            Dictionary<string, string> actualValues,
            string? context = null);

        /// <summary>
        /// 使用AI深度分析TransitionInfo的XML结构
        /// </summary>
        /// <param name="transitionXml">TransitionInfo XML内容</param>
        /// <returns>结构分析结果</returns>
        Task<AIStructureAnalysis> AnalyzeXmlStructureAsync(string transitionXml);
    }

    /// <summary>
    /// AI转换条件验证结果
    /// </summary>
    public class AITransitionValidationResult
    {
        /// <summary>
        /// 验证是否成功执行
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 条件是否通过
        /// </summary>
        public bool IsPassed { get; set; }

        /// <summary>
        /// 置信度分数（0-1）
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// 判断原因和详细说明
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>
        /// AI原始响应
        /// </summary>
        public string? RawResponse { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 使用的AI模型
        /// </summary>
        public string? ModelUsed { get; set; }

        /// <summary>
        /// 处理耗时（毫秒）
        /// </summary>
        public long ProcessingTimeMs { get; set; }
    }
}
