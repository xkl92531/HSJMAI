

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:53
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 技能执行历史表 - 记录每次技能调用详情
    /// </summary>
    public class Skill_Execution_LogInsertDTO
    {
            /// <summary>
        ///  关联技能
        /// </summary>
        [Description("关联技能")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? SkillID {get;set;}
        
          /// <summary>
        ///  执行的智能体角色
        /// </summary>
        [Description("执行的智能体角色")]
                  [Range(0, long.MaxValue)]
                  public long? AgentRoleID {get;set;}
        
          /// <summary>
        ///  关联工作流
        /// </summary>
        [Description("关联工作流")]
                  [Range(0, long.MaxValue)]
                  public long? WorkflowID {get;set;}
        
          /// <summary>
        ///  关联任务
        /// </summary>
        [Description("关联任务")]
                  [Range(0, long.MaxValue)]
                  public long? TaskID {get;set;}
        
          /// <summary>
        ///  请求唯一ID(幂等性保障)
        /// </summary>
        [Description("请求唯一ID(幂等性保障)")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string RequestID {get;set;}
        
          /// <summary>
        ///  输入参数JSON
        /// </summary>
        [Description("输入参数JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string InputParams {get;set;}
        
          /// <summary>
        ///  输出结果JSON
        /// </summary>
        [Description("输出结果JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string OutputResult {get;set;}
        
          /// <summary>
        ///  状态: Success/Failed/Timeout/Cancelled/Pending
        /// </summary>
        [Description("状态: Success/Failed/Timeout/Cancelled/Pending")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string ExecutionStatus {get;set;}
        
          /// <summary>
        ///  执行耗时(ms)
        /// </summary>
        [Description("执行耗时(ms)")]
                  [Range(0, int.MaxValue)]
                  public int? ExecutionTime {get;set;}
        
          /// <summary>
        ///  开始时间
        /// </summary>
        [Description("开始时间")]
                 public DateTime? StartTime {get;set;}
        
          /// <summary>
        ///  结束时间
        /// </summary>
        [Description("结束时间")]
                 public DateTime? EndTime {get;set;}
        
          /// <summary>
        ///  错误码
        /// </summary>
        [Description("错误码")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string ErrorCode {get;set;}
        
          /// <summary>
        ///  错误信息
        /// </summary>
        [Description("错误信息")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ErrorMessage {get;set;}
        
          /// <summary>
        ///  错误详情/堆栈
        /// </summary>
        [Description("错误详情/堆栈")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ErrorDetails {get;set;}
        
          /// <summary>
        ///  父执行记录ID(复合技能子步骤)
        /// </summary>
        [Description("父执行记录ID(复合技能子步骤)")]
                  [Range(0, long.MaxValue)]
                  public long? ParentExecutionID {get;set;}
        
          /// <summary>
        ///  步骤序号(复合技能)
        /// </summary>
        [Description("步骤序号(复合技能)")]
                  [Range(0, int.MaxValue)]
                  public int? StepIndex {get;set;}
        
          /// <summary>
        ///  质量评分(0-5)
        /// </summary>
        [Description("质量评分(0-5)")]
                  [Range(0, double.MaxValue)]
                  public decimal? QualityScore {get;set;}
        
          /// <summary>
        ///  反馈来源: Auto/Human/System
        /// </summary>
        [Description("反馈来源: Auto/Human/System")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string FeedbackSource {get;set;}
        
          /// <summary>
        ///  反馈备注
        /// </summary>
        [Description("反馈备注")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string FeedbackNotes {get;set;}
        
     

    }
}
