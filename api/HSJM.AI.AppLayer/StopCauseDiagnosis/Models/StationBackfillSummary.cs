namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 工位结果反向回填摘要。
    /// </summary>
    public class StationBackfillSummary
    {
        public bool PersistedBindings { get; set; }

        public bool UpdatedBlockStationCode { get; set; }

        public int CandidateBlockCount { get; set; }

        public int UpdatedBlockCount { get; set; }

        public int LogicUnitBindingCount { get; set; }
    }
}