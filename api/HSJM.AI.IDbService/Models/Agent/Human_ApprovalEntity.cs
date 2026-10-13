
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:00
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 人工审批记录表
    /// </summary>
    [SugarTable("AI_Human_Approval",tableDescription:"人工审批记录表")]
    public class Human_ApprovalEntity: BaseField
    {

       public  Human_ApprovalEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联工作流ID(AI_Agent_Workflow)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联工作流ID(AI_Agent_Workflow)")]
        public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  关联任务ID(AI_Agent_Task)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联任务ID(AI_Agent_Task)")]
        public long? TaskID {get;set;}
        
          /// <summary>
        ///  审批类型(如TaskConfirmation/RiskNotice)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批类型(如TaskConfirmation/RiskNotice)")]
        public string ApprovalType {get;set;}
        
          /// <summary>
        ///  审批状态:0待审批,1通过,2拒绝,3撤回,4超时
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批状态:0待审批,1通过,2拒绝,3撤回,4超时")]
        public int? Status {get;set;}
        
          /// <summary>
        ///  审批请求上下文JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批请求上下文JSON")]
        public string RequestData {get;set;}
        
          /// <summary>
        ///  审批结果回写JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批结果回写JSON")]
        public string ResultData {get;set;}
        
          /// <summary>
        ///  审批人ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批人ID")]
        public long? ApproverID {get;set;}
        
          /// <summary>
        ///  审批人姓名
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批人姓名")]
        public string ApproverName {get;set;}
        
          /// <summary>
        ///  审批完成时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批完成时间")]
        public DateTime? ApproveTime {get;set;}
        
          /// <summary>
        ///  审批意见
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批意见")]
        public string ApproveComment {get;set;}
        
          /// <summary>
        ///  通知渠道(Email/DingTalk/WeCom)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="通知渠道(Email/DingTalk/WeCom)")]
        public string NotificationChannel {get;set;}
        
          /// <summary>
        ///  审批回调URL
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批回调URL")]
        public string CallbackUrl {get;set;}
        
          /// <summary>
        ///  审批过期时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批过期时间")]
        public DateTime? ExpireTime {get;set;}
        
          /// <summary>
        ///  最后一次通知时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="最后一次通知时间")]
        public DateTime? LastNotifyTime {get;set;}
        
          /// <summary>
        ///  通知重试次数
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="通知重试次数")]
        public int? RetryCount {get;set;}
        
     

    }

   
}
