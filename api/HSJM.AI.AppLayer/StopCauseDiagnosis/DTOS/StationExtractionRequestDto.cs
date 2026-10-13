namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 工位提取与标记请求。
    /// 用于导入解析完成后，先独立跑工位归属，再进入 Q 点和 AST 阶段。
    /// </summary>
    public class StationExtractionRequestDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public long? BlockId { get; set; }

        public bool UseLlmAnnotation { get; set; }

        public bool IncludePhysicalTags { get; set; } = true;

        public bool IncludeVariableGroups { get; set; } = true;

        public bool IncludeLogicUnitMarking { get; set; } = true;

        public bool IncludeSourceItems { get; set; }

        public bool PersistStationMarking { get; set; }

        public bool UpdateBlockStationCode { get; set; }

        public int MaxDetailCountPerCategory { get; set; } = 200;
    }
}