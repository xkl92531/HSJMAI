//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:39:57

using SqlSugar;
using MAF.DEMO.Core;

namespace MAF.DEMO.IDbService
{
    /// <summary>
    /// 角色表
    /// </summary>
    [SugarTable("SYS_Role", TableDescription = "角色表")]
    public class SysRoleEntity : BaseField
    {
        public SysRoleEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        ///  名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "名称")]
        public string Name { get; set; }

        /// <summary>
        ///  用户组（预留）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "用户组（预留）")]
        public long? GroupID { get; set; }

        /// <summary>
        ///  排序
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "排序")]
        public int? Sort { get; set; }

        /// <summary>
        ///  备注
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "备注")]
        public string Remarks { get; set; }

        /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = " 状态（正常_1、停用_2）")]
        public int? Status { get; set; }
    }
}