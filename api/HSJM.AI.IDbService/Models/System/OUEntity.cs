//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/03/05 13:43:57

using SqlSugar;
using HSJM.AI.Core;

namespace HSJM.AI.IDbService
{
    /// <summary>
    /// 组织架构
    /// </summary>
    [SugarTable("SYS_OU")]
    public class OUEntity : BaseField
    {
        public OUEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }

        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true)]
        public long Id { get; set; }

        /// <summary>
        ///  顶级ID
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public long? CompanyID { get; set; }

        /// <summary>
        ///  父级ID
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public long? ParentID { get; set; }

        /// <summary>
        ///  组织名称
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string Name { get; set; }

        /// <summary>
        ///  组织类型（1,公司，2.部门）
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public int? Type { get; set; }

        /// <summary>
        ///  层级数
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public int? LevelCode { get; set; }

        /// <summary>
        ///  排序
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public int? Sort { get; set; }

        /// <summary>
        ///  备注
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string Remarks { get; set; }

        /// <summary>
        ///  状态（正常_1、停用_2）
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public int? Status { get; set; }
    }
}