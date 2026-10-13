namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class PersistedWriterAstPagedQueryDto : PersistedWriterAstQueryDto
    {
        public string BlockName { get; set; }

        public string VariableName { get; set; }

        public string CompileStatus { get; set; }

        public int Page { get; set; } = 1;

        public int Limit { get; set; } = 20;
    }
}