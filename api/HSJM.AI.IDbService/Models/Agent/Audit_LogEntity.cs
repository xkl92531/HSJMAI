
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:33:39
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// AI决策审计日志表
    /// </summary>
    [SugarTable("AI_Audit_Log",tableDescription:"AI决策审计日志表")]
    public class Audit_LogEntity: BaseField
    {

       public  Audit_LogEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  动作类型(参数调整/停机/预警)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="动作类型(参数调整/停机/预警)")]
        public string ActionType {get;set;}
        
          /// <summary>
        ///  目标对象ID(如设备ID)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="目标对象ID(如设备ID)")]
        public string TargetObjID {get;set;}
        
          /// <summary>
        ///  智能体ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="智能体ID")]
        public long? AgentRoleID {get;set;}
        
          /// <summary>
        ///  决策内容/建议
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="决策内容/建议")]
        public string DecisionContent {get;set;}
        
          /// <summary>
        ///  决策时的数据快照JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="决策时的数据快照JSON")]
        public string SnapshotData {get;set;}
        
          /// <summary>
        ///  风险等级: 1-低, 2-中, 3-高
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="风险等级: 1-低, 2-中, 3-高")]
        public int? RiskLevel {get;set;}
        
          /// <summary>
        ///  审核人ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审核人ID")]
        public long? HumanReviewerID {get;set;}
        
          /// <summary>
        ///  审核状态: 0-无需审核, 1-待审核, 2-已通过, 3-已驳回
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审核状态: 0-无需审核, 1-待审核, 2-已通过, 3-已驳回")]
        public int? ReviewStatus {get;set;}
        
          /// <summary>
        ///  审核意见
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审核意见")]
        public string ReviewComment {get;set;}
        
          /// <summary>
        ///  客户端IP
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="客户端IP")]
        public string ClientIP {get;set;}
        
          /// <summary>
        ///  执行耗时(ms)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="执行耗时(ms)")]
        public int? ExecutionDuration {get;set;}
        
     

    }

   
}
