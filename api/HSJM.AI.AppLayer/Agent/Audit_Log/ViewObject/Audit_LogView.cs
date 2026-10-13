
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:33:39
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Agent.ViewObject
{
    /// <summary>
    /// AI决策审计日志表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Audit_LogView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  动作类型(参数调整/停机/预警)
        /// </summary>
          [ImporterHeader(Name = "动作类型(参数调整/停机/预警)", Format = ""), ExporterHeader("动作类型(参数调整/停机/预警)")]
        public string ActionType {get;set;}
        
          /// <summary>
        ///  目标对象ID(如设备ID)
        /// </summary>
          [ImporterHeader(Name = "目标对象ID(如设备ID)", Format = ""), ExporterHeader("目标对象ID(如设备ID)")]
        public string TargetObjID {get;set;}
        
          /// <summary>
        ///  智能体ID
        /// </summary>
          [ImporterHeader(Name = "智能体ID", Format = ""), ExporterHeader("智能体ID")]
        public long? AgentRoleID {get;set;}
        
          /// <summary>
        ///  决策内容/建议
        /// </summary>
          [ImporterHeader(Name = "决策内容/建议", Format = ""), ExporterHeader("决策内容/建议")]
        public string DecisionContent {get;set;}
        
          /// <summary>
        ///  决策时的数据快照JSON
        /// </summary>
          [ImporterHeader(Name = "决策时的数据快照JSON", Format = ""), ExporterHeader("决策时的数据快照JSON")]
        public string SnapshotData {get;set;}
        
          /// <summary>
        ///  风险等级: 1-低, 2-中, 3-高
        /// </summary>
          [ImporterHeader(Name = "风险等级: 1-低, 2-中, 3-高", Format = ""), ExporterHeader("风险等级: 1-低, 2-中, 3-高")]
        public int? RiskLevel {get;set;}
        
          /// <summary>
        ///  审核人ID
        /// </summary>
          [ImporterHeader(Name = "审核人ID", Format = ""), ExporterHeader("审核人ID")]
        public long? HumanReviewerID {get;set;}
        
          /// <summary>
        ///  审核状态: 0-无需审核, 1-待审核, 2-已通过, 3-已驳回
        /// </summary>
          [ImporterHeader(Name = "审核状态: 0-无需审核, 1-待审核, 2-已通过, 3-已驳回", Format = ""), ExporterHeader("审核状态: 0-无需审核, 1-待审核, 2-已通过, 3-已驳回")]
        public int? ReviewStatus {get;set;}
        
          /// <summary>
        ///  审核意见
        /// </summary>
          [ImporterHeader(Name = "审核意见", Format = ""), ExporterHeader("审核意见")]
        public string ReviewComment {get;set;}
        
          /// <summary>
        ///  客户端IP
        /// </summary>
          [ImporterHeader(Name = "客户端IP", Format = ""), ExporterHeader("客户端IP")]
        public string ClientIP {get;set;}
        
          /// <summary>
        ///  执行耗时(ms)
        /// </summary>
          [ImporterHeader(Name = "执行耗时(ms)", Format = ""), ExporterHeader("执行耗时(ms)")]
        public int? ExecutionDuration {get;set;}
        
     

    }
}
