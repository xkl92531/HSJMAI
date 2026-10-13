using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class AstPlainExplainRequestDto
    {
        public long LogicUnitId { get; set; }

        public bool ExpandCalls { get; set; } = true;

        public int MaxCallDepth { get; set; } = 3;

        public int MaxExpandedUnitsPerCall { get; set; } = 80;

        public bool IncludeTree { get; set; } = true;

        public bool IncludeRawAstJson { get; set; } = false;
    }

    public class AstPlainExplainResultDto
    {
        public long LogicUnitId { get; set; }

        public bool ExpandCalls { get; set; }

        public int MaxCallDepth { get; set; }

        public AstPlainExplainUnitDto Root { get; set; }

        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class AstPlainExplainUnitDto
    {
        public long LogicUnitId { get; set; }

        public long? ProjectId { get; set; }

        public string StationCode { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public string UnitType { get; set; }

        public int? NetworkId { get; set; }

        public string Title { get; set; }

        public string Comment { get; set; }

        public string ProgrammingLanguage { get; set; }

        public string CompileStatus { get; set; }

        public List<string> InputVariables { get; set; } = new List<string>();

        public List<string> OutputVariables { get; set; } = new List<string>();

        public string Expression { get; set; }

        public string PlainText { get; set; }

        public List<string> TreeLines { get; set; } = new List<string>();

        public int NodeCount { get; set; }

        public int MaxDepth { get; set; }

        public string RawAstJson { get; set; }

        public List<AstPlainExplainCallDto> Calls { get; set; } = new List<AstPlainExplainCallDto>();

        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class AstPlainExplainCallDto
    {
        public string CalleeName { get; set; }

        public string CalleeBlockType { get; set; }

        public string InstanceDb { get; set; }

        public string CallUid { get; set; }

        public Dictionary<string, string> InputPins { get; set; } = new Dictionary<string, string>();

        public Dictionary<string, string> OutputPins { get; set; } = new Dictionary<string, string>();

        public Dictionary<string, string> InOutPins { get; set; } = new Dictionary<string, string>();

        public Dictionary<string, string> SubstitutionMap { get; set; } = new Dictionary<string, string>();

        public List<string> RelevantFormalPins { get; set; } = new List<string>();

        public List<AstPlainExplainUnitDto> ExpandedUnits { get; set; } = new List<AstPlainExplainUnitDto>();

        public List<string> Warnings { get; set; } = new List<string>();
    }
}
