
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:49
using System;
using HSJM.AI.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace HSJM.AI.AppLayer.SYS.ViewObject
{
    /// <summary>
    /// 角色用户表
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class SysRoleUsersView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  角色ID
        /// </summary>
          [ImporterHeader(Name = "角色ID", Format = ""), ExporterHeader("角色ID")]
        public long? RoleID {get;set;}
        
          /// <summary>
        ///  用户ID
        /// </summary>
          [ImporterHeader(Name = "用户ID", Format = ""), ExporterHeader("用户ID")]
        public long? UserID {get;set;}
        
     

    }
}
