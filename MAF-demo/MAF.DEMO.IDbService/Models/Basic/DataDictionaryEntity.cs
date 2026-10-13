
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 10:03:23
using SqlSugar;
using MAF.DEMO.Core;
namespace MAF.DEMO.IDbService
{
    /// <summary>
    /// 数据字典
    /// </summary>
    [SugarTable("Basic_DataDictionary", TableDescription = "字典表")]
    public class DataDictionaryEntity : BaseField
    {

        public DataDictionaryEntity()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }
        [SugarColumn(ColumnName = "Id", IsPrimaryKey = true, ColumnDescription = "ID")]
        public long Id { get; set; }
        /// <summary>
        ///  数据代码
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "数据代码")]
        public string Code { get; set; }

        /// <summary>
        ///  数据名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "数据名称")]
        public string Name { get; set; }

        /// <summary>
        ///  父级代码
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "父级代码")]
        public string ParentCode { get; set; }

        /// <summary>
        ///  父级名称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "父级名称")]
        public string ParentName { get; set; }

        /// <summary>
        ///  简称
        /// </summary>
        [SugarColumn(IsNullable = true, ColumnDescription = "简称")]
        public string ShorterForm { get; set; }

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
        [SugarColumn(IsNullable = true, ColumnDescription = "状态（正常_1、停用_2）")]
        public int? Status { get; set; }



    }


}
