using System.Collections.Generic;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 步骤交叉引用诊断请求DTO
    /// </summary>
    public class StepCrossReferenceDiagnosticRequestDto
    {
        /// <summary>
        /// 生产线代码
        /// </summary>
        public string ProductionLineCode { get; set; } = "";

        /// <summary>
        /// 工位代码
        /// </summary>
        public string? WorkstationCode { get; set; }

        /// <summary>
        /// 工步代码
        /// </summary>
        public string? StepCode { get; set; }

        /// <summary>
        /// 动作代码
        /// </summary>
        public string? ActionCode { get; set; }

        /// <summary>
        /// 动作地址
        /// </summary>
        public string? ActionAddress { get; set; }

        /// <summary>
        /// 实际变量值(变量名 -> 值)
        /// 例如:{ "DB1.DBD0": "85.5", "M0.0": "true" }
        /// </summary>
        public Dictionary<string, string> ActualValues { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 卡住状态值（默认为"1"）
        /// 用于从Redis中过滤出卡住的步骤
        /// 可根据实际业务配置为其他值，如 "Stuck"、"Blocked"、"Error" 等
        /// </summary>
        public string StuckStatusValue { get; set; } = "1";

        /// <summary>
        /// 是否包含交叉引用分析(默认true)
        /// </summary>
        public bool IncludeCrossReference { get; set; } = true;

        /// <summary>
        /// 交叉引用搜索深度(默认3层)
        /// </summary>
        public int CrossReferenceDepth { get; set; } = 100;

        /// <summary>
        /// 是否分析根因(默认true)
        /// </summary>
        public bool AnalyzeRootCause { get; set; } = true;
    }
}
