
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:30:47
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 智能体-技能关系表 - 管理智能体技能授权与熟练度
    /// </summary>
    [SugarTable("AI_Agent_Skill_Relation",tableDescription:"智能体-技能关系表 - 管理智能体技能授权与熟练度")]
    public class Agent_Skill_RelationEntity: BaseField
    {

       public  Agent_Skill_RelationEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  关联智能体角色 (AI_Agent_Role)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联智能体角色 (AI_Agent_Role)")]
        public long? AgentRoleID {get;set;}
        
          /// <summary>
        ///  关联技能 (AI_Skill_Definition)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联技能 (AI_Skill_Definition)")]
        public long? SkillID {get;set;}
        
          /// <summary>
        ///  熟练度: Novice/Competent/Proficient/Expert
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="熟练度: Novice/Competent/Proficient/Expert")]
        public string ProficiencyLevel {get;set;}
        
          /// <summary>
        ///  熟练度积分(0-100)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="熟练度积分(0-100)")]
        public int? ProficiencyScore {get;set;}
        
          /// <summary>
        ///  获取时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="获取时间")]
        public DateTime? AcquiredDate {get;set;}
        
          /// <summary>
        ///  最后使用时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="最后使用时间")]
        public DateTime? LastUsedDate {get;set;}
        
          /// <summary>
        ///  执行次数
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="执行次数")]
        public int? ExecutionCount {get;set;}
        
          /// <summary>
        ///  成功次数
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="成功次数")]
        public int? SuccessCount {get;set;}
        
          /// <summary>
        ///  失败次数
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="失败次数")]
        public int? FailureCount {get;set;}
        
          /// <summary>
        ///  总执行时间(ms)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="总执行时间(ms)")]
        public long? TotalExecutionTime {get;set;}
        
          /// <summary>
        ///  针对该智能体的特定配置覆盖JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="针对该智能体的特定配置覆盖JSON")]
        public string OverrideConfig {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否启用")]
        public bool? IsEnabled {get;set;}
        
     

    }

   
}
