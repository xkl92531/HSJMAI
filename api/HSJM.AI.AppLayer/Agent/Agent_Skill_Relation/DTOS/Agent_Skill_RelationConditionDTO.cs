

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:30:47
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class Agent_Skill_RelationConditionDTO
    {
            /// <summary>
        ///  关联智能体角色 (AI_Agent_Role)
        /// </summary>
        public long? AgentRoleID {get;set;}
        
           /// <summary>
        ///  关联技能 (AI_Skill_Definition)
        /// </summary>
        public long? SkillID {get;set;}
        
           /// <summary>
        ///  熟练度: Novice/Competent/Proficient/Expert
        /// </summary>
        public string ProficiencyLevel {get;set;}
        
           /// <summary>
        ///  熟练度积分(0-100)
        /// </summary>
        public int? ProficiencyScore {get;set;}
        
           /// <summary>
        ///  最后使用时间起始时间
        /// </summary>
        public DateTime? LastUsedDateStart {get;set;}
        
           /// <summary>
        ///  最后使用时间结束时间
        /// </summary>
        public DateTime? LastUsedDateEnd {get;set;}
        
           /// <summary>
        ///  是否启用
        /// </summary>
        public bool? IsEnabled {get;set;}
        
      

    }
}
