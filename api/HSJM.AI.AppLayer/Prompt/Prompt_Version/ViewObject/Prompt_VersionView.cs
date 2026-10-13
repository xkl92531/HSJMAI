
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/14 21:35:26
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.Prompt.ViewObject
{
    /// <summary>
    /// Prompt版本管理表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class Prompt_VersionView:ViewBaseField
    {
            /// <summary>
        ///  主键ID
        /// </summary>
          [ImporterHeader(Name = "主键ID", Format = ""), ExporterHeader("主键ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  关联的角色ID
        /// </summary>
          [ImporterHeader(Name = "关联的角色ID", Format = ""), ExporterHeader("关联的角色ID")]
        public long? RoleID {get;set;}
        
          /// <summary>
        ///  版本号
        /// </summary>
          [ImporterHeader(Name = "版本号", Format = ""), ExporterHeader("版本号")]
        public string VersionNumber {get;set;}
        
          /// <summary>
        ///  Prompt内容
        /// </summary>
          [ImporterHeader(Name = "Prompt内容", Format = ""), ExporterHeader("Prompt内容")]
        public string SystemPromptContent {get;set;}
        
          /// <summary>
        ///  变更说明
        /// </summary>
          [ImporterHeader(Name = "变更说明", Format = ""), ExporterHeader("变更说明")]
        public string ChangeLog {get;set;}
        
          /// <summary>
        ///  是否当前激活版本
        /// </summary>
          [ImporterHeader(Name = "是否当前激活版本", Format = ""), ExporterHeader("是否当前激活版本")]
        public bool? IsActive {get;set;}
        
          /// <summary>
        ///  是否参与A/B测试
        /// </summary>
          [ImporterHeader(Name = "是否参与A/B测试", Format = ""), ExporterHeader("是否参与A/B测试")]
        public bool? IsABTest {get;set;}
        
          /// <summary>
        ///  流量分配比例
        /// </summary>
          [ImporterHeader(Name = "流量分配比例", Format = ""), ExporterHeader("流量分配比例")]
        public int? TrafficRatio {get;set;}
        
          /// <summary>
        ///  该版本的平均评分
        /// </summary>
          [ImporterHeader(Name = "该版本的平均评分", Format = ""), ExporterHeader("该版本的平均评分")]
        public decimal? AverageScore {get;set;}
        
          /// <summary>
        ///  使用次数
        /// </summary>
          [ImporterHeader(Name = "使用次数", Format = ""), ExporterHeader("使用次数")]
        public int? UsageCount {get;set;}
        
     

    }
}
