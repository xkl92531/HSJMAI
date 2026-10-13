

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:50
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Prompt.DTOS
{
    /// <summary>
    /// 记录 Prompt 拼装过程，方便复现最终 Prompt 及所选模板/片段。
    /// </summary>
    public class Prompt_Assembly_LogUpdateDTO
    {
            /// <summary>
        ///  记录主键。
        /// </summary>
        [Description("记录主键。")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  场景标识，例如 Parsing、Diagnosis、Planning。
        /// </summary>
        [Description("场景标识，例如 Parsing、Diagnosis、Planning。")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string Scenario {get;set;}
        
          /// <summary>
        ///  品牌或客户筛选条件。
        /// </summary>
        [Description("品牌或客户筛选条件。")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Brand {get;set;}
        
          /// <summary>
        ///  Prompt 输出语言。
        /// </summary>
        [Description("Prompt 输出语言。")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Language {get;set;}
        
          /// <summary>
        ///  任务动作，例如 ParseBlock/Diagnose。
        /// </summary>
        [Description("任务动作，例如 ParseBlock/Diagnose。")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string Action {get;set;}
        
          /// <summary>
        ///  使用的模板 ID。
        /// </summary>
        [Description("使用的模板 ID。")]
                  [Range(0, long.MaxValue)]
                  public long? TemplateID {get;set;}
        
          /// <summary>
        ///  拼装的片段 ID 列表。
        /// </summary>
        [Description("拼装的片段 ID 列表。")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string FragmentIDs {get;set;}
        
          /// <summary>
        ///  Few-shot 示例 ID 列表。
        /// </summary>
        [Description("Few-shot 示例 ID 列表。")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string FewShotIDs {get;set;}
        
          /// <summary>
        ///  最终发送给模型的 Prompt 文本。
        /// </summary>
        [Description("最终发送给模型的 Prompt 文本。")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(-1)]
                  public string FinalPrompt {get;set;}
        
          /// <summary>
        ///  最终 Prompt 的 token 数。
        /// </summary>
        [Description("最终 Prompt 的 token 数。")]
                  [Range(0, int.MaxValue)]
                  public int? PromptTokenCount {get;set;}
        
          /// <summary>
        ///  拼装耗时（毫秒）。
        /// </summary>
        [Description("拼装耗时（毫秒）。")]
                  [Range(0, int.MaxValue)]
                  public int? AssemblyTimeMs {get;set;}
        
          /// <summary>
        ///  回退层级：0 精确、1 一级、2 二级。
        /// </summary>
        [Description("回退层级：0 精确、1 一级、2 二级。")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? FallbackLevel {get;set;}
        
          /// <summary>
        ///  关联的模型运行日志 ID。
        /// </summary>
        [Description("关联的模型运行日志 ID。")]
                  [Range(0, long.MaxValue)]
                  public long? RuntimeLogID {get;set;}
        
          /// <summary>
        ///  备注信息。
        /// </summary>
        [Description("备注信息。")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string Remark {get;set;}
        
     

    }
}
