
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:44
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// 技能定义表 - 智能体能力的语义化封装
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Skill_DefinitionView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  技能编码(唯一标识)
        /// </summary>
          [ImporterHeader(Name = "技能编码(唯一标识)", Format = ""), ExporterHeader("技能编码(唯一标识)")]
        public string SkillCode {get;set;}
        
          /// <summary>
        ///  语义化名称(如: 设备状态诊断)
        /// </summary>
          [ImporterHeader(Name = "语义化名称(如: 设备状态诊断)", Format = ""), ExporterHeader("语义化名称(如: 设备状态诊断)")]
        public string SkillName {get;set;}
        
          /// <summary>
        ///  分类: Perception/Analysis/Decision/Execution/Collaboration
        /// </summary>
          [ImporterHeader(Name = "分类: Perception/Analysis/Decision/Execution/Collaboration", Format = ""), ExporterHeader("分类: Perception/Analysis/Decision/Execution/Collaboration")]
        public string Category {get;set;}
        
          /// <summary>
        ///  复杂度: Basic/Intermediate/Advanced/Expert
        /// </summary>
          [ImporterHeader(Name = "复杂度: Basic/Intermediate/Advanced/Expert", Format = ""), ExporterHeader("复杂度: Basic/Intermediate/Advanced/Expert")]
        public string ComplexityLevel {get;set;}
        
          /// <summary>
        ///  技能描述(供AI理解)
        /// </summary>
          [ImporterHeader(Name = "技能描述(供AI理解)", Format = ""), ExporterHeader("技能描述(供AI理解)")]
        public string Description {get;set;}
        
          /// <summary>
        ///  输入参数定义JSON Schema
        /// </summary>
          [ImporterHeader(Name = "输入参数定义JSON Schema", Format = ""), ExporterHeader("输入参数定义JSON Schema")]
        public string InputSchema {get;set;}
        
          /// <summary>
        ///  输出格式定义JSON Schema
        /// </summary>
          [ImporterHeader(Name = "输出格式定义JSON Schema", Format = ""), ExporterHeader("输出格式定义JSON Schema")]
        public string OutputSchema {get;set;}
        
          /// <summary>
        ///  实现类型: Local/HTTP/MCP/Composite
        /// </summary>
          [ImporterHeader(Name = "实现类型: Local/HTTP/MCP/Composite", Format = ""), ExporterHeader("实现类型: Local/HTTP/MCP/Composite")]
        public string ImplementationType {get;set;}
        
          /// <summary>
        ///  实现引用: 程序集类名/URL/MCP服务名
        /// </summary>
          [ImporterHeader(Name = "实现引用: 程序集类名/URL/MCP服务名", Format = ""), ExporterHeader("实现引用: 程序集类名/URL/MCP服务名")]
        public string ImplementationRef {get;set;}
        
          /// <summary>
        ///  关联底层工具ID (AI_Tool_Definition)
        /// </summary>
          [ImporterHeader(Name = "关联底层工具ID (AI_Tool_Definition)", Format = ""), ExporterHeader("关联底层工具ID (AI_Tool_Definition)")]
        public long? ToolID {get;set;}
        
          /// <summary>
        ///  是否复合技能
        /// </summary>
          [ImporterHeader(Name = "是否复合技能", Format = ""), ExporterHeader("是否复合技能")]
        public bool? IsComposite {get;set;}
        
          /// <summary>
        ///  前置技能依赖JSON
        /// </summary>
          [ImporterHeader(Name = "前置技能依赖JSON", Format = ""), ExporterHeader("前置技能依赖JSON")]
        public string PrerequisiteSkills {get;set;}
        
          /// <summary>
        ///  组合技能列表JSON
        /// </summary>
          [ImporterHeader(Name = "组合技能列表JSON", Format = ""), ExporterHeader("组合技能列表JSON")]
        public string ComposedSkills {get;set;}
        
          /// <summary>
        ///  组合执行逻辑JSON
        /// </summary>
          [ImporterHeader(Name = "组合执行逻辑JSON", Format = ""), ExporterHeader("组合执行逻辑JSON")]
        public string CompositionLogic {get;set;}
        
          /// <summary>
        ///  使用统计
        /// </summary>
          [ImporterHeader(Name = "使用统计", Format = ""), ExporterHeader("使用统计")]
        public int? UsageCount {get;set;}
        
          /// <summary>
        ///  成功次数
        /// </summary>
          [ImporterHeader(Name = "成功次数", Format = ""), ExporterHeader("成功次数")]
        public int? SuccessCount {get;set;}
        
          /// <summary>
        ///  成功率
        /// </summary>
          [ImporterHeader(Name = "成功率", Format = ""), ExporterHeader("成功率")]
        public decimal? SuccessRate {get;set;}
        
          /// <summary>
        ///  平均执行时间(ms)
        /// </summary>
          [ImporterHeader(Name = "平均执行时间(ms)", Format = ""), ExporterHeader("平均执行时间(ms)")]
        public int? AvgExecutionTime {get;set;}
        
          /// <summary>
        ///  最后执行时间
        /// </summary>
          [ImporterHeader(Name = "最后执行时间", Format = ""), ExporterHeader("最后执行时间")]
        public DateTime? LastExecutionTime {get;set;}
        
          /// <summary>
        ///  是否需要人工确认(高危技能)
        /// </summary>
          [ImporterHeader(Name = "是否需要人工确认(高危技能)", Format = ""), ExporterHeader("是否需要人工确认(高危技能)")]
        public bool? RequiresConfirmation {get;set;}
        
          /// <summary>
        ///  安全等级: 1-信息提供, 2-建议推荐, 3-半自动控制, 4-全自动控制
        /// </summary>
          [ImporterHeader(Name = "安全等级: 1-信息提供, 2-建议推荐, 3-半自动控制, 4-全自动控制", Format = ""), ExporterHeader("安全等级: 1-信息提供, 2-建议推荐, 3-半自动控制, 4-全自动控制")]
        public int? SafetyLevel {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
          [ImporterHeader(Name = "是否启用", Format = ""), ExporterHeader("是否启用")]
        public bool? IsEnabled {get;set;}
        
          /// <summary>
        ///  版本号
        /// </summary>
          [ImporterHeader(Name = "版本号", Format = ""), ExporterHeader("版本号")]
        public int? Version {get;set;}
        
     

    }
}
