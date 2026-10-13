using System;
using System.Collections.Generic;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// AST 深度追踪测试请求。
    /// 支持两种输入方式：
    ///   1) 直接传 LogicUnitId（最直接）
    ///   2) 传 VariableName / SignalKey（系统自动匹配 AST）
    /// 两种方式都需要 ProjectId。
    /// </summary>
    public class AstDeepTraceTestRequestDto
    {
        /// <summary>
        /// 项目 ID（必填）
        /// </summary>
        public long ProjectId { get; set; }

        /// <summary>
        /// 工位编号（可选，辅助 AST 定位）
        /// </summary>
        public string StationCode { get; set; }

        /// <summary>
        /// 逻辑单元 ID（可选，优先使用）
        /// </summary>
        public long? LogicUnitId { get; set; }

        /// <summary>
        /// 信号/变量名（可选，当未传 LogicUnitId 时用此名称搜索匹配的 AST）
        /// </summary>
        public string VariableName { get; set; }

        /// <summary>
        /// 最大追踪深度（默认 10，范围 1-20）
        /// </summary>
        public int MaxTraceDepth { get; set; } = 10;

        /// <summary>
        /// 是否包含信号变化历史分析（默认 true）
        /// </summary>
        public bool IncludeSignalHistory { get; set; } = true;

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

    /// <summary>
    /// AST 深度追踪测试结果 — 一次接口调用返回完整的 AST 求值 + 失败路径追踪 + 信号历史分析。
    /// </summary>
    public class AstDeepTraceTestResultDto
    {
        // ── 元信息 ──
        public long ProjectId { get; set; }
        public string StationCode { get; set; }
        public long LogicUnitId { get; set; }
        public string BlockName { get; set; }
        public string VariableName { get; set; }
        public string UnitType { get; set; }
        public string CompileStatus { get; set; }

        // ── AST 求值结果 ──
        /// <summary>
        /// 根节点求值结果：true / false / null
        /// </summary>
        public bool? EvaluatedValue { get; set; }
        public string EvaluationStatus { get; set; }
        public string DiagnosisQuality { get; set; }
        public int UnsupportedNodeCount { get; set; }
        public int UnknownNodeCount { get; set; }

        /// <summary>
        /// 输入变量列表（AST 依赖的 PLC 变量）
        /// </summary>
        public List<string> InputVariables { get; set; } = new List<string>();

        /// <summary>
        /// 输出变量列表（AST 控制/写入的 PLC 变量）
        /// </summary>
        public List<string> OutputVariables { get; set; } = new List<string>();

        /// <summary>
        /// 缺失符号（PLC 快照未读到的变量）
        /// </summary>
        public List<string> MissingSymbols { get; set; } = new List<string>();

        // ── 完整求值树 ──
        /// <summary>
        /// AST 求值树根节点（完整树结构，包含每个节点的实际值和求值结果）
        /// </summary>
        public StopCauseAstTraceNode RootEvaluation { get; set; }

        /// <summary>
        /// 首条失败路径（从根节点到第一个 FALSE 叶节点的路径）
        /// </summary>
        public List<StopCauseAstTraceNode> FailurePath { get; set; } = new List<StopCauseAstTraceNode>();

        // ── 深度追踪结果 ──
        /// <summary>
        /// 是否为 GRAPH StepAction 类型
        /// </summary>
        public bool IsGraphAction { get; set; }

        /// <summary>
        /// GRAPH 转移条件分析（仅当 IsGraphAction = true 且 EvaluatedValue = true 时有值）
        /// </summary>
        public List<StationAnchorFinding> TransitionFindings { get; set; } = new List<StationAnchorFinding>();

        /// <summary>
        /// 跨 LogicUnit 写入源深度追踪 Findings（核心追踪能力验证）
        /// </summary>
        public List<StationAnchorFinding> DeepTraceFindings { get; set; } = new List<StationAnchorFinding>();

        /// <summary>
        /// 追踪过程中访问过的变量（用于理解追踪路径）
        /// </summary>
        public List<string> VisitedVariables { get; set; } = new List<string>();

        /// <summary>
        /// 实际达到的最大追踪深度
        /// </summary>
        public int ActualMaxDepth { get; set; }

        // ── 信号历史 ──
        /// <summary>
        /// 失败路径上的信号变化历史时序分析
        /// </summary>
        public string SignalHistoryAnalysis { get; set; }

        // ── 诊断总结 ──
        /// <summary>
        /// 汇总诊断文本（自动生成的人可读结论）
        /// </summary>
        public string DiagnosisSummary { get; set; }

        /// <summary>
        /// 总耗时(ms)
        /// </summary>
        public long ElapsedMs { get; set; }

        /// <summary>
        /// 各阶段耗时明细
        /// </summary>
        public Dictionary<string, long> TimingBreakdown { get; set; } = new Dictionary<string, long>();

        /// <summary>
        /// 处理过程中的消息/日志
        /// </summary>
        public List<string> Messages { get; set; } = new List<string>();

        /// <summary>
        /// 输入解析方式说明（例如 "通过 LogicUnitId 直接定位" / "通过 VariableName 匹配 AST 输出"）
        /// </summary>
        public string InputResolution { get; set; }
    }
}
