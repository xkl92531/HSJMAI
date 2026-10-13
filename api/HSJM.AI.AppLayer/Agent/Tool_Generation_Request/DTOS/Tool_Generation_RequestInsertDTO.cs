

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:25:03
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 工具生成请求表 - 管理智能体自动创建新工具的请求与审批流程
    /// </summary>
    public class Tool_Generation_RequestInsertDTO
    {
            /// <summary>
        ///  请求生成工具的智能体ID (AI_Agent_Role)
        /// </summary>
        [Description("请求生成工具的智能体ID (AI_Agent_Role)")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long? RequestorAgentID {get;set;}
        
          /// <summary>
        ///  触发该请求的任务ID(可选, 用于追溯上下文)
        /// </summary>
        [Description("触发该请求的任务ID(可选, 用于追溯上下文)")]
                  [Range(0, long.MaxValue)]
                  public long? RequestorTaskID {get;set;}
        
          /// <summary>
        ///  工具用途描述(用于生成 Prompt)
        /// </summary>
        [Description("工具用途描述(用于生成 Prompt)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(500)]
                  public string ToolPurpose {get;set;}
        
          /// <summary>
        ///  期望的输入格式 JSON Schema
        /// </summary>
        [Description("期望的输入格式 JSON Schema")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string InputSchema {get;set;}
        
          /// <summary>
        ///  期望的输出格式 JSON Schema
        /// </summary>
        [Description("期望的输出格式 JSON Schema")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string OutputSchema {get;set;}
        
          /// <summary>
        ///  首选语言: CSharp/Python/JavaScript
        /// </summary>
        [Description("首选语言: CSharp/Python/JavaScript")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string PreferredLanguage {get;set;}
        
          /// <summary>
        ///  所需能力列表 JSON: ["FileAccess", "NetworkAccess", "DatabaseAccess"]
        /// </summary>
        [Description("所需能力列表 JSON: [\"FileAccess\", \"NetworkAccess\", \"DatabaseAccess\"]")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string RequiredCapabilities {get;set;}
        
          /// <summary>
        ///  LLM 生成的代码
        /// </summary>
        [Description("LLM 生成的代码")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string GeneratedCode {get;set;}
        
          /// <summary>
        ///  生成使用的模型(如: gpt-4)
        /// </summary>
        [Description("生成使用的模型(如: gpt-4)")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string GenerationModel {get;set;}
        
          /// <summary>
        ///  完整生成 Prompt(用于审计和复现)
        /// </summary>
        [Description("完整生成 Prompt(用于审计和复现)")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string GenerationPrompt {get;set;}
        
          /// <summary>
        ///  代码生成时间
        /// </summary>
        [Description("代码生成时间")]
                 public DateTime? GenerationTime {get;set;}
        
          /// <summary>
        ///  测试用例 JSON: [{input, expectedOutput}]
        /// </summary>
        [Description("测试用例 JSON: [{input, expectedOutput}]")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string TestCases {get;set;}
        
          /// <summary>
        ///  测试结果 JSON: {passed, failed, errors}
        /// </summary>
        [Description("测试结果 JSON: {passed, failed, errors}")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string TestResult {get;set;}
        
          /// <summary>
        ///  安全扫描结果 JSON: {vulnerabilities, warnings}
        /// </summary>
        [Description("安全扫描结果 JSON: {vulnerabilities, warnings}")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string SecurityScanResult {get;set;}
        
          /// <summary>
        ///  状态: Requested(已请求)/Generating(生成中)/Generated(已生成)/Testing(测试中)/PendingApproval(待审批)/Approved(已批准)/Rejected(已拒绝)/Deployed(已部署)
        /// </summary>
        [Description("状态: Requested(已请求)/Generating(生成中)/Generated(已生成)/Testing(测试中)/PendingApproval(待审批)/Approved(已批准)/Rejected(已拒绝)/Deployed(已部署)")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(20)]
                  public string Status {get;set;}
        
          /// <summary>
        ///  审批人ID
        /// </summary>
        [Description("审批人ID")]
                  [Range(0, long.MaxValue)]
                  public long? ApproverID {get;set;}
        
          /// <summary>
        ///  审批时间
        /// </summary>
        [Description("审批时间")]
                 public DateTime? ApprovalTime {get;set;}
        
          /// <summary>
        ///  审批意见
        /// </summary>
        [Description("审批意见")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string ApprovalComment {get;set;}
        
          /// <summary>
        ///  生成后的工具ID (AI_Tool_Definition)
        /// </summary>
        [Description("生成后的工具ID (AI_Tool_Definition)")]
                  [Range(0, long.MaxValue)]
                  public long? GeneratedToolID {get;set;}
        
          /// <summary>
        ///  生成后的技能ID (AI_Skill_Definition, 可选)
        /// </summary>
        [Description("生成后的技能ID (AI_Skill_Definition, 可选)")]
                  [Range(0, long.MaxValue)]
                  public long? GeneratedSkillID {get;set;}
        
          /// <summary>
        ///  部署时间
        /// </summary>
        [Description("部署时间")]
                 public DateTime? DeploymentTime {get;set;}
        
          /// <summary>
        ///  代码质量得分(0-100)
        /// </summary>
        [Description("代码质量得分(0-100)")]
                  [Range(0, double.MaxValue)]
                  public decimal? QualityScore {get;set;}
        
          /// <summary>
        ///  性能得分(0-100)
        /// </summary>
        [Description("性能得分(0-100)")]
                  [Range(0, double.MaxValue)]
                  public decimal? PerformanceScore {get;set;}
        
          /// <summary>
        ///  安全得分(0-100)
        /// </summary>
        [Description("安全得分(0-100)")]
                  [Range(0, double.MaxValue)]
                  public decimal? SecurityScore {get;set;}
        
     

    }
}
