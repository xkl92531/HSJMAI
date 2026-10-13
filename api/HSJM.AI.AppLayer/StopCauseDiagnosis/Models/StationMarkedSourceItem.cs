using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 工位提取阶段对原始来源项的标记结果。
    /// </summary>
    public class StationMarkedSourceItem
    {
        public string SourceKey { get; set; }

        public string SourceType { get; set; }

        public string VariableName { get; set; }

        public string Address { get; set; }

        public string Path { get; set; }

        public string TableName { get; set; }

        public string EquipmentId { get; set; }

        public string StationCode { get; set; }

        public decimal Confidence { get; set; }

        public string StationSource { get; set; }

        public string StationSourceDetail { get; set; }

        public int ReadCount { get; set; }

        public int WriteCount { get; set; }

        public int LogicUnitCount { get; set; }

        public int BlockCount { get; set; }

        public List<string> Evidence { get; set; } = new List<string>();
    }
}