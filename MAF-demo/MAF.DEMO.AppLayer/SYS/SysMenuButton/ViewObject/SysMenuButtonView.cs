
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:39:28
using System;
using MAF.DEMO.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace MAF.DEMO.AppLayer.SYS.ViewObject
{
    /// <summary>
    /// 菜单按钮
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class SysMenuButtonView : ViewBaseField
    {
        /// <summary>
        ///  ID
        /// </summary>
        [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID { get; set; }

        /// <summary>
        ///  菜单ID
        /// </summary>
        [ImporterHeader(Name = "菜单ID", Format = ""), ExporterHeader("菜单ID")]
        public long? MenuID { get; set; }

        /// <summary>
        ///  按钮名称
        /// </summary>
        [ImporterHeader(Name = "按钮名称", Format = ""), ExporterHeader("按钮名称")]
        public string Name { get; set; }

        /// <summary>
        ///  按钮代码
        /// </summary>
        [ImporterHeader(Name = "按钮代码", Format = ""), ExporterHeader("按钮代码")]
        public string Code { get; set; }

        /// <summary>
        ///  排序
        /// </summary>
        [ImporterHeader(Name = "排序", Format = ""), ExporterHeader("排序")]
        public int? Sort { get; set; }



    }
}
