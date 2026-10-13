

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:33:39
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class Audit_LogConditionDTO
    {
            /// <summary>
        ///  动作类型(参数调整/停机/预警)
        /// </summary>
        public string ActionType {get;set;}
        
           /// <summary>
        ///  目标对象ID(如设备ID)
        /// </summary>
        public string TargetObjID {get;set;}
        
           /// <summary>
        ///  智能体ID
        /// </summary>
        public long? AgentRoleID {get;set;}
        
           /// <summary>
        ///  风险等级: 1-低, 2-中, 3-高
        /// </summary>
        public int? RiskLevel {get;set;}
        
           /// <summary>
        ///  审核状态: 0-无需审核, 1-待审核, 2-已通过, 3-已驳回
        /// </summary>
        public int? ReviewStatus {get;set;}
        
           /// <summary>
        ///  审核人ID
        /// </summary>
        public long? HumanReviewerID {get;set;}
        
      

    }
}
