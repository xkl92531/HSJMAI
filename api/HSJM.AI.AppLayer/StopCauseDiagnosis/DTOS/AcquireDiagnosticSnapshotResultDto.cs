using System.Collections.Generic;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 在线采集工位诊断快照结果。
    /// </summary>
    public class AcquireDiagnosticSnapshotResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public string DeviceName { get; set; }

        public string FallbackDeviceName { get; set; }

        public List<string> RequestedSymbols { get; set; } = new List<string>();

        public List<string> MissingSymbols { get; set; } = new List<string>();

        public StopCauseExecutionContext ExecutionContext { get; set; } = new StopCauseExecutionContext();

        public DiagnosticSnapshot Snapshot { get; set; } = new DiagnosticSnapshot();
    }
}