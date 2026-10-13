using System;
using System.Collections.Generic;
using HSJM.AI.AppLayer.Agent.Services;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 全线停机诊断请求
    /// </summary>
    public class LineDiagnosisRequestDto
    {
        /// <summary>
        /// 可选：指定要排查的 Project 列表。为空时自动获取系统中所有 Project。
        /// （一条产线可能对应多个 PLC / 多个 Project）
        /// </summary>
        public List<long> ProjectIds { get; set; } = new List<long>();

        /// <summary>
        /// 可选：仅排查指定工位列表。为空时自动获取所有工位。
        /// </summary>
        public List<string> StationCodes { get; set; } = new List<string>();

        /// <summary>
        /// 最大并行工位数，默认 3
        /// </summary>
        public int MaxParallelStations { get; set; } = 3;

        /// <summary>
        /// 每工位最多求值的锚点数，默认 500（全量计算）。
        /// 已使用批量 PLC 读取优化，100 个锚点与 10 个的 PLC 读取次数相同（均为 1 次）。
        /// </summary>
        public int MaxAnchorsPerStation { get; set; } = 500;

        /// <summary>
        /// 是否调用 LLM 做跨工位根因分析
        /// </summary>
        public bool UseLlmAnalysis { get; set; } = true;

        /// <summary>
        /// 面向用户的诊断输出语言。默认 zh-CN，可传 en-US 用于英文演示。
        /// 仅影响 AI 总结、工位级 LLM 摘要和前端请求关联的文案输出，不改变字段名/枚举值。
        /// </summary>
        public string OutputLanguage { get; set; } = "zh-CN";

        public bool ForceLlmSummaryWhenNoAbnormal { get; set; } = false;

        /// <summary>
        /// 是否使用 Agent 驱动的诊断（ReAct 循环 + 工具调用）。
        /// true = Agent 模式（LLM 自主探查），false = 传统 LLM 摘要模式。
        /// 仅在 UseLlmAnalysis=true 时生效。
        /// </summary>
        public bool UseAgentDiagnosis { get; set; } = false;

        /// <summary>
        /// Agent 模式最大迭代次数，默认 12
        /// </summary>
        public int AgentMaxIterations { get; set; } = 12;

        /// <summary>
        /// 是否启用工位独立 LLM 诊断（每个异常工位单独做一轮轻量级 LLM 分析，结果写入 StationDiagnosisSummaryDto.LlmSummary）。
        /// 默认 false。
        /// </summary>
        public bool EnablePerStationLlm { get; set; } = false;

        /// <summary>
        /// 全局超时(秒)。默认 300（无 Agent）/ 600（Agent 模式）。0 表示不限制。
        /// </summary>
        public int TimeoutSeconds { get; set; } = 0;

        /// <summary>
        /// 可选：前端预先生成的 TaskId，用于 SignalR 订阅。为空时后端自动生成。
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// 是否仅处理 GRAPH 类工位。默认 true。
        /// true：无激活 GRAPH 步骤的工位直接跳过（视为正常），不做 IO 信号后继推导。
        /// false：无 GRAPH 时回退到 RecentIoTagBased（以最近激活的 IO 信号为种子推导）。
        /// </summary>
        public bool GraphOnlyMode { get; set; } = true;

        /// <summary>
        /// 是否在一键停机诊断中启用设备关注信号检查。默认 false；开启后作为 GRAPH/IO 之外的增强链路。
        /// </summary>
        public bool EnableDeviceFocusCheck { get; set; } = false;

        /// <summary>
        /// Device focus anchor types enabled for line diagnosis.
        /// Empty keeps backward compatibility and checks DeviceHoldState only.
        /// Supported values: DeviceHoldState, DevicePermitState, DeviceMotionFeedback, DeviceAlarmState.
        /// </summary>
        public List<string> DeviceFocusAnchorTypes { get; set; } = new List<string>();

        /// <summary>
        /// 当前 GRAPH 步骤刚激活后的诊断宽限期（秒）。默认 8 秒，0 表示关闭。
        /// 宽限期内视为工位正在推进，避免刚切步时后继条件尚未到位而误判为故障。
        /// </summary>
        public int ActiveGraphGraceSeconds { get; set; } = 8;

        /// <summary>
        /// 是否同时运行 IO 约束检查（互斥/蕴含/时序等规则），将约束违反结果纳入 LLM 根因分析。
        /// 默认 false。开启后会对当前所有工位信号快照做约束规则校验，
        /// 并把违反列表追加到 LLM 汇总 Prompt 中作为参考依据。
        /// </summary>
        public bool IncludeConstraintAnalysis { get; set; } = false;

        /// <summary>
        /// FB 穿透追踪最大层深。默认 4（L1~L4）。
        /// 增大此值可追踪更深的 FB 嵌套调用链（如 L5/L6），但会增加诊断耗时。
        /// </summary>
        public int MaxFbPenetrationDepth { get; set; } = 4;
    }

    /// <summary>
    /// 设备关注信号独立诊断请求。
    /// </summary>
    public class DeviceFocusDiagnosisRequestDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public int MaxCandidates { get; set; } = 20;

        public int MaxFbPenetrationDepth { get; set; } = 4;

        /// <summary>
        /// 历史开关。默认只分析保持/互锁类设备关注信号；报警类需通过 AnchorTypes 显式指定。
        /// </summary>
        public bool AlarmFaultOnly { get; set; } = true;

        /// <summary>
        /// 可选：指定要分析的设备关注信号类型，例如 DeviceAlarmState、DeviceHoldState。
        /// 为空时 AlarmFaultOnly=true 只分析 DeviceHoldState；AlarmFaultOnly=false 分析除报警/上下文外的诊断类型。
        /// </summary>
        public List<string> AnchorTypes { get; set; } = new List<string>();

        /// <summary>
        /// 返回的关键发现上限，避免深度追踪展开过多内容。
        /// </summary>
        public int MaxFindings { get; set; } = 50;
    }

    /// <summary>
    /// 全线停机诊断结果
    /// </summary>
    public class AgentLineDiagnosisRequestDto : LineDiagnosisRequestDto
    {
        public long? ProjectId { get; set; }

        public string StationCode { get; set; }

        public string Mode { get; set; }

        public string OutputMode { get; set; } = "FinalNodesOnly";

        public string LlmMode { get; set; }

        public bool IncludeProcess { get; set; }

        public bool IncludeRawResult { get; set; }

        public int MaxFinalNodes { get; set; } = 30;
    }

    public class AgentLineDiagnosisResponseDto
    {
        public string TaskId { get; set; }

        public string OutputMode { get; set; }

        public string LlmMode { get; set; }

        public string OutputLanguage { get; set; }

        public DateTime DiagnosedAt { get; set; }

        public long ElapsedMs { get; set; }

        public int TotalStations { get; set; }

        public int DiagnosedStations { get; set; }

        public int AbnormalStations { get; set; }

        public bool TimedOut { get; set; }

        public LineDiagnosisLlmReport Summary { get; set; }

        public List<AgentLineDiagnosisFinalNodeDto> FinalNodes { get; set; } = new List<AgentLineDiagnosisFinalNodeDto>();

        public List<AgentLineDiagnosisStationStatusDto> StationStatuses { get; set; } = new List<AgentLineDiagnosisStationStatusDto>();

        public List<LineDiagnosisProgressEventDto> Events { get; set; } = new List<LineDiagnosisProgressEventDto>();

        public LineDiagnosisResultDto RawResult { get; set; }

        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class AgentLineDiagnosisStationStatusDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public int StationOrder { get; set; }

        public string OverallStatus { get; set; }

        public string DiagnosisStrategy { get; set; }

        public int AnchorCount { get; set; }

        public int EvaluatedCount { get; set; }

        public int FalseAnchorCount { get; set; }

        public int FindingCount { get; set; }

        public long ElapsedMs { get; set; }

        public string TimingBreakdown { get; set; }

        public string TemplateMatchInfo { get; set; }

        public string ErrorMessage { get; set; }
    }

    public class AgentLineDiagnosisFinalNodeDto
    {
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public int StationOrder { get; set; }

        public string OverallStatus { get; set; }

        public string DiagnosisStrategy { get; set; }

        public string VariableName { get; set; }

        public string DisplayName { get; set; }

        public string EvaluationStatus { get; set; }

        public bool? EvaluatedValue { get; set; }

        public string DiagnosisQuality { get; set; }

        public string FailurePathSummary { get; set; }

        public string BlockName { get; set; }

        public int TraceDepth { get; set; }

        public string FaultOwnership { get; set; }

        public string DiagnosisPhase { get; set; }

        public string IoTagAddress { get; set; }

        public string IoTagComment { get; set; }

        public DateTime? LastTrueAt { get; set; }

        public string SignalHistoryAnalysis { get; set; }
    }

    public class LineDiagnosisProgressEventDto
    {
        public string Type { get; set; }

        public string TaskId { get; set; }

        public DateTime Timestamp { get; set; } = DateTime.Now;

        public string Message { get; set; }

        public string StationCode { get; set; }

        public int CompletedCount { get; set; }

        public int TotalCount { get; set; }

        public List<string> StationCodes { get; set; }

        public StationDiagnosisSummaryDto Station { get; set; }

        public LineDiagnosisResultFragmentDto ResultFragment { get; set; }

        /// <summary>
        /// AI 阶段完成时返回的完整跨工站诊断结论。
        /// 该对象与产线诊断页面使用的 LlmReport 保持同一结构。
        /// </summary>
        public LineDiagnosisLlmReport LlmReport { get; set; }

        public object Payload { get; set; }
    }

    /// <summary>
    /// MCP 进度消息允许公开的诊断结果片段。
    /// 与详细过程 Payload 分离，避免业务载荷变化或敏感字段进入外部进度流。
    /// </summary>
    public class LineDiagnosisResultFragmentDto
    {
        public int? Iteration { get; set; }

        public ReActPhase? Phase { get; set; }

        public ReActStepDecision? Decision { get; set; }

        public bool? Success { get; set; }

        public string RootCauseStation { get; set; }

        public List<string> AllowedStationCodes { get; set; } = new List<string>();

        public double? Confidence { get; set; }

        public int? SuggestedActionCount { get; set; }
    }

    public class LineDiagnosisResultDto
    {
        /// <summary>
        /// 持久化记录 ID（存库后回填）
        /// </summary>
        public long? RecordId { get; set; }

        /// <summary>
        /// 本次诊断涉及的所有 ProjectId
        /// </summary>
        public List<long> ProjectIds { get; set; } = new List<long>();

        public DateTime DiagnosedAt { get; set; }

        public string OutputLanguage { get; set; }

        public int TotalStations { get; set; }

        public int DiagnosedStations { get; set; }

        public int AbnormalStations { get; set; }

        public long ElapsedMs { get; set; }

        /// <summary>
        /// 整次排查分阶段耗时（2026-09-23），如 "Prepare:120ms Stations:45000ms Recheck:3010ms Constraint:900ms Llm:21000ms"。
        /// 超时返回时只含已完成的阶段。
        /// </summary>
        public string TimingBreakdown { get; set; }

        /// <summary>
        /// 各工位诊断摘要（按工位顺序排列）
        /// </summary>
        public List<StationDiagnosisSummaryDto> StationSummaries { get; set; } = new List<StationDiagnosisSummaryDto>();

        /// <summary>
        /// LLM 跨工位分析结果（UseLlmAnalysis=true 时填充）
        /// </summary>
        public LineDiagnosisLlmReport LlmReport { get; set; }

        /// <summary>
        /// 是否因超时终止
        /// </summary>
        public bool TimedOut { get; set; }

        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>
        /// IO 约束诊断报告（IncludeConstraintAnalysis=true 时填充，来自 DiagnoseEnergyFlowAsync）
        /// </summary>
        public string ConstraintReport { get; set; }

        public string OperatingModeReport { get; set; }

        /// <summary>
        /// 阀门报警阶段整体失败原因；成功为 null（2026-09-30）。
        /// </summary>
        public string AlarmReadError { get; set; }
    }

    /// <summary>
    /// 单工位诊断摘要
    /// </summary>
    public class StationDiagnosisSummaryDto
    {
        /// <summary>
        /// 该工位所属的 ProjectId
        /// </summary>
        public long ProjectId { get; set; }

        public string StationCode { get; set; }

        public int StationOrder { get; set; }

        /// <summary>
        /// AllTrue | SomeFalse | Running | NoAst | Skipped | Error
        /// </summary>
        public string OverallStatus { get; set; }

        /// <summary>
        /// StallPointBased（事件驱动） | SuccessorChainBased（激活GRAPH后继链） | SkippedNoActiveGraph | SkippedFreshActiveGraph | SkippedNoSuccessorCandidate
        /// </summary>
        public string DiagnosisStrategy { get; set; }

        public int AnchorCount { get; set; }

        public int FalseAnchorCount { get; set; }

        public int EvaluatedCount { get; set; }

        /// <summary>
        /// 关键发现列表（仅包含 False/异常 的锚点）
        /// </summary>
        public List<StationAnchorFinding> KeyFindings { get; set; } = new List<StationAnchorFinding>();

        /// <summary>
        /// 当前激活的阀门报警（2026-09-30，只作证据，不影响 OverallStatus）。
        /// </summary>
        public List<ActiveValveAlarmDto> ActiveAlarms { get; set; } = new List<ActiveValveAlarmDto>();

        /// <summary>
        /// 本工位读取失败（质量非 Good / 未返回 / 非布尔）的阀门报警位数量。
        /// </summary>
        public int AlarmReadFailedCount { get; set; }

        public string ErrorMessage { get; set; }

        /// <summary>
        /// 单工位诊断耗时(ms)
        /// </summary>
        public long ElapsedMs { get; set; }

        /// <summary>
        /// 各阶段耗时明细(ms)：Redis|AstLoad|BatchEval|Trace|History
        /// </summary>
        public string TimingBreakdown { get; set; }

        /// <summary>
        /// 模板匹配命中信息（TemplateBased策略时填充）
        /// </summary>
        public string TemplateMatchInfo { get; set; }

        /// <summary>
        /// 模板匹配度评分 0-100（T4.2：置信度40分 + 支持数30分 + 缺失覆盖30分）
        /// </summary>
        public int TemplateMatchScore { get; set; }

        /// <summary>
        /// 模板匹配未命中原因分类（UnknownSignature/NoBundles/AllExpectedOccurred/NoAstMapping/NoExpectedData）
        /// </summary>
        public string TemplateMissReason { get; set; }

        /// <summary>
        /// 工位独立 LLM 诊断摘要（EnablePerStationLlm=true 时填充）
        /// </summary>
        public string LlmSummary { get; set; }
    }

    /// <summary>
    /// 单个锚点诊断发现
    /// </summary>
    public class StationAnchorFinding
    {
        public string VariableName { get; set; }

        public string DisplayName { get; set; }

        public string EvaluationStatus { get; set; }

        public bool? EvaluatedValue { get; set; }

        public string DiagnosisQuality { get; set; }

        /// <summary>
        /// 失败路径摘要（首条失败节点链，紧凑文本）
        /// </summary>
        public string FailurePathSummary { get; set; }

        public string BlockName { get; set; }

        /// <summary>
        /// 该锚点最近一次变为 TRUE 的时间（来自监控历史，卡滞分析用）
        /// </summary>
        public DateTime? LastTrueAt { get; set; }

        /// <summary>
        /// 跨 LogicUnit 追溯的深度（0=锚点本身，1=第一层写入源，...）
        /// </summary>
        public int TraceDepth { get; set; }

        /// <summary>
        /// 信号历史自动分析结论（从 Redis 变化记录中提取，人类可读）。
        /// 例如："DB110.SafetyOK 在 14:32:05 从 True→False，2秒后恢复为 True（瞬态抖动）"
        /// </summary>
        public string SignalHistoryAnalysis { get; set; }

        /// <summary>
        /// 故障归属分类：InternalFault（本工位内部故障）/ WaitingFor:OP005（等待上游工位）/ ExternalInput（外部I点输入）/ Unknown
        /// </summary>
        public string FaultOwnership { get; set; }

        /// <summary>
        /// 诊断阶段标识：GraphTransitionTrace（GRAPH优先阶段-转移条件追溯）/ EnableMissing（使能缺失阶段）
        /// </summary>
        public string DiagnosisPhase { get; set; }

        /// <summary>
        /// IO Tag 物理地址（如 %I1.0 / %Q3.2），当信号为全局 I/O 变量时填充，方便操作工对照接线图定位。
        /// </summary>
        public string IoTagAddress { get; set; }

        /// <summary>
        /// IO Tag 注释（来自 PLC Tag 表），人类可读的设备描述。
        /// </summary>
        public string IoTagComment { get; set; }
    }

    /// <summary>
    /// LLM 跨工位根因分析报告
    /// </summary>
    public class LineDiagnosisLlmReport
    {
        public bool Success { get; set; }

        public string RootCauseStation { get; set; }

        public string RootCauseDescription { get; set; }

        public double Confidence { get; set; }

        /// <summary>
        /// 故障影响范围（辐射式，非线性传播）。
        /// 格式："根因:OP030 → 受影响:[OP010,OP020,OP040,OP050]" 或简单描述。
        /// 产线任意工位故障都可导致全线停机，传播方向不固定。
        /// </summary>
        public string ImpactChain { get; set; }

        public List<LineDiagnosisStationAnalysis> StationAnalysis { get; set; } = new List<LineDiagnosisStationAnalysis>();

        /// <summary>
        /// 次因工位列表：被根因连带影响、有具体异常信号的工位及原因描述
        /// </summary>
        public List<SecondaryCauseItem> SecondaryCauses { get; set; } = new List<SecondaryCauseItem>();

        public List<string> SuggestedActions { get; set; } = new List<string>();

        public string Reasoning { get; set; }

        public string RawResponse { get; set; }

        public long ExecutionTimeMs { get; set; }

        /// <summary>
        /// Agent 模式下的 ReAct 迭代次数
        /// </summary>
        public int AgentIterations { get; set; }

        /// <summary>
        /// Agent 模式下的工具调用统计摘要
        /// </summary>
        public string ToolCallSummary { get; set; }
    }

    /// <summary>
    /// LLM 对单个工位的分析结论
    /// </summary>
    public class LineDiagnosisStationAnalysis
    {
        public string StationCode { get; set; }

        /// <summary>
        /// RootCause | Impacted | Normal | Unknown
        /// </summary>
        public string Role { get; set; }

        public string Summary { get; set; }
    }

    /// <summary>
    /// 次因工位：被根因连带停机的关键工位
    /// </summary>
    public class SecondaryCauseItem
    {
        /// <summary>
        /// 次因工位编码
        /// </summary>
        public string StationCode { get; set; }

        /// <summary>
        /// 次因描述（说明该工位为何停机，等待什么信号）
        /// </summary>
        public string Description { get; set; }
    }
}
