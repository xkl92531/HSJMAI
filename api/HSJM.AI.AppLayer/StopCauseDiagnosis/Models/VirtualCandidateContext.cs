using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 虚拟 Q 点候选的富上下文，供 LLM 主分类使用。
    /// 故意不依赖变量命名关键词，由 LLM 从结构证据和上下文中推断语义。
    /// </summary>
    public class VirtualCandidateContext
    {
        /// <summary>所属项目ID（供 AST 上下文查询使用）</summary>
        public long ProjectId { get; set; }

        /// <summary>原始变量名，可能不规范</summary>
        public string VariableName { get; set; }

        /// <summary>变量注释（如有）</summary>
        public string Comment { get; set; }

        /// <summary>写入该变量的指令类型列表，如 COIL、SET、STEP_ACTION</summary>
        public List<string> WriteInstructionTypes { get; set; } = new List<string>();

        /// <summary>写入该变量所在的块名列表</summary>
        public List<string> BlockNames { get; set; } = new List<string>();

        /// <summary>写入该变量所在的网络标题列表（来自 LogicUnit.Title）</summary>
        public List<string> NetworkTitles { get; set; } = new List<string>();

        /// <summary>同一网络内同时被写入的其他变量（Co-location 证据）</summary>
        public List<string> CoLocatedWriteVariables { get; set; } = new List<string>();

        /// <summary>是否来自 GRAPH Step Action 写入</summary>
        public bool HasGraphWrite { get; set; }

        /// <summary>是否来自 LAD Coil / Set / Reset 写入</summary>
        public bool HasLadCoilWrite { get; set; }

        public int WriteCount { get; set; }

        public int ReadCount { get; set; }

        public string StationCode { get; set; }

        public decimal StationConfidence { get; set; }

        public List<string> StationEvidence { get; set; } = new List<string>();

        public string LogicSourceType { get; set; }
    }
}
