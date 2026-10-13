using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.AppLayer.SYS.SysMenu.ViewObject
{
    public class MetaViewModel
    { 
        /// <summary>
        /// 标题
        /// </summary>
        public string Title { get; set; }
        /// <summary>
        /// 图标
        /// </summary>
        public string Icon { get; set; }

        /// <summary>
        /// 导航激活时显示的图标
        /// </summary>
        public string ActiveIcon { get; set; }

        /// <summary>
        /// 子导航是否默认展开
        /// </summary>
        public bool DefaultOpened { get; set; }

        /// <summary>
        /// 子导航是否始终展开
        /// </summary>
        public bool AlwaysOpened { get; set; }
        /// <summary>
        /// 是否常驻标签页
        /// </summary>
        public bool Permanent { get; set; }
        /// <summary>
        /// 是否在导航中展示
        /// </summary>
        public bool Menu { get; set; }
        /// <summary>
        /// 指定高亮的导航，需要设置完整路由地址
        /// 该配置与 meta.menu: false 一起使用，因为子导航不显示，会导致进入该导航路由后，导航高亮效果失效，所以需要手动指定。
        /// </summary>
        public string ActiveMenu { get; set; }
        /// <summary>
        /// 是否为单个一级导航
        /// </summary> 
        public bool SingleMenu { get; set; }
        /// <summary>
        /// 是否对该页面进行缓存
        /// </summary>
        public bool Cache { get; set; }
        /// <summary>
        /// 是否对该页面清除缓存，须设置 cache 才会生效\
        /// 可设置一个目标路由的 name 数组
        /// </summary>
        public List<string> NoCache { get; set; }
        /// <summary>
        /// 导航徽章
        /// </summary>
        public bool Badge { get; set; }
        /// <summary>
        /// 是否最大化
        /// </summary>
        public bool Maximize { get; set; }
        /// <summary>
        /// 是否在新窗口打开
        /// </summary>
        public bool NewWindow { get; set; }
        /// <summary>
        /// 内嵌网页链接，会启用一个 <iframe> 并载入该链接
        /// </summary>
        public string Iframe { get; set; }

        /// <summary>
        /// 是否显示底部版权信息
        /// </summary>
        public bool Copyright { get; set; }

        /// <summary>
        /// 是否开启白名单，开启后无需登录即可访问
        /// </summary>
        public bool WhiteList { get; set; }
        /// <summary>
        /// 该路由是否在面包屑导航中展示
        /// </summary>
        public bool Breadcrumb { get; set; } = true;
        /// <summary>
        /// 外部网页链接
        /// </summary>
        public string Link { get; set; }
    }
}
