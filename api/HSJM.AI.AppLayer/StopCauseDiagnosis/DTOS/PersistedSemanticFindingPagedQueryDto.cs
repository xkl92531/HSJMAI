namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class PersistedSemanticFindingPagedQueryDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public long? BlockId { get; set; }

        public string Category { get; set; }

        public int Page { get; set; } = 1;

        public int Limit { get; set; } = 100;
    }

    public class PersistedSemanticFindingListItemDto
    {
        public long? LogicUnitId { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public string StationCode { get; set; }

        public string VariableName { get; set; }

        public string Category { get; set; }

        public string SemanticLabel { get; set; }

        public string Reason { get; set; }

        public decimal? Confidence { get; set; }

        public System.Collections.Generic.List<string> RelatedIOTags { get; set; } = new System.Collections.Generic.List<string>();

        public string IoMapping { get; set; }
    }
}