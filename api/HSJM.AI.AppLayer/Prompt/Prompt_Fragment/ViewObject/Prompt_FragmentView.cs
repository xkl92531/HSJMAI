
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:04
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Prompt.ViewObject
{
    /// <summary>
    /// 提示词片段
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Prompt_FragmentView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  片段类型（Persona/Instruction 等）
        /// </summary>
          [ImporterHeader(Name = "片段类型（Persona/Instruction 等）", Format = ""), ExporterHeader("片段类型（Persona/Instruction 等）")]
        public string FragmentType {get;set;}
        
          /// <summary>
        ///  标签/用途标识
        /// </summary>
          [ImporterHeader(Name = "标签/用途标识", Format = ""), ExporterHeader("标签/用途标识")]
        public string Tag {get;set;}
        
          /// <summary>
        ///  片段内容
        /// </summary>
          [ImporterHeader(Name = "片段内容", Format = ""), ExporterHeader("片段内容")]
        public string Content {get;set;}
        
          /// <summary>
        ///  片段版本号
        /// </summary>
          [ImporterHeader(Name = "片段版本号", Format = ""), ExporterHeader("片段版本号")]
        public string Version {get;set;}
        
          /// <summary>
        ///  是否启用
        /// </summary>
          [ImporterHeader(Name = "是否启用", Format = ""), ExporterHeader("是否启用")]
        public bool? IsActive {get;set;}
        
          /// <summary>
        ///  扩展元数据 JSON
        /// </summary>
          [ImporterHeader(Name = "扩展元数据 JSON", Format = ""), ExporterHeader("扩展元数据 JSON")]
        public string MetadataJson {get;set;}
        
     

    }
}
