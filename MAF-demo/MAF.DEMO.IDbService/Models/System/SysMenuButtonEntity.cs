//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:39:28

using SqlSugar;
using MAF.DEMO.Core;

namespace MAF.DEMO.IDbService
{
    /// <summary>
    /// 菜单按钮
    /// </summary>
    [SugarTable("SYS_MenuButton",TableDescription = "菜单按钮")]
    public class SysMenuButtonEntity : BaseField
    {
        public SysMenuButtonEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        ///  菜单ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "菜单ID")]
        public long? MenuID { get; set; }

        /// <summary>
        ///  按钮名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "按钮名称")]
        public string Name { get; set; }

        /// <summary>
        ///  按钮代码
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "按钮代码")]
        public string Code { get; set; }

        /// <summary>
        ///  排序
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "排序")]
        public int? Sort { get; set; }
    }
}