
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:00
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 人工审批记录表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Human_ApprovalView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  关联工作流ID(AI_Agent_Workflow)
        /// </summary>
          [ImporterHeader(Name = "关联工作流ID(AI_Agent_Workflow)", Format = ""), ExporterHeader("关联工作流ID(AI_Agent_Workflow)")]
        public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  关联任务ID(AI_Agent_Task)
        /// </summary>
          [ImporterHeader(Name = "关联任务ID(AI_Agent_Task)", Format = ""), ExporterHeader("关联任务ID(AI_Agent_Task)")]
        public long? TaskID {get;set;}
        
          /// <summary>
        ///  审批类型(如TaskConfirmation/RiskNotice)
        /// </summary>
          [ImporterHeader(Name = "审批类型(如TaskConfirmation/RiskNotice)", Format = ""), ExporterHeader("审批类型(如TaskConfirmation/RiskNotice)")]
        public string ApprovalType {get;set;}
        
          /// <summary>
        ///  审批状态:0待审批,1通过,2拒绝,3撤回,4超时
        /// </summary>
          [ImporterHeader(Name = "审批状态:0待审批,1通过,2拒绝,3撤回,4超时", Format = ""), ExporterHeader("审批状态:0待审批,1通过,2拒绝,3撤回,4超时")]
        public int? Status {get;set;}
        
          /// <summary>
        ///  审批请求上下文JSON
        /// </summary>
          [ImporterHeader(Name = "审批请求上下文JSON", Format = ""), ExporterHeader("审批请求上下文JSON")]
        public string RequestData {get;set;}
        
          /// <summary>
        ///  审批结果回写JSON
        /// </summary>
          [ImporterHeader(Name = "审批结果回写JSON", Format = ""), ExporterHeader("审批结果回写JSON")]
        public string ResultData {get;set;}
        
          /// <summary>
        ///  审批人ID
        /// </summary>
          [ImporterHeader(Name = "审批人ID", Format = ""), ExporterHeader("审批人ID")]
        public long? ApproverID {get;set;}
        
          /// <summary>
        ///  审批人姓名
        /// </summary>
          [ImporterHeader(Name = "审批人姓名", Format = ""), ExporterHeader("审批人姓名")]
        public string ApproverName {get;set;}
        
          /// <summary>
        ///  审批完成时间
        /// </summary>
          [ImporterHeader(Name = "审批完成时间", Format = ""), ExporterHeader("审批完成时间")]
        public DateTime? ApproveTime {get;set;}
        
          /// <summary>
        ///  审批意见
        /// </summary>
          [ImporterHeader(Name = "审批意见", Format = ""), ExporterHeader("审批意见")]
        public string ApproveComment {get;set;}
        
          /// <summary>
        ///  通知渠道(Email/DingTalk/WeCom)
        /// </summary>
          [ImporterHeader(Name = "通知渠道(Email/DingTalk/WeCom)", Format = ""), ExporterHeader("通知渠道(Email/DingTalk/WeCom)")]
        public string NotificationChannel {get;set;}
        
          /// <summary>
        ///  审批回调URL
        /// </summary>
          [ImporterHeader(Name = "审批回调URL", Format = ""), ExporterHeader("审批回调URL")]
        public string CallbackUrl {get;set;}
        
          /// <summary>
        ///  审批过期时间
        /// </summary>
          [ImporterHeader(Name = "审批过期时间", Format = ""), ExporterHeader("审批过期时间")]
        public DateTime? ExpireTime {get;set;}
        
          /// <summary>
        ///  最后一次通知时间
        /// </summary>
          [ImporterHeader(Name = "最后一次通知时间", Format = ""), ExporterHeader("最后一次通知时间")]
        public DateTime? LastNotifyTime {get;set;}
        
          /// <summary>
        ///  通知重试次数
        /// </summary>
          [ImporterHeader(Name = "通知重试次数", Format = ""), ExporterHeader("通知重试次数")]
        public int? RetryCount {get;set;}
        
     

    }
}
