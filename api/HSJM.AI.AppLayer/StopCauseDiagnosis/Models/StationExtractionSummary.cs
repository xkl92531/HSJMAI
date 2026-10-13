namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 工位提取阶段的汇总统计。
    /// </summary>
    public class StationExtractionSummary
    {
        public int StationCount { get; set; }

        public int PhysicalTagCount { get; set; }

        public int VariableGroupCount { get; set; }

        public int TaggedPhysicalTagCount { get; set; }

        public int TaggedVariableGroupCount { get; set; }

        public int UnassignedPhysicalTagCount { get; set; }

        public int UnassignedVariableGroupCount { get; set; }

        public int PathGroupLlmCount { get; set; }

        public int TableGroupLlmCount { get; set; }

        public int SingleItemLlmCount { get; set; }

        public int RuleInferenceCount { get; set; }

        public int ColoredLogicUnitCount { get; set; }

        public int ConflictLogicUnitCount { get; set; }
    }
}