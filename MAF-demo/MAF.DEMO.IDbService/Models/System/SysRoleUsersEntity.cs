//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:49

using SqlSugar;
using MAF.DEMO.Core;

namespace MAF.DEMO.IDbService
{
    /// <summary>
    /// 角色用户表
    /// </summary>
    [SugarTable("SYS_RoleUsers", TableDescription = "角色用户表")]
    public class SysRoleUsersEntity : BaseField
    {
        public SysRoleUsersEntity()
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
        ///  用户ID
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "用户ID")]
        public long? UserID { get; set; }
    }
}