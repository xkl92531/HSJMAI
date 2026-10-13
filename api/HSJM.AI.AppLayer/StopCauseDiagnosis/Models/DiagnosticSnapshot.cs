using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 停机瞬时快照
    /// </summary>
    public class DiagnosticSnapshot
    {
        public string StationCode { get; set; }

        public DateTime SnapshotTime { get; set; } = DateTime.Now;

        public Dictionary<string, object> CurrentValues { get; set; } = new Dictionary<string, object>();

        public Dictionary<string, object> PreviousValues { get; set; } = new Dictionary<string, object>();

        public Dictionary<string, DateTime?> LastChangeTimes { get; set; } = new Dictionary<string, DateTime?>();

        public List<string> RelatedAnchors { get; set; } = new List<string>();

        public Dictionary<string, string> SignalQuality { get; set; } = new Dictionary<string, string>();

        public List<string> SourceSymbols { get; set; } = new List<string>();

        public List<string> MissingSymbols { get; set; } = new List<string>();
    }
}
