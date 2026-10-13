
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 16:36:10
using System;
using System.Collections.Generic;
using MAF.DEMO.Core;
using MAF.DEMO.IDbService;
using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
using System.ComponentModel;
using AutoMapper.Configuration.Annotations;
namespace MAF.DEMO.AppLayer.SYS.ViewObject
{
    /// <summary>
    /// 系统菜单
    /// </summary>
    [ExcelImporter(HeaderRowIndex = 1)]
    public class SysMenuView : ViewBaseField
    {
        /// <summary>
        ///  ID
        /// </summary>
        [ImporterHeader(Name = "ID", Format = ""), ExporterHeader("ID")]
        public long ID { get; set; }

        /// <summary>
        ///  菜单名称()
        /// </summary>
        [ImporterHeader(Name = "菜单名称()", Format = ""), ExporterHeader("菜单名称()")]
        public string MenuName { get; set; }

        /// <summary>
        ///  系统ID
        /// </summary>
        [ImporterHeader(Name = "系统ID", Format = ""), ExporterHeader("系统ID")]
        public long? SystemID { get; set; }

        /// <summary>
        ///  系统名称
        /// </summary>
        [ImporterHeader(Name = "系统名称", Format = ""), ExporterHeader("系统名称")]
        public string SystemName { get; set; }

        /// <summary>
        ///  父级菜单ID
        /// </summary>
        [ImporterHeader(Name = "父级菜单ID", Format = ""), ExporterHeader("父级菜单ID")]
        public long? ParentId { get; set; }

        /// <summary>
        ///  父级菜单名称
        /// </summary>
        [ImporterHeader(Name = "父级菜单名称", Format = ""), ExporterHeader("父级菜单名称")]
        public string ParentName { get; set; }

        /// <summary>
        ///  路由名称
        /// </summary>
        [ImporterHeader(Name = "路由名称", Format = ""), ExporterHeader("路由名称")]
        public string RouterName { get; set; }

        /// <summary>
        ///  路由地址
        /// </summary>
        [ImporterHeader(Name = "路由地址", Format = ""), ExporterHeader("路由地址")]
        public string MenuPath { get; set; }

        /// <summary>
        ///  组件地址
        /// </summary>
        [ImporterHeader(Name = "组件地址", Format = ""), ExporterHeader("组件地址")]
        public string MenuComponent { get; set; }

        /// <summary>
        ///  路由重定向地址
        /// </summary>
        [ImporterHeader(Name = "路由重定向地址", Format = ""), ExporterHeader("路由重定向地址")]
        public string MenuRedirect { get; set; }

        /// <summary>
        ///  国际化（ key 值，例如：route.page）
        /// </summary>
        [ImporterHeader(Name = "国际化（ key 值，例如：route.page）", Format = ""), ExporterHeader("国际化（ key 值，例如：route.page）")]
        public string MenuI18n { get; set; }

        /// <summary>
        ///  菜单图标
        /// </summary>
        [ImporterHeader(Name = "菜单图标", Format = ""), ExporterHeader("菜单图标")]
        public string MenuIcon { get; set; }

        /// <summary>
        ///  导航激活时显示的图标
        /// </summary>
        [ImporterHeader(Name = "导航激活时显示的图标", Format = ""), ExporterHeader("导航激活时显示的图标")]
        public string MenuActiveIcon { get; set; }

        /// <summary>
        ///  指定高亮的导航，需要设置完整路由地址(meta.menu: false),例如：/news/list
        /// </summary>
        [ImporterHeader(Name = "指定高亮的导航，需要设置完整路由地址(meta.menu: false),例如：/news/list", Format = ""), ExporterHeader("指定高亮的导航，需要设置完整路由地址(meta.menu: false),例如：/news/list")]
        public string MenuActive { get; set; }

        /// <summary>
        ///  子导航是否默认展开
        /// </summary>
        [ImporterHeader(Name = "子导航是否默认展开", Format = ""), ExporterHeader("子导航是否默认展开")]
        public bool? MenuDefaultOpened { get; set; }

        /// <summary>
        ///  子导航是否始终展开
        /// </summary>
        [ImporterHeader(Name = "子导航是否始终展开", Format = ""), ExporterHeader("子导航是否始终展开")]
        public bool? MenuAlwaysOpened { get; set; }

        /// <summary>
        ///  是否常驻标签页
        /// </summary>
        [ImporterHeader(Name = "是否常驻标签页", Format = ""), ExporterHeader("是否常驻标签页")]
        public bool? MenuPermanent { get; set; }

        /// <summary>
        ///  是否在导航中展示(menu)
        /// </summary>
        [ImporterHeader(Name = "是否在导航中展示(menu)", Format = ""), ExporterHeader("是否在导航中展示(menu)")]
        public bool? MenuDisplay { get; set; }

        /// <summary>
        ///  导航是否是一级导航
        /// </summary>
        [ImporterHeader(Name = "导航是否是一级导航", Format = ""), ExporterHeader("导航是否是一级导航")]
        public bool? MenuSingle { get; set; }

        /// <summary>
        ///  导航徽章
        /// </summary>
        [ImporterHeader(Name = "导航徽章", Format = ""), ExporterHeader("导航徽章")]
        public bool? MenuBadge { get; set; }

        /// <summary>
        ///  是否启用缓存
        /// </summary>
        [ImporterHeader(Name = "是否启用缓存", Format = ""), ExporterHeader("是否启用缓存")]
        public bool? EnableCache { get; set; }

        /// <summary>
        ///  是否对该页面清除缓存，须设置 EnableCache 才会生效
        /// </summary>
        [ImporterHeader(Name = "是否对该页面清除缓存，须设置 EnableCache 才会生效", Format = ""), ExporterHeader("是否对该页面清除缓存，须设置 EnableCache 才会生效")]
        public string NoCache { get; set; }

        /// <summary>
        ///  是否最大化
        /// </summary>
        [ImporterHeader(Name = "是否最大化", Format = ""), ExporterHeader("是否最大化")]
        public bool? MenuMaximize { get; set; }

        /// <summary>
        ///  是否在新窗口打开
        /// </summary>
        [ImporterHeader(Name = "是否在新窗口打开", Format = ""), ExporterHeader("是否在新窗口打开")]
        public bool? MenuNewWindow { get; set; }

        /// <summary>
        ///  内嵌网页链接，会启用一个 <iframe> 并载入该链接
        /// </summary>
        [ImporterHeader(Name = "内嵌网页链接，会启用一个 <iframe> 并载入该链接", Format = ""), ExporterHeader("内嵌网页链接，会启用一个 <iframe> 并载入该链接")]
        public string MenuIframe { get; set; }

        /// <summary>
        ///  是否显示底部版权信息
        /// </summary>
        [ImporterHeader(Name = "是否显示底部版权信息", Format = ""), ExporterHeader("是否显示底部版权信息")]
        public bool? MenuCopyright { get; set; }

        /// <summary>
        ///  在面包屑显示
        /// </summary>
        [ImporterHeader(Name = "在面包屑显示", Format = ""), ExporterHeader("在面包屑显示")]
        public bool? MenuBreadcrumb { get; set; }

        /// <summary>
        ///  徽标变体
        /// </summary>
        [ImporterHeader(Name = "徽标变体", Format = ""), ExporterHeader("徽标变体")]
        public string MenuBadgeVariant { get; set; }

        /// <summary>
        ///  是否开启白名单，开启后无需登录即可访问
        /// </summary>
        [ImporterHeader(Name = "是否开启白名单，开启后无需登录即可访问", Format = ""), ExporterHeader("是否开启白名单，开启后无需登录即可访问")]
        public bool? IsWhitelist { get; set; }

        /// <summary>
        ///  外部网页链接，会在浏览器新窗口访问该链接
        /// </summary>
        [ImporterHeader(Name = "外部网页链接，会在浏览器新窗口访问该链接", Format = ""), ExporterHeader("外部网页链接，会在浏览器新窗口访问该链接")]
        public string Link { get; set; }

        /// <summary>
        ///  备注
        /// </summary>
        [ImporterHeader(Name = "备注", Format = ""), ExporterHeader("备注")]
        public string Remarks { get; set; }

        /// <summary>
        ///  排序
        /// </summary>
        [ImporterHeader(Name = "排序", Format = ""), ExporterHeader("排序")]
        public int? Sort { get; set; }

        /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [ImporterHeader(Name = "状态（正常_1、停用_2）", Format = ""), ExporterHeader("状态（正常_1、停用_2）")]
        public int? Status { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public List<string> NoCacheList { get; set; } = new List<string>();

        ///// <summary>
        ///// 菜单按钮
        ///// </summary> 
        public List<SysMenuButtonEntity> ButtonList { get; set; } = new List<SysMenuButtonEntity>();

    }
}
