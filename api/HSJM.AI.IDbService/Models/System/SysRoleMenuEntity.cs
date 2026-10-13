
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:27
using SqlSugar;
using HSJM.AI.Core;
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 角色菜单表
    /// </summary>
    [SugarTable("SYS_RoleMenu", TableDescription = "角色菜单表")]
    public class SysRoleMenuEntity : BaseField
    {

        public SysRoleMenuEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }
        /// <summary>
        ///  角色ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "角色ID")]
        public long? RoleID { get; set; }

        /// <summary>
        ///  菜单ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "菜单ID")]
        public long? MenuID { get; set; }

        /// <summary>
        /// 按钮权限
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "按钮权限")]
        public string MenuButtonCodes { get; set; }

        /// <summary>
        /// 系统ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "系统ID")]
        public long? SystemID { get; set; }
    }


}
