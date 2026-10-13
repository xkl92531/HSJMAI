namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class PersistedWriterAstQueryDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public long? BlockId { get; set; }

        public string LogicUnitIds { get; set; }

        public int Limit { get; set; } = 100;
    }
}
