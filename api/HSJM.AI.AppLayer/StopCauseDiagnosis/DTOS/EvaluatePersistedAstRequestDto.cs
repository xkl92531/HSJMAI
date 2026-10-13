using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    public class EvaluatePersistedAstRequestDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public string StationMode { get; set; }

        public long? LogicUnitId { get; set; }

        public long? BlockId { get; set; }

        public string VariableName { get; set; }

        public bool IncludeStationAnchorVariables { get; set; }

        public List<string> AdditionalSymbolPaths { get; set; } = new List<string>();

        public List<string> RelatedAnchors { get; set; } = new List<string>();

        public Dictionary<string, object> PreviousValues { get; set; } = new Dictionary<string, object>();

        public Dictionary<string, string> PreviousSignalQuality { get; set; } = new Dictionary<string, string>();

        public Dictionary<string, DateTime?> LastChangeTimes { get; set; } = new Dictionary<string, DateTime?>();

        /// <summary>
        /// FB 穿透时的输入形参→外部实参映射表。
        /// Key = FB 形参名（如 IHP），Value = 调用处外部实参变量名（如 OP070_CYL06_H）。
        /// 评估器会自动将外部实参加入信号读取列表，读取后注入 #形参名 → 信号值 到求值上下文中，
        /// 使 FB 内部 AST 可正确解析 # 变量。
        /// </summary>
        public Dictionary<string, string> FbInputParamMap { get; set; }

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