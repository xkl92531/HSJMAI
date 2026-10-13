using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class PersistedWriterAstDetailDto
    {
        public long LogicUnitId { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public string StationCode { get; set; }

        public string UnitType { get; set; }

        public int? NetworkId { get; set; }

        public string Title { get; set; }

        public string Comment { get; set; }

        public string ProgrammingLanguage { get; set; }

        public int? ReadVarsCount { get; set; }

        public int? WriteVarsCount { get; set; }

        public int? CallsCount { get; set; }

        public string CompilerName { get; set; }

        public string CompileStatus { get; set; }

        public string AstVersion { get; set; }

        public string SourceHash { get; set; }

        public string RootNodeJson { get; set; }

        public string CoverageReportsJson { get; set; }

        public List<string> Messages { get; set; } = new List<string>();

        public List<string> InputVariables { get; set; } = new List<string>();

        public List<string> OutputVariables { get; set; } = new List<string>();

        public string RawContentExcerpt { get; set; }

        public List<PersistedSemanticFindingDto> SemanticFindings { get; set; } = new List<PersistedSemanticFindingDto>();
    }

    public class PersistedSemanticFindingDto
    {
        public long? LogicUnitId { get; set; }

        public string VariableName { get; set; }

        public string Category { get; set; }

        public string SemanticLabel { get; set; }

        public string Reason { get; set; }

        public decimal? Confidence { get; set; }

        public List<string> RelatedIOTags { get; set; } = new List<string>();

        public string IoMapping { get; set; }
    }
}