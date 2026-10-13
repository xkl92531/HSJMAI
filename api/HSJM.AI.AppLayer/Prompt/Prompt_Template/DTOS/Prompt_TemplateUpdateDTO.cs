

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:18
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Prompt.DTOS
{
    /// <summary>
    /// 提示词模板
    /// </summary>
    public class Prompt_TemplateUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  应用场景（Parsing/Diagnosis 等）
        /// </summary>
        [Description("应用场景（Parsing/Diagnosis 等）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string Scenario {get;set;}
        
          /// <summary>
        ///  适用品牌
        /// </summary>
        [Description("适用品牌")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Brand {get;set;}
        
          /// <summary>
        ///  适用型号/系列
        /// </summary>
        [Description("适用型号/系列")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Model {get;set;}
        
          /// <summary>
        ///  语言（指令/自然语言）
        /// </summary>
        [Description("语言（指令/自然语言）")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Language {get;set;}
        
          /// <summary>
        ///  适用块类型 OB/FB/FC/DB 等
        /// </summary>
        [Description("适用块类型 OB/FB/FC/DB 等")]
                  [MinLength(0)]
                   [MaxLength(20)]
                  public string BlockType {get;set;}
        
          /// <summary>
        ///  操作/任务类型
        /// </summary>
        [Description("操作/任务类型")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Action {get;set;}
        
          /// <summary>
        ///  模板唯一键
        /// </summary>
        [Description("模板唯一键")]
                  [MinLength(0)]
                   [MaxLength(200)]
                  public string PromptKey {get;set;}
        
          /// <summary>
        ///  模板版本号
        /// </summary>
        [Description("模板版本号")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string Version {get;set;}
        
          /// <summary>
        ///  父模板ID
        /// </summary>
        [Description("父模板ID")]
                  [Range(0, long.MaxValue)]
                  public long? ParentTemplateID {get;set;}
        
          /// <summary>
        ///  回退层级（数字越大越靠后）
        /// </summary>
        [Description("回退层级（数字越大越靠后）")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? FallbackLevel {get;set;}
        
          /// <summary>
        ///  优先级（大值优先）
        /// </summary>
        [Description("优先级（大值优先）")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? Priority {get;set;}
        
          /// <summary>
        ///  状态 0-草稿 1-启用 2-停用
        /// </summary>
        [Description("状态 0-草稿 1-启用 2-停用")]
                  [Required]
                   [Range(0, int.MaxValue)]
                  public int? Status {get;set;}
        
          /// <summary>
        ///  提示词正文内容
        /// </summary>
        [Description("提示词正文内容")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(-1)]
                  public string Content {get;set;}
        
          /// <summary>
        ///  约束配置 JSON
        /// </summary>
        [Description("约束配置 JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string ConstraintsJson {get;set;}
        
          /// <summary>
        ///  输出结构定义
        /// </summary>
        [Description("输出结构定义")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string OutputSchema {get;set;}
        
          /// <summary>
        ///  扩展元数据 JSON
        /// </summary>
        [Description("扩展元数据 JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string MetadataJson {get;set;}
        
     

    }
}
