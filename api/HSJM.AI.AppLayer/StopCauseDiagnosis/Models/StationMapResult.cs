using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 工位地图层输出。
    /// </summary>
    public class StationMapResult
    {
        public List<AnchorStationAssignment> AnchorAssignments { get; set; } = new List<AnchorStationAssignment>();

        public List<StationBucket> StationBuckets { get; set; } = new List<StationBucket>();

        public List<StationConflict> StationConflicts { get; set; } = new List<StationConflict>();

        public List<OrphanAnchor> OrphanAnchors { get; set; } = new List<OrphanAnchor>();

        public List<VariablePropagationLink> VariablePropagations { get; set; } = new List<VariablePropagationLink>();
    }
}
