

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:24:25
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class Agent_RelationConditionDTO
    {
            /// <summary>
        ///  父智能体ID (Manager/Supervisor)
        /// </summary>
        public long? ParentRoleID {get;set;}
        
           /// <summary>
        ///  子智能体ID (Worker/Subordinate)
        /// </summary>
        public long? ChildRoleID {get;set;}
        
           /// <summary>
        ///  关系类型: Supervisor, Member, Delegate
        /// </summary>
        public string RelationType {get;set;}
        
           /// <summary>
        ///  关系描述
        /// </summary>
        public string Description {get;set;}
        
      

    }
}
