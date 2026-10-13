

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:33:39
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// AI决策审计日志表
    /// </summary>
    public class Audit_LogInsertDTO
    {
            /// <summary>
        ///  动作类型(参数调整/停机/预警)
        /// </summary>
        [Description("动作类型(参数调整/停机/预警)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string ActionType {get;set;}
        
          /// <summary>
        ///  目标对象ID(如设备ID)
        /// </summary>
        [Description("目标对象ID(如设备ID)")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string TargetObjID {get;set;}
        
          /// <summary>
        ///  智能体ID
        /// </summary>
        [Description("智能体ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? AgentRoleID {get;set;}
        
          /// <summary>
        ///  决策内容/建议
        /// </summary>
        [Description("决策内容/建议")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string DecisionContent {get;set;}
        
          /// <summary>
        ///  决策时的数据快照JSON
        /// </summary>
        [Description("决策时的数据快照JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string SnapshotData {get;set;}
        
          /// <summary>
        ///  风险等级: 1-低, 2-中, 3-高
        /// </summary>
        [Description("风险等级: 1-低, 2-中, 3-高")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? RiskLevel {get;set;}
        
          /// <summary>
        ///  审核人ID
        /// </summary>
        [Description("审核人ID")]
                  [Range(0, long.MaxValue)]
                  public long? HumanReviewerID {get;set;}
        
          /// <summary>
        ///  审核状态: 0-无需审核, 1-待审核, 2-已通过, 3-已驳回
        /// </summary>
        [Description("审核状态: 0-无需审核, 1-待审核, 2-已通过, 3-已驳回")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? ReviewStatus {get;set;}
        
          /// <summary>
        ///  审核意见
        /// </summary>
        [Description("审核意见")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ReviewComment {get;set;}
        
          /// <summary>
        ///  客户端IP
        /// </summary>
        [Description("客户端IP")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string ClientIP {get;set;}
        
          /// <summary>
        ///  执行耗时(ms)
        /// </summary>
        [Description("执行耗时(ms)")]
                  [Range(0, int.MaxValue)]
                  public int? ExecutionDuration {get;set;}
        
     

    }
}
