

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:04
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
namespace HSJM.AI.AppLayer.Prompt.DTOS
{
    /// <summary>
    /// 提示词片段
    /// </summary>
    public class Prompt_FragmentInsertDTO
    {
            /// <summary>
        ///  片段类型（Persona/Instruction 等）
        /// </summary>
        [Description("片段类型（Persona/Instruction 等）")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string FragmentType {get;set;}
        
          /// <summary>
        ///  标签/用途标识
        /// </summary>
        [Description("标签/用途标识")]
                  [MinLength(0)]
                   [MaxLength(100)]
                  public string Tag {get;set;}
        
          /// <summary>
        ///  片段内容
        /// </summary>
        [Description("片段内容")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(-1)]
                  public string Content {get;set;}
        
          /// <summary>
        ///  片段版本号
        /// </summary>
        [Description("片段版本号")]
                  [Required]
                   [MinLength(0)]
                   [MaxLength(50)]
                  public string Version {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
        [Description("是否启用")]
                  [Required]
                  public bool? IsActive {get;set;}
        
          /// <summary>
        ///  扩展元数据 JSON
        /// </summary>
        [Description("扩展元数据 JSON")]
                  [MinLength(0)]
                   [MaxLength(-1)]
                  public string MetadataJson {get;set;}
        
     

    }
}
