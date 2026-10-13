using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// AI 能量流诊断结果解析用DTO
    /// 用于解析AI返回的JSON响应
    /// </summary>
    public class EnergyFlowDiagnosticResultDto
    {
        /// <summary>
        /// 异常列表
        /// </summary>
        [JsonPropertyName("issues")]
        public List<DiagnosticIssue> Issues { get; set; }

        /// <summary>
        /// 白话文Markdown格式诊断报告
        /// </summary>
        [JsonPropertyName("simpleReport")]
        public string SimpleReport { get; set; }
    }
}
