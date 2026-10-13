
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:27
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.SYS.ViewObject
{
    /// <summary>
    /// 角色菜单表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class SysRoleMenuView : ViewBaseField
    {
        /// <summary>
        ///  ID
        /// </summary>
        [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID { get; set; }

        /// <summary>
        ///  角色ID
        /// </summary>
        [ImporterHeader(Name = "角色ID", Format = ""), ExporterHeader("角色ID")]
        public long? RoleID { get; set; }

        /// <summary>
        ///  菜单ID
        /// </summary>
        [ImporterHeader(Name = "菜单ID", Format = ""), ExporterHeader("菜单ID")]
        public long? MenuID { get; set; }

        /// <summary>
        /// 按钮权限
        /// </summary>
        public string MenuButtonCodes { get; set; }

    }
}
