using Magicodes.ExporterAndImporter.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.SYS.SysMenu.ViewObject
{
    /// <summary>
    /// 菜单
    /// </summary>
    public class MenuTreeViewModel
    {
        /// <summary>
        ///  ID
        /// </summary> 
        public long ID { get; set; }

        /// <summary>
        ///  菜单名称
        /// </summary> 
        public string MenuName { get; set; }
        /// <summary>
        ///  父级菜单ID
        /// </summary> 
        public long? ParentID { get; set; }

        /// <summary>
        /// 是否选中
        /// </summary>
        public bool IsChecked { get; set; }

        /// <summary>
        /// 按钮列表
        /// </summary>
        public List<ButtonView> MenuButtonList { get; set; }

        public List<long> ButtonIdList { get; set; }

        /// <summary>
        /// 子集数据
        /// </summary>
        public List<MenuTreeViewModel> Children { get; set; }
    }

    /// <summary>
    /// 
    /// </summary>
    public class ButtonView
    {

        /// <summary>
        ///  ID
        /// </summary> 
        public long ID { get; set; }

        /// <summary>
        ///  菜单ID
        /// </summary> 
        public long? MenuID { get; set; }

        /// <summary>
        ///  按钮名称
        /// </summary> 
        public string Name { get; set; }

        /// <summary>
        ///  按钮代码
        /// </summary> 
        public string Code { get; set; }

        /// <summary>
        /// 是否选中
        /// </summary>
        public bool IsChecked { get; set; }
    }
}
