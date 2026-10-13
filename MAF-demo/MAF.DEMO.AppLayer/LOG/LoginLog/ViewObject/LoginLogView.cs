
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 10:05:46
using System;
using MAF.DEMO.Core;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
namespace MAF.DEMO.AppLayer.LOG.ViewObject
{
    /// <summary>
    /// 用户登录日志
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class LoginLogView:ViewBaseField
    {
            /// <summary>
        ///  ID
        /// </summary>
          [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID {get;set;}
        
          /// <summary>
        ///  用户ID
        /// </summary>
          [ImporterHeader(Name = "用户ID", Format = ""), ExporterHeader("用户ID")]
        public long? UserID {get;set;}
        
          /// <summary>
        ///  用户名
        /// </summary>
          [ImporterHeader(Name = "用户名", Format = ""), ExporterHeader("用户名")]
        public string Account {get;set;}
        
          /// <summary>
        ///  IP地址
        /// </summary>
          [ImporterHeader(Name = "IP地址", Format = ""), ExporterHeader("IP地址")]
        public string LoginIP {get;set;}
        
          /// <summary>
        ///  登录类型（浏览器，手机端）
        /// </summary>
          [ImporterHeader(Name = "登录类型（浏览器，手机端）", Format = ""), ExporterHeader("登录类型（浏览器，手机端）")]
        public string LoginType {get;set;}
        
          /// <summary>
        ///  操作系统
        /// </summary>
          [ImporterHeader(Name = "操作系统", Format = ""), ExporterHeader("操作系统")]
        public string OSVersion {get;set;}
        
          /// <summary>
        ///  登录时间
        /// </summary>
          [ImporterHeader(Name = "登录时间", Format = ""), ExporterHeader("登录时间")]
        public DateTime? LoginDate {get;set;}
        
          /// <summary>
        ///  地点（省份，城市）
        /// </summary>
          [ImporterHeader(Name = "地点（省份，城市）", Format = ""), ExporterHeader("地点（省份，城市）")]
        public string Location {get;set;}
        
          /// <summary>
        ///  地理信息
        /// </summary>
          [ImporterHeader(Name = "地理信息", Format = ""), ExporterHeader("地理信息")]
        public string Address {get;set;}
        
     

    }
}
