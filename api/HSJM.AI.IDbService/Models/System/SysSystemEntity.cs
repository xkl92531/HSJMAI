//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:57:03

using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 
    /// </summary>
    [SugarTable("SYS_System", TableDescription = "系统表")]
    public class SysSystemEntity : BaseField
    {
        public SysSystemEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        ///  系统名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "系统名称")]
        public string Name { get; set; }

        /// <summary>
        ///  备注
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "备注")]
        public string Remarks { get; set; }

        /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "状态（正常_1、停用_2）")]
        public int? Status { get; set; }

        /// <summary>
        ///  排序
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "排序")]
        public int? Sort { get; set; }
    }
}