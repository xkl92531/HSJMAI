namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class UnassignedBlocksQueryDto
    {
        public long ProjectId { get; set; }

        public string BlockName { get; set; }

        public string BlockType { get; set; }

        public string ProgrammingLanguage { get; set; }

        public int Page { get; set; } = 1;

        public int Limit { get; set; } = 50;
    }
}
