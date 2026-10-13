using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 在线求值上下文
    /// </summary>
    public class StopCauseExecutionContext
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public string StationMode { get; set; }

        public Dictionary<string, object> CurrentValues { get; set; } = new Dictionary<string, object>();

        public Dictionary<string, object> PreviousValues { get; set; } = new Dictionary<string, object>();

        public Dictionary<string, string> SignalQuality { get; set; } = new Dictionary<string, string>();

        public Dictionary<string, string> PreviousSignalQuality { get; set; } = new Dictionary<string, string>();

        public List<string> SourceSymbols { get; set; } = new List<string>();

        /// <summary>
        /// GRAPH Transition AST 预加载缓存。Key = NetworkId（Transition编号），Value = 已反序列化的 AST 节点。
        /// 由 EvaluateAsync 在求值前填充，供 EvaluateOutputNode 对 StepAction 的出口转移条件做同步求值。
        /// </summary>
        public Dictionary<int, StopCauseAstNode> GraphTransitionAstCache { get; set; } = new Dictionary<int, StopCauseAstNode>();

        /// <summary>
        /// FB 穿透追踪最大层深（L1 = 第一层 FB 穿透，L2/L3... 依此类推）。
        /// 默认 4。可通过 LineDiagnosisRequestDto.MaxFbPenetrationDepth 在 API 层设置。
        /// </summary>
        public int MaxFbPenetrationDepth { get; set; } = 4;
    }
}
