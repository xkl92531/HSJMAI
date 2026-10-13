using System.Collections.Generic;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class RecompileLogicUnitAstRequestDto
    {
        public long ProjectId { get; set; }

        public long LogicUnitId { get; set; }

        public string StationCode { get; set; }

        public int RawContentMaxLength { get; set; } = 4000;

        public bool Persist { get; set; } = true;

        public bool IncludeRootNode { get; set; } = true;

        public bool IncludeRootNodeJson { get; set; }
    }

    public class RecompileLogicUnitAstResultDto
    {
        public long ProjectId { get; set; }

        public long LogicUnitId { get; set; }

        public string StationCode { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public string UnitType { get; set; }

        public int? NetworkId { get; set; }

        public string Title { get; set; }

        public string ProgrammingLanguage { get; set; }

        public string AnchorVariable { get; set; }

        public string CompileStatus { get; set; }

        public string CompilerName { get; set; }

        public List<string> InputVariables { get; set; } = new List<string>();

        public List<string> OutputVariables { get; set; } = new List<string>();

        public int NodeCount { get; set; }

        public int MaxDepth { get; set; }

        public bool Persisted { get; set; }

        public int PersistedAstRows { get; set; }

        public StopCauseAstNode RootNode { get; set; }

        public string RootNodeJson { get; set; }

        public List<string> Messages { get; set; } = new List<string>();

        public List<string> Warnings { get; set; } = new List<string>();
    }
}
