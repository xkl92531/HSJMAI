using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 在线采集工位诊断快照请求。
    /// </summary>
    public class AcquireDiagnosticSnapshotRequestDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public string StationMode { get; set; }

        public bool IncludeStationAnchorVariables { get; set; } = true;

        public List<string> SymbolPaths { get; set; } = new List<string>();

        public List<string> RelatedAnchors { get; set; } = new List<string>();

        public Dictionary<string, object> PreviousValues { get; set; } = new Dictionary<string, object>();

        public Dictionary<string, string> PreviousSignalQuality { get; set; } = new Dictionary<string, string>();

        public Dictionary<string, System.DateTime?> LastChangeTimes { get; set; } = new Dictionary<string, System.DateTime?>();

        /// <summary>
        /// 预计算的 AnchorType 映射（symbolPath → anchorType），由调用方传入可跳过 DB 查询。
        /// </summary>
        public Dictionary<string, string> AnchorTypeMap { get; set; }

        /// <summary>
        /// 是否使用模拟数据替代真实 PLC 读取。用于无设备连接时的离线诊断测试。
        /// </summary>
        public bool UseSimulation { get; set; }

        /// <summary>
        /// 模拟信号值。Key = 变量全路径，Value = 当前值。
        /// 仅当 UseSimulation = true 时生效。
        /// </summary>
        public Dictionary<string, object> SimulatedCurrentValues { get; set; } = new Dictionary<string, object>();
    }
}