
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 16:36:10
using SqlSugar;
using HSJM.AI.Core;
using System.Collections;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 系统菜单
    /// </summary>
    [SugarTable("SYS_Menu", TableDescription = "系统菜单")]
    public class SysMenuEntity : BaseField
    {

        public SysMenuEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }
        /// <summary>
        ///  菜单名称()
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "菜单名称")]
        public string MenuName { get; set; }

        /// <summary>
        ///  系统ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "系统ID")]
        public long? SystemID { get; set; }

        /// <summary>
        ///  系统名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "系统名称")]
        public string SystemName { get; set; }

        /// <summary>
        ///  父级菜单ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "父级菜单ID")]
        public long? ParentID { get; set; }

        /// <summary>
        ///  父级菜单名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "父级菜单名称")]
        public string ParentName { get; set; }

        /// <summary>
        ///  路由名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "路由名称")]
        public string RouterName { get; set; }

        /// <summary>
        ///  路由地址
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "路由地址")]
        public string MenuPath { get; set; }

        /// <summary>
        ///  组件地址
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "组件地址")]
        public string MenuComponent { get; set; }

        /// <summary>
        ///  路由重定向地址
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "路由重定向地址")]
        public string MenuRedirect { get; set; }

        /// <summary>
        ///  国际化（ key 值，例如：route.page）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "国际化（ key 值，例如：route.page）")]
        public string MenuI18n { get; set; }

        /// <summary>
        ///  菜单图标
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "菜单图标")]
        public string MenuIcon { get; set; }

        /// <summary>
        ///  导航激活时显示的图标
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "导航激活时显示的图标")]
        public string MenuActiveIcon { get; set; }

        /// <summary>
        ///  指定高亮的导航，需要设置完整路由地址(meta.menu: false),例如：/news/list
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "指定高亮的导航，需要设置完整路由地址(meta.menu: false),例如：/news/list")]
        public string MenuActive { get; set; }

        /// <summary>
        ///  子导航是否默认展开
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "子导航是否默认展开")]
        public bool? MenuDefaultOpened { get; set; }

        /// <summary>
        ///  子导航是否始终展开
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "子导航是否始终展开")]
        public bool? MenuAlwaysOpened { get; set; }

        /// <summary>
        ///  是否常驻标签页
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否常驻标签页")]
        public bool? MenuPermanent { get; set; }

        /// <summary>
        ///  是否在导航中展示(menu)
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否在导航中展示(menu)")]
        public bool? MenuDisplay { get; set; }

        /// <summary>
        ///  导航是否是一级导航
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "导航是否是一级导航")]
        public bool? MenuSingle { get; set; }

        /// <summary>
        ///  导航徽章
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "导航徽章")]
        public bool? MenuBadge { get; set; }

        /// <summary>
        ///  是否启用缓存
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否启用缓存")]
        public bool? EnableCache { get; set; }

        /// <summary>
        ///  是否对该页面清除缓存，须设置 EnableCache 才会生效
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否对该页面清除缓存，须设置 EnableCache 才会生效")]
        public string NoCache { get; set; }

        /// <summary>
        ///  是否最大化
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否最大化")]
        public bool? MenuMaximize { get; set; }

        /// <summary>
        ///  是否在新窗口打开
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否在新窗口打开")]
        public bool? MenuNewWindow { get; set; }

        /// <summary>
        ///  内嵌网页链接，会启用一个 <iframe> 并载入该链接
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "内嵌网页链接，会启用一个 <iframe> 并载入该链接")]
        public string MenuIframe { get; set; }

        /// <summary>
        ///  是否显示底部版权信息
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否显示底部版权信息")]
        public bool? MenuCopyright { get; set; }

        /// <summary>
        ///  在面包屑显示
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "在面包屑显示")]
        public bool? MenuBreadcrumb { get; set; }

        /// <summary>
        ///  徽标变体
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "徽标变体")]
        public string MenuBadgeVariant { get; set; }

        /// <summary>
        ///  是否开启白名单，开启后无需登录即可访问
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "是否开启白名单，开启后无需登录即可访问")]
        public bool? IsWhitelist { get; set; }

        /// <summary>
        ///  外部网页链接，会在浏览器新窗口访问该链接
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "外部网页链接，会在浏览器新窗口访问该链接")]
        public string Link { get; set; }

        /// <summary>
        ///  备注
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "备注")]
        public string Remarks { get; set; }

        /// <summary>
        ///  排序
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "排序")]
        public int? Sort { get; set; }

        /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "状态（正常_1、停用_2）")]
        public int? Status { get; set; }

        /// <summary>
        /// 
        /// </summary>
        [SugarColumn(IsIgnore = true)]
        public List<string> NoCacheList { get; set; }
    }


    /// <summary>
    /// 系统菜单
    /// </summary>
    [SugarTable("SYS_Menu")]
    public class SysMenuTreeEntity : BaseField, ITreeNode
    {
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        ///  菜单名称()
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "菜单名称")]
        public string MenuName { get; set; }

        /// <summary>
        ///  系统ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "系统ID")]
        public long? SystemID { get; set; }

        /// <summary>
        ///  系统名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "系统名称")]
        public string SystemName { get; set; }

        /// <summary>
        ///  父级菜单ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "父级菜单ID")]
        public long? ParentID { get; set; }


        /// <summary>
        ///  菜单图标
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "菜单图标")]
        public string MenuIcon { get; set; }

        /// <summary>
        /// 子集数据
        /// </summary>
        [SugarColumn(IsIgnore = true)]
        public List<SysMenuTreeEntity> Children { get; set; } = new List<SysMenuTreeEntity>();

        public long GetId()
        {
            return Id;
        }

        public long GetPid()
        {
            return ParentID.Value;
        }

        public void SetChildren(IList children)
        {
            Children = (List<SysMenuTreeEntity>)children;
        }
    }
}
