

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:44
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 技能定义表 - 智能体能力的语义化封装
    /// </summary>
    public class Skill_DefinitionUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  技能编码(唯一标识)
        /// </summary>
        [Description("技能编码(唯一标识)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string SkillCode {get;set;}
        
          /// <summary>
        ///  语义化名称(如: 设备状态诊断)
        /// </summary>
        [Description("语义化名称(如: 设备状态诊断)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(100)]
                  public string SkillName {get;set;}
        
          /// <summary>
        ///  分类: Perception/Analysis/Decision/Execution/Collaboration
        /// </summary>
        [Description("分类: Perception/Analysis/Decision/Execution/Collaboration")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(30)]
                  public string Category {get;set;}
        
          /// <summary>
        ///  复杂度: Basic/Intermediate/Advanced/Expert
        /// </summary>
        [Description("复杂度: Basic/Intermediate/Advanced/Expert")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string ComplexityLevel {get;set;}
        
          /// <summary>
        ///  技能描述(供AI理解)
        /// </summary>
        [Description("技能描述(供AI理解)")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Description {get;set;}
        
          /// <summary>
        ///  输入参数定义JSON Schema
        /// </summary>
        [Description("输入参数定义JSON Schema")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string InputSchema {get;set;}
        
          /// <summary>
        ///  输出格式定义JSON Schema
        /// </summary>
        [Description("输出格式定义JSON Schema")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string OutputSchema {get;set;}
        
          /// <summary>
        ///  实现类型: Local/HTTP/MCP/Composite
        /// </summary>
        [Description("实现类型: Local/HTTP/MCP/Composite")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string ImplementationType {get;set;}
        
          /// <summary>
        ///  实现引用: 程序集类名/URL/MCP服务名
        /// </summary>
        [Description("实现引用: 程序集类名/URL/MCP服务名")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ImplementationRef {get;set;}
        
          /// <summary>
        ///  关联底层工具ID (AI_Tool_Definition)
        /// </summary>
        [Description("关联底层工具ID (AI_Tool_Definition)")]
                  [Range(0, long.MaxValue)]
                  public long? ToolID {get;set;}
        
          /// <summary>
        ///  是否复合技能
        /// </summary>
        [Description("是否复合技能")]
                  [Required]
                  public bool? IsComposite {get;set;}
        
          /// <summary>
        ///  前置技能依赖JSON
        /// </summary>
        [Description("前置技能依赖JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string PrerequisiteSkills {get;set;}
        
          /// <summary>
        ///  组合技能列表JSON
        /// </summary>
        [Description("组合技能列表JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ComposedSkills {get;set;}
        
          /// <summary>
        ///  组合执行逻辑JSON
        /// </summary>
        [Description("组合执行逻辑JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string CompositionLogic {get;set;}
        
          /// <summary>
        ///  使用统计
        /// </summary>
        [Description("使用统计")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? UsageCount {get;set;}
        
          /// <summary>
        ///  成功次数
        /// </summary>
        [Description("成功次数")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? SuccessCount {get;set;}
        
          /// <summary>
        ///  成功率
        /// </summary>
        [Description("成功率")]
                  [Range(0, double.MaxValue)]
                  public decimal? SuccessRate {get;set;}
        
          /// <summary>
        ///  平均执行时间(ms)
        /// </summary>
        [Description("平均执行时间(ms)")]
                  [Range(0, int.MaxValue)]
                  public int? AvgExecutionTime {get;set;}
        
          /// <summary>
        ///  最后执行时间
        /// </summary>
        [Description("最后执行时间")]
                 public DateTime? LastExecutionTime {get;set;}
        
          /// <summary>
        ///  是否需要人工确认(高危技能)
        /// </summary>
        [Description("是否需要人工确认(高危技能)")]
                  [Required]
                  public bool? RequiresConfirmation {get;set;}
        
          /// <summary>
        ///  安全等级: 1-信息提供, 2-建议推荐, 3-半自动控制, 4-全自动控制
        /// </summary>
        [Description("安全等级: 1-信息提供, 2-建议推荐, 3-半自动控制, 4-全自动控制")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? SafetyLevel {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
        [Description("是否启用")]
                  [Required]
                  public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  版本号
        /// </summary>
        [Description("版本号")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? Version {get;set;}
        
     

    }
}
