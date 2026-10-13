using System;
using System.Collections.Generic;

namespace 博图项目读取.Models
{
    /// <summary>
    /// TIA Portal项目信息
    /// </summary>
    public class TiaPortalProject
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTime CreatedTime { get; set; }
        public DateTime ModifiedTime { get; set; }
        public string Author { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        
        public List<PlcDevice> Devices { get; set; } = new List<PlcDevice>();
        public List<SymbolInfo> Symbols { get; set; } = new List<SymbolInfo>();
        public List<ProgramBlock> ProgramBlocks { get; set; } = new List<ProgramBlock>();
        public List<DataBlock> DataBlocks { get; set; } = new List<DataBlock>();
        public List<BlockCallRelation> CallRelations { get; set; } = new List<BlockCallRelation>();
        public List<GraphSequence> GraphSequences { get; set; } = new List<GraphSequence>();
        public ProjectStatistics Statistics { get; set; } = new ProjectStatistics();
    }

    /// <summary>
    /// PLC设备信息
    /// </summary>
    public class PlcDevice
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Rack { get; set; }
        public int Slot { get; set; }
        
        public List<ProgramBlock> Blocks { get; set; } = new List<ProgramBlock>();
        public List<SymbolInfo> LocalSymbols { get; set; } = new List<SymbolInfo>();
        public List<NetworkInfo> Networks { get; set; } = new List<NetworkInfo>();
        public DeviceConfiguration Configuration { get; set; } = new DeviceConfiguration();
    }

    /// <summary>
    /// 符号信息
    /// </summary>
    public class SymbolInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string LogicalAddress { get; set; } = string.Empty;
        public string AccessLevel { get; set; } = string.Empty;
        public bool IsRetentive { get; set; }
        public bool IsConstant { get; set; }
        public object InitialValue { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public SymbolScope Scope { get; set; } = SymbolScope.Global;
    }

    /// <summary>
    /// 程序块信息
    /// </summary>
    public class ProgramBlock
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // OB, FB, FC, DB
        public int Number { get; set; }
        public string Language { get; set; } = string.Empty; // LAD, FBD, STL, SCL, GRAPH
        public string Comment { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty; // 程序块族
        public DateTime CreatedTime { get; set; }
        public DateTime ModifiedTime { get; set; }
        public string Version { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty; // 程序块所在的文件夹路径
        public bool IsProtected { get; set; }
        public int CodeSize { get; set; }
        public int DataSize { get; set; }

        public List<NetworkInfo> Networks { get; set; } = new List<NetworkInfo>();
        public List<BlockParameter> Parameters { get; set; } = new List<BlockParameter>();
        public List<BlockVariable> LocalVariables { get; set; } = new List<BlockVariable>();
        public List<string> CalledBlocks { get; set; } = new List<string>();
        public List<string> CallingBlocks { get; set; } = new List<string>();
        public BlockInterface Interface { get; set; } = new BlockInterface();
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(); // 扩展属性
    }

    /// <summary>
    /// 数据块信息
    /// </summary>
    public class DataBlock
    {
        public string Name { get; set; } = string.Empty;
        public int Number { get; set; }
        public string Type { get; set; } = string.Empty; // Global DB, Instance DB
        public string Comment { get; set; } = string.Empty;
        public string AssociatedBlock { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty; // 数据块所在的文件夹路径
        public bool IsRetentive { get; set; }
        public bool IsOptimized { get; set; }
        public int Size { get; set; }
        
        public List<DataBlockElement> Elements { get; set; } = new List<DataBlockElement>();
        public List<SymbolInfo> Symbols { get; set; } = new List<SymbolInfo>();
    }

    /// <summary>
    /// 网络信息
    /// </summary>
    public class NetworkInfo
    {
        public int Number { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;

        public List<NetworkElement> Elements { get; set; } = new List<NetworkElement>();
        public List<NetworkConnection> Connections { get; set; } = new List<NetworkConnection>();
        public NetworkLogic Logic { get; set; } = new NetworkLogic();
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(); // 扩展属性
    }

    /// <summary>
    /// 网络元素（触点、线圈、功能块等）
    /// </summary>
    public class NetworkElement
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // Contact, Coil, Block, Function
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public int X { get; set; } // 网络中的X坐标
        public int Y { get; set; } // 网络中的Y坐标
        public bool IsNegated { get; set; }
        public bool IsSet { get; set; }
        public bool IsReset { get; set; }
        public bool IsInitialStep { get; set; } // 用于GRAPH步骤标识

        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        public List<ElementPin> Pins { get; set; } = new List<ElementPin>();
    }

    /// <summary>
    /// 网络连接
    /// </summary>
    public class NetworkConnection
    {
        public string Id { get; set; } = string.Empty;
        public string FromElementId { get; set; } = string.Empty;
        public string FromPin { get; set; } = string.Empty;
        public string ToElementId { get; set; } = string.Empty;
        public string ToPin { get; set; } = string.Empty;
        public bool IsNegated { get; set; }
        public List<ConnectionPoint> Points { get; set; } = new List<ConnectionPoint>();
    }

    /// <summary>
    /// GRAPH顺序控制
    /// </summary>
    public class GraphSequence
    {
        public string Name { get; set; } = string.Empty;
        public string BlockName { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        
        public List<GraphStep> Steps { get; set; } = new List<GraphStep>();
        public List<GraphTransition> Transitions { get; set; } = new List<GraphTransition>();
        public List<GraphAction> Actions { get; set; } = new List<GraphAction>();
        public List<GraphBranch> Branches { get; set; } = new List<GraphBranch>();
    }

    /// <summary>
    /// GRAPH步骤
    /// </summary>
    public class GraphStep
    {
        public string Name { get; set; } = string.Empty;
        public int Number { get; set; }
        public int StepNumber { get; set; } // 添加StepNumber属性
        public string Comment { get; set; } = string.Empty;
        public bool IsInitialStep { get; set; }
        public TimeSpan MinTime { get; set; }
        public TimeSpan MaxTime { get; set; }
        public string SupervisionCondition { get; set; } = string.Empty;
        public string StepType { get; set; } = "Normal"; // 添加StepType属性
        public GraphPosition Position { get; set; } // 添加Position属性

        public List<GraphAction> Actions { get; set; } = new List<GraphAction>();
        public List<string> NextTransitions { get; set; } = new List<string>();
        
        // 新增属性用于连接关系解析
        public List<int> OutgoingTransitions { get; set; } = new List<int>(); // 出口转换条件
        public List<int> IncomingTransitions { get; set; } = new List<int>(); // 入口转换条件
        public int OutputBranch { get; set; } = 0; // 输出分支编号
        public int InputBranch { get; set; } = 0; // 输入分支编号
        public int InputBranchOut { get; set; } = 0; // 输入分支输出编号
        public string IndirectFromStep { get; set; } = string.Empty; // 间接来源步骤
        public int IndirectFromStepNumber { get; set; } = 0; // 间接来源步骤编号
    }

    /// <summary>
    /// GRAPH转移
    /// </summary>
    public class GraphTransition
    {
        public string Name { get; set; } = string.Empty;
        public int Number { get; set; }
        public int TransitionNumber { get; set; } // 添加TransitionNumber属性
        public string Comment { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
        public string FromStep { get; set; } = string.Empty;
        public string ToStep { get; set; } = string.Empty;
        public int FromStepNumber { get; set; } // 添加FromStepNumber属性
        public int ToStepNumber { get; set; } // 添加ToStepNumber属性
        public TransitionType Type { get; set; } = TransitionType.Normal;
        public GraphPosition Position { get; set; } // 添加Position属性

        // 新增属性用于详细解析
        public List<DbVariable> DbVariables { get; set; } = new List<DbVariable>();
        public List<string> InputSignals { get; set; } = new List<string>();
        public List<string> OutputSignals { get; set; } = new List<string>();
        public List<string> MemoryFlags { get; set; } = new List<string>();
        public List<string> Timers { get; set; } = new List<string>();
        public List<string> Counters { get; set; } = new List<string>();
        public List<string> LogicElements { get; set; } = new List<string>();
        
        // 新增属性用于分支和连接关系解析
        public int FromBranch { get; set; } = 0; // 来源分支编号
        public int FromBranchOut { get; set; } = 0; // 来源分支输出编号
        public int ToBranch { get; set; } = 0; // 目标分支编号
        public int ToBranchIn { get; set; } = 0; // 目标分支输入编号
        public bool IsJump { get; set; } = false; // 是否为跳转连接
    }

    /// <summary>
    /// GRAPH动作
    /// </summary>
    public class GraphAction
    {
        public string Name { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public ActionType Type { get; set; } = Models.ActionType.Normal;
        public string ActionType { get; set; } = "N"; // 添加ActionType字符串属性
        public ActionQualifier Qualifier { get; set; } = ActionQualifier.None;
        public string AssociatedStep { get; set; } = string.Empty;
        public int StepNumber { get; set; } // 添加StepNumber属性
        public string Expression { get; set; } = string.Empty; // 添加Expression属性
    }

    /// <summary>
    /// 程序块调用关系
    /// </summary>
    public class BlockCallRelation
    {
        public string CallerBlock { get; set; } = string.Empty;
        public string CalledBlock { get; set; } = string.Empty;
        public string CallType { get; set; } = string.Empty; // CALL, UC, CC
        public int NetworkNumber { get; set; }
        public string Condition { get; set; } = string.Empty;
        public List<ParameterMapping> ParameterMappings { get; set; } = new List<ParameterMapping>();
    }

    /// <summary>
    /// 项目统计信息
    /// </summary>
    public class ProjectStatistics
    {
        public int TotalBlocks { get; set; }
        public int TotalNetworks { get; set; }
        public int TotalSymbols { get; set; }
        public int TotalDataBlocks { get; set; }
        public int TotalConnections { get; set; }
        public int TotalGraphSequences { get; set; }
        public Dictionary<string, int> BlockTypeCount { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> LanguageCount { get; set; } = new Dictionary<string, int>();
        public long TotalCodeSize { get; set; }
        public long TotalDataSize { get; set; }
    }

    // 辅助类和枚举
    public class DeviceConfiguration
    {
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        public List<ModuleInfo> Modules { get; set; } = new List<ModuleInfo>();
    }

    public class ModuleInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public int Position { get; set; }
    }

    public class BlockParameter
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty; // Input, Output, InOut
        public string Comment { get; set; } = string.Empty;
        public object DefaultValue { get; set; }
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(); // 扩展属性
    }

    public class BlockVariable
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public object InitialValue { get; set; }
        public bool IsRetentive { get; set; }
    }

    public class BlockInterface
    {
        public List<BlockParameter> Input { get; set; } = new List<BlockParameter>();
        public List<BlockParameter> Output { get; set; } = new List<BlockParameter>();
        public List<BlockParameter> InOut { get; set; } = new List<BlockParameter>();
        public List<BlockVariable> Static { get; set; } = new List<BlockVariable>();
        public List<BlockVariable> Temp { get; set; } = new List<BlockVariable>();
        public List<BlockVariable> Constant { get; set; } = new List<BlockVariable>();
    }

    public class DataBlockElement
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public object Value { get; set; }
        public string Address { get; set; } = string.Empty;
        public bool IsRetentive { get; set; }
    }

    public class ElementPin
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // Input, Output, Enable, etc.
        public string Address { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
    }

    public class ConnectionPoint
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    public class NetworkLogic
    {
        public string Expression { get; set; } = string.Empty;
        public List<string> InputVariables { get; set; } = new List<string>();
        public List<string> OutputVariables { get; set; } = new List<string>();
        public LogicType Type { get; set; } = LogicType.Ladder;
    }

    /// <summary>
    /// GRAPH分支信息
    /// </summary>
    public class GraphBranch
    {
        public int Number { get; set; }
        public string Type { get; set; } // "Alternative", "Parallel", etc.
        public string Name { get; set; }
        public int InputStep { get; set; } // 输入步骤编号
        public Dictionary<int, List<int>> OutputTransitions { get; set; } = new Dictionary<int, List<int>>(); // 分支输出转换条件
        public Dictionary<int, List<int>> InputTransitions { get; set; } = new Dictionary<int, List<int>>(); // 分支输入转换条件
        public Dictionary<int, List<int>> OutputSteps { get; set; } = new Dictionary<int, List<int>>(); // 分支输出步骤
    }

    /// <summary>
    /// GRAPH位置信息
    /// </summary>
    public class GraphPosition
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    public class ParameterMapping
    {
        public string ParameterName { get; set; } = string.Empty;
        public string MappedAddress { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
    }

    /// <summary>
    /// 详细转移条件信息
    /// </summary>
    public class DetailedTransitionInfo
    {
        public string Name { get; set; } = string.Empty;
        public int Number { get; set; }
        public string Comment { get; set; } = string.Empty;
        public string DetailedCondition { get; set; } = string.Empty;
        public string FromStep { get; set; } = string.Empty;
        public string ToStep { get; set; } = string.Empty;
        public TransitionType Type { get; set; } = TransitionType.Normal;
        public List<string> LogicElements { get; set; } = new List<string>();
    }

    /// <summary>
    /// DB变量信息
    /// </summary>
    public class DbVariable
    {
        public string FullAddress { get; set; } = string.Empty;
        public int DbNumber { get; set; }
        public string DataType { get; set; } = string.Empty;
        public int Offset { get; set; }
        public int BitNumber { get; set; }
        public string Description { get; set; } = string.Empty;
        public object InitialValue { get; set; }
        public bool IsRetentive { get; set; }
    }

    /// <summary>
    /// 枚举定义
    /// </summary>
    public enum SymbolScope
    {
        Global,
        Local,
        Parameter
    }

    public enum TransitionType
    {
        Normal,
        Conditional,
        Timed,
        Event
    }

    public enum ActionType
    {
        Normal,
        Set,
        Reset,
        Pulse,
        Delayed
    }

    public enum ActionQualifier
    {
        None,
        N,  // Normal
        S,  // Set
        R,  // Reset
        L,  // Time Limited
        D,  // Time Delayed
        P,  // Pulse
        SD, // Stored and Time Delayed
        DS, // Delayed and Stored
        SL  // Stored and Time Limited
    }

    public enum BranchType
    {
        Sequential,
        Parallel,
        Alternative,
        Loop
    }

    public enum LogicType
    {
        Ladder,
        FunctionBlock,
        StatementList,
        StructuredControl,
        Graph
    }

    /// <summary>
    /// 业务系统数据
    /// </summary>
    public class BusinessSystemData
    {
        public string SystemName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTime ExportTime { get; set; }
        public string ExportedBy { get; set; } = string.Empty;
        public TiaPortalProject ProjectData { get; set; } = new TiaPortalProject();
        public List<MonitoringPoint> MonitoringPoints { get; set; } = new List<MonitoringPoint>();
        public List<AlarmConfiguration> AlarmConfigurations { get; set; } = new List<AlarmConfiguration>();
        public CommunicationSettings CommunicationSettings { get; set; } = new CommunicationSettings();
    }

    public class AlarmConfiguration
    {
        public string Name { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
    }

    public class CommunicationSettings
    {
        public string Protocol { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; }
        public int Timeout { get; set; } = 5000;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    // 故障诊断相关的数据模型
    public class FaultDiagnosisReport
    {
        public string BlockName { get; set; } = string.Empty;
        public string BlockType { get; set; } = string.Empty;
        public DateTime GeneratedTime { get; set; }
        public List<FaultPoint> FaultPoints { get; set; } = new List<FaultPoint>();
        public List<DiagnosisRule> DiagnosisRules { get; set; } = new List<DiagnosisRule>();
        public List<MonitoringPoint> MonitoringPoints { get; set; } = new List<MonitoringPoint>();
        public string Summary { get; set; } = string.Empty;
    }

    public class FaultPoint
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public FaultType Type { get; set; }
        public FaultSeverity Severity { get; set; }
        public string Location { get; set; } = string.Empty;
        public string MonitoringCondition { get; set; } = string.Empty;
        public List<string> PossibleCauses { get; set; } = new List<string>();
        public List<string> DiagnosisActions { get; set; } = new List<string>();
    }

    public class DiagnosisRule
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public int Priority { get; set; }
        public string Category { get; set; } = string.Empty;
    }

    public class MonitoringPoint
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public MonitoringType Type { get; set; }
        public string Address { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public TimeSpan SamplingInterval { get; set; }
        public int ScanInterval { get; set; } = 100; // 扫描间隔（毫秒）
        public string AlarmThreshold { get; set; } = string.Empty;
        public double MinValue { get; set; } = 0;
        public double MaxValue { get; set; } = 0;
        public string Unit { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
    }

    public enum FaultType
    {
        Signal,
        Logic,
        Timeout,
        Output,
        Timer,
        Control,
        Communication,
        Hardware
    }

    public enum FaultSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    public enum MonitoringType
    {
        Input,
        Output,
        Status,
        Time,
        Analog,
        Digital
    }
} 