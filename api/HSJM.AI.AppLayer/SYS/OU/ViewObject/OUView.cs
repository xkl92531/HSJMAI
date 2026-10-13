
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/03/05 13:43:57
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.SYS.ViewObject
{
    /// <summary>
    /// 组织架构
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class OUView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  顶级ID
        /// </summary>
          [ImporterHeader(Name = "顶级ID", Format = ""), ExporterHeader("顶级ID")]
        public long? CompanyID {get;set;}
        
          /// <summary>
        ///  父级ID
        /// </summary>
          [ImporterHeader(Name = "父级ID", Format = ""), ExporterHeader("父级ID")]
        public long? ParentID {get;set;}
        
          /// <summary>
        ///  组织名称
        /// </summary>
          [ImporterHeader(Name = "组织名称", Format = ""), ExporterHeader("组织名称")]
        public string Name {get;set;}
        
          /// <summary>
        ///  组织类型（1,公司，2.部门）
        /// </summary>
          [ImporterHeader(Name = "组织类型（1,公司，2.部门）", Format = ""), ExporterHeader("组织类型（1,公司，2.部门）")]
        public int? Type {get;set;}
        
          /// <summary>
        ///  层级数
        /// </summary>
          [ImporterHeader(Name = "层级数", Format = ""), ExporterHeader("层级数")]
        public int? LevelCode {get;set;}
        
          /// <summary>
        ///  排序
        /// </summary>
          [ImporterHeader(Name = "排序", Format = ""), ExporterHeader("排序")]
        public int? Sort {get;set;}
        
          /// <summary>
        ///  备注
        /// </summary>
          [ImporterHeader(Name = "备注", Format = ""), ExporterHeader("备注")]
        public string Remarks {get;set;}
        
          /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
          [ImporterHeader(Name = "状态（正常_1、停用_2）", Format = ""), ExporterHeader("状态（正常_1、停用_2）")]
        public int? Status {get;set;}
        
     

    }
}
