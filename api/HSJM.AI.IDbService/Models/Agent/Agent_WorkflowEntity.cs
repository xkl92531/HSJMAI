
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:33:05
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 描述跨任务的整体工作流，记录触发来源、规划 DAG、MAF 编排映射与执行状态。
    /// </summary>
    [SugarTable("AI_Agent_Workflow",tableDescription:"描述跨任务的整体工作流，记录触发来源、规划 DAG、MAF 编排映射与执行状态。")]
    public class Agent_WorkflowEntity: BaseField
    {

       public  Agent_WorkflowEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  工作流名称。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工作流名称。")]
        public string Name {get;set;}
        
          /// <summary>
        ///  原始需求或任务描述。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="原始需求或任务描述。")]
        public string Description {get;set;}
        
          /// <summary>
        ///  状态：0 待规划、1 规划中、2 执行中、3 完成、4 失败、5 取消。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="状态：0 待规划、1 规划中、2 执行中、3 完成、4 失败、5 取消。")]
        public int? Status {get;set;}
        
          /// <summary>
        ///  负责规划的智能体 ID。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="负责规划的智能体 ID。")]
        public long? PlannerAgentID {get;set;}
        
          /// <summary>
        ///  分布式追踪 ID。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="分布式追踪 ID。")]
        public string TraceId {get;set;}
        
          /// <summary>
        ///  共享上下文或黑板数据 JSON。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="共享上下文或黑板数据 JSON。")]
        public string GlobalContext {get;set;}
        
          /// <summary>
        ///  工作流最终输出摘要。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工作流最终输出摘要。")]
        public string ResultSummary {get;set;}
        
          /// <summary>
        ///  优先级，数值越大优先。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="优先级，数值越大优先。")]
        public int? Priority {get;set;}
        
          /// <summary>
        ///  触发方式：User/System/Schedule/SubWorkflow。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="触发方式：User/System/Schedule/SubWorkflow。")]
        public string TriggerType {get;set;}
        
          /// <summary>
        ///  外部关联 ID（会话、事件等）。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="外部关联 ID（会话、事件等）。")]
        public string CorrelationID {get;set;}
        
          /// <summary>
        ///  父工作流 ID，支持嵌套。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="父工作流 ID，支持嵌套。")]
        public long? ParentWorkflowID {get;set;}
        
          /// <summary>
        ///  触发本工作流的父任务 ID。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="触发本工作流的父任务 ID。")]
        public long? TriggerTaskID {get;set;}
        
          /// <summary>
        ///  暂停原因。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="暂停原因。")]
        public string PausedReason {get;set;}
        
          /// <summary>
        ///  恢复条件描述或表达式。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="恢复条件描述或表达式。")]
        public string ResumeCondition {get;set;}
        
          /// <summary>
        ///  推理模式：ReAct/TreeOfThought/PlanExecute 等。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="推理模式：ReAct/TreeOfThought/PlanExecute 等。")]
        public string ThinkingMode {get;set;}
        
          /// <summary>
        ///  规划输出的 DAG JSON。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="规划输出的 DAG JSON。")]
        public string PlanDAG {get;set;}
        
          /// <summary>
        ///  规划状态：Draft/Approved/Rejected。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="规划状态：Draft/Approved/Rejected。")]
        public string PlanStatus {get;set;}
        
          /// <summary>
        ///  预计耗时（秒）。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="预计耗时（秒）。")]
        public int? EstimatedDuration {get;set;}
        
          /// <summary>
        ///  实际耗时（秒）。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="实际耗时（秒）。")]
        public int? ActualDuration {get;set;}
        
          /// <summary>
        ///  映射的外部 MAF Workflow 主键。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="映射的外部 MAF Workflow 主键。")]
        public string MAFWorkflowId {get;set;}
        
          /// <summary>
        ///  MAF 侧最近 Checkpoint。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="MAF 侧最近 Checkpoint。")]
        public string MAFCheckpointId {get;set;}
        
          /// <summary>
        ///  下发到 MAF 执行器的配置 JSON。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="下发到 MAF 执行器的配置 JSON。")]
        public string MAFExecutorConfig {get;set;}
        
          /// <summary>
        ///  MAF 运行态快照。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="MAF 运行态快照。")]
        public string MAFStateSnapshot {get;set;}
        
          /// <summary>
        ///  是否由 MAF 托管执行。
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否由 MAF 托管执行。")]
        public bool? UsesMAFWorkflow {get;set;}
        
     

    }

   
}
