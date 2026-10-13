namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 锚点发现状态
    /// </summary>
    public static class AnchorDiscoveryStatus
    {
        public const string Confirmed = "Confirmed";
        public const string Review = "Review";
        public const string Rejected = "Rejected";
    }

    /// <summary>
    /// 锚点评分分项
    /// </summary>
    public class AnchorScoreBreakdown
    {
        public decimal NameScore { get; set; }

        public decimal WriteScore { get; set; }

        public decimal SourceScore { get; set; }

        public decimal FlowScore { get; set; }

        public decimal StationScore { get; set; }

        public decimal ExposureScore { get; set; }

        public decimal TotalScore =>
            NameScore + WriteScore + SourceScore + FlowScore + StationScore + ExposureScore;
    }
}