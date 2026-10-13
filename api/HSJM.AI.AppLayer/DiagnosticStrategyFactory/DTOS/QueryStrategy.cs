using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// AI 生成的数据库查询策略
    /// </summary>
    public class QueryStrategy
    {
        /// <summary>
        /// 查询范围
        /// - "same_step": 查询同一 Step 下的所有 LogicUnit
        /// - "same_block": 查询同一 Block 下的所有 LogicUnit
        /// - "all_project": 查询整个项目（谨慎使用）
        /// </summary>
        [JsonPropertyName("searchScope")]
        public string SearchScope { get; set; }

        /// <summary>
        /// 选择该范围的原因（AI 的解释）
        /// </summary>
        [JsonPropertyName("scopeReason")]
        public string ScopeReason { get; set; }

        /// <summary>
        /// 需要搜索的变量名称列表
        /// </summary>
        [JsonPropertyName("searchVariables")]
        public List<string> SearchVariables { get; set; }

        /// <summary>
        /// 是否需要位置信息（Step/Block 名称）
        /// </summary>
        [JsonPropertyName("needsLocationInfo")]
        public bool NeedsLocationInfo { get; set; }

        /// <summary>
        /// 分析策略描述（AI 如何分析交叉引用）
        /// </summary>
        [JsonPropertyName("analysisStrategy")]
        public string AnalysisStrategy { get; set; }
    }
}
