

//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 16:36:10
using MAF.DEMO.AppLayer.SYS.ViewObject;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SqlSugar;
namespace MAF.DEMO.AppLayer.SYS.DTOS
{
    /// <summary>
    /// 系统菜单
    /// </summary>
    public class SysMenuInsertDTO
    {
        /// <summary>
        ///  菜单名称()
        /// </summary>
        [Description("菜单名称()")]
        [MinLength(0)]
        [MaxLength(50)]
        public string MenuName { get; set; }

        /// <summary>
        ///  系统ID
        /// </summary>
        [Description("系统ID")]
        [Range(0, long.MaxValue)]
        public long? SystemID { get; set; }

        /// <summary>
        ///  系统名称
        /// </summary>
        [Description("系统名称")]
        [MinLength(0)]
        [MaxLength(50)]
        public string SystemName { get; set; }

        /// <summary>
        ///  父级菜单ID
        /// </summary>
        [Description("父级菜单ID")]
        [Range(0, long.MaxValue)]
        public long? ParentID { get; set; }

        /// <summary>
        ///  父级菜单名称
        /// </summary>
        [Description("父级菜单名称")]
        [MinLength(0)]
        [MaxLength(50)]
        public string ParentName { get; set; }

        /// <summary>
        ///  路由名称
        /// </summary>
        [Description("路由名称")]
        [MinLength(0)]
        [MaxLength(200)]
        public string RouterName { get; set; }

        /// <summary>
        ///  路由地址
        /// </summary>
        [Description("路由地址")]
        [MinLength(0)]
        [MaxLength(500)]
        public string MenuPath { get; set; }

        /// <summary>
        ///  组件地址
        /// </summary>
        [Description("组件地址")]
        [MinLength(0)]
        [MaxLength(500)]
        public string MenuComponent { get; set; }

        /// <summary>
        ///  路由重定向地址
        /// </summary>
        [Description("路由重定向地址")]
        [MinLength(0)]
        [MaxLength(500)]
        public string MenuRedirect { get; set; }

        /// <summary>
        ///  国际化（ key 值，例如：route.page）
        /// </summary>
        [Description("国际化（ key 值，例如：route.page）")]
        [MinLength(0)]
        [MaxLength(200)]
        public string MenuI18n { get; set; }

        /// <summary>
        ///  菜单图标
        /// </summary>
        [Description("菜单图标")]
        [MinLength(0)]
        [MaxLength(500)]
        public string MenuIcon { get; set; }

        /// <summary>
        ///  导航激活时显示的图标
        /// </summary>
        [Description("导航激活时显示的图标")]
        [MinLength(0)]
        [MaxLength(500)]
        public string MenuActiveIcon { get; set; }

        /// <summary>
        ///  指定高亮的导航，需要设置完整路由地址(meta.menu: false),例如：/news/list
        /// </summary>
        [Description("指定高亮的导航，需要设置完整路由地址(meta.menu: false),例如：/news/list")]
        [MinLength(0)]
        [MaxLength(500)]
        public string MenuActive { get; set; }

        /// <summary>
        ///  子导航是否默认展开
        /// </summary>
        [Description("子导航是否默认展开")]
        public bool? MenuDefaultOpened { get; set; }

        /// <summary>
        ///  子导航是否始终展开
        /// </summary>
        [Description("子导航是否始终展开")]
        public bool? MenuAlwaysOpened { get; set; }

        /// <summary>
        ///  是否常驻标签页
        /// </summary>
        [Description("是否常驻标签页")]
        public bool? MenuPermanent { get; set; }

        /// <summary>
        ///  是否在导航中展示(menu)
        /// </summary>
        [Description("是否在导航中展示(menu)")]
        public bool? MenuDisplay { get; set; }

        /// <summary>
        ///  导航是否是一级导航
        /// </summary>
        [Description("导航是否是一级导航")]
        public bool? MenuSingle { get; set; }

        /// <summary>
        ///  导航徽章
        /// </summary>
        [Description("导航徽章")]
        public bool? MenuBadge { get; set; }

        /// <summary>
        ///  是否启用缓存
        /// </summary>
        [Description("是否启用缓存")]
        public bool? EnableCache { get; set; }

        /// <summary>
        ///  是否对该页面清除缓存，须设置 EnableCache 才会生效
        /// </summary>
        [Description("是否对该页面清除缓存，须设置 EnableCache 才会生效")]
        [MinLength(0)]
        [MaxLength(3000)]
        public string NoCache { get; set; }

        /// <summary>
        ///  是否最大化
        /// </summary>
        [Description("是否最大化")]
        public bool? MenuMaximize { get; set; }

        /// <summary>
        ///  是否在新窗口打开
        /// </summary>
        [Description("是否在新窗口打开")]
        public bool? MenuNewWindow { get; set; }

        /// <summary>
        ///  内嵌网页链接，会启用一个 <iframe> 并载入该链接
        /// </summary>
        [Description("内嵌网页链接，会启用一个 <iframe> 并载入该链接")]
        [MinLength(0)]
        [MaxLength(500)]
        public string MenuIframe { get; set; }

        /// <summary>
        ///  是否显示底部版权信息
        /// </summary>
        [Description("是否显示底部版权信息")]
        public bool? MenuCopyright { get; set; }

        /// <summary>
        ///  在面包屑显示
        /// </summary>
        [Description("在面包屑显示")]
        public bool? MenuBreadcrumb { get; set; }

        /// <summary>
        ///  徽标变体
        /// </summary>
        [Description("徽标变体")]
        [MinLength(0)]
        [MaxLength(20)]
        public string MenuBadgeVariant { get; set; }

        /// <summary>
        ///  是否开启白名单，开启后无需登录即可访问
        /// </summary>
        [Description("是否开启白名单，开启后无需登录即可访问")]
        public bool? IsWhitelist { get; set; }

        /// <summary>
        ///  外部网页链接，会在浏览器新窗口访问该链接
        /// </summary>
        [Description("外部网页链接，会在浏览器新窗口访问该链接")]
        [MinLength(0)]
        [MaxLength(3000)]
        public string Link { get; set; }

        /// <summary>
        ///  备注
        /// </summary>
        [Description("备注")]
        [MinLength(0)]
        [MaxLength(500)]
        public string Remarks { get; set; }

        /// <summary>
        ///  排序
        /// </summary>
        [Description("排序")]
        [Range(0, int.MaxValue)]
        public int? Sort { get; set; }

        /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [Description("状态（正常_1、停用_2）")]
        [Range(0, int.MaxValue)]
        public int? Status { get; set; }



        public List<string> NoCacheList { get; set; } = new List<string>();
        /// <summary>
        /// 菜单按钮
        /// </summary>
        public List<SysMenuButtonView> ButtonList { get; set; }

    }
}
