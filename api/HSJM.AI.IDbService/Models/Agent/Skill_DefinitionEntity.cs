
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:44
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 技能定义表 - 智能体能力的语义化封装
    /// </summary>
    [SugarTable("AI_Skill_Definition",tableDescription:"技能定义表 - 智能体能力的语义化封装")]
    public class Skill_DefinitionEntity: BaseField
    {

       public  Skill_DefinitionEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  技能编码(唯一标识)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="技能编码(唯一标识)")]
        public string SkillCode {get;set;}
        
          /// <summary>
        ///  语义化名称(如: 设备状态诊断)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="语义化名称(如: 设备状态诊断)")]
        public string SkillName {get;set;}
        
          /// <summary>
        ///  分类: Perception/Analysis/Decision/Execution/Collaboration
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="分类: Perception/Analysis/Decision/Execution/Collaboration")]
        public string Category {get;set;}
        
          /// <summary>
        ///  复杂度: Basic/Intermediate/Advanced/Expert
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="复杂度: Basic/Intermediate/Advanced/Expert")]
        public string ComplexityLevel {get;set;}
        
          /// <summary>
        ///  技能描述(供AI理解)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="技能描述(供AI理解)")]
        public string Description {get;set;}
        
          /// <summary>
        ///  输入参数定义JSON Schema
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输入参数定义JSON Schema")]
        public string InputSchema {get;set;}
        
          /// <summary>
        ///  输出格式定义JSON Schema
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="输出格式定义JSON Schema")]
        public string OutputSchema {get;set;}
        
          /// <summary>
        ///  实现类型: Local/HTTP/MCP/Composite
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="实现类型: Local/HTTP/MCP/Composite")]
        public string ImplementationType {get;set;}
        
          /// <summary>
        ///  实现引用: 程序集类名/URL/MCP服务名
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="实现引用: 程序集类名/URL/MCP服务名")]
        public string ImplementationRef {get;set;}
        
          /// <summary>
        ///  关联底层工具ID (AI_Tool_Definition)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="关联底层工具ID (AI_Tool_Definition)")]
        public long? ToolID {get;set;}
        
          /// <summary>
        ///  是否复合技能
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否复合技能")]
        public bool? IsComposite {get;set;}
        
          /// <summary>
        ///  前置技能依赖JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="前置技能依赖JSON")]
        public string PrerequisiteSkills {get;set;}
        
          /// <summary>
        ///  组合技能列表JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="组合技能列表JSON")]
        public string ComposedSkills {get;set;}
        
          /// <summary>
        ///  组合执行逻辑JSON
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="组合执行逻辑JSON")]
        public string CompositionLogic {get;set;}
        
          /// <summary>
        ///  使用统计
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="使用统计")]
        public int? UsageCount {get;set;}
        
          /// <summary>
        ///  成功次数
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="成功次数")]
        public int? SuccessCount {get;set;}
        
          /// <summary>
        ///  成功率
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="成功率")]
        public decimal? SuccessRate {get;set;}
        
          /// <summary>
        ///  平均执行时间(ms)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="平均执行时间(ms)")]
        public int? AvgExecutionTime {get;set;}
        
          /// <summary>
        ///  最后执行时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="最后执行时间")]
        public DateTime? LastExecutionTime {get;set;}
        
          /// <summary>
        ///  是否需要人工确认(高危技能)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否需要人工确认(高危技能)")]
        public bool? RequiresConfirmation {get;set;}
        
          /// <summary>
        ///  安全等级: 1-信息提供, 2-建议推荐, 3-半自动控制, 4-全自动控制
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="安全等级: 1-信息提供, 2-建议推荐, 3-半自动控制, 4-全自动控制")]
        public int? SafetyLevel {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="是否启用")]
        public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  版本号
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="版本号")]
        public int? Version {get;set;}
        
        /// <summary>
        /// 是否为Agent Skill类型（遵循agentskills规范的SKILL.md格式）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否为Agent Skill类型")]
        public bool? IsAgentSkill { get; set; }

        /// <summary>
        /// 技能包目录路径（相对于站点skills目录，如：plc-analyzer/）
        /// Agent Skill的SKILL.md和脚本都在此目录下
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 500, ColumnDescription = "技能包目录路径")]
        public string? SkillPackagePath { get; set; }

        /// <summary>
        /// 审批状态: Pending(待审批), Approved(已批准), Rejected(已拒绝)
        /// 用于动态生成的技能需要人工审批后才能使用
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 20, ColumnDescription = "审批状态: Pending/Approved/Rejected")]
        public string? ApprovalStatus { get; set; }

        /// <summary>
        /// 生成此技能的任务ID（AI_Agent_Task）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "生成此技能的任务ID")]
        public long? GeneratedByTaskID { get; set; }

        /// <summary>
        /// 生成此技能的智能体角色ID（AI_Agent_Role）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "生成此技能的智能体角色ID")]
        public long? GeneratedByAgentID { get; set; }

        /// <summary>
        /// 适用的PLC品牌（Siemens/Mitsubishi/AB等）
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 100, ColumnDescription = "适用的PLC品牌")]
        public string? ApplicableBrand { get; set; }

        /// <summary>
        /// 适用的文件格式（XML/AWL/SCL等）
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 100, ColumnDescription = "适用的文件格式")]
        public string? ApplicableFileFormat { get; set; }

    }

   
}
