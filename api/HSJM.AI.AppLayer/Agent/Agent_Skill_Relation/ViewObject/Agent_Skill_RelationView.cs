
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:30:47
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 智能体-技能关系表 - 管理智能体技能授权与熟练度
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Agent_Skill_RelationView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  关联智能体角色 (AI_Agent_Role)
        /// </summary>
          [ImporterHeader(Name = "关联智能体角色 (AI_Agent_Role)", Format = ""), ExporterHeader("关联智能体角色 (AI_Agent_Role)")]
        public long? AgentRoleID {get;set;}
        
          /// <summary>
        ///  关联技能 (AI_Skill_Definition)
        /// </summary>
          [ImporterHeader(Name = "关联技能 (AI_Skill_Definition)", Format = ""), ExporterHeader("关联技能 (AI_Skill_Definition)")]
        public long? SkillID {get;set;}
        
          /// <summary>
        ///  熟练度: Novice/Competent/Proficient/Expert
        /// </summary>
          [ImporterHeader(Name = "熟练度: Novice/Competent/Proficient/Expert", Format = ""), ExporterHeader("熟练度: Novice/Competent/Proficient/Expert")]
        public string ProficiencyLevel {get;set;}
        
          /// <summary>
        ///  熟练度积分(0-100)
        /// </summary>
          [ImporterHeader(Name = "熟练度积分(0-100)", Format = ""), ExporterHeader("熟练度积分(0-100)")]
        public int? ProficiencyScore {get;set;}
        
          /// <summary>
        ///  获取时间
        /// </summary>
          [ImporterHeader(Name = "获取时间", Format = ""), ExporterHeader("获取时间")]
        public DateTime? AcquiredDate {get;set;}
        
          /// <summary>
        ///  最后使用时间
        /// </summary>
          [ImporterHeader(Name = "最后使用时间", Format = ""), ExporterHeader("最后使用时间")]
        public DateTime? LastUsedDate {get;set;}
        
          /// <summary>
        ///  执行次数
        /// </summary>
          [ImporterHeader(Name = "执行次数", Format = ""), ExporterHeader("执行次数")]
        public int? ExecutionCount {get;set;}
        
          /// <summary>
        ///  成功次数
        /// </summary>
          [ImporterHeader(Name = "成功次数", Format = ""), ExporterHeader("成功次数")]
        public int? SuccessCount {get;set;}
        
          /// <summary>
        ///  失败次数
        /// </summary>
          [ImporterHeader(Name = "失败次数", Format = ""), ExporterHeader("失败次数")]
        public int? FailureCount {get;set;}
        
          /// <summary>
        ///  总执行时间(ms)
        /// </summary>
          [ImporterHeader(Name = "总执行时间(ms)", Format = ""), ExporterHeader("总执行时间(ms)")]
        public long? TotalExecutionTime {get;set;}
        
          /// <summary>
        ///  针对该智能体的特定配置覆盖JSON
        /// </summary>
          [ImporterHeader(Name = "针对该智能体的特定配置覆盖JSON", Format = ""), ExporterHeader("针对该智能体的特定配置覆盖JSON")]
        public string OverrideConfig {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
          [ImporterHeader(Name = "是否启用", Format = ""), ExporterHeader("是否启用")]
        public bool? IsEnabled {get;set;}
        
     

    }
}
