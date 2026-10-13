using System.Collections.Generic;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class EvaluatePersistedAstResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public long LogicUnitId { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public string VariableName { get; set; }

        public string CompileStatus { get; set; }

        public string DeviceName { get; set; }

        public string FallbackDeviceName { get; set; }

        public List<string> InputVariables { get; set; } = new List<string>();

        public List<string> OutputVariables { get; set; } = new List<string>();

        public List<string> RequestedSymbols { get; set; } = new List<string>();

        public List<string> MissingSymbols { get; set; } = new List<string>();

        public bool? EvaluatedValue { get; set; }

        public string EvaluationStatus { get; set; }

        /// <summary>
        /// 综合诊断质量：Diagnosed / DiagnosedPartial / Undetermined。
        /// Diagnosed  — 根节点已确定，且失败路径上无 Unknown/Unsupported 节点。
        /// DiagnosedPartial — 根节点已确定，但失败路径含不可读节点，推断存在不确定性。
        /// Undetermined — 根节点本身为 Unknown/Unsupported，无法给出结论。
        /// </summary>
        public string DiagnosisQuality { get; set; }

        public int UnsupportedNodeCount { get; set; }

        public int UnknownNodeCount { get; set; }

        public StopCauseExecutionContext ExecutionContext { get; set; }

        public DiagnosticSnapshot Snapshot { get; set; }

        public StopCauseAstTraceNode RootEvaluation { get; set; }

        public List<StopCauseAstTraceNode> FailurePath { get; set; } = new List<StopCauseAstTraceNode>();

        public List<string> Messages { get; set; } = new List<string>();
    }
}