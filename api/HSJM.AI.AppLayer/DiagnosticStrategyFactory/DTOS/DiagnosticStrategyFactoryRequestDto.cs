using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 验证请求模型
    /// </summary>
    public class DiagnosticStrategyFactoryRequestDto
    {
        /// <summary>
        /// 待验证的 PLC 数据负载
        /// 支持多种格式：西门子 S7 地址格式、JSON 格式的标签数据、OPC UA 节点数据等
        /// </summary>
        /// <example>DB1.DBD0:1500.5, M0.0:true, M0.1:false</example>
        public List<string> Payload { get; set; }

        /// <summary>
        /// 最大并发度（默认 5）
        /// 控制同时处理的验证任务数量，避免过度占用资源
        /// </summary>
        /// <example>5</example>
        public int? MaxDegreeOfParallelism { get; init; }
    }
}
