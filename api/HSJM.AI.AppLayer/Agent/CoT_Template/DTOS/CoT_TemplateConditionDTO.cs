

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/15 15:23:26
using System;
using System.Collections.Generic;
namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 
    /// </summary>
    public class CoT_TemplateConditionDTO
    {
            /// <summary>
        ///  分类: CodeReview(代码审查)/Diagnosis(设备诊断)/Planning(任务规划)/Analysis(数据分析)/Other(其他)
        /// </summary>
        public string Category {get;set;}
        
           /// <summary>
        ///  模板名称(唯一标识)
        /// </summary>
        public string TemplateName {get;set;}
        
           /// <summary>
        ///  是否启用
        /// </summary>
        public bool? IsEnabled {get;set;}
        
           /// <summary>
        ///  模板描述
        /// </summary>
        public string Description {get;set;}
        
      

    }
}
