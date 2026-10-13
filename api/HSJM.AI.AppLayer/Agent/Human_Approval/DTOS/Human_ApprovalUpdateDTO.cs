

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:00
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 人工审批记录表
    /// </summary>
    public class Human_ApprovalUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  关联工作流ID(AI_Agent_Workflow)
        /// </summary>
        [Description("关联工作流ID(AI_Agent_Workflow)")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  关联任务ID(AI_Agent_Task)
        /// </summary>
        [Description("关联任务ID(AI_Agent_Task)")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? TaskID {get;set;}
        
          /// <summary>
        ///  审批类型(如TaskConfirmation/RiskNotice)
        /// </summary>
        [Description("审批类型(如TaskConfirmation/RiskNotice)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string ApprovalType {get;set;}
        
          /// <summary>
        ///  审批状态:0待审批,1通过,2拒绝,3撤回,4超时
        /// </summary>
        [Description("审批状态:0待审批,1通过,2拒绝,3撤回,4超时")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? Status {get;set;}
        
          /// <summary>
        ///  审批请求上下文JSON
        /// </summary>
        [Description("审批请求上下文JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string RequestData {get;set;}
        
          /// <summary>
        ///  审批结果回写JSON
        /// </summary>
        [Description("审批结果回写JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ResultData {get;set;}
        
          /// <summary>
        ///  审批人ID
        /// </summary>
        [Description("审批人ID")]
                  [Range(0, long.MaxValue)]
                  public long? ApproverID {get;set;}
        
          /// <summary>
        ///  审批人姓名
        /// </summary>
        [Description("审批人姓名")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string ApproverName {get;set;}
        
          /// <summary>
        ///  审批完成时间
        /// </summary>
        [Description("审批完成时间")]
                 public DateTime? ApproveTime {get;set;}
        
          /// <summary>
        ///  审批意见
        /// </summary>
        [Description("审批意见")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ApproveComment {get;set;}
        
          /// <summary>
        ///  通知渠道(Email/DingTalk/WeCom)
        /// </summary>
        [Description("通知渠道(Email/DingTalk/WeCom)")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string NotificationChannel {get;set;}
        
          /// <summary>
        ///  审批回调URL
        /// </summary>
        [Description("审批回调URL")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string CallbackUrl {get;set;}
        
          /// <summary>
        ///  审批过期时间
        /// </summary>
        [Description("审批过期时间")]
                 public DateTime? ExpireTime {get;set;}
        
          /// <summary>
        ///  最后一次通知时间
        /// </summary>
        [Description("最后一次通知时间")]
                 public DateTime? LastNotifyTime {get;set;}
        
          /// <summary>
        ///  通知重试次数
        /// </summary>
        [Description("通知重试次数")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? RetryCount {get;set;}
        
     

    }
}
