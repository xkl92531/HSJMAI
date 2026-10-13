using System;
using System.Collections.Generic;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 工位提取与标记结果。
    /// </summary>
    public class StationExtractionResultDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        public StationExtractionSummary Summary { get; set; } = new StationExtractionSummary();

        public List<StationExtractionBucket> StationBuckets { get; set; } = new List<StationExtractionBucket>();

        public List<LogicUnitStationLabel> StationColoringResult { get; set; } = new List<LogicUnitStationLabel>();

        public List<StationMarkedSourceItem> PhysicalTagItems { get; set; } = new List<StationMarkedSourceItem>();

        public List<StationMarkedSourceItem> VariableGroupItems { get; set; } = new List<StationMarkedSourceItem>();

        public StationBackfillSummary BackfillSummary { get; set; } = new StationBackfillSummary();

        public List<string> Warnings { get; set; } = new List<string>();
    }
}