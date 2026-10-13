using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Models
{
    /// <summary>
    /// 物理 IOTag 分组上下文，供 LLM 批量判定工位使用。
    /// </summary>
    public class PhysicalTagGroupContext
    {
        public string GroupKey { get; set; }

        public string GroupType { get; set; }

        public string GroupPath { get; set; }

        public string GroupTableName { get; set; }

        public int ItemCount { get; set; }

        public List<string> SampleTagNames { get; set; } = new List<string>();

        public List<string> SampleComments { get; set; } = new List<string>();

        public List<string> SampleEquipmentIds { get; set; } = new List<string>();
    }
}