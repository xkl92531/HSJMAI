using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 按工位分段批量运行的请求，继承所有单次参数。
    /// </summary>
    public class BatchDiscoverRequestDto : AnchorDiscoveryRequestDto
    {
        /// <summary>
        /// 指定要运行的工位列表，如 ["OP010","OP020","OP030"]。
        /// 留空则自动枚举项目内所有工位后逐一运行。
        /// </summary>
        public List<string> StationCodes { get; set; }

        /// <summary>
        /// 工位级并发数，建议 2-3，过高会触发 LLM 速率限制。默认 2。
        /// </summary>
        public int MaxParallelStations { get; set; } = 2;
    }
}
