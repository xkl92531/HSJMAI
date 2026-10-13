
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:25:03
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 工具生成请求表 - 管理智能体自动创建新工具的请求与审批流程
    /// </summary>
    [SugarTable("AI_Tool_Generation_Request",tableDescription:"工具生成请求表 - 管理智能体自动创建新工具的请求与审批流程")]
    public class Tool_Generation_RequestEntity: BaseField
    {

       public  Tool_Generation_RequestEntity()
       {
                    Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                         }
         [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
       public long Id{get;set;}
            /// <summary>
        ///  请求生成工具的智能体ID (AI_Agent_Role)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="请求生成工具的智能体ID (AI_Agent_Role)")]
        public long? RequestorAgentID {get;set;}
        
          /// <summary>
        ///  触发该请求的任务ID(可选, 用于追溯上下文)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="触发该请求的任务ID(可选, 用于追溯上下文)")]
        public long? RequestorTaskID {get;set;}
        
          /// <summary>
        ///  工具用途描述(用于生成 Prompt)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="工具用途描述(用于生成 Prompt)")]
        public string ToolPurpose {get;set;}
        
          /// <summary>
        ///  期望的输入格式 JSON Schema
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="期望的输入格式 JSON Schema")]
        public string InputSchema {get;set;}
        
          /// <summary>
        ///  期望的输出格式 JSON Schema
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="期望的输出格式 JSON Schema")]
        public string OutputSchema {get;set;}
        
          /// <summary>
        ///  首选语言: CSharp/Python/JavaScript
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="首选语言: CSharp/Python/JavaScript")]
        public string PreferredLanguage {get;set;}
        
          /// <summary>
        ///  所需能力列表 JSON: ["FileAccess", "NetworkAccess", "DatabaseAccess"]
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="所需能力列表 JSON: [FileAccess, NetworkAccess, DatabaseAccess]")]
        public string RequiredCapabilities {get;set;}
        
          /// <summary>
        ///  LLM 生成的代码
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="LLM 生成的代码")]
        public string GeneratedCode {get;set;}
        
          /// <summary>
        ///  生成使用的模型(如: gpt-4)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="生成使用的模型(如: gpt-4)")]
        public string GenerationModel {get;set;}
        
          /// <summary>
        ///  完整生成 Prompt(用于审计和复现)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="完整生成 Prompt(用于审计和复现)")]
        public string GenerationPrompt {get;set;}
        
          /// <summary>
        ///  代码生成时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="代码生成时间")]
        public DateTime? GenerationTime {get;set;}
        
          /// <summary>
        ///  测试用例 JSON: [{input, expectedOutput}]
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="测试用例 JSON: [{input, expectedOutput}]")]
        public string TestCases {get;set;}
        
          /// <summary>
        ///  测试结果 JSON: {passed, failed, errors}
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="测试结果 JSON: {passed, failed, errors}")]
        public string TestResult {get;set;}
        
          /// <summary>
        ///  安全扫描结果 JSON: {vulnerabilities, warnings}
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="安全扫描结果 JSON: {vulnerabilities, warnings}")]
        public string SecurityScanResult {get;set;}
        
          /// <summary>
        ///  状态: Requested(已请求)/Generating(生成中)/Generated(已生成)/Testing(测试中)/PendingApproval(待审批)/Approved(已批准)/Rejected(已拒绝)/Deployed(已部署)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="状态: Requested(已请求)/Generating(生成中)/Generated(已生成)/Testing(测试中)/PendingApproval(待审批)/Approved(已批准)/Rejected(已拒绝)/Deployed(已部署)")]
        public string Status {get;set;}
        
          /// <summary>
        ///  审批人ID
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批人ID")]
        public long? ApproverID {get;set;}
        
          /// <summary>
        ///  审批时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批时间")]
        public DateTime? ApprovalTime {get;set;}
        
          /// <summary>
        ///  审批意见
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="审批意见")]
        public string ApprovalComment {get;set;}
        
          /// <summary>
        ///  生成后的工具ID (AI_Tool_Definition)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="生成后的工具ID (AI_Tool_Definition)")]
        public long? GeneratedToolID {get;set;}
        
          /// <summary>
        ///  生成后的技能ID (AI_Skill_Definition, 可选)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="生成后的技能ID (AI_Skill_Definition, 可选)")]
        public long? GeneratedSkillID {get;set;}
        
          /// <summary>
        ///  部署时间
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="部署时间")]
        public DateTime? DeploymentTime {get;set;}
        
          /// <summary>
        ///  代码质量得分(0-100)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="代码质量得分(0-100)")]
        public decimal? QualityScore {get;set;}
        
          /// <summary>
        ///  性能得分(0-100)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="性能得分(0-100)")]
        public decimal? PerformanceScore {get;set;}
        
          /// <summary>
        ///  安全得分(0-100)
        /// </summary>
                   [SugarColumn(IsNullable = true,ColumnDescription ="安全得分(0-100)")]
        public decimal? SecurityScore {get;set;}
        
     

    }

   
}
