

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:34:56
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Prompt.DTOS
{
    /// <summary>
    /// Few-shot 样例
    /// </summary>
    public class Prompt_FewShotUpdateDTO
    {
            /// <summary>
        ///  主键ID
        /// </summary>
        [Description("主键ID")]
                  [Required]
                   [Range(0, long.MaxValue)]
                  public long ID {get;set;}
        
          /// <summary>
        ///  应用场景
        /// </summary>
        [Description("应用场景")]
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
        ///  语言
        /// </summary>
        [Description("语言")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string Language {get;set;}
        
          /// <summary>
        ///  块类型
        /// </summary>
        [Description("块类型")]
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
        ///  样例类型（正例/反例等）
        /// </summary>
        [Description("样例类型（正例/反例等）")]
                  [MinLength(0)]
                   [MaxLength(50)]
                  public string ExampleType {get;set;}
        
          /// <summary>
        ///  输入摘要
        /// </summary>
        [Description("输入摘要")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string InputSummary {get;set;}
        
          /// <summary>
        ///  输出摘要
        /// </summary>
        [Description("输出摘要")]
                  [MinLength(0)]
                   [MaxLength(500)]
                  public string OutputSummary {get;set;}
        
          /// <summary>
        ///  样例 JSON 内容
        /// </summary>
        [Description("样例 JSON 内容")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(-1)]
                  public string ExampleJson {get;set;}
        
          /// <summary>
        ///  Embedding 模型
        /// </summary>
        [Description("Embedding 模型")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string EmbeddingModel {get;set;}
        
          /// <summary>
        ///  向量库ID/标识
        /// </summary>
        [Description("向量库ID/标识")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string VectorID {get;set;}
        
          /// <summary>
        ///  相似度哈希/指纹
        /// </summary>
        [Description("相似度哈希/指纹")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string SimilarityHash {get;set;}
        
          /// <summary>
        ///  扩展元数据 JSON
        /// </summary>
        [Description("扩展元数据 JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string MetadataJson {get;set;}
        
     

    }
}
