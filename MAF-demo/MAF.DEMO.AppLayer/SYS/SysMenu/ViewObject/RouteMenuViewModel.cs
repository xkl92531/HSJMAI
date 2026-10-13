using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.AppLayer.SYS.SysMenu.ViewObject
{
    public class RouteMenuViewModel
    {
        /// <summary>
        /// 菜单名称
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// 路径
        /// </summary>
        public string Path { get; set; }
        /// <summary>
        /// 组件
        /// </summary>
        public string Component { get; set; }
        /// <summary>
        /// 重定向
        /// </summary>
        public string Redirect { get; set; }
        /// <summary>
        /// 标签
        /// </summary>
        public MetaViewModel Meta { get; set; }

        /// <summary>
        /// 子数据
        /// </summary>
        public List<RouteMenuViewModel> Children { get; set; }
    }
}
