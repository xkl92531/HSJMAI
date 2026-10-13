using System;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 一键分析入库进度 DTO
    /// </summary>
    public class FullAnalysisProgressDto
    {
        public string TaskId { get; set; }
        public long ProjectId { get; set; }
        public string StationCode { get; set; }

        /// <summary>Running | Completed | Failed</summary>
        public string Status { get; set; }

        /// <summary>当前步骤编号 (1-based)</summary>
        public int CurrentStep { get; set; }

        /// <summary>总步骤数</summary>
        public int TotalSteps { get; set; }

        /// <summary>当前步骤名称</summary>
        public string StepName { get; set; }

        /// <summary>人类可读消息</summary>
        public string Message { get; set; }

        /// <summary>0-100</summary>
        public int Percentage { get; set; }

        public string ErrorMessage { get; set; }

        public DateTime StartedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        /// <summary>完成后的汇总数据</summary>
        public FullAnalysisSummaryDto Summary { get; set; }
    }

    /// <summary>
    /// 一键分析完成后的汇总数据
    /// </summary>
    public class FullAnalysisSummaryDto
    {
        public int StationCount { get; set; }
        public int AstSeedCount { get; set; }
        public int AstCompileResultCount { get; set; }
        public int AnchorBindingCount { get; set; }
        public int MonitoringSignalCount { get; set; }
    }

    /// <summary>
    /// 启动一键分析的请求
    /// </summary>
    public class StartFullAnalysisRequestDto
    {
        public long ProjectId { get; set; }
        public string StationCode { get; set; }
        public long? BlockId { get; set; }
        public bool UseLlmAnnotation { get; set; }
        public int RawContentMaxLength { get; set; }
    }

    /// <summary>
    /// 启动一键分析的返回
    /// </summary>
    public class StartFullAnalysisResultDto
    {
        public string TaskId { get; set; }
        public string HangfireJobId { get; set; }
        public string HangfireQueueName { get; set; }
        public string Message { get; set; }
    }
}
