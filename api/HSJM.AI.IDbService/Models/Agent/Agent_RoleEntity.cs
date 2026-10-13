
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:27:12
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 智能体角色配置表
    /// </summary>
    [SugarTable("AI_Agent_Role",tableDescription:"智能体角色配置表")]
    public class Agent_RoleEntity: BaseField
    {

       public  Agent_RoleEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true, IsIdentity =true)]
       public long Id{get;set;}
            /// <summary>
        ///  角色名称
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="角色名称")]
        public string RoleName {get;set;}
        
          /// <summary>
        ///  角色编码(唯一标识)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="角色编码(唯一标识)")]
        public string RoleCode {get;set;}
        
          /// <summary>
        ///  角色描述
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="角色描述")]
        public string Description {get;set;}
        
          /// <summary>
        ///  角色头像URL
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="角色头像URL")]
        public string AvatarUrl {get;set;}
        
          /// <summary>
        ///  默认欢迎语
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="默认欢迎语")]
        public string WelcomeMessage {get;set;}
        
          /// <summary>
        ///  系统提示词(Persona)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="系统提示词(Persona)")]
        public string SystemPrompt {get;set;}
        
          /// <summary>
        ///  关联的大模型ID (AI_LLM_Model)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联的大模型ID (AI_LLM_Model)")]
        public long? ModelID {get;set;}
        
          /// <summary>
        ///  模型特定参数覆盖JSON(TemperatureTopP等)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="模型特定参数覆盖JSON(TemperatureTopP等)")]
        public string ModelParams {get;set;}
        
          /// <summary>
        ///  智能体类型: Standard(通用), Workflow(工作流), Composite(组合), Code(代码定制)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="智能体类型: Standard(通用), Workflow(工作流), Composite(组合), Code(代码定制)")]
        public string AgentType {get;set;}
        
          /// <summary>
        ///  智能体行为配置JSON(MaxSteps, MemoryStrategy, PlanningMethod等)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="智能体行为配置JSON(MaxSteps, MemoryStrategy, PlanningMethod等)")]
        public string AgentConfig {get;set;}
        
          /// <summary>
        ///  代码实现类全名(用于Code类型智能体)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="代码实现类全名(用于Code类型智能体)")]
        public string ImplementationClass {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否启用")]
        public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  是否公开(所有用户可见)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否公开(所有用户可见)")]
        public bool? IsPublic {get;set;}
        
          /// <summary>
        ///  版本号
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="版本号")]
        public int? Version {get;set;}
        
     

    }

   
}
